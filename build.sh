#!/usr/bin/env bash
# Release build pipeline for Clowd.
#
# Usage: ./build.sh <version>
#
# 1. dotnet publish Clowd.Ui (Release, framework-dependent, host RID) to build/<version>/<rid>
# 2. cargo build --release (builds clowd_capture, the scrolling capture
#    driver, and the clowd_ai inference binary the overlay spawns for OCR and
#    Clowd.VideoSDK spawns for the AI effects)
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
# clowd_ai only builds where ONNX Runtime still exists: upstream
# dropped macOS x86_64, so Intel macs skip it (the app grays the AI
# effects out there and OCR reports as unavailable) — matches ci.yml's
# `ai: false` on the osx-x64 leg.
CARGO_EXCLUDE=()
if [ "$RID" = "osx-x64" ]; then
    CARGO_EXCLUDE=(--workspace --exclude clowd_ai)
fi
# stamps the Sentry release name so the capturer and the scrolling capture driver
# report against the same release as the shell (clowd_rust_core/src/telemetry.rs)
CLOWD_VERSION="$VERSION" cargo build --release --manifest-path "$ROOT/Cargo.toml" "${CARGO_EXCLUDE[@]}"

echo "==> Copying capture binaries into publish output"
cp "$ROOT/target/release/clowd_capture$EXE" "$OUT/"
# Beside the overlay, which is where CaptureBinaryLocator.ResolveScrollDriver
# looks for it.
cp "$ROOT/target/release/clowd_scroll_driver$EXE" "$OUT/"
if [ "$RID" != "osx-x64" ]; then
    # The AI inference binary, beside Clowd.Ui where AiLoader looks for it
    # and beside the overlay, which spawns it for OCR. ONNX Runtime is
    # statically linked by the ort crate;
    # on Windows the hardware execution providers still need dylibs beside the
    # exe (ort's copy-dylibs staged them into target/release during the build).
    cp "$ROOT/target/release/clowd_ai$EXE" "$OUT/"
    if [[ "$RID" == win-* ]]; then
        cp "$ROOT/target/release/DirectML.dll" "$OUT/"
        # clowd_ai is /MD (see its build.rs) and imports
        # msvcp140/vcruntime140 — app-local like CI ships them. Best
        # effort here: a dev machine runs fine on its system-wide redist.
        CRT_DIR=$(find "/c/Program Files/Microsoft Visual Studio" -type d \
            -path "*/VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT" 2>/dev/null | sort | tail -1)
        if [ -n "$CRT_DIR" ]; then
            for f in msvcp140.dll msvcp140_1.dll vcruntime140.dll vcruntime140_1.dll; do
                if [ -f "$CRT_DIR/$f" ]; then cp "$CRT_DIR/$f" "$OUT/"; fi
            done
        else
            echo "warning: VC redist not found; clowd_ai needs msvcp140/vcruntime140 at runtime" >&2
        fi
    fi
    # GPL-3.0 license text + notice must travel with every distributed copy of
    # the AI inference binary (GPL sections 4/6).
    cp "$ROOT/clowd_ai/LICENSE" "$OUT/clowd_ai.LICENSE.txt"
    cp "$ROOT/clowd_ai/NOTICE.txt" "$OUT/clowd_ai.NOTICE.txt"
fi

echo "Build complete: $OUT"
