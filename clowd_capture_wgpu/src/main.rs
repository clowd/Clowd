mod app;
mod geometry;
mod gpu;
mod img;
mod platform;
mod render;
mod selection;
mod settings;
mod sync;
mod system;
mod ui;

#[macro_use]
extern crate log;

#[macro_use]
extern crate anyhow;

use std::sync::atomic::AtomicUsize;
use std::sync::{Arc, OnceLock};
use std::time::Duration;

fn main() -> anyhow::Result<()> {
    let t_start = std::time::Instant::now();

    let _ = simplelog::TermLogger::init(
        log::LevelFilter::Info,
        simplelog::Config::default(),
        simplelog::TerminalMode::Mixed,
        simplelog::ColorChoice::Auto,
    );

    // One-time platform init (COM, dialog subsystem, etc.).
    system::SystemInterop::init();

    // Enumerate monitors immediately — needed to spawn workers and the
    // screenshot thread.
    let monitors = system::SystemInterop::all_monitors();
    if monitors.is_empty() {
        error!("no monitors detected; nothing to render to");
        std::process::exit(1);
    }

    // Snapshot the cursor position before any window exists.
    let initial_mouse = system::SystemInterop::get_mouse_position(&monitors);
    let initial_mouse_f = geometry::ScreenPointF::new(initial_mouse.x as f32, initial_mouse.y as f32);

    let settings = Arc::new(settings::CapturerSettings::default());

    // Shared wgpu Instance — used by workers (adapter/device) and the
    // main thread (surface creation in resumed()).
    #[allow(unused_mut)]
    let mut backend_options = wgpu::BackendOptions::default();
    #[cfg(windows)]
    {
        backend_options.dx12.latency_waitable_object = wgpu::Dx12UseFrameLatencyWaitableObject::Wait;
    }
    #[cfg(windows)]
    let backends = wgpu::Backends::DX12;
    #[cfg(target_os = "macos")]
    let backends = wgpu::Backends::METAL;
    #[cfg(not(any(windows, target_os = "macos")))]
    let backends = wgpu::Backends::VULKAN;

    let instance = Arc::new(wgpu::Instance::new(wgpu::InstanceDescriptor {
        backends,
        backend_options,
        ..wgpu::InstanceDescriptor::new_without_display_handle()
    }));

    let startup = Arc::new(ui::components::debug::startup::StartupTimings::new(t_start, monitors.len()));
    startup.mark_initialize();

    let shown_time: Arc<OnceLock<Duration>> = Arc::new(OnceLock::new());
    let ready_count = Arc::new(AtomicUsize::new(0));
    let visible_latch = Arc::new(sync::VisibleLatch::new());

    // ── Spawn render workers (one per monitor) ──────────────────────

    let mut worker_setups = Vec::with_capacity(monitors.len());
    for (i, m) in monitors.iter().enumerate() {
        let setup = render::spawn_render_worker(render::RenderWorkerParams {
            monitor: m.clone(),
            monitor_index: i,
            settings: settings.clone(),
            instance: instance.clone(),
            initial_mouse: initial_mouse_f,
            startup: startup.clone(),
            shown_time: shown_time.clone(),
            ready_count: ready_count.clone(),
            visible_latch: visible_latch.clone(),
        });
        worker_setups.push(setup);
    }

    // ── Spawn screenshot thread ─────────────────────────────────────
    // Captures the desktop bitmap and fans out to every worker.

    let screenshot_latch = Arc::new(sync::Latch::new());
    {
        let monitors_for_capture = monitors.clone();
        let input_txs: Vec<_> = worker_setups
            .iter()
            .map(|s| s.input_tx.clone())
            .collect();
        let latch = screenshot_latch.clone();
        let startup_bg = startup.clone();
        std::thread::Builder::new()
            .name("screenshot".into())
            .spawn(move || {
                let captured = Arc::new(system::SystemInterop::capture_desktop_bitmap(monitors_for_capture));
                latch.set(captured.clone());
                startup_bg
                    .background
                    .screenshot
                    .set_once(startup_bg.t_start.elapsed());
                for tx in &input_txs {
                    let _ = tx.send(render::WorkerInput::Screenshot(captured.clone()));
                }
            })
            .expect("spawn screenshot thread");
    }

    // ── Spawn walker thread ─────────────────────────────────────────
    // After enumerating windows, continues on the same thread to
    // PrintWindow each obstructed window and stream peek images to
    // render workers.

    let peek_txs: Vec<_> = worker_setups
        .iter()
        .map(|s| s.render_msg_tx.clone())
        .collect();

    let walker_latch = Arc::new(sync::Latch::new());
    let peek_images_latch: Arc<sync::Latch<Vec<Arc<system::WindowPeekImage>>>> = Arc::new(sync::Latch::new());
    {
        let monitors_for_walker = monitors.clone();
        let latch = walker_latch.clone();
        let peek_latch = peek_images_latch.clone();
        let startup_bg = startup.clone();
        let peek_enabled = settings.obscured_window_peek_enabled;
        let visibility_threshold = settings.obscured_window_detection_threshold;
        let screenshot_latch_for_walker = screenshot_latch.clone();
        std::thread::Builder::new()
            .name("walker".into())
            .spawn(move || {
                let walker = system::SystemInterop::snapshot_windows(&monitors_for_walker, visibility_threshold);
                let obstructed = if peek_enabled { walker.obstructed_windows() } else { Vec::new() };
                latch.set(Arc::new(walker));
                startup_bg
                    .background
                    .walker
                    .set_once(startup_bg.t_start.elapsed());

                if !peek_enabled {
                    return;
                }

                // Pre-blur the desktop screenshot for the peek shader.
                let desktop = screenshot_latch_for_walker.wait();
                let blurred = img::blur_desktop_bgra(&desktop.bgra, desktop.width, desktop.height, 6.0);
                let blurred = Arc::new(render::BlurredDesktopImage {
                    bgra: blurred,
                    width: desktop.width,
                    height: desktop.height,
                });
                for tx in &peek_txs {
                    let _ = tx.send(render::RenderMsg::BlurredDesktop(blurred.clone()));
                }
                info!("walker: desktop blur complete");

                // Capture each obstructed window via PrintWindow and
                // stream results to render workers as they complete.
                #[cfg(windows)]
                {
                    use windows::Win32::System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED};
                    let _ = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };

                    info!("walker: capturing {} obstructed window images", obstructed.len());
                    let mut all_peeks = Vec::new();
                    for ow in &obstructed {
                        if let Some((bgra, w, h)) = system::win_capture::capture_window_image(ow.hwnd, &ow.raw_rect) {
                            let crop_x = ow.rect.min_x() - ow.raw_rect.min_x();
                            let crop_y = ow.rect.min_y() - ow.raw_rect.min_y();
                            let peek = Arc::new(system::WindowPeekImage {
                                window_index: ow.window_index,
                                window_rect: ow.rect,
                                bgra,
                                width: w,
                                height: h,
                                crop_x,
                                crop_y,
                                obstruction_rects: ow.obstruction_rects.clone(),
                            });
                            for tx in &peek_txs {
                                let _ = tx.send(render::RenderMsg::PeekImage(peek.clone()));
                            }
                            all_peeks.push(peek);
                        }
                    }
                    peek_latch.set(all_peeks);
                    info!("walker: obstructed window capture complete");
                }

                #[cfg(target_os = "macos")]
                {
                    info!("walker: capturing {} obstructed window images", obstructed.len());
                    let mut all_peeks = Vec::new();
                    for ow in &obstructed {
                        if let Some((bgra, w, h)) = system::mac_capture::capture_window_image(ow.window_id) {
                            let peek = Arc::new(system::WindowPeekImage {
                                window_index: ow.window_index,
                                window_rect: ow.rect,
                                bgra,
                                width: w,
                                height: h,
                                crop_x: 0,
                                crop_y: 0,
                                obstruction_rects: ow.obstruction_rects.clone(),
                            });
                            for tx in &peek_txs {
                                let _ = tx.send(render::RenderMsg::PeekImage(peek.clone()));
                            }
                            all_peeks.push(peek);
                        }
                    }
                    peek_latch.set(all_peeks);
                    info!("walker: obstructed window capture complete");
                }
            })
            .expect("spawn walker thread");
    }

    // ── Wait for the screenshot before entering the event loop ─────
    // Ensures the desktop bitmap is ready before any windows are
    // created, so macOS windows open with the screenshot already
    // painted (no black flash) and the app doesn't activate early.

    let desktop_buffer = screenshot_latch.wait();

    // ── Start the event loop ────────────────────────────────────────

    let event_loop = winit::event_loop::EventLoop::new()?;
    event_loop.set_control_flow(winit::event_loop::ControlFlow::Poll);

    let mut app = app::App::new(
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
    event_loop.run_app(&mut app)?;
    Ok(())
}
