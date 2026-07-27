//! stdout JSON protocol (§1.3) and the 1 Hz status thread with its
//! paused-adjusted recording clock.

use std::collections::VecDeque;
use std::io::Write;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde::Serialize;

/// Writes one JSON line to stdout (protocol channel; stderr carries all
/// free-form output).
pub fn emit_json(value: serde_json::Value) {
    let stdout = std::io::stdout();
    let mut lock = stdout.lock();
    let _ = writeln!(lock, "{value}");
    let _ = lock.flush();
}

pub fn emit_simple(msg_type: &str) {
    emit_json(serde_json::json!({ "type": msg_type }));
}

pub fn emit_stopped_recording(code: i64, error: Option<String>) {
    emit_json(serde_json::json!({
        "type": "stopped_recording",
        "code": code,
        "message": stop_code_message(code),
        "error": error,
    }));
}

/// Fixed message per OBS stop code (C++ parity table).
pub fn stop_code_message(code: i64) -> String {
    match code {
        0 => "Successfully stopped".to_string(),
        -1 => "The specified path was invalid".to_string(),
        -4 => "Generic error".to_string(),
        -6 => {
            "The settings, video/audio format, or codecs are unsupported by this output".to_string()
        }
        -7 => "Ran out of disk space".to_string(),
        -8 => "Encoder error".to_string(),
        -99 => "Timed out waiting for output to stop".to_string(),
        n => format!("Unknown error: {n}"),
    }
}

#[derive(Serialize)]
struct StatusMessage {
    #[serde(rename = "type")]
    msg_type: &'static str,
    #[serde(rename = "timeMs")]
    time_ms: u64,
    fps: f64,
    dropped: i64,
    #[serde(rename = "droppedPerc")]
    dropped_perc: f64,
}

/// Wall clock since `started_recording` minus accumulated paused time. Both
/// `timeMs` and `fps` are computed against this same clock so fps does not dip
/// permanently after a pause.
pub struct RecordingClock {
    inner: Mutex<ClockInner>,
}

struct ClockInner {
    start: Instant,
    paused_accum: Duration,
    paused_since: Option<Instant>,
}

impl RecordingClock {
    pub fn new() -> Self {
        RecordingClock {
            inner: Mutex::new(ClockInner {
                start: Instant::now(),
                paused_accum: Duration::ZERO,
                paused_since: None,
            }),
        }
    }

    pub fn pause(&self) {
        let mut inner = self.inner.lock().unwrap();
        if inner.paused_since.is_none() {
            inner.paused_since = Some(Instant::now());
        }
    }

    pub fn resume(&self) {
        let mut inner = self.inner.lock().unwrap();
        if let Some(since) = inner.paused_since.take() {
            inner.paused_accum += since.elapsed();
        }
    }

    pub fn is_paused(&self) -> bool {
        self.inner.lock().unwrap().paused_since.is_some()
    }

    pub fn elapsed_ms(&self) -> u64 {
        let inner = self.inner.lock().unwrap();
        let mut elapsed = inner.start.elapsed();
        let mut paused = inner.paused_accum;
        if let Some(since) = inner.paused_since {
            paused += since.elapsed();
        }
        elapsed = elapsed.saturating_sub(paused);
        elapsed.as_millis() as u64
    }
}

impl Default for RecordingClock {
    fn default() -> Self {
        Self::new()
    }
}

/// Trailing-window frame rate behind the `fps` status field.
///
/// `fps` used to be the lifetime ratio `total_frames / elapsed`, which reads
/// permanently low: the frame counter trails the recording clock by a fixed
/// deficit (encoder startup, plus the frames in flight in the encoder), and
/// that deficit is never repaid, so a healthy 30 fps capture reports ~29 for
/// the first minute and only crawls towards 30 afterwards. Differencing two
/// samples cancels the deficit — it is present at both ends — so a trailing
/// window reports the rate the encoder is actually sustaining *now*, which is
/// also what a dropped-frame spike should move.
#[derive(Default)]
struct FpsWindow {
    /// `(elapsed_ms, total_frames)` samples, oldest first.
    samples: VecDeque<(u64, i64)>,
}

