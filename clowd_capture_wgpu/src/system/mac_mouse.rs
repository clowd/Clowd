use core_graphics::display::{CGDisplay, CGPoint};

use crate::geometry::{LogicalPoint, ScreenPoint};
use crate::system::MonitorInfo;

extern "C" {
    fn CGEventCreate(source: *const std::ffi::c_void) -> *mut std::ffi::c_void;
    fn CGEventGetLocation(event: *const std::ffi::c_void) -> CGPoint;
    fn CFRelease(cf: *const std::ffi::c_void);
    fn CGAssociateMouseAndMouseCursorPosition(connected: i32) -> i32;
}

pub fn get_position(monitors: &[MonitorInfo]) -> ScreenPoint {
    let cg_pt = unsafe {
        let event = CGEventCreate(std::ptr::null());
        let pt = CGEventGetLocation(event);
        CFRelease(event);
        pt
    };
    let logical_pt = LogicalPoint::new(cg_pt.x, cg_pt.y);

    if let Some(m) = find_monitor_for_logical_point(logical_pt, monitors) {
        m.logical_to_screen(logical_pt)
    } else {
        let s = fallback_scale(monitors) as f64;
        ScreenPoint::new((logical_pt.x * s).round() as i32, (logical_pt.y * s).round() as i32)
    }
}

pub fn set_position(pos: ScreenPoint, monitors: &[MonitorInfo]) {
    let logical_pt = if let Some(m) = find_monitor_for_physical_point(pos, monitors) {
        let lp = m.screen_to_logical(pos);
        CGPoint::new(lp.x, lp.y)
    } else {
        let s = fallback_scale(monitors) as f64;
        CGPoint::new(pos.x as f64 / s, pos.y as f64 / s)
    };

    let _ = CGDisplay::warp_mouse_cursor_position(logical_pt);
    unsafe {
        CGAssociateMouseAndMouseCursorPosition(1);
    }
}

fn find_monitor_for_logical_point<'a>(pt: LogicalPoint, monitors: &'a [MonitorInfo]) -> Option<&'a MonitorInfo> {
    monitors.iter().find(|m| {
        let ox = m.logical_origin.x;
        let oy = m.logical_origin.y;
        let lw = m.bounds.width() as f64 / m.scale_factor as f64;
        let lh = m.bounds.height() as f64 / m.scale_factor as f64;
        pt.x >= ox && pt.x < ox + lw && pt.y >= oy && pt.y < oy + lh
    })
}

fn find_monitor_for_physical_point<'a>(pt: ScreenPoint, monitors: &'a [MonitorInfo]) -> Option<&'a MonitorInfo> {
    monitors
        .iter()
        .find(|m| m.bounds.contains(pt))
}

fn fallback_scale(monitors: &[MonitorInfo]) -> f32 {
    monitors
        .iter()
        .find(|m| m.is_primary)
        .map(|m| m.scale_factor)
        .unwrap_or(2.0)
}
