use serde::Serialize;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Instant;

#[derive(Serialize)]
pub struct StatusMessage {
    #[serde(rename = "type")]
    pub msg_type: &'static str,
    #[serde(rename = "timeMs")]
    pub time_ms: u64,
    pub fps: f64,
    pub dropped: i32,
    #[serde(rename = "droppedPerc")]
    pub dropped_perc: f64,
}

pub fn start_status_thread(
    output_ptr: *mut obs_sys::obs_output_t,
    start_time: Instant,
    stop_flag: Arc<AtomicBool>,
) -> std::thread::JoinHandle<()> {
    // Cast pointer to usize to cross thread boundary (OBS frame counters are thread-safe)
    let ptr_addr = output_ptr as usize;
    std::thread::spawn(move || {
        let ptr = ptr_addr as *const obs_sys::obs_output_t;
        while !stop_flag.load(Ordering::Relaxed) {
            std::thread::sleep(std::time::Duration::from_secs(1));
            if stop_flag.load(Ordering::Relaxed) {
                break;
            }

            let total = unsafe { obs_sys::obs_output_get_total_frames(ptr) };
            let dropped = unsafe { obs_sys::obs_output_get_frames_dropped(ptr) };
            let elapsed = start_time.elapsed().as_millis() as u64;
            let fps = if elapsed > 0 {
                (total as f64 * 1000.0) / elapsed as f64
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
                time_ms: elapsed,
                fps,
                dropped,
                dropped_perc,
            };

            if let Ok(json) = serde_json::to_string(&status) {
                println!("{json}");
            }
        }
    })
}
