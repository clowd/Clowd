//! The coordinate spaces every Prisc process speaks.
//!
//! `ScreenUnit` — physical pixels in virtual-desktop space — is the one that
//! crosses process boundaries: `session.json` and every Win32 call agree on
//! it, and so do the `action.txt` marker and the scrolling driver's
//! `--region`/`--point` flags *on Windows*. Those two carry the platform
//! capture space rather than this one (CAPTURE_PROTOCOL.md §1.2), which on
//! macOS is CG points — the overlay converts on the way out and the driver
//! consumes them as-is. The other units here are local conveniences that live
//! in this module because they convert to and from `ScreenUnit`.

use euclid::{Point2D, Rect, Size2D};
use std::ops;

// Physical pixels in virtual-desktop space.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct ScreenUnit;
pub type ScreenRect = Rect<i32, ScreenUnit>;
pub type ScreenRectF = Rect<f32, ScreenUnit>;
pub type ScreenPoint = Point2D<i32, ScreenUnit>;
pub type ScreenPointF = Point2D<f32, ScreenUnit>;

// OS logical coordinates (CG points on macOS, DIPs on Windows).
#[cfg(target_os = "macos")]
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct LogicalUnit;
#[cfg(target_os = "macos")]
pub type LogicalPoint = Point2D<f64, LogicalUnit>;
#[cfg(target_os = "macos")]
pub type LogicalSize = Size2D<f64, LogicalUnit>;

// Physical pixels relative to a window's client-area top-left.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct WindowUnit;
pub type WindowPoint = Point2D<f32, WindowUnit>;

// Initialize rounded ScreenRect
const PIXEL_SELECTION_ROUNDING_THRESHOLD: f32 = 0.2;
fn round_pixel(px: f32, prefer_down: bool) -> i32 {
    let pfloor = px.floor() as i32;
    let position = px - pfloor as f32;
    let cut_ratio = if prefer_down {
        1.0 - PIXEL_SELECTION_ROUNDING_THRESHOLD
    } else {
        PIXEL_SELECTION_ROUNDING_THRESHOLD
    };
    if position < cut_ratio {
        pfloor
    } else {
        px.ceil() as i32
    }
}

fn round_pixel_pair(v1: f32, v2: f32) -> (i32, i32) {
    let vmin = v1.min(v2);
    let vmax = v1.max(v2);
    (round_pixel(vmin, true), round_pixel(vmax, false))
}

pub trait ScreenRectRounded {
    fn from_rounded_threshold(x1: f32, y1: f32, x2: f32, y2: f32) -> Option<ScreenRect>;
}

impl ScreenRectRounded for ScreenRect {
    fn from_rounded_threshold(x1: f32, y1: f32, x2: f32, y2: f32) -> Option<ScreenRect> {
        let (x1, x2) = round_pixel_pair(x1, x2);
        let (y1, y2) = round_pixel_pair(y1, y2);
        let rect = ScreenRect::from_exact(x1, y1, x2, y2);

        if rect.width() > 0 && rect.height() > 0 {
            Some(rect)
        } else {
            None
        }
    }
}

pub trait RectExt<T, U>
where
    T: ops::Add<Output = T> + ops::Sub<Output = T> + Copy + PartialOrd + Default,
    U: Copy,
{
    fn left(&self) -> T;
    fn right(&self) -> T;
    fn top(&self) -> T;
    fn bottom(&self) -> T;
    fn from_exact(x1: T, y1: T, x2: T, y2: T) -> Rect<T, U> {
        Self::from_xy_size(x1, y1, x2 - x1, y2 - y1)
    }
    fn from_xy_size(x1: T, y1: T, width: T, height: T) -> Rect<T, U> {
        Rect::new(Point2D::new(x1, y1), Size2D::new(width, height))
    }
}

impl<T, U> RectExt<T, U> for Rect<T, U>
where
    T: ops::Add<Output = T> + ops::Sub<Output = T> + Copy + PartialOrd + Default,
    U: Copy,
{
    fn left(&self) -> T {
        self.min_x()
    }
    fn right(&self) -> T {
        self.max_x()
    }
    fn top(&self) -> T {
        self.min_y()
    }
    fn bottom(&self) -> T {
        self.max_y()
    }
}

// ── ScreenRect extensions (i32 rects) ──────────────────────────────

pub trait ScreenRectExt<U: Copy> {
    fn contains_point_f32(&self, p: Point2D<f32, U>) -> bool;
    fn center_x(&self) -> i32;
    fn center_y(&self) -> i32;
}

