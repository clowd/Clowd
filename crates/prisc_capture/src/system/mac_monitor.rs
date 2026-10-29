use std::collections::VecDeque;

use anyhow::Result;

use crate::system::MonitorInfo;
use prisc_rust_core::desktop::screens;
use prisc_rust_core::geometry::{LogicalPoint, RectExt, ScreenRect};

/// The overlay's own frame rate target when a display reports no refresh rate (most built-in
/// panels do not).
const FALLBACK_REFRESH_HZ: f32 = 60.0;

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

/// Every display from the shared CoreGraphics screen list (`desktop::screens`, the list Prisc.Ui
/// reads too), shaped for the overlay: physical-pixel bounds laid out by
/// [`compute_physical_origins`], the CG origin kept as `logical_origin`.
pub fn all_monitors() -> Result<Vec<MonitorInfo>> {
    let screens = screens::list().map_err(|e| anyhow!("the display list failed: {e}"))?;

    let mut raw: Vec<RawMonitor> = Vec::with_capacity(screens.len());
    for (i, screen) in screens.iter().enumerate() {
        let cg_bounds = screen.bounds;
        let (phys_w, phys_h) = (screen.pixel_width, screen.pixel_height);

        let scale = if cg_bounds.width() > 0 {
            phys_w as f32 / cg_bounds.width() as f32
        } else {
            1.0
        };

        raw.push(RawMonitor {
            cg_x: f64::from(cg_bounds.min_x()),
            cg_y: f64::from(cg_bounds.min_y()),
            cg_w: f64::from(cg_bounds.width()),
            cg_h: f64::from(cg_bounds.height()),
            phys_w,
            phys_h,
            scale,
            is_primary: screen.is_primary,
            refresh_hz: screen
                .refresh_hz
                .map_or(FALLBACK_REFRESH_HZ, |hz| hz as f32),
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
            low_vram_adapter: false,
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
