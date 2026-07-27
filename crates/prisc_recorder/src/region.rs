//! Pure region math (DESIGN §2.3): capture-region planning and output scaling.
//! No OBS types, fully unit-tested.

use std::fmt;

use crate::platform::MonitorInfo;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub w: u32,
    pub h: u32,
}

#[derive(Debug, Clone)]
pub struct RegionPlan {
    /// Canvas size in capture PIXELS: the region size × the densest
    /// intersected display's scale, forced even (min 2).
    pub canvas: (u32, u32),
    /// Canvas pixels per capture-space unit — the max `MonitorInfo::scale`
    /// among the intersected displays. 1.0 on Windows.
    pub canvas_scale: f64,
    pub items: Vec<PlannedItem>,
}

#[derive(Debug, Clone, Copy)]
pub struct PlannedItem {
    /// Index into the monitor slice given to `plan_region`.
    pub monitor_index: usize,
    /// Scene-item position in canvas px: (display_origin - region_origin) × canvas scale.
    pub pos: (f32, f32),
    /// Scene-item scale (canvas scale / display scale): maps the display's
    /// pixel-sized frames onto the canvas density. 1.0 everywhere on Windows.
    pub scale: f32,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RegionError {
    /// The region intersects no monitor.
    NoDisplayInBounds,
    /// Malformed `--region` string.
    Parse(String),
}

impl fmt::Display for RegionError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            RegionError::NoDisplayInBounds => {
                write!(f, "The capture region does not intersect any display")
            }
            RegionError::Parse(msg) => write!(f, "Invalid region: {msg}"),
        }
    }
}

impl std::error::Error for RegionError {}

/// Parses `"x,y,w,h"` (all four components used — x,y included).
pub fn parse_region(s: &str) -> Result<Rect, RegionError> {
    let parts: Vec<&str> = s.split(',').map(str::trim).collect();
    if parts.len() != 4 {
        return Err(RegionError::Parse("region must be X,Y,W,H".to_string()));
    }
    let x: i32 = parts[0]
        .parse()
        .map_err(|_| RegionError::Parse(format!("invalid X '{}'", parts[0])))?;
    let y: i32 = parts[1]
        .parse()
        .map_err(|_| RegionError::Parse(format!("invalid Y '{}'", parts[1])))?;
    let w: u32 = parts[2]
        .parse()
        .map_err(|_| RegionError::Parse(format!("invalid W '{}'", parts[2])))?;
    let h: u32 = parts[3]
        .parse()
        .map_err(|_| RegionError::Parse(format!("invalid H '{}'", parts[3])))?;
    if w < 2 || h < 2 {
        return Err(RegionError::Parse("W and H must be >= 2".to_string()));
    }
    Ok(Rect { x, y, w, h })
}

/// Canvas = region × canvas scale (forced even, min 2); one item per monitor
/// whose bounds intersect the region. Region and monitor bounds are in the
/// §1.1 capture coordinate space (Windows: physical px; macOS: CG points);
/// the canvas scale — the max `MonitorInfo::scale` among intersected displays
/// — converts to capture pixels so the densest display is recorded lossless.
pub fn plan_region(region: Rect, monitors: &[MonitorInfo]) -> Result<RegionPlan, RegionError> {
    let intersecting: Vec<usize> = monitors
        .iter()
        .enumerate()
        .filter(|(_, m)| rects_intersect(region, m))
        .map(|(i, _)| i)
        .collect();
    if intersecting.is_empty() {
        return Err(RegionError::NoDisplayInBounds);
    }

    let canvas_scale = intersecting
        .iter()
        .map(|&i| monitors[i].scale)
        .fold(1.0f64, f64::max);

    let items = intersecting
        .iter()
        .map(|&i| {
            let m = &monitors[i];
            PlannedItem {
                monitor_index: i,
                pos: (
                    ((m.x - region.x) as f64 * canvas_scale) as f32,
                    ((m.y - region.y) as f64 * canvas_scale) as f32,
                ),
                scale: (canvas_scale / m.scale) as f32,
            }
        })
        .collect();

    let canvas_w = ((region.w as f64 * canvas_scale).round() as u32 & !1).max(2);
    let canvas_h = ((region.h as f64 * canvas_scale).round() as u32 & !1).max(2);
    Ok(RegionPlan {
        canvas: (canvas_w, canvas_h),
        canvas_scale,
        items,
    })
}

