//! OCR text bubbles: the primary presentation of a recognized line — a
//! rounded pill containing the recognized string as REAL GLYPHS, revealed
//! line by line as the sweep's band passes over the selection.
//!
//! Styling is deliberately the hint pills' (`super::hints`): same fill,
//! border, text colour, corner radius and padding proportions, imported
//! from the shared constants rather than re-derived, so the two families
//! cannot drift apart. Lines the embedded fonts cannot cover never reach
//! this renderer — `ocr::coverage` classified them once, on the app
//! thread, and `super::lift` draws those as pixel crops instead.
//!
//! Draw-order contract (see `UiRenderer::draw` for the enforcement): the
//! bubble RECTS are the leading range of the shared rect buffer, drawn
//! right after the lift pass; the bubble TEXT goes through the TextStack's
//! dedicated bubble renderer, drawn between that range and the rest of the
//! rects. Net stacking, bottom to top: dimmed desktop → sweep/pixel crops
//! → bubble pills → bubble glyphs → panel/hint rects → icons → panel/hint
//! text. That is what puts readable text over the darkened screenshot
//! while the panel and its labels still cover everything.
//!
//! Animation is a pure function of the phase anchor carried in `OcrState`
//! (never a per-worker clock, never dt — the workers free-run at their
//! monitors' refresh rates), and all physical sizing uses the MODE's
//! `dpi_scale`, not this monitor's, so a bubble crossing a mixed-DPI seam
//! is byte-identical on both halves.

use glyphon::{Attrs, Buffer, Color, Family, Metrics, Shaping, TextArea, TextBounds, Wrap};

use crate::interaction::OcrState;
use crate::ocr::anim;
use crate::ocr::coverage::LinePresentation;
use crate::ui::components::hints::layout::{CORNER_RADIUS, HINT_FONT_PX, HINT_PADDING_H, HINT_PADDING_V};
use crate::ui::gpu::hints::{AA, TOOLTIP_BORDER, TOOLTIP_FILL, TOOLTIP_TEXT_COLOR};
use crate::ui::gpu::rect::RectInstance;
use crate::ui::gpu::text::{TextStack, FAMILY_CODE};
use crate::ui::shared::{UiMonitor, UiSharedState};
use clowd_rust_core::geometry::RectExt;

/// Fraction of the recognized line's rect height used as the bubble font
/// size. The OCR word rects hug the glyph ink (ascender to descender for a
/// typical mixed-case line), which for most faces spans a bit under one
/// em — so a font a bit under the rect height renders text of visually
/// similar size to the source. REASONED, not observed: this is the first
/// knob to turn if bubbles read as too large/small against their lines.
const FONT_FRACTION: f32 = 0.82;

/// Fonts below this are unreadable dots; a source line that small produces
/// a small-but-legible bubble instead of a faithful-but-useless one.
const MIN_FONT_PX: f32 = 6.0;

/// Fit policy for long lines (documented choice): shrink the font until
/// the bubble fits the selection width, but never below this fraction of
/// its natural size — past that, faithfulness to the source line's scale
/// matters more than containment, so the bubble keeps the floor font and
/// is allowed a modest overhang past the selection's right edge instead.
const MIN_FIT_SHRINK: f32 = 0.6;

/// One laid-out bubble, cached for the lifetime of the outcome (shaping is
/// the expensive part and the resting geometry never changes — only the
/// per-frame rise/alpha do).
struct BubbleEntry {
    buffer: Buffer,
    /// Reveal key: the line's top edge in region-height units.
    rel_top: f32,
    /// Resting pill rect in virtual-desktop px (before the lift offset).
    rect: [f32; 4],
    /// Resting text origin in virtual-desktop px.
    text_x: f32,
    text_y: f32,
    /// Pill metrics baked at layout time (font-proportional, see
    /// `layout_bubble`).
    corner_radius: f32,
    border_px: f32,
}

/// Per-frame text placement, window-local.
struct DrawnText {
    entry_idx: usize,
    x: f32,
    y: f32,
    alpha: f32,
    /// Clip rect (the pill's window-local bounds) — glyphs must never
    /// escape their bubble.
    bounds: [i32; 4],
}

pub struct OcrBubblesRenderer {
    entries: Vec<BubbleEntry>,
    /// Identity of the outcome the entries were shaped from, as a plain
    /// address — same caching discipline as `LiftPipeline::snapshot_key`:
    /// safe against ABA because `prepare` clears it on every frame where
    /// the mode is not Lifted, so a key never outlives the outcome it
    /// names (the mode always passes through Retracting/Idle — or the
    /// cycle ends, which clears via `end_cycle` — before a new outcome
    /// can exist).
    outcome_key: Option<usize>,
    drawn: Vec<DrawnText>,
}

