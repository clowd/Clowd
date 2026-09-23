#!/usr/bin/env bash
# Installs the RUNTIME packages the Linux bundle needs on Debian/Ubuntu (no
# -dev packages, no FFmpeg), plus what verify-bundle.sh needs to run it
# headless (Xvfb and Mesa's software renderer). Used by CI's portability
# check on stock ubuntu-22.04 / ubuntu-24.04 runners; it doubles as the
# reference list for users. Must run as root (or via sudo).
#
# One entry per system SONAME the bundle's ELF files need (see the list
# stage-bundle.sh prints); glibc itself (libc, libm, libdl, libpthread, librt,
# libmvec, ld-linux) and libgcc_s are always present.
set -euo pipefail

runtime=(
  libx11-6 libx11-xcb1 libxrandr2 libxkbcommon0
  libxcb1 libxcb-composite0 libxcb-randr0 libxcb-render0 libxcb-shape0
  libxcb-shm0 libxcb-xfixes0 libxcb-xinerama0 libxcb-xinput0
  libegl1 libopengl0 libglx0 libdrm2 libwayland-client0 libwayland-egl1
  libglib2.0-0 libpipewire-0.3-0 libpulse0 libv4l-0 libudev1 libuuid1
  libjansson4 libva2 libva-drm2 libpci3 zlib1g
)
headless_test=(xvfb xauth libgl1-mesa-dri libegl-mesa0)

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
# Ubuntu 24.04 / Debian 13 renamed several libraries for the 64-bit time_t
# transition (libglib2.0-0 -> libglib2.0-0t64, ...); prefer the t64 name
# wherever the distro has one.
pkgs=()
for p in "${runtime[@]}"; do
  if apt-cache show "${p}t64" > /dev/null 2>&1; then pkgs+=("${p}t64"); else pkgs+=("$p"); fi
done
apt-get install -y -qq --no-install-recommends "${pkgs[@]}" "${headless_test[@]}" > /dev/null
echo "Installed: ${pkgs[*]} ${headless_test[*]}"
