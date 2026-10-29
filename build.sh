#!/usr/bin/env bash
# Local release build of a Prisc package for the host platform, the same
# directory layout CI's package job assembles (ci.yml).
#
# Usage: ./build.sh <version>
#
# 1. dotnet publish Prisc.Ui and Prisc.VideoRender (Release, self-contained,
#    host RID, multi-file like build-ui.yml: vpk pack needs that) into
#    build/<version>/<rid>
# 2. cargo build --release of the app crates and stage them beside Prisc.Ui
#    (tools/staging/stage-app.sh, the same script build-rust.yml runs)
# 3. cargo build --release of the recorder stack (prisc_recorder,
#    prisc_share_region, prisc_vid2gif, plus libobs via CMake: 20-40 min cold,
#    see BUILDING.md for its prerequisites) and stage its runtime into
#    recorder/ (tools/staging/stage-recorder.sh, as build-recorder.yml does)
# 4. check the result with tools/staging/verify-package.sh, as CI does
#
# The shell extension's sparse MSIX and the macOS apphost SDK stamp are CI
# only (build-rust.yml, build-ui.yml).
#
# Works on macOS (arm64, x64), Linux (x64, arm64) and on Windows (x64, ARM64)
# under git bash. On Linux the recorder is staged by stage-bundle.sh with CI's
# bundle rules (only the glibc ceiling is relaxed to this machine's), so it
# needs patchelf and binutils, and a build that links libstdc++ or a system
# FFmpeg / x264 / Mbed TLS fails there; see BUILDING.md.
set -euo pipefail

case "${1:-}" in
    -h|--help)
        sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//'
        exit 0
        ;;
esac
if [ $# -ne 1 ] || [ -z "$1" ]; then
    echo "Usage: $0 <version>" >&2
    echo "Example: $0 1.2.3" >&2
    exit 1
fi
VERSION="$1"

ROOT="$(cd "$(dirname "$0")" && pwd)"
STAGING="$ROOT/tools/staging"
# shellcheck source=tools/staging/shipped.sh
. "$STAGING/shipped.sh"

# Host detection -> .NET RID
case "$(uname -s)" in
    Darwin)
        case "$(uname -m)" in
            arm64) RID="osx-arm64" ;;
            *)     RID="osx-x64" ;;
        esac
        ;;
    Linux)
        case "$(uname -m)" in
            aarch64|arm64) RID="linux-arm64" ;;
            *)             RID="linux-x64" ;;
        esac
        ;;
    MINGW*|MSYS*)
        # An x64 git bash on an ARM64 machine reports the emulated arch in
        # PROCESSOR_ARCHITECTURE and the real one in PROCESSOR_ARCHITEW6432.
        case "${PROCESSOR_ARCHITEW6432:-${PROCESSOR_ARCHITECTURE:-AMD64}}" in
            ARM64) RID="win-arm64" ;;
            *)     RID="win-x64" ;;
        esac
        ;;
    *)
        echo "error: unsupported platform: $(uname -s)" >&2
        exit 1
        ;;
esac
TRIPLE="$(rid_triple "$RID")"
# Both halves land in one profile dir whatever the host toolchain's default
# target is. --target only when the RID differs from that default: with it,
# cargo builds into target/<triple>/, and on macOS (where libobs builds in
# prisc_obs_sys's OUT_DIR) that would cost a second cold libobs build beside
# the one `cargo recr` keeps in target/release.
if [ "$TRIPLE" = "$(rustc -vV | sed -n 's/^host: //p')" ]; then
    TARGET_ARGS=()
    PROFILE="$ROOT/target/release"
else
    TARGET_ARGS=(--target "$TRIPLE")
    PROFILE="$ROOT/target/$TRIPLE/release"
fi
OUT="$ROOT/build/$VERSION/$RID"

echo "==> Publishing Prisc.Ui and Prisc.VideoRender ($RID, version $VERSION)"
rm -rf "$OUT"
for proj in Prisc.Ui Prisc.VideoRender; do
    dotnet publish "$ROOT/ui/$proj/$proj.csproj" \
        -c Release -r "$RID" --self-contained true \
        -p:Version="$VERSION" \
        -o "$OUT"
done

echo "==> Building the Rust binaries (release)"
# An explicit package list, never the whole workspace: the workspace also holds
# the recorder stack, which builds libobs and is built separately below.
# PRISC_VERSION stamps the Sentry release name so the Rust binaries report
# against the same release as the shell (crates/prisc_rust_core/src/telemetry.rs).
# shellcheck disable=SC2046 # word-split `-p <crate>` pairs
PRISC_VERSION="$VERSION" cargo build --release --manifest-path "$ROOT/Cargo.toml" ${TARGET_ARGS[@]+"${TARGET_ARGS[@]}"} \
    $("$STAGING/stage-app.sh" packages "$RID")
# The in-process interop library, each in a cargo invocation of its own: built
# beside the packages above, cargo would unify features across them and link
# Sentry into it (tools/staging/shipped.sh, app_packages_isolated). Then the
# same artifact checks build-rust.yml runs (exports, Sentry, dependencies).
while read -r args; do
    # shellcheck disable=SC2086 # word-split `-p <crate>`
    PRISC_VERSION="$VERSION" cargo build --release --manifest-path "$ROOT/Cargo.toml" ${TARGET_ARGS[@]+"${TARGET_ARGS[@]}"} $args
done < <("$STAGING/stage-app.sh" packages-isolated "$RID")
"$STAGING/stage-app.sh" verify-isolated "$RID" "$PROFILE"

echo "==> Staging the Rust binaries beside Prisc.Ui"
# Best effort on the VC++ runtime here: a dev machine runs fine on its
# system-wide redist.
VCRT_OPTIONAL=1 "$STAGING/stage-app.sh" stage "$RID" "$PROFILE" "$OUT"

echo "==> Building the recorder stack (release; libobs via CMake, 20-40 min cold)"
# Needs the obs-studio submodule, cmake >= 3.28 and LLVM/libclang
# (LIBCLANG_PATH on Windows); see BUILDING.md.
# shellcheck disable=SC2046 # word-split `-p <crate>` pairs
cargo build --release --manifest-path "$ROOT/Cargo.toml" ${TARGET_ARGS[@]+"${TARGET_ARGS[@]}"} \
    $(recorder_packages "$RID" | as_cargo_args)

echo "==> Staging the recorder runtime into recorder/"
case "$RID" in
    linux-*)
        # stage-bundle.sh enforces the glibc floor of the manylinux build image
        # (tools/linux-build/Dockerfile). Built on this machine, the bundle can
        # only promise this machine's glibc; a portable one comes from running
        # the build inside that image (see BUILDING.md).
        GLIBC_MAX="$(ldd --version | sed -n '1s/.* \([0-9][0-9.]*\)$/\1/p')"
        export GLIBC_MAX
        ;;
esac
VCRT_OPTIONAL=1 "$STAGING/stage-recorder.sh" "$RID" "$PROFILE" "$OUT/recorder"

"$STAGING/verify-package.sh" "$RID" "$OUT" --local

echo "Build complete: $OUT"
