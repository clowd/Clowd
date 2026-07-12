//! stdout JSON protocol (§1.3) and the 1 Hz status thread with its
//! paused-adjusted recording clock.

use std::io::Write;
use std::sync::atomic::{AtomicBool, Ordering};
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
            let fps = if time_ms > 0 {
                (total as f64 * 1000.0) / time_ms as f64
            } else {
                0.0
            };
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
}
