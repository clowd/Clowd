#![allow(dead_code)]

use euclid::{Point2D, Rect, Size2D};
use num::traits::real::Real;
use std::ops;

// Type aliases for screen and window units
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct ScreenUnit;
// #[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
// pub struct WindowUnit;
pub type ScreenRect = Rect<i32, ScreenUnit>;
pub type ScreenRectF = Rect<f32, ScreenUnit>;
pub type ScreenPoint = Point2D<i32, ScreenUnit>;
pub type ScreenPointF = Point2D<f32, ScreenUnit>;
// pub type WindowRectF = Rect<f32, WindowUnit>;
// pub type WindowPointF = Point2D<f32, WindowUnit>;

// Conversions between WindowUnit and Bevy
// pub trait EuclidRectExt {
//     fn to_bevy(&self) -> bevy::math::Rect;
// }

// pub trait EuclidPointExt {
//     fn to_bevy(&self) -> bevy::math::Vec2;
// }

// pub trait BevyRectExt {
//     fn to_euclid(&self) -> ScreenRectF;
// }

// pub trait BevyPointExt {
//     fn to_euclid(&self) -> ScreenPointF;
// }

// impl EuclidRectExt for ScreenRectF {
//     fn to_bevy(&self) -> bevy::math::Rect {
//         let top_left = self.top_left();
//         let bottom_right = self.bottom_right();
//         bevy::math::Rect::new(top_left.x as f32, top_left.y as f32, bottom_right.x as f32, bottom_right.y as f32)
//     }
// }

// impl EuclidPointExt for ScreenPointF {
//     fn to_bevy(&self) -> bevy::math::Vec2 {
//         bevy::math::Vec2::new(self.x as f32, self.y as f32)
//     }
// }

// impl BevyRectExt for bevy::math::Rect {
//     fn to_euclid(&self) -> ScreenRectF {
//         let top_left = self.min.to_euclid();
//         let bottom_right = self.max.to_euclid();
//         ScreenRectF::from_corners(top_left, bottom_right)
//     }
// }

// impl BevyPointExt for bevy::math::Vec2 {
//     fn to_euclid(&self) -> ScreenPointF {
//         ScreenPointF::new(self.x as f32, self.y as f32)
//     }
// }

// Transforms between Screen and Window coordinates
// #[derive(Debug, Clone)]
// pub struct TransformUnit {
//     window_bounds: ScreenRect,
//     window_scale: f32,
//     zoom: Option<(ScreenPointF, f32)>,
//     scissored: bool,
//     logical_units: bool,
// }

// impl TransformUnit {
//     pub fn new(window_bounds: ScreenRect, window_scale: f32) -> Self {
//         TransformUnit {
//             window_bounds,
//             window_scale,
//             zoom: None,
//             scissored: false,
//             logical_units: false,
//         }
//     }

//     pub fn with_logical_units(&self) -> Self {
//         let mut new = self.clone();
//         new.logical_units = true;
//         new
//     }

//     pub fn with_zoom(&self, origin: ScreenPointF, zoom: f32) -> Self {
//         let mut new = self.clone();
//         new.zoom = Some((origin, zoom));
//         new
//     }

//     pub fn with_scissor(&self) -> Self {
//         let mut new = self.clone();
//         new.scissored = true;
//         new
//     }

//     pub fn pt_to_window<U: NumCast + Copy>(&self, pt: Point2D<U, ScreenUnit>) -> WindowPointF {
//         let pt = pt.to_f32();
//         let transform = self.transform_to_window();
//         transform.transform_point(pt)
//     }

//     pub fn rect_to_window<U: NumCast + Copy>(&self, rect: Rect<U, ScreenUnit>) -> WindowRectF {
//         let rect = rect.to_f32();
//         let transform = self.transform_to_window();
//         transform.outer_transformed_rect(&rect)
//     }

//     pub fn pt_to_screen<U: NumCast + Copy>(&self, pt: Point2D<U, WindowUnit>) -> ScreenPointF {
//         let pt = pt.to_f32();
//         let transform = self.transform_to_screen();
//         transform.transform_point(pt)
//     }

//     pub fn rect_to_screen<U: NumCast + Copy>(&self, rect: Rect<U, WindowUnit>) -> ScreenRectF {
//         let rect = rect.to_f32();
//         let transform = self.transform_to_screen();
//         transform.outer_transformed_rect(&rect)
//     }

