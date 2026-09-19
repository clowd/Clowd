//! GPU button-panel renderer: the floating tray.
//!
//! Per-frame work on this render thread, in the order the pieces are
//! emitted (rect push order is paint order within the rect pipeline, and
//! every rect paints under every icon, which paints under every glyph):
//!   1. Check [`panel_visibility`] — early out if this monitor isn't the
//!      target.
//!   2. Animate each button's hover veil opacity toward 1 (hovered) or 0,
//!      linearly over [`HOVER_FADE_SECS`].
//!   3. Rects: the blurred tray shadow, the tray fill, the 1 px inner
//!      ring, one segment fill per button (with the white hover veil
//!      mixed in), then — after the labels are shaped — the accelerator
//!      underline bars.
//!   4. Icons: the Clowd emblem centred in its slot, then one icon per
//!      button at the left inset; all from one atlas keyed by
//!      `(icon px, emblem mark px)`.
//!   5. Text: the bold "W × H" readout and the regular Title-case labels,
//!      cached as cosmic-text `Buffer`s and returned by [`PanelRenderer::text_areas`].
//!
//! Every size comes from the layout's [`PanelMetrics`](crate::ui::components::panel::layout::PanelMetrics);
//! the DPI is read only for the shadow's blur and the 100 % double-draw
//! gate. Click routing runs on the app thread via the same shared layout
//! function — this renderer never sends anything back.

use crate::ui::gpu::text::{Attrs, Buffer, Color, Family, Metrics, Shaping, TextArea, TextBounds, Weight, Wrap};

use crate::ui::components::panel::assets::SVG_CLOWD_LOGO;
use crate::ui::components::panel::layout::{text_width_px, PanelLayout};
use crate::ui::components::panel::model::{PanelButtonSet, MAX_PANEL_BUTTONS, PANEL_ICONS};
use crate::ui::gpu::hints::AA;
use crate::ui::gpu::icon::{IconAtlas, IconInstance};
use crate::ui::gpu::rect::RectInstance;
use crate::ui::gpu::text::{TextStack, FAMILY_MONO};
use crate::ui::shared::{panel_visibility, UiMonitor, UiSharedState};
use clowd_rust_core::geometry::RectExt;

// Colours from the C# `TrayTokens` (display space, straight alpha).
/// Tray chassis fill, `#25272B`.
const TRAY_RGBA: [f32; 4] = [0x25 as f32 / 255.0, 0x27 as f32 / 255.0, 0x2B as f32 / 255.0, 1.0];
/// Button segment fill, `#3A3E44`.
const SEG_RGBA: [f32; 4] = [0x3A as f32 / 255.0, 0x3E as f32 / 255.0, 0x44 as f32 / 255.0, 1.0];
/// The 1 px ring just inside the tray edge: white at 6 %.
const RING_RGBA: [f32; 4] = [1.0, 1.0, 1.0, 0.06];
/// `ShadowCompact` colour `#59000000`: black at 35 %.
const SHADOW_RGBA: [f32; 4] = [0.0, 0.0, 0.0, 0x59 as f32 / 255.0];
/// Readout text: white at 80 % (workbench `.area{opacity:.8}`).
const AREA_TEXT_RGBA: [u8; 4] = [0xFF, 0xFF, 0xFF, 204];
/// Label text: plain white.
const LABEL_RGBA: [u8; 4] = [0xFF, 0xFF, 0xFF, 0xFF];
/// Hover veil: white at 12 % over the segment fill. On an opaque fill,
/// `mix(fill, white, 0.12)` is the same colour, so it rides the rect
/// shader's `lighten` parameter.
const HOVER_VEIL_ALPHA: f32 = 0.12;
/// `TrayTokens.FillTransition`: 180 ms, linear (Avalonia's default easing).
const HOVER_FADE_SECS: f32 = 0.18;
/// `ShadowCompact` offset: `0 3 10 0`, y = 3 logical px.
const SHADOW_OFFSET_Y_UNSCALED: f32 = 3.0;
/// `ShadowCompact` blur 10 as a gaussian sigma: Skia's `0.288675 * blur + 0.5`.
const SHADOW_SIGMA_UNSCALED: f32 = 3.4;

