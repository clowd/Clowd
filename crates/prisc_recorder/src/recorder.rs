//! Pipeline construction and the command run loop (DESIGN §2.4).
//!
//! Failure policy: any error during construction prints to stderr and exits
//! via `platform::exit_process(1)` directly — the error paths never unwind, so
//! no destructors of partial OBS state run (libobs teardown is intentionally
//! skipped, §1.4). Runtime `configure` failures never exit: they ack with
//! `configure_error` and let the parent decide.

use std::ffi::CString;
use std::fmt::Display;
use std::io::BufRead;
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{mpsc, Arc, Mutex};
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
use obs::volmeter::ObsVolmeter;

use crate::cli::{Cli, MAX_AUDIO_SOURCES};
use crate::commands::{self, Command};
use crate::encoder_config::{self, EncoderConfig};
use crate::input_capture::InputCapture;
use crate::platform;
use crate::region::{self, Rect};
use crate::settings::Settings;
use crate::status::{self, LevelPeaks, RecordingClock};
use crate::tracker::{self, MouseTracker};
use crate::tracks::{self, AudioTrack, MAX_AUDIO_TRACKS};
use crate::webcam::{self, Webcam};
use crate::window_capture::WindowCapture;

/// Overall deadline waiting for the OBS stop signal after `quit` (§1.4).
const STOP_DEADLINE: Duration = Duration::from_secs(30);
const STOP_WARN_INTERVAL: Duration = Duration::from_secs(10);
/// Deadline waiting for the output's "deactivate" signal after its "stop"
/// signal — i.e. for the recording file to be flushed and closed (see
/// [`Recorder::wait_for_flush`]). Generous: mp4_output's buffered serializer
/// can hold up to 256 MiB that still needs to reach a possibly slow disk.
const FLUSH_DEADLINE: Duration = Duration::from_secs(30);

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

/// Creates a volmeter on `source` whose callback stores the latest
/// cross-channel max peak (dBFS, f32 bits) in the returned atomic. Silence is
/// -inf; the levels emitter clamps before serializing.
fn attach_volmeter(source: &ObsSource) -> Result<(ObsVolmeter, Arc<AtomicU32>), String> {
    let mut volmeter = ObsVolmeter::new().map_err(|e| format!("Failed to create volmeter: {e}"))?;
    let peak_store = Arc::new(AtomicU32::new(f32::NEG_INFINITY.to_bits()));
    let store = peak_store.clone();
    volmeter.add_callback(move |_magnitude, peak, _input_peak| {
        let max = peak.iter().copied().fold(f32::NEG_INFINITY, f32::max);
        store.store(max.to_bits(), Ordering::Relaxed);
    });
    if !volmeter.attach_source(source) {
        return Err("Failed to attach volmeter to audio source".to_string());
    }
    Ok((volmeter, peak_store))
}

/// One side (speakers or mics) of the audio pipeline, built but not yet
/// assigned to output channels.
struct BuiltSide {
    sources: Vec<ObsSource>,
    meters: Vec<(ObsVolmeter, Arc<AtomicU32>)>,
    /// Device ids parallel to `sources` (volume compensation needs them for
    /// the speaker side).
    devices: Vec<String>,
}

enum AudioBuildError {
    /// The request itself is invalid on this platform (exit 2 at startup).
    Args(String),
    /// Source/volmeter creation failed (exit 1 at startup).
    Create(String),
}

/// Creates speaker sources + volmeters for `devices` without touching output
/// channels — `configure` needs every fallible step done before any pipeline
/// mutation, so channel assignment is a separate, infallible commit.
fn build_speaker_sources(devices: &[String]) -> Result<BuiltSide, AudioBuildError> {
    let mut sources = Vec::new();
    let mut meters = Vec::new();
    for (i, device_id) in devices.iter().enumerate() {
        let (source_id, settings) = platform::audio_output_capture(device_id);
        if source_id == "sck_audio_capture" && i > 0 {
            return Err(AudioBuildError::Args(
                "repeated speakers are not supported on macOS 13+: \
                 ScreenCaptureKit captures all system audio as a single stream"
                    .to_string(),
            ));
        }
        let source = ObsSource::create(source_id, &format!("speaker_{i}"), Some(&settings))
            .map_err(|e| {
                AudioBuildError::Create(format!(
                    "Failed to create speaker source for '{device_id}': {e}"
                ))
            })?;
        meters.push(attach_volmeter(&source).map_err(AudioBuildError::Create)?);
        sources.push(source);
    }
    Ok(BuiltSide {
        sources,
        meters,
        devices: devices.to_vec(),
    })
}

/// Microphone counterpart of [`build_speaker_sources`].
fn build_mic_sources(devices: &[String]) -> Result<BuiltSide, AudioBuildError> {
    let mut sources = Vec::new();
    let mut meters = Vec::new();
    for (i, device_id) in devices.iter().enumerate() {
        let settings = ObsData::new();
        settings.set_string("device_id", device_id);
        let source = ObsSource::create(
            platform::AUDIO_INPUT_CAPTURE_ID,
            &format!("mic_{i}"),
            Some(&settings),
        )
        .map_err(|e| {
            AudioBuildError::Create(format!(
                "Failed to create microphone source for '{device_id}': {e}"
            ))
        })?;
        meters.push(attach_volmeter(&source).map_err(AudioBuildError::Create)?);
        sources.push(source);
    }
    Ok(BuiltSide {
        sources,
        meters,
        devices: devices.to_vec(),
    })
}

/// Routes every audio source to the libobs mixer feeding its output track:
/// one mixer per device in multi-track mode, mixer 0 for everything in
/// single-track mode (`tracks::audio_mixer_mask`). Must be re-applied over the
/// WHOLE list whenever either side changes — a speaker added or removed
/// shifts every microphone's track index.
fn apply_audio_mixers(speakers: &[ObsSource], mics: &[ObsSource], multi_track: bool) {
    for (index, source) in speakers.iter().chain(mics.iter()).enumerate() {
        source.set_audio_mixers(tracks::audio_mixer_mask(index, multi_track));
    }
}

