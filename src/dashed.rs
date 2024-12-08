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
