//! Where each hint chip goes, as pure functions of its size and the
//! screen it lives on.
//!
//! Ported from the old `hints/layout.rs` with the DPI factors dropped:
//! every value here is in this monitor's points, and the chips are only
//! ever placed on one monitor at a time, so the old pre-DPI constants are
//! used literally.
//!
//! The rules themselves are unchanged. A chip offset from the cursor
//! flips to the other side rather than running off the edge; a chip
//! anchored to the monitor is centred and inset; every chip but the
//! scroll-pick instruction then steps out of the way of the chips already
//! placed this pass.

use egui::{pos2, vec2, Pos2, Rect, Vec2};

use crate::ui::components::hints::show::Anchor;
use crate::ui::components::pill;

/// Diagonal offset of the `[H]` chip from the crosshair.
pub const CROSSHAIR_OFFSET: f32 = 18.0;
/// Inset of a monitor-anchored chip from the top or bottom edge.
pub const MONITOR_INSET_Y: f32 = 40.0;
/// Gap between the dashed cursor square and the `[M]` chip.
pub const CURSOR_OFFSET_Y: f32 = 6.0;
/// Padding around the cursor image for the dashed highlight square.
pub const CURSOR_SQUARE_PAD: f32 = 4.0;
/// Gap left when a chip is pushed clear of one already placed.
pub const OVERLAP_GAP: f32 = 4.0;
/// Gap between the selection's top edge and the notice pill below it.
pub const NOTICE_GAP_Y: f32 = 8.0;
/// Diagonal offset of the scroll-pick instruction from the cursor.
/// Derived from the reticle's own extent so the chip's near corner always
/// clears the outer ticks — the two move together if it is ever resized.
pub const SCOPE_HINT_OFFSET: f32 = crate::ui::components::scope::layout::SCOPE_EXTENT * 0.8;

/// The chip that holds `text`, with or without a keycap. The keycap and
/// its gap collapse to nothing for a chip that describes an action with no
/// key behind it.
pub fn chip_size(text: Vec2, with_keycap: bool) -> Vec2 {
    let (cap, gap) = if with_keycap {
        (pill::KEYCAP, pill::KEYCAP_GAP)
    } else {
        (0.0, 0.0)
    };
    vec2(
        2.0 * pill::PAD_H + cap + gap + text.x,
        2.0 * pill::PAD_V + if with_keycap { cap.max(text.y) } else { text.y },
    )
}

/// Pull a chip fully inside the screen without resizing it. A chip taller
/// or wider than the screen is pinned to the top-left rather than pushed
/// off the other side.
fn clamp_into(r: Rect, screen: Rect) -> Rect {
    let x = r
        .left()
        .clamp(screen.left(), (screen.right() - r.width()).max(screen.left()));
    let y = r
        .top()
        .clamp(screen.top(), (screen.bottom() - r.height()).max(screen.top()));
    Rect::from_min_size(pos2(x, y), r.size())
}

/// Step a chip clear of the ones already placed: below the chip it hits,
/// or above it when below would leave the screen.
pub fn avoid_overlaps(mut r: Rect, placed: &[Rect], screen: Rect) -> Rect {
    for e in placed {
        if r.intersects(*e) {
            let mut y = e.bottom() + OVERLAP_GAP;
            if y + r.height() > screen.bottom() {
                y = e.top() - r.height() - OVERLAP_GAP;
            }
            r = clamp_into(Rect::from_min_size(pos2(r.left(), y), r.size()), screen);
        }
    }
    r
}

/// `[H]` Select Colour — follows the crosshair, flipping to its other side
/// when it would run past the right or bottom edge.
pub fn color_hint(cursor: Pos2, size: Vec2, screen: Rect, placed: &[Rect]) -> Rect {
    offset_from_cursor(cursor, size, screen, CROSSHAIR_OFFSET, placed)
}

/// The scroll-point picker's instruction. Same flip-and-clamp rule as the
/// colour chip, further out to clear the reticle, and deliberately NOT
/// avoiding the other chips: it is the only one on screen while picking.
pub fn scroll_pick_hint(cursor: Pos2, size: Vec2, screen: Rect) -> Rect {
    offset_from_cursor(cursor, size, screen, SCOPE_HINT_OFFSET, &[])
}