/// Buffer slot of the bold "W × H" readout.
const IDX_AREA: usize = 0;
/// First of `MAX_PANEL_BUTTONS` label buffer slots, one per button slot.
const IDX_LABEL_BASE: usize = 1;
/// Atlas entry of the Clowd emblem: appended after every `PANEL_ICONS`
/// entry so `icon_id`s keep indexing the atlas directly.
const EMBLEM_SLOT: usize = PANEL_ICONS.len();

/// One cosmic-text buffer + content cache for change detection.
struct CachedBuffer {
    buffer: Buffer,
    /// Shaped with `Weight::BOLD` when set (the readout); regular otherwise.
    bold: bool,
    last_text: String,
    last_font_px: f32,
    last_underline_idx: Option<usize>,
}

impl CachedBuffer {
    fn new(ts: &mut TextStack, font_px: f32, bold: bool) -> Self {
        let metrics = Metrics::new(font_px, font_px * 1.2);
        let mut buffer = Buffer::new(&mut ts.font_system, metrics);
        buffer.set_wrap(Wrap::None);
        Self {
            buffer,
            bold,
            last_text: String::new(),
            last_font_px: font_px,
            last_underline_idx: None,
        }
    }

    fn set(&mut self, ts: &mut TextStack, text: &str, font_px: f32, underline_idx: Option<usize>) {
        let font_changed = (font_px - self.last_font_px).abs() > 0.25;
        let text_changed = text != self.last_text;
        let ul_changed = underline_idx != self.last_underline_idx;
        if !font_changed && !text_changed && !ul_changed {
            return;
        }
        if font_changed {
            self.buffer
                .set_metrics(Metrics::new(font_px, font_px * 1.2));
            self.last_font_px = font_px;
        }
        // The whole string is shaped uniformly; the accelerator underline
        // is a rect the caller draws from `glyph_bounds_at_byte`. Cascadia
        // Mono, not Code: same advances, no `calt` ligature table, so the
        // glyph count always equals the char count the layout sized by.
        let weight = if self.bold { Weight::BOLD } else { Weight::NORMAL };
        let attrs = Attrs::new()
            .family(Family::Name(FAMILY_MONO))
            .weight(weight);
        self.buffer
            .set_text(text, &attrs, Shaping::Advanced, None);
        self.buffer
            .shape_until_scroll(&mut ts.font_system, false);
        if text_changed {
            self.last_text.clear();
            self.last_text.push_str(text);
        }
        self.last_underline_idx = underline_idx;
    }

    /// Return the pixel x offset + advance of the glyph whose source
    /// byte index matches `byte_idx`. Uses the first shaped line — button
    /// labels are always one line. Returns `None` if the index doesn't
    /// land on a glyph start (only possible with mid-cluster indices,
    /// which our ASCII-only button labels don't produce).
    fn glyph_bounds_at_byte(&self, byte_idx: usize) -> Option<(f32, f32)> {
        let run = self.buffer.layout_runs().next()?;
        run.glyphs
            .iter()
            .find(|g| g.start == byte_idx)
            .map(|g| (g.x, g.w))
    }
}

#[derive(Clone, Copy)]
struct PositionedText {
    buffer_idx: usize,
    x: f32,
    y: f32,
    color: [u8; 4],
    /// Bold text skips the 100 % double-draw (it would double-thicken).
    bold: bool,
}

/// Rounded rect on the intended `(l, t, r, b)`: the quad is inflated by
/// [`AA`] on every side and `AA` is passed as `aa_pad` so the SDF edge
/// lands exactly on the rect. `border_px` of 0 (or a transparent
/// `border`) disables the border; `lighten` mixes the fill toward white.
fn rounded(rect: (f32, f32, f32, f32), radius: f32, fill: [f32; 4], border: [f32; 4], border_px: f32, lighten: f32) -> RectInstance {
    let (l, t, r, b) = rect;
    RectInstance {
        dest_px: [l - AA, t - AA, r + AA, b + AA],
        fill_rgba: fill,
        border_rgba: border,
        params: [border_px, lighten, radius, AA],
    }
}

