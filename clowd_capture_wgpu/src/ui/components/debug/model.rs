//! Debug-panel content models.
//!
//! Two structs (`MonitorPanelData`, `PrimaryPanelData`) hold everything
//! the two debug panels display.

use std::fmt::{Arguments, Write};
use std::time::Duration;

use crate::geometry::{RectExt, ScreenPointF, ScreenRect};
use crate::ui::components::debug::perf::{PerfStats, PerfTracker, Series};
use crate::ui::components::debug::startup::StartupTimings;

pub const COLOR_WHITE: [u8; 4] = [0xFF, 0xFF, 0xFF, 0xFF];

/// Reusable line buffer with per-line colour. Holds `Vec<String>` across
/// frames so the debug panel's per-line `format!` calls can write into
/// existing allocations via `String::clear()` + `write!`. After the first
/// few frames no allocation happens unless the rendered text grows (which
/// it doesn't in steady state).
#[derive(Default)]
pub struct LineBuf {
    lines: Vec<String>,
    colors: Vec<[u8; 4]>,
    len: usize,
}

impl LineBuf {
    pub fn new() -> Self {
        Self {
            lines: Vec::with_capacity(20),
            colors: Vec::with_capacity(20),
            len: 0,
        }
    }

    pub fn reset(&mut self) {
        self.len = 0;
    }

    pub fn push(&mut self, args: Arguments<'_>) {
        self.push_colored(args, COLOR_WHITE);
    }

    pub fn push_colored(&mut self, args: Arguments<'_>, color: [u8; 4]) {
        if self.lines.len() <= self.len {
            self.lines.push(String::new());
            self.colors.push(COLOR_WHITE);
        }
        let s = &mut self.lines[self.len];
        s.clear();
        let _ = s.write_fmt(args);
        self.colors[self.len] = color;
        self.len += 1;
    }

    pub fn push_empty(&mut self) {
        if self.lines.len() <= self.len {
            self.lines.push(String::new());
            self.colors.push(COLOR_WHITE);
        }
        self.lines[self.len].clear();
        self.colors[self.len] = COLOR_WHITE;
        self.len += 1;
    }

    pub fn as_slice(&self) -> &[String] {
        &self.lines[..self.len]
    }

    pub fn colors(&self) -> &[[u8; 4]] {
        &self.colors[..self.len]
    }
}

fn write_stats_row(out: &mut LineBuf, label: &str, s: PerfStats) {
    write_stats_row_colored(out, label, s, COLOR_WHITE);
}

fn write_stats_row_colored(out: &mut LineBuf, label: &str, s: PerfStats, color: [u8; 4]) {
    const LABEL_WIDTH: usize = 7;
    if s.count == 0 {
        out.push(format_args!("{:<width$} n/a", label, width = LABEL_WIDTH));
        return;
    }
    out.push_colored(
        format_args!(
            "{:<width$} p50 {:>5.2}  p95 {:>5.2}  p99 {:>5.2}  low1 {:>5.2}",
            label,
            s.p50_ms,
            s.p95_ms,
            s.p99_ms,
            s.low1_ms,
            width = LABEL_WIDTH,
        ),
        color,
    );
}

/// Green→yellow→red gradient based on `t` in [0, 1].
fn budget_color(t: f32) -> [u8; 4] {
    let t = t.clamp(0.0, 1.0);
    let r = (2.0 * t).min(1.0);
    let g = (2.0 * (1.0 - t)).min(1.0);
    [(r * 255.0) as u8, (g * 255.0) as u8, 0x00, 0xFF]
}

/// Format helper: wall-clock duration as "x.xx ms" (two decimals).
struct DisplayMs(Duration);

impl std::fmt::Display for DisplayMs {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{:.2}ms", self.0.as_secs_f64() * 1000.0)
    }
}

fn phase_between(end: Option<Duration>, start: Option<Duration>) -> Option<Duration> {
    match (end, start) {
        (Some(end), Some(start)) => Some(end.saturating_sub(start)),
        (Some(end), None) => Some(end),
        _ => None,
    }
}

/// Format helper: wall-clock duration as "1h02m03s" / "2m03s" / "5s".
struct DisplaySessionElapsed(Duration);

impl std::fmt::Display for DisplaySessionElapsed {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let secs = self.0.as_secs();
        let h = secs / 3600;
        let m = (secs / 60) % 60;
        let s = secs % 60;
        if h > 0 {
            write!(f, "{}h{:02}m{:02}s", h, m, s)
        } else if m > 0 {
            write!(f, "{}m{:02}s", m, s)
        } else {
            write!(f, "{}s", s)
        }
    }
}

/// Format helper for a `ScreenRect` as "RECT[x, y, w, h]".
struct DisplayRect(ScreenRect);

impl std::fmt::Display for DisplayRect {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let r = self.0;
        write!(f, "RECT[{}, {}, {}, {}]", r.left(), r.top(), r.width(), r.height())
    }
}

