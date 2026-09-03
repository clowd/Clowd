use crate::gpu::desktop::{CursorTextures, WindowUniforms};
use crate::gpu::overlay::OverlayUniforms;
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
    /// The overlay passes' shared uniform buffer + per-shader bind
    /// groups (crosshair.wgsl / selection.wgsl — one buffer, two
    /// layouts). Written every frame alongside `ubo`.
    pub overlay_ubo: gxi::Buffer,
    /// The crosshair pass's peek-replication uniforms (see
    /// `gpu::overlay::CrosshairPeekUniforms`): filled by the render loop
    /// whenever a peek quad draws, zeroed otherwise.
    pub crosshair_peek_ubo: gxi::Buffer,
    /// Fallback crosshair bind group — peek textures are 1×1
    /// placeholders. The render loop builds a peek-aware replacement in
    /// the frames a peek quad actually draws.
    pub crosshair_bind_group: gxi::BindGroup,
    pub selection_bind_group: gxi::BindGroup,
    pub overlay_uniforms: OverlayUniforms,
    /// Seeded once from `CycleParams`; never updated per frame.
    pub accent_color: [f32; 4],
    /// The snapped dash period the selection border is drawing with,
    /// held across the whole of a drag (see [`dash_period_for_frame`]).
    /// `None` until the first selection settles.
    pub held_dash_period: Option<f32>,
    /// This monitor's DPI scale factor (1.0 = 100 %).
    pub dpi_scale: f32,
}

/// Which overlay passes [`SnapshotState::update_uniforms`] decided are
/// on screen this frame. A `false` means the pass's draw call is
/// skipped entirely — the feature costs no GPU time at all.
#[derive(Clone, Copy, Debug, Default)]
pub(crate) struct OverlayVisibility {
    pub crosshair: bool,
    pub selection: bool,
}

pub(crate) struct FrameState {
    pub monitor_bounds: ScreenRect,
    pub mouse_pos: ScreenPointF,
    pub zoom: f32,
    pub selection: Option<ScreenRect>,
    /// Corner radius of `selection` in virtual-desktop px (0 = square).
    pub selection_radius: f32,
    /// The selection is mid-drag (button down): its rect changes every
    /// frame, so the dash period holds instead of re-snapping to it.
    pub selection_dragging: bool,
    pub captured: bool,
    pub overlays_visible: bool,
    pub cursor_overlay_visible: bool,
    /// Mirrors [`crate::ui::shared::UiSharedState::scroll_pick_mode`].
    /// Suppresses the resize handles (see `desktop.wgsl`) and the frozen
    /// cursor composited from the snapshot.
    pub scroll_pick_mode: bool,
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
    /// Write this frame's uniforms for the desktop pass and the two
    /// overlay passes, and decide which overlay passes draw at all.
    pub fn update_uniforms(
        &mut self,
        queue: &gxi::Queue,
        frame: &FrameState,
        cursor_textures: Option<&CursorTextures>,
    ) -> OverlayVisibility {
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
            ocr_dim,
            ocr_gray,
            ocr_active,
            elapsed,
            surface_size,
        } = *frame;

        // OCR uniforms track the state machine unconditionally, not only
        // while overlays draw — the dim/desaturation are animations on
        // the phase's shared anchor clock.
        self.uniforms.ocr_params = [ocr_dim.clamp(0.0, 1.0), 0.0, ocr_gray.clamp(0.0, 1.0), 0.0];

        // While overlays are hidden the desktop pass shows the plain
        // (zoomed) desktop — no fade — and the selection/crosshair
        // passes are skipped outright.
        let fade = if overlays_visible { grayscale_fade(elapsed) } else { 0.0 };
        self.uniforms.params[0] = fade;

        let local = screen_to_window(monitor_bounds, mouse_pos);
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

        // A selection that has gone away takes its held period with it, so
        // the next one starts from the nominal pattern rather than
        // inheriting a snap made for a rect that no longer exists.
        if selection.is_none() {
            self.held_dash_period = None;
        }

        // Handle visibility is a CPU decision: shown only after capture,
        // and never while a scroll point is being picked (the picker owns
        // the next click, so nothing that looks draggable may be on
        // screen) or while OCR lines are lifted (they must not draw over
        // the raised text). Decided here rather than at the uniform write
        // below because the dash period depends on it — a handle over the
        // corner hides the pattern's seam.
        let handles = captured && !scroll_pick_mode && !ocr_active;

