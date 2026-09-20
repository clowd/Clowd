//! Which hint chips this monitor shows, and painting them.
//!
//! The hints themselves are decided on the app thread, as data: a key, a
//! label, the anchor the chip hangs off, and whether it wears the accent
//! comet. Measurement, placement and painting all happen inside the pass,
//! because only there can a label's width be known — which is also what
//! lets the chips step out of each other's way in the order they were
//! pushed.
//!
//! The push order below is the old renderer's early-return ladder: the
//! scroll-point picker owns the overlay alone, OCR mode suppresses
//! everything, and `[M]` is the one chip that survives a capture.

use egui::{pos2, vec2, Color32, Context, Painter, Pos2, Rect, Shape, Stroke, StrokeKind, Vec2};

use crate::interaction::{notice_alpha, OcrNotice};
use crate::ui::components::hints::{model, place, trail};
use crate::ui::components::pill;
use crate::ui::components::{InputCtx, Local};
use crate::ui::egui_host::REPAINT_FLOOR;
use crate::ui::shared::{monitor_index_at, pick_monitor_containing_center, UiMonitor};
use clowd_rust_core::geometry::{RectExt, ScreenPointF};

/// What a chip hangs off. Resolved to a rect by [`place::place`] once the
/// label has been measured.
#[derive(Clone, Copy, PartialEq)]
pub enum Anchor {
    /// Trails the crosshair: `[H]`.
    Cursor(Pos2),
    /// Trails the crosshair further out, clear of the scroll-pick
    /// reticle, and avoids nothing (it is alone on screen).
    ScopeCursor(Pos2),
    /// Bottom-centre of the monitor: `[Q]` and scroll-to-zoom.
    MonitorBottom,
    /// Top-centre of the monitor: `[F]`.
    MonitorTop,
    /// Below the cursor image, inside the selection: `[M]`.
    CursorImage { image: Rect, selection: Rect },
}

/// One chip, in this monitor's points.
#[derive(Clone, PartialEq)]
pub struct Hint {
    /// The keycap letter, or `None` for a chip with no key behind it.
    pub key: Option<&'static str>,
    pub text: String,
    pub anchor: Anchor,
    /// Wears the orbiting accent comet instead of its border.
    pub trail: bool,
    /// Constant opacity: the hidden-cursor `[M]` chip is 0.5/0.85.
    pub alpha: f32,
    /// The colour square the `[H]` chip carries after its label.
    pub swatch: Option<Color32>,
}

/// Every chip this host draws this pass, plus the dashed square that goes
/// with `[M]`.
#[derive(Clone, PartialEq, Default)]
pub struct HintsInputs {
    pub hints: Vec<Hint>,
    /// The `[M]` square in local points, padding included, and its alpha.
    /// Emitted only together with the `[M]` chip, which is what [`show`]
    /// paints it under.
    pub dashed_square: Option<(Rect, f32)>,
}

/// The transient "OCR gave you nothing" pill, on the one host that shows
/// it. Built only while it is still visible, so the memo catches the tick
/// it disappears.
#[derive(Clone, Copy, PartialEq)]
pub struct NoticeInputs {
    pub notice: OcrNotice,
    /// The selection in local points: the pill sits above its top edge.
    pub selection: Rect,
}

pub const SCROLL_PICK_TEXT: &str = "Click within the scrollable area";

