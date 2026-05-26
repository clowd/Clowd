mod cli;
mod commands;
mod encoder_config;
mod platform;
mod recorder;
mod status;

use clap::Parser;

fn main() {
    let cli = cli::Cli::parse();

    let (plugin_path, data_path) = find_obs_paths();

    match recorder::Recorder::new(&cli, &plugin_path, &data_path) {
        Ok(recorder) => {
            if let Err(e) = recorder.run(cli.start_paused) {
                eprintln!("Recording error: {e:#}");
                std::process::exit(1);
            }
        }
        Err(e) => {
            eprintln!("Failed to initialize recorder: {e:#}");
            std::process::exit(1);
        }
    }
}

fn find_obs_paths() -> (String, String) {
    // Check env vars first
    if let (Ok(plugin), Ok(data)) = (
        std::env::var("OBS_PLUGIN_PATH"),
        std::env::var("OBS_DATA_PATH"),
    ) {
        return (plugin, data);
    }

    // Find plugins in the OBS build directory
    // The build output has plugins at: obs-build/plugins/<name>/RelWithDebInfo/<name>.plugin
    // We need the parent: obs-build/plugins/*/RelWithDebInfo/
    if let Ok(build_dir) = std::env::var("OBS_BUILD_DIR") {
        return (build_dir.clone(), build_dir);
    }

    // Try to find from build artifacts relative to exe
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            let obs_plugins = dir.join("obs-plugins");
            if obs_plugins.exists() {
                let p = obs_plugins.to_string_lossy().into_owned();
                return (p.clone(), p);
            }
        }
    }

    (String::new(), String::new())
}
