//! Layout math for the button panel.
//!
//! Port of `SetButtonPanelPositions` from
//! `clowd_capture_dx/DxScreenCapture.cpp:112-195`. Pure CPU; no wgpu,
//! no winit, no globals — the caller passes the monitor's bounds, the
//! current selection, and the monitor's DPI scale, and gets back a
//! `PanelLayout` carrying the `NUM_SVG_BUTTONS` button rects plus the
//! area-indicator rect, both in virtual-desktop pixel coordinates.
//!
//! The layout is computed in **integer** virtual-desktop pixels because
//! the C++ is integer and any f32 drift on top of integer selection
//! rects would show up as one-pixel jitter under zoom. Rounding follows
//! the C++ ceil/floor convention so a side-by-side comparison gives
//! identical pixel positions at every DPI.

use crate::selection::intersect_rects;
use clowd_rust_core::geometry::{RectExt, ScreenRect};

use super::model::NUM_SVG_BUTTONS;

/// Base DPI used by the C++ to convert logical (CSS-pixel) sizes to
/// per-monitor physical pixels. Matches `BASE_DPI` at
/// `clowd_capture_dx/pch.h:54`.
const BASE_DPI: f32 = 96.0;

/// 50 px at 100% scale. The C++ calls this `UNSCALED_BUTTON_SIZE`
/// (DxScreenCapture.cpp:24). Every button in the panel is a square
/// of this size multiplied by `dpi_zoom`.
const UNSCALED_BUTTON_SIZE: i32 = 50;

/// Whether the panel is laid out as a horizontal row (area indicator on
/// the left, buttons extending to the right) or a vertical column
/// (area indicator on top, buttons extending downwards). Matches the
/// C++ `vert` local in `SetButtonPanelPositions`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PanelOrientation {
    Horizontal,
    Vertical,
}

/// Result of laying out the panel. All rectangles are in **virtual-
/// desktop pixel coordinates** (same space as `ScreenRect` elsewhere
/// in the crate), so each render thread can translate them into its
/// own window-local physical pixels identically to how it treats the
/// selection rect.
#[derive(Debug, Clone, Copy)]
pub struct PanelLayout {
    /// Non-clickable info box showing the selection's width × height.
    /// Drawn first in the visual row, corresponds to
    /// `buttonPositions[NUM_SVG_BUTTONS]` in the C++.
    pub area_rect: ScreenRect,
    /// Clickable button rects in the same order as `BUTTON_DEFS`.
    pub buttons: [ScreenRect; NUM_SVG_BUTTONS],
}

impl PanelLayout {
    /// Return the button index whose rect contains `pt`, or `None` if
    /// no button is hit. Corresponds to `FrameUpdateHitTest` at
    /// `DxScreenCapture.cpp:1670-1690`.
    ///
    /// The area indicator is deliberately *not* hittable — clicking on
    /// it should do nothing (matching the C++, which only dispatches
    /// button hits at indices `0..NUM_SVG_BUTTONS`).
    pub fn hit_test(&self, pt_x_vd: f32, pt_y_vd: f32) -> Option<usize> {
        let px = pt_x_vd.floor() as i32;
        let py = pt_y_vd.floor() as i32;
        for (i, r) in self.buttons.iter().enumerate() {
            if px >= r.left() && px < r.right() && py >= r.top() && py < r.bottom() {
                return Some(i);
            }
        }
        None
    }
}

