use crate::gpu::desktop::{CursorTextures, WindowUniforms};
use crate::gxi;
use crate::interaction::OcrState;
use crate::ocr::anim;
use clowd_rust_core::geometry::{screen_to_window, RectExt, ScreenPointF, ScreenRect};

/// Duration of the color to grayscale fade after the window first becomes
/// visible — and of the OCR selection's own color→grayscale ramp, which
/// deliberately reuses the same curve (see [`grayscale_fade`]).
const FADE_DURATION_SECS: f32 = 0.3;

/// The color→grayscale easing: quartic ease-out over
/// [`FADE_DURATION_SECS`]. ONE function on purpose — the overlay's opening
/// fade (outside the selection) and the OCR mode's selection desaturation
/// (inside it) must feel like the same effect, so they share the curve
/// rather than each hand-rolling a nearly-identical one.
fn grayscale_fade(elapsed: f32) -> f32 {
    let t = (elapsed / FADE_DURATION_SECS).clamp(0.0, 1.0);
    let inv = 1.0 - t;
    1.0 - inv * inv * inv * inv
}

pub(crate) struct SnapshotState {
    pub ubo: gxi::Buffer,
    pub bind_group: gxi::BindGroup,
    pub uniforms: WindowUniforms,
    pub base_uv_offset_scale: [f32; 4],
}

pub(crate) struct FrameState {
    pub monitor_bounds: ScreenRect,
    pub mouse_pos: ScreenPointF,
    pub zoom: f32,
    pub selection: Option<ScreenRect>,
    /// Corner radius of `selection` in virtual-desktop px (0 = square).
    pub selection_radius: f32,
    /// The selection is mid-drag (button down): its rect changes every
    /// frame, so the dash period must not re-snap to it.
    pub selection_dragging: bool,
    pub captured: bool,
    pub overlays_visible: bool,
    pub cursor_overlay_visible: bool,
    /// Mirrors [`crate::ui::shared::UiSharedState::scroll_pick_mode`].
    /// Suppresses the resize handles (see `desktop.wgsl`) and the frozen
    /// cursor composited from the snapshot.
    pub scroll_pick_mode: bool,
    /// OCR source region in virtual-desktop pixels — `None` while OCR mode
    /// is idle. Derived per frame from the mirrored [`OcrState`] via
    /// [`ocr_overlay`].
    pub ocr_rect: Option<ScreenRect>,
    /// Source-region dim in [0, 1], already evaluated on the OCR phase's
    /// shared anchor clock (never this worker's own) so every monitor dims
    /// in lockstep with the lift animation.
    pub ocr_dim: f32,
    /// Selection-interior desaturation in [0, 1], same shared clock as the
    /// dim. Ramps on OCR entry so the whole screen reads monochrome while
    /// the sweep runs, and reverses with the retract.
    pub ocr_gray: f32,
    /// Mirrors `OcrState::active()`. Suppresses the resize handles (see
    /// `desktop.wgsl`) — they must not draw over lifted text.
    pub ocr_active: bool,
    pub elapsed: f32,
    pub surface_size: (u32, u32),
}

