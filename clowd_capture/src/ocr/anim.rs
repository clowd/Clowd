//! Pure animation math for the OCR sweep/reveal/retract effects.
//!
//! Everything here is a pure function of an absolute time `t` (seconds since
//! the phase's shared anchor `Instant`) — the ONLY form that is both
//! frame-rate independent and consistent across render workers running at
//! different refresh rates. No function stores state, and every output is
//! clamped so a worker that wakes up late (or early, from timer skew) still
//! draws a sane frame.

/// Minimum time the scanning sweep stays on screen. Only failure paths use
/// this floor directly (see [`scan_release_secs`]): a successful outcome is
/// additionally held until the sweep's current pass wraps, so the reveal
/// pass can start with the band off-screen and no visible jump.
pub const MIN_SCAN_SECS: f32 = 0.3;
/// One full sweep of the scanning band.
pub const SCAN_PERIOD_SECS: f32 = 1.2;
/// How long the region's dim/desaturation take to fade back to colour on
/// exit. The TEXT does not animate out at all — every bubble and crop
/// vanishes on the first Retracting frame (a reverse cascade reads as the
/// overlay stalling on the way out; disappearance should be instant) — so
/// this fade is the only thing the Retracting phase exists to play.
pub const RETRACT_DURATION_SECS: f32 = 0.18;
/// How long one line/bubble takes to rise and fade in once the sweep's
/// band centre has passed its top edge.
pub const LIFT_DURATION_SECS: f32 = 0.28;
/// Vertical lift distance in physical px at dpi 1.0 — callers multiply by
/// the mode's single dpi_scale.
pub const LIFT_PX: f32 = 4.0;
pub const LIFT_SCALE: f32 = 1.06;
/// The region dim — ONE level for the whole mode. It ramps in when OCR is
/// pressed, HOLDS through the reveal, and fades out on exit. There is
/// deliberately no deeper "lifted" dim: an earlier build darkened a second
/// time as the text rendered, which read as the screenshot dimming twice
/// (owner call). Composed with the desaturation in desktop.wgsl/peek.wgsl
/// this leaves the region at ~65% of its luma — darkened, never crushed.
pub const DIM_MAX: f32 = 0.35;

/// Width (σ) of the sweep band's gaussian falloff, in region-height units.
/// Defined here rather than hardcoded in `ui_lift.wgsl` so the travel
/// overshoot below and the fragment falloff cannot drift apart — the value
/// rides to the GPU per instance (`params.w`, see the shader header).
pub const SWEEP_SIGMA: f32 = 0.10;

/// How far past the region's top/bottom edges the band CENTRE travels, in
/// region-height units. At 3.5σ the gaussian is under 0.3% of peak —
/// visually nothing — so the band has fully exited the bottom when the
/// phase wraps and re-enters from above the top after it: back-to-back
/// passes loop with no visible jump. (The old band travelled exactly
/// [0, 1], which popped a half-band at every wrap.)
const SWEEP_OVERSHOOT: f32 = 3.5 * SWEEP_SIGMA;

/// Cubic ease-out, clamped to [0, 1] on both ends — deliberately no
/// overshoot, because lifted quads sample the desktop texture and any
/// overshoot would reveal pixels outside the line rect.
pub fn ease_out(t: f32) -> f32 {
    let t = t.clamp(0.0, 1.0);
    1.0 - (1.0 - t).powi(3)
}

/// Exit fade, 1 -> 0 over [`RETRACT_DURATION_SECS`]: the dim and
/// desaturation ride this back to colour together while the (already
/// vanished) text is gone. Exactly 0 at the duration, which is when the
/// app thread flips Retracting -> Idle — a mismatch would pop the region
/// bright or leave it stuck dark.
pub fn retract_fade(t: f32) -> f32 {
    1.0 - ease_out(t / RETRACT_DURATION_SECS)
}

/// Source-region dim on OCR entry, ramping 0 -> DIM_MAX in step with the
/// desaturation (`render::desktop::grayscale_fade` runs on a near-equal
/// duration) so the two read as one darkening. It never goes deeper —
/// see [`DIM_MAX`].
pub fn dim_amount(t: f32) -> f32 {
    DIM_MAX * ease_out(t / LIFT_DURATION_SECS)
}

/// Phase of the scanning sweep in [0, 1) — a plain fract so it never
/// accumulates error and stays finite no matter how long the scan runs.
pub fn scan_phase(t: f32) -> f32 {
    (t.max(0.0) / SCAN_PERIOD_SECS).fract()
}

/// Band centre for a phase in [0, 1): sweeps TOP → BOTTOM of the region,
/// overshooting both edges by [`SWEEP_OVERSHOOT`] so the loop wrap happens
/// entirely off-screen (see the constant's comment).
pub fn sweep_band(phase: f32) -> f32 {
    -SWEEP_OVERSHOOT + phase.clamp(0.0, 1.0) * (1.0 + 2.0 * SWEEP_OVERSHOOT)
}