/// Creates one audio encoder per planned track, each bound to the libobs
/// mixer of the same index and to the global audio output. Nothing is
/// attached to the output here — the caller commits the whole set at once.
fn create_audio_encoders(
    encoder_types: &[String],
    plan: &[AudioTrack],
    audio: *mut obs_sys::audio_t,
) -> Result<Vec<ObsEncoder>, String> {
    let mut encoders = Vec::with_capacity(plan.len());
    for (idx, track) in plan.iter().enumerate() {
        let encoder = encoder_config::create_audio_encoder(encoder_types, &track.name, idx)
            .map_err(|e| format!("Failed to create the audio encoder for track {idx}: {e}"))?;
        encoder.set_audio(audio);
        encoders.push(encoder);
    }
    Ok(encoders)
}

/// Applies the current compensation gain (system software volume inverse; 1.0
/// where none applies) to each speaker source. The levels thread keeps the
/// values current afterwards; this makes the very first captured samples
/// correct too.
fn apply_speaker_compensation(sources: &[ObsSource], devices: &[String]) {
    for (source, device_id) in sources.iter().zip(devices) {
        let gain = platform::speaker_compensation_gain(device_id);
        source.set_volume(gain);
        if (gain - 1.0).abs() > 0.001 {
            eprintln!(
                "Speaker volume compensation: device '{device_id}' has a software master \
                 volume; applying gain {gain:.2}x"
            );
        }
    }
}

pub struct Recorder {
    // ---- Sidecars first: DECLARATION ORDER IS DROP ORDER. ----
    // Both hold a raw `*mut obs_output_t` that background threads (the
    // input-capture writer, the window-capture poller) dereference for the
    // pause gate. Declared below `output` they would be dropped *after* the
    // `ObsOutput` that owns that pointer, so a panic unwind through the run
    // loop could leave a thread calling `obs_output_paused` on freed memory —
    // their Drops must run, and stop those threads, while the output is still
    // alive. Nothing here is touched by construction order (the struct literal
    // is by name), so this placement costs nothing.
    /// `--input-capture`: JSONL sidecar of cursor/mouse/key state (hooks +
    /// tick sampler + writer thread). Session-fixed; `configure` never touches
    /// it. Every exit path must `close()` it before `emit_stopped_recording`.
    input_capture: Option<InputCapture>,
    /// `--window-capture`: JSONL sidecar of per-window geometry relative to
    /// the capture region (tick clock + poll thread). Session-fixed like
    /// `input_capture`, and closed on the same exit paths.
    window_capture: Option<WindowCapture>,
    output: ObsOutput,
    speakers: Vec<ObsSource>,
    /// Device ids parallel to `speakers`, for volume compensation.
    speaker_devices: Vec<String>,
    mics: Vec<ObsSource>,
    /// Volmeter + latest peak dBFS (f32 bits) per source, in list order
    /// (matching the mute indices).
    speaker_meters: Vec<(ObsVolmeter, Arc<AtomicU32>)>,
    mic_meters: Vec<(ObsVolmeter, Arc<AtomicU32>)>,
    /// Peak stores shared with the levels thread; the contents are swapped
    /// under the lock when `configure` rebuilds the audio sources.
    level_peaks: Arc<Mutex<LevelPeaks>>,
    display_sources: Vec<ObsSource>,
    _scene_items: Vec<ObsSceneItem>,
    /// Owns the click-highlight item and its libobs tick callback; created and
    /// dropped at runtime by `configure` as well as at startup.
    tracker: Option<MouseTracker>,
    scene: ObsScene,
    video_encoder: ObsEncoder,
    /// Webcam second-video-track chain (`--webcam` or the `webcam_device`
    /// settings key), with its own view mix and x264 encoder on output track
    /// 1. None when no device is configured.
    webcam: Option<Webcam>,
    /// The effective webcam device id, kept for the view-mix rebuild any
    /// `obs_reset_video` path requires (see `configure_full`). Mirrored into
    /// `settings.webcam_device` (as "" when None).
    webcam_device: Option<String>,
    /// True when the device came from `--webcam`: the flag pins the webcam for
    /// the process lifetime, so `configure` never changes it (the settings
    /// file the parent re-sends does not contain the flag).
    webcam_from_cli: bool,
    /// `--multi-track`: hybrid mp4 output, one track per stream. Session-fixed
    /// (the output type cannot change once created), so `configure` never
    /// touches it.
    multi_track: bool,
    /// One encoder per audio track, in output track order; each reads the
    /// libobs mixer of its own index. Single-track mode always has exactly
    /// one (mixer 0).
    audio_encoders: Vec<ObsEncoder>,
    /// The audio track layout `audio_encoders` was built from, for `tracks`.
    audio_tracks: Vec<AudioTrack>,
    _sig_start: SignalConnection,
    _sig_stop: SignalConnection,
    _sig_deactivate: SignalConnection,
    context: ObsContext,
    /// Current effective tunable config (defaults overlaid by `--settings`,
    /// or the individual flags); updated by successful `configure`s.
    settings: Settings,
    /// Session-fixed inputs `configure` re-derives the video setup from. The
    /// canvas and scene items never change (region and monitors are fixed);
    /// only the fps and the output downscale can.
    capture_region: Rect,
    canvas: (u32, u32),
    canvas_scale: f64,
    encoder_types: Vec<String>,
    cmd_tx: mpsc::Sender<Command>,
    cmd_rx: mpsc::Receiver<Command>,
}

