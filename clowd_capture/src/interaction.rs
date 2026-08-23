use std::collections::VecDeque;
use std::sync::Arc;
use std::time::{Duration, Instant};

use crate::ocr::OcrOutcome;
use crate::selection::{dpi_at_point, hit_test, DragMode, Hittest};
use crate::settings::TipsMode;
use crate::system::{MonitorInfo, WindowTarget};
use clowd_rust_core::geometry::{ScreenPoint, ScreenPointF, ScreenRect};
use winit::window::CursorIcon;

pub const ZOOM_MIN: f32 = 1.0;
pub const ZOOM_MAX: f32 = 256.0;

const VELOCITY_WINDOW: Duration = Duration::from_secs(5);
const SLOW_SPEED_THRESHOLD: f32 = 15.0;
const FAST_SPEED_THRESHOLD: f32 = 40.0;
const HINT_MIN_DISPLAY: Duration = Duration::from_secs(3);
const MIN_HISTORY: Duration = Duration::from_secs(3);
const MAX_VELOCITY_SAMPLES: usize = 512;

/// How long an OCR notice stays up before it expires, in seconds. The last
/// [`NOTICE_FADE_SECS`] of that window fades it out.
pub const NOTICE_SECS: f32 = 2.5;
pub const NOTICE_FADE_SECS: f32 = 0.5;

/// Which "OCR did not give you anything" message the overlay is showing.
///
/// This exists because the alternative is worse than missing polish: press
/// OCR, get nothing back, and the overlay silently snaps to the captured
/// state — indistinguishable from a dead button. A dialog is not an option
/// (the overlay is topmost and fullscreen, so a dialog would open *behind*
/// it), hence an in-overlay pill.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OcrNoticeKind {
    /// Recognition succeeded but produced zero lines.
    NoText,
    /// No recognizer at all — no language pack, or a platform with no OCR.
    Unavailable,
    /// The engine errored. The detail goes to the log, not to the user:
    /// a WinRT HRESULT string is noise on screen.
    Failed,
}

impl OcrNoticeKind {
    /// Short, plain user-facing text. Deliberately terse — the pill is
    /// small, transient, and sits over the user's own screen content.
    pub fn message(&self) -> &'static str {
        match self {
            OcrNoticeKind::NoText => "No text found",
            OcrNoticeKind::Unavailable => "OCR is not available on this PC",
            OcrNoticeKind::Failed => "OCR failed",
        }
    }
}

/// A transient notice pill shown over the selection.
///
/// `anchor` is an absolute `Instant` for the same reason [`OcrState`]'s is:
/// every render worker free-runs at its own refresh rate, so the fade must
/// be a pure function of wall-clock elapsed time, not of frame counts.
///
/// No extra frame scheduling is needed to animate the fade: the capture
/// cycle already runs `ControlFlow::Poll` and the render workers free-run,
/// so frames keep arriving on their own while the notice is up.
#[derive(Debug, Clone, Copy)]
pub struct OcrNotice {
    pub anchor: Instant,
    pub kind: OcrNoticeKind,
}

impl OcrNotice {
    /// Whether the notice should still be drawn at all.
    pub fn visible(&self) -> bool {
        self.anchor.elapsed().as_secs_f32() < NOTICE_SECS
    }

    /// Opacity multiplier: solid, then a linear ramp to zero across the
    /// final [`NOTICE_FADE_SECS`].
    pub fn alpha(&self) -> f32 {
        notice_alpha(self.anchor.elapsed().as_secs_f32())
    }
}

/// The fade curve, split out as a pure function of elapsed seconds so it is
/// testable without doing arithmetic on `Instant` (subtracting from
/// `Instant::now()` can panic on a freshly-booted machine, and adding to it
/// cannot be observed by `elapsed()`).
fn notice_alpha(elapsed: f32) -> f32 {
    let fade_starts = NOTICE_SECS - NOTICE_FADE_SECS;
    if elapsed <= fade_starts {
        return 1.0;
    }
    // Clamped rather than assumed in-range: a notice left up across a
    // suspend/resume can come back with an arbitrarily large elapsed.
    (1.0 - (elapsed - fade_starts) / NOTICE_FADE_SECS).clamp(0.0, 1.0)
}

