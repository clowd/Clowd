//! Which hosts draw the OCR overlay, and painting it.
//!
//! Two things share this module because they share one clock. The sweep
//! band travels down the selection while recognition runs, loops until the
//! outcome lands, then plays exactly one more pass as the reveal wave; each
//! recognised line's bubble starts rising the moment that wave's centre
//! crosses its top edge. Both are pure functions of the phase anchor's
//! elapsed time (`crate::ocr::anim`) rather than of any per-host clock,
//! which is what lets two monitors draw the same seam-crossing shape.
//!
//! The band was a fragment shader with a gaussian falloff and a rounded-rect
//! clip. egui has neither a scissor nor a shader here, so it is built as a
//! horizontal strip mesh instead: one row per sample across the band's
//! visible width, each row's x-extent the selection's rounded chord at that
//! height intersected with this monitor, each row's vertex alpha the
//! gaussian. Clamping the row edges rather than clipping them is what makes
//! two hosts tile a seam exactly.
//!
//! The geometry is authored in virtual-desktop physical pixels and converted
//! per host by [`Local`], because everything here can straddle a seam and a
//! straddling shape must come out the same physical size on both sides.

use std::sync::Arc;
use std::time::Instant;

use egui::{Color32, Context, FontFamily, FontId, Mesh, Painter, Shape};

use crate::interaction::OcrState;
use crate::ocr::{anim, OcrLine, OcrOutcome};
use crate::ui::components::pill;
use crate::ui::components::{InputCtx, Local};
use crate::ui::egui_host::REPAINT_FLOOR;
use crate::ui::shared::{aabb_intersects, UiMonitor};
use clowd_rust_core::geometry::{RectExt, ScreenRect, ScreenRectF};

/// Peak opacity of the sweep band.
pub const SWEEP_ALPHA: f32 = 0.30;
/// Rows sampled across the band's visible width (±3.5σ). The band is a
/// smooth gradient over about a tenth of the region's height, so this is
/// far more than enough for the alpha ramp to read as continuous.
const ROWS: usize = 32;
/// Extra row pitch inside the corner arcs, where the chord curves and a
/// straight edge between two far-apart rows would cut the curve.
const CORNER_ROW_PX: f32 = 2.0;

/// Fraction of the recognised line's rect height used as the bubble font
/// size. The OCR word rects hug the glyph ink (ascender to descender for a
/// typical mixed-case line), which for most faces spans a bit under one em
/// — so a font a bit under the rect height renders text of visually similar
/// size to the source. REASONED, not observed: this is the first knob to
/// turn if bubbles read as too large or too small against their lines.
const FONT_FRACTION: f32 = 0.82;

/// Fonts below this are unreadable dots; a source line that small produces
/// a small-but-legible bubble instead of a faithful-but-useless one.
const MIN_FONT_PX: f32 = 6.0;

/// Fit policy for long lines: shrink the font until the bubble fits the
/// selection width, but never below this fraction of its natural size —
/// past that, faithfulness to the source line's scale matters more than
/// containment, so the bubble keeps the floor font and is allowed a modest
/// overhang past the selection's right edge instead.
const MIN_FIT_SHRINK: f32 = 0.6;

/// What this host draws of the OCR overlay. The region and the radius are
/// virtual-desktop physical pixels, as every straddling overlay's are.
#[derive(Clone)]
pub struct OcrInputs {
    pub region: ScreenRect,
    /// The selection's corner radius in physical px, 0 for square. The
    /// region IS the frozen selection, so the band stops at the same curve
    /// the desktop pass draws the border around.
    pub radius: f32,
    pub phase: Phase,
}

/// The two phases that draw anything. `Retracting` and `Idle` do not: the
/// only exit animation is the region's colour fade, which belongs to the
/// desktop pass.
#[derive(Clone)]
pub enum Phase {
    Scanning {
        anchor: Instant,
    },
    Lifted {
        anchor: Instant,
        req: u64,
        /// The scale of the monitor holding the region's centre — ONE value
        /// for every host, so a bubble crossing a mixed-DPI seam rises by
        /// the same physical amount on both halves.
        dpi: f32,
        outcome: Arc<OcrOutcome>,
    },
}

