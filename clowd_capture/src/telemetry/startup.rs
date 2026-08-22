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
    /// When the worker received its window + surface handoff.
    pub handoff: AtomicDuration,
    pub first_render_start: AtomicDuration,
    /// `queue.present()` of frame 0 returned — the last moment this worker
    /// controls before the compositor owns the pixels. `first_render` is
    /// later: it also waits on the device poll.
    pub first_present: AtomicDuration,
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
            handoff: AtomicDuration::new(),
            first_render_start: AtomicDuration::new(),
            first_present: AtomicDuration::new(),
            first_render: AtomicDuration::new(),
        }
    }

    /// Chronological view of this worker's marks, unset ones dropped.
    fn stages(&self) -> Vec<(&'static str, Duration)> {
        [
            ("prep_start", self.prep_start.get()),
            ("prep_adapter", self.prep_adapter.get()),
            ("prep_device", self.prep_device.get()),
            ("prep_pipelines", self.prep_pipelines.get()),
            ("render_prep", self.render_prep.get()),
            ("upload_start", self.upload_start.get()),
            ("upload", self.upload.get()),
            ("surface_start", self.surface_start.get()),
            ("surface_bind", self.surface_bind.get()),
            ("handoff", self.handoff.get()),
            ("first_render_start", self.first_render_start.get()),
            ("first_present", self.first_present.get()),
            ("first_render", self.first_render.get()),
            // Off the critical path, and printed last because that is where they
            // actually land on the clock: the UI stack (fonts, glyph atlas, SVG
            // parses, rect/icon/lift pipelines) is built on a side thread that the
            // worker only joins after frame 0, so both of these can be — and on a
            // healthy run are — later than `first_render` and often later than the
            // overlay being on screen. Their deltas are against `first_render`
            // above, which is the point they were deferred past.
            ("deferred_fonts", self.prep_fonts.get()),
            ("deferred_ready", self.prep_ui_pipelines.get()),
        ]
        .into_iter()
        .filter_map(|(name, d)| d.map(|d| (name, d)))
        .collect()
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
            // `prep_ui_pipelines` / `prep_fonts` are deliberately absent: the UI
            // stack is built on its own thread that is joined AFTER frame 0 and
            // routinely finishes after `t_shown`. Folding it in here would let
            // work the user never waited for inflate `gate()`, and through it the
            // report header and `total()` — making the deferral read as a
            // regression. They are still reported, at the end of each worker's
            // rows, as `deferred_*`.
            for d in [
                w.prep_start.get(),
                w.render_prep.get(),
                w.prep_adapter.get(),
                w.prep_device.get(),
                w.prep_pipelines.get(),
                w.upload_start.get(),
                w.upload.get(),
                w.surface_start.get(),
                w.surface_bind.get(),
                w.handoff.get(),
                w.first_render_start.get(),
                w.first_present.get(),
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

    /// When the LAST monitor got frame 0 in front of the compositor. The
    /// per-worker marks are first-writer-wins, so the fleet-wide answer has
    /// to be a max here rather than a shared `set_once` — and it is the max
    /// that matters: the desktop is only fully covered once the slowest
    /// display has presented. Workers that never presented (a dead worker,
    /// which the show gate tolerates) are simply absent from the max.
    pub fn first_present(&self) -> Option<Duration> {
        self.workers
            .iter()
            .filter_map(|w| w.first_present.get())
            .max()
    }
}

/// Offsets taken in `main` before `StartupTimings` can exist: the timings
/// object is sized by the monitor count, and everything here happens before
/// the monitors have been enumerated. Carried as raw offsets from the same
/// `t_start` and folded in with `apply_prologue` once the session hands the
/// timings back, so the report has no unmeasured head.
#[derive(Debug, Default, Clone, Copy)]
pub struct Prologue {
    pub logging_ready: Duration,
    pub sentry_ready: Duration,
    pub system_init: Duration,
    pub permission_checked: Duration,
}

/// Every phase of the one-shot capture, anchored at process start so all
/// offsets are directly comparable across threads.
pub struct StartupTimings {
    pub t_start: Instant,
    pub t_logging_ready: AtomicDuration,
    pub t_sentry_ready: AtomicDuration,
    pub t_system_init: AtomicDuration,
    pub t_permission_checked: AtomicDuration,
    pub t_monitors_enumerated: AtomicDuration,
    pub t_instance_created: AtomicDuration,
    pub t_initialize: AtomicDuration,
    pub t_workers_spawned: AtomicDuration,
    pub background: BackgroundGroup,
    /// The main thread picked the desktop screenshot up off its latch
    /// (`app.rs::try_pick_up_screenshot`) — no longer a blocking wait, so on
    /// a typical run this lands *after* window creation, which now overlaps
    /// the capture.
    pub t_screenshot_latch_released: AtomicDuration,
    pub t_event_loop_built: AtomicDuration,
    pub t_run_app_entered: AtomicDuration,
    pub t_window_create_start: AtomicDuration,
    pub t_window_create: AtomicDuration,
    pub t_show_start: AtomicDuration,
    /// When every window became visible.
    pub t_shown: AtomicDuration,
}

impl StartupTimings {
    pub fn new(t_start: Instant, worker_count: usize) -> Self {
        Self {
            t_start,
            t_logging_ready: AtomicDuration::new(),
            t_sentry_ready: AtomicDuration::new(),
            t_system_init: AtomicDuration::new(),
            t_permission_checked: AtomicDuration::new(),
            t_monitors_enumerated: AtomicDuration::new(),
            t_instance_created: AtomicDuration::new(),
            t_initialize: AtomicDuration::new(),
            t_workers_spawned: AtomicDuration::new(),
            background: BackgroundGroup::new(worker_count),
            t_screenshot_latch_released: AtomicDuration::new(),
            t_event_loop_built: AtomicDuration::new(),
            t_run_app_entered: AtomicDuration::new(),
            t_window_create_start: AtomicDuration::new(),
            t_window_create: AtomicDuration::new(),
            t_show_start: AtomicDuration::new(),
            t_shown: AtomicDuration::new(),
        }
    }

    pub fn apply_prologue(&self, p: Prologue) {
        self.t_logging_ready
            .set_once(p.logging_ready);
        self.t_sentry_ready.set_once(p.sentry_ready);
        self.t_system_init.set_once(p.system_init);
        self.t_permission_checked
            .set_once(p.permission_checked);
    }

    pub fn mark_monitors_enumerated(&self, at: Duration) {
        self.t_monitors_enumerated.set_once(at);
    }

    pub fn mark_instance_created(&self) {
        self.t_instance_created
            .set_once(self.t_start.elapsed());
    }

    pub fn mark_initialize(&self) {
        self.t_initialize
            .set_once(self.t_start.elapsed());
    }

    pub fn mark_workers_spawned(&self) {
        self.t_workers_spawned
            .set_once(self.t_start.elapsed());
    }

    pub fn mark_screenshot_latch_released(&self) {
        self.t_screenshot_latch_released
            .set_once(self.t_start.elapsed());
    }

    pub fn mark_event_loop_built(&self) {
        self.t_event_loop_built
            .set_once(self.t_start.elapsed());
    }

    pub fn mark_run_app_entered(&self) {
        self.t_run_app_entered
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

    pub fn mark_show_start(&self) {
        self.t_show_start
            .set_once(self.t_start.elapsed());
    }

    pub fn mark_shown(&self) {
        self.t_shown.set_once(self.t_start.elapsed());
    }

    /// Total startup time: the latest recorded phase.
    pub fn total(&self) -> Duration {
        let mut total = Duration::ZERO;
        for (_, d) in self.stages() {
            total = total.max(d);
        }
        if let Some(d) = self.background.gate() {
            total = total.max(d);
        }
        total
    }

    /// The main-thread timeline, chronological, unset marks dropped. A
    /// missing line means that mark was never reached — worth seeing as a
    /// gap rather than as a zero.
    fn stages(&self) -> Vec<(&'static str, Duration)> {
        [
            ("logging_ready", self.t_logging_ready.get()),
            ("sentry_ready", self.t_sentry_ready.get()),
            ("system_init", self.t_system_init.get()),
            ("permission_checked", self.t_permission_checked.get()),
            ("monitors_enumerated", self.t_monitors_enumerated.get()),
            ("instance_created", self.t_instance_created.get()),
            ("initialize", self.t_initialize.get()),
            ("workers_spawned", self.t_workers_spawned.get()),
            ("event_loop_built", self.t_event_loop_built.get()),
            ("run_app_entered", self.t_run_app_entered.get()),
            ("window_create_start", self.t_window_create_start.get()),
            ("window_create", self.t_window_create.get()),
            // After window_create, not before event_loop_built: the pickup
            // happens inside the event loop, normally on the about_to_wait
            // pass after the (screenshot-overlapped) window creation.
            ("screenshot_latch", self.t_screenshot_latch_released.get()),
            ("first_present", self.background.first_present()),
            ("show_start", self.t_show_start.get()),
            ("shown", self.t_shown.get()),
        ]
        .into_iter()
        .filter_map(|(name, d)| d.map(|d| (name, d)))
        .collect()
    }

    /// The whole startup as one log record: every stage on its own line,
    /// chronological, with its absolute offset from process start and its
    /// delta from the stage above it. The background threads run *beside*
    /// the main-thread stages, so their offsets are printed against the same
    /// t_start and are meant to be compared column-wise, not summed.
    ///
    /// This is the benchmark output a human reads, so the columns are fixed
    /// width and the deltas are what the eye lands on: the biggest delta is
    /// the next thing to fix.
    pub fn report(&self) -> String {
        use std::fmt::Write;

        let mut out = String::with_capacity(2048);
        let _ = write!(
            out,
            "startup {:.2}ms (offsets in ms from process entry; delta is from the line above)\n  {:<22}{:>9}{:>9}",
            ms(self.total()),
            "stage",
            "at",
            "delta"
        );

        let mut prev = Duration::ZERO;
        for (name, at) in self.stages() {
            stage_line(&mut out, "  ", name, at, &mut prev);
        }

        let gate = self
            .background
            .gate()
            .map(|d| format!("{:.2}ms", ms(d)))
            .unwrap_or_else(|| "not reached".into());
        let _ = write!(
            out,
            "\n  background ({} worker(s), gate {gate}) — concurrent with the stages above",
            self.background.workers.len()
        );

        let mut prev = Duration::ZERO;
        for (name, at) in [
            ("screenshot_start", self.background.screenshot_start.get()),
            ("screenshot", self.background.screenshot.get()),
        ]
        .into_iter()
        .filter_map(|(n, d)| d.map(|d| (n, d)))
        {
            stage_line(&mut out, "    ", name, at, &mut prev);
        }

        let mut prev = Duration::ZERO;
        for (name, at) in [
            ("walker_start", self.background.walker_start.get()),
            ("walker", self.background.walker.get()),
        ]
        .into_iter()
        .filter_map(|(n, d)| d.map(|d| (n, d)))
        {
            stage_line(&mut out, "    ", name, at, &mut prev);
        }

        for (i, w) in self.background.workers.iter().enumerate() {
            let _ = write!(out, "\n    worker {i}");
            let mut prev = Duration::ZERO;
            for (name, at) in w.stages() {
                stage_line(&mut out, "      ", name, at, &mut prev);
            }
        }

        out
    }
}

fn ms(d: Duration) -> f64 {
    d.as_secs_f64() * 1000.0
}

/// One `indent + name + absolute + delta` row, keeping the two numeric
/// columns at the same screen position whatever the indent is.
fn stage_line(out: &mut String, indent: &str, name: &str, at: Duration, prev: &mut Duration) {
    use std::fmt::Write;
    let width = 24usize.saturating_sub(indent.len());
    let _ = write!(out, "\n{indent}{name:<width$}{:>9.2}{:>9.2}", ms(at), ms(at.saturating_sub(*prev)));
    *prev = at;
}
