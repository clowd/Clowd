mod cli;
mod commands;
mod encoder_config;
mod platform;
mod recorder;
mod region;
mod status;
mod tracker;

use clap::Parser;

fn main() {
    // Route every libobs log line to stderr before anything else can touch
    // libobs — stdout is reserved for the JSON protocol (§1.3). Also installs
    // the crash handler (stderr + exit 1 instead of libobs's silent exit 0).
    obs::log::install_handlers();

    // clap itself exits 2 on invalid arguments; mirror that for the §1.1
    // validations it cannot express.
    let cli = cli::Cli::parse();
    if let Err(msg) = cli.validate() {
        eprintln!("Error: {msg}");
        platform::exit_process(2);
    }

    // Recorder::new exits the process itself on any construction failure (it
    // must not unwind — partial OBS state is never torn down, §1.4).
    let recorder = recorder::Recorder::new(&cli);

    // Never returns: every exit routes through platform::exit_process, which
    // skips libobs teardown intentionally (known OBS shutdown crashes; the C++
    // original calls ExitProcess for the same reason).
    recorder.run(cli.pause)
}
