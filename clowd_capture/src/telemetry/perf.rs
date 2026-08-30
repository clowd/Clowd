//! Per-render-thread frame timing tracker.
//!
//! Four CPU-side series plus one optional GPU-side series:
//!   * `wait` — time blocked on `get_current_texture()` (DXGI waitable
//!     object on Windows).
//!   * `draw` — CPU time building the encoder and submitting to the queue.
//!   * `present` — time inside `frame.present()`.
//!   * `overall` — wall-clock gap between consecutive loop iterations.
//!     Reciprocal ≈ FPS. Also the series the dropped-frame detector uses.
//!   * `gpu` — actual GPU execution time (desktop pass + UI pass).
//!     Permanently `None` today: both backends' `GpuTimings` are stubs
//!     (no timestamp-query implementation yet), so the series only fills
//!     in once a backend grows one.
//!
//! Rolling window: dynamically sized to cover ~10 s of wall-clock time
//! at the monitor's refresh rate (600 samples @ 60 Hz, 1440 @ 144 Hz).
//! The instantaneous `fps` / `time_to_render` readouts use only the last
//! `RECENT_WINDOW` samples so the number feels live; stats (percentiles,
//! 1% low, min/max) use the full window. Session aggregates run for the
//! lifetime of the tracker.

use std::cell::RefCell;
use std::collections::VecDeque;
use std::time::{Duration, Instant};

/// Target sample window duration in seconds. The actual buffer size
/// is computed from `refresh_hz * TARGET_SAMPLE_SECONDS` so both
/// 60 Hz and 144 Hz monitors cover roughly the same wall-clock span.
const TARGET_SAMPLE_SECONDS: f64 = 10.0;

/// Fallback window size when the refresh rate is unknown (~10 s @ 60 Hz).
const DEFAULT_PERF_WINDOW: usize = 600;

/// Short tail used for the headline `fps` readout so it reacts within
/// a second.
pub const RECENT_WINDOW: usize = 60;

/// Which timing series a stats query is about. Matches the order of
/// `SessionStats::min_ms` / `max_ms`.
///
/// `Cpu` is a derived series: projection returns `draw + present`, the
/// CPU work from "drawable acquired" to "frame.present() returned" —
/// i.e. the CPU critical-path contribution to the `cpu + gpu ≤ vsync`
/// budget that governs 60 fps under `frame_latency: 1`. `Wait` and its
/// components are kept for diagnostics but aren't displayed by default
/// because `wait` is slack absorbed by vsync, not actionable work.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Series {
    Wait = 0,
    Draw = 1,
    Present = 2,
    Overall = 3,
    Gpu = 4,
    Cpu = 5,
}

pub const SERIES_COUNT: usize = 6;

/// One per-frame timing record.
#[derive(Debug, Clone, Copy)]
pub struct PerfSample {
    pub wait: Duration,
    pub draw: Duration,
    pub present: Duration,
    pub overall: Duration,
    /// Filled in by the GPU timestamp-query readback path, which runs
    /// 1–2 frames behind the CPU sample. `None` means "not yet readback"
    /// or "backend doesn't support timestamp queries".
    pub gpu: Option<Duration>,
}

impl PerfSample {
    fn project(&self, series: Series) -> Option<Duration> {
        match series {
            Series::Wait => Some(self.wait),
            Series::Draw => Some(self.draw),
            Series::Present => Some(self.present),
            Series::Overall => Some(self.overall),
            Series::Gpu => self.gpu,
            Series::Cpu => Some(self.draw + self.present),
        }
    }
}

/// Summary statistics for one timing series over the rolling window.
/// All values in milliseconds. Percentiles tell the full distribution
/// story — mean isn't included because a long-tailed frame-time
/// distribution makes it a misleading summary (one 100 ms spike shifts
/// the mean visibly while leaving p50 untouched). Session-wide
/// aggregates live on `SessionStats`.
#[derive(Debug, Clone, Copy, Default)]
pub struct PerfStats {
    pub p50_ms: f64,
    pub p95_ms: f64,
    pub p99_ms: f64,
    /// Mean of the worst 1 % of samples. For the `overall` series the
    /// reciprocal of this is the "1 % low FPS" figure.
    pub low1_ms: f64,
    pub count: usize,
}

