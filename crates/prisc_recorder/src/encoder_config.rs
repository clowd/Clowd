//! Encoder selection & settings (DESIGN §2.5). Selection is a pure function
//! over the runtime-enumerated encoder id list so it can be unit-tested;
//! presence == usable on Windows (the obs-*-test.exe probes gate registration).

use obs::data::ObsData;
use obs::encoder::ObsEncoder;
use obs::error::ObsError;

pub struct EncoderConfig {
    pub hw_accel: bool,
    pub crf: u16,
    pub low_cpu: bool,
    /// Output (post-downscale) frame size and rate. Only the bitrate-driven
    /// fallback paths need them (Intel-Mac VideoToolbox, see
    /// [`vt_abr_bitrate_kbps`]); the quality-driven mappings ignore them.
    pub width: u32,
    pub height: u32,
    pub fps: u32,
}

/// Runtime encoder capabilities that change which settings are valid. Probed
/// once from libobs by [`create_video_encoder`] and passed into the pure
/// [`encoder_settings`] so the mapping stays unit-testable.
#[derive(Debug, Clone, Copy, Default)]
pub struct EncoderCaps {
    /// obs-qsv11 lists `ICQ` in its `rate_control` property only on Haswell
    /// or newer (or an unrecognised CPU); older iGPUs must use CQP.
    pub qsv_icq: bool,
}

impl EncoderCaps {
    /// Probes the capabilities the way OBS's own recording presets do
    /// (`SimpleOutput::icq_available`): read the encoder type's property list
    /// rather than guessing from the CPU generation.
    pub fn probe() -> Self {
        let qsv_icq = obs::properties::encoder_list_property(QSV_ID, "rate_control")
            .map(|items| items.iter().any(|i| i.value == "ICQ"))
            .unwrap_or(false);
        Self { qsv_icq }
    }
}

pub const X264_ID: &str = "obs_x264";
pub const NVENC_ID: &str = "obs_nvenc_h264_tex";
pub const AMF_ID: &str = "h264_texture_amf";
pub const QSV_ID: &str = "obs_qsv11_v2";

/// Keyframe interval (seconds) for every video encoder, screen and webcam
/// alike. The hybrid MP4 output ("mp4_output") flushes a fragment to disk at
/// each track-0 keyframe, so this is also the crash-resilience cadence: with
/// the encoder default GOP (x264: 250 frames ≈ 8.3 s @ 30 fps) a hard kill
/// loses up to a whole GOP and a recording killed before the first fragment
/// (~9 s) is a zero-byte total loss. 2 s bounds crash loss to a few seconds
/// and keeps editor seeks (which decode forward from the previous keyframe)
/// fast. The `keyint_sec` key is honored by x264, NVENC, AMF, QSV and
/// VideoToolbox.
pub const KEYINT_SEC: i64 = 2;

/// Picks the video encoder id from the available list. Hardware priority
/// (Windows): NVENC → AMF → QSV; anything else (or `hw_accel == false`) falls
/// back to x264. macOS scans for a VideoToolbox H.264 encoder instead.
pub fn select_encoder(available: &[String], hw_accel: bool) -> String {
    if hw_accel {
        if let Some(id) = select_hardware_encoder(available) {
            return id;
        }
        eprintln!("No hardware encoder available, falling back to x264");
    }
    X264_ID.to_string()
}

#[cfg(not(target_os = "macos"))]
fn select_hardware_encoder(available: &[String]) -> Option<String> {
    const PRIORITY: [&str; 3] = [NVENC_ID, AMF_ID, QSV_ID];
    PRIORITY
        .iter()
        .find(|id| available.iter().any(|a| a == *id))
        .map(|id| id.to_string())
}

#[cfg(target_os = "macos")]
fn select_hardware_encoder(available: &[String]) -> Option<String> {
    select_videotoolbox_encoder(available)
}

