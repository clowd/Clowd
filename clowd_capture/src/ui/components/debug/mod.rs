//! Debug / instrumentation component.
//!
//! Toggled by the `D` key (see `DxScreenCapture.cpp:1209-1213`). Exposes
//! two overlay panels that mirror the C++ version:
//!   * `monitor` — per-display stats panel anchored top-left of every
//!     monitor. Shows adapter, DPI, bounds, FPS, frame-time rolling stats.
//!   * `primary` — scene/state panel anchored top-right of the monitor
//!     containing the virtual cursor. Shows startup timings, cursor,
//!     selection, hovered-window info.

pub mod layout;
pub mod model;
pub mod resources;
