//! Geometry of the scroll-pick scope reticle.
//!
//! The reticle replaces the OS pointer while [`crate::ui::shared::UiSharedState::scroll_pick_mode`]
//! is set: a ring around the cursor, cut open at the four axes, with a
//! hair running out through each gap and a center dot marking the exact
//! point the wheel will be aimed at. Every dimension is in DIPs and scaled
//! by the monitor's DPI at compute time, so the reticle is the same
//! physical size on every display.

/// Outer radius of the ring.
const RING_RADIUS: f32 = 13.0;
/// Ring stroke, drawn inward from `RING_RADIUS`.
const RING_STROKE: f32 = 2.0;
/// Half-width of the gap the ring leaves at each axis, in radians. The
/// hairs pass through these, so the gap is wide enough to clear a hair
/// plus its halo with air either side at every DPI.
const RING_GAP: f32 = 0.23;
/// Dark outline drawn one step outside every bright element so the reticle
/// reads against both light and dark backgrounds.
const HALO: f32 = 1.0;
/// Radius kept clear at the center so the pixel under the cursor stays
/// visible around the dot.
const CENTER_GAP: f32 = 4.0;
/// Thickness of the hairs.
const HAIR_THICKNESS: f32 = 1.0;
/// How far each hair runs from the center — out through the ring's gap
/// and a little past it.
const HAIR_LENGTH: f32 = 20.0;
/// Radius of the center dot.
const DOT_RADIUS: f32 = 1.5;

/// Distance from the cursor to the furthest pixel the reticle touches, in
/// DIPs. The picker's hint uses this to keep clear of the reticle.
pub const SCOPE_EXTENT: f32 = HAIR_LENGTH + HALO;

/// The reticle in window-local physical pixels, ready to emit.
#[derive(Debug, Clone, Copy)]
pub struct ScopeLayout {
    pub center_x: f32,
    pub center_y: f32,
    pub ring_radius: f32,
    pub ring_stroke: f32,
    /// Half-width of the gap left at each axis, in radians. Unscaled — an
    /// angle is the same at every DPI.
    pub ring_gap: f32,
    pub halo: f32,
    /// Hairs run from `hair_inner` to `hair_outer` out from the center
    /// along each axis, crossing the ring's gaps on the way.
    pub hair_inner: f32,
    pub hair_outer: f32,
    pub hair_thickness: f32,
    pub dot_radius: f32,
}

impl ScopeLayout {
    /// Build the reticle centered on `(center_x, center_y)`, given in
    /// window-local physical pixels.
    pub fn compute(center_x: f32, center_y: f32, dpi: f32) -> Self {
        let dpi = dpi.max(0.1);
        // Whole physical pixels: the hairs are one pixel wide at 100 %,
        // and a fractional thickness would smear them across two.
        let px = |dips: f32| (dips * dpi).round().max(1.0);

        Self {
            center_x: center_x.round(),
            center_y: center_y.round(),
            ring_radius: px(RING_RADIUS),
            ring_stroke: px(RING_STROKE),
            ring_gap: RING_GAP,
            halo: px(HALO),
            hair_inner: px(CENTER_GAP),
            hair_outer: px(HAIR_LENGTH),
            hair_thickness: px(HAIR_THICKNESS),
            dot_radius: px(DOT_RADIUS),
        }
    }

    /// The four arcs of the ring, as `(start, end)` angle pairs in
    /// radians, each one spanning the quadrant between two gaps.
    /// `pad` widens both ends of every arc, which the halo pass uses to
    /// keep its outline past the bright arc's tips.
    pub fn ring_arcs(&self, pad: f32) -> [(f32, f32); 4] {
        let quarter = std::f32::consts::FRAC_PI_2;
        let mut out = [(0.0, 0.0); 4];
        for (i, slot) in out.iter_mut().enumerate() {
            let start = i as f32 * quarter + self.ring_gap;
            *slot = (start - pad, start + quarter - 2.0 * self.ring_gap + pad);
        }
        out
    }
}

/// The four arms of the cross, each spanning `inner..outer` out from the
/// center along its axis: left, right, top, bottom, in window-local
/// physical pixels. The across-axis span comes from [`line_span`], so a
/// one-pixel hair lands on the cursor's own pixel instead of straddling
/// two.
pub fn arm_rects(l: &ScopeLayout, inner: f32, outer: f32, thickness: f32) -> [[f32; 4]; 4] {
    let (cx, cy) = (l.center_x, l.center_y);
    let (x0, x1) = line_span(cx, thickness);
    let (y0, y1) = line_span(cy, thickness);
    [
        [cx - outer, y0, cx - inner, y1],
        [cx + inner, y0, cx + outer, y1],
        [x0, cy - outer, x1, cy - inner],
        [x0, cy + inner, x1, cy + outer],
    ]
}