/// Where the OCR "lift-and-act" mode is in its lifecycle. One enum rather
/// than a bool plus a result pair, so illegal states — a pending result
/// outside the mode, a mode with no result — are unrepresentable.
///
/// Every variant that animates carries an `anchor`: the animation clock's
/// t=0 for the CURRENT phase, as an absolute `Instant`. That is the whole
/// point of the shape. Each render worker free-runs at its own monitor's
/// refresh rate and would otherwise anchor its clock at BeginCycle delivery
/// time; the resulting skew would pull the per-line stagger apart across
/// monitors. `Instant` is `Copy + Send` off one process-global monotonic
/// source, so every worker derives byte-identical geometry from
/// `anchor.elapsed()`.
///
#[derive(Debug, Clone)]
pub enum OcrState {
    Idle,
    /// Recognition in flight; the scanning sweep plays over `region`.
    /// `req` is a per-cycle monotonic id — a late result whose id no longer
    /// matches is discarded. BACK leaves the same cycle alive, so nothing
    /// coarser than this id can tell a stale result from a current one.
    Scanning {
        anchor: Instant,
        req: u64,
        region: ScreenRect,
    },
    /// Lines recognized; the reveal pass sweeps top→bottom raising them,
    /// and the OCR button set is live.
    /// `req` is the id of the request that produced `outcome` (same
    /// counter Scanning carries): unique within the cycle, so the bubble
    /// renderer keys its shaped-layout cache on it — an `Arc` address
    /// could be reused by a later outcome on a render worker that stalled
    /// through every intermediate state, an id cannot.
    /// `dpi_scale` is the scale of the monitor containing the region's
    /// centre — ONE value for all lift geometry, so a line crossing a
    /// mixed-DPI seam moves by the same physical amount on both halves
    /// instead of tearing at the seam.
    Lifted {
        anchor: Instant,
        req: u64,
        region: ScreenRect,
        dpi_scale: f32,
        outcome: Arc<OcrOutcome>,
    },
    /// BACK/Escape pressed. The text does NOT animate out — every bubble
    /// and crop vanishes on the first frame of this phase (see
    /// `anim::RETRACT_DURATION_SECS`) — so all this phase does is fade the
    /// region's dim/desaturation back to colour, which is why it carries
    /// no outcome: there is nothing left to draw that needs one. The app
    /// thread flips to `Idle` once `anchor.elapsed()` passes the fade
    /// duration.
    Retracting {
        anchor: Instant,
        region: ScreenRect,
    },
}

impl OcrState {
    /// Any non-Idle phase. Used to freeze selection drag/resize and gate
    /// input: the selection must not move out from under lifted lines whose
    /// geometry was computed against it.
    pub fn active(&self) -> bool {
        !matches!(self, OcrState::Idle)
    }

    /// Phases that show the OCR button set: `Lifted` ONLY.
    ///
    /// `Scanning` deliberately shows no panel at all (see [`Self::hides_panel`]
    /// and `ui::shared::active_panel_set`): while the sweep is looping there
    /// is no text yet, so COPY/SEARCH/UPLOAD would be lit but dead — the
    /// strip appears exactly when it becomes usable. `Retracting` shows the
    /// Normal set again — BACK restores the familiar buttons instantly
    /// while the retract animation plays out purely cosmetically.
    pub fn shows_ocr_panel(&self) -> bool {
        matches!(self, OcrState::Lifted { .. })
    }

