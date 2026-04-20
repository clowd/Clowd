//! Startup timing markers.
//!
//! One-shot wall-clock measurements taken during app bootstrap, frozen
//! into an `Arc<StartupTimings>` and handed to each render thread so the
//! primary debug panel can show the same breakdown as the C++ version at
//! `DxScreenCapture.cpp:938-957` (`startup: XXms total` + per-phase
//! sub-lines).
//!
//! All phases are timed on the main thread and frozen before the render
//! threads spawn, so the `Arc<StartupTimings>` they receive is immutable
//! and needs no synchronization.

use std::time::{Duration, Instant};

#[derive(Debug, Clone, Copy)]
pub struct StartupTimings {
    /// `main()` entry — baseline for every offset below.
    pub t_start: Instant,
    /// Offset of `SystemInterop::init()` completion.
    pub t_initialize: Option<Duration>,
    /// Offset of `capture_desktop()` + monitor enumeration completion.
    pub t_desktop_search: Option<Duration>,
    /// Offset of the window-creation loop completion in `App::resumed`.
    pub t_window_create: Option<Duration>,
}

impl StartupTimings {
    pub fn new() -> Self {
        Self {
            t_start: Instant::now(),
            t_initialize: None,
            t_desktop_search: None,
            t_window_create: None,
        }
    }

    pub fn mark_initialize(&mut self) {
        self.t_initialize
            .get_or_insert_with(|| self.t_start.elapsed());
    }

    pub fn mark_desktop_search(&mut self) {
        self.t_desktop_search
            .get_or_insert_with(|| self.t_start.elapsed());
    }

    pub fn mark_window_create(&mut self) {
        self.t_window_create
            .get_or_insert_with(|| self.t_start.elapsed());
    }

    /// Total startup time: the latest recorded offset (so the panel shows
    /// something sensible before every phase has fired).
    pub fn total(&self) -> Duration {
        self.t_window_create
            .or(self.t_desktop_search)
            .or(self.t_initialize)
            .unwrap_or_default()
    }
}

impl Default for StartupTimings {
    fn default() -> Self {
        Self::new()
    }
}
