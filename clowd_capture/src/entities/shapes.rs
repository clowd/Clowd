use crate::geometry::*;
use bevy::math::{Rect, Vec2};
use bevy_prototype_lyon::path::ShapePath;

pub fn shape_dashed_line(start: Vec2, end: Vec2, dash_length: f32) -> ShapePath {
    let total_distance = start.distance(end);
    let direction = (end - start).normalize();

    let mut current_distance = 0.0;
    let mut shape = ShapePath::new();
    let mut toggle = true;

    while current_distance < total_distance {
        let dash_start = start + direction * current_distance;
        let dash_end = start + direction * (current_distance + dash_length).min(total_distance);

        if toggle {
            shape = shape.move_to(dash_start);
            shape = shape.line_to(dash_end);
        }

        toggle = !toggle;
        current_distance += dash_length;
    }

    shape
}

/// Given the edges of the rectangle and a distance `dist` along its perimeter (starting from top-left corner, going clockwise),
/// return the position (Vec2) on the perimeter at that distance.
fn position_along_perimeter(edges: &[(Vec2, Vec2)], dist: f32) -> Vec2 {
    let mut remaining = dist;
    for (start, end) in edges {
        let edge_len = start.distance(*end);
        if remaining <= edge_len {
            let dir = (*end - *start).normalize();
            return *start + dir * remaining;
        }
        remaining -= edge_len;
    }
    // If `dist` == perimeter, return the start point (closing the loop).
    edges[0].0
}

pub fn shape_rectangle(rect: Rect) -> ShapePath {
    let top_left = rect.top_left();
    let top_right = rect.top_right();
    let bottom_right = rect.bottom_right();
    let bottom_left = rect.bottom_left();

    let mut shape = ShapePath::new();
    shape = shape.move_to(top_left);
    shape = shape.line_to(top_right);
    shape = shape.line_to(bottom_right);
    shape = shape.line_to(bottom_left);
    shape = shape.close();
    shape
}

pub fn shape_dashed_rectangle(rect: Rect, dash_length: f32, time: f32) -> ShapePath {
    let dash_offset = (time * 30.0) % (dash_length * 2.0);

    // Extract corners and form edges in order: top, right, bottom, left
    let top_left = rect.top_left();
    let top_right = rect.top_right();
    let bottom_right = rect.bottom_right();
    let bottom_left = rect.bottom_left();

    let edges = [
        (top_left, top_right),
        (top_right, bottom_right),
        (bottom_right, bottom_left),
        (bottom_left, top_left),
    ];

    // Compute the total perimeter and the cumulative distances at each corner.
    let mut edge_lengths = Vec::new();
    for (s, e) in &edges {
        edge_lengths.push(s.distance(*e));
    }

    let perimeter: f32 = edge_lengths.iter().sum();

    // Distances at corners along the perimeter:
    // corner_distances[0] = top_left (0.0)
    // corner_distances[1] = top_right
    // corner_distances[2] = bottom_right
    // corner_distances[3] = bottom_left
    let mut corner_distances = Vec::new();
    {
        let mut accum = 0.0;
        for &len in &edge_lengths {
            corner_distances.push(accum);
            accum += len;
        }
        // This final accum equals the perimeter, which loops back to top_left
        // We don't push perimeter again because it closes the loop.
    }

    // The pattern: Two colors alternate every dash_length.
    // cycle_length = dash_length * 2.0
    // first color = [0.0,0.0], second color = [1.0,1.0]
    let cycle_length = dash_length * 2.0;

    // Initial color toggle based on dash_offset
    let mut toggle = dash_offset < dash_length;

    // How much of the current color segment is left from our starting offset
    let initial_segment_length = if toggle {
        // Starting in first color segment
        dash_length - dash_offset.min(dash_length)
    } else {
        // Starting in second color segment
        cycle_length - dash_offset
    };

    // let mut points_colored = Vec::new();

    let mut traveled = 0.0;
    let mut current_segment_length = initial_segment_length;

    // let mut corner_segments = Vec::new();
    let mut shape = ShapePath::new();

    while traveled < perimeter {
        let end_segment = (traveled + current_segment_length).min(perimeter);

        // Draw this dash segment, possibly split by corners
        // Find corners that lie strictly between traveled and end_segment
        let mut sub_segment_starts = vec![traveled];
        for &c_dist in &corner_distances {
            if c_dist > traveled && c_dist < end_segment {
                // This corner lies inside the dash
                sub_segment_starts.push(c_dist);
            }
        }
        // Also add the end of the segment
        sub_segment_starts.push(end_segment);

        // Now draw sub-segments from these breakpoints
        for w in 0..(sub_segment_starts.len() - 1) {
            let seg_start = sub_segment_starts[w];
            let seg_end = sub_segment_starts[w + 1];

            let start_pos = position_along_perimeter(&edges, seg_start);
            let end_pos = position_along_perimeter(&edges, seg_end);

            // Push the points for this sub-segment
            // Ensure we don't duplicate points unnecessarily. But it's usually fine if we do.
            //     points_colored.push((start_pos, texture_coords));
            //     points_colored.push((end_pos, texture_coords));
            if toggle {
                shape = shape.move_to(start_pos);
                shape = shape.line_to(end_pos);
            }
        }

        // Move forward
        traveled += current_segment_length;

        // Flip toggle (switch colors)
        toggle = !toggle;
        // From now on, full dash_length segments
        current_segment_length = dash_length;
    }

    shape

    // Draw the resulting polyline
    // draw.polyline()
    //     .weight(weight)
    //     .points_textured(texture, points_colored);

    // for (start, end) in corner_segments {
    //     draw.line()
    //         .weight(weight)
    //         .start(start)
    //         .end(end)
    //         .color(nannou::color::RED);
    // }
}