impl PartialEq for OcrInputs {
    /// Never the recognised text itself: `req` is unique within the cycle,
    /// so two inputs carrying the same request carry the same outcome, and
    /// comparing thousands of glyphs per host per tick would not.
    fn eq(&self, o: &Self) -> bool {
        self.region == o.region
            && self.radius == o.radius
            && match (&self.phase, &o.phase) {
                (
                    Phase::Scanning {
                        anchor: a,
                    },
                    Phase::Scanning {
                        anchor: b,
                    },
                ) => a == b,
                (
                    Phase::Lifted {
                        anchor: a,
                        req: ra,
                        dpi: da,
                        ..
                    },
                    Phase::Lifted {
                        anchor: b,
                        req: rb,
                        dpi: db,
                        ..
                    },
                ) => a == b && ra == rb && da == db,
                _ => false,
            }
    }
}

/// Whether this monitor draws the OCR overlay.
///
/// While scanning, every host whose bounds strictly intersect the region
/// does. Once lifted, that OR the shaping-free bound of every bubble: a
/// floor-font overhang, a bubble's vertical padding or the four-pixel lift
/// can all reach a monitor the region itself does not touch.
///
/// The host's index is unused — both tests are geometric — but the
/// signature matches every other overlay's builder.
pub fn inputs(_index: usize, monitor: &UiMonitor, c: &InputCtx<'_>) -> Option<OcrInputs> {
    let mon = monitor.bounds.to_f32();
    let radius = c.input.selection_radius.max(0.0);
    match &c.input.ocr {
        OcrState::Scanning {
            anchor,
            region,
            ..
        } => aabb_intersects(region.to_f32(), mon).then_some(OcrInputs {
            region: *region,
            radius,
            phase: Phase::Scanning {
                anchor: *anchor,
            },
        }),
        OcrState::Lifted {
            anchor,
            req,
            region,
            dpi_scale,
            outcome,
        } => {
            let rf = region.to_f32();
            let reach = aabb_intersects(rf, mon) || aabb_intersects(estimated_bubble_bounds(&outcome.lines, rf, *dpi_scale), mon);
            reach.then(|| OcrInputs {
                region: *region,
                radius,
                phase: Phase::Lifted {
                    anchor: *anchor,
                    req: *req,
                    dpi: *dpi_scale,
                    outcome: outcome.clone(),
                },
            })
        }
        OcrState::Idle
        | OcrState::Retracting {
            ..
        } => None,
    }
}

/// Whether the reveal has fully settled at elapsed time `t`: the bottom-most
/// possible line has finished its rise, so nothing on screen moves again.
pub fn at_rest(t: f32) -> bool {
    t >= anim::reveal_start_secs(1.0) + anim::LIFT_DURATION_SECS
}

pub fn show(ctx: &Context, p: &Painter, o: &OcrInputs, monitor: &UiMonitor) {
    let local = Local::of(monitor);
    let region = o.region.to_f32();
    let clip = monitor.bounds.to_f32();
    let (anchor, animating) = match &o.phase {
        Phase::Scanning {
            anchor,
        } => (*anchor, true),
        Phase::Lifted {
            anchor,
            ..
        } => (*anchor, !at_rest(anchor.elapsed().as_secs_f32())),
    };
    let t = anchor.elapsed().as_secs_f32();

    // Scanning loops the band; Lifted plays exactly one more pass — the
    // reveal wave the bubbles rise under, wrap-aligned by the app thread so
    // the band re-enters seamlessly.
    let sweep = matches!(o.phase, Phase::Scanning { .. }) || t < anim::reveal_pass_secs();
    if sweep {
        let band = anim::sweep_band(anim::scan_phase(t));
        if let Some(mesh) = sweep_mesh(&sweep_strips(region, o.radius, band, clip), &local) {
            p.add(Shape::mesh(mesh));
        }
    }
    if let Phase::Lifted {
        dpi,
        outcome,
        ..
    } = &o.phase
    {
        bubbles(p, &local, clip, region, *dpi, outcome, t);
    }
    if animating {
        ctx.request_repaint_after(REPAINT_FLOOR);
    }
}

