//! Geometry for the Tips & Hotkeys panel.
//!
//! Mirrors `DxScreenCapture.cpp:741-828` and the constants at `pch.h:52-54`:
//!   * `DEBUGBOX_MARGIN = 50 px` — distance from screen edge.
//!   * `paddingHalf    = 10 px` — inner half-padding; `padding = 20 px`.
//!   * Minimum panel width = 450 px.
//!
//! All raw-pixel values are scaled by `dpi` (the primary monitor's DPI
//! scale) at computation time.

use crate::geometry::{RectExt, ScreenPointF, ScreenRect};

/// Distance from the screen edge (pre-DPI). `DEBUGBOX_MARGIN` in `pch.h:53`.
pub const SCREEN_MARGIN: f32 = 50.0;

/// Half of the inner padding (pre-DPI). `paddingHalf` in
/// `DxScreenCapture.cpp:768`.
pub const PADDING_HALF: f32 = 10.0;

/// Full inner padding (pre-DPI).
pub const PADDING: f32 = PADDING_HALF * 2.0;

/// Minimum panel width (pre-DPI). `DxScreenCapture.cpp:771`.
pub const MIN_PANEL_WIDTH: f32 = 450.0;

/// Body font size in pixels at 100% DPI. `DxScreenCapture.cpp:436`.
pub const BODY_FONT_PX: f32 = 12.0;

/// Title font size in pixels at 100% DPI. `DxScreenCapture.cpp:435`.
pub const TITLE_FONT_PX: f32 = 14.0;

/// Final layout for one bake of the panel. All rects are in
/// virtual-desktop pixel coordinates.
#[derive(Clone, Copy)]
pub struct TipsLayout {
    /// The outer panel rect covering both the title bar and body.
    pub panel_rect: ScreenRect,
    /// Title bar — the accent-colored strip above the body.
    pub title_rect: ScreenRect,
    /// Height (in px) of a single body-text line at the current DPI.
    pub row_height: f32,
    /// Starting y (panel-local) of the first row of the top tip block.
    pub top_block_y: f32,
    /// Starting y (panel-local) of the color-sampler row.
    pub color_row_y: f32,
    /// Starting y (panel-local) of the first row of the bottom tip block.
    pub bottom_block_y: f32,
    /// Panel-local x where the hotkey column starts.
    pub col_hotkey_x: f32,
    /// Panel-local x where the description column starts.
    pub col_desc_x: f32,
    /// Color-sampler square side length in pixels.
    pub color_box_size: f32,
    /// Panel-local x where the `#RRGGBB` text starts.
    pub color_hex_x: f32,
    /// Panel-local y for the `#RRGGBB` baseline.
    pub color_hex_y: f32,
    /// Panel-local y for the `rgb(R, G, B)` baseline.
    pub color_rgb_y: f32,
    /// DPI scale this layout was computed at.
    pub dpi_scale: f32,
    /// Width / height (px) of the drop-shadow strip extending out of
    /// the panel's right and bottom edges. The bake pixmap is enlarged
    /// by this on the right and bottom so the shadow has somewhere to
    /// sit. Mirrors `paddingHalf` in DxScreenCapture.cpp:784-787.
    pub shadow_extension_px: f32,
    /// `true` if the cursor occupies the bottom-right quadrant where
    /// the default right-anchored panel would sit, so we fell back to
    /// anchoring bottom-left. The component lifts this into its
    /// hashed `State` so the cursor itself (which changes every mouse
    /// move) can stay out of the hash.
    pub use_left_fallback: bool,
}

