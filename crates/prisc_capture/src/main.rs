#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod accent;
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
mod protocol;
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

#[macro_use]
extern crate log;

#[macro_use]
extern crate anyhow;

use std::ops::ControlFlow;
use std::sync::Arc;
use std::time::Instant;

use clap::Parser;
use prisc_ipc::catalog::capture::{self as wire, CaptureResult, Event};
use prisc_ipc::catalog::ErrorCode;
use prisc_ipc::Inbound;

use app::CycleAction;
use telemetry::startup::Prologue;

fn main() {
    // The very first statement of the process: every offset in the startup
    // report is measured from here, so clap, the logger, sentry and the
    // permission check are inside the measurement instead of hidden in front
    // of it. Anything moved above this line becomes invisible to the
    // benchmark.
    let process_start = Instant::now();
    // `hello` before anything else, with stdout claimed: from here on only the
    // protocol channel reaches the pipe.
    protocol::init(prisc_ipc::open::<Event>("prisc_capture"));
    // This is a process-lifetime scheduling posture. Standby spends its idle
    // time blocked in the event loop, so retaining it does not consume CPU and
    // avoids changing priority around every capture cycle.
    system::raise_process_priority_class();
    let mut args = settings::CliArgs::parse();
    let logger = cycle_logger::CycleLogger::install();
    // Every exit goes through prisc_ipc::lifetime::exit, never a drop of this
    // guard, so the session is ended and the queue flushed by the exit hook.
    let _sentry = prisc_rust_core::telemetry::init("prisc_capture");
    prisc_ipc::lifetime::on_exit(prisc_rust_core::telemetry::shutdown_hook);
    system::SystemInterop::init();
    let mut event_loop = match build_event_loop() {
        Ok(event_loop) => event_loop,
        Err(err) => fatal(ErrorCode::InitFailed, err),
    };

    if !args.standby {
        one_shot(args, process_start, logger, &mut event_loop);
    }

    let mut standby = match standby::Standby::new(event_loop.create_proxy()) {
        Ok(standby) => standby,
        Err(err) => fatal(ErrorCode::InitFailed, err),
    };
    loop {
        match standby.wait(&mut args, &mut event_loop) {
            Ok(true) => {}
            // The quit path fired between captures.
            Ok(false) => finish(CaptureResult::Stopped, None),
            Err(err) => fatal(ErrorCode::Failed, err),
        }
        let t_start = Instant::now();
        let session_dir = args
            .session_dir
            .clone()
            .expect("standby creates a session");
        logger.begin_session(&session_dir);
        let result = run_cycle(args.clone(), t_start, Prologue::default(), &mut event_loop);
        logger.end_session();
        if let Err(err) = result {
            fatal(ErrorCode::Failed, err);
        }
        protocol::out().emit(&Event::CaptureFinished {
            dir: session_dir.to_string_lossy().into_owned(),
        });
        args.session_dir = None;
        args.capture_mode = settings::CaptureMode::Region;
        args.video = false;
        args.share = false;
    }
}

