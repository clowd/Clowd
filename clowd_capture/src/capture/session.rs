use std::sync::atomic::{AtomicBool, AtomicUsize};
use std::sync::{mpsc, Arc};
use std::time::{Duration, Instant};

use winit::event_loop::EventLoopProxy;

use crate::app::{App, CycleSetup};
use crate::host::AppEvent;
use crate::image_extract;
use crate::render::protocol::{next_cycle_gen, BlurredDesktopImage, CycleParams, RenderMsg, WorkerInput};
use crate::render::worker::{self, RenderWorkerParams, WorkerSetup};
use crate::settings::{CapturerSettings, MemoryHintsMode};
use crate::sync::{Latch, VisibleLatch};
use crate::system::{CapturedCursor, CapturedDesktop, MonitorInfo, SystemInterop, WindowPeekImage, WindowWalker};
use crate::telemetry::startup::{CaptureTimings, WarmupTimings};
use clowd_rust_core::geometry::ScreenPointF;

type WalkerLatch = Arc<Latch<Arc<WindowWalker>>>;
type PeekImagesLatch = Arc<Latch<Vec<Arc<WindowPeekImage>>>>;

pub struct CaptureSession {
    app: App,
}

/// The warm half of [`CaptureSession::new`]: monitors, the wgpu instance
/// and render workers through Stage A — everything slow, nothing
/// per-capture. No screenshot/walker/cursor jobs run here; the persistent
/// host spawns those when a `show` command arrives (`App::handle_show`).
pub struct WarmSession {
    app: App,
}

impl WarmSession {
    /// `proxy` lets each worker's wgpu device-lost callback signal the
    /// main thread (→ `EXIT_GPU_LOST` restart); a lost device would
    /// otherwise sit undetected until the next `show` failed.
    pub fn new(proxy: EventLoopProxy<AppEvent>, memory_hints: MemoryHintsMode) -> anyhow::Result<Self> {
        let t_start = Instant::now();

        let monitors = SystemInterop::all_monitors();
        if monitors.is_empty() {
            anyhow::bail!("no monitors detected; nothing to render to");
        }

        let instance = Arc::new(create_wgpu_instance());
        let warmup = Arc::new(WarmupTimings::new(t_start, monitors.len()));
        warmup.mark_initialize();

        let worker_failed = Arc::new(AtomicUsize::new(0));
        let worker_parked = Arc::new(AtomicUsize::new(0));
        let worker_setups = spawn_render_workers(
            &monitors,
            &instance,
            &warmup,
            memory_hints,
            &worker_failed,
            &worker_parked,
            Some(&proxy),
        );

        let mut app = App::new(warmup, instance, monitors, worker_setups, worker_failed);
        app.enable_persistent(worker_parked);
        Ok(Self {
            app,
        })
    }

    pub fn into_app(self) -> App {
        self.app
    }
}