        // Selection rect in window-local physical pixels — through the
        // zoom mapping, so it stays congruent with the UV path above.
        // `None` while overlays are hidden: the desktop shows unfaded
        // and no border draws.
        let sel_local = if overlays_visible {
            selection.map(|sel| {
                let cx = mouse_pos.x;
                let cy = mouse_pos.y;
                let sel_f = sel.to_f32();
                let to_local = |vd_x: f32, vd_y: f32| -> (f32, f32) { ((vd_x - cx) * zoom + local.x, (vd_y - cy) * zoom + local.y) };
                let (l, t) = to_local(sel_f.left(), sel_f.top());
                let (r, b) = to_local(sel_f.right(), sel_f.bottom());
                // The radius is a length in the same space as the rect, so
                // it scales with the magnifier like the rect's edges do.
                let radius_local = selection_radius.max(0.0) * zoom.max(1.0);
                // Dash period: snapped to the border's perimeter (same DPI
                // step rule the shader uses for the stroke) so the pattern
                // wraps without a cut dash — but only when that seam is on
                // screen to see, and never mid-drag (see
                // [`dash_period_for_frame`] and [`seam_visible`]).
                let dpi_step = self.dpi_scale.max(1.0).floor();
                let nominal = NOMINAL_DASH_PERIOD * dpi_step;
                let period = dash_period_for_frame(
                    &mut self.held_dash_period,
                    !selection_dragging && seam_visible(handles, radius_local),
                    nominal,
                    border_perimeter(r - l, b - t, radius_local),
                );
                ([l, t, r, b], radius_local, period)
            })
        } else {
            None
        };
        self.uniforms.selection_rect = sel_local
            .map(|(rect, _, _)| rect)
            .unwrap_or([0.0, 0.0, -1.0, -1.0]);

        // The picker draws its own reticle at the live cursor. The
        // snapshot's frozen cursor sits wherever the pointer happened to
        // be when the screenshot was taken, so it reads as a second,
        // stuck pointer right where the user is aiming — hide it for the
        // duration whatever the M toggle says. Display only: the frozen
        // cursor is not part of what the scroll driver captures, and the
        // user's setting is untouched when they back out.
        let show_frozen_cursor = cursor_overlay_visible && !scroll_pick_mode;
        self.set_cursor_uniforms(cursor_textures, show_frozen_cursor, monitor_bounds, mouse_pos, zoom);
        queue.write_buffer(&self.ubo, 0, bytemuck::bytes_of(&self.uniforms));

        // ── Overlay passes (crosshair + selection border/handles) ──
        let o = &mut self.overlay_uniforms;
        o.viewport = [surface_size.0 as f32, surface_size.1 as f32, self.dpi_scale, fade];
        o.cursor = [local.x, local.y, 0.0, 0.0];
        o.accent_color = self.accent_color;
        o.uv_offset_scale = self.uniforms.uv_offset_scale;
        if let Some((rect, radius, period)) = sel_local {
            o.selection_rect = rect;
            o.sel_params = [elapsed, period, radius, if handles { 1.0 } else { 0.0 }];
        } else {
            o.sel_params = [elapsed, 0.0, 0.0, 0.0];
        }
        queue.write_buffer(&self.overlay_ubo, 0, bytemuck::bytes_of(&self.overlay_uniforms));