impl<U: Copy> ScreenRectExt<U> for Rect<i32, U> {
    fn contains_point_f32(&self, p: Point2D<f32, U>) -> bool {
        let f = self.to_f32();
        p.x >= f.left() && p.x < f.right() && p.y >= f.top() && p.y < f.bottom()
    }

    fn center_x(&self) -> i32 {
        (self.min_x() + self.max_x()) / 2
    }

    fn center_y(&self) -> i32 {
        (self.min_y() + self.max_y()) / 2
    }
}

// ── ScreenPointF helpers ───────────────────────────────────────────

pub fn to_screen_point(p: ScreenPointF) -> ScreenPoint {
    ScreenPoint::new(p.x.floor() as i32, p.y.floor() as i32)
}

// ── Coordinate space conversions ───────────────────────────────────

pub fn screen_to_window(monitor_bounds: ScreenRect, pt: ScreenPointF) -> WindowPoint {
    let b = monitor_bounds.to_f32();
    WindowPoint::new(pt.x - b.left(), pt.y - b.top())
}

pub fn window_to_screen(monitor_bounds: ScreenRect, pt: WindowPoint) -> ScreenPointF {
    let b = monitor_bounds.to_f32();
    ScreenPointF::new(pt.x + b.left(), pt.y + b.top())
}

// ── Command-line parsing ───────────────────────────────────────────

/// `X,Y,W,H`, the region format every Prisc command line shares (the
/// scrolling driver's `--region`, the recorder's `--region`, the share-region
/// `move` command). Negative origins are legal (a monitor left of or above the
/// primary); a non-positive width or height is not, since it fails later with
/// an OS error that says nothing about where the bad rect came from.
pub fn parse_rect(s: &str) -> Result<ScreenRect, String> {
    let n = parse_i32_list::<4>(s)?;
    if n[2] <= 0 || n[3] <= 0 {
        return Err(format!("'{s}' has a non-positive width or height"));
    }
    Ok(ScreenRect::from_xy_size(n[0], n[1], n[2], n[3]))
}

/// `X,Y`. Negative coordinates are legal and common.
pub fn parse_point(s: &str) -> Result<ScreenPoint, String> {
    let n = parse_i32_list::<2>(s)?;
    Ok(ScreenPoint::new(n[0], n[1]))
}

/// Exactly `N` comma-separated decimal integers, no more and no fewer.
fn parse_i32_list<const N: usize>(s: &str) -> Result<[i32; N], String> {
    let mut out = [0i32; N];
    let mut parts = s.split(',');
    for slot in out.iter_mut() {
        let part = parts
            .next()
            .ok_or_else(|| format!("'{s}' needs {N} comma-separated integers"))?
            .trim();
        *slot = part
            .parse()
            .map_err(|_| format!("'{part}' is not an integer"))?;
    }
    if parts.next().is_some() {
        return Err(format!("'{s}' needs exactly {N} comma-separated integers"));
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rects_parse_and_reject_degenerate() {
        assert_eq!(parse_rect("10,20,300,400").unwrap(), ScreenRect::from_xy_size(10, 20, 300, 400));
        // Secondary monitor left of the primary: negative origin, positive size.
        assert_eq!(
            parse_rect("-1920,0,1920,1080").unwrap(),
            ScreenRect::from_xy_size(-1920, 0, 1920, 1080)
        );
        assert_eq!(parse_rect(" 1 , 2 , 3 , 4 ").unwrap(), ScreenRect::from_xy_size(1, 2, 3, 4));
        assert!(parse_rect("10,20,0,400").is_err());
        assert!(parse_rect("10,20,300,-1").is_err());
        assert!(parse_rect("10,20,300").is_err());
        assert!(parse_rect("10,20,300,400,500").is_err());
        assert!(parse_rect("10,20,300,x").is_err());
        assert!(parse_rect("").is_err());
    }

    #[test]
    fn points_parse_negative_coordinates() {
        assert_eq!(parse_point("40,50").unwrap(), ScreenPoint::new(40, 50));
        assert_eq!(parse_point("-40,-50").unwrap(), ScreenPoint::new(-40, -50));
        assert!(parse_point("40").is_err());
        assert!(parse_point("40,50,60").is_err());
    }

    #[test]
    fn parse_errors_name_the_offending_input() {
        assert_eq!(parse_rect("1,2,3").unwrap_err(), "'1,2,3' needs 4 comma-separated integers");
        assert_eq!(parse_rect("1,2,3,x").unwrap_err(), "'x' is not an integer");
        assert_eq!(
            parse_rect("1,2,3,4,5").unwrap_err(),
            "'1,2,3,4,5' needs exactly 4 comma-separated integers"
        );
    }
}
