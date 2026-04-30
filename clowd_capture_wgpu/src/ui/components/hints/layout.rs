use crate::geometry::{RectExt, ScreenRect, ScreenRectF};
use crate::ui::shared::{UiMonitor, UiSharedState};

pub const HINT_FONT_PX: f32 = 11.0;
const KEYCAP_SIZE: f32 = 20.0;
const KEYCAP_GAP: f32 = 5.0;
const HINT_PADDING_H: f32 = 6.0;
const HINT_PADDING_V: f32 = 4.0;
pub const CORNER_RADIUS: f32 = 6.0;
pub const SHADOW_OFFSET: f32 = 3.0;
pub const SHADOW_EXTRA: f32 = 2.0;
pub const KEYCAP_INSET: f32 = 3.0;
pub const KEYCAP_CORNER_RADIUS: f32 = 4.0;
pub const KEYCAP_INNER_CORNER_RADIUS: f32 = 3.0;

const CURSOR_OFFSET_Y: f32 = 6.0;
const CROSSHAIR_OFFSET_X: f32 = 18.0;
const CROSSHAIR_OFFSET_Y: f32 = 18.0;
const MONITOR_INSET_Y: f32 = 40.0;

/// Padding around the cursor image for the dashed highlight square.
pub const CURSOR_SQUARE_PAD: f32 = 4.0;

pub struct HintLayout {
    pub tooltip_x: f32,
    pub tooltip_y: f32,
    pub tooltip_w: f32,
    pub tooltip_h: f32,
    pub keycap_x: f32,
    pub keycap_y: f32,
    pub keycap_size: f32,
    pub keycap_inset: f32,
    pub desc_text_x: f32,
    pub desc_text_y: f32,
    pub corner_radius: f32,
    pub keycap_corner_radius: f32,
    pub keycap_inner_corner_radius: f32,
    pub shadow_offset: f32,
    pub shadow_extra: f32,
    pub border_px: f32,
}

/// Simple AABB for overlap testing.
#[derive(Clone, Copy)]
pub struct HintRect {
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
}

impl HintRect {
    pub fn overlaps(&self, other: &HintRect) -> bool {
        self.x < other.x + other.w && self.x + self.w > other.x && self.y < other.y + other.h && self.y + self.h > other.y
    }
}

/// Nudge `rect` so it doesn't overlap any of `placed`, staying within monitor bounds.
pub fn avoid_overlaps(rect: &mut HintRect, placed: &[HintRect], mon: ScreenRectF) {
    for existing in placed {
        if rect.overlaps(existing) {
            let gap = 4.0;
            rect.y = existing.y + existing.h + gap;
            if rect.y + rect.h > mon.bottom() {
                rect.y = existing.y - rect.h - gap;
            }
            rect.x = rect
                .x
                .clamp(mon.left(), (mon.right() - rect.w).max(mon.left()));
            rect.y = rect
                .y
                .clamp(mon.top(), (mon.bottom() - rect.h).max(mon.top()));
        }
    }
}

/// [H] Select Color — follows the crosshair (virtual_cursor).
pub fn compute_color_hint(
    state: &UiSharedState,
    monitor: &UiMonitor,
    dpi: f32,
    _key_text_width: f32,
    desc_text_width: f32,
    text_line_height: f32,
    placed: &[HintRect],
) -> (HintLayout, HintRect) {
    let (tooltip_w, tooltip_h, inner) = tooltip_dimensions(dpi, desc_text_width, text_line_height);
    let mon = monitor.bounds.to_f32();
    let cx = state.virtual_cursor.x.round();
    let cy = state.virtual_cursor.y.round();
    let off_x = CROSSHAIR_OFFSET_X * dpi;
    let off_y = CROSSHAIR_OFFSET_Y * dpi;

    let mut x = cx + off_x;
    let mut y = cy + off_y;
    if x + tooltip_w > mon.right() {
        x = cx - off_x - tooltip_w;
    }
    if y + tooltip_h > mon.bottom() {
        y = cy - off_y - tooltip_h;
    }
    x = x.clamp(mon.left(), (mon.right() - tooltip_w).max(mon.left()));
    y = y.clamp(mon.top(), (mon.bottom() - tooltip_h).max(mon.top()));

    let mut hr = HintRect {
        x,
        y,
        w: tooltip_w,
        h: tooltip_h,
    };
    avoid_overlaps(&mut hr, placed, mon);

    let layout = finalize_layout(hr.x, hr.y, tooltip_w, tooltip_h, dpi, inner);
    (layout, hr)
}

