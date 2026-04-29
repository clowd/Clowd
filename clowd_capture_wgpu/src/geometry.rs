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
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct LogicalUnit;
pub type LogicalPoint = Point2D<f64, LogicalUnit>;
pub type LogicalSize = Size2D<f64, LogicalUnit>;

// Physical pixels relative to a window's client-area top-left.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct WindowUnit;
pub type WindowPoint = Point2D<f32, WindowUnit>;

// Initialise rounded ScreenRect
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

// Base type extensions
pub trait PointExt<T, U>
where
    T: ops::Add<Output = T> + ops::Sub<Output = T> + Copy + PartialOrd + Default,
    U: Copy,
{
    fn to_widened_rect(&self, radius: T) -> Rect<T, U>;
}

impl<T, U> PointExt<T, U> for Point2D<T, U>
where
    T: ops::Add<Output = T> + ops::Sub<Output = T> + Copy + PartialOrd + Default,
    U: Copy,
{
    fn to_widened_rect(&self, radius: T) -> Rect<T, U> {
        let x1 = self.x - radius;
        let y1 = self.y - radius;
        let x2 = self.x + radius;
        let y2 = self.y + radius;
        Rect::from_exact(x1, y1, x2, y2)
    }
}

pub trait RectExt<T, U>
where
    T: ops::Add<Output = T> + ops::Sub<Output = T> + Copy + PartialOrd + Default,
    U: Copy,
{
    fn top_left(&self) -> Point2D<T, U> {
        Point2D::new(self.left(), self.top())
    }
    fn top_right(&self) -> Point2D<T, U> {
        Point2D::new(self.right(), self.top())
    }
    fn bottom_left(&self) -> Point2D<T, U> {
        Point2D::new(self.left(), self.bottom())
    }
    fn bottom_right(&self) -> Point2D<T, U> {
        Point2D::new(self.right(), self.bottom())
    }
    fn left(&self) -> T;
    fn right(&self) -> T;
    fn top(&self) -> T;
    fn bottom(&self) -> T;
    fn from_exact(x1: T, y1: T, x2: T, y2: T) -> Rect<T, U> {
        Self::from_xy_size(x1, y1, x2 - x1, y2 - y1)
    }
    fn from_corners(top_left: Point2D<T, U>, bottom_right: Point2D<T, U>) -> Rect<T, U> {
        Self::from_exact(top_left.x, top_left.y, bottom_right.x, bottom_right.y)
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
