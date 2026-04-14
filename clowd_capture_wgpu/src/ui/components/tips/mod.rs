//! Tips & Hotkeys help panel. Shows hotkey hints, the hovered window
//! and monitor, and a color sampler for the pixel under the cursor —
//! ported from `DxScreenCapture.cpp:741-828`. Toggleable with the `T`
//! key; hides itself once a selection is captured or while the user is
//! actively dragging.

pub mod assets;
pub mod component;
pub mod layout;
pub mod model;

pub use component::TipsPanelComponent;
