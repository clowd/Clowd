//! Button panel that appears after a selection is finalised.
//!
//! Mirrors the C++ `clowd_capture_dx` button panel: seven SVG action
//! buttons (UPLOAD / EDIT / VIDEO / COPY / SAVE / RESET / EXIT) plus a
//! non-clickable area indicator that shows the selected width×height with
//! decorative corner brackets.

pub mod assets;
pub mod component;
pub mod layout;
pub mod model;

pub use component::ButtonPanelComponent;
pub use model::lookup_command_by_key;