/// Session-wide aggregates — never reset while the tracker is alive.
#[derive(Debug, Clone, Copy)]
pub struct SessionStats {
    pub started: Instant,
    pub total_frames: u64,
    pub drops: u64,
    pub min_ms: [f64; SERIES_COUNT],
    pub max_ms: [f64; SERIES_COUNT],
    /// Whether each series has ever seen a sample. Separate from
    /// min/max because 0 is a legitimate duration on fast paths
    /// (`wait` on non-DX12 backends reads near-zero) and we don't want
    /// the sentinel to mask it.
    pub seen: [bool; SERIES_COUNT],
}

impl SessionStats {
    fn new() -> Self {
        Self {
            started: Instant::now(),
            total_frames: 0,
            drops: 0,
            min_ms: [f64::INFINITY; SERIES_COUNT],
            max_ms: [f64::NEG_INFINITY; SERIES_COUNT],
            seen: [false; SERIES_COUNT],
        }
    }

    fn record_value(&mut self, series: Series, ms: f64) {
        let i = series as usize;
        self.seen[i] = true;
        if ms < self.min_ms[i] {
            self.min_ms[i] = ms;
        }
        if ms > self.max_ms[i] {
            self.max_ms[i] = ms;
        }
    }
}

/// Stats for every series, computed in a single batch so the whole
/// panel can share one pass over the ring buffer per update.
#[derive(Debug, Clone, Copy, Default)]
pub struct FrameStats {
    pub wait: PerfStats,
    pub draw: PerfStats,
    pub present: PerfStats,
    pub cpu: PerfStats,
    pub gpu: PerfStats,
    pub overall: PerfStats,
}

/// Number of series the stats cache rotates through. One series is
/// recomputed per frame, so each series gets a refresh every
/// `STATS_ROTATION_LEN` frames (= 10 Hz at 60 fps, same effective UI
/// update rate as the old "refresh everything every 6 frames" model).
/// The win is that per-frame CPU cost is ~N× flatter: no single
/// "cache refresh" frame that pays for 6 sorts at once.
const STATS_ROTATION_LEN: u64 = 6;

/// Diagnostic switch. When `false`, `stats()` stops computing anything
/// after the first frame — every call returns the single snapshot
/// taken at bootstrap, forever. Use this to verify whether the
/// rotating percentile work is actually the source of a per-frame
/// cost: toggle off and see if `cpu` frame time stops growing as the
/// sample window fills. Production value: `true`.
const STATS_ROTATION_ENABLED: bool = true;

struct CachedFrameStats {
    /// `session.total_frames` value the last time we advanced the
    /// rotation. Used to skip re-computing within the same frame when
    /// `stats()` is called multiple times.
    last_advance_frame: u64,
    all: FrameStats,
}

pub struct PerfTracker {
    samples: VecDeque<PerfSample>,
    session: SessionStats,
    /// Target frame period derived from `refresh_hz`. Used to decide
    /// whether a frame's `overall` duration counts as a drop. `None` on
    /// render threads where the refresh rate wasn't supplied.
    target_period: Option<Duration>,
    /// Dynamic buffer capacity computed from refresh rate ×
    /// `TARGET_SAMPLE_SECONDS`.
    window_size: usize,
    /// Cached percentile/1%-low stats. Each entry is refreshed on a
    /// 6-frame rotation (one series per frame) so same-frame `stats()`
    /// calls are warm-cache hits.
    cached_stats: RefCell<Option<CachedFrameStats>>,
    /// Reusable scratch buffer for percentile computation.
    scratch: RefCell<Vec<f64>>,
}

impl PerfTracker {
    /// Tracker without refresh-rate info; dropped-frame counting is
    /// disabled. Prefer `new_with_refresh`.
    pub fn new() -> Self {
        Self {
            samples: VecDeque::with_capacity(DEFAULT_PERF_WINDOW),
            session: SessionStats::new(),
            target_period: None,
            window_size: DEFAULT_PERF_WINDOW,
            cached_stats: RefCell::new(None),
            scratch: RefCell::new(Vec::with_capacity(DEFAULT_PERF_WINDOW)),
        }
    }

