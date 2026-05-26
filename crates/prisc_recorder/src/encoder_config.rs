use obs::data::ObsData;
use obs::encoder::ObsEncoder;
use obs::error::ObsError;
use obs::properties::enum_encoder_types;

pub struct EncoderConfig {
    pub hw_accel: bool,
    pub crf: u16,
    pub low_cpu: bool,
    pub output_width: u32,
    pub output_height: u32,
}

pub fn create_video_encoder(config: &EncoderConfig) -> Result<ObsEncoder, ObsError> {
    let encoder_id = if config.hw_accel {
        find_hardware_encoder().unwrap_or_else(|| {
            eprintln!("No hardware encoder found, falling back to x264");
            "obs_x264".to_string()
        })
    } else {
        "obs_x264".to_string()
    };

    let settings = ObsData::new();

    if encoder_id == "obs_x264" {
        settings.set_string("rate_control", "CRF");
        settings.set_int("crf", config.crf as i64);
        let preset = if config.low_cpu { "ultrafast" } else { "veryfast" };
        settings.set_string("preset", preset);
        settings.set_string("profile", "high");
    } else {
        // VideoToolbox hardware encoder
        settings.set_string("rate_control", "CRF");
        settings.set_int("quality", (51 - config.crf.min(51)) as i64);
        settings.set_string("profile", "high");
    }

    ObsEncoder::create_video(&encoder_id, "video_encoder", Some(&settings))
}

pub fn create_audio_encoder() -> Result<ObsEncoder, ObsError> {
    let settings = ObsData::new();
    settings.set_int("bitrate", 128);

    // Prefer CoreAudio AAC, fall back to ffmpeg_aac
    let encoder_types = enum_encoder_types();
    let id = if encoder_types.iter().any(|t| t == "CoreAudio_AAC") {
        "CoreAudio_AAC"
    } else {
        "ffmpeg_aac"
    };

    ObsEncoder::create_audio(id, "audio_encoder", 0, Some(&settings))
}

fn find_hardware_encoder() -> Option<String> {
    let types = enum_encoder_types();
    // Look for VideoToolbox H.264 encoders
    for t in &types {
        if t.contains("apple") || t.contains("videotoolbox") || t.contains("vt_") {
            if t.contains("h264") || t.contains("avc") {
                return Some(t.clone());
            }
        }
    }
    // Also check for com.apple.videotoolbox pattern
    for t in &types {
        if t.starts_with("com.apple") && !t.contains("prores") {
            return Some(t.clone());
        }
    }
    None
}
