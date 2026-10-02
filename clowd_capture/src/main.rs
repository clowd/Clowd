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
    // Once per process: xdialog's runtime outlives every run of the loop.
    let mut dialog_host = ui::dialogs::install(&event_loop);

    // Linux is one-shot only in this pass: no warm capturer, no hotkey hook
    // (handy-keys' evdev listener wants input-group access the shell cannot
    // assume), so a shell that asks for standby anyway gets a clear refusal
    // rather than a process that idles with no way to trigger it.
    //
    // Host contract note: the refusal exits with CAPTURE_FAILED, which the
    // shell's CaptureStandbySupervisor cannot tell from a crash (it would
    // retry, file a standby-crash report per attempt, then fall back to
    // one-shot). The shell must therefore not construct the supervisor on
    // Linux at all; a distinct "standby unsupported" exit code is the
    // alternative if that ever becomes awkward, but today no such code
    // exists in `clowd_rust_core::exit` and NO_SCREEN_PERMISSION (the only
    // one mapped to a silent fallback) would be a lie.
    #[cfg(target_os = "linux")]
    if args.standby {
        error!("--standby is not supported on Linux; run one capture per process with --session-dir");
        std::process::exit(system::EXIT_CAPTURE_FAILED);
    }

    if !args.standby {
        if let Some(dir) = &args.session_dir {
            logger.begin_session(dir);
        }
        let mut prologue = Prologue {
            logging_ready: process_start.elapsed(),
            ..Prologue::default()
        };
        prologue.sentry_ready = process_start.elapsed();
        let result = run_cycle(args, process_start, prologue, &mut event_loop, dialog_host.as_mut());
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
        let result = run_cycle(args.clone(), t_start, Prologue::default(), &mut event_loop, dialog_host.as_mut());
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
        args.share = false;
    }
}

fn run_cycle(
    args: settings::CliArgs,
    t_start: Instant,
    mut prologue: Prologue,
    event_loop: &mut winit::event_loop::EventLoop<()>,
    dialog_host: Option<&mut xdialog::host::XDialogHost>,
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
    let settings = Arc::new(args.into_settings());
    if let Some(dir) = &settings.session_dir {
        info!("session mode: payload will be written to {:?}", dir);
    }
    // The wgpu backend's GL fallback can only present on the display its
    // instance was built for; the event loop (pinned to X11, see
    // `build_event_loop`) is the one place that display comes from, and
    // the session below builds the instance without ever seeing it.
    #[cfg(target_os = "linux")]
    gxi::set_display_handle(event_loop.owned_display_handle());
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
    // Wrapped so the cycle's retry prompts can be drawn in this run.
    match dialog_host {
        Some(host) => event_loop.run_app_on_demand(&mut host.wrap(&mut app))?,
        None => event_loop.run_app_on_demand(&mut app)?,
    }
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
    // Pinned to X11: every Linux system piece (RandR monitor rectangles,
    // the root-window GetImage, the XFixes cursor, WarpPointer, the X11
    // window type and physical placement) speaks root-window coordinates,
    // and winit would otherwise pick Wayland whenever WAYLAND_DISPLAY is
    // set — placing the overlays wherever the compositor likes while the
    // photograph and the pointer are in X11 space. Under a Wayland session
    // this makes the process an XWayland client (only X clients appear in
    // the shot, but the geometry is coherent); the shell routes real
    // Wayland sessions to clowd_capture_wayland instead.
    #[cfg(target_os = "linux")]
    {
        use winit::platform::x11::EventLoopBuilderExtX11;
        Ok(winit::event_loop::EventLoop::builder()
            .with_x11()
            .build()?)
    }
    #[cfg(not(any(target_os = "macos", target_os = "linux")))]
    Ok(winit::event_loop::EventLoop::new()?)
}
