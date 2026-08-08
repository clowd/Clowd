//! Layout math for the button panel.
//!
//! Port of `SetButtonPanelPositions` from
//! `clowd_capture_dx/DxScreenCapture.cpp:112-195`. Pure CPU; no wgpu,
//! no winit, no globals — the caller passes the monitor's bounds, the
//! current selection, the monitor's DPI scale and which button set is
//! showing, and gets back a `PanelLayout` carrying that set's button
//! rects plus the area-indicator rect, both in virtual-desktop pixel
//! coordinates.
//!
//! The layout is computed in **integer** virtual-desktop pixels because
//! the C++ is integer and any f32 drift on top of integer selection
//! rects would show up as one-pixel jitter under zoom. Rounding follows
//! the C++ ceil/floor convention so a side-by-side comparison gives
//! identical pixel positions at every DPI.

use crate::selection::intersect_rects;
use clowd_rust_core::geometry::{RectExt, ScreenRect};

use super::model::{PanelButtonSet, MAX_PANEL_BUTTONS};
use crate::ui::command::Command;

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
///
/// Stays `Copy`: it is passed and stored by value in five places
/// (`PanelVisibility`, the renderer's per-frame snapshot, the app's
/// click routing, …) and lives on the per-frame-per-monitor path, so a
/// `Vec` here would allocate on every frame of every display.
#[derive(Debug, Clone, Copy)]
pub struct PanelLayout {
    /// Non-clickable info box showing the selection's width × height.
    /// Drawn first in the visual row, corresponds to
    /// `buttonPositions[NUM_SVG_BUTTONS]` in the C++.
    pub area_rect: ScreenRect,
    /// Which set these rects belong to. Carried *on the layout* rather
    /// than looked up separately so a button index can only ever be
    /// resolved against the set it was hit-tested in — see
    /// [`PanelLayout::command_at`].
    pub set: PanelButtonSet,
    /// Clickable button rects in the same order as `set.defs()`. Private
    /// because only the first `count` entries are real; the rest are the
    /// zero-rect padding that keeps this array `Copy` and fixed-size.
    /// Handing out the whole array is what would let a caller hit-test a
    /// stale button from the longer set.
    buttons: [ScreenRect; MAX_PANEL_BUTTONS],
    /// How many entries of `buttons` are live — always `set.len()`.
    count: usize,
}

impl PanelLayout {
    /// The live button rects, in `set.defs()` order. Never includes the
    /// padding slots.
    pub fn buttons(&self) -> &[ScreenRect] {
        &self.buttons[..self.count]
    }

