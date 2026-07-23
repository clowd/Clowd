//! Shared UI state + visibility rules.
//!
//! The app thread builds one [`UiSharedState`] per tick and broadcasts it
//! (as an [`Arc`]) to every render thread. Every render thread runs the
//! **same** pure visibility/layout rules against its own monitor to decide
//! what to draw — no coordination needed between threads.
//!
//! The app thread also calls the same functions to route clicks: it knows
//! exactly where every component is because those positions are a pure
//! function of the state it just broadcast.

use std::sync::Arc;

use crate::geometry::{RectExt, ScreenPointF, ScreenRect};
use crate::settings::TipsMode;
use crate::ui::components::panel::layout::{compute_layout as compute_panel_layout, PanelLayout};

/// Minimal per-monitor info the UI layout rules need.
///
/// Mirrors a subset of `system::MonitorInfo` without the fields the UI
/// doesn't use (refresh rate, DXGI adapter id, raw OS name).
#[derive(Debug, Clone, Copy)]
pub struct UiMonitor {
    pub bounds: ScreenRect,
    pub dpi_scale: f32,
    pub is_primary: bool,
}

/// The single app-wide state snapshot broadcast to every render thread
/// every tick.
///
/// Fields are owned (not borrowed), so the struct is `Send + 'static` and
/// trivially wrappable in `Arc`. Strings are cloned on build — they're
/// tiny and only change on mouse move.
#[derive(Debug, Clone)]
pub struct UiSharedState {
    pub monitors: Arc<[UiMonitor]>,
    pub selection: Option<ScreenRect>,
    pub captured: bool,
    pub mouse_down: bool,
    pub dragging: bool,
    pub zoom: f32,
    pub virtual_cursor: ScreenPointF,
    pub accent_color: [f32; 4],
    pub tips_mode: TipsMode,
    pub debug_visible: bool,
    /// Master overlay switch. When `false`, every UI overlay (tips,
    /// debug, panel, selection border, crosshair, dim) is suppressed so
    /// the desktop shows through unobstructed. Toggled by the Q key
    /// (`DxScreenCapture.cpp:1234-1239`).
    pub overlays_visible: bool,
    pub hovered_monitor_name: Option<String>,
    pub hovered_window_title: Option<String>,
    pub hovered_window_bounds: Option<ScreenRect>,
    pub hovered_window_index: Option<usize>,
    pub hovered_window_obstructed: bool,
    pub cursor_overlay_visible: bool,
    pub hovered_pixel_bgra: Option<[u8; 4]>,
    /// Bounding rect of the captured cursor image in virtual-desktop
    /// physical pixels: `(left, top, right, bottom)`. Computed from the
    /// cursor position minus hotspot + image dimensions. `None` when
    /// cursor capture failed or the desktop buffer is unavailable.
    pub cursor_image_rect: Option<[f32; 4]>,
    pub show_scroll_hint: bool,
    pub has_used_magnifier: bool,
}

/// Return the monitor whose bounds contain the center of `rect`. `None`
/// when no monitor contains the center.
fn pick_monitor_containing_center(monitors: &[UiMonitor], rect: ScreenRect) -> Option<UiMonitor> {
    let cx = (rect.left() + rect.right()) / 2;
    let cy = (rect.top() + rect.bottom()) / 2;
    monitors.iter().find_map(|m| {
        let b = m.bounds;
        if cx >= b.left() && cx < b.right() && cy >= b.top() && cy < b.bottom() {
            Some(*m)
        } else {
            None
        }
    })
}

/// Result of evaluating the button-panel visibility rule.
pub struct PanelVisibility {
    pub monitor: UiMonitor,
    /// Fully-computed layout (panel rect + per-button rects) in
    /// virtual-desktop pixels.
    pub layout: PanelLayout,
}

