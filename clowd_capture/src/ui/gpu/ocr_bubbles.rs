//! OCR text bubbles: the ONLY presentation of a recognized line — a
//! rounded pill containing the recognized string as REAL GLYPHS, revealed
//! line by line as the sweep's band passes over the selection. Scripts the
//! embedded Cascadia faces cannot shape (CJK, Cyrillic, …) come from
//! system fonts via cosmic-text's per-script fallback —
//! `TextStack::try_merge_system_fonts` folds a background scan's result
//! in during the Scanning warmup. (There used to be a pixel-crop fallback pass for those lines,
//! sampling the desktop snapshot texture; system-font fallback replaced
//! it wholesale.)
//!
//! Styling is deliberately the hint pills' (`super::hints`): same fill,
//! border, text color, corner radius and padding proportions, imported
//! from the shared constants rather than re-derived, so the two families
//! cannot drift apart.
//!
//! First-reveal smoothness is engineered in three stages, because a fresh
//! process pays three one-time costs (system-font scan, cosmic-text
//! font-matching, swash glyph rasterization + atlas growth) that would
//! otherwise land on animated frames and visibly chop them. The governing
//! rule: no single frame ever carries more than a frame's slack of warmup
//! work — the work is SLICED, never paused-for:
//!
//! * **Scanning frames** (the sweep playing while recognition is in
//!   flight): the system-font scan runs on a background thread the OCR
//!   press started, and its result is folded into the DB the frame it
//!   lands (a metadata copy, not a scan); then printable ASCII is shaped +
//!   staged as INVISIBLE (alpha-0) text at the quantized bubble sizes, ~a
//!   dozen glyphs per frame (~1-2 ms). The recognizer's round-trip is
//!   dozens of frames even warm, so the atlas is usually fully warm by
//!   Lifted. Nothing runs before the first OCR press — by decision, a
//!   non-OCR session pays zero warmup — and slicing PAUSES during Lifted
//!   so the reveal never shares its frames with generic warmup.
//! * **The Scanning→Lifted transition frame** shapes every line at once.
//!   That is deliberate: the app thread wrap-aligns the transition, so on
//!   this exact frame the band is entirely off-screen — the one frame in
//!   the whole choreography where a burst is invisible by construction.
//! * **Lifted, ahead of the wave:** lines whose reveal starts within the
//!   next [`PRERASTER_LOOKAHEAD_SECS`] are staged at their resting spot
//!   with alpha 0, so whatever the generic warmup missed (odd glyphs,
//!   uncommon sizes, CJK) rasterizes a comfortable margin before its line
//!   can show, a few lines per frame as the band descends. Re-staging
//!   every frame until reveal also marks the glyphs in-use, which is what
//!   protects them from atlas eviction between rasterization and reveal.
//!
//! Draw-order contract (see `UiRenderer::draw` for the enforcement): the
//! bubble RECTS are the leading range of the shared rect buffer, drawn
//! right after the lift pass; the bubble TEXT goes through the TextStack's
//! dedicated bubble renderer, drawn between that range and the rest of the
//! rects. Net stacking, bottom to top: dimmed desktop → sweep →
//! bubble pills → bubble glyphs → panel/hint rects → icons → panel/hint
//! text. That is what puts readable text over the darkened screenshot
//! while the panel and its labels still cover everything.
//!
//! Animation is a pure function of the phase anchor carried in `OcrState`
//! (never a per-worker clock, never dt — the workers free-run at their
//! monitors' refresh rates), and all physical sizing uses the MODE's
//! `dpi_scale`, not this monitor's, so a bubble crossing a mixed-DPI seam
//! is byte-identical on both halves.

use crate::ui::gpu::text::{Attrs, Buffer, Color, Family, Metrics, Shaping, TextArea, TextBounds, Wrap};

use crate::interaction::OcrState;
use crate::ocr::anim;
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

