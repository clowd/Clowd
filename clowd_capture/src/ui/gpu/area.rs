//! GPU area-indicator renderer.
//!
//! Draws a pill-shaped "W × H" label inside the selection rectangle
//! while the user is dragging (before capture is confirmed). The pill
//! is centered horizontally at the bottom of the selection, clamped to
//! monitor bounds. Matches `DxScreenCapture.cpp:652-704`.

use crate::ui::gpu::rect::RectInstance;
use crate::ui::gpu::text::{TextStack, FAMILY_CODE};
use crate::ui::shared::{area_indicator_visibility, UiMonitor, UiSharedState};
use clowd_rust_core::geometry::RectExt;
use glyphon::{Attrs, Buffer, Color, Family, Metrics, Shaping, TextArea, TextBounds, Weight, Wrap};

const AREA_FONT_PX: f32 = 14.0;
const AREA_PADDING_PX: f32 = 10.0;
const AREA_BORDER_UNSCALED: f32 = 2.0;

struct CachedBuffer {
    buffer: Buffer,
    last_text: String,
    last_font_px: f32,
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
        }
    }

    fn set(&mut self, ts: &mut TextStack, text: &str, font_px: f32) {
        let font_changed = (font_px - self.last_font_px).abs() > 0.25;
        let text_changed = text != self.last_text;
        if !font_changed && !text_changed {
            return;
        }
        if font_changed {
            self.buffer
                .set_metrics(Metrics::new(font_px, font_px * 1.2));
            self.last_font_px = font_px;
        }
        let attrs = Attrs::new()
            .family(Family::Name(FAMILY_CODE))
            .weight(Weight::BOLD);
        self.buffer
            .set_text(text, &attrs, Shaping::Advanced, None);
        self.buffer
            .shape_until_scroll(&mut ts.font_system, false);
        if text_changed {
            self.last_text.clear();
            self.last_text.push_str(text);
        }
    }

    fn width(&self) -> f32 {
        self.buffer
            .layout_runs()
            .map(|r| r.line_w)
            .fold(0.0f32, f32::max)
    }
}

#[derive(Clone, Copy)]
struct PositionedText {
    x: f32,
    y: f32,
}

pub struct AreaRenderer {
    buffer: CachedBuffer,
    position: Option<PositionedText>,
    text_buf: String,
    last_selection: Option<clowd_rust_core::geometry::ScreenRect>,
}

impl AreaRenderer {
    pub fn new(ts: &mut TextStack) -> Self {
        Self {
            buffer: CachedBuffer::new(ts, AREA_FONT_PX),
            position: None,
            text_buf: String::new(),
            last_selection: None,
        }
    }

    pub fn prepare(&mut self, ts: &mut TextStack, state: &UiSharedState, this_monitor: &UiMonitor, rects: &mut Vec<RectInstance>) {
        self.position = None;

        if !state.dragging {
            return;
        }

        let Some(vis) = area_indicator_visibility(state) else {
            return;
        };
        if vis.monitor.bounds != this_monitor.bounds {
            return;
        }

        let sel = match state.selection {
            Some(s) => s,
            None => return,
        };

        let dpi = vis.monitor.dpi_scale.max(0.1);
        let font_px = (AREA_FONT_PX * dpi).floor();
        let padding = (AREA_PADDING_PX * dpi).floor();

        // Format text using unclipped selection dimensions.
        if self.last_selection != state.selection {
            self.text_buf.clear();
            use std::fmt::Write;
            let _ = write!(self.text_buf, "{} \u{00D7} {}", sel.width(), sel.height());
            self.last_selection = state.selection;
        }
        self.buffer.set(ts, &self.text_buf, font_px);

        let text_width = self.buffer.width();
        let text_line_h = font_px * 1.2;
        let area_width = text_width + padding * 2.0;
        let area_height = text_line_h + padding;

        // Clip selection to this monitor.
        let mon = this_monitor.bounds;
        let mon_f = mon.to_f32();

        let clip_left = sel.left().max(mon.left());
        let clip_top = sel.top().max(mon.top());
        let clip_right = sel.right().min(mon.right());
        let clip_bottom = sel.bottom().min(mon.bottom());
        let sel_w = (clip_right - clip_left) as f32;
        let sel_h = (clip_bottom - clip_top) as f32;

        if sel_w <= 0.0 || sel_h <= 0.0 {
            return;
        }

        // Only draw if the pill fits inside the selection.
        let zoom = state.zoom;
        if sel_w * zoom <= area_width + padding || sel_h * zoom <= area_height + padding {
            return;
        }

        // Anchor at center-bottom of clipped selection (window-local).
        let sel_local_left = (clip_left - mon.left()) as f32;
        let sel_local_bottom = (clip_bottom - mon.top()) as f32;
        let center_x = sel_local_left + sel_w / 2.0;
        let bottom_y = sel_local_bottom;

        // Apply zoom transform around cursor.
        let cursor_local_x = state.virtual_cursor.x - mon_f.left();
        let cursor_local_y = state.virtual_cursor.y - mon_f.top();

        let (zoomed_x, zoomed_y) = if zoom > 1.0 {
            (
                (center_x - cursor_local_x) * zoom + cursor_local_x,
                (bottom_y - cursor_local_y) * zoom + cursor_local_y,
            )
        } else {
            (center_x, bottom_y)
        };

        // Position pill inside selection, near bottom.
        let mut origin_x = zoomed_x - area_width / 2.0;
        let mut origin_y = zoomed_y - padding / 2.0 - area_height;

        // Clamp to monitor bounds.
        origin_x = origin_x.clamp(0.0, (mon_f.width() - area_width).max(0.0));
        origin_y = origin_y.clamp(0.0, (mon_f.height() - area_height).max(0.0));

        // Pill background (rounded rect). Inflate quad by aa_pad so the
        // shader's fwidth-based AA has room for the full transition fringe.
        let corner_radius = area_height / 2.0;
        let border_px = AREA_BORDER_UNSCALED * dpi.floor().max(1.0);
        let aa_pad: f32 = 1.5;
        rects.push(RectInstance {
            dest_px: [
                origin_x - aa_pad,
                origin_y - aa_pad,
                origin_x + area_width + aa_pad,
                origin_y + area_height + aa_pad,
            ],
            fill_rgba: [1.0, 1.0, 1.0, 1.0],
            border_rgba: state.accent_color,
            params: [border_px, 0.0, corner_radius, aa_pad],
        });

        self.position = Some(PositionedText {
            x: origin_x + padding,
            y: (origin_y + padding / 2.0).round(),
        });
    }

    pub fn text_areas<'a>(&'a self, viewport_px: (u32, u32), out: &mut Vec<TextArea<'a>>) {
        let Some(pos) = self.position else {
            return;
        };
        let (vw, vh) = (viewport_px.0 as i32, viewport_px.1 as i32);
        let area = TextArea {
            buffer: &self.buffer.buffer,
            left: pos.x,
            top: pos.y,
            scale: 1.0,
            bounds: TextBounds {
                left: 0,
                top: 0,
                right: vw,
                bottom: vh,
            },
            default_color: Color::rgba(0, 0, 0, 0xFF),
            custom_glyphs: &[],
        };
        out.push(area);
    }
}
