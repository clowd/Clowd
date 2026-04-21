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
    let initial_mouse = system::SystemInterop::get_mouse_position();
    let initial_mouse_f =
        geometry::ScreenPointF::new(initial_mouse.x as f32, initial_mouse.y as f32);

    let settings = Arc::new(settings::CapturerSettings::default());

    // Shared wgpu Instance — used by workers (adapter/device) and the
    // main thread (surface creation in resumed()).
    #[allow(unused_mut)]
    let mut backend_options = wgpu::BackendOptions::default();
    #[cfg(windows)]
    {
        backend_options.dx12.latency_waitable_object =
            wgpu::Dx12UseFrameLatencyWaitableObject::Wait;
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

    let startup = Arc::new(
        ui::components::debug::startup::StartupTimings::new(t_start, monitors.len()),
    );
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
                let captured = Arc::new(system::SystemInterop::capture_desktop_bitmap(
                    monitors_for_capture,
                ));
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

    let walker_latch = Arc::new(sync::Latch::new());
    {
        let latch = walker_latch.clone();
        let startup_bg = startup.clone();
        std::thread::Builder::new()
            .name("walker".into())
            .spawn(move || {
                let walker = system::SystemInterop::snapshot_windows();
                latch.set(Arc::new(walker));
                startup_bg
                    .background
                    .walker
                    .set_once(startup_bg.t_start.elapsed());
            })
            .expect("spawn walker thread");
    }

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
        screenshot_latch,
        walker_latch,
        ready_count,
        visible_latch,
        shown_time,
    );
    event_loop.run_app(&mut app)?;
    Ok(())
}
