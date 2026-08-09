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
}

pub const X264_ID: &str = "obs_x264";

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
    const PRIORITY: [&str; 3] = ["obs_nvenc_h264_tex", "h264_texture_amf", "obs_qsv11_v2"];
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

/// Encoder-specific settings (§2.5 table). The CRF/CQP value is passed through
/// unmodified — the shell owns quality mapping — except VideoToolbox, whose
/// quality slider uses an inverted 0-100 scale (see [`vt_quality_from_crf`]).
pub fn encoder_settings(encoder_id: &str, config: &EncoderConfig) -> ObsData {
    let settings = ObsData::new();
    let crf = config.crf as i64;
    // Uniform across every encoder we select (see the const's rationale).
    settings.set_int("keyint_sec", KEYINT_SEC);
    match encoder_id {
        "obs_nvenc_h264_tex" => {
            settings.set_string("rate_control", "CQP");
            settings.set_int("cqp", crf);
            settings.set_string("preset", "p5");
            settings.set_string("profile", "high");
        }
        "h264_texture_amf" => {
            settings.set_string("rate_control", "CQP");
            settings.set_int("cqp", crf);
            settings.set_string("preset", "quality");
            settings.set_string("profile", "high");
        }
        "obs_qsv11_v2" => {
            settings.set_string("rate_control", "CQP");
            settings.set_int("qpi", crf);
            settings.set_int("qpp", crf);
            settings.set_int("qpb", crf);
            settings.set_string("profile", "high");
        }
        X264_ID => {
            settings.set_string("rate_control", "CRF");
            settings.set_int("crf", crf);
            settings.set_string(
                "preset",
                if config.low_cpu {
                    "ultrafast"
                } else {
                    "veryfast"
                },
            );
            settings.set_string("profile", "high");
        }
        id if id.to_lowercase().contains("apple") || id.to_lowercase().contains("videotoolbox") => {
            // CRF rate control needs Apple Silicon; on Intel VT warns and falls
            // back to ABR at the "bitrate" setting, so give that a sane value
            // rather than inheriting the plugin default silently.
            settings.set_string("rate_control", "CRF");
            settings.set_int("quality", vt_quality_from_crf(config.crf));
            settings.set_int("bitrate", 8000);
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

pub fn create_video_encoder(
    available: &[String],
    config: &EncoderConfig,
) -> Result<ObsEncoder, ObsError> {
    let encoder_id = select_encoder(available, config.hw_accel);
    let settings = encoder_settings(&encoder_id, config);
    eprintln!(
        "Using video encoder '{encoder_id}' (crf/cqp={}, low_cpu={})",
        config.crf, config.low_cpu
    );
    ObsEncoder::create_video(&encoder_id, "video_encoder", Some(&settings))
}

pub fn create_audio_encoder(available: &[String]) -> Result<ObsEncoder, ObsError> {
    let settings = ObsData::new();
    settings.set_int("bitrate", 128);
    let id = if available.iter().any(|t| t == "CoreAudio_AAC") {
        "CoreAudio_AAC"
    } else {
        "ffmpeg_aac"
    };
    eprintln!("Using audio encoder '{id}'");
    ObsEncoder::create_audio(id, "audio_encoder", 0, Some(&settings))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ids(list: &[&str]) -> Vec<String> {
        list.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn no_hw_accel_always_x264() {
        let available = ids(&["obs_nvenc_h264_tex", "obs_qsv11_v2", "obs_x264"]);
        assert_eq!(select_encoder(&available, false), "obs_x264");
    }

    #[cfg(not(target_os = "macos"))]
    #[test]
    fn hw_priority_nvenc_first() {
        let available = ids(&[
            "obs_qsv11_v2",
            "h264_texture_amf",
            "obs_nvenc_h264_tex",
            "obs_x264",
        ]);
        assert_eq!(select_encoder(&available, true), "obs_nvenc_h264_tex");
    }

    #[cfg(not(target_os = "macos"))]
    #[test]
    fn hw_priority_amf_over_qsv() {
        let available = ids(&["obs_qsv11_v2", "h264_texture_amf", "obs_x264"]);
        assert_eq!(select_encoder(&available, true), "h264_texture_amf");
    }

    #[cfg(not(target_os = "macos"))]
    #[test]
    fn hw_qsv_when_only_qsv() {
        let available = ids(&["obs_qsv11_v2", "obs_x264"]);
        assert_eq!(select_encoder(&available, true), "obs_qsv11_v2");
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
}
