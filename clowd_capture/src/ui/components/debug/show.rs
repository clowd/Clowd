//! The two debug panels as egui `Area`s.
//!
//! Both are anchored to a monitor edge and neither is interactive: the
//! panels have never been hit-tested, and making them so would let them
//! steal hover from the tray or from a selection drag underneath. The
//! monitor panel adds a sparkline of recent frame times, painted directly
//! rather than built out of widgets — one `Painter` call per bar is far
//! less machinery than a widget per bar for something that changes ten
//! times a second.

use egui::{pos2, vec2, Align2, Color32, FontId, Sense, Stroke};

use crate::ui::components::panel::theme::{self, tokens};
use crate::ui::egui_host::{DebugInputs, MonitorPanelInputs};

/// Bar and legend colours. One constant per series so a swatch can never
/// drift from the bars it labels.
const COLOR_CPU: Color32 = Color32::from_rgba_unmultiplied_const(102, 153, 242, 217);
const COLOR_GPU: Color32 = Color32::from_rgba_unmultiplied_const(242, 204, 77, 217);

/// Scale at which a bar saturates at the full graph height: one budget
/// period is half height, so a dropped frame fills the graph on its own and
/// needs no marker of its own.
const BUDGET_HEADROOM: f32 = 2.0;

/// Frame period assumed when the monitor's refresh rate is unknown, so the
/// bars still self-normalise instead of all being zero-height.
const FALLBACK_BUDGET_MS: f32 = 16.67;

/// Build both panels for one monitor's context.
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
}

/// One panel: the rows, then the sparkline and its legend when this is the
/// monitor panel. Rows are laid out on an explicit pitch rather than on
/// egui's own spacing so a blank separator is exactly as tall as a row of
/// text, the way the C++ panel it mirrors did it.
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
            sparkline(ui, g, width);
            legend(ui, width);
        }
    });
}

/// How wide one bar is and how many of them fit, in points. Bars are a
/// whole number of points wide so the whole window's worth fills the graph
/// exactly; the oldest samples fall off the left when they cannot.
pub fn bars_to_draw(graph_width: f32, window_size: usize, available: usize) -> (f32, usize) {
    let window = window_size.max(1);
    let bar_w = (graph_width / window as f32).ceil().max(1.0);
    let fits = (graph_width / bar_w).floor().max(0.0) as usize;
    (bar_w, fits.min(available))
}

/// The frame-time sparkline: newest sample at the right edge, walking left.
fn sparkline(ui: &mut egui::Ui, g: &MonitorPanelInputs, width: f32) {
    let (response, painter) = ui.allocate_painter(vec2(width, tokens::DEBUG_GRAPH_H), Sense::hover());
    let r = response.rect;
    painter.rect_filled(r, 0u8, tokens::DEBUG_GRAPH_BG);

    let budget_ms = g
        .target_period
        .map_or(FALLBACK_BUDGET_MS, |p| (p.as_secs_f64() * 1000.0) as f32);
    let full_scale_ms = budget_ms * BUDGET_HEADROOM;
    if full_scale_ms <= 0.0 {
        return;
    }
    let px_per_ms = r.height() / full_scale_ms;

    // The reference line sits at the monitor's refresh rate, which with the
    // headroom above is always mid-height: a bar visibly past it is a
    // dropped frame.
    if g.target_period.is_some() {
        let budget_y = r.bottom() - budget_ms * px_per_ms;
        painter.line_segment(
            [pos2(r.left(), budget_y), pos2(r.right(), budget_y)],
            Stroke {
                width: 1.0,
                color: tokens::DEBUG_BUDGET_LINE,
            },
        );
        painter.text(
            pos2(r.left() + 4.0, budget_y - 1.0),
            Align2::LEFT_BOTTOM,
            format!("{:.0} fps", 1000.0 / budget_ms),
            FontId::monospace(tokens::DEBUG_LABEL_FONT),
            tokens::FG,
        );
    }

    let (bar_w, count) = bars_to_draw(r.width(), g.window_size, g.bars.len());
    let mut right = r.right();
    for [overall_ms, cpu_ms, gpu_ms] in g.bars.iter().copied().take(count) {
        let left = right - bar_w;
        // A sample with no wall-clock time keeps its slot but draws
        // nothing, so the bars either side stay where they belong.
        if overall_ms > 0.0 {
            // The bar's total height tracks the wall-clock frame time; cpu
            // and gpu stack from the bottom at their own scale, and
            // whatever is left below the total is vsync slack, deliberately
            // unfilled.
            let total_h = (overall_ms * px_per_ms).min(r.height());
            let cpu_h = (cpu_ms * px_per_ms).min(total_h);
            let gpu_h = (gpu_ms * px_per_ms).min((total_h - cpu_h).max(0.0));
            if cpu_h > 0.5 {
                let top = r.bottom() - cpu_h;
                painter.rect_filled(egui::Rect::from_min_max(pos2(left, top), pos2(right, r.bottom())), 0u8, COLOR_CPU);
            }
            if gpu_h > 0.5 {
                let bottom = r.bottom() - cpu_h;
                painter.rect_filled(
                    egui::Rect::from_min_max(pos2(left, bottom - gpu_h), pos2(right, bottom)),
                    0u8,
                    COLOR_GPU,
                );
            }
        }
        right = left;
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

    #[test]
    fn bars_to_draw_caps_by_width_and_window() {
        // A window that fits exactly: 600 samples across 600 pt.
        assert_eq!(bars_to_draw(600.0, 600, 600), (1.0, 600));
        // A window wider than the graph: bars widen to a whole point and
        // only as many as fit are drawn.
        assert_eq!(bars_to_draw(300.0, 600, 600), (1.0, 300));
        assert_eq!(bars_to_draw(300.0, 100, 600), (3.0, 100));
        // Fewer samples than slots: the sample count wins.
        assert_eq!(bars_to_draw(600.0, 600, 12), (1.0, 12));
        // Degenerate inputs must not divide by zero or panic.
        assert_eq!(bars_to_draw(0.0, 0, 0), (1.0, 0));
    }

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
        ctx.set_fonts(theme::font_definitions());
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

    /// A blank separator has to occupy a full row, or the panel's rows
    /// would bunch up wherever the model writes one.
    #[test]
    fn a_blank_row_is_a_full_row_tall() {
        let ctx = egui::Context::default();
        ctx.set_fonts(theme::font_definitions());
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