impl Recorder {
    /// Builds the whole OBS pipeline. Order matters: the libobs data path MUST
    /// be registered before `obs_reset_video` (graphics init loads
    /// `default.effect` etc. through `obs_find_data_file`, whose built-in
    /// fallback is CWD-relative and resolves nowhere in our layout).
    pub fn new(cli: &Cli, mut settings: Settings) -> Recorder {
        // Effective webcam device: --webcam wins (and pins it for the process
        // lifetime); otherwise the settings file's `webcam_device` ("" = none).
        let multi_track = cli.multi_track;
        let webcam_from_cli = cli.webcam.is_some();
        let webcam_device: Option<String> = cli.webcam.clone().or_else(|| {
            (!settings.webcam_device.is_empty()).then(|| settings.webcam_device.clone())
        });
        // Both are §1.1 validations `Cli::validate` already made; re-checked
        // here so a programmatically built Cli cannot silently produce a
        // recording that drops the webcam or an audio device.
        if webcam_device.is_some() && !multi_track {
            fail_args("a webcam requires --multi-track");
        }
        if multi_track && settings.speakers.len() + settings.microphones.len() > MAX_AUDIO_TRACKS {
            fail_args(format_args!(
                "--multi-track supports at most {MAX_AUDIO_TRACKS} audio devices"
            ));
        }
        // Keep the stored settings reflecting the *effective* device so
        // `configure` diffs against reality.
        settings.webcam_device = webcam_device.clone().unwrap_or_default();

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
            region::compute_output_size(plan.canvas, settings.max_width, settings.max_height);
        let video_info = VideoInfo {
            graphics_module: platform::GRAPHICS_MODULE,
            base_width: plan.canvas.0,
            base_height: plan.canvas.1,
            output_width: out_w,
            output_height: out_h,
            fps_num: settings.fps,
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
        // Same registration check for the webcam source, only when requested
        // (the "test" pseudo-device uses color_source from image-source).
        if matches!(webcam_device.as_deref(), Some(id) if id != webcam::TEST_DEVICE_ID) {
            let webcam_c = CString::new(platform::WEBCAM_SOURCE_ID).unwrap();
            if unsafe { obs_sys::obs_source_get_display_name(webcam_c.as_ptr()) }.is_null() {
                fail(format_args!(
                    "Webcam source '{}' is not registered — the camera capture plugin failed \
                     to load.\n  module bin:  {}\n  module data: {}",
                    platform::WEBCAM_SOURCE_ID,
                    paths.module_bin,
                    paths.module_data
                ));
            }
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
            let source_settings = platform::display_capture_settings(m, settings.cursor);
            let source = match ObsSource::create(
                platform::DISPLAY_CAPTURE_ID,
                &format!("display_{i}"),
                Some(&source_settings),
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

        // The click highlight is added last so it stacks above every display
        // capture. Its tick callback starts animating immediately — harmless
        // before the output starts, since nothing is being encoded yet.
        let tracker = if settings.tracker {
            let color = match tracker::parse_color(&settings.tracker_color) {
                Ok(c) => c,
                Err(e) => fail_args(e),
            };
            match MouseTracker::create(&scene, color, capture_region, plan.canvas_scale) {
                Ok(t) => Some(t),
                Err(e) => fail(format_args!(
                    "Failed to create the mouse click tracker: {e}"
                )),
            }
        } else {
            None
        };

        context.set_output_source_raw(0, scene.get_source());

        // 8. Audio sources on output channels 1..=N (speakers first, then mics,
        //    in list order).
        let speakers_built = match build_speaker_sources(&settings.speakers) {
            Ok(b) => b,
            Err(AudioBuildError::Args(e)) => fail_args(e),
            Err(AudioBuildError::Create(e)) => fail(e),
        };
        let mics_built = match build_mic_sources(&settings.microphones) {
            Ok(b) => b,
            Err(AudioBuildError::Args(e)) => fail_args(e),
            Err(AudioBuildError::Create(e)) => fail(e),
        };
        for (channel, source) in (1_u32..).zip(
            speakers_built
                .sources
                .iter()
                .chain(mics_built.sources.iter()),
        ) {
            context.set_output_source(channel, Some(source));
        }
        // Output channels carry the sources; the mixer masks decide which
        // audio *track* each one lands in.
        apply_audio_mixers(&speakers_built.sources, &mics_built.sources, multi_track);
        if settings.speaker_volume_compensation {
            apply_speaker_compensation(&speakers_built.sources, &speakers_built.devices);
        }
        let level_peaks = Arc::new(Mutex::new(LevelPeaks {
            speaker: speakers_built
                .meters
                .iter()
                .map(|(_, p)| p.clone())
                .collect(),
            mic: mics_built.meters.iter().map(|(_, p)| p.clone()).collect(),
            speaker_devices: speakers_built.devices.clone(),
            compensate: settings.speaker_volume_compensation,
        }));

        // 9. Encoders + output.
        let video_encoder = match encoder_config::create_video_encoder(
            &encoder_types,
            &EncoderConfig {
                hw_accel: settings.hw_accel,
                crf: settings.crf,
                low_cpu: settings.low_cpu,
            },
        ) {
            Ok(e) => e,
            Err(e) => fail(format_args!("Failed to create video encoder: {e}")),
        };
        video_encoder.set_video(context.get_video());
        // One audio encoder per track: with --multi-track that is one per
        // configured device (speakers then mics), otherwise a single encoder
        // reading mixer 0, into which every source is mixed.
        let audio_tracks =
            tracks::plan_audio_tracks(&settings.speakers, &settings.microphones, multi_track);
        let audio_encoders =
            match create_audio_encoders(&encoder_types, &audio_tracks, context.get_audio()) {
                Ok(e) => e,
                Err(e) => fail(e),
            };

        // Webcam chain (--webcam / settings `webcam_device`): built AFTER the
        // main pipeline is up but before the output — all fallible work
        // happens inside create().
        let webcam_chain = match webcam_device {
            Some(ref device_id) => match webcam::create(device_id, &settings) {
                Ok(w) => Some(w),
                Err(e) => fail(e),
            },
            None => None,
        };

        // Output: "ffmpeg_muxer" (single video track, one mixed audio track)
        // unless --multi-track opts into "mp4_output" (Hybrid MP4), which
        // carries a track per stream, fragments continuously
        // (crash-resilient) and soft-remuxes to a standard mp4 on stop. Both
        // take the same 'path' setting and emit the same 'stop' signal codes.
        let output_id = if multi_track {
            "mp4_output"
        } else {
            "ffmpeg_muxer"
        };
        let output_path = cli
            .output
            .as_ref()
            .unwrap_or_else(|| fail("--output is required"));
        let output_settings = ObsData::new();
        output_settings.set_string("path", &output_path.to_string_lossy());
        let output = match ObsOutput::create(output_id, "recording", Some(&output_settings)) {
            Ok(o) => o,
            Err(e) => fail(format_args!("Failed to create '{output_id}' output: {e}")),
        };
        output.set_video_encoder(&video_encoder);
        if let Some(ref w) = webcam_chain {
            // Track 1 = webcam. Only honored by multi-track outputs
            // (mp4_output); ffmpeg_muxer would silently drop it.
            output.set_video_encoder2(Some(&w.encoder), 1);
        }
        for (idx, encoder) in audio_encoders.iter().enumerate() {
            output.set_audio_encoder(Some(encoder), idx);
        }

        // Input-capture sidecar (--input-capture): installs the global input
        // hooks and a tick callback now, but no rows flow until the run loop
        // arms it on OutputStarted. Needs the output pointer (pause state /
        // pause offset), hence built after the output. No view or encoder of
        // its own, so — unlike the webcam — it has no obs_reset_video
        // interaction (tick callbacks survive a video reset).
        let input_capture = match cli.input_capture {
            Some(ref path) => {
                match InputCapture::new(
                    path,
                    capture_region,
                    &monitors,
                    plan.canvas_scale,
                    plan.canvas,
                    output.as_ptr(),
                ) {
                    Ok(ic) => Some(ic),
                    Err(e) => fail(format_args!("Failed to start input capture: {e}")),
                }
            }
            None => None,
        };

        // Window-capture sidecar (--window-capture): same construction rules
        // as the input-capture one (needs the output pointer for the pause
        // gate, no video-reset interaction), writing its own file.
        let window_capture = match cli.window_capture {
            Some(ref path) => {
                match WindowCapture::new(
                    path,
                    capture_region,
                    plan.canvas_scale,
                    plan.canvas,
                    output.as_ptr(),
                ) {
                    Ok(wc) => Some(wc),
                    Err(e) => fail(format_args!("Failed to start window capture: {e}")),
                }
            }
            None => None,
        };

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
        let deactivate_tx = cmd_tx.clone();
        let sig_deactivate =
            SignalConnection::connect(output.signal_handler(), "deactivate", move || {
                let _ = deactivate_tx.send(Command::OutputDeactivated);
            });

        Recorder {
            output,
            speakers: speakers_built.sources,
            speaker_devices: speakers_built.devices,
            mics: mics_built.sources,
            speaker_meters: speakers_built.meters,
            mic_meters: mics_built.meters,
            level_peaks,
            display_sources,
            _scene_items: scene_items,
            tracker,
            scene,
            video_encoder,
            webcam: webcam_chain,
            webcam_device,
            webcam_from_cli,
            multi_track,
            input_capture,
            window_capture,
            audio_encoders,
            audio_tracks,
            _sig_start: sig_start,
            _sig_stop: sig_stop,
            _sig_deactivate: sig_deactivate,
            context,
            settings,
            capture_region,
            canvas: plan.canvas,
            canvas_scale: plan.canvas_scale,
            encoder_types,
            cmd_tx,
            cmd_rx,
        }
    }

    /// The command loop. Never returns — every path ends in
    /// `platform::exit_process` (§1.4).
    pub fn run(&mut self, pause: bool) -> ! {
        status::emit_simple("initialized");

        self.spawn_stdin_thread();
        self.install_signal_handler();

        let mut start_requested = false;
        let mut started = false;
        let mut paused = false;
        let mut clock: Option<Arc<RecordingClock>> = None;
        let status_stop = Arc::new(AtomicBool::new(false));
        let mut status_handle: Option<std::thread::JoinHandle<()>> = None;

        // Levels flow from initialization on (the pre-start WAIT phase too),
        // not just while recording. Always spawned — the audio lists can go
        // from empty to non-empty via `configure`; the thread stays silent
        // while both are empty.
        let mut levels_handle = Some(status::start_levels_thread(
            self.level_peaks.clone(),
            status_stop.clone(),
            self.cmd_tx.clone(),
        ));

        // Without --pause the output starts immediately; with --pause we sit in
        // initialized-wait mode until stdin `start`.
        if !pause {
            self.start_output(&status_stop, &mut levels_handle);
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
                        self.start_output(&status_stop, &mut levels_handle);
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
                        // Arm the sidecar first: its t0 is the frame time of
                        // the next tick, which should sit as close to the
                        // first encoded frame as possible.
                        if let Some(ref ic) = self.input_capture {
                            ic.on_output_started(self.settings.fps);
                        }
                        if let Some(ref wc) = self.window_capture {
                            wc.on_output_started(self.settings.fps);
                        }
                        let mut started_msg = serde_json::json!({
                            "type": "started_recording",
                            "tracks": self.tracks_json(),
                        });
                        if let Some(ref ic) = self.input_capture {
                            started_msg["input_capture"] =
                                serde_json::json!(ic.path().to_string_lossy());
                        }
                        if let Some(ref wc) = self.window_capture {
                            started_msg["window_capture"] =
                                serde_json::json!(wc.path().to_string_lossy());
                        }
                        status::emit_json(started_msg);
                        let c = Arc::new(RecordingClock::new());
                        status_handle = Some(status::start_status_thread(
                            self.output.as_ptr(),
                            1 + self.webcam.is_some() as i64,
                            c.clone(),
                            status_stop.clone(),
                        ));
                        clock = Some(c);
                    }
                }
                // Live-safe-only once a start has been requested (the window
                // between requesting and the OBS start signal counts as live:
                // obs_reset_video must not race an activating output).
                Command::Configure(path) => self.handle_configure(&path, start_requested),
                Command::MuteSpeaker(idx) => self.set_muted(&self.speakers, "speaker", idx, true),
                Command::UnmuteSpeaker(idx) => {
                    self.set_muted(&self.speakers, "speaker", idx, false)
                }
                Command::MuteMic(idx) => self.set_muted(&self.mics, "microphone", idx, true),
                Command::UnmuteMic(idx) => self.set_muted(&self.mics, "microphone", idx, false),
                Command::SetSpeakerVolume(idx, gain) => {
                    // From the levels thread; may race a configure that just
                    // disabled compensation or shrank the list — re-check both.
                    if self.settings.speaker_volume_compensation {
                        if let Some(source) = self.speakers.get(idx) {
                            source.set_volume(gain);
                        }
                    }
                }
                // Only consumed by `wait_for_flush` (it always follows a stop
                // signal, and every stop path ends in `finish`); ignore the
                // stray case defensively.
                Command::OutputDeactivated => {}
                Command::OutputStopped(code) => {
                    // Spontaneous stop (disk full, encoder error, …): surface it
                    // immediately and exit — do not wait for `quit` (§1.3).
                    self.finish(
                        code,
                        true,
                        &status_stop,
                        status_handle.take(),
                        levels_handle.take(),
                    );
                }
                Command::Quit => {
                    if !start_requested {
                        // Cancelled before recording started (§1.2). Stop the
                        // levels thread first so stopped_recording stays last.
                        status_stop.store(true, Ordering::Relaxed);
                        if let Some(handle) = levels_handle.take() {
                            let _ = handle.join();
                        }
                        // Exit paths skip Drop: make the (empty) sidecar
                        // durable before the final protocol line.
                        if let Some(ref ic) = self.input_capture {
                            ic.close();
                        }
                        if let Some(ref wc) = self.window_capture {
                            wc.close();
                        }
                        let mut stopped_msg = serde_json::json!({
                            "type": "stopped_recording",
                            "code": 0,
                            "message": "Cancelled before recording started",
                            "error": null,
                        });
                        if let Some(ref ic) = self.input_capture {
                            stopped_msg["input_capture"] =
                                serde_json::json!(ic.path().to_string_lossy());
                        }
                        if let Some(ref wc) = self.window_capture {
                            stopped_msg["window_capture"] =
                                serde_json::json!(wc.path().to_string_lossy());
                        }
                        status::emit_json(stopped_msg);
                        platform::exit_process(0);
                    }
                    self.output.stop();
                    let deadline = Instant::now() + STOP_DEADLINE;
                    loop {
                        let remaining = deadline.saturating_duration_since(Instant::now());
                        if remaining.is_zero() {
                            // Synthetic timeout stop (§1.4).
                            self.finish(
                                -99,
                                false,
                                &status_stop,
                                status_handle.take(),
                                levels_handle.take(),
                            );
                        }
                        match self.cmd_rx.recv_timeout(remaining.min(STOP_WARN_INTERVAL)) {
                            Ok(Command::OutputStopped(code)) => {
                                self.finish(
                                    code,
                                    true,
                                    &status_stop,
                                    status_handle.take(),
                                    levels_handle.take(),
                                );
                            }
                            // An accepted configure still needs its ack (§2.3);
                            // nothing is applied while tearing down, so the
                            // pipeline is untouched (fatal:false).
                            Ok(Command::Configure(_)) => status::emit_configure_error(
                                "recorder is stopping, configure not applied",
                                false,
                            ),
                            Ok(_) => {} // ignore anything else while stopping
                            Err(mpsc::RecvTimeoutError::Timeout) => {
                                eprintln!("Warning: still waiting for the recording to stop...");
                            }
                            Err(mpsc::RecvTimeoutError::Disconnected) => {
                                self.finish(
                                    -99,
                                    false,
                                    &status_stop,
                                    status_handle.take(),
                                    levels_handle.take(),
                                );
                            }
                        }
                    }
                }
            }
        }
    }