/// Which chips belong on this monitor.
///
/// The rule this replaces is the old `shared::hints_visibility` plus the
/// per-chip gates that lived in the GPU renderer. Everything but `[M]`
/// belongs to the host under the cursor; `[M]` goes to the host holding
/// the cursor IMAGE's centre, which near a seam is not the same monitor.
pub fn inputs(index: usize, monitor: &UiMonitor, c: &InputCtx<'_>) -> HintsInputs {
    let i = c.input;
    let here = c.cursor_index == Some(index);
    let local = Local::of(monitor);
    let cur = local.pos(i.virtual_cursor.x, i.virtual_cursor.y);
    let mut out = HintsInputs::default();
    let hint = |key, text: String, anchor, trail| Hint {
        key,
        text,
        anchor,
        trail,
        alpha: 1.0,
        swatch: None,
    };

    // The scroll-point picker takes the whole overlay: the only input it
    // wants is one click inside the selection, so it shows a single
    // instruction and suppresses everything else — including `[M]`, which
    // is otherwise the one chip that survives a capture.
    if i.scroll_pick_mode {
        if i.overlays_visible && here {
            out.hints
                .push(hint(None, SCROLL_PICK_TEXT.into(), Anchor::ScopeCursor(cur), true));
        }
        return out;
    }

    // Nothing at all while the OCR mode is live. The app thread swallows
    // `m` in that mode, so `[M]` must not advertise a key that does
    // nothing.
    if i.ocr.active() {
        return out;
    }

    let hints_visible = i.tips_mode.show_hints() && (i.zoom > 1.0 || i.overlays_visible) && !i.captured && !i.mouse_down;
    if hints_visible && here {
        if i.zoom > 1.0 {
            // Yes, that way round: under Q the overlays are hidden and the
            // chip offers the way back in.
            let text = if i.overlays_visible { "Enter Magnifier" } else { "Exit Magnifier" };
            out.hints
                .push(hint(Some("Q"), text.into(), Anchor::MonitorBottom, !i.has_used_magnifier));
            if !i.overlays_visible {
                // Only the way out of the magnifier survives Q.
                return out;
            }
        } else if c.hovered_monitor_name.is_some() {
            out.hints.push(hint(
                Some("F"),
                model::render_hint_text(model::HINT_MONITOR.template, c.hovered_window_title, c.hovered_monitor_name),
                Anchor::MonitorTop,
                false,
            ));
        }
        let (text, swatch) = match c.hovered_pixel_bgra {
            Some([b, g, r, _]) => (format!("Select #{r:02X}{g:02X}{b:02X}"), Some(Color32::from_rgb(r, g, b))),
            None => ("Select #------".to_owned(), None),
        };
        out.hints.push(Hint {
            swatch,
            ..hint(Some("H"), text, Anchor::Cursor(cur), false)
        });
    }

    // Scroll-to-zoom deliberately ignores the tips mode: it is shown once,
    // after a slow mouse, and hidden forever on the first scroll.
    if i.show_scroll_hint && i.overlays_visible && !i.captured && !i.mouse_down && here {
        out.hints
            .push(hint(Some("\u{2195}"), "Scroll to zoom".into(), Anchor::MonitorBottom, true));
    }

    // `[M]` survives a capture, needs the cursor image fully inside the
    // selection, and goes to the host holding that image's centre.
    if i.overlays_visible && i.tips_mode.show_hints() && !i.mouse_down && i.zoom <= 1.0 {
        if let (Some(img), Some(sel)) = (c.cursor_image_rect, i.selection) {
            let sel = sel.to_f32();
            let inside = img.left() >= sel.left() && img.right() <= sel.right() && img.top() >= sel.top() && img.bottom() <= sel.bottom();
            let centre = ScreenPointF::new((img.left() + img.right()) / 2.0, (img.top() + img.bottom()) / 2.0);
            if inside && monitor_index_at(c.monitors, centre) == Some(index) {
                let alpha = if c.cursor_overlay_visible { 1.0 } else { 0.5 / 0.85 };
                let (image, selection) = (local.rect(img), local.rect(sel));
                let side = image.width().max(image.height()) / 2.0 + place::CURSOR_SQUARE_PAD;
                out.dashed_square = Some((Rect::from_center_size(image.center(), Vec2::splat(2.0 * side)), alpha));
                let text = if c.cursor_overlay_visible { "Hide Cursor" } else { "Show Cursor" };
                out.hints.push(Hint {
                    alpha,
                    ..hint(
                        Some("M"),
                        text.into(),
                        Anchor::CursorImage {
                            image,
                            selection,
                        },
                        false,
                    )
                });
            }
        }
    }
    out
}

