#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod app;
mod capture;
mod capture_output;
mod gpu;
mod host;
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
    //
    // Persistent mode differs on both counts: stdout is the protocol channel
    // (host::emit), so terminal logging goes to stderr only, and the file
    // mirror is one long-lived --log-dir/capture-host.log (previous run kept
    // as .1) instead of a per-session capture.log.
    let mut loggers: Vec<Box<dyn simplelog::SharedLogger>> = vec![simplelog::TermLogger::new(
        log::LevelFilter::Info,
        simplelog::Config::default(),
        if args.persistent {
            simplelog::TerminalMode::Stderr
        } else {
            simplelog::TerminalMode::Mixed
        },
        simplelog::ColorChoice::Auto,
    )];
    let log_file = if args.persistent {
        args.log_dir.as_ref().and_then(|dir| {
            let _ = std::fs::create_dir_all(dir);
            let path = dir.join("capture-host.log");
            // Keep exactly one previous generation (std::fs::rename
            // replaces an existing .1 on every platform we ship).
            let _ = std::fs::rename(&path, dir.join("capture-host.log.1"));
            std::fs::File::create(path).ok()
        })
    } else {
        args.session_dir
            .as_ref()
            .and_then(|dir| std::fs::File::create(dir.join("capture.log")).ok())
    };
    if let Some(file) = log_file {
        loggers.push(simplelog::WriteLogger::new(
            log::LevelFilter::Info,
            simplelog::Config::default(),
            std::io::LineWriter::new(file),
        ));
    }
    clowd_rust_core::telemetry::install_logger(simplelog::CombinedLogger::new(loggers));

    // held for the rest of main: dropping the guard flushes anything still queued
    let _sentry = clowd_rust_core::telemetry::init("clowd_capture");

    // run() bails out with `?` in several places, and an Err return is not a panic —
    // the hook would never see it. Report it here, then hand it back to the runtime
    // so the exit code and stderr output are unchanged (the shell reads both:
    // ScreenCaptureService.LaunchAsync).
    let result = run(args);
    if let Err(err) = &result {
        clowd_rust_core::telemetry::capture_error(err);
    }
    result
}

fn run(args: settings::CliArgs) -> anyhow::Result<()> {
    system::SystemInterop::init();

    // Before any window exists, so no cycle can end without knowing who to
    // hand foreground rights back to. Applies to both modes: the persistent
    // host serves many cycles for the same shell.
    system::SystemInterop::set_shell_pid(args.shell_pid);

    // The shell preflights this before spawning us and owns the whole permission
    // conversation with the user (Settings → General → Permissions), so all we do
    // here is refuse to run — no prompt, no System Settings, no overlay flashing up
    // over a blank desktop. This still fires if permission was revoked between the
    // shell's check and now.
    if !system::SystemInterop::has_screen_recording_permission() {
        error!("Screen Recording permission has not been granted; refusing to capture");
        std::process::exit(system::EXIT_NO_SCREEN_PERMISSION);
    }

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
        winit::event_loop::EventLoop::<host::AppEvent>::with_user_event()
            .with_activation_policy(ActivationPolicy::Accessory)
            .with_activate_ignoring_other_apps(false)
            .build()?
    };
    #[cfg(not(target_os = "macos"))]
    let event_loop = {
        #[allow(unused_mut)]
        let mut builder = winit::event_loop::EventLoop::<host::AppEvent>::with_user_event();
        // Posted-message observer for display changes; the sent-message
        // hook (the delivery path WM_DISPLAYCHANGE actually takes) is
        // installed by host::display::install below. Persistent only —
        // one-shot mode has no restart contract.
        #[cfg(windows)]
        if args.persistent {
            use winit::platform::windows::EventLoopBuilderExtWindows;
            builder.with_msg_hook(host::display::win_msg_hook);
        }
        builder.build()?
    };

    let memory_hints = args.memory_hints;
    let mut app = if args.persistent {
        // Persistent host: warm up only — every capture's settings arrive
        // with its `show` command (protocol lines on stdin, fed into the
        // event loop by the reader thread). Wait (not Poll) while idle;
        // the app flips to Poll for the duration of each cycle.
        info!("persistent host mode: warming up");
        host::stdin::spawn_stdin_reader(event_loop.create_proxy());
        // Restart-on-topology-change observers (WM_DISPLAYCHANGE hook /
        // CGDisplayRegisterReconfigurationCallback) — persistent only.
        host::display::install(event_loop.create_proxy());
        event_loop.set_control_flow(winit::event_loop::ControlFlow::Wait);
        capture::session::WarmSession::new(event_loop.create_proxy(), memory_hints)?.into_app()
    } else {
        let settings = Arc::new(args.into_settings());
        if let Some(dir) = &settings.session_dir {
            info!("session mode: payload will be written to {:?}", dir);
        }
        event_loop.set_control_flow(winit::event_loop::ControlFlow::Poll);
        capture::session::CaptureSession::new(settings, memory_hints)?.into_app()
    };
    event_loop.run_app(&mut app)?;
    Ok(())
}
