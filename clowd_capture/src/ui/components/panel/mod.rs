//! Button panel that appears after a selection is finalized.
//!
//! One rounded tray chassis (the floating-tray design shared with the C#
//! recording / share-region / scrolling strips) holding, in order, the
//! Clowd emblem (dead to hover and clicks), the "W × H" area readout
//! (dead), and the labelled action buttons: icon then Title-case label
//! with the accelerator glyph underlined. The tray is anchored to the
//! selection — a row beneath it or a column beside it — and is never
//! draggable.
//!
//! The panel shows one of two strips at a time — see
//! [`model::PanelButtonSet`]:
//!   * `Normal`, the capture strip (Upload / Edit / Video / Share / Scroll
//!     / Copy / Save / OCR / Reset / Exit; Scroll and OCR are
//!     Windows-only), and
//!   * `Ocr`, the strip that replaces it while recognized text is lifted
//!     off the selection (Upload / Search / Copy / Back / Exit).
//!
//! They have different lengths, so every entry point takes the set as a
//! parameter — and a second one, [`model::PanelFeatures`], because the
//! shell can switch Upload / Share / Scroll / OCR off (SettingsCapture's
//! "Optional features"), which narrows either strip further. Each strip is
//! positioned by the same algorithm with its own length, so the shorter OCR
//! strip re-centers under the selection on a swap — the re-click hazard
//! that movement creates is `PanelSwapGuard`'s job (app.rs), not
//! geometry's. See `layout::compute_layout`.
//!
//! Pure layout/model logic only — GPU rendering lives in
//! [`crate::ui::gpu`].

pub mod assets;
pub mod layout;
pub mod model;

pub use model::lookup_command_by_key;
