//! GPU button-panel renderer: paints the `PanelScene` the app thread
//! shipped in the broadcast. Nothing is measured, formatted or placed
//! here. Per frame:
//!   1. [`panel_visibility`] and the scene's monitor gate the draw; on an
//!      early out the hover state is dropped;
//!   2. the hover veil is animated per widget id ([`HoverFade`]) from the
//!      broadcast cursor;
//!   3. the icon atlas is rebuilt when the scene's `(icon px, emblem px)`
//!      key changes;
//!   4. [`paint`] turns the scene into rect instances, icon draws (resolved
//!      against the atlas) and text draws, which are shaped into cached
//!      cosmic-text buffers and handed back through
//!      [`PanelRenderer::text_areas`];
//!   5. the accelerator underline goes under the glyph the shaper placed,
//!      after every fill so it paints on top of its segment.
//!
//! The DPI is read only for the shadow blur and the 100 % double-draw
//! gate. Click routing runs on the app thread against the same scene.

use crate::ui::gpu::text::{Attrs, Buffer, Color, Family, Metrics, Shaping, TextArea, TextBounds, Weight, Wrap};

use crate::ui::components::panel::assets::SVG_CLOWD_LOGO;
use crate::ui::components::panel::model::{EMBLEM_SLOT, PANEL_ICONS};
use crate::ui::gpu::icon::{IconAtlas, IconInstance};
use crate::ui::gpu::rect::RectInstance;
use crate::ui::gpu::text::{TextStack, FAMILY_MONO};
use crate::ui::kit::hover::HoverFade;
use crate::ui::kit::paint::paint;
use crate::ui::kit::tokens::HOVER_FADE_SECS;
use crate::ui::shared::{panel_visibility, UiMonitor, UiSharedState};
use clowd_rust_core::geometry::RectExt;

/// One cosmic-text buffer + content cache for change detection.
struct CachedBuffer {
    buffer: Buffer,
    last_text: String,
    last_font_px: f32,
    last_bold: bool,
}

impl CachedBuffer {
    fn new(ts: &mut TextStack, font_px: f32) -> Self {
        let metrics = Metrics::new(font_px, font_px * 1.2);
        let mut buffer = Buffer::new(&mut ts.font_system, metrics);
        buffer.set_wrap(Wrap::None);
        Self {
            buffer,
            last_text: String::new(),
            last_font_px: font_px,
            last_bold: false,
        }
    }

