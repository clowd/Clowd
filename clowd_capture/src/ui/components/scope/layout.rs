//! Geometry of the scroll-pick scope reticle.
//!
//! The reticle replaces the OS pointer while [`crate::ui::shared::UiSharedState::scroll_pick_mode`]
//! is set: a ring around the cursor, four thin hairs inside it, four thick
//! ticks outside it, and a centre dot marking the exact point the wheel
//! will be aimed at. Every dimension is in DIPs and scaled by the
//! monitor's DPI at compute time, so the reticle is the same physical size
//! on every display.

/// Outer radius of the ring.
const RING_RADIUS: f32 = 15.0;
/// Ring stroke, drawn inward from `RING_RADIUS`.
const RING_STROKE: f32 = 2.0;
/// Dark outline drawn one step outside every bright element so the reticle
/// reads against both light and dark backgrounds.
const HALO: f32 = 1.0;
/// Radius kept clear at the centre so the pixel under the cursor stays
/// visible around the dot.
const CENTER_GAP: f32 = 4.0;
/// Thickness of the hairs inside the ring.
const HAIR_THICKNESS: f32 = 1.0;
/// Gap between the ring and the outer ticks.
const TICK_GAP: f32 = 3.0;
/// Length of each outer tick.
const TICK_LEN: f32 = 9.0;
/// Thickness of the outer ticks.
const TICK_THICKNESS: f32 = 3.0;
/// Radius of the centre dot.
const DOT_RADIUS: f32 = 1.5;

/// Distance from the cursor to the furthest pixel the reticle touches, in
/// DIPs. The picker's hint uses this to keep clear of the reticle.
pub const SCOPE_EXTENT: f32 = RING_RADIUS + TICK_GAP + TICK_LEN + HALO;

/// The reticle in window-local physical pixels, ready to emit.
#[derive(Debug, Clone, Copy)]
pub struct ScopeLayout {
    pub center_x: f32,
    pub center_y: f32,
    pub ring_radius: f32,
    pub ring_stroke: f32,
    pub halo: f32,
    /// Hairs run from `hair_inner` to `hair_outer` out from the centre
    /// along each axis.
    pub hair_inner: f32,
    pub hair_outer: f32,
    pub hair_thickness: f32,
    /// Ticks run from `tick_inner` to `tick_outer`, outside the ring.
    pub tick_inner: f32,
    pub tick_outer: f32,
    pub tick_thickness: f32,
    pub dot_radius: f32,
}

impl ScopeLayout {
    /// Build the reticle centred on `(center_x, center_y)`, given in
    /// window-local physical pixels.
    pub fn compute(center_x: f32, center_y: f32, dpi: f32) -> Self {
        let dpi = dpi.max(0.1);
        // Whole physical pixels: the hairs are one pixel wide at 100 %,
        // and a fractional thickness would smear them across two.
        let px = |dips: f32| (dips * dpi).round().max(1.0);

        let ring_radius = px(RING_RADIUS);
        let ring_stroke = px(RING_STROKE);
        let halo = px(HALO);
        let tick_inner = ring_radius + px(TICK_GAP);

        Self {
            center_x: center_x.round(),
            center_y: center_y.round(),
            ring_radius,
            ring_stroke,
            halo,
            hair_inner: px(CENTER_GAP),
            // Stop at the ring's inner edge — overlapping it would thicken
            // the stroke at four points.
            hair_outer: (ring_radius - ring_stroke).max(px(CENTER_GAP)),
            hair_thickness: px(HAIR_THICKNESS),
            tick_inner,
            tick_outer: tick_inner + px(TICK_LEN),
            tick_thickness: px(TICK_THICKNESS),
            dot_radius: px(DOT_RADIUS),
        }
    }
}

/// Pixel span of a `thickness`-wide line centred on `center`. Snapped so a
/// 1-px hair covers the cursor's own pixel exactly rather than straddling
/// two at half intensity — the rect pipeline's axis-aligned path has no
/// anti-aliasing.
pub fn line_span(center: f32, thickness: f32) -> (f32, f32) {
    let lo = center - (thickness / 2.0).floor();
    (lo, lo + thickness)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hairs_stop_at_the_ring_and_ticks_start_outside_it() {
        for dpi in [1.0, 1.5, 2.0, 3.0] {
            let s = ScopeLayout::compute(100.0, 100.0, dpi);
            assert!(s.hair_inner < s.hair_outer, "dpi {dpi}: hairs must have length");
            assert!(
                s.hair_outer <= s.ring_radius - s.ring_stroke,
                "dpi {dpi}: hairs must stop at the ring's inner edge"
            );
            assert!(s.tick_inner > s.ring_radius, "dpi {dpi}: ticks must clear the ring");
            assert!(s.tick_outer > s.tick_inner, "dpi {dpi}: ticks must have length");
            assert!(
                s.tick_outer <= SCOPE_EXTENT * dpi + s.halo,
                "dpi {dpi}: reticle must fit inside the extent the hint keeps clear"
            );
        }
    }

    #[test]
    fn the_reticle_survives_a_negative_local_origin() {
        // At a monitor seam the neighbouring window draws the same reticle
        // with a centre outside its own bounds, so every rect it emits is
        // negative or off-window. That must stay well-formed (min < max)
        // rather than degenerate — the GPU clips it, the layout must not
        // invert it.
        let s = ScopeLayout::compute(-40.0, 12.0, 1.5);
        assert!(s.hair_inner < s.hair_outer);
        assert!(s.tick_inner < s.tick_outer);
        let (lo, hi) = line_span(s.center_x, s.hair_thickness);
        assert!(lo < hi, "a hair at a negative origin must keep min < max");
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