    /// Tracker wired with a refresh rate. `hz <= 0` is treated as missing.
    pub fn new_with_refresh(hz: f32) -> Self {
        let (target_period, window_size) = if hz > 0.0 {
            let period = Duration::from_secs_f64(1.0 / hz as f64);
            let win = (hz as f64 * TARGET_SAMPLE_SECONDS).round() as usize;
            (Some(period), win.max(RECENT_WINDOW))
        } else {
            (None, DEFAULT_PERF_WINDOW)
        };
        Self {
            samples: VecDeque::with_capacity(window_size),
            session: SessionStats::new(),
            target_period,
            window_size,
            cached_stats: RefCell::new(None),
            scratch: RefCell::new(Vec::with_capacity(window_size)),
        }
    }

    pub fn record(&mut self, sample: PerfSample) {
        if self.samples.len() == self.window_size {
            self.samples.pop_front();
        }
        self.samples.push_back(sample);
        self.session.total_frames = self.session.total_frames.saturating_add(1);

        for &series in &[Series::Wait, Series::Draw, Series::Present, Series::Cpu, Series::Overall] {
            if let Some(d) = sample.project(series) {
                self.session
                    .record_value(series, d.as_secs_f64() * 1000.0);
            }
        }
        if let Some(g) = sample.gpu {
            self.session
                .record_value(Series::Gpu, g.as_secs_f64() * 1000.0);
        }

        // Dropped-frame detection: overall > 1.5 × target period. Skip
        // the very first recorded sample — `overall` then reflects the
        // gap since `last_iter` was seeded at loop entry, which is
        // unrelated to render cadence.
        if let Some(period) = self.target_period {
            if self.session.total_frames > 1 && sample.overall > period + period / 2 {
                self.session.drops = self.session.drops.saturating_add(1);
            }
        }
    }

    /// Attach a GPU duration to the oldest recorded sample that doesn't
    /// yet have one. Called from the timestamp-query readback path.
    ///
    /// GPU readbacks arrive in submit order, which matches the order CPU
    /// samples were pushed onto the ring, so "oldest pending" produces
    /// the correct pairing without tracking ages explicitly. Samples
    /// whose GPU time never landed before they scrolled off the ring are
    /// simply lost — they become `None` for their entire lifetime.
    pub fn backfill_next_gpu(&mut self, gpu: Duration) {
        for sample in self.samples.iter_mut() {
            if sample.gpu.is_none() {
                sample.gpu = Some(gpu);
                self.session
                    .record_value(Series::Gpu, gpu.as_secs_f64() * 1000.0);
                return;
            }
        }
    }

    /// Windowed stats for one series. Backed by a round-robin cache
    /// that recomputes *one* series per frame, so the per-frame CPU
    /// cost stays flat instead of a 6× spike every 100 ms.
    pub fn stats(&self, series: Series) -> PerfStats {
        self.advance_rotation_if_new_frame();
        let cache = self.cached_stats.borrow();
        let all = &cache
            .as_ref()
            .expect("cache populated above")
            .all;
        match series {
            Series::Wait => all.wait,
            Series::Draw => all.draw,
            Series::Present => all.present,
            Series::Cpu => all.cpu,
            Series::Gpu => all.gpu,
            Series::Overall => all.overall,
        }
    }

    /// Advance the stats rotation by one series for this frame. If
    /// `stats()` is called multiple times within the same frame the
    /// cache only advances once, because `last_advance_frame` already
    /// equals `total_frames` after the first call.
    ///
    /// On the very first call (cache empty) we bootstrap every series
    /// at once so the panel doesn't render blanks while the rotation
    /// primes — this is a one-time cost paid when the debug panel is
    /// first opened.
    fn advance_rotation_if_new_frame(&self) {
        let current = self.session.total_frames;

        let (needs_bootstrap, needs_advance) = {
            let cache = self.cached_stats.borrow();
            match cache.as_ref() {
                None => (true, false),
                Some(c) if c.last_advance_frame != current => (false, true),
                Some(_) => (false, false),
            }
        };

        if needs_bootstrap {
            let fresh = FrameStats {
                wait: self.compute_series(Series::Wait),
                draw: self.compute_series(Series::Draw),
                present: self.compute_series(Series::Present),
                cpu: self.compute_series(Series::Cpu),
                gpu: self.compute_series(Series::Gpu),
                overall: self.compute_series(Series::Overall),
            };
            *self.cached_stats.borrow_mut() = Some(CachedFrameStats {
                last_advance_frame: current,
                all: fresh,
            });
        } else if needs_advance && STATS_ROTATION_ENABLED {
            const ROTATION: [Series; STATS_ROTATION_LEN as usize] = [
                Series::Wait,
                Series::Draw,
                Series::Present,
                Series::Cpu,
                Series::Gpu,
                Series::Overall,
            ];
            let picked = ROTATION[(current % STATS_ROTATION_LEN) as usize];
            let fresh = self.compute_series(picked);

            let mut cache = self.cached_stats.borrow_mut();
            let entry = cache
                .as_mut()
                .expect("not-bootstrap implies Some");
            entry.last_advance_frame = current;
            match picked {
                Series::Wait => entry.all.wait = fresh,
                Series::Draw => entry.all.draw = fresh,
                Series::Present => entry.all.present = fresh,
                Series::Cpu => entry.all.cpu = fresh,
                Series::Gpu => entry.all.gpu = fresh,
                Series::Overall => entry.all.overall = fresh,
            }
        }
    }

