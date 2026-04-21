//! GPU button-panel renderer.
//!
//! Per-frame work on this render thread:
//!   1. Check [`panel_visibility`] — early out if this monitor isn't the
//!      target.
//!   2. Animate the hover-amount-per-button toward a new target (0.30 on
//!      the hovered button, 0.0 on the rest).
//!   3. Emit rect instances for the button backgrounds, area indicator,
//!      and corner brackets.
//!   4. Emit one SVG draw per button (scaled into the button rect).
//!   5. Cache glyphon `Buffer`s for each label + the "W × H" area text
//!      and return their positions.
//!
//! Click routing runs on the app thread via the same shared layout
//! function — this renderer never sends anything back.

use glyphon::{Attrs, Buffer, Color, Family, Metrics, Shaping, TextArea, TextBounds, Wrap};

use crate::geometry::RectExt;
use crate::ui::components::panel::layout::PanelLayout;
use crate::ui::components::panel::model::{button_defs, NUM_SVG_BUTTONS};
use crate::ui::gpu::rect::RectInstance;
use crate::ui::gpu::svg::{SvgInstance, SvgMesh, SvgPipeline};
use crate::ui::gpu::text::{TextStack, FAMILY_ROBOTO};
use crate::ui::shared::{panel_visibility, UiMonitor, UiSharedState};

// Tuning constants (port of the old CPU code).
const LABEL_FONT_PX: f32 = 11.0;
const AREA_FONT_PX: f32 = 11.0;
const ICON_UNSCALED_PX: f32 = 26.0;
const GRAY_RGBA: [f32; 4] = [0x37 as f32 / 255.0, 0x37 as f32 / 255.0, 0x37 as f32 / 255.0, 1.0];
const HOVER_OVERLAY_STRENGTH: f32 = 0.30;
/// Speed of the exponential ease-out on hover transitions. `12.0` matches
/// the old `animation.rs` value (~200 ms to reach 90 % of target at 60 FPS).
const HOVER_ANIM_SPEED: f32 = 12.0;

/// One glyphon buffer + content cache for change detection.
struct CachedBuffer {
    buffer: Buffer,
    last_text: String,
    last_font_px: f32,
    last_underline_idx: Option<usize>,
}

impl CachedBuffer {
    fn new(ts: &mut TextStack, font_px: f32) -> Self {
        let metrics = Metrics::new(font_px, font_px * 1.2);
        let mut buffer = Buffer::new(&mut ts.font_system, metrics);
        buffer.set_wrap(&mut ts.font_system, Wrap::None);
        Self {
            buffer,
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
                .set_metrics(&mut ts.font_system, Metrics::new(font_px, font_px * 1.2));
            self.last_font_px = font_px;
        }
        // glyphon's `set_rich_text` lets us underline a single glyph via
        // attrs. Simpler: render the whole string uniformly and draw the
        // underline as a rect in the caller.
        let attrs = Attrs::new().family(Family::Name(FAMILY_ROBOTO));
        self.buffer
            .set_text(&mut ts.font_system, text, &attrs, Shaping::Advanced, None);
        self.buffer
            .shape_until_scroll(&mut ts.font_system, false);
        if text_changed {
            self.last_text.clear();
            self.last_text.push_str(text);
        }
        self.last_underline_idx = underline_idx;
    }

    fn width(&self) -> f32 {
        self.buffer
            .layout_runs()
            .map(|r| r.line_w)
            .fold(0.0f32, f32::max)
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
}

pub struct PanelRenderer {
    /// Icons tessellated once at construction, indexed by button id.
    pub icons: Vec<SvgMesh>,
    /// 0: width string ("1920")
    /// 1: height string ("1080")
    /// 2: "×" separator
    /// 3..3+NUM_SVG_BUTTONS: button labels ("UPLOAD", "EDIT", ...)
    buffers: Vec<CachedBuffer>,
    /// Text positions captured during the latest `prepare()`.
    positions: Vec<PositionedText>,
    /// Per-button lighten amount, animated each frame.
    hover_amounts: [f32; NUM_SVG_BUTTONS],
    /// Reused string buffers for the selection's width / height digits.
    /// Only rebuilt when the selection rect actually changes (not every
    /// frame), avoiding ~2 heap allocations/frame when the panel is
    /// visible.
    width_str: String,
    height_str: String,
    last_selection: Option<crate::geometry::ScreenRect>,
}

const IDX_WIDTH: usize = 0;
const IDX_HEIGHT: usize = 1;
const IDX_CROSS: usize = 2;
const IDX_LABEL_BASE: usize = 3;

impl PanelRenderer {
    pub fn new(device: &wgpu::Device, svg: &SvgPipeline, ts: &mut TextStack) -> Self {
        // Tessellate all 7 SVGs up-front.
        let usvg_opts = usvg::Options::default();
        let defs = button_defs();
        let mut icons: Vec<SvgMesh> = Vec::with_capacity(NUM_SVG_BUTTONS);
        for i in 0..NUM_SVG_BUTTONS {
            let tree = match usvg::Tree::from_data(defs[i].svg_bytes, &usvg_opts) {
                Ok(t) => t,
                Err(e) => {
                    log::error!("failed to parse SVG for button {i}: {e:?}");
                    usvg::Tree::from_str("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", &usvg::Options::default())
                        .expect("empty SVG parses")
                }
            };
            icons.push(svg.load_mesh(device, &tree));
        }

        let mut buffers = Vec::with_capacity(3 + NUM_SVG_BUTTONS);
        buffers.push(CachedBuffer::new(ts, 11.0)); // width
        buffers.push(CachedBuffer::new(ts, 11.0)); // height
        buffers.push(CachedBuffer::new(ts, 11.0)); // ×
        for _ in 0..NUM_SVG_BUTTONS {
            buffers.push(CachedBuffer::new(ts, 11.0));
        }

        Self {
            icons,
            buffers,
            positions: Vec::new(),
            hover_amounts: [0.0; NUM_SVG_BUTTONS],
            width_str: String::new(),
            height_str: String::new(),
            last_selection: None,
        }
    }

