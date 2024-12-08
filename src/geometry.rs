use euclid::{Point2D, Rect, Size2D};

pub struct ScreenUnit;
pub struct WindowUnit;
pub type ScreenRect = Rect<i32, ScreenUnit>;
pub type ScreenRectF = Rect<f64, ScreenUnit>;
pub type ScreenPoint = Point2D<i32, ScreenUnit>;
pub type ScreenPointF = Point2D<f64, ScreenUnit>;
pub type WindowRectF = Rect<f64, WindowUnit>;
pub type WindowPointF = Point2D<f64, WindowUnit>;

pub trait WindowRectExt {
    // fn to_screen_rect(&self, window_bounds: ScreenRect) -> ScreenRect;
    // fn to_screen_rect_f(&self, window_bounds: ScreenRect) -> ScreenRectF;
    fn to_nannou(&self) -> nannou::geom::Rect;
}

pub trait WindowPointExt {
    // fn to_screen_point(&self, window_bounds: ScreenRect) -> ScreenPoint;
    // fn to_screen_point_f(&self, window_bounds: ScreenRect) -> ScreenPointF;
    fn to_nannou(&self) -> nannou::geom::Vec2;
}

pub trait ScreenRectExt {
    fn to_window_rect(&self, window_bounds: ScreenRect) -> WindowRectF;
}

pub trait ScreenPointExt {
    fn to_window_point(&self, window_bounds: ScreenRect) -> WindowPointF;
}

impl WindowRectExt for WindowRectF {
    fn to_nannou(&self) -> nannou::geom::Rect {
        let center = self.center();
        nannou::geom::Rect::from_x_y_w_h(center.x as f32, center.y as f32, self.size.width as f32, self.size.height as f32)
    }
}

impl WindowPointExt for WindowPointF {
    fn to_nannou(&self) -> nannou::geom::Vec2 {
        nannou::geom::Vec2::new(self.x as f32, self.y as f32)
    }
}

impl ScreenRectExt for ScreenRect {
    fn to_window_rect(&self, window_bounds: ScreenRect) -> WindowRectF {
        let top_left = self.top_left();
        let bottom_right = self.bottom_right();
        let top_left = top_left.to_window_point(window_bounds);
        let bottom_right = bottom_right.to_window_point(window_bounds);
        WindowRectF::from_corners(top_left, bottom_right)
    }
}

impl ScreenPointExt for ScreenPoint {
    fn to_window_point(&self, window_bounds: ScreenRect) -> WindowPointF {
        let pt = self.to_f64();
        pt.to_window_point(window_bounds)
    }
}

impl ScreenPointExt for ScreenPointF {
    fn to_window_point(&self, window_bounds: ScreenRect) -> WindowPointF {
        let monitor_pos = window_bounds.to_f64();
        let x = self.x - monitor_pos.min_x();
        let y = self.y - monitor_pos.min_y();
        let tx = |x: f64| (x - monitor_pos.width() / 2.0);
        let ty = |y: f64| (-(y - monitor_pos.height() / 2.0));
        let x = tx(x);
        let y = ty(y);
        WindowPointF::new(x, y)
    }
}

