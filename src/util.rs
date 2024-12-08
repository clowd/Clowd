use std::cmp::{max, min};

use bracket_geometry::prelude::{Point, PointF};
use nannou::prelude::*;
use wgpu::Texture;

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

pub fn draw_dashed_rectangle(draw: &Draw, rect: Rect, weight: f32, dash_length: f32, texture: &Texture, time: f32) {
    // let draw = draw.scissor(rect.pad(-1.0));

    let x = vec2(dash_length * 4.0, 0.0);
    let y = vec2(0.0, dash_length * 4.0);

    draw_dashed_line_polyline(&draw, rect.top_left() - x, rect.top_right() + x, weight, dash_length, texture, time);
    draw_dashed_line_polyline(&draw, rect.top_right() + y, rect.bottom_right() - y, weight, dash_length, texture, time);
    draw_dashed_line_polyline(&draw, rect.bottom_right() + x, rect.bottom_left() - x, weight, dash_length, texture, time);
    draw_dashed_line_polyline(&draw, rect.bottom_left() - y, rect.top_left() + y, weight, dash_length, texture, time);

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
    let cut_ratio = if prefer_down { 1.0 - PIXEL_SELECTION_ROUNDING_THRESHOLD } else { PIXEL_SELECTION_ROUNDING_THRESHOLD };
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
    (start.x - end.x).abs().max((start.y - end.y).abs())
}