/// VideoToolbox registers one OBS encoder per OS-enumerated VT encoder, id =
/// the VT EncoderID verbatim — which can include Apple's *software* H.264
/// encoder. Rank hardware implementations first: "ave" is the Apple Silicon
/// media engine, "gva" the Intel-era GPU encoder; any other match (e.g. the
/// plain software id) is a last resort rather than a miss, since it still
/// beats x264 only when nothing better is registered.
#[cfg(any(target_os = "macos", test))]
fn select_videotoolbox_encoder(available: &[String]) -> Option<String> {
    let is_vt_h264 = |t: &str| {
        let lower = t.to_lowercase();
        (lower.contains("apple") || lower.contains("videotoolbox"))
            && (lower.contains("h264") || lower.contains("avc") || lower.contains("264"))
    };
    let matches: Vec<&String> = available.iter().filter(|t| is_vt_h264(t)).collect();
    for hw_marker in ["ave", "gva"] {
        if let Some(id) = matches
            .iter()
            .find(|t| t.to_lowercase().contains(hw_marker))
        {
            return Some((*id).clone());
        }
    }
    matches.first().map(|id| (*id).clone())
}

/// Maps x264-style CRF (0 best - 51 worst) onto VideoToolbox's quality slider
/// (0 worst - 100 best; the plugin divides by 100 for
/// kVTCompressionPropertyKey_Quality). VT ignores the x264 "crf" key entirely,
/// so without this mapping every recording used the plugin default of 60.
fn vt_quality_from_crf(crf: u16) -> i64 {
    ((51u16.saturating_sub(crf) as f64) * 100.0 / 51.0).round() as i64
}

/// Intel-Mac VideoToolbox has no quality mode, so it records at an average
/// bitrate. Scale a 4 Mbps-at-3440x1440@30 budget by pixel rate and clamp to
/// 1.5-6 Mbps: enough for a screen recording at the common sizes, and a hard
/// ceiling so a 4K60 capture on an old Mac cannot balloon (6 Mbps is still
/// ~1.35 GB per 30 minutes). This is a size compromise by design, not an
/// equivalent of the CRF tiers; Apple Silicon takes the quality path instead.
fn vt_abr_bitrate_kbps(width: u32, height: u32, fps: u32) -> i64 {
    const REF_PIXELS: f64 = 3440.0 * 1440.0;
    let pixel_rate = (width as f64 * height as f64 / REF_PIXELS) * (fps.max(1) as f64 / 30.0);
    (4000.0 * pixel_rate).round().clamp(1500.0, 6000.0) as i64
}

/// Upper bound shared by x264's CRF and NVENC's target quality; both are
/// 0-51-ish scales where 51 is the worst quality the encoder accepts.
const MAX_QUALITY: i64 = 51;

