use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

/// Nanosecond-resolution duration stored in an `AtomicU64`. Zero means
/// "not yet recorded". `set_once` uses compare-exchange so only the
/// first writer wins — safe for repeated `resumed()` calls on Wayland
/// and for concurrent workers. Per-cycle metrics rely on their whole
/// containing struct ([`CaptureTimings`]) being allocated fresh each
/// cycle rather than on any reset.
pub struct AtomicDuration(AtomicU64);

impl AtomicDuration {
    pub const fn new() -> Self {
        Self(AtomicU64::new(0))
    }

    pub fn set_once(&self, d: Duration) {
        let nanos = d.as_nanos() as u64;
        let _ = self
            .0
            .compare_exchange(0, nanos, Ordering::Release, Ordering::Relaxed);
    }

    pub fn get(&self) -> Option<Duration> {
        let v = self.0.load(Ordering::Acquire);
        if v == 0 {
            None
        } else {
            Some(Duration::from_nanos(v))
        }
    }
}

impl std::fmt::Debug for AtomicDuration {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{:?}", self.get())
    }
}

// ── Warm-up (once per process) ──────────────────────────────────────

/// Per-render-worker warm-up breakdown: Stage A (adapter, device,
/// pipelines, fonts) on the worker thread plus window/surface creation
/// on the main thread. All offsets from [`WarmupTimings::t_start`].
pub struct WarmupWorkerTimings {
    pub prep_start: AtomicDuration,
    pub render_prep: AtomicDuration,
    pub prep_adapter: AtomicDuration,
    pub prep_device: AtomicDuration,
    pub prep_pipelines: AtomicDuration,
    pub prep_ui_pipelines: AtomicDuration,
    pub prep_fonts: AtomicDuration,
    pub surface_start: AtomicDuration,
    pub surface_bind: AtomicDuration,
    /// When the worker received its window + surface handoff.
    pub handoff: AtomicDuration,
}

impl WarmupWorkerTimings {
    pub fn new() -> Self {
        Self {
            prep_start: AtomicDuration::new(),
            render_prep: AtomicDuration::new(),
            prep_adapter: AtomicDuration::new(),
            prep_device: AtomicDuration::new(),
            prep_pipelines: AtomicDuration::new(),
            prep_ui_pipelines: AtomicDuration::new(),
            prep_fonts: AtomicDuration::new(),
            surface_start: AtomicDuration::new(),
            surface_bind: AtomicDuration::new(),
            handoff: AtomicDuration::new(),
        }
    }

    fn gate(&self) -> Option<Duration> {
        [
            self.prep_start.get(),
            self.render_prep.get(),
            self.prep_adapter.get(),
            self.prep_device.get(),
            self.prep_pipelines.get(),
            self.prep_ui_pipelines.get(),
            self.prep_fonts.get(),
            self.surface_start.get(),
            self.surface_bind.get(),
            self.handoff.get(),
        ]
        .into_iter()
        .flatten()
        .max()
    }
}

/// One-time warm-up timings, anchored at process start. Recorded once —
/// in persistent mode this happens minutes or hours before the first
/// capture, so nothing per-cycle may anchor here (see [`CaptureTimings`]).
pub struct WarmupTimings {
    pub t_start: Instant,
    pub t_initialize: AtomicDuration,
    pub workers: Vec<WarmupWorkerTimings>,
    pub t_window_create_start: AtomicDuration,
    pub t_window_create: AtomicDuration,
}

impl WarmupTimings {
    pub fn new(t_start: Instant, worker_count: usize) -> Self {
        let mut workers = Vec::with_capacity(worker_count);
        for _ in 0..worker_count {
            workers.push(WarmupWorkerTimings::new());
        }
        Self {
            t_start,
            t_initialize: AtomicDuration::new(),
            workers,
            t_window_create_start: AtomicDuration::new(),
            t_window_create: AtomicDuration::new(),
        }
    }

    pub fn mark_initialize(&self) {
        self.t_initialize
            .set_once(self.t_start.elapsed());
    }

    pub fn mark_window_create_start(&self) {
        self.t_window_create_start
            .set_once(self.t_start.elapsed());
    }