/// Font sizes (physical px — bubble fonts are whole-pixel, see
/// `bubble_font_px`) pre-rasterized by the sliced warmup, most common
/// screen-text sizes first so an early OCR press still finds the sizes
/// that matter warm. Deliberately stops at 30: pages whose bubbles are
/// larger have FEW lines, so their look-ahead rasterization is cheap
/// without help from the ladder.
const WARMUP_SIZES: &[f32] = &[
    11.0, 12.0, 13.0, 10.0, 14.0, 9.0, 15.0, 16.0, 8.0, 18.0, 7.0, 6.0, 20.0, 23.0, 26.0, 30.0,
];

/// Every printable-ASCII glyph, as one line — warmup steps slice it into
/// [`WARMUP_CHUNK_BYTES`]-char chunks (all ASCII, so byte slicing is
/// safe). A chunk stays well inside any monitor's width even at the
/// largest ladder size, which matters because the glyph renderer culls out-of-bounds
/// glyphs BEFORE rasterization and would silently defeat the warmup.
const WARMUP_CHARS: &str = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";

/// Glyphs rasterized per warmup frame. ~12 small glyphs ≈ 1-2 ms of swash
/// work — inside a frame's slack even at high refresh rates, which is the
/// entire point of slicing (a whole-size-per-frame warmup visibly chopped
/// the scanning wave).
const WARMUP_CHUNK_BYTES: usize = 12;

/// How far ahead of a line's reveal moment its glyphs are pre-staged
/// (alpha 0) during Lifted. Comfortably more than one frame at any
/// refresh rate, and long enough that the pre-stage doubles as an
/// eviction pin for the glyphs about to be needed; short enough that a
/// dense page's rasterization spreads across the band's descent instead
/// of piling onto its first frame.
const PRERASTER_LOOKAHEAD_SECS: f32 = 0.3;

/// One warmup step: `None` for the font-DB step (step 0), else the
/// (size, chunk) to shape+stage. Steps sequence the whole ladder, chunk
/// by chunk, one step per frame. Pure so the schedule is testable.
fn warmup_step(step: usize) -> Option<(f32, &'static str)> {
    let chunks_per_size = WARMUP_CHARS
        .len()
        .div_ceil(WARMUP_CHUNK_BYTES);
    let i = step.checked_sub(1)?;
    let size = *WARMUP_SIZES.get(i / chunks_per_size)?;
    let start = (i % chunks_per_size) * WARMUP_CHUNK_BYTES;
    let chunk = &WARMUP_CHARS[start..(start + WARMUP_CHUNK_BYTES).min(WARMUP_CHARS.len())];
    Some((size, chunk))
}

/// Total steps including the font-DB step — `warm_step` past this means
/// the warmup is finished for the process lifetime.
fn warmup_total_steps() -> usize {
    1 + WARMUP_SIZES.len()
        * WARMUP_CHARS
            .len()
            .div_ceil(WARMUP_CHUNK_BYTES)
}

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
    /// Identity of the outcome the entries were shaped from: the OCR
    /// request id `OcrState::Lifted` carries. Unique within the cycle by
    /// construction (the app thread bumps it on every dispatch AND every
    /// exit), and `UiRenderer::begin_cycle`/`end_cycle` clear it across
    /// cycles — so unlike the `Arc` address this used to be, it cannot
    /// alias a later outcome even on a worker that stalled through every
    /// intermediate state.
    outcome_key: Option<u64>,
    drawn: Vec<DrawnText>,
    /// This frame's warmup chunk, staged as one alpha-0 text area and
    /// replaced (or dropped) next frame — rasterization into the atlas is
    /// the durable product, not the buffer.
    warm_buffer: Option<Buffer>,
    /// Next `warmup_step` to run, EVER — never reset, because the atlas
    /// and the caches the warmup fills are process-lifetime. A later
    /// capture re-runs nothing; if a warm glyph was evicted under atlas
    /// pressure in between, the Lifted look-ahead pre-stage (see module
    /// docs) re-rasterizes it before it can show.
    warm_step: usize,
    /// Sticky: OCR has been engaged (any non-Idle state seen) at some
    /// point this process. The warmup runs only after this flips — by
    /// decision, NOTHING OCR-related (font scan, glyph warmup, the
    /// `clowd_ai` child) runs before OCR is actually used.
    ocr_engaged: bool,
    /// Window-local clip size for the warmup area, captured in `prepare`
    /// (the glyph renderer culls out-of-bounds glyphs before rasterizing, so the
    /// warmup must be staged inside the real viewport).
    warm_bounds: [i32; 2],
}