    /// Run the full panel logic. Appends rect instances to `rects` and
    /// SVG draw records to `svg_draws`, caches text positions internally.
    pub fn prepare(
        &mut self,
        ts: &mut TextStack,
        state: &UiSharedState,
        this_monitor: &UiMonitor,
        rects: &mut Vec<RectInstance>,
        svg_draws: &mut Vec<(usize, SvgInstance)>,
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
        let dpi = vis.monitor.dpi_scale.max(0.1);

        // Hover animation. Hit-test happens in VD coords since that's
        // what the broadcast cursor + layout both use.
        let cursor = state.virtual_cursor;
        let hovered_idx = layout.hit_test(cursor.x, cursor.y);
        for i in 0..NUM_SVG_BUTTONS {
            let target = if Some(i) == hovered_idx { HOVER_OVERLAY_STRENGTH } else { 0.0 };
            let diff = target - self.hover_amounts[i];
            if diff.abs() <= 0.01 {
                self.hover_amounts[i] = target;
            } else {
                let step = diff * (1.0 - (-HOVER_ANIM_SPEED * dt_secs).exp());
                self.hover_amounts[i] += step;
            }
        }

        let mon = this_monitor.bounds;
        let to_local = |r: crate::geometry::ScreenRect| -> (f32, f32, f32, f32) {
            (
                (r.left() - mon.left()) as f32,
                (r.top() - mon.top()) as f32,
                (r.right() - mon.left()) as f32,
                (r.bottom() - mon.top()) as f32,
            )
        };

        let accent = state.accent_color;

        // Button backgrounds + hover lighten.
        for (i, b) in layout.buttons.iter().enumerate() {
            let (l, t, r, bt) = to_local(*b);
            let def = &button_defs()[i];
            let fill = if def.primary { accent } else { GRAY_RGBA };
            rects.push(RectInstance {
                dest_px: [l, t, r, bt],
                fill_rgba: fill,
                border_rgba: [0.0; 4],
                params: [0.0, self.hover_amounts[i], 0.0, 0.0],
            });
        }

        // Area indicator background.
        let (al, at, ar, ab) = to_local(layout.area_rect);
        rects.push(RectInstance::filled(al, at, ar, ab, GRAY_RGBA));

        // Corner brackets (4 corners × 2 arms each).
        let line_px = 2.0_f32;
        let arm_px = (((ar - al) / 3.0).floor()).max(1.0);
        let white = [1.0, 1.0, 1.0, 1.0];
        // top-left
        rects.push(RectInstance::filled(al, at, al + arm_px, at + line_px, white));
        rects.push(RectInstance::filled(al, at, al + line_px, at + arm_px, white));
        // top-right
        rects.push(RectInstance::filled(ar - arm_px, at, ar, at + line_px, white));
        rects.push(RectInstance::filled(ar - line_px, at, ar, at + arm_px, white));
        // bottom-left
        rects.push(RectInstance::filled(al, ab - line_px, al + arm_px, ab, white));
        rects.push(RectInstance::filled(al, ab - arm_px, al + line_px, ab, white));
        // bottom-right
        rects.push(RectInstance::filled(ar - arm_px, ab - line_px, ar, ab, white));
        rects.push(RectInstance::filled(ar - line_px, ab - arm_px, ar, ab, white));

        // Button SVG icons.
        let icon_size = (ICON_UNSCALED_PX * dpi).floor();
        for (i, b) in layout.buttons.iter().enumerate() {
            let (l, t, r, bt) = to_local(*b);
            let bw = r - l;
            let bh = bt - t;
            let label_px = (LABEL_FONT_PX * dpi).floor();
            let label_line_h = label_px * 1.2;
            let v_gap = ((bh - icon_size - label_line_h) / 3.0).max(0.0);
            let icon_left = l + (bw / 2.0) - (icon_size / 2.0);
            let icon_top = t + v_gap;
            let mesh = &self.icons[i];
            let sx = if mesh.size[0] > 0.0 { icon_size / mesh.size[0] } else { 1.0 };
            let sy = if mesh.size[1] > 0.0 { icon_size / mesh.size[1] } else { 1.0 };
            svg_draws.push((
                i,
                SvgInstance {
                    offset_px: [icon_left, icon_top],
                    scale_px: [sx, sy],
                    alpha_mul: 1.0,
                    _pad: [0.0; 3],
                },
            ));
        }

        // Labels (after icons so text lands on top).
        let label_px = (LABEL_FONT_PX * dpi).floor();
        let area_px = (AREA_FONT_PX * dpi).floor();
        let label_line_h = label_px * 1.2;
        for i in 0..NUM_SVG_BUTTONS {
            let def = &button_defs()[i];
            self.buffers[IDX_LABEL_BASE + i].set(ts, def.label, label_px, Some(def.underline_idx));
        }

        // Area indicator text buffers. Rebuild the digit strings only
        // when the selection rect actually changes — otherwise reuse the
        // cached `width_str` / `height_str` and let `CachedBuffer::set`
        // noop on the unchanged text.
        if self.last_selection != state.selection {
            self.width_str.clear();
            self.height_str.clear();
            if let Some(s) = state.selection {
                use std::fmt::Write;
                let _ = write!(self.width_str, "{}", s.width());
                let _ = write!(self.height_str, "{}", s.height());
            }
            self.last_selection = state.selection;
        }
        self.buffers[IDX_WIDTH].set(ts, &self.width_str, area_px, None);
        self.buffers[IDX_HEIGHT].set(ts, &self.height_str, area_px, None);
        self.buffers[IDX_CROSS].set(ts, "\u{00D7}", area_px, None);

        // Position labels beneath each icon.
        for (i, b) in layout.buttons.iter().enumerate() {
            let (l, t, r, bt) = to_local(*b);
            let bw = r - l;
            let bh = bt - t;
            let v_gap = ((bh - icon_size - label_line_h) / 3.0).max(0.0);
            let icon_top = t + v_gap;
            let label_width = self.buffers[IDX_LABEL_BASE + i].width();
            let label_x = l + (bw / 2.0) - (label_width / 2.0);
            // Round Y to the pixel grid. glyphon's `physical()` truncates
            // the Y component (cosmic-text `layout.rs`) to hint on the
            // baseline; feeding it a fractional `top` causes the text to
            // snap one pixel upward. At low DPI (Windows 100%) that pixel
            // is a full logical pixel and the label drifts visibly high.
            let label_y = (icon_top + icon_size + v_gap).round();
            self.positions.push(PositionedText {
                buffer_idx: IDX_LABEL_BASE + i,
                x: label_x,
                y: label_y,
                color: [0xFF, 0xFF, 0xFF, 0xFF],
            });

            // Underline bar for the accelerator key. Roboto is
            // proportional, so we can't approximate by averaging —
            // read the real glyph bounds from glyphon's shaping output.
            // For ASCII labels, `char index == byte index`; we pass the
            // byte index directly.
            let def = &button_defs()[i];
            let label_buf = &self.buffers[IDX_LABEL_BASE + i];
            if let Some((glyph_x, glyph_w)) = label_buf.glyph_bounds_at_byte(def.underline_idx) {
                let u_x = label_x + glyph_x;
                let u_y = label_y + label_line_h - (dpi.round().max(1.0));
                let u_h = dpi.round().max(1.0);
                rects.push(RectInstance::filled(u_x, u_y, u_x + glyph_w, u_y + u_h, [1.0, 1.0, 1.0, 1.0]));
            }
        }

        // Area indicator layout (W above, × in the middle, H below).
        let width_w = self.buffers[IDX_WIDTH].width();
        let height_w = self.buffers[IDX_HEIGHT].width();
        let cross_w = self.buffers[IDX_CROSS].width();
        let aw = ar - al;
        let ah = ab - at;
        let area_line_h = area_px * 1.2;

        // Round Y so glyphon's `truncf` hinting doesn't steal a pixel.
        // See the label block above for the full rationale.
        self.positions.push(PositionedText {
            buffer_idx: IDX_WIDTH,
            x: al + (aw - width_w) * 0.5,
            y: (at + (ah / 4.0) - area_line_h * 0.5).round(),
            color: [0xFF, 0xFF, 0xFF, 0xFF],
        });
        self.positions.push(PositionedText {
            buffer_idx: IDX_CROSS,
            x: al + (aw - cross_w) * 0.5,
            y: (at + (ah / 2.0) - area_line_h * 0.5).round(),
            color: [0xFF, 0xFF, 0xFF, (0.70 * 255.0) as u8],
        });
        self.positions.push(PositionedText {
            buffer_idx: IDX_HEIGHT,
            x: al + (aw - height_w) * 0.5,
            y: (at + (ah * 3.0 / 4.0) - area_line_h * 0.5).round(),
            color: [0xFF, 0xFF, 0xFF, 0xFF],
        });
    }

    pub fn text_areas<'a>(&'a self, viewport_px: (u32, u32), out: &mut Vec<TextArea<'a>>) {
        let (vw, vh) = (viewport_px.0 as i32, viewport_px.1 as i32);
        out.extend(self.positions.iter().map(|p| TextArea {
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
            custom_glyphs: &[],
        }));
    }
}
