#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod app;
mod capture;
mod capture_output;
mod filename_pattern;
mod gpu;
mod image_extract;
mod interaction;
mod ocr;
mod render;
mod selection;
mod session_output;
mod settings;
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
    let t_start = Instant::now();
    let args = settings::CliArgs::parse();

    // Terminal output plus a mirror into the session dir: when the shell spawns us
    // from an installed .app, stdout goes to /dev/null and stderr is only read back
    // after a non-zero exit, so the file is the only diagnostics that survive a hang
    // — or a native fault, which never reaches a Rust error path at all. LineWriter
    // flushes each record, so the log is current even if the process dies without
    // unwinding.
    let mut loggers: Vec<Box<dyn simplelog::SharedLogger>> = vec![simplelog::TermLogger::new(
        log::LevelFilter::Info,
        simplelog::Config::default(),
        simplelog::TerminalMode::Mixed,
        simplelog::ColorChoice::Auto,
    )];
    let log_file = args
        .session_dir
        .as_ref()
        .and_then(|dir| std::fs::File::create(dir.join("capture.log")).ok());
    if let Some(file) = log_file {
        loggers.push(simplelog::WriteLogger::new(
            log::LevelFilter::Info,
            simplelog::Config::default(),
            std::io::LineWriter::new(file),
        ));
    }
    clowd_rust_core::telemetry::install_logger(simplelog::CombinedLogger::new(loggers));

    // Stashed rather than marked: `StartupTimings` is sized by the monitor
    // count and cannot exist until the session enumerates them, which is
    // several stages from here.
    let mut prologue = Prologue {
        logging_ready: t_start.elapsed(),
        ..Prologue::default()
    };

    // held for the rest of main: dropping the guard flushes anything still queued
    let _sentry = clowd_rust_core::telemetry::init("clowd_capture");
    prologue.sentry_ready = t_start.elapsed();

    // run() bails out with `?` in several places, and an Err return is not a panic —
    // the hook would never see it. Report it here, then hand it back to the runtime
    // so the exit code and stderr output are unchanged (the shell reads both:
    // ScreenCaptureService.LaunchAsync).
    let result = run(args, t_start, prologue);
    if let Err(err) = &result {
        clowd_rust_core::telemetry::capture_error(err);
    }
    result
}

fn run(args: settings::CliArgs, t_start: Instant, mut prologue: Prologue) -> anyhow::Result<()> {
    system::SystemInterop::init();

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

    // All the slow work — monitors, wgpu instance, render workers, the desktop
    // screenshot — happens before the event loop exists, so the overlay windows
    // can be created against state that is already warm.
    let memory_hints = args.memory_hints;
    // Read before the workers exist: `Features::TIMESTAMP_QUERY` is a device
    // creation parameter, so this must be set before the first
    // `request_adapter_device`. Not on `CapturerSettings` — nothing after
    // device creation consults it.
    ui::gpu::gpu_timing::set_gpu_timing_enabled(args.gpu_timing);
    let settings = Arc::new(args.into_settings());
    if let Some(dir) = &settings.session_dir {
        info!("session mode: payload will be written to {:?}", dir);
    }
    let session = capture::session::CaptureSession::new(settings, memory_hints, t_start)?;
    let timings = session.timings().clone();
    timings.apply_prologue(prologue);

    // Accessory keeps us out of the dock and stops the overlay stealing activation
    // before it is shown; focus is taken explicitly once every window is ready.
    // Do NOT "harden" this to NSApplicationActivationPolicy::Prohibited: when the
    // binary runs from inside an .app bundle (the installed layout — but not any
    // `cargo run`), a pre-event-loop Prohibited poisons the window-server session
    // and orderFrontRegardless() silently never puts windows on screen, even after
    // winit switches the policy back to Accessory at applicationDidFinishLaunching.
    #[cfg(target_os = "macos")]
    let event_loop = {
        use winit::platform::macos::{ActivationPolicy, EventLoopBuilderExtMacOS};
        winit::event_loop::EventLoop::builder()
            .with_activation_policy(ActivationPolicy::Accessory)
            .with_activate_ignoring_other_apps(false)
            .build()?
    };
    #[cfg(not(target_os = "macos"))]
    let event_loop = winit::event_loop::EventLoop::new()?;

    timings.mark_event_loop_built();

    event_loop.set_control_flow(winit::event_loop::ControlFlow::Poll);
    let mut app = session.into_app();
    timings.mark_run_app_entered();
    event_loop.run_app(&mut app)?;
    // A failure detected inside the event loop (the screenshot deadline)
    // exits the loop cleanly and parks its error here — surface it so the
    // shell still sees a non-zero exit, as it did when the wait was a
    // blocking `?` in CaptureSession::new.
    app.fatal_result()
}
