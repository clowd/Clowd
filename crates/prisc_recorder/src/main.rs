mod cameras;
mod cli;
mod commands;
mod cursor_sprite;
mod encoder_config;
mod frame_clock;
mod input_capture;
mod input_hook;
mod platform;
mod recorder;
mod region;
mod settings;
mod status;
mod tracker;
mod tracks;
mod webcam;
mod window_capture;

use std::ops::ControlFlow;
use std::sync::mpsc;

use clap::Parser;
use prisc_ipc::catalog::{recorder as wire, ErrorCode};

use commands::Command;

fn main() {
    // `hello` first, before libobs is initialised or called, and with stdout
    // claimed: from here on every libobs, FFmpeg or plugin print to fd 1 lands
    // on stderr, and only the protocol channel reaches the pipe (HELPER_PROTOCOL.md §3.2).
    status::init(prisc_ipc::open::<wire::Event>("prisc_recorder"));
    // Every exit goes through prisc_ipc::lifetime::exit, never a drop of this
    // guard, so the session is ended and the queue flushed by the exit hook.
    let _sentry = prisc_rust_core::telemetry::init("prisc_recorder");
    prisc_ipc::lifetime::on_exit(prisc_rust_core::telemetry::shutdown_hook);
    // `_exit` / `ExitProcess`: libobs teardown is intentionally skipped (known
    // OBS shutdown crashes; the C++ original calls ExitProcess for the same
    // reason).
    prisc_ipc::lifetime::set_exit_fn(platform::exit_process);

    // Route every libobs log line to stderr before anything else can touch
    // libobs. Also installs the crash handler (stderr + exit 1 instead of
    // libobs's silent exit 0).
    prisc_obs::log::install_handlers();

    // clap itself exits 2 on invalid arguments, with nothing on stdout beyond
    // `hello`.
    let cli = cli::Cli::parse();

    // --list-cameras: no recording pipeline and no commands. Enumerate, emit
    // `cameras`, exit. A quit only arms the hard-exit timer.
    if cli.list_cameras {
        prisc_ipc::lifetime::install(|_| {}, wire::QUIT_GRACE + prisc_ipc::SELF_EXIT_MARGIN);
        cameras::run();
    }

    // The command queue exists before the pipeline: `quit`, EOF and signals
    // must work during construction (picker mode waits on the user there).
    // The quit path posts into the same queue stdin feeds, so a command sent
    // before `quit` is handled before the stop.
    let (cmd_tx, cmd_rx) = mpsc::channel::<Command>();
    let quit_tx = cmd_tx.clone();
    prisc_ipc::lifetime::install(
        move |_| {
            let _ = quit_tx.send(Command::Quit);
        },
        wire::QUIT_GRACE + prisc_ipc::SELF_EXIT_MARGIN,
    );
    let stdin_tx = cmd_tx.clone();
    prisc_ipc::read_commands::<wire::Command>(move |inbound| {
        match stdin_tx.send(commands::from_wire(inbound)) {
            Ok(()) => ControlFlow::Continue(()),
            Err(_) => ControlFlow::Break(()),
        }
    });

    let settings = match cli.validate() {
        Ok(s) => s,
        Err(msg) => status::out().fatal(ErrorCode::Usage, msg),
    };

    // Recorder::new exits the process itself on any construction failure (it
    // must not unwind: partial OBS state is never torn down, §1.4).
    let mut recorder = recorder::Recorder::new(&cli, settings, cmd_tx, cmd_rx);

    // Never returns: every exit routes through prisc_ipc::lifetime::exit.
    recorder.run(cli.pause)
}