impl OcrBubblesRenderer {
    pub fn new() -> Self {
        Self {
            entries: Vec::new(),
            outcome_key: None,
            drawn: Vec::new(),
        }
    }

    /// Drop the cached layouts (glyphon Buffers hold shaped-glyph heap
    /// data — a page of recognized text is worth releasing promptly).
    /// Called whenever the mode leaves Lifted and from
    /// `UiRenderer::end_cycle`.
    pub fn clear(&mut self) {
        self.entries.clear();
        self.outcome_key = None;
        self.drawn.clear();
    }

    /// Stage this frame's bubbles: pill rects into `bubble_rects` (the
    /// DEDICATED leading rect range — not the shared list the panel uses,
    /// see the module docs) and text placements for [`Self::text_areas`].
    pub fn prepare(&mut self, ts: &mut TextStack, state: &UiSharedState, this_monitor: &UiMonitor, bubble_rects: &mut Vec<RectInstance>) {
        self.drawn.clear();

        let (anchor, region, dpi, outcome, presentation) = match &state.ocr {
            OcrState::Lifted {
                anchor,
                region,
                dpi_scale,
                outcome,
                presentation,
            } => (anchor, region, *dpi_scale, outcome, presentation),
            // Scanning: nothing is recognized yet, so the sweep just loops
            // (lift.rs). Retracting: the text vanishes AT ONCE on exit —
            // no reverse animation, by explicit owner call — so bubbles
            // stop existing the frame BACK is pressed and the shaped
            // buffers are released immediately.
            OcrState::Idle
            | OcrState::Scanning {
                ..
            }
            | OcrState::Retracting {
                ..
            } => {
                self.clear();
                return;
            }
        };

        let rf = region.to_f32();
        let key = std::sync::Arc::as_ptr(outcome) as usize;
        if self.outcome_key != Some(key) {
            self.entries.clear();
            for (i, line) in outcome.lines.iter().enumerate() {
                if presentation.get(i).copied() != Some(LinePresentation::Bubble) {
                    continue;
                }
                self.entries.push(layout_bubble(
                    ts,
                    &line.text,
                    [line.rect.left(), line.rect.top(), line.rect.right(), line.rect.bottom()],
                    [rf.left(), rf.top(), rf.right(), rf.bottom()],
                    dpi,
                ));
            }
            self.outcome_key = Some(key);
        }

        if self.entries.is_empty() {
            return;
        }

        // Shared animation clock — the phase anchor, never this worker's.
        let t = anchor.elapsed().as_secs_f32();
        let mon_f = this_monitor.bounds.to_f32();
        let (mon_left, mon_top) = (mon_f.left(), mon_f.top());

        for (entry_idx, entry) in self.entries.iter().enumerate() {
            let e = anim::reveal_progress(t, entry.rel_top);
            // Not yet revealed: the bubble simply does not exist. This is
            // the wave doing the revealing.
            if e <= 0.001 {
                continue;
            }

            // Rise + fade over the reveal ease. No drop shadow (owner
            // call): the pill already sits on a darkened, desaturated
            // page — its own bright fill IS the separation, and a shadow
            // on that ground reads as smudge. No scale animation either —
            // glyphs re-rasterise per fractional scale and would churn
            // the atlas every frame.
            let dy = -e * anim::LIFT_PX * dpi;

            let x0 = entry.rect[0];
            let y0 = entry.rect[1] + dy;
            let x1 = entry.rect[2];
            let y1 = entry.rect[3] + dy;

            // Every monitor the bubble reaches draws its share — the
            // seam-spanning rule the lift pass documents.
            let touches = x1 > mon_f.left() && x0 < mon_f.right() && y1 > mon_f.top() && y0 < mon_f.bottom();
            if !touches {
                continue;
            }

            let a = |c: [f32; 4]| -> [f32; 4] { [c[0], c[1], c[2], c[3] * e] };

            bubble_rects.push(RectInstance {
                dest_px: [x0 - mon_left - AA, y0 - mon_top - AA, x1 - mon_left + AA, y1 - mon_top + AA],
                fill_rgba: a(TOOLTIP_FILL),
                border_rgba: a(TOOLTIP_BORDER),
                params: [entry.border_px, 0.0, entry.corner_radius, AA],
            });

            self.drawn.push(DrawnText {
                entry_idx,
                x: entry.text_x - mon_left,
                y: entry.text_y + dy - mon_top,
                alpha: e,
                bounds: [
                    (x0 - mon_left).floor() as i32,
                    (y0 - mon_top).floor() as i32,
                    (x1 - mon_left).ceil() as i32,
                    (y1 - mon_top).ceil() as i32,
                ],
            });
        }
    }