    /// Phases where no panel may be on screen at all. Split from
    /// [`Self::shows_ocr_panel`] so `active_panel_set` reads as policy
    /// rather than pattern-matching: during the scan the only live inputs
    /// are Escape and waiting, and nothing clickable may suggest otherwise.
    pub fn hides_panel(&self) -> bool {
        matches!(self, OcrState::Scanning { .. })
    }
}

pub(crate) struct MouseVelocityTracker {
    samples: VecDeque<(Instant, ScreenPointF)>,
    hint_shown_at: Option<Instant>,
}

impl MouseVelocityTracker {
    pub fn new() -> Self {
        Self {
            samples: VecDeque::with_capacity(MAX_VELOCITY_SAMPLES),
            hint_shown_at: None,
        }
    }

    pub fn record(&mut self, now: Instant, pos: ScreenPointF) {
        let cutoff = now - VELOCITY_WINDOW;
        while self
            .samples
            .front()
            .is_some_and(|(t, _)| *t < cutoff)
        {
            self.samples.pop_front();
        }
        if self.samples.len() >= MAX_VELOCITY_SAMPLES {
            self.samples.pop_front();
        }
        self.samples.push_back((now, pos));
    }

    fn average_speed(&self, now: Instant) -> f32 {
        if self.samples.len() < 2 {
            return 0.0;
        }
        let mut total_distance: f32 = 0.0;
        let mut prev: Option<&ScreenPointF> = None;
        for (_, pos) in &self.samples {
            if let Some(p) = prev {
                let dx = pos.x - p.x;
                let dy = pos.y - p.y;
                total_distance += (dx * dx + dy * dy).sqrt();
            }
            prev = Some(pos);
        }
        let first_time = self.samples.front().unwrap().0;
        let elapsed = now
            .duration_since(first_time)
            .max(Duration::from_secs(1));
        total_distance / elapsed.as_secs_f32()
    }

    pub fn evaluate(&mut self, now: Instant, currently_shown: bool) -> bool {
        let speed = self.average_speed(now);
        if currently_shown {
            if let Some(shown_at) = self.hint_shown_at {
                if now.duration_since(shown_at) < HINT_MIN_DISPLAY {
                    return true;
                }
            }
            if speed > FAST_SPEED_THRESHOLD {
                self.hint_shown_at = None;
                return false;
            }
            true
        } else {
            let has_enough_history = self
                .samples
                .front()
                .is_some_and(|(t, _)| now.duration_since(*t) >= MIN_HISTORY);
            if has_enough_history && speed < SLOW_SPEED_THRESHOLD {
                self.hint_shown_at = Some(now);
                true
            } else {
                false
            }
        }
    }

    pub fn dismiss_hint(&mut self) {
        self.hint_shown_at = None;
    }
}

