//! GPU tips panel renderer.
//!
//! Per-frame work on this render thread:
//!   1. Check [`tips_visibility`] against this monitor — early out if
//!      the tips aren't on this monitor.
//!   2. Update cached glyphon buffers for each text element whose
//!      content changed (hovered window/monitor/pixel).
//!   3. Feed glyphon measurements into the shared `compute_layout`
//!      function to decide the panel rect and internal positions.
//!   4. Emit rect instances (body background, title bar, shadow, color
//!      swatch) into the caller's accumulator and a list of
//!      `(text_id, x, y, color)` positions.
//!
//! The caller ([`super::renderer::UiRenderer`]) is then responsible for
//! assembling the final text areas (which need `&Buffer` references into
//! `Self`) — we hand those out via [`TipsRenderer::text_areas`] once
//! positions are known.

use glyphon::{Attrs, Buffer, Color, Family, Metrics, Shaping, TextArea, TextBounds, Weight, Wrap};

use crate::geometry::RectExt;
use crate::ui::components::tips::layout::{compute_layout as compute_tips_layout, BODY_FONT_PX, TITLE_FONT_PX};
use crate::ui::components::tips::model::{render_description, COLOR_ROW_HOTKEY, HOTKEY_GAP, TIPS_BOTTOM, TIPS_TOP, TITLE};
use crate::ui::gpu::rect::RectInstance;
use crate::ui::gpu::text::{TextStack, FAMILY_MONO};
use crate::ui::shared::{tips_visibility, UiMonitor, UiSharedState};

/// Panel opacity applied to body + title (excluding shadow). Matches the
/// old `base_opacity` constant.
const BASE_OPACITY: f32 = 0.70;
/// Shadow strip opacity — black * this alpha.
const SHADOW_ALPHA: f32 = 0.30;

/// Cached glyphon buffer + last-rendered content for change detection.
struct CachedBuffer {
    buffer: Buffer,
    last_text: String,
    last_font_px: f32,
    last_bold: bool,
}

impl CachedBuffer {
    fn new(ts: &mut TextStack, font_px: f32, bold: bool) -> Self {
        let metrics = Metrics::new(font_px, font_px * 1.2);
        let mut buffer = Buffer::new(&mut ts.font_system, metrics);
        buffer.set_wrap(Wrap::None);
        Self {
            buffer,
            last_text: String::new(),
            last_font_px: font_px,
            last_bold: bold,
        }
    }

    /// Update content if anything changed. Returns `true` if the buffer
    /// was re-shaped.
    fn set(&mut self, ts: &mut TextStack, text: &str, font_px: f32, bold: bool) -> bool {
        let font_changed = (font_px - self.last_font_px).abs() > 0.25 || bold != self.last_bold;
        let text_changed = text != self.last_text;
        if !font_changed && !text_changed {
            return false;
        }
        if font_changed {
            let metrics = Metrics::new(font_px, font_px * 1.2);
            self.buffer.set_metrics(metrics);
            self.last_font_px = font_px;
            self.last_bold = bold;
        }
        let mut attrs = Attrs::new().family(Family::Name(FAMILY_MONO));
        if bold {
            attrs = attrs.weight(Weight::BOLD);
        }
        self.buffer
            .set_text(text, &attrs, Shaping::Advanced, None);
        self.buffer
            .shape_until_scroll(&mut ts.font_system, false);
        if text_changed {
            self.last_text.clear();
            self.last_text.push_str(text);
        }
        true
    }

    /// Widest shaped line, in pixels at the current font size.
    fn width(&self) -> f32 {
        self.buffer
            .layout_runs()
            .map(|r| r.line_w)
            .fold(0.0f32, f32::max)
    }
}

/// Positioned text element. `x` and `y` are window-local physical pixels;
/// `buffer_idx` selects one of the cached buffers on the renderer.
#[derive(Clone, Copy)]
struct PositionedText {
    buffer_idx: usize,
    x: f32,
    y: f32,
    color: [u8; 4],
}