/// One row of the band as `(y, x0, x1, alpha)`, in virtual-desktop physical
/// px, top to bottom.
///
/// Rows are sampled uniformly across the band's visible width, with extra
/// edges through both corner zones so the curved chord is not cut by a
/// straight edge spanning it. Each row's x-extent is the region's rounded
/// chord at that height clamped to `clip`, and the rows themselves are
/// clamped to `clip` vertically — clamped, not cut, so the alpha at the
/// clamped edge is exactly the alpha the neighbouring host computes there
/// and the two tile a seam with no scissor.
pub fn sweep_strips(region: ScreenRectF, radius: f32, band: f32, clip: ScreenRectF) -> Vec<(f32, f32, f32, f32)> {
    let (l, t, r, b) = (region.left(), region.top(), region.right(), region.bottom());
    let (w, h) = (r - l, b - t);
    if w <= 0.0 || h <= 0.0 {
        return Vec::new();
    }
    let rad = radius.clamp(0.0, (w / 2.0).min(h / 2.0));
    // Past 3.5σ the gaussian is under 0.3 % of peak: nothing to draw.
    let (v0, v1) = ((band - 3.5 * anim::SWEEP_SIGMA).max(0.0), (band + 3.5 * anim::SWEEP_SIGMA).min(1.0));
    if v1 <= v0 {
        return Vec::new();
    }
    let (y_lo, y_hi) = ((t + v0 * h).max(clip.top()), (t + v1 * h).min(clip.bottom()));
    if y_hi <= y_lo {
        return Vec::new();
    }

    let mut ys: Vec<f32> = (0..=ROWS)
        .map(|i| t + (v0 + (v1 - v0) * i as f32 / ROWS as f32) * h)
        .collect();
    if rad > 0.0 {
        let mut y = t;
        while y <= t + rad {
            ys.push(y);
            y += CORNER_ROW_PX;
        }
        let mut y = b - rad;
        while y <= b {
            ys.push(y);
            y += CORNER_ROW_PX;
        }
    }
    ys.push(y_lo);
    ys.push(y_hi);
    ys.retain(|y| *y >= y_lo && *y <= y_hi);
    ys.sort_by(f32::total_cmp);
    ys.dedup();

    let chord = |y: f32| {
        let dy = ((y - (t + h / 2.0)).abs() - (h / 2.0 - rad)).max(0.0);
        let inset = rad - (rad * rad - dy * dy).max(0.0).sqrt();
        ((l + inset).max(clip.left()), (r - inset).min(clip.right()))
    };
    let alpha = |y: f32| {
        let dv = (y - t) / h - band;
        SWEEP_ALPHA * (-(dv * dv) / (2.0 * anim::SWEEP_SIGMA * anim::SWEEP_SIGMA)).exp()
    };
    ys.iter()
        .map(|&y| {
            let (x0, x1) = chord(y);
            (y, x0, x1, alpha(y))
        })
        // A row narrower than nothing, or one whose alpha rounds to zero in
        // eight bits, is not worth two triangles.
        .filter(|r| r.2 > r.1 && r.3 * 255.0 >= 0.5)
        .collect()
}

/// Stitch consecutive rows into a quad strip, in this host's points. The
/// vertex colours are premultiplied white, exactly what the old shader
/// wrote, so the interpolation between two rows is the same gradient.
pub fn sweep_mesh(rows: &[(f32, f32, f32, f32)], local: &Local) -> Option<Mesh> {
    let mut m = Mesh::default();
    for w in rows.windows(2) {
        let ((y0, a0, b0, al0), (y1, a1, b1, al1)) = (w[0], w[1]);
        let c0 = Color32::from_white_alpha((al0 * 255.0).round() as u8);
        let c1 = Color32::from_white_alpha((al1 * 255.0).round() as u8);
        let i = m.vertices.len() as u32;
        m.colored_vertex(local.pos(a0, y0), c0);
        m.colored_vertex(local.pos(b0, y0), c0);
        m.colored_vertex(local.pos(a1, y1), c1);
        m.colored_vertex(local.pos(b1, y1), c1);
        m.add_triangle(i, i + 1, i + 2);
        m.add_triangle(i + 1, i + 3, i + 2);
    }
    (!m.indices.is_empty()).then_some(m)
}

