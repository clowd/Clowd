//! Button panel that appears after a selection is finalised.
//!
//! Mirrors the C++ `clowd_capture_dx` button panel: SVG action buttons
//! plus a non-clickable area indicator that shows the selected
//! width×height with decorative corner brackets.
//!
//! The panel shows one of two strips at a time — see
//! [`model::PanelButtonSet`]:
//!   * `Normal`, the capture strip (UPLOAD / EDIT / VIDEO / SCROLL / OCR
//!     / COPY / SAVE / RESET / EXIT; SCROLL and OCR are Windows-only), and
//!   * `Ocr`, the strip that replaces it while recognised text is lifted
//!     off the selection (UPLOAD / SEARCH / COPY / BACK / EXIT).
//!
//! They have different lengths, so every entry point takes the set as a
//! parameter — and a second one, [`model::PanelFeatures`], because the
//! shell can switch UPLOAD / SCROLL / OCR off (SettingsCapture's "Optional
//! features"), which narrows either strip further. Each strip is
//! positioned by the same algorithm with its own width, so the shorter OCR
//! strip re-centres under the selection on a swap — the re-click hazard
//! that movement creates is `PanelSwapGuard`'s job (app.rs), not
//! geometry's. See `layout::compute_layout`.
//!
//! Pure layout/model logic only — GPU rendering lives in
//! [`crate::ui::gpu`].

pub mod assets;
pub mod layout;
pub mod model;

pub use model::lookup_command_by_key;