impl CaptureSession {
    pub fn new(settings: Arc<CapturerSettings>, memory_hints: MemoryHintsMode) -> anyhow::Result<Self> {
        let t_start = Instant::now();

        let monitors = SystemInterop::all_monitors();
        if monitors.is_empty() {
            anyhow::bail!("no monitors detected; nothing to render to");
        }

        let initial_mouse = SystemInterop::get_mouse_position(&monitors);
        let initial_mouse_f = ScreenPointF::new(initial_mouse.x as f32, initial_mouse.y as f32);
        let instance = Arc::new(create_wgpu_instance());
        let warmup = Arc::new(WarmupTimings::new(t_start, monitors.len()));
        warmup.mark_initialize();

        // ── Warm state: workers spun up once, reused for every cycle ─

        let worker_failed = Arc::new(AtomicUsize::new(0));
        // Only observed by the persistent host; a fresh counter satisfies
        // the worker spawn signature in one-shot mode. No device-lost
        // proxy either — one-shot keeps wgpu's default loss behaviour.
        let worker_parked = Arc::new(AtomicUsize::new(0));
        let worker_setups = spawn_render_workers(&monitors, &instance, &warmup, memory_hints, &worker_failed, &worker_parked, None);

        // ── Per-cycle state: screenshot + walker jobs, fresh latches ─

        // Anchored here — where the per-cycle jobs are spawned — NOT at
        // `start_cycle`, which one-shot mode only reaches after blocking on
        // the screenshot below; anchoring there would put the screenshot
        // and walker durations before the cycle's own t=0.
        let timings = Arc::new(CaptureTimings::new(monitors.len()));
        let ready_count = Arc::new(AtomicUsize::new(0));
        let visible_latch = Arc::new(VisibleLatch::new());
        let cycle_gen = next_cycle_gen();
        let cancelled = Arc::new(AtomicBool::new(false));
        let captured_cursor = SystemInterop::capture_cursor(&monitors);

        let input_txs: Vec<_> = worker_setups
            .iter()
            .map(|s| s.input_tx.clone())
            .collect();
        let render_msg_txs: Vec<_> = worker_setups
            .iter()
            .map(|s| s.render_msg_tx.clone())
            .collect();

        let screenshot_latch = spawn_screenshot_job(ScreenshotJobParams {
            monitors: monitors.clone(),
            cursor: captured_cursor,
            input_txs,
            render_msg_txs: render_msg_txs.clone(),
            peek_enabled: settings.obscured_window_peek_enabled,
            accent_color: settings.accent_color,
            initial_mouse: initial_mouse_f,
            ready_count: ready_count.clone(),
            visible_latch: visible_latch.clone(),
            cycle_gen,
            cancelled: cancelled.clone(),
            timings: timings.clone(),
        });
        let (walker_latch, peek_images_latch) = spawn_walker_job(
            monitors.clone(),
            render_msg_txs,
            cycle_gen,
            settings.obscured_window_peek_enabled,
            settings.obscured_window_detection_threshold,
            timings.clone(),
        );

        // Bounded: an unbounded wait turned any wedged CG capture call into a
        // process that idles forever with nothing on screen and no way to close it
        // from the shell. 30s is far beyond a slow multi-display capture.
        // (Persistent mode replaces this with a non-blocking per-cycle
        // deadline — see `App::try_pick_up_screenshot`.)
        screenshot_latch
            .wait_timeout(Duration::from_secs(30))
            .ok_or_else(|| anyhow!("timed out waiting for the desktop screenshot"))?;

        let mut app = App::new(warmup, instance, monitors, worker_setups, worker_failed);
        app.start_cycle(CycleSetup {
            settings,
            initial_mouse: initial_mouse_f,
            screenshot_latch,
            walker_latch,
            peek_images_latch,
            ready_count,
            visible_latch,
            cycle_gen,
            cancelled,
            timings,
        });

        Ok(Self {
            app,
        })
    }

    pub fn into_app(self) -> App {
        self.app
    }
}

fn create_wgpu_instance() -> wgpu::Instance {
    #[allow(unused_mut)]
    let mut backend_options = wgpu::BackendOptions::default();
    #[cfg(windows)]
    {
        backend_options.dx12.shader_compiler = wgpu::Dx12Compiler::Fxc;
        backend_options.dx12.latency_waitable_object = wgpu::Dx12UseFrameLatencyWaitableObject::Wait;
    }
    #[cfg(windows)]
    let backends = wgpu::Backends::DX12;
    #[cfg(target_os = "macos")]
    let backends = wgpu::Backends::METAL;
    #[cfg(not(any(windows, target_os = "macos")))]
    let backends = wgpu::Backends::VULKAN;

    wgpu::Instance::new(wgpu::InstanceDescriptor {
        backends,
        flags: wgpu::InstanceFlags::DISCARD_HAL_LABELS,
        backend_options,
        ..wgpu::InstanceDescriptor::new_without_display_handle()
    })
}

fn spawn_render_workers(
    monitors: &[MonitorInfo],
    instance: &Arc<wgpu::Instance>,
    warmup: &Arc<WarmupTimings>,
    memory_hints: MemoryHintsMode,
    failed_count: &Arc<AtomicUsize>,
    parked_count: &Arc<AtomicUsize>,
    gpu_lost_proxy: Option<&EventLoopProxy<AppEvent>>,
) -> Vec<WorkerSetup> {
    monitors
        .iter()
        .enumerate()
        .map(|(i, m)| {
            worker::spawn_render_worker(RenderWorkerParams {
                monitor: m.clone(),
                monitor_index: i,
                instance: instance.clone(),
                warmup: warmup.clone(),
                memory_hints,
                failed_count: failed_count.clone(),
                parked_count: parked_count.clone(),
                gpu_lost_proxy: gpu_lost_proxy.cloned(),
            })
        })
        .collect()
}

