//! The two debug panels as egui `Area`s.
//!
//! Both are anchored to a monitor edge and neither is interactive: the
//! panels have never been hit-tested, and making them so would let them
//! steal hover from the tray or from a selection drag underneath. The
//! monitor panel adds a frame-time plot drawn by `egui_plot`, with its
//! axes, grid and legend off and its interaction senses cleared, so the
//! plot rect never claims a press either. While the panels are up the
//! workers publish a perf snapshot every frame and the panel asks for a
//! repaint at the host's floor, so the plot scrolls at frame rate instead
//! of being quantised to the ten-times-a-second poll it used to run on.

use egui::{pos2, vec2, Align2, Color32, FontId, Sense, Shape, Vec2};
use egui_plot::{HLine, Line, Plot, PlotPoint, PlotPoints};

use crate::ui::components::panel::theme::{self, tokens};
use crate::ui::egui_host::{DebugInputs, MonitorPanelInputs, REPAINT_FLOOR};

/// Series and legend colours. One constant per series so a swatch can
/// never drift from the area it labels. Both are opaque: a filled series
/// takes its fill from the stroke colour made opaque and multiplied by the
/// plot's own `fill_alpha`, so that alpha belongs on the series and not on
/// the colour.
const COLOR_CPU: Color32 = Color32::from_rgb(102, 153, 242);
const COLOR_GPU: Color32 = Color32::from_rgb(242, 204, 77);

/// The old bar alpha, applied to the cpu+gpu series. The cpu series is
/// filled opaque over it: an 85 % blue over yellow would tint green, while
/// opaque blue over the 4 % white graph background only reads a little
/// darker than the old bars did.
const FILL_ALPHA: f32 = 217.0 / 255.0;

/// Scale at which a sample saturates at the full graph height: one budget
/// period is half height, so a dropped frame fills the graph on its own and
/// needs no marker of its own.
const BUDGET_HEADROOM: f32 = 2.0;

/// Frame period assumed when the monitor's refresh rate is unknown, so the
/// samples still self-normalise instead of all being flat.
const FALLBACK_BUDGET_MS: f32 = 16.67;

/// Build both panels for one monitor's context. The plot is an animation
/// for as long as the panels are up, so this always asks for the next
/// tick; the host's floor is what keeps that at frame rate rather than as
/// fast as the app thread can run.
pub fn show(ctx: &egui::Context, d: &DebugInputs) {
    egui::Area::new(egui::Id::new("clowd-debug-monitor"))
        .order(egui::Order::Middle)
        .interactable(false)
        .fade_in(false)
        .anchor(Align2::LEFT_TOP, vec2(tokens::DEBUG_MARGIN, tokens::DEBUG_MARGIN))
        .show(ctx, |ui| body(ui, &d.monitor.lines, Some(&d.monitor)));

    if let Some(p) = &d.primary {
        egui::Area::new(egui::Id::new("clowd-debug-primary"))
            .order(egui::Order::Middle)
            .interactable(false)
            .fade_in(false)
            .anchor(Align2::RIGHT_TOP, vec2(-tokens::DEBUG_MARGIN, tokens::DEBUG_MARGIN))
            .show(ctx, |ui| body(ui, &p.lines, None));
    }
    ctx.request_repaint_after(REPAINT_FLOOR);
}

/// One panel: the rows, then the frame-time plot and its legend when this
/// is the monitor panel. Rows are laid out on an explicit pitch rather
/// than on egui's own spacing so a blank separator is exactly as tall as a
/// row of text, the way the C++ panel it mirrors did it.
fn body(ui: &mut egui::Ui, lines: &[(String, Color32)], graph: Option<&MonitorPanelInputs>) {
    theme::debug_frame().show(ui, |ui| {
        ui.set_max_width(tokens::DEBUG_MAX_W - 2.0 * tokens::DEBUG_PAD);
        ui.spacing_mut().item_spacing.y = 0.0;
        for (text, color) in lines {
            ui.add(
                egui::Label::new(
                    egui::RichText::new(text.as_str())
                        .text_style(egui::TextStyle::Monospace)
                        .color(*color)
                        .line_height(Some(tokens::DEBUG_ROW)),
                )
                .extend()
                .selectable(false),
            );
        }
        if let Some(g) = graph {
            // The graph spans the text, not the width cap: the panel is as
            // wide as its longest row, exactly as the hand layout sized it.
            let width = ui.min_rect().width().max(1.0);
            ui.add_space(tokens::DEBUG_ROW);
            plot(ui, g, width);
            legend(ui, width);
        }
    });
}