/// [F] Select Monitor — bottom-center of the current monitor (used by magnifier hint).
pub fn compute_monitor_hint(
    monitor: &UiMonitor,
    dpi: f32,
    _key_text_width: f32,
    desc_text_width: f32,
    text_line_height: f32,
    placed: &[HintRect],
) -> (HintLayout, HintRect) {
    let (tooltip_w, tooltip_h, inner) = tooltip_dimensions(dpi, desc_text_width, text_line_height);
    let mon = monitor.bounds.to_f32();
    let mon_center_x = (mon.left() + mon.right()) / 2.0;
    let inset_y = MONITOR_INSET_Y * dpi;

    let x = (mon_center_x - tooltip_w / 2.0).clamp(mon.left(), (mon.right() - tooltip_w).max(mon.left()));
    let y = (mon.bottom() - inset_y - tooltip_h).clamp(mon.top(), (mon.bottom() - tooltip_h).max(mon.top()));

    let mut hr = HintRect {
        x,
        y,
        w: tooltip_w,
        h: tooltip_h,
    };
    avoid_overlaps(&mut hr, placed, mon);

    let layout = finalize_layout(hr.x, hr.y, tooltip_w, tooltip_h, dpi, inner);
    (layout, hr)
}

/// [F] Select Monitor — top-center of the current monitor.
pub fn compute_monitor_hint_top(
    monitor: &UiMonitor,
    dpi: f32,
    _key_text_width: f32,
    desc_text_width: f32,
    text_line_height: f32,
    placed: &[HintRect],
) -> (HintLayout, HintRect) {
    let (tooltip_w, tooltip_h, inner) = tooltip_dimensions(dpi, desc_text_width, text_line_height);
    let mon = monitor.bounds.to_f32();
    let mon_center_x = (mon.left() + mon.right()) / 2.0;
    let inset_y = MONITOR_INSET_Y * dpi;

    let x = (mon_center_x - tooltip_w / 2.0).clamp(mon.left(), (mon.right() - tooltip_w).max(mon.left()));
    let y = (mon.top() + inset_y).clamp(mon.top(), (mon.bottom() - tooltip_h).max(mon.top()));

    let mut hr = HintRect {
        x,
        y,
        w: tooltip_w,
        h: tooltip_h,
    };
    avoid_overlaps(&mut hr, placed, mon);

    let layout = finalize_layout(hr.x, hr.y, tooltip_w, tooltip_h, dpi, inner);
    (layout, hr)
}

