use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

/// Nanosecond-resolution duration stored in an `AtomicU64`. Zero means
/// "not yet recorded". `set_once` uses compare-exchange so only the
/// first writer wins — safe for repeated `resumed()` calls on Wayland
/// and for concurrent workers.
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

/// Per-render-worker timing breakdown recorded during stages A/B/C.
pub struct WorkerTimings {
    pub prep_start: AtomicDuration,
    pub render_prep: AtomicDuration,
    pub prep_adapter: AtomicDuration,
    pub prep_device: AtomicDuration,
    pub prep_pipelines: AtomicDuration,
    pub prep_ui_pipelines: AtomicDuration,
    pub prep_fonts: AtomicDuration,
    pub upload_start: AtomicDuration,
    pub upload: AtomicDuration,
    pub surface_start: AtomicDuration,
    pub surface_bind: AtomicDuration,
    pub first_render_start: AtomicDuration,
    pub first_render: AtomicDuration,
}

impl WorkerTimings {
    pub fn new() -> Self {
        Self {
            prep_start: AtomicDuration::new(),
            render_prep: AtomicDuration::new(),
            prep_adapter: AtomicDuration::new(),
            prep_device: AtomicDuration::new(),
            prep_pipelines: AtomicDuration::new(),
            prep_ui_pipelines: AtomicDuration::new(),
            prep_fonts: AtomicDuration::new(),
            upload_start: AtomicDuration::new(),
            upload: AtomicDuration::new(),
            surface_start: AtomicDuration::new(),
            surface_bind: AtomicDuration::new(),
            first_render_start: AtomicDuration::new(),
            first_render: AtomicDuration::new(),
        }
    }
}

/// Parallel-phase timings. All durations are offsets from `t_start` so
/// they're directly comparable across threads. The group's gate time
/// is `max(all set children)`.
pub struct BackgroundGroup {
    pub screenshot_start: AtomicDuration,
    pub screenshot: AtomicDuration,
    pub walker_start: AtomicDuration,
    pub walker: AtomicDuration,
    pub workers: Vec<WorkerTimings>,
}

impl BackgroundGroup {
    pub fn new(worker_count: usize) -> Self {
        let mut workers = Vec::with_capacity(worker_count);
        for _ in 0..worker_count {
            workers.push(WorkerTimings::new());
        }
        Self {
            screenshot_start: AtomicDuration::new(),
            screenshot: AtomicDuration::new(),
            walker_start: AtomicDuration::new(),
            walker: AtomicDuration::new(),
            workers,
        }
    }

    /// Gate time: the latest-finishing child in the parallel group.
    pub fn gate(&self) -> Option<Duration> {
        let mut max = Duration::ZERO;
        let mut any = false;
        for d in [self.screenshot.get(), self.walker.get()]
            .into_iter()
            .flatten()
        {
            max = max.max(d);
            any = true;
        }
        for w in &self.workers {
            for d in [
                w.prep_start.get(),
                w.render_prep.get(),
                w.prep_adapter.get(),
                w.prep_device.get(),
                w.prep_pipelines.get(),
                w.prep_ui_pipelines.get(),
                w.prep_fonts.get(),
                w.upload_start.get(),
                w.upload.get(),
                w.surface_start.get(),
                w.surface_bind.get(),
                w.first_render_start.get(),
                w.first_render.get(),
            ]
            .into_iter()
            .flatten()
            {
                max = max.max(d);
                any = true;
            }
        }
        if any {
            Some(max)
        } else {
            None
        }
    }
}

pub struct StartupTimings {
    pub t_start: Instant,
    pub t_initialize: AtomicDuration,
    pub background: BackgroundGroup,
    pub t_window_create_start: AtomicDuration,
    pub t_window_create: AtomicDuration,
    pub t_show_start: AtomicDuration,
}

impl StartupTimings {
    pub fn new(t_start: Instant, worker_count: usize) -> Self {
        Self {
            t_start,
            t_initialize: AtomicDuration::new(),
            background: BackgroundGroup::new(worker_count),
            t_window_create_start: AtomicDuration::new(),
            t_window_create: AtomicDuration::new(),
            t_show_start: AtomicDuration::new(),
        }
    }

    pub fn mark_initialize(&self) {
        self.t_initialize
            .set_once(self.t_start.elapsed());
    }

    pub fn mark_window_create(&self) {
        self.t_window_create
            .set_once(self.t_start.elapsed());
    }

    pub fn mark_window_create_start(&self) {
        self.t_window_create_start
            .set_once(self.t_start.elapsed());
    }

    pub fn mark_show_start(&self) {
        self.t_show_start
            .set_once(self.t_start.elapsed());
    }

    /// Total startup time: the latest recorded phase.
    pub fn total(&self) -> Duration {
        let mut total = Duration::ZERO;
        if let Some(d) = self.t_initialize.get() {
            total = total.max(d);
        }
        if let Some(d) = self.background.gate() {
            total = total.max(d);
        }
        if let Some(d) = self.t_window_create_start.get() {
            total = total.max(d);
        }
        if let Some(d) = self.t_window_create.get() {
            total = total.max(d);
        }
        total
    }
}