    /// stdin `configure <path>` (CONTRACT §6): re-read the settings file and
    /// apply the diff against the current effective config. Emits exactly one
    /// `configure_applied` XOR `configure_error` ack.
    fn handle_configure(&mut self, path: &str, live_only: bool) {
        let new = match Settings::load(Path::new(path)) {
            Ok(s) => s,
            Err(e) => return status::emit_configure_error(&e, false),
        };
        if live_only {
            self.configure_live(new);
        } else {
            self.configure_full(new);
        }
    }

    /// Pre-start configure: every key is applicable. Fallible work is done
    /// before any pipeline mutation, so early failures leave the pipeline
    /// intact (`fatal:false`); once `obs_reset_video` runs, a failure means
    /// the pipeline may match neither config (`fatal:true`).
    fn configure_full(&mut self, mut new: Settings) {
        let cur = self.settings.clone();

        // Effective webcam device under the new config: pinned by --webcam
        // (the settings file never carries the flag, so a re-sent file must
        // not tear the webcam down), otherwise the file's `webcam_device`
        // ("" = none).
        let new_webcam_device: Option<String> = if self.webcam_from_cli {
            self.webcam_device.clone()
        } else if new.webcam_device.is_empty() {
            None
        } else {
            Some(new.webcam_device.clone())
        };
        let webcam_changed = new_webcam_device != self.webcam_device;

        // The output type is session-fixed, so the two multi-track-only
        // capabilities are rejected up front rather than half-applied.
        if new_webcam_device.is_some() && !self.multi_track {
            return status::emit_configure_error(
                "`webcam_device` requires --multi-track; the recorder was started without it",
                false,
            );
        }
        let new_audio_sources = new.speakers.len() + new.microphones.len();
        if self.multi_track && new_audio_sources > MAX_AUDIO_TRACKS {
            return status::emit_configure_error(
                &format!(
                    "--multi-track records one audio track per device and supports at most \
                     {MAX_AUDIO_TRACKS} (got {new_audio_sources})"
                ),
                false,
            );
        }

        // -- Fallible preparation (no pipeline mutation on failure).
        let new_speakers = if new.speakers != cur.speakers {
            match build_speaker_sources(&new.speakers) {
                Ok(b) => Some(b),
                Err(AudioBuildError::Args(e)) | Err(AudioBuildError::Create(e)) => {
                    return status::emit_configure_error(&e, false)
                }
            }
        } else {
            None
        };
        let new_mics = if new.microphones != cur.microphones {
            match build_mic_sources(&new.microphones) {
                Ok(b) => Some(b),
                Err(AudioBuildError::Args(e)) | Err(AudioBuildError::Create(e)) => {
                    return status::emit_configure_error(&e, false)
                }
            }
        } else {
            None
        };
        // Multi-track puts every device on its own track, so a changed device
        // list is a changed track layout: rebuild the whole encoder set (also
        // fallible, hence still in the preparation phase). Single-track keeps
        // its one mixer-0 encoder no matter what the device lists do.
        let new_audio_encoders =
            if self.multi_track && (new_speakers.is_some() || new_mics.is_some()) {
                let plan = tracks::plan_audio_tracks(&new.speakers, &new.microphones, true);
                match create_audio_encoders(&self.encoder_types, &plan, self.context.get_audio()) {
                    Ok(encoders) => Some((plan, encoders)),
                    Err(e) => return status::emit_configure_error(&e, false),
                }
            } else {
                None
            };

        let video_changed = new.fps != cur.fps
            || new.max_width != cur.max_width
            || new.max_height != cur.max_height;
        let encoder_changed = video_changed
            || new.crf != cur.crf
            || new.hw_accel != cur.hw_accel
            || new.low_cpu != cur.low_cpu;
        let new_encoder = if encoder_changed {
            // Recreating (rather than obs_encoder_update) is the safe route:
            // the encoder id itself can change with hw_accel.
            match encoder_config::create_video_encoder(
                &self.encoder_types,
                &EncoderConfig {
                    hw_accel: new.hw_accel,
                    crf: new.crf,
                    low_cpu: new.low_cpu,
                },
            ) {
                Ok(e) => Some(e),
                Err(e) => {
                    return status::emit_configure_error(
                        &format!("Failed to create video encoder: {e}"),
                        false,
                    )
                }
            }
        } else {
            None
        };

        // A failed create rolls itself back (nothing was added to the scene),
        // and on a later failure the local's drop removes the item again.
        let new_tracker = if new.tracker && self.tracker.is_none() {
            match self.create_tracker(&new.tracker_color) {
                Ok(t) => Some(t),
                Err(e) => return status::emit_configure_error(&e, false),
            }
        } else {
            None
        };

        // -- Point of no return: obs_reset_video invalidates the video_t* the
        // current encoder is bound to.
        if video_changed {
            // CRITICAL invariant (verified in libobs 32.1.2): obs_reset_video
            // destroys ALL obs_view video mixes — the webcam's included; its
            // encoder would keep a dangling video_t. Tear the webcam chain
            // down first (detaching its encoder), then rebuild and rebind
            // after the reset. Phase-2 configure work MUST keep this ordering
            // for every future obs_reset_video call site.
            if self.webcam.is_some() {
                self.output.set_video_encoder2(None, 1);
                self.webcam = None;
            }

            let (out_w, out_h) =
                region::compute_output_size(self.canvas, new.max_width, new.max_height);
            let video_info = VideoInfo {
                graphics_module: platform::GRAPHICS_MODULE,
                base_width: self.canvas.0,
                base_height: self.canvas.1,
                output_width: out_w,
                output_height: out_h,
                fps_num: new.fps,
                fps_den: 1,
            };
            if let Err(e) = self.context.reset_video(&video_info) {
                return status::emit_configure_error(
                    &format!("Failed to reset OBS video: {e}; the pipeline may be unusable"),
                    true,
                );
            }

            // (Re)build under the new fps/crf — covers both a chain that was
            // just torn down and one newly requested via `webcam_device`;
            // legal because the output is inactive pre-start.
            if let Some(ref device) = new_webcam_device {
                match webcam::create(device, &new) {
                    Ok(w) => {
                        self.output.set_video_encoder2(Some(&w.encoder), 1);
                        self.webcam = Some(w);
                    }
                    Err(e) => {
                        // The screen pipeline is intact but the webcam track
                        // is gone — the recording would not match the
                        // requested config, so the parent should respawn.
                        self.webcam_device = None;
                        self.settings.webcam_device = String::new();
                        return status::emit_configure_error(
                            &format!("Failed to rebuild the webcam chain: {e}"),
                            true,
                        );
                    }
                }
            }
        } else if webcam_changed {
            // No video reset needed: swap the webcam chain alone (legal
            // pre-start — the output is inactive). Teardown before build:
            // the outgoing chain may hold the same physical camera.
            if self.webcam.is_some() {
                self.output.set_video_encoder2(None, 1);
                self.webcam = None;
            }
            if let Some(ref device) = new_webcam_device {
                match webcam::create(device, &new) {
                    Ok(w) => {
                        self.output.set_video_encoder2(Some(&w.encoder), 1);
                        self.webcam = Some(w);
                    }
                    Err(e) => {
                        // Screen pipeline intact, but the recording would not
                        // match the requested config — parent should respawn.
                        self.webcam_device = None;
                        self.settings.webcam_device = String::new();
                        return status::emit_configure_error(
                            &format!("Failed to build the webcam chain: {e}"),
                            true,
                        );
                    }
                }
            }
        } else if new.crf != cur.crf {
            // No reset needed: refresh the webcam encoder's quality in place
            // (safe pre-start — the encoder has not initialized yet).
            if let Some(ref w) = self.webcam {
                w.encoder.update(&webcam::encoder_settings(new.crf));
            }
        }

        // -- Infallible commit.
        if let Some(encoder) = new_encoder {
            // Bind to the (possibly fresh) video_t and swap into the output;
            // legal because the output is inactive pre-start.
            encoder.set_video(self.context.get_video());
            self.output.set_video_encoder(&encoder);
            self.video_encoder = encoder;
        }
        if new_speakers.is_some() || new_mics.is_some() {
            self.commit_audio(new_speakers, new_mics, new.speaker_volume_compensation);
            if let Some((plan, encoders)) = new_audio_encoders {
                self.commit_audio_encoders(plan, encoders);
            }
        }
        if let Some(t) = new_tracker {
            self.tracker = Some(t);
        }
        self.apply_live_keys(&new);
        // Store the *effective* device (differs from the file's value when
        // --webcam pinned it) so later diffs compare against reality.
        self.webcam_device = new_webcam_device;
        new.webcam_device = self.webcam_device.clone().unwrap_or_default();
        self.settings = new;
        status::emit_configure_applied(&[]);
    }

