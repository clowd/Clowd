//! Shared UI state, the per-monitor descriptor and the rules the overlay
//! components share.
//!
//! The app thread builds one [`UiSharedState`] per tick and broadcasts it
//! (as an [`Arc`]) to every render thread. What it carries has shrunk to
//! what a worker still decides for itself: nearly every overlay is now
//! tessellated on the app thread by egui — the run that answers a click is
//! the run that produced the picture — and shipped as primitives, one set
//! per monitor, for the workers to paint.
//!
//! The pure geometry rules below (which monitor holds a point, whether two
//! rects overlap) stay here because every component's own `inputs` builder
//! asks the same questions.

use std::sync::Arc;

use crate::interaction::OcrState;
use crate::system::{CapturedDesktop, CursorImage};
use crate::ui::components::panel::model::PanelButtonSet;
use crate::ui::egui_frame::EguiFrame;
use clowd_rust_core::geometry::{RectExt, ScreenPoint, ScreenPointF, ScreenRect, ScreenRectF};

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
/// trivially wrappable in `Arc`. Nothing in it allocates on build any
/// more: the four flags are `Copy`, and the OCR outcome and the per-monitor
/// egui frames are behind `Arc`s, so a broadcast costs a handful of atomic
/// increments even on the per-mouse-move path.
#[derive(Debug, Clone)]
pub struct UiSharedState {
    pub debug_visible: bool,
    /// Master overlay switch. When `false`, every UI overlay (tips,
    /// debug, panel, selection border, crosshair, dim) is suppressed so
    /// the desktop shows through unobstructed. Toggled by the Q key
    /// (`DxScreenCapture.cpp:1234-1239`).
    pub overlays_visible: bool,
    pub cursor_overlay_visible: bool,
    /// Mirror of `InteractionState::scroll_pick_mode`: the user pressed
    /// SCROLL and is now picking the point wheel events will be aimed
    /// from. The reticle and its instruction are drawn by egui; what a
    /// worker still reads this for is the desktop pass's own treatment of
    /// the picking state.
    pub scroll_pick_mode: bool,
    /// Mirror of `InteractionState::ocr`. Carried whole rather than
    /// decomposed into flags so the lifted lines, the modal state and the
    /// panel set are guaranteed to change together in one broadcast — a
    /// renderer can never see the OCR button set over un-lifted lines.
    pub ocr: OcrState,
    /// What egui produced for each monitor this broadcast, indexed the same
    /// as `monitors`. `None` in a slot means that monitor has nothing to
    /// draw (no host has run yet, or it drew nothing).
    pub egui: Arc<[Option<Arc<EguiFrame>>]>,
}

/// The index of the monitor whose bounds hold `p`, or `None` in a gap
/// between monitors. The point is rounded first and the right and bottom
/// edges are open, which is the one "which monitor is this on" rule every
/// overlay shares — the hosts hand the pointer to egui by the same test.
pub fn monitor_index_at(monitors: &[UiMonitor], p: ScreenPointF) -> Option<usize> {
    let (x, y) = (p.x.round() as i32, p.y.round() as i32);
    monitors.iter().position(|m| {
        let b = m.bounds;
        x >= b.left() && x < b.right() && y >= b.top() && y < b.bottom()
    })
}

/// Strict axis-aligned overlap: rects that merely touch do not intersect,
/// so a shape ending exactly on a monitor's left edge is the neighbour's
/// alone and no seam draws it twice.
pub fn aabb_intersects(a: ScreenRectF, b: ScreenRectF) -> bool {
    a.right() > b.left() && a.left() < b.right() && a.bottom() > b.top() && a.top() < b.bottom()
}

/// Sample one desktop pixel as BGRA. Shared by the info overlay and the
/// SELECT-COLOR command.
pub fn sample_bgra(buf: &CapturedDesktop, p: ScreenPoint) -> Option<[u8; 4]> {
    let dx = p.x - buf.bounds.min_x();
    let dy = p.y - buf.bounds.min_y();
    if dx < 0 || dy < 0 {
        return None;
    }
    let (w, h) = (buf.width as i32, buf.height as i32);
    if dx >= w || dy >= h {
        return None;
    }
    let idx = ((dy * w + dx) as usize) * 4;
    let s = buf.bgra.get(idx..idx + 4)?;
    Some([s[0], s[1], s[2], s[3]])
}

