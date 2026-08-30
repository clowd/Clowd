#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod app;
mod capture;
mod capture_output;
mod cycle_logger;
mod filename_pattern;
mod gpu;
mod gxi;
mod image_extract;
mod interaction;
mod ocr;
mod render;
mod selection;
mod session_output;
mod settings;
// Also include!()'d by build.rs; some items (ShaderDef, ALL_SHADERS) are
// build-script-only, hence the allow.
#[allow(dead_code)]
mod shader_bindings;
mod standby;
mod standby_hotkeys;
mod sync;
mod system;
mod telemetry;
mod ui;
mod ui_state;

#[macro_use]
extern crate log;

#[macro_use]
extern crate anyhow;

use std::sync::Arc;
use std::time::Instant;

use clap::Parser;

use telemetry::startup::Prologue;

fn main() -> anyhow::Result<()> {
    // The very first statement of the process: every offset in the startup
    // report is measured from here, so clap, the logger, sentry and the
    // permission check are inside the measurement instead of hidden in front
    // of it. Anything moved above this line becomes invisible to the
    // benchmark.
    let process_start = Instant::now();
    // This is a process-lifetime scheduling posture. Standby spends its idle
    // time blocked in the event loop, so retaining it does not consume CPU and
    // avoids changing priority around every capture cycle.
    system::raise_process_priority_class();
    let mut args = settings::CliArgs::parse();
    let logger = cycle_logger::CycleLogger::install();
    let _sentry = clowd_rust_core::telemetry::init("clowd_capture");
    system::SystemInterop::init();
    let mut event_loop = build_event_loop()?;

    if !args.standby {
        if let Some(dir) = &args.session_dir {
            logger.begin_session(dir);
        }
        let mut prologue = Prologue {
            logging_ready: process_start.elapsed(),
            ..Prologue::default()
        };
        prologue.sentry_ready = process_start.elapsed();
        let result = run_cycle(args, process_start, prologue, &mut event_loop);
        if let Err(err) = &result {
            clowd_rust_core::telemetry::capture_error(err);
        }
        logger.end_session();
        return result;
    }

    let mut standby = standby::Standby::new(event_loop.create_proxy())?;
    loop {
        if !standby.wait(&mut args, &mut event_loop)? {
            return Ok(());
        }
        let t_start = Instant::now();
        let session_dir = args
            .session_dir
            .clone()
            .expect("standby creates a session");
        logger.begin_session(&session_dir);
        let result = run_cycle(args.clone(), t_start, Prologue::default(), &mut event_loop);
        if let Err(err) = result {
            clowd_rust_core::telemetry::capture_error(&err);
            logger.end_session();
            return Err(err);
        }
        logger.end_session();
        standby::emit!("CLOWD_CAPTURE_FINISHED {}", session_dir.display());
        args.session_dir = None;
        args.capture_mode = settings::CaptureMode::Region;
        args.video = false;
    }
}

fn run_cycle(
    args: settings::CliArgs,
    t_start: Instant,
    mut prologue: Prologue,
    event_loop: &mut winit::event_loop::EventLoop<()>,
) -> anyhow::Result<()> {
    // Before any window exists, so the cycle cannot end without knowing who to
    // hand foreground rights back to.
    system::SystemInterop::set_shell_pid(args.shell_pid);
    prologue.system_init = t_start.elapsed();

    // The shell preflights this before spawning us and owns the whole permission
    // conversation with the user (Settings → General → Permissions), so all we do
    // here is refuse to run — no prompt, no System Settings, no overlay flashing up
    // over a blank desktop. This still fires if permission was revoked between the
    // shell's check and now.
    if !system::SystemInterop::has_screen_recording_permission() {
        error!("Screen Recording permission has not been granted; refusing to capture");
        std::process::exit(system::EXIT_NO_SCREEN_PERMISSION);
    }
    prologue.permission_checked = t_start.elapsed();

    // All the slow work — monitors, GPU instance, render workers, the desktop
    // screenshot — happens before the event loop exists, so the overlay windows
    // can be created against state that is already warm.
    // Set before the workers exist: each render worker reads the switch
    // once, in its `GpuTimings::new` (see `gxi::set_gpu_timing_enabled`).
    // Not on `CapturerSettings`: nothing after worker startup consults
    // it.
    gxi::set_gpu_timing_enabled(args.gpu_timing);
    let settings = Arc::new(args.into_settings());
    if let Some(dir) = &settings.session_dir {
        info!("session mode: payload will be written to {:?}", dir);
    }
    let session = capture::session::CaptureSession::new(settings, t_start)?;
    let timings = session.timings().clone();
    timings.apply_prologue(prologue);

    // Accessory keeps us out of the dock and stops the overlay stealing activation
    // before it is shown; focus is taken explicitly once every window is ready.
    // Do NOT "harden" this to NSApplicationActivationPolicy::Prohibited: when the
    // binary runs from inside an .app bundle (the installed layout — but not any
    // `cargo run`), a pre-event-loop Prohibited poisons the window-server session
    // and orderFrontRegardless() silently never puts windows on screen, even after
    // winit switches the policy back to Accessory at applicationDidFinishLaunching.
    timings.mark_event_loop_built();

    event_loop.set_control_flow(winit::event_loop::ControlFlow::Poll);
    let mut app = session.into_app();
    timings.mark_run_app_entered();
    use winit::platform::run_on_demand::EventLoopExtRunOnDemand;
    event_loop.run_app_on_demand(&mut app)?;
    // A failure detected inside the event loop (the screenshot deadline)
    // exits the loop cleanly and parks its error here — surface it so the
    // shell still sees a non-zero exit, as it did when the wait was a
    // blocking `?` in CaptureSession::new.
    let fatal = app.fatal_result();
    drop(app);
    fatal
}

fn build_event_loop() -> anyhow::Result<winit::event_loop::EventLoop<()>> {
    #[cfg(target_os = "macos")]
    {
        use winit::platform::macos::{ActivationPolicy, EventLoopBuilderExtMacOS};
        Ok(winit::event_loop::EventLoop::builder()
            .with_activation_policy(ActivationPolicy::Accessory)
            .with_activate_ignoring_other_apps(false)
            .build()?)
    }
    #[cfg(not(target_os = "macos"))]
    Ok(winit::event_loop::EventLoop::new()?)
}
