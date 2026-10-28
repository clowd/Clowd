#!/usr/bin/env bash
# Release build pipeline for Prisc.
#
# Usage: ./build.sh <version>
#
# 1. dotnet publish Prisc.Ui (Release, self-contained, host RID) to build/<version>/<rid>
# 2. cargo build --release of the app crates (prisc_capture, the scrolling
#    capture driver, the prisc_hotkeys global hotkey library Prisc.Ui
#    P/Invokes, and the prisc_ai inference binary the overlay spawns for OCR
#    and Prisc.VideoSDK spawns for the AI effects)
# 3. copy those binaries next to the published Prisc.Ui executable, where
#    release binary discovery expects them
# 4. cargo build --release of the recorder stack (prisc_recorder,
#    prisc_share_region, prisc_vid2gif, plus libobs via CMake: 20-40 min cold,
#    see BUILDING.md for its prerequisites) and copy its runtime tree into
#    recorder/ beside Prisc.Ui
#
# Works on macOS/Linux and on Windows under git bash.
set -euo pipefail

if [ $# -ne 1 ] || [ -z "$1" ]; then
    echo "Usage: $0 <version>" >&2
    echo "Example: $0 1.2.3" >&2
    exit 1
fi
VERSION="$1"

ROOT="$(cd "$(dirname "$0")" && pwd)"

# Host detection -> .NET RID + exe suffix
EXE=""
case "$(uname -s)" in
    Darwin)
        case "$(uname -m)" in
            arm64) RID="osx-arm64" ;;
            *)     RID="osx-x64" ;;
        esac
        ;;
    Linux)
        RID="linux-x64"
        ;;
    MINGW*|MSYS*)
        RID="win-x64"
        EXE=".exe"
        ;;
    *)
        echo "error: unsupported platform: $(uname -s)" >&2
        exit 1
        ;;
esac

OUT="$ROOT/build/$VERSION/$RID"

echo "==> Publishing Prisc.Ui ($RID, version $VERSION)"
dotnet publish "$ROOT/ui/Prisc.Ui/Prisc.Ui.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile="true" \
    -p:Version="$VERSION" \
    -o "$OUT"

echo "==> Building the Rust binaries (release)"
# An explicit package list, never the whole workspace: the workspace also holds
# the recorder stack, which builds libobs and is built separately below.
# prisc_scroll_driver is a compile_error! on Linux (no backend there).
CARGO_PKGS=(-p prisc_capture -p prisc_hotkeys)
if [ "$RID" != "linux-x64" ]; then
    CARGO_PKGS+=(-p prisc_scroll_driver)
fi
# prisc_ai only builds where ONNX Runtime still exists: upstream
# dropped macOS x86_64, so Intel macs skip it (the app grays the AI
# effects out there and OCR reports as unavailable) — matches build-rust.yml's
# `ai: false` on the osx-x64 leg.
if [ "$RID" != "osx-x64" ]; then
    CARGO_PKGS+=(-p prisc_ai)
fi
# The shell extension is built for parity with the CI package (it is not copied
# into the output below; the MSIX packaging lives in CI).
case "$RID" in
    win-*) CARGO_PKGS+=(-p prisc_shell_ext) ;;
esac
# stamps the Sentry release name so the capturer and the scrolling capture driver
# report against the same release as the shell (crates/prisc_rust_core/src/telemetry.rs)
PRISC_VERSION="$VERSION" cargo build --release --manifest-path "$ROOT/Cargo.toml" "${CARGO_PKGS[@]}"

echo "==> Copying capture binaries into publish output"
cp "$ROOT/target/release/prisc_capture$EXE" "$OUT/"
# Beside the overlay, which is where CaptureBinaryLocator.ResolveScrollDriver
# looks for it.
if [ "$RID" != "linux-x64" ]; then
    cp "$ROOT/target/release/prisc_scroll_driver$EXE" "$OUT/"
fi
# The global hotkey library, beside Prisc.Ui where HotkeysLibraryLocator looks for it.
# Linux has no hotkey backend yet and Prisc.Ui never loads the library there, so
# nothing ships for it.
case "$RID" in
    win-*) cp "$ROOT/target/release/prisc_hotkeys.dll" "$OUT/" ;;
    osx-*) cp "$ROOT/target/release/libprisc_hotkeys.dylib" "$OUT/" ;;