    /// Post-start configure: apply only the always-live keys and report every
    /// differing non-live key in `ignored_keys` (schema order). Must never
    /// stop or degrade the recording — errors are always `fatal:false`.
    fn configure_live(&mut self, new: Settings) {
        // Tracker creation is the only fallible live apply; do it before
        // mutating anything so an error leaves the pipeline untouched.
        if new.tracker && self.tracker.is_none() {
            match self.create_tracker(&new.tracker_color) {
                Ok(t) => self.tracker = Some(t),
                Err(e) => return status::emit_configure_error(&e, false),
            }
        }
        self.apply_live_keys(&new);

        let cur = &self.settings;
        let mut ignored: Vec<&str> = Vec::new();
        if new.fps != cur.fps {
            ignored.push("fps");
        }
        if new.crf != cur.crf {
            ignored.push("crf");
        }
        if new.max_width != cur.max_width {
            ignored.push("max_width");
        }
        if new.max_height != cur.max_height {
            ignored.push("max_height");
        }
        if new.hw_accel != cur.hw_accel {
            ignored.push("hw_accel");
        }
        if new.low_cpu != cur.low_cpu {
            ignored.push("low_cpu");
        }
        if new.speakers != cur.speakers {
            ignored.push("speakers");
        }
        if new.microphones != cur.microphones {
            ignored.push("microphones");
        }
        // The webcam is a pipeline element, never touched post-start. Compare
        // against the *effective* device: when --webcam pinned it, a file
        // without the key accurately reports as ignored.
        if new.webcam_device != cur.webcam_device {
            ignored.push("webcam_device");
        }

        // Ignored keys keep their current values, so re-sending the same file
        // re-reports the same ignored_keys.
        self.settings.cursor = new.cursor;
        self.settings.tracker = new.tracker;
        self.settings.tracker_color = new.tracker_color;
        self.settings.speaker_volume_compensation = new.speaker_volume_compensation;
        status::emit_configure_applied(&ignored);
    }