/// `(sigma, pad)` for the tray shadow at `dpi`: the blur's gaussian sigma
/// in physical px, and how far the shadow quad must extend past its body
/// for the falloff to reach ~0 (`ceil(3 sigma) + 1`). Pure so a test can
/// pin it. Unrounded on purpose: these parametrise a blurred shape, not
/// hit-tested geometry, and Skia paints the C# strips at the fractional
/// `3 * scale` too.
fn shadow_params(dpi: f32) -> (f32, f32) {
    let sigma = SHADOW_SIGMA_UNSCALED * dpi;
    (sigma, (3.0 * sigma).ceil() + 1.0)
}

pub struct PanelRenderer {
    /// Parsed SVG trees, indexed by `ButtonDef::icon_id` (i.e. by
    /// position in `PANEL_ICONS`, *not* by button index — the two button
    /// sets share icons and have different lengths). Kept around so the
    /// atlas can be rebuilt at a new DPI without re-parsing.
    svg_trees: Vec<usvg::Tree>,
    /// The Clowd logo drawn as the tray emblem; atlas entry [`EMBLEM_SLOT`].
    emblem_tree: usvg::Tree,
    /// CPU-rasterized icon atlas, built lazily on the first `prepare()`
    /// and rebuilt whenever either target size changes.
    atlas: Option<IconAtlas>,
    /// `(icon px, emblem mark px)` the atlas was built for.
    last_atlas_key: (u32, u32),
    /// [`IDX_AREA`]: the bold readout; then [`IDX_LABEL_BASE`] +
    /// `MAX_PANEL_BUTTONS` regular label buffers, one per button slot.
    buffers: Vec<CachedBuffer>,
    /// Text positions captured during the latest `prepare()`.
    positions: Vec<PositionedText>,
    /// Per-button hover veil opacity in `[0, 1]`, animated each frame.
    hover_amounts: [f32; MAX_PANEL_BUTTONS],
    /// Which set `hover_amounts` was last animated for, so a set swap can
    /// clear it — see the reset in `prepare`.
    last_set: Option<PanelButtonSet>,
    /// "W × H", rebuilt only when the selection changes.
    area_str: String,
    last_selection: Option<clowd_rust_core::geometry::ScreenRect>,
    dpi_scale: f32,
}

impl PanelRenderer {
    pub fn new(ts: &mut TextStack) -> Self {
        let usvg_opts = usvg::Options::default();
        let parse = |what: &str, bytes: &[u8]| match usvg::Tree::from_data(bytes, &usvg_opts) {
            Ok(t) => t,
            Err(e) => {
                log::error!("failed to parse panel SVG {what}: {e:?}");
                usvg::Tree::from_str("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", &usvg::Options::default()).expect("empty SVG parses")
            }
        };
        // The whole union, not one set's icons: the atlas is built once
        // and both sets index it by `icon_id`, so every entry must have
        // a tree at its own index even if the set on screen never uses
        // it.
        let svg_trees: Vec<usvg::Tree> = PANEL_ICONS
            .iter()
            .enumerate()
            .map(|(i, bytes)| parse(&i.to_string(), bytes))
            .collect();
        let emblem_tree = parse("emblem", SVG_CLOWD_LOGO);

        // Labels are keyed by *button slot*, not by icon, so this sizes
        // off the longest set rather than the icon table.
        let mut buffers = Vec::with_capacity(IDX_LABEL_BASE + MAX_PANEL_BUTTONS);
        buffers.push(CachedBuffer::new(ts, 12.0, true)); // readout
        for _ in 0..MAX_PANEL_BUTTONS {
            buffers.push(CachedBuffer::new(ts, 12.0, false));
        }

        Self {
            svg_trees,
            emblem_tree,
            atlas: None,
            last_atlas_key: (0, 0),
            buffers,
            positions: Vec::new(),
            hover_amounts: [0.0; MAX_PANEL_BUTTONS],
            last_set: None,
            area_str: String::new(),
            last_selection: None,
            dpi_scale: 1.0,
        }
    }

    pub fn atlas(&self) -> Option<&IconAtlas> {
        self.atlas.as_ref()
    }