/// One copy of the notice, on the host whose monitor holds the selection's
/// centre — the same rule that places the tray, so the pill and the button
/// that raised it share a monitor.
///
/// Deliberately not gated on the overlay toggle or the tips mode: the
/// notice is feedback for a press the user just made, and it has to reach
/// them whatever else is hidden. The one thing that does suppress it is
/// the scroll-point picker, which owns the whole overlay — with the OS
/// pointer hidden and a reticle in its place, a pill fading in beside the
/// aiming instruction would read as part of it.
pub fn notice_inputs(index: usize, monitor: &UiMonitor, c: &InputCtx<'_>) -> Option<NoticeInputs> {
    let _ = index;
    if c.input.scroll_pick_mode {
        return None;
    }
    let n = c.input.ocr_notice.filter(|n| n.visible())?;
    let sel = c.input.selection?;
    let target = pick_monitor_containing_center(c.monitors, sel)?;
    if target.bounds != monitor.bounds {
        return None;
    }
    Some(NoticeInputs {
        notice: n,
        selection: Local::of(monitor).rect(sel.to_f32()),
    })
}

/// Paint the chips, the dashed square and the notice.
///
/// The square goes in with the `[M]` chip it belongs to, which is the last
/// chip pushed: its dashes therefore cross every other chip they reach and
/// only `[M]` itself covers them, as the old renderer's push order had it.
///
/// `time` comes from this host's own context, which is legal because no
/// chip ever straddles a seam; the notice fades off its own anchor, which
/// it shares with nothing.
pub fn show(ctx: &Context, p: &Painter, h: &HintsInputs, notice: Option<&NoticeInputs>, screen: Rect, accent: Color32, ppp: f32) {
    let time = ctx.input(|i| i.time);
    let mut placed: Vec<Rect> = Vec::with_capacity(4);
    let mut animating = false;

    for hint in &h.hints {
        // The square belongs to `[M]`, and the builder only ever emits the
        // two together, so it goes in immediately under that chip.
        if matches!(hint.anchor, Anchor::CursorImage { .. }) {
            if let Some((sq, alpha)) = h.dashed_square {
                dashed_square(p, sq, alpha);
            }
        }
        let galley = p.layout_no_wrap(hint.text.clone(), pill::font(), Color32::PLACEHOLDER);
        let swatch = (pill::FONT_PT * 1.4).floor();
        let extra = if hint.swatch.is_some() { pill::SWATCH_GAP + swatch } else { 0.0 };
        let size = place::chip_size(galley.size() + vec2(extra, 0.0), hint.key.is_some());
        let rect = place::place(hint.anchor, size, screen, &placed);
        // The picker's instruction is alone on screen and must not shove
        // anything else around.
        if !matches!(hint.anchor, Anchor::ScopeCursor(_)) {
            placed.push(rect);
        }
        let a = hint.alpha;
        p.add(pill::shadow(rect, a));
        p.add(pill::body(rect, pill::RADIUS, a, !hint.trail));
        if hint.trail {
            p.add(Shape::mesh(trail::mesh(rect, pill::RADIUS, pill::BORDER_W, ppp, accent, a, time)));
            animating = true;
        }
        let mut x = rect.left() + pill::PAD_H;
        if let Some(k) = hint.key {
            pill::keycap(
                p,
                Rect::from_min_size(pos2(x, rect.center().y - pill::KEYCAP / 2.0), Vec2::splat(pill::KEYCAP)),
                k,
                a,
            );
            x += pill::KEYCAP + pill::KEYCAP_GAP;
        }
        let text_w = galley.size().x;
        let text_h = galley.size().y;
        p.galley(pos2(x, rect.center().y - text_h / 2.0), galley, pill::TEXT.gamma_multiply(a));
        if let Some(c) = hint.swatch {
            let sw = Rect::from_min_size(
                pos2(x + text_w + pill::SWATCH_GAP, rect.center().y - swatch / 2.0),
                Vec2::splat(swatch),
            );
            p.rect(
                sw,
                0u8,
                c.gamma_multiply(a),
                Stroke::new(1.0, Color32::BLACK.gamma_multiply(a)),
                StrokeKind::Inside,
            );
        }
    }

    if let Some(n) = notice {
        let a = notice_alpha(n.notice.anchor.elapsed().as_secs_f32());
        let galley = p.layout_no_wrap(n.notice.kind.message().to_owned(), pill::font(), Color32::PLACEHOLDER);
        let text_h = galley.size().y;
        let rect = place::notice_rect(n.selection, place::chip_size(galley.size(), false), screen);
        p.add(pill::shadow(rect, a));
        p.add(pill::body(rect, pill::RADIUS, a, true));
        p.galley(
            pos2(rect.left() + pill::PAD_H, rect.center().y - text_h / 2.0),
            galley,
            pill::TEXT.gamma_multiply(a),
        );
        animating = true;
    }

    if animating {
        ctx.request_repaint_after(REPAINT_FLOOR);
    }
}