impl OcrBubblesRenderer {
    pub fn new() -> Self {
        Self {
            entries: Vec::new(),
            outcome_key: None,
            drawn: Vec::new(),
            warm_buffer: None,
            warm_step: 0,
            ocr_engaged: false,
            warm_bounds: [0, 0],
        }
    }

    /// Drop the cached layouts (cosmic-text Buffers hold shaped-glyph heap
    /// data — a page of recognized text is worth releasing promptly).
    /// Called whenever the mode leaves Lifted. Deliberately does NOT touch
    /// `warm_step`: the caches the warmup filled outlive it.
    pub fn clear(&mut self) {
        self.entries.clear();
        self.outcome_key = None;
        self.drawn.clear();
        self.warm_buffer = None;
    }

    /// Stage this frame's bubbles: pill rects into `bubble_rects` (the
    /// DEDICATED leading rect range — not the shared list the panel uses,
    /// see the module docs) and text placements for [`Self::text_areas`].
    ///
    /// Returns whether the bubble scene is AT REST: Lifted, reveal pass
    /// finished, every bubble fully risen and opaque — from here on every
    /// frame's staging is byte-identical (the animation is a pure clamped
    /// function of elapsed time), which is what lets `UiRenderer` skip the
    /// per-frame glyph re-prepare of the whole page and re-issue its
    /// retained vertices instead.
    pub fn prepare(
        &mut self,
        ts: &mut TextStack,
        state: &UiSharedState,
        this_monitor: &UiMonitor,
        bubble_rects: &mut Vec<RectInstance>,
    ) -> bool {
        self.drawn.clear();
        self.ocr_engaged |= !matches!(state.ocr, OcrState::Idle);

        // One sliced warmup step per frame once OCR has been engaged, in
        // EVERY state except Lifted (the reveal never shares its frames
        // with generic warmup — its own look-ahead pre-stage below has
        // priority). Warmup runs mostly under Scanning, hidden behind the
        // recognizer's round-trip; before the first OCR press it never
        // runs at all — non-OCR sessions pay nothing. See the module docs
        // for the staging story.
        let lifted = matches!(state.ocr, OcrState::Lifted { .. });
        if self.ocr_engaged && !lifted {
            self.advance_warmup(ts, this_monitor);
        } else {
            self.warm_buffer = None;
        }

        let (anchor, req, region, dpi, outcome) = match &state.ocr {
            OcrState::Lifted {
                anchor,
                req,
                region,
                dpi_scale,
                outcome,
            } => (anchor, *req, region, *dpi_scale, outcome),
            OcrState::Scanning {
                ..
            } => {
                self.entries.clear();
                self.outcome_key = None;
                return false;
            }
            // Retracting: the text vanishes AT ONCE on exit — no reverse
            // animation, by explicit owner call — so bubbles stop existing
            // the frame BACK is pressed and the shaped buffers are
            // released immediately.
            OcrState::Idle
            | OcrState::Retracting {
                ..
            } => {
                self.entries.clear();
                self.outcome_key = None;
                return false;
            }
        };

        let rf = region.to_f32();
        let mon_f = this_monitor.bounds.to_f32();
        // Shaping every line in ONE frame is deliberate, not an oversight:
        // this branch runs on the wrap-aligned Scanning→Lifted transition
        // frame, the one frame in the choreography where the band is
        // entirely off-screen and a burst is invisible by construction
        // (see the module docs).
        if self.outcome_key != Some(req) {
            // A worker whose monitor the bubbles can never reach skips the
            // shaping burst entirely — it would pay the full-page layout
            // for glyphs it never draws. The bound is a conservative
            // OVERestimate computed without shaping (see
            // `estimated_bubble_bounds`), so a partial overlap always
            // shapes. The key stays unset: monitors never move within a
            // cycle, so this cheap check simply repeats per frame.
            let est = estimated_bubble_bounds(&outcome.lines, [rf.left(), rf.top(), rf.right(), rf.bottom()], dpi);
            if !(est[2] > mon_f.left() && est[0] < mon_f.right() && est[3] > mon_f.top() && est[1] < mon_f.bottom()) {
                self.entries.clear();
                return at_rest(anchor.elapsed().as_secs_f32());
            }
            // Belt-and-braces: the Scanning warmup already merged the
            // fallback fonts, but bubble shaping must NEVER run without
            // them in the DB (tofu otherwise, cached for the life of the
            // request), so re-assert the invariant where it matters. The
            // scan itself is done by now — the OCR worker waits it out
            // before publishing the outcome — so a false here is the
            // scan-timeout case, where shaping embedded-only is the
            // accepted fallback.
            let _ = ts.try_merge_system_fonts();
            self.entries.clear();
            for line in outcome.lines.iter() {
                // An all-whitespace line has no ink to lift; an empty pill
                // floating over the page would be pure noise.
                if line.text.trim().is_empty() {
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
            self.outcome_key = Some(req);
        }

        // Shared animation clock — the phase anchor, never this worker's.
        let t = anchor.elapsed().as_secs_f32();

        if self.entries.is_empty() {
            return at_rest(t);
        }

        let (mon_left, mon_top) = (mon_f.left(), mon_f.top());

        for (entry_idx, entry) in self.entries.iter().enumerate() {
            let e = anim::reveal_progress(t, entry.rel_top);
            // Not yet revealed but revealing SOON: no pill, but the TEXT
            // is staged at its resting spot with alpha 0 — the look-ahead
            // pre-rasterization pass the module docs describe. Glyphon
            // rasterizes staged glyphs regardless of color, so by the
            // time the wave reaches this line its glyphs are guaranteed
            // atlas-resident (and re-staging every frame until reveal
            // keeps them pinned there). Lines beyond the look-ahead don't
            // exist yet — that bounding is what spreads a dense page's
            // rasterization across the band's descent instead of piling
            // it onto one frame.
            let visible = e > 0.001;
            if !visible && anim::reveal_start_secs(entry.rel_top) > t + PRERASTER_LOOKAHEAD_SECS {
                continue;
            }

            // Rise + fade over the reveal ease. No drop shadow (owner
            // call): the pill already sits on a darkened, desaturated
            // page — its own bright fill IS the separation, and a shadow
            // on that ground reads as smudge. No scale animation either —
            // glyphs re-rasterize per fractional scale and would churn
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

            if visible {
                let a = |c: [f32; 4]| -> [f32; 4] { [c[0], c[1], c[2], c[3] * e] };
                bubble_rects.push(RectInstance {
                    dest_px: [x0 - mon_left - AA, y0 - mon_top - AA, x1 - mon_left + AA, y1 - mon_top + AA],
                    fill_rgba: a(TOOLTIP_FILL),
                    border_rgba: a(TOOLTIP_BORDER),
                    params: [entry.border_px, 0.0, entry.corner_radius, AA],
                });
            }

            self.drawn.push(DrawnText {
                entry_idx,
                x: entry.text_x - mon_left,
                y: entry.text_y + dy - mon_top,
                alpha: if visible { e } else { 0.0 },
                bounds: [
                    (x0 - mon_left).floor() as i32,
                    (y0 - mon_top).floor() as i32,
                    (x1 - mon_left).ceil() as i32,
                    (y1 - mon_top).ceil() as i32,
                ],
            });
        }
        at_rest(t)
    }

    /// One frame's slice of the first-reveal warmup (see the module docs):
    /// step 0 loads the fallback fonts, every later step shapes + stages
    /// one small glyph chunk. Each slice is sized to fit inside a frame's
    /// slack precisely so NO animation ever needs pausing for warmup.
    fn advance_warmup(&mut self, ts: &mut TextStack, this_monitor: &UiMonitor) {
        self.warm_buffer = None;
        if self.warm_step >= warmup_total_steps() {
            return;
        }

        if self.warm_step == 0 {
            // Non-blocking: the scan itself runs on the background thread
            // the OCR press started (`begin_system_font_scan`); this only
            // folds its result in — a per-face metadata copy, ~ms — and
            // retries next frame until the scan lands.
            if ts.try_merge_system_fonts() {
                self.warm_step = 1;
            }
            return;
        }

        let (size, chunk) = warmup_step(self.warm_step).expect("warm_step < total_steps has a chunk");
        let mon = this_monitor.bounds;
        self.warm_bounds = [mon.width(), mon.height()];
        let mut buffer = Buffer::new(&mut ts.font_system, Metrics::new(size, size * 1.2));
        buffer.set_wrap(Wrap::None);
        // Same attrs as the real bubbles — that identity is what makes the
        // shaping/matching caches this warms the ones layout_bubble hits.
        buffer.set_text(chunk, &Attrs::new().family(Family::Name(FAMILY_CODE)), Shaping::Advanced, None);
        buffer.shape_until_scroll(&mut ts.font_system, false);
        self.warm_buffer = Some(buffer);
        self.warm_step += 1;
        if self.warm_step == warmup_total_steps() {
            log::info!(
                "OCR glyph warmup complete ({} sizes, {} steps)",
                WARMUP_SIZES.len(),
                warmup_total_steps()
            );
        }
    }

    /// Collect this frame's bubble text areas. Goes through the
    /// TextStack's DEDICATED bubble renderer (`prepare_bubbles`), not the
    /// main one: the main text draw runs last (above the panel), while
    /// bubble glyphs must sit below the panel's rects — see the module
    /// docs for the full stacking contract.
    pub fn text_areas<'a>(&'a self, out: &mut Vec<TextArea<'a>>) {
        // This frame's warmup chunk (if any): fully transparent,
        // positioned inside the viewport so the glyph renderer actually rasterizes
        // it. One frame on stage is all a chunk needs — the atlas keeps
        // the rasterization.
        out.extend(
            self.warm_buffer
                .iter()
                .map(|buffer| TextArea {
                    buffer,
                    left: 0.0,
                    top: 0.0,
                    scale: 1.0,
                    bounds: TextBounds {
                        left: 0,
                        top: 0,
                        right: self.warm_bounds[0],
                        bottom: self.warm_bounds[1],
                    },
                    default_color: Color::rgba(255, 255, 255, 0),
                }),
        );
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
            }
        }));
    }
}