/// [M] Toggle Cursor — positioned near the cursor image, kept inside the
/// selection bounds so it doesn't overlap the selection border.
pub fn compute_cursor_hint(
    cursor_rect: [f32; 4],
    selection: ScreenRect,
    monitor: &UiMonitor,
    dpi: f32,
    _key_text_width: f32,
    desc_text_width: f32,
    text_line_height: f32,
    placed: &[HintRect],
) -> (HintLayout, HintRect) {
    let (tooltip_w, tooltip_h, inner) = tooltip_dimensions(dpi, desc_text_width, text_line_height);
    let mon = monitor.bounds.to_f32();
    let sel = selection.to_f32();

    let pad = (CURSOR_SQUARE_PAD * dpi).floor();
    let cursor_center_x = (cursor_rect[0] + cursor_rect[2]) / 2.0;
    let off_y = CURSOR_OFFSET_Y * dpi;

    let padded_bottom = cursor_rect[3] + pad;
    let padded_top = cursor_rect[1] - pad;

    let mut y = padded_bottom + off_y;
    if y + tooltip_h > sel.bottom() {
        y = padded_top - off_y - tooltip_h;
    }

    let mut x = cursor_center_x - tooltip_w / 2.0;
    x = x.clamp(sel.left(), (sel.right() - tooltip_w).max(sel.left()));
    y = y.clamp(sel.top(), (sel.bottom() - tooltip_h).max(sel.top()));
    x = x.clamp(mon.left(), (mon.right() - tooltip_w).max(mon.left()));
    y = y.clamp(mon.top(), (mon.bottom() - tooltip_h).max(mon.top()));

    let mut hr = HintRect {
        x,
        y,
        w: tooltip_w,
        h: tooltip_h,
    };
    avoid_overlaps(&mut hr, placed, mon);

    let layout = finalize_layout(hr.x, hr.y, tooltip_w, tooltip_h, dpi, inner);
    (layout, hr)
}

struct InnerMetrics {
    keycap_size: f32,
    keycap_inset: f32,
    padding_h: f32,
    padding_v: f32,
    keycap_gap: f32,
}

fn tooltip_dimensions(dpi: f32, desc_text_width: f32, text_line_height: f32) -> (f32, f32, InnerMetrics) {
    let keycap_size = (KEYCAP_SIZE * dpi).floor();
    let keycap_inset = (KEYCAP_INSET * dpi).floor().max(1.0);
    let padding_h = (HINT_PADDING_H * dpi).floor();
    let padding_v = (HINT_PADDING_V * dpi).floor();
    let keycap_gap = (KEYCAP_GAP * dpi).floor();

    let content_h = keycap_size.max(text_line_height);
    let tooltip_w = padding_h + keycap_size + keycap_gap + desc_text_width + padding_h;
    let tooltip_h = padding_v + content_h + padding_v;

    (
        tooltip_w,
        tooltip_h,
        InnerMetrics {
            keycap_size,
            keycap_inset,
            padding_h,
            padding_v,
            keycap_gap,
        },
    )
}

fn finalize_layout(x: f32, y: f32, w: f32, h: f32, dpi: f32, inner: InnerMetrics) -> HintLayout {
    let corner_radius = (CORNER_RADIUS * dpi).floor();
    let keycap_corner = (KEYCAP_CORNER_RADIUS * dpi).floor();
    let keycap_inner_corner = (KEYCAP_INNER_CORNER_RADIUS * dpi).floor();
    let shadow_offset = (SHADOW_OFFSET * dpi).floor();
    let shadow_extra = (SHADOW_EXTRA * dpi).floor();
    let border_px = dpi.ceil().max(1.0);

    let content_h = h - inner.padding_v * 2.0;
    let keycap_x = x + inner.padding_h;
    let keycap_y = y + inner.padding_v + (content_h - inner.keycap_size) / 2.0;

    let desc_text_x = keycap_x + inner.keycap_size + inner.keycap_gap;
    let font_px = (HINT_FONT_PX * dpi).floor();
    let text_line_h = font_px * 1.2;
    let desc_text_y = (y + (h - text_line_h) / 2.0 + font_px * 0.1).floor();

    HintLayout {
        tooltip_x: x,
        tooltip_y: y,
        tooltip_w: w,
        tooltip_h: h,
        keycap_x,
        keycap_y,
        keycap_size: inner.keycap_size,
        keycap_inset: inner.keycap_inset,
        desc_text_x,
        desc_text_y,
        corner_radius,
        keycap_corner_radius: keycap_corner,
        keycap_inner_corner_radius: keycap_inner_corner,
        shadow_offset,
        shadow_extra,
        border_px,
    }
}
