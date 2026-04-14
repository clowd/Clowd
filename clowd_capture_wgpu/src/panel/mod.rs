//! Button panel that appears after a selection is finalised.
//!
//! Mirrors the C++ `clowd_capture_dx` button panel: seven SVG action
//! buttons (UPLOAD / EDIT / VIDEO / COPY / SAVE / RESET / EXIT) plus a
//! non-clickable area indicator that shows the selected width×height with
//! decorative corner brackets. Layout, hit-testing, and the button model
//! live in this module; `backend_bake` produces the pixels by CPU
//! rasterizing into a `tiny_skia::Pixmap` and uploading it as a single
//! textured quad.
//!
//! See C++ references:
//!   * `SetButtonPanelPositions` — DxScreenCapture.cpp:112-195
//!   * `captureButtonDetails`    — DxScreenCapture.cpp:52-60
//!   * Render loop               — DxScreenCapture.cpp:833-906

pub mod assets;
pub mod draw;
pub mod hover;
pub mod layout;
pub mod model;
pub mod renderer;
pub mod state;

pub use renderer::BakePanelBackend;
pub use layout::compute_layout;
pub use model::{button_defs, lookup_action_by_key, ButtonAction};
pub use state::PanelState;