/// Compute the panel layout for a freshly-finalised selection on a
/// given monitor. `monitor_bounds` is the monitor's full screen rect in
/// virtual-desktop pixels; `selection` is the selection rect (already
/// clipped to this monitor by the caller — the C++ does this via
/// `Gdiplus::Rect::Intersect`); `dpi_scale` is `monitor.dpi / 96` and
/// scales every measurement to match the target display.
///
/// Returns `None` if the selection doesn't overlap the monitor at all
/// (i.e. the intersect produced an empty rect) — the caller handles
/// that by not showing a panel on this monitor.
pub fn compute_layout(monitor_bounds: ScreenRect, selection: ScreenRect, dpi_scale: f32) -> Option<PanelLayout> {
    // Clip the selection to the monitor. Mirrors
    // `Gdiplus::Rect::Intersect(selection, screenBounds, ...)` at
    // DxScreenCapture.cpp:130.
    let sel = intersect_rects(monitor_bounds, selection)?;

    // `dpi_zoom = screen.dpi / BASE_DPI` in the C++. Our caller already
    // hands us that ratio as `dpi_scale` (1.0 = 100%, 1.5 = 150%, …), so
    // we just rename it for symmetry with the C++ source. Keeping the
    // C++ variable name intact makes the side-by-side diff trivial.
    let dpi_zoom = dpi_scale as f64;
    let _ = BASE_DPI; // referenced only in the comment above

    let min_distance = (2.0 * dpi_zoom).ceil() as i32;
    let max_distance = (15.0 * dpi_zoom).ceil() as i32;
    let button_spacing = (3.0 * dpi_zoom).ceil() as i32;
    let svg_button_size = ((UNSCALED_BUTTON_SIZE as f64) * dpi_zoom).floor() as i32;
    let area_size = svg_button_size;
    let long_edge_px = svg_button_size * NUM_SVG_BUTTONS as i32 + button_spacing * 2 + area_size;
    let short_edge_px = svg_button_size;

    // Available space on each side of the selection (C++ lines 132-134).
    // `min_distance` is subtracted so the panel never hugs the screen
    // edge; can become negative if the selection already pushes past
    // that gap, which is fine — the comparisons below treat that as
    // "no space".
    let bottom_space = (monitor_bounds.bottom() - sel.bottom()).max(0) - min_distance;
    let right_space = (monitor_bounds.right() - sel.right()).max(0) - min_distance;
    let left_space = (sel.left() - monitor_bounds.left()).max(0) - min_distance;

    // Pick orientation + initial anchor point. Four priority cases from
    // the C++: below → right → left → inside. `vert` in the C++ is
    // confusingly-named (true means *horizontal* row under the
    // selection); we use `PanelOrientation` for clarity. The variable
    // names `ind_left` / `ind_top` are kept the same as the C++ so a
    // side-by-side diff is trivial.
    let orientation;
    let ind_left;
    let ind_top;

    if bottom_space >= short_edge_px {
        // Below the selection, horizontal row.
        orientation = PanelOrientation::Horizontal;
        ind_left = sel.left() + sel.width() / 2 - long_edge_px / 2;
        ind_top = monitor_bounds
            .bottom()
            .min(sel.bottom() + max_distance + short_edge_px)
            - short_edge_px;
    } else if right_space >= short_edge_px {
        // Right of the selection, vertical column.
        orientation = PanelOrientation::Vertical;
        ind_left = monitor_bounds
            .right()
            .min(sel.right() + max_distance + short_edge_px)
            - short_edge_px;
        ind_top = sel.bottom() - long_edge_px;
    } else if left_space >= short_edge_px {
        // Left of the selection, vertical column.
        orientation = PanelOrientation::Vertical;
        ind_left = (sel.left() - max_distance - short_edge_px).max(0);
        ind_top = sel.bottom() - long_edge_px;
    } else {
        // Inside the selection (fallback), horizontal row pulled up from
        // the bottom of the selection by 2 × max_distance. Matches the
        // "inside capture rect" branch at DxScreenCapture.cpp:156-161.
        orientation = PanelOrientation::Horizontal;
        ind_left = sel.left() + sel.width() / 2 - long_edge_px / 2;
        ind_top = sel.bottom() - short_edge_px - (max_distance * 2);
    }

    let horizontal_size = match orientation {
        PanelOrientation::Horizontal => long_edge_px,
        PanelOrientation::Vertical => short_edge_px,
    };
    // Clip the left edge so the panel stays on-screen. Matches the
    // C++ horizontal clip at lines 166-169. The vertical clip is
    // implicit in the `min(screenBounds.GetBottom(), …)` pattern above
    // and is intentionally one-sided — the layout never goes *below*
    // the screen in the bottom-case, so we don't need a bottom clamp.
    let mut panel_left = ind_left;
    if panel_left < monitor_bounds.left() {
        panel_left = monitor_bounds.left();
    } else if panel_left + horizontal_size > monitor_bounds.right() {
        panel_left = monitor_bounds.right() - horizontal_size;
    }

    let panel_top = ind_top;

    // Place the area indicator at the panel's origin, then walk the
    // buttons after it along the major axis (x for Horizontal, y for
    // Vertical). Matches the C++ `vchange += ...` loop at lines 184-194.
    let area_rect = ScreenRect::from_xy_size(panel_left, panel_top, area_size, area_size);

    let mut buttons = [ScreenRect::zero(); NUM_SVG_BUTTONS];
    let (mut cursor_x, mut cursor_y) = match orientation {
        PanelOrientation::Horizontal => {
            // Jump past the area indicator + spacing along X.
            (panel_left + area_size + button_spacing, panel_top)
        }
        PanelOrientation::Vertical => {
            // Jump past the area indicator + spacing along Y.
            (panel_left, panel_top + area_size + button_spacing)
        }
    };
    for (i, slot) in buttons.iter_mut().enumerate() {
        *slot = ScreenRect::from_xy_size(cursor_x, cursor_y, svg_button_size, svg_button_size);
        // The C++ has an `if (i == 0) *vchange += buttonSpacing;` after
        // the first button. That spacing is already consumed above
        // (we skipped ahead before placing button[0]), so we don't
        // duplicate it here — but we keep the second-spacing behaviour
        // by... actually, re-reading the C++: the loop body does
        // `*vchange += svgButtonSize; if (i == 0) *vchange += buttonSpacing;`
        // which means the spacing appears *between* button[0] and
        // button[1]. So the first and second SVG buttons have one
        // spacing between them in addition to the area→button[0]
        // spacing we've already placed.
        //
        // Replicating that gap:
        let step = svg_button_size + if i == 0 { button_spacing } else { 0 };
        match orientation {
            PanelOrientation::Horizontal => cursor_x += step,
            PanelOrientation::Vertical => cursor_y += step,
        }
    }

    Some(PanelLayout {
        area_rect,
        buttons,
    })
}