    /// Collect this frame's bubble text areas. Goes through the
    /// TextStack's DEDICATED bubble renderer (`prepare_bubbles`), not the
    /// main one: the main text draw runs last (above the panel), while
    /// bubble glyphs must sit below the panel's rects — see the module
    /// docs for the full stacking contract.
    pub fn text_areas<'a>(&'a self, out: &mut Vec<TextArea<'a>>) {
        out.extend(self.drawn.iter().map(|d| {
            let a = (TOOLTIP_TEXT_COLOR[3] as f32 * d.alpha)
                .round()
                .clamp(0.0, 255.0) as u8;
            TextArea {
                buffer: &self.entries[d.entry_idx].buffer,
                left: d.x,
                top: d.y,
                scale: 1.0,
                bounds: TextBounds {
                    left: d.bounds[0],
                    top: d.bounds[1],
                    right: d.bounds[2],
                    bottom: d.bounds[3],
                },
                default_color: Color::rgba(TOOLTIP_TEXT_COLOR[0], TOOLTIP_TEXT_COLOR[1], TOOLTIP_TEXT_COLOR[2], a),
                custom_glyphs: &[],
            }
        }));
    }
}

/// Shape one line and compute its resting pill geometry. Impure only in
/// that it drives glyphon; every numeric decision is delegated to the pure
/// helpers below, which carry the tests.
fn layout_bubble(ts: &mut TextStack, text: &str, line: [f32; 4], region: [f32; 4], dpi: f32) -> BubbleEntry {
    let line_h = line[3] - line[1];
    let mut font_px = bubble_font_px(line_h);

    let shape = |ts: &mut TextStack, font_px: f32| -> Buffer {
        let mut buffer = Buffer::new(&mut ts.font_system, Metrics::new(font_px, font_px * 1.2));
        buffer.set_wrap(Wrap::None);
        // Regular weight, code family: exactly the hint description text.
        buffer.set_text(text, &Attrs::new().family(Family::Name(FAMILY_CODE)), Shaping::Advanced, None);
        buffer.shape_until_scroll(&mut ts.font_system, false);
        buffer
    };
    let measure = |b: &Buffer| -> f32 {
        b.layout_runs()
            .map(|r| r.line_w)
            .fold(0.0f32, f32::max)
    };

    let mut buffer = shape(ts, font_px);
    let mut text_w = measure(&buffer);

    // Long-line policy (see MIN_FIT_SHRINK): one shrink-and-reshape pass.
    // The shrink is computed against the pre-shrink padding, which is
    // slightly conservative — good enough, and it bounds shaping to at
    // most twice per line per outcome.
    let shrink = fit_shrink(text_w, region[2] - region[0], bubble_pad_h(font_px));
    if shrink < 1.0 {
        font_px = (font_px * shrink).max(MIN_FONT_PX);
        buffer = shape(ts, font_px);
        text_w = measure(&buffer);
    }

    // Pill metrics scale with the bubble's own font (s = font/HINT_FONT),
    // not with monitor dpi as the cursor hints do: a 40 px headline bubble
    // must keep the pill's PROPORTIONS, not wear an 11 px pill's frame.
    // The border is the one exception — it stays the pills' dpi hairline,
    // because a font-scaled border reads as a different component.
    let pad_h = bubble_pad_h(font_px);
    let pad_v = (HINT_PADDING_V * font_px / HINT_FONT_PX)
        .floor()
        .max(2.0);
    let corner_radius = (CORNER_RADIUS * font_px / HINT_FONT_PX)
        .floor()
        .max(2.0);
    let border_px = dpi.ceil().max(1.0);

    let text_line_h = font_px * 1.2;
    let bubble_w = pad_h * 2.0 + text_w;
    let bubble_h = pad_v * 2.0 + text_line_h;

    // Anchor: text left edge over the line's left edge, pill centred on
    // the line's vertical centre. Horizontally clamped into the selection
    // (with the MIN_FIT_SHRINK overhang exception); vertically NOT clamped
    // — a bubble belongs to its line even when the padding pokes past the
    // region edge by a couple of px.
    let x = bubble_x(line[0] - pad_h, bubble_w, region[0], region[2]);
    let cy = (line[1] + line[3]) * 0.5;
    let y = cy - bubble_h * 0.5;

    // Same vertical text centring bias the hint pills use
    // (`finalize_layout`): nudge by 0.1 em and floor to a whole pixel so
    // glyphs sit crisp.
    let text_y = (y + (bubble_h - text_line_h) / 2.0 + font_px * 0.1).floor();

    BubbleEntry {
        buffer,
        rel_top: anim::line_rel_top(line[1], region[1], region[3] - region[1]),
        rect: [x, y, x + bubble_w, y + bubble_h],
        text_x: x + pad_h,
        text_y,
        corner_radius,
        border_px,
    }
}

