use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc, Condvar, Mutex};
use std::time::Instant;

use anyhow::{bail, Context, Result};
use obs::audio::AudioInfo;
use obs::context::ObsContext;
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

    speaker_count: usize,
    stop_flag: Arc<AtomicBool>,
    stopped: Arc<(Mutex<bool>, Condvar)>,
}

impl Recorder {
    pub fn new(cli: &Cli, obs_plugin_path: &str) -> Result<Self> {
        let context = ObsContext::new("en-US")
            .context("Failed to initialize OBS")?;

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

        let (out_w, out_h) = compute_output_size(base_w, base_h, cli.max_width, cli.max_height);

        context.reset_video(&VideoInfo {
            base_width: base_w,
            base_height: base_h,
            output_width: out_w,
            output_height: out_h,
            fps_num: cli.fps,
            fps_den: 1,
        }).context("Failed to reset OBS video")?;

        context.reset_audio(&AudioInfo { samples_per_sec: 44100 })
            .context("Failed to reset OBS audio")?;

        if !obs_plugin_path.is_empty() {
            let bin = format!(
                "{obs_plugin_path}/%module%/RelWithDebInfo/%module%.plugin/Contents/MacOS"
            );
            let data = format!(
                "{obs_plugin_path}/%module%/RelWithDebInfo/%module%.plugin/Contents/Resources"
            );
            context.add_module_path(&bin, &data);
        }
        context.load_all_modules();

        let capture_settings = ObsData::new();
        capture_settings.set_int("type", 0);
        capture_settings.set_string("display_uuid", &monitor.uuid);
        capture_settings.set_bool("show_cursor", !cli.no_cursor);

        let capture_source =
            ObsSource::create("screen_capture", "screen", Some(&capture_settings))
                .context("Failed to create screen capture source")?;

        let scene = ObsScene::create("main_scene")
            .context("Failed to create scene")?;
        let scene_item = scene.add(&capture_source);

        unsafe { obs_sys::obs_set_output_source(0, scene.get_source()) };

        let mut sources: Vec<ObsSource> = vec![capture_source];

        for (i, spk_id) in cli.speaker.iter().enumerate() {
            let settings = ObsData::new();
            settings.set_string("device_id", spk_id);
            sources.push(
                ObsSource::create("coreaudio_output_capture", &format!("speaker_{i}"), Some(&settings))
                    .context(format!("Failed to create speaker source for '{spk_id}'"))?,
            );
        }
        let speaker_count = cli.speaker.len();

        for (i, mic_id) in cli.microphone.iter().enumerate() {
            let settings = ObsData::new();
            settings.set_string("device_id", mic_id);
            sources.push(
                ObsSource::create("coreaudio_input_capture", &format!("mic_{i}"), Some(&settings))
                    .context(format!("Failed to create mic source for '{mic_id}'"))?,
            );
        }

        let video_encoder = encoder_config::create_video_encoder(&encoder_config::EncoderConfig {
            hw_accel: cli.hw_accel,
            crf: cli.crf,
            low_cpu: cli.low_cpu,
        }).context("Failed to create video encoder")?;
        video_encoder.set_video(context.get_video());

        let audio_encoder = encoder_config::create_audio_encoder()
            .context("Failed to create audio encoder")?;
        audio_encoder.set_audio(context.get_audio());

        let output_settings = ObsData::new();
        output_settings.set_string("path", cli.output.to_str().unwrap_or("output.mp4"));

        let output = ObsOutput::create("ffmpeg_muxer", "recording", Some(&output_settings))
            .context("Failed to create FFmpeg muxer output")?;
        output.set_video_encoder(&video_encoder);
        output.set_audio_encoder(&audio_encoder, 0);

        let stopped = Arc::new((Mutex::new(false), Condvar::new()));
        let stopped_clone = stopped.clone();
        let signal_stop = SignalConnection::connect(
            output.signal_handler(),
            "stop",
            move || {
                let (lock, cvar) = &*stopped_clone;
                *lock.lock().unwrap() = true;
                cvar.notify_all();
            },
        );

        Ok(Recorder {
            _signal_stop: signal_stop,
            output,
            _video_encoder: video_encoder,
            _audio_encoder: audio_encoder,
            _sources: sources,
            _scene_items: vec![scene_item],
            _scene: scene,
            _context: context,
            speaker_count,
            stop_flag: Arc::new(AtomicBool::new(false)),
            stopped,
        })
    }

    pub fn run(&self, start_paused: bool) -> Result<()> {
        self.output.start().context("Failed to start recording")?;

        if start_paused {
            self.output.pause(true);
            emit_event("recording_paused");
        } else {
            emit_event("recording_started");
        }

        let status_handle = status::start_status_thread(
            self.output.as_ptr(),
            Instant::now(),
            self.stop_flag.clone(),
        );

        // Stdin command reader
        let (cmd_tx, cmd_rx) = mpsc::channel::<Command>();
        let sigint_tx = cmd_tx.clone();
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

        // SIGINT / SIGTERM → Quit
        unsafe {
            libc::signal(libc::SIGINT, signal_handler as *const () as libc::sighandler_t);
            libc::signal(libc::SIGTERM, signal_handler as *const () as libc::sighandler_t);
            SIGNAL_TX.store(
                Box::into_raw(Box::new(sigint_tx)) as *mut u8,
                Ordering::Release,
            );
        }

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
                Command::MuteSpeaker(idx) => {
                    if let Some(source) = self._sources.get(1 + idx) {
                        source.set_muted(true);
                    }
                }
                Command::UnmuteSpeaker(idx) => {
                    if let Some(source) = self._sources.get(1 + idx) {
                        source.set_muted(false);
                    }
                }
                Command::MuteMic(idx) => {
                    if let Some(source) = self._sources.get(1 + self.speaker_count + idx) {
                        source.set_muted(true);
                    }
                }
                Command::UnmuteMic(idx) => {
                    if let Some(source) = self._sources.get(1 + self.speaker_count + idx) {
                        source.set_muted(false);
                    }
                }
            }
        }

        self.output.stop();

        let (lock, cvar) = &*self.stopped;
        let mut done = lock.lock().unwrap();
        while !*done {
            done = cvar
                .wait_timeout(done, std::time::Duration::from_secs(10))
                .unwrap()
                .0;
            if !*done {
                eprintln!("Warning: still waiting for recording to stop...");
            }
        }

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
    out_w &= !1;
    out_h &= !1;
    (out_w.max(2), out_h.max(2))
}

// Signal handling for graceful shutdown
static SIGNAL_TX: std::sync::atomic::AtomicPtr<u8> =
    std::sync::atomic::AtomicPtr::new(std::ptr::null_mut());

extern "C" fn signal_handler(_sig: libc::c_int) {
    let ptr = SIGNAL_TX.load(Ordering::Acquire);
    if !ptr.is_null() {
        let tx = unsafe { &*(ptr as *const mpsc::Sender<Command>) };
        let _ = tx.send(Command::Quit);
    }
}
