//! The button panel shown once a selection is captured: one floating tray
//! (the design shared with the C# recording / share-region / scrolling
//! strips) holding the Clowd emblem, the "W × H" readout and the action
//! buttons, anchored beneath or beside the selection.
//!
//! The tray lives in [`place`] (which side of the selection, pure and in
//! physical pixels), [`theme`] (the design tokens as egui `Style`, fonts
//! and frames), [`icons`] (the SVG marks as per-DPI textures),
//! [`widgets`] (the two button styles, the readout, the emblem) and
//! [`show`] (the analytic strip size, the placement and the `Area`). It is
//! run by `ui::egui_host` on the app thread and shipped to the workers as
//! tessellated primitives, so the run that answers a click is the run that
//! drew the strip.
//!
//!   * [`model`]: the static truth. The two button sets ([`model::PanelButtonSet`]:
//!     `Normal` for a fresh capture, `Ocr` while lifted text is on screen),
//!     the per-button command / label / accelerator / icon bytes, the two
//!     button styles ([`model::ButtonStyle`]), the shell's opt-out switches
//!     ([`model::PanelFeatures`]), and the accelerator lookup. No geometry.
//!
//! Each strip is centred with its own width, so a set swap moves the tray
//! under the cursor; the re-click hazard that creates is `PanelSwapGuard`'s
//! job (app.rs), not this module's.

pub mod assets;
pub mod icons;
pub mod model;
pub mod place;
pub mod show;
pub mod theme;
pub mod widgets;

pub use model::lookup_command_by_key;
