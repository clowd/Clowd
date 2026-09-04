//! Webcam second-video-track chain (RECORDER CORE R1).
//!
//! The webcam renders into its own `obs_view` video mix (independent size and
//! shared fps), encoded by a dedicated x264 encoder attached to the output as
//! video track 1 (`obs_output_set_video_encoder2`). Track 0 stays the clean
//! screen canvas. Requires `--multi-track` (the "mp4_output" muxer) —
//! `ffmpeg_muxer` silently ignores video track indices > 0.
//!
//! The camera source is platform-provided (`platform::WEBCAM_SOURCE_ID`:
//! DirectShow on Windows, AVFoundation on macOS); everything downstream of it
//! is identical on both.
//!
//! CRITICAL invariant (verified in libobs 32.1.2): `obs_reset_video` destroys
//! ALL view mixes. Any code path calling `obs_reset_video` while a `Webcam`
//! exists MUST drop it first (detaching its encoder from the output) and
//! rebuild + rebind afterwards — see `Recorder::configure_full`. This also
//! applies to the phase-2 `configure` work: never reset video around a live
//! webcam chain.

use std::time::{Duration, Instant};

use obs::data::ObsData;
use obs::encoder::ObsEncoder;
use obs::scene::{ObsScene, ObsSceneItem};
use obs::source::ObsSource;
use obs::video::VideoInfo;
use obs::view::ObsView;

use crate::platform;
use crate::region;
use crate::settings::Settings;

/// Hidden `--webcam` value substituting a solid color source for the camera —
/// enables webcam-path testing on machines without cameras (CI/fixtures).
pub const TEST_DEVICE_ID: &str = "test";

/// Webcam mix bounding box: the native camera size is inner-fit downscaled
/// (never upscaled beyond native) into at most this.
const MAX_W: u32 = 1280;
const MAX_H: u32 = 720;

/// How long to wait for the camera to report its frame size after creation.
const SIZE_POLL_TIMEOUT: Duration = Duration::from_secs(2);
const SIZE_POLL_INTERVAL: Duration = Duration::from_millis(50);

/// The complete webcam chain. Field order is drop order and it matters:
/// the encoder must release before the view (its `video_t` belongs to the
/// view's mix), and the view's mix/channel refs must clear before the scene
/// item, scene and source are released.
pub struct Webcam {
    /// Track-1 video encoder, bound to the view mix's `video_t`.
    pub encoder: ObsEncoder,
    _view: ObsView,
    _item: ObsSceneItem,
    _scene: ObsScene,
    _source: ObsSource,
    /// Final mix canvas size (for logging/tests).
    pub canvas: (u32, u32),
}

