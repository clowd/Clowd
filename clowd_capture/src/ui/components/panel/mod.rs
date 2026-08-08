//! Button panel that appears after a selection is finalised.
//!
//! Mirrors the C++ `clowd_capture_dx` button panel: SVG action buttons
//! (UPLOAD / EDIT / VIDEO / SCROLL / COPY / SAVE / RESET / EXIT — SCROLL
//! is Windows-only, see `model::NUM_SVG_BUTTONS`) plus a non-clickable
//! area indicator that shows the selected width×height with decorative
//! corner brackets.
//!
//! Pure layout/model logic only — GPU rendering lives in
//! [`crate::ui::gpu`].

pub mod assets;
pub mod layout;
pub mod model;

pub use model::lookup_command_by_key;