    /// Applies the always-live keys (cursor, tracker off, tracker color,
    /// speaker volume compensation). Tracker *creation* is fallible and must
    /// already have been done by the caller; everything here is infallible.
    fn apply_live_keys(&mut self, new: &Settings) {
        if new.speaker_volume_compensation != self.settings.speaker_volume_compensation {
            if new.speaker_volume_compensation {
                apply_speaker_compensation(&self.speakers, &self.speaker_devices);
            } else {
                for source in &self.speakers {
                    source.set_volume(1.0);
                }
            }
            self.level_peaks.lock().unwrap().compensate = new.speaker_volume_compensation;
        }
        if new.cursor != self.settings.cursor {
            let update = platform::cursor_update_settings(new.cursor);
            for source in &self.display_sources {
                source.update(&update);
            }
        }
        if !new.tracker {
            // Dropping deregisters the tick callback and removes the item.
            self.tracker = None;
        } else if let Some(ref tracker) = self.tracker {
            if new.tracker_color != self.settings.tracker_color {
                if let Ok(color) = tracker::parse_color(&new.tracker_color) {
                    tracker.set_color(color);
                }
            }
        }
    }

    /// Per-track stream map for `started_recording` / `stopped_recording`:
    /// `{"screen":{"index":0,..},"webcam":{..},"audio":[..]}`.
    ///
    /// The `webcam` entry is ABSENT (not null) without a webcam track —
    /// consumers treat a missing key as "no such track". Screen dims are the
    /// encoded output size (canvas after the max_width / max_height downscale
    /// — the same computation `reset_video` was given); webcam dims are its
    /// track-1 mix canvas. `index` is per media type (video / audio),
    /// matching the container's per-type stream numbering.
    ///
    /// `audio` always has at least one entry: single-track recordings report
    /// their one `"mixed"` track, multi-track ones a `"speaker"` /
    /// `"microphone"` entry per device.
    fn tracks_json(&self) -> serde_json::Value {
        let (out_w, out_h) = region::compute_output_size(
            self.canvas,
            self.settings.max_width,
            self.settings.max_height,
        );
        let audio: Vec<serde_json::Value> = self
            .audio_tracks
            .iter()
            .enumerate()
            .map(|(idx, track)| {
                serde_json::json!({
                    "index": idx,
                    "kind": track.kind.as_str(),
                    "device": track.device,
                    "name": track.name,
                })
            })
            .collect();
        let mut tracks = serde_json::json!({
            "screen": { "index": 0, "width": out_w, "height": out_h },
            "audio": audio,
        });
        if let Some(ref w) = self.webcam {
            tracks["webcam"] = serde_json::json!({
                "index": 1, "width": w.canvas.0, "height": w.canvas.1,
            });
        }
        tracks
    }

