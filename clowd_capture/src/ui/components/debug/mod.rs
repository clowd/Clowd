//! Debug / instrumentation component.
//!
//! Toggled by the `D` key. Two overlay panels, both run by egui on the app
//! thread and shipped to the workers as tessellated primitives:
//!   * the **monitor** panel, anchored top-left of every monitor, showing
//!     adapter, DPI, bounds, FPS and frame-time stats plus a plot of
//!     recent frames. Its live numbers come from the render worker's
//!     `PerfSnapshot`s.
//!   * the **primary** panel, anchored top-right of the monitor holding the
//!     virtual cursor, showing startup timings, cursor, selection and
//!     hovered-window info — all app-thread values already.
//!
//! [`model`] formats the rows, [`resources`] polls RAM/VRAM and [`show`]
//! lays both panels out.

pub mod model;
pub mod resources;
pub mod show;
