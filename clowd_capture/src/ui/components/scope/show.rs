//! Painting the scroll-pick reticle.
//!
//! [`layout`] still owns every dimension, in window-local physical pixels;
//! this module decides which hosts draw the reticle and turns that geometry
//! into egui shapes. It replaces the old rect-pipeline emitter.
//!
//! The reticle is drawn by every monitor it reaches rather than only the
//! one under the cursor: aiming near a seam would otherwise show half a
//! scope, and with the OS pointer hidden that half is the only aim
//! indicator there is. It is sized by the CURSOR's monitor so both halves
//! match in physical pixels and line up across the seam, which is why the
//! inputs carry a DPI of their own instead of using the host's.

use egui::{pos2, Color32, Painter, Rect, Stroke};

use crate::ui::components::scope::layout;
use crate::ui::components::InputCtx;
use crate::ui::shared::{aabb_intersects, UiMonitor};
use clowd_rust_core::geometry::{RectExt, ScreenPointF, ScreenRectF};

/// Dark outline behind every bright element, so the reticle reads against
/// both light and dark desktops.
pub const HALO: Color32 = Color32::from_black_alpha(140);
/// The hairs inside the ring — white, so they stay legible over the accent
/// ring whatever colour it is.
pub const HAIR: Color32 = Color32::from_rgba_unmultiplied_const(255, 255, 255, 242);

/// One host's copy of the reticle. The center stays in virtual-desktop
/// physical pixels because the shape straddles seams; `dpi` is the cursor
/// monitor's, not this host's.
#[derive(Clone, Copy, PartialEq)]
pub struct ScopeInputs {
    pub center: ScreenPointF,
    pub dpi: f32,
    pub accent: Color32,
}

/// Whether this monitor draws the reticle.
///
/// The rule is the old `shared::scroll_pick_visibility` plus the per-host
/// reach test that used to sit in the GPU hints renderer: while the picker
/// owns the overlay, every host whose bounds intersect the square
/// `cursor ± SCOPE_EXTENT * cursor_monitor.dpi` draws the whole reticle.
///
/// The `overlays_visible` gate must keep agreeing with
/// `app::update_cursor_visibility`, which hides the OS pointer under
/// exactly the same condition: if this said `None` while the pointer stayed
/// hidden there would be no pointer at all.
///
/// The host's index is unused — the reach test is geometric — but the
/// signature matches every other overlay's builder.
pub fn inputs(_index: usize, monitor: &UiMonitor, c: &InputCtx<'_>) -> Option<ScopeInputs> {
    let i = c.input;
    if !(i.scroll_pick_mode && i.overlays_visible) {
        return None;
    }
    let target = c.monitors[c.cursor_index?];
    let ext = layout::SCOPE_EXTENT * target.dpi_scale.max(0.1);
    let (cx, cy) = (i.virtual_cursor.x, i.virtual_cursor.y);
    let square = ScreenRectF::from_xy_size(cx - ext, cy - ext, 2.0 * ext, 2.0 * ext);
    aabb_intersects(square, monitor.bounds.to_f32()).then_some(ScopeInputs {
        center: i.virtual_cursor,
        dpi: target.dpi_scale,
        accent: c.accent,
    })
}