impl FpsWindow {
    /// Span the rate is averaged over. 5 s is the shortest window that keeps
    /// the ±1 frame quantisation of a 1 Hz sample (±0.2 fps here) far enough
    /// inside the rounding a consumer applies to print a whole number.
    const WINDOW_MS: u64 = 5000;

    /// Records a sample and returns the frame rate over the window ending at
    /// it. Paused spans need no handling: the caller takes no sample while
    /// paused and neither endpoint advances during one, so the window sees a
    /// pause as a gap in sampling rather than a frame rate collapse.
    fn push(&mut self, time_ms: u64, total: i64) -> f64 {
        self.samples.push_back((time_ms, total));

        // Keep the newest sample that is already a full window old as the
        // baseline, so the span stays >= WINDOW_MS rather than collapsing to
        // whatever is left after trimming.
        while self.samples.len() > 2 && time_ms.saturating_sub(self.samples[1].0) >= Self::WINDOW_MS
        {
            self.samples.pop_front();
        }

        let (base_ms, base_frames) = self.samples[0];
        let fps = if time_ms > base_ms {
            // Before the window has filled this is simply a shorter span: it
            // is already deficit-free, only noisier.
            ((total - base_frames) as f64 * 1000.0) / (time_ms - base_ms) as f64
        } else if time_ms > 0 {
            // First sample only — nothing to difference against yet.
            (total as f64 * 1000.0) / time_ms as f64
        } else {
            0.0
        };
        fps.max(0.0)
    }
}

/// Emits a `status` line every 1000 ms while recording and not paused.
///
/// Reads the raw output pointer directly (frame counters are thread-safe);
/// this is sound because the process always terminates via
/// `platform::exit_process` while the output is still alive.
pub fn start_status_thread(
    output_ptr: *mut obs_sys::obs_output_t,
    clock: Arc<RecordingClock>,
    stop_flag: Arc<AtomicBool>,
) -> std::thread::JoinHandle<()> {
    let ptr_addr = output_ptr as usize;
    std::thread::spawn(move || {
        let ptr = ptr_addr as *const obs_sys::obs_output_t;
        let mut fps_window = FpsWindow::default();
        while !stop_flag.load(Ordering::Relaxed) {
            std::thread::sleep(Duration::from_millis(1000));
            if stop_flag.load(Ordering::Relaxed) {
                break;
            }
            if clock.is_paused() {
                continue;
            }

            let total = unsafe { obs_sys::obs_output_get_total_frames(ptr) } as i64;
            let dropped = unsafe { obs_sys::obs_output_get_frames_dropped(ptr) } as i64;
            let time_ms = clock.elapsed_ms();

            let fps = fps_window.push(time_ms, total);
            let dropped_perc = if total > 0 {
                (dropped as f64 / total as f64) * 100.0
            } else {
                0.0
            };

            let status = StatusMessage {
                msg_type: "status",
                time_ms,
                fps,
                dropped,
                dropped_perc,
            };
            if let Ok(json) = serde_json::to_value(&status) {
                emit_json(json);
            }
        }
    })
}

/// Peak dBFS floor for the `levels` protocol line. JSON has no -inf/NaN (serde
/// serializes non-finite floats as null), so every non-finite value — silence
/// (-inf), plus NaN/+inf from corrupt device samples — clamps here.
const LEVELS_FLOOR_DB: f64 = -100.0;

fn clamp_dbfs(peak: f32) -> f64 {
    if peak.is_finite() {
        (peak as f64).max(LEVELS_FLOOR_DB)
    } else {
        LEVELS_FLOOR_DB
    }
}

