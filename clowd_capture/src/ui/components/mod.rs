//! Concrete UI components and the one function that draws them.
//!
//! Each subdir holds one component: its data model, its pure layout or
//! placement rules, and the `show` that paints it into an egui pass.
//! [`compose`] is the whole picture for one monitor, called from inside a
//! host's run closure, so the order the calls appear in here is the order
//! the shapes land in.

pub mod area;
pub mod debug;
pub mod hints;
pub mod ocr;
pub mod panel;
pub mod pill;
pub mod scope;
pub mod tips;

use egui::{pos2, Color32, Id, LayerId, Order, Pos2, Rect};

use crate::interaction::InteractionState;
use crate::ui::egui_host::{HostInputs, PanelOutcome};
use crate::ui::shared::UiMonitor;
use clowd_rust_core::geometry::{RectExt, ScreenRectF};

/// One host's mapping from virtual-desktop physical px to its own points.
///
/// Overlays that can straddle a seam are authored in physical px — the
/// same shape has to come out the same physical size on both sides of it —
/// and converted here, per host. Overlays that only ever live on one
/// monitor skip this and work in points throughout.
#[derive(Clone, Copy, Debug)]
pub struct Local {
    pub left: f32,
    pub top: f32,
    pub ppp: f32,
}

impl Local {
    pub fn of(m: &UiMonitor) -> Self {
        Self {
            left: m.bounds.left() as f32,
            top: m.bounds.top() as f32,
            ppp: m.dpi_scale.max(0.1),
        }
    }

    pub fn pos(&self, x: f32, y: f32) -> Pos2 {
        pos2((x - self.left) / self.ppp, (y - self.top) / self.ppp)
    }

    pub fn rect(&self, r: ScreenRectF) -> Rect {
        Rect::from_min_max(self.pos(r.left(), r.top()), self.pos(r.right(), r.bottom()))
    }
}

/// Everything the per-host input builders read, computed once per sync
/// rather than once per monitor.
pub struct InputCtx<'a> {
    pub input: &'a InteractionState,
    /// Every monitor, so a builder can read the CURSOR monitor's terms
    /// rather than its own host's.
    pub monitors: &'a [UiMonitor],
    /// Index of the monitor under the (rounded) virtual cursor, `None`
    /// when it sits in a gap between monitors.
    pub cursor_index: Option<usize>,
    pub accent: Color32,
    /// Title of the window under the cursor, for the rows that name it.
    pub hovered_window_title: Option<&'a str>,
    /// Name of the monitor under the cursor, likewise.
    pub hovered_monitor_name: Option<&'a str>,
    /// The desktop pixel under the cursor, sampled once per sync.
    pub hovered_pixel_bgra: Option<[u8; 4]>,
    /// The captured cursor image's rect in virtual-desktop physical px,
    /// already `None` when the peek window covers it.
    pub cursor_image_rect: Option<ScreenRectF>,
    /// Whether the software cursor is drawn — already ANDed with "the
    /// peek does not cover it".
    pub cursor_overlay_visible: bool,
}

/// The accent colour, which the settings carry as floats, in the 8-bit
/// straight-alpha form egui paints with.
pub fn accent_color32(c: [f32; 4]) -> Color32 {
    let q = |v: f32| (v.clamp(0.0, 1.0) * 255.0).round() as u8;
    Color32::from_rgba_unmultiplied(q(c[0]), q(c[1]), q(c[2]), q(c[3]))
}

/// The overlays' half of one host's inputs: what every overlay other than
/// the tray and the debug panels draws this pass, already reduced to this
/// monitor's own terms by the per-overlay `inputs` builders.
///
/// One overlay at a time lands here as it moves onto egui.
#[derive(Clone, PartialEq, Default)]
pub struct OverlayInputs {
    /// The Q toggle. The tray still runs while hidden — clicks keep
    /// routing exactly as they did — but paints at opacity 0; every other
    /// overlay folds this into its own rule inside its `inputs` builder.
    pub overlays_visible: bool,
    /// The accent colour, which the hint comet paints with.
    pub accent: Color32,
    /// The "W x H" pill, on the host holding the cursor while a selection
    /// is being dragged out.
    pub area: Option<area::show::AreaInputs>,
    /// The Tips & Hotkeys panel, on the host holding the cursor.
    pub tips: Option<tips::show::TipsInputs>,
    /// The floating hint chips and the dashed cursor square.
    pub hints: hints::show::HintsInputs,
    /// The transient OCR notice pill, on the selection's own host.
    pub notice: Option<hints::show::NoticeInputs>,
    /// The scroll-pick reticle, on every host it reaches.
    pub scope: Option<scope::show::ScopeInputs>,
    /// The OCR sweep band and its text bubbles, on every host they reach.
    pub ocr: Option<ocr::show::OcrInputs>,
}