/// Inputs for one cycle's screenshot job. Everything here is per-cycle
/// (fresh `ready_count`/`visible_latch`, current accent/mouse) except the
/// channel senders, which are the retained worker channels.
pub(crate) struct ScreenshotJobParams {
    pub monitors: Vec<MonitorInfo>,
    pub cursor: Option<CapturedCursor>,
    pub input_txs: Vec<mpsc::Sender<WorkerInput>>,
    pub render_msg_txs: Vec<mpsc::Sender<RenderMsg>>,
    pub peek_enabled: bool,
    pub accent_color: [f32; 4],
    pub initial_mouse: ScreenPointF,
    pub ready_count: Arc<AtomicUsize>,
    pub visible_latch: Arc<VisibleLatch>,
    /// This cycle's generation — stamped on `BeginCycle`'s `CycleParams`
    /// and on the `BlurredDesktop` message so workers can discard output
    /// that outlived its cycle.
    pub cycle_gen: u64,
    /// Shared cancel flag for this cycle (see `CycleParams::cancelled`).
    pub cancelled: Arc<AtomicBool>,
    /// This cycle's debug timings; the screenshot offsets are recorded here
    /// and the `Arc` rides to every worker on `CycleParams`.
    pub timings: Arc<CaptureTimings>,
}

/// Capture the desktop bitmap on a background thread, then broadcast
/// `BeginCycle` (snapshot + per-cycle params) to every render worker and,
/// when peek is enabled, follow up with the blurred-desktop image. Callable
/// once per capture cycle.
pub(crate) fn spawn_screenshot_job(params: ScreenshotJobParams) -> Arc<Latch<Arc<CapturedDesktop>>> {
    let ScreenshotJobParams {
        monitors,
        cursor,
        input_txs,
        render_msg_txs,
        peek_enabled,
        accent_color,
        initial_mouse,
        ready_count,
        visible_latch,
        cycle_gen,
        cancelled,
        timings,
    } = params;

    let screenshot_latch = Arc::new(Latch::new());
    let latch = screenshot_latch.clone();
    std::thread::Builder::new()
        .name("screenshot".into())
        .spawn(move || {
            timings
                .screenshot_start
                .set_once(timings.t_start.elapsed());
            let captured = Arc::new(SystemInterop::capture_desktop_bitmap(monitors, cursor));
            latch.set(captured.clone());
            timings
                .screenshot
                .set_once(timings.t_start.elapsed());
            let cycle = Arc::new(CycleParams {
                snapshot: captured.clone(),
                accent_color,
                initial_mouse,
                ready_count,
                visible_latch,
                cycle_gen,
                cancelled,
                timings: timings.clone(),
            });
            for tx in &input_txs {
                let _ = tx.send(WorkerInput::BeginCycle(cycle.clone()));
            }
            if peek_enabled {
                // stack_blur radius 4 visually approximates the old sigma-2.0 gaussian.
                let (bgra, w, h) = image_extract::blur_desktop_bgra(&captured.bgra, captured.width, captured.height, 4);
                let blurred = Arc::new(BlurredDesktopImage {
                    bgra,
                    width: w,
                    height: h,
                });
                for tx in &render_msg_txs {
                    let _ = tx.send(RenderMsg::BlurredDesktop {
                        cycle_gen,
                        image: blurred.clone(),
                    });
                }
                info!("screenshot: desktop blur complete");
            }
        })
        .expect("spawn screenshot thread");
    screenshot_latch
}

