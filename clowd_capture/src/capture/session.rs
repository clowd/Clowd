use std::sync::atomic::AtomicUsize;
use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant};

use crate::app::App;
use crate::geometry::ScreenPointF;
use crate::image_extract;
use crate::render::protocol::{BlurredDesktopImage, RenderMsg, WorkerInput};
use crate::render::worker::{self, RenderWorkerParams, WorkerSetup};
use crate::settings::CapturerSettings;
use crate::sync::{Latch, VisibleLatch};
use crate::system::{CapturedCursor, CapturedDesktop, MonitorInfo, SystemInterop, WindowPeekImage, WindowWalker};
use crate::telemetry::startup::StartupTimings;

type WalkerLatch = Arc<Latch<Arc<WindowWalker>>>;
type PeekImagesLatch = Arc<Latch<Vec<Arc<WindowPeekImage>>>>;

pub struct CaptureSession {
    app: App,
}

impl CaptureSession {
    pub fn new(settings: Arc<CapturerSettings>) -> anyhow::Result<Self> {
        let t_start = Instant::now();

        let monitors = SystemInterop::all_monitors();
        if monitors.is_empty() {
            anyhow::bail!("no monitors detected; nothing to render to");
        }

        let initial_mouse = SystemInterop::get_mouse_position(&monitors);
        let initial_mouse_f = ScreenPointF::new(initial_mouse.x as f32, initial_mouse.y as f32);
        let instance = Arc::new(create_wgpu_instance());
        let startup = Arc::new(StartupTimings::new(t_start, monitors.len()));
        startup.mark_initialize();

        let shown_time: Arc<OnceLock<Duration>> = Arc::new(OnceLock::new());
        let ready_count = Arc::new(AtomicUsize::new(0));
        let visible_latch = Arc::new(VisibleLatch::new());

        let captured_cursor = SystemInterop::capture_cursor(&monitors);

        let worker_setups = spawn_render_workers(
            &monitors,
            &settings,
            &instance,
            initial_mouse_f,
            &startup,
            &shown_time,
            &ready_count,
            &visible_latch,
        );

        let screenshot_latch = spawn_screenshot_job(
            monitors.clone(),
            captured_cursor,
            &worker_setups,
            settings.obscured_window_peek_enabled,
            startup.clone(),
        );
        let (walker_latch, peek_images_latch) = spawn_walker_job(
            monitors.clone(),
            &worker_setups,
            settings.obscured_window_peek_enabled,
            settings.obscured_window_detection_threshold,
            startup.clone(),
        );

        // Bounded: an unbounded wait turned any wedged CG capture call into a
        // process that idles forever with nothing on screen and no way to close it
        // from the shell. 30s is far beyond a slow multi-display capture.
        let desktop_buffer = screenshot_latch
            .wait_timeout(Duration::from_secs(30))
            .ok_or_else(|| anyhow!("timed out waiting for the desktop screenshot"))?;
        let app = App::new(
            settings,
            startup,
            instance,
            monitors,
            initial_mouse_f,
            worker_setups,
            desktop_buffer,
            walker_latch,
            peek_images_latch,
            ready_count,
            visible_latch,
            shown_time,
        );

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

#[allow(clippy::too_many_arguments)]
fn spawn_render_workers(
    monitors: &[MonitorInfo],
    settings: &Arc<CapturerSettings>,
    instance: &Arc<wgpu::Instance>,
    initial_mouse: ScreenPointF,
    startup: &Arc<StartupTimings>,
    shown_time: &Arc<OnceLock<Duration>>,
    ready_count: &Arc<AtomicUsize>,
    visible_latch: &Arc<VisibleLatch>,
) -> Vec<WorkerSetup> {
    monitors
        .iter()
        .enumerate()
        .map(|(i, m)| {
            worker::spawn_render_worker(RenderWorkerParams {
                monitor: m.clone(),
                monitor_index: i,
                settings: settings.clone(),
                instance: instance.clone(),
                initial_mouse,
                startup: startup.clone(),
                shown_time: shown_time.clone(),
                ready_count: ready_count.clone(),
                visible_latch: visible_latch.clone(),
            })
        })
        .collect()
}

fn spawn_screenshot_job(
    monitors: Vec<MonitorInfo>,
    cursor: Option<CapturedCursor>,
    worker_setups: &[WorkerSetup],
    peek_enabled: bool,
    startup: Arc<StartupTimings>,
) -> Arc<Latch<Arc<CapturedDesktop>>> {
    let screenshot_latch = Arc::new(Latch::new());
    let input_txs: Vec<_> = worker_setups
        .iter()
        .map(|s| s.input_tx.clone())
        .collect();
    let render_msg_txs: Vec<_> = worker_setups
        .iter()
        .map(|s| s.render_msg_tx.clone())
        .collect();
    let latch = screenshot_latch.clone();
    std::thread::Builder::new()
        .name("screenshot".into())
        .spawn(move || {
            startup
                .background
                .screenshot_start
                .set_once(startup.t_start.elapsed());
            let captured = Arc::new(SystemInterop::capture_desktop_bitmap(monitors, cursor));
            latch.set(captured.clone());
            startup
                .background
                .screenshot
                .set_once(startup.t_start.elapsed());
            for tx in &input_txs {
                let _ = tx.send(WorkerInput::Screenshot(captured.clone()));
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
                    let _ = tx.send(RenderMsg::BlurredDesktop(blurred.clone()));
                }
                info!("screenshot: desktop blur complete");
            }
        })
        .expect("spawn screenshot thread");
    screenshot_latch
}

fn spawn_walker_job(
    monitors: Vec<MonitorInfo>,
    worker_setups: &[WorkerSetup],
    peek_enabled: bool,
    visibility_threshold: f32,
    startup: Arc<StartupTimings>,
) -> (WalkerLatch, PeekImagesLatch) {
    let walker_latch = Arc::new(Latch::new());
    let peek_images_latch = Arc::new(Latch::new());
    let peek_txs: Vec<_> = worker_setups
        .iter()
        .map(|s| s.render_msg_tx.clone())
        .collect();
    let latch = walker_latch.clone();
    let peek_latch = peek_images_latch.clone();

    std::thread::Builder::new()
        .name("walker".into())
        .spawn(move || {
            startup
                .background
                .walker_start
                .set_once(startup.t_start.elapsed());
            let walker = SystemInterop::snapshot_windows(&monitors, visibility_threshold);
            let obstructed = if peek_enabled { walker.obstructed_windows() } else { Vec::new() };
            latch.set(Arc::new(walker));
            startup
                .background
                .walker
                .set_once(startup.t_start.elapsed());

            if !peek_enabled {
                return;
            }

            capture_peek_images(&obstructed, &peek_txs, &peek_latch);
        })
        .expect("spawn walker thread");

    (walker_latch, peek_images_latch)
}

#[cfg(windows)]
fn capture_peek_images(
    obstructed: &[crate::system::ObstructedWindow],
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
                        let _ = tx.send(RenderMsg::PeekImage(peek.clone()));
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
                        let _ = tx.send(RenderMsg::PeekImage(peek.clone()));
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