    /// Run the full panel logic. Appends rect instances to `rects` and
    /// icon instances to `icon_draws`, caches text positions internally.
    #[allow(clippy::too_many_arguments)]
    pub fn prepare(
        &mut self,
        device: &crate::gxi::Device,
        queue: &crate::gxi::Queue,
        ts: &mut TextStack,
        state: &UiSharedState,
        this_monitor: &UiMonitor,
        rects: &mut Vec<RectInstance>,
        icon_draws: &mut Vec<IconInstance>,
        dt_secs: f32,
    ) {
        self.positions.clear();

        let Some(vis) = panel_visibility(state) else {
            self.hover_amounts.fill(0.0);
            return;
        };
        if vis.monitor.bounds != this_monitor.bounds {
            self.hover_amounts.fill(0.0);
            return;
        }

        let layout: PanelLayout = vis.layout;
        // The click that swaps the set is, by definition, on a button the
        // cursor is hovering — so `hover_amounts[idx]` is at full
        // strength at the exact moment the strip changes underneath it.
        // Without this reset a ghost highlight bleeds onto whichever
        // button inherits that slot, on 100% of entries *and* exits, and
        // then fades away as if the user had moused over it.
        if self.last_set != Some(layout.set) {
            self.hover_amounts.fill(0.0);
            self.last_set = Some(layout.set);
        }
        let m = layout.metrics;
        // Only the shadow blur and the double-draw gate read the DPI;
        // every size is an integer the layout already scaled.
        let dpi = vis.monitor.dpi_scale.max(0.1);
        self.dpi_scale = dpi;

        // The strip AS LAID OUT — not `layout.set.defs()`, which is the
        // full table including any button the user switched off. Indexing
        // that by a button slot would draw a label and icon one button
        // ahead of the rect they sit in.
        let defs = layout.defs();
        let buttons = layout.buttons();

        // Hover veil, linear fade. Hit-test happens in VD coords since
        // that's what the broadcast cursor + layout both use.
        let cursor = state.virtual_cursor;
        let hovered_idx = layout.hit_test(cursor.x, cursor.y);
        let fade_step = dt_secs / HOVER_FADE_SECS;
        for (i, amount) in self.hover_amounts[..buttons.len()]
            .iter_mut()
            .enumerate()
        {
            let target = if Some(i) == hovered_idx { 1.0 } else { 0.0 };
            let diff = target - *amount;
            if diff.abs() <= fade_step {
                *amount = target;
            } else {
                *amount += fade_step.copysign(diff);
            }
        }

        let mon = this_monitor.bounds;
        let to_local = |r: clowd_rust_core::geometry::ScreenRect| -> (f32, f32, f32, f32) {
            (
                (r.left() - mon.left()) as f32,
                (r.top() - mon.top()) as f32,
                (r.right() - mon.left()) as f32,
                (r.bottom() - mon.top()) as f32,
            )
        };

        // Chassis: shadow under everything, then the fill, then the ring
        // as its own transparent-fill instance (the rounded mode's border
        // REPLACES the fill under the band, so a translucent border on
        // the fill instance would punch a see-through ring).
        let tray = to_local(layout.tray_rect);
        let (tl, tt, tr, tb) = tray;
        let cr = m.corner_radius as f32;
        let off = SHADOW_OFFSET_Y_UNSCALED * dpi;
        let (sigma, shadow_pad) = shadow_params(dpi);
        rects.push(RectInstance::blurred_shadow(
            (tl, tt + off, tr, tb + off),
            cr,
            sigma,
            shadow_pad,
            SHADOW_RGBA,
        ));
        rects.push(rounded(tray, cr, TRAY_RGBA, [0.0; 4], 0.0, 0.0));
        rects.push(rounded(tray, cr, [0.0; 4], RING_RGBA, m.ring_px as f32, 0.0));

        // Button segments with the hover veil. The emblem slot and the
        // readout sit straight on the tray fill and get nothing here.
        for (b, amount) in buttons.iter().zip(&self.hover_amounts) {
            rects.push(rounded(to_local(*b), cr, SEG_RGBA, [0.0; 4], 0.0, HOVER_VEIL_ALPHA * amount));
        }

        // One atlas for the button icons and the emblem, keyed on both
        // sizes so a DPI change rebuilds it exactly once.
        let key = (m.icon as u32, m.emblem_mark as u32);
        if self.atlas.is_none() || self.last_atlas_key != key {
            let mut entries: Vec<(&usvg::Tree, u32)> = self
                .svg_trees
                .iter()
                .map(|t| (t, key.0))
                .collect();
            entries.push((&self.emblem_tree, key.1));
            debug_assert_eq!(entries.len(), EMBLEM_SLOT + 1);
            self.atlas = Some(IconAtlas::build(device, queue, &entries));
            self.last_atlas_key = key;
        }
        let atlas = self.atlas.as_ref().unwrap();

        // Emblem: the mark centred in its slot (4 px inset in a 40 slot at
        // 100 %; centred across the column's inner width in a column).
        // Integer operands, so the floor is an integer division.
        let (el, et, er, eb) = to_local(layout.emblem_rect);
        let mark = m.emblem_mark as f32;
        let ex = (el + ((er - el) - mark) / 2.0).floor();
        let ey = (et + ((eb - et) - mark) / 2.0).floor();
        icon_draws.push(IconInstance {
            dest_px: [ex, ey, ex + mark, ey + mark],
            uv: atlas.uv_for(EMBLEM_SLOT),
            alpha_mul: 1.0,
            _pad: [0.0; 3],
        });

        // Button content: icon at the left inset, label after the gap,
        // both centred on the button's cross axis. The same expression
        // serves both orientations — a row button is content-sized, so
        // left-aligned is centred; a column button is full-width with its
        // content left-aligned at the inset.
        let icon = m.icon as f32;
        let line_h = m.font_px as f32 * 1.2;
        let label_dx = (m.icon + m.icon_label_gap) as f32;
        let underline_h = m.underline_px as f32;
        for (i, (b, def)) in buttons.iter().zip(defs).enumerate() {
            let (l, t, _r, bt) = to_local(*b);
            let icon_left = l + m.button_pad_h as f32;
            let icon_top = t + ((m.button - m.icon) / 2) as f32;
            icon_draws.push(IconInstance {
                dest_px: [icon_left, icon_top, icon_left + icon, icon_top + icon],
                // Keyed by the button's icon, not its slot: the OCR strip
                // reuses UPLOAD/COPY/EXIT from the capture strip and sits
                // at different indices.
                uv: atlas.uv_for(def.icon_id),
                alpha_mul: 1.0,
                _pad: [0.0; 3],
            });

            let buf_idx = IDX_LABEL_BASE + i;
            self.buffers[buf_idx].set(ts, def.label, m.font_px as f32, Some(def.underline_idx));
            let label_x = icon_left + label_dx;
            // Round Y to the pixel grid. cosmic-text's `physical()`
            // truncates the Y component to hint on the baseline; a
            // fractional `top` would snap the text one pixel upward.
            let label_y = (t + ((bt - t) - line_h) / 2.0).round();
            self.positions.push(PositionedText {
                buffer_idx: buf_idx,
                x: label_x,
                y: label_y,
                color: LABEL_RGBA,
                bold: false,
            });

            // Accelerator underline: a hairline under the glyph, pushed
            // after every fill so it paints on top of its segment.
            if let Some((gx, gw)) = self.buffers[buf_idx].glyph_bounds_at_byte(def.underline_idx) {
                let u_y = (label_y + line_h).round() - underline_h;
                rects.push(RectInstance::filled(
                    label_x + gx,
                    u_y,
                    label_x + gx + gw,
                    u_y + underline_h,
                    [1.0; 4],
                ));
            }
        }

        // Readout "W × H" from the UNCLIPPED selection — the digit count
        // the layout sized the segment by. Rebuilt only when the selection
        // changes; `CachedBuffer::set` no-ops on unchanged text.
        if self.last_selection != state.selection {
            self.area_str.clear();
            if let Some(s) = state.selection {
                use std::fmt::Write;
                let _ = write!(self.area_str, "{} \u{00D7} {}", s.width(), s.height());
            }
            self.last_selection = state.selection;
        }
        self.buffers[IDX_AREA].set(ts, &self.area_str, m.font_px as f32, None);
        let (al, at, ar, ab) = to_local(layout.area_rect);
        // The integer width the layout reserved, not the measured width,
        // so the text stays on the grid: in a row this is exactly the
        // readout padding; in a column it centres in the inner width,
        // which can leave an odd remainder, so floor it — cosmic-text
        // keeps a fractional X and would rasterise every glyph half a
        // pixel softer than the labels beneath it.
        let text_w = text_width_px(self.area_str.chars().count(), m.font_px) as f32;
        self.positions.push(PositionedText {
            buffer_idx: IDX_AREA,
            x: (al + ((ar - al) - text_w) / 2.0).floor(),
            y: (at + ((ab - at) - line_h) / 2.0).round(),
            color: AREA_TEXT_RGBA,
            bold: true,
        });
    }

