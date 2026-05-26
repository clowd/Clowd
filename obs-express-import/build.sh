#!/bin/bash
set -euo pipefail

echo "=== obs-express-rs build ==="

for tool in cmake cargo git xcodebuild; do
    command -v "$tool" >/dev/null || { echo "Missing: $tool"; exit 1; }
done

if [[ "$(uname)" == "Darwin" ]]; then
    if ! xcode-select -p | grep -q "Xcode.app"; then
        echo "Warning: Xcode.app not found. Metal renderer requires full Xcode (not just CLT)."
        echo "Install from App Store or developer.apple.com"
    fi
fi

echo "Initializing submodules..."
git submodule update --init --recursive

echo "Building workspace..."
cargo build --release

echo "=== Build complete ==="
echo "Binary: target/release/obs-express"
