//! macOS: window chrome and the traffic lights (`prisc_window_*`).
//!
//! Every call here mutates or reads an `NSWindow`, which AppKit allows only on the main thread. The
//! gate ([`with_window`]) runs before the handle is dereferenced, so a call from another thread is
//! `PRISC_E_STATE` and never undefined behaviour. The window is borrowed, never retained: the
//! caller (Avalonia's `IMacOSTopLevelPlatformHandle.NSWindow`) keeps it alive for the call, and the
//! export prologue has already refused a zero handle.

use objc2::MainThreadMarker;
use objc2_app_kit::{NSApplication, NSView, NSWindow, NSWindowButton, NSWindowSharingType};
use objc2_foundation::NSPoint;
use prisc_rust_core::desktop::overlay::{self, OverlayFlags};

use crate::error::Result;
use crate::ffi::status::{PRISC_OK, PRISC_S_NONE};

/// `CoverMenuBar`: the shared overlay level and collection behaviour. `ClickThrough`:
/// `setIgnoresMouseEvents:YES`. `OpaqueLayered` is a Windows concept and a no-op here.
pub(crate) fn prepare_overlay(handle: isize, cover_menu_bar: bool, _opaque_layered: bool, click_through: bool) -> Result<()> {
    with_window(handle, "prisc_window_prepare_overlay", |window, _| {
        if cover_menu_bar {
            overlay::apply(window, OverlayFlags::OVERLAY);
        }
        if click_through {
            window.setIgnoresMouseEvents(true);
        }
    })
}

/// `setIgnoresMouseEvents:`, with an explicit `NO` to leave pass-through (see the export).
pub(crate) fn set_click_through(handle: isize, on: bool) -> Result<()> {
    with_window(handle, "prisc_window_set_click_through", |window, _| {
        window.setIgnoresMouseEvents(on)
    })
}

/// The above-overlay level and `orderFrontRegardless`, which raises without activating the app or
/// making the window key.
pub(crate) fn raise_topmost_no_activate(handle: isize) -> Result<()> {
    with_window(handle, "prisc_window_raise_topmost_no_activate", |window, _| {
        overlay::raise_above_overlays(window)
    })
}

/// `NSWindowSharingNone` hides the window from the CGWindowList capture path and ScreenCaptureKit;
/// `NSWindowSharingReadOnly` is AppKit's default.
pub(crate) fn set_excluded_from_capture(handle: isize, excluded: bool) -> Result<()> {
    with_window(handle, "prisc_window_set_excluded_from_capture", |window, _| {
        window.setSharingType(if excluded {
            NSWindowSharingType::None
        } else {
            NSWindowSharingType::ReadOnly
        })
    })
}

/// The app is active and this window is its key window: what has the keyboard on macOS.
pub(crate) fn is_foreground(handle: isize) -> Result<bool> {
    with_window(handle, "prisc_window_is_foreground", |window, mtm| {
        NSApplication::sharedApplication(mtm).isActive() && window.isKeyWindow()
    })
}

/// Translates the three standard buttons as a unit (see the export). `PRISC_S_NONE` when there is
/// nothing to move.
pub(crate) fn set_traffic_light_position(handle: isize, left_inset: f64, top_inset: f64) -> Result {
    with_window(handle, "prisc_window_set_traffic_light_position_macos", |window, _| {
        // Read all three frames before moving any, so AppKit's own spacing and size survive.
        let kinds = [
            NSWindowButton::CloseButton,
            NSWindowButton::MiniaturizeButton,
            NSWindowButton::ZoomButton,
        ];
        let mut buttons = Vec::with_capacity(kinds.len());
        for kind in kinds {
            let Some(button) = window.standardWindowButton(kind) else {
                return PRISC_S_NONE;
            };
            let frame = button.frame();
            buttons.push((button, frame));
        }

        // The three share one superview (NSTitlebarView). It is not flipped, so y counts up from
        // the bar's bottom edge, and its height turns a top inset into that y; a negative y simply
        // puts a button below the bar (NSTitlebarView does not clip).
        // SAFETY: the superview is retained by the returned `Retained` for as long as it is used.
        let Some(title_bar) = (unsafe { buttons[0].0.superview() }) else {
            return PRISC_S_NONE;
        };
        let title_bar_height = NSView::frame(&title_bar).size.height;
        if title_bar_height <= 0.0 {
            return PRISC_S_NONE;
        }

        let shift = left_inset - buttons[0].1.origin.x;
        for (button, frame) in &buttons {
            button.setFrameOrigin(NSPoint::new(
                frame.origin.x + shift,
                title_bar_height - frame.size.height - top_inset,
            ));
        }
        PRISC_OK
    })
}

/// The main-thread gate, then the borrowed window. `export` names the caller in the
/// `PRISC_E_STATE` message.
fn with_window<R>(handle: isize, export: &str, body: impl FnOnce(&NSWindow, MainThreadMarker) -> R) -> Result<R> {
    let mtm = super::main_thread(export)?;
    // SAFETY: the export contract: `handle` is a live NSWindow* the caller keeps alive for the
    // call, and this is the main thread. Nothing here retains or releases it.
    let window: &NSWindow = unsafe { &*(handle as *const NSWindow) };
    Ok(body(window, mtm))
}

#[cfg(test)]
mod tests {
    use crate::ffi::status::PriscInteropStatus as S;
    use crate::ffi::window::*;

    /// libtest runs every test off the main thread, so each main-thread export must answer
    /// `PRISC_E_STATE` for a non-zero handle without touching it (1 is no window at all).
    #[test]
    fn the_main_thread_exports_refuse_a_worker_thread() {
        let mut flag = 0u8;
        assert_eq!(unsafe { prisc_window_prepare_overlay(1, 1) }, S::State as i32);
        assert_eq!(unsafe { prisc_window_set_click_through(1, 1) }, S::State as i32);
        assert_eq!(unsafe { prisc_window_raise_topmost_no_activate(1) }, S::State as i32);
        assert_eq!(unsafe { prisc_window_set_excluded_from_capture(1, 1) }, S::State as i32);
        assert_eq!(unsafe { prisc_window_is_foreground(1, &mut flag) }, S::State as i32);
        assert_eq!(
            unsafe { prisc_window_set_traffic_light_position_macos(1, 14.0, 14.0) },
            S::State as i32
        );
    }
}
