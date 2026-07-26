//! Pipeline construction and the command run loop (DESIGN §2.4).
//!
//! Failure policy: any error during construction prints to stderr and exits
//! via `platform::exit_process(1)` directly — the error paths never unwind, so
//! no destructors of partial OBS state run (libobs teardown is intentionally
//! skipped, §1.4).

use std::ffi::CString;
use std::fmt::Display;
use std::io::BufRead;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc};
use std::time::{Duration, Instant};

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
use crate::encoder_config::{self, EncoderConfig};
use crate::platform;
use crate::region::{self, Rect};
use crate::status::{self, RecordingClock};

/// Overall deadline waiting for the OBS stop signal after `quit` (§1.4).
const STOP_DEADLINE: Duration = Duration::from_secs(30);
const STOP_WARN_INTERVAL: Duration = Duration::from_secs(10);

fn fail(msg: impl Display) -> ! {
    eprintln!("Fatal: {msg}");
    platform::exit_process(1)
}

/// §1.1 argument validations exit 2 (§1.4), distinguishing caller bugs from
/// recording/init failures for clients that key off the exit code.
fn fail_args(msg: impl Display) -> ! {
    eprintln!("Fatal: {msg}");
    platform::exit_process(2)
}

pub struct Recorder {
    output: ObsOutput,
    speakers: Vec<ObsSource>,
    mics: Vec<ObsSource>,
    _display_sources: Vec<ObsSource>,
    _scene_items: Vec<ObsSceneItem>,
    _scene: ObsScene,
    _video_encoder: ObsEncoder,
    _audio_encoder: ObsEncoder,
    _sig_start: SignalConnection,
    _sig_stop: SignalConnection,
    _context: ObsContext,
    cmd_tx: mpsc::Sender<Command>,
    cmd_rx: mpsc::Receiver<Command>,
}

