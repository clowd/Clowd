#!/usr/bin/env bash
# Release build pipeline for Clowd.
#
# Usage: ./build.sh <version>
#
# 1. dotnet publish Clowd.Ui (Release, framework-dependent, host RID) to build/<version>/<rid>
# 2. cargo build --release (builds clowd_capture_wgpu)
# 3. copy the capture binary next to the published Clowd.Ui executable
#    (release binary discovery expects it there — see clowd_ui/MIGRATION.md)
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

echo "==> Building clowd_capture_wgpu (release)"
# stamps the Sentry release name so the capturer reports against the same release
# as the shell (telemetry/crash.rs)
CLOWD_VERSION="$VERSION" cargo build --release --manifest-path "$ROOT/Cargo.toml"

echo "==> Copying capture binary into publish output"
cp "$ROOT/target/release/clowd_capture_wgpu$EXE" "$OUT/"

echo "Build complete: $OUT"
