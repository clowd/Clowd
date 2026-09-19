//! Layout math for the button panel: the floating tray.
//!
//! Pure CPU; no GPU, no winit, no globals. The caller passes the
//! monitor's bounds, the current selection, the monitor's DPI scale,
//! which button set is showing and which optional buttons are switched
//! on, and gets back a [`PanelLayout`]: the tray chassis rect, the emblem
//! slot, the "W × H" readout segment and one rect per visible button, all
//! in virtual-desktop pixel coordinates.
//!
//! # Tray and segments
//!
//! The panel is one rounded tray (`#25272B`, radius 8) holding a strip of
//! segments laid out along one axis, one `gap` apart, inset by `pad` on
//! every side:
//!
//! ```text
//! pad | emblem | gap | "W × H" | gap | button | gap | button | ... | pad
//! ```
//!
//! The emblem (a 40 px slot with the 32 px Clowd mark centred in it) and
//! the readout are drawn straight on the tray fill and are dead to hover
//! and clicks; only the buttons are hittable. Each button is a segment
//! (`#3A3E44`, radius 8) holding its icon and Title-case label inline:
//! `button_pad_h | icon | icon_label_gap | text | button_pad_h`, never
//! shorter than `button` along the strip.
//!
//! In a **row** (under the selection) every segment is `button` tall and
//! content-sized along X. In a **column** (beside the selection) every
//! segment is the column's full inner width — the widest label in the
//! UNION of both button tables (`MAX_LABEL_CHARS`) or the readout,
//! whichever is wider — so the column's width does not depend on which
//! set or which feature switches are visible, and a set swap can never
//! flip the orientation. The readout is a single line in both
//! orientations; a column widens to fit it rather than wrapping.
//!
//! # Tokens and rounding
//!
//! Logical sizes come from the C# `TrayTokens` (pad 4, gap 4, button 40,
//! emblem 40, mark 32, icon 20, radius 8) and the design workbench's
//! capture profile (button padding 8, icon/label gap 6, 12 px text,
//! readout padding 10). [`PanelMetrics::for_dpi`] scales them once per
//! layout: sizes **floor**, gaps/paddings/distances **ceil**, the icon
//! rounds, hairlines `round().max(1)`. Every product is computed in `f64`
//! and stored as an integer; nothing downstream may re-derive a size from
//! the DPI. A row is `pad + button + pad` = 48 px thick at 100 %.
//!
//! # Both threads must agree
//!
//! The same function runs on the render thread (to draw) and on the app
//! thread (to route clicks), so the geometry may depend on nothing the
//! app thread cannot see. Text can only be measured on the render thread,
//! so text widths are NOT measured: the bundled Cascadia faces are
//! monospaced at exactly 75/128 em per glyph, and [`text_width_px`] is
//! the integer ceiling of `chars × font_px × 75/128`. Buttons are sized
//! from `label.len()` (labels are ASCII) and the readout from the digit
//! count of the UNCLIPPED selection ([`area_text_chars`]), which is what
//! the renderer prints.
//!
//! # Placement
//!
//! Anchored to the selection, never draggable: below the selection as a
//! row when there is room, else a column to its right, else to its left,
//! else a row pulled up inside the selection. A placement has room when
//! the tray's THICKNESS (row height / column width) fits the free space
//! on that side and the LONGEST strip either set can show fits the
//! monitor along the strip axis; both tests are independent of the set
//! and the feature switches, so a swap never flips the orientation. A
//! display too narrow for the labelled row therefore gets the column.
//! The whole tray is then clamped onto the monitor on both axes; only
//! the shadow may clip. When nothing fits at all the thickness alone
//! picks the side and the strip's head (emblem, readout) overflows so
//! that every button stays on the monitor; [`PanelLayout::hit_test`] and
//! [`PanelLayout::contains`] treat anything off the monitor as dead.

use crate::selection::intersect_rects;
use clowd_rust_core::geometry::{RectExt, ScreenRect};

use super::model::{ButtonDef, PanelButtonSet, PanelFeatures, MAX_LABEL_CHARS, MAX_PANEL_BUTTONS};
use crate::ui::command::Command;

/// Whether the panel is laid out as a horizontal row (emblem on the
/// left, readout and buttons extending to the right) or a vertical
/// column (emblem on top, readout and buttons extending downwards).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PanelOrientation {
    Horizontal,
    Vertical,
}

/// Every DPI-scaled measurement the panel needs, computed once per layout
/// and carried on [`PanelLayout`] so both threads and the renderer read
/// the same integers. Virtual-desktop pixels.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PanelMetrics {
    /// Inset from the tray edge to the first segment (`TrayTokens.TrayPad`, 4).
    pub pad: i32,
    /// Space between consecutive segments (`TrayTokens.Gap`, 4).
    pub gap: i32,
    /// Segment thickness across the strip and minimum length along it
    /// (`TrayTokens.ButtonHeight` / `ButtonMinWidth`, 40).
    pub button: i32,
    /// Emblem slot length along the strip (`TrayTokens.EmblemLength`, 40).
    pub emblem: i32,
    /// Side of the Clowd mark centred in the emblem slot (`TrayEmblem.MarkSize`, 32).
    pub emblem_mark: i32,
    /// Horizontal padding inside a button, both ends (workbench `.btn{padding:0 8px}`).
    pub button_pad_h: i32,
    /// Button icon side (`TrayTokens.IconSize`, 20).
    pub icon: i32,
    /// Space between a button's icon and its label (workbench `.btn{gap:6px}`).
    pub icon_label_gap: i32,
    /// Label and readout font size (workbench `.lbl` / `.area`, 12 px).
    pub font_px: i32,
    /// Horizontal padding inside the readout, both ends (workbench `.area{padding:0 10px}`).
    pub area_pad_h: i32,
    /// Corner radius of the tray AND of every button (`TrayTokens.Radius`, 8).
    pub corner_radius: i32,
    /// Thickness of the tray's inner ring; a hairline.
    pub ring_px: i32,
    /// Thickness of the accelerator underline; a hairline.
    pub underline_px: i32,
    /// Minimum clearance kept between the tray and the monitor edge when
    /// deciding whether a side has room.
    pub min_distance: i32,
    /// Preferred gap between the selection and the tray.
    pub max_distance: i32,
}

impl PanelMetrics {
    /// Scale the logical tokens to `dpi_scale` (1.0 = 100 %). Sizes floor,
    /// gaps/paddings/distances ceil, the icon rounds, hairlines
    /// `round().max(1)`; products in `f64` so integer selection rects
    /// never pick up f32 drift.
    pub fn for_dpi(dpi_scale: f32) -> Self {
        let z = dpi_scale as f64;
        let floor = |v: f64| (v * z).floor() as i32;
        let ceil = |v: f64| (v * z).ceil() as i32;
        let hair = (z.round() as i32).max(1);
        Self {
            pad: ceil(4.0),
            gap: ceil(4.0),
            button: floor(40.0),
            emblem: floor(40.0),
            emblem_mark: floor(32.0),
            button_pad_h: ceil(8.0),
            icon: ((20.0 * z).round() as i32).max(1),
            icon_label_gap: ceil(6.0),
            font_px: floor(12.0),
            area_pad_h: ceil(10.0),
            corner_radius: floor(8.0),
            ring_px: hair,
            underline_px: hair,
            min_distance: ceil(2.0),
            max_distance: ceil(15.0),
        }
    }

    /// Tray thickness across the strip axis: pad + button + pad (48 at 100 %).
    pub fn row_thickness(&self) -> i32 {
        self.pad + self.button + self.pad
    }
}

/// Advance of `chars` glyphs of the bundled Cascadia faces at an integer
/// pixel size, rounded UP to whole pixels. Exact: every glyph in all four
/// bundled TTFs is 1200/2048 = 75/128 em wide (pinned by
/// `bundled_mono_glyphs_advance_exactly_75_128_em` in ui/gpu/panel.rs).
pub const fn text_width_px(chars: usize, font_px: i32) -> i32 {
    (chars as i32 * font_px * 75 + 127) / 128
}

/// `format!("{n}").len()` without allocating; the sign counts.
pub const fn decimal_len(n: i32) -> usize {
    let mut v = (n as i64).abs();
    let mut len = if n < 0 { 2 } else { 1 };
    while v >= 10 {
        v /= 10;
        len += 1;
    }
    len
}

/// Glyph count of the readout "W × H" (space, U+00D7, space = 3). Takes
/// the UNCLIPPED selection: the renderer prints `state.selection` as-is,
/// so a selection straddling two monitors keeps showing its true size.
pub fn area_text_chars(selection: ScreenRect) -> usize {
    decimal_len(selection.width()) + 3 + decimal_len(selection.height())
}