/// Single-pass aspect-preserving downscale: `s = min(1, max_w/w, max_h/h)`
/// (0 caps = off), applied once to both dims, then forced even, min 2.
pub fn compute_output_size(base: (u32, u32), max_w: u32, max_h: u32) -> (u32, u32) {
    let (w, h) = base;
    let mut scale = 1.0f64;
    if max_w > 0 && w > 0 {
        scale = scale.min(max_w as f64 / w as f64);
    }
    if max_h > 0 && h > 0 {
        scale = scale.min(max_h as f64 / h as f64);
    }
    let out_w = (w as f64 * scale) as u32;
    let out_h = (h as f64 * scale) as u32;
    ((out_w & !1).max(2), (out_h & !1).max(2))
}

fn rects_intersect(r: Rect, m: &MonitorInfo) -> bool {
    // i64 math: virtual-desktop coords can be negative and spans can overflow i32.
    let (rx1, ry1) = (r.x as i64, r.y as i64);
    let (rx2, ry2) = (rx1 + r.w as i64, ry1 + r.h as i64);
    let (mx1, my1) = (m.x as i64, m.y as i64);
    let (mx2, my2) = (mx1 + m.width as i64, my1 + m.height as i64);
    rx1 < mx2 && mx1 < rx2 && ry1 < my2 && my1 < ry2
}

#[cfg(test)]
mod tests {
    use super::*;

    fn mon(x: i32, y: i32, w: u32, h: u32) -> MonitorInfo {
        mon_scaled(x, y, w, h, 1.0)
    }

    fn mon_scaled(x: i32, y: i32, w: u32, h: u32, scale: f64) -> MonitorInfo {
        MonitorInfo {
            id: format!("mon-{x},{y}"),
            alt_id: None,
            x,
            y,
            width: w,
            height: h,
            scale,
            is_primary: x == 0 && y == 0,
        }
    }

    #[test]
    fn parse_region_valid() {
        assert_eq!(
            parse_region("100,200,800,600").unwrap(),
            Rect {
                x: 100,
                y: 200,
                w: 800,
                h: 600
            }
        );
        // Negative virtual-desktop origin is legal.
        assert_eq!(
            parse_region("-1920, 200, 640, 480").unwrap(),
            Rect {
                x: -1920,
                y: 200,
                w: 640,
                h: 480
            }
        );
    }

    #[test]
    fn parse_region_invalid() {
        assert!(parse_region("1,2,3").is_err());
        assert!(parse_region("a,b,c,d").is_err());
        assert!(parse_region("0,0,1,600").is_err()); // W < 2
        assert!(parse_region("0,0,800,0").is_err()); // H < 2
        assert!(parse_region("0,0,-800,600").is_err()); // negative size
    }

    #[test]
    fn plan_single_monitor() {
        let monitors = [mon(0, 0, 2560, 1440)];
        let plan = plan_region(
            Rect {
                x: 100,
                y: 200,
                w: 800,
                h: 600,
            },
            &monitors,
        )
        .unwrap();
        assert_eq!(plan.canvas, (800, 600));
        assert_eq!(plan.items.len(), 1);
        assert_eq!(plan.items[0].monitor_index, 0);
        assert_eq!(plan.items[0].pos, (-100.0, -200.0));
    }

    #[test]
    fn plan_negative_coords() {
        // Secondary monitor left of primary; region entirely on it.
        let monitors = [mon(0, 0, 2560, 1440), mon(-1920, 200, 1920, 1080)];
        let plan = plan_region(
            Rect {
                x: -1800,
                y: 300,
                w: 640,
                h: 480,
            },
            &monitors,
        )
        .unwrap();
        assert_eq!(plan.items.len(), 1);
        assert_eq!(plan.items[0].monitor_index, 1);
        // display_origin - region_origin = (-1920 - -1800, 200 - 300)
        assert_eq!(plan.items[0].pos, (-120.0, -100.0));
    }

