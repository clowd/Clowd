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

use crate::interaction::{OcrNotice, OcrState};
use crate::settings::TipsMode;
use crate::ui::components::panel::layout::{compute_layout as compute_panel_layout, PanelLayout};
use crate::ui::components::panel::model::{PanelButtonSet, PanelFeatures};
use clowd_rust_core::geometry::{RectExt, ScreenPointF, ScreenRect};

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
    /// Corner radius of `selection` in physical px, 0 = square — see
    /// `InteractionState::selection_radius`. Read by overlays that paint
    /// INSIDE the selection (the OCR sweep) so they stop at the same curve
    /// the desktop pass draws the border around.
    pub selection_radius: f32,
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
    /// Mirror of `InteractionState::scroll_pick_mode`: the user pressed
    /// SCROLL and is now picking the point wheel events will be aimed
    /// from. Renderers use it to drop the panel — the click that follows
    /// belongs to the picker, so nothing clickable may be in the way.
    pub scroll_pick_mode: bool,
    /// Mirror of `InteractionState::ocr`. Carried whole rather than
    /// decomposed into flags so the lifted lines, the modal state and the
    /// panel set are guaranteed to change together in one broadcast — a
    /// renderer can never see the OCR button set over un-lifted lines.
    pub ocr: OcrState,
    /// Mirror of `InteractionState::ocr_notice`: the transient "OCR gave
    /// you nothing" pill.
    pub ocr_notice: Option<OcrNotice>,
    /// Which optional panel buttons the shell left switched on. Fixed for
    /// the whole cycle, but it rides in the broadcast rather than in each
    /// renderer's own copy of the settings so the app thread and every
    /// render thread compute the panel from one value.
    pub panel_features: PanelFeatures,
}

