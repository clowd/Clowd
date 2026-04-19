use anyhow::Result;
use core_graphics::display::{CGDisplay, CGMainDisplayID};

use crate::geometry::{RectExt, ScreenRect};
use crate::system::MonitorInfo;

pub fn all_monitors() -> Result<Vec<MonitorInfo>> {
    let display_ids = CGDisplay::active_displays()
        .map_err(|e| anyhow!("CGGetActiveDisplayList failed: {:?}", e))?;

    let main_id = unsafe { CGMainDisplayID() };

    let mut monitors = Vec::with_capacity(display_ids.len());
    for (i, &id) in display_ids.iter().enumerate() {
        let display = CGDisplay::new(id);
        let cg_bounds = display.bounds();

        // Use CGDisplayMode to get the true physical pixel dimensions.
        // CGDisplayPixelsWide/High are deprecated and return the logical
        // resolution on modern Retina displays, which breaks DPI scaling.
        let mode = display.display_mode();
        let (phys_w, phys_h, refresh_hz) = if let Some(ref mode) = mode {
            let pw = mode.pixel_width() as u32;
            let ph = mode.pixel_height() as u32;
            let hz = mode.refresh_rate() as f32;
            (pw, ph, hz)
        } else {
            (display.pixels_wide() as u32, display.pixels_high() as u32, 0.0)
        };

        let scale = if cg_bounds.size.width > 0.0 {
            phys_w as f32 / cg_bounds.size.width as f32
        } else {
            1.0
        };

        // Convert logical origin (CG points) to physical pixels.
        // CG global display coordinates: origin at top-left of primary, Y-down.
        let phys_x = (cg_bounds.origin.x * scale as f64).round() as i32;
        let phys_y = (cg_bounds.origin.y * scale as f64).round() as i32;

        // ProMotion / variable refresh displays report 0 Hz.
        let refresh_hz = if refresh_hz <= 0.0 { 60.0 } else { refresh_hz };

        monitors.push(MonitorInfo {
            bounds: ScreenRect::from_xy_size(phys_x, phys_y, phys_w as i32, phys_h as i32),
            scale_factor: scale,
            is_primary: id == main_id,
            refresh_hz,
            name: format!("Display {}", i + 1),
            adapter_id: None,
        });
    }

    Ok(monitors)
}

/// Bounding box of all monitor physical rects in virtual-desktop coordinates.
pub fn virtual_desktop_bounds(monitors: &[MonitorInfo]) -> ScreenRect {
    if monitors.is_empty() {
        return ScreenRect::from_xy_size(0, 0, 0, 0);
    }
    let mut min_x = i32::MAX;
    let mut min_y = i32::MAX;
    let mut max_x = i32::MIN;
    let mut max_y = i32::MIN;
    for m in monitors {
        min_x = min_x.min(m.bounds.min_x());
        min_y = min_y.min(m.bounds.min_y());
        max_x = max_x.max(m.bounds.max_x());
        max_y = max_y.max(m.bounds.max_y());
    }
    ScreenRect::from_exact(min_x, min_y, max_x, max_y)
}
