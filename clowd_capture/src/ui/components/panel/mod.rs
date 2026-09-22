//! The button panel shown once a selection is captured: one floating tray
//! (the design shared with the C# recording / share-region / scrolling
//! strips) holding the Clowd emblem, the "W × H" readout and the action
//! buttons in their groups (the accent group first, then the grey ones),
//! anchored beneath or beside the selection.
//!
//! Two chassis ([`model::PanelLayout`]). Every set but one is the single
//! strip above. The capture set is the exception: it has three times the
//! buttons of any other and most of them are beside the point of the
//! capture, so it gets a double-height tray — the five finishing actions
//! on their own row with full labels, and the hand-offs and the two ways
//! out on a second row as accelerator tiles sitting straight on the
//! chassis, the hand-offs left and the ways out flush right. Stood on its
//! end it becomes the emblem beside the readout, the labelled actions one
//! per line, and the rest in a two-column grid. `--panel-buttons` picks
//! the presentation for the single strip only; the double one draws both
//! presentations itself.
//!
//! The tray lives in [`place`] (which side of the selection, pure and in
//! physical pixels), [`theme`] (the design tokens as egui `Style`, fonts
//! and frames), [`assets`] (the SVG marks and the sources egui's image
//! loader rasterises them from), [`widgets`] (the two button styles, the readout, the emblem) and
//! [`show`] (the analytic strip size, the placement and the `Area`). It is
//! run by `ui::egui_host` on the app thread and shipped to the workers as
//! tessellated primitives, so the run that answers a click is the run that
//! drew the strip.
//!
//!   * [`model`]: the static truth. The two button sets ([`model::PanelButtonSet`]:
//!     `Normal` for a fresh capture, `Ocr` while lifted text is on screen),
//!     the per-button command / label / accelerator / icon, the two
//!     button styles ([`model::ButtonStyle`]), which chassis a set uses
//!     ([`model::PanelLayout`]), the shell's opt-out switches
//!     ([`model::PanelFeatures`]), and the accelerator lookup. No geometry.
//!
//! Each strip is centred with its own width, so a set swap moves the tray
//! under the cursor; the re-click hazard that creates is `PanelSwapGuard`'s
//! job (app.rs), not this module's.

pub mod assets;
pub mod model;
pub mod place;
pub mod show;
pub mod theme;
pub mod widgets;

pub use model::lookup_command_by_key;
