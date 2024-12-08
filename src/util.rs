use std::cmp::{max, min};

use bracket_geometry::prelude::{Point, PointF, Rect, RectF};
use nannou::{
    glam::Vec2,
    prelude::{vec2, Rect as NRect},
    wgpu::Texture,
    Draw,
};

pub fn draw_dashed_line_polyline(draw: &Draw, start: Vec2, end: Vec2, weight: f32, dash_length: f32, texture: &Texture, time: f32) {
    let total_distance = start.distance(end);
    let direction = (end - start).normalize();

    let dash_offset = (time * 30.0) % (dash_length * 2.0);

    let mut current_distance = dash_offset;
    let mut points_colored = Vec::new();
    let mut toggle = true;

    while current_distance < total_distance {
        let dash_start = start + direction * current_distance;
        let dash_end = start + direction * (current_distance + dash_length).min(total_distance);

        let texture_coords = if toggle { [0.0, 0.0] } else { [1.0, 1.0] };
        points_colored.push((dash_start, texture_coords));
        points_colored.push((dash_end, texture_coords));

        toggle = !toggle;
        current_distance += dash_length;
    }

    draw.polyline()
        .weight(weight)
        .points_textured(&texture, points_colored);
}

pub fn draw_dashed_rectangle(draw: &Draw, rect: NRect, weight: f32, dash_length: f32, texture: &Texture, time: f32) {
    // let draw = draw.scissor(rect.pad(-1.0));

    let x = vec2(dash_length * 4.0, 0.0);
    let y = vec2(0.0, dash_length * 4.0);

    draw_dashed_line_polyline(&draw, rect.top_left() - x, rect.top_right() + x, weight, dash_length, texture, time);
    draw_dashed_line_polyline(
        &draw,
        rect.top_right() + y,
        rect.bottom_right() - y,
        weight,
        dash_length,
        texture,
        time,
    );
    draw_dashed_line_polyline(
        &draw,
        rect.bottom_right() + x,
        rect.bottom_left() - x,
        weight,
        dash_length,
        texture,
        time,
    );
    draw_dashed_line_polyline(
        &draw,
        rect.bottom_left() - y,
        rect.top_left() + y,
        weight,
        dash_length,
        texture,
        time,
    );

    // draw.line()
    //     .start(rect.top_left() + vec2(-1.0, 1.0))
    //     .end(rect.top_left())
    //     .weight(weight)
    //     .color(BLACK);

    // draw.line()
    //     .start(rect.top_right() + vec2(1.0, 1.0))
    //     .end(rect.top_right())
    //     .weight(weight)
    //     .color(BLACK);

    // draw.line()
    //     .start(rect.bottom_right() + vec2(1.0, -1.0))
    //     .end(rect.bottom_right())
    //     .weight(weight)
    //     .color(BLACK);

    // draw.line()
    //     .start(rect.bottom_left() + vec2(-1.0, -1.0))
    //     .end(rect.bottom_left())
    //     .weight(weight)
    //     .color(BLACK);
}

const PIXEL_SELECTION_ROUNDING_THRESHOLD: f64 = 0.2;

/// Helper function to round a pixel value
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

/// Helper function to round a pair of pixels
fn round_pixel_pair(v1: f64, v2: f64) -> (i32, i32) {
    let vmin = v1.min(v2);
    let vmax = v1.max(v2);
    (round_pixel(vmin, true), round_pixel(vmax, false))
}

/// Rounds pixel selection coordinates
pub fn round_px_selection(x1: f64, y1: f64, x2: f64, y2: f64) -> (i32, i32, i32, i32) {
    let (horz_low, horz_high) = round_pixel_pair(x1, x2);
    let (vert_low, vert_high) = round_pixel_pair(y1, y2);
    (horz_low, vert_low, horz_high, vert_high)
}

pub fn distance2d_pythagoras_squared(start: Point, end: Point) -> f32 {
    let dx = (max(start.x, end.x) - min(start.x, end.x)) as f32;
    let dy = (max(start.y, end.y) - min(start.y, end.y)) as f32;
    (dx * dx) + (dy * dy)
}

pub fn distance2d_pythagoras_squared_f(start: PointF, end: PointF) -> f32 {
    let dx = start.x.max(end.x) - start.x.min(end.x);
    let dy = start.y.max(end.y) - start.y.min(end.y);
    (dx * dx) + (dy * dy)
}

/// Calculates a Manhattan distance between two points
pub fn distance2d_manhattan(start: Point, end: Point) -> f32 {
    let dx = (max(start.x, end.x) - min(start.x, end.x)) as f32;
    let dy = (max(start.y, end.y) - min(start.y, end.y)) as f32;
    dx + dy
}

/// Calculates a Chebyshev distance between two points
/// See: http://theory.stanford.edu/~amitp/GameProgramming/Heuristics.html
pub fn distance2d_chebyshev(start: Point, end: Point) -> f32 {
    let dx = (max(start.x, end.x) - min(start.x, end.x)) as f32;
    let dy = (max(start.y, end.y) - min(start.y, end.y)) as f32;
    if dx > dy {
        (dx - dy) + 1.0 * dy
    } else {
        (dy - dx) + 1.0 * dx
    }
}

