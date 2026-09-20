//! The Tips & Hotkeys panel, measured and painted by egui.
//!
//! The rule that decides which host draws it (the cursor's monitor, in
//! the tips display mode, while nothing is captured), the pure layout the
//! old `tips/layout.rs` held, and the painting. The layout is computed
//! from galleys measured in the same pass rather than by an `egui::Area`,
//! because the bottom-right/bottom-left corner rule needs the panel's own
//! size and an `Area` only learns its rect one pass late.

use egui::{pos2, vec2, Color32, FontFamily, FontId, Painter, Pos2, Rect, Stroke, StrokeKind, Vec2};

use crate::ui::components::tips::model;
use crate::ui::components::{InputCtx, Local};
use crate::ui::fonts;
use crate::ui::shared::UiMonitor;

/// One host's copy of the panel: every row already rendered to a string,
/// so the pass only has to measure and paint.
#[derive(Clone, PartialEq)]
pub struct TipsInputs {
    /// The four rows above the colour sampler, `"<hotkey><gap><text>"`.
    pub top: [String; 4],
    /// The four rows below it.
    pub bottom: [String; 4],
    /// `"#RRGGBB"`, or `"#------"` when there is no pixel under the cursor.
    pub hex: String,
    /// `"rgb(r, g, b)"`, or `None` in the same case — the second line of
    /// the colour row is simply absent then, as it was before.
    pub rgb: Option<String>,
    /// The sampled pixel, or black.
    pub swatch: Color32,
    /// The cursor in local points: the panel jumps to the other corner
    /// when it would otherwise sit under it.
    pub cursor: Pos2,
    pub accent: Color32,
}

/// Title font size in points. `DxScreenCapture.cpp:435`.
pub const TITLE_PT: f32 = 14.0;
/// Body font size in points. `DxScreenCapture.cpp:436`.
pub const BODY_PT: f32 = 12.0;
/// Distance from the screen edge. `DEBUGBOX_MARGIN` in `pch.h:53`.
pub const SCREEN_MARGIN: f32 = 50.0;
/// Half the inner padding. `paddingHalf` in `DxScreenCapture.cpp:768`.
pub const PADDING_HALF: f32 = 10.0;
/// Full inner padding.
pub const PADDING: f32 = PADDING_HALF * 2.0;
/// Width floor. `DxScreenCapture.cpp:771`.
pub const MIN_PANEL_WIDTH: f32 = 400.0;
/// Body and title bar opacity; the shadow strips keep their own alpha.
pub const OPACITY: f32 = 0.70;
/// The hard-edged drop shadow, black at the old 0.30.
pub const SHADOW: Color32 = Color32::from_black_alpha(77);
/// One body row's height — the old `body_row_height` of `font * 1.4`.
pub const ROW_H: f32 = BODY_PT * 1.4;
/// Cap height of the title, which is what the title bar is sized around.
/// The old code fed swash's `measure_line("Hg").height` here; for Cascadia
/// that is about 0.7 x the font size.
pub const TITLE_H: f32 = TITLE_PT * 0.7;

/// Whether this monitor draws the panel, and what it draws.
///
/// The rule is the old `shared::tips_visibility`: the host holding the
/// cursor shows it, in the tips display mode, while the overlays are on
/// and nothing is captured, the mouse is up and the debug panels are down.
pub fn inputs(index: usize, monitor: &UiMonitor, c: &InputCtx<'_>) -> Option<TipsInputs> {
    let i = c.input;
    let shown = i.overlays_visible && !i.captured && !i.mouse_down && i.tips_mode.show_tips_panel() && !i.debug_visible;
    if !shown || c.cursor_index != Some(index) {
        return None;
    }
    let row = |r: &model::TipRow| {
        format!(
            "{}{}{}",
            r.hotkey,
            model::HOTKEY_GAP,
            model::render_description(r.description_template, c.hovered_window_title, c.hovered_monitor_name)
        )
    };
    let (hex, rgb, swatch) = match c.hovered_pixel_bgra {
        Some([b, g, r, _]) => (
            format!("#{r:02X}{g:02X}{b:02X}"),
            Some(format!("rgb({r}, {g}, {b})")),
            Color32::from_rgb(r, g, b),
        ),
        None => ("#------".to_owned(), None, Color32::BLACK),
    };
    let local = Local::of(monitor);
    Some(TipsInputs {
        top: std::array::from_fn(|k| row(&model::TIPS_TOP[k])),
        bottom: std::array::from_fn(|k| row(&model::TIPS_BOTTOM[k])),
        hex,
        rgb,
        swatch,
        cursor: local.pos(i.virtual_cursor.x, i.virtual_cursor.y),
        accent: c.accent,
    })
}