/// A line's top edge in region-height units (0 = region top, 1 = bottom).
/// The zero-height guard matters: a degenerate region must not poison the
/// reveal times of every line with NaN.
pub fn line_rel_top(line_top: f32, region_top: f32, region_height: f32) -> f32 {
    if region_height <= 0.0 {
        return 0.0;
    }
    ((line_top - region_top) / region_height).clamp(0.0, 1.0)
}

/// Seconds into the reveal pass at which the band centre crosses
/// `rel_top` — the exact inverse of [`sweep_band`] over one
/// [`SCAN_PERIOD_SECS`] pass. This is what keys each line's appearance to
/// the wave instead of to its index: a bubble starts rising the moment the
/// descending band passes its top edge.
pub fn reveal_start_secs(rel_top: f32) -> f32 {
    SCAN_PERIOD_SECS * (rel_top.clamp(0.0, 1.0) + SWEEP_OVERSHOOT) / (1.0 + 2.0 * SWEEP_OVERSHOOT)
}

/// Reveal progress of a line whose top edge sits at `rel_top`: 0 until the
/// band centre reaches it, then eased to 1 over [`LIFT_DURATION_SECS`].
pub fn reveal_progress(t: f32, rel_top: f32) -> f32 {
    ease_out((t - reveal_start_secs(rel_top)) / LIFT_DURATION_SECS)
}

/// Wall time of one complete reveal pass: the sweep instance is emitted
/// while `t` is below this and every line has started rising by then
/// (`reveal_start_secs(1.0) < SCAN_PERIOD_SECS` — pinned in the tests).
pub fn reveal_pass_secs() -> f32 {
    SCAN_PERIOD_SECS
}