/// Decide whether the button panel is visible and where. Pure function —
/// the app thread and every render thread call this with the same state.
pub fn panel_visibility(state: &UiSharedState) -> Option<PanelVisibility> {
    if !state.overlays_visible {
        return None;
    }
    if !state.captured {
        return None;
    }
    let sel = state.selection?;
    let monitor = pick_monitor_containing_center(&state.monitors, sel)?;
    let layout = compute_panel_layout(monitor.bounds, sel, monitor.dpi_scale)?;
    Some(PanelVisibility {
        monitor,
        layout,
    })
}

/// Decide whether the tips panel is visible and on which monitor.
/// Follows the cursor — shown on whichever monitor contains it.
pub fn tips_visibility(state: &UiSharedState) -> Option<(usize, UiMonitor)> {
    if !state.overlays_visible {
        return None;
    }
    if state.captured || state.mouse_down || !state.tips_mode.show_tips_panel() || state.debug_visible {
        return None;
    }
    let cx = state.virtual_cursor.x.round() as i32;
    let cy = state.virtual_cursor.y.round() as i32;
    let idx = state.monitors.iter().position(|m| {
        let b = m.bounds;
        cx >= b.left() && cx < b.right() && cy >= b.top() && cy < b.bottom()
    })?;
    let monitor = *state.monitors.get(idx)?;
    Some((idx, monitor))
}

/// Result of evaluating the area-indicator visibility rule.
pub struct AreaIndicatorVisibility {
    pub monitor: UiMonitor,
}

/// Decide whether the in-selection area indicator ("W × H" pill) is
/// visible and on which monitor. Shown on the monitor containing the
/// cursor, only while a selection exists but has not yet been captured.
pub fn area_indicator_visibility(state: &UiSharedState) -> Option<AreaIndicatorVisibility> {
    if !state.overlays_visible {
        return None;
    }
    if state.captured {
        return None;
    }
    let _sel = state.selection?;
    let cx = state.virtual_cursor.x.round() as i32;
    let cy = state.virtual_cursor.y.round() as i32;
    let monitor = state.monitors.iter().find(|m| {
        let b = m.bounds;
        cx >= b.left() && cx < b.right() && cy >= b.top() && cy < b.bottom()
    })?;
    Some(AreaIndicatorVisibility {
        monitor: *monitor,
    })
}

/// Decide whether the floating hint tooltips are visible and on which
/// monitor. Follows the cursor — only shown in `Hints` mode.
/// Exception: when zoomed in, the magnifier hint stays visible even
/// with overlays off (the renderer decides which hints to show) so the
/// user can always find their way out of the magnifier. Zoom does *not*
/// override the mode gate: `Off`/`Tips` never show floating hints.
pub fn hints_visibility(state: &UiSharedState) -> Option<UiMonitor> {
    if !state.tips_mode.show_hints() {
        return None;
    }
    let zoomed = state.zoom > 1.0;
    if !zoomed && !state.overlays_visible {
        return None;
    }
    if state.captured || state.mouse_down {
        return None;
    }
    let cx = state.virtual_cursor.x.round() as i32;
    let cy = state.virtual_cursor.y.round() as i32;
    state
        .monitors
        .iter()
        .find(|m| {
            let b = m.bounds;
            cx >= b.left() && cx < b.right() && cy >= b.top() && cy < b.bottom()
        })
        .copied()
}

/// Whether the per-monitor debug panel is visible on `this` monitor. Shown
/// on **every** monitor when the `D`-key toggle is on, mirroring
/// `DxScreenCapture.cpp:915-933`.
pub fn debug_monitor_visibility(state: &UiSharedState, _this: &UiMonitor) -> bool {
    state.overlays_visible && state.debug_visible
}