/// Bounding rect of the captured cursor image, in virtual-desktop physical
/// px: the cursor position minus its hotspot, sized by the bitmap. `None`
/// when the cursor was not captured or the OS reports it hidden.
pub fn cursor_image_rect(buf: Option<&CapturedDesktop>) -> Option<ScreenRectF> {
    let cursor = buf?.cursor.as_ref()?;
    if !cursor.visible {
        return None;
    }
    let (w, h) = match &cursor.image {
        CursorImage::AlphaBlended {
            width,
            height,
            ..
        } => (*width, *height),
        CursorImage::Masked {
            width,
            height,
            ..
        } => (*width, *height),
    };
    let left = cursor.position.x as f32 - cursor.hotspot_x as f32;
    let top = cursor.position.y as f32 - cursor.hotspot_y as f32;
    Some(ScreenRectF::from_xy_size(left, top, w as f32, h as f32))
}

/// Whether the peek window covers any part of the cursor image. The peek
/// draws a window's true pixels over that area, so the cursor overlay and
/// everything that points at it are suppressed while it does.
pub fn peek_covers_cursor(cursor: Option<ScreenRectF>, peek: Option<ScreenRect>) -> bool {
    cursor
        .zip(peek)
        .is_some_and(|(c, p)| aabb_intersects(c, p.to_f32()))
}

/// The monitor whose bounds hold the point, or `None` in a gap between
/// monitors.
fn monitor_at(monitors: &[UiMonitor], x: i32, y: i32) -> Option<UiMonitor> {
    let index = monitor_index_at(monitors, ScreenPointF::new(x as f32, y as f32))?;
    monitors.get(index).copied()
}

/// The monitor whose bounds contain the center of `rect`.
pub(crate) fn pick_monitor_containing_center(monitors: &[UiMonitor], rect: ScreenRect) -> Option<UiMonitor> {
    monitor_at(monitors, (rect.left() + rect.right()) / 2, (rect.top() + rect.bottom()) / 2)
}

