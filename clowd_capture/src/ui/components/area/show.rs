//! The "W x H" pill drawn inside the selection while it is being dragged.
//!
//! Ported from the old GPU renderer: the same font size, padding, border
//! and placement, now measured and painted by egui. The pill is centred on
//! the bottom edge of the part of the selection that falls on THIS monitor,
//! pushed out by the magnifier's zoom about the cursor, and clamped so it
//! never leaves the screen.

use egui::{pos2, vec2, Color32, FontId, Painter, Pos2, Rect, Stroke, StrokeKind, Vec2};

use crate::selection::intersect_rects;
use crate::ui::components::{InputCtx, Local};
use crate::ui::fonts;
use crate::ui::shared::UiMonitor;

/// One host's copy of the indicator, already in that monitor's points.
#[derive(Clone, Copy, PartialEq)]
pub struct AreaInputs {
    /// The UNCLIPPED selection size: the label prints this, so a rect
    /// straddling two monitors keeps reporting its true dimensions.
    pub size: (i32, i32),
    /// The selection intersected with this monitor, in local points: the
    /// pill sits on its bottom edge.
    pub clipped: Rect,
    pub zoom: f32,
    /// The cursor in local points — the pivot the magnifier zooms about.
    pub cursor: Pos2,
    pub accent: Color32,
}

pub const FONT_PT: f32 = 14.0;
pub const PADDING: f32 = 10.0;
pub const BORDER_W: f32 = 2.0;

/// Whether this monitor draws the indicator, and what it draws.
///
/// The rule is the old `area_indicator_visibility` plus the `dragging`
/// gate that used to live in the GPU renderer: the pill belongs to the
/// host holding the cursor, only while an uncaptured selection is actively
/// being dragged out and the overlays are on.
pub fn inputs(index: usize, monitor: &UiMonitor, c: &InputCtx<'_>) -> Option<AreaInputs> {
    let i = c.input;
    if !(i.overlays_visible && !i.captured && i.dragging) || c.cursor_index != Some(index) {
        return None;
    }
    let sel = i.selection?;
    let clipped = intersect_rects(monitor.bounds, sel)?;
    let local = Local::of(monitor);
    Some(AreaInputs {
        size: (sel.width(), sel.height()),
        clipped: local.rect(clipped.to_f32()),
        zoom: i.zoom,
        cursor: local.pos(i.virtual_cursor.x, i.virtual_cursor.y),
        accent: c.accent,
    })
}

/// Where the pill goes, or `None` when the (zoomed) clipped selection is
/// too small to hold it — the old renderer's "only draw if it fits" rule.
///
/// Pure so the clamping and the zoom pivot are testable without a pass;
/// `screen` is the host's own screen rect, whose origin is the monitor.
pub fn pill_rect(clipped: Rect, text_size: Vec2, zoom: f32, cursor: Pos2, screen: Rect) -> Option<Rect> {
    let w = text_size.x + 2.0 * PADDING;
    let h = text_size.y + PADDING;
    if clipped.width() * zoom <= w + PADDING || clipped.height() * zoom <= h + PADDING {
        return None;
    }
    let mut anchor = pos2(clipped.center().x, clipped.bottom());
    if zoom > 1.0 {
        // The magnifier scales the desktop about the cursor, so the pill's
        // anchor has to travel with the selection edge it sits on.
        anchor = cursor + (anchor - cursor) * zoom;
    }
    let x = (anchor.x - w / 2.0).clamp(0.0, (screen.width() - w).max(0.0));
    let y = (anchor.y - PADDING / 2.0 - h).clamp(0.0, (screen.height() - h).max(0.0));
    Some(Rect::from_min_size(pos2(x, y), vec2(w, h)))
}

