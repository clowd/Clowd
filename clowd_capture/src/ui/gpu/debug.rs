//! GPU debug-panel renderer.
//!
//! Two panels share one renderer:
//!   * **Monitor Info** — drawn on every monitor when `debug_visible`,
//!     anchored top-left. Includes a sparkline of recent frame times.
//!   * **Primary Debug** — drawn only on the cursor's monitor, anchored
//!     top-right. Text-only.

use glyphon::{Attrs, Buffer, Color, Family, Metrics, Shaping, TextArea, TextBounds, Weight, Wrap};

use crate::geometry::{RectExt, ScreenRect};
use crate::ui::components::debug::layout::{compute_layout, DebugPanelLayout, PanelAnchor, BODY_FONT_PX};
use crate::ui::components::debug::model::{LineBuf, MonitorPanelData, PrimaryPanelData};
use std::time::Duration;

use crate::telemetry::perf::{PerfSample, PerfTracker};
use crate::telemetry::startup::StartupTimings;
use crate::ui::gpu::rect::RectInstance;
use crate::ui::gpu::text::{TextStack, FAMILY_MONO};
use crate::ui::shared::{debug_monitor_visibility, debug_primary_visibility, UiMonitor, UiSharedState};

/// Panel body opacity.
const BODY_ALPHA: f32 = 0.70;

/// Scale at which a bar saturates at the full graph height. 1.0 ×
/// budget → half height; 2.0 × budget → full height. Gives spikes
/// visual headroom above the budget line before they clip.
const SPARK_BUDGET_HEADROOM: f32 = 2.0;

/// Master switch for the sparkline. When `false` the monitor panel
/// skips both reserving the graph area in its layout and all the
/// per-frame rect + text emission work — useful for A/B comparing
/// frame times with vs without the graph's overhead.
const SPARKLINE_ENABLED: bool = true;

// Bar / legend / drop-marker colours. Exposed as constants so the
// legend swatches are guaranteed to match the bar fills.
const COLOR_CPU: [f32; 4] = [0.40, 0.60, 0.95, 0.85];
const COLOR_GPU: [f32; 4] = [0.95, 0.80, 0.30, 0.85];

struct CachedLine {
    buffer: Buffer,
    last_text: String,
    last_font_px: f32,
}

impl CachedLine {
    fn new(ts: &mut TextStack, font_px: f32) -> Self {
        let metrics = Metrics::new(font_px, font_px * 1.2);
        let mut buffer = Buffer::new(&mut ts.font_system, metrics);
        buffer.set_wrap(Wrap::None);
        Self {
            buffer,
            last_text: String::new(),
            last_font_px: font_px,
        }
    }

    fn set(&mut self, ts: &mut TextStack, text: &str, font_px: f32) -> bool {
        // Skip every call into cosmic-text when the line hasn't
        // visually changed. Static labels (legend, fps annotations,
        // panel header) hit this path every frame — a no-op equality
        // check is vastly cheaper than letting cosmic-text decide.
        if text == self.last_text && (font_px - self.last_font_px).abs() < 0.01 {
            return false;
        }

        if (font_px - self.last_font_px).abs() >= 0.01 {
            let metrics = Metrics::new(font_px, font_px * 1.2);
            self.buffer.set_metrics(metrics);
            self.last_font_px = font_px;
        }

        if text != self.last_text {
            let attrs = Attrs::new()
                .family(Family::Name(FAMILY_MONO))
                .weight(Weight::NORMAL);
            self.buffer
                .set_text(text, &attrs, Shaping::Advanced, None);
            self.buffer
                .shape_until_scroll(&mut ts.font_system, false);
            self.last_text.clear();
            self.last_text.push_str(text);
        }
        true
    }

    fn width(&self) -> f32 {
        self.buffer
            .layout_runs()
            .map(|r| r.line_w)
            .fold(0.0f32, f32::max)
    }
}

#[derive(Clone, Copy)]
struct PositionedLine {
    idx: usize,
    x: f32,
    y: f32,
    color: Color,
}

pub struct DebugRenderer {
    lines: Vec<CachedLine>,
    positions: Vec<PositionedLine>,
    monitor_index: usize,
    /// Reused across frames to avoid heap allocations in the
    /// `MonitorPanelData::write_lines` / `PrimaryPanelData::write_lines`
    /// hot path. Cleared before each write.
    line_buf: LineBuf,
}

impl DebugRenderer {
    pub fn new(monitor_index: usize) -> Self {
        Self {
            lines: Vec::new(),
            positions: Vec::new(),
            monitor_index,
            line_buf: LineBuf::new(),
        }
    }

