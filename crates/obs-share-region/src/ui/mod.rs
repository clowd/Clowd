//! Platform-neutral UI facade. The platform modules (win32/appkit) own the
//! three windows — mirror, mask, frame (SHARE_REGION_PLAN §1) — and the event
//! loop; the app core (main.rs / mirror.rs) never touches a window handle
//! except through [`AppEvents::mirror_ready`].
//!
//! Threading contract: `run` is called on the main thread and never returns;
//! every `AppEvents` callback is delivered on that same main/UI thread. The
//! only app code that runs anywhere else is the obs_display draw callback
//! (obs graphics thread), which the app registers inside `mirror_ready`.
//!
//! All geometry crossing this boundary is in **capture space** (top-left
//! origin, y-down — Windows: physical px on the virtual desktop; macOS: CG
//! points). Platform layers convert to their native window coordinates (e.g.
//! Cocoa's bottom-left space) internally, at the window boundary only.

use obs_platform::region::Rect;

pub struct UiConfig {
    pub title: String,
    pub accent: (u8, u8, u8),
    pub border: u32,
    pub resizable: bool,
    pub show_frame: bool,
}

/// Implemented in main.rs over `Mirror`. All calls arrive on the UI/main thread.
pub trait AppEvents {
    /// Mirror window exists; `handle` is HWND (win) / NSView* (mac). Create the
    /// ObsDisplay here, sized to the canvas px.
    fn mirror_ready(&mut self, handle: *mut std::ffi::c_void);
    /// Live during a move drag (same size). Cheap path only.
    fn region_moved(&mut self, region: Rect);
    /// On drag release (move or resize). Returns the clamped/validated region the
    /// app actually applied; the UI must adopt it (frame layout, mirror+mask
    /// geometry).
    fn region_committed(&mut self, region: Rect) -> Rect;
    /// Close requested (X button, mirror window closed). Must not return.
    fn quit(&mut self) -> !;
}

// Each platform file implements
// `pub fn run(region: Rect, cfg: UiConfig, events: Box<dyn AppEvents>) -> !`:
// creates mirror+mask(+frame per cfg.show_frame), calls events.mirror_ready,
// then runs the platform event loop forever (exit happens via events.quit()).

#[cfg(windows)]
mod win32;
#[cfg(windows)]
pub use win32::run;

#[cfg(target_os = "macos")]
mod appkit;
#[cfg(target_os = "macos")]
pub use appkit::run;