pub fn show(p: &Painter, s: &ScopeInputs, monitor: &UiMonitor) {
    let l = layout::ScopeLayout::compute(
        s.center.x - monitor.bounds.left() as f32,
        s.center.y - monitor.bounds.top() as f32,
        s.dpi,
    );
    // The layout is in this window's physical pixels; the painter works in
    // this monitor's points.
    let k = 1.0 / monitor.dpi_scale.max(0.1);
    let c = pos2(l.center_x * k, l.center_y * k);

    // Ring: the dark ring is one halo wider on both edges, so the accent
    // ring sits inside it with an outline either side. egui centres a
    // stroke on its radius, where the old SDF drew it inward, hence the
    // half-width each radius steps back by.
    let halo_w = l.ring_stroke + 2.0 * l.halo;
    p.circle_stroke(c, (l.ring_radius + l.halo - halo_w / 2.0) * k, Stroke::new(halo_w * k, HALO));
    p.circle_stroke(
        c,
        (l.ring_radius - l.ring_stroke / 2.0) * k,
        Stroke::new(l.ring_stroke * k, s.accent),
    );

    // Hairs inside the ring, then ticks outside it. Each band is drawn
    // twice — the dark arms, inflated by one halo all round, then the
    // bright ones over them.
    for (inner, outer, thick, col) in [
        (l.hair_inner, l.hair_outer, l.hair_thickness, HAIR),
        (l.tick_inner, l.tick_outer, l.tick_thickness, s.accent),
    ] {
        for halo in [true, false] {
            let (t, col, grow) = if halo {
                (thick + 2.0 * l.halo, HALO, l.halo)
            } else {
                (thick, col, 0.0)
            };
            for [x0, y0, x1, y1] in layout::arm_rects(&l, inner - grow, outer + grow, t) {
                p.rect_filled(Rect::from_min_max(pos2(x0 * k, y0 * k), pos2(x1 * k, y1 * k)), 0u8, col);
            }
        }
    }

    // Center dot — the exact point the wheel will be aimed at.
    p.circle_filled(c, (l.dot_radius + l.halo) * k, HALO);
    p.circle_filled(c, l.dot_radius * k, s.accent);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::interaction::InteractionState;
    use crate::ui::shared::monitor_index_at;
    use clowd_rust_core::geometry::ScreenRect;

    /// Two monitors at the same scale, so the reach either side of the
    /// seam is measured in the same units the cursor's monitor sets.
    fn monitors() -> [UiMonitor; 2] {
        [
            UiMonitor {
                bounds: ScreenRect::from_xy_size(0, 0, 1920, 1080),
                dpi_scale: 1.0,
                is_primary: true,
            },
            UiMonitor {
                bounds: ScreenRect::from_xy_size(1920, 0, 1920, 1080),
                dpi_scale: 1.0,
                is_primary: false,
            },
        ]
    }

    fn ctx<'a>(input: &'a InteractionState, monitors: &'a [UiMonitor]) -> InputCtx<'a> {
        InputCtx {
            input,
            monitors,
            cursor_index: monitor_index_at(monitors, input.virtual_cursor),
            accent: Color32::RED,
            hovered_window_title: None,
            hovered_monitor_name: None,
            hovered_pixel_bgra: None,
            cursor_image_rect: None,
            cursor_overlay_visible: true,
        }
    }

    fn picking(cursor_x: f32) -> InteractionState {
        let mut input = InteractionState::new();
        input.captured = true;
        input.scroll_pick_mode = true;
        input.virtual_cursor = ScreenPointF::new(cursor_x, 400.0);
        input
    }

    /// A reticle near a seam is drawn whole by both hosts, so neither side
    /// shows a half scope; once the cursor is further than the reticle's
    /// own extent, the neighbour stops drawing it.
    #[test]
    fn a_cursor_ten_px_from_the_seam_reaches_both_hosts_and_forty_px_reaches_one() {
        let m = monitors();
        // SCOPE_EXTENT is 28 DIPs, so 10 px short of the seam reaches over
        // it and 40 px does not.
        let near = picking(1910.0);
        assert!(inputs(0, &m[0], &ctx(&near, &m)).is_some(), "the cursor's own host");
        assert!(inputs(1, &m[1], &ctx(&near, &m)).is_some(), "the neighbour across the seam");

        let far = picking(1880.0);
        assert!(inputs(0, &m[0], &ctx(&far, &m)).is_some());
        assert!(inputs(1, &m[1], &ctx(&far, &m)).is_none(), "out of reach");

        // Both copies agree on the size, which is the cursor monitor's.
        let from_neighbour = inputs(1, &m[1], &ctx(&near, &m)).expect("in reach");
        assert_eq!(from_neighbour.dpi, m[0].dpi_scale);
        assert_eq!(from_neighbour.center, near.virtual_cursor);
    }

    /// The old `shared::scroll_pick_visibility` rule, now answered per
    /// host: nothing outside pick mode, nothing under Q, and nothing at all
    /// when the cursor sits in a gap between monitors.
    #[test]
    fn scroll_pick_reticle_follows_the_cursor_and_obeys_the_overlay_toggle() {
        let m = monitors();
        let mut input = picking(300.0);

        input.scroll_pick_mode = false;
        assert!(
            inputs(0, &m[0], &ctx(&input, &m)).is_none(),
            "not picking, even with a captured selection"
        );
        input.scroll_pick_mode = true;
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_some());
        assert!(inputs(1, &m[1], &ctx(&input, &m)).is_none(), "far from the seam");

        // Cursor off every monitor — nothing to size the reticle by.
        input.virtual_cursor = ScreenPointF::new(300.0, 2000.0);
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none());
        input.virtual_cursor = ScreenPointF::new(300.0, 400.0);

        input.overlays_visible = false;
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none(), "Q hides it");
    }
}
