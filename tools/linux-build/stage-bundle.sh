#!/usr/bin/env bash
# Assembles the self-contained Linux bundle from a cargo profile dir and checks
# that it is relocatable and portable. Used by the linux job in
# .github/workflows/build.yml (inside the tools/linux-build image), and
# runnable by hand in that image:
#
#   tools/linux-build/stage-bundle.sh target/release dist/obs-express-linux-x64
#
# Checks, each fatal:
#   - every required binary / library / plugin / data file is present;
#   - every RUNPATH is $ORIGIN-relative and no build-tree path survives;
#   - no ELF file needs a GLIBC symbol version newer than GLIBC_MAX (the
#     build image's glibc, 2.34), which is what makes the bundle run on
#     Ubuntu 22.04+, Debian 12+, Fedora / RHEL 9+;
#   - no ELF file depends on libstdc++ or on a system FFmpeg / x264 / Mbed TLS.
set -euo pipefail

src=${1:?usage: stage-bundle.sh <cargo profile dir> <dist dir>}
dist=${2:?usage: stage-bundle.sh <cargo profile dir> <dist dir>}
GLIBC_MAX=${GLIBC_MAX:-2.34}

rm -rf "$dist"; mkdir -p "$dist"

# 1. Executables, the shared libraries build.rs staged under their SONAMEs
#    (libobs.so.30, libobs-opengl.so.30, the FFmpeg set) and the plugin/data
#    trees. Explicit items only, so cargo's build junk (deps/, build/, *.rlib,
#    *.d) stays behind. clowd_share_region is not built on Linux (the CI
#    build excludes it from the workspace), so it is not part of the bundle.
cp -a "$src/obs-express" "$src/vid2gif" "$src/obs-ffmpeg-mux" "$dist/"
cp -a "$src"/lib*.so.* "$dist/"
cp -a "$src/obs-plugins" "$src/data" "$dist/"

for required in obs-express vid2gif obs-ffmpeg-mux \
    libobs.so.30 libobs-opengl.so.30 \
    libavcodec.so.61 libavformat.so.61 libavutil.so.59 libavfilter.so.10 \
    libavdevice.so.61 libswscale.so.8 libswresample.so.5 \
    obs-plugins/obs-ffmpeg.so obs-plugins/obs-x264.so obs-plugins/obs-outputs.so \
    obs-plugins/linux-capture.so obs-plugins/linux-pipewire.so \
    obs-plugins/linux-pulseaudio.so obs-plugins/linux-v4l2.so \
    obs-plugins/image-source.so obs-plugins/obs-filters.so \
    data/libobs/default.effect data/obs-plugins/obs-ffmpeg; do
  if [ ! -e "$dist/$required" ]; then
    echo "::error::required file missing from dist: $required"
    exit 1
  fi
done

# 2. Relocate: the Rust executables carry $ORIGIN followed by absolute
#    build-tree entries (so they, and the cargo test binaries in deps/, also
#    resolve libobs/FFmpeg in place). The shipped copies keep only $ORIGIN.
#    Everything else was staged by build.rs with a relative RUNPATH already.
# shellcheck disable=SC2016 # a literal $ORIGIN, for the loader
for exe in obs-express vid2gif; do
  patchelf --set-rpath '$ORIGIN' "$dist/$exe"
done

cat > "$dist/README.txt" <<'README'
obs-express (Linux x64, self-contained)

libobs, the OpenGL renderer, the OBS plugins (obs-plugins/), their data
(data/) and the FFmpeg shared libraries are bundled alongside the binaries;
x264 and Mbed TLS are linked statically into the plugins that use them.
Every ELF file resolves its dependencies through an $ORIGIN-relative
RUNPATH, so this directory can be moved anywhere and run in place.

Requires glibc >= 2.34 (Ubuntu 22.04+, Debian 12+, Fedora / RHEL 9+) and
the usual desktop libraries: X11/xcb, Wayland, EGL/OpenGL (Mesa or a vendor
driver), PipeWire, PulseAudio (or pipewire-pulse), libv4l2, udev, glib
(>= 2.68), jansson, xkbcommon, and libva/libpci/libdrm.

X11: --monitor / --region work as on Windows and macOS.
Wayland: the desktop portal's screen-share dialog picks the monitor or
window (needs xdg-desktop-portal with a backend, plus PipeWire); --monitor
and --region are rejected.
README

# 3. Per-ELF checks: relative RUNPATHs, no build-tree paths, glibc ceiling,
#    no forbidden system libraries.
bad=0
max_glibc=0.0
while IFS= read -r -d '' f; do
  [ "$(head -c 4 "$f" | od -An -c | tr -d ' ')" = '177ELF' ] || continue
  rel=${f#"$dist"/}
  dyn=$(readelf -d "$f")
  paths=$(echo "$dyn" | grep -E '\((RUNPATH|RPATH)\)' | sed -E 's/.*\[(.*)\]/\1/' || true)
  printf '  %-40s %s\n' "$rel" "${paths:-<none>}"
  for p in $(echo "$paths" | tr ':' ' '); do
    # shellcheck disable=SC2016 # matching the literal $ORIGIN token
    case "$p" in
      '$ORIGIN'|'$ORIGIN/'*) : ;;
      *) echo "::error::$rel has a non-relative RUNPATH entry: $p"; bad=1 ;;
    esac
  done
  if grep -Eq "/home/runner|/__w/|/work/|$PWD|/target/|/\.deps/|rundir" <<< "$dyn"; then
    echo "::error::$rel references a build-tree path"; bad=1
  fi
  needed=$(echo "$dyn" | grep NEEDED | sed -E 's/.*\[(.*)\]/\1/' || true)
  if grep -Eq '^lib(stdc\+\+|x264|mbed(tls|crypto|x509))\.' <<< "$needed"; then
    echo "::error::$rel needs a library that must be bundled or static:"; grep -E '^lib(stdc|x264|mbed)' <<< "$needed"; bad=1
  fi
  v=$(objdump -T "$f" 2>/dev/null | grep -oE 'GLIBC_2\.[0-9]+' | sed 's/GLIBC_//' | sort -t. -k2 -n -u | tail -1 || true)
  if [ -n "$v" ]; then
    if [ "$(printf '%s\n%s\n' "$GLIBC_MAX" "$v" | sort -t. -k2 -n | tail -1)" != "$GLIBC_MAX" ]; then
      echo "::error::$rel needs GLIBC_$v, newer than the GLIBC_$GLIBC_MAX floor"; bad=1
    fi
    if [ "$(printf '%s\n%s\n' "$max_glibc" "$v" | sort -t. -k2 -n | tail -1)" = "$v" ]; then max_glibc=$v; fi
  fi
done < <(find "$dist" -type f -print0)
[ "$bad" -eq 0 ]
echo "Newest GLIBC symbol version needed: GLIBC_$max_glibc (ceiling GLIBC_$GLIBC_MAX)"

echo "System libraries needed (outside the bundle):"
# (readelf fails on the non-ELF files, such as data/; that is expected.)
find "$dist" -type f -print0 | xargs -0 -n1 sh -c 'readelf -d "$0" 2>/dev/null || true' \
  | grep NEEDED | sed -E 's/.*\[(.*)\]/\1/' | sort -u \
  | grep -vE '^lib(obs|obs-opengl|av[a-z]*|sw[a-z]*|postproc)\.so' | sed 's/^/  /'

echo "Staged contents:"; ls -la "$dist"
echo "Plugins:"; ls "$dist/obs-plugins"
echo "Bundle size:"; du -sh "$dist"
