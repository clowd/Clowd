//! Per-render-thread frame timing tracker.
//!
//! Mirrors the four-sample design of C++ `DxPerfStats` at
//! `DxOutputDevice.h:40-94`:
//!   * `wait` — time blocked on `get_current_texture()` (DXGI waitable
//!     object on Windows). Analogue of the C++ "wait for vsync" sample.
//!   * `draw` — time spent building the frame encoder and submitting
//!     to the queue. CPU side only; GPU execution happens asynchronously
//!     after `submit()`.
//!   * `present` — time inside `frame.present()`.
//!   * `overall` — wall-clock gap between consecutive loop iterations.
//!     Reciprocal ≈ FPS.
//!
//! Rolling window: 120 samples (≈ 2 s at 60 fps). Stats computed on
//! demand each frame when the debug panel is visible; no work is done
//! otherwise.

use std::collections::VecDeque;
use std::time::Duration;

/// Samples per rolling window. 120 ≈ 2 seconds at 60 Hz, matching the C++
/// tracker's default window (`DxOutputDevice.h:44`).
pub const PERF_WINDOW: usize = 120;

/// One per-frame timing record. All fields are wall-clock durations, not
/// GPU timestamps.
#[derive(Debug, Clone, Copy)]
pub struct PerfSample {
    pub wait: Duration,
    pub draw: Duration,
    pub present: Duration,
    pub overall: Duration,
}

/// Summary statistics for one timing series over the rolling window. All
/// values in milliseconds.
#[derive(Debug, Clone, Copy, Default)]
pub struct PerfStats {
    pub avg_ms: f64,
    pub min_ms: f64,
    pub max_ms: f64,
    pub stdev_ms: f64,
    pub count: usize,
}

pub struct PerfTracker {
    samples: VecDeque<PerfSample>,
    /// Monotonically incremented every `record()`. Used for the `fps:`
    /// counter (which prefers "frames per second" over "samples in the
    /// rolling window") and for the time_to_render value in ms.
    total_frames: u64,
}

impl PerfTracker {
    pub fn new() -> Self {
        Self {
            samples: VecDeque::with_capacity(PERF_WINDOW),
            total_frames: 0,
        }
    }

    pub fn record(&mut self, sample: PerfSample) {
        if self.samples.len() == PERF_WINDOW {
            self.samples.pop_front();
        }
        self.samples.push_back(sample);
        self.total_frames = self.total_frames.saturating_add(1);
    }

    /// Compute stats for one field of `PerfSample`, selected by a
    /// projection closure. Returns `Default::default()` when the window
    /// is empty.
    pub fn stats<F: Fn(&PerfSample) -> Duration>(&self, project: F) -> PerfStats {
        if self.samples.is_empty() {
            return PerfStats::default();
        }
        let mut sum = 0.0f64;
        let mut min = f64::INFINITY;
        let mut max = f64::NEG_INFINITY;
        for s in &self.samples {
            let ms = project(s).as_secs_f64() * 1000.0;
            sum += ms;
            if ms < min {
                min = ms;
            }
            if ms > max {
                max = ms;
            }
        }
        let count = self.samples.len();
        let avg = sum / count as f64;
        let mut var = 0.0f64;
        for s in &self.samples {
            let ms = project(s).as_secs_f64() * 1000.0;
            let d = ms - avg;
            var += d * d;
        }
        let stdev = (var / count as f64).sqrt();
        PerfStats {
            avg_ms: avg,
            min_ms: min,
            max_ms: max,
            stdev_ms: stdev,
            count,
        }
    }

    /// Latest sample in the window (most recent frame). `None` before any
    /// `record()` call.
    pub fn latest(&self) -> Option<PerfSample> {
        self.samples.back().copied()
    }
}

impl Default for PerfTracker {
    fn default() -> Self {
        Self::new()
    }
}
