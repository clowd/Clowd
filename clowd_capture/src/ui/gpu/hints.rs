use glyphon::{Attrs, Buffer, Color, Family, Metrics, Shaping, TextArea, TextBounds, Weight, Wrap};

use crate::ui::components::hints::layout::{
    compute_color_hint, compute_cursor_hint, compute_monitor_hint, compute_monitor_hint_top, compute_scroll_pick_hint, HintLayout,
    HintRect, CORNER_RADIUS, CURSOR_SQUARE_PAD, HINT_FONT_PX, HINT_PADDING_H, HINT_PADDING_V, SHADOW_EXTRA, SHADOW_OFFSET,
};
use crate::ui::components::hints::model::{render_hint_text, HINT_MONITOR};
use crate::ui::components::scope::layout::{ScopeLayout, SCOPE_EXTENT};
use crate::ui::gpu::rect::RectInstance;
use crate::ui::gpu::scope::emit_scope;
use crate::ui::gpu::text::{TextStack, FAMILY_CODE, FAMILY_MONO};
use crate::ui::shared::{hints_visibility, scroll_pick_visibility, UiMonitor, UiSharedState};
use clowd_rust_core::geometry::{RectExt, ScreenRectExt};

// pub(crate): the tooltip palette is shared style — the OCR text bubbles
// (`super::ocr_bubbles`) must match the hint pills exactly, so they import
// these instead of re-deriving them. (SHADOW_FILL stays private: the
// bubbles deliberately carry no shadow — they sit on a darkened page.)
pub(crate) const TOOLTIP_FILL: [f32; 4] = [1.0, 1.0, 1.0, 0.80];
pub(crate) const TOOLTIP_BORDER: [f32; 4] = [0.75, 0.75, 0.75, 0.80];
const SHADOW_FILL: [f32; 4] = [0.0, 0.0, 0.0, 0.40];
/// The pill body-text color (near-black at 88% alpha) — same sharing
/// story as the fills above.
pub(crate) const TOOLTIP_TEXT_COLOR: [u8; 4] = [0x1A, 0x1A, 0x1A, 0xE0];

// Keycap: 3-layer bevel design.
// Layer 1 (base): dark gray — visible as bottom-right shadow edge.
const KEYCAP_DARK: [f32; 4] = [0.18, 0.18, 0.18, 0.90];
// Layer 2 (bevel highlight): lighter gray — covers top-left of base,
// inset on bottom/right so the dark shows through there.
const KEYCAP_LIGHT: [f32; 4] = [0.52, 0.52, 0.52, 0.90];
// Layer 3 (face): medium gray — the key surface.
const KEYCAP_FACE: [f32; 4] = [0.38, 0.38, 0.38, 0.90];
// All keycap layers share this very dark border.
const KEYCAP_BORDER: [f32; 4] = [0.06, 0.06, 0.06, 0.95];

const DASH_LEN: f32 = 6.0;

/// AA fringe the rounded-rect instances are inflated by. Shared with the
/// OCR bubbles for the same reason as the palette above.
pub(crate) const AA: f32 = 1.5;

struct CachedBuffer {
    buffer: Buffer,
    last_text: String,
    last_font_px: f32,
    last_bold: bool,
    mono: bool,
}

impl CachedBuffer {
    fn new(ts: &mut TextStack, font_px: f32, bold: bool, mono: bool) -> Self {
        let metrics = Metrics::new(font_px, font_px * 1.2);
        let mut buffer = Buffer::new(&mut ts.font_system, metrics);
        buffer.set_wrap(Wrap::None);
        Self {
            buffer,
            last_text: String::new(),
            last_font_px: font_px,
            last_bold: bold,
            mono,
        }
    }