/// Calculates a Pythagoras distance between two points.
pub fn distance2d_pythagoras(start: Point, end: Point) -> f32 {
    let dsq = distance2d_pythagoras_squared(start, end);
    f32::sqrt(dsq)
}

// Calculates a diagonal distance
pub fn distance2d_diagonal(start: Point, end: Point) -> f32 {
    i32::max((start.x - end.x).abs(), (start.y - end.y).abs()) as f32
}

pub fn distance2d_diagonal_f(start: PointF, end: PointF) -> f32 {
    (start.x - end.x)
        .abs()
        .max((start.y - end.y).abs())
}

pub trait ToFloatRect {
    fn to_float(&self) -> RectF;
}

impl ToFloatRect for Rect {
    fn to_float(&self) -> RectF {
        RectF::with_exact(self.x1 as f32, self.y1 as f32, self.x2 as f32, self.y2 as f32)
    }
}

pub trait ToIntRect {
    fn to_int(&self) -> Rect;
}

impl ToIntRect for RectF {
    fn to_int(&self) -> Rect {
        Rect::with_exact(self.x1 as i32, self.y1 as i32, self.x2 as i32, self.y2 as i32)
    }
}

pub fn point_to_widened_rect(radius: i32, pt: Point) -> Rect {
    Rect::with_size(pt.x - radius, pt.y - radius, radius * 2, radius * 2)
}

pub fn point_to_widened_rect_f(radius: f32, pt: PointF) -> RectF {
    RectF::with_size(pt.x - radius, pt.y - radius, radius * 2.0, radius * 2.0)
}

pub fn point_to_widened_rect_n(radius: f32, pt: Vec2) -> NRect {
    NRect::from_x_y_w_h(pt.x, pt.y, radius * 2.0, radius * 2.0)
}

pub fn line_to_widened_rect(radius: i32, start: Point, end: Point) -> Rect {
    let x1 = start.x.min(end.x) - radius;
    let y1 = start.y.min(end.y) - radius;
    let x2 = start.x.max(end.x) + radius;
    let y2 = start.y.max(end.y) + radius;
    Rect::with_exact(x1, y1, x2, y2)
}

pub fn line_to_widened_rect_f(radius: f32, start: PointF, end: PointF) -> RectF {
    let x1 = start.x.min(end.x) - radius;
    let y1 = start.y.min(end.y) - radius;
    let x2 = start.x.max(end.x) + radius;
    let y2 = start.y.max(end.y) + radius;
    RectF::with_exact(x1, y1, x2, y2)
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
    fn intersect_with(&self, other: &TRect) -> TRect;
    fn expand(&self, amount: T) -> TRect;
}

impl RectExt<RectF, PointF, f32> for RectF {
    fn top_left(&self) -> PointF {
        PointF::new(self.left(), self.top())
    }

    fn top_right(&self) -> PointF {
        PointF::new(self.right(), self.top())
    }

    fn bottom_left(&self) -> PointF {
        PointF::new(self.left(), self.bottom())
    }

    fn bottom_right(&self) -> PointF {
        PointF::new(self.right(), self.bottom())
    }

    fn left(&self) -> f32 {
        self.x1.min(self.x2)
    }

    fn right(&self) -> f32 {
        self.x1.max(self.x2)
    }

    fn top(&self) -> f32 {
        self.y1.min(self.y2)
    }

    fn bottom(&self) -> f32 {
        self.y1.max(self.y2)
    }

    fn intersect_with(&self, other: &RectF) -> RectF {
        RectF::with_exact(
            self.left().max(other.left()),
            self.top().max(other.top()),
            self.right().min(other.right()),
            self.bottom().min(other.bottom()),
        )
    }

    fn expand(&self, amount: f32) -> RectF {
        RectF::with_exact(
            self.left() - amount,
            self.top() - amount,
            self.right() + amount,
            self.bottom() + amount,
        )
    }
}

impl RectExt<Rect, Point, i32> for Rect {
    fn top_left(&self) -> Point {
        Point::new(self.left(), self.top())
    }

    fn top_right(&self) -> Point {
        Point::new(self.right(), self.top())
    }

    fn bottom_left(&self) -> Point {
        Point::new(self.left(), self.bottom())
    }

    fn bottom_right(&self) -> Point {
        Point::new(self.right(), self.bottom())
    }

    fn left(&self) -> i32 {
        self.x1.min(self.x2)
    }

    fn right(&self) -> i32 {
        self.x1.max(self.x2)
    }

    fn top(&self) -> i32 {
        self.y1.min(self.y2)
    }

    fn bottom(&self) -> i32 {
        self.y1.max(self.y2)
    }

    fn intersect_with(&self, other: &Rect) -> Rect {
        Rect::with_exact(
            self.left().max(other.left()),
            self.top().max(other.top()),
            self.right().min(other.right()),
            self.bottom().min(other.bottom()),
        )
    }

    fn expand(&self, amount: i32) -> Rect {
        Rect::with_exact(
            self.left() - amount,
            self.top() - amount,
            self.right() + amount,
            self.bottom() + amount,
        )
    }
}