impl SnapshotState {
    pub fn update_uniforms(&mut self, queue: &gxi::Queue, frame: &FrameState, cursor_textures: Option<&CursorTextures>) {
        let FrameState {
            monitor_bounds,
            mouse_pos,
            zoom,
            selection,
            selection_radius,
            selection_dragging,
            captured,
            overlays_visible,
            cursor_overlay_visible,
            scroll_pick_mode,
            ocr_rect,
            ocr_dim,
            ocr_gray,
            ocr_active,
            elapsed,
            surface_size,
        } = *frame;

        self.uniforms.selection_params[3] = if scroll_pick_mode { 1.0 } else { 0.0 };

        // OCR uniforms are written before the overlays_visible early-return
        // (same treatment as scroll_pick above): the mode flags must track
        // the state machine unconditionally, not only while overlays draw.
        self.uniforms.ocr_params = [
            ocr_dim.clamp(0.0, 1.0),
            if ocr_active { 1.0 } else { 0.0 },
            ocr_gray.clamp(0.0, 1.0),
            0.0,
        ];
        self.uniforms.ocr_rect = match ocr_rect {
            Some(r) => {
                // Same window-local transform as selection_rect below —
                // through the zoom mapping, so the two rects stay congruent
                // in every magnifier state.
                let local_cursor = screen_to_window(monitor_bounds, mouse_pos);
                let to_local = |vd_x: f32, vd_y: f32| -> (f32, f32) {
                    (
                        (vd_x - mouse_pos.x) * zoom + local_cursor.x,
                        (vd_y - mouse_pos.y) * zoom + local_cursor.y,
                    )
                };
                let rf = r.to_f32();
                let (l, t) = to_local(rf.left(), rf.top());
                let (rr, b) = to_local(rf.right(), rf.bottom());
                [l, t, rr, b]
            }
            None => [0.0, 0.0, -1.0, -1.0],
        };

        // The picker draws its own reticle at the live cursor. The
        // snapshot's frozen cursor sits wherever the pointer happened to
        // be when the screenshot was taken, so it reads as a second,
        // stuck pointer right where the user is aiming — hide it for the
        // duration whatever the M toggle says. Display only: the frozen
        // cursor is not part of what the scroll driver captures, and the
        // user's setting is untouched when they back out.
        let show_frozen_cursor = cursor_overlay_visible && !scroll_pick_mode;

        if !overlays_visible {
            self.uniforms.params[0] = 0.0;
            let local = screen_to_window(monitor_bounds, mouse_pos);
            self.uniforms.params[1] = -1.0;
            self.uniforms.params[2] = -1.0;
            if zoom <= 1.0 {
                self.uniforms.uv_offset_scale = self.base_uv_offset_scale;
            } else {
                let w = surface_size.0 as f32;
                let h = surface_size.1 as f32;
                let cu = local.x / w;
                let cv = local.y / h;
                let k = 1.0 - 1.0 / zoom;
                let base = self.base_uv_offset_scale;
                self.uniforms.uv_offset_scale = [
                    base[0] + base[2] * cu * k,
                    base[1] + base[3] * cv * k,
                    base[2] / zoom,
                    base[3] / zoom,
                ];
            }
            self.uniforms.selection_rect = [0.0, 0.0, -1.0, -1.0];
            self.uniforms.selection_shape = [0.0; 4];
            self.uniforms.selection_params[0] = elapsed;
            self.uniforms.selection_params[1] = 0.0;
            self.uniforms.selection_params[2] = zoom;
            self.set_cursor_uniforms(cursor_textures, show_frozen_cursor, monitor_bounds, mouse_pos, zoom);
            queue.write_buffer(&self.ubo, 0, bytemuck::bytes_of(&self.uniforms));
            return;
        }

        self.uniforms.params[0] = grayscale_fade(elapsed);

        let local = screen_to_window(monitor_bounds, mouse_pos);
        self.uniforms.params[1] = local.x;
        self.uniforms.params[2] = local.y;

        if zoom <= 1.0 {
            self.uniforms.uv_offset_scale = self.base_uv_offset_scale;
        } else {
            let w = surface_size.0 as f32;
            let h = surface_size.1 as f32;
            let cu = local.x / w;
            let cv = local.y / h;
            let k = 1.0 - 1.0 / zoom;
            let base = self.base_uv_offset_scale;
            self.uniforms.uv_offset_scale = [
                base[0] + base[2] * cu * k,
                base[1] + base[3] * cv * k,
                base[2] / zoom,
                base[3] / zoom,
            ];
        }

        if let Some(sel) = selection {
            let cx = mouse_pos.x;
            let cy = mouse_pos.y;
            let local_cursor = screen_to_window(monitor_bounds, mouse_pos);
            let sel_f = sel.to_f32();
            let to_local =
                |vd_x: f32, vd_y: f32| -> (f32, f32) { ((vd_x - cx) * zoom + local_cursor.x, (vd_y - cy) * zoom + local_cursor.y) };
            let (l, t) = to_local(sel_f.left(), sel_f.top());
            let (r, b) = to_local(sel_f.right(), sel_f.bottom());
            self.uniforms.selection_rect = [l, t, r, b];
            // The radius is a length in the same space as the rect, so it
            // scales with the magnifier like the rect's edges do.
            let radius_local = selection_radius.max(0.0) * zoom.max(1.0);
            // Dash period: snapped to the border's perimeter (same DPI
            // step rule the shader uses for the stroke) so the pattern
            // wraps without a cut dash — square or rounded — except
            // while a drag is in progress: the rect changes every frame
            // then, and a period that tracked it would re-phase every
            // dash along the walk under the cursor. Nominal mid-drag;
            // re-snapped once on release.
            let dpi_step = self.uniforms.params[3].max(1.0).floor();
            let nominal = NOMINAL_DASH_PERIOD * dpi_step;
            let period = if selection_dragging {
                nominal
            } else {
                dash_period(nominal, border_perimeter(r - l, b - t, radius_local))
            };
            self.uniforms.selection_shape = [radius_local, period, 0.0, 0.0];
        } else {
            self.uniforms.selection_rect = [0.0, 0.0, -1.0, -1.0];
            self.uniforms.selection_shape = [0.0; 4];
        }

        self.uniforms.selection_params[0] = elapsed;
        self.uniforms.selection_params[1] = if captured { 1.0 } else { 0.0 };
        self.uniforms.selection_params[2] = zoom;
        self.set_cursor_uniforms(cursor_textures, show_frozen_cursor, monitor_bounds, mouse_pos, zoom);

        queue.write_buffer(&self.ubo, 0, bytemuck::bytes_of(&self.uniforms));
    }