/// Draw one monitor's UI and report what the tray found under the pointer.
///
/// Called inside `MonitorHost::run`'s closure, which is the only place
/// text measurement and image loading are legal. The tray is an
/// `Order::Foreground` area and the debug panels are `Order::Middle`
/// areas, so the tray covers them both; the shape-only overlays paint
/// below both, through one background layer whose emission order is their
/// draw order — egui drains the bare layers of one order in hash order, so
/// a single layer id is what makes that order deterministic.
pub fn compose(ctx: &egui::Context, inputs: &HostInputs, monitor: UiMonitor) -> PanelOutcome {
    let o = &inputs.overlays;
    let painter = ctx.layer_painter(LayerId::new(Order::Background, Id::new("clowd-overlays")));
    // egui 0.36 calls the old `screen_rect` the viewport rect; with no
    // safe-area insets on Windows it is the whole monitor, in points.
    let screen = ctx.viewport_rect();
    let ppp = monitor.dpi_scale.max(0.1);
    if let Some(x) = &o.ocr {
        ocr::show::show(ctx, &painter, x, &monitor);
    }
    if let Some(x) = &o.scope {
        scope::show::show(&painter, x, &monitor);
    }
    if let Some(x) = &o.area {
        area::show::show(&painter, x, screen);
    }
    hints::show::show(ctx, &painter, &o.hints, o.notice.as_ref(), screen, o.accent, ppp);
    if let Some(x) = &o.tips {
        tips::show::show(&painter, x, screen);
    }
    let mut out = PanelOutcome::default();
    if let Some(p) = &inputs.panel {
        out = panel::show::show(ctx, p, monitor, o.overlays_visible);
    }
    if let Some(d) = &inputs.debug {
        debug::show::show(ctx, d);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::selection::intersect_rects;
    use crate::ui::components::panel::model::{ButtonStyle, PanelButtonSet, PanelFeatures};
    use crate::ui::components::panel::{show as panel_show, theme};
    use crate::ui::egui_host::PanelInputs;
    use clowd_rust_core::geometry::{RectExt, ScreenRect};
    use egui::epaint::Shape;

    fn monitor() -> UiMonitor {
        UiMonitor {
            bounds: ScreenRect::from_xy_size(0, 0, 1920, 1080),
            dpi_scale: 1.0,
            is_primary: true,
        }
    }

    /// A context fonted and styled the way a host's is, so `compose` meets
    /// the same egui it meets in production.
    fn context() -> egui::Context {
        let ctx = egui::Context::default();
        egui_extras::install_image_loaders(&ctx);
        ctx.set_fonts(crate::ui::fonts::font_definitions(&[]));
        theme::apply_style(&ctx);
        ctx
    }

    fn raw_input(monitor: UiMonitor, pointer: Option<egui::Pos2>) -> egui::RawInput {
        let b = monitor.bounds;
        egui::RawInput {
            screen_rect: Some(egui::Rect::from_min_size(
                egui::Pos2::ZERO,
                egui::vec2(b.width() as f32, b.height() as f32),
            )),
            events: vec![match pointer {
                Some(p) => egui::Event::PointerMoved(p),
                None => egui::Event::PointerGone,
            }],
            focused: true,
            ..Default::default()
        }
    }

    /// Every shape one pass produced, with the nested `Shape::Vec`s (a
    /// frame's background plus its shadow, for one) flattened out.
    fn shapes_of(output: &egui::FullOutput) -> Vec<Shape> {
        fn push(out: &mut Vec<Shape>, shape: &Shape) {
            match shape {
                Shape::Vec(inner) => inner.iter().for_each(|s| push(out, s)),
                other => out.push(other.clone()),
            }
        }
        let mut out = Vec::new();
        for clipped in &output.shapes {
            push(&mut out, &clipped.shape);
        }
        out
    }

    fn panel_inputs(monitor: UiMonitor) -> PanelInputs {
        let selection = ScreenRect::from_xy_size(500, 300, 600, 400);
        PanelInputs {
            set: PanelButtonSet::Normal,
            features: PanelFeatures::ALL,
            style: ButtonStyle::KeyHint,
            selection,
            anchor: intersect_rects(monitor.bounds, selection).expect("the selection is on this monitor"),
        }
    }

    /// The quiet case: a monitor with no tray, no debug panels and no
    /// overlays costs a pass that paints nothing at all. This is what lets
    /// a worker keep its previous upload instead of drawing an empty frame.
    #[test]
    fn compose_with_every_input_absent_emits_no_shapes() {
        let ctx = context();
        let monitor = monitor();
        let inputs = HostInputs {
            panel: None,
            debug: None,
            pointer: None,
            overlays: OverlayInputs::default(),
        };
        let output = ctx.run_ui(raw_input(monitor, None), |ui| {
            let out = compose(ui.ctx(), &inputs, monitor);
            assert_eq!(out, PanelOutcome::default());
        });
        let shapes = shapes_of(&output);
        output.drop_without_applying_deltas();
        assert!(
            shapes
                .iter()
                .all(|s| matches!(s, Shape::Noop)),
            "{shapes:?}"
        );
    }

    /// The tray's outcome is `compose`'s return value: the run that draws
    /// the strip is the run that answers what the pointer is over.
    #[test]
    fn compose_with_a_panel_returns_its_outcome() {
        let ctx = context();
        let monitor = monitor();
        let p = panel_inputs(monitor);
        let inputs = HostInputs {
            panel: Some(p),
            debug: None,
            pointer: None,
            overlays: OverlayInputs {
                overlays_visible: true,
                ..Default::default()
            },
        };
        // Two passes to place the strip and one with the pointer on it:
        // an `Area` only knows its own rect from its second pass, and egui
        // hit-tests against the previous pass's rects.
        for _ in 0..2 {
            ctx.run_ui(raw_input(monitor, None), |ui| {
                compose(ui.ctx(), &inputs, monitor);
            })
            .drop_without_applying_deltas();
        }
        let tray = ctx
            .memory(|m| m.area_rect(egui::Id::new(panel_show::PANEL_ID)))
            .expect("the tray has an area rect after a pass");

        let mut outcome = PanelOutcome::default();
        let output = ctx.run_ui(raw_input(monitor, Some(tray.center())), |ui| {
            outcome = compose(ui.ctx(), &inputs, monitor);
        });
        let shapes = shapes_of(&output);
        output.drop_without_applying_deltas();
        assert!(outcome.over_tray, "the pointer is on the strip");
        assert!(
            shapes
                .iter()
                .any(|s| !matches!(s, Shape::Noop)),
            "the strip paints"
        );
    }
}