/// The dashed highlight square around the cursor image: a black outline
/// with a white dash pattern over it, each edge's phase restarting at its
/// own origin, as the old rect shader drew it.
fn dashed_square(p: &Painter, sq: Rect, alpha: f32) {
    p.rect_stroke(
        sq,
        0u8,
        Stroke::new(1.0, Color32::BLACK.gamma_multiply(0.9 * alpha)),
        StrokeKind::Inside,
    );
    // The centre line of the inside band the outline occupies.
    let r = sq.shrink(0.5);
    let white = Stroke::new(1.0, Color32::WHITE.gamma_multiply(0.9 * alpha));
    for [a, b] in [
        [r.left_top(), r.right_top()],
        [r.left_bottom(), r.right_bottom()],
        [r.left_top(), r.left_bottom()],
        [r.right_top(), r.right_bottom()],
    ] {
        p.add(Shape::Vec(Shape::dashed_line(&[a, b], white, pill::DASH, pill::DASH)));
    }
}

#[cfg(test)]
mod tests {
    use std::time::Instant;

    use super::*;
    use crate::interaction::{InteractionState, OcrNoticeKind, OcrState};
    use crate::settings::TipsMode;
    use clowd_rust_core::geometry::{ScreenRect, ScreenRectF};

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
            hovered_window_title: None,
            hovered_monitor_name: None,
            hovered_pixel_bgra: None,
            cursor_image_rect: None,
            cursor_overlay_visible: true,
        }
    }

    /// A state with the cursor on the primary and the hint mode on.
    fn hinting() -> InteractionState {
        let mut input = InteractionState::new();
        input.tips_mode = TipsMode::Hints;
        input.virtual_cursor = ScreenPointF::new(300.0, 300.0);
        input
    }

    fn keys(h: &HintsInputs) -> Vec<Option<&'static str>> {
        h.hints.iter().map(|x| x.key).collect()
    }

    /// While the picker owns the overlay there is exactly one chip, on the
    /// cursor's host, and nothing else — not even `[M]`.
    #[test]
    fn scroll_pick_suppresses_every_other_hint() {
        let m = monitors();
        let mut input = hinting();
        input.captured = true;
        input.scroll_pick_mode = true;
        input.selection = Some(ScreenRect::from_xy_size(100, 100, 800, 600));
        let mut c = ctx(&input, &m);
        c.cursor_image_rect = Some(ScreenRectF::from_xy_size(300.0, 300.0, 32.0, 32.0));

        let here = inputs(0, &m[0], &c);
        assert_eq!(keys(&here), vec![None]);
        assert_eq!(here.hints[0].text, SCROLL_PICK_TEXT);
        assert!(here.hints[0].trail, "the instruction wears the comet");
        assert!(here.dashed_square.is_none());
        assert!(inputs(1, &m[1], &c).hints.is_empty(), "only the cursor's host");

        input.overlays_visible = false;
        assert!(
            inputs(0, &m[0], &ctx(&input, &m))
                .hints
                .is_empty(),
            "Q hides it"
        );
    }

    /// Nothing is shown while the lift mode is live: the app swallows the
    /// keys those chips advertise.
    #[test]
    fn ocr_active_leaves_no_hint() {
        let m = monitors();
        let mut input = hinting();
        input.captured = true;
        input.selection = Some(ScreenRect::from_xy_size(100, 100, 800, 600));
        input.ocr = OcrState::Scanning {
            anchor: Instant::now(),
            req: 1,
            region: ScreenRect::from_xy_size(100, 100, 800, 600),
        };
        let mut c = ctx(&input, &m);
        c.cursor_image_rect = Some(ScreenRectF::from_xy_size(300.0, 300.0, 32.0, 32.0));
        let out = inputs(0, &m[0], &c);
        assert!(out.hints.is_empty());
        assert!(out.dashed_square.is_none());
    }

    /// Zoomed with the overlays off, the way out of the magnifier is the
    /// only thing left on screen.
    #[test]
    fn zoomed_under_q_leaves_only_the_magnifier_hint() {
        let m = monitors();
        let mut input = hinting();
        input.zoom = 4.0;
        input.overlays_visible = false;

        let out = inputs(0, &m[0], &ctx(&input, &m));
        assert_eq!(keys(&out), vec![Some("Q")]);
        assert_eq!(out.hints[0].text, "Exit Magnifier");
        assert!(out.hints[0].trail, "the comet draws the eye until it has been used");

        // With the overlays back on the colour chip joins it, and the
        // comet is gone once the magnifier has been used before.
        input.overlays_visible = true;
        input.has_used_magnifier = true;
        let out = inputs(0, &m[0], &ctx(&input, &m));
        assert_eq!(keys(&out), vec![Some("Q"), Some("H")]);
        assert!(!out.hints[0].trail);
        assert_eq!(out.hints[0].text, "Enter Magnifier");
    }

    /// `[M]` is the one chip that outlives a capture, but it needs the
    /// cursor image wholly inside the selection.
    #[test]
    fn m_survives_capture_but_needs_the_image_inside_the_selection() {
        let m = monitors();
        let mut input = hinting();
        input.captured = true;
        input.selection = Some(ScreenRect::from_xy_size(200, 200, 600, 400));
        let mut c = ctx(&input, &m);
        c.cursor_image_rect = Some(ScreenRectF::from_xy_size(300.0, 300.0, 32.0, 32.0));

        let out = inputs(0, &m[0], &c);
        assert_eq!(keys(&out), vec![Some("M")], "capture killed every other chip");
        assert_eq!(out.hints[0].text, "Hide Cursor");
        assert_eq!(out.hints[0].alpha, 1.0);
        let (square, alpha) = out
            .dashed_square
            .expect("the square comes with the chip");
        assert_eq!(alpha, 1.0);
        assert_eq!(square.center(), pos2(316.0, 316.0));
        assert_eq!(square.width(), 32.0 + 2.0 * place::CURSOR_SQUARE_PAD);

        // A hidden cursor dims both, and the label flips.
        c.cursor_overlay_visible = false;
        let dimmed = inputs(0, &m[0], &c);
        assert_eq!(dimmed.hints[0].text, "Show Cursor");
        assert!((dimmed.hints[0].alpha - 0.5 / 0.85).abs() < 1e-6);
        assert!((dimmed.dashed_square.expect("still there").1 - 0.5 / 0.85).abs() < 1e-6);

        // Hanging off the selection's left edge: nothing.
        c.cursor_overlay_visible = true;
        c.cursor_image_rect = Some(ScreenRectF::from_xy_size(190.0, 300.0, 32.0, 32.0));
        let out = inputs(0, &m[0], &c);
        assert!(out.hints.is_empty() && out.dashed_square.is_none());
    }

    /// The scroll prompt is shown in every tips mode, including the one
    /// that hides the floating hints: it is a one-time teaching aid.
    #[test]
    fn scroll_to_zoom_ignores_tips_mode() {
        let m = monitors();
        let mut input = hinting();
        input.tips_mode = TipsMode::Tips;
        input.show_scroll_hint = true;

        let out = inputs(0, &m[0], &ctx(&input, &m));
        assert_eq!(keys(&out), vec![Some("\u{2195}")], "no [H] in Tips mode");
        assert!(out.hints[0].trail);

        input.mouse_down = true;
        assert!(
            inputs(0, &m[0], &ctx(&input, &m))
                .hints
                .is_empty(),
            "not mid-drag"
        );
    }

    /// Near a seam the cursor and its image can be on different monitors:
    /// the chip and its square follow the image, not the pointer.
    #[test]
    fn the_m_hint_goes_to_the_host_holding_the_image_centre() {
        let m = monitors();
        let mut input = hinting();
        input.captured = true;
        input.virtual_cursor = ScreenPointF::new(1910.0, 400.0);
        input.selection = Some(ScreenRect::from_xy_size(1800, 200, 400, 400));
        let mut c = ctx(&input, &m);
        // Image centred just past the seam, on the neighbour.
        c.cursor_image_rect = Some(ScreenRectF::from_xy_size(1910.0, 400.0, 32.0, 32.0));

        assert!(inputs(0, &m[0], &c).hints.is_empty(), "the cursor's host does not draw it");
        let over = inputs(1, &m[1], &c);
        assert_eq!(keys(&over), vec![Some("M")]);
        // Local points on a 150 % monitor whose origin is x = 1920.
        let (square, _) = over
            .dashed_square
            .expect("the square comes with the chip");
        assert!((square.center().x - (1926.0 - 1920.0) / 1.5).abs() < 1e-3, "{square:?}");
    }

    /// The notice belongs to the selection's monitor, and disappears when
    /// it expires rather than lingering as a transparent pill.
    #[test]
    fn notice_goes_to_the_selection_centre_host_only() {
        let m = monitors();
        let mut input = hinting();
        input.captured = true;
        input.selection = Some(ScreenRect::from_xy_size(2000, 200, 400, 400));
        input.ocr_notice = Some(OcrNotice {
            anchor: Instant::now(),
            kind: OcrNoticeKind::NoText,
        });

        let c = ctx(&input, &m);
        assert!(notice_inputs(0, &m[0], &c).is_none(), "the selection is on the neighbour");
        let there = notice_inputs(1, &m[1], &c).expect("the selection's own host");
        assert_eq!(there.notice.kind, OcrNoticeKind::NoText);
        assert_eq!(there.selection.min, pos2((2000.0 - 1920.0) / 1.5, 200.0 / 1.5));

        // Q does not hide it: it is feedback for a press just made.
        input.overlays_visible = false;
        assert!(notice_inputs(1, &m[1], &ctx(&input, &m)).is_some());
        input.overlays_visible = true;

        // The scroll-point picker does: it owns the overlay alone.
        input.scroll_pick_mode = true;
        assert!(notice_inputs(1, &m[1], &ctx(&input, &m)).is_none());
        input.scroll_pick_mode = false;

        // Expired notices are simply not built.
        let expired = Instant::now().checked_sub(std::time::Duration::from_secs(30));
        if let Some(anchor) = expired {
            input.ocr_notice = Some(OcrNotice {
                anchor,
                kind: OcrNoticeKind::NoText,
            });
            assert!(notice_inputs(1, &m[1], &ctx(&input, &m)).is_none());
        }
    }

    /// The old `shared::hints_visibility` rule, now answered per host: the
    /// floating chips need the Hints mode, and zoom does not override it.
    #[test]
    fn hints_only_in_hints_mode_even_when_zoomed() {
        let m = monitors();
        let mut input = hinting();

        assert!(!inputs(0, &m[0], &ctx(&input, &m))
            .hints
            .is_empty());
        input.zoom = 2.0;
        input.overlays_visible = false;
        assert!(
            !inputs(0, &m[0], &ctx(&input, &m))
                .hints
                .is_empty(),
            "the exit-magnifier chip survives Q"
        );

        for mode in [TipsMode::Off, TipsMode::Tips] {
            input.tips_mode = mode;
            input.zoom = 1.0;
            input.overlays_visible = true;
            assert!(
                inputs(0, &m[0], &ctx(&input, &m))
                    .hints
                    .is_empty(),
                "{mode:?} at zoom 1 should hide the chips"
            );
            input.zoom = 2.0;
            assert!(
                inputs(0, &m[0], &ctx(&input, &m))
                    .hints
                    .is_empty(),
                "{mode:?} when zoomed should still hide them"
            );
        }
    }

    /// The old renderer pushed the dashed square after every other chip
    /// and before `[M]`, so its dashes crossed the `[H]` chip that sits a
    /// few points away from the same cursor and only `[M]` covered them.
    /// Painting it first instead would hide those edges behind `[H]`'s
    /// body and shadow in the most ordinary pre-capture state there is.
    #[test]
    fn the_dashed_square_is_painted_over_the_other_chips_and_under_the_m_chip() {
        let image = Rect::from_min_size(pos2(300.0, 300.0), Vec2::splat(32.0));
        let h = HintsInputs {
            hints: vec![
                Hint {
                    key: Some("H"),
                    text: "Select #101010".to_owned(),
                    anchor: Anchor::Cursor(pos2(300.0, 300.0)),
                    trail: false,
                    alpha: 1.0,
                    swatch: Some(Color32::RED),
                },
                Hint {
                    key: Some("M"),
                    text: "Hide Cursor".to_owned(),
                    anchor: Anchor::CursorImage {
                        image,
                        selection: Rect::from_min_size(pos2(100.0, 100.0), vec2(800.0, 600.0)),
                    },
                    trail: false,
                    alpha: 1.0,
                    swatch: None,
                },
            ],
            dashed_square: Some((image.expand(place::CURSOR_SQUARE_PAD), 1.0)),
        };

        let screen = Rect::from_min_size(Pos2::ZERO, vec2(1920.0, 1080.0));
        let egui_ctx = Context::default();
        let output = egui_ctx.run_ui(
            egui::RawInput {
                screen_rect: Some(screen),
                ..Default::default()
            },
            |ui| {
                let p = ui
                    .ctx()
                    .layer_painter(egui::LayerId::new(egui::Order::Background, egui::Id::new("hints-order-test")));
                show(ui.ctx(), &p, &h, None, screen, Color32::RED, 1.0);
            },
        );
        let mut shapes = Vec::new();
        fn push(out: &mut Vec<Shape>, shape: &Shape) {
            match shape {
                Shape::Vec(inner) => inner.iter().for_each(|s| push(out, s)),
                other => out.push(other.clone()),
            }
        }
        for clipped in &output.shapes {
            push(&mut shapes, &clipped.shape);
        }
        output.drop_without_applying_deltas();

        // Only the square draws line segments; only the chips draw rects.
        let dashes: Vec<usize> = shapes
            .iter()
            .enumerate()
            .filter(|(_, s)| matches!(s, Shape::LineSegment { .. }))
            .map(|(i, _)| i)
            .collect();
        let (first, last) = (
            *dashes.first().expect("the square dashes"),
            *dashes.last().expect("the square dashes"),
        );
        let rects = |range: std::ops::Range<usize>| {
            shapes[range]
                .iter()
                .filter(|s| matches!(s, Shape::Rect(_)))
                .count()
        };
        // Shadow, body, three keycap layers and the swatch: the whole `[H]`
        // chip, plus the square's own black outline.
        assert_eq!(rects(0..first), 7, "{shapes:?}");
        // Shadow, body and three keycap layers: the whole `[M]` chip.
        assert_eq!(rects(last + 1..shapes.len()), 5, "{shapes:?}");
    }
}