    pub fn mark_window_create(&self) {
        self.t_window_create
            .set_once(self.t_start.elapsed());
    }

    /// Total warm-up time: the latest recorded phase.
    pub fn total(&self) -> Duration {
        let mut total = Duration::ZERO;
        for d in [
            self.t_initialize.get(),
            self.t_window_create_start.get(),
            self.t_window_create.get(),
        ]
        .into_iter()
        .flatten()
        {
            total = total.max(d);
        }
        for w in &self.workers {
            if let Some(d) = w.gate() {
                total = total.max(d);
            }
        }
        total
    }
}

// ── Per capture cycle ───────────────────────────────────────────────

/// Per-render-worker timings for one capture cycle: the swapchain
/// un-park, the snapshot upload (Stage B) and frame 0 (Stage C).
/// Offsets from [`CaptureTimings::t_start`].
pub struct CaptureWorkerTimings {
    /// Reconfiguring the parked (1×1) surface back to monitor size — a
    /// swapchain recreation, paid once per cycle on the show path.
    pub configure_start: AtomicDuration,
    pub configure: AtomicDuration,
    pub upload_start: AtomicDuration,
    pub upload: AtomicDuration,
    pub first_render_start: AtomicDuration,
    pub first_render: AtomicDuration,
}

impl CaptureWorkerTimings {
    pub fn new() -> Self {
        Self {
            configure_start: AtomicDuration::new(),
            configure: AtomicDuration::new(),
            upload_start: AtomicDuration::new(),
            upload: AtomicDuration::new(),
            first_render_start: AtomicDuration::new(),
            first_render: AtomicDuration::new(),
        }
    }

    fn gate(&self) -> Option<Duration> {
        [
            self.configure_start.get(),
            self.configure.get(),
            self.upload_start.get(),
            self.upload.get(),
            self.first_render_start.get(),
            self.first_render.get(),
        ]
        .into_iter()
        .flatten()
        .max()
    }
}

/// Timings for a single capture cycle, anchored at the moment the cycle's
/// per-capture jobs are spawned — the `show` command in persistent mode,
/// just before the screenshot job in one-shot mode. Allocated fresh per
/// cycle (which is what keeps `set_once` correct across cycles) and
/// distributed to the workers via `CycleParams`.
pub struct CaptureTimings {
    pub t_start: Instant,
    pub screenshot_start: AtomicDuration,
    pub screenshot: AtomicDuration,
    pub walker_start: AtomicDuration,
    pub walker: AtomicDuration,
    pub workers: Vec<CaptureWorkerTimings>,
    pub t_show_start: AtomicDuration,
    /// When every window of this cycle became visible.
    pub t_shown: AtomicDuration,
}

impl CaptureTimings {
    pub fn new(worker_count: usize) -> Self {
        let mut workers = Vec::with_capacity(worker_count);
        for _ in 0..worker_count {
            workers.push(CaptureWorkerTimings::new());
        }
        Self {
            t_start: Instant::now(),
            screenshot_start: AtomicDuration::new(),
            screenshot: AtomicDuration::new(),
            walker_start: AtomicDuration::new(),
            walker: AtomicDuration::new(),
            workers,
            t_show_start: AtomicDuration::new(),
            t_shown: AtomicDuration::new(),
        }
    }

    pub fn mark_show_start(&self) {
        self.t_show_start
            .set_once(self.t_start.elapsed());
    }

    pub fn mark_shown(&self) {
        self.t_shown.set_once(self.t_start.elapsed());
    }

    /// Total capture time so far: the latest recorded phase.
    pub fn total(&self) -> Duration {
        let mut total = Duration::ZERO;
        for d in [
            self.screenshot_start.get(),
            self.screenshot.get(),
            self.walker_start.get(),
            self.walker.get(),
            self.t_show_start.get(),
            self.t_shown.get(),
        ]
        .into_iter()
        .flatten()
        {
            total = total.max(d);
        }
        for w in &self.workers {
            if let Some(d) = w.gate() {
                total = total.max(d);
            }
        }
        total
    }
}