    #[allow(clippy::too_many_arguments)]
    pub fn prepare(
        &mut self,
        ts: &mut TextStack,
        state: &UiSharedState,
        this_monitor: &UiMonitor,
        monitor_name: &str,
        adapter_name: &str,
        perf: &PerfTracker,
        startup: &StartupTimings,
        shown_time: Option<Duration>,
        time_to_first_render: Option<Duration>,
        rects: &mut Vec<RectInstance>,
    ) {
        self.positions.clear();

        if !debug_monitor_visibility(state, this_monitor) && !debug_primary_visibility(state, this_monitor) {
            return;
        }

        let dpi = this_monitor.dpi_scale.max(0.1);
        let font_px = (BODY_FONT_PX * dpi).floor();

        // --- Monitor panel (every monitor when debug_visible) ---
        if debug_monitor_visibility(state, this_monitor) {
            let data = MonitorPanelData {
                index: self.monitor_index,
                name: monitor_name,
                is_primary: this_monitor.is_primary,
                adapter: adapter_name,
                dpi: (dpi * 96.0).round() as u32,
                bounds: this_monitor.bounds,
                time_to_first_render,
                perf,
                target_period: perf.target_period(),
            };
            data.write_lines(&mut self.line_buf);
            let layout = render_panel_inner(
                &mut self.lines,
                &mut self.positions,
                ts,
                this_monitor,
                font_px,
                &self.line_buf,
                PanelAnchor::TopLeft,
                SPARKLINE_ENABLED,
                rects,
            );
            if SPARKLINE_ENABLED {
                if let Some(graph_rect) = layout.graph_rect {
                    self.emit_sparkline(ts, graph_rect, this_monitor, perf, font_px, rects);
                }
            }
        }

        // --- Primary panel (cursor monitor only) ---
        if debug_primary_visibility(state, this_monitor) {
            let data = PrimaryPanelData {
                startup,
                shown_time,
                zoom: state.zoom,
                cursor: state.virtual_cursor,
                color_bgra: state.hovered_pixel_bgra,
                dragging: state.dragging,
                captured: state.captured,
                selection: state.selection,
                hovered_window_title: state.hovered_window_title.as_deref(),
                hovered_window_bounds: state.hovered_window_bounds,
                hovered_window_index: state.hovered_window_index,
                hovered_window_obstructed: state.hovered_window_obstructed,
            };
            data.write_lines(&mut self.line_buf);
            render_panel_inner(
                &mut self.lines,
                &mut self.positions,
                ts,
                this_monitor,
                font_px,
                &self.line_buf,
                PanelAnchor::TopRight,
                false,
                rects,
            );
        }
    }

    fn ensure_capacity(&mut self, ts: &mut TextStack, n: usize, font_px: f32) {
        while self.lines.len() < n {
            self.lines.push(CachedLine::new(ts, font_px));
        }
    }

    pub fn text_areas<'a>(&'a self, viewport_px: (u32, u32), out: &mut Vec<TextArea<'a>>) {
        let (vw, vh) = (viewport_px.0 as i32, viewport_px.1 as i32);
        out.extend(self.positions.iter().map(|p| TextArea {
            buffer: &self.lines[p.idx].buffer,
            left: p.x,
            top: p.y,
            scale: 1.0,
            bounds: TextBounds {
                left: 0,
                top: 0,
                right: vw,
                bottom: vh,
            },
            default_color: p.color,
            custom_glyphs: &[],
        }));
    }
}

/// Shared path for both panels. Takes mutable slices over the renderer's
/// own fields rather than `&mut DebugRenderer` so the caller can pass in
/// the line buf without a borrow-checker conflict against the other
/// `&mut self` field accesses it needs. Returns the final layout so the
/// caller can emit sparkline content inside `graph_rect` when present.
#[allow(clippy::too_many_arguments)]
fn render_panel_inner(
    lines: &mut Vec<CachedLine>,
    positions: &mut Vec<PositionedLine>,
    ts: &mut TextStack,
    this_monitor: &UiMonitor,
    font_px: f32,
    line_buf: &LineBuf,
    anchor: PanelAnchor,
    include_graph: bool,
    rects: &mut Vec<RectInstance>,
) -> DebugPanelLayout {
    let text_lines = line_buf.as_slice();
    let line_colors = line_buf.colors();

    let first_idx = positions.len();
    while lines.len() < first_idx + text_lines.len() {
        lines.push(CachedLine::new(ts, font_px));
    }

    let mut longest = 0.0f32;
    for (i, text) in text_lines.iter().enumerate() {
        let line = &mut lines[first_idx + i];
        line.set(ts, text, font_px);
        longest = longest.max(line.width());
    }

    let layout = compute_layout(
        this_monitor.bounds,
        this_monitor.dpi_scale.max(0.1),
        longest,
        text_lines.len(),
        anchor,
        include_graph,
    );

    emit_background_rect(&layout, this_monitor, rects);

    let mon_f = this_monitor.bounds.to_f32();
    let panel_f = layout.panel_rect.to_f32();
    let local_panel_left = panel_f.left() - mon_f.left();
    let local_panel_top = panel_f.top() - mon_f.top();
    let x = local_panel_left + layout.padding_px;
    let mut y = local_panel_top + layout.padding_px;
    for (i, &[r, g, b, a]) in line_colors.iter().enumerate() {
        positions.push(PositionedLine {
            idx: first_idx + i,
            x,
            y,
            color: Color::rgba(r, g, b, a),
        });
        y += layout.row_height;
    }

    layout
}

