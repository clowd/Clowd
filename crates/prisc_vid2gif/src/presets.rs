//! Quality presets, output sizing, and the filter-graph strings for the two
//! conversion passes. The graph strings use the same syntax as the ffmpeg
//! CLI — `avfilter_graph_parse_ptr` accepts them verbatim.

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

/// Resolves the `--max-width`/`--max-height` clamps against the source size:
/// aspect is preserved, the more restrictive clamp wins, and the result is
/// `None` when no downscale is needed (never upscales — same contract as
/// obs-express's recording resolution cap). The width is floored to even,
/// which keeps output dimensions stable and predictable.
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

/// The shared fps/scale front of both passes.
fn chain(fps: u32, scale_width: Option<u32>) -> String {
    match scale_width {
        Some(w) => format!("fps={fps},scale={w}:-1:flags=lanczos"),
        None => format!("fps={fps}"),
    }
}

/// Pass 1 (single input → single output, default `in`/`out` labels): resample
/// and build one global 256-color palette. `stats_mode=diff` weights pixels
/// that change between frames, which favors the moving content of a screen
/// recording over its static background.
pub fn pass1_graph(fps: u32, scale_width: Option<u32>) -> String {
    format!("{},palettegen=stats_mode=diff", chain(fps, scale_width))
}

/// Pass 2 (video + palette inputs, labeled `vid`/`pal` → `out`): resample and
/// map frames onto the palette. `diff_mode=rectangle` re-dithers only the
/// changed region of each frame, which both shrinks the file and avoids
/// shimmering in static areas.
pub fn pass2_graph(fps: u32, scale_width: Option<u32>, quality: Quality) -> String {
    format!(
        "[vid]{}[x];[x][pal]paletteuse=dither={}:diff_mode=rectangle[out]",
        chain(fps, scale_width),
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
    fn pass1_graph_without_scale() {
        assert_eq!(pass1_graph(15, None), "fps=15,palettegen=stats_mode=diff");
    }

    #[test]
    fn pass1_graph_with_scale() {
        assert_eq!(
            pass1_graph(20, Some(480)),
            "fps=20,scale=480:-1:flags=lanczos,palettegen=stats_mode=diff"
        );
    }

    #[test]
    fn pass2_graph_per_quality() {
        assert_eq!(
            pass2_graph(15, None, Quality::Good),
            "[vid]fps=15[x];[x][pal]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle[out]"
        );
        assert_eq!(
            pass2_graph(20, Some(480), Quality::Best),
            "[vid]fps=20,scale=480:-1:flags=lanczos[x];[x][pal]paletteuse=dither=sierra2_4a:diff_mode=rectangle[out]"
        );
        assert!(pass2_graph(10, None, Quality::Fair).contains("bayer_scale=5"));
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
}
