//! The desktop as the OS describes it: the top-level window list, the
//! monitors, and the cursor.
//!
//! The overlay (`prisc_capture`), the scrolling driver and the recorder
//! (through `prisc_obs_platform`) all ask these questions, and the answers
//! have to agree: the scrolling driver validates a window id the overlay's
//! walk produced, and the recorder's window sidecar is read against the same
//! window set the user picked from. Each walk used to be written out once per
//! binary; this module is the one copy.
//!
//! What lives here is enumeration plus the filtering every caller shares.
//! Where callers genuinely differ (the recorder keeps floating tool palettes,
//! the overlay looks only at the current virtual desktop) that is a field on
//! the platform's `WindowFilter` rather than a second walk. What a caller does
//! with the result (hit testing, obstruction, wire rows) stays with it.
//!
//! Coordinates are each platform's native space, untouched: physical
//! virtual-desktop pixels on Windows (every Prisc process is per-monitor-v2
//! DPI aware, see `app.manifest`), global CG points on macOS.
//!
//! Nothing here links libobs, so `prisc_capture` (a default workspace member)
//! can use it without pulling the recorder stack in.

mod filter;
/// The screen grab every reader of screen pixels shares (filled by the desktop work package).
pub mod grab;
mod icon;
/// The macOS overlay window level and collection behaviour.
pub mod overlay;
/// The monitor list every reader of screens shares (filled by the desktop work package).
pub mod screens;

pub use filter::*;
pub use icon::*;

#[cfg(windows)]
mod win;
#[cfg(windows)]
pub use win::*;

#[cfg(target_os = "macos")]
mod mac;
#[cfg(target_os = "macos")]
pub use mac::*;