    fn set_cursor_uniforms(
        &mut self,
        cursor_textures: Option<&CursorTextures>,
        visible: bool,
        monitor_bounds: ScreenRect,
        mouse_pos: ScreenPointF,
        zoom: f32,
    ) {
        let ct = match cursor_textures {
            Some(ct) if visible && ct.visible => ct,
            _ => {
                self.uniforms.cursor_rect = [0.0, 0.0, -1.0, -1.0];
                self.uniforms.cursor_params = [0.0, 0.0, 0.0, 0.0];
                return;
            }
        };

        let vd_left = (ct.position.x - ct.hotspot_x) as f32;
        let vd_top = (ct.position.y - ct.hotspot_y) as f32;
        let vd_right = vd_left + ct.width as f32;
        let vd_bottom = vd_top + ct.height as f32;

        let cx = mouse_pos.x;
        let cy = mouse_pos.y;
        let local_cursor = screen_to_window(monitor_bounds, mouse_pos);

        let to_local = |vd_x: f32, vd_y: f32| -> (f32, f32) { ((vd_x - cx) * zoom + local_cursor.x, (vd_y - cy) * zoom + local_cursor.y) };

        let (l, t) = to_local(vd_left, vd_top);
        let (r, b) = to_local(vd_right, vd_bottom);
        self.uniforms.cursor_rect = [l, t, r, b];
        self.uniforms.cursor_params = [ct.cursor_type as f32, 0.0, 0.0, 0.0];
    }
}

// ── Selection border dashes ─────────────────────────────────────────

/// Marching-ants dash period at 100 % DPI, physical px: 16 on + 16 off.
/// Matches the C++ D2D stroke style of {8, 8} × 2 DIPs
/// (DxScreenCapture.cpp:638-645) and the shader's own constant.
pub(crate) const NOMINAL_DASH_PERIOD: f32 = 32.0;

/// Length of the path the dashes walk: the selection border's perimeter
/// in px for a `w`×`h` rect with corner radius `r` (0 = square). For the
/// square border this is the integer path's `2 * top_len + 2 * side_len`
/// (the stroke's extra `half` on the top/bottom runs cancels the `half`
/// the sides give up); the rounded path swaps four corners for one
/// circle's worth of arc. `r` is clamped like the shader clamps it.
pub(crate) fn border_perimeter(w: f32, h: f32, r: f32) -> f32 {
    let w = w.max(0.0);
    let h = h.max(0.0);
    let r = r.clamp(0.0, w.min(h) * 0.5);
    2.0 * (w + h) - 8.0 * r + 2.0 * std::f32::consts::PI * r
}

/// The dash period that divides `perimeter` a whole number of times,
/// nearest to `nominal`. Without it the pattern wraps at a fractional
/// period and the walk's start shows a seam where one dash is cut short;
/// with it the ants march as one continuous loop, at the cost of dashes
/// up to half a period longer or shorter than nominal — a few percent on
/// anything bigger than a button. NOT applied while the selection is
/// being dragged: the perimeter then changes every frame and the snapped
/// period with it, which re-phases every dash along the walk and reads
/// as the far end jittering.
pub(crate) fn dash_period(nominal: f32, perimeter: f32) -> f32 {
    // Degenerate or NaN inputs: hand back the nominal period untouched
    // rather than a 0 / inf / NaN the shader would have to guard against.
    if nominal <= 0.0 || perimeter <= 0.0 || nominal.is_nan() || perimeter.is_nan() {
        return nominal;
    }
    let n = (perimeter / nominal).round().max(1.0);
    perimeter / n
}