    /// Actual percentile / 1%-low computation for one series. Never
    /// called directly from outside the tracker — always go through
    /// `stats()` so results are cached.
    fn compute_series(&self, series: Series) -> PerfStats {
        let mut scratch = self.scratch.borrow_mut();
        scratch.clear();
        for s in &self.samples {
            if let Some(d) = s.project(series) {
                scratch.push(d.as_secs_f64() * 1000.0);
            }
        }
        let count = scratch.len();
        if count == 0 {
            return PerfStats::default();
        }

        // `total_cmp` is a direct bit-level f64 ordering — avoids the
        // `Option<Ordering>` round-trip that `partial_cmp` forces and
        // shaves comparison cost on the hot sort.
        scratch.sort_unstable_by(|a, b| a.total_cmp(b));

        let pick = |p: f64| -> f64 {
            let idx = ((count as f64) * p).floor() as usize;
            scratch[idx.min(count - 1)]
        };
        let p50 = pick(0.50);
        let p95 = pick(0.95);
        let p99 = pick(0.99);

        // Worst 1 % (tail of sorted asc). At least 1 sample.
        let tail_n = count.div_ceil(100).max(1);
        let tail_start = count - tail_n;
        let low1 = scratch[tail_start..].iter().sum::<f64>() / tail_n as f64;

        PerfStats {
            p50_ms: p50,
            p95_ms: p95,
            p99_ms: p99,
            low1_ms: low1,
            count,
        }
    }

    /// Average `overall` duration across the last `RECENT_WINDOW` samples
    /// — drives the headline `fps` number. Matches the feel of the old
    /// 120-sample window.
    pub fn recent_overall_avg(&self) -> Duration {
        let n = self.samples.len().min(RECENT_WINDOW);
        if n == 0 {
            return Duration::ZERO;
        }
        let mut sum = 0u128;
        for s in self.samples.iter().rev().take(n) {
            sum += s.overall.as_nanos();
        }
        Duration::from_nanos((sum / n as u128) as u64)
    }

    /// Latest sample in the window (most recent frame). `None` before any
    /// `record()` call.
    #[allow(dead_code)]
    pub fn latest(&self) -> Option<PerfSample> {
        self.samples.back().copied()
    }

    /// Read-only access to session aggregates for the panel footer.
    pub fn session(&self) -> &SessionStats {
        &self.session
    }

    /// Target frame period from the monitor's refresh rate, if known.
    /// Used by the sparkline to draw a reference line at budget.
    pub fn target_period(&self) -> Option<Duration> {
        self.target_period
    }

    /// Iterator over the samples newest-first. Used by the sparkline to
    /// render the most-recent N bars.
    pub fn samples_newest_first(&self) -> impl Iterator<Item = &PerfSample> {
        self.samples.iter().rev()
    }

    /// Maximum buffer capacity (dynamic, based on refresh rate).
    pub fn window_size(&self) -> usize {
        self.window_size
    }

    /// Number of samples currently in the buffer.
    pub fn sample_count(&self) -> usize {
        self.samples.len()
    }

    /// Approximate wall-clock seconds the current samples cover.
    pub fn sample_time_secs(&self) -> f64 {
        match self.target_period {
            Some(p) => self.samples.len() as f64 * p.as_secs_f64(),
            None => 0.0,
        }
    }
}

impl Default for PerfTracker {
    fn default() -> Self {
        Self::new()
    }
}