/// Snapshot the window z-order on a background thread and, when peek is
/// enabled, capture obstructed-window images. Callable once per capture
/// cycle.
pub(crate) fn spawn_walker_job(
    monitors: Vec<MonitorInfo>,
    render_msg_txs: Vec<mpsc::Sender<RenderMsg>>,
    cycle_gen: u64,
    peek_enabled: bool,
    visibility_threshold: f32,
    timings: Arc<CaptureTimings>,
) -> (WalkerLatch, PeekImagesLatch) {
    let walker_latch = Arc::new(Latch::new());
    let peek_images_latch = Arc::new(Latch::new());
    let peek_txs = render_msg_txs;
    let latch = walker_latch.clone();
    let peek_latch = peek_images_latch.clone();

    std::thread::Builder::new()
        .name("walker".into())
        .spawn(move || {
            timings
                .walker_start
                .set_once(timings.t_start.elapsed());
            let walker = SystemInterop::snapshot_windows(&monitors, visibility_threshold);
            let obstructed = if peek_enabled { walker.obstructed_windows() } else { Vec::new() };
            latch.set(Arc::new(walker));
            timings
                .walker
                .set_once(timings.t_start.elapsed());

            if !peek_enabled {
                return;
            }

            capture_peek_images(&obstructed, cycle_gen, &peek_txs, &peek_latch);
        })
        .expect("spawn walker thread");

    (walker_latch, peek_images_latch)
}

#[cfg(windows)]
fn capture_peek_images(
    obstructed: &[crate::system::ObstructedWindow],
    cycle_gen: u64,
    peek_txs: &[std::sync::mpsc::Sender<RenderMsg>],
    peek_latch: &Arc<Latch<Vec<Arc<WindowPeekImage>>>>,
) {
    info!("walker: capturing {} obstructed window images", obstructed.len());
    let all_peeks: Vec<Arc<WindowPeekImage>> = std::thread::scope(|s| {
        let handles: Vec<_> = obstructed
            .iter()
            .map(|ow| {
                s.spawn(|| {
                    use windows::Win32::System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED};
                    let _ = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
                    let (bgra, w, h) = SystemInterop::capture_peek_image(ow)?;
                    let crop_x = ow.rect.min_x() - ow.raw_rect.min_x();
                    let crop_y = ow.rect.min_y() - ow.raw_rect.min_y();
                    let peek = Arc::new(WindowPeekImage {
                        window_index: ow.window_index,
                        window_rect: ow.rect,
                        bgra,
                        width: w,
                        height: h,
                        crop_x,
                        crop_y,
                        obstruction_rects: ow.obstruction_rects.clone(),
                    });
                    for tx in peek_txs {
                        let _ = tx.send(RenderMsg::PeekImage {
                            cycle_gen,
                            image: peek.clone(),
                        });
                    }
                    Some(peek)
                })
            })
            .collect();
        handles
            .into_iter()
            .filter_map(|h| h.join().ok().flatten())
            .collect()
    });
    peek_latch.set(all_peeks);
    info!("walker: obstructed window capture complete");
}

#[cfg(target_os = "macos")]
fn capture_peek_images(
    obstructed: &[crate::system::ObstructedWindow],
    cycle_gen: u64,
    peek_txs: &[std::sync::mpsc::Sender<RenderMsg>],
    peek_latch: &Arc<Latch<Vec<Arc<WindowPeekImage>>>>,
) {
    info!("walker: capturing {} obstructed window images", obstructed.len());
    let all_peeks: Vec<Arc<WindowPeekImage>> = std::thread::scope(|s| {
        let handles: Vec<_> = obstructed
            .iter()
            .map(|ow| {
                s.spawn(|| {
                    let (bgra, w, h) = SystemInterop::capture_peek_image(ow)?;
                    let peek = Arc::new(WindowPeekImage {
                        window_index: ow.window_index,
                        window_rect: ow.rect,
                        bgra,
                        width: w,
                        height: h,
                        crop_x: 0,
                        crop_y: 0,
                        obstruction_rects: ow.obstruction_rects.clone(),
                    });
                    for tx in peek_txs {
                        let _ = tx.send(RenderMsg::PeekImage {
                            cycle_gen,
                            image: peek.clone(),
                        });
                    }
                    Some(peek)
                })
            })
            .collect();
        handles
            .into_iter()
            .filter_map(|h| h.join().ok().flatten())
            .collect()
    });
    peek_latch.set(all_peeks);
    info!("walker: obstructed window capture complete");
}
