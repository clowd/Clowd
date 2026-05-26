use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc, Condvar, Mutex};
use std::time::Instant;

use anyhow::{bail, Context, Result};
use obs::context::ObsContext;
use obs::audio::AudioInfo;
use obs::data::ObsData;
use obs::encoder::ObsEncoder;
use obs::output::ObsOutput;
use obs::scene::{ObsScene, ObsSceneItem};
use obs::signal::SignalConnection;
use obs::source::ObsSource;
use obs::video::VideoInfo;

use crate::cli::Cli;
use crate::commands::{self, Command};
use crate::encoder_config;
use crate::status;

pub struct Recorder {
    _signal_stop: SignalConnection,
    output: ObsOutput,
    _video_encoder: ObsEncoder,
    _audio_encoder: ObsEncoder,
    _sources: Vec<ObsSource>,
    _scene_items: Vec<ObsSceneItem>,
    _scene: ObsScene,
    _context: ObsContext,

    stop_flag: Arc<AtomicBool>,
    stopped: Arc<(Mutex<bool>, Condvar)>,
}

impl Recorder {
    pub fn new(cli: &Cli, obs_plugin_path: &str, obs_data_path: &str) -> Result<Self> {
        // 1. Initialize OBS
        let context = ObsContext::new("en-US")
            .context("Failed to initialize OBS")?;

        // 2. Determine capture dimensions
        #[cfg(target_os = "macos")]
        let monitor = if let Some(ref id) = cli.monitor {
            crate::platform::macos::find_monitor(id)
                .context(format!("Monitor '{id}' not found"))?
        } else {
            crate::platform::macos::get_primary_monitor()
                .context("No primary monitor found")?
        };

        let (base_w, base_h) = if let Some(ref region) = cli.region {
            parse_region(region)?
        } else {
            (monitor.width, monitor.height)
        };

        let (out_w, out_h) = compute_output_size(
            base_w,
            base_h,
            cli.max_width,
            cli.max_height,
        );

        // 3. Reset video
        let video_info = VideoInfo {
            base_width: base_w,
            base_height: base_h,
            output_width: out_w,
            output_height: out_h,
            fps_num: cli.fps,
            fps_den: 1,
        };
        context.reset_video(&video_info)
            .context("Failed to reset OBS video")?;

        // 4. Reset audio
        let audio_info = AudioInfo {
            samples_per_sec: 44100,
        };
        context.reset_audio(&audio_info)
            .context("Failed to reset OBS audio")?;

        // 5. Load plugins
        // OBS searches for plugins using glob patterns with %module% placeholder.
        // With Xcode build, plugins are at: plugins/<name>/RelWithDebInfo/<name>.plugin
        // parse_binary_from_directory replaces %module%, appends '/', then appends the module name.
        // So bin path should resolve to: .../Contents/MacOS/<name>
        if !obs_plugin_path.is_empty() {
            let bin_pattern = format!(
                "{obs_plugin_path}/%module%/RelWithDebInfo/%module%.plugin/Contents/MacOS"
            );
            let data_pattern = format!(
                "{obs_plugin_path}/%module%/RelWithDebInfo/%module%.plugin/Contents/Resources"
            );
            context.add_module_path(&bin_pattern, &data_pattern);
        }
        context.load_all_modules();

        // 6. Create screen capture source
        let capture_settings = ObsData::new();
        capture_settings.set_int("type", 0); // Display capture
        capture_settings.set_string("display_uuid", &monitor.uuid);
        capture_settings.set_bool("show_cursor", !cli.no_cursor);

        let capture_source = ObsSource::create("screen_capture", "screen", Some(&capture_settings))
            .context("Failed to create screen capture source")?;

        // 7. Create scene and add source
        let scene = ObsScene::create("main_scene")
            .context("Failed to create scene")?;

        let scene_item = scene.add(&capture_source);

        // Set the scene as the main output source
        unsafe {
            obs_sys::obs_set_output_source(0, scene.get_source());
        }

        // 8. Create audio sources
        let mut sources: Vec<ObsSource> = vec![capture_source];

        for (i, spk_id) in cli.speaker.iter().enumerate() {
            let settings = ObsData::new();
            settings.set_string("device_id", spk_id);
            let source = ObsSource::create(
                "coreaudio_output_capture",
                &format!("speaker_{i}"),
                Some(&settings),
            ).context(format!("Failed to create speaker source for '{spk_id}'"))?;
            sources.push(source);
        }

        for (i, mic_id) in cli.microphone.iter().enumerate() {
            let settings = ObsData::new();
            settings.set_string("device_id", mic_id);
            let source = ObsSource::create(
                "coreaudio_input_capture",
                &format!("mic_{i}"),
                Some(&settings),
            ).context(format!("Failed to create mic source for '{mic_id}'"))?;
            sources.push(source);
        }

        // 9. Create video encoder
        let enc_config = encoder_config::EncoderConfig {
            hw_accel: cli.hw_accel,
            crf: cli.crf,
            low_cpu: cli.low_cpu,
            output_width: out_w,
            output_height: out_h,
        };
        let video_encoder = encoder_config::create_video_encoder(&enc_config)
            .context("Failed to create video encoder")?;
        video_encoder.set_video(context.get_video());

        // 10. Create audio encoder
        let audio_encoder = encoder_config::create_audio_encoder()
            .context("Failed to create audio encoder")?;
        audio_encoder.set_audio(context.get_audio());

        // 11. Create output
        let output_settings = ObsData::new();
        output_settings.set_string("path", cli.output.to_str().unwrap_or("output.mp4"));

        let output = ObsOutput::create("ffmpeg_muxer", "recording", Some(&output_settings))
            .context("Failed to create FFmpeg muxer output")?;

        output.set_video_encoder(&video_encoder);
        output.set_audio_encoder(&audio_encoder, 0);

        // 12. Set up stop signal
        let stopped = Arc::new((Mutex::new(false), Condvar::new()));
        let stopped_clone = stopped.clone();
        let signal_stop = SignalConnection::connect(
            output.signal_handler(),
            "stop",
            move || {
                let (lock, cvar) = &*stopped_clone;
                let mut done = lock.lock().unwrap();
                *done = true;
                cvar.notify_all();
            },
        );

        let stop_flag = Arc::new(AtomicBool::new(false));

        Ok(Recorder {
            _signal_stop: signal_stop,
            output,
            _video_encoder: video_encoder,
            _audio_encoder: audio_encoder,
            _sources: sources,
            _scene_items: vec![scene_item],
            _scene: scene,
            _context: context,
            stop_flag,
            stopped,
        })
    }