/// Along-axis length of a button whose label has `chars` glyphs:
/// pad | icon | gap | text | pad, never below the 40 px minimum.
pub fn button_len_for_chars(m: &PanelMetrics, chars: usize) -> i32 {
    (m.button_pad_h + m.icon + m.icon_label_gap + text_width_px(chars, m.font_px) + m.button_pad_h).max(m.button)
}

/// Slide `pos` so `[pos, pos+len)` lies inside `[lo, hi)`. When
/// `len > hi - lo` nothing can fit and the HIGH edge wins: the strip's
/// tail (the button end, `Exit` last) stays on the monitor and its head
/// (emblem, readout) is what overflows — those two are dead anyway.
fn clamp_span(pos: i32, len: i32, lo: i32, hi: i32) -> i32 {
    pos.max(lo).min(hi - len)
}

/// Row and column lengths of the LONGEST strip either set can show with
/// every feature switched on, for a readout `area_len` long. The
/// along-axis fit test in [`compute_layout`] compares these rather than
/// the visible strip so that, like the thickness test, it cannot flip
/// the orientation on a set swap or a feature switch.
fn longest_strip_lens(m: &PanelMetrics, area_len: i32) -> (i32, i32) {
    let mut row_buttons = 0;
    let mut col_buttons = 0;
    for set in PanelButtonSet::ALL {
        let defs = set.defs();
        let gaps = m.gap * (defs.len() as i32 - 1);
        let sum: i32 = defs
            .iter()
            .map(|d| button_len_for_chars(m, d.label.len()))
            .sum();
        row_buttons = row_buttons.max(sum + gaps);
        col_buttons = col_buttons.max(m.button * defs.len() as i32 + gaps);
    }
    let row = m.pad + m.emblem + m.gap + area_len + m.gap + row_buttons + m.pad;
    let col = m.pad + m.emblem + m.gap + m.button + m.gap + col_buttons + m.pad;
    (row, col)
}

/// Where the tray goes relative to the selection, in priority order.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Placement {
    /// A row under the selection.
    Below,
    /// A column to the right of the selection.
    Right,
    /// A column to the left of the selection.
    Left,
    /// A row pulled up inside the selection: the unconditional fallback.
    Inside,
}