/// One capture, then exit. The outputs are the session directory's files, as
/// always; the protocol adds the terminal `done`, and stdin is a lease: a dead
/// parent (EOF) or a `quit` ends the overlay instead of orphaning it.
fn one_shot(
    args: settings::CliArgs,
    process_start: Instant,
    logger: Arc<cycle_logger::CycleLogger>,
    event_loop: &mut winit::event_loop::EventLoop<()>,
) -> ! {
    // The overlay owns the main thread, so the quit path cannot wait for it:
    // it ends the process from a thread of its own, with the session unwritten
    // (an overlay closed underneath the user is a cancel).
    prisc_ipc::lifetime::install(
        |reason| {
            let spawned = std::thread::Builder::new()
                .name("capture-quit".into())
                .spawn(move || {
                    info!("quit ({reason:?}) during a one-shot capture; closing the overlay");
                    finish(CaptureResult::Cancelled, None)
                });
            if spawned.is_err() {
                prisc_ipc::lifetime::exit(prisc_ipc::exit::OK);
            }
        },
        wire::QUIT_GRACE + prisc_ipc::SELF_EXIT_MARGIN,
    );
    // A one-shot run takes no commands; it only answers a malformed line.
    prisc_ipc::read_commands::<wire::Command>(|inbound| {
        match inbound {
            Inbound::Command(command) => warn!("ignoring {command:?}: a one-shot capture takes no commands"),
            Inbound::Malformed {
                reason,
            } => protocol::out().error(ErrorCode::BadCommand, reason, false),
        }
        ControlFlow::Continue(())
    });

    if let Some(dir) = &args.session_dir {
        logger.begin_session(dir);
    }
    let session_dir = args.session_dir.clone();
    let mut prologue = Prologue {
        logging_ready: process_start.elapsed(),
        ..Prologue::default()
    };
    prologue.sentry_ready = process_start.elapsed();
    let result = run_cycle(args, process_start, prologue, event_loop);
    logger.end_session();
    match result {
        Ok(CycleAction::Canceled) => finish(CaptureResult::Cancelled, None),
        Ok(_) => finish(CaptureResult::Finished, session_dir.map(|dir| dir.to_string_lossy().into_owned())),
        Err(err) => fatal(ErrorCode::Failed, err),
    }
}

/// The terminal `done`, then exit 0. Also called from the quit path's own
/// thread (see `one_shot` and `Standby::new`), racing the main thread.
pub(crate) fn finish(result: CaptureResult, dir: Option<String>) -> ! {
    protocol::claim_terminal();
    protocol::out().emit(&Event::Done {
        result,
        dir,
    });
    prisc_ipc::lifetime::exit(prisc_ipc::exit::OK)
}

/// A fatal `error` (reported to Sentry first), then exit 1.
fn fatal(code: ErrorCode, err: anyhow::Error) -> ! {
    protocol::claim_terminal();
    prisc_rust_core::telemetry::capture_error(&err);
    protocol::out().fatal(code, format!("{err:#}"))
}

fn run_cycle(
    args: settings::CliArgs,
    t_start: Instant,
    mut prologue: Prologue,
    event_loop: &mut winit::event_loop::EventLoop<()>,
) -> anyhow::Result<CycleAction> {
    // Before any window exists, so the cycle cannot end without knowing who to
    // hand foreground rights back to.
    system::SystemInterop::set_shell_pid(args.shell_pid);
    prologue.system_init = t_start.elapsed();

    // The shell preflights this before spawning us and owns the whole permission
    // conversation with the user (Settings → System → Permissions), so all we do
    // here is refuse to run — no prompt, no System Settings, no overlay flashing up
    // over a blank desktop. This still fires if permission was revoked between the
    // shell's check and now.
    if !system::SystemInterop::has_screen_recording_permission() {
        error!("Screen Recording permission has not been granted; refusing to capture");
        protocol::fail_exit(
            ErrorCode::Permission,
            "Screen Recording permission has not been granted",
            system::EXIT_NO_SCREEN_PERMISSION,
        );
    }
    prologue.permission_checked = t_start.elapsed();

    // All the slow work — monitors, GPU instance, render workers, the desktop
    // screenshot — happens before the event loop exists, so the overlay windows
    // can be created against state that is already warm.
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
    let finished = app.finished();
    drop(app);
    fatal.map(|()| finished)
}

fn build_event_loop() -> anyhow::Result<winit::event_loop::EventLoop<()>> {
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
    // winit takes the process's keyboard and mouse raw input when the loop is built, and Windows
    // gives a process one raw-input target per device. Nothing here reads DeviceEvents, and the
    // standby hotkey hook's watchdog (prisc_hotkeys) needs keyboard raw input to notice a hook
    // Windows removed silently — so hand it back before the hook starts.
    event_loop.listen_device_events(winit::event_loop::DeviceEvents::Never);
    Ok(event_loop)
}