/// Horizontal padding for a bubble of the given font — the hint pill's
/// padding scaled to the bubble's own type size (see `layout_bubble`).
fn bubble_pad_h(font_px: f32) -> f32 {
    (HINT_PADDING_H * font_px / HINT_FONT_PX)
        .floor()
        .max(2.0)
}

/// Font size that renders text at visually the same height as the
/// recognized line — see [`FONT_FRACTION`].
fn bubble_font_px(line_h: f32) -> f32 {
    (line_h * FONT_FRACTION).max(MIN_FONT_PX)
}

/// How much the font must shrink for the bubble to fit the region width:
/// 1.0 when it already fits, down to [`MIN_FIT_SHRINK`] otherwise.
fn fit_shrink(text_w: f32, region_w: f32, pad_h: f32) -> f32 {
    let avail = region_w - 2.0 * pad_h;
    if text_w <= avail || text_w <= 0.0 || avail <= 0.0 {
        return 1.0;
    }
    (avail / text_w).clamp(MIN_FIT_SHRINK, 1.0)
}

/// Clamp the bubble's left edge into the region. When the bubble is wider
/// than the region (the floor-font overhang case) it left-aligns, keeping
/// the START of the text — the reading anchor — inside the selection.
fn bubble_x(desired_left: f32, bubble_w: f32, region_left: f32, region_right: f32) -> f32 {
    desired_left.clamp(region_left, (region_right - bubble_w).max(region_left))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The size mapping is what makes a bubble read as "this line, lifted":
    /// proportional to the source, floored at legibility.
    #[test]
    fn font_tracks_line_height_with_a_floor() {
        assert_eq!(bubble_font_px(100.0), 82.0);
        assert!((bubble_font_px(20.0) - 16.4).abs() < 1e-3);
        // Tiny source lines hit the legibility floor instead of vanishing.
        assert_eq!(bubble_font_px(4.0), MIN_FONT_PX);
        assert_eq!(bubble_font_px(0.0), MIN_FONT_PX);
    }

    /// Fit policy: no shrink when it fits, proportional shrink when it
    /// doesn't, hard floor after which overhang is accepted.
    #[test]
    fn fit_shrink_clamps_between_floor_and_one() {
        // Fits: untouched.
        assert_eq!(fit_shrink(100.0, 200.0, 6.0), 1.0);
        // Slightly over: shrinks by exactly the overflow ratio.
        let s = fit_shrink(200.0, 166.0, 8.0); // avail = 150
        assert!((s - 0.75).abs() < 1e-3);
        // Wildly over: floored, never microscopic.
        assert_eq!(fit_shrink(10_000.0, 200.0, 6.0), MIN_FIT_SHRINK);
        // Degenerate inputs must not divide by zero / go negative.
        assert_eq!(fit_shrink(0.0, 200.0, 6.0), 1.0);
        assert_eq!(fit_shrink(100.0, 4.0, 6.0), 1.0);
    }

    /// Placement policy: pinned to the line's left edge, clamped inside
    /// the region, and left-aligned (text start visible) when wider than
    /// the region.
    #[test]
    fn bubble_x_clamps_and_left_aligns_overhang() {
        // Room on both sides: exactly where the line asked.
        assert_eq!(bubble_x(50.0, 100.0, 0.0, 400.0), 50.0);
        // Would poke out left: clamped to the region's left edge.
        assert_eq!(bubble_x(-10.0, 100.0, 0.0, 400.0), 0.0);
        // Would poke out right: pulled back inside.
        assert_eq!(bubble_x(350.0, 100.0, 0.0, 400.0), 300.0);
        // Wider than the region: left-aligned overhang.
        assert_eq!(bubble_x(20.0, 500.0, 0.0, 400.0), 0.0);
        // Negative-origin regions (multi-monitor left of primary).
        assert_eq!(bubble_x(-1900.0, 100.0, -1920.0, -1520.0), -1900.0);
        assert_eq!(bubble_x(-1500.0, 100.0, -1920.0, -1520.0), -1620.0);
    }
}