/// Encoder-specific settings (§2.5 table). `crf` is an x264-style CRF
/// (0 best - 51 worst) and is the single quality knob the caller sees; each
/// encoder maps it onto its own quality scale here so that a given `crf`
/// lands on comparable bytes/quality whichever encoder ends up selected:
///
/// - x264: CRF passthrough (`superfast` needs a +2 offset, see below).
/// - NVENC: constant-quality VBR (`CQVBR`) at `crf + 4` — measured to match
///   the byte size of the old CQP-at-`crf` output at equal or better quality.
/// - AMF: constant QP at `crf` (what OBS's own recording presets use).
/// - QSV: `ICQ` (Intel's constant-quality mode) at `crf` when the plugin
///   lists it, otherwise constant QP at `crf` — the same choice and the same
///   probe as OBS's recording presets. The plugin only validates the rate
///   control when the output starts, so the probe has to happen up front.
/// - VideoToolbox: its inverted 0-100 quality slider on Apple Silicon, see
///   [`vt_quality_from_crf`]; Intel Macs fall back to ABR at
///   [`vt_abr_bitrate_kbps`].
pub fn encoder_settings(encoder_id: &str, config: &EncoderConfig, caps: &EncoderCaps) -> ObsData {
    let settings = ObsData::new();
    let crf = config.crf as i64;
    // Uniform across every encoder we select (see the const's rationale).
    settings.set_int("keyint_sec", KEYINT_SEC);
    match encoder_id {
        NVENC_ID => {
            // NVIDIA's recommended recording configuration as exposed by the
            // OBS 32 nvenc plugin (keys per obs-nvenc/nvenc-properties.c).
            // CQVBR is NVENC's CRF-like mode: VBR steered by a target quality
            // with no bitrate ceiling (bitrate/max_bitrate deliberately unset
            // = unconstrained). NVENC's CQ scale sits about 4 points "below"
            // x264's CRF scale, so +4 recenters it: bytes match the previous
            // CQP-at-crf output at every crf, at equal-or-better quality.
            // bframe_ref_mode stays at the plugin default (disabled): measured
            // free in encode time but +2.6% bytes for +0.2 dB at p6 on screen
            // content, and a pure byte cost at lighter presets. p7 produced
            // byte-identical H.264 output to p6, and 4 B-frames matched 2.
            settings.set_string("rate_control", "CQVBR");
            settings.set_int("target_quality", (crf + 4).clamp(1, MAX_QUALITY));
            settings.set_string("preset", "p6");
            settings.set_string("tune", "hq");
            settings.set_string("multipass", "qres");
            settings.set_bool("lookahead", true);
            settings.set_bool("adaptive_quantization", true);
            settings.set_int("bf", 2);
            settings.set_string("profile", "high");
        }
        AMF_ID => {
            settings.set_string("rate_control", "CQP");
            settings.set_int("cqp", crf);
            settings.set_string("preset", "quality");
            settings.set_string("profile", "high");
        }
        QSV_ID => {
            if caps.qsv_icq {
                settings.set_string("rate_control", "ICQ");
                settings.set_int("icq_quality", crf);
            } else {
                settings.set_string("rate_control", "CQP");
            }
            // Always populated: harmless under ICQ, and the CQP fallback (and
            // anyone reading the log) gets the same value either way.
            settings.set_int("qpi", crf);
            settings.set_int("qpp", crf);
            settings.set_int("qpb", crf);
            settings.set_string("profile", "high");
        }
        X264_ID => {
            settings.set_string("rate_control", "CRF");
            if config.low_cpu {
                // `ultrafast` was the previous low-CPU preset, but it disables
                // CABAC and mbtree and measured 6.3x the bytes of `veryfast`
                // at the same CRF for only ~27% less CPU. `superfast` keeps
                // CABAC and is ~6% faster than `veryfast`; it still drops
                // mbtree, which shifts its CRF scale, so +2 re-centers it on
                // `veryfast`'s bytes.
                settings.set_int("crf", (crf + 2).min(MAX_QUALITY));
                settings.set_string("preset", "superfast");
            } else {
                settings.set_int("crf", crf);
                settings.set_string("preset", "veryfast");
            }
            settings.set_string("profile", "high");
        }
        id if id.to_lowercase().contains("apple") || id.to_lowercase().contains("videotoolbox") => {
            // CRF rate control needs Apple Silicon; on Intel VT warns and falls
            // back to ABR at the "bitrate" setting, so size that to the frame
            // rather than inheriting the plugin default (or a flat number that
            // is far too much for 720p and too little for 4K).
            settings.set_string("rate_control", "CRF");
            settings.set_int("quality", vt_quality_from_crf(config.crf));
            settings.set_int(
                "bitrate",
                vt_abr_bitrate_kbps(config.width, config.height, config.fps),
            );
            // Plugin default, made explicit: frame reordering is part of the
            // 2 B-frame policy every other encoder here runs.
            settings.set_bool("bframes", true);
            settings.set_string("profile", "high");
        }
        _ => {
            // Unknown encoder: minimal generic quality settings.
            settings.set_string("rate_control", "CRF");
            settings.set_int("crf", crf);
            settings.set_string("profile", "high");
        }
    }
    settings
}

/// Human-readable summary of the effective rate control and quality value
/// in `settings` (as produced by [`encoder_settings`]) for `encoder_id`, e.g.
/// `NVENC CQVBR target_quality=22 preset=p6` or
/// `x264 CRF crf=18 preset=veryfast`. Read back from the settings object
/// rather than recomputed, so the startup log and the tests both report what
/// the encoder is actually handed.
pub fn describe_settings(encoder_id: &str, settings: &ObsData) -> String {
    let rc = settings.get_string("rate_control");
    match encoder_id {
        NVENC_ID => format!(
            "NVENC {rc} target_quality={} preset={}",
            settings.get_int("target_quality"),
            settings.get_string("preset")
        ),
        AMF_ID => format!(
            "AMF {rc} cqp={} preset={}",
            settings.get_int("cqp"),
            settings.get_string("preset")
        ),
        QSV_ID if rc == "ICQ" => {
            format!("QSV ICQ icq_quality={}", settings.get_int("icq_quality"))
        }
        QSV_ID => format!("QSV {rc} qp={}", settings.get_int("qpi")),
        X264_ID => format!(
            "x264 {rc} crf={} preset={}",
            settings.get_int("crf"),
            settings.get_string("preset")
        ),
        id if id.to_lowercase().contains("apple") || id.to_lowercase().contains("videotoolbox") => {
            format!(
                "VideoToolbox {rc} quality={} (ABR fallback bitrate={} kbps)",
                settings.get_int("quality"),
                settings.get_int("bitrate")
            )
        }
        _ => format!("{rc} crf={}", settings.get_int("crf")),
    }
}

