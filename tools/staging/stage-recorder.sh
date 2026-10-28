#!/usr/bin/env bash
# Assembles the self-contained recorder runtime (the recorder/ folder beside
# Prisc.Ui, which HelperLocator resolves) from a cargo profile dir that a
# release build of the recorder packages (shipped.sh: recorder_packages) has
# filled, and checks it. Used by build.sh and build-recorder.yml.
#
#   stage-recorder.sh <rid> <cargo profile dir> <dest>
#
# Windows: the runtime tree prisc_recorder's build.rs assembled in the profile
#   dir (obs.dll, graphics modules, FFmpeg/x264 DLLs, obs-plugins/, data/),
#   plus the VC++ runtime.
# macOS: libobs.framework, the graphics modules, the dep dylibs build.rs
#   staged, the plugin bundles, all relocated to @executable_path/Frameworks
#   and ad-hoc re-signed. The arch the host can run also gets a vid2gif
#   conversion and a dlopen of the dep dylibs from a foreign process.
# Linux: tools/linux-build/stage-bundle.sh, which also enforces the
#   portability rules of the manylinux build image.
#
# Works on macOS, Linux and on Windows under git bash.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
# shellcheck source=tools/staging/shipped.sh
. "$here/shipped.sh"

if [ $# -ne 3 ]; then
  sed -n '2,19p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
fi
rid=$1
src=$(cd "$2" && pwd)
dist=$3
rid_triple "$rid" >/dev/null

rm -rf "$dist"; mkdir -p "$dist"
dist=$(cd "$dist" && pwd)

require() {
  local f
  for f in "$@"; do
    if ! compgen -G "$dist/$f" >/dev/null; then
      echo "::error::required file missing from the recorder bundle: $f"
      exit 1
    fi
  done
}

stage_windows() {
  local f name
  # Every top-level exe and dll except the app's own binaries (and their
  # DirectML companion), which share the profile dir when build.sh builds
  # both halves together. That leaves the recorder executables, the
  # obs-*-test.exe probes, obs.dll, its graphics modules and the FFmpeg/x264
  # runtime DLLs. *.pdb and cargo's build junk stay behind.
  local skip
  skip=$(app_symbolicated_binaries "$rid"; echo DirectML.dll)
  for f in "$src"/*.exe "$src"/*.dll; do
    [ -f "$f" ] || continue
    name=$(basename "$f")
    if grep -qxF "$name" <<< "$skip"; then continue; fi
    cp "$f" "$dist/"
  done
  cp -R "$src/obs-plugins" "$src/data" "$dist/"

  # obs.dll, its plugins and the FFmpeg DLLs are /MD builds (copy_vc_runtime
  # checks its own result).
  copy_vc_runtime "$rid" "$dist"

  # prisc_vid2gif links the FFmpeg DLLs directly; it is a hard shipping
  # requirement on every platform (no separate ffmpeg.exe needed). The FFmpeg
  # names carry their ABI major, which an obs-deps bump changes, so match any.
  # shellcheck disable=SC2046 # one name per line, none with spaces
  require $(recorder_binaries "$rid") obs.dll 'avcodec-*.dll' 'avfilter-*.dll'
}

stage_macos() {
  local config=RelWithDebInfo obs_build="" cand f name pdir plug lib native=0
  case "$rid:$(uname -m)" in osx-arm64:arm64 | osx-x64:x86_64) native=1 ;; esac
  mkdir -p "$dist/Frameworks" "$dist/obs-plugins"

  # Locate the OBS build tree prisc_obs_sys assembled (OUT_DIR/obs-build).
  # Several prisc_obs_sys-<hash>/out/obs-build dirs can accumulate (a restored
  # CI cache, prisc_obs_sys/build.rs changing across commits), so pick the
  # newest one that actually holds the built framework.
  # shellcheck disable=SC2045 # build dirs have no spaces; ls -t sorts by age
  for cand in $(ls -dt "$src"/build/prisc_obs_sys-*/out/obs-build 2>/dev/null); do
    if [ -d "$cand/libobs/$config/libobs.framework" ]; then obs_build="$cand"; break; fi
  done
  echo "OBS build tree: ${obs_build:-<none found>}"
  test -n "$obs_build"

  # 1. Executables
  while IFS= read -r f; do
    cp "$src/$f" "$dist/"
  done < <(recorder_binaries "$rid")

  # 2. libobs.framework + Metal/OpenGL graphics modules
  cp -R "$obs_build/libobs/$config/libobs.framework" "$dist/Frameworks/"
  cp -a "$obs_build/libobs-metal/$config/"*.dylib "$dist/Frameworks/" 2>/dev/null || true
  cp -a "$obs_build/libobs-opengl/$config/"*.dylib "$dist/Frameworks/" 2>/dev/null || true

  # 3. Prebuilt dependency dylibs (FFmpeg, x264, ...). Taken from the profile
  #    dir, NOT from crates/obs-studio/.deps: build.rs stages them there
  #    (stage_macos_dylibs) with an @loader_path rpath and a fresh ad-hoc
  #    signature, so they are self-resolving for any consumer that dlopens
  #    them, and the dev tree and this bundle carry byte-identical files.
  #    `cp -a` keeps the versioned aliases as symlinks. The app's own
  #    libprisc_hotkeys.dylib shares the profile dir under build.sh.
  find "$src" -maxdepth 1 -name '*.dylib' ! -name 'libprisc_*' -exec cp -a {} "$dist/Frameworks/" \;
  for f in libavcodec.dylib libavformat.dylib libavutil.dylib libx264.dylib; do
    require "Frameworks/$f"
  done

  # 4. Plugins (.plugin bundles), preserving the <name>/RelWithDebInfo layout
  #    the runtime plugin-path template expects.
  for pdir in "$obs_build/plugins/"*/; do
    name=$(basename "$pdir")
    plug="$pdir$config/$name.plugin"
    if [ -d "$plug" ]; then
      mkdir -p "$dist/obs-plugins/$name/$config"
      cp -R "$plug" "$dist/obs-plugins/$name/$config/"
    fi
  done

  # 5. Relocate: add an @executable_path-relative rpath, drop the absolute
  #    build-tree rpaths, then ad-hoc re-sign (install_name_tool invalidates
  #    the signature on Apple Silicon).
  relocate() {
    local bin="$1" rp
    install_name_tool -add_rpath "@executable_path/Frameworks" "$bin" 2>/dev/null || true
    otool -l "$bin" | grep -A2 'cmd LC_RPATH' | awk '$1=="path"{print $2}' | while read -r rp; do
      case "$rp" in
        @*) : ;;
        *) install_name_tool -delete_rpath "$rp" "$bin" 2>/dev/null || true ;;
      esac
    done
    codesign --force --sign - "$bin" 2>/dev/null || true
  }
  while IFS= read -r f; do
    relocate "$dist/$f"
  done < <(recorder_binaries "$rid")
  # Plugin binaries are dlopen'd by prisc_recorder; @executable_path in them
  # resolves to prisc_recorder, so the same rpath reaches the Frameworks dir
  # beside it.
  find "$dist/obs-plugins" -type f -path '*/Contents/MacOS/*' | while read -r pb; do
    install_name_tool -add_rpath "@executable_path/Frameworks" "$pb" 2>/dev/null || true
    codesign --force --sign - "$pb" 2>/dev/null || true
  done

  # 6. Verify relocation: relative rpaths only, no build-tree leakage.
  echo "=== prisc_recorder LC_RPATH ==="
  otool -l "$dist/prisc_recorder" | grep -A2 'cmd LC_RPATH' | awk '$1=="path"{print "  rpath:",$2}'
  # tail skips otool's header line, which echoes the (absolute) path of the binary itself.
  if otool -l "$dist/prisc_recorder" | tail -n +2 | grep -Eq "/Users/|obs-build|/\.deps/"; then
    echo "::error::prisc_recorder still references an absolute build-tree path"
    otool -l "$dist/prisc_recorder" | tail -n +2 | grep -E "/Users/|obs-build|/\.deps/" || true
    exit 1
  fi
  codesign -v "$dist/prisc_recorder" || { echo "::error::prisc_recorder signature invalid"; exit 1; }

  # 7. Verify prisc_vid2gif (links the bundled FFmpeg dylibs). The arch the
  #    host runs gets a real conversion against a repo fixture; a
  #    cross-compiled slice can only be checked for presence.
  if [ "$native" = 1 ]; then
    local tmp gif holder
    tmp=$(mktemp -d)
    gif=$tmp/smoke.gif
    # stdin held open, since EOF on it cancels the conversion, by a writer
    # that is stopped right after. A process substitution would do it too,
    # but under bash 3.2 its sleep keeps this script's stdout open, so a
    # caller reading that through a pipe waits the sleep out.
    mkfifo "$tmp/stdin"
    sleep 600 > "$tmp/stdin" 2> /dev/null &
    holder=$!
    "$dist/prisc_vid2gif" "$root/crates/prisc_vid2gif/tests/fixtures/clip.mp4" "$gif" --quality fair < "$tmp/stdin"
    kill "$holder" 2> /dev/null || true
    [ "$(head -c 6 "$gif")" = "GIF89a" ]
    echo "prisc_vid2gif GIF toolchain OK"
  fi

  # 8. Verify the dep dylibs are self-resolving: @loader_path rpath and a
  #    valid signature on every real file (the symlink aliases resolve to
  #    them), and, on the native slice, an actual dlopen from a process that
  #    is NOT prisc_recorder. libavformat has the widest @rpath fan-out in the
  #    payload (avcodec, swresample, avutil, rist, srt, mbedtls x3, x264), so
  #    if it loads, everything does.
  find "$dist/Frameworks" -maxdepth 1 -type f -name 'lib*.dylib' ! -name 'libobs-*.dylib' | while read -r lib; do
    if ! otool -l "$lib" | grep -A2 'cmd LC_RPATH' | grep -q 'path @loader_path'; then
      echo "::error::$(basename "$lib") has no @loader_path rpath"; exit 1
    fi
    codesign -v "$lib" || { echo "::error::$(basename "$lib") signature invalid"; exit 1; }
  done
  if [ "$native" = 1 ]; then
    python3 -c "import ctypes, sys; [ctypes.CDLL(p) for p in sys.argv[1:]]" \
      "$dist/Frameworks/libavformat.dylib" \
      "$dist/Frameworks/libavfilter.dylib" \
      "$dist/Frameworks/libavdevice.dylib"
    echo "dep dylibs dlopen OK from a foreign host"
  fi

  echo "Frameworks:"; ls "$dist/Frameworks"
  echo "Plugins:"; ls "$dist/obs-plugins"
}

case "$rid" in
  win-*) stage_windows ;;
  osx-*) stage_macos ;;
  linux-*) "$root/tools/linux-build/stage-bundle.sh" "$src" "$dist" ;;
esac

# GPL-2.0: the license text travels with every distributed copy.
cp "$root/crates/prisc_recorder/LICENSE" "$dist/LICENSE.txt"

echo "Staged contents:"; ls "$dist"
echo "Bundle size:"; du -sh "$dist"