    /// The command button `idx` emits.
    ///
    /// Resolving the index through the layout's own `set` is what makes
    /// an index/set desync structurally impossible: the alternative
    /// (index into a globally-chosen def table) silently fires
    /// `Command::Video` when the user clicks BACK, because both live at
    /// index 3 in their respective sets.
    ///
    /// Panics on an out-of-range index, which cannot happen for an index
    /// that came from [`hit_test`](Self::hit_test) — `count == set.len()`.
    pub fn command_at(&self, idx: usize) -> Command {
        self.set.defs()[idx].command
    }

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
        for (i, r) in self.buttons().iter().enumerate() {
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
/// `set` selects which strip of buttons to place. The strip is positioned
/// with its OWN width — the shorter OCR strip re-centres under the
/// selection on a swap rather than inheriting the capture strip's
/// footprint (see `long_edge_px` below for the re-click hazard story).
///
/// Returns `None` if the selection doesn't overlap the monitor at all
/// (i.e. the intersect produced an empty rect) — the caller handles
/// that by not showing a panel on this monitor.
pub fn compute_layout(monitor_bounds: ScreenRect, selection: ScreenRect, dpi_scale: f32, set: PanelButtonSet) -> Option<PanelLayout> {
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
    // The set's OWN length: each strip is positioned by the same
    // algorithm with its true width, so the shorter OCR strip re-centres
    // under the selection instead of sitting left-aligned in the capture
    // strip's wider footprint (owner call — an off-centre strip read as
    // misplaced). The strip therefore MOVES on every set swap; the
    // double-click hazard that motivated the old fixed-footprint
    // anchoring is absorbed by `PanelSwapGuard` in app.rs, which ignores
    // panel-aimed clicks for one OS double-click interval after ANY swap
    // — a stronger guarantee than frozen geometry ever was, since it
    // also covers the None→Some appearance.
    //
    // Orientation is unaffected: all three orientation predicates below
    // compare against `short_edge_px`, which does not depend on the
    // button count at all.
    let long_edge_px = svg_button_size * set.len() as i32 + button_spacing * 2 + area_size;
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

    // Fixed-size array (padded past `count`) so `PanelLayout` stays
    // `Copy`; only `set.len()` slots are filled and only that prefix is
    // ever handed out.
    let mut buttons = [ScreenRect::zero(); MAX_PANEL_BUTTONS];
    let count = set.len();
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
    for (i, slot) in buttons[..count].iter_mut().enumerate() {
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
        set,
        buttons,
        count,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The reference case every test below reuses: a 1080p monitor at the
    /// virtual-desktop origin with a comfortably-inset selection, which
    /// lands in the "below the selection, horizontal row" branch.
    const MON: (i32, i32, i32, i32) = (0, 0, 1920, 1080);
    const SEL: (i32, i32, i32, i32) = (500, 300, 600, 400);

    fn rect(t: (i32, i32, i32, i32)) -> ScreenRect {
        ScreenRect::from_xy_size(t.0, t.1, t.2, t.3)
    }

    fn layout_for(set: PanelButtonSet, dpi: f32) -> PanelLayout {
        compute_layout(rect(MON), rect(SEL), dpi, set).expect("selection overlaps the monitor")
    }

    /// Re-derives the two size constants exactly as `compute_layout`
    /// does, so the formula tests below check the *relationship* between
    /// the pieces rather than restating one magic number with another.
    fn metrics(dpi: f32) -> (i32, i32) {
        let z = dpi as f64;
        let button_size = ((UNSCALED_BUTTON_SIZE as f64) * z).floor() as i32;
        let spacing = (3.0 * z).ceil() as i32;
        (button_size, spacing)
    }

    /// Regression pin for the panel's exact pixel geometry — the anchor
    /// for the whole two-set refactor.
    ///
    /// Each added button widens `long_edge_px` by one `svg_button_size`
    /// and therefore shifts the centred panel left by half of that. The
    /// original single-set capture (7 buttons) was area x=597 with
    /// buttons at 650/703/…/953; macOS (8 buttons, + OCR) is that shifted
    /// left by 25; Windows (9 buttons, + OCR and SCROLL) by another 25.
    /// Nothing else about the math has moved since the capture.
    #[test]
    fn panel_geometry_is_pinned() {
        #[cfg(windows)]
        const EXPECTED: (i32, [i32; 9]) = (547, [600, 653, 703, 753, 803, 853, 903, 953, 1003]);
        #[cfg(not(windows))]
        const EXPECTED: (i32, [i32; 8]) = (572, [625, 678, 728, 778, 828, 878, 928, 978]);

        let l = layout_for(PanelButtonSet::Normal, 1.0);
        let (area_left, button_lefts) = EXPECTED;

        assert_eq!(l.area_rect, ScreenRect::from_xy_size(area_left, 715, 50, 50));
        assert_eq!(l.buttons().len(), button_lefts.len());
        for (i, expected_left) in button_lefts.iter().enumerate() {
            assert_eq!(l.buttons()[i], ScreenRect::from_xy_size(*expected_left, 715, 50, 50), "button {i}");
        }
    }

    /// Each strip spans exactly its own `long_edge_px` (area box, both
    /// spacings, one button per slot of the set actually drawn) and is
    /// centred under the selection with THAT width — the recentring the
    /// owner asked for, at every DPI, for both sets.
    #[test]
    fn each_set_is_centred_with_its_own_width_at_every_dpi() {
        for dpi in [1.0_f32, 1.25, 1.5, 2.0] {
            for set in PanelButtonSet::ALL {
                let (button_size, spacing) = metrics(dpi);
                let long_edge = button_size * set.len() as i32 + spacing * 2 + button_size;

                let l = layout_for(*set, dpi);
                let spanned = l.buttons().last().unwrap().right() - l.area_rect.left();
                assert_eq!(spanned, long_edge, "{set:?} span at dpi {dpi}");

                let centred_left = SEL.0 + SEL.2 / 2 - long_edge / 2;
                assert_eq!(l.area_rect.left(), centred_left, "{set:?} at dpi {dpi}");
            }
        }
    }

    /// The C++ emits one extra `button_spacing` *after* the first SVG
    /// button and none after any other, so buttons 1..n are flush. It
    /// looks like a bug and is not: it is the shipped pixel layout, and
    /// `long_edge_px` does not account for it. Pinned for both sets so a
    /// future "cleanup" has to delete this test on purpose.
    #[test]
    fn only_the_first_button_carries_extra_spacing() {
        let (_, spacing) = metrics(1.0);
        for set in PanelButtonSet::ALL {
            let l = layout_for(*set, 1.0);
            let b = l.buttons();
            assert_eq!(b[1].left() - b[0].right(), spacing, "{set:?} gap after button 0");
            assert_eq!(b[2].left() - b[1].right(), 0, "{set:?} gap after button 1");
        }
    }

    /// The recentring itself: the shorter OCR strip's midpoint sits on
    /// the selection's midpoint (to integer-division rounding), which
    /// necessarily means it does NOT share the capture strip's left edge
    /// — the old fixed-footprint anchoring is intentionally gone.
    #[test]
    fn ocr_strip_recentres_on_swap() {
        let normal = layout_for(PanelButtonSet::Normal, 1.0);
        let ocr = layout_for(PanelButtonSet::Ocr, 1.0);
        assert!(
            ocr.buttons().len() < normal.buttons().len(),
            "test assumes the OCR strip is shorter"
        );

        let sel_mid = SEL.0 + SEL.2 / 2;
        for (name, l) in [("Normal", &normal), ("Ocr", &ocr)] {
            let mid = (l.area_rect.left() + l.buttons().last().unwrap().right()) / 2;
            assert!((mid - sel_mid).abs() <= 1, "{name} strip midpoint {mid} vs selection {sel_mid}");
        }
        // And therefore the two strips genuinely moved relative to each
        // other — a regression back to shared-footprint anchoring would
        // keep the midpoints equal only by failing this.
        assert!(ocr.area_rect.left() > normal.area_rect.left(), "OCR strip did not recentre");
    }

    /// The padded tail of the fixed-size button array must never be
    /// hittable: only `set.len()` slots are live, and the zero rects past
    /// them contain no point. Probed just past the strip's right edge and
    /// at the origin (where `ScreenRect::zero()` padding would sit).
    #[test]
    fn hit_test_past_count_is_none() {
        let ocr = layout_for(PanelButtonSet::Ocr, 1.0);
        let last = ocr.buttons().len() - 1;
        let b = ocr.buttons()[last];

        // Its own last button is hittable…
        assert_eq!(ocr.hit_test((b.left() + 1) as f32, (b.top() + 1) as f32), Some(last));
        // …one pixel past its right edge is not…
        assert_eq!(ocr.hit_test((b.right() + 1) as f32, (b.top() + 1) as f32), None);
        // …and neither is the zero-rect padding's home at the origin.
        assert_eq!(ocr.hit_test(0.5, 0.5), None);
    }

    /// Orientation is chosen from `short_edge_px`, which is
    /// count-independent — so a set swap can never flip the panel from a
    /// row to a column. This selection hugs the bottom of the monitor,
    /// forcing the vertical (right-of-selection) branch for both sets.
    #[test]
    fn orientation_is_count_independent() {
        let mon = rect(MON);
        let sel = rect((100, 100, 400, 960));
        let normal = compute_layout(mon, sel, 1.0, PanelButtonSet::Normal).unwrap();
        let ocr = compute_layout(mon, sel, 1.0, PanelButtonSet::Ocr).unwrap();

        for (name, l) in [("Normal", &normal), ("Ocr", &ocr)] {
            let b = l.buttons();
            assert!(b[1].top() > b[0].bottom(), "{name} is not stacked vertically");
            for (i, r) in b.iter().enumerate() {
                assert_eq!(r.left(), l.area_rect.left(), "{name} button {i} is off the column axis");
            }
            // The vertical branch is bottom-anchored to the selection, so
            // the per-set length shows up as a different TOP while the
            // column's bottom edge stays put — the vertical analogue of
            // the horizontal recentring.
            assert_eq!(b[b.len() - 1].bottom(), sel.bottom(), "{name} column is not bottom-anchored");
        }
        // Same column axis; the shorter strip starts lower.
        assert_eq!(normal.area_rect.left(), ocr.area_rect.left());
        assert!(ocr.area_rect.top() > normal.area_rect.top());
    }

    /// Multi-monitor layouts routinely put a display at a negative
    /// virtual-desktop origin (a second monitor to the left of the
    /// primary). The layout is pure translation, so shifting the monitor
    /// and the selection together must shift every rect by exactly the
    /// same amount — no `.max(0)` clamp may leak in.
    #[test]
    fn negative_origin_monitor_is_a_pure_translation() {
        const DX: i32 = -1920;
        for set in PanelButtonSet::ALL {
            let here = layout_for(*set, 1.0);
            let there = compute_layout(
                rect((MON.0 + DX, MON.1, MON.2, MON.3)),
                rect((SEL.0 + DX, SEL.1, SEL.2, SEL.3)),
                1.0,
                *set,
            )
            .unwrap();

            assert_eq!(there.area_rect, here.area_rect.translate(euclid::vec2(DX, 0)), "{set:?} area");
            for (i, r) in there.buttons().iter().enumerate() {
                assert_eq!(*r, here.buttons()[i].translate(euclid::vec2(DX, 0)), "{set:?} button {i}");
            }
        }
    }

    /// `command_at` must answer for the layout's own set — the bug this
    /// whole shape exists to prevent is index 3 of the OCR strip
    /// resolving to `Command::Video`.
    #[test]
    fn command_at_resolves_against_the_layouts_own_set() {
        let ocr = layout_for(PanelButtonSet::Ocr, 1.0);
        for (i, def) in PanelButtonSet::Ocr.defs().iter().enumerate() {
            assert_eq!(ocr.command_at(i), def.command, "OCR button {i}");
        }
        let normal = layout_for(PanelButtonSet::Normal, 1.0);
        assert_eq!(normal.command_at(0), Command::Upload);
        assert_ne!(normal.command_at(0), ocr.command_at(0), "the two strips' index 0 must not collide");
    }
}