/// The two plotted series, newest sample at the right edge and the oldest
/// falling off the left. Both are clamped into `[0, full]` because nothing
/// clips a line to the plot rect: the painter has no scissor, so a spike
/// left at its true height would be drawn straight through the rows above.
/// `cpu` is the lower series and `top` is cpu+gpu; whatever is left below
/// the wall-clock total is vsync slack and is deliberately never drawn.
fn series(g: &MonitorPanelInputs, full: f32, n: f64) -> (Vec<[f64; 2]>, Vec<[f64; 2]>) {
    g.bars
        .iter()
        .copied()
        .take(g.window_size)
        .enumerate()
        .map(|(i, [overall_ms, cpu_ms, gpu_ms])| {
            let x = n - i as f64;
            let total = overall_ms.clamp(0.0, full);
            let cpu = cpu_ms.min(total);
            let gpu = gpu_ms.min((total - cpu).max(0.0));
            ([x, cpu as f64], [x, (cpu + gpu) as f64])
        })
        .unzip()
}

/// The frame-time plot: a filled cpu+gpu series with the filled cpu series
/// over it, which is what the old stacked bars amounted to. Every knob
/// that would make the plot interactive is off — the panels sit over a
/// live selection drag, and a plot that senses a press would swallow it.
fn plot(ui: &mut egui::Ui, g: &MonitorPanelInputs, width: f32) {
    let budget_ms = g
        .target_period
        .map_or(FALLBACK_BUDGET_MS, |p| p.as_secs_f32() * 1000.0);
    let full = (budget_ms * BUDGET_HEADROOM).max(1.0);
    let n = g.window_size.max(2) as f64;
    let (cpu, top) = series(g, full, n);
    // Reserved before the plot so the background lands under the series;
    // the rect is only known once the plot has allocated its space.
    let bg = ui.painter().add(Shape::Noop);
    let resp = Plot::new("clowd-frame-times")
        .width(width)
        .height(tokens::DEBUG_GRAPH_H)
        .min_size(vec2(1.0, 1.0))
        .show_axes(false)
        .show_grid(false)
        .show_background(false)
        .show_x(false)
        .show_y(false)
        .show_crosshair(false)
        .allow_zoom(false)
        .allow_drag(false)
        .allow_scroll(false)
        .allow_boxed_zoom(false)
        .allow_double_click_reset(false)
        .allow_axis_zoom_drag(false)
        .sense(Sense::hover())
        .set_margin_fraction(Vec2::ZERO)
        .default_x_bounds(0.0, n)
        .default_y_bounds(0.0, full as f64)
        .show(ui, |pu| {
            pu.line(
                Line::new("gpu", PlotPoints::new(top))
                    .color(COLOR_GPU)
                    .width(1.0)
                    .fill(0.0)
                    .fill_alpha(FILL_ALPHA),
            );
            pu.line(
                Line::new("cpu", PlotPoints::new(cpu))
                    .color(COLOR_CPU)
                    .width(1.0)
                    .fill(0.0)
                    .fill_alpha(1.0),
            );
            if g.target_period.is_some() {
                pu.hline(
                    HLine::new("budget", budget_ms as f64)
                        .color(tokens::DEBUG_BUDGET_LINE)
                        .width(1.0),
                );
            }
        });
    let r = resp.response.rect;
    ui.painter()
        .set(bg, Shape::rect_filled(r, 0u8, tokens::DEBUG_GRAPH_BG));
    // The reference line sits at the monitor's refresh rate, which with
    // the headroom above is always mid-height: a series visibly past it is
    // a dropped frame.
    if g.target_period.is_some() {
        let y = resp
            .transform
            .position_from_point(&PlotPoint::new(0.0, budget_ms as f64))
            .y;
        ui.painter().text(
            pos2(r.left() + 4.0, y - 1.0),
            Align2::LEFT_BOTTOM,
            format!("{:.0} fps", 1000.0 / budget_ms),
            FontId::monospace(tokens::DEBUG_LABEL_FONT),
            tokens::FG,
        );
    }
}