    fn set(&mut self, ts: &mut TextStack, text: &str, font_px: f32, bold: bool) {
        let font_changed = (font_px - self.last_font_px).abs() > 0.25 || bold != self.last_bold;
        let text_changed = text != self.last_text;
        if !font_changed && !text_changed {
            return;
        }
        if font_changed {
            self.buffer
                .set_metrics(Metrics::new(font_px, font_px * 1.2));
            self.last_font_px = font_px;
            self.last_bold = bold;
        }
        let family = if self.mono { FAMILY_MONO } else { FAMILY_CODE };
        let mut attrs = Attrs::new().family(Family::Name(family));
        if bold {
            attrs = attrs.weight(if self.mono { Weight::BOLD } else { Weight::MEDIUM });
        }
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
    buffer_idx: usize,
    x: f32,
    y: f32,
    color: [u8; 4],
    alpha_mul: f32,
}

const IDX_KEY_COLOR: usize = 0;
const IDX_DESC_COLOR: usize = 1;
const IDX_KEY_MONITOR: usize = 2;
const IDX_DESC_MONITOR: usize = 3;
const IDX_KEY_CURSOR: usize = 4;
const IDX_DESC_CURSOR: usize = 5;
const IDX_KEY_MAGNIFIER: usize = 6;
const IDX_DESC_MAGNIFIER: usize = 7;
const IDX_KEY_SCROLL: usize = 8;
const IDX_DESC_SCROLL: usize = 9;
/// The scroll-point picker's hint has no keycap — description only.
const IDX_DESC_PICK: usize = 10;
/// The OCR notice pill ("No text found" etc.) — also keycap-less.
const IDX_DESC_NOTICE: usize = 11;
const TOTAL_BUFFERS: usize = 12;

const SCROLL_PICK_TEXT: &str = "Click within the scrollable area";

/// Gap between the selection's top edge and the pill's bottom edge.
const NOTICE_GAP_Y: f32 = 8.0;

pub struct HintsRenderer {
    buffers: Vec<CachedBuffer>,
    positions: Vec<PositionedText>,
    text_buf: String,
}

impl HintsRenderer {
    pub fn new(ts: &mut TextStack) -> Self {
        let mut buffers = Vec::with_capacity(TOTAL_BUFFERS);
        for i in 0..TOTAL_BUFFERS {
            // Key/description buffers alternate up to the keycap-less
            // picker + OCR-notice hints, which are descriptions like any
            // other.
            let is_key = i < IDX_DESC_PICK && i % 2 == 0;
            buffers.push(CachedBuffer::new(ts, 11.0, is_key, !is_key));
        }
        Self {
            buffers,
            positions: Vec::new(),
            text_buf: String::new(),
        }
    }