/// Everything rendered in the top-left "Monitor Info" panel.
pub struct MonitorPanelData<'a> {
    pub index: usize,
    pub name: &'a str,
    pub is_primary: bool,
    pub adapter: &'a str,
    pub dpi: u32,
    pub bounds: ScreenRect,
    pub time_to_first_render: Option<Duration>,
    pub perf: &'a PerfTracker,
    pub target_period: Option<Duration>,
}

impl<'a> MonitorPanelData<'a> {
    pub fn write_lines(&self, out: &mut LineBuf) {
        out.reset();

        if self.is_primary {
            out.push(format_args!("{}: {} (PRIMARY)", self.index, self.name));
        } else {
            out.push(format_args!("{}: {}", self.index, self.name));
        }
        out.push(format_args!("{}", self.adapter));
        match self.target_period {
            Some(p) => {
                let hz = 1.0 / p.as_secs_f64();
                out.push(format_args!("dpi: {}  refresh: {:.0} Hz", self.dpi, hz));
            }
            None => out.push(format_args!("dpi: {}", self.dpi)),
        }
        out.push(format_args!("pos: {}", DisplayRect(self.bounds)));
        match self.time_to_first_render {
            Some(d) => out.push(format_args!("first render: {}", DisplayMs(d))),
            None => out.push(format_args!("first render: ...")),
        }
        out.push_empty();

        let overall = self.perf.stats(Series::Overall);
        let recent_ms = self.perf.recent_overall_avg().as_secs_f64() * 1000.0;
        let fps = if recent_ms > 0.0 { 1000.0 / recent_ms } else { 0.0 };
        let low1_fps = if overall.low1_ms > 0.0 { 1000.0 / overall.low1_ms } else { 0.0 };
        let session = self.perf.session();
        out.push(format_args!(
            "fps: {:04.0}  1%low: {:04.0}  dropped: {}  session: {}",
            fps,
            low1_fps,
            session.drops,
            DisplaySessionElapsed(session.started.elapsed()),
        ));

        let count = self.perf.sample_count();
        let secs = self.perf.sample_time_secs();
        if secs > 0.0 {
            out.push(format_args!("samples: {} ({:.0}s)", count, secs));
        } else {
            out.push(format_args!("samples: {}", count));
        }
        out.push_empty();

        let cpu_stats = self.perf.stats(Series::Cpu);
        write_stats_row(out, "cpu", cpu_stats);
        let gpu_stats = self.perf.stats(Series::Gpu);
        if gpu_stats.count > 0 {
            write_stats_row(out, "gpu", gpu_stats);
        } else {
            out.push(format_args!("gpu     n/a"));
        }

        // "overall" = total work time (cpu + gpu). Summing percentiles
        // isn't strictly correct but close enough for a debug readout.
        let work = if gpu_stats.count > 0 {
            PerfStats {
                p50_ms: cpu_stats.p50_ms + gpu_stats.p50_ms,
                p95_ms: cpu_stats.p95_ms + gpu_stats.p95_ms,
                p99_ms: cpu_stats.p99_ms + gpu_stats.p99_ms,
                low1_ms: cpu_stats.low1_ms + gpu_stats.low1_ms,
                count: cpu_stats.count.min(gpu_stats.count),
            }
        } else {
            cpu_stats
        };

        let budget_ms = self
            .target_period
            .map(|p| p.as_secs_f64() * 1000.0);
        let color = match budget_ms {
            Some(b) if b > 0.0 => budget_color((work.p50_ms / b) as f32),
            _ => COLOR_WHITE,
        };
        write_stats_row_colored(out, "overall", work, color);

        // Session footer
        out.push_empty();
        if session.seen[Series::Overall as usize] {
            let min = session.min_ms[Series::Overall as usize];
            let max = session.max_ms[Series::Overall as usize];
            out.push(format_args!(
                "session overall: min {:.2}  max {:.2}  frames {}",
                min, max, session.total_frames,
            ));
        } else {
            out.push(format_args!("session frames: {}", session.total_frames));
        }
    }
}

/// Everything rendered in the top-right "Primary Debug" panel.
pub struct PrimaryPanelData<'a> {
    pub startup: &'a StartupTimings,
    pub shown_time: Option<Duration>,
    pub zoom: f32,
    pub cursor: ScreenPointF,
    pub color_bgra: Option<[u8; 4]>,
    pub dragging: bool,
    pub captured: bool,
    pub selection: Option<ScreenRect>,
    pub hovered_window_title: Option<&'a str>,
    pub hovered_window_bounds: Option<ScreenRect>,
    pub hovered_window_index: Option<usize>,
    pub hovered_window_obstructed: bool,
}

