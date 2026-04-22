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
        use objc2_app_kit::{NSColor, NSView, NSWindowAnimationBehavior, NSWindowCollectionBehavior};

        let ns_view: &NSView = &*(h.ns_view.as_ptr() as *const NSView);
        let Some(ns_window) = ns_view.window() else {
            return;
        };

        ns_window.setLevel(25); // NSStatusWindowLevel
        ns_window.setAnimationBehavior(NSWindowAnimationBehavior::None);
        ns_window.setCollectionBehavior(
            NSWindowCollectionBehavior::Stationary
                | NSWindowCollectionBehavior::CanJoinAllSpaces
                | NSWindowCollectionBehavior::FullScreenAuxiliary
                | NSWindowCollectionBehavior::IgnoresCycle,
        );

        let black = NSColor::blackColor();
        ns_window.setBackgroundColor(Some(&black));
        ns_window.setOpaque(true);
    }
}

/// Create a CGImage from the monitor's region of the desktop screenshot.
#[cfg(target_os = "macos")]
pub fn crop_screenshot_to_cgimage(
    screenshot: &crate::system::CapturedDesktop,
    monitor_bounds: crate::geometry::ScreenRect,
) -> Option<core_graphics::image::CGImage> {
    use core_graphics::color_space::CGColorSpace;
    use core_graphics::context::CGContext;

    let vd = screenshot.bounds;
    let crop_x = (monitor_bounds.min_x() - vd.min_x()) as usize;
    let crop_y = (monitor_bounds.min_y() - vd.min_y()) as usize;
    let crop_w = monitor_bounds.width() as usize;
    let crop_h = monitor_bounds.height() as usize;
    if crop_w == 0 || crop_h == 0 {
        return None;
    }
    let vd_stride = screenshot.width as usize * 4;

    let mut crop_buf = vec![0u8; crop_w * crop_h * 4];
    for row in 0..crop_h {
        let src_off = (crop_y + row) * vd_stride + crop_x * 4;
        let dst_off = row * crop_w * 4;
        let end = (src_off + crop_w * 4).min(screenshot.bgra.len());
        let len = end.saturating_sub(src_off).min(crop_w * 4);
        if len > 0 {
            crop_buf[dst_off..dst_off + len]
                .copy_from_slice(&screenshot.bgra[src_off..src_off + len]);
        }
    }

    let color_space = CGColorSpace::create_device_rgb();
    let bitmap_info: u32 = (2 << 12) // kCGBitmapByteOrder32Little
        | 6; // kCGImageAlphaNoneSkipFirst
    let ctx = CGContext::create_bitmap_context(
        Some(crop_buf.as_mut_ptr() as *mut _),
        crop_w,
        crop_h,
        8,
        crop_w * 4,
        &color_space,
        bitmap_info,
    );
    ctx.create_image()
}

/// Hide or show the hardware cursor at the compositor/system level.
///
/// On macOS, calls `CGDisplayHideCursor`/`CGDisplayShowCursor` — unlike winit's
/// `set_cursor_visible` (which swaps to a transparent image), this suppresses
/// "shake to locate" and other compositor-level effects.
///
/// On Windows, calls Win32 `ShowCursor` directly. winit's per-window
/// `set_cursor_visible` is unusable here: its refresh path unconditionally
/// calls `set_cursor_hidden(false)` for every window whose client rect doesn't
/// contain the cursor (winit 0.30 `window_state.rs:536-541`). Since our
/// capture overlays span the virtual desktop, broadcasting a hide across all
/// windows produces a race depending on HashMap iteration order.
#[cfg(target_os = "macos")]
pub fn set_hardware_cursor_visible(visible: bool) {
    use core_graphics::display::{CGDisplay, CGMainDisplayID};
    use std::sync::atomic::{AtomicBool, Ordering};

    static HIDDEN: AtomicBool = AtomicBool::new(false);

    let currently_hidden = HIDDEN.load(Ordering::Relaxed);
    if visible && currently_hidden {
        unsafe { CGDisplay::new(CGMainDisplayID()).show_cursor() }.ok();
        HIDDEN.store(false, Ordering::Relaxed);
    } else if !visible && !currently_hidden {
        unsafe { CGDisplay::new(CGMainDisplayID()).hide_cursor() }.ok();
        HIDDEN.store(true, Ordering::Relaxed);
    }
}

#[cfg(windows)]
pub fn set_hardware_cursor_visible(visible: bool) {
    use std::sync::atomic::{AtomicBool, Ordering};
    use windows::Win32::UI::WindowsAndMessaging::ShowCursor;

    static HIDDEN: AtomicBool = AtomicBool::new(false);

    let currently_hidden = HIDDEN.load(Ordering::Relaxed);
    if visible && currently_hidden {
        unsafe { ShowCursor(true) };
        HIDDEN.store(false, Ordering::Relaxed);
    } else if !visible && !currently_hidden {
        unsafe { ShowCursor(false) };
        HIDDEN.store(true, Ordering::Relaxed);
    }
}

#[cfg(not(any(target_os = "macos", windows)))]
pub fn set_hardware_cursor_visible(_visible: bool) {}

/// Reveal all capture windows after frame 0 is rendered.
///
/// On macOS: the window already shows the static screenshot; we snap the
/// render subview's CALayer opacity to 1.0 so the wgpu content takes over.
///
/// On Windows: windows were created hidden. `set_visible(true)` is
/// instantaneous thanks to `WS_EX_NOREDIRECTIONBITMAP`.
#[cfg(target_os = "macos")]
pub fn show_windows_atomically<'a>(handles: impl Iterator<Item = &'a crate::render::WindowHandle>) {
    for h in handles {
        if let Some(ref subview) = h.render_subview {
            if let Some(layer) = subview.layer() {
                layer.setOpacity(1.0);
            }
        }
    }
}

#[cfg(not(target_os = "macos"))]
pub fn show_windows_atomically<'a>(handles: impl Iterator<Item = &'a crate::render::WindowHandle>) {
    for h in handles {
        h.window.set_visible(true);
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
