#!/usr/bin/env bash
# Release build pipeline for Clowd.
#
# Usage: ./build.sh <version>
#
# 1. dotnet publish Clowd.Ui (Release, framework-dependent, host RID) to build/<version>/<rid>
# 2. cargo build --release (builds clowd_capture_wgpu, the clowd_ocr recognizer
#    it spawns, and the scrolling capture driver)
# 3. copy the capture binaries next to the published Clowd.Ui executable
#    (release binary discovery expects them there — see clowd_ui/MIGRATION.md)
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

echo "==> Publishing Clowd.Ui ($RID, version $VERSION)"
dotnet publish "$ROOT/clowd_ui/Clowd.Ui/Clowd.Ui.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile="true" \
    -p:Version="$VERSION" \
    -o "$OUT"

echo "==> Building the Rust binaries (release)"
# stamps the Sentry release name so the capturer and the scrolling capture driver
# report against the same release as the shell (clowd_rust_core/src/telemetry.rs)
CLOWD_VERSION="$VERSION" cargo build --release --manifest-path "$ROOT/Cargo.toml"

echo "==> Copying capture binaries into publish output"
cp "$ROOT/target/release/clowd_capture_wgpu$EXE" "$OUT/"
# Beside the overlay, which is where CaptureBinaryLocator.ResolveScrollDriver
# looks for it.
cp "$ROOT/target/release/clowd_scroll_driver$EXE" "$OUT/"
# The AI effects binary, beside Clowd.Ui where TractnniLoader looks for it.
# (clowd_ocr has historically been missing from this dev script — CI packages
# it; not fixed here.) Note it also needs an ONNX Runtime dylib beside it (or
# ORT_DYLIB_PATH set) to actually run inference: CI downloads the official
# release archive into the package, this script does not.
cp "$ROOT/target/release/clowd_tractnni$EXE" "$OUT/"
# GPL-3.0 license text + notice must travel with every distributed copy of
# the AI effects binary (GPL sections 4/6).
cp "$ROOT/clowd_tractnni/LICENSE" "$OUT/clowd_tractnni.LICENSE.txt"
cp "$ROOT/clowd_tractnni/NOTICE.txt" "$OUT/clowd_tractnni.NOTICE.txt"

echo "Build complete: $OUT"