/// Where the panel and everything inside it goes, all in this host's
/// points. Rects and positions are absolute; the hotkey column offset is
/// panel-local, as it was in the old layout.
pub struct TipsLayout {
    pub panel: Rect,
    pub title_bar: Rect,
    pub col_hotkey_x: f32,
    pub top_block_y: f32,
    pub color_row_y: f32,
    pub bottom_block_y: f32,
    pub swatch: Rect,
    pub hex_pos: Pos2,
    pub rgb_pos: Pos2,
}

/// The old `tips::layout::compute_layout` with the DPI scaling gone: egui
/// works in points and rounds to pixels itself.
///
/// `longest_body` is the widest measured body row (the colour row
/// included) and `title_w` the measured title, both in points.
pub fn compute_layout(screen: Rect, cursor: Pos2, longest_body: f32, title_w: f32) -> TipsLayout {
    let panel_w = (longest_body.max(title_w) + 2.0 * PADDING).max(MIN_PANEL_WIDTH);
    // Breathing room above and below the two-line colour row, so the
    // swatch does not butt up against the text rows either side of it.
    let gap = PADDING_HALF * 0.4;
    // Four rows, the two-line colour row, four more rows.
    let body_h = ROW_H * 10.0 + 2.0 * PADDING + 2.0 * gap;
    let title_h = TITLE_H + PADDING;
    let panel_h = title_h + body_h;

    // Bottom-right by default, bottom-left when the cursor sits in the
    // zone the panel would otherwise cover (`DxScreenCapture.cpp:775-779`).
    let right_left = screen.right() - SCREEN_MARGIN - panel_w;
    let top = screen.bottom() - SCREEN_MARGIN - panel_h;
    let left = if cursor.x > right_left - 2.0 * SCREEN_MARGIN && cursor.y > top - 2.0 * SCREEN_MARGIN {
        screen.left() + SCREEN_MARGIN
    } else {
        right_left
    };
    let panel = Rect::from_min_size(pos2(left, top), vec2(panel_w, panel_h));

    // The description column lines up with where the description text
    // starts in the monospace rows: the hotkey plus the three-space gap
    // is about 1.75 row heights at Cascadia 12 pt.
    let col_desc_x = PADDING + ROW_H * 1.75;
    let top_block_y = title_h + PADDING;
    let color_row_y = top_block_y + ROW_H * 4.0 + gap;
    let bottom_block_y = color_row_y + ROW_H * 2.0 + gap;
    let box_size = ROW_H * 2.0;
    TipsLayout {
        panel,
        title_bar: Rect::from_min_size(panel.min, vec2(panel_w, title_h)),
        col_hotkey_x: PADDING,
        top_block_y,
        color_row_y,
        bottom_block_y,
        swatch: Rect::from_min_size(panel.min + vec2(col_desc_x, color_row_y), Vec2::splat(box_size)),
        hex_pos: panel.min + vec2(col_desc_x + box_size + PADDING_HALF, color_row_y),
        rgb_pos: panel.min + vec2(col_desc_x + box_size + PADDING_HALF, color_row_y + ROW_H),
    }
}