// ── OCR dim + desaturation ──────────────────────────────────────────

/// Per-frame OCR inputs for [`FrameState`].
pub(crate) struct OcrOverlay {
    pub rect: Option<ScreenRect>,
    pub dim: f32,
    pub gray: f32,
    pub active: bool,
}

/// Evaluate the OCR overlay's dim + desaturation on the phase's shared
/// `anchor` clock — the impure shell around the pure curves below, which
/// carry the tests. The clamps inside `ocr::anim` make a worker that wakes
/// late (or a suspend/resume gap) produce sane, settled values rather than
/// out-of-range ones.
pub(crate) fn ocr_overlay(ocr: &OcrState) -> OcrOverlay {
    let (rect, dim, gray, active) = match ocr {
        OcrState::Idle => (None, 0.0, 0.0, false),
        OcrState::Scanning {
            anchor,
            region,
            ..
        } => {
            let t = anchor.elapsed().as_secs_f32();
            (Some(*region), scanning_dim(t), scanning_gray(t), true)
        }
        OcrState::Lifted {
            region,
            ..
        } => {
            // Both HOLD at their scanning ceilings — no new ramp on this
            // phase's fresh anchor. The gray finished long before any
            // outcome can land (the release is wrap-aligned, and even the
            // failure floor MIN_SCAN_SECS >= FADE_DURATION_SECS), and the
            // dim deliberately does NOT deepen when the text renders: an
            // earlier build darkened again here and the region visibly
            // dimmed twice (owner call — one darkening, on entry, only).
            (Some(*region), anim::DIM_MAX, 1.0, true)
        }
        OcrState::Retracting {
            anchor,
            region,
        } => {
            let t = anchor.elapsed().as_secs_f32();
            (Some(*region), retracting_dim(t), retracting_gray(t), true)
        }
    };
    OcrOverlay {
        rect,
        dim,
        gray,
        active,
    }
}

fn scanning_dim(t: f32) -> f32 {
    anim::dim_amount(t)
}

/// The selection's color→grayscale ramp on OCR entry: the same curve and
/// duration as the overlay's opening fade outside the selection, so the
/// interior joining the monochrome page reads as one continuous treatment,
/// not a second effect.
fn scanning_gray(t: f32) -> f32 {
    grayscale_fade(t)
}

/// Exit: the text has already vanished (first Retracting frame), so the
/// dim just fades back over the shared exit curve, hitting exactly 0 when
/// the app thread flips Retracting -> Idle.
fn retracting_dim(t: f32) -> f32 {
    anim::DIM_MAX * anim::retract_fade(t)
}