/// Floor + half-open point-in-rect test shared by [`PanelLayout::hit_test`]
/// and [`PanelLayout::contains`].
fn rect_holds(r: ScreenRect, px: i32, py: i32) -> bool {
    px >= r.left() && px < r.right() && py >= r.top() && py < r.bottom()
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
    /// The chassis: shadow, fill and ring are drawn on this. Dead to
    /// clicks except via [`contains`](Self::contains).
    pub tray_rect: ScreenRect,
    /// Emblem slot (`emblem` along the strip; full inner width across).
    /// Not hittable.
    pub emblem_rect: ScreenRect,
    /// "W × H" readout segment. Not hittable. Row: `area_len` × `button`;
    /// column: `col_inner` × `button`.
    pub area_rect: ScreenRect,
    /// Row or column. The renderer has no orientation branch (icon and
    /// label placement is the same expression in both), so outside tests
    /// this is only ever written.
    #[cfg_attr(not(test), allow(dead_code))]
    pub orientation: PanelOrientation,
    /// The scaled tokens this layout was built from; the renderer reads
    /// every size from here rather than rescaling from the DPI.
    pub metrics: PanelMetrics,
    /// Which set these rects belong to. Kept for the callers that reason
    /// about the *mode* rather than the buttons (the double-click swap
    /// guard, the renderer's hover reset). Button identity does NOT come
    /// from here — see `defs` below.
    pub set: PanelButtonSet,
    /// Clickable button rects, one per *visible* button in strip order.
    /// Private because only the first `count` entries are real; the rest
    /// are the zero-rect padding that keeps this array `Copy` and
    /// fixed-size. Handing out the whole array is what would let a caller
    /// hit-test a stale button from a longer strip.
    buttons: [ScreenRect; MAX_PANEL_BUTTONS],
    /// The button behind each rect, same indices as `buttons`. Carried on
    /// the layout rather than re-derived from `set` because the visible
    /// strip depends on the shell's feature switches as well as the set:
    /// re-filtering at every consumer is exactly how index N comes to mean
    /// two different buttons on two threads. `&'static ButtonDef` keeps
    /// this `Copy`. Padding slots hold the set's first def and are never
    /// handed out.
    defs: [&'static ButtonDef; MAX_PANEL_BUTTONS],
    /// How many entries of `buttons` / `defs` are live.
    count: usize,
    /// The monitor this layout was built for. The panel is drawn only on
    /// this monitor's window, so a point outside it can never hit the
    /// tray: a strip that overflows the monitor (see `clamp_span`) must
    /// not leave undrawn buttons clickable from a neighbouring display.
    monitor: ScreenRect,
}

impl PanelLayout {
    /// The live button rects, in strip order. Never includes the padding
    /// slots.
    pub fn buttons(&self) -> &[ScreenRect] {
        &self.buttons[..self.count]
    }

    /// The live buttons, in the same order (and of the same length) as
    /// [`buttons`](Self::buttons) — what the renderer draws labels and
    /// icons from.
    pub fn defs(&self) -> &[&'static ButtonDef] {
        &self.defs[..self.count]
    }

    /// The command button `idx` emits.
    ///
    /// Resolving the index through the layout's own captured strip is
    /// what makes an index/strip desync structurally impossible: the
    /// alternative (index into a globally-chosen def table) silently
    /// fires `Command::Video` when the user clicks BACK, because both
    /// live at index 3 in their respective sets — and, now that buttons
    /// can be switched off, would fire the wrong command in the *same*
    /// set as soon as an earlier button is missing.
    ///
    /// Panics on an out-of-range index, which cannot happen for an index
    /// that came from [`hit_test`](Self::hit_test).
    pub fn command_at(&self, idx: usize) -> Command {
        self.defs()[idx].command
    }

    /// Return the button index whose rect contains `pt`, or `None` if
    /// no button is hit. Both coordinates are floored and each rect is
    /// half-open, so a point on a right/bottom edge belongs to the gap.
    ///
    /// The emblem, the readout and the tray padding are deliberately
    /// *not* hittable — clicking them does nothing; see
    /// [`contains`](Self::contains) for the chassis test. A point off the
    /// layout's monitor never hits anything, drawn rect or not.
    pub fn hit_test(&self, pt_x_vd: f32, pt_y_vd: f32) -> Option<usize> {
        let px = pt_x_vd.floor() as i32;
        let py = pt_y_vd.floor() as i32;
        if !rect_holds(self.monitor, px, py) {
            return None;
        }
        self.buttons()
            .iter()
            .position(|r| rect_holds(*r, px, py))
    }

    /// True when the point is anywhere on the tray body (padding, ring,
    /// emblem, readout or a button) AND on the layout's monitor. Same
    /// floor + half-open test as [`hit_test`](Self::hit_test), so a click
    /// that misses every button but lands here is the tray's to swallow.
    pub fn contains(&self, pt_x_vd: f32, pt_y_vd: f32) -> bool {
        let px = pt_x_vd.floor() as i32;
        let py = pt_y_vd.floor() as i32;
        rect_holds(self.monitor, px, py) && rect_holds(self.tray_rect, px, py)
    }
}

/// Compute the panel layout for a freshly-finalized selection on a
/// given monitor. `monitor_bounds` is the monitor's full screen rect in
/// virtual-desktop pixels; `selection` is the (unclipped) selection rect,
/// clipped to this monitor here for placement but read unclipped for the
/// readout's digit count; `dpi_scale` is `monitor.dpi / 96` and scales
/// every measurement to match the target display.
///
/// `set` selects which strip of buttons to place and `features` which of
/// its optional buttons the user has left switched on. The strip is
/// positioned with its OWN length — the shorter OCR strip re-centers under
/// the selection on a swap rather than inheriting the capture strip's
/// footprint (the re-click hazard that movement creates is
/// `PanelSwapGuard`'s job in app.rs), and a strip narrowed by a
/// switched-off button re-centers the same way. Orientation is unaffected:
/// the predicates compare the tray's thickness and the longest strip
/// either set can show, which depend on neither.
///
/// Returns `None` if the selection doesn't overlap the monitor at all
/// (i.e. the intersect produced an empty rect) — the caller handles
/// that by not showing a panel on this monitor.
pub fn compute_layout(
    monitor_bounds: ScreenRect,
    selection: ScreenRect,
    dpi_scale: f32,
    set: PanelButtonSet,
    features: PanelFeatures,
) -> Option<PanelLayout> {
    // Clip the selection to the monitor for placement.
    let sel = intersect_rects(monitor_bounds, selection)?;
    let m = PanelMetrics::for_dpi(dpi_scale);

    // 1. Resolve the visible strip ONCE, before any geometry: everything
    // below (the length, the rect walk, and the identity each rect
    // carries out of here) has to agree on the same button list.
    //
    // Fixed-size arrays (padded past `count`) so `PanelLayout` stays
    // `Copy`; only `count` slots are filled and only that prefix is ever
    // handed out. The def padding is the set's first button, an arbitrary
    // non-null filler — `defs()` never exposes it.
    let mut buttons = [ScreenRect::zero(); MAX_PANEL_BUTTONS];
    let mut defs: [&'static ButtonDef; MAX_PANEL_BUTTONS] = [&set.defs()[0]; MAX_PANEL_BUTTONS];
    let mut count = 0;
    for def in set.visible_defs(features) {
        defs[count] = def;
        count += 1;
    }

    // 2. Along-axis lengths. The readout's digit count comes from the
    // UNCLIPPED `selection`, which is what the renderer prints. Labels
    // are ASCII, so `label.len()` is the glyph count (pinned in model.rs).
    let area_chars = area_text_chars(selection);
    let area_len = m.area_pad_h * 2 + text_width_px(area_chars, m.font_px);
    let mut widths = [0i32; MAX_PANEL_BUTTONS];
    for (w, def) in widths[..count]
        .iter_mut()
        .zip(&defs[..count])
    {
        *w = button_len_for_chars(&m, def.label.len());
    }
    let sum_w: i32 = widths[..count].iter().sum();
    // `count >= 1`: `visible_defs_is_a_subsequence_and_never_empty`.
    let gaps = m.gap * (count as i32 - 1);

    // 3. The two candidate boxes. The column's inner width is the widest
    // label in the union of both tables, so it is set- and
    // feature-independent; it widens further only for a long readout.
    let row_len = m.pad + m.emblem + m.gap + area_len + m.gap + sum_w + gaps + m.pad;
    let row_thick = m.row_thickness();
    let col_inner = button_len_for_chars(&m, MAX_LABEL_CHARS).max(area_len);
    let col_thick = m.pad + col_inner + m.pad;
    let col_len = m.pad + m.emblem + m.gap + m.button + m.gap + m.button * count as i32 + gaps + m.pad;

    // 4. Available space on each side of the selection. `min_distance`
    // is subtracted so the tray never hugs the screen edge; can become
    // negative if the selection already pushes past that gap, which is
    // fine — the comparisons below treat that as "no space".
    let bottom_space = (monitor_bounds.bottom() - sel.bottom()).max(0) - m.min_distance;
    let right_space = (monitor_bounds.right() - sel.right()).max(0) - m.min_distance;
    let left_space = (sel.left() - monitor_bounds.left()).max(0) - m.min_distance;

    // 5. Placement, in priority order: below → right → left → inside. A
    // candidate fits when the tray's THICKNESS in that orientation fits
    // the free space on that side AND the longest strip either set can
    // show fits the monitor ALONG the strip; neither test depends on the
    // set or the features, so a swap can never flip the orientation. The
    // labelled row is 928 px at 100 % (1484 at 150 %), so a small or
    // portrait display that has room under the selection but not for the
    // whole row gets the column instead. When no candidate passes both
    // tests the thickness alone decides, as before, and step 6 keeps the
    // button end of the strip on the monitor.
    let (max_row_len, max_col_len) = longest_strip_lens(&m, area_len);
    let row_fits_along = max_row_len <= monitor_bounds.width();
    let col_fits_along = max_col_len <= monitor_bounds.height();
    let candidates = [
        (Placement::Below, bottom_space >= row_thick, row_fits_along),
        (Placement::Right, right_space >= col_thick, col_fits_along),
        (Placement::Left, left_space >= col_thick, col_fits_along),
        (Placement::Inside, true, row_fits_along),
    ];
    let placement = candidates
        .iter()
        .find(|(_, across, along)| *across && *along)
        .or_else(|| {
            candidates
                .iter()
                .find(|(_, across, _)| *across)
        })
        .map(|(p, _, _)| *p)
        .unwrap_or(Placement::Inside);

    // Orientation + initial anchor for the chosen placement.
    let (orientation, ind_left, ind_top) = match placement {
        // Below the selection, horizontal row, centred on the selection.
        Placement::Below => (
            PanelOrientation::Horizontal,
            sel.left() + sel.width() / 2 - row_len / 2,
            monitor_bounds
                .bottom()
                .min(sel.bottom() + m.max_distance + row_thick)
                - row_thick,
        ),
        // Right of the selection, vertical column, bottom-anchored.
        Placement::Right => (
            PanelOrientation::Vertical,
            monitor_bounds
                .right()
                .min(sel.right() + m.max_distance + col_thick)
                - col_thick,
            sel.bottom() - col_len,
        ),
        // Left of the selection, vertical column, bottom-anchored. No
        // absolute clamp here: step 6 clamps to the monitor, which is what
        // keeps a negative-origin monitor a pure translation.
        Placement::Left => (
            PanelOrientation::Vertical,
            sel.left() - m.max_distance - col_thick,
            sel.bottom() - col_len,
        ),
        // Inside the selection (fallback), horizontal row pulled up from
        // the bottom of the selection by 2 × max_distance.
        Placement::Inside => (
            PanelOrientation::Horizontal,
            sel.left() + sel.width() / 2 - row_len / 2,
            sel.bottom() - row_thick - m.max_distance * 2,
        ),
    };
    let (tray_w, tray_h) = match orientation {
        PanelOrientation::Horizontal => (row_len, row_thick),
        PanelOrientation::Vertical => (col_thick, col_len),
    };

    // 6. Keep the WHOLE tray on the monitor, both axes (the shadow may
    // clip). Only when nothing fits (step 5 fell back to thickness alone)
    // can the tray still be longer than the monitor; then its head
    // overflows and the buttons stay reachable, and `hit_test` /
    // `contains` ignore whatever lies off the monitor.
    let left = clamp_span(ind_left, tray_w, monitor_bounds.left(), monitor_bounds.right());
    let top = clamp_span(ind_top, tray_h, monitor_bounds.top(), monitor_bounds.bottom());
    let tray_rect = ScreenRect::from_xy_size(left, top, tray_w, tray_h);

    // 7. Walk the segments: emblem, readout, buttons, one `gap` apart,
    // starting `pad` inside the tray.
    let (ox, oy) = (left + m.pad, top + m.pad);
    let (emblem_rect, area_rect) = match orientation {
        PanelOrientation::Horizontal => {
            let emblem_rect = ScreenRect::from_xy_size(ox, oy, m.emblem, m.button);
            let mut x = ox + m.emblem + m.gap;
            let area_rect = ScreenRect::from_xy_size(x, oy, area_len, m.button);
            x += area_len + m.gap;
            for (slot, w) in buttons[..count]
                .iter_mut()
                .zip(&widths[..count])
            {
                *slot = ScreenRect::from_xy_size(x, oy, *w, m.button);
                x += *w + m.gap;
            }
            (emblem_rect, area_rect)
        }
        PanelOrientation::Vertical => {
            let emblem_rect = ScreenRect::from_xy_size(ox, oy, col_inner, m.emblem);
            let mut y = oy + m.emblem + m.gap;
            let area_rect = ScreenRect::from_xy_size(ox, y, col_inner, m.button);
            y += m.button + m.gap;
            for slot in buttons[..count].iter_mut() {
                *slot = ScreenRect::from_xy_size(ox, y, col_inner, m.button);
                y += m.button + m.gap;
            }
            (emblem_rect, area_rect)
        }
    };

    Some(PanelLayout {
        tray_rect,
        emblem_rect,
        area_rect,
        orientation,
        metrics: m,
        set,
        buttons,
        defs,
        count,
        monitor: monitor_bounds,
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
    const DPIS: [f32; 4] = [1.0, 1.25, 1.5, 2.0];

    fn rect(t: (i32, i32, i32, i32)) -> ScreenRect {
        ScreenRect::from_xy_size(t.0, t.1, t.2, t.3)
    }

    /// A selection hugging the bottom of `MON`, which forces the vertical
    /// (right-of-selection) branch for every set at every DPI.
    fn column_sel() -> ScreenRect {
        rect((100, 100, 400, 960))
    }

    fn layout_for(set: PanelButtonSet, dpi: f32) -> PanelLayout {
        layout_with(set, dpi, PanelFeatures::ALL)
    }

    fn layout_with(set: PanelButtonSet, dpi: f32, features: PanelFeatures) -> PanelLayout {
        compute_layout(rect(MON), rect(SEL), dpi, set, features).expect("selection overlaps the monitor")
    }

    fn column_for(set: PanelButtonSet, dpi: f32) -> PanelLayout {
        compute_layout(rect(MON), column_sel(), dpi, set, PanelFeatures::ALL).expect("selection overlaps the monitor")
    }

    /// All 32 on/off combinations of the five switches, built the same way
    /// as `FEATURE_COMBINATIONS` in model.rs.
    fn all_feature_combos() -> [PanelFeatures; 32] {
        let mut out = [PanelFeatures::ALL; 32];
        for (i, f) in out.iter_mut().enumerate() {
            *f = PanelFeatures {
                upload: i & 1 != 0,
                scroll_capture: i & 2 != 0,
                ocr: i & 4 != 0,
                share: i & 8 != 0,
                video: i & 16 != 0,
            };
        }
        out
    }

    /// The row length re-derived from the pieces (metrics, per-label
    /// button lengths, readout digit count) so the tests below check the
    /// relationship between them rather than one magic number.
    fn expected_row_len(m: &PanelMetrics, set: PanelButtonSet, features: PanelFeatures, sel: ScreenRect) -> i32 {
        let area_len = m.area_pad_h * 2 + text_width_px(area_text_chars(sel), m.font_px);
        let widths: Vec<i32> = set
            .visible_defs(features)
            .map(|d| button_len_for_chars(m, d.label.len()))
            .collect();
        let sum: i32 = widths.iter().sum();
        let gaps = m.gap * (widths.len() as i32 - 1);
        m.pad + m.emblem + m.gap + area_len + m.gap + sum + gaps + m.pad
    }

    fn centre(r: ScreenRect) -> (f32, f32) {
        ((r.left() + r.width() / 2) as f32, (r.top() + r.height() / 2) as f32)
    }

    /// Regression pin for the tray's exact pixel geometry at 100 %: the
    /// section-5 table of the plan, re-derived by hand. Readout
    /// "600 × 400" is 9 glyphs → 64 px + 2 × 10 = 84; button lengths are
    /// 42 + text (OCR 64, four-letter labels 71, five 78, six 85); the row
    /// is 4 + 40 + 4 + 84 + 4 + 752 + 36 + 4 = 928 wide and centred on
    /// x = 800, 15 px under the selection.
    #[test]
    fn panel_geometry_is_pinned() {
        let l = layout_for(PanelButtonSet::Normal, 1.0);
        assert_eq!(l.orientation, PanelOrientation::Horizontal);
        assert_eq!(l.tray_rect, rect((336, 715, 928, 48)));
        assert_eq!(l.emblem_rect, rect((340, 719, 40, 40)));
        assert_eq!(l.area_rect, rect((384, 719, 84, 40)));

        const BUTTONS: [(&str, i32, i32); 10] = [
            ("Upload", 472, 85),
            ("Edit", 561, 71),
            ("Video", 636, 78),
            ("Share", 718, 78),
            ("Scroll", 800, 85),
            ("Copy", 889, 71),
            ("Save", 964, 71),
            ("OCR", 1039, 64),
            ("Reset", 1107, 78),
            ("Exit", 1189, 71),
        ];
        assert_eq!(l.buttons().len(), BUTTONS.len());
        for (i, (label, x, w)) in BUTTONS.iter().enumerate() {
            assert_eq!(l.defs()[i].label, *label, "button {i}");
            assert_eq!(l.buttons()[i], rect((*x, 719, *w, 40)), "button {i} ({label})");
        }
        assert_eq!(l.buttons()[9].right() + 4, l.tray_rect.right());

        // The OCR strip: 85 + 85 + 71 + 71 + 71 = 383, four gaps, 539 wide.
        let o = layout_for(PanelButtonSet::Ocr, 1.0);
        assert_eq!(o.tray_rect, rect((531, 715, 539, 48)));
    }

    /// The literal token table for the four standard DPIs, every field.
    #[test]
    fn metrics_are_pinned_at_each_dpi() {
        // (pad, gap, button, emblem, emblem_mark, button_pad_h, icon,
        //  icon_label_gap, font_px, area_pad_h, corner_radius, ring_px,
        //  underline_px, min_distance, max_distance)
        const TABLE: [(f32, [i32; 15]); 4] = [
            (1.0, [4, 4, 40, 40, 32, 8, 20, 6, 12, 10, 8, 1, 1, 2, 15]),
            (1.25, [5, 5, 50, 50, 40, 10, 25, 8, 15, 13, 10, 1, 1, 3, 19]),
            (1.5, [6, 6, 60, 60, 48, 12, 30, 9, 18, 15, 12, 2, 2, 3, 23]),
            (2.0, [8, 8, 80, 80, 64, 16, 40, 12, 24, 20, 16, 2, 2, 4, 30]),
        ];
        for (dpi, t) in TABLE {
            let m = PanelMetrics::for_dpi(dpi);
            let expected = PanelMetrics {
                pad: t[0],
                gap: t[1],
                button: t[2],
                emblem: t[3],
                emblem_mark: t[4],
                button_pad_h: t[5],
                icon: t[6],
                icon_label_gap: t[7],
                font_px: t[8],
                area_pad_h: t[9],
                corner_radius: t[10],
                ring_px: t[11],
                underline_px: t[12],
                min_distance: t[13],
                max_distance: t[14],
            };
            assert_eq!(m, expected, "metrics at dpi {dpi}");
            assert_eq!(m.row_thickness(), t[0] * 2 + t[2], "row thickness at dpi {dpi}");
        }
    }

    /// `text_width_px` is the ceiling of `chars × px × 75/128`, the exact
    /// advance of the bundled monospace faces.
    #[test]
    fn text_width_px_is_the_ceiling_of_the_mono_advance() {
        const CASES: [(usize, i32, i32); 12] = [
            (3, 12, 22),
            (4, 12, 29),
            (5, 12, 36),
            (6, 12, 43),
            (9, 12, 64),
            (10, 12, 71),
            (11, 12, 78),
            (13, 12, 92),
            (6, 15, 53),
            (6, 18, 64),
            (6, 24, 85),
            (0, 12, 0),
        ];
        for (chars, px, want) in CASES {
            assert_eq!(text_width_px(chars, px), want, "{chars} glyphs at {px} px");
            let exact = chars as f64 * px as f64 * 75.0 / 128.0;
            assert_eq!(text_width_px(chars, px), exact.ceil() as i32, "ceiling of {exact}");
        }
    }

    #[test]
    fn decimal_len_matches_to_string() {
        for n in [-100000, -1, 0, 1, 9, 10, 99, 100, 9999, 10000, i32::MAX, i32::MIN] {
            assert_eq!(decimal_len(n), n.to_string().len(), "decimal_len({n})");
        }
    }

    /// The readout glyph count must match what the renderer formats,
    /// including the "×" and its two spaces, for any width/height.
    #[test]
    fn area_text_chars_matches_format_len() {
        const VALUES: [i32; 8] = [0, 9, 10, 99, 100, 9999, 10000, -5];
        for w in VALUES {
            for h in VALUES {
                let r = ScreenRect::from_xy_size(0, 0, w, h);
                let formatted = format!("{} \u{00D7} {}", w, h);
                assert_eq!(area_text_chars(r), formatted.chars().count(), "{w} x {h}");
            }
        }
    }

    /// A row is `pad + button + pad` thick (48 logical px) and a column is
    /// `pad + inner + pad` wide, at every standard DPI.
    #[test]
    fn tray_thickness_is_48_logical_px_at_every_standard_dpi() {
        for dpi in DPIS {
            let m = PanelMetrics::for_dpi(dpi);
            for set in PanelButtonSet::ALL {
                let row = layout_for(*set, dpi);
                assert_eq!(row.orientation, PanelOrientation::Horizontal, "{set:?} at dpi {dpi}");
                assert_eq!(row.tray_rect.height(), (48.0 * dpi) as i32, "{set:?} row thickness at dpi {dpi}");
                assert_eq!(row.tray_rect.height(), m.row_thickness());

                let col = column_for(*set, dpi);
                assert_eq!(col.orientation, PanelOrientation::Vertical, "{set:?} at dpi {dpi}");
                assert_eq!(
                    col.tray_rect.width(),
                    2 * m.pad + col.area_rect.width(),
                    "{set:?} column thickness at dpi {dpi}"
                );
            }
        }
    }

    /// Each strip spans exactly its own row length (emblem, readout, one
    /// content-sized button per visible def, one gap between each) and is
    /// centered under the selection with THAT length, at every DPI, for
    /// both sets. At 200 % the labelled capture row (1845 px) is wider
    /// than half the monitor, so its centred left edge falls off the
    /// monitor and the clamp pins it to x = 0 instead.
    #[test]
    fn each_set_is_centered_with_its_own_width_at_every_dpi() {
        let mut clamped_cases = 0;
        for dpi in DPIS {
            for set in PanelButtonSet::ALL {
                let m = PanelMetrics::for_dpi(dpi);
                let row_len = expected_row_len(&m, *set, PanelFeatures::ALL, rect(SEL));
                assert!(row_len <= MON.2, "{set:?} at dpi {dpi} does not fit the monitor");

                let l = layout_for(*set, dpi);
                assert_eq!(l.tray_rect.width(), row_len, "{set:?} row length at dpi {dpi}");
                assert_eq!(
                    l.buttons().last().unwrap().right() + m.pad,
                    l.tray_rect.right(),
                    "{set:?} at dpi {dpi}"
                );

                let centered_left = SEL.0 + SEL.2 / 2 - row_len / 2;
                if centered_left < MON.0 {
                    clamped_cases += 1;
                    assert_eq!(
                        l.tray_rect.left(),
                        MON.0,
                        "{set:?} at dpi {dpi} should be pinned to the monitor edge"
                    );
                } else {
                    assert_eq!(l.tray_rect.left(), centered_left, "{set:?} at dpi {dpi}");
                }
            }
        }
        assert_eq!(clamped_cases, 1, "only Normal at 200 % is expected to hit the left clamp");
    }

    /// The recentring itself: the shorter OCR strip's midpoint sits on
    /// the selection's midpoint (to integer-division rounding), which
    /// necessarily means it does NOT share the capture strip's left edge
    /// — the old fixed-footprint anchoring is intentionally gone.
    #[test]
    fn ocr_strip_recenters_on_swap() {
        let normal = layout_for(PanelButtonSet::Normal, 1.0);
        let ocr = layout_for(PanelButtonSet::Ocr, 1.0);
        assert!(
            ocr.buttons().len() < normal.buttons().len(),
            "test assumes the OCR strip is shorter"
        );

        let sel_mid = SEL.0 + SEL.2 / 2;
        for (name, l) in [("Normal", &normal), ("Ocr", &ocr)] {
            let mid = (l.tray_rect.left() + l.tray_rect.right()) / 2;
            assert!((mid - sel_mid).abs() <= 1, "{name} strip midpoint {mid} vs selection {sel_mid}");
        }
        // And therefore the two strips genuinely moved relative to each
        // other — a regression back to shared-footprint anchoring would
        // keep the midpoints equal only by failing this.
        assert!(ocr.tray_rect.left() > normal.tray_rect.left(), "OCR strip did not recenter");
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

    /// Orientation is chosen from the tray thickness, which is
    /// count-independent — so a set swap can never flip the panel from a
    /// row to a column. This selection hugs the bottom of the monitor,
    /// forcing the vertical (right-of-selection) branch for both sets.
    #[test]
    fn orientation_is_count_independent() {
        let sel = column_sel();
        let normal = column_for(PanelButtonSet::Normal, 1.0);
        let ocr = column_for(PanelButtonSet::Ocr, 1.0);
        let m = PanelMetrics::for_dpi(1.0);

        for (name, l) in [("Normal", &normal), ("Ocr", &ocr)] {
            assert_eq!(l.orientation, PanelOrientation::Vertical, "{name}");
            let b = l.buttons();
            assert_eq!(b[1].top() - b[0].bottom(), m.gap, "{name} is not stacked one gap apart");
            for (i, r) in b.iter().enumerate() {
                assert_eq!(r.left(), l.tray_rect.left() + m.pad, "{name} button {i} is off the column axis");
                assert_eq!(r.width(), l.area_rect.width(), "{name} button {i} is not full width");
            }
            // The vertical branch is bottom-anchored to the selection, so
            // the per-set length shows up as a different TOP while the
            // column's bottom edge stays put — the vertical analog of
            // the horizontal recentring.
            assert_eq!(l.tray_rect.bottom(), sel.bottom(), "{name} column is not bottom-anchored");
        }
        // Same column axis; the shorter strip starts lower.
        assert_eq!(normal.tray_rect.left(), ocr.tray_rect.left());
        assert!(ocr.emblem_rect.top() > normal.emblem_rect.top());
    }

    /// Multi-monitor layouts routinely put a display at a negative
    /// virtual-desktop origin (a second monitor to the left of the
    /// primary). The layout is pure translation, so shifting the monitor
    /// and the selection together must shift every rect by exactly the
    /// same amount — no `.max(0)` clamp may leak in. The second selection
    /// takes the left-of-selection branch, which used to carry exactly
    /// such a clamp.
    #[test]
    fn negative_origin_monitor_is_a_pure_translation() {
        const DX: i32 = -1920;
        let below = rect(SEL);
        let left_branch = rect((1400, 100, 500, 960));
        for set in PanelButtonSet::ALL {
            for (case, sel) in [("below", below), ("left", left_branch)] {
                let here = compute_layout(rect(MON), sel, 1.0, *set, PanelFeatures::ALL).unwrap();
                let there = compute_layout(
                    rect((MON.0 + DX, MON.1, MON.2, MON.3)),
                    sel.translate(euclid::vec2(DX, 0)),
                    1.0,
                    *set,
                    PanelFeatures::ALL,
                )
                .unwrap();
                if case == "left" {
                    assert_eq!(here.orientation, PanelOrientation::Vertical);
                    assert!(here.tray_rect.right() < sel.left(), "{set:?} is not left of the selection");
                }

                let shift = euclid::vec2(DX, 0);
                assert_eq!(there.orientation, here.orientation, "{set:?} {case} orientation");
                assert_eq!(there.tray_rect, here.tray_rect.translate(shift), "{set:?} {case} tray");
                assert_eq!(there.emblem_rect, here.emblem_rect.translate(shift), "{set:?} {case} emblem");
                assert_eq!(there.area_rect, here.area_rect.translate(shift), "{set:?} {case} area");
                for (i, r) in there.buttons().iter().enumerate() {
                    assert_eq!(*r, here.buttons()[i].translate(shift), "{set:?} {case} button {i}");
                }
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

    /// The failure mode a switched-off button introduces: every rect after
    /// the gap shifts down one index, so a layout that resolved commands
    /// through the *unfiltered* table would fire EDIT when the user clicks
    /// the button drawn as VIDEO. UPLOAD is index 0, so switching it off
    /// moves every remaining button.
    #[test]
    fn switching_a_button_off_shifts_the_rest_and_their_commands_with_them() {
        let full = layout_for(PanelButtonSet::Normal, 1.0);
        let no_upload = layout_with(
            PanelButtonSet::Normal,
            1.0,
            PanelFeatures {
                upload: false,
                ..PanelFeatures::ALL
            },
        );

        assert_eq!(no_upload.buttons().len(), full.buttons().len() - 1);
        assert_eq!(no_upload.command_at(0), Command::Edit);
        for (i, def) in PanelButtonSet::Normal
            .defs()
            .iter()
            .filter(|d| d.command != Command::Upload)
            .enumerate()
        {
            assert_eq!(no_upload.command_at(i), def.command, "button {i}");
        }

        // Hit-testing the drawn rect must agree: the click lands where
        // EDIT is now drawn and fires EDIT, not UPLOAD.
        let b = no_upload.buttons()[0];
        let idx = no_upload
            .hit_test((b.left() + 1) as f32, (b.top() + 1) as f32)
            .expect("first button is hittable");
        assert_eq!(no_upload.command_at(idx), Command::Edit);
    }

    /// A narrowed strip re-centers with its own length, exactly like a set
    /// swap does — it must not sit left-aligned in the full strip's
    /// footprint.
    #[test]
    fn a_narrowed_strip_recenters() {
        let m = PanelMetrics::for_dpi(1.0);
        let features = PanelFeatures {
            upload: false,
            share: false,
            scroll_capture: false,
            video: false,
            ocr: false,
        };
        let l = layout_with(PanelButtonSet::Normal, 1.0, features);

        let row_len = expected_row_len(&m, PanelButtonSet::Normal, features, rect(SEL));
        assert_eq!(l.tray_rect.width(), row_len);
        assert_eq!(l.tray_rect.left(), SEL.0 + SEL.2 / 2 - row_len / 2);
        assert!(
            l.tray_rect.left()
                > layout_for(PanelButtonSet::Normal, 1.0)
                    .tray_rect
                    .left(),
            "narrowed strip did not recenter"
        );
    }

    /// The segment walk: emblem `pad` inside the tray, then readout, then
    /// the buttons, each one `gap` after the last, the last one `pad`
    /// before the far tray edge; every segment is `button` across in a
    /// row and the column's inner width across in a column.
    #[test]
    fn segments_are_inset_by_the_pad_and_one_gap_apart() {
        for dpi in DPIS {
            let m = PanelMetrics::for_dpi(dpi);
            for set in PanelButtonSet::ALL {
                let row = layout_for(*set, dpi);
                let t = row.tray_rect;
                assert_eq!(row.emblem_rect.left(), t.left() + m.pad, "{set:?} row emblem at dpi {dpi}");
                assert_eq!(row.emblem_rect.width(), m.emblem);
                assert_eq!(row.area_rect.left() - row.emblem_rect.right(), m.gap);
                let b = row.buttons();
                assert_eq!(b[0].left() - row.area_rect.right(), m.gap);
                for pair in b.windows(2) {
                    assert_eq!(pair[1].left() - pair[0].right(), m.gap, "{set:?} row gap at dpi {dpi}");
                }
                assert_eq!(b.last().unwrap().right() + m.pad, t.right());
                for r in [row.emblem_rect, row.area_rect]
                    .iter()
                    .chain(b)
                {
                    assert_eq!(r.top(), t.top() + m.pad, "{set:?} row segment top at dpi {dpi}");
                    assert_eq!(r.height(), m.button, "{set:?} row segment height at dpi {dpi}");
                }

                let col = column_for(*set, dpi);
                let t = col.tray_rect;
                let col_inner = t.width() - 2 * m.pad;
                assert_eq!(col.emblem_rect.top(), t.top() + m.pad, "{set:?} column emblem at dpi {dpi}");
                assert_eq!(col.emblem_rect.height(), m.emblem);
                assert_eq!(col.area_rect.top() - col.emblem_rect.bottom(), m.gap);
                let b = col.buttons();
                assert_eq!(b[0].top() - col.area_rect.bottom(), m.gap);
                for pair in b.windows(2) {
                    assert_eq!(pair[1].top() - pair[0].bottom(), m.gap, "{set:?} column gap at dpi {dpi}");
                }
                assert_eq!(b.last().unwrap().bottom() + m.pad, t.bottom());
                for r in [col.emblem_rect, col.area_rect]
                    .iter()
                    .chain(b)
                {
                    assert_eq!(r.left(), t.left() + m.pad, "{set:?} column segment left at dpi {dpi}");
                    assert_eq!(r.width(), col_inner, "{set:?} column segment width at dpi {dpi}");
                }
                for r in [col.area_rect].iter().chain(b) {
                    assert_eq!(r.height(), m.button, "{set:?} column segment height at dpi {dpi}");
                }
            }
        }
    }

    /// A row button is `pad | icon | gap | text | pad` long, never shorter
    /// than the 40 px minimum (which the chrome alone already exceeds).
    #[test]
    fn button_length_is_pad_icon_gap_text_pad() {
        let l = layout_for(PanelButtonSet::Normal, 1.0);
        let widths: Vec<i32> = l
            .buttons()
            .iter()
            .map(|r| r.width())
            .collect();
        assert_eq!(widths, [85, 71, 78, 78, 85, 71, 71, 64, 78, 71]);

        for dpi in DPIS {
            let m = PanelMetrics::for_dpi(dpi);
            for set in PanelButtonSet::ALL {
                let l = layout_for(*set, dpi);
                for (r, def) in l.buttons().iter().zip(l.defs()) {
                    assert_eq!(r.width(), button_len_for_chars(&m, def.label.len()), "{} at dpi {dpi}", def.label);
                    assert!(r.width() >= m.button, "{} at dpi {dpi} is under the minimum", def.label);
                }
            }
            // The 40 px minimum can only bind below the chrome: even an
            // empty label is `pad + icon + gap + pad` = 42 at 100 %, so with
            // these tokens no real label is ever clamped up to it.
            let chrome = m.button_pad_h * 2 + m.icon + m.icon_label_gap;
            assert_eq!(button_len_for_chars(&m, 0), chrome.max(m.button), "empty label at dpi {dpi}");
            assert!(button_len_for_chars(&m, 0) >= m.button, "minimum at dpi {dpi}");
            assert_eq!(
                button_len_for_chars(&m, 1),
                chrome + text_width_px(1, m.font_px),
                "one glyph at dpi {dpi}"
            );
        }
    }

    /// The readout segment is 2 × 10 plus the width of its glyphs, and the
    /// glyphs are counted from the UNCLIPPED selection.
    #[test]
    fn area_segment_width_tracks_the_digit_count() {
        assert_eq!(
            layout_for(PanelButtonSet::Normal, 1.0)
                .area_rect
                .width(),
            84
        );

        let full = compute_layout(rect(MON), rect(MON), 1.0, PanelButtonSet::Normal, PanelFeatures::ALL).unwrap();
        assert_eq!(full.area_rect.width(), 98, "\"1920 × 1080\" is 11 glyphs");

        let huge = compute_layout(
            rect(MON),
            rect((-9000, -9000, 10000, 10040)),
            1.0,
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
        )
        .unwrap();
        assert_eq!(huge.area_rect.width(), 2 * 10 + text_width_px(13, 12));
        assert_eq!(
            huge.area_rect.width(),
            112,
            "\"10000 × 10040\" is 13 glyphs, not the clipped 1000 × 1040"
        );
    }

    /// In a column every segment is the column's full inner width and
    /// shares one left edge.
    #[test]
    fn column_buttons_are_full_width_and_share_the_column_x() {
        for set in PanelButtonSet::ALL {
            let l = column_for(*set, 1.0);
            let w = l.area_rect.width();
            assert_eq!(l.emblem_rect.width(), w, "{set:?} emblem");
            assert_eq!(l.emblem_rect.left(), l.area_rect.left(), "{set:?} emblem");
            for (i, r) in l.buttons().iter().enumerate() {
                assert_eq!(r.width(), w, "{set:?} button {i} width");
                assert_eq!(r.left(), l.area_rect.left(), "{set:?} button {i} left");
            }
        }
    }

    /// The column is sized from the widest label in the UNION of both
    /// tables, so its width is the same (93 at 100 %) whichever set is
    /// showing and whichever buttons are switched off.
    #[test]
    fn column_width_is_set_and_feature_independent() {
        let all_off = PanelFeatures {
            upload: false,
            share: false,
            scroll_capture: false,
            video: false,
            ocr: false,
        };
        let cases = [
            (PanelButtonSet::Normal, PanelFeatures::ALL),
            (PanelButtonSet::Ocr, PanelFeatures::ALL),
            (PanelButtonSet::Normal, all_off),
        ];
        for (set, features) in cases {
            let l = compute_layout(rect(MON), column_sel(), 1.0, set, features).unwrap();
            assert_eq!(l.orientation, PanelOrientation::Vertical, "{set:?}");
            assert_eq!(l.tray_rect.width(), 93, "{set:?} with {features:?}");
        }
    }

    /// A readout longer than the widest label widens the column rather
    /// than wrapping: "10000 × 10040" needs 112 px, so every segment and
    /// the tray grow to hold it on one line.
    #[test]
    fn column_widens_to_fit_a_long_area_readout() {
        // Clips to (0, 0, 1000, 1040): 38 px below is not enough for a row,
        // 918 px to the right is plenty for a column.
        let l = compute_layout(
            rect(MON),
            rect((-9000, -9000, 10000, 10040)),
            1.0,
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
        )
        .unwrap();
        assert_eq!(l.orientation, PanelOrientation::Vertical);
        assert_eq!(l.area_rect.width(), 112);
        for (i, r) in l.buttons().iter().enumerate() {
            assert_eq!(r.width(), 112, "button {i}");
        }
        assert_eq!(l.tray_rect.width(), 120);
    }

    /// A column beside a short selection near the top of a short monitor
    /// would start above the monitor; the clamp slides it down so the
    /// whole tray is visible.
    #[test]
    fn tall_column_is_clamped_to_the_monitor_top() {
        let mon = rect((0, 0, 1920, 560));
        let l = compute_layout(mon, rect((100, 300, 400, 220)), 1.0, PanelButtonSet::Normal, PanelFeatures::ALL).unwrap();
        assert_eq!(l.orientation, PanelOrientation::Vertical);
        assert_eq!(l.tray_rect.height(), 532);
        // Unclamped it would be 520 - 532 = -12.
        assert_eq!(l.tray_rect.top(), 0);
        assert_eq!(l.tray_rect.bottom(), 532);
        assert!(l.tray_rect.bottom() <= mon.bottom());
        assert_eq!(l.tray_rect.left(), 515);
    }

    /// A column that cannot fit the monitor's height is not a candidate
    /// even though there is room beside the selection: the placement
    /// falls through to the inside row, which fits, rather than pinning a
    /// 532 px column onto a 420 px display.
    #[test]
    fn column_that_cannot_fit_the_monitor_height_falls_back_to_the_inside_row() {
        let mon = rect((0, 0, 1920, 420));
        let sel = rect((100, 100, 400, 300));
        let l = compute_layout(mon, sel, 1.0, PanelButtonSet::Normal, PanelFeatures::ALL).unwrap();
        assert_eq!(l.orientation, PanelOrientation::Horizontal);
        // Inside the selection: 400 - 48 - 2 × 15; centred on x = 300 and
        // then slid right onto the monitor.
        assert_eq!(l.tray_rect, rect((0, 322, 928, 48)));
        assert!(mon.contains_rect(&l.tray_rect));
    }

    /// `longest_strip_lens` is the full capture strip with every feature
    /// on, whatever the readout, and its row length matches the row the
    /// layout actually builds for Normal/ALL.
    #[test]
    fn longest_strip_is_the_full_capture_row_at_every_dpi() {
        for dpi in DPIS {
            let m = PanelMetrics::for_dpi(dpi);
            let area_len = m.area_pad_h * 2 + text_width_px(area_text_chars(rect(SEL)), m.font_px);
            let (row, col) = longest_strip_lens(&m, area_len);
            assert_eq!(
                row,
                expected_row_len(&m, PanelButtonSet::Normal, PanelFeatures::ALL, rect(SEL)),
                "row at dpi {dpi}"
            );
            let n = MAX_PANEL_BUTTONS as i32;
            assert_eq!(
                col,
                m.pad + m.emblem + m.gap + m.button + m.gap + m.button * n + m.gap * (n - 1) + m.pad,
                "column at dpi {dpi}"
            );
            assert!(
                row > expected_row_len(&m, PanelButtonSet::Ocr, PanelFeatures::ALL, rect(SEL)),
                "dpi {dpi}"
            );
        }
        // The concrete sizes the fit test compares against small displays.
        let at = |dpi: f32| {
            let m = PanelMetrics::for_dpi(dpi);
            longest_strip_lens(&m, m.area_pad_h * 2 + text_width_px(9, m.font_px))
        };
        assert_eq!(at(1.0), (928, 532));
        assert_eq!(at(1.25), (1160, 665));
        assert_eq!(at(1.5), (1384, 798));
    }

    /// A monitor with room under the selection but narrower than the
    /// labelled row gets the column beside the selection instead, so
    /// every button stays on the monitor. The decision follows the
    /// longest strip in the union: the OCR row (539 px) would fit an
    /// 800 px display on its own, but it takes the column too, so a set
    /// swap cannot flip the orientation.
    #[test]
    fn row_wider_than_the_monitor_falls_back_to_a_column() {
        let mon = rect((0, 0, 800, 600));
        let sel = rect((100, 100, 400, 200));
        let m = PanelMetrics::for_dpi(1.0);
        assert!(expected_row_len(&m, PanelButtonSet::Ocr, PanelFeatures::ALL, sel) <= mon.width());
        assert!(expected_row_len(&m, PanelButtonSet::Normal, PanelFeatures::ALL, sel) > mon.width());

        let normal = compute_layout(mon, sel, 1.0, PanelButtonSet::Normal, PanelFeatures::ALL).unwrap();
        let ocr = compute_layout(mon, sel, 1.0, PanelButtonSet::Ocr, PanelFeatures::ALL).unwrap();
        for (name, l) in [("Normal", &normal), ("Ocr", &ocr)] {
            assert_eq!(l.orientation, PanelOrientation::Vertical, "{name}");
            assert!(l.tray_rect.left() > sel.right(), "{name} is not right of the selection");
            assert_eq!(l.tray_rect.left(), 515, "{name}");
            assert!(mon.contains_rect(&l.tray_rect), "{name} tray {:?} is off the monitor", l.tray_rect);
            for (i, b) in l.buttons().iter().enumerate() {
                let (cx, cy) = centre(*b);
                assert_eq!(l.hit_test(cx, cy), Some(i), "{name} button {i}");
            }
        }
        assert_eq!(
            (normal.tray_rect.left(), normal.tray_rect.width()),
            (ocr.tray_rect.left(), ocr.tray_rect.width())
        );

        // The review's portrait case: 1080 × 1920 at 125 % (row 1160 px).
        let portrait = rect((0, 0, 1080, 1920));
        let sel = rect((100, 100, 400, 400));
        let m = PanelMetrics::for_dpi(1.25);
        assert!(expected_row_len(&m, PanelButtonSet::Normal, PanelFeatures::ALL, sel) > portrait.width());
        let l = compute_layout(portrait, sel, 1.25, PanelButtonSet::Normal, PanelFeatures::ALL).unwrap();
        assert_eq!(l.orientation, PanelOrientation::Vertical);
        assert!(
            portrait.contains_rect(&l.tray_rect),
            "tray {:?} is off the portrait monitor",
            l.tray_rect
        );
    }

    /// When neither the row nor the column fits the monitor along its
    /// axis the thickness alone picks the side (as it always did) and the
    /// clamp lets the strip's HEAD overflow, not its tail: the emblem and
    /// readout (dead anyway) go off the monitor while every button stays
    /// on it and hittable. Whatever does lie off the monitor is dead to
    /// `hit_test` and `contains` even though it is inside the tray, so a
    /// click at those coordinates on a neighbouring display fires nothing.
    #[test]
    fn nothing_fits_keeps_the_button_end_on_the_monitor_and_kills_the_rest() {
        struct Case {
            name: &'static str,
            mon: (i32, i32, i32, i32),
            sel: (i32, i32, i32, i32),
            dpi: f32,
            orientation: PanelOrientation,
        }
        let cases = [
            // 800 × 300: the row (928) overflows by 128, less than the
            // emblem + readout head (132), so every button is whole.
            Case {
                name: "row on 800x300",
                mon: (0, 0, 800, 300),
                sel: (100, 50, 400, 100),
                dpi: 1.0,
                orientation: PanelOrientation::Horizontal,
            },
            // 800 × 420: the column (532) overflows by 112, more than its
            // 92 px head, so Upload is partly off the top; Exit is whole.
            Case {
                name: "column on 800x420",
                mon: (0, 0, 800, 420),
                sel: (100, 100, 400, 300),
                dpi: 1.0,
                orientation: PanelOrientation::Vertical,
            },
            // The review's 1366 × 768 at 150 %: row 1384, column 798.
            Case {
                name: "row on 1366x768 at 150 %",
                mon: (0, 0, 1366, 768),
                sel: (300, 200, 600, 300),
                dpi: 1.5,
                orientation: PanelOrientation::Horizontal,
            },
        ];
        for c in cases {
            let mon = rect(c.mon);
            let l = compute_layout(mon, rect(c.sel), c.dpi, PanelButtonSet::Normal, PanelFeatures::ALL).unwrap();
            let m = l.metrics;
            let t = l.tray_rect;
            assert_eq!(l.orientation, c.orientation, "{}", c.name);

            // The tail edge sits on the monitor edge and the head overflows.
            let last = *l.buttons().last().unwrap();
            match c.orientation {
                PanelOrientation::Horizontal => {
                    assert!(t.width() > mon.width(), "{}: expected an overflowing row", c.name);
                    assert_eq!(t.right(), mon.right(), "{}", c.name);
                    assert_eq!(last.right() + m.pad, mon.right(), "{}", c.name);
                    assert!(t.left() < mon.left(), "{}", c.name);
                    assert!(l.emblem_rect.left() < mon.left(), "{}: the emblem should be what overflows", c.name);
                    assert!(t.top() >= mon.top() && t.bottom() <= mon.bottom(), "{}", c.name);
                }
                PanelOrientation::Vertical => {
                    assert!(t.height() > mon.height(), "{}: expected an overflowing column", c.name);
                    assert_eq!(t.bottom(), mon.bottom(), "{}", c.name);
                    assert_eq!(last.bottom() + m.pad, mon.bottom(), "{}", c.name);
                    assert!(t.top() < mon.top(), "{}", c.name);
                    assert!(l.emblem_rect.top() < mon.top(), "{}: the emblem should be what overflows", c.name);
                    assert!(t.left() >= mon.left() && t.right() <= mon.right(), "{}", c.name);
                }
            }

            // Every button's on-monitor part is hittable and its
            // off-monitor part (if any) is not.
            for (i, b) in l.buttons().iter().enumerate() {
                let (cx, cy) = centre(*b);
                let (vx, vy) = (cx.max(mon.left() as f32), cy.max(mon.top() as f32));
                assert_eq!(l.hit_test(vx, vy), Some(i), "{}: button {i} visible part", c.name);
                assert!(l.contains(vx, vy), "{}: button {i}", c.name);
                if b.left() < mon.left() {
                    assert_eq!(l.hit_test(b.left() as f32, cy), None, "{}: button {i} off-monitor part", c.name);
                }
                if b.top() < mon.top() {
                    assert_eq!(l.hit_test(cx, b.top() as f32), None, "{}: button {i} off-monitor part", c.name);
                }
            }
            // The last button is whole in every case.
            assert!(mon.contains_rect(&last), "{}: Exit {:?} is not whole", c.name, last);

            // Just off the monitor is dead, tray or not.
            let (off_x, off_y) = match c.orientation {
                PanelOrientation::Horizontal => ((mon.left() - 1) as f32, centre(t).1),
                PanelOrientation::Vertical => (centre(t).0, (mon.top() - 1) as f32),
            };
            assert!(
                rect_holds(t, off_x as i32, off_y as i32),
                "{}: probe should be inside the tray",
                c.name
            );
            assert_eq!(l.hit_test(off_x, off_y), None, "{}", c.name);
            assert!(!l.contains(off_x, off_y), "{}", c.name);
            // ...and the first on-monitor pixel of the tray is not.
            let (on_x, on_y) = match c.orientation {
                PanelOrientation::Horizontal => (mon.left() as f32, off_y),
                PanelOrientation::Vertical => (off_x, mon.top() as f32),
            };
            assert!(l.contains(on_x, on_y), "{}", c.name);
        }
    }

    /// A selection close to the bottom edge still takes the row branch
    /// when the tray fits, and the row is pushed up so its chassis (not
    /// just its buttons) stays on the monitor, eating into the 15 px gap.
    #[test]
    fn pushed_up_row_keeps_the_chassis_on_the_monitor() {
        let sel = rect((500, 300, 600, 720));
        let l = compute_layout(rect(MON), sel, 1.0, PanelButtonSet::Normal, PanelFeatures::ALL).unwrap();
        assert_eq!(l.orientation, PanelOrientation::Horizontal);
        assert_eq!(l.tray_rect.top(), 1032);
        assert_eq!(l.tray_rect.bottom(), MON.1 + MON.3);
        let gap = l.tray_rect.top() - sel.bottom();
        assert_eq!(gap, 12);
        assert!(gap < 15);
    }

    /// A full-screen selection has no room on any side, so the row sits
    /// inside the selection, 2 × 15 px above its bottom, and is contained
    /// on both axes.
    #[test]
    fn inside_fallback_row_stays_on_the_monitor() {
        let mon = rect(MON);
        let l = compute_layout(mon, mon, 1.0, PanelButtonSet::Normal, PanelFeatures::ALL).unwrap();
        assert_eq!(l.orientation, PanelOrientation::Horizontal);
        assert_eq!(l.tray_rect.top(), 1080 - 48 - 30);
        assert_eq!(l.tray_rect.top(), 1002);
        assert!(l.tray_rect.left() >= mon.left() && l.tray_rect.right() <= mon.right());
        assert!(l.tray_rect.top() >= mon.top() && l.tray_rect.bottom() <= mon.bottom());
    }

    /// The emblem, the readout, the padding and the gaps are dead to
    /// `hit_test` but inside `contains`; `contains` is half-open on the
    /// tray rect exactly like `hit_test` is on a button.
    #[test]
    fn emblem_and_area_are_dead_but_contained() {
        for l in [layout_for(PanelButtonSet::Normal, 1.0), column_for(PanelButtonSet::Normal, 1.0)] {
            let t = l.tray_rect;
            let b = l.buttons();
            let gap_pt = match l.orientation {
                PanelOrientation::Horizontal => (b[0].right() as f32, centre(b[0]).1),
                PanelOrientation::Vertical => (centre(b[0]).0, b[0].bottom() as f32),
            };
            let probes = [
                centre(l.emblem_rect),
                centre(l.area_rect),
                ((t.left() + 1) as f32, (t.top() + 1) as f32),
                gap_pt,
            ];
            for (i, (x, y)) in probes.iter().enumerate() {
                assert_eq!(l.hit_test(*x, *y), None, "{:?} probe {i} hit a button", l.orientation);
                assert!(l.contains(*x, *y), "{:?} probe {i} is not on the tray", l.orientation);
            }

            let (left, top, right) = (t.left() as f32, t.top() as f32, t.right() as f32);
            assert!(l.contains(left, top));
            assert!(!l.contains(left - 1.0, top));
            assert!(!l.contains(right, top));
            assert!(l.contains(right - 0.5, top));
        }
    }

    /// Hit-testing agrees with the drawn rects to the pixel: the top-left
    /// and bottom-right pixels of every button belong to it, its right and
    /// bottom edges do not, and the pixel just before its left edge is the
    /// gap (or the padding) — in both orientations, both sets, four DPIs.
    #[test]
    fn hit_test_agrees_with_every_drawn_rect_edge() {
        for dpi in DPIS {
            for set in PanelButtonSet::ALL {
                for l in [layout_for(*set, dpi), column_for(*set, dpi)] {
                    for (i, r) in l.buttons().iter().enumerate() {
                        let (lf, tp, rt, bt) = (r.left() as f32, r.top() as f32, r.right() as f32, r.bottom() as f32);
                        let ctx = format!("{set:?} {:?} button {i} at dpi {dpi}", l.orientation);
                        assert_eq!(l.hit_test(lf, tp), Some(i), "{ctx}: top-left");
                        assert_eq!(l.hit_test(rt - 1.0, bt - 1.0), Some(i), "{ctx}: bottom-right");
                        assert_ne!(l.hit_test(rt, tp), Some(i), "{ctx}: right edge");
                        assert_ne!(l.hit_test(lf, bt), Some(i), "{ctx}: bottom edge");
                        assert_eq!(l.hit_test(lf + 0.999, tp + 0.999), Some(i), "{ctx}: sub-pixel inside");
                        assert_eq!(l.hit_test(lf - 0.001, tp), None, "{ctx}: sub-pixel before the left edge");
                    }
                }
            }
        }
    }

    /// The below/right/left predicates compare the tray THICKNESS (48 for
    /// a row, 93 for a column at 100 %) against the free space, one pixel
    /// either side of the threshold.
    #[test]
    fn orientation_predicates_use_tray_thickness() {
        let mon = rect(MON);
        let layout = |sel| compute_layout(mon, rect(sel), 1.0, PanelButtonSet::Normal, PanelFeatures::ALL).unwrap();

        // 1080 - 1031 - 2 = 47 < 48: no room below, so a column.
        assert_eq!(layout((100, 100, 400, 931)).orientation, PanelOrientation::Vertical);
        // 1080 - 1030 - 2 = 48: exactly enough for a row.
        assert_eq!(layout((100, 100, 400, 930)).orientation, PanelOrientation::Horizontal);

        // 1920 - 1826 - 2 = 92 < 93: no room on the right, so the column
        // goes to the left of the selection.
        let sel = (926, 100, 900, 960);
        let l = layout(sel);
        assert_eq!(l.orientation, PanelOrientation::Vertical);
        assert!(l.tray_rect.right() < sel.0, "expected the column left of the selection");
        // 1920 - 1825 - 2 = 93: exactly enough on the right.
        let sel = (925, 100, 900, 960);
        let l = layout(sel);
        assert_eq!(l.orientation, PanelOrientation::Vertical);
        assert!(l.tray_rect.left() > sel.0 + sel.2, "expected the column right of the selection");
    }

    /// Because the column width comes from the union of both tables, the
    /// orientation decision and the tray's position are identical for
    /// every set and every feature combination.
    #[test]
    fn orientation_predicates_ignore_the_set() {
        let sel = rect((926, 100, 900, 960));
        for set in PanelButtonSet::ALL {
            for features in all_feature_combos() {
                let l = compute_layout(rect(MON), sel, 1.0, *set, features).unwrap();
                assert_eq!(
                    (l.orientation, l.tray_rect.left(), l.tray_rect.width()),
                    (PanelOrientation::Vertical, 818, 93),
                    "{set:?} with {features:?}"
                );
            }
        }
    }
}
