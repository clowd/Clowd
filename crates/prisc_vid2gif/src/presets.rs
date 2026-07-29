//! Quality presets and the FFmpeg filter strings for each conversion stage.
//!
//! The pipeline is three ffmpeg invocations (see main.rs): fps/scale are baked
//! into a small lossless intermediate first, so the palette stages — which are
//! the expensive part at full resolution — always run on small frames.

use clap::ValueEnum;

#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
pub enum Quality {
    /// 20 fps, error-diffusion dithering — best colors, largest file.
    Best,
    /// 15 fps, fine ordered dithering — good balance (default).
    Good,
    /// 10 fps, coarse ordered dithering — smallest file.
    Fair,
}

impl Quality {
    /// Output frame rate when `--fps` is not given.
    pub fn fps(self) -> u32 {
        match self {
            Quality::Best => 20,
            Quality::Good => 15,
            Quality::Fair => 10,
        }
    }

    /// `paletteuse` dithering — with fps, the main quality/size lever.
    fn dither(self) -> &'static str {
        match self {
            Quality::Best => "sierra2_4a",
            Quality::Good => "bayer:bayer_scale=3",
            Quality::Fair => "bayer:bayer_scale=5",
        }
    }
}

/// Stage A (`-vf`): resample to the target fps, optionally downscale.
/// `scale` uses `-2` (not `-1`) for the height so the x264 intermediate never
/// sees an odd dimension.
pub fn intermediate_vf(fps: u32, scale_width: Option<u32>) -> String {
    match scale_width {
        Some(w) => format!("fps={fps},scale={w}:-2:flags=lanczos"),
        None => format!("fps={fps}"),
    }
}

/// Resolves the `--max-width`/`--max-height` clamps against the source size:
/// aspect is preserved, the more restrictive clamp wins, and the result is
/// `None` when no downscale is needed (never upscales — same contract as
/// obs-express's recording resolution cap). The returned width is floored to
/// even so `scale=W:-2` keeps both output dimensions near the clamp.
pub fn clamp_width(src_w: u32, src_h: u32, max_w: Option<u32>, max_h: Option<u32>) -> Option<u32> {
    let fw = max_w.map(|m| m as f64 / src_w as f64);
    let fh = max_h.map(|m| m as f64 / src_h as f64);
    let f = match (fw, fh) {
        (Some(a), Some(b)) => a.min(b),
        (Some(a), None) => a,
        (None, Some(b)) => b,
        (None, None) => return None,
    };
    if f >= 1.0 {
        return None;
    }
    let w = (src_w as f64 * f) as u32 & !1;
    Some(w.max(2))
}

/// Stage B (`-vf`): build one global 256-color palette. `stats_mode=diff`
/// weights pixels that change between frames, which favors the moving content
/// of a screen recording over its static background.
pub fn palettegen_vf() -> &'static str {
    "palettegen=stats_mode=diff"
}

/// Stage C (`-lavfi`): map frames onto the palette. `diff_mode=rectangle`
/// re-dithers only the changed region of each frame, which both shrinks the
/// file and avoids shimmering in static areas.
pub fn paletteuse_lavfi(quality: Quality) -> String {
    format!(
        "[0:v][1:v]paletteuse=dither={}:diff_mode=rectangle",
        quality.dither()
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn preset_fps_ordering() {
        assert!(Quality::Best.fps() > Quality::Good.fps());
        assert!(Quality::Good.fps() > Quality::Fair.fps());
    }

    #[test]
    fn intermediate_vf_without_scale() {
        assert_eq!(intermediate_vf(15, None), "fps=15");
    }

    #[test]
    fn intermediate_vf_with_scale() {
        assert_eq!(
            intermediate_vf(20, Some(480)),
            "fps=20,scale=480:-2:flags=lanczos"
        );
    }

    #[test]
    fn clamp_width_none_when_unconstrained() {
        assert_eq!(clamp_width(1920, 1080, None, None), None);
    }

    #[test]
    fn clamp_width_never_upscales() {
        assert_eq!(clamp_width(320, 240, Some(5000), None), None);
        assert_eq!(clamp_width(320, 240, None, Some(240)), None);
        assert_eq!(clamp_width(320, 240, Some(320), Some(9999)), None);
    }

    #[test]
    fn clamp_width_by_width() {
        assert_eq!(clamp_width(320, 240, Some(120), None), Some(120));
    }

    #[test]
    fn clamp_width_by_height() {
        // 240 -> 60 is a factor of 0.25: width 320 -> 80.
        assert_eq!(clamp_width(320, 240, None, Some(60)), Some(80));
    }

    #[test]
    fn clamp_width_takes_more_restrictive() {
        // height clamp (0.25) beats width clamp (0.375)
        assert_eq!(clamp_width(320, 240, Some(120), Some(60)), Some(80));
        // width clamp beats height clamp when it is tighter
        assert_eq!(clamp_width(320, 240, Some(80), Some(120)), Some(80));
    }

    #[test]
    fn clamp_width_is_even_and_at_least_two() {
        // 1920 * (500/1080) = 888.9 -> floored to even 888
        assert_eq!(clamp_width(1920, 1080, None, Some(500)), Some(888));
        assert_eq!(clamp_width(100, 100, Some(1), None), Some(2));
    }

    #[test]
    fn paletteuse_per_quality() {
        assert_eq!(
            paletteuse_lavfi(Quality::Best),
            "[0:v][1:v]paletteuse=dither=sierra2_4a:diff_mode=rectangle"
        );
        assert_eq!(
            paletteuse_lavfi(Quality::Good),
            "[0:v][1:v]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle"
        );
        assert_eq!(
            paletteuse_lavfi(Quality::Fair),
            "[0:v][1:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle"
        );
    }
}