    pub fn text_areas<'a>(&'a self, viewport_px: (u32, u32), out: &mut Vec<TextArea<'a>>) {
        let (vw, vh) = (viewport_px.0 as i32, viewport_px.1 as i32);
        for p in &self.positions {
            let area = TextArea {
                buffer: &self.buffers[p.buffer_idx].buffer,
                left: p.x,
                top: p.y,
                scale: 1.0,
                bounds: TextBounds {
                    left: 0,
                    top: 0,
                    right: vw,
                    bottom: vh,
                },
                default_color: Color::rgba(p.color[0], p.color[1], p.color[2], p.color[3]),
            };
            // At 100 % the regular 12 px labels are drawn twice to fake a
            // little weight; the bold readout would only double-thicken.
            if self.dpi_scale < 1.01 && !p.bold {
                out.push(area.clone());
            }
            out.push(area);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ui::gpu::text::{FONT_CODE_BOLD, FONT_CODE_REGULAR, FONT_MONO_BOLD, FONT_MONO_REGULAR};

    /// 1200 / 2048 em: the single advance every glyph of the bundled
    /// Cascadia faces has (see `text_width_px`).
    const ADVANCE_EM: f32 = 0.5859375;

    /// A font system over the bundled bytes only, so a system-installed
    /// Cascadia cannot shadow them.
    fn test_font_system() -> cosmic_text::FontSystem {
        let mut db = cosmic_text::fontdb::Database::new();
        for f in [FONT_MONO_REGULAR, FONT_MONO_BOLD, FONT_CODE_REGULAR, FONT_CODE_BOLD] {
            db.load_font_data(f.to_vec());
        }
        cosmic_text::FontSystem::new_with_locale_and_db("en-US".to_string(), db)
    }

    /// Shape `text` exactly as `CachedBuffer::set` does.
    fn shape(fs: &mut cosmic_text::FontSystem, text: &str, px: i32, bold: bool) -> Buffer {
        let mut buf = Buffer::new(fs, Metrics::new(px as f32, px as f32 * 1.2));
        buf.set_wrap(Wrap::None);
        let attrs = Attrs::new()
            .family(Family::Name(FAMILY_MONO))
            .weight(if bold { Weight::BOLD } else { Weight::NORMAL });
        buf.set_text(text, &attrs, Shaping::Advanced, None);
        buf.shape_until_scroll(fs, false);
        buf
    }

    /// Every label of both sets plus representative readouts.
    fn sample_texts() -> Vec<&'static str> {
        let mut v: Vec<&'static str> = PanelButtonSet::ALL
            .iter()
            .flat_map(|s| s.defs().iter().map(|d| d.label))
            .collect();
        v.extend(["600 \u{00D7} 400", "1920 \u{00D7} 1080", "10000 \u{00D7} 10040"]);
        v
    }

