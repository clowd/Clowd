//! Debug-panel content models.
//!
//! Two structs (`MonitorPanelData`, `PrimaryPanelData`) hold everything
//! the two debug panels display.

use std::time::Duration;

use crate::geometry::{RectExt, ScreenPointF, ScreenRect};
use crate::ui::components::debug::perf::{PerfStats, PerfTracker, Series};
use crate::ui::components::debug::startup::StartupTimings;

fn fmt_rect(r: ScreenRect) -> String {
    format!("RECT[{}, {}, {}, {}]", r.left(), r.top(), r.width(), r.height())
}

/// Render one stats row with a left-justified label column and
/// percentile / 1%-low columns. The caller guarantees `label` is <=
/// `LABEL_WIDTH`; wider labels wrap the columns but still read clearly.
fn fmt_stats_row(label: &str, s: PerfStats) -> String {
    const LABEL_WIDTH: usize = 7;
    if s.count == 0 {
        return format!("{:<width$} n/a", label, width = LABEL_WIDTH);
    }
    format!(
        "{:<width$} p50 {:>5.2}  p95 {:>5.2}  p99 {:>5.2}  low1 {:>5.2} ({})",
        label,
        s.p50_ms,
        s.p95_ms,
        s.p99_ms,
        s.low1_ms,
        s.count,
        width = LABEL_WIDTH,
    )
}

fn fmt_ms(d: Duration) -> String {
    format!("{:.2}ms", d.as_secs_f64() * 1000.0)
}

fn fmt_session_elapsed(d: Duration) -> String {
    let secs = d.as_secs();
    let h = secs / 3600;
    let m = (secs / 60) % 60;
    let s = secs % 60;
    if h > 0 {
        format!("{}h{:02}m{:02}s", h, m, s)
    } else if m > 0 {
        format!("{}m{:02}s", m, s)
    } else {
        format!("{}s", s)
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
    /// Build one `String` per panel line.
    pub fn lines(&self) -> Vec<String> {
        let mut out = Vec::with_capacity(16);

        let header = if self.is_primary {
            format!("{}: {} (PRIMARY)", self.index, self.name)
        } else {
            format!("{}: {}", self.index, self.name)
        };
        out.push(header);
        out.push(self.adapter.to_string());
        let dpi_line = match self.target_period {
            Some(p) => {
                let hz = 1.0 / p.as_secs_f64();
                format!("dpi: {}  refresh: {:.0} Hz", self.dpi, hz)
            }
            None => format!("dpi: {}", self.dpi),
        };
        out.push(dpi_line);
        out.push(format!("pos: {}", fmt_rect(self.bounds)));
        let first_render_line = match self.time_to_first_render {
            Some(d) => format!("first render: {}", fmt_ms(d)),
            None => "first render: ...".to_string(),
        };
        out.push(first_render_line);
        out.push(String::new());

        let overall = self.perf.stats(Series::Overall);
        // Headline FPS uses only the recent tail so the number feels
        // live; the full-window average shows up in the stats row.
        let recent_ms = self.perf.recent_overall_avg().as_secs_f64() * 1000.0;
        let fps = if recent_ms > 0.0 { 1000.0 / recent_ms } else { 0.0 };
        let low1_fps = if overall.low1_ms > 0.0 { 1000.0 / overall.low1_ms } else { 0.0 };
        let session = self.perf.session();
        out.push(format!(
            "fps: {:04.0}  1%low: {:04.0}  dropped: {}  session: {}",
            fps,
            low1_fps,
            session.drops,
            fmt_session_elapsed(session.started.elapsed()),
        ));
        out.push(String::new());

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
        out.push(fmt_stats_row("cpu", self.perf.stats(Series::Cpu)));
        let gpu = self.perf.stats(Series::Gpu);
        if gpu.count > 0 {
            out.push(fmt_stats_row("gpu", gpu));
        } else {
            out.push("gpu     n/a".to_string());
        }
        out.push(fmt_stats_row("overall", overall));

        // Session footer: per-series lifetime min/max for the overall
        // series, plus total frame count.
        out.push(String::new());
        if session.seen[Series::Overall as usize] {
            let min = session.min_ms[Series::Overall as usize];
            let max = session.max_ms[Series::Overall as usize];
            out.push(format!(
                "session overall: min {:.2}  max {:.2}  frames {}",
                min, max, session.total_frames,
            ));
        } else {
            out.push(format!("session frames: {}", session.total_frames));
        }
        out
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
    pub fn lines(&self) -> Vec<String> {
        let mut out = Vec::with_capacity(16);

        let total = self
            .shown_time
            .unwrap_or_else(|| self.startup.total());
        out.push(format!("startup: {} total", fmt_ms(total)));

        let mut prev = Duration::ZERO;
        let mut push_phase = |out: &mut Vec<String>, label: &str, offset: Duration| {
            let delta = offset.saturating_sub(prev);
            out.push(format!("  {} +{}", label, fmt_ms(delta)));
            prev = offset;
        };
        if let Some(d) = self.startup.t_initialize {
            push_phase(&mut out, "initialize    ", d);
        }
        if let Some(d) = self.startup.t_desktop_search {
            push_phase(&mut out, "desktop search", d);
        }
        if let Some(d) = self.startup.t_window_create {
            push_phase(&mut out, "window create ", d);
        }
        if let Some(d) = self.shown_time {
            push_phase(&mut out, "shown         ", d);
        }
        out.push(String::new());

        out.push(format!("zoom: {:.2}", self.zoom));
        out.push(format!("mouse: {:.2}, {:.2}", self.cursor.x, self.cursor.y));
        let color_line = match self.color_bgra {
            Some([b, g, r, _]) => format!("color: rgb({}, {}, {})", r, g, b),
            None => "color: rgb(-, -, -)".to_string(),
        };
        out.push(color_line);
        out.push(format!("dragging: {}", self.dragging));
        out.push(format!("captured: {}", self.captured));
        let sel_line = match self.selection {
            Some(s) => format!("select: {}", fmt_rect(s)),
            None => "select: (none)".to_string(),
        };
        out.push(sel_line);

        if let Some(title) = self.hovered_window_title {
            out.push(String::new());
            out.push(format!("wnd_title: {}", truncate_display(title, 60)));
            if let Some(b) = self.hovered_window_bounds {
                out.push(format!("wnd_bounds: {}", fmt_rect(b)));
            }
        }

        out
    }
}

fn truncate_display(s: &str, max_chars: usize) -> String {
    if s.chars().count() <= max_chars {
        s.to_string()
    } else {
        let mut out: String = s
            .chars()
            .take(max_chars.saturating_sub(1))
            .collect();
        out.push('…');
        out
    }
}