/// Which set of buttons the panel is showing, or `None` when there is no
/// panel at all.
///
/// This is the SINGLE decision point: `egui_host` builds the strip's
/// inputs from it, and `PanelSwapGuard` watches it for swaps. Keeping the
/// decision in one pure function means the guard and the strip on screen
/// can never disagree about which set is up.
///
/// Takes the three inputs loose rather than a `&UiSharedState` so the app
/// thread can call it straight off `InteractionState` without building a
/// snapshot first.
pub fn active_panel_set(captured: bool, scroll_pick_mode: bool, ocr: &OcrState) -> Option<PanelButtonSet> {
    if !captured {
        return None;
    }
    // Scroll-point picking gets a strip of its own: the aiming
    // instruction where the readout goes, and BACK / EXIT. It outranks
    // OCR mode for the same reason picking outranks every other input —
    // the click the user is about to make belongs to the picker.
    if scroll_pick_mode {
        return Some(PanelButtonSet::ScrollPick);
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

/// Whether the per-monitor debug panel is visible. Shown on **every**
/// monitor when the `D`-key toggle is on, so it needs no monitor at all.
///
/// Both debug rules take loose arguments rather than a `UiSharedState`
/// because the panels are now built on the app thread, from the
/// `InteractionState` that the broadcast is about to be made out of — the
/// state itself does not exist yet at that point.
pub fn debug_monitor_visibility(overlays_visible: bool, debug_visible: bool) -> bool {
    overlays_visible && debug_visible
}

/// Whether the primary (cursor-follow) debug panel is visible on the
/// monitor spanning `bounds`. Shown on exactly one monitor — the one
/// containing the virtual cursor — or on none when the cursor sits in a gap
/// between monitors.
pub fn debug_primary_visibility(overlays_visible: bool, debug_visible: bool, cursor: ScreenPointF, bounds: ScreenRect) -> bool {
    if !overlays_visible || !debug_visible {
        return false;
    }
    let cx = cursor.x.round() as i32;
    let cy = cursor.y.round() as i32;
    cx >= bounds.left() && cx < bounds.right() && cy >= bounds.top() && cy < bounds.bottom()
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
    fn panel_only_after_capture() {
        assert_eq!(active_panel_set(false, false, &OcrState::Idle), None);
        assert_eq!(active_panel_set(true, false, &OcrState::Idle), Some(PanelButtonSet::Normal));
    }

    #[test]
    fn scroll_pick_shows_its_own_strip() {
        assert_eq!(active_panel_set(true, true, &OcrState::Idle), Some(PanelButtonSet::ScrollPick));
        assert_eq!(active_panel_set(true, false, &OcrState::Idle), Some(PanelButtonSet::Normal));
        assert_eq!(active_panel_set(false, true, &OcrState::Idle), None, "no selection, no strip");
    }

    /// The panel's OCR lifecycle: HIDDEN while the sweep loops (nothing to
    /// act on — buttons that no-op read as broken), the OCR strip once the
    /// outcome is lifted. Both click routing and drawing flow through this
    /// one function, so this test pins the behavior for both.
    #[test]
    fn panel_hidden_while_scanning_shows_ocr_set_when_lifted() {
        let region = ScreenRect::from_xy_size(20, 20, 80, 40);
        let scanning = OcrState::Scanning {
            anchor: Instant::now(),
            req: 1,
            region,
        };
        assert_eq!(active_panel_set(true, false, &scanning), None);

        let lifted = OcrState::Lifted {
            anchor: Instant::now(),
            req: 1,
            region,
            dpi_scale: 1.0,
            outcome: dummy_outcome(),
        };
        assert_eq!(active_panel_set(true, false, &lifted), Some(PanelButtonSet::Ocr));
    }

    /// BACK must hand the familiar buttons back immediately; the retract
    /// animation is cosmetic and must not hold the OCR strip on screen.
    #[test]
    fn panel_shows_normal_set_while_retracting() {
        let retracting = OcrState::Retracting {
            anchor: Instant::now(),
        };
        assert_eq!(active_panel_set(true, false, &retracting), Some(PanelButtonSet::Normal));
    }

    /// Scroll picking outranks OCR mode: its strip wins whatever the OCR
    /// state says. (Unreachable today — the two modes cannot both be
    /// engaged — but the ordering is what makes that true.)
    #[test]
    fn scroll_pick_outranks_ocr_mode() {
        assert_eq!(active_panel_set(false, false, &OcrState::Idle), None);
        assert_eq!(active_panel_set(true, false, &OcrState::Idle), Some(PanelButtonSet::Normal));
        let lifted = OcrState::Lifted {
            anchor: Instant::now(),
            req: 1,
            region: ScreenRect::from_xy_size(0, 0, 10, 10),
            dpi_scale: 1.0,
            outcome: dummy_outcome(),
        };
        assert_eq!(active_panel_set(true, true, &lifted), Some(PanelButtonSet::ScrollPick));
        assert_eq!(active_panel_set(false, false, &lifted), None);
    }

    /// Negative-origin virtual desktops (a monitor left of the primary)
    /// are the case the offset math historically gets wrong. Moved here
    /// with the rule itself, which every straddling overlay now shares.
    #[test]
    fn aabb_intersects_negative_coordinates() {
        let a = ScreenRectF::from_exact(-1920.0, 0.0, -1820.0, 50.0);
        assert!(aabb_intersects(a, ScreenRectF::from_exact(-1920.0, 0.0, 0.0, 1080.0)));
        assert!(!aabb_intersects(a, ScreenRectF::from_exact(0.0, 0.0, 1920.0, 1080.0)));
        // Touching edges do not count — the neighbouring monitor draws it.
        assert!(!aabb_intersects(
            ScreenRectF::from_exact(0.0, 0.0, 10.0, 10.0),
            ScreenRectF::from_exact(10.0, 0.0, 20.0, 10.0)
        ));
    }

    #[test]
    fn debug_visibility_respects_overlays_and_cursor_monitor() {
        let b = monitor().bounds;
        let inside = ScreenPointF::new(30.0, 30.0);
        let elsewhere = ScreenPointF::new(300.0, 30.0);

        assert!(debug_monitor_visibility(true, true));
        assert!(debug_primary_visibility(true, true, inside, b));
        assert!(!debug_primary_visibility(true, true, elsewhere, b));
        assert!(!debug_monitor_visibility(false, true));
        assert!(!debug_primary_visibility(false, true, inside, b));
        assert!(!debug_monitor_visibility(true, false));
        assert!(!debug_primary_visibility(true, false, inside, b));
    }
}
