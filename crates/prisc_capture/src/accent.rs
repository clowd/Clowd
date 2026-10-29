//! The accent colour maths the overlay paints with: `prisc_rust_core::color`, the one
//! implementation Prisc.Ui's `AccentColors` and `AccentTheme` also call (through prisc_interop), so
//! the overlay and the C# windows beside it derive the same colours from the same pick.
//!
//! The shell passes the accent exactly as the user chose it (`--accent-color`); the panel's fill is
//! [`ensure_contrast_with_white`] of it unless `--no-accent-contrast` is passed, and the hint comet
//! is [`comet_colors`] of the pick itself.

pub use prisc_rust_core::color::*;