/// The desaturation reverses on the same clock as the dim: both must be
/// exactly 0 at the Retracting -> Idle flip or the region pops.
fn retracting_gray(t: f32) -> f32 {
    anim::retract_fade(t)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The scan dim must ramp from zero (no pop on entry) and settle at
    /// the mode's ONE dim level. Lifted holds that exact level — pinned
    /// here because a deeper "lifted" dim is precisely the darkening-twice
    /// regression the owner flagged.
    #[test]
    fn scanning_dim_ramps_to_the_single_mode_level() {
        assert_eq!(scanning_dim(0.0), 0.0);
        assert!(scanning_dim(0.05) > 0.0);
        assert_eq!(scanning_dim(10.0), anim::DIM_MAX);
    }

    /// The desaturation on OCR entry rides the same curve as the overlay's
    /// opening fade: 0 at press (no pop), saturated at the fade duration,
    /// and pinned there — Lifted assumes gray == 1 on handover.
    #[test]
    fn scanning_gray_ramps_and_saturates_before_any_release() {
        assert_eq!(scanning_gray(0.0), 0.0);
        assert!(scanning_gray(0.05) > 0.0);
        assert_eq!(scanning_gray(FADE_DURATION_SECS), 1.0);
        assert_eq!(scanning_gray(1.0e6), 1.0);
        // The handover invariant Lifted's hard-coded 1.0 rests on: the ramp
        // has ALWAYS finished by the earliest possible Scanning exit.
        // (Const block per clippy: the relation between two consts is
        // checkable at compile time.)
        const {
            assert!(anim::MIN_SCAN_SECS >= FADE_DURATION_SECS);
        }
        assert_eq!(scanning_gray(anim::MIN_SCAN_SECS), 1.0);
    }

    /// Lifted's hard-held DIM_MAX is exactly where the scanning ramp
    /// settles, so the Scanning→Lifted handover (fresh anchor and all)
    /// moves the dim by nothing: no flash back to bright, and no second
    /// darkening as the text renders.
    #[test]
    fn lifted_holds_exactly_where_scanning_settled() {
        // The earliest possible Scanning exit is the wrap-aligned release
        // (>= one SCAN_PERIOD), by which point the ramp has long settled.
        assert_eq!(scanning_dim(anim::SCAN_PERIOD_SECS), anim::DIM_MAX);
        assert_eq!(scanning_gray(anim::SCAN_PERIOD_SECS), 1.0);
    }

    /// Retract must start where Lifted left off (DIM_MAX / full gray, no
    /// pop) and both must be exactly 0 at the instant the app thread flips
    /// the mode to Idle — a mismatch either pops the region bright or
    /// leaves it stuck dark.
    #[test]
    fn retracting_dim_and_gray_reverse_to_zero_together() {
        assert_eq!(retracting_dim(0.0), anim::DIM_MAX);
        assert_eq!(retracting_gray(0.0), 1.0);
        assert_eq!(retracting_dim(anim::RETRACT_DURATION_SECS), 0.0);
        assert_eq!(retracting_gray(anim::RETRACT_DURATION_SECS), 0.0);
        let mid_d = retracting_dim(anim::RETRACT_DURATION_SECS * 0.5);
        let mid_g = retracting_gray(anim::RETRACT_DURATION_SECS * 0.5);
        assert!((0.0..=anim::DIM_MAX).contains(&mid_d));
        assert!((0.0..=1.0).contains(&mid_g));
    }

    /// A snapped period divides the perimeter exactly and never strays more
    /// than half a nominal period from nominal.
    #[test]
    fn dash_period_divides_perimeter_and_stays_near_nominal() {
        for perimeter in [100.0f32, 333.0, 1000.0, 4097.5, 20_000.0] {
            let p = dash_period(32.0, perimeter);
            let n = perimeter / p;
            assert!((n - n.round()).abs() < 1e-3, "perimeter {perimeter}: {n} periods");
            assert!((p - 32.0).abs() <= 16.0 + 1e-3, "perimeter {perimeter}: period {p}");
        }
        // Exactly divisible: untouched.
        assert_eq!(dash_period(32.0, 320.0), 32.0);
        // Tiny selection: one dash round the whole thing, never zero.
        assert_eq!(dash_period(32.0, 10.0), 10.0);
        // Degenerate inputs fall back to nominal rather than NaN/inf.
        assert_eq!(dash_period(32.0, 0.0), 32.0);
        assert_eq!(dash_period(32.0, -5.0), 32.0);
        assert_eq!(dash_period(0.0, 100.0), 0.0);
    }

    /// The square perimeter is 2(w+h); rounding trades 8r of corners for a
    /// full circle of arc, and a radius past half the short side clamps.
    #[test]
    fn border_perimeter_square_and_rounded() {
        assert_eq!(border_perimeter(100.0, 50.0, 0.0), 300.0);
        let rounded = border_perimeter(100.0, 50.0, 10.0);
        assert!((rounded - (300.0 - 80.0 + 2.0 * std::f32::consts::PI * 10.0)).abs() < 1e-3);
        // Radius clamps to 25 (half of 50): a stadium.
        assert_eq!(border_perimeter(100.0, 50.0, 1000.0), border_perimeter(100.0, 50.0, 25.0));
    }

    /// The shared curve's endpoints: byte-exact passthrough at t=0 (the
    /// uncloak invariant) and fully faded at the duration.
    #[test]
    fn grayscale_fade_endpoints() {
        assert_eq!(grayscale_fade(0.0), 0.0);
        assert_eq!(grayscale_fade(FADE_DURATION_SECS), 1.0);
        assert_eq!(grayscale_fade(99.0), 1.0);
        let mut prev = 0.0;
        for step in 1..=30 {
            let v = grayscale_fade(step as f32 * FADE_DURATION_SECS / 30.0);
            assert!(v >= prev, "fade regressed at step {step}");
            prev = v;
        }
    }
}
