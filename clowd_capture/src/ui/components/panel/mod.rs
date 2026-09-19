//! The button panel shown once a selection is captured: one floating tray
//! (the design shared with the C# recording / share-region / scrolling
//! strips) holding the Clowd emblem, the "W × H" readout and the action
//! buttons, anchored beneath or beside the selection.
//!
//! Two files, two jobs:
//!   * [`model`]: the static truth. The two button sets ([`model::PanelButtonSet`]:
//!     `Normal` for a fresh capture, `Ocr` while lifted text is on screen),
//!     the per-button command / label / accelerator / icon, the shell's
//!     opt-out switches ([`model::PanelFeatures`]), and the accelerator
//!     lookup. No geometry.
//!   * [`compose`]: the one function that turns a set, the switches, the
//!     selection and a monitor into a placed [`compose::PanelScene`], using
//!     the generic [`crate::ui::kit`] for every pixel. The scene is built
//!     once on the app thread and shipped in `UiSharedState`; renderers
//!     paint it and the app thread hit-tests it, so the two never disagree.
//!
//! Each strip is centred with its own width, so a set swap moves the tray
//! under the cursor; the re-click hazard that creates is `PanelSwapGuard`'s
//! job (app.rs), not this module's.

pub mod assets;
pub mod compose;
pub mod model;

pub use model::lookup_command_by_key;
