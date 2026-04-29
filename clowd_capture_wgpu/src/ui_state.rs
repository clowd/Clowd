use std::sync::Arc;

use crate::geometry::{ScreenPoint, ScreenPointF, ScreenRect};
use crate::settings::TipsMode;
use crate::system::{CapturedDesktop, CursorImage, MonitorInfo};
use crate::ui::shared::{UiMonitor, UiSharedState};

pub struct UiStateBuildInput<'a> {
    pub monitors: &'a [MonitorInfo],
    pub selection: Option<ScreenRect>,
    pub captured: bool,
    pub mouse_down: bool,
    pub dragging: bool,
    pub zoom: f32,
    pub virtual_cursor: ScreenPointF,
    pub accent_color: [f32; 4],
    pub tips_mode: TipsMode,
    pub debug_visible: bool,
    pub overlays_visible: bool,
    pub hovered_monitor_name: Option<String>,
    pub hovered_window_title: Option<String>,
    pub hovered_window_bounds: Option<ScreenRect>,
    pub hovered_window_index: Option<usize>,
    pub hovered_window_obstructed: bool,
    pub cursor_overlay_visible: bool,
    pub desktop_buffer: Option<&'a CapturedDesktop>,
}

pub fn build_ui_shared_state(input: UiStateBuildInput<'_>) -> UiSharedState {
    let hovered_pixel_bgra = input
        .desktop_buffer
        .and_then(|buf| sample_bgra(buf, cursor_point(input.virtual_cursor)));

    let cursor_image_rect = input.desktop_buffer.and_then(|buf| {
        let c = buf.cursor.as_ref()?;
        let (w, h) = match &c.image {
            CursorImage::AlphaBlended {
                width,
                height,
                ..
            } => (*width, *height),
            CursorImage::Masked {
                width,
                height,
                ..
            } => (*width, *height),
        };
        let left = c.position.x as f32 - c.hotspot_x as f32;
        let top = c.position.y as f32 - c.hotspot_y as f32;
        Some([left, top, left + w as f32, top + h as f32])
    });

    let monitors: Arc<[UiMonitor]> = input
        .monitors
        .iter()
        .map(|m| UiMonitor {
            bounds: m.bounds,
            dpi_scale: m.scale_factor,
            is_primary: m.is_primary,
        })
        .collect();

    let peek_covers_cursor = input.hovered_window_obstructed
        && cursor_image_rect
            .zip(input.hovered_window_bounds)
            .is_some_and(|(cr, wb)| {
                cr[0] < wb.max_x() as f32
                    && cr[2] > wb.min_x() as f32
                    && cr[1] < wb.max_y() as f32
                    && cr[3] > wb.min_y() as f32
            });

    UiSharedState {
        monitors,
        selection: input.selection,
        captured: input.captured,
        mouse_down: input.mouse_down,
        dragging: input.dragging,
        zoom: input.zoom,
        virtual_cursor: input.virtual_cursor,
        accent_color: input.accent_color,
        tips_mode: input.tips_mode,
        debug_visible: input.debug_visible,
        overlays_visible: input.overlays_visible,
        hovered_monitor_name: input.hovered_monitor_name,
        hovered_window_title: input.hovered_window_title,
        hovered_window_bounds: input.hovered_window_bounds,
        hovered_window_index: input.hovered_window_index,
        hovered_window_obstructed: input.hovered_window_obstructed,
        cursor_overlay_visible: input.cursor_overlay_visible && !peek_covers_cursor,
        hovered_pixel_bgra,
        cursor_image_rect: if peek_covers_cursor { None } else { cursor_image_rect },
    }
}

pub fn cursor_point(cursor: ScreenPointF) -> ScreenPoint {
    ScreenPoint::new(cursor.x.floor() as i32, cursor.y.floor() as i32)
}

fn sample_bgra(buf: &CapturedDesktop, p: ScreenPoint) -> Option<[u8; 4]> {
    let dx = p.x - buf.bounds.min_x();
    let dy = p.y - buf.bounds.min_y();
    if dx < 0 || dy < 0 {
        return None;
    }
    let (w, h) = (buf.width as i32, buf.height as i32);
    if dx >= w || dy >= h {
        return None;
    }
    let idx = ((dy * w + dx) as usize) * 4;
    let s = buf.bgra.get(idx..idx + 4)?;
    Some([s[0], s[1], s[2], s[3]])
}
