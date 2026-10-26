use std::sync::atomic::AtomicUsize;
use std::sync::{mpsc, Arc};
use std::thread::{self, JoinHandle};

use crate::gxi;
use crate::render::protocol::{RenderMsg, WorkerInput};
use crate::system::MonitorInfo;
use crate::telemetry::perf::PerfSlot;
use crate::telemetry::startup::StartupTimings;
use clowd_rust_core::geometry::ScreenRect;

pub struct RenderWorkerParams {
    pub monitor: MonitorInfo,
    pub monitor_index: usize,
    pub instance: gxi::Instance,
    pub startup: Arc<StartupTimings>,
    /// Incremented (once, via `ReadyGuard`) when this worker dies without a
    /// clean shutdown, so the app's show gate (`ready + failed >= expected`)
    /// can never deadlock on a dead worker.
    pub failed_count: Arc<AtomicUsize>,
}

pub struct WorkerSetup {
    pub input_tx: mpsc::Sender<WorkerInput>,
    pub render_msg_tx: mpsc::Sender<RenderMsg>,
    pub thread: JoinHandle<()>,
    pub monitor_bounds: ScreenRect,
    /// Where this worker publishes its frame-timing snapshots for the app
    /// thread's debug panel. Empty until the panel is first shown.
    pub perf_slot: PerfSlot,
}

pub fn spawn_render_worker(params: RenderWorkerParams) -> WorkerSetup {
    let (input_tx, input_rx) = mpsc::channel();
    let (render_msg_tx, render_msg_rx) = mpsc::channel();
    let monitor_bounds = params.monitor.bounds;
    let thread_name = format!("render-worker-{}", params.monitor_index);
    let perf_slot: PerfSlot = PerfSlot::default();
    let worker_perf_slot = perf_slot.clone();
    let thread = thread::Builder::new()
        .name(thread_name)
        .spawn(move || {
            super::render_worker_main(params, input_rx, render_msg_rx, worker_perf_slot);
        })
        .expect("spawn render worker");
    WorkerSetup {
        input_tx,
        render_msg_tx,
        thread,
        monitor_bounds,
        perf_slot,
    }
}