impl<'a> PrimaryPanelData<'a> {
    pub fn write_lines(&self, out: &mut LineBuf) {
        out.reset();

        let total = self
            .shown_time
            .unwrap_or_else(|| self.startup.total());
        out.push(format_args!("startup: {} total", DisplayMs(total)));

        let init_end = self.startup.t_initialize.get();

        if let Some(d) = phase_between(init_end, None) {
            out.push(format_args!("  initialize:      {}", DisplayMs(d)));
        }

        let bg = &self.startup.background;
        let bg_start = init_end.unwrap_or(Duration::ZERO);
        let bg_gate = bg.gate();
        if let Some(d) = phase_between(bg_gate, Some(bg_start)) {
            out.push(format_args!("  background:      {}", DisplayMs(d)));
        }
        if let Some(d) = phase_between(bg.screenshot.get(), bg.screenshot_start.get()) {
            out.push(format_args!("    screenshot:    {}", DisplayMs(d)));
        }
        if let Some(d) = phase_between(bg.walker.get(), bg.walker_start.get()) {
            out.push(format_args!("    walker:        {}", DisplayMs(d)));
        }
        let multi = bg.workers.len() > 1;
        for (i, w) in bg.workers.iter().enumerate() {
            let suffix = if multi { format!("[{}]", i) } else { String::new() };
            let worker_start = w.prep_start.get().unwrap_or(bg_start);
            let worker_end = [w.render_prep.get(), w.upload.get(), w.surface_bind.get(), w.first_render.get()]
                .into_iter()
                .flatten()
                .max();
            if let Some(d) = phase_between(worker_end, Some(worker_start)) {
                out.push(format_args!("    worker{}:      {}", suffix, DisplayMs(d)));
            }
            if let Some(d) = phase_between(w.render_prep.get(), w.prep_start.get()) {
                out.push(format_args!("      prep:        {}", DisplayMs(d)));
            }
            if let Some(d) = phase_between(w.prep_adapter.get(), w.prep_start.get()) {
                out.push(format_args!("        adapter:   {}", DisplayMs(d)));
            }
            if let Some(d) = phase_between(w.prep_device.get(), w.prep_adapter.get()) {
                out.push(format_args!("        device:    {}", DisplayMs(d)));
            }
            if let Some(d) = phase_between(w.prep_pipelines.get(), w.prep_device.get()) {
                out.push(format_args!("        pipes:     {}", DisplayMs(d)));
            }
            if let Some(d) = phase_between(w.prep_ui_pipelines.get(), w.prep_pipelines.get()) {
                out.push(format_args!("        ui_pipe:   {}", DisplayMs(d)));
            }
            if let Some(d) = phase_between(w.prep_fonts.get(), w.prep_ui_pipelines.get()) {
                out.push(format_args!("        fonts:     {}", DisplayMs(d)));
            }
            if let Some(d) = phase_between(w.upload.get(), w.upload_start.get()) {
                out.push(format_args!("      upload:      {}", DisplayMs(d)));
            }
            if let Some(d) = phase_between(w.surface_bind.get(), w.surface_start.get()) {
                out.push(format_args!("      surface:     {}", DisplayMs(d)));
            }
            if let Some(d) = phase_between(w.first_render.get(), w.first_render_start.get()) {
                out.push(format_args!("      first render: {}", DisplayMs(d)));
            }
        }

        if let Some(d) = phase_between(self.startup.t_window_create.get(), self.startup.t_window_create_start.get()) {
            out.push(format_args!("  window create:  {}", DisplayMs(d)));
        }
        if let Some(d) = phase_between(self.shown_time, self.startup.t_show_start.get()) {
            out.push(format_args!("  shown:          {}", DisplayMs(d)));
        }
        out.push_empty();

        out.push(format_args!("zoom: {:.2}", self.zoom));
        out.push(format_args!("mouse: {:.2}, {:.2}", self.cursor.x, self.cursor.y));
        match self.color_bgra {
            Some([b, g, r, _]) => out.push(format_args!("color: rgb({}, {}, {})", r, g, b)),
            None => out.push(format_args!("color: rgb(-, -, -)")),
        }
        out.push(format_args!("dragging: {}", self.dragging));
        out.push(format_args!("captured: {}", self.captured));
        match self.selection {
            Some(s) => out.push(format_args!("select: {}", DisplayRect(s))),
            None => out.push(format_args!("select: (none)")),
        }

        if let Some(title) = self.hovered_window_title {
            out.push_empty();
            out.push(format_args!("wnd_title: {}", TruncatedTitle(title, 60)));
            if let Some(b) = self.hovered_window_bounds {
                out.push(format_args!("wnd_bounds: {}", DisplayRect(b)));
            }
            if let Some(idx) = self.hovered_window_index {
                out.push(format_args!("wnd_index: {}  obstructed: {}", idx, self.hovered_window_obstructed));
            }
        }
    }
}

/// Format helper that writes `s` straight into the formatter, truncating
/// to `max_chars` with an ellipsis.
struct TruncatedTitle<'a>(&'a str, usize);

impl<'a> std::fmt::Display for TruncatedTitle<'a> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let s = self.0;
        let max_chars = self.1;
        if s.chars().count() <= max_chars {
            f.write_str(s)
        } else {
            for ch in s.chars().take(max_chars.saturating_sub(1)) {
                f.write_char(ch)?;
            }
            f.write_char('…')
        }
    }
}