esac
if [ "$RID" != "osx-x64" ]; then
    # The AI inference binary, beside Prisc.Ui where AiLoader looks for it
    # and beside the overlay, which spawns it for OCR. ONNX Runtime is
    # statically linked by the ort crate;
    # on Windows the hardware execution providers still need dylibs beside the
    # exe (ort's copy-dylibs staged them into target/release during the build).
    cp "$ROOT/target/release/prisc_ai$EXE" "$OUT/"
    if [[ "$RID" == win-* ]]; then
        cp "$ROOT/target/release/DirectML.dll" "$OUT/"
        # prisc_ai is /MD (see its build.rs) and imports
        # msvcp140/vcruntime140 — app-local like CI ships them. Best
        # effort here: a dev machine runs fine on its system-wide redist.
        CRT_DIR=$(find "/c/Program Files/Microsoft Visual Studio" -type d \
            -path "*/VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT" 2>/dev/null | sort | tail -1)
        if [ -n "$CRT_DIR" ]; then
            for f in msvcp140.dll msvcp140_1.dll vcruntime140.dll vcruntime140_1.dll; do
                if [ -f "$CRT_DIR/$f" ]; then cp "$CRT_DIR/$f" "$OUT/"; fi
            done
        else
            echo "warning: VC redist not found; prisc_ai needs msvcp140/vcruntime140 at runtime" >&2
        fi
    fi
    # GPL-3.0 license text + notice must travel with every distributed copy of
    # the AI inference binary (GPL sections 4/6).
    cp "$ROOT/crates/prisc_ai/LICENSE" "$OUT/prisc_ai.LICENSE.txt"
    cp "$ROOT/crates/prisc_ai/NOTICE.txt" "$OUT/prisc_ai.NOTICE.txt"
fi

echo "==> Building the recorder stack (release; libobs via CMake, 20-40 min cold)"
# Same packages as `cargo recr`, minus prisc_share_region on Linux (no Linux UI;
# it is not built there). Needs the obs-studio submodule, cmake >= 3.28 and
# LLVM/libclang (LIBCLANG_PATH on Windows); see BUILDING.md.
REC_PKGS=(-p prisc_recorder -p prisc_vid2gif)
if [ "$RID" != "linux-x64" ]; then
    REC_PKGS+=(-p prisc_share_region)
fi
cargo build --release --manifest-path "$ROOT/Cargo.toml" "${REC_PKGS[@]}"

echo "==> Copying the recorder runtime into publish output (recorder/)"
REC="$OUT/recorder"
SRC="$ROOT/target/release"
rm -rf "$REC"
mkdir -p "$REC"
# GPL-2.0 license text travels with the GPL'd recorder binaries.
cp "$ROOT/crates/prisc_recorder/LICENSE" "$REC/LICENSE.txt"
case "$RID" in
    win-*)
        # The runtime tree the recorder's build.rs assembled in target/release
        # (mirrors the Windows stage step in .github/workflows/build-recorder.yml).
        # An explicit list, not *.exe/*.dll: target/release also holds the app
        # binaries built above.
        for f in prisc_recorder.exe prisc_share_region.exe prisc_vid2gif.exe obs-ffmpeg-mux.exe; do
            cp "$SRC/$f" "$REC/"
        done
        for f in "$SRC"/obs-*-test.exe; do
            if [ -f "$f" ]; then cp "$f" "$REC/"; fi
        done
        # libobs, its graphics modules and the FFmpeg/x264 runtime DLLs. The app
        # crates' DLLs (prisc_hotkeys, prisc_shell_ext, DirectML) stay out.
        for f in "$SRC"/*.dll; do
            case "$(basename "$f")" in
                prisc_*.dll|DirectML.dll) ;;
                *) cp "$f" "$REC/" ;;
            esac
        done
        cp -R "$SRC/obs-plugins" "$SRC/data" "$REC/"
        # obs.dll and the plugins are built /MD by CMake, so the recorder ships
        # the VC++ CRT app-local as CI does. Best effort, as above.
        CRT_DIR=$(find "/c/Program Files/Microsoft Visual Studio" -type d \
            -path "*/VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT" 2>/dev/null | sort | tail -1)
        if [ -n "$CRT_DIR" ]; then
            for f in msvcp140.dll msvcp140_1.dll vcruntime140.dll vcruntime140_1.dll; do
                if [ -f "$CRT_DIR/$f" ]; then cp "$CRT_DIR/$f" "$REC/"; fi
            done
        else
            echo "warning: VC redist not found; the recorder needs msvcp140/vcruntime140 at runtime" >&2
        fi
        ;;
    linux-*)
        # The same items tools/linux-build/stage-bundle.sh copies. That script
        # also rewrites the RUNPATHs to $ORIGIN and enforces the portability
        # rules of the manylinux build image, so a portable bundle comes from
        # running it inside that image (see BUILDING.md); this copy runs on the
        # build machine.
        cp -a "$SRC/prisc_recorder" "$SRC/prisc_vid2gif" "$SRC/obs-ffmpeg-mux" "$REC/"
        cp -a "$SRC"/lib*.so.* "$REC/"
        cp -a "$SRC/obs-plugins" "$SRC/data" "$REC/"
        ;;
    osx-*)
        # The macOS recorder bundle (libobs.framework, graphics modules, FFmpeg
        # dylibs, plugins, rpath rewrites and ad-hoc re-signing) is assembled by
        # the stage step in .github/workflows/build-recorder.yml, which is too
        # involved to duplicate here. Only the executables are copied; use a CI
        # recorder-osx-* artifact for a runnable recorder/ folder.
        for f in prisc_recorder prisc_share_region prisc_vid2gif obs-ffmpeg-mux; do
            if [ -f "$SRC/$f" ]; then cp "$SRC/$f" "$REC/"; fi
        done
        echo "warning: recorder/ holds only the executables on macOS; see build-recorder.yml for the full stage" >&2
        ;;
esac

echo "Build complete: $OUT"