pub(crate) struct InteractionState {
    pub virtual_cursor: ScreenPointF,
    pub zoom: f32,
    pub anchored: bool,
    pub anchor_just_engaged: bool,
    pub anchor: ScreenPoint,
    pub mouse_down: bool,
    pub mouse_down_pt: Option<ScreenPointF>,
    pub mouse_down_dpi: f32,
    pub dragging: bool,
    pub selection: Option<ScreenRect>,
    /// Corner radius of `selection` in physical px; 0 = square. Non-zero
    /// only while the selection IS a window the user picked (hover, click
    /// without drag, `W`, `--capture-mode window`) — it is the radius the
    /// OS composites that window with. Every other way a rect comes to be
    /// (drag-select, F / A, moving or resizing a captured window selection)
    /// sets it back to 0: the rect no longer describes a window's shape.
    /// Rides along to the render workers with the selection and into the
    /// copy / save / preview crop.
    pub selection_radius: f32,
    pub captured: bool,
    pub hittest: Hittest,
    pub drag_mode: Option<DragMode>,
    pub drag_anchor_selection: Option<ScreenRect>,
    pub tips_mode: TipsMode,
    pub debug_visible: bool,
    pub last_scroll_end: Option<Instant>,
    pub scroll_momentum: bool,
    pub overlays_visible: bool,
    pub cursor_overlay_visible: bool,
    pub peek_suspended: bool,
    pub has_ever_scrolled: bool,
    pub show_scroll_hint: bool,
    pub velocity_tracker: MouseVelocityTracker,
    pub has_used_magnifier: bool,
    /// SCROLL was pressed and we are waiting for the user to click the
    /// point the driver will aim wheel events from. The selection is
    /// already captured and frozen — this mode only collects a point, so
    /// the panel hides, the cursor becomes a crosshair, and clicks are
    /// routed to the picker instead of the panel/drag machinery. Escape
    /// leaves the mode without cancelling the cycle.
    pub scroll_pick_mode: bool,
    /// Where the OCR lift-and-act mode is in its lifecycle — see
    /// [`OcrState`]. Mirrored verbatim onto `UiSharedState` so the lifted
    /// lines, the modal input gates and the panel set all swap in one
    /// atomic broadcast.
    pub ocr: OcrState,
    /// A transient "that produced nothing" pill — see [`OcrNotice`]. It
    /// deliberately outlives the OCR attempt that raised it: by the time
    /// the user reads it, `ocr` is back to `Idle`, so it cannot live inside
    /// the state enum.
    pub ocr_notice: Option<OcrNotice>,
}

#[derive(Default)]
pub(crate) struct InteractionEffects {
    pub broadcast_mouse: bool,
    pub broadcast_ui: bool,
    pub update_cursor_visibility: bool,
    pub restore_mouse: Option<ScreenPoint>,
    pub set_cursor: Option<CursorIcon>,
}

impl InteractionState {
    /// Point the un-captured selection at whatever the walker found under
    /// the cursor — rect and corner radius together, or neither. The only
    /// path by which `selection_radius` becomes non-zero.
    pub fn set_hover_target(&mut self, target: Option<WindowTarget>) {
        self.selection = target.map(|t| t.rect);
        self.selection_radius = target.map_or(0.0, |t| t.corner_radius);
    }
}

pub(crate) struct InteractionController;

impl InteractionController {
    pub fn apply_zoom_factor(input: &mut InteractionState, factor: f32) -> InteractionEffects {
        if !factor.is_finite() || factor <= 0.0 {
            return InteractionEffects::default();
        }
        let new_zoom = (input.zoom * factor).clamp(ZOOM_MIN, ZOOM_MAX);
        if (new_zoom - input.zoom).abs() < f32::EPSILON {
            return InteractionEffects::default();
        }

        let restore_mouse = if !input.anchored && new_zoom > 1.0 {
            input.anchored = true;
            input.anchor_just_engaged = true;
            Some(input.anchor)
        } else {
            None
        };

        input.zoom = new_zoom;

        // Auto-exit magnifier mode when zoom returns to 1×.
        let restore_mouse = if input.anchored && (new_zoom - 1.0).abs() < f32::EPSILON {
            input.anchored = false;
            input.anchor_just_engaged = false;
            input.overlays_visible = true;
            Some(ScreenPoint::new(
                input.virtual_cursor.x.floor() as i32,
                input.virtual_cursor.y.floor() as i32,
            ))
        } else {
            restore_mouse
        };

        InteractionEffects {
            broadcast_mouse: true,
            broadcast_ui: true,
            restore_mouse,
            ..Default::default()
        }
    }