pub struct TipsRenderer {
    /// All text buffers in a single Vec so `text_areas()` can index into
    /// them by `buffer_idx`. Order is fixed:
    ///   0: title
    ///   1..=4: top row 0..3
    ///   5: color hotkey "H"
    ///   6: color hex
    ///   7: color rgb
    ///   8..=11: bottom row 0..3
    buffers: Vec<CachedBuffer>,
    /// Text positions produced by the latest `prepare`.
    positions: Vec<PositionedText>,
}

const IDX_TITLE: usize = 0;
const IDX_TOP_BASE: usize = 1;
const IDX_COLOR_HOTKEY: usize = IDX_TOP_BASE + TIPS_TOP.len();
const IDX_COLOR_HEX: usize = IDX_COLOR_HOTKEY + 1;
const IDX_COLOR_RGB: usize = IDX_COLOR_HEX + 1;
const IDX_BOTTOM_BASE: usize = IDX_COLOR_RGB + 1;
const TOTAL_BUFFERS: usize = IDX_BOTTOM_BASE + TIPS_BOTTOM.len();

impl TipsRenderer {
    pub fn new(ts: &mut TextStack) -> Self {
        let mut buffers = Vec::with_capacity(TOTAL_BUFFERS);
        // Initial font sizes are placeholders — set() updates them on
        // first use.
        buffers.push(CachedBuffer::new(ts, 14.0, true)); // title
        for _ in 0..TIPS_TOP.len() {
            buffers.push(CachedBuffer::new(ts, 12.0, false));
        }
        buffers.push(CachedBuffer::new(ts, 12.0, false)); // color hotkey
        buffers.push(CachedBuffer::new(ts, 12.0, false)); // color hex
        buffers.push(CachedBuffer::new(ts, 12.0, false)); // color rgb
        for _ in 0..TIPS_BOTTOM.len() {
            buffers.push(CachedBuffer::new(ts, 12.0, false));
        }
        Self {
            buffers,
            positions: Vec::new(),
        }
    }

