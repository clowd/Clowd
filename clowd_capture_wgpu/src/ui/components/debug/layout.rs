//! Geometry for the debug instrumentation panels.

use crate::geometry::{RectExt, ScreenRect};

/// Distance from the screen edge (pre-DPI).
pub const SCREEN_MARGIN: f32 = 50.0;
/// Maximum panel width (pre-DPI).
pub const MAX_PANEL_WIDTH: f32 = 600.0;
/// Inner padding (pre-DPI).
pub const PADDING: f32 = 20.0;
/// Body font size in pixels at 100% DPI.
pub const BODY_FONT_PX: f32 = 12.0;
/// Sparkline graph height (pre-DPI). Covers enough vertical range to see
/// one budget line plus spikes above it without dominating the panel.
pub const GRAPH_HEIGHT: f32 = 60.0;
/// Extra height reserved below the sparkline for its colour legend row
/// (swatches + short labels). Pre-DPI.
pub const LEGEND_HEIGHT: f32 = 16.0;

/// Placement anchor for the top-level panel rect.
#[derive(Clone, Copy, Debug)]
pub enum PanelAnchor {
    TopLeft,
    TopRight,
}

/// Final layout for one bake of a debug panel.
#[derive(Clone, Copy, Debug)]
pub struct DebugPanelLayout {
    /// Outer panel rect in virtual-desktop pixels.
    pub panel_rect: ScreenRect,
    /// Inner padding (physical pixels at the target DPI).
    pub padding_px: f32,
    /// Height of one body text line in pixels at the target DPI.
    pub row_height: f32,
    /// Sparkline area in virtual-desktop pixels. Present only when the
    /// caller requested a graph; the panel height already accounts for it.
    pub graph_rect: Option<ScreenRect>,
}

/// Compute the panel rect + internal metrics for a debug overlay.
pub fn compute_layout(
    monitor_bounds: ScreenRect,
    dpi: f32,
    longest_line_px: f32,
    line_count: usize,
    anchor: PanelAnchor,
    include_graph: bool,
) -> DebugPanelLayout {
    let margin = (SCREEN_MARGIN * dpi).round();
    let padding = (PADDING * dpi).round();
    let max_w = (MAX_PANEL_WIDTH * dpi).round();
    let row_height = (BODY_FONT_PX * dpi * 1.4).round();
    let graph_h = (GRAPH_HEIGHT * dpi).round();
    let legend_h = (LEGEND_HEIGHT * dpi).round();

    let content_w = longest_line_px
        .min(max_w - padding * 2.0)
        .max(0.0);
    let panel_w = (content_w + padding * 2.0)
        .min(max_w)
        .round();

    // Base height: text + inner padding. When a graph is requested,
    // add the graph height, one row of spacing between the last text
    // line and the graph, plus a short legend strip below the graph.
    let mut panel_h = row_height * line_count as f32 + padding * 2.0;
    if include_graph {
        panel_h += graph_h + row_height + legend_h;
    }
    let panel_h = panel_h.round();

    let mon_left = monitor_bounds.left() as f32;
    let mon_top = monitor_bounds.top() as f32;
    let mon_right = monitor_bounds.right() as f32;

    let panel_left = match anchor {
        PanelAnchor::TopLeft => mon_left + margin,
        PanelAnchor::TopRight => mon_right - margin - panel_w,
    };
    let panel_top = mon_top + margin;

    let panel_rect = ScreenRect::from_xy_size(panel_left.round() as i32, panel_top.round() as i32, panel_w as i32, panel_h as i32);

    let graph_rect = if include_graph {
        // Graph sits inside the inner padding, directly below the text
        // block. Width = content width (full panel width minus
        // horizontal padding).
        let g_left = panel_rect.left() + padding as i32;
        let text_block_h = (row_height * line_count as f32).round() as i32;
        let g_top = panel_rect.top() + padding as i32 + text_block_h + row_height as i32;
        let g_w = (panel_w - padding * 2.0).round() as i32;
        let g_h = graph_h as i32;
        Some(ScreenRect::from_xy_size(g_left, g_top, g_w, g_h))
    } else {
        None
    };

    DebugPanelLayout {
        panel_rect,
        padding_px: padding,
        row_height,
        graph_rect,
    }
}