/// Whether the primary (cursor-follow) debug panel is visible on `this`
/// monitor. Shown on exactly one monitor — the one containing the virtual
/// cursor. Mirrors `DxScreenCapture.cpp:935-977`.
pub fn debug_primary_visibility(state: &UiSharedState, this: &UiMonitor) -> bool {
    if !state.overlays_visible {
        return false;
    }
    if !state.debug_visible {
        return false;
    }
    let cx = state.virtual_cursor.x.round() as i32;
    let cy = state.virtual_cursor.y.round() as i32;
    let b = this.bounds;
    cx >= b.left() && cx < b.right() && cy >= b.top() && cy < b.bottom()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn monitor() -> UiMonitor {
        UiMonitor {
            bounds: ScreenRect::from_xy_size(0, 0, 200, 120),
            dpi_scale: 1.0,
            is_primary: true,
        }
    }

    fn state() -> UiSharedState {
        UiSharedState {
            monitors: Arc::from([monitor()]),
            selection: Some(ScreenRect::from_xy_size(20, 20, 80, 40)),
            captured: false,
            mouse_down: false,
            dragging: false,
            zoom: 1.0,
            virtual_cursor: ScreenPointF::new(30.0, 30.0),
            accent_color: [1.0, 0.0, 0.0, 1.0],
            tips_mode: TipsMode::Tips,
            debug_visible: false,
            overlays_visible: true,
            hovered_monitor_name: None,
            hovered_window_title: None,
            hovered_window_bounds: None,
            hovered_window_index: None,
            hovered_window_obstructed: false,
            cursor_overlay_visible: true,
            hovered_pixel_bgra: None,
            cursor_image_rect: None,
            show_scroll_hint: false,
            has_used_magnifier: false,
        }
    }

    #[test]
    fn panel_only_visible_after_capture() {
        let mut s = state();

        assert!(panel_visibility(&s).is_none());
        s.captured = true;
        assert!(panel_visibility(&s).is_some());
        s.overlays_visible = false;
        assert!(panel_visibility(&s).is_none());
    }

    #[test]
    fn tips_hide_during_capture_mouse_down_or_debug() {
        let mut s = state();

        assert!(tips_visibility(&s).is_some());
        s.mouse_down = true;
        assert!(tips_visibility(&s).is_none());
        s.mouse_down = false;
        s.debug_visible = true;
        assert!(tips_visibility(&s).is_none());
        s.debug_visible = false;
        s.captured = true;
        assert!(tips_visibility(&s).is_none());
    }

    #[test]
    fn area_indicator_visible_only_for_uncaptured_selection() {
        let mut s = state();

        assert!(area_indicator_visibility(&s).is_some());
        s.captured = true;
        assert!(area_indicator_visibility(&s).is_none());
        s.captured = false;
        s.selection = None;
        assert!(area_indicator_visibility(&s).is_none());
    }

    #[test]
    fn hints_only_in_hints_mode_even_when_zoomed() {
        let mut s = state();

        // Hints mode: shown normally, and still shown when zoomed with
        // overlays toggled off (the exit-magnifier hint must survive).
        s.tips_mode = TipsMode::Hints;
        assert!(hints_visibility(&s).is_some());
        s.zoom = 2.0;
        s.overlays_visible = false;
        assert!(hints_visibility(&s).is_some());

        // Off / Tips modes: zoom must NOT force the floating hints on.
        for mode in [TipsMode::Off, TipsMode::Tips] {
            s.tips_mode = mode;
            s.zoom = 1.0;
            s.overlays_visible = true;
            assert!(hints_visibility(&s).is_none(), "{mode:?} at zoom 1 should hide hints");
            s.zoom = 2.0;
            assert!(hints_visibility(&s).is_none(), "{mode:?} when zoomed should still hide hints");
        }
    }

    #[test]
    fn debug_visibility_respects_overlays_and_cursor_monitor() {
        let m = monitor();
        let mut s = state();
        s.debug_visible = true;

        assert!(debug_monitor_visibility(&s, &m));
        assert!(debug_primary_visibility(&s, &m));
        s.virtual_cursor = ScreenPointF::new(300.0, 30.0);
        assert!(!debug_primary_visibility(&s, &m));
        s.overlays_visible = false;
        assert!(!debug_monitor_visibility(&s, &m));
    }
}