    /// The layout sizes text as `chars * px * 75/128` without measuring;
    /// this pins that cosmic-text agrees for both weights at every
    /// shipped font size, glyph by glyph.
    #[test]
    fn bundled_mono_glyphs_advance_exactly_75_128_em() {
        let mut fs = test_font_system();
        for px in [12, 15, 18, 24] {
            for bold in [false, true] {
                for text in sample_texts() {
                    let buf = shape(&mut fs, text, px, bold);
                    let mut runs = buf.layout_runs();
                    let run = runs.next().expect("one layout run");
                    assert!(runs.next().is_none(), "{text:?} wrapped");
                    let chars = text.chars().count();
                    assert_eq!(run.glyphs.len(), chars, "{text:?} at {px}px bold={bold}: glyph count");
                    let adv = px as f32 * ADVANCE_EM;
                    for g in run.glyphs {
                        assert!(
                            (g.w - adv).abs() < 1e-3,
                            "{text:?} at {px}px bold={bold}: glyph {:?} advances {}",
                            g.start,
                            g.w
                        );
                    }
                    let expect_w = chars as f32 * adv;
                    assert!(
                        (run.line_w - expect_w).abs() < 1e-3,
                        "{text:?} at {px}px bold={bold}: line_w {} != {expect_w}",
                        run.line_w
                    );
                    let reserved = text_width_px(chars, px) as f32;
                    assert!(
                        reserved >= run.line_w - 1e-3,
                        "{text:?} at {px}px: reserved {reserved} < {}",
                        run.line_w
                    );
                    assert!(
                        reserved - run.line_w < 1.0,
                        "{text:?} at {px}px: reserved {reserved} over-allocates {}",
                        run.line_w
                    );
                }
            }
        }
    }

