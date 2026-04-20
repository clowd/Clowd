//! Geometry for the debug instrumentation panels.
//!
//! Two panels share this layout module because they share the same body
//! style (dark 70%-opaque background, white monospaced text, 20px inner
//! padding). Mirrors the C++ version at
//! `DxScreenCapture.cpp:915-977` and the pixel constants at
//! `pch.h:52-53`:
//!   * `DEBUGBOX_MARGIN = 50 px` — distance from screen edge.
//!   * `DEBUGBOX_SIZE   = 600 px` — max panel width.
//!
//! Panels size themselves to the widest rendered line up to the cap.
//! Values pre-DPI; caller multiplies by monitor DPI at compute time.

use crate::geometry::{RectExt, ScreenRect};

/// Distance from the screen edge (pre-DPI). `DEBUGBOX_MARGIN` in `pch.h:53`.
pub const SCREEN_MARGIN: f32 = 50.0;
/// Maximum panel width (pre-DPI). `DEBUGBOX_SIZE` in `pch.h:52`.
pub const MAX_PANEL_WIDTH: f32 = 600.0;
/// Inner padding (pre-DPI). Matches the Tips panel / C++ `padding` at
/// `DxScreenCapture.cpp:769`.
pub const PADDING: f32 = 20.0;
/// Body font size in pixels at 100% DPI. Monospaced — "Consolas 12pt" in
/// the C++ version (`DxScreenCapture.cpp:436-437`).
pub const BODY_FONT_PX: f32 = 12.0;

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
    /// Inner padding (physical pixels at the target DPI). Text is
    /// rendered starting at `(panel.left + padding, panel.top + padding)`
    /// and advances by `row_height` per line.
    pub padding_px: f32,
    /// Height of one body text line in pixels at the target DPI.
    pub row_height: f32,
}

/// Compute the panel rect + internal metrics for a debug overlay.
///
/// * `monitor_bounds` — VD-pixel bounds of the monitor we're drawing on.
/// * `dpi` — that monitor's DPI scale.
/// * `longest_line_px` — widest shaped body line (from glyphon
///   measurement).
/// * `line_count` — number of body lines to render.
/// * `anchor` — which corner of the monitor to attach to.
pub fn compute_layout(
    monitor_bounds: ScreenRect,
    dpi: f32,
    longest_line_px: f32,
    line_count: usize,
    anchor: PanelAnchor,
) -> DebugPanelLayout {
    let margin = (SCREEN_MARGIN * dpi).round();
    let padding = (PADDING * dpi).round();
    let max_w = (MAX_PANEL_WIDTH * dpi).round();
    let row_height = (BODY_FONT_PX * dpi * 1.4).round();

    // Panel size: content bounded by MAX_PANEL_WIDTH; height grows with
    // line count.
    let content_w = longest_line_px
        .min(max_w - padding * 2.0)
        .max(0.0);
    let panel_w = (content_w + padding * 2.0)
        .min(max_w)
        .round();
    let panel_h = (row_height * line_count as f32 + padding * 2.0).round();

    let mon_left = monitor_bounds.left() as f32;
    let mon_top = monitor_bounds.top() as f32;
    let mon_right = monitor_bounds.right() as f32;

    let panel_left = match anchor {
        PanelAnchor::TopLeft => mon_left + margin,
        PanelAnchor::TopRight => mon_right - margin - panel_w,
    };
    let panel_top = mon_top + margin;

    let panel_rect = ScreenRect::from_xy_size(panel_left.round() as i32, panel_top.round() as i32, panel_w as i32, panel_h as i32);

    DebugPanelLayout {
        panel_rect,
        padding_px: padding,
        row_height,
    }
}