        OverlayVisibility {
            // Once the user has finalized a selection the OS cursor takes
            // over and the rendered crosshair is suppressed entirely.
            crosshair: overlays_visible && !captured,
            selection: sel_local.is_some(),
        }
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
/// anything bigger than a button. Only re-evaluated when the selection is
/// at rest; see [`dash_period_for_frame`].
pub(crate) fn dash_period(nominal: f32, perimeter: f32) -> f32 {
    // Degenerate or NaN inputs: hand back the nominal period untouched
    // rather than a 0 / inf / NaN the shader would have to guard against.
    if nominal <= 0.0 || perimeter <= 0.0 || nominal.is_nan() || perimeter.is_nan() {
        return nominal;
    }
    let n = (perimeter / nominal).round().max(1.0);
    perimeter / n
}

/// Whether the seam the snapped period exists to hide is on screen at
/// all, for a border with `radius` and the handles up or down.
///
/// The dash walk wraps at the top-left corner — exactly on it for a
/// square border, `radius` px along the left edge for a rounded one (see
/// selection.wgsl). The corner handle is a disc centred on that same
/// corner, several times the stroke's own half-thickness across, so
/// while the handles are up a square border's seam is underneath one and
/// no snapping can make any visible difference. A rounded border's seam
/// sits a radius away, out past the disc, and still needs it.
///
/// This is what spares every dragged and handle-resized selection a
/// re-snap: those are always square (only a picked window carries a
/// radius) and always end with the handles up.
pub(crate) fn seam_visible(handles: bool, radius: f32) -> bool {
    !handles || radius > 0.0
}

/// This frame's dash period, given the one `held` from previous frames.
///
/// A snapped period tracks the perimeter, so re-deriving it every frame
/// of a drag re-phases every dash along the walk and reads as the far end
/// jittering. Holding it instead — nominal only when the drag is drawing
/// the first selection, which has no earlier period to keep — leaves the
/// pattern still for the whole gesture, and it re-snaps once the drag
/// ends, if it re-snaps at all: `resnap` is false both mid-drag and
/// whenever the seam is hidden ([`seam_visible`]). A drag that moved the
/// selection without resizing it lands on the same period it started
/// with, so even a release that does snap costs no visible step.
pub(crate) fn dash_period_for_frame(held: &mut Option<f32>, resnap: bool, nominal: f32, perimeter: f32) -> f32 {
    if resnap {
        let period = dash_period(nominal, perimeter);
        *held = Some(period);
        period
    } else {
        *held.get_or_insert(nominal)
    }
}

// ── OCR dim + desaturation ──────────────────────────────────────────

/// Per-frame OCR inputs for [`FrameState`].
pub(crate) struct OcrOverlay {
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
    let (dim, gray, active) = match ocr {
        OcrState::Idle => (0.0, 0.0, false),
        OcrState::Scanning {
            anchor,
            ..
        } => {
            let t = anchor.elapsed().as_secs_f32();
            (scanning_dim(t), scanning_gray(t), true)
        }
        OcrState::Lifted {
            ..
        } => {
            // Both HOLD at their scanning ceilings — no new ramp on this
            // phase's fresh anchor. The gray finished long before any
            // outcome can land (the release is wrap-aligned, and even the
            // failure floor MIN_SCAN_SECS >= FADE_DURATION_SECS), and the
            // dim deliberately does NOT deepen when the text renders: an
            // earlier build darkened again here and the region visibly
            // dimmed twice (owner call — one darkening, on entry, only).
            (anim::DIM_MAX, 1.0, true)
        }
        OcrState::Retracting {
            anchor,
        } => {
            let t = anchor.elapsed().as_secs_f32();
            (retracting_dim(t), retracting_gray(t), true)
        }
    };
    OcrOverlay {
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

    /// A drag draws with one period from first frame to last, and the
    /// release re-snaps only when the drag actually changed the size.
    #[test]
    fn dash_period_holds_for_the_length_of_a_drag() {
        let mut held = None;
        // First selection ever: nothing to hold, so the drag is nominal.
        for perimeter in [40.0f32, 180.0, 640.0] {
            assert_eq!(dash_period_for_frame(&mut held, false, 32.0, perimeter), 32.0);
        }
        // A release whose seam shows snaps to the final rect and
        // remembers that period.
        let settled = dash_period_for_frame(&mut held, true, 32.0, 640.0);
        assert_eq!(settled, dash_period(32.0, 640.0));
        assert_eq!(held, Some(settled));

        // A resize drag holds the settled period the whole way down...
        for perimeter in [600.0f32, 410.0, 333.0] {
            assert_eq!(dash_period_for_frame(&mut held, false, 32.0, perimeter), settled);
        }
        // ...and steps exactly once, on release.
        let resized = dash_period_for_frame(&mut held, true, 32.0, 333.0);
        assert_eq!(resized, dash_period(32.0, 333.0));

        // A move drag ends on the perimeter it began with, so the release
        // lands back on the same period: no step at all.
        for _ in 0..3 {
            assert_eq!(dash_period_for_frame(&mut held, false, 32.0, 333.0), resized);
        }
        assert_eq!(dash_period_for_frame(&mut held, true, 32.0, 333.0), resized);
    }

    /// The seam only shows where no handle covers it: under the handles a
    /// square border hides it, a rounded one holds it a radius away.
    #[test]
    fn seam_shows_unless_a_handle_covers_the_corner() {
        assert!(seam_visible(false, 0.0), "no handles: square seam shows");
        assert!(seam_visible(false, 8.0), "no handles: rounded seam shows");
        assert!(seam_visible(true, 8.0), "rounded seam clears the handle");
        assert!(!seam_visible(true, 0.0), "square corner sits under a handle");
    }

    /// Dragged and handle-resized selections are square with the handles
    /// up, so nothing in the gesture re-snaps: the period the border came
    /// in with is the period it leaves with, whatever the size did.
    #[test]
    fn square_selection_under_handles_never_resnaps() {
        // A window pick settles on a snapped period while the handles are
        // still down.
        let mut held = None;
        let picked = dash_period_for_frame(&mut held, seam_visible(false, 0.0), 32.0, 640.0);
        assert_eq!(picked, dash_period(32.0, 640.0));

        // Handles up, then a resize drag and its release: not one frame
        // of it re-derives the period.
        for (dragging, perimeter) in [(false, 640.0f32), (true, 500.0), (true, 337.0), (false, 337.0)] {
            let resnap = !dragging && seam_visible(true, 0.0);
            assert_eq!(dash_period_for_frame(&mut held, resnap, 32.0, perimeter), picked);
        }
        assert_eq!(held, Some(picked));
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