fn emit_background_rect(layout: &DebugPanelLayout, this_monitor: &UiMonitor, rects: &mut Vec<RectInstance>) {
    let mon_f = this_monitor.bounds.to_f32();
    let panel_f = layout.panel_rect.to_f32();
    let l = panel_f.left() - mon_f.left();
    let t = panel_f.top() - mon_f.top();
    let r = l + panel_f.width();
    let b = t + panel_f.height();
    rects.push(RectInstance::filled(l, t, r, b, [0.0, 0.0, 0.0, BODY_ALPHA]));
}

impl DebugRenderer {
    /// Build the sparkline inside the given graph rect.
    ///
    /// Layout: one stacked bar per frame, newest on the right. Stacks
    /// from bottom up: wait (blue), gpu-or-draw (yellow/green),
    /// present (orange). The top of the graph maps to 2 × budget, so
    /// budget-height = middle, spikes = top, red cap = clamped
    /// overflow.
    fn emit_sparkline(
        &mut self,
        ts: &mut TextStack,
        graph_rect: ScreenRect,
        this_monitor: &UiMonitor,
        perf: &PerfTracker,
        font_px: f32,
        rects: &mut Vec<RectInstance>,
    ) {
        let mon_f = this_monitor.bounds.to_f32();
        let gf = graph_rect.to_f32();
        let g_left = gf.left() - mon_f.left();
        let g_top = gf.top() - mon_f.top();
        let g_right = g_left + gf.width();
        let g_bottom = g_top + gf.height();
        let g_w = gf.width();
        let g_h = gf.height();

        // Subtle graph background so the bars aren't floating on a flat
        // panel surface.
        rects.push(RectInstance::filled(g_left, g_top, g_right, g_bottom, [1.0, 1.0, 1.0, 0.04]));

        // Single budget reference line at the refresh rate (e.g. 60 fps on
        // a 60 Hz monitor). With headroom=2.0 this sits at half the graph
        // height. Bars that visibly exceed this line are dropped frames —
        // their `overall` pushed them toward the top of the graph on their
        // own, so no explicit drop marker is needed.
        let target_period = perf.target_period();
        let mut budget_y: Option<f32> = None;
        if let Some(period) = target_period {
            let budget_ms = period.as_secs_f64() * 1000.0;
            let full_scale_ms = budget_ms * SPARK_BUDGET_HEADROOM as f64;
            let y = g_bottom - ((budget_ms / full_scale_ms) * g_h as f64) as f32;
            rects.push(RectInstance::filled(g_left, y, g_right, y + 1.0, [1.0, 1.0, 1.0, 0.35]));
            budget_y = Some(y);
        }

        // Pick a full-scale height reference. When the monitor's refresh
        // rate is unknown, use the 95th-percentile-ish top of the recent
        // samples so the bars still self-normalise instead of all being
        // zero-height.
        let full_scale_ms = match target_period {
            Some(p) => p.as_secs_f64() * 1000.0 * SPARK_BUDGET_HEADROOM as f64,
            None => 16.67 * SPARK_BUDGET_HEADROOM as f64,
        };
        if full_scale_ms <= 0.0 {
            return;
        }

        // Single fps label anchored to the budget line (= monitor refresh
        // rate). Placed just above the line on the left edge of the graph.
        let label_font_px = (font_px * 0.85).floor().max(9.0);
        if let (Some(period), Some(by)) = (target_period, budget_y) {
            let budget_fps = 1.0 / period.as_secs_f64();
            let label_height = label_font_px * 1.2;
            self.push_graph_label(
                ts,
                &format!("{:.0} fps", budget_fps),
                g_left + 4.0,
                by - label_height - 1.0,
                label_font_px,
            );
        }

        // Uniform whole-pixel bar width: smallest integer width that
        // completely fills the graph for the target window size. Newest
        // samples draw from the right; oldest fall off the left if the
        // wider bars can't fit them all.
        let window = perf.window_size().max(1);
        let bar_w = ((g_w / window as f32).ceil() as usize).max(1);
        let bar_wf = bar_w as f32;
        let max_bars = (g_w / bar_wf).floor() as usize;

        let mut x_right = g_right;
        for sample in perf.samples_newest_first().take(max_bars) {
            let x_left = x_right - bar_wf;
            if x_left < g_left {
                break;
            }
            emit_bar_stack(x_left, x_right, g_top, g_bottom, g_h, full_scale_ms, sample, rects);
            x_right -= bar_wf;
        }

        // Legend: cpu / gpu. Matches what the bars actually use. Drops
        // don't get their own colour — a dropped frame shows up naturally
        // as a ~2× tall bar.
        self.emit_legend(ts, g_left, g_bottom + label_font_px * 0.8, label_font_px, rects);
    }

