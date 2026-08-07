use std::sync::atomic::AtomicUsize;
use std::sync::{mpsc, Arc};
use std::thread::{self, JoinHandle};

use winit::event_loop::EventLoopProxy;

use crate::geometry::ScreenRect;
use crate::host::AppEvent;
use crate::render::protocol::{RenderMsg, WorkerInput};
use crate::settings::MemoryHintsMode;
use crate::system::MonitorInfo;
use crate::telemetry::startup::WarmupTimings;

pub struct RenderWorkerParams {
    pub monitor: MonitorInfo,
    pub monitor_index: usize,
    pub instance: Arc<wgpu::Instance>,
    pub warmup: Arc<WarmupTimings>,
    /// GPU allocator strategy for this worker's device (`--memory-hints`).
    pub memory_hints: MemoryHintsMode,
    /// Incremented (once, via `ReadyGuard`) when this worker dies without a
    /// clean shutdown, so the app's show gate (`ready + failed >= expected`)
    /// can never deadlock on a dead worker.
    pub failed_count: Arc<AtomicUsize>,
    /// Incremented once when this worker first reaches the parked state
    /// (device + pipelines built, surface configured, waiting for a
    /// `BeginCycle`). The persistent host emits `ready` once
    /// `parked + failed` covers every worker.
    pub parked_count: Arc<AtomicUsize>,
    /// `Some` only in persistent mode: the wgpu device-lost callback
    /// registered in Stage A signals the main thread through this so the
    /// host restarts (`EXIT_GPU_LOST`) instead of sitting warm with a
    /// dead device. `None` in one-shot mode — its device-loss behaviour
    /// stays unchanged.
    pub gpu_lost_proxy: Option<EventLoopProxy<AppEvent>>,
}

pub struct WorkerSetup {
    pub input_tx: mpsc::Sender<WorkerInput>,
    pub render_msg_tx: mpsc::Sender<RenderMsg>,
    pub thread: JoinHandle<()>,
    pub monitor_bounds: ScreenRect,
}

pub fn spawn_render_worker(params: RenderWorkerParams) -> WorkerSetup {
    let (input_tx, input_rx) = mpsc::channel();
    let (render_msg_tx, render_msg_rx) = mpsc::channel();
    let monitor_bounds = params.monitor.bounds;
    let thread_name = format!("render-worker-{}", params.monitor_index);
    let thread = thread::Builder::new()
        .name(thread_name)
        .spawn(move || {
            super::render_worker_main(params, input_rx, render_msg_rx);
        })
        .expect("spawn render worker");
    WorkerSetup {
        input_tx,
        render_msg_tx,
        thread,
        monitor_bounds,
    }
}
