use std::collections::VecDeque;
use std::time::{Duration, Instant};

use crate::geometry::{ScreenPoint, ScreenPointF, ScreenRect};
use crate::selection::{dpi_at_point, hit_test, DragMode, Hittest};
use crate::settings::TipsMode;
use crate::system::MonitorInfo;
use winit::window::CursorIcon;

pub const ZOOM_MIN: f32 = 1.0;
pub const ZOOM_MAX: f32 = 256.0;

const VELOCITY_WINDOW: Duration = Duration::from_secs(5);
const SLOW_SPEED_THRESHOLD: f32 = 15.0;
const FAST_SPEED_THRESHOLD: f32 = 40.0;
const HINT_MIN_DISPLAY: Duration = Duration::from_secs(3);
const MIN_HISTORY: Duration = Duration::from_secs(3);
const MAX_VELOCITY_SAMPLES: usize = 512;

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
}

#[derive(Default)]
pub(crate) struct InteractionEffects {
    pub broadcast_mouse: bool,
    pub broadcast_ui: bool,
    pub update_cursor_visibility: bool,
    pub restore_mouse: Option<ScreenPoint>,
    pub set_cursor: Option<CursorIcon>,
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

    pub fn finalize_selection(input: &mut InteractionState, rect: ScreenRect, monitors: &[MonitorInfo]) -> InteractionEffects {
        input.selection = Some(rect);
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
        input.captured = false;
        // Pick mode only means anything while a captured selection exists
        // to click inside; dropping the selection must drop the mode too,
        // or the crosshair/hidden-panel state would outlive its reason.
        input.scroll_pick_mode = false;
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
    use crate::geometry::RectExt;

    fn monitor() -> MonitorInfo {
        MonitorInfo {
            bounds: ScreenRect::from_xy_size(0, 0, 200, 120),
            scale_factor: 1.0,
            is_primary: true,
            refresh_hz: 60.0,
            name: "test".to_string(),
            adapter_id: None,
            #[cfg(target_os = "macos")]
            logical_origin: crate::geometry::LogicalPoint::new(0.0, 0.0),
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
        }
    }

    #[test]
    fn finalize_selection_resets_drag_zoom_and_restores_anchor() {
        let mut input = state();
        let rect = ScreenRect::from_xy_size(20, 20, 40, 30);

        let effects = InteractionController::finalize_selection(&mut input, rect, &[monitor()]);

        assert_eq!(input.selection, Some(rect));
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
        input.scroll_pick_mode = true;

        let effects = InteractionController::reset(&mut input);

        assert!(!input.captured);
        assert_eq!(input.selection, None);
        assert!(!input.scroll_pick_mode);
        assert_eq!(input.hittest, Hittest::Outside);
        assert!(effects.broadcast_ui);
        assert!(effects.update_cursor_visibility);
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
