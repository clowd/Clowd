//! Helpers shared by the workspace's build scripts.
//!
//! Today this is only the Linux runtime bundle: Windows and macOS get FFmpeg
//! from the prebuilt obs-deps archives that OBS's own CMake configure
//! downloads, and their build scripts keep their original, self-contained
//! logic. Linux has no obs-deps bundle, so the workspace fetches a pinned
//! FFmpeg itself — and because obs-sys (libobs + plugins), ffmpeg-sys (vid2gif)
//! and the staging scripts must all agree on the *same* copy, the pin and the
//! code that materialises it live here once rather than in four `build.rs`
//! files that could drift apart.
//!
//! The crate compiles on every host (it is a plain build-dependency); only
//! Linux build scripts call into [`linux`].

pub mod linux;