const PIXEL_SELECTION_ROUNDING_THRESHOLD: f64 = 0.2;
fn round_pixel(px: f64, prefer_down: bool) -> i32 {
    let pfloor = px.floor() as i32;
    let position = px - pfloor as f64;
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

fn round_pixel_pair(v1: f64, v2: f64) -> (i32, i32) {
    let vmin = v1.min(v2);
    let vmax = v1.max(v2);
    (round_pixel(vmin, true), round_pixel(vmax, false))
}

pub trait ScreenRectRounded {
    fn from_rounded_threshold(x1: f64, y1: f64, x2: f64, y2: f64) -> ScreenRect;
}

impl ScreenRectRounded for ScreenRect {
    fn from_rounded_threshold(x1: f64, y1: f64, x2: f64, y2: f64) -> ScreenRect {
        let (x1, x2) = round_pixel_pair(x1, x2);
        let (y1, y2) = round_pixel_pair(y1, y2);
        ScreenRect::from_exact(x1, y1, x2, y2)
    }
}

pub trait RectExt<TRect, TPoint, T> {
    fn top_left(&self) -> TPoint;
    fn top_right(&self) -> TPoint;
    fn bottom_left(&self) -> TPoint;
    fn bottom_right(&self) -> TPoint;
    fn left(&self) -> T;
    fn right(&self) -> T;
    fn top(&self) -> T;
    fn bottom(&self) -> T;
    fn from_exact(x1: T, y1: T, x2: T, y2: T) -> TRect;
    fn from_corners(top_left: TPoint, bottom_right: TPoint) -> TRect;
}

impl RectExt<ScreenRect, ScreenPoint, i32> for ScreenRect {
    fn top_left(&self) -> ScreenPoint {
        ScreenPoint::new(self.left(), self.top())
    }

    fn top_right(&self) -> ScreenPoint {
        ScreenPoint::new(self.right(), self.top())
    }

    fn bottom_left(&self) -> ScreenPoint {
        ScreenPoint::new(self.left(), self.bottom())
    }

    fn bottom_right(&self) -> ScreenPoint {
        ScreenPoint::new(self.right(), self.bottom())
    }

    fn left(&self) -> i32 {
        self.min_x()
    }

    fn right(&self) -> i32 {
        self.max_x()
    }

    fn top(&self) -> i32 {
        self.min_y()
    }

    fn bottom(&self) -> i32 {
        self.max_y()
    }

    fn from_exact(x1: i32, y1: i32, x2: i32, y2: i32) -> ScreenRect {
        ScreenRect::new(
            ScreenPoint::new(x1.min(x2), y1.min(y2)),
            Size2D::new((x1 - x2).abs(), (y1 - y2).abs()),
        )
    }

    fn from_corners(top_left: ScreenPoint, bottom_right: ScreenPoint) -> ScreenRect {
        ScreenRect::from_exact(top_left.x, top_left.y, bottom_right.x, bottom_right.y)
    }
}

impl RectExt<ScreenRectF, ScreenPointF, f64> for ScreenRectF {
    fn top_left(&self) -> ScreenPointF {
        ScreenPointF::new(self.left(), self.top())
    }

    fn top_right(&self) -> ScreenPointF {
        ScreenPointF::new(self.right(), self.top())
    }

    fn bottom_left(&self) -> ScreenPointF {
        ScreenPointF::new(self.left(), self.bottom())
    }

    fn bottom_right(&self) -> ScreenPointF {
        ScreenPointF::new(self.right(), self.bottom())
    }
    
    fn left(&self) -> f64 {
        self.min_x()
    }

    fn right(&self) -> f64 {
        self.max_x()
    }

    fn top(&self) -> f64 {
        self.min_y()
    }

    fn bottom(&self) -> f64 {
        self.max_y()
    }

    fn from_exact(x1: f64, y1: f64, x2: f64, y2: f64) -> ScreenRectF {
        ScreenRectF::new(
            ScreenPointF::new(x1.min(x2), y1.min(y2)),
            Size2D::new((x1 - x2).abs(), (y1 - y2).abs()),
        )
    }

    fn from_corners(top_left: ScreenPointF, bottom_right: ScreenPointF) -> ScreenRectF {
        ScreenRectF::from_exact(top_left.x, top_left.y, bottom_right.x, bottom_right.y)
    }
}

impl RectExt<WindowRectF, WindowPointF, f64> for WindowRectF {
    fn top_left(&self) -> WindowPointF {
        WindowPointF::new(self.left(), self.top())
    }

    fn top_right(&self) -> WindowPointF {
        WindowPointF::new(self.right(), self.top())
    }

    fn bottom_left(&self) -> WindowPointF {
        WindowPointF::new(self.left(), self.bottom())
    }

    fn bottom_right(&self) -> WindowPointF {
        WindowPointF::new(self.right(), self.bottom())
    }

    fn left(&self) -> f64 {
        self.min_x()
    }

    fn right(&self) -> f64 {
        self.max_x()
    }

    fn top(&self) -> f64 {
        self.min_y()
    }

    fn bottom(&self) -> f64 {
        self.max_y()
    }

    fn from_exact(x1: f64, y1: f64, x2: f64, y2: f64) -> WindowRectF {
        WindowRectF::new(
            WindowPointF::new(x1.min(x2), y1.min(y2)),
            Size2D::new((x1 - x2).abs(), (y1 - y2).abs()),
        )
    }

    fn from_corners(top_left: WindowPointF, bottom_right: WindowPointF) -> WindowRectF {
        WindowRectF::from_exact(top_left.x, top_left.y, bottom_right.x, bottom_right.y)
    }
}

pub fn point_to_widened_rect(radius: i32, pt: ScreenPoint) -> ScreenRect {
    let origin = pt - Size2D::new(radius, radius);
    let size = Size2D::new(radius * 2, radius * 2);
    Rect::new(origin, size)
}

pub fn point_to_widened_rect_f(radius: f64, pt: ScreenPointF) -> ScreenRectF {
    let origin = pt - Size2D::new(radius, radius);
    let size = Size2D::new(radius * 2.0, radius * 2.0);
    Rect::new(origin, size)
}

pub fn point_to_widened_rect_n(radius: f32, pt: nannou::glam::Vec2) -> nannou::geom::Rect {
    nannou::geom::Rect::from_x_y_w_h(pt.x, pt.y, radius * 2.0, radius * 2.0)
}

pub fn line_to_widened_rect(radius: i32, start: ScreenPoint, end: ScreenPoint) -> ScreenRect {
    let x1 = start.x.min(end.x) - radius;
    let y1 = start.y.min(end.y) - radius;
    let x2 = start.x.max(end.x) + radius;
    let y2 = start.y.max(end.y) + radius;
    ScreenRect::from_exact(x1, y1, x2, y2)
}

pub fn line_to_widened_rect_f(radius: f64, start: ScreenPointF, end: ScreenPointF) -> ScreenRectF {
    let x1 = start.x.min(end.x) - radius;
    let y1 = start.y.min(end.y) - radius;
    let x2 = start.x.max(end.x) + radius;
    let y2 = start.y.max(end.y) + radius;
    ScreenRectF::from_exact(x1, y1, x2, y2)
}