/// Pixel span of a `thickness`-wide line centered on `center`. Snapped so a
/// 1-px hair covers the cursor's own pixel exactly rather than straddling
/// two at half intensity, which is what a hair landing on a half pixel
/// would look like once the painter antialiases it.
pub fn line_span(center: f32, thickness: f32) -> (f32, f32) {
    let lo = center - (thickness / 2.0).floor();
    (lo, lo + thickness)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hairs_run_through_the_rings_gaps_and_out_past_it() {
        for dpi in [1.0, 1.5, 2.0, 3.0] {
            let s = ScopeLayout::compute(100.0, 100.0, dpi);
            assert!(s.hair_inner < s.hair_outer, "dpi {dpi}: hairs must have length");
            assert!(
                s.hair_inner < s.ring_radius - s.ring_stroke,
                "dpi {dpi}: hairs must start inside the ring"
            );
            assert!(s.hair_outer > s.ring_radius, "dpi {dpi}: hairs must carry on past the ring");
            assert!(
                s.hair_outer + s.halo <= SCOPE_EXTENT * dpi + s.halo,
                "dpi {dpi}: reticle must fit inside the extent the hint keeps clear"
            );
        }
    }

    /// A hair and its halo pass through the gap without touching either
    /// arc tip, at every DPI: the gap is an angle, so the clearance it
    /// buys shrinks in DIPs as the ring grows in pixels.
    #[test]
    fn the_gaps_clear_the_hairs_at_every_dpi() {
        for dpi in [1.0, 1.5, 2.0, 3.0] {
            let s = ScopeLayout::compute(100.0, 100.0, dpi);
            // Distance from the axis to an arc tip, at the ring's radius.
            let clearance = s.ring_radius * s.ring_gap.sin();
            let hair_half = s.hair_thickness / 2.0 + s.halo;
            assert!(
                clearance > hair_half,
                "dpi {dpi}: gap {clearance} must clear the haloed hair {hair_half}"
            );
        }
    }

    /// The arcs tile the ring: four equal spans with a gap of `2 *
    /// ring_gap` between each pair, and `pad` grows both ends of each.
    #[test]
    fn ring_arcs_leave_a_gap_at_every_axis() {
        let s = ScopeLayout::compute(100.0, 100.0, 1.0);
        let arcs = s.ring_arcs(0.0);
        for (i, (start, end)) in arcs.iter().enumerate() {
            let quarter = std::f32::consts::FRAC_PI_2;
            assert!((start - (i as f32 * quarter + s.ring_gap)).abs() < 1e-5);
            assert!((end - start - (quarter - 2.0 * s.ring_gap)).abs() < 1e-5);
            // The axis itself — the multiple of a quarter turn — lies in
            // the gap, not under an arc.
            assert!(*start > i as f32 * quarter, "arc {i} must start after its axis");
        }
        let padded = s.ring_arcs(0.05);
        assert!(padded[0].0 < arcs[0].0 && padded[0].1 > arcs[0].1, "pad grows both ends");
    }

    #[test]
    fn the_reticle_survives_a_negative_local_origin() {
        // At a monitor seam the neighboring window draws the same reticle
        // with a center outside its own bounds, so every rect it emits is
        // negative or off-window. That must stay well-formed (min < max)
        // rather than degenerate — the GPU clips it, the layout must not
        // invert it.
        let s = ScopeLayout::compute(-40.0, 12.0, 1.5);
        assert!(s.hair_inner < s.hair_outer);
        let (lo, hi) = line_span(s.center_x, s.hair_thickness);
        assert!(lo < hi, "a hair at a negative origin must keep min < max");
    }

    /// The arms of one band are mirror images across the center, and the
    /// two horizontal ones share the hair's y span while the two vertical
    /// ones share its x span — otherwise the cross would be lopsided.
    #[test]
    fn arms_are_symmetric_about_the_center() {
        let l = ScopeLayout::compute(100.0, 100.0, 1.0);
        let [left, right, top, bottom] = arm_rects(&l, 4.0, 12.0, 1.0);
        assert_eq!(left[0], 88.0);
        assert_eq!(left[2], 96.0);
        assert_eq!(right[0], 104.0);
        assert_eq!(right[2], 112.0);
        assert_eq!((left[1], left[3]), (right[1], right[3]));
        assert_eq!((top[0], top[2]), (bottom[0], bottom[2]));
    }

    #[test]
    fn a_one_px_hair_lands_on_the_cursors_own_pixel() {
        let (lo, hi) = line_span(100.0, 1.0);
        assert_eq!((lo, hi), (100.0, 101.0));
        // Odd and even thicknesses both stay on whole pixels.
        assert_eq!(line_span(100.0, 3.0), (99.0, 102.0));
        assert_eq!(line_span(100.0, 4.0), (98.0, 102.0));
    }
}
