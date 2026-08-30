//! `gxi` — the capture overlay's thin GPU abstraction.
//!
//! Concrete structs, zero dynamic dispatch, compile-time backend selection.
//! Both backends expose the *same* public API (identical type names and
//! signatures, enforced by the CI compile matrix building both OSes), so
//! the rest of the crate is written against `crate::gxi::*` and never
//! names a backend.
//!
//! Backend selection: Windows ships the `d3d11` backend and macOS ships
//! the `metal` backend. No other platform has a backend: the overlay
//! only supports these two, and any other target fails to compile here.
//! Exactly one backend is compiled into any given binary.

pub mod types;

#[cfg(windows)]
mod d3d11;
#[cfg(target_os = "macos")]
mod metal;

#[cfg(windows)]
pub use self::d3d11::*;
#[cfg(target_os = "macos")]
pub use self::metal::*;
pub use types::*;