/// Every bubble this host can see, laid out and painted.
///
/// Every line is laid out on every pass: egui's galley cache is the cache,
/// and the first Lifted pass — the one with the band still off the top edge
/// and nothing yet revealed — is where the whole page's glyphs enter the
/// atlas, which is why a line below its reveal time is measured before it is
/// skipped.
fn bubbles(p: &Painter, local: &Local, clip: ScreenRectF, region: ScreenRectF, dpi: f32, outcome: &OcrOutcome, t: f32) {
    for line in outcome
        .lines
        .iter()
        .filter(|l| !l.text.trim().is_empty())
    {
        let mut font_px = bubble_font_px(line.rect.height());
        let lay = |px: f32| {
            p.layout_no_wrap(
                line.text.clone(),
                FontId::new(px / local.ppp, FontFamily::Monospace),
                Color32::PLACEHOLDER,
            )
        };
        let mut galley = lay(font_px);
        // One shrink-and-relayout pass at most, against the pre-shrink
        // padding (slightly conservative, and it bounds the work per line).
        let shrink = fit_shrink(galley.size().x * local.ppp, region.width(), bubble_pad_h(font_px));
        if shrink < 1.0 {
            font_px = (font_px * shrink).floor().max(MIN_FONT_PX);
            galley = lay(font_px);
        }

        // The pill's metrics scale with the bubble's own font, not with the
        // monitor's DPI: a headline bubble keeps the chip's proportions
        // rather than wearing an 11 pt chip's frame.
        let pad_h = bubble_pad_h(font_px);
        let pad_v = (pill::PAD_V * font_px / pill::FONT_PT)
            .floor()
            .max(2.0);
        let radius = (pill::RADIUS * font_px / pill::FONT_PT)
            .floor()
            .max(2.0);
        let text = galley.size() * local.ppp;
        let (w, h) = (2.0 * pad_h + text.x, 2.0 * pad_v + font_px * 1.2);
        let x = bubble_x(line.rect.left() - pad_h, w, region.left(), region.right());
        let cy = (line.rect.top() + line.rect.bottom()) / 2.0;

        let e = anim::reveal_progress(t, anim::line_rel_top(line.rect.top(), region.top(), region.height()));
        if e <= 0.001 {
            continue;
        }
        let dy = -e * anim::LIFT_PX * dpi;
        let rect = ScreenRectF::from_xy_size(x, cy - h / 2.0 + dy, w, h);
        if !aabb_intersects(rect, clip) {
            continue;
        }
        // Pixel rounding off, so the rise is sub-pixel smooth rather than
        // stepping four times over its 0.28 s.
        p.add(pill::body(local.rect(rect), radius / local.ppp, e, true).with_round_to_pixels(false));
        p.galley(
            local.pos(x + pad_h, rect.top() + (h - text.y) / 2.0),
            galley,
            pill::TEXT.gamma_multiply(e),
        );
    }
}

/// Conservative bounding rect (virtual-desktop px) that every bubble of this
/// outcome is guaranteed to stay inside, computed WITHOUT laying anything
/// out — the whole point is to let a host decide it draws nothing before it
/// measures a page. Overestimates on purpose:
///
/// * Left edge: [`bubble_x`] clamps every pill to at least the region's left
///   edge, so the region's own left is exact.
/// * Width: bounded by characters × 1.5 em — the widest real advances are
///   about 1 em (full-width CJK; Latin is nearer 0.6) and the extra half em
///   absorbs any exotic fallback face. Fit-shrink only ever narrows.
/// * Vertically: a pill is centred on its line and its height is padding
///   plus one line box, so a full bubble height either side of the line's
///   centre covers both directions; the rise only ever moves bubbles up.
fn estimated_bubble_bounds(lines: &[OcrLine], region: ScreenRectF, dpi: f32) -> ScreenRectF {
    let mut right = region.right();
    let mut top = region.top();
    let mut bottom = region.bottom();
    for line in lines {
        let font_px = bubble_font_px(line.rect.height());
        let pad_h = bubble_pad_h(font_px);
        let pad_v = (pill::PAD_V * font_px / pill::FONT_PT)
            .floor()
            .max(2.0);
        let bubble_h = pad_v * 2.0 + font_px * 1.2;
        let est_w = pad_h * 2.0 + line.text.chars().count() as f32 * font_px * 1.5;
        let left = bubble_x(line.rect.left() - pad_h, est_w, region.left(), region.right());
        right = right.max(left + est_w);
        let cy = (line.rect.top() + line.rect.bottom()) * 0.5;
        top = top.min(cy - bubble_h);
        bottom = bottom.max(cy + bubble_h);
    }
    ScreenRectF::from_exact(region.left(), top - anim::LIFT_PX * dpi, right, bottom)
}

