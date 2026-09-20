//! The tray as one egui `Area`: measure the strip, place it beside the
//! selection, then lay the emblem, the readout and the button groups out
//! along the chosen axis. Inside a group the buttons are flush; between
//! groups (and before the first) there is one `GAP`.
//!
//! The `key` style has no labels, so — as the C# strips do — a button
//! hovered for 350 ms grows a tooltip chip naming it, hung off the strip's
//! far edge (below a row, right of a column) and flipped to the other
//! side when the monitor has no room there. Once a chip is up, the next
//! button's chip follows the pointer without the wait, across the gap
//! between groups too, until the pointer has been off every button for
//! the same 350 ms.
//!
//! The strip's size is analytic — computed from the same numbers the
//! widgets are laid out with — because placement needs it before the Area
//! is positioned, and because each strip must be centred with its own
//! width for the set-swap guard's premise to hold. A test pins the
//! analytic size against what egui actually lays out at every DPI.

use egui::{pos2, vec2, Rect, Vec2};

use super::assets;
use super::model::{Body, ButtonDef, ButtonStyle, GroupTone, PanelButtonSet, PanelFeatures, Readout};
use super::place::{self, Axis, Fit, Footprint, Near};
use super::theme::{self, tokens};
use super::widgets;
use crate::ui::egui_host::{PanelInputs, PanelOutcome};
use crate::ui::shared::UiMonitor;
use clowd_rust_core::geometry::RectExt;

/// The Area id. One tray per context, so one id per context.
pub const PANEL_ID: &str = "clowd-tray";
/// The tooltip chip's Area id, and the key its hover clock is kept under.
pub const TIP_ID: &str = "clowd-tray-tip";

/// The tooltip's clock, in egui input time, kept in the context's temp
/// data so it survives between passes: which button the pointer is on and
/// since when, and when a chip was last on screen. The last-shown mark is
/// what makes the chip follow the pointer between buttons without a fresh
/// wait — and outlives the pointer leaving the strip by the grace period.
#[derive(Clone, Copy, Default)]
struct TipClock {
    button: Option<egui::Id>,
    since: f64,
    shown_at: Option<f64>,
}

/// The tooltip chip's rect for a button at `button` inside a strip at
/// `tray`, in points, kept inside `screen`. Below a row / right of a
/// column, flipped to the near side when the far side has no room; then
/// slid along the strip so it stays on the monitor.
fn tip_rect(axis: Axis, tray: Rect, button: Rect, chip: Vec2, screen: Rect) -> Rect {
    let rect = match axis {
        Axis::Row => {
            let top = tray.bottom() + tokens::TIP_GAP_BOTTOM;
            let top = if top + chip.y > screen.bottom() {
                tray.top() - tokens::TIP_GAP_BOTTOM - chip.y
            } else {
                top
            };
            Rect::from_min_size(pos2(button.center().x - chip.x / 2.0, top), chip)
        }
        Axis::Column => {
            let left = tray.right() + tokens::TIP_GAP_RIGHT;
            let left = if left + chip.x > screen.right() {
                tray.left() - tokens::TIP_GAP_RIGHT - chip.x
            } else {
                left
            };
            Rect::from_min_size(pos2(left, button.center().y - chip.y / 2.0), chip)
        }
    };
    let dx = (screen.left() - rect.left()).max(0.0) - (rect.right() - screen.right()).max(0.0);
    let dy = (screen.top() - rect.top()).max(0.0) - (rect.bottom() - screen.bottom()).max(0.0);
    rect.translate(vec2(dx, dy))
}

