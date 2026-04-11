//! Runtime state for the button panel.
//!
//! `PanelState` is built by the app thread when a selection becomes
//! captured (or changes post-capture) and then shipped to exactly one
//! render thread — the one whose monitor contains the panel — via a
//! new `RenderMsg::PanelState` variant. It's `Clone` so the app thread
//! can keep its own copy for hit-testing.

use crate::geometry::ScreenRect;

use super::layout::PanelLayout;

/// Panel state handed to the render thread. Carries everything a
/// backend needs to draw the panel for one frame:
///
///   * the layout (button rects + area-indicator rect, all in
///     virtual-desktop pixels),
///   * the hover index (the button index under the cursor, if any) —
///     so the bake backend can tint it with the 30% white overlay
///     without having to see discrete mouse events,
///   * the selection's (width, height) — which drives the area
///     indicator text,
///   * the containing monitor's bounds and DPI — so the backend can
///     translate the virtual-desktop rects into window-local physical
///     pixels exactly the way the existing shader does for the
///     selection rect,
///   * the accent colour (RGBA in [0, 1]) — primary buttons use this
///     as their background fill.
#[derive(Debug, Clone)]
pub struct PanelState {
    pub layout: PanelLayout,
    /// Index (0..NUM_SVG_BUTTONS) of the button currently under the
    /// cursor, or `None`. The area indicator is never "hovered" —
    /// hovering over it returns `None`.
    pub hover_idx: Option<usize>,
    /// Selection width / height in virtual-desktop pixels. Drives the
    /// `123 × 456` text in the area indicator.
    pub selection_size: (i32, i32),
    /// Bounds of the monitor the panel is being drawn on (virtual-
    /// desktop pixels). The render thread needs these to convert
    /// `layout.buttons[i]` into window-local physical pixels.
    pub monitor_bounds: ScreenRect,
    /// Monitor DPI scale (1.0 = 100%, 1.5 = 150%, …). Used to size
    /// font metrics and line thickness identically to the C++.
    pub dpi_scale: f32,
    /// User-configured accent colour (RGBA, each channel in [0, 1]).
    /// Matches `brushAccent` in the C++.
    pub accent_color: [f32; 4],
}
