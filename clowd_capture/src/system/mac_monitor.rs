use std::collections::VecDeque;

use anyhow::Result;
use core_graphics::display::{CGDisplay, CGMainDisplayID};

use crate::system::MonitorInfo;
use clowd_rust_core::geometry::{LogicalPoint, RectExt, ScreenRect};

struct RawMonitor {
    cg_x: f64,
    cg_y: f64,
    cg_w: f64,
    cg_h: f64,
    phys_w: u32,
    phys_h: u32,
    scale: f32,
    is_primary: bool,
    refresh_hz: f32,
    name: String,
}

pub fn all_monitors() -> Result<Vec<MonitorInfo>> {
    let display_ids = CGDisplay::active_displays().map_err(|e| anyhow!("CGGetActiveDisplayList failed: {:?}", e))?;

    let main_id = unsafe { CGMainDisplayID() };

    let mut raw: Vec<RawMonitor> = Vec::with_capacity(display_ids.len());
    for (i, &id) in display_ids.iter().enumerate() {
        let display = CGDisplay::new(id);
        let cg_bounds = display.bounds();

        let mode = display.display_mode();
        let (phys_w, phys_h, refresh_hz) = if let Some(ref mode) = mode {
            (mode.pixel_width() as u32, mode.pixel_height() as u32, mode.refresh_rate() as f32)
        } else {
            (display.pixels_wide() as u32, display.pixels_high() as u32, 0.0)
        };

        let scale = if cg_bounds.size.width > 0.0 {
            phys_w as f32 / cg_bounds.size.width as f32
        } else {
            1.0
        };

        let refresh_hz = if refresh_hz <= 0.0 { 60.0 } else { refresh_hz };

        raw.push(RawMonitor {
            cg_x: cg_bounds.origin.x,
            cg_y: cg_bounds.origin.y,
            cg_w: cg_bounds.size.width,
            cg_h: cg_bounds.size.height,
            phys_w,
            phys_h,
            scale,
            is_primary: id == main_id,
            refresh_hz,
            name: format!("Display {}", i + 1),
        });
    }

    let origins = compute_physical_origins(&raw);

    let monitors = raw
        .iter()
        .zip(origins)
        .map(|(m, (px, py))| MonitorInfo {
            bounds: ScreenRect::from_xy_size(px, py, m.phys_w as i32, m.phys_h as i32),
            scale_factor: m.scale,
            is_primary: m.is_primary,
            refresh_hz: m.refresh_hz,
            name: m.name.clone(),
            adapter_id: None,
            logical_origin: LogicalPoint::new(m.cg_x, m.cg_y),
        })
        .collect();

    Ok(monitors)
}

/// BFS from the primary monitor to compute physical-pixel origins that
/// preserve adjacency from the CG (logical-point) topology.
///
/// On macOS each display has its own backing scale factor; the CG global
/// coordinate space is in logical points. A naïve `cg_origin * own_scale`
/// breaks when adjacent monitors have different scales — the physical
/// rectangles gap or overlap. Instead we walk the topology: the primary
/// gets physical origin (0, 0), and every other monitor's origin is
/// derived from a neighbor whose origin is already known, ensuring
/// shared edges in CG space become shared edges in physical space.
fn compute_physical_origins(raw: &[RawMonitor]) -> Vec<(i32, i32)> {
    let n = raw.len();
    let mut origins: Vec<Option<(i32, i32)>> = vec![None; n];

    let primary_idx = raw
        .iter()
        .position(|m| m.is_primary)
        .unwrap_or(0);
    origins[primary_idx] = Some((0, 0));

    let mut queue = VecDeque::new();
    queue.push_back(primary_idx);

    while let Some(pi) = queue.pop_front() {
        let placed = &raw[pi];
        let (px, py) = origins[pi].unwrap();
        let placed_right = placed.cg_x + placed.cg_w;
        let placed_bottom = placed.cg_y + placed.cg_h;

        for (oi, other) in raw.iter().enumerate() {
            if origins[oi].is_some() {
                continue;
            }

            let other_right = other.cg_x + other.cg_w;
            let other_bottom = other.cg_y + other.cg_h;

            // Shared vertical edge (horizontal adjacency).
            let y_touch = placed.cg_y <= other_bottom && other.cg_y <= placed_bottom;
            if y_touch {
                if cg_eq(other.cg_x, placed_right) {
                    let oy = py + ((other.cg_y - placed.cg_y) * placed.scale as f64).round() as i32;
                    origins[oi] = Some((px + placed.phys_w as i32, oy));
                    queue.push_back(oi);
                    continue;
                }
                if cg_eq(other_right, placed.cg_x) {
                    let oy = py + ((other.cg_y - placed.cg_y) * placed.scale as f64).round() as i32;
                    origins[oi] = Some((px - other.phys_w as i32, oy));
                    queue.push_back(oi);
                    continue;
                }
            }

            // Shared horizontal edge (vertical adjacency).
            let x_touch = placed.cg_x <= other_right && other.cg_x <= placed_right;
            if x_touch {
                if cg_eq(other.cg_y, placed_bottom) {
                    let ox = px + ((other.cg_x - placed.cg_x) * placed.scale as f64).round() as i32;
                    origins[oi] = Some((ox, py + placed.phys_h as i32));
                    queue.push_back(oi);
                    continue;
                }
                if cg_eq(other_bottom, placed.cg_y) {
                    let ox = px + ((other.cg_x - placed.cg_x) * placed.scale as f64).round() as i32;
                    origins[oi] = Some((ox, py - other.phys_h as i32));
                    queue.push_back(oi);
                    continue;
                }
            }
        }
    }

    // Fallback for disconnected monitors (rare).
    for (i, origin) in origins.iter_mut().enumerate() {
        if origin.is_none() {
            let m = &raw[i];
            *origin = Some(((m.cg_x * m.scale as f64).round() as i32, (m.cg_y * m.scale as f64).round() as i32));
        }
    }

    origins
        .into_iter()
        .map(|o| o.unwrap())
        .collect()
}

fn cg_eq(a: f64, b: f64) -> bool {
    (a - b).abs() < 0.5
}