    pub fn run(&self, start_paused: bool) -> Result<()> {
        // Start recording
        if !start_paused {
            self.output.start()
                .context("Failed to start recording")?;
            emit_event("recording_started");
        } else {
            self.output.start()
                .context("Failed to start recording")?;
            self.output.pause(true);
            emit_event("recording_paused");
        }

        // Start status thread
        let status_handle = status::start_status_thread(
            self.output.as_ptr(),
            Instant::now(),
            self.stop_flag.clone(),
        );

        // Command loop on stdin
        let (cmd_tx, cmd_rx) = mpsc::channel::<Command>();
        let _stdin_handle = std::thread::spawn(move || {
            let stdin = std::io::stdin();
            let mut line = String::new();
            loop {
                line.clear();
                if stdin.read_line(&mut line).unwrap_or(0) == 0 {
                    let _ = cmd_tx.send(Command::Quit);
                    break;
                }
                if let Some(cmd) = commands::parse_command(&line) {
                    if cmd_tx.send(cmd).is_err() {
                        break;
                    }
                }
            }
        });

        // Process commands
        for cmd in cmd_rx {
            match cmd {
                Command::Start => {
                    if !self.output.active() {
                        self.output.start().context("Failed to start")?;
                    } else {
                        self.output.pause(false);
                    }
                    emit_event("recording_started");
                }
                Command::Pause => {
                    self.output.pause(true);
                    emit_event("recording_paused");
                }
                Command::Quit => {
                    emit_event("recording_stopping");
                    break;
                }
                Command::MuteSpeaker(idx) | Command::MuteMic(idx) => {
                    let offset = match cmd {
                        Command::MuteSpeaker(_) => 1, // speakers start after capture source
                        Command::MuteMic(_) => 1 + self._sources.len().saturating_sub(2),
                        _ => unreachable!(),
                    };
                    if let Some(source) = self._sources.get(offset + idx) {
                        source.set_muted(true);
                    }
                }
                Command::UnmuteSpeaker(idx) | Command::UnmuteMic(idx) => {
                    let offset = match cmd {
                        Command::UnmuteSpeaker(_) => 1,
                        Command::UnmuteMic(_) => 1 + self._sources.len().saturating_sub(2),
                        _ => unreachable!(),
                    };
                    if let Some(source) = self._sources.get(offset + idx) {
                        source.set_muted(false);
                    }
                }
            }
        }

        // Stop recording
        self.output.stop();

        // Wait for stop signal
        let (lock, cvar) = &*self.stopped;
        let mut done = lock.lock().unwrap();
        while !*done {
            done = cvar.wait_timeout(done, std::time::Duration::from_secs(10))
                .unwrap().0;
            if !*done {
                eprintln!("Warning: still waiting for recording to stop...");
            }
        }

        // Stop status thread
        self.stop_flag.store(true, Ordering::Relaxed);
        let _ = status_handle.join();

        emit_event("recording_stopped");
        Ok(())
    }
}

fn emit_event(event: &str) {
    let msg = serde_json::json!({"type": "event", "event": event});
    println!("{msg}");
}

fn parse_region(region: &str) -> Result<(u32, u32)> {
    let parts: Vec<&str> = region.split(',').collect();
    if parts.len() != 4 {
        bail!("Region must be x,y,w,h");
    }
    let w: u32 = parts[2].trim().parse().context("Invalid region width")?;
    let h: u32 = parts[3].trim().parse().context("Invalid region height")?;
    Ok((w, h))
}

fn compute_output_size(
    base_w: u32,
    base_h: u32,
    max_width: Option<u32>,
    max_height: Option<u32>,
) -> (u32, u32) {
    let mut out_w = base_w;
    let mut out_h = base_h;

    if let Some(max_w) = max_width {
        if out_w > max_w {
            let scale = max_w as f64 / out_w as f64;
            out_w = max_w;
            out_h = (out_h as f64 * scale) as u32;
        }
    }

    if let Some(max_h) = max_height {
        if out_h > max_h {
            let scale = max_h as f64 / out_h as f64;
            out_h = max_h;
            out_w = (out_w as f64 * scale) as u32;
        }
    }

    // OBS requires even dimensions
    out_w = out_w & !1;
    out_h = out_h & !1;

    (out_w.max(2), out_h.max(2))
}