    /// Re-shape when the text, size or weight changed. The whole string is
    /// one uniformly shaped run; the accelerator underline is a rect the
    /// caller draws from `glyph_bounds_at_byte`, so it never touches the
    /// shaping.
    fn set(&mut self, ts: &mut TextStack, text: &str, font_px: f32, bold: bool) {
        let font_changed = (font_px - self.last_font_px).abs() > 0.25;
        if !font_changed && text == self.last_text && bold == self.last_bold {
            return;
        }
        if font_changed {
            self.buffer
                .set_metrics(Metrics::new(font_px, font_px * 1.2));
            self.last_font_px = font_px;
        }
        // Cascadia Mono, not Code: same advances, no `calt` ligature table,
        // so the glyph count always equals the char count the composer
        // sized by.
        let weight = if bold { Weight::BOLD } else { Weight::NORMAL };
        let attrs = Attrs::new()
            .family(Family::Name(FAMILY_MONO))
            .weight(weight);
        self.buffer
            .set_text(text, &attrs, Shaping::Advanced, None);
        self.buffer
            .shape_until_scroll(&mut ts.font_system, false);
        self.last_text.clear();
        self.last_text.push_str(text);
        self.last_bold = bold;
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

/// Where the buffer at the same index in `buffers` is drawn.
#[derive(Clone, Copy)]
struct PositionedText {
    x: f32,
    y: f32,
    color: [u8; 4],
    /// Bold text skips the 100 % double-draw (it would double-thicken).
    bold: bool,
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
    /// One per text draw of the latest scene, in draw order, grown on
    /// demand and never shrunk; `positions` says how many are live.
    buffers: Vec<CachedBuffer>,
    /// Text positions captured during the latest `prepare()`, parallel to
    /// the leading entries of `buffers`.
    positions: Vec<PositionedText>,
    /// Hover veil per widget id, animated each frame.
    hover: HoverFade,
    dpi_scale: f32,
}

impl PanelRenderer {
    pub fn new() -> Self {
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

        Self {
            svg_trees,
            emblem_tree,
            atlas: None,
            last_atlas_key: (0, 0),
            buffers: Vec::new(),
            positions: Vec::new(),
            hover: HoverFade::new(HOVER_FADE_SECS),
            dpi_scale: 1.0,
        }
    }

    pub fn atlas(&self) -> Option<&IconAtlas> {
        self.atlas.as_ref()
    }

    /// Paint the shipped scene. Appends rect instances to `rects` and
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

        let Some(panel) = panel_visibility(state) else {
            self.hover.clear();
            return;
        };
        if panel.monitor.bounds != this_monitor.bounds {
            self.hover.clear();
            return;
        }
        // Only the shadow blur and the double-draw gate read the DPI;
        // every size is an integer the composer already scaled.
        let dpi = panel.monitor.dpi_scale.max(0.1);
        self.dpi_scale = dpi;

        // Hover veil from the broadcast cursor, in VD coords like the
        // scene. No reset on a set swap: ids encode their set, so the
        // strip that replaces this one carries all-new ids and `advance`
        // prunes the old veils with the old ids. A ghost highlight on the
        // button that inherits a slot is unrepresentable.
        let cursor = state.virtual_cursor;
        let hovered = panel.scene.hit_test(cursor.x, cursor.y);
        self.hover
            .advance(dt_secs, hovered, panel.scene.ids());

        // One atlas for the button icons and the emblem, keyed on both
        // sizes so a DPI change rebuilds it exactly once.
        let key = (panel.icon_px, panel.emblem_px);
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

        let mut icons = Vec::new();
        let mut texts = Vec::new();
        let mon = this_monitor.bounds;
        paint(
            &panel.scene,
            (mon.left(), mon.top()),
            dpi,
            &|id| self.hover.amount(id),
            rects,
            &mut icons,
            &mut texts,
        );
        icon_draws.extend(icons.iter().map(|d| IconInstance {
            dest_px: d.dest_px,
            uv: atlas.uv_for(d.slot),
            alpha_mul: 1.0,
            _pad: [0.0; 3],
        }));

        for (i, t) in texts.iter().enumerate() {
            let font_px = t.spec.font_px as f32;
            while self.buffers.len() <= i {
                self.buffers
                    .push(CachedBuffer::new(ts, font_px));
            }
            self.buffers[i].set(ts, &t.spec.text, font_px, t.spec.bold);
            self.positions.push(PositionedText {
                x: t.x,
                y: t.y,
                color: t.spec.color,
                bold: t.spec.bold,
            });

            // Accelerator underline: a hairline under the glyph the shaper
            // placed (only it knows the glyph's x), pushed after every fill
            // so it paints on top of its segment. Sits on the shaper's
            // fractional line box, which is where the glyphs are.
            let glyph = t
                .spec
                .underline
                .and_then(|idx| self.buffers[i].glyph_bounds_at_byte(idx));
            if let Some((gx, gw)) = glyph {
                let hair = t.spec.hairline_px as f32;
                let u_y = (t.y + font_px * 1.2).round() - hair;
                rects.push(RectInstance::filled(t.x + gx, u_y, t.x + gx + gw, u_y + hair, [1.0; 4]));
            }
        }
    }

    pub fn text_areas<'a>(&'a self, viewport_px: (u32, u32), out: &mut Vec<TextArea<'a>>) {
        let (vw, vh) = (viewport_px.0 as i32, viewport_px.1 as i32);
        for (p, cached) in self.positions.iter().zip(&self.buffers) {
            let area = TextArea {
                buffer: &cached.buffer,
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
    use crate::ui::components::panel::model::PanelButtonSet;
    use crate::ui::gpu::text::{FONT_CODE_BOLD, FONT_CODE_REGULAR, FONT_MONO_BOLD, FONT_MONO_REGULAR};
    use crate::ui::kit::arrange::mono_text_width;

    /// 1200 / 2048 em: the single advance every glyph of the bundled
    /// Cascadia faces has (see `mono_text_width`).
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

    /// The composer's measure sizes text as `chars * px * 75/128` without
    /// a font; this pins that cosmic-text agrees for both weights at every
    /// shipped font size, glyph by glyph, so the shaped label fits the box
    /// the composer reserved for it.
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
                    let reserved = mono_text_width(chars, px) as f32;
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

    /// The composer centres the integer line box in the button with no
    /// baseline bias: cosmic-text centres ascent + descent in the line box
    /// and Cascadia's cap height equals ascent - descent, so the capitals'
    /// vertical centre is the line box's centre.
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
}
