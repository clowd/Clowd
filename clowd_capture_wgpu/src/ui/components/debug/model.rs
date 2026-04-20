//! Debug-panel content models.
//!
//! Two structs (`MonitorPanelData`, `PrimaryPanelData`) hold everything
//! the two debug panels display, with `Display` impls that produce the
//! line-by-line text exactly matching the C++ format strings at
//! `DxScreenCapture.cpp:915-977`.

use std::time::Duration;

use crate::geometry::{RectExt, ScreenPointF, ScreenRect};
use crate::ui::components::debug::perf::{PerfStats, PerfTracker};
use crate::ui::components::debug::startup::StartupTimings;

fn fmt_rect(r: ScreenRect) -> String {
    format!("RECT[{}, {}, {}, {}]", r.left(), r.top(), r.width(), r.height())
}

fn fmt_stats(label: &str, s: PerfStats) -> String {
    format!(
        "{} avg: {:.2}, min: {:.2}, max: {:.2} // sd: {:.2} ({} samples)",
        label, s.avg_ms, s.min_ms, s.max_ms, s.stdev_ms, s.count
    )
}

fn fmt_ms(d: Duration) -> String {
    format!("{:.2}ms", d.as_secs_f64() * 1000.0)
}

/// Everything rendered in the top-left "Monitor Info" panel, built fresh
/// on every frame the panel is visible. Field order matches the C++
/// layout at `DxScreenCapture.cpp:915-933`.
pub struct MonitorPanelData<'a> {
    pub index: usize,
    pub name: &'a str,
    pub is_primary: bool,
    pub adapter: &'a str,
    pub dpi: u32,
    pub bounds: ScreenRect,
    pub time_to_render: Duration,
    pub perf: &'a PerfTracker,
}

impl<'a> MonitorPanelData<'a> {
    /// Build one `String` per panel line. Order: header, adapter, dpi,
    /// pos, blank, time_to_render, fps/dropped, blank, 4 perf stat blocks.
    pub fn lines(&self) -> Vec<String> {
        let mut out = Vec::with_capacity(12);

        let header = if self.is_primary {
            format!("{}: {} (PRIMARY)", self.index, self.name)
        } else {
            format!("{}: {}", self.index, self.name)
        };
        out.push(header);
        out.push(self.adapter.to_string());
        out.push(format!("dpi: {}", self.dpi));
        out.push(format!("pos: {}", fmt_rect(self.bounds)));
        out.push(String::new());
        out.push(format!("time_to_render: {}", fmt_ms(self.time_to_render)));

        let overall = self.perf.stats(|s| s.overall);
        let fps = if overall.avg_ms > 0.0 { 1000.0 / overall.avg_ms } else { 0.0 };
        // C++ shows `fps: 0120, dropped: 0`. We don't track dropped
        // frames today (DXGI GetFrameStatistics would be invasive through
        // wgpu); placeholder matches the format for visual parity.
        out.push(format!("fps: {:04.0}, dropped: {}", fps, 0));
        out.push(String::new());

        out.push(fmt_stats("wait", self.perf.stats(|s| s.wait)));
        out.push(fmt_stats("draw", self.perf.stats(|s| s.draw)));
        out.push(fmt_stats("present", self.perf.stats(|s| s.present)));
        out.push(fmt_stats("overall", overall));
        out
    }
}

/// Everything rendered in the top-right "Primary Debug" panel. Shown only
/// on the monitor containing the virtual cursor. Mirrors
/// `DxScreenCapture.cpp:935-977`.
pub struct PrimaryPanelData<'a> {
    pub startup: &'a StartupTimings,
    /// Wall-clock offset (from app `main()` entry) at which every window
    /// became visible atomically — the single "user can see anything"
    /// moment, same value on every display. `None` until the show call
    /// has fired (only observable on the vanishing first few frames).
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

        // Startup block — matches the C++ `startup: XX.XXms total` +
        // indented sub-lines.
        out.push(format!("startup: {} total", fmt_ms(self.startup.total())));
        if let Some(d) = self.startup.t_initialize {
            out.push(format!("  - {} (initialize)", fmt_ms(d)));
        }
        if let Some(d) = self.startup.t_desktop_search {
            out.push(format!("  - {} (desktop search)", fmt_ms(d)));
        }
        if let Some(d) = self.startup.t_window_create {
            out.push(format!("  - {} (window create)", fmt_ms(d)));
        }
        // "All windows visible" moment — the single instant at which the
        // user can first see anything. Captured on the main thread right
        // after `show_windows_atomically`. Same value on every display.
        let shown_line = match self.shown_time {
            Some(d) => format!("  - {} (shown)", fmt_ms(d)),
            None => "  - ... (shown)".to_string(),
        };
        out.push(shown_line);
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

/// Clip a string for on-screen display. Windows titles can be long
/// ("Mozilla Firefox — file:///very/long/path/…"); chopping them avoids
/// pushing the panel past its max width and wrapping.
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