/// Horizontal padding for a bubble of the given font — the chip's padding
/// scaled to the bubble's own type size.
fn bubble_pad_h(font_px: f32) -> f32 {
    (pill::PAD_H * font_px / pill::FONT_PT)
        .floor()
        .max(2.0)
}

/// Font size that renders text at visually the same height as the recognised
/// line — see [`FONT_FRACTION`]. Floored to a whole pixel: sub-pixel sizes
/// are indistinguishable at these magnitudes, and whole ones let two lines
/// of the same height share one set of rasterised glyphs.
fn bubble_font_px(line_h: f32) -> f32 {
    (line_h * FONT_FRACTION)
        .floor()
        .max(MIN_FONT_PX)
}

/// How much the font must shrink for the bubble to fit the region's width:
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
    use crate::interaction::InteractionState;
    use crate::ui::shared::monitor_index_at;
    use clowd_rust_core::geometry::ScreenPointF;

    fn region() -> ScreenRectF {
        ScreenRectF::from_xy_size(100.0, 200.0, 400.0, 300.0)
    }

    /// A clip big enough to hold anything the tests build, so a row is
    /// clamped only where a test asks for it.
    fn wide_clip() -> ScreenRectF {
        ScreenRectF::from_exact(-10_000.0, -10_000.0, 10_000.0, 10_000.0)
    }

    fn monitors() -> [UiMonitor; 2] {
        [
            UiMonitor {
                bounds: ScreenRect::from_xy_size(0, 0, 1920, 1080),
                dpi_scale: 1.0,
                is_primary: true,
            },
            UiMonitor {
                bounds: ScreenRect::from_xy_size(1920, 0, 1920, 1080),
                dpi_scale: 1.0,
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

    fn outcome(lines: Vec<OcrLine>) -> Arc<OcrOutcome> {
        Arc::new(OcrOutcome {
            lines,
            full_text: String::new(),
            text_angle: 0.0,
        })
    }

    /// The band's shape: alpha climbs to the row nearest its centre and
    /// falls away either side, which is the whole of what the old gaussian
    /// fragment did.
    #[test]
    fn sweep_rows_are_monotone_either_side_of_the_band() {
        let rows = sweep_strips(region(), 0.0, 0.5, wide_clip());
        assert!(rows.len() > 8, "{} rows", rows.len());
        let peak = rows
            .iter()
            .enumerate()
            .max_by(|a, b| a.1 .3.total_cmp(&b.1 .3))
            .expect("a peak row")
            .0;
        for w in rows[..=peak].windows(2) {
            assert!(w[1].3 >= w[0].3, "rising side dips at y = {}", w[1].0);
        }
        for w in rows[peak..].windows(2) {
            assert!(w[1].3 <= w[0].3, "falling side climbs at y = {}", w[1].0);
        }
    }

    /// The loop wrap must happen with the band invisible, or every pass
    /// would start and end with a half band popping at an edge.
    #[test]
    fn sweep_is_invisible_at_the_wrap() {
        for phase in [0.0, 1.0] {
            let band = anim::sweep_band(phase);
            let peak = sweep_strips(region(), 0.0, band, wide_clip())
                .iter()
                .map(|r| r.3)
                .fold(0.0f32, f32::max);
            assert!(peak < 0.012 * SWEEP_ALPHA, "phase {phase} shows {peak}");
        }
    }

    /// The rounded selection's chord: rows inside a corner arc are inset on
    /// both sides, rows on the straight part are the region's full width.
    #[test]
    fn corner_rows_are_narrower_than_straight_rows() {
        let r = region();
        let rad = 16.0;
        // A band centred on the very top of the region reaches into the top
        // corner arc.
        let rows = sweep_strips(r, rad, 0.0, wide_clip());
        let top_row = rows.first().expect("rows at the top edge");
        assert!(top_row.1 > r.left(), "the top row is not inset");
        assert!(top_row.2 < r.right(), "the top row is not inset");

        let middle = sweep_strips(r, rad, 0.5, wide_clip());
        for row in &middle {
            assert_eq!((row.1, row.2), (r.left(), r.right()), "a straight row is inset");
        }
    }

    /// Two hosts tile a seam by clamping their rows to their own monitor:
    /// nothing is drawn outside it, and the clamped edge keeps the alpha the
    /// neighbour computes there.
    #[test]
    fn rows_are_clamped_to_the_clip() {
        let r = region();
        let clip = ScreenRectF::from_exact(200.0, 300.0, 400.0, 400.0);
        let rows = sweep_strips(r, 0.0, 0.5, clip);
        assert!(!rows.is_empty());
        for (y, x0, x1, _) in rows {
            assert!(y >= clip.top() && y <= clip.bottom(), "row at {y} escapes the clip");
            assert!(x0 >= clip.left() && x1 <= clip.right(), "row spans {x0}..{x1}");
        }
        // A monitor the band cannot reach draws nothing at all.
        let elsewhere = ScreenRectF::from_exact(2000.0, 0.0, 3000.0, 1000.0);
        assert!(sweep_strips(r, 0.0, 0.5, elsewhere).is_empty());
    }

    /// The reach rule: a host draws the overlay when it meets the region or
    /// the bubbles' conservative bound, and nothing when it meets neither.
    #[test]
    fn a_host_whose_monitor_misses_region_and_bubble_bounds_gets_no_inputs() {
        let m = monitors();
        let mut input = InteractionState::new();
        input.captured = true;
        input.virtual_cursor = ScreenPointF::new(300.0, 400.0);
        let region = ScreenRect::from_xy_size(100, 200, 400, 300);

        input.ocr = OcrState::Scanning {
            anchor: Instant::now(),
            req: 1,
            region,
        };
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_some(), "the region's own host");
        assert!(inputs(1, &m[1], &ctx(&input, &m)).is_none(), "a monitor the region misses");

        // A line hugging the region's right edge whose bubble is far wider
        // than the region: the overhang reaches over the seam even though
        // the region does not.
        let line = OcrLine {
            text: "a".repeat(200),
            rect: ScreenRectF::from_xy_size(1800.0, 250.0, 100.0, 20.0),
        };
        input.ocr = OcrState::Lifted {
            anchor: Instant::now(),
            req: 1,
            region: ScreenRect::from_xy_size(1000, 200, 900, 300),
            dpi_scale: 1.0,
            outcome: outcome(vec![line]),
        };
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_some());
        assert!(
            inputs(1, &m[1], &ctx(&input, &m)).is_some(),
            "the overhanging bubble reaches the neighbour"
        );

        // Nothing at all outside the two drawing phases.
        input.ocr = OcrState::Retracting {
            anchor: Instant::now(),
        };
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none());
        input.ocr = OcrState::Idle;
        assert!(inputs(0, &m[0], &ctx(&input, &m)).is_none());
    }

    /// The size mapping is what makes a bubble read as "this line, lifted":
    /// proportional to the source, whole-pixel, floored at legibility.
    #[test]
    fn font_tracks_line_height_with_a_floor() {
        assert_eq!(bubble_font_px(100.0), 82.0);
        // 20 * 0.82 = 16.4 -> floored to a whole pixel.
        assert_eq!(bubble_font_px(20.0), 16.0);
        // Tiny source lines hit the legibility floor instead of vanishing.
        assert_eq!(bubble_font_px(4.0), MIN_FONT_PX);
        assert_eq!(bubble_font_px(0.0), MIN_FONT_PX);
    }

    /// Fit policy: no shrink when it fits, proportional shrink when it does
    /// not, hard floor after which overhang is accepted.
    #[test]
    fn fit_shrink_clamps_between_floor_and_one() {
        // Fits: untouched.
        assert_eq!(fit_shrink(100.0, 200.0, 6.0), 1.0);
        // Slightly over: shrinks by exactly the overflow ratio.
        let s = fit_shrink(200.0, 166.0, 8.0); // avail = 150
        assert!((s - 0.75).abs() < 1e-3);
        // Wildly over: floored, never microscopic.
        assert_eq!(fit_shrink(10_000.0, 200.0, 6.0), MIN_FIT_SHRINK);
        // Degenerate inputs must not divide by zero or go negative.
        assert_eq!(fit_shrink(0.0, 200.0, 6.0), 1.0);
        assert_eq!(fit_shrink(100.0, 4.0, 6.0), 1.0);
    }

    /// Placement policy: pinned to the line's left edge, clamped inside the
    /// region, and left-aligned (text start visible) when wider than it.
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
        // Negative-origin regions (a monitor left of the primary).
        assert_eq!(bubble_x(-1900.0, 100.0, -1920.0, -1520.0), -1900.0);
        assert_eq!(bubble_x(-1500.0, 100.0, -1920.0, -1520.0), -1620.0);
    }
}