    /// Emit the sparkline colour legend: 3 swatches + text labels,
    /// one row below the graph.
    fn emit_legend(&mut self, ts: &mut TextStack, x_start: f32, y: f32, font_px: f32, rects: &mut Vec<RectInstance>) {
        let swatch = font_px; // square, tracking text height
        let gap_swatch_text = 4.0;
        let gap_item = font_px * 0.9;
        let mut x = x_start;

        let items: [([f32; 4], &str); 2] = [(COLOR_CPU, "cpu"), (COLOR_GPU, "gpu")];

        for (color, label) in items {
            rects.push(RectInstance::filled(x, y, x + swatch, y + swatch, color));
            x += swatch + gap_swatch_text;
            self.push_graph_label(ts, label, x, y - 1.0, font_px);
            let label_w = font_px * 0.6 * label.chars().count() as f32;
            x += label_w + gap_item;
        }
    }

    /// Shape a small label and register it as a positioned text line.
    /// Uses the same CachedLine pool the main panel text uses; these
    /// entries land after all panel text lines in `self.positions`, so
    /// they render on top of the sparkline bars in the UI pass.
    fn push_graph_label(&mut self, ts: &mut TextStack, text: &str, x: f32, y: f32, font_px: f32) {
        let idx = self.positions.len();
        self.ensure_capacity(ts, idx + 1, font_px);
        self.lines[idx].set(ts, text, font_px);
        self.positions.push(PositionedLine {
            idx,
            x,
            y,
            color: Color::rgba(0xFF, 0xFF, 0xFF, 0xFF),
        });
    }
}

/// Emit one bar for a single frame sample.
///
/// Bar **total height** tracks `overall` (wall-clock frame time)
/// scaled against `full_scale_ms` (= 2 × refresh period). A 60 Hz
/// frame fills half the graph; a dropped frame ~doubles in duration
/// and fills the whole graph — so drops are self-evident from the
/// bar's height without needing a coloured marker.
///
/// Inside that height, `cpu` (blue) and `gpu` (yellow) stack at the
/// bottom at their absolute ms scale. Whatever's left up to the total
/// height is vsync slack — rendered empty.
///
///   ┌─────────┐  ← top of graph (2 × refresh period)
///   │  slack  │   (unfilled; = `overall - cpu - gpu`)
///   ├─────────┤
///   │   gpu   │   yellow
///   ├─────────┤
///   │   cpu   │   blue (draw + present)
///   └─────────┘  ← bottom (0 ms)
#[allow(clippy::too_many_arguments)]
fn emit_bar_stack(
    x_left: f32,
    x_right: f32,
    _g_top: f32,
    g_bottom: f32,
    g_h: f32,
    full_scale_ms: f64,
    sample: &PerfSample,
    rects: &mut Vec<RectInstance>,
) {
    let overall_ms = sample.overall.as_secs_f64() * 1000.0;
    if overall_ms <= 0.0 {
        return;
    }

    let px_per_ms = g_h as f64 / full_scale_ms;
    let total_h = ((overall_ms * px_per_ms).min(g_h as f64)) as f32;

    let cpu_ms = sample.draw.as_secs_f64() * 1000.0 + sample.present.as_secs_f64() * 1000.0;
    let gpu_ms = sample
        .gpu
        .map(|g| g.as_secs_f64() * 1000.0)
        .unwrap_or(0.0);

    let cpu_h = ((cpu_ms * px_per_ms) as f32).min(total_h);
    let gpu_h = ((gpu_ms * px_per_ms) as f32).min((total_h - cpu_h).max(0.0));

    let bottom = g_bottom;
    if cpu_h > 0.5 {
        rects.push(RectInstance::filled(x_left, bottom - cpu_h, x_right, bottom, COLOR_CPU));
    }
    if gpu_h > 0.5 {
        let gpu_top = bottom - cpu_h - gpu_h;
        rects.push(RectInstance::filled(x_left, gpu_top, x_right, bottom - cpu_h, COLOR_GPU));
    }
}
