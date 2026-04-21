//! Debug-panel content models.
//!
//! Two structs (`MonitorPanelData`, `PrimaryPanelData`) hold everything
//! the two debug panels display.

use std::fmt::{Arguments, Write};
use std::time::Duration;

use crate::geometry::{RectExt, ScreenPointF, ScreenRect};
use crate::ui::components::debug::perf::{PerfStats, PerfTracker, Series};
use crate::ui::components::debug::startup::StartupTimings;

/// Reusable line buffer. Holds `Vec<String>` across frames so the debug
/// panel's per-line `format!` calls can write into existing allocations
/// via `String::clear()` + `write!`. After the first few frames no
/// allocation happens unless the rendered text grows (which it doesn't
/// in steady state). Before each use the caller must call
/// [`LineBuf::reset`]; after writing it exposes the populated lines via
/// [`LineBuf::as_slice`].
#[derive(Default)]
pub struct LineBuf {
    lines: Vec<String>,
    len: usize,
}

impl LineBuf {
    pub fn new() -> Self {
        Self {
            lines: Vec::with_capacity(20),
            len: 0,
        }
    }

    pub fn reset(&mut self) {
        self.len = 0;
    }

    /// Append a formatted line. Reuses an existing `String`'s backing
    /// allocation when available; only the first few frames actually
    /// grow the pool.
    pub fn push(&mut self, args: Arguments<'_>) {
        if self.lines.len() <= self.len {
            self.lines.push(String::new());
        }
        let s = &mut self.lines[self.len];
        s.clear();
        let _ = s.write_fmt(args);
        self.len += 1;
    }

    pub fn push_empty(&mut self) {
        if self.lines.len() <= self.len {
            self.lines.push(String::new());
        }
        self.lines[self.len].clear();
        self.len += 1;
    }

    pub fn as_slice(&self) -> &[String] {
        &self.lines[..self.len]
    }
}

/// Render one stats row with a left-justified label column and
/// percentile / 1%-low columns. The caller guarantees `label` is <=
/// `LABEL_WIDTH`; wider labels wrap the columns but still read clearly.
fn write_stats_row(out: &mut LineBuf, label: &str, s: PerfStats) {
    const LABEL_WIDTH: usize = 7;
    if s.count == 0 {
        out.push(format_args!("{:<width$} n/a", label, width = LABEL_WIDTH));
        return;
    }
    out.push(format_args!(
        "{:<width$} p50 {:>5.2}  p95 {:>5.2}  p99 {:>5.2}  low1 {:>5.2} ({})",
        label,
        s.p50_ms,
        s.p95_ms,
        s.p99_ms,
        s.low1_ms,
        s.count,
        width = LABEL_WIDTH,
    ));
}

/// Format helper: wall-clock duration as "x.xx ms" (two decimals).
/// Used inside `format_args!` via `DisplayMs(d)` so the calling
/// `write!` assembles straight into the target string without an
/// intermediate heap allocation.
struct DisplayMs(Duration);

impl std::fmt::Display for DisplayMs {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{:.2}ms", self.0.as_secs_f64() * 1000.0)
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
    /// Target frame period from the monitor's refresh rate, used to show
    /// the effective refresh in the header. `None` when unknown.
    pub target_period: Option<Duration>,
}

impl<'a> MonitorPanelData<'a> {
    /// Write one line per panel entry into `out`. Reuses existing
    /// allocations; on the steady-state frame no heap allocations happen.
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
        // Headline FPS uses only the recent tail so the number feels
        // live; the full-window average shows up in the stats row.
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
        out.push_empty();

        // cpu  = CPU work from "drawable acquired" to "frame.present()
        //        returned" (= draw + present). The time the CPU is
        //        actually doing work for this frame.
        // gpu  = GPU execution time for this frame's commands.
        // With `frame_latency: 1` the next frame's drawable can't be
        // acquired until this frame's GPU work finishes, so the
        // critical path is `cpu + gpu`; the budget to stay vsync-locked
        // at 60 Hz is `cpu + gpu ≤ 16.67 ms`, *per frame*, every frame.
        // overall = wall-clock frame time (= 1/fps). The gap between
        // `cpu + gpu` and `overall` is vsync slack (what used to show
        // up as `wait`).
        write_stats_row(out, "cpu", self.perf.stats(Series::Cpu));
        let gpu = self.perf.stats(Series::Gpu);
        if gpu.count > 0 {
            write_stats_row(out, "gpu", gpu);
        } else {
            out.push(format_args!("gpu     n/a"));
        }
        write_stats_row(out, "overall", overall);

        // Session footer: per-series lifetime min/max for the overall
        // series, plus total frame count.
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
}

impl<'a> PrimaryPanelData<'a> {
    pub fn write_lines(&self, out: &mut LineBuf) {
        out.reset();

        let total = self
            .shown_time
            .unwrap_or_else(|| self.startup.total());
        out.push(format_args!("startup: {} total", DisplayMs(total)));

        if let Some(d) = self.startup.t_initialize.get() {
            out.push(format_args!("  initialize:    {}", DisplayMs(d)));
        }

        let bg = &self.startup.background;
        if let Some(gate) = bg.gate() {
            out.push(format_args!("  background:    {}", DisplayMs(gate)));
        }
        if let Some(d) = bg.screenshot.get() {
            out.push(format_args!("    screenshot:  {}", DisplayMs(d)));
        }
        if let Some(d) = bg.walker.get() {
            out.push(format_args!("    walker:      {}", DisplayMs(d)));
        }
        let multi = bg.workers.len() > 1;
        for (i, w) in bg.workers.iter().enumerate() {
            if let Some(d) = w.render_prep.get() {
                if multi {
                    out.push(format_args!("    prep[{}]:     {}", i, DisplayMs(d)));
                } else {
                    out.push(format_args!("    prep:        {}", DisplayMs(d)));
                }
            }
            if let Some(d) = w.upload.get() {
                if multi {
                    out.push(format_args!("    upload[{}]:   {}", i, DisplayMs(d)));
                } else {
                    out.push(format_args!("    upload:      {}", DisplayMs(d)));
                }
            }
            if let Some(d) = w.surface_bind.get() {
                if multi {
                    out.push(format_args!("    surface[{}]:  {}", i, DisplayMs(d)));
                } else {
                    out.push(format_args!("    surface:     {}", DisplayMs(d)));
                }
            }
        }

        if let Some(d) = self.startup.t_window_create.get() {
            out.push(format_args!("  window create: {}", DisplayMs(d)));
        }
        let first_render = bg
            .workers
            .iter()
            .filter_map(|w| w.first_render.get())
            .max();
        if let Some(d) = first_render {
            out.push(format_args!("  first render:  {}", DisplayMs(d)));
        }
        if let Some(d) = self.shown_time {
            out.push(format_args!("  shown:         {}", DisplayMs(d)));
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
        }
    }
}

/// Format helper that writes `s` straight into the formatter, truncating
/// to `max_chars` with an ellipsis. Avoids the intermediate `String`
/// allocation that the old `truncate_display` returned.
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
