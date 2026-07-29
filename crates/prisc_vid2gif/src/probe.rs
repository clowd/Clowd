//! Parses `ffprobe -of json` output for the one thing each stage needs:
//! the video dimensions (for `--width` clamping) and the container duration
//! (to turn `out_time_us` into a percentage).

use anyhow::{Context, Result};

#[derive(Debug)]
pub struct ProbeInfo {
    pub width: u32,
    pub height: u32,
    /// `None` when the container does not report a duration (progress then
    /// falls back to stage-boundary jumps only).
    pub duration_us: Option<u64>,
}

pub fn parse(json: &str) -> Result<ProbeInfo> {
    let v: serde_json::Value = serde_json::from_str(json).context("invalid ffprobe JSON")?;
    let stream = v["streams"].get(0).context("input has no video stream")?;
    let width = stream["width"]
        .as_u64()
        .context("video stream has no width")? as u32;
    let height = stream["height"]
        .as_u64()
        .context("video stream has no height")? as u32;
    let duration_us = v["format"]["duration"]
        .as_str()
        .and_then(|s| s.trim().parse::<f64>().ok())
        .filter(|d| d.is_finite() && *d > 0.0)
        .map(|d| (d * 1_000_000.0) as u64);
    Ok(ProbeInfo {
        width,
        height,
        duration_us,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_typical_output() {
        let json = r#"{
            "streams": [{"width": 1920, "height": 1080}],
            "format": {"duration": "3.500000"}
        }"#;
        let info = parse(json).unwrap();
        assert_eq!(info.width, 1920);
        assert_eq!(info.height, 1080);
        assert_eq!(info.duration_us, Some(3_500_000));
    }

    #[test]
    fn missing_duration_is_none() {
        let json = r#"{"streams": [{"width": 640, "height": 480}], "format": {}}"#;
        assert_eq!(parse(json).unwrap().duration_us, None);
    }

    #[test]
    fn non_numeric_duration_is_none() {
        let json = r#"{"streams": [{"width": 640, "height": 480}], "format": {"duration": "N/A"}}"#;
        assert_eq!(parse(json).unwrap().duration_us, None);
    }

    #[test]
    fn no_video_stream_errors() {
        let err = parse(r#"{"streams": [], "format": {"duration": "1.0"}}"#).unwrap_err();
        assert!(err.to_string().contains("no video stream"), "{err}");
    }

    #[test]
    fn invalid_json_errors() {
        assert!(parse("not json").is_err());
    }
}