/// Return the monitor the virtual cursor is over. `None` when it sits in
/// a gap between monitors.
fn monitor_under_cursor(state: &UiSharedState) -> Option<UiMonitor> {
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

/// Which set of buttons the panel is showing, or `None` when there is no
/// panel at all.
///
/// This is the SINGLE decision point, consulted by both
/// [`panel_visibility`] (what the renderers draw) and
/// `app::current_panel_layout` (where the app thread routes clicks). Those
/// two are documented mirrors of each other — see the warning above
/// `current_panel_layout` — and the set is exactly the kind of thing that
/// drifts between them: get it wrong and a click on BACK fires the command
/// that happens to sit at that index in the *other* set. Keeping the
/// decision in one pure function makes that class of bug unrepresentable
/// rather than merely unlikely.
///
/// Takes the three inputs loose rather than a `&UiSharedState` so the app
/// thread can call it straight off `InteractionState` without building a
/// snapshot first.
pub fn active_panel_set(captured: bool, scroll_pick_mode: bool, ocr: &OcrState) -> Option<PanelButtonSet> {
    if !captured {
        return None;
    }
    // Scroll-point picking runs over the same selection the panel sits on
    // top of: the panel must be gone so the pick click can land anywhere
    // inside the region, including under where the buttons were.
    if scroll_pick_mode {
        return None;
    }
    // While the OCR sweep is looping there is nothing to act on yet, so no
    // panel AT ALL — not the Normal set (its buttons would act on a frozen
    // selection mid-scan) and not the OCR set (COPY/SEARCH/UPLOAD would be
    // lit but dead, indistinguishable from broken buttons). The strip
    // materializes with the reveal, when the actions become real.
    if ocr.hides_panel() {
        return None;
    }
    if ocr.shows_ocr_panel() {
        return Some(PanelButtonSet::Ocr);
    }
    Some(PanelButtonSet::Normal)
}

/// Decide whether the button panel is visible and where. Pure function —
/// the app thread and every render thread call this with the same state.
pub fn panel_visibility(state: &UiSharedState) -> Option<PanelVisibility> {
    // Deliberately ahead of `active_panel_set`, and deliberately not part
    // of it: the Q toggle is about *drawing*, not about which buttons are
    // live. The app-thread mirror keeps routing clicks while overlays are
    // hidden (that is pre-existing behavior), so folding this gate into
    // the shared function would silently change it.
    if !state.overlays_visible {
        return None;
    }
    let set = active_panel_set(state.captured, state.scroll_pick_mode, &state.ocr)?;
    let sel = state.selection?;
    let monitor = pick_monitor_containing_center(&state.monitors, sel)?;
    let layout = compute_panel_layout(monitor.bounds, sel, monitor.dpi_scale, set, state.panel_features)?;
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
    monitor_under_cursor(state)
}

/// Decide whether the scroll-point picker's scope reticle (and the one
/// hint that goes with it) is visible, and which monitor the cursor is on.
///
/// While this is `Some`, the picker owns the overlay: every other hint,
/// the tips panel, the button panel and the selection's resize handles are
/// suppressed, because the only input the overlay is waiting for is one
/// click anywhere inside the selection.
///
/// The returned monitor is where the *hint* goes. The reticle itself is
/// drawn by every monitor within `SCOPE_EXTENT` of the cursor, so it is not
/// cut in half at a seam — see `ui::gpu::hints`.
///
/// The magnifier's Q toggle still hides everything, hence the
/// `overlays_visible` gate. `app::update_cursor_visibility` carries the
/// same gate: if this returns `None` the OS pointer must come back, or
/// there would be no pointer at all. Unreachable today (Q is swallowed
/// while picking) but the two must not drift apart.
pub fn scroll_pick_visibility(state: &UiSharedState) -> Option<UiMonitor> {
    if !state.scroll_pick_mode || !state.overlays_visible {
        return None;
    }
    monitor_under_cursor(state)
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
    use std::time::Instant;

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
            selection_radius: 0.0,
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
            scroll_pick_mode: false,
            ocr: OcrState::Idle,
            ocr_notice: None,
            panel_features: PanelFeatures::ALL,
        }
    }

    /// The panel-set decision never looks inside the outcome, so an empty
    /// one is enough to stand a Lifted/Retracting state up.
    fn dummy_outcome() -> Arc<crate::ocr::OcrOutcome> {
        Arc::new(crate::ocr::OcrOutcome {
            lines: Vec::new(),
            full_text: String::new(),
            text_angle: 0.0,
        })
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
    fn panel_hidden_while_picking_scroll_point() {
        let mut s = state();
        s.captured = true;

        assert!(panel_visibility(&s).is_some());
        s.scroll_pick_mode = true;
        assert!(panel_visibility(&s).is_none());
        s.scroll_pick_mode = false;
        assert!(panel_visibility(&s).is_some());
    }

    /// The panel's OCR lifecycle: HIDDEN while the sweep loops (nothing to
    /// act on — buttons that no-op read as broken), the OCR strip once the
    /// outcome is lifted. Both click routing and drawing flow through this
    /// one function, so this test pins the behavior for both.
    #[test]
    fn panel_hidden_while_scanning_shows_ocr_set_when_lifted() {
        let mut s = state();
        s.captured = true;
        assert_eq!(panel_visibility(&s).unwrap().layout.set, PanelButtonSet::Normal);

        s.ocr = OcrState::Scanning {
            anchor: Instant::now(),
            req: 1,
            region: s.selection.unwrap(),
        };
        assert!(panel_visibility(&s).is_none());

        s.ocr = OcrState::Lifted {
            anchor: Instant::now(),
            req: 1,
            region: s.selection.unwrap(),
            dpi_scale: 1.0,
            outcome: dummy_outcome(),
        };
        assert_eq!(panel_visibility(&s).unwrap().layout.set, PanelButtonSet::Ocr);
    }

    /// BACK must hand the familiar buttons back immediately; the retract
    /// animation is cosmetic and must not hold the OCR strip on screen.
    #[test]
    fn panel_shows_normal_set_while_retracting() {
        let mut s = state();
        s.captured = true;
        s.ocr = OcrState::Retracting {
            anchor: Instant::now(),
            region: s.selection.unwrap(),
        };
        assert_eq!(panel_visibility(&s).unwrap().layout.set, PanelButtonSet::Normal);
    }

    /// Scroll picking outranks OCR mode: the panel is gone entirely, so
    /// there is no set to argue about. (Unreachable today — the two modes
    /// cannot both be engaged — but the ordering is what makes that true.)
    #[test]
    fn scroll_pick_hides_the_panel_even_in_ocr_mode() {
        assert_eq!(active_panel_set(false, false, &OcrState::Idle), None);
        assert_eq!(active_panel_set(true, false, &OcrState::Idle), Some(PanelButtonSet::Normal));
        let lifted = OcrState::Lifted {
            anchor: Instant::now(),
            req: 1,
            region: ScreenRect::from_xy_size(0, 0, 10, 10),
            dpi_scale: 1.0,
            outcome: dummy_outcome(),
        };
        assert_eq!(active_panel_set(true, true, &lifted), None);
        assert_eq!(active_panel_set(false, false, &lifted), None);
    }

    #[test]
    fn scroll_pick_reticle_follows_the_cursor_and_obeys_the_overlay_toggle() {
        let mut s = state();
        s.captured = true;

        // Not picking: no reticle, even though a selection is captured.
        assert!(scroll_pick_visibility(&s).is_none());

        s.scroll_pick_mode = true;
        assert!(scroll_pick_visibility(&s).is_some());

        // Cursor off every monitor — nothing to draw it on.
        s.virtual_cursor = ScreenPointF::new(1000.0, 30.0);
        assert!(scroll_pick_visibility(&s).is_none());
        s.virtual_cursor = ScreenPointF::new(30.0, 30.0);

        s.overlays_visible = false;
        assert!(scroll_pick_visibility(&s).is_none());
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
