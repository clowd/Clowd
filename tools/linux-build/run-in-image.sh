#!/usr/bin/env bash
# Runs a command inside the Linux build image (tools/linux-build/Dockerfile)
# with the current checkout bind-mounted at the same path, so paths printed by
# the build match the host's. The cargo registry/git caches are mounted from
# the host's ~/.cargo, so CI's actions/cache of those dirs applies. Build
# outputs (target/, obs-studio/.deps) land in the checkout as usual.
#
#   docker build -t obs-express-linux tools/linux-build
#   tools/linux-build/run-in-image.sh cargo build --release
#
# IMAGE overrides the image tag (default obs-express-linux).
set -euo pipefail

image=${IMAGE:-obs-express-linux}
root=$(git rev-parse --show-toplevel)
mkdir -p "$HOME/.cargo/registry" "$HOME/.cargo/git"

exec docker run --rm \
  -v "$root:$root" -w "$root" \
  -v "$HOME/.cargo/registry:/root/.cargo/registry" \
  -v "$HOME/.cargo/git:/root/.cargo/git" \
  -e CARGO_TERM_COLOR -e GITHUB_ACTIONS \
  "$image" bash -euo pipefail -c "$*"