pub fn create_video_encoder(
    available: &[String],
    config: &EncoderConfig,
) -> Result<ObsEncoder, ObsError> {
    let encoder_id = select_encoder(available, config.hw_accel);
    let caps = EncoderCaps::probe();
    let settings = encoder_settings(&encoder_id, config, &caps);
    eprintln!(
        "Using video encoder '{encoder_id}' ({}; requested crf={}, low_cpu={})",
        describe_settings(&encoder_id, &settings),
        config.crf,
        config.low_cpu
    );
    // The encoder name becomes the mp4 track name (mp4_output writes it into
    // the track's udta box), so it is user-visible in players.
    ObsEncoder::create_video(&encoder_id, "Screen", Some(&settings))
}

/// AAC bitrate (kbps) per audio track. 192 rather than the previous 128:
/// screen recordings carry speech and system audio that get re-encoded again
/// at edit/upload time, and the extra headroom is cheap next to the video.
pub const AUDIO_BITRATE_KBPS: i64 = 192;

/// libobs mixer sample rate (every source is resampled to it, every AAC track
/// is encoded at it). 48 kHz is what Windows and macOS run their devices at
/// in shared mode, so the common case involves no resampling at all.
pub const AUDIO_SAMPLE_RATE: u32 = 48000;

fn audio_encoder_settings() -> ObsData {
    let settings = ObsData::new();
    settings.set_int("bitrate", AUDIO_BITRATE_KBPS);
    settings
}