    /// Creates the click tracker. Adding it to the scene now — with the
    /// display items long since added and nothing else ever appended — keeps
    /// it the top scene item.
    fn create_tracker(&self, color_str: &str) -> Result<MouseTracker, String> {
        let color = tracker::parse_color(color_str)?;
        MouseTracker::create(&self.scene, color, self.capture_region, self.canvas_scale)
            .map_err(|e| format!("Failed to create the mouse click tracker: {e}"))
    }

    /// Swaps in freshly built audio sides, reassigns output channels (speakers
    /// first, then mics), clears channels the shorter new lists no longer use,
    /// and publishes the new peak stores to the levels thread. Sources on an
    /// unchanged side are kept (mute states persist); new sources start
    /// unmuted — the parent re-applies mutes after the ack. `compensate` is the
    /// incoming config's `speaker_volume_compensation`: fresh speaker sources
    /// are created at unity and need their gain applied here.
    fn commit_audio(
        &mut self,
        new_speakers: Option<BuiltSide>,
        new_mics: Option<BuiltSide>,
        compensate: bool,
    ) {
        let speakers_replaced = new_speakers.is_some();
        if let Some(built) = new_speakers {
            // Meters before sources: a volmeter must detach before the source
            // it observes is released.
            self.speaker_meters = built.meters;
            self.speakers = built.sources;
            self.speaker_devices = built.devices;
        }
        if let Some(built) = new_mics {
            self.mic_meters = built.meters;
            self.mics = built.sources;
        }
        // The channel slots hold their own source references, so replacing /
        // clearing them here also releases the outgoing sources' last refs.
        let mut channel: u32 = 1;
        for source in self.speakers.iter().chain(self.mics.iter()) {
            self.context.set_output_source(channel, Some(source));
            channel += 1;
        }
        for unused in channel..=(MAX_AUDIO_SOURCES as u32) {
            self.context.set_output_source(unused, None);
        }
        // Over the whole list, not just the replaced side: in multi-track mode
        // a changed speaker count shifts every microphone's track index.
        apply_audio_mixers(&self.speakers, &self.mics, self.multi_track);
        if speakers_replaced && compensate {
            apply_speaker_compensation(&self.speakers, &self.speaker_devices);
        }
        let mut peaks = self.level_peaks.lock().unwrap();
        peaks.speaker = self.speaker_meters.iter().map(|(_, p)| p.clone()).collect();
        peaks.mic = self.mic_meters.iter().map(|(_, p)| p.clone()).collect();
        peaks.speaker_devices = self.speaker_devices.clone();
        peaks.compensate = compensate;
    }