//     fn transform_to_window(&self) -> Transform2D<f32, ScreenUnit, WindowUnit> {
//         let window_center = self
//             .window_bounds
//             .to_f32()
//             .center()
//             .to_vector();

//         let mut transform = Transform2D::<f32, ScreenUnit, ScreenUnit>::identity()
//             // Translate units into cartesian space
//             .then_translate(-window_center)
//             .then_scale(1.0, -1.0)
//             .with_destination::<WindowUnit>();

//         if let Some((origin, zoom)) = self.zoom {
//             let translated_mouse_pt = transform.transform_point(origin);
//             transform = transform
//                 .then_translate(-translated_mouse_pt.to_vector())
//                 .then_scale(zoom.into(), zoom.into())
//                 .then_translate(translated_mouse_pt.to_vector());
//         }

//         if self.logical_units {
//             transform = transform.then_scale(1.0 / self.window_scale, 1.0 / self.window_scale);
//         }

//         if self.scissored {
//             if self.logical_units {
//                 transform = transform.then_scale(1.0, -1.0);
//             } else {
//                 transform = transform.then_scale(1.0 / self.window_scale, -1.0 / self.window_scale);
//             }
//         }

//         transform
//     }

//     fn transform_to_screen(&self) -> Transform2D<f32, WindowUnit, ScreenUnit> {
//         self.transform_to_window().inverse().unwrap()
//     }
// }

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

pub struct LineSegment<T, U>
where
    T: ops::Add<Output = T> + ops::Sub<Output = T> + Copy + PartialOrd + Default,
    U: Copy,
{
    start: Point2D<T, U>,
    end: Point2D<T, U>,
}

impl<T, U> LineSegment<T, U>
where
    T: ops::Add<Output = T> + ops::Sub<Output = T> + Copy + PartialOrd + Default + Real,
    U: Copy,
{
    pub fn start(&self) -> Point2D<T, U> {
        self.start
    }

    pub fn end(&self) -> Point2D<T, U> {
        self.end
    }

    pub fn to_widened_rect(&self, radius: T) -> Rect<T, U> {
        let start = self.start();
        let end = self.end();
        let x1 = start.x.min(end.x) - radius;
        let y1 = start.y.min(end.y) - radius;
        let x2 = start.x.max(end.x) + radius;
        let y2 = start.y.max(end.y) + radius;
        Rect::from_exact(x1, y1, x2, y2)
    }
}

// Base type extensions
#[allow(dead_code)]
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

#[allow(dead_code)]
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
    fn left_line(&self) -> LineSegment<T, U> {
        LineSegment {
            start: self.top_left(),
            end: self.bottom_left(),
        }
    }
    fn right_line(&self) -> LineSegment<T, U> {
        LineSegment {
            start: self.top_right(),
            end: self.bottom_right(),
        }
    }
    fn top_line(&self) -> LineSegment<T, U> {
        LineSegment {
            start: self.top_left(),
            end: self.top_right(),
        }
    }
    fn bottom_line(&self) -> LineSegment<T, U> {
        LineSegment {
            start: self.bottom_left(),
            end: self.bottom_right(),
        }
    }
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

// #[allow(dead_code)]
// pub trait BevyConvRectExt {
//     fn top_left(&self) -> bevy::math::Vec2 {
//         bevy::math::Vec2::new(self.left(), self.top())
//     }
//     fn top_right(&self) -> bevy::math::Vec2 {
//         bevy::math::Vec2::new(self.right(), self.top())
//     }
//     fn bottom_left(&self) -> bevy::math::Vec2 {
//         bevy::math::Vec2::new(self.left(), self.bottom())
//     }
//     fn bottom_right(&self) -> bevy::math::Vec2 {
//         bevy::math::Vec2::new(self.right(), self.bottom())
//     }
//     fn left(&self) -> f32;
//     fn right(&self) -> f32;
//     fn top(&self) -> f32;
//     fn bottom(&self) -> f32;
// }

// impl BevyConvRectExt for bevy::math::Rect {
//     fn left(&self) -> f32 {
//         self.min.x
//     }
//     fn right(&self) -> f32 {
//         self.max.x
//     }
//     fn top(&self) -> f32 {
//         self.min.y
//     }
//     fn bottom(&self) -> f32 {
//         self.max.y
//     }
// }
