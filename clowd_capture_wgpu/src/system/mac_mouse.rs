use core_graphics::display::{CGDisplay, CGPoint};

use crate::geometry::ScreenPoint;

// CGEvent FFI — the safe CGEvent::new() requires a non-null CGEventSource,
// but we just need the current mouse location which only needs a NULL source.
extern "C" {
    fn CGEventCreate(source: *const std::ffi::c_void) -> *mut std::ffi::c_void;
    fn CGEventGetLocation(event: *const std::ffi::c_void) -> CGPoint;
    fn CFRelease(cf: *const std::ffi::c_void);
    fn CGAssociateMouseAndMouseCursorPosition(connected: i32) -> i32;
}

pub fn get_position() -> ScreenPoint {
    let logical_pt = unsafe {
        let event = CGEventCreate(std::ptr::null());
        let pt = CGEventGetLocation(event);
        CFRelease(event);
        pt
    };

    let scale = display_scale_at_logical_point(logical_pt) as f64;
    ScreenPoint::new(
        (logical_pt.x * scale).round() as i32,
        (logical_pt.y * scale).round() as i32,
    )
}

pub fn set_position(pos: ScreenPoint) {
    let scale = display_scale_at_physical_point(pos) as f64;
    let logical_pt = CGPoint::new(pos.x as f64 / scale, pos.y as f64 / scale);
    let _ = CGDisplay::warp_mouse_cursor_position(logical_pt);
    // macOS suppresses mouse-moved events for ~0.25s after a cursor warp.
    // Re-associating the mouse clears the suppression so the anchor-warp
    // loop in the virtual cursor receives continuous CursorMoved events.
    unsafe {
        CGAssociateMouseAndMouseCursorPosition(1);
    }
}

/// Compute the scale factor for a display from its CGDisplayMode.
/// Falls back to 1.0 if the mode is unavailable or logical width is zero.
fn display_scale(d: &CGDisplay) -> f32 {
    let logical_w = d.bounds().size.width as f32;
    if logical_w > 0.0 {
        if let Some(mode) = d.display_mode() {
            return mode.pixel_width() as f32 / logical_w;
        }
    }
    1.0
}

/// Find the scale factor of the display whose logical (CG point) bounds
/// contain the given point.
fn display_scale_at_logical_point(pt: CGPoint) -> f32 {
    if let Ok(ids) = CGDisplay::active_displays() {
        for id in ids {
            let d = CGDisplay::new(id);
            let b = d.bounds();
            if pt.x >= b.origin.x
                && pt.x < b.origin.x + b.size.width
                && pt.y >= b.origin.y
                && pt.y < b.origin.y + b.size.height
            {
                return display_scale(&d);
            }
        }
    }
    // Fallback: assume Retina 2x.
    2.0
}

/// Find the scale factor of the display whose physical-pixel bounds
/// contain the given point.
fn display_scale_at_physical_point(pt: ScreenPoint) -> f32 {
    if let Ok(ids) = CGDisplay::active_displays() {
        for id in ids {
            let d = CGDisplay::new(id);
            let b = d.bounds();
            let scale = display_scale(&d);

            let phys_x = (b.origin.x * scale as f64).round() as i32;
            let phys_y = (b.origin.y * scale as f64).round() as i32;
            let phys_w_i = if let Some(mode) = d.display_mode() {
                mode.pixel_width() as i32
            } else {
                (b.size.width * scale as f64).round() as i32
            };
            let phys_h_i = if let Some(mode) = d.display_mode() {
                mode.pixel_height() as i32
            } else {
                (b.size.height * scale as f64).round() as i32
            };

            if pt.x >= phys_x
                && pt.x < phys_x + phys_w_i
                && pt.y >= phys_y
                && pt.y < phys_y + phys_h_i
            {
                return scale;
            }
        }
    }
    2.0
}
