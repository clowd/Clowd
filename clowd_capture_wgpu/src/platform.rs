//! Platform-specific window tweaks applied after window creation.

#[cfg(windows)]
pub fn apply_capture_window_tweaks(window: &winit::window::Window) {
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};
    use windows::Win32::Foundation::HWND;
    use windows::Win32::Graphics::Dwm::{
        DWMWA_EXCLUDED_FROM_PEEK, DWMWA_TRANSITIONS_FORCEDISABLED, DwmSetWindowAttribute,
    };

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
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};
    use objc2_app_kit::NSView;

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