    /// The underline bar is placed from the glyph whose `start` byte is
    /// `underline_idx`; every accelerator must resolve to a glyph, and
    /// that glyph sits at the monospace column the index implies.
    #[test]
    fn accelerator_glyph_is_found_by_byte_index() {
        let mut fs = test_font_system();
        let px = 12;
        for set in PanelButtonSet::ALL {
            for def in set.defs() {
                let buf = shape(&mut fs, def.label, px, false);
                let run = buf
                    .layout_runs()
                    .next()
                    .expect("one layout run");
                let g = run
                    .glyphs
                    .iter()
                    .find(|g| g.start == def.underline_idx)
                    .unwrap_or_else(|| panic!("{:?}: no glyph starts at byte {}", def.label, def.underline_idx));
                let expect_x = def.underline_idx as f32 * px as f32 * ADVANCE_EM;
                assert!((g.x - expect_x).abs() < 1e-3, "{:?}: glyph x {} != {expect_x}", def.label, g.x);
            }
        }
    }

    /// The labels are placed at `(bh - line_h) / 2` with no baseline bias:
    /// cosmic-text centres ascent + descent in the line box and Cascadia's
    /// cap height equals ascent - descent, so the capitals' vertical
    /// centre is the line box's centre.
    #[test]
    fn cap_height_is_centred_in_the_line_box() {
        let mut fs = test_font_system();
        let px = 12;
        let line_h = px as f32 * 1.2;
        let buf = shape(&mut fs, "H", px, false);
        let run = buf
            .layout_runs()
            .next()
            .expect("one layout run");
        let g = &run.glyphs[0];
        let font = fs
            .get_font(g.font_id, g.font_weight)
            .expect("shaped glyph's font is loaded");
        let metrics = font.as_swash().metrics(&[]).scale(px as f32);
        let cap_centre = run.line_y - metrics.cap_height / 2.0;
        assert!(
            (cap_centre - line_h / 2.0).abs() < 0.05,
            "cap centre {cap_centre} vs line centre {} (line_y {}, cap {})",
            line_h / 2.0,
            run.line_y,
            metrics.cap_height
        );
    }

    /// The shadow quad reaches three sigma past its body at every
    /// standard DPI, so the falloff is ~0 before the quad edge.
    #[test]
    fn shadow_reach_covers_three_sigma() {
        for (dpi, sigma, pad) in [(1.0, 3.4, 12.0), (1.25, 4.25, 14.0), (1.5, 5.1, 17.0), (2.0, 6.8, 22.0)] {
            let (s, p) = shadow_params(dpi);
            assert!((s - sigma).abs() < 1e-5, "dpi {dpi}: sigma {s} != {sigma}");
            assert_eq!(p, pad, "dpi {dpi}: pad");
        }
    }
}