/// Creates one audio encoder reading libobs audio mixer `mixer_idx` — the
/// mixer whose sources make up output audio track `mixer_idx` (multi-track
/// mode); single-track recordings only ever use mixer 0. `name` becomes the
/// mp4 track name.
pub fn create_audio_encoder(
    available: &[String],
    name: &str,
    mixer_idx: usize,
) -> Result<ObsEncoder, ObsError> {
    let settings = audio_encoder_settings();
    let id = if available.iter().any(|t| t == "CoreAudio_AAC") {
        "CoreAudio_AAC"
    } else {
        "ffmpeg_aac"
    };
    eprintln!("Using audio encoder '{id}' for track {mixer_idx} ('{name}')");
    ObsEncoder::create_audio(id, name, mixer_idx, Some(&settings))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ids(list: &[&str]) -> Vec<String> {
        list.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn no_hw_accel_always_x264() {
        let available = ids(&[NVENC_ID, QSV_ID, "obs_x264"]);
        assert_eq!(select_encoder(&available, false), "obs_x264");
    }

    #[cfg(not(target_os = "macos"))]
    #[test]
    fn hw_priority_nvenc_first() {
        let available = ids(&[QSV_ID, AMF_ID, NVENC_ID, "obs_x264"]);
        assert_eq!(select_encoder(&available, true), NVENC_ID);
    }

    #[cfg(not(target_os = "macos"))]
    #[test]
    fn hw_priority_amf_over_qsv() {
        let available = ids(&[QSV_ID, AMF_ID, "obs_x264"]);
        assert_eq!(select_encoder(&available, true), AMF_ID);
    }

    #[cfg(not(target_os = "macos"))]
    #[test]
    fn hw_qsv_when_only_qsv() {
        let available = ids(&[QSV_ID, "obs_x264"]);
        assert_eq!(select_encoder(&available, true), QSV_ID);
    }

    #[test]
    fn hw_falls_back_to_x264_when_none_registered() {
        let available = ids(&["obs_x264", "ffmpeg_aac"]);
        assert_eq!(select_encoder(&available, true), "obs_x264");
    }

    #[test]
    fn vt_prefers_ave_hardware_over_software() {
        let available = ids(&[
            "com.apple.videotoolbox.videoencoder.h264",
            "com.apple.videotoolbox.videoencoder.ave.avc",
        ]);
        assert_eq!(
            select_videotoolbox_encoder(&available).unwrap(),
            "com.apple.videotoolbox.videoencoder.ave.avc"
        );
    }

    #[test]
    fn vt_software_match_still_beats_nothing() {
        let available = ids(&["com.apple.videotoolbox.videoencoder.h264", "obs_x264"]);
        assert_eq!(
            select_videotoolbox_encoder(&available).unwrap(),
            "com.apple.videotoolbox.videoencoder.h264"
        );
    }

    #[test]
    fn vt_ignores_non_h264_and_non_apple_ids() {
        let available = ids(&[
            "com.apple.videotoolbox.videoencoder.hevc.vcp",
            "obs_x264",
            "CoreAudio_AAC",
        ]);
        assert_eq!(select_videotoolbox_encoder(&available), None);
    }

    #[test]
    fn vt_quality_mapping_inverts_crf() {
        assert_eq!(vt_quality_from_crf(0), 100);
        assert_eq!(vt_quality_from_crf(51), 0);
        assert_eq!(vt_quality_from_crf(24), 53);
        // Clowd presets must be distinguishable: High(16) > Medium(23) > Low(29)
        assert!(vt_quality_from_crf(16) > vt_quality_from_crf(23));
        assert!(vt_quality_from_crf(23) > vt_quality_from_crf(29));
        // out-of-range input saturates instead of wrapping
        assert_eq!(vt_quality_from_crf(60), 0);
    }

    fn cfg(crf: u16, low_cpu: bool) -> EncoderConfig {
        EncoderConfig {
            hw_accel: true,
            crf,
            low_cpu,
            width: 3440,
            height: 1440,
            fps: 30,
        }
    }

    const NO_CAPS: EncoderCaps = EncoderCaps { qsv_icq: false };
    const ICQ_CAPS: EncoderCaps = EncoderCaps { qsv_icq: true };

    #[test]
    fn nvenc_maps_crf_onto_cqvbr_target_quality() {
        let s = encoder_settings(NVENC_ID, &cfg(18, false), &NO_CAPS);
        assert_eq!(s.get_string("rate_control"), "CQVBR");
        assert_eq!(s.get_int("target_quality"), 22);
        assert_eq!(s.get_string("preset"), "p6");
        assert_eq!(s.get_string("tune"), "hq");
        assert_eq!(s.get_string("multipass"), "qres");
        assert!(s.get_bool("lookahead"));
        assert!(s.get_bool("adaptive_quantization"));
        assert_eq!(s.get_int("bf"), 2);
        assert_eq!(s.get_string("profile"), "high");
        assert_eq!(s.get_int("keyint_sec"), KEYINT_SEC);
        // unconstrained VBR: no bitrate ceiling is handed to the plugin
        assert_eq!(s.get_int("bitrate"), 0);
        assert_eq!(s.get_int("max_bitrate"), 0);
        // low_cpu is an x264-only knob
        let s = encoder_settings(NVENC_ID, &cfg(18, true), &NO_CAPS);
        assert_eq!(s.get_string("preset"), "p6");
        assert_eq!(
            describe_settings(NVENC_ID, &s),
            "NVENC CQVBR target_quality=22 preset=p6"
        );
    }

    #[test]
    fn nvenc_target_quality_clamps_to_51() {
        for crf in [47, 48, 50, 51] {
            let s = encoder_settings(NVENC_ID, &cfg(crf, false), &NO_CAPS);
            assert_eq!(s.get_int("target_quality"), 51, "crf {crf}");
        }
        assert_eq!(
            encoder_settings(NVENC_ID, &cfg(0, false), &NO_CAPS).get_int("target_quality"),
            4
        );
    }

    #[test]
    fn x264_low_cpu_uses_superfast_at_crf_plus_two() {
        let s = encoder_settings(X264_ID, &cfg(18, true), &NO_CAPS);
        assert_eq!(s.get_string("rate_control"), "CRF");
        assert_eq!(s.get_string("preset"), "superfast");
        assert_eq!(s.get_int("crf"), 20);
        assert_eq!(s.get_string("profile"), "high");
        assert_eq!(
            describe_settings(X264_ID, &s),
            "x264 CRF crf=20 preset=superfast"
        );
        for crf in [49, 50, 51] {
            let s = encoder_settings(X264_ID, &cfg(crf, true), &NO_CAPS);
            assert_eq!(s.get_int("crf"), 51, "crf {crf}");
        }
    }

    #[test]
    fn x264_default_path_passes_crf_through_at_veryfast() {
        for crf in [0, 18, 24, 51] {
            let s = encoder_settings(X264_ID, &cfg(crf, false), &NO_CAPS);
            assert_eq!(s.get_string("rate_control"), "CRF");
            assert_eq!(s.get_string("preset"), "veryfast");
            assert_eq!(s.get_int("crf"), crf as i64);
            assert_eq!(s.get_string("profile"), "high");
            assert_eq!(s.get_int("keyint_sec"), KEYINT_SEC);
        }
        assert_eq!(
            describe_settings(
                X264_ID,
                &encoder_settings(X264_ID, &cfg(18, false), &NO_CAPS)
            ),
            "x264 CRF crf=18 preset=veryfast"
        );
    }

    #[test]
    fn amf_stays_constant_qp_at_crf() {
        let s = encoder_settings(AMF_ID, &cfg(18, false), &NO_CAPS);
        assert_eq!(s.get_string("rate_control"), "CQP");
        assert_eq!(s.get_int("cqp"), 18);
        assert_eq!(s.get_string("preset"), "quality");
        assert_eq!(
            describe_settings(AMF_ID, &s),
            "AMF CQP cqp=18 preset=quality"
        );
    }

    #[test]
    fn qsv_uses_icq_when_listed_and_cqp_otherwise() {
        let s = encoder_settings(QSV_ID, &cfg(18, false), &ICQ_CAPS);
        assert_eq!(s.get_string("rate_control"), "ICQ");
        assert_eq!(s.get_int("icq_quality"), 18);
        assert_eq!(describe_settings(QSV_ID, &s), "QSV ICQ icq_quality=18");

        let s = encoder_settings(QSV_ID, &cfg(18, false), &NO_CAPS);
        assert_eq!(s.get_string("rate_control"), "CQP");
        assert_eq!(
            (s.get_int("qpi"), s.get_int("qpp"), s.get_int("qpb")),
            (18, 18, 18)
        );
        assert_eq!(describe_settings(QSV_ID, &s), "QSV CQP qp=18");
        // the ICQ path carries the same QP triple, so a manual fallback or a
        // log reader sees one number either way
        let s = encoder_settings(QSV_ID, &cfg(23, false), &ICQ_CAPS);
        assert_eq!((s.get_int("qpi"), s.get_int("icq_quality")), (23, 23));
    }

    #[test]
    fn videotoolbox_abr_fallback_scales_with_pixel_rate_and_clamps() {
        // reference point: 3440x1440@30 -> 4 Mbps
        assert_eq!(vt_abr_bitrate_kbps(3440, 1440, 30), 4000);
        // 1080p30 scales down proportionally, 720p30 hits the floor
        assert_eq!(vt_abr_bitrate_kbps(1920, 1080, 30), 1674);
        assert_eq!(vt_abr_bitrate_kbps(1280, 720, 30), 1500);
        // 4K60 hits the ceiling
        assert_eq!(vt_abr_bitrate_kbps(3840, 2160, 60), 6000);
        // 60 fps doubles the 30 fps budget at the same size
        assert_eq!(vt_abr_bitrate_kbps(1920, 1080, 60), 3349);

        let vt = "com.apple.videotoolbox.videoencoder.ave.avc";
        let s = encoder_settings(vt, &cfg(18, false), &NO_CAPS);
        assert_eq!(s.get_string("rate_control"), "CRF");
        assert_eq!(s.get_int("quality"), vt_quality_from_crf(18));
        assert_eq!(s.get_int("bitrate"), 4000);
        assert!(s.get_bool("bframes"));
        assert_eq!(s.get_int("keyint_sec"), KEYINT_SEC);
        let mut c = cfg(18, false);
        c.width = 3840;
        c.height = 2160;
        c.fps = 60;
        assert_eq!(encoder_settings(vt, &c, &NO_CAPS).get_int("bitrate"), 6000);
    }

    #[test]
    fn audio_sample_rate_is_48k() {
        assert_eq!(AUDIO_SAMPLE_RATE, 48000);
    }

    #[test]
    fn audio_encoder_bitrate_is_192() {
        assert_eq!(AUDIO_BITRATE_KBPS, 192);
        assert_eq!(audio_encoder_settings().get_int("bitrate"), 192);
    }
}
