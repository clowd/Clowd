mod cameras;
mod cli;
mod commands;
mod cursor_sprite;
mod encoder_config;
mod frame_clock;
mod input_capture;
mod input_hook;
mod recorder;
mod settings;
mod status;
mod tracker;
mod tracks;
mod webcam;
mod window_capture;

use clap::Parser;
// The platform layer and the region math live in prisc_obs_platform, shared with
// prisc_share_region; every module here reaches them as `crate::platform` and
// `crate::region`.
use prisc_obs_platform::region;
use prisc_obs_platform as platform;

fn main() {
    // Route every libobs log line to stderr before anything else can touch
    // libobs — stdout is reserved for the JSON protocol (§1.3). Also installs
    // the crash handler (stderr + exit 1 instead of libobs's silent exit 0).
    prisc_obs::log::install_handlers();

    // clap itself exits 2 on invalid arguments; mirror that for the §1.1
    // validations it cannot express (including a bad --settings file).
    let cli = cli::Cli::parse();

    // --list-cameras: no recording pipeline — enumerate, print one JSON line,
    // exit. Never returns.
    if cli.list_cameras {
        cameras::run();
    }

    let settings = match cli.validate() {
        Ok(s) => s,
        Err(msg) => {
            eprintln!("Error: {msg}");
            platform::exit_process(2);
        }
    };

    // Recorder::new exits the process itself on any construction failure (it
    // must not unwind — partial OBS state is never torn down, §1.4).
    let mut recorder = recorder::Recorder::new(&cli, settings);

    // Never returns: every exit routes through platform::exit_process, which
    // skips libobs teardown intentionally (known OBS shutdown crashes; the C++
    // original calls ExitProcess for the same reason).
    recorder.run(cli.pause)
}