    pub fn prepare(&mut self, ts: &mut TextStack, state: &UiSharedState, this_monitor: &UiMonitor, rects: &mut Vec<RectInstance>) {
        self.positions.clear();

        let dpi = this_monitor.dpi_scale.max(0.1);
        let font_px = (HINT_FONT_PX * dpi).floor();
        let key_font_px = (HINT_FONT_PX * 1.15 * dpi).floor();
        let text_line_h = font_px * 1.2;
        let mon_f = this_monitor.bounds.to_f32();
        let mon_left = mon_f.left();
        let mon_top = mon_f.top();

        let mut placed: Vec<HintRect> = Vec::with_capacity(4);

        // --- Scroll-point picker ---
        // The picker takes the whole overlay: the only input it wants is
        // one click inside the selection, so it replaces the pointer with
        // its own reticle and shows a single instruction. Returning here
        // is what suppresses the [M] cursor hint below, which is otherwise
        // the one hint that survives capture.
        if state.scroll_pick_mode {
            if let Some(target) = scroll_pick_visibility(state) {
                // The reticle is drawn by every monitor it reaches, not just
                // the one under the cursor: aiming near a seam would
                // otherwise show half a scope, and with the OS pointer
                // hidden that half is the only aim indicator there is.
                // Sized by the *cursor's* monitor so both halves match in
                // physical pixels and line up across the seam.
                let cx = state.virtual_cursor.x;
                let cy = state.virtual_cursor.y;
                let reach = SCOPE_EXTENT * target.dpi_scale.max(0.1);
                let touches_this_monitor =
                    cx + reach >= mon_f.left() && cx - reach < mon_f.right() && cy + reach >= mon_f.top() && cy - reach < mon_f.bottom();
                if touches_this_monitor {
                    let layout = ScopeLayout::compute(cx - mon_left, cy - mon_top, target.dpi_scale);
                    emit_scope(rects, &layout, state.accent_color);
                }

                // The hint stays on the cursor's own monitor — one copy, and
                // its placement rules are written against that monitor's
                // bounds.
                if target.bounds == this_monitor.bounds {
                    self.buffers[IDX_DESC_PICK].set(ts, SCROLL_PICK_TEXT, font_px, false);
                    let desc_w = self.buffers[IDX_DESC_PICK].width();
                    let layout = compute_scroll_pick_hint(state, &target, dpi, desc_w, text_line_h);
                    self.emit_hint(
                        rects,
                        &layout,
                        mon_left,
                        mon_top,
                        None,
                        IDX_DESC_PICK,
                        1.0,
                        state.accent_color,
                        true,
                    );
                }
            }
            return;
        }

        // --- OCR notice pill ---
        // Transient feedback for an OCR attempt that produced nothing
        // (no text / engine unavailable / engine failure). By design it is
        // only ever up while `state.ocr` is Idle — the failed attempt has
        // already unwound back to the plain captured state — so it MUST
        // sit before the ocr.active() suppression below, or it could never
        // render at all and the OCR button would be indistinguishable from
        // a dead one.
        if let Some(notice) = state.ocr_notice {
            // The notice anchors to the selection the OCR attempt ran on;
            // both are cleared together on reset, so a missing selection
            // just means there is nothing to anchor to.
            if notice.visible() {
                if let Some(sel) = state.selection {
                    // One copy, on the monitor holding the selection's
                    // center — the same rule that places the panel, so the
                    // pill and the buttons that caused it share a monitor.
                    let b = this_monitor.bounds;
                    let cx = sel.center_x();
                    let cy = sel.center_y();
                    if cx >= b.min_x() && cx < b.max_x() && cy >= b.min_y() && cy < b.max_y() {
                        self.buffers[IDX_DESC_NOTICE].set(ts, notice.kind.message(), font_px, false);
                        let desc_w = self.buffers[IDX_DESC_NOTICE].width();

                        // The shared pill paddings (the OCR button-strip
                        // press was what raised this pill, and the bubbles
                        // it leads to use the same metrics).
                        let padding_h = (HINT_PADDING_H * dpi).floor();
                        let padding_v = (HINT_PADDING_V * dpi).floor();
                        let tooltip_w = padding_h * 2.0 + desc_w;
                        let tooltip_h = padding_v * 2.0 + text_line_h;

                        // Centered over the selection, just above its top
                        // edge, clamped fully inside the monitor (a
                        // selection at the very top pushes the pill down
                        // over itself rather than off screen).
                        let sel_f = sel.to_f32();
                        let x = ((sel_f.left() + sel_f.right()) * 0.5 - tooltip_w * 0.5)
                            .clamp(mon_f.left(), (mon_f.right() - tooltip_w).max(mon_f.left()));
                        let y = (sel_f.top() - (NOTICE_GAP_Y * dpi).floor() - tooltip_h)
                            .clamp(mon_f.top(), (mon_f.bottom() - tooltip_h).max(mon_f.top()));

                        // A hand-rolled keycap-less HintLayout mirroring
                        // what layout.rs's finalize_layout would produce —
                        // its dimension helpers are private, and this pill
                        // is the only selection-anchored hint.
                        let layout = HintLayout {
                            tooltip_x: x,
                            tooltip_y: y,
                            tooltip_w,
                            tooltip_h,
                            keycap_x: x,
                            keycap_y: y,
                            keycap_size: 0.0,
                            keycap_inset: 0.0,
                            desc_text_x: x + padding_h,
                            desc_text_y: (y + (tooltip_h - text_line_h) / 2.0 + font_px * 0.1).floor(),
                            corner_radius: (CORNER_RADIUS * dpi).floor(),
                            keycap_corner_radius: 0.0,
                            keycap_inner_corner_radius: 0.0,
                            shadow_offset: (SHADOW_OFFSET * dpi).floor(),
                            shadow_extra: (SHADOW_EXTRA * dpi).floor(),
                            border_px: dpi.ceil().max(1.0),
                        };
                        // The fade needs no frame scheduling: the cycle
                        // runs ControlFlow::Poll and every render worker
                        // free-runs, so `alpha()` is simply re-read each
                        // frame.
                        self.emit_hint(
                            rects,
                            &layout,
                            mon_left,
                            mon_top,
                            None,
                            IDX_DESC_NOTICE,
                            notice.alpha(),
                            state.accent_color,
                            false,
                        );
                    }
                }
            }
        }

        // --- OCR mode: every hint is suppressed ---
        // Same reasoning as the picker above: while OCR mode is active the
        // only live surface is the button panel, so nothing else may look
        // interactive. That includes the [M] cursor hint — the one hint
        // that otherwise survives capture — because the app thread
        // swallows 'm' in this mode; the hint and the key gate must agree
        // or the overlay advertises a key that does nothing.
        if state.ocr.active() {
            return;
        }

        // --- Non-cursor hints (gated by hints_visibility) ---
        if let Some(target) = hints_visibility(state) {
            if target.bounds == this_monitor.bounds {
                let zoomed = state.zoom > 1.0;

                if zoomed {
                    let mag_label = if state.overlays_visible {
                        "Enter Magnifier"
                    } else {
                        "Exit Magnifier"
                    };
                    self.buffers[IDX_KEY_MAGNIFIER].set(ts, "Q", key_font_px, true);
                    self.buffers[IDX_DESC_MAGNIFIER].set(ts, mag_label, font_px, false);
                    let desc_w = self.buffers[IDX_DESC_MAGNIFIER].width();
                    let (layout, hr) = compute_monitor_hint(&target, dpi, desc_w, text_line_h, &placed);
                    let mag_trail = !state.has_used_magnifier;
                    self.emit_hint(
                        rects,
                        &layout,
                        mon_left,
                        mon_top,
                        Some(IDX_KEY_MAGNIFIER),
                        IDX_DESC_MAGNIFIER,
                        1.0,
                        state.accent_color,
                        mag_trail,
                    );
                    placed.push(hr);

                    if !state.overlays_visible {
                        return;
                    }
                } else if state.hovered_monitor_name.is_some() {
                    self.text_buf.clear();
                    self.text_buf.push_str(&render_hint_text(
                        HINT_MONITOR.template,
                        state.hovered_window_title.as_deref(),
                        state.hovered_monitor_name.as_deref(),
                    ));
                    self.buffers[IDX_KEY_MONITOR].set(ts, "F", key_font_px, true);
                    self.buffers[IDX_DESC_MONITOR].set(ts, &self.text_buf, font_px, false);
                    let desc_w = self.buffers[IDX_DESC_MONITOR].width();
                    let (layout, hr) = compute_monitor_hint_top(&target, dpi, desc_w, text_line_h, &placed);
                    self.emit_hint(
                        rects,
                        &layout,
                        mon_left,
                        mon_top,
                        Some(IDX_KEY_MONITOR),
                        IDX_DESC_MONITOR,
                        1.0,
                        state.accent_color,
                        false,
                    );
                    placed.push(hr);
                }

                // --- [H] Select Color (follows crosshair) ---
                {
                    self.text_buf.clear();
                    match state.hovered_pixel_bgra {
                        Some([b, g, r, _]) => {
                            use std::fmt::Write;
                            let _ = write!(self.text_buf, "Select #{:02X}{:02X}{:02X}", r, g, b);
                        }
                        None => self.text_buf.push_str("Select #------"),
                    }
                    self.buffers[IDX_KEY_COLOR].set(ts, "H", key_font_px, true);
                    self.buffers[IDX_DESC_COLOR].set(ts, &self.text_buf, font_px, false);
                    let desc_w = self.buffers[IDX_DESC_COLOR].width();
                    let swatch_size = (font_px * 1.4).floor();
                    let swatch_gap = (6.0 * dpi).floor();
                    let total_desc_w = desc_w + swatch_gap + swatch_size;
                    let (layout, hr) = compute_color_hint(state, &target, dpi, total_desc_w, text_line_h, &placed);
                    self.emit_hint(
                        rects,
                        &layout,
                        mon_left,
                        mon_top,
                        Some(IDX_KEY_COLOR),
                        IDX_DESC_COLOR,
                        1.0,
                        state.accent_color,
                        false,
                    );

                    if let Some([b, g, r, _]) = state.hovered_pixel_bgra {
                        let swatch_x = (layout.desc_text_x - mon_left + desc_w + swatch_gap).round();
                        let swatch_y = (layout.desc_text_y - mon_top + (text_line_h - swatch_size) / 2.0).round();
                        let swatch_fill = [r as f32 / 255.0, g as f32 / 255.0, b as f32 / 255.0, 1.0];
                        rects.push(RectInstance {
                            dest_px: [swatch_x, swatch_y, swatch_x + swatch_size, swatch_y + swatch_size],
                            fill_rgba: swatch_fill,
                            border_rgba: [0.0, 0.0, 0.0, 1.0],
                            params: [dpi.ceil().max(1.0), 0.0, 0.0, 0.0],
                        });
                    }

                    placed.push(hr);
                }
            }
        }

        // --- Scroll-to-zoom hint (bottom-right, shown after slow mouse, hidden on first scroll) ---
        if state.show_scroll_hint && state.overlays_visible && !state.captured && !state.mouse_down {
            let cx = state.virtual_cursor.x.round() as i32;
            let cy = state.virtual_cursor.y.round() as i32;
            let on_this = cx >= this_monitor.bounds.min_x()
                && cx < this_monitor.bounds.max_x()
                && cy >= this_monitor.bounds.min_y()
                && cy < this_monitor.bounds.max_y();
            if on_this {
                self.buffers[IDX_KEY_SCROLL].set(ts, "↕", key_font_px, true);
                self.buffers[IDX_DESC_SCROLL].set(ts, "Scroll to zoom", font_px, false);
                let desc_w = self.buffers[IDX_DESC_SCROLL].width();
                let (layout, hr) = compute_monitor_hint(this_monitor, dpi, desc_w, text_line_h, &placed);
                self.emit_hint(
                    rects,
                    &layout,
                    mon_left,
                    mon_top,
                    Some(IDX_KEY_SCROLL),
                    IDX_DESC_SCROLL,
                    1.0,
                    state.accent_color,
                    true,
                );
                placed.push(hr);
            }
        }

        // --- [M] Toggle Cursor (independent of hints_visibility — survives capture) ---
        let cursor_hint_visible = state.overlays_visible && state.tips_mode.show_hints() && !state.mouse_down && state.zoom <= 1.0;

        if cursor_hint_visible {
            if let Some(cursor_rect) = state.cursor_image_rect {
                let cursor_in_selection = state.selection.filter(|sel| {
                    let sf = sel.to_f32();
                    cursor_rect[0] >= sf.left()
                        && cursor_rect[1] >= sf.top()
                        && cursor_rect[2] <= sf.right()
                        && cursor_rect[3] <= sf.bottom()
                });

                if let Some(sel) = cursor_in_selection {
                    let cursor_cx = (cursor_rect[0] + cursor_rect[2]) / 2.0;
                    let cursor_cy = (cursor_rect[1] + cursor_rect[3]) / 2.0;
                    let on_this_monitor = (cursor_cx as i32) >= this_monitor.bounds.min_x()
                        && (cursor_cx as i32) < this_monitor.bounds.max_x()
                        && (cursor_cy as i32) >= this_monitor.bounds.min_y()
                        && (cursor_cy as i32) < this_monitor.bounds.max_y();

                    if on_this_monitor {
                        let alpha_mul = if state.cursor_overlay_visible { 1.0 } else { 0.5 / 0.85 };

                        let pad = (CURSOR_SQUARE_PAD * dpi).floor();
                        let stroke = dpi.round().max(1.0);
                        Self::emit_dashed_square(rects, cursor_rect, mon_left, mon_top, pad, stroke, dpi, alpha_mul);

                        let cursor_label = if state.cursor_overlay_visible {
                            "Hide Cursor"
                        } else {
                            "Show Cursor"
                        };
                        self.buffers[IDX_KEY_CURSOR].set(ts, "M", key_font_px, true);
                        self.buffers[IDX_DESC_CURSOR].set(ts, cursor_label, font_px, false);
                        let desc_w = self.buffers[IDX_DESC_CURSOR].width();
                        let (layout, hr) = compute_cursor_hint(cursor_rect, sel, this_monitor, dpi, desc_w, text_line_h, &placed);
                        self.emit_hint(
                            rects,
                            &layout,
                            mon_left,
                            mon_top,
                            Some(IDX_KEY_CURSOR),
                            IDX_DESC_CURSOR,
                            alpha_mul,
                            state.accent_color,
                            false,
                        );
                        placed.push(hr);
                    }
                }
            }
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn emit_dashed_square(
        rects: &mut Vec<RectInstance>,
        cursor_rect: [f32; 4],
        mon_left: f32,
        mon_top: f32,
        pad: f32,
        stroke: f32,
        dpi: f32,
        alpha_mul: f32,
    ) {
        let cw = cursor_rect[2] - cursor_rect[0];
        let ch = cursor_rect[3] - cursor_rect[1];
        let side = cw.max(ch);
        let cx = (cursor_rect[0] + cursor_rect[2]) / 2.0;
        let cy = (cursor_rect[1] + cursor_rect[3]) / 2.0;
        let half = side / 2.0 + pad;
        let left = cx - half - mon_left;
        let top = cy - half - mon_top;
        let right = cx + half - mon_left;
        let bottom = cy + half - mon_top;
        let dash = (DASH_LEN * dpi).floor();

        rects.push(RectInstance {
            dest_px: [left, top, right, bottom],
            fill_rgba: [0.0, 0.0, 0.0, 0.9 * alpha_mul],
            border_rgba: [1.0, 1.0, 1.0, 0.9 * alpha_mul],
            params: [stroke, -dash, 0.0, 0.0],
        });
    }

    #[allow(clippy::too_many_arguments)]
    /// `key_idx == None` draws the pill without a keycap — the layout has
    /// already collapsed the space it would have taken.
    fn emit_hint(
        &mut self,
        rects: &mut Vec<RectInstance>,
        layout: &HintLayout,
        mon_left: f32,
        mon_top: f32,
        key_idx: Option<usize>,
        desc_idx: usize,
        alpha_mul: f32,
        accent: [f32; 4],
        trail: bool,
    ) {
        let a = |base: [f32; 4]| -> [f32; 4] { [base[0], base[1], base[2], base[3] * alpha_mul] };

        let lx = layout.tooltip_x - mon_left;
        let ly = layout.tooltip_y - mon_top;
        let lw = layout.tooltip_w;
        let lh = layout.tooltip_h;
        let so = layout.shadow_offset;
        let se = layout.shadow_extra;
        let aa = AA;

        let shadow_cr = layout.corner_radius + se;
        rects.push(RectInstance {
            dest_px: [lx + so - se - aa, ly + so - se - aa, lx + lw + so + se + aa, ly + lh + so + se + aa],
            fill_rgba: a(SHADOW_FILL),
            border_rgba: [0.0; 4],
            params: [0.0, 0.0, shadow_cr, aa],
        });

        let body_border = if trail { [0.0; 4] } else { a(TOOLTIP_BORDER) };
        rects.push(RectInstance {
            dest_px: [lx - aa, ly - aa, lx + lw + aa, ly + lh + aa],
            fill_rgba: a(TOOLTIP_FILL),
            border_rgba: body_border,
            params: [layout.border_px, 0.0, layout.corner_radius, aa],
        });

        if trail {
            let glow_pad: f32 = 7.0;
            rects.push(RectInstance {
                dest_px: [lx - glow_pad, ly - glow_pad, lx + lw + glow_pad, ly + lh + glow_pad],
                fill_rgba: [0.0; 4],
                border_rgba: a(accent),
                params: [layout.border_px, 2.0, layout.corner_radius, glow_pad],
            });
        }

        if let Some(key_idx) = key_idx {
            self.emit_keycap(rects, layout, mon_left, mon_top, key_idx, alpha_mul);
        }

        let desc_x = layout.desc_text_x - mon_left;
        let desc_y = layout.desc_text_y - mon_top;
        self.positions.push(PositionedText {
            buffer_idx: desc_idx,
            x: desc_x,
            y: desc_y,
            color: TOOLTIP_TEXT_COLOR,
            alpha_mul,
        });
    }

    /// The 3-layer bevelled keycap and its letter.
    fn emit_keycap(
        &mut self,
        rects: &mut Vec<RectInstance>,
        layout: &HintLayout,
        mon_left: f32,
        mon_top: f32,
        key_idx: usize,
        alpha_mul: f32,
    ) {
        let a = |base: [f32; 4]| -> [f32; 4] { [base[0], base[1], base[2], base[3] * alpha_mul] };
        let aa = AA;

        let kx = layout.keycap_x - mon_left;
        let ky = layout.keycap_y - mon_top;
        let ksz = layout.keycap_size;
        let ki = layout.keycap_inset;
        let kcr = layout.keycap_corner_radius;
        let icr = layout.keycap_inner_corner_radius;
        let bp = layout.border_px;

        rects.push(RectInstance {
            dest_px: [kx - aa, ky - aa, kx + ksz + aa, ky + ksz + aa],
            fill_rgba: a(KEYCAP_LIGHT),
            border_rgba: a(KEYCAP_BORDER),
            params: [bp, 0.0, kcr, aa],
        });

        let face_w = ksz - ki * 2.0;
        let face_h = ksz - ki * 2.0;
        let face_x = kx + (ksz - face_w) / 2.0;
        let vert_bias = bp.ceil().max(1.0);
        let face_y = ky + (ksz - face_h) / 2.0 - vert_bias;

        let shadow_cr = ((kcr - ki).max(1.0) + icr) * 0.5;
        rects.push(RectInstance {
            dest_px: [face_x - aa, face_y - aa, kx + ksz - bp + aa, ky + ksz - bp + aa],
            fill_rgba: a(KEYCAP_DARK),
            border_rgba: [0.0; 4],
            params: [0.0, 0.0, shadow_cr, aa],
        });

        rects.push(RectInstance {
            dest_px: [face_x - aa, face_y - aa, face_x + face_w + aa, face_y + face_h + aa],
            fill_rgba: a(KEYCAP_FACE),
            border_rgba: a(KEYCAP_BORDER),
            params: [bp, 0.0, icr, aa],
        });

        let key_text_w = self.buffers[key_idx].width();
        let font_px = self.buffers[key_idx].last_font_px;
        let key_line_h = font_px * 1.2;
        let key_text_x = face_x + (face_w - key_text_w) / 2.0;
        let key_text_y = (face_y + (face_h - key_line_h) / 2.0 + vert_bias / 2.0).round();
        self.positions.push(PositionedText {
            buffer_idx: key_idx,
            x: key_text_x,
            y: key_text_y,
            color: [0xFF, 0xFF, 0xFF, 0xFF],
            alpha_mul,
        });
    }

    pub fn text_areas<'a>(&'a self, viewport_px: (u32, u32), out: &mut Vec<TextArea<'a>>) {
        let (vw, vh) = (viewport_px.0 as i32, viewport_px.1 as i32);
        out.extend(self.positions.iter().map(|p| {
            let a = (p.color[3] as f32 * p.alpha_mul)
                .round()
                .min(255.0) as u8;
            TextArea {
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
                default_color: Color::rgba(p.color[0], p.color[1], p.color[2], a),
                custom_glyphs: &[],
            }
        }));
    }
}