/// Builds the whole webcam chain: dshow source (or color source for
/// [`TEST_DEVICE_ID`]) -> private "webcam" scene -> `obs_view` channel 0 ->
/// `obs_view_add2` mix sized to the camera -> x264 track-1 encoder bound to
/// the mix. Everything fallible happens in here, before the caller mutates
/// the output (`build_*_sources` pattern).
///
/// `device_id` must be the exact string from `--list-cameras` (the camera
/// source's property list item value — win-dshow's escaped `<name>:<path>` on
/// Windows, an `AVCaptureDevice.uniqueID` on macOS — passed through verbatim).
pub fn create(device_id: &str, settings: &Settings) -> Result<Webcam, String> {
    let source = if device_id == TEST_DEVICE_ID {
        let s = ObsData::new();
        s.set_int("width", MAX_W as i64);
        s.set_int("height", MAX_H as i64);
        // Opaque ABGR teal — visibly not screen content.
        s.set_int("color", 0xFFB1862Eu32 as i64);
        ObsSource::create("color_source", "webcam_0", Some(&s))
            .map_err(|e| format!("Failed to create the webcam test source: {e}"))?
    } else {
        let s = platform::webcam_settings(device_id);
        ObsSource::create(platform::WEBCAM_SOURCE_ID, "webcam_0", Some(&s))
            .map_err(|e| format!("Failed to create webcam source for '{device_id}': {e}"))?
    };

    // Audio gotcha (verified): libobs mixes every active audio-capable source
    // into mixer 0 globally — a webcam's built-in mic WOULD leak into the
    // recording's audio track. Detach from all mixers AND mute.
    source.set_audio_mixers(0);
    source.set_muted(true);

    // The camera reports 0x0 until its first frame; poll briefly.
    let deadline = Instant::now() + SIZE_POLL_TIMEOUT;
    let mut native = (source.get_width(), source.get_height());
    while (native.0 < 2 || native.1 < 2) && Instant::now() < deadline {
        std::thread::sleep(SIZE_POLL_INTERVAL);
        native = (source.get_width(), source.get_height());
    }

    // Canvas: native size downscaled aspect-preserving (width 4-aligned,
    // height even) to fit
    // 1280x720; if the camera never reported a size, fall back to a fixed
    // 1280x720 canvas — the SCALE_INNER bounds below letterbox whatever frame
    // size eventually arrives.
    let canvas = if native.0 >= 2 && native.1 >= 2 {
        region::compute_output_size(native, MAX_W, MAX_H)
    } else {
        eprintln!(
            "Warning: webcam '{device_id}' did not report a frame size within \
             {SIZE_POLL_TIMEOUT:?}; using a {MAX_W}x{MAX_H} canvas"
        );
        (MAX_W, MAX_H)
    };
    eprintln!(
        "Webcam '{device_id}': native {}x{}, track-1 canvas {}x{} @ {} fps",
        native.0, native.1, canvas.0, canvas.1, settings.fps
    );

    let scene = ObsScene::create("webcam")
        .map_err(|e| format!("Failed to create the webcam scene: {e}"))?;
    let item = scene.add(&source);
    // Inner-fit the camera into the full canvas: aspect-preserving, centered,
    // correct both when the canvas was derived from the native size (exact
    // fit) and in the unknown-size fallback (letterboxed).
    item.set_pos(0.0, 0.0);
    item.set_bounds_type(obs_sys::obs_bounds_type_OBS_BOUNDS_SCALE_INNER);
    item.set_bounds_alignment(0);
    item.set_bounds(canvas.0 as f32, canvas.1 as f32);

    let mut view = ObsView::new().map_err(|e| format!("Failed to create the webcam view: {e}"))?;
    view.set_source_raw(0, scene.get_source());
    let video = view
        .add2(&VideoInfo {
            graphics_module: platform::GRAPHICS_MODULE,
            base_width: canvas.0,
            base_height: canvas.1,
            output_width: canvas.0,
            output_height: canvas.1,
            fps_num: settings.fps,
            fps_den: 1,
            // A view mix reuses the graphics device the main reset already
            // created; `adapter` is only read when that device is built.
            adapter: 0,
        })
        .map_err(|e| format!("Failed to create the webcam video mix: {e}"))?;

    // Track 1 is always x264 (CRF): predictable, no second hardware-encoder
    // session contention with the screen track. The encoder name becomes the
    // mp4 track name (mp4_output writes it into the track's udta box).
    let encoder =
        ObsEncoder::create_video("obs_x264", "Webcam", Some(&encoder_settings(settings.crf)))
            .map_err(|e| format!("Failed to create the webcam encoder: {e}"))?;
    encoder.set_video(video);

    Ok(Webcam {
        encoder,
        _view: view,
        _item: item,
        _scene: scene,
        _source: source,
        canvas,
    })
}

/// x264 settings for the webcam track.
pub fn encoder_settings(crf: u16) -> ObsData {
    let s = ObsData::new();
    s.set_string("rate_control", "CRF");
    s.set_int("crf", crf as i64);
    s.set_string("preset", "veryfast");
    s.set_string("profile", "high");
    // Same keyframe cadence as the screen track: mp4_output can only flush a
    // fragment once EVERY track has caught up to the track-0 keyframe, so a
    // long webcam GOP would stall the crash-resilience cadence too.
    s.set_int("keyint_sec", crate::encoder_config::KEYINT_SEC);
    s
}
