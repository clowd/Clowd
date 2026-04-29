use std::time::Instant;

use crate::geometry::{ScreenPoint, ScreenPointF, ScreenRect};
use crate::selection::{dpi_at_point, hit_test, DragMode, Hittest};
use crate::settings::TipsMode;
use crate::system::MonitorInfo;
use winit::window::CursorIcon;

pub const ZOOM_MIN: f32 = 1.0;
pub const ZOOM_MAX: f32 = 256.0;

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

        let effects = InteractionController::reset(&mut input);

        assert!(!input.captured);
        assert_eq!(input.selection, None);
        assert_eq!(input.hittest, Hittest::Outside);
        assert!(effects.broadcast_ui);
        assert!(effects.update_cursor_visibility);
    }
}