/// Shared body of the two cursor-following chips.
fn offset_from_cursor(cursor: Pos2, size: Vec2, screen: Rect, offset: f32, placed: &[Rect]) -> Rect {
    let mut x = cursor.x + offset;
    let mut y = cursor.y + offset;
    if x + size.x > screen.right() {
        x = cursor.x - offset - size.x;
    }
    if y + size.y > screen.bottom() {
        y = cursor.y - offset - size.y;
    }
    let r = clamp_into(Rect::from_min_size(pos2(x, y), size), screen);
    avoid_overlaps(r, placed, screen)
}

/// A chip anchored to the bottom edge, horizontally centred: `[Q]` and the
/// scroll-to-zoom prompt.
pub fn monitor_hint_bottom(size: Vec2, screen: Rect, placed: &[Rect]) -> Rect {
    let r = Rect::from_min_size(
        pos2(screen.center().x - size.x / 2.0, screen.bottom() - MONITOR_INSET_Y - size.y),
        size,
    );
    avoid_overlaps(clamp_into(r, screen), placed, screen)
}

/// The same, against the top edge: `[F]`.
pub fn monitor_hint_top(size: Vec2, screen: Rect, placed: &[Rect]) -> Rect {
    let r = Rect::from_min_size(pos2(screen.center().x - size.x / 2.0, screen.top() + MONITOR_INSET_Y), size);
    avoid_overlaps(clamp_into(r, screen), placed, screen)
}

/// `[M]` Toggle Cursor — under the cursor image, or above it when below
/// would leave the selection. Kept inside the selection first and the
/// screen second, so a selection at the very edge still owns the chip.
pub fn cursor_hint(image: Rect, selection: Rect, size: Vec2, screen: Rect, placed: &[Rect]) -> Rect {
    let mut y = image.bottom() + CURSOR_SQUARE_PAD + CURSOR_OFFSET_Y;
    if y + size.y > selection.bottom() {
        y = image.top() - CURSOR_SQUARE_PAD - CURSOR_OFFSET_Y - size.y;
    }
    let r = Rect::from_min_size(pos2(image.center().x - size.x / 2.0, y), size);
    let r = clamp_into(r, selection);
    avoid_overlaps(clamp_into(r, screen), placed, screen)
}

/// The OCR notice: centred over the selection, just above its top edge,
/// clamped fully inside the screen. A selection at the very top pushes the
/// pill down over itself rather than off screen.
pub fn notice_rect(selection: Rect, size: Vec2, screen: Rect) -> Rect {
    let r = Rect::from_min_size(
        pos2(selection.center().x - size.x / 2.0, selection.top() - NOTICE_GAP_Y - size.y),
        size,
    );
    clamp_into(r, screen)
}