    /// Swaps in a freshly built audio encoder set (multi-track only): attaches
    /// each encoder to its track and clears the tracks a shorter new layout no
    /// longer uses. Legal only pre-start — libobs refuses encoder changes on an
    /// active output. The outgoing encoders are released last (the output holds
    /// its own refs until each `set_audio_encoder` replaces them).
    fn commit_audio_encoders(&mut self, plan: Vec<AudioTrack>, encoders: Vec<ObsEncoder>) {
        for (idx, encoder) in encoders.iter().enumerate() {
            self.output.set_audio_encoder(Some(encoder), idx);
        }
        for idx in encoders.len()..self.audio_encoders.len() {
            self.output.set_audio_encoder(None, idx);
        }
        self.audio_encoders = encoders;
        self.audio_tracks = plan;
    }

    /// Starts the output; on failure emits `stopped_recording` code -4 with the
    /// output's last error and exits 1 (joining the levels thread first so
    /// stopped_recording stays the last JSON line).
    fn start_output(
        &self,
        status_stop: &Arc<AtomicBool>,
        levels_handle: &mut Option<std::thread::JoinHandle<()>>,
    ) {
        if let Err(e) = self.output.start() {
            eprintln!("Failed to start recording: {e}");
            status_stop.store(true, Ordering::Relaxed);
            if let Some(handle) = levels_handle.take() {
                let _ = handle.join();
            }
            // Exit paths skip Drop: close the sidecars before the final line.
            if let Some(ref ic) = self.input_capture {
                ic.close();
            }
            if let Some(ref wc) = self.window_capture {
                wc.close();
            }
            // No tracks object: nothing was ever recorded. The sidecar paths
            // still report (the files exist — the parent may want them gone).
            status::emit_stopped_recording(
                -4,
                self.output.get_last_error(),
                None,
                self.input_capture.as_ref().map(|ic| ic.path()),
                self.window_capture.as_ref().map(|wc| wc.path()),
            );
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
    ///
    /// `wait_flush` is true when the output's "stop" signal was actually
    /// received: the file is not necessarily flushed yet at that point, so
    /// [`wait_for_flush`](Self::wait_for_flush) runs first. The synthetic -99
    /// paths (stop deadline blown / channel dead) pass false — no stop signal
    /// ever arrived, so there is no flush in progress to wait for.
    fn finish(
        &self,
        code: i64,
        wait_flush: bool,
        status_stop: &Arc<AtomicBool>,
        status_handle: Option<std::thread::JoinHandle<()>>,
        levels_handle: Option<std::thread::JoinHandle<()>>,
    ) -> ! {
        if wait_flush {
            self.wait_for_flush();
        }
        status_stop.store(true, Ordering::Relaxed);
        for handle in [status_handle, levels_handle].into_iter().flatten() {
            // ≤1 s: guarantees no status/levels line can print after
            // stopped_recording.
            let _ = handle.join();
        }
        // Exit paths skip Drop: disarm the sidecar (ticks and hooks outlive
        // the output — no rows may land after the final flush) and get its
        // tail on disk before the parent reads stopped_recording and opens
        // the file.
        if let Some(ref ic) = self.input_capture {
            ic.close();
        }
        if let Some(ref wc) = self.window_capture {
            wc.close();
        }
        let error = if code == 0 {
            None
        } else {
            self.output.get_last_error()
        };
        status::emit_stopped_recording(
            code,
            error,
            Some(self.tracks_json()),
            self.input_capture.as_ref().map(|ic| ic.path()),
            self.window_capture.as_ref().map(|wc| wc.path()),
        );
        platform::exit_process(if code == 0 { 0 } else { 1 })
    }

    /// Blocks (bounded by [`FLUSH_DEADLINE`]) until the output's "deactivate"
    /// signal arrives. The OBS "stop" signal fires *before* the recording file
    /// is flushed: mp4_output signals stop from `obs_output_end_data_capture`
    /// and only afterwards drains and closes its buffered file serializer,
    /// while libobs emits "deactivate" strictly after the in-flight packet
    /// callback (which includes that final drain/close) has returned. Exiting
    /// on "stop" alone can therefore kill the flush mid-write and silently
    /// truncate the mp4 tail (the final fragment + moov). On timeout we
    /// proceed anyway — exiting with a possibly-truncated file beats hanging
    /// the parent forever on a dead disk.
    fn wait_for_flush(&self) {
        let deadline = Instant::now() + FLUSH_DEADLINE;
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                eprintln!(
                    "Warning: the output did not deactivate within {FLUSH_DEADLINE:?}; \
                     the recording file may be incomplete"
                );
                return;
            }
            match self.cmd_rx.recv_timeout(remaining.min(STOP_WARN_INTERVAL)) {
                Ok(Command::OutputDeactivated) => return,
                // An accepted configure still needs its ack (§2.3); nothing is
                // applied while tearing down (fatal:false).
                Ok(Command::Configure(_)) => status::emit_configure_error(
                    "recorder is stopping, configure not applied",
                    false,
                ),
                Ok(_) => {} // ignore anything else while flushing
                Err(mpsc::RecvTimeoutError::Timeout) => {
                    eprintln!("Warning: still waiting for the recording file to be finalized...");
                }
                // Unreachable in practice (self holds a sender).
                Err(mpsc::RecvTimeoutError::Disconnected) => return,
            }
        }
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

    use windows::core::BOOL;
    use windows::Win32::System::Console::SetConsoleCtrlHandler;

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
        if unsafe { SetConsoleCtrlHandler(Some(handler), true) }.is_err() {
            eprintln!("Warning: failed to install console signal handler");
        }
    }
}
