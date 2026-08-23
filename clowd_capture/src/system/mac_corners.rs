//! macOS glue for [`super::corners`]: the OS-version lookup used until the
//! window server has been asked, and the probe that asks it.
//!
//! There is no public API that reports an `NSWindow`'s corner radius from
//! outside its process, and on macOS Tahoe there is no single answer anyway
//! (toolbar windows, titlebar-only windows and Catalyst/custom windows all
//! differ). What IS available is the window server's own composite: a
//! single-window `CGWindowListCreateImage` carries the corner mask in its
//! alpha channel, so capturing a few dozen points at one corner of the
//! window and measuring the transparent area gives the exact radius the
//! user sees — for any app, on any version. It costs a window-server
//! round-trip (~10 ms) per window, which is why the walker runs it after
//! publishing the snapshot rather than before.

use core_graphics::geometry::{CGPoint, CGRect, CGSize};
use core_graphics::window::{self, kCGWindowImageBestResolution, kCGWindowImageBoundsIgnoreFraming, kCGWindowListOptionIncludingWindow};
use objc2_foundation::NSProcessInfo;

use super::corners::{estimate_radius_from_alpha, macos_fallback_radius_points, Corner};
use super::MonitorInfo;

/// Side of the square captured at a window corner, in CG points. Has to
/// exceed the largest radius in use (26 pt on Tahoe) with room for the
/// straight-edge band the estimator calibrates against.
const PROBE_POINTS: f64 = 48.0;

/// Major OS version from `NSProcessInfo`, read once.
fn os_major_version() -> i64 {
    use std::sync::OnceLock;
    static MAJOR: OnceLock<i64> = OnceLock::new();
    *MAJOR.get_or_init(|| {
        NSProcessInfo::processInfo()
            .operatingSystemVersion()
            .majorVersion as i64
    })
}

/// The lookup-table radius for this OS, in points. Used for every window
/// until its probe lands, and for any window the probe cannot measure.
pub fn fallback_radius_points() -> f32 {
    macos_fallback_radius_points(os_major_version())
}

/// A window's CG-space bounds (points, top-left origin, as
/// `kCGWindowBounds` reports them).
#[derive(Debug, Clone, Copy)]
pub struct CgBounds {
    pub x: f64,
    pub y: f64,
    pub w: f64,
    pub h: f64,
}

/// Measure the corner radius the window server composites `window_id`
/// with, in physical pixels. `None` when no corner of the window sits
/// fully on a display (nothing to photograph) or the capture failed — the
/// caller keeps the lookup-table value then.
pub fn probe_corner_radius(window_id: u32, bounds: CgBounds, monitors: &[MonitorInfo]) -> Option<f32> {
    let side = PROBE_POINTS.min(bounds.w).min(bounds.h);
    if side < 4.0 {
        return None;
    }
    // Try the four corners in turn; a window half off the left of the
    // display still has a right-hand corner to measure.
    let candidates = [
        (Corner::TopLeft, bounds.x, bounds.y),
        (Corner::TopRight, bounds.x + bounds.w - side, bounds.y),
        (Corner::BottomLeft, bounds.x, bounds.y + bounds.h - side),
        (Corner::BottomRight, bounds.x + bounds.w - side, bounds.y + bounds.h - side),
    ];
    let (corner, x, y) = candidates.into_iter().find(|&(_, x, y)| {
        monitors
            .iter()
            .any(|m| contains_square(m, x, y, side))
    })?;

    let rect = CGRect::new(&CGPoint::new(x, y), &CGSize::new(side, side));
    let image = window::create_image(
        rect,
        kCGWindowListOptionIncludingWindow,
        window_id,
        kCGWindowImageBestResolution | kCGWindowImageBoundsIgnoreFraming,
    )?;
    let w = image.width();
    let h = image.height();
    let bpr = image.bytes_per_row();
    if image.bits_per_pixel() != 32 || w == 0 || h == 0 {
        return None;
    }
    let data = image.data();
    let bytes = data.bytes();
    if bytes.len() < (h - 1) * bpr + w * 4 {
        return None;
    }
    // Same pixel layout `capture_window_image` relies on: BGRA, alpha last.
    let alpha = |px: usize, py: usize| -> u8 { bytes[py * bpr + px * 4 + 3] };
    Some(estimate_radius_from_alpha(w, h, corner, alpha))
}

/// Whether the `side`-pt square at CG point (`x`, `y`) lies entirely on
/// monitor `m`.
fn contains_square(m: &MonitorInfo, x: f64, y: f64, side: f64) -> bool {
    let ox = m.logical_origin.x;
    let oy = m.logical_origin.y;
    let lw = m.bounds.width() as f64 / m.scale_factor as f64;
    let lh = m.bounds.height() as f64 / m.scale_factor as f64;
    x >= ox && y >= oy && x + side <= ox + lw && y + side <= oy + lh
}
