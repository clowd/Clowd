# shellcheck shell=bash
# The one list of what a Prisc package ships, sourced by the other scripts in
# this directory (stage-app.sh, stage-recorder.sh, verify-package.sh), which
# build.sh and the CI workflows call. Adding a binary here adds it to the
# cargo package list, the staging, the Sentry debug-file upload and the
# package check at once.
#
# Every function takes the .NET RID (win-x64, win-arm64, osx-x64, osx-arm64,
# linux-x64, linux-arm64).

# The cargo target triple a RID builds for.
rid_triple() {
  case "$1" in
    win-x64) echo x86_64-pc-windows-msvc ;;
    win-arm64) echo aarch64-pc-windows-msvc ;;
    osx-x64) echo x86_64-apple-darwin ;;
    osx-arm64) echo aarch64-apple-darwin ;;
    linux-x64) echo x86_64-unknown-linux-gnu ;;
    linux-arm64) echo aarch64-unknown-linux-gnu ;;
    *) echo "unsupported RID: $1" >&2; return 1 ;;
  esac
}

rid_exe() {
  case "$1" in win-*) echo .exe ;; *) echo "" ;; esac
}

# Whether prisc_ai builds for this RID. Upstream ONNX Runtime dropped macOS
# x86_64, so ort has no binaries for it: the app grays the AI effects out there
# and the overlay reports OCR as unavailable.
rid_has_ai() {
  [ "$1" != osx-x64 ]
}

# Cargo packages of the app's Rust half (everything but the recorder stack).
# The shell extension is Windows only and is staged by CI under a versioned
# name next to its MSIX (build-rust.yml), so it is built here but not part of
# app_files.
app_packages() {
  local rid=$1
  echo prisc_capture prisc_hotkeys
  # prisc_scroll_driver is a compile_error! on Linux (no backend there).
  case "$rid" in linux-*) ;; *) echo prisc_scroll_driver ;; esac
  if rid_has_ai "$rid"; then echo prisc_ai; fi
  case "$rid" in win-*) echo prisc_shell_ext ;; esac
}

# The app's Rust binaries, as named in the cargo profile dir. They ship beside
# Prisc.Ui, which (with the overlay, for prisc_scroll_driver and prisc_ai)
# resolves them as its siblings. One per line.
app_binaries() {
  local rid=$1 exe
  exe=$(rid_exe "$rid")
  echo "prisc_capture$exe"
  case "$rid" in linux-*) ;; *) echo "prisc_scroll_driver$exe" ;; esac
  # The global hotkey library Prisc.Ui P/Invokes (HotkeysLibraryLocator).
  # Linux has no hotkey backend yet and Prisc.Ui never loads it there.
  case "$rid" in
    win-*) echo prisc_hotkeys.dll ;;
    osx-*) echo libprisc_hotkeys.dylib ;;
  esac
  if rid_has_ai "$rid"; then echo "prisc_ai$exe"; fi
}

# Binaries built from the app packages that have Sentry debug files: the
# shipped ones plus the shell extension.
app_symbolicated_binaries() {
  app_binaries "$1"
  case "$1" in win-*) echo prisc_shell_ext.dll ;; esac
}

# Cargo packages of the recorder stack. prisc_share_region is not built on
# Linux (no Linux UI; see its README).
recorder_packages() {
  echo prisc_recorder prisc_vid2gif
  case "$1" in linux-*) ;; *) echo prisc_share_region ;; esac
}

# The recorder's executables, which ship in recorder/ beside Prisc.Ui with the
# libobs runtime the stage scripts add around them. One per line.
recorder_binaries() {
  local exe
  exe=$(rid_exe "$1")
  recorder_packages "$1" | tr ' ' '\n' | sed "s/\$/$exe/"
  echo "obs-ffmpeg-mux$exe"
}

# `-p a -p b ...` for a package list on stdin.
as_cargo_args() {
  tr ' ' '\n' | sed '/^$/d; s/^/-p /' | tr '\n' ' '
}

# Copies the VC++ runtime (msvcp140/vcruntime140) for a win-* RID into $2,
# from the newest Visual Studio redist installed. Everything /MD needs it
# app-local: prisc_ai (its build.rs swaps it off +crt-static to match pyke's
# ONNX Runtime libs) and the CMake-built libobs, plugins and FFmpeg DLLs. These
# are not OS components, so shipping them is the only way to be sure they are
# there. Fails when the redist is missing, unless VCRT_OPTIONAL=1 (build.sh: a
# dev machine runs fine on its system-wide redist).
copy_vc_runtime() {
  local rid=$1 dest=$2 arch crt f
  arch=${rid#win-}
  crt=$(find "/c/Program Files/Microsoft Visual Studio" -type d \
    -path "*/VC/Redist/MSVC/*/$arch/Microsoft.VC*.CRT" 2>/dev/null | sort | tail -1 || true)
  if [ -z "$crt" ]; then
    if [ "${VCRT_OPTIONAL:-0}" = 1 ]; then
      echo "warning: VC++ redist ($arch) not found; $dest needs msvcp140/vcruntime140 at runtime" >&2
      return 0
    fi
    echo "::error::VC++ redist ($arch) not found under /c/Program Files/Microsoft Visual Studio" >&2
    return 1
  fi
  echo "VC++ redist: $crt"
  for f in msvcp140.dll msvcp140_1.dll vcruntime140.dll vcruntime140_1.dll; do
    if [ -f "$crt/$f" ]; then cp "$crt/$f" "$dest/"; fi
  done
  # The two every-arch dlls must have made it; fail here rather than with a
  # cryptic loader error on user machines.
  test -f "$dest/msvcp140.dll" && test -f "$dest/vcruntime140.dll"
}
