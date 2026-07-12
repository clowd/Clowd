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
    // Existing VideoToolbox scan, unchanged.
    available
        .iter()
        .find(|t| {
            let lower = t.to_lowercase();
            (lower.contains("apple") || lower.contains("videotoolbox"))
                && (lower.contains("h264") || lower.contains("avc") || lower.contains("264"))
        })
        .cloned()
}

/// Encoder-specific settings (§2.5 table). The CRF/CQP value is passed through
/// unmodified — the shell owns quality mapping.
pub fn encoder_settings(encoder_id: &str, config: &EncoderConfig) -> ObsData {
    let settings = ObsData::new();
    let crf = config.crf as i64;
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
        _ => {
            // macOS VideoToolbox or other: minimal generic quality settings.
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
}