/// Whether the reveal animation has fully settled at elapsed time `t`:
/// the bottom-most possible line has finished its rise, so every bubble is
/// at its resting spot with alpha 1 and all later frames stage
/// byte-identical output. (Pure clamped-time animation is what makes this
/// a mere threshold test.)
fn at_rest(t: f32) -> bool {
    t >= anim::reveal_start_secs(1.0) + anim::LIFT_DURATION_SECS
}

/// Conservative bounding rect (virtual-desktop px) that every bubble of
/// this outcome is guaranteed to stay inside, computed WITHOUT shaping —
/// the whole point is to let a worker skip the shaping burst when its
/// monitor cannot intersect any bubble. Overestimates on purpose:
///
/// * Left edge: `bubble_x` clamps every pill to at least the region's
///   left edge, so the region's own left is exact.
/// * Width: bounded by chars × 1.5 em — the widest real advances are
///   ~1 em (full-width CJK; Latin is ~0.6 em), and the extra half em
///   absorbs any exotic fallback face. Fit-shrink only ever narrows.
/// * Vertically: a pill is centered on its line and its height is padding
///   plus one line box, so a full bubble-height past the line's center
///   covers both directions; the lift rise only ever moves bubbles UP.
fn estimated_bubble_bounds(lines: &[crate::ocr::OcrLine], region: [f32; 4], dpi: f32) -> [f32; 4] {
    let mut right = region[2];
    let mut top = region[1];
    let mut bottom = region[3];
    for line in lines {
        let font_px = bubble_font_px(line.rect.height());
        let pad_h = bubble_pad_h(font_px);
        let pad_v = (HINT_PADDING_V * font_px / HINT_FONT_PX)
            .floor()
            .max(2.0);
        let bubble_h = pad_v * 2.0 + font_px * 1.2;
        let est_w = pad_h * 2.0 + line.text.chars().count() as f32 * font_px * 1.5;
        let left = bubble_x(line.rect.left() - pad_h, est_w, region[0], region[2]);
        right = right.max(left + est_w);
        let cy = (line.rect.top() + line.rect.bottom()) * 0.5;
        top = top.min(cy - bubble_h);
        bottom = bottom.max(cy + bubble_h);
    }
    top -= anim::LIFT_PX * dpi;
    [region[0], top, right, bottom]
}