    /// Run the full tips-panel logic for this monitor. Emits rect
    /// instances into `rects`; stores computed text positions inside
    /// `self` so `text_areas()` can produce the final `TextArea` list.
    pub fn prepare(&mut self, ts: &mut TextStack, state: &UiSharedState, this_monitor: &UiMonitor, rects: &mut Vec<RectInstance>) {
        self.positions.clear();

        // Visibility rule.
        let Some((_, target)) = tips_visibility(state) else {
            return;
        };
        // Only draw on the monitor the rule picked. Identity via bounds
        // equality (each monitor in the list has a unique rect).
        if target.bounds != this_monitor.bounds {
            return;
        }

        let dpi = target.dpi_scale.max(0.1);
        let body_px = (BODY_FONT_PX * dpi).floor();
        let title_px = (TITLE_FONT_PX * dpi).floor();

        // Update cached buffers with the current content.
        let hw = state.hovered_window_title.as_deref();
        let hm = state.hovered_monitor_name.as_deref();
        self.buffers[IDX_TITLE].set(ts, TITLE, title_px, true);
        for (i, row) in TIPS_TOP.iter().enumerate() {
            let desc = render_description(row.description_template, hw, hm);
            let combined = format!("{}{}{}", row.hotkey, HOTKEY_GAP, desc);
            self.buffers[IDX_TOP_BASE + i].set(ts, &combined, body_px, false);
        }
        let (hex_text, rgb_text) = match state.hovered_pixel_bgra {
            Some([b, g, r, _]) => (format!("#{:02X}{:02X}{:02X}", r, g, b), format!("rgb({}, {}, {})", r, g, b)),
            None => ("#------".to_string(), String::new()),
        };
        self.buffers[IDX_COLOR_HOTKEY].set(ts, COLOR_ROW_HOTKEY, body_px, false);
        self.buffers[IDX_COLOR_HEX].set(ts, &hex_text, body_px, false);
        self.buffers[IDX_COLOR_RGB].set(ts, &rgb_text, body_px, false);
        for (i, row) in TIPS_BOTTOM.iter().enumerate() {
            let combined = format!("{}{}{}", row.hotkey, HOTKEY_GAP, row.description_template);
            self.buffers[IDX_BOTTOM_BASE + i].set(ts, &combined, body_px, false);
        }

        // Measure widest body row + title + body-row height from glyphon.
        let mut longest_body = 0.0f32;
        for i in IDX_TOP_BASE..IDX_TOP_BASE + TIPS_TOP.len() {
            longest_body = longest_body.max(self.buffers[i].width());
        }
        for i in IDX_BOTTOM_BASE..IDX_BOTTOM_BASE + TIPS_BOTTOM.len() {
            longest_body = longest_body.max(self.buffers[i].width());
        }
        // Color row: "H" hotkey column + swatch + hex text must fit. Take
        // the greater of hex.width() and rgb.width() as the color text
        // width and add the "H" column width so the color row doesn't
        // force a narrower panel than the other rows.
        let h_col_w = self.buffers[IDX_COLOR_HOTKEY].width();
        let color_box = body_px * 2.4; // matches layout::color_box_size ≈ row_height * 2
        let color_line_w = self.buffers[IDX_COLOR_HEX]
            .width()
            .max(self.buffers[IDX_COLOR_RGB].width());
        let color_total = h_col_w + color_box + color_line_w + (body_px * 0.5);
        longest_body = longest_body.max(color_total);

        let title_width = self.buffers[IDX_TITLE].width();
        let body_row_height = body_px * 1.4;
        // Approximate cap-height (top of caps → baseline). The old code
        // fed swash's `measure_line("Hg").height` here, which is the
        // cap-height. For Cascadia at 14/12 px it's ~0.7 × font size.
        let title_height = title_px * 0.7;

        let monitor_bounds = target.bounds;
        let Some(layout) = compute_tips_layout(
            monitor_bounds,
            state.virtual_cursor,
            dpi,
            longest_body,
            title_width,
            body_row_height,
            title_height,
        ) else {
            return;
        };

        // Convert VD coords to window-local physical pixels.
        let mon_f = this_monitor.bounds.to_f32();
        let panel_f = layout.panel_rect.to_f32();
        let panel_left_w = panel_f.left() - mon_f.left();
        let panel_top_w = panel_f.top() - mon_f.top();
        let panel_w = panel_f.width();
        let panel_h = panel_f.height();
        let title_h = layout.title_rect.height() as f32;
        let shadow = layout.shadow_extension_px.round();

        let accent = premul_alpha(state.accent_color, BASE_OPACITY);
        let white = premul_alpha([1.0, 1.0, 1.0, 1.0], BASE_OPACITY);
        let black_shadow = [0.0, 0.0, 0.0, SHADOW_ALPHA];
        let swatch_border = [0.0, 0.0, 0.0, 1.0];
        let swatch_fill = match state.hovered_pixel_bgra {
            Some([b, g, r, _]) => [r as f32 / 255.0, g as f32 / 255.0, b as f32 / 255.0, 1.0],
            None => [0.0, 0.0, 0.0, 1.0],
        };

        // Shadow strips first (drawn behind panel body). The right strip
        // extends all the way down to `panel_h + shadow` so it covers
        // the bottom-right corner too; the bottom strip stops at
        // `panel_w` (which is where the right strip begins on X), so
        // there's no overdraw.
        if shadow > 0.0 {
            // Right strip (including the corner).
            rects.push(RectInstance::filled(
                panel_left_w + panel_w,
                panel_top_w + shadow,
                panel_left_w + panel_w + shadow,
                panel_top_w + panel_h + shadow,
                black_shadow,
            ));
            // Bottom strip (left of the corner only).
            rects.push(RectInstance::filled(
                panel_left_w + shadow,
                panel_top_w + panel_h,
                panel_left_w + panel_w,
                panel_top_w + panel_h + shadow,
                black_shadow,
            ));
        }

        // Body (white).
        rects.push(RectInstance::filled(
            panel_left_w,
            panel_top_w + title_h,
            panel_left_w + panel_w,
            panel_top_w + panel_h,
            white,
        ));
        // Title bar (accent).
        rects.push(RectInstance::filled(
            panel_left_w,
            panel_top_w,
            panel_left_w + panel_w,
            panel_top_w + title_h,
            accent,
        ));

        // Color swatch: 1-px border filled rect with the sampled color.
        let box_x = panel_left_w + layout.col_desc_x;
        let box_y = panel_top_w + layout.color_row_y;
        let box_sz = layout.color_box_size;
        rects.push(RectInstance {
            dest_px: [box_x, box_y, box_x + box_sz, box_y + box_sz],
            fill_rgba: swatch_fill,
            border_rgba: swatch_border,
            params: [dpi.max(1.0).round(), 0.0, 0.0, 0.0],
        });

        // Emit text positions. Title is centered in the title bar; each
        // row uses panel-local column offsets from the shared layout.
        let title_width_f = self.buffers[IDX_TITLE].width();
        let title_x = panel_left_w + (panel_w - title_width_f) * 0.5;
        // Center the glyphon line (which occupies line_height = font * 1.2)
        // vertically in the title bar.
        let title_line_h = title_px * 1.2;
        let title_y = panel_top_w + (title_h - title_line_h) * 0.5;
        self.positions.push(PositionedText {
            buffer_idx: IDX_TITLE,
            x: title_x,
            y: title_y,
            color: [0xFF, 0xFF, 0xFF, 0xFF],
        });

        let text_y_adjust = 0.0; // glyphon renders from top-left of the run
        let mut y = panel_top_w + layout.top_block_y;
        for i in 0..TIPS_TOP.len() {
            self.positions.push(PositionedText {
                buffer_idx: IDX_TOP_BASE + i,
                x: panel_left_w + layout.col_hotkey_x,
                y: y + text_y_adjust,
                color: [0, 0, 0, 0xFF],
            });
            y += layout.row_height;
        }

        // Color row: hotkey "H" + hex + rgb on two lines.
        self.positions.push(PositionedText {
            buffer_idx: IDX_COLOR_HOTKEY,
            x: panel_left_w + layout.col_hotkey_x,
            y: panel_top_w + layout.color_row_y,
            color: [0, 0, 0, 0xFF],
        });
        self.positions.push(PositionedText {
            buffer_idx: IDX_COLOR_HEX,
            x: panel_left_w + layout.color_hex_x,
            y: panel_top_w + layout.color_hex_y,
            color: [0, 0, 0, 0xFF],
        });
        if !rgb_text.is_empty() {
            self.positions.push(PositionedText {
                buffer_idx: IDX_COLOR_RGB,
                x: panel_left_w + layout.color_hex_x,
                y: panel_top_w + layout.color_rgb_y,
                color: [0, 0, 0, 0xFF],
            });
        }

        let mut y = panel_top_w + layout.bottom_block_y;
        for i in 0..TIPS_BOTTOM.len() {
            self.positions.push(PositionedText {
                buffer_idx: IDX_BOTTOM_BASE + i,
                x: panel_left_w + layout.col_hotkey_x,
                y,
                color: [0, 0, 0, 0xFF],
            });
            y += layout.row_height;
        }
    }

    /// Build the glyphon `TextArea` list for this frame. Must be called
    /// AFTER `prepare()` and the results must be consumed before any
    /// `&mut self` call (they borrow `self.buffers`).
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

fn premul_alpha(rgba: [f32; 4], alpha_mul: f32) -> [f32; 4] {
    // Store STRAIGHT alpha — the shader premultiplies for blending.
    [rgba[0], rgba[1], rgba[2], rgba[3] * alpha_mul]
}