    pub fn finalize_selection(
        input: &mut InteractionState,
        rect: ScreenRect,
        corner_radius: f32,
        monitors: &[MonitorInfo],
    ) -> InteractionEffects {
        input.selection = Some(rect);
        input.selection_radius = corner_radius;
        input.mouse_down = false;
        input.mouse_down_pt = None;
        input.dragging = false;
        input.drag_mode = None;
        input.drag_anchor_selection = None;
        input.captured = true;
        input.overlays_visible = true;

        let restore_mouse = if input.anchored {
            input.anchored = false;
            input.anchor_just_engaged = false;
            Some(ScreenPoint::new(
                input.virtual_cursor.x.floor() as i32,
                input.virtual_cursor.y.floor() as i32,
            ))
        } else {
            None
        };
        input.zoom = 1.0;

        let dpi = dpi_at_point(input.virtual_cursor, monitors);
        let ht = hit_test(input.virtual_cursor, rect, dpi);
        input.hittest = ht;

        InteractionEffects {
            broadcast_mouse: true,
            broadcast_ui: true,
            update_cursor_visibility: true,
            restore_mouse,
            set_cursor: Some(ht.cursor()),
        }
    }

    pub fn reset(input: &mut InteractionState) -> InteractionEffects {
        input.selection = None;
        input.selection_radius = 0.0;
        input.captured = false;
        // Pick mode only means anything while a captured selection exists
        // to click inside; dropping the selection must drop the mode too,
        // or the crosshair/hidden-panel state would outlive its reason.
        input.scroll_pick_mode = false;
        // Same reasoning as pick mode, one step stronger: a recognition
        // result is a set of rects positioned against the selection that
        // produced it. Once that selection is gone the lifted lines have
        // nothing to sit on and the OCR button set has nothing to act on,
        // so the mode — and any notice it raised — goes with it.
        input.ocr = OcrState::Idle;
        input.ocr_notice = None;
        input.hittest = Hittest::Outside;
        input.drag_mode = None;
        input.drag_anchor_selection = None;

        InteractionEffects {
            broadcast_ui: true,
            update_cursor_visibility: true,
            set_cursor: Some(CursorIcon::Default),
            ..Default::default()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use clowd_rust_core::geometry::RectExt;

    fn monitor() -> MonitorInfo {
        MonitorInfo {
            bounds: ScreenRect::from_xy_size(0, 0, 200, 120),
            scale_factor: 1.0,
            is_primary: true,
            refresh_hz: 60.0,
            name: "test".to_string(),
            adapter_id: None,
            #[cfg(target_os = "macos")]
            logical_origin: clowd_rust_core::geometry::LogicalPoint::new(0.0, 0.0),
        }
    }

    fn state() -> InteractionState {
        InteractionState {
            virtual_cursor: ScreenPointF::new(30.0, 30.0),
            zoom: 2.0,
            anchored: true,
            anchor_just_engaged: true,
            anchor: ScreenPoint::new(100, 60),
            mouse_down: true,
            mouse_down_pt: Some(ScreenPointF::new(20.0, 20.0)),
            mouse_down_dpi: 1.0,
            dragging: true,
            selection: None,
            selection_radius: 0.0,
            captured: false,
            hittest: Hittest::Outside,
            drag_mode: None,
            drag_anchor_selection: None,
            tips_mode: TipsMode::Hints,
            debug_visible: false,
            last_scroll_end: None,
            scroll_momentum: false,
            overlays_visible: true,
            cursor_overlay_visible: true,
            peek_suspended: false,
            has_ever_scrolled: false,
            show_scroll_hint: false,
            velocity_tracker: MouseVelocityTracker::new(),
            has_used_magnifier: false,
            scroll_pick_mode: false,
            ocr: OcrState::Idle,
            ocr_notice: None,
        }
    }

    /// A result-shaped payload for state assertions. Recognition itself is
    /// never exercised here — this module only cares that the mode field is
    /// carried and cleared correctly.
    fn dummy_outcome() -> Arc<OcrOutcome> {
        Arc::new(OcrOutcome {
            lines: Vec::new(),
            full_text: String::new(),
            text_angle: 0.0,
        })
    }

    #[test]
    fn finalize_selection_resets_drag_zoom_and_restores_anchor() {
        let mut input = state();
        let rect = ScreenRect::from_xy_size(20, 20, 40, 30);

        let effects = InteractionController::finalize_selection(&mut input, rect, 12.0, &[monitor()]);

        assert_eq!(input.selection, Some(rect));
        assert_eq!(input.selection_radius, 12.0);
        assert!(input.captured);
        assert!(!input.mouse_down);
        assert_eq!(input.zoom, 1.0);
        assert!(!input.anchored);
        assert_eq!(effects.restore_mouse, Some(ScreenPoint::new(30, 30)));
        assert!(effects.broadcast_mouse);
        assert!(effects.broadcast_ui);
    }

    #[test]
    fn apply_zoom_factor_engages_anchor_on_first_zoom_in() {
        let mut input = state();
        input.zoom = 1.0;
        input.anchored = false;

        let effects = InteractionController::apply_zoom_factor(&mut input, 2.0);

        assert_eq!(input.zoom, 2.0);
        assert!(input.anchored);
        assert_eq!(effects.restore_mouse, Some(input.anchor));
        assert!(effects.broadcast_mouse);
    }

    #[test]
    fn reset_clears_capture_and_selection() {
        let mut input = state();
        input.captured = true;
        input.selection = Some(ScreenRect::from_xy_size(1, 1, 10, 10));
        input.selection_radius = 16.0;
        input.scroll_pick_mode = true;
        input.ocr = OcrState::Lifted {
            anchor: Instant::now(),
            req: 1,
            region: ScreenRect::from_xy_size(1, 1, 10, 10),
            dpi_scale: 1.0,
            outcome: dummy_outcome(),
        };
        input.ocr_notice = Some(OcrNotice {
            anchor: Instant::now(),
            kind: OcrNoticeKind::NoText,
        });

        let effects = InteractionController::reset(&mut input);

        assert!(!input.captured);
        assert_eq!(input.selection, None);
        assert_eq!(input.selection_radius, 0.0);
        assert!(!input.scroll_pick_mode);
        // The lifted lines were positioned against the selection that just
        // disappeared; leaving the mode up would draw them over nothing.
        assert!(!input.ocr.active());
        assert!(input.ocr_notice.is_none());
        assert_eq!(input.hittest, Hittest::Outside);
        assert!(effects.broadcast_ui);
        assert!(effects.update_cursor_visibility);
    }

    #[test]
    fn ocr_state_gates_agree_on_each_phase() {
        let region = ScreenRect::from_xy_size(1, 1, 10, 10);

        assert!(!OcrState::Idle.active());
        assert!(!OcrState::Idle.shows_ocr_panel());
        assert!(!OcrState::Idle.hides_panel());

        // Scanning: modal, but NO panel of any kind — the strip used to
        // show here with COPY/SEARCH/UPLOAD lit-but-dead, which read as
        // broken buttons. The strip now appears only once there is text to
        // act on.
        let scanning = OcrState::Scanning {
            anchor: Instant::now(),
            req: 1,
            region,
        };
        assert!(scanning.active());
        assert!(!scanning.shows_ocr_panel());
        assert!(scanning.hides_panel());

        let lifted = OcrState::Lifted {
            anchor: Instant::now(),
            req: 1,
            region,
            dpi_scale: 1.0,
            outcome: dummy_outcome(),
        };
        assert!(lifted.active());
        assert!(lifted.shows_ocr_panel());
        assert!(!lifted.hides_panel());

        // Retracting is still modal (the colour fade is playing, the
        // selection stays frozen) but hands the Normal buttons back at once.
        let retracting = OcrState::Retracting {
            anchor: Instant::now(),
            region,
        };
        assert!(retracting.active());
        assert!(!retracting.shows_ocr_panel());
        assert!(!retracting.hides_panel());
    }

    #[test]
    fn notice_alpha_is_solid_then_ramps_to_zero() {
        // t=0 and anywhere before the fade window: fully opaque.
        assert_eq!(notice_alpha(0.0), 1.0);
        assert_eq!(notice_alpha(NOTICE_SECS - NOTICE_FADE_SECS), 1.0);
        // Halfway through the fade window.
        assert!((notice_alpha(NOTICE_SECS - NOTICE_FADE_SECS / 2.0) - 0.5).abs() < 1e-4);
        // Just before expiry: nearly gone, but not yet zero.
        let nearly_done = notice_alpha(NOTICE_SECS - 0.001);
        assert!(nearly_done > 0.0 && nearly_done < 0.01, "got {nearly_done}");
        // Past expiry, including absurdly far past it (suspend/resume).
        assert_eq!(notice_alpha(NOTICE_SECS), 0.0);
        assert_eq!(notice_alpha(NOTICE_SECS + 10.0), 0.0);
        assert_eq!(notice_alpha(1.0e9), 0.0);
    }

    #[test]
    fn fresh_notice_is_visible_and_opaque() {
        let n = OcrNotice {
            anchor: Instant::now(),
            kind: OcrNoticeKind::Unavailable,
        };
        assert!(n.visible());
        assert_eq!(n.alpha(), 1.0);
        assert!(!n.kind.message().is_empty());
    }

    #[test]
    fn velocity_tracker_slow_mouse_shows_hint() {
        let mut tracker = MouseVelocityTracker::new();
        let start = Instant::now();
        for i in 0..25 {
            let t = start + Duration::from_millis(i * 200);
            tracker.record(t, ScreenPointF::new(100.0 + i as f32 * 0.5, 100.0));
        }
        let now = start + Duration::from_millis(24 * 200);
        assert!(tracker.evaluate(now, false));
    }

    #[test]
    fn velocity_tracker_fast_mouse_no_hint() {
        let mut tracker = MouseVelocityTracker::new();
        let start = Instant::now();
        for i in 0..25 {
            let t = start + Duration::from_millis(i * 200);
            tracker.record(t, ScreenPointF::new(100.0 + i as f32 * 100.0, 100.0));
        }
        let now = start + Duration::from_millis(24 * 200);
        assert!(!tracker.evaluate(now, false));
    }

    #[test]
    fn velocity_tracker_hint_stays_for_min_duration() {
        let mut tracker = MouseVelocityTracker::new();
        let start = Instant::now();
        for i in 0..20 {
            let t = start + Duration::from_millis(i * 200);
            tracker.record(t, ScreenPointF::new(100.0, 100.0));
        }
        let show_time = start + Duration::from_millis(19 * 200);
        assert!(tracker.evaluate(show_time, false));

        let fast_time = show_time + Duration::from_secs(1);
        tracker.record(fast_time, ScreenPointF::new(500.0, 500.0));
        assert!(tracker.evaluate(fast_time, true));
    }

    #[test]
    fn velocity_tracker_hides_after_min_duration_with_fast_movement() {
        let mut tracker = MouseVelocityTracker::new();
        let start = Instant::now();
        for i in 0..20 {
            let t = start + Duration::from_millis(i * 200);
            tracker.record(t, ScreenPointF::new(100.0, 100.0));
        }
        let show_time = start + Duration::from_millis(19 * 200);
        assert!(tracker.evaluate(show_time, false));

        let later = show_time + Duration::from_secs(4);
        for i in 0..30 {
            let t = show_time + Duration::from_secs(3) + Duration::from_millis(i * 30);
            tracker.record(t, ScreenPointF::new(100.0 + i as f32 * 80.0, 100.0));
        }
        assert!(!tracker.evaluate(later, true));
    }

    #[test]
    fn velocity_tracker_no_hint_before_enough_history() {
        let mut tracker = MouseVelocityTracker::new();
        let start = Instant::now();
        tracker.record(start, ScreenPointF::new(100.0, 100.0));
        let now = start + Duration::from_millis(500);
        tracker.record(now, ScreenPointF::new(100.0, 100.0));
        assert!(!tracker.evaluate(now, false));
    }
}
