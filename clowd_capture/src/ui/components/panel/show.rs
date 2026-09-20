//! The tray as one egui `Area`: measure the strip, place it beside the
//! selection, then lay the emblem, the readout and the buttons out along
//! the chosen axis.
//!
//! The strip's size is analytic — computed from the same numbers the
//! widgets are laid out with — because placement needs it before the Area
//! is positioned, and because each strip must be centred with its own
//! width for the set-swap guard's premise to hold. A test pins the
//! analytic size against what egui actually lays out at every DPI.

use egui::{vec2, Vec2};

use super::assets;
use super::model::{ButtonDef, ButtonStyle, PanelButtonSet, PanelFeatures};
use super::place::{self, Axis, Fit, Footprint, Near};
use super::theme::{self, tokens};
use super::widgets;
use crate::ui::egui_host::{PanelInputs, PanelOutcome};
use crate::ui::shared::UiMonitor;
use clowd_rust_core::geometry::RectExt;

/// The Area id. One tray per context, so one id per context.
pub const PANEL_ID: &str = "clowd-tray";

/// Every number the strip is laid out with, in points, measured before the
/// Area is positioned.
pub struct StripMetrics {
    /// Item thickness across the strip: the button's, and the readout's
    /// square side.
    pub thick: f32,
    /// The readout slot's length along a row.
    pub readout_along: f32,
    /// The visible buttons in strip order: table index, def, and the
    /// button's length along a row.
    pub buttons: Vec<(usize, &'static ButtonDef, f32)>,
}

impl StripMetrics {
    /// Measure one set under one feature switch set. Only valid inside a
    /// run: `Context::fonts_mut` panics before the first pass.
    pub fn measure(ctx: &egui::Context, set: PanelButtonSet, features: PanelFeatures, style: ButtonStyle, size: (i32, i32)) -> Self {
        let thick = match style {
            ButtonStyle::KeyHint => tokens::KEY_TILE,
            ButtonStyle::Below => tokens::BELOW_HEIGHT,
        };
        let buttons = set
            .visible_defs(features)
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
        let readout = ctx.fonts_mut(|f| f.layout_job(widgets::readout_job(size)));
        Self {
            thick,
            readout_along: thick.max(readout.size().x + 2.0 * tokens::READOUT_PAD_H),
            buttons,
        }
    }

    /// The strip's content size (the tray's padding not included) for one
    /// axis. A column is `col_inner` wide throughout, so a set swap or a
    /// feature change can never change its width.
    pub fn content_size(&self, axis: Axis, col_inner: f32) -> Vec2 {
        let n = self.buttons.len() as f32;
        let gaps = tokens::GAP * (n - 1.0).max(0.0);
        let buttons_along: f32 = match axis {
            Axis::Row => self
                .buttons
                .iter()
                .map(|(_, _, along)| *along)
                .sum(),
            Axis::Column => n * self.thick,
        };
        let readout_along = match axis {
            Axis::Row => self.readout_along,
            Axis::Column => self.thick,
        };
        let along = tokens::EMBLEM_SLOT + tokens::GAP + readout_along + tokens::GAP + buttons_along + gaps;
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

/// The longest box either set can become in each orientation with every
/// feature on, plus the column's inner width — so neither the side choice
/// nor the column thickness depends on which set is up or which switches
/// are on.
fn union_fit(ctx: &egui::Context, style: ButtonStyle, size: (i32, i32), ppp: f32) -> (Fit, f32) {
    let metrics: Vec<StripMetrics> = PanelButtonSet::ALL
        .iter()
        .map(|&set| StripMetrics::measure(ctx, set, PanelFeatures::ALL, style, size))
        .collect();
    let mut col_inner = tokens::EMBLEM_SLOT;
    for m in &metrics {
        col_inner = col_inner.max(m.readout_along);
        for (_, _, along) in &m.buttons {
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
    // The readout prints the UNCLIPPED selection, so a rect straddling two
    // monitors keeps showing its true size; only placement uses the
    // clipped anchor.
    let sel = (p.selection.width(), p.selection.height());
    let (fit, col_inner) = union_fit(ctx, p.style, sel, ppp);
    let m = StripMetrics::measure(ctx, p.set, p.features, p.style, sel);
    let (side, rect_px) = place::place(p.anchor, monitor.bounds, fit, Near::at_dpi(ppp), |axis| {
        outer_px(m.content_size(axis, col_inner), ppp)
    });
    let axis = side.axis();
    let local = egui::pos2(
        (rect_px.left() - monitor.bounds.left()) as f32 / ppp,
        (rect_px.top() - monitor.bounds.top()) as f32 / ppp,
    );

    let mut out = PanelOutcome::default();
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
                widgets::emblem(ui, emblem_slot);
                let readout_slot = match axis {
                    Axis::Row => vec2(m.readout_along, across),
                    Axis::Column => vec2(across, m.thick),
                };
                widgets::readout(ui, sel, readout_slot);
                for (table_idx, def, along) in &m.buttons {
                    // The set is part of the id, so hover state and hit
                    // tests can never resolve against the other strip; the
                    // table index keeps a switched-off button from
                    // renumbering its neighbours.
                    let id = egui::Id::new(("panel", p.set as u8, *table_idx));
                    let min = match axis {
                        Axis::Row => vec2(*along, across),
                        Axis::Column => vec2(across, m.thick),
                    };
                    let button = match p.style {
                        ButtonStyle::KeyHint => widgets::key_hint_button(def, id, min),
                        ButtonStyle::Below => widgets::below_button(def, id, min),
                    };
                    let r = button.show(ui);
                    out.over_button |= r.contains_pointer();
                    if r.clicked() {
                        out.clicked = Some(def.command);
                    }
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

    fn inputs(set: PanelButtonSet, features: PanelFeatures, style: ButtonStyle, selection: ScreenRect, monitor: UiMonitor) -> PanelInputs {
        PanelInputs {
            set,
            features,
            style,
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
                let ctx = ui.ctx();
                let ppp = monitor.dpi_scale.max(0.1);
                let sel = (p.selection.width(), p.selection.height());
                let (fit, col_inner) = union_fit(ctx, p.style, sel, ppp);
                let m = StripMetrics::measure(ctx, p.set, p.features, p.style, sel);
                got = Some(place::place(p.anchor, monitor.bounds, fit, Near::at_dpi(ppp), |axis| {
                    outer_px(m.content_size(axis, col_inner), ppp)
                }));
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
                let button = widgets::key_hint_button(def, egui::Id::new(def.label), Vec2::ZERO);
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

    #[test]
    fn column_width_is_the_union_thickness() {
        let mon = monitor(rect(0, 0, 1920, 1300), 1.0);
        let sel = rect(100, 300, 400, 998);
        let mut widths = Vec::new();
        for &set in PanelButtonSet::ALL {
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
