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
    /// Open in the *prompt* phase (see [`run`]) rather than going straight to
    /// mirroring. On by default; `--no-prompt` turns it off for callers that
    /// drive the picker some other way.
    pub prompt: bool,
}

/// Implemented in main.rs over `Mirror`. All calls arrive on the UI/main thread.
pub trait AppEvents {
    /// The mirror's client area is now the share surface; `handle` is HWND
    /// (win) / NSView* (mac). Create the ObsDisplay here, sized to the canvas
    /// px. Fires when the user accepts the prompt, or immediately at startup
    /// when `UiConfig::prompt` is false — never before the window exists, and
    /// exactly once.
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
// `pub fn run(region: Rect, cfg: UiConfig, events: Box<dyn AppEvents>) -> !`,
// which never returns (exit happens via events.quit()). It runs two phases:
//
// PROMPT PHASE (skipped when !cfg.prompt). One ordinary window — titled,
// front, activated — SMALL and wherever the platform cares to put it, whose
// client area shows "Share this window, then press OK" and an OK button.
// Nothing else exists yet: no mask, no frame, no ObsDisplay.
//
// Why: a mirror parked at the bottom of the Z-order under an opaque mask is
// unreachable in the click-to-pick share UIs macOS apps increasingly use — the
// click lands on the mask. Here the user picks a window that is plainly
// visible and frontmost, and only then does it become the mirror. Small and
// unplaced rather than region-shaped because it has to be easy to find and
// click: a window already sized and positioned like the final mirror is
// awkward to pick, and for a region at a screen edge is largely off-screen.
//
// MIRROR PHASE (on OK, or at once when !cfg.prompt). The SAME window is
// reused — never recreated — because the share the user just started is bound
// to that window's identity. The platform strips the prompt controls, drops
// the title bar and border (a window share captures the whole window frame, so
// a caption would otherwise appear in the shared output), resizes and moves the
// window onto the region, hands the client area to `mirror_ready`, drops it to
// the back, and brings up the mask over it and the frame around the region.
// Only the window's identity is preserved across the transition; its style,
// size and position all change.

#[cfg(windows)]
mod win32;
#[cfg(windows)]
pub use win32::run;

#[cfg(target_os = "macos")]
mod appkit;
#[cfg(target_os = "macos")]
pub use appkit::run;
