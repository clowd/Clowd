//! Tips & Hotkeys help panel. Shows hotkey hints, the hovered window
//! and monitor, and a color sampler for the pixel under the cursor —
//! ported from `DxScreenCapture.cpp:741-828`. Toggleable with the `T`
//! key; hides itself once a selection is captured or while the user is
//! actively dragging.
//!
//! [`model`] is the static content; [`show`] holds the per-host rule, the
//! pure layout and the painting, together because the layout is measured
//! from the galleys of the same pass.

pub mod model;
pub mod show;
