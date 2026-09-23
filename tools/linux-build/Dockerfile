# Linux build image for obs-express (x86_64): manylinux_2_34 = AlmaLinux 9,
# glibc 2.34. Everything built here runs on any distro with glibc >= 2.34
# (Ubuntu 22.04+, Debian 12+, Fedora / RHEL 9+), which the Ubuntu 24.04 image
# (glibc 2.38) could not offer. CI builds inside this exact image (see the
# linux job in .github/workflows/build.yml); for a local build:
#
#   docker build -t obs-express-linux tools/linux-build
#   docker run --rm -it -v "$PWD":/src -w /src obs-express-linux cargo build --release
#
# The image carries the toolchain and the -devel headers of the system
# libraries the bundle links at runtime (X11/xcb, EGL/GL, Wayland, PipeWire,
# PulseAudio, libv4l2, udev, glib, jansson, curl, libva/libpci/libdrm).
# FFmpeg, x264 and mbedtls are deliberately absent: obs-sys downloads or
# builds pinned copies of those (crates/build-support, crates/obs-sys/build.rs)
# so no distro-specific SONAME of theirs ends up in the bundle.
#
# The manylinux base provides GCC from gcc-toolset (its libstdc++ extras are
# linked statically, so the output only needs the system libstdc++) and a
# current CMake. Its glib is 2.68; OBS's linux-pipewire asks for 2.76 only
# for the inline g_clear_fd(), which obs-sys backfills (crates/obs-sys/cmake).
ARG BASE=quay.io/pypa/manylinux_2_34_x86_64
FROM ${BASE}

RUN dnf install -y --setopt=install_weak_deps=False \
      ninja-build nasm patchelf git curl xz bzip2 file which perl rsync \
      clang clang-devel llvm-devel \
      extra-cmake-modules libuuid-devel zlib-devel \
      libX11-devel libxcb-devel xcb-util-devel libXcomposite-devel libXinerama-devel \
      libXrandr-devel libXfixes-devel libXext-devel \
      mesa-libEGL-devel mesa-libGL-devel libglvnd-devel \
      wayland-devel libxkbcommon-devel \
      pipewire-devel pulseaudio-libs-devel alsa-lib-devel libv4l-devel systemd-devel \
      jansson-devel libcurl-devel libdrm-devel libva-devel pciutils-devel \
      uthash-devel glib2-devel \
      xorg-x11-server-Xvfb xorg-x11-xauth mesa-dri-drivers mesa-libEGL \
    && dnf clean all

# Rust (stable, minimal profile). CARGO_HOME stays at the default so CI can
# mount a cached registry at /root/.cargo/registry.
RUN curl -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal --default-toolchain stable
ENV PATH=/root/.cargo/bin:$PATH

# The checkout is bind-mounted and owned by another uid; let git (used by
# obs-sys to fetch pinned x264 / SIMDe, and by OBS's version discovery)
# operate on it.
RUN git config --system --add safe.directory '*'
