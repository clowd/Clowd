#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod app;
mod capture;
mod capture_output;
mod geometry;
mod gpu;
mod image_extract;
mod interaction;
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

use clap::Parser;

fn main() -> anyhow::Result<()> {
    let args = settings::CliArgs::parse();

    // Terminal output plus, in session mode, a mirror into the session dir: when the
    // shell spawns us from an installed .app, stdout goes to /dev/null and stderr is
    // only read back after a non-zero exit, so the file is the only diagnostics that
    // survive a hang. LineWriter flushes each record, so the log is current even if
    // the process dies without unwinding.
    let mut loggers: Vec<Box<dyn simplelog::SharedLogger>> = vec![simplelog::TermLogger::new(
        log::LevelFilter::Info,
        simplelog::Config::default(),
        simplelog::TerminalMode::Mixed,
        simplelog::ColorChoice::Auto,
    )];
    if let Some(dir) = &args.session_dir {
        if let Ok(file) = std::fs::File::create(dir.join("capture.log")) {
            loggers.push(simplelog::WriteLogger::new(
                log::LevelFilter::Info,
                simplelog::Config::default(),
                std::io::LineWriter::new(file),
            ));
        }
    }
    telemetry::crash::install_logger(simplelog::CombinedLogger::new(loggers));

    // held for the rest of main: dropping the guard flushes anything still queued
    let _sentry = telemetry::crash::init();

    // run() bails out with `?` in several places, and an Err return is not a panic —
    // the hook would never see it. Report it here, then hand it back to the runtime
    // so the exit code and stderr output are unchanged (the shell reads both:
    // ScreenCaptureService.LaunchAsync).
    let result = run(args);
    if let Err(err) = &result {
        telemetry::crash::capture_error(err);
    }
    result
}

fn run(args: settings::CliArgs) -> anyhow::Result<()> {
    system::SystemInterop::init();

    // The shell preflights this before spawning us and owns the whole permission
    // conversation with the user (Settings → General → Permissions), so all we do
    // here is refuse to run — no prompt, no System Settings, no overlay flashing up
    // over a blank desktop. This still fires if permission was revoked between the
    // shell's check and now.
    if !system::SystemInterop::has_screen_recording_permission() {
        error!("Screen Recording permission has not been granted; refusing to capture");
        std::process::exit(system::EXIT_NO_SCREEN_PERMISSION);
    }

    let settings = Arc::new(args.into_settings());
    if let Some(dir) = &settings.session_dir {
        info!("session mode: payload will be written to {:?}", dir);
    }
    let session = capture::session::CaptureSession::new(settings)?;

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
    event_loop.set_control_flow(winit::event_loop::ControlFlow::Poll);

    let mut app = session.into_app();
    event_loop.run_app(&mut app)?;
    Ok(())
}