pub fn show(p: &Painter, t: &TipsInputs, screen: Rect) {
    let body = |s: &str| p.layout_no_wrap(s.to_owned(), FontId::new(BODY_PT, FontFamily::Monospace), Color32::BLACK);
    let title = p.layout_no_wrap(
        model::TITLE.to_owned(),
        FontId::new(TITLE_PT, fonts::MONO_BOLD.clone()),
        Color32::WHITE,
    );
    let rows: Vec<_> = t
        .top
        .iter()
        .chain(t.bottom.iter())
        .map(|s| body(s))
        .collect();
    let (hotkey, hex, rgb) = (body(model::COLOR_ROW_HOTKEY), body(&t.hex), t.rgb.as_deref().map(body));
    // The colour row has to fit too: its hotkey column, the swatch, and
    // the wider of the two colour lines.
    let colour_row_w = hotkey.size().x
        + BODY_PT * 2.4
        + hex
            .size()
            .x
            .max(rgb.as_ref().map_or(0.0, |g| g.size().x))
        + BODY_PT * 0.5;
    let longest = rows
        .iter()
        .map(|g| g.size().x)
        .fold(colour_row_w, f32::max);
    let l = compute_layout(screen, t.cursor, longest, title.size().x);

    let pr = l.panel;
    let s = PADDING_HALF;
    // Two literal strips rather than one offset rect: the body is 70 %
    // translucent, so a full shadow rect underneath would tint it. The
    // right strip owns the corner; the bottom one stops where it starts.
    p.rect_filled(
        Rect::from_min_max(pos2(pr.right(), pr.top() + s), pos2(pr.right() + s, pr.bottom() + s)),
        0u8,
        SHADOW,
    );
    p.rect_filled(
        Rect::from_min_max(pos2(pr.left() + s, pr.bottom()), pos2(pr.right(), pr.bottom() + s)),
        0u8,
        SHADOW,
    );
    p.rect_filled(
        Rect::from_min_max(pos2(pr.left(), l.title_bar.bottom()), pr.max),
        0u8,
        Color32::WHITE.gamma_multiply(OPACITY),
    );
    p.rect_filled(l.title_bar, 0u8, t.accent.gamma_multiply(OPACITY));
    p.galley(l.title_bar.center() - title.size() / 2.0, title, Color32::WHITE);

    let x = pr.left() + l.col_hotkey_x;
    for (k, g) in rows.iter().take(4).enumerate() {
        p.galley(pos2(x, pr.top() + l.top_block_y + k as f32 * ROW_H), g.clone(), Color32::BLACK);
    }
    p.galley(pos2(x, pr.top() + l.color_row_y), hotkey, Color32::BLACK);
    p.rect(l.swatch, 0u8, t.swatch, Stroke::new(1.0, Color32::BLACK), StrokeKind::Inside);
    p.galley(l.hex_pos, hex, Color32::BLACK);
    if let Some(g) = rgb {
        p.galley(l.rgb_pos, g, Color32::BLACK);
    }
    for (k, g) in rows.iter().skip(4).enumerate() {
        p.galley(pos2(x, pr.top() + l.bottom_block_y + k as f32 * ROW_H), g.clone(), Color32::BLACK);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::interaction::InteractionState;
    use crate::settings::TipsMode;
    use crate::ui::shared::monitor_index_at;
    use clowd_rust_core::geometry::{RectExt, ScreenPointF, ScreenRect};

    fn screen() -> Rect {
        Rect::from_min_size(Pos2::ZERO, vec2(1920.0, 1080.0))
    }

    /// The mixed pair every per-host rule is checked against: the primary
    /// at 100 % and its right-hand neighbour at 150 %.
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
            hovered_window_title: Some("Notepad"),
            hovered_monitor_name: Some("DELL U2723"),
            hovered_pixel_bgra: Some([0x10, 0x20, 0x30, 0xFF]),
            cursor_image_rect: None,
            cursor_overlay_visible: true,
        }
    }

    /// A state with the tips panel up: the mode is the one thing the
    /// neutral fixture does not already have.
    fn tips_state() -> InteractionState {
        let mut input = InteractionState::new();
        input.tips_mode = TipsMode::Tips;
        input.virtual_cursor = ScreenPointF::new(300.0, 300.0);
        input
    }

    /// The panel's home corner: inset by the margin from the bottom and
    /// the right, with the cursor far away from it.
    #[test]
    fn layout_anchors_bottom_right_at_the_margin() {
        let s = screen();
        let l = compute_layout(s, pos2(10.0, 10.0), 200.0, 100.0);
        assert_eq!(l.panel.right(), s.right() - SCREEN_MARGIN);
        assert_eq!(l.panel.bottom(), s.bottom() - SCREEN_MARGIN);
        assert!((l.title_bar.height() - (TITLE_H + PADDING)).abs() < 1e-3);
        assert_eq!(l.title_bar.width(), l.panel.width());
        // The blocks run in reading order down the body, and the swatch
        // sits on the colour row.
        assert!(l.top_block_y < l.color_row_y && l.color_row_y < l.bottom_block_y);
        assert!((l.swatch.top() - (l.panel.top() + l.color_row_y)).abs() < 1e-3);
        assert!((l.rgb_pos.y - l.hex_pos.y - ROW_H).abs() < 1e-3);
    }

    /// The cursor in the bottom-right corner zone sends the panel to the
    /// other side, so it never hides what the user is pointing at.
    #[test]
    fn layout_flips_left_when_the_cursor_is_in_the_corner_zone() {
        let s = screen();
        let home = compute_layout(s, pos2(10.0, 10.0), 200.0, 100.0).panel;
        let flipped = compute_layout(s, pos2(s.right() - 20.0, s.bottom() - 20.0), 200.0, 100.0).panel;
        assert_eq!(flipped.left(), s.left() + SCREEN_MARGIN);
        assert_eq!(flipped.width(), home.width());
        assert_eq!(flipped.top(), home.top());

        // Just outside the zone on either axis keeps the home corner.
        let x_only = compute_layout(s, pos2(s.right() - 20.0, 10.0), 200.0, 100.0).panel;
        assert_eq!(x_only.left(), home.left());
        let y_only = compute_layout(s, pos2(10.0, s.bottom() - 20.0), 200.0, 100.0).panel;
        assert_eq!(y_only.left(), home.left());
    }

    /// Short rows never shrink the panel below the floor; long ones grow
    /// it by the padding on both sides.
    #[test]
    fn layout_width_is_at_least_the_minimum() {
        let s = screen();
        assert_eq!(
            compute_layout(s, Pos2::ZERO, 10.0, 10.0)
                .panel
                .width(),
            MIN_PANEL_WIDTH
        );
        let wide = compute_layout(s, Pos2::ZERO, 600.0, 100.0)
            .panel
            .width();
        assert_eq!(wide, 600.0 + 2.0 * PADDING);
        // The title can be the widest thing on the panel.
        let by_title = compute_layout(s, Pos2::ZERO, 100.0, 700.0)
            .panel
            .width();
        assert_eq!(by_title, 700.0 + 2.0 * PADDING);
    }

    /// `TipsInputs` carries the two blocks as fixed four-row arrays, which
    /// is only correct while the tables hold four rows each — and the body
    /// height in `compute_layout` counts on the same ten lines.
    #[test]
    fn tips_top_and_bottom_hold_four_rows_each() {
        assert_eq!(model::TIPS_TOP.len(), 4);
        assert_eq!(model::TIPS_BOTTOM.len(), 4);
    }

    /// The old `shared::tips_visibility` rule, now with the host question
    /// answered: the cursor's monitor draws it, alone.
    #[test]
    fn tips_hide_during_capture_mouse_down_or_debug() {
        let m = monitors();
        let mut input = tips_state();

        let shown = inputs(0, &m[0], &ctx(&input, &m)).expect("the cursor is on the primary");
        assert_eq!(shown.cursor, pos2(300.0, 300.0));
        assert_eq!(shown.hex, "#302010");
        assert_eq!(shown.rgb.as_deref(), Some("rgb(48, 32, 16)"));
        assert_eq!(shown.swatch, Color32::from_rgb(0x30, 0x20, 0x10));
        assert!(shown.top[1].contains("Notepad"), "{}", shown.top[1]);
        assert!(shown.top[2].contains("DELL U2723"), "{}", shown.top[2]);
        assert!(inputs(1, &m[1], &ctx(&input, &m)).is_none(), "only the cursor's host draws it");

        input.mouse_down = true;
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none());
        input.mouse_down = false;

        input.debug_visible = true;
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none());
        input.debug_visible = false;

        input.captured = true;
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none());
        input.captured = false;

        input.overlays_visible = false;
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none(), "Q hides it");
        input.overlays_visible = true;

        for mode in [TipsMode::Hints, TipsMode::Off] {
            input.tips_mode = mode;
            assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none(), "{mode:?} has no tips panel");
        }
        input.tips_mode = TipsMode::Tips;

        // On the 150 % neighbour the panel is that host's, and the cursor
        // arrives in its own points.
        input.virtual_cursor = ScreenPointF::new(2520.0, 300.0);
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none());
        let far = inputs(1, &m[1], &ctx(&input, &m)).expect("the cursor moved to the neighbour");
        assert_eq!(far.cursor, pos2(400.0, 200.0));

        // No pixel under the cursor: the placeholder hex and no rgb line.
        input.virtual_cursor = ScreenPointF::new(300.0, 300.0);
        let mut c = ctx(&input, &m);
        c.hovered_pixel_bgra = None;
        let blank = inputs(0, &m[0], &c).expect("still shown");
        assert_eq!(blank.hex, "#------");
        assert!(blank.rgb.is_none());
        assert_eq!(blank.swatch, Color32::BLACK);
    }
}