/// Emits a `levels` line every 100 ms: peak dBFS per audio source (speakers
/// then mics, CLI order), clamped to -100.0. Runs from initialization —
/// including the pre-start WAIT phase — until `stop_flag`.
pub fn start_levels_thread(
    speaker_peaks: Vec<Arc<AtomicU32>>,
    mic_peaks: Vec<Arc<AtomicU32>>,
    stop_flag: Arc<AtomicBool>,
) -> std::thread::JoinHandle<()> {
    std::thread::spawn(move || {
        let read = |peaks: &[Arc<AtomicU32>]| -> Vec<f64> {
            peaks
                .iter()
                .map(|p| clamp_dbfs(f32::from_bits(p.load(Ordering::Relaxed))))
                .collect()
        };
        while !stop_flag.load(Ordering::Relaxed) {
            std::thread::sleep(Duration::from_millis(100));
            if stop_flag.load(Ordering::Relaxed) {
                break;
            }
            emit_json(serde_json::json!({
                "type": "levels",
                "speaker": read(&speaker_peaks),
                "mic": read(&mic_peaks),
            }));
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stop_code_messages() {
        assert_eq!(stop_code_message(0), "Successfully stopped");
        assert_eq!(stop_code_message(-1), "The specified path was invalid");
        assert_eq!(stop_code_message(-4), "Generic error");
        assert_eq!(
            stop_code_message(-6),
            "The settings, video/audio format, or codecs are unsupported by this output"
        );
        assert_eq!(stop_code_message(-7), "Ran out of disk space");
        assert_eq!(stop_code_message(-8), "Encoder error");
        assert_eq!(
            stop_code_message(-99),
            "Timed out waiting for output to stop"
        );
        // Codes without a fixed string fall through to the generic form.
        assert_eq!(stop_code_message(-2), "Unknown error: -2");
        assert_eq!(stop_code_message(-3), "Unknown error: -3");
        assert_eq!(stop_code_message(-5), "Unknown error: -5");
        assert_eq!(stop_code_message(42), "Unknown error: 42");
    }

    #[test]
    fn clock_subtracts_paused_time() {
        let wall_start = Instant::now();
        let clock = RecordingClock::new();
        std::thread::sleep(Duration::from_millis(30));
        clock.pause();
        assert!(clock.is_paused());
        std::thread::sleep(Duration::from_millis(50));
        clock.resume();
        assert!(!clock.is_paused());
        let elapsed = clock.elapsed_ms();
        let wall = wall_start.elapsed().as_millis() as u64;
        // The clock must exclude the ~50 ms spent paused, so it reads
        // meaningfully less than the total wall time. Assert the *gap* between
        // wall and clock rather than an absolute upper bound on the clock: under
        // CI scheduler load any sleep can overrun by tens of ms, which is what
        // flaked the old `elapsed < 75` bound (observed `elapsed 160`). The gap
        // is just the paused span, which is stable regardless of overrun.
        assert!(elapsed >= 20, "clock lost unpaused time: elapsed {elapsed}");
        assert!(
            wall >= elapsed + 40,
            "pause not subtracted: elapsed {elapsed} wall {wall}"
        );
    }

    /// Sample stream of a healthy capture at `fps`: the encoder is `deficit`
    /// frames behind the clock from the first sample onward and stays there.
    fn steady(window: &mut FpsWindow, fps: i64, deficit: i64, secs: u64) -> f64 {
        let mut last = 0.0;
        for s in 1..=secs {
            let total = fps * s as i64 - deficit;
            last = window.push(s * 1000, total.max(0));
        }
        last
    }

    #[test]
    fn fps_window_cancels_the_encoder_deficit() {
        // The lifetime ratio this replaced would report 28.5 here (285/10),
        // which rounds to the "29 FPS" a 30 fps capture used to display.
        let mut w = FpsWindow::default();
        let fps = steady(&mut w, 30, 15, 10);
        assert!((fps - 30.0).abs() < 0.01, "expected ~30, got {fps}");

        // ...and the bias does not shrink with a bigger deficit, only the
        // lifetime ratio's does.
        let mut w = FpsWindow::default();
        let fps = steady(&mut w, 60, 90, 10);
        assert!((fps - 60.0).abs() < 0.01, "expected ~60, got {fps}");
    }

    #[test]
    fn fps_window_smooths_whole_frame_quantisation() {
        // A 30 fps capture sampled at 1 Hz lands on 29/30/31 frames per tick
        // depending on where the sample falls between frames. Once the window
        // has filled, every reading must round to 30 — a single tick still
        // swings by a whole frame, which is exactly what the window is for, so
        // the first WINDOW_MS of (deliberately shorter, noisier) spans is not
        // held to it.
        let mut w = FpsWindow::default();
        let mut total = 0;
        for (i, delta) in [29, 31, 30, 29, 30, 31, 29, 30, 30, 31, 29, 30]
            .into_iter()
            .enumerate()
        {
            total += delta;
            let time_ms = (i as u64 + 1) * 1000;
            let fps = w.push(time_ms, total);
            if time_ms > FpsWindow::WINDOW_MS {
                assert!((fps - 30.0).abs() < 0.5, "tick {i}: {fps}");
            }
        }
    }

    #[test]
    fn fps_window_reports_a_real_drop() {
        // Smoothing must not hide an actual stall: 5 s at 10 fps after 10 s at
        // 30 fps has fully aged the good samples out of the window.
        let mut w = FpsWindow::default();
        steady(&mut w, 30, 0, 10);
        let mut total = 300;
        let mut fps = 0.0;
        for s in 11..=15 {
            total += 10;
            fps = w.push(s * 1000, total);
        }
        assert!((fps - 10.0).abs() < 0.5, "expected ~10, got {fps}");
    }

    #[test]
    fn fps_window_first_sample_falls_back_to_the_lifetime_ratio() {
        let mut w = FpsWindow::default();
        assert_eq!(w.push(1000, 30), 30.0);
        // A zero clock has nothing to divide by and must not produce NaN/inf.
        let mut w = FpsWindow::default();
        assert_eq!(w.push(0, 0), 0.0);
    }

    #[test]
    fn fps_window_survives_a_pause() {
        // No sample is taken while paused, and the clock excludes the paused
        // span, so resuming must not read as a frame rate collapse.
        let mut w = FpsWindow::default();
        steady(&mut w, 30, 0, 6);
        // 30 s of wall time paused; the clock advanced 1 s, frames by 30.
        let fps = w.push(7000, 210);
        assert!((fps - 30.0).abs() < 0.01, "expected ~30, got {fps}");
    }

    #[test]
    fn fps_window_never_reports_a_negative_rate() {
        // The frame counter is monotonic in practice; guard the arithmetic
        // anyway so a wrapped counter cannot emit a negative fps.
        let mut w = FpsWindow::default();
        w.push(1000, 500);
        assert_eq!(w.push(2000, 10), 0.0);
    }

    #[test]
    fn clamp_dbfs_floors_non_finite_and_quiet_values() {
        assert_eq!(clamp_dbfs(f32::NEG_INFINITY), -100.0);
        assert_eq!(clamp_dbfs(f32::INFINITY), -100.0);
        assert_eq!(clamp_dbfs(f32::NAN), -100.0);
        assert_eq!(clamp_dbfs(-250.0), -100.0);
        assert_eq!(clamp_dbfs(-100.0), -100.0);
        assert!((clamp_dbfs(-18.5) - -18.5f32 as f64).abs() < 1e-6);
        assert_eq!(clamp_dbfs(0.0), 0.0);
        // Clamped values always serialize as JSON numbers, never null.
        let v = serde_json::json!(clamp_dbfs(f32::NEG_INFINITY));
        assert!(v.is_f64(), "expected number, got {v}");
    }
}
