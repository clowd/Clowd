//! Platform-specific window tweaks applied after window creation.

#[cfg(windows)]
pub fn apply_capture_window_tweaks(window: &winit::window::Window) {
    use windows::Win32::Foundation::HWND;
    use windows::Win32::Graphics::Dwm::{DwmSetWindowAttribute, DWMWA_EXCLUDED_FROM_PEEK, DWMWA_TRANSITIONS_FORCEDISABLED};
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};

    let Ok(handle) = window.window_handle() else {
        return;
    };
    let RawWindowHandle::Win32(h) = handle.as_raw() else {
        return;
    };

    let hwnd = HWND(isize::from(h.hwnd) as *mut _);
    let enable: i32 = 1;
    let ptr = &enable as *const i32 as *const core::ffi::c_void;
    unsafe {
        let _ = DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ptr, 4);
        let _ = DwmSetWindowAttribute(hwnd, DWMWA_EXCLUDED_FROM_PEEK, ptr, 4);
    }
}

#[cfg(target_os = "macos")]
pub fn apply_capture_window_tweaks(window: &winit::window::Window) {
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};

    let Ok(handle) = window.window_handle() else {
        return;
    };
    let RawWindowHandle::AppKit(h) = handle.as_raw() else {
        return;
    };

    unsafe {
        use objc2_app_kit::{NSColor, NSView, NSWindowAnimationBehavior};

        let ns_view: &NSView = &*(h.ns_view.as_ptr() as *const NSView);
        let Some(ns_window) = ns_view.window() else {
            return;
        };

        // Place above the Dock (level 20) and menu bar (level 24).
        // winit's AlwaysOnTop only maps to NSFloatingWindowLevel (3).
        ns_window.setLevel(25); // NSStatusWindowLevel

        let black = NSColor::blackColor();
        ns_window.setBackgroundColor(Some(&black));
        ns_window.setOpaque(true);
        ns_window.setAnimationBehavior(NSWindowAnimationBehavior::None);

        // Window is created visible (so Metal can render into it) but
        // fully transparent — invisible to the user until we snap alpha
        // to 1.0 after frame 0 is ready.
        ns_window.setAlphaValue(0.0);
    }
}

/// Reveal all capture windows after frame 0 is rendered.
///
/// On macOS: windows were created visible but alpha=0. We snap alpha to
/// 1.0 and the GPU shader then runs its normal colour→grayscale fade.
///
/// On Windows: windows were created hidden. `set_visible(true)` is
/// instantaneous thanks to `WS_EX_NOREDIRECTIONBITMAP`.
#[cfg(target_os = "macos")]
pub fn show_windows_atomically<'a>(windows: impl Iterator<Item = &'a std::sync::Arc<winit::window::Window>>) {
    use objc2_app_kit::NSView;
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};

    for w in windows {
        let Ok(handle) = w.window_handle() else { continue };
        let RawWindowHandle::AppKit(h) = handle.as_raw() else { continue };

        unsafe {
            let ns_view: &NSView = &*(h.ns_view.as_ptr() as *const NSView);
            if let Some(ns_window) = ns_view.window() {
                ns_window.setAlphaValue(1.0);
            }
        }
    }
}

#[cfg(not(target_os = "macos"))]
pub fn show_windows_atomically<'a>(windows: impl Iterator<Item = &'a std::sync::Arc<winit::window::Window>>) {
    for w in windows {
        w.set_visible(true);
    }
}

/// Accumulator + opaque monitor-token for the macOS pinch event tap.
///
/// winit 0.30's `magnifyWithEvent:` override ignores events whose
/// `NSEvent.phase()` isn't `Began/Changed/Ended/Cancelled`
/// (winit src `view.rs:709-716`). In practice macOS delivers magnify
/// events with other phases — in particular while a mouse button is
/// held down during a drag — and winit silently drops them, so
/// `WindowEvent::PinchGesture` never fires during a drag.
///
/// To work around this we install an application-level NSEvent local
/// monitor on `NSEventMask::Magnify`. The monitor reads
/// `magnification()` directly, accumulates it into a shared `f64`,
/// and returns `nil` to swallow the event so winit's NSView override
/// never sees it (avoids double processing).
///
/// The accumulator is drained from the main event loop and turned
/// into a `zoom *= 1 + delta` application; phase is irrelevant.
pub struct PinchMonitor {
    #[cfg(target_os = "macos")]
    accum: std::sync::Arc<std::sync::Mutex<f64>>,
    #[cfg(target_os = "macos")]
    _token: objc2::rc::Retained<objc2::runtime::AnyObject>,
}

#[cfg(target_os = "macos")]
pub fn install_pinch_monitor() -> Option<PinchMonitor> {
    use block2::RcBlock;
    use core::ptr::NonNull;
    use objc2_app_kit::{NSEvent, NSEventMask};
    use std::sync::{Arc, Mutex};

    let accum = Arc::new(Mutex::new(0.0f64));
    let accum_clone = accum.clone();

    // The block is invoked on the main thread by AppKit as magnify
    // events are dispatched. We must return a `*mut NSEvent` — nil to
    // drop the event, or the event itself to let it continue. We
    // always drop so the unified pinch pipeline lives entirely in
    // `App::drain_pinch_accum`.
    let block = RcBlock::new(move |event: NonNull<NSEvent>| -> *mut NSEvent {
        let mag = unsafe { event.as_ref().magnification() };
        if let Ok(mut g) = accum_clone.lock() {
            *g += mag;
        }
        core::ptr::null_mut()
    });

    let token = unsafe {
        NSEvent::addLocalMonitorForEventsMatchingMask_handler(NSEventMask::Magnify, &block)
    }?;

    Some(PinchMonitor { accum, _token: token })
}

#[cfg(not(target_os = "macos"))]
pub fn install_pinch_monitor() -> Option<PinchMonitor> {
    None
}

impl PinchMonitor {
    /// Consume and return the accumulated pinch delta since the
    /// previous drain. Called once per main-loop tick.
    #[cfg(target_os = "macos")]
    pub fn drain(&self) -> f64 {
        let mut g = self.accum.lock().unwrap();
        let v = *g;
        *g = 0.0;
        v
    }

    #[cfg(not(target_os = "macos"))]
    pub fn drain(&self) -> f64 {
        0.0
    }
}
