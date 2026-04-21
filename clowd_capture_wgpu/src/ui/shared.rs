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
    pub tips_visible: bool,
    pub debug_visible: bool,
    /// Master overlay switch. When `false`, every UI overlay (tips,
    /// debug, panel, selection border, crosshair, dim) is suppressed so
    /// the desktop shows through unobstructed. Toggled by the Q key
    /// (`DxScreenCapture.cpp:1234-1239`).
    pub overlays_visible: bool,
    pub hovered_monitor_name: Option<String>,
    pub hovered_window_title: Option<String>,
    pub hovered_window_bounds: Option<ScreenRect>,
    pub hovered_pixel_bgra: Option<[u8; 4]>,
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
    if state.captured || state.mouse_down || !state.tips_visible {
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
    Some(AreaIndicatorVisibility { monitor: *monitor })
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
