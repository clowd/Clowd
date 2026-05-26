mod cli;
mod commands;
mod encoder_config;
mod platform;
mod recorder;
mod status;

use clap::Parser;

fn main() {
    let cli = cli::Cli::parse();

    let plugin_path = std::env::var("OBS_PLUGIN_PATH")
        .unwrap_or_else(|_| env!("OBS_PLUGIN_DIR").to_string());

    let recorder = match recorder::Recorder::new(&cli, &plugin_path) {
        Ok(r) => r,
        Err(e) => {
            eprintln!("Failed to initialize recorder: {e:#}");
            std::process::exit(1);
        }
    };

    let exit_code = match recorder.run(cli.start_paused) {
        Ok(()) => 0,
        Err(e) => {
            eprintln!("Recording error: {e:#}");
            1
        }
    };

    // Recording is complete and the MP4 is flushed. Exit immediately to skip
    // OBS resource teardown which can segfault (known OBS issue — the C++ version
    // calls ExitProcess for the same reason).
    unsafe { libc::_exit(exit_code) };
}