impl Recorder {
    /// Builds the whole OBS pipeline. Order matters: the libobs data path MUST
    /// be registered before `obs_reset_video` (graphics init loads
    /// `default.effect` etc. through `obs_find_data_file`, whose built-in
    /// fallback is CWD-relative and resolves nowhere in our layout).
    pub fn new(cli: &Cli) -> Recorder {
        // 1. Log/crash handlers were installed first thing in main (stdout must
        //    stay protocol-only from the first libobs line).
        platform::init_process();
        let context = match ObsContext::new("en-US") {
            Ok(c) => c,
            Err(e) => fail(format_args!("Failed to initialize OBS: {e}")),
        };

        // 2. Paths first (§2.4 step 2).
        let exe_dir = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|p| p.to_path_buf()))
            .unwrap_or_else(|| fail("Could not determine the executable directory"));
        let paths = platform::default_obs_paths(&exe_dir);
        if let Some(ref libobs_data) = paths.libobs_data {
            context.add_data_path(libobs_data);
        }

        // 3. Resolve region.
        let monitors = platform::enumerate_monitors();
        if monitors.is_empty() {
            fail("No displays found");
        }
        let capture_region = if let Some(ref region_str) = cli.region {
            match region::parse_region(region_str) {
                Ok(r) => r,
                Err(e) => fail_args(e),
            }
        } else if let Some(ref monitor_id) = cli.monitor {
            match platform::find_monitor(monitor_id) {
                Some(m) => Rect {
                    x: m.x,
                    y: m.y,
                    w: m.width,
                    h: m.height,
                },
                None => fail_args(format_args!("Monitor '{monitor_id}' not found")),
            }
        } else {
            let primary = monitors
                .iter()
                .find(|m| m.is_primary)
                .unwrap_or(&monitors[0]);
            Rect {
                x: primary.x,
                y: primary.y,
                w: primary.width,
                h: primary.height,
            }
        };
        let plan = match region::plan_region(capture_region, &monitors) {
            Ok(p) => p,
            Err(e) => fail_args(e),
        };

        // 4. Video (canvas = region plan, output = single-pass scaled).
        let (out_w, out_h) =
            region::compute_output_size(plan.canvas, cli.max_width, cli.max_height);
        let video_info = VideoInfo {
            graphics_module: platform::GRAPHICS_MODULE,
            base_width: plan.canvas.0,
            base_height: plan.canvas.1,
            output_width: out_w,
            output_height: out_h,
            fps_num: cli.fps,
            fps_den: 1,
        };
        if let Err(e) = context.reset_video(&video_info) {
            fail(format_args!("Failed to reset OBS video: {e}"));
        }

        // 5. Audio.
        if let Err(e) = context.reset_audio(&AudioInfo {
            samples_per_sec: 44100,
        }) {
            fail(format_args!("Failed to reset OBS audio: {e}"));
        }

        // 6. Modules + sanity checks.
        context.add_module_path(&paths.module_bin, &paths.module_data);
        context.load_all_modules();
        let encoder_types = obs::properties::enum_encoder_types();
        if encoder_types.is_empty() {
            fail(format_args!("No OBS encoders registered after module load — no usable plugins were found.\n  module \
                               bin:  {}\n  module data: {}",
                              paths.module_bin, paths.module_data));
        }
        // NOT obs_source_create != null: libobs creates a placeholder source for
        // unknown ids; get_display_name returns null exactly when unregistered.
        let display_capture_c = CString::new(platform::DISPLAY_CAPTURE_ID).unwrap();
        let display_name =
            unsafe { obs_sys::obs_source_get_display_name(display_capture_c.as_ptr()) };
        if display_name.is_null() {
            fail(format_args!(
                "Display capture source '{}' is not registered — the capture plugin failed to \
                               load.\n  module bin:  {}\n  module data: {}",
                platform::DISPLAY_CAPTURE_ID,
                paths.module_bin,
                paths.module_data
            ));
        }

        // 7. Scene: one display-capture item per intersected monitor, offset
        //    onto the region canvas.
        let scene = match ObsScene::create("main") {
            Ok(s) => s,
            Err(e) => fail(format_args!("Failed to create scene: {e}")),
        };
        let mut display_sources = Vec::new();
        let mut scene_items = Vec::new();
        for (i, item) in plan.items.iter().enumerate() {
            let m = &monitors[item.monitor_index];
            let settings = platform::display_capture_settings(m, !cli.no_cursor);
            let source = match ObsSource::create(
                platform::DISPLAY_CAPTURE_ID,
                &format!("display_{i}"),
                Some(&settings),
            ) {
                Ok(s) => s,
                Err(e) => fail(format_args!(
                    "Failed to create display capture for monitor '{}': {e}",
                    m.id
                )),
            };
            let scene_item = scene.add(&source);
            scene_item.set_pos(item.pos.0, item.pos.1);
            scene_item.set_scale(item.scale, item.scale);
            display_sources.push(source);
            scene_items.push(scene_item);
        }
        context.set_output_source_raw(0, scene.get_source());

        // 8. Audio sources on output channels 1..=N (speakers first, then mics,
        //    in command-line order).
        let mut channel: u32 = 1;
        let mut speakers = Vec::new();
        for (i, device_id) in cli.speaker.iter().enumerate() {
            let settings = ObsData::new();
            settings.set_string("device_id", device_id);
            let source = match ObsSource::create(
                platform::AUDIO_OUTPUT_CAPTURE_ID,
                &format!("speaker_{i}"),
                Some(&settings),
            ) {
                Ok(s) => s,
                Err(e) => fail(format_args!(
                    "Failed to create speaker source for '{device_id}': {e}"
                )),
            };
            context.set_output_source(channel, Some(&source));
            channel += 1;
            speakers.push(source);
        }
        let mut mics = Vec::new();
        for (i, device_id) in cli.microphone.iter().enumerate() {
            let settings = ObsData::new();
            settings.set_string("device_id", device_id);
            let source = match ObsSource::create(
                platform::AUDIO_INPUT_CAPTURE_ID,
                &format!("mic_{i}"),
                Some(&settings),
            ) {
                Ok(s) => s,
                Err(e) => fail(format_args!(
                    "Failed to create microphone source for '{device_id}': {e}"
                )),
            };
            context.set_output_source(channel, Some(&source));
            channel += 1;
            mics.push(source);
        }

        // 9. Encoders + ffmpeg_muxer output.
        let video_encoder = match encoder_config::create_video_encoder(
            &encoder_types,
            &EncoderConfig {
                hw_accel: cli.hw_accel,
                crf: cli.crf,
                low_cpu: cli.low_cpu,
            },
        ) {
            Ok(e) => e,
            Err(e) => fail(format_args!("Failed to create video encoder: {e}")),
        };
        video_encoder.set_video(context.get_video());
        let audio_encoder = match encoder_config::create_audio_encoder(&encoder_types) {
            Ok(e) => e,
            Err(e) => fail(format_args!("Failed to create audio encoder: {e}")),
        };
        audio_encoder.set_audio(context.get_audio());

        let output_settings = ObsData::new();
        output_settings.set_string("path", &cli.output.to_string_lossy());
        let output = match ObsOutput::create("ffmpeg_muxer", "recording", Some(&output_settings)) {
            Ok(o) => o,
            Err(e) => fail(format_args!("Failed to create FFmpeg muxer output: {e}")),
        };
        output.set_video_encoder(&video_encoder);
        output.set_audio_encoder(&audio_encoder, 0);

        // 10. Signals → command-loop injection.
        let (cmd_tx, cmd_rx) = mpsc::channel::<Command>();
        let start_tx = cmd_tx.clone();
        let sig_start = SignalConnection::connect(output.signal_handler(), "start", move || {
            let _ = start_tx.send(Command::OutputStarted);
        });
        let stop_tx = cmd_tx.clone();
        let sig_stop =
            SignalConnection::connect_with_code(output.signal_handler(), "stop", move |code| {
                let _ = stop_tx.send(Command::OutputStopped(code));
            });

        Recorder {
            output,
            speakers,
            mics,
            _display_sources: display_sources,
            _scene_items: scene_items,
            _scene: scene,
            _video_encoder: video_encoder,
            _audio_encoder: audio_encoder,
            _sig_start: sig_start,
            _sig_stop: sig_stop,
            _context: context,
            cmd_tx,
            cmd_rx,
        }
    }

    /// The command loop. Never returns — every path ends in
    /// `platform::exit_process` (§1.4).
    pub fn run(&self, pause: bool) -> ! {
        status::emit_simple("initialized");

        self.spawn_stdin_thread();
        self.install_signal_handler();

        let mut start_requested = false;
        let mut started = false;
        let mut paused = false;
        let mut clock: Option<Arc<RecordingClock>> = None;
        let status_stop = Arc::new(AtomicBool::new(false));
        let mut status_handle: Option<std::thread::JoinHandle<()>> = None;

        // Without --pause the output starts immediately; with --pause we sit in
        // initialized-wait mode until stdin `start`.
        if !pause {
            self.start_output();
            start_requested = true;
        }

        loop {
            let cmd = match self.cmd_rx.recv() {
                Ok(c) => c,
                // Unreachable in practice (self holds a sender), but never spin.
                Err(_) => fail("Command channel closed unexpectedly"),
            };
            match cmd {
                Command::Start => {
                    if !start_requested {
                        self.start_output();
                        start_requested = true;
                    } else if started && paused {
                        if self.output.pause(false) {
                            paused = false;
                            if let Some(ref c) = clock {
                                c.resume();
                            }
                            status::emit_simple("recording_resumed");
                        } else {
                            eprintln!("Failed to unpause output");
                        }
                    }
                    // Otherwise (already started / start pending): ignored.
                }
                Command::Pause => {
                    if started && !paused {
                        if self.output.pause(true) {
                            paused = true;
                            if let Some(ref c) = clock {
                                c.pause();
                            }
                            status::emit_simple("recording_paused");
                        } else {
                            eprintln!("Failed to pause output");
                        }
                    }
                }
                Command::OutputStarted => {
                    if !started {
                        started = true;
                        status::emit_simple("started_recording");
                        let c = Arc::new(RecordingClock::new());
                        status_handle = Some(status::start_status_thread(
                            self.output.as_ptr(),
                            c.clone(),
                            status_stop.clone(),
                        ));
                        clock = Some(c);
                    }
                }
                Command::MuteSpeaker(idx) => self.set_muted(&self.speakers, "speaker", idx, true),
                Command::UnmuteSpeaker(idx) => {
                    self.set_muted(&self.speakers, "speaker", idx, false)
                }
                Command::MuteMic(idx) => self.set_muted(&self.mics, "microphone", idx, true),
                Command::UnmuteMic(idx) => self.set_muted(&self.mics, "microphone", idx, false),
                Command::OutputStopped(code) => {
                    // Spontaneous stop (disk full, encoder error, …): surface it
                    // immediately and exit — do not wait for `quit` (§1.3).
                    self.finish(code, &status_stop, status_handle.take());
                }
                Command::Quit => {
                    if !start_requested {
                        // Cancelled before recording started (§1.2).
                        status::emit_json(serde_json::json!({
                            "type": "stopped_recording",
                            "code": 0,
                            "message": "Cancelled before recording started",
                            "error": null,
                        }));
                        platform::exit_process(0);
                    }
                    self.output.stop();
                    let deadline = Instant::now() + STOP_DEADLINE;
                    loop {
                        let remaining = deadline.saturating_duration_since(Instant::now());
                        if remaining.is_zero() {
                            // Synthetic timeout stop (§1.4).
                            self.finish(-99, &status_stop, status_handle.take());
                        }
                        match self.cmd_rx.recv_timeout(remaining.min(STOP_WARN_INTERVAL)) {
                            Ok(Command::OutputStopped(code)) => {
                                self.finish(code, &status_stop, status_handle.take());
                            }
                            Ok(_) => {} // ignore anything else while stopping
                            Err(mpsc::RecvTimeoutError::Timeout) => {
                                eprintln!("Warning: still waiting for the recording to stop...");
                            }
                            Err(mpsc::RecvTimeoutError::Disconnected) => {
                                self.finish(-99, &status_stop, status_handle.take());
                            }
                        }
                    }
                }
            }
        }
    }

    /// Starts the output; on failure emits `stopped_recording` code -4 with the
    /// output's last error and exits 1.
    fn start_output(&self) {
        if let Err(e) = self.output.start() {
            eprintln!("Failed to start recording: {e}");
            status::emit_stopped_recording(-4, self.output.get_last_error());
            platform::exit_process(1);
        }
    }

    fn set_muted(&self, sources: &[ObsSource], kind: &str, idx: usize, muted: bool) {
        match sources.get(idx) {
            Some(source) => source.set_muted(muted),
            None => eprintln!("Ignoring mute command: no {kind} at index {idx}"),
        }
    }

    /// Emits the final `stopped_recording` line and exits (code 0 → exit 0,
    /// anything else → exit 1). Always the last JSON line before exit.
    fn finish(
        &self,
        code: i64,
        status_stop: &Arc<AtomicBool>,
        status_handle: Option<std::thread::JoinHandle<()>>,
    ) -> ! {
        status_stop.store(true, Ordering::Relaxed);
        if let Some(handle) = status_handle {
            // ≤1 s: guarantees no status line can print after stopped_recording.
            let _ = handle.join();
        }
        let error = if code == 0 {
            None
        } else {
            self.output.get_last_error()
        };
        status::emit_stopped_recording(code, error);
        platform::exit_process(if code == 0 { 0 } else { 1 })
    }

    /// stdin reader: line-oriented commands; EOF is equivalent to `quit` (the
    /// orphan-safety mechanism — a dead parent closes the pipe).
    fn spawn_stdin_thread(&self) {
        let tx = self.cmd_tx.clone();
        std::thread::spawn(move || {
            let stdin = std::io::stdin();
            let mut lines = stdin.lock().lines();
            loop {
                match lines.next() {
                    Some(Ok(line)) => {
                        if line.trim().is_empty() {
                            continue;
                        }
                        match commands::parse_command(&line) {
                            Some(cmd) => {
                                if tx.send(cmd).is_err() {
                                    break;
                                }
                            }
                            None => eprintln!("Ignoring unknown command: {}", line.trim()),
                        }
                    }
                    // EOF or read error == quit.
                    _ => {
                        let _ = tx.send(Command::Quit);
                        break;
                    }
                }
            }
        });
    }

    /// CTRL_C/CTRL_BREAK/CTRL_CLOSE (Windows) / SIGINT+SIGTERM (POSIX) behave
    /// exactly like stdin `quit` (§1.5).
    ///
    /// Windows uses a directly-registered `SetConsoleCtrlHandler` routine, NOT
    /// the ctrlc crate: for CTRL_CLOSE/LOGOFF/SHUTDOWN the OS grace period
    /// lasts only while the HandlerRoutine itself is executing — returning
    /// from it terminates the process immediately. ctrlc's registered routine
    /// returns in microseconds (it only signals a worker thread), which would
    /// forfeit the grace window and leave the mp4 unflushed. Our routine sends
    /// `quit` and then blocks; the stop sequence terminates the process via
    /// `exit_process` underneath it.
    #[cfg(windows)]
    fn install_signal_handler(&self) {
        console_ctrl::install(self.cmd_tx.clone());
    }

    #[cfg(not(windows))]
    fn install_signal_handler(&self) {
        let tx = self.cmd_tx.clone();
        let result = ctrlc::set_handler(move || {
            let _ = tx.send(Command::Quit);
            loop {
                std::thread::sleep(Duration::from_secs(60));
            }
        });
        if let Err(e) = result {
            eprintln!("Warning: failed to install console signal handler: {e}");
        }
    }
}

#[cfg(windows)]
mod console_ctrl {
    use std::sync::{mpsc, OnceLock};
    use std::time::Duration;

    use windows_sys::core::BOOL;
    use windows_sys::Win32::Foundation::TRUE;
    use windows_sys::Win32::System::Console::SetConsoleCtrlHandler;

    use crate::commands::Command;

    static QUIT_TX: OnceLock<mpsc::Sender<Command>> = OnceLock::new();

    /// Runs on an OS-injected thread. Never returns: for CTRL_CLOSE the grace
    /// period (~5 s) ends the moment this routine returns, so it blocks while
    /// the command loop stops the output and exits the process (§1.5).
    unsafe extern "system" fn handler(_ctrl_type: u32) -> BOOL {
        if let Some(tx) = QUIT_TX.get() {
            let _ = tx.send(Command::Quit);
        }
        loop {
            std::thread::sleep(Duration::from_secs(60));
        }
    }

    pub fn install(tx: mpsc::Sender<Command>) {
        let _ = QUIT_TX.set(tx);
        if unsafe { SetConsoleCtrlHandler(Some(handler), TRUE) } == 0 {
            eprintln!("Warning: failed to install console signal handler");
        }
    }
}