/// One row of swatch + label per series the bars use.
fn legend(ui: &mut egui::Ui, width: f32) {
    let (response, painter) = ui.allocate_painter(vec2(width, tokens::DEBUG_LEGEND_H), Sense::hover());
    let r = response.rect;
    let swatch = tokens::DEBUG_LABEL_FONT;
    let gap_item = tokens::DEBUG_LABEL_FONT * 0.9;
    let mut x = r.left();
    for (color, label) in [(COLOR_CPU, "cpu"), (COLOR_GPU, "gpu")] {
        painter.rect_filled(egui::Rect::from_min_size(pos2(x, r.top()), vec2(swatch, swatch)), 0u8, color);
        x += swatch + 4.0;
        let text = painter.text(
            pos2(x, r.top()),
            Align2::LEFT_TOP,
            label,
            FontId::monospace(tokens::DEBUG_LABEL_FONT),
            tokens::FG,
        );
        x = text.right() + gap_item;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ui::egui_host::PrimaryPanelInputs;
    use std::sync::Arc;
    use std::time::Duration;

    fn lines(n: usize) -> Arc<[(String, Color32)]> {
        (0..n)
            .map(|i| (format!("row {i}"), Color32::WHITE))
            .collect()
    }

    fn inputs() -> DebugInputs {
        DebugInputs {
            monitor: MonitorPanelInputs {
                lines: lines(15),
                bars: (0..200)
                    .map(|i| [8.0 + i as f32 * 0.01, 2.0, 3.0])
                    .collect(),
                target_period: Some(Duration::from_micros(16_667)),
                window_size: 600,
                seq: 1,
            },
            primary: Some(PrimaryPanelInputs {
                lines: lines(30),
            }),
        }
    }

    /// Both panels lay out against the real fonts, stay out of the hit-test
    /// entirely, and hang off the edges the hand layout anchored them to.
    #[test]
    fn debug_panels_build_without_panic() {
        let ctx = egui::Context::default();
        ctx.set_fonts(crate::ui::fonts::font_definitions(&[]));
        theme::apply_style(&ctx);
        let screen = egui::Rect::from_min_size(egui::Pos2::ZERO, vec2(1920.0, 1080.0));
        let d = inputs();
        for _ in 0..3 {
            let raw = egui::RawInput {
                screen_rect: Some(screen),
                ..Default::default()
            };
            let full = ctx.run_ui(raw, |ui| show(ui.ctx(), &d));
            full.drop_without_applying_deltas();
        }
        assert!(ctx.interactive_rects_last_pass().is_empty());

        let monitor = ctx
            .memory(|m| m.area_rect(egui::Id::new("clowd-debug-monitor")))
            .expect("the monitor panel has an area rect after a run");
        assert!((monitor.left() - tokens::DEBUG_MARGIN).abs() < 0.01, "{monitor:?}");
        assert!((monitor.top() - tokens::DEBUG_MARGIN).abs() < 0.01, "{monitor:?}");

        // Sub-point tolerance: egui anchors an Area with the pixel-rounded
        // size it recorded last pass, so the right edge can sit a fraction
        // of a point off the exact margin.
        let primary = ctx
            .memory(|m| m.area_rect(egui::Id::new("clowd-debug-primary")))
            .expect("the primary panel has an area rect after a run");
        assert!(
            (primary.right() - (screen.right() - tokens::DEBUG_MARGIN)).abs() < 0.5,
            "{primary:?}"
        );
    }

    /// A sample past the full scale is clamped rather than drawn where it
    /// falls: the plot rect is not clipped, so an unclamped spike would be
    /// painted over the rows above it. The stacking is clamped too — gpu
    /// only gets what the wall-clock total leaves above cpu.
    #[test]
    fn samples_are_clamped_to_full_scale() {
        let g = MonitorPanelInputs {
            lines: lines(0),
            bars: Arc::from([[100.0, 90.0, 40.0], [8.0, 2.0, 3.0], [0.0, 0.0, 0.0], [10.0, 12.0, 5.0]].as_slice()),
            target_period: Some(Duration::from_micros(16_667)),
            window_size: 600,
            seq: 1,
        };
        let full = 33.34_f32;
        let (cpu, top) = series(&g, full, 600.0);
        assert_eq!(cpu.len(), 4);
        assert_eq!(top.len(), 4);
        // Newest sample at the right edge, walking left one slot per
        // sample.
        assert_eq!(cpu[0][0], 600.0);
        assert_eq!(cpu[3][0], 597.0);
        // Way past full scale: both series stop at the ceiling.
        assert_eq!(cpu[0][1], full as f64);
        assert_eq!(top[0][1], full as f64);
        // A normal sample stacks as it always did.
        assert_eq!(cpu[1][1], 2.0);
        assert_eq!(top[1][1], 5.0);
        // A sample with no wall-clock time draws nothing at all.
        assert_eq!(cpu[2][1], 0.0);
        assert_eq!(top[2][1], 0.0);
        // cpu over the total clamps to it, and gpu then has nothing left.
        assert_eq!(cpu[3][1], 10.0);
        assert_eq!(top[3][1], 10.0);
    }

    /// The plot must stay out of the hit-test: the panels float over a
    /// live selection drag, and a plot rect that senses a press would take
    /// it. Drawn here inside a non-interactable `Area`, exactly as the
    /// panels do it.
    #[test]
    fn the_plot_registers_no_interactive_rect() {
        let ctx = egui::Context::default();
        ctx.set_fonts(crate::ui::fonts::font_definitions(&[]));
        theme::apply_style(&ctx);
        let d = inputs();
        let mut shapes = 0;
        for _ in 0..3 {
            let raw = egui::RawInput {
                screen_rect: Some(egui::Rect::from_min_size(egui::Pos2::ZERO, vec2(1920.0, 1080.0))),
                ..Default::default()
            };
            let full = ctx.run_ui(raw, |ui| {
                egui::Area::new(egui::Id::new("plot-probe"))
                    .interactable(false)
                    .fade_in(false)
                    .show(ui.ctx(), |ui| body(ui, &d.monitor.lines, Some(&d.monitor)));
            });
            shapes = full.shapes.len();
            full.drop_without_applying_deltas();
        }
        assert!(ctx.interactive_rects_last_pass().is_empty());
        // The plot really ran: 200 samples' worth of series, background,
        // budget line and legend are all in there.
        assert!(shapes > 0, "the probe emitted no shapes");
    }

    /// A blank separator has to occupy a full row, or the panel's rows
    /// would bunch up wherever the model writes one.
    #[test]
    fn a_blank_row_is_a_full_row_tall() {
        let ctx = egui::Context::default();
        ctx.set_fonts(crate::ui::fonts::font_definitions(&[]));
        theme::apply_style(&ctx);
        let measure = |texts: &[&str]| -> f32 {
            let rows: Arc<[(String, Color32)]> = texts
                .iter()
                .map(|t| ((*t).to_owned(), Color32::WHITE))
                .collect();
            let mut height = 0.0;
            for _ in 0..2 {
                let raw = egui::RawInput {
                    screen_rect: Some(egui::Rect::from_min_size(egui::Pos2::ZERO, vec2(1920.0, 1080.0))),
                    ..Default::default()
                };
                let full = ctx.run_ui(raw, |ui| {
                    let area = egui::Area::new(egui::Id::new("blank-row-probe"))
                        .fade_in(false)
                        .show(ui.ctx(), |ui| body(ui, &rows, None));
                    height = area.response.rect.height();
                });
                full.drop_without_applying_deltas();
            }
            height
        };
        let one = measure(&["a"]);
        let with_blank = measure(&["a", "", "b"]);
        assert!(
            (with_blank - one - 2.0 * tokens::DEBUG_ROW).abs() < 0.51,
            "one row {one}, three rows {with_blank}"
        );
    }
}