/// Compute panel placement and internal metrics.
///
/// `primary_bounds` is the primary monitor's rect in virtual-desktop pixels
/// (where we anchor). `cursor` is the virtual cursor, used to detect whether
/// the default bottom-right placement would overlap the cursor — in that
/// case we fall back to bottom-left. `dpi` is the primary monitor's DPI
/// scale. `longest_body_row_px` is the pre-measured max width of any body
/// row at the final pixel font size.
pub fn compute_layout(
    primary_bounds: ScreenRect,
    cursor: ScreenPointF,
    dpi: f32,
    longest_body_row_px: f32,
    title_width_px: f32,
    body_row_height_px: f32,
    title_height_px: f32,
) -> Option<TipsLayout> {
    let margin = (SCREEN_MARGIN * dpi).round();
    let padding = (PADDING * dpi).round();
    let padding_half = (PADDING_HALF * dpi).round();
    let min_w = (MIN_PANEL_WIDTH * dpi).round();

    // Panel width: max of the widest body row and the title, plus
    // horizontal padding on both sides — with a floor at MIN_PANEL_WIDTH.
    let content_w = longest_body_row_px.max(title_width_px);
    let panel_w = (content_w + padding * 2.0).max(min_w);

    // Row count for the body block. Four tips + two-line color row +
    // three tips = 9 lines. The color-sampler row takes two mono lines
    // plus a gap so the box doesn't crowd the hex/rgb text.
    //
    // Matches DxScreenCapture.cpp:772:
    //   panelHeight = metricsTips.height     ← 4 lines
    //               + metricsTips2.height    ← 3 lines
    //               + metricsColorHeader.height * 2  ← 2 lines for color row
    //               + padding*2
    //
    // On top of the C++ formula we add a half-padding of vertical
    // breathing room above AND below the color-sampler row, because
    // the 2×height swatch butts up against the adjacent text rows
    // otherwise.
    let body_lines = 4.0 + 3.0 + 2.0;
    let color_row_gap = padding_half;
    let body_h = body_row_height_px * body_lines + padding * 2.0 + color_row_gap * 2.0;

    // Title bar height: cap-height + half padding above + half padding below.
    let title_h = title_height_px + padding;

    let panel_h = title_h + body_h;

    // Default anchor: bottom-right of the primary monitor, inset by
    // SCREEN_MARGIN. Fall back to bottom-left if the cursor sits in the
    // zone where it would occlude the panel. Matches
    // DxScreenCapture.cpp:775-779 (the `mx > tr.left - DEBUGBOX_MARGIN*2`
    // fallback).
    let right_anchor_left = primary_bounds.right() as f32 - margin - panel_w;
    let anchor_top = primary_bounds.bottom() as f32 - margin - panel_h;

    let use_left_fallback = cursor.x > right_anchor_left - margin * 2.0
        && cursor.y > anchor_top - margin * 2.0;

    let panel_left = if use_left_fallback {
        primary_bounds.left() as f32 + margin
    } else {
        right_anchor_left
    };
    let panel_top = anchor_top;

    let panel_rect = ScreenRect::from_xy_size(
        panel_left.round() as i32,
        panel_top.round() as i32,
        panel_w.round() as i32,
        panel_h.round() as i32,
    );

    let title_rect = ScreenRect::from_xy_size(
        panel_rect.left(),
        panel_rect.top(),
        panel_rect.width(),
        title_h.round() as i32,
    );

    // Inner layout (panel-local coordinates).
    let body_top = title_h;
    let col_hotkey_x = padding;
    let col_desc_x = padding + body_row_height_px * 2.2;

    // Each tip block starts a row's height after the previous one.
    let top_block_y = body_top + padding;
    // Color row starts after the top block plus a half-padding gap so
    // the swatch doesn't visually crowd the "A  Select all monitors"
    // row above it.
    let color_row_y = top_block_y + body_row_height_px * 4.0 + color_row_gap;
    // Bottom block starts after the two-line color row plus another
    // half-padding gap below the swatch.
    let bottom_block_y = color_row_y + body_row_height_px * 2.0 + color_row_gap;

    // Color sampler: square = 2× text height, matches
    // `metricsColorHeader.height * 2` from the old code.
    let color_box_size = (body_row_height_px * 2.0).round();
    let color_hex_x = col_desc_x + color_box_size + padding_half;
    let color_hex_y = color_row_y;
    let color_rgb_y = color_row_y + body_row_height_px;

    Some(TipsLayout {
        panel_rect,
        title_rect,
        row_height: body_row_height_px,
        top_block_y,
        color_row_y,
        bottom_block_y,
        col_hotkey_x,
        col_desc_x,
        color_box_size,
        color_hex_x,
        color_hex_y,
        color_rgb_y,
        dpi_scale: dpi,
        shadow_extension_px: padding_half,
        use_left_fallback,
    })
}
