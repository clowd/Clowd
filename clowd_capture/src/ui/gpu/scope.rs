//! Draws the scroll-pick scope reticle into the shared rect instance
//! buffer. Geometry comes from [`crate::ui::components::scope::layout`];
//! this module only turns it into [`RectInstance`]s.

use crate::ui::components::scope::layout::{line_span, ScopeLayout};
use crate::ui::gpu::rect::RectInstance;

/// Dark outline behind every bright element.
const HALO_COLOR: [f32; 4] = [0.0, 0.0, 0.0, 0.55];
/// The hairs inside the ring — white, so they stay legible over the accent
/// ring's color whatever it is.
const HAIR_COLOR: [f32; 4] = [1.0, 1.0, 1.0, 0.95];
/// AA fringe for the SDF (rounded) instances, matching the hints renderer.
const AA: f32 = 1.5;

/// Emit the reticle centered on the cursor. `layout` is in window-local
/// physical pixels; `accent` is the user's accent color, used for the
/// ring, the outer ticks and the center dot.
pub fn emit_scope(rects: &mut Vec<RectInstance>, layout: &ScopeLayout, accent: [f32; 4]) {
    let cx = layout.center_x;
    let cy = layout.center_y;
    let halo = layout.halo;

    // Ring: the dark ring is one halo wider on both edges, so the accent
    // ring sits inside it with an outline either side.
    push_ring(
        rects,
        cx,
        cy,
        layout.ring_radius + halo,
        layout.ring_stroke + halo * 2.0,
        HALO_COLOR,
    );
    push_ring(rects, cx, cy, layout.ring_radius, layout.ring_stroke, accent);

    // Hairs inside the ring, then ticks outside it. Each arm is drawn
    // twice — a dark rect inflated by one halo, then the bright rect.
    for (inner, outer, thickness, color) in [
        (layout.hair_inner, layout.hair_outer, layout.hair_thickness, HAIR_COLOR),
        (layout.tick_inner, layout.tick_outer, layout.tick_thickness, accent),
    ] {
        let arms = arm_rects(cx, cy, inner, outer, thickness);
        for arm in arms {
            rects.push(RectInstance::filled(
                arm[0] - halo,
                arm[1] - halo,
                arm[2] + halo,
                arm[3] + halo,
                HALO_COLOR,
            ));
        }
        for arm in arms {
            rects.push(RectInstance::filled(arm[0], arm[1], arm[2], arm[3], color));
        }
    }

    // Center dot — the exact point the wheel will be aimed at.
    push_disc(rects, cx, cy, layout.dot_radius + halo, HALO_COLOR);
    push_disc(rects, cx, cy, layout.dot_radius, accent);
}

/// The four arms of a cross: left, right, top, bottom, each spanning
/// `inner..outer` from the center.
fn arm_rects(cx: f32, cy: f32, inner: f32, outer: f32, thickness: f32) -> [[f32; 4]; 4] {
    let (x0, x1) = line_span(cx, thickness);
    let (y0, y1) = line_span(cy, thickness);
    [
        [cx - outer, y0, cx - inner, y1],
        [cx + inner, y0, cx + outer, y1],
        [x0, cy - outer, x1, cy - inner],
        [x0, cy + inner, x1, cy + outer],
    ]
}

/// A circular outline of `stroke` width, drawn inward from `radius`. The
/// rect pipeline's SDF path draws the border on the inside of the shape,
/// so a transparent fill leaves just the ring.
fn push_ring(rects: &mut Vec<RectInstance>, cx: f32, cy: f32, radius: f32, stroke: f32, color: [f32; 4]) {
    rects.push(RectInstance {
        dest_px: [cx - radius - AA, cy - radius - AA, cx + radius + AA, cy + radius + AA],
        fill_rgba: [0.0; 4],
        border_rgba: color,
        params: [stroke, 0.0, radius, AA],
    });
}

/// A filled circle.
fn push_disc(rects: &mut Vec<RectInstance>, cx: f32, cy: f32, radius: f32, color: [f32; 4]) {
    rects.push(RectInstance {
        dest_px: [cx - radius - AA, cy - radius - AA, cx + radius + AA, cy + radius + AA],
        fill_rgba: color,
        border_rgba: [0.0; 4],
        params: [0.0, 0.0, radius, AA],
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn arms_are_symmetric_about_the_center() {
        let [left, right, top, bottom] = arm_rects(100.0, 100.0, 4.0, 12.0, 1.0);
        assert_eq!(left[0], 88.0);
        assert_eq!(left[2], 96.0);
        assert_eq!(right[0], 104.0);
        assert_eq!(right[2], 112.0);
        // Horizontal arms share the hair's y span; vertical ones its x span.
        assert_eq!((left[1], left[3]), (right[1], right[3]));
        assert_eq!((top[0], top[2]), (bottom[0], bottom[2]));
    }

    #[test]
    fn a_reticle_emits_a_bounded_number_of_rects() {
        let mut rects = Vec::new();
        emit_scope(&mut rects, &ScopeLayout::compute(50.0, 50.0, 1.0), [1.0, 0.0, 0.0, 1.0]);
        // 2 ring + (4 halo + 4 bright) × 2 bands + 2 dot.
        assert_eq!(rects.len(), 20);
    }
}