/// The anchor a hint carries, resolved to a rect.
pub fn place(anchor: Anchor, size: Vec2, screen: Rect, placed: &[Rect]) -> Rect {
    match anchor {
        Anchor::Cursor(c) => color_hint(c, size, screen, placed),
        Anchor::ScopeCursor(c) => scroll_pick_hint(c, size, screen),
        Anchor::MonitorBottom => monitor_hint_bottom(size, screen, placed),
        Anchor::MonitorTop => monitor_hint_top(size, screen, placed),
        Anchor::CursorImage {
            image,
            selection,
        } => cursor_hint(image, selection, size, screen, placed),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn screen() -> Rect {
        Rect::from_min_size(Pos2::ZERO, vec2(1920.0, 1080.0))
    }

    fn chip() -> Vec2 {
        vec2(120.0, 28.0)
    }

    /// The colour chip trails the crosshair down-right until that would
    /// take it off screen, then flips to the other side of it; a cursor in
    /// the very corner leaves it clamped inside.
    #[test]
    fn color_hint_flips_left_and_up_at_the_edge_and_clamps() {
        let s = screen();
        let c = chip();

        let normal = color_hint(pos2(400.0, 400.0), c, s, &[]);
        assert_eq!(normal.min, pos2(418.0, 418.0));

        let right = color_hint(pos2(1900.0, 400.0), c, s, &[]);
        assert_eq!(right.min.x, 1900.0 - CROSSHAIR_OFFSET - c.x);

        let bottom = color_hint(pos2(400.0, 1070.0), c, s, &[]);
        assert_eq!(bottom.min.y, 1070.0 - CROSSHAIR_OFFSET - c.y);

        // Top-left corner: the flip would go negative, so the clamp wins.
        let corner = color_hint(pos2(2.0, 2.0), c, s, &[]);
        assert!(corner.min.x >= 0.0 && corner.min.y >= 0.0);
        assert!(s.contains_rect(corner));
    }

    /// Same rule, further out, and it ignores whatever else was placed.
    #[test]
    fn scroll_pick_hint_flips_and_clamps() {
        let s = screen();
        let c = chip();

        let normal = scroll_pick_hint(pos2(500.0, 500.0), c, s);
        assert_eq!(normal.min, pos2(500.0 + SCOPE_HINT_OFFSET, 500.0 + SCOPE_HINT_OFFSET));

        let flipped = scroll_pick_hint(pos2(1910.0, 1075.0), c, s);
        assert_eq!(flipped.min.x, 1910.0 - SCOPE_HINT_OFFSET - c.x);
        assert_eq!(flipped.min.y, 1075.0 - SCOPE_HINT_OFFSET - c.y);
        assert!(s.contains_rect(flipped));
    }

    #[test]
    fn monitor_hints_are_centred_and_inset() {
        let s = screen();
        let c = chip();

        let bottom = monitor_hint_bottom(c, s, &[]);
        assert_eq!(bottom.center().x, s.center().x);
        assert_eq!(bottom.bottom(), s.bottom() - MONITOR_INSET_Y);

        let top = monitor_hint_top(c, s, &[]);
        assert_eq!(top.center().x, s.center().x);
        assert_eq!(top.top(), s.top() + MONITOR_INSET_Y);
    }

    /// The `[M]` chip sits under the cursor image unless that would leave
    /// the selection, in which case it goes above it.
    #[test]
    fn cursor_hint_flips_above_when_below_leaves_the_selection() {
        let s = screen();
        let c = chip();
        let selection = Rect::from_min_size(pos2(200.0, 200.0), vec2(600.0, 400.0));

        let image = Rect::from_min_size(pos2(400.0, 300.0), vec2(32.0, 32.0));
        let below = cursor_hint(image, selection, c, s, &[]);
        assert_eq!(below.top(), image.bottom() + CURSOR_SQUARE_PAD + CURSOR_OFFSET_Y);
        assert_eq!(below.center().x, image.center().x);

        // Near the selection's bottom edge there is no room below.
        let low = Rect::from_min_size(pos2(400.0, 560.0), vec2(32.0, 32.0));
        let above = cursor_hint(low, selection, c, s, &[]);
        assert_eq!(above.bottom(), low.top() - CURSOR_SQUARE_PAD - CURSOR_OFFSET_Y);
    }

    /// Two chips that want the same place: the second drops below the
    /// first, and goes above it instead when below would leave the screen.
    #[test]
    fn avoid_overlaps_moves_the_second_chip_below_then_above() {
        let s = screen();
        let c = chip();
        let first = Rect::from_min_size(pos2(400.0, 400.0), c);

        let moved = avoid_overlaps(Rect::from_min_size(pos2(410.0, 405.0), c), &[first], s);
        assert_eq!(moved.top(), first.bottom() + OVERLAP_GAP);

        // The same collision against the bottom edge goes the other way.
        let low = Rect::from_min_size(pos2(400.0, 1040.0), c);
        let lifted = avoid_overlaps(Rect::from_min_size(pos2(405.0, 1045.0), c), &[low], s);
        assert_eq!(lifted.bottom(), low.top() - OVERLAP_GAP);
    }

    /// A selection at the very top of the screen pushes the notice down
    /// over itself rather than off the edge.
    #[test]
    fn notice_rect_at_the_top_edge_stays_inside() {
        let s = screen();
        let c = chip();

        let middle = notice_rect(Rect::from_min_size(pos2(600.0, 400.0), vec2(500.0, 300.0)), c, s);
        assert_eq!(middle.bottom(), 400.0 - NOTICE_GAP_Y);
        assert_eq!(middle.center().x, 850.0);

        let top = notice_rect(Rect::from_min_size(pos2(600.0, 2.0), vec2(500.0, 300.0)), c, s);
        assert_eq!(top.top(), s.top());
        assert!(s.contains_rect(top));
    }
}
