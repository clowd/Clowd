use std::sync::atomic::AtomicUsize;
use std::sync::{mpsc, Arc, OnceLock};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use crate::geometry::{ScreenPointF, ScreenRect};
use crate::render::protocol::{RenderMsg, WorkerInput};
use crate::settings::CapturerSettings;
use crate::sync::VisibleLatch;
use crate::system::MonitorInfo;
use crate::telemetry::startup::StartupTimings;

pub struct RenderWorkerParams {
    pub monitor: MonitorInfo,
    pub monitor_index: usize,
    pub settings: Arc<CapturerSettings>,
    pub instance: Arc<wgpu::Instance>,
    pub initial_mouse: ScreenPointF,
    pub startup: Arc<StartupTimings>,
    pub shown_time: Arc<OnceLock<Duration>>,
    pub ready_count: Arc<AtomicUsize>,
    pub visible_latch: Arc<VisibleLatch>,
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