/// Run the hover clock for this pass and paint the chip once it has
/// waited long enough. `hovered` is the button under the pointer (its id,
/// rect and def), `None` when the pointer is on no button.
///
/// A new button restarts the wait unless a chip was on screen within the
/// last `TIP_DELAY_SECS`: then it shows at once, so a pointer sliding
/// along the strip — through the gap between two groups included — keeps
/// its chip. Only a pause off every button as long as the wait itself
/// brings the wait back.
fn show_tip(ctx: &egui::Context, axis: Axis, tray: Rect, hovered: Option<(egui::Id, Rect, &'static ButtonDef)>, visible: bool) {
    let key = egui::Id::new(TIP_ID);
    let now = ctx.input(|i| i.time);
    let mut clock = ctx.data(|d| {
        d.get_temp::<TipClock>(key)
            .unwrap_or_default()
    });
    let Some((button, rect, def)) = hovered else {
        clock.button = None;
        ctx.data_mut(|d| d.insert_temp(key, clock));
        return;
    };
    if clock.button != Some(button) {
        clock.button = Some(button);
        let in_grace = clock
            .shown_at
            .is_some_and(|t| now - t <= tokens::TIP_DELAY_SECS);
        clock.since = if in_grace { now - tokens::TIP_DELAY_SECS } else { now };
    }
    let waited = now - clock.since;
    if waited < tokens::TIP_DELAY_SECS {
        ctx.data_mut(|d| d.insert_temp(key, clock));
        ctx.request_repaint_after(std::time::Duration::from_secs_f64(tokens::TIP_DELAY_SECS - waited));
        return;
    }
    clock.shown_at = Some(now);
    ctx.data_mut(|d| d.insert_temp(key, clock));
    let text = ctx.fonts_mut(|f| f.layout_job(widgets::tip_job(def)));
    // Whole points, so the chip's edges land where egui pins the Area.
    let chip = (text.size() + 2.0 * vec2(tokens::TIP_PAD_H, tokens::TIP_PAD_V)).ceil();
    let chip_rect = tip_rect(axis, tray, rect, chip, ctx.viewport_rect());
    egui::Area::new(key)
        .order(egui::Order::Tooltip)
        .fixed_pos(chip_rect.min)
        .constrain(false)
        .fade_in(false)
        .interactable(false)
        .show(ctx, |ui| {
            if !visible {
                ui.set_opacity(0.0);
            }
            let (r, _) = ui.allocate_exact_size(chip, egui::Sense::hover());
            widgets::tip(ui.painter(), r, def);
        });
}

/// One visible button as measured: table index, def, and its length
/// along a row.
pub type MeasuredButton = (usize, &'static ButtonDef, f32);

/// The strip's body as measured: which one it is, and — for the
/// instruction — the wrap width it was laid out at, so the pass that
/// paints it uses the width the pass that sized the tray measured.
#[derive(Clone, Copy, PartialEq)]
pub enum MeasuredBody {
    Readout(Readout),
    Hint { text: &'static str, wrap: f32 },
}

/// The narrowest wrap width, in points, that fits `text` in
/// `tokens::HINT_LINES` lines, and the line count it achieved.
///
/// Narrowest is also best-balanced: at the width where the last line is
/// about to spill into a third, the two lines are as near equal as the
/// break points allow. The search starts from the width a perfectly even
/// split would need (half the unwrapped run) and steps up, so it costs a
/// handful of layouts of one short paragraph, once per measured pass.
///
/// A text that will not fit in `HINT_LINES` lines at any width up to its
/// full run returns that full width: one line, never a clipped box.
fn hint_wrap(ctx: &egui::Context, text: &'static str) -> (f32, usize) {
    let full = ctx
        .fonts_mut(|f| f.layout_job(widgets::hint_job(text, f32::INFINITY)))
        .size()
        .x;
    let lines = |wrap: f32| {
        ctx.fonts_mut(|f| f.layout_job(widgets::hint_job(text, wrap)))
            .rows
            .len()
    };
    let start = (full / tokens::HINT_LINES as f32).floor();
    let mut wrap = start;
    while wrap < full {
        let rows = lines(wrap);
        if rows <= tokens::HINT_LINES {
            return (wrap, rows);
        }
        wrap += tokens::HINT_WRAP_STEP;
    }
    (full, lines(full))
}

/// Every number the strip is laid out with, in points, measured before the
/// Area is positioned.
pub struct StripMetrics {
    /// Item thickness across the strip: the button's, and the readout's
    /// square side.
    pub thick: f32,
    /// What sits between the emblem and the buttons, measured: the
    /// readout, or the instruction with the wrap width it was measured
    /// at.
    pub body: MeasuredBody,
    /// The body slot's length along a row.
    pub body_along: f32,
    /// The visible groups in strip order, each with its buttons: table
    /// index, def, and the button's length along a row.
    pub groups: Vec<(GroupTone, Vec<MeasuredButton>)>,
}

impl StripMetrics {
    /// Measure one set under one feature switch set. Only valid inside a
    /// run: `Context::fonts_mut` panics before the first pass.
    pub fn measure(ctx: &egui::Context, set: PanelButtonSet, features: PanelFeatures, style: ButtonStyle, readout: Readout) -> Self {
        let body = match set.body() {
            Body::Readout => MeasuredBody::Readout(readout),
            Body::Hint(text) => {
                let (wrap, _) = hint_wrap(ctx, text);
                MeasuredBody::Hint {
                    text,
                    wrap,
                }
            }
        };
        let thick = match style {
            ButtonStyle::KeyHint => tokens::KEY_TILE,
            ButtonStyle::Below => tokens::BELOW_HEIGHT,
        };
        let groups = set
            .visible_groups(features)
            .into_iter()
            .map(|(tone, members)| {
                let buttons = members
                    .into_iter()
                    .map(|(i, def)| {
                        let along = match style {
                            ButtonStyle::KeyHint => tokens::KEY_TILE,
                            ButtonStyle::Below => {
                                let label = ctx.fonts_mut(|f| f.layout_job(widgets::underlined_label(def)));
                                tokens::BUTTON_MIN_LENGTH.max(label.size().x + 2.0 * tokens::BELOW_PAD_H)
                            }
                        };
                        (i, def, along)
                    })
                    .collect();
                (tone, buttons)
            })
            .collect();
        let body_along = match body {
            MeasuredBody::Readout(readout) => {
                let galley = ctx.fonts_mut(|f| f.layout_job(widgets::readout_job(readout)));
                thick.max(galley.size().x + 2.0 * tokens::READOUT_PAD_H)
            }
            MeasuredBody::Hint {
                text,
                wrap,
            } => {
                let galley = ctx.fonts_mut(|f| f.layout_job(widgets::hint_job(text, wrap)));
                thick.max(galley.size().x + 2.0 * tokens::HINT_PAD_H)
            }
        };
        Self {
            thick,
            body,
            body_along,
            groups,
        }
    }

    /// Every visible button, in strip order, with its length along a row.
    pub fn buttons(&self) -> impl Iterator<Item = &MeasuredButton> {
        self.groups
            .iter()
            .flat_map(|(_, b)| b.iter())
    }

    /// One group's length along `axis`: its buttons flush, no gaps.
    fn group_along(&self, axis: Axis, buttons: &[MeasuredButton]) -> f32 {
        match axis {
            Axis::Row => buttons
                .iter()
                .map(|(_, _, along)| *along)
                .sum(),
            Axis::Column => buttons.len() as f32 * self.thick,
        }
    }

    /// The strip's content size (the tray's padding not included) for one
    /// axis. A column is `col_inner` wide throughout, so a set swap or a
    /// feature change can never change its width.
    pub fn content_size(&self, axis: Axis, col_inner: f32) -> Vec2 {
        // One gap before each group, so `n` groups cost `n` gaps: the
        // first follows the readout.
        let groups_along: f32 = self
            .groups
            .iter()
            .map(|(_, buttons)| tokens::GAP + self.group_along(axis, buttons))
            .sum();
        let body_along = match axis {
            Axis::Row => self.body_along,
            Axis::Column => self.thick,
        };
        let along = tokens::EMBLEM_SLOT + tokens::EMBLEM_GAP + body_along + groups_along;
        match axis {
            Axis::Row => vec2(along, self.thick),
            Axis::Column => vec2(col_inner, along),
        }
    }
}

/// The strip box in physical pixels: the content plus the tray's padding
/// on either side, rounded once per axis.
fn outer_px(content: Vec2, ppp: f32) -> (i32, i32) {
    let pad = 2.0 * tokens::PAD;
    (((content.x + pad) * ppp).round() as i32, ((content.y + pad) * ppp).round() as i32)
}

/// The readout each set would show, given the current inputs: the size
/// for the capture strip, the word count for the OCR strip. Before any
/// text is lifted the count is unknown, and zero stands in: the "words"
/// caption is the wide line, so the slot's width does not depend on the
/// number.
fn readout_for(set: PanelButtonSet, p: &PanelInputs) -> Readout {
    match (set, p.readout) {
        // The scroll-pick strip shows an instruction, not a readout; the
        // value is carried along unused so `measure` stays one signature.
        (PanelButtonSet::ScrollPick, r) => r,
        (PanelButtonSet::Normal, _) => Readout::Size {
            width: p.selection.width(),
            height: p.selection.height(),
        },
        (PanelButtonSet::Ocr, words @ Readout::Words(_)) => words,
        (
            PanelButtonSet::Ocr,
            Readout::Size {
                ..
            },
        ) => Readout::Words(0),
    }
}

/// The longest box either set can become in each orientation with every
/// feature on, plus the column's inner width — so neither the side choice
/// nor the column thickness depends on which set is up or which switches
/// are on.
fn union_fit(ctx: &egui::Context, p: &PanelInputs, ppp: f32) -> (Fit, f32) {
    let metrics: Vec<StripMetrics> = PanelButtonSet::UNION
        .iter()
        .map(|&set| StripMetrics::measure(ctx, set, PanelFeatures::ALL, p.style, readout_for(set, p)))
        .collect();
    let mut col_inner = tokens::EMBLEM_SLOT;
    for m in &metrics {
        col_inner = col_inner.max(m.body_along);
        for (_, _, along) in m.buttons() {
            col_inner = col_inner.max(*along);
        }
    }
    let mut fit = Fit {
        row: Footprint {
            len: 0,
            thick: 0,
        },
        col: Footprint {
            len: 0,
            thick: 0,
        },
    };
    for m in &metrics {
        let (w, h) = outer_px(m.content_size(Axis::Row, col_inner), ppp);
        fit.row.len = fit.row.len.max(w);
        fit.row.thick = fit.row.thick.max(h);
        let (w, h) = outer_px(m.content_size(Axis::Column, col_inner), ppp);
        fit.col.len = fit.col.len.max(h);
        fit.col.thick = fit.col.thick.max(w);
    }
    (fit, col_inner)
}

/// Measure the strip that is up and place its box, in physical pixels —
/// everything `show` needs before the Area exists, and the whole of what
/// the placement tests check.
fn measure_and_place(
    ctx: &egui::Context,
    p: &PanelInputs,
    monitor: UiMonitor,
) -> (StripMetrics, place::Side, clowd_rust_core::geometry::ScreenRect) {
    let ppp = monitor.dpi_scale.max(0.1);
    let (mut fit, col_inner) = union_fit(ctx, p, ppp);
    let m = StripMetrics::measure(ctx, p.set, p.features, p.style, p.readout);
    // A set outside the union (the scroll-picker) is not in `fit`, and the
    // side cascade's "does the strip fit along the monitor" test has to be
    // asked about the strip that is actually up — otherwise a two-line
    // instruction wider than the monitor would still pass as `Below`.
    if !PanelButtonSet::UNION.contains(&p.set) {
        let (w, h) = outer_px(m.content_size(Axis::Row, col_inner), ppp);
        fit.row.len = fit.row.len.max(w);
        fit.row.thick = fit.row.thick.max(h);
    }
    let (side, rect_px) = place::place(p.anchor, monitor.bounds, fit, Near::at_dpi(ppp), p.set.axis_lock(), |axis| {
        outer_px(m.content_size(axis, col_inner), ppp)
    });
    (m, side, rect_px)
}

/// Build the tray for one monitor and report what the pointer found.
/// Called inside a host's run closure, so text measurement is legal here.
///
/// `visible` is the Q toggle. A hidden tray is still laid out, still
/// hit-tested and still clicked — that is the overlay's long-standing
/// behaviour — it simply paints nothing: at opacity 0 every shape the
/// painter is handed becomes a `Shape::Noop`, while the widget rects and
/// the hover animation carry on as they are.
pub fn show(ctx: &egui::Context, p: &PanelInputs, monitor: UiMonitor, visible: bool) -> PanelOutcome {
    // Ahead of any measurement: a mark that cannot be rasterised is logged
    // here rather than drawn as egui's placeholder glyph.
    assets::preload(ctx);
    let ppp = monitor.dpi_scale.max(0.1);
    // The size readout prints the UNCLIPPED selection, so a rect straddling
    // two monitors keeps showing its true size; only placement uses the
    // clipped anchor.
    let (m, side, rect_px) = measure_and_place(ctx, p, monitor);
    let col_inner = union_fit(ctx, p, ppp).1;
    let axis = side.axis();
    let local = egui::pos2(
        (rect_px.left() - monitor.bounds.left()) as f32 / ppp,
        (rect_px.top() - monitor.bounds.top()) as f32 / ppp,
    );

    let mut out = PanelOutcome::default();
    let mut hovered: Option<(egui::Id, Rect, &'static ButtonDef)> = None;
    let area = egui::Area::new(egui::Id::new(PANEL_ID))
        .order(egui::Order::Foreground)
        .fixed_pos(local)
        // Never `constrain`: egui's symmetric clamp would override the
        // tail-wins rule `place` just applied.
        .constrain(false)
        .fade_in(false)
        .interactable(true)
        // The dead chassis swallows clicks, as the C# strips do.
        .sense(egui::Sense::CLICK);
    let inner = area.show(ctx, |ui| {
        if !visible {
            ui.set_opacity(0.0);
        }
        theme::tray_frame().show(ui, |ui| {
            ui.spacing_mut().item_spacing = Vec2::splat(tokens::GAP);
            let strip = |ui: &mut egui::Ui| {
                let across = match axis {
                    Axis::Row => m.thick,
                    Axis::Column => col_inner,
                };
                let emblem_slot = match axis {
                    Axis::Row => vec2(tokens::EMBLEM_SLOT, across),
                    Axis::Column => vec2(across, tokens::EMBLEM_SLOT),
                };
                // The emblem's slot is padded around a smaller mark, so it
                // needs less of a gap than the flush button boxes. egui
                // spends `item_spacing` AFTER a widget, so this is set
                // before the emblem and restored before the readout.
                ui.spacing_mut().item_spacing = Vec2::splat(tokens::EMBLEM_GAP);
                widgets::emblem(ui, emblem_slot);
                ui.spacing_mut().item_spacing = Vec2::splat(tokens::GAP);
                let body_slot = match axis {
                    Axis::Row => vec2(m.body_along, across),
                    Axis::Column => vec2(across, m.thick),
                };
                match m.body {
                    MeasuredBody::Readout(readout) => {
                        widgets::readout(ui, readout, body_slot);
                    }
                    MeasuredBody::Hint {
                        text,
                        wrap,
                    } => {
                        widgets::hint(ui, text, wrap, body_slot);
                    }
                }
                for (tone, buttons) in &m.groups {
                    let base = theme::group_fill(*tone, p.accent);
                    let veil = theme::hover_veil(*tone);
                    let group = |ui: &mut egui::Ui| {
                        // Flush inside the group; the tray's gap is
                        // between groups only.
                        ui.spacing_mut().item_spacing = Vec2::ZERO;
                        for (table_idx, def, along) in buttons {
                            // The set is part of the id, so hover state
                            // and hit tests can never resolve against the
                            // other strip; the table index keeps a
                            // switched-off button from renumbering its
                            // neighbours.
                            let id = egui::Id::new(("panel", p.set as u8, *table_idx));
                            let min = match axis {
                                Axis::Row => vec2(*along, across),
                                Axis::Column => vec2(across, m.thick),
                            };
                            let button = match p.style {
                                ButtonStyle::KeyHint => widgets::key_hint_button(def, id, min, base, veil),
                                ButtonStyle::Below => widgets::below_button(def, id, min, base, veil),
                            };
                            let r = button.show(ui);
                            out.over_button |= r.contains_pointer();
                            if r.contains_pointer() {
                                hovered = Some((id, r.rect, def));
                            }
                            if r.clicked() {
                                out.clicked = Some(def.command);
                            }
                        }
                    };
                    // The frame's content ui inherits the strip's layout,
                    // so the buttons run along the same axis; a nested
                    // `horizontal`/`vertical` here would pad the cross
                    // axis with its own initial size.
                    theme::group_frame(base).show(ui, group);
                }
            };
            match axis {
                Axis::Row => {
                    ui.horizontal(strip);
                }
                Axis::Column => {
                    ui.vertical(strip);
                }
            }
        });
    });
    // The whole tray: emblem, readout, padding and gaps included.
    out.over_tray = inner.response.contains_pointer();
    // Only the label-less style needs naming; `below` already says it.
    if p.style == ButtonStyle::KeyHint {
        show_tip(ctx, axis, inner.response.rect, hovered, visible);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ui::components::panel::model::FEATURE_COMBINATIONS;
    use clowd_rust_core::geometry::ScreenRect;
    use egui::{Pos2, Rect};

    const DPIS: [f32; 4] = [1.0, 1.25, 1.5, 2.0];

    fn rect(x: i32, y: i32, w: i32, h: i32) -> ScreenRect {
        ScreenRect::from_xy_size(x, y, w, h)
    }

    fn monitor(bounds: ScreenRect, dpi: f32) -> UiMonitor {
        UiMonitor {
            bounds,
            dpi_scale: dpi,
            is_primary: true,
        }
    }

    const ACCENT: egui::Color32 = egui::Color32::from_rgb(0x2F, 0x7C, 0xAE);

    fn inputs(set: PanelButtonSet, features: PanelFeatures, style: ButtonStyle, selection: ScreenRect, monitor: UiMonitor) -> PanelInputs {
        let readout = match set {
            // The scroll-pick strip has no readout; the value is carried
            // through unused, as the shell carries it.
            PanelButtonSet::Normal | PanelButtonSet::ScrollPick => Readout::Size {
                width: selection.width(),
                height: selection.height(),
            },
            PanelButtonSet::Ocr => Readout::Words(1234),
        };
        PanelInputs {
            set,
            features,
            style,
            accent: ACCENT,
            readout,
            selection,
            anchor: crate::selection::intersect_rects(monitor.bounds, selection).expect("the selection overlaps the monitor"),
        }
    }

    /// A styled, fonted context that runs the real panel. Real Cascadia
    /// faces are mandatory: `FontDefinitions::empty()` lays out zero-width
    /// glyphs and every size assertion would be wrong.
    struct Harness {
        ctx: egui::Context,
        monitor: UiMonitor,
    }

    impl Harness {
        fn new(monitor: UiMonitor) -> Self {
            let ctx = egui::Context::default();
            egui_extras::install_image_loaders(&ctx);
            ctx.set_fonts(crate::ui::fonts::font_definitions(&[]));
            theme::apply_style(&ctx);
            Self {
                ctx,
                monitor,
            }
        }

        fn raw_input(&self, pointer: Option<Pos2>, extra: &[egui::Event]) -> egui::RawInput {
            let ppp = self.monitor.dpi_scale.max(0.1);
            let b = self.monitor.bounds;
            let mut events = Vec::with_capacity(extra.len() + 1);
            events.push(match pointer {
                Some(p) => egui::Event::PointerMoved(p),
                None => egui::Event::PointerGone,
            });
            events.extend_from_slice(extra);
            egui::RawInput {
                screen_rect: Some(Rect::from_min_size(
                    Pos2::ZERO,
                    egui::vec2(b.width() as f32, b.height() as f32) / ppp,
                )),
                viewports: [(
                    egui::ViewportId::ROOT,
                    egui::ViewportInfo {
                        native_pixels_per_point: Some(ppp),
                        ..Default::default()
                    },
                )]
                .into_iter()
                .collect(),
                events,
                focused: true,
                ..Default::default()
            }
        }

        /// A fixed three passes — enough for the Area's rects to settle,
        /// and for a press to land on the second the way the host feeds
        /// it — with every `FullOutput` dropped without applying its
        /// deltas (epaint debug-asserts a non-empty `TexturesDelta` on
        /// drop). Unlike `MonitorHost::run` this reports the pointer on
        /// every pass: the host gates that on movement so it can tell
        /// when it is owed another run, and a fixed-length loop is not.
        fn run(&mut self, p: &PanelInputs, pointer: Option<Pos2>) -> PanelOutcome {
            self.tick(p, pointer, false)
        }

        /// One tick with the press-and-release pair fed from the second
        /// pass on, exactly as the host does it, and `clicked` ORed over
        /// the passes.
        fn press(&mut self, p: &PanelInputs, pointer: Pos2) -> PanelOutcome {
            self.tick(p, Some(pointer), true)
        }

        fn tick(&mut self, p: &PanelInputs, pointer: Option<Pos2>, press: bool) -> PanelOutcome {
            let press_events: Vec<egui::Event> = match pointer.filter(|_| press) {
                Some(pos) => [true, false]
                    .into_iter()
                    .map(|pressed| egui::Event::PointerButton {
                        pos,
                        button: egui::PointerButton::Primary,
                        pressed,
                        modifiers: egui::Modifiers::NONE,
                    })
                    .collect(),
                None => Vec::new(),
            };
            let mut acc = PanelOutcome::default();
            for pass in 0..3 {
                let extra: &[egui::Event] = if press && pass == 1 { &press_events } else { &[] };
                let raw = self.raw_input(pointer, extra);
                let monitor = self.monitor;
                let mut out = PanelOutcome::default();
                let full = self.ctx.run_ui(raw, |ui| {
                    out = show(ui.ctx(), p, monitor, true);
                });
                full.drop_without_applying_deltas();
                acc = PanelOutcome {
                    clicked: acc.clicked.or(out.clicked),
                    over_button: out.over_button,
                    over_tray: out.over_tray,
                };
            }
            acc
        }

        fn area_rect(&self) -> Rect {
            self.ctx
                .memory(|m| m.area_rect(egui::Id::new(PANEL_ID)))
                .expect("the tray has an area rect after a run")
        }

        fn interactive_rects(&self) -> Vec<Rect> {
            self.ctx.interactive_rects_last_pass()
        }

        /// The analytic placement, computed inside a pass so text
        /// measurement is legal. Call after at least one `run`.
        fn analytic(&mut self, p: &PanelInputs) -> (place::Side, ScreenRect) {
            let monitor = self.monitor;
            let mut got = None;
            let raw = self.raw_input(None, &[]);
            let full = self.ctx.run_ui(raw, |ui| {
                let (_, side, rect) = measure_and_place(ui.ctx(), p, monitor);
                got = Some((side, rect));
            });
            full.drop_without_applying_deltas();
            got.expect("the closure ran")
        }
    }

    const HD: (i32, i32) = (1920, 1080);

    fn hd(dpi: f32) -> UiMonitor {
        monitor(rect(0, 0, HD.0, HD.1), dpi)
    }

    /// A selection comfortably inset, so the row goes beneath it.
    fn row_selection() -> ScreenRect {
        rect(500, 300, 600, 400)
    }

    #[test]
    fn button_count_matches_visible_defs() {
        for &style in &[ButtonStyle::KeyHint, ButtonStyle::Below] {
            for &set in PanelButtonSet::ALL {
                for features in FEATURE_COMBINATIONS {
                    let mon = hd(1.0);
                    let mut h = Harness::new(mon);
                    let p = inputs(set, features, style, row_selection(), mon);
                    h.run(&p, None);
                    let expected = set.visible_defs(features).count();
                    // One extra: the tray body itself senses clicks.
                    assert_eq!(h.interactive_rects().len(), expected + 1, "{set:?} {style:?} {features:?}");
                }
            }
        }
    }

    /// The OCR strip's readout is the word count over a "words" caption,
    /// and the caption is what sets the slot's width, so the count's
    /// digits never change the strip.
    #[test]
    fn ocr_readout_shows_words_and_the_caption_sets_the_width() {
        let job = widgets::readout_job(Readout::Words(1234));
        assert_eq!(job.text, "1234\nwords");
        let size = widgets::readout_job(Readout::Size {
            width: 600,
            height: 400,
        });
        assert_eq!(size.text, "600\n\u{00D7}\n400");

        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let mut widths = Vec::new();
        for n in [0, 7, 1234, 99_999] {
            let p = PanelInputs {
                readout: Readout::Words(n),
                ..inputs(PanelButtonSet::Ocr, PanelFeatures::ALL, ButtonStyle::KeyHint, row_selection(), mon)
            };
            h.run(&p, None);
            widths.push(h.area_rect().width());
        }
        assert!(
            widths
                .iter()
                .all(|w| (w - widths[0]).abs() <= 0.01),
            "{widths:?}"
        );
    }

    /// The chip hangs off the strip's far edge, centred on the button,
    /// and flips to the near edge when the far one is off the monitor.
    #[test]
    fn tip_rect_hangs_off_the_far_edge_and_flips_when_out_of_room() {
        let screen = Rect::from_min_size(Pos2::ZERO, vec2(1920.0, 1080.0));
        let chip = vec2(50.0, 20.0);
        let tray = Rect::from_min_size(pos2(600.0, 700.0), vec2(500.0, 48.0));
        let button = Rect::from_min_size(pos2(700.0, 704.0), vec2(40.0, 40.0));
        let below = tip_rect(Axis::Row, tray, button, chip, screen);
        assert_eq!(below.top(), tray.bottom() + tokens::TIP_GAP_BOTTOM);
        assert_eq!(below.center().x, button.center().x);

        let low_tray = Rect::from_min_size(pos2(600.0, 1030.0), vec2(500.0, 48.0));
        let low_button = Rect::from_min_size(pos2(700.0, 1034.0), vec2(40.0, 40.0));
        let above = tip_rect(Axis::Row, low_tray, low_button, chip, screen);
        assert_eq!(above.bottom(), low_tray.top() - tokens::TIP_GAP_BOTTOM);

        let col = Rect::from_min_size(pos2(1000.0, 100.0), vec2(48.0, 500.0));
        let col_button = Rect::from_min_size(pos2(1004.0, 200.0), vec2(40.0, 40.0));
        let right = tip_rect(Axis::Column, col, col_button, chip, screen);
        assert_eq!(right.left(), col.right() + tokens::TIP_GAP_RIGHT);
        assert_eq!(right.center().y, col_button.center().y);

        let edge_col = Rect::from_min_size(pos2(1870.0, 100.0), vec2(48.0, 500.0));
        let edge_button = Rect::from_min_size(pos2(1874.0, 200.0), vec2(40.0, 40.0));
        let left = tip_rect(Axis::Column, edge_col, edge_button, chip, screen);
        assert_eq!(left.right(), edge_col.left() - tokens::TIP_GAP_RIGHT);

        // Slid back onto the monitor along the strip.
        let corner_button = Rect::from_min_size(pos2(1900.0, 704.0), vec2(40.0, 40.0));
        let slid = tip_rect(Axis::Row, tray, corner_button, chip, screen);
        assert!(slid.right() <= screen.right(), "{slid:?}");
    }

    /// The tooltip is a `key`-style affair only, and it waits: a pointer
    /// resting on a button for a tick shows nothing, one resting for the
    /// delay shows the chip, and moving off the button removes it at
    /// once. Once a chip has been up, the next button gets its chip
    /// straight away — even after a short hop across no button — until
    /// the pointer has been off the buttons for the whole delay. The
    /// harness feeds no clock, so egui steps time by its predicted frame
    /// (1/60 s) per pass.
    #[test]
    fn key_hint_tooltip_appears_after_the_delay_and_names_the_button() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);
        let first = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.width() <= tokens::KEY_TILE + 0.5)
            .min_by(|a, b| a.left().total_cmp(&b.left()))
            .expect("a button rect");
        let tip_layer = egui::LayerId::new(egui::Order::Tooltip, egui::Id::new(TIP_ID));
        let tip_shown = |h: &Harness| {
            h.ctx
                .memory(|m| m.areas().visible_last_frame(&tip_layer))
        };

        h.run(&p, Some(first.center()));
        assert!(!tip_shown(&h), "the chip must wait out the delay");
        let passes = (tokens::TIP_DELAY_SECS * 60.0).ceil() as usize + 3;
        for _ in 0..passes / 3 {
            h.run(&p, Some(first.center()));
        }
        assert!(tip_shown(&h), "the chip is up after {passes} passes");
        let tip = h
            .ctx
            .memory(|m| m.area_rect(egui::Id::new(TIP_ID)))
            .expect("the chip has a rect");
        let tray = h.area_rect();
        assert!(
            (tip.top() - (tray.bottom() + tokens::TIP_GAP_BOTTOM)).abs() < 0.01,
            "{tip:?} vs {tray:?}"
        );
        // Centred to the pixel the Area was pinned on.
        assert!((tip.center().x - first.center().x).abs() <= 0.5, "{tip:?} vs {first:?}");

        h.run(&p, None);
        assert!(!tip_shown(&h), "the chip goes with the pointer");

        // Within the grace period, the next button shows at once — one
        // tick off every button (a gap between groups) does not reset it.
        let second = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.width() <= tokens::KEY_TILE + 0.5 && r.left() > first.left() + 1.0)
            .min_by(|a, b| a.left().total_cmp(&b.left()))
            .expect("a second button rect");
        h.run(&p, Some(second.center()));
        assert!(tip_shown(&h), "the chip follows the pointer within the grace period");
        let tip = h
            .ctx
            .memory(|m| m.area_rect(egui::Id::new(TIP_ID)))
            .expect("the chip has a rect");
        assert!((tip.center().x - second.center().x).abs() <= 0.5, "{tip:?} vs {second:?}");

        // Off every button for the whole delay: the wait is back.
        for _ in 0..passes / 3 {
            h.run(&p, None);
        }
        h.run(&p, Some(first.center()));
        assert!(!tip_shown(&h), "a long pause brings the wait back");

        // `below` has labels and never shows a chip.
        let below = inputs(PanelButtonSet::Normal, PanelFeatures::ALL, ButtonStyle::Below, row_selection(), mon);
        let mut h = Harness::new(mon);
        h.run(&below, None);
        let first = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.height() <= tokens::BELOW_HEIGHT + 0.5 && r.width() < 200.0)
            .min_by(|a, b| a.left().total_cmp(&b.left()))
            .expect("a button rect");
        for _ in 0..passes / 3 {
            h.run(&below, Some(first.center()));
        }
        assert!(!tip_shown(&h));
    }

    #[test]
    fn key_hint_letters_match_accelerators() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);
        for set in PanelButtonSet::ALL {
            for def in set.defs() {
                let button = widgets::key_hint_button(def, egui::Id::new(def.label), Vec2::ZERO, ACCENT, tokens::HOVER_VEIL_PRIMARY);
                assert_eq!(
                    button.text.text(),
                    def.accel_key()
                        .to_ascii_uppercase()
                        .to_string(),
                    "{}",
                    def.label
                );
            }
        }
    }

    #[test]
    fn below_labels_underline_the_accelerator_byte() {
        for set in PanelButtonSet::ALL {
            for def in set.defs() {
                let job = widgets::underlined_label(def);
                assert_eq!(job.text, def.label);
                assert_eq!(job.sections.len(), 3, "{}", def.label);
                let accel = &job.sections[1];
                assert_eq!(
                    (accel.byte_range.start.0, accel.byte_range.end.0),
                    (def.underline_idx, def.underline_idx + 1),
                    "{}",
                    def.label
                );
                assert!(accel.format.underline.width > 0.0, "{}", def.label);
            }
        }
    }

    #[test]
    fn frame_is_48_tall_in_key_style_and_56_in_below_at_100_percent() {
        for (style, height) in [(ButtonStyle::KeyHint, 48.0), (ButtonStyle::Below, 56.0)] {
            let mon = hd(1.0);
            let mut h = Harness::new(mon);
            let p = inputs(PanelButtonSet::Normal, PanelFeatures::ALL, style, row_selection(), mon);
            h.run(&p, None);
            assert!((h.area_rect().height() - height).abs() <= 0.5, "{style:?}: {:?}", h.area_rect());
        }
    }

    #[test]
    fn area_rect_equals_the_analytic_strip() {
        // A row anchor and a column anchor: a selection hugging the bottom
        // of a tall monitor forces the column for either set.
        let cases: [(UiMonitor, ScreenRect); 2] = [
            (hd(1.0), row_selection()),
            (monitor(rect(0, 0, 1920, 1300), 1.0), rect(100, 300, 400, 998)),
        ];
        for &style in &[ButtonStyle::KeyHint, ButtonStyle::Below] {
            for &set in PanelButtonSet::ALL {
                for (mon, sel) in cases {
                    for dpi in DPIS {
                        let mon = monitor(mon.bounds, dpi);
                        let mut h = Harness::new(mon);
                        let p = inputs(set, PanelFeatures::ALL, style, sel, mon);
                        h.run(&p, None);
                        let (_, expected) = h.analytic(&p);
                        let area = h.area_rect();
                        let tol = 1.0 / dpi + 1e-3;
                        assert!(
                            (area.width() - expected.width() as f32 / dpi).abs() <= tol
                                && (area.height() - expected.height() as f32 / dpi).abs() <= tol,
                            "{set:?} {style:?} at {dpi}: {area:?} vs {expected:?}"
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn side_follows_the_placement_rules() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            rect(500, 300, 100, 100),
            mon,
        );
        h.run(&p, None);
        assert_eq!(h.analytic(&p).0.axis(), Axis::Row);

        let low = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            rect(500, 1000, 100, 60),
            mon,
        );
        h.run(&low, None);
        assert_eq!(h.analytic(&low).0.axis(), Axis::Column);
    }

    #[test]
    fn swap_recentres_with_the_new_width() {
        for dpi in DPIS {
            let mon = hd(dpi);
            let centres: Vec<f32> = PanelButtonSet::ALL
                .iter()
                .map(|&set| {
                    let mut h = Harness::new(mon);
                    let p = inputs(set, PanelFeatures::ALL, ButtonStyle::KeyHint, row_selection(), mon);
                    h.run(&p, None);
                    h.area_rect().center().x
                })
                .collect();
            assert!((centres[0] - centres[1]).abs() <= 1.0 / dpi, "at {dpi}: {centres:?}");
        }
    }

    /// Pinned over the union sets only: the scroll-picker is not one of
    /// them — it is locked to a row, so it has no column width to agree
    /// about, and its instruction would widen every other set's column if
    /// it were folded in.
    /// The instruction is wrapped by the strip, not by hand: two lines,
    /// near enough the same length that the block reads as a paragraph
    /// rather than a long line with a word hanging off it.
    #[test]
    fn the_scroll_pick_instruction_wraps_into_two_balanced_lines() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::ScrollPick,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);

        let raw = h.raw_input(None, &[]);
        let mut rows: Vec<f32> = Vec::new();
        let full = h.ctx.run_ui(raw, |ui| {
            let ctx = ui.ctx();
            let (wrap, lines) = hint_wrap(ctx, crate::ui::components::panel::model::SCROLL_PICK_HINT);
            assert_eq!(lines, 2, "wrapped at {wrap}");
            let galley = ctx.fonts_mut(|f| f.layout_job(widgets::hint_job(crate::ui::components::panel::model::SCROLL_PICK_HINT, wrap)));
            rows = galley
                .rows
                .iter()
                .map(|r| r.rect().width())
                .collect();
        });
        full.drop_without_applying_deltas();

        assert_eq!(rows.len(), 2);
        let (short, long) = (rows[0].min(rows[1]), rows[0].max(rows[1]));
        assert!(short / long >= 0.75, "lines are lopsided: {rows:?}");
    }

    /// The scroll-pick strip is a row wherever it lands: the anchor here
    /// is the one every other set answers with a column.
    #[test]
    fn the_scroll_pick_strip_is_always_a_row() {
        let mon = monitor(rect(0, 0, 1920, 1300), 1.0);
        let sel = rect(100, 300, 400, 998);
        for &set in PanelButtonSet::ALL {
            let mut h = Harness::new(mon);
            let p = inputs(set, PanelFeatures::ALL, ButtonStyle::Below, sel, mon);
            h.run(&p, None);
            let want = if set == PanelButtonSet::ScrollPick {
                Axis::Row
            } else {
                Axis::Column
            };
            assert_eq!(h.analytic(&p).0.axis(), want, "{set:?}");
        }
    }

    #[test]
    fn column_width_is_the_union_thickness() {
        let mon = monitor(rect(0, 0, 1920, 1300), 1.0);
        let sel = rect(100, 300, 400, 998);
        let mut widths = Vec::new();
        for &set in PanelButtonSet::UNION {
            for features in [
                PanelFeatures::ALL,
                PanelFeatures {
                    upload: false,
                    ..PanelFeatures::ALL
                },
            ] {
                let mut h = Harness::new(mon);
                let p = inputs(set, features, ButtonStyle::Below, sel, mon);
                h.run(&p, None);
                assert_eq!(h.analytic(&p).0.axis(), Axis::Column, "{set:?} {features:?}");
                widths.push(h.area_rect().width());
            }
        }
        let first = widths[0];
        for w in &widths {
            assert!((w - first).abs() <= 0.01, "{widths:?}");
        }
    }

    #[test]
    fn dead_tray_swallows_and_reports_over_tray() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);
        let area = h.area_rect();
        let emblem = egui::pos2(area.left() + tokens::PAD + tokens::EMBLEM_SLOT / 2.0, area.center().y);
        let out = h.press(&p, emblem);
        assert!(out.clicked.is_none() && out.over_tray && !out.over_button, "{out:?}");

        let outside = egui::pos2(area.right() + 1.0, area.center().y);
        let out = h.run(&p, Some(outside));
        assert!(!out.over_tray && !out.over_button, "{out:?}");
    }

    /// Decision 3: the overlay fires on the OS press, so the press tick
    /// must answer with the command in the same call that laid the strip
    /// out — no second event, no frame of latency.
    #[test]
    fn press_on_a_button_reports_its_command_synchronously() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);
        // The leftmost button of a row is the first visible def.
        let first = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.width() <= tokens::KEY_TILE + 0.5)
            .min_by(|a, b| a.left().total_cmp(&b.left()))
            .expect("a button rect");
        let expected = PanelButtonSet::Normal
            .visible_defs(PanelFeatures::ALL)
            .next()
            .expect("a visible button")
            .1
            .command;
        let out = h.press(&p, first.center());
        assert_eq!(out.clicked, Some(expected), "{out:?}");
        assert!(out.over_button && out.over_tray, "{out:?}");
    }

    #[test]
    fn pointer_gone_clears_hover() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);
        let first = h
            .interactive_rects()
            .into_iter()
            .find(|r| r.width() <= tokens::KEY_TILE + 0.5)
            .expect("a button rect");
        let over = h.run(&p, Some(first.center()));
        assert!(over.over_button && over.over_tray, "{over:?}");
        let gone = h.run(&p, None);
        assert!(!gone.over_button && !gone.over_tray, "{gone:?}");
    }
}
