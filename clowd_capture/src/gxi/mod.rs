//! `gxi` — the capture overlay's thin GPU abstraction.
//!
//! Concrete structs, zero dynamic dispatch, compile-time backend selection.
//! Every backend exposes the *same* public API (identical type names and
//! signatures, enforced by the CI compile matrix building all three OSes),
//! so the rest of the crate is written against `crate::gxi::*` and never
//! names a backend.
//!
//! Backend selection: Windows ships the `d3d11` backend, macOS ships the
//! `metal` backend, and Linux ships the `wgpu` backend (Vulkan, with GL
//! as an explicit fallback). No other platform has a backend: the overlay
//! only supports these three, and any other target fails to compile here.
//! Exactly one backend is compiled into any given binary.

pub mod types;

#[cfg(windows)]
mod d3d11;
#[cfg(target_os = "macos")]
mod metal;
#[cfg(target_os = "linux")]
mod wgpu;

#[cfg(windows)]
pub use self::d3d11::*;
#[cfg(target_os = "macos")]
pub use self::metal::*;
#[cfg(target_os = "linux")]
pub use self::wgpu::*;
pub use types::*;