/// When the app thread may leave Scanning, given that the recognition
/// result arrived `t_ready` seconds into the phase.
///
/// `align_to_pass` is set for a successful outcome: the transition is held
/// until the moment the looping sweep wraps — the one instant the band is
/// entirely off-screen at BOTH ends — so the Lifted phase's fresh anchor
/// starts its reveal pass with no visible jump in the band. Worst case
/// this costs one extra `SCAN_PERIOD_SECS` of theatre before the reveal;
/// deliberate — a band teleporting mid-region is exactly the pop the
/// wrap-overshoot exists to kill. Failures skip the alignment (nothing is
/// going to be revealed) and keep only the MIN_SCAN floor that stops a
/// warm 6-35 ms recognition from flickering the sweep for a single frame.
pub fn scan_release_secs(t_ready: f32, align_to_pass: bool) -> f32 {
    let floor = t_ready.max(MIN_SCAN_SECS);
    if !align_to_pass {
        return floor;
    }
    SCAN_PERIOD_SECS * (floor / SCAN_PERIOD_SECS).ceil()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The clamp is what protects render workers that wake before a line's
    /// reveal time (negative argument) or long after it finished.
    #[test]
    fn ease_out_endpoints_and_clamping() {
        assert_eq!(ease_out(0.0), 0.0);
        assert_eq!(ease_out(1.0), 1.0);
        assert_eq!(ease_out(-1.0), 0.0);
        assert_eq!(ease_out(5.0), 1.0);
    }

    /// A non-monotone ease would make lines jitter backwards mid-rise.
    #[test]
    fn ease_out_is_monotone() {
        let mut prev = ease_out(0.0);
        for step in 1..=100 {
            let v = ease_out(step as f32 / 100.0);
            assert!(v >= prev, "ease_out regressed at step {step}");
            prev = v;
        }
    }

    /// The exit fade starts fully applied, ends at exactly zero at the
    /// duration the app thread waits before flipping to Idle, and never
    /// climbs back up — the region must settle to colour, once.
    #[test]
    fn retract_fade_reverses_to_zero_monotonically() {
        assert_eq!(retract_fade(0.0), 1.0);
        assert_eq!(retract_fade(RETRACT_DURATION_SECS), 0.0);
        assert_eq!(retract_fade(99.0), 0.0);
        let mut prev = retract_fade(0.0);
        for step in 1..=50 {
            let v = retract_fade(step as f32 * RETRACT_DURATION_SECS / 50.0);
            assert!(v <= prev, "fade climbed at step {step}");
            prev = v;
        }
    }

    /// fract-based phase: periodic, bounded, and still finite after long
    /// scans (a huge t through a naive accumulator would lose precision).
    #[test]
    fn scan_phase_periodic_and_finite() {
        let a = scan_phase(0.3);
        let b = scan_phase(0.3 + SCAN_PERIOD_SECS);
        assert!((a - b).abs() < 1e-4);
        for t in [0.0, 0.5, 123.4, 10_000.0] {
            let p = scan_phase(t);
            assert!(p.is_finite() && (0.0..1.0).contains(&p), "t={t} -> {p}");
        }
    }

    /// Dim tracks the reveal ramp and tops out at DIM_MAX.
    #[test]
    fn dim_ramps_to_max() {
        assert_eq!(dim_amount(0.0), 0.0);
        assert_eq!(dim_amount(LIFT_DURATION_SECS), DIM_MAX);
        assert_eq!(dim_amount(99.0), DIM_MAX);
    }

    /// Direction pin: the band centre must INCREASE with phase (v grows
    /// downward in quad space, so increasing = top → bottom), and must sit
    /// far enough off-screen at both ends of the pass that the wrap is
    /// invisible.
    #[test]
    fn sweep_band_travels_top_to_bottom_and_wraps_off_screen() {
        let mut prev = sweep_band(0.0);
        for step in 1..=100 {
            let v = sweep_band(step as f32 / 100.0);
            assert!(v > prev, "band reversed at step {step}");
            prev = v;
        }
        // Off-screen by >= 3σ at both endpoints: gaussian falloff there is
        // < e^{-4.5} ≈ 1.1% of an already-0.3-alpha band — sub-perceptual.
        assert!(sweep_band(0.0) <= -3.0 * SWEEP_SIGMA);
        assert!(sweep_band(1.0) >= 1.0 + 3.0 * SWEEP_SIGMA);
    }

    /// Seamlessness at the wrap, measured the way the fragment shader sees
    /// it: the band's intensity anywhere inside the region must be nothing
    /// on the last frame of a pass AND on the first frame of the next.
    #[test]
    fn sweep_band_is_invisible_at_the_wrap() {
        for v in [0.0f32, 0.5, 1.0] {
            for band in [sweep_band(0.0), sweep_band(1.0)] {
                let dv = v - band;
                let fall = (-(dv * dv) / (2.0 * SWEEP_SIGMA * SWEEP_SIGMA)).exp();
                assert!(fall < 0.012, "band {band} still visible at v={v}: {fall}");
            }
        }
    }

    /// The reveal is KEYED to the wave: at a line's reveal start the band
    /// centre is exactly at its top edge — the inverse relationship the
    /// whole effect rests on.
    #[test]
    fn reveal_start_matches_band_position() {
        for rel in [0.0f32, 0.25, 0.5, 0.99, 1.0] {
            let t = reveal_start_secs(rel);
            let band = sweep_band(scan_phase(t));
            assert!((band - rel).abs() < 1e-3, "rel={rel} band={band}");
            // Zero before, rising after: the appearance is gated on the
            // band actually having passed.
            assert_eq!(reveal_progress(t, rel), 0.0);
            assert!(reveal_progress(t + 0.01, rel) > 0.0);
        }
    }

    /// Ordering falls out of geometry now, not indices: a line physically
    /// above another must never start after it.
    #[test]
    fn reveal_is_ordered_by_vertical_position() {
        for step in 0..=100 {
            let t = step as f32 * 0.02;
            let mut prev = f32::INFINITY;
            for rel in [0.0f32, 0.3, 0.6, 1.0] {
                let e = reveal_progress(t, rel);
                assert!(e <= prev + 1e-6, "reveal inverted at t={t} rel={rel}");
                prev = e;
            }
        }
    }

    /// The bottom-most line starts before the pass ends — otherwise the
    /// sweep instance (emitted only while t < reveal_pass_secs) would
    /// disappear before finishing its job.
    #[test]
    fn every_line_starts_within_the_pass() {
        assert!(reveal_start_secs(1.0) < reveal_pass_secs());
        assert!(reveal_start_secs(0.0) > 0.0);
    }

    /// Degenerate/edge geometry must clamp, not NaN: a zero-height region
    /// or a rect outside the region still yields a sane reveal time.
    #[test]
    fn line_rel_top_clamps_and_survives_degenerate_regions() {
        assert_eq!(line_rel_top(50.0, 0.0, 100.0), 0.5);
        assert_eq!(line_rel_top(-10.0, 0.0, 100.0), 0.0);
        assert_eq!(line_rel_top(500.0, 0.0, 100.0), 1.0);
        assert_eq!(line_rel_top(50.0, 0.0, 0.0), 0.0);
        // Negative-origin virtual desktops — the historical failure mode.
        assert_eq!(line_rel_top(-1900.0, -1920.0, 40.0), 0.5);
    }

    /// Success release is wrap-aligned (a whole multiple of the period);
    /// failure release keeps only the flicker floor.
    #[test]
    fn scan_release_aligns_success_to_the_pass_wrap() {
        // Warm result: held to the end of the first full pass.
        let r = scan_release_secs(0.03, true);
        assert_eq!(r, SCAN_PERIOD_SECS);
        // Slow result mid-second-pass: held to the end of that pass.
        let r = scan_release_secs(SCAN_PERIOD_SECS + 0.05, true);
        assert!((r - 2.0 * SCAN_PERIOD_SECS).abs() < 1e-4);
        // Wrap-aligned releases are always whole passes.
        for t in [0.0f32, 0.9, 1.3, 7.77] {
            let r = scan_release_secs(t, true);
            let passes = r / SCAN_PERIOD_SECS;
            assert!((passes - passes.round()).abs() < 1e-4, "t={t} -> {r}");
            assert!(r >= t);
        }
        // Failures: just the anti-flicker floor.
        assert_eq!(scan_release_secs(0.01, false), MIN_SCAN_SECS);
        assert_eq!(scan_release_secs(0.7, false), 0.7);
    }
}