/// Shape one line and compute its resting pill geometry. Impure only in
/// that it drives the glyph renderer; every numeric decision is delegated to the pure
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
        // Floored for the same atlas-sharing reason as bubble_font_px.
        font_px = (font_px * shrink).floor().max(MIN_FONT_PX);
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

    // Anchor: text left edge over the line's left edge, pill centered on
    // the line's vertical center. Horizontally clamped into the selection
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
/// recognized line — see [`FONT_FRACTION`]. Floored to a WHOLE pixel:
/// sub-pixel sizes are visually indistinguishable at these magnitudes,
/// while integer sizes are what let the Scanning-phase warmup (and every
/// earlier capture) actually share atlas entries with this bubble —
/// glyph rasterizations are keyed by exact size.
fn bubble_font_px(line_h: f32) -> f32 {
    (line_h * FONT_FRACTION)
        .floor()
        .max(MIN_FONT_PX)
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
    /// proportional to the source, whole-pixel (atlas sharing — see
    /// `bubble_font_px`), floored at legibility.
    #[test]
    fn font_tracks_line_height_with_a_floor() {
        assert_eq!(bubble_font_px(100.0), 82.0);
        // 20 * 0.82 = 16.4 -> floored to a whole pixel.
        assert_eq!(bubble_font_px(20.0), 16.0);
        // Tiny source lines hit the legibility floor instead of vanishing.
        assert_eq!(bubble_font_px(4.0), MIN_FONT_PX);
        assert_eq!(bubble_font_px(0.0), MIN_FONT_PX);
    }

    /// Every whole-pixel bubble size from the legibility floor up to 16 px
    /// (the sizes dense small-text pages actually produce, where glyph
    /// volume is highest) must be in the warmup ladder — a gap would
    /// silently reintroduce first-reveal rasterization for exactly the
    /// pages with the most glyphs.
    #[test]
    fn warmup_ladder_covers_the_dense_sizes() {
        for px in (MIN_FONT_PX as u32)..=16 {
            assert!(WARMUP_SIZES.contains(&(px as f32)), "warmup ladder is missing {px}px");
        }
        // And the ladder itself only contains whole pixels — fractional
        // entries could never match a bubble_font_px result.
        for s in WARMUP_SIZES {
            assert_eq!(s.fract(), 0.0, "{s} is not a whole pixel");
        }
    }

    /// The sliced schedule must, across all steps, cover every printable-
    /// ASCII glyph at every ladder size exactly — an off-by-one in the
    /// chunk math would silently leave a cold stripe of glyphs for the
    /// first reveal to rasterize. Also pins the per-step budget (chunk
    /// length) and the schedule's endpoints.
    #[test]
    fn warmup_schedule_covers_everything_in_small_steps() {
        // Step 0 is the font-DB step, past-the-end steps are None.
        assert!(warmup_step(0).is_none());
        assert!(warmup_step(warmup_total_steps()).is_none());

        let mut seen: std::collections::HashMap<u32, String> = Default::default();
        for step in 1..warmup_total_steps() {
            let (size, chunk) = warmup_step(step).expect("in-range step");
            assert!(chunk.len() <= WARMUP_CHUNK_BYTES, "step {step} over budget");
            assert!(!chunk.is_empty(), "step {step} is a no-op");
            seen.entry(size as u32)
                .or_default()
                .push_str(chunk);
        }
        assert_eq!(seen.len(), WARMUP_SIZES.len(), "sizes missing from schedule");
        for (size, chars) in seen {
            assert_eq!(chars, WARMUP_CHARS, "size {size} does not cover WARMUP_CHARS exactly");
        }
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