    #[test]
    fn plan_two_monitor_span() {
        let monitors = [mon(0, 0, 2560, 1440), mon(2560, 0, 1920, 1080)];
        let plan = plan_region(
            Rect {
                x: 2000,
                y: 100,
                w: 1200,
                h: 800,
            },
            &monitors,
        )
        .unwrap();
        assert_eq!(plan.items.len(), 2);
        assert_eq!(plan.items[0].monitor_index, 0);
        assert_eq!(plan.items[0].pos, (-2000.0, -100.0));
        assert_eq!(plan.items[1].monitor_index, 1);
        assert_eq!(plan.items[1].pos, (560.0, -100.0));
    }

    #[test]
    fn plan_no_intersection() {
        let monitors = [mon(0, 0, 2560, 1440)];
        let err = plan_region(
            Rect {
                x: 5000,
                y: 5000,
                w: 100,
                h: 100,
            },
            &monitors,
        )
        .unwrap_err();
        assert_eq!(err, RegionError::NoDisplayInBounds);
    }

    #[test]
    fn plan_touching_edge_does_not_intersect() {
        let monitors = [mon(0, 0, 1000, 1000)];
        // Region starts exactly at the right edge: empty intersection.
        assert!(plan_region(
            Rect {
                x: 1000,
                y: 0,
                w: 100,
                h: 100
            },
            &monitors
        )
        .is_err());
    }

    #[test]
    fn plan_forces_even_canvas() {
        let monitors = [mon(0, 0, 2560, 1440)];
        let plan = plan_region(
            Rect {
                x: 0,
                y: 0,
                w: 801,
                h: 601,
            },
            &monitors,
        )
        .unwrap();
        assert_eq!(plan.canvas, (800, 600));
    }

    #[test]
    fn plan_retina_single_monitor() {
        // 2x Retina display: canvas and item pos are in pixels; the source
        // already emits pixel-sized frames, so the item scale stays 1.
        let monitors = [mon_scaled(0, 0, 1728, 1117, 2.0)];
        let plan = plan_region(
            Rect {
                x: 100,
                y: 200,
                w: 800,
                h: 600,
            },
            &monitors,
        )
        .unwrap();
        assert_eq!(plan.canvas, (1600, 1200));
        assert_eq!(plan.items[0].pos, (-200.0, -400.0));
        assert_eq!(plan.items[0].scale, 1.0);
    }

    #[test]
    fn plan_mixed_scale_span() {
        // Region spans a 2x display and a 1x display to its right: canvas
        // density follows the denser display; the 1x frames upscale 2x.
        let monitors = [
            mon_scaled(0, 0, 1728, 1117, 2.0),
            mon_scaled(1728, 0, 1920, 1080, 1.0),
        ];
        let plan = plan_region(
            Rect {
                x: 1600,
                y: 100,
                w: 400,
                h: 300,
            },
            &monitors,
        )
        .unwrap();
        assert_eq!(plan.canvas, (800, 600));
        assert_eq!(plan.items[0].pos, (-3200.0, -200.0));
        assert_eq!(plan.items[0].scale, 1.0);
        assert_eq!(plan.items[1].pos, (256.0, -200.0));
        assert_eq!(plan.items[1].scale, 2.0);
    }

    #[test]
    fn output_size_off_when_caps_zero() {
        assert_eq!(compute_output_size((1920, 1080), 0, 0), (1920, 1080));
    }

    #[test]
    fn output_size_single_pass_preserves_aspect() {
        // Both caps bind: the smaller scale factor wins, applied once to both
        // dims (the old sequential clamp drifted aspect here).
        assert_eq!(compute_output_size((3840, 2160), 1920, 1440), (1920, 1080));
        assert_eq!(compute_output_size((3840, 2160), 3000, 1080), (1920, 1080));
    }

    #[test]
    fn output_size_no_upscale() {
        assert_eq!(compute_output_size((640, 480), 1920, 1080), (640, 480));
    }

    #[test]
    fn output_size_even_and_min_two() {
        // 999/500 scale of (999, 501) -> odd results get forced even.
        let (w, h) = compute_output_size((999, 501), 500, 0);
        assert_eq!(w % 2, 0);
        assert_eq!(h % 2, 0);
        assert!(w >= 2 && h >= 2);
        // Extreme downscale still yields the 2x2 floor.
        assert_eq!(compute_output_size((10000, 10), 4, 0), (4, 2));
        assert_eq!(compute_output_size((10000, 10000), 1, 1), (2, 2));
    }
}