pub fn show(p: &Painter, a: &AreaInputs, screen: Rect) {
    let galley = p.layout_no_wrap(
        format!("{} \u{00D7} {}", a.size.0, a.size.1),
        FontId::new(FONT_PT, fonts::MONO_BOLD.clone()),
        Color32::BLACK,
    );
    let Some(rect) = pill_rect(a.clipped, galley.size(), a.zoom, a.cursor, screen) else {
        return;
    };
    p.rect(
        rect,
        rect.height() / 2.0,
        Color32::WHITE,
        Stroke::new(BORDER_W, a.accent),
        StrokeKind::Inside,
    );
    p.galley(rect.min + vec2(PADDING, PADDING / 2.0), galley, Color32::BLACK);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::interaction::InteractionState;
    use crate::ui::shared::monitor_index_at;
    use clowd_rust_core::geometry::{RectExt, ScreenPointF, ScreenRect};

    fn screen() -> Rect {
        Rect::from_min_size(Pos2::ZERO, vec2(1920.0, 1080.0))
    }

    /// A label the size a four-digit "1920 x 1080" comes out at, so the
    /// pill is 100 x 27 points.
    fn text() -> Vec2 {
        vec2(80.0, 17.0)
    }

    /// The mixed pair every straddling rule is checked against: the
    /// primary at 100 % and its right-hand neighbour at 150 %.
    fn monitors() -> [UiMonitor; 2] {
        [
            UiMonitor {
                bounds: ScreenRect::from_xy_size(0, 0, 1920, 1080),
                dpi_scale: 1.0,
                is_primary: true,
            },
            UiMonitor {
                bounds: ScreenRect::from_xy_size(1920, 0, 1920, 1080),
                dpi_scale: 1.5,
                is_primary: false,
            },
        ]
    }

    fn ctx<'a>(input: &'a InteractionState, monitors: &'a [UiMonitor]) -> InputCtx<'a> {
        InputCtx {
            input,
            monitors,
            cursor_index: monitor_index_at(monitors, input.virtual_cursor),
            accent: Color32::RED,
            hovered_window_title: None,
            hovered_monitor_name: None,
            hovered_pixel_bgra: None,
            cursor_image_rect: None,
            cursor_overlay_visible: true,
        }
    }

    /// A drag that runs off a monitor's edge must not push the pill off
    /// with it. Only the magnifier can move the anchor that far, so the
    /// clamp is exercised through the zoom pivot, once per edge.
    #[test]
    fn pill_rect_clamps_at_every_screen_edge() {
        let s = screen();
        let clipped = Rect::from_min_size(pos2(200.0, 200.0), vec2(1400.0, 700.0));
        let (w, h) = (text().x + 2.0 * PADDING, text().y + PADDING);

        // Zoomed about the top-left corner: the anchor flies past the
        // right and bottom edges.
        let far = pill_rect(clipped, text(), 6.0, pos2(0.0, 0.0), s).expect("the zoomed selection is huge");
        assert_eq!(far.min.x, s.width() - w);
        assert_eq!(far.min.y, s.height() - h);

        // Zoomed about the bottom-right corner: it flies past the top-left.
        let near = pill_rect(clipped, text(), 6.0, pos2(1920.0, 1080.0), s).expect("the zoomed selection is huge");
        assert_eq!(near.min, Pos2::ZERO);
    }

    /// The old renderer drew nothing rather than a pill wider than the
    /// rect it labels; the fit test is on both axes and follows the zoom.
    #[test]
    fn pill_rect_is_none_when_the_selection_is_too_small() {
        let s = screen();
        let (w, h) = (text().x + 2.0 * PADDING, text().y + PADDING);

        // Exactly the pill plus its margin on either axis is still a miss.
        let narrow = Rect::from_min_size(pos2(100.0, 100.0), vec2(w + PADDING, 400.0));
        assert!(pill_rect(narrow, text(), 1.0, Pos2::ZERO, s).is_none());
        let short = Rect::from_min_size(pos2(100.0, 100.0), vec2(600.0, h + PADDING));
        assert!(pill_rect(short, text(), 1.0, Pos2::ZERO, s).is_none());

        // A hair more on both and it fits.
        let fits = Rect::from_min_size(pos2(100.0, 100.0), vec2(w + PADDING + 1.0, h + PADDING + 1.0));
        assert!(pill_rect(fits, text(), 1.0, Pos2::ZERO, s).is_some());

        // Too small at 1x, big enough once the magnifier blows it up.
        let tiny = Rect::from_min_size(pos2(100.0, 100.0), vec2(w, h));
        assert!(pill_rect(tiny, text(), 1.0, Pos2::ZERO, s).is_none());
        assert!(pill_rect(tiny, text(), 4.0, pos2(100.0, 100.0), s).is_some());
    }

    /// Zoom scales the anchor about the cursor, not about the monitor
    /// origin: a cursor sitting on the anchor leaves it exactly where it
    /// was, and every other cursor moves it by the pivot's own formula.
    #[test]
    fn zoom_pivots_the_anchor_around_the_cursor() {
        let s = screen();
        let clipped = Rect::from_min_size(pos2(400.0, 300.0), vec2(600.0, 400.0));
        let anchor = pos2(clipped.center().x, clipped.bottom());

        let unzoomed = pill_rect(clipped, text(), 1.0, pos2(10.0, 10.0), s).expect("fits");
        let pinned = pill_rect(clipped, text(), 2.0, anchor, s).expect("fits");
        assert_eq!(pinned.min, unzoomed.min);

        let cursor = pos2(500.0, 400.0);
        let zoomed = pill_rect(clipped, text(), 2.0, cursor, s).expect("fits");
        let moved = cursor + (anchor - cursor) * 2.0;
        assert_eq!(zoomed.min.x, moved.x - zoomed.width() / 2.0);
        assert_eq!(zoomed.min.y, moved.y - PADDING / 2.0 - zoomed.height());
    }

    /// The old `shared::area_indicator_visibility` rule, now with the
    /// `dragging` gate the GPU renderer used to apply separately, and with
    /// the host question answered: the cursor's monitor draws it, alone.
    #[test]
    fn area_indicator_visible_only_for_uncaptured_selection_being_dragged() {
        let m = monitors();
        let mut input = InteractionState::new();
        input.virtual_cursor = ScreenPointF::new(300.0, 300.0);
        // Straddles the seam, so the clip and the printed size differ.
        input.selection = Some(ScreenRect::from_xy_size(1800, 100, 400, 600));
        input.dragging = true;

        let shown = inputs(0, &m[0], &ctx(&input, &m)).expect("the cursor is on the primary");
        assert_eq!(shown.size, (400, 600), "the label prints the unclipped selection");
        assert_eq!(shown.clipped, Rect::from_min_max(pos2(1800.0, 100.0), pos2(1920.0, 700.0)));
        assert!(inputs(1, &m[1], &ctx(&input, &m)).is_none(), "only the cursor's host draws it");

        input.captured = true;
        assert!(
            inputs(0, &m[0], &ctx(&input, &m)).is_none(),
            "a captured selection is done being sized"
        );
        input.captured = false;

        input.dragging = false;
        assert!(
            inputs(0, &m[0], &ctx(&input, &m)).is_none(),
            "a hovered window target is not a drag"
        );
        input.dragging = true;

        input.overlays_visible = false;
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none(), "Q hides it");
        input.overlays_visible = true;

        input.selection = None;
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none(), "nothing to measure");

        // A selection entirely on the neighbour, with the cursor still
        // here, leaves this host nothing to clip.
        input.selection = Some(ScreenRect::from_xy_size(2000, 100, 400, 600));
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none());
    }
}
