//! The H.264 quality mapping the recorder and the video renderer (Prisc.VideoSDK, through
//! prisc_interop) apply identically. `crf` (0 best to 51 worst, x264's scale) is the single
//! quality knob either caller sees; each encoder maps it onto its own scale so a given crf lands
//! on comparable bytes and quality whichever encoder ends up selected:
//!
//! - x264 and AMF take crf as it is (AMF as a constant qp).
//! - NVENC: constant-quality VBR at [`nvenc_cq`].
//! - VideoToolbox: its inverted 0 to 100 quality slider on Apple Silicon ([`video_toolbox_quality`]);
//!   Intel Macs have no quality mode and encode at [`video_toolbox_bitrate_kbps`] instead.
//!
//! Presets, GOP length, B-frames and encoder priority are each caller's own choice and stay there.

pub use crate::contract::encoding::{
    MAX_CRF, NVENC_CQ_OFFSET, VIDEO_TOOLBOX_BITRATE_MAX_KBPS, VIDEO_TOOLBOX_BITRATE_MIN_KBPS, VIDEO_TOOLBOX_QUALITY_MAX,
    VIDEO_TOOLBOX_QUALITY_MIN,
};

/// The frame size the VideoToolbox bitrate budget is set at (3440x1440).
const REFERENCE_PIXELS: f64 = 3440.0 * 1440.0;

/// The VideoToolbox bitrate at [`REFERENCE_PIXELS`] and 30 fps, in kbps.
const REFERENCE_KBPS: f64 = 4000.0;

/// One crf mapped onto every encoder's scale.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct H264Quality {
    /// x264's crf, and AMF's qp.
    pub crf: i32,
    pub nvenc_cq: i32,
    pub video_toolbox_quality: i32,
    pub video_toolbox_bitrate_kbps: i32,
}

/// Why [`h264_quality`] refused its input.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct InvalidQuality(pub String);

impl std::fmt::Display for InvalidQuality {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for InvalidQuality {}

/// `crf` mapped onto every encoder for a `width` x `height` output at `fps_num` / `fps_den` frames
/// per second (the size and rate only matter to the VideoToolbox bitrate). Refuses a crf outside
/// 0 to [`MAX_CRF`] and a size or rate component that is not positive.
pub fn h264_quality(crf: i32, width: i32, height: i32, fps_num: i32, fps_den: i32) -> Result<H264Quality, InvalidQuality> {
    if !(0..=MAX_CRF).contains(&crf) {
        return Err(InvalidQuality(format!("crf {crf} is outside 0-{MAX_CRF}")));
    }
    if width <= 0 || height <= 0 {
        return Err(InvalidQuality(format!("output size {width}x{height} is not positive")));
    }
    if fps_num <= 0 || fps_den <= 0 {
        return Err(InvalidQuality(format!(
            "frame rate {fps_num}/{fps_den} components must be positive"
        )));
    }
    Ok(H264Quality {
        crf,
        nvenc_cq: nvenc_cq(crf),
        video_toolbox_quality: video_toolbox_quality(crf),
        video_toolbox_bitrate_kbps: video_toolbox_bitrate_kbps(width as u32, height as u32, fps_num as u32, fps_den as u32),
    })
}

/// NVENC's constant-quality target for an x264-style crf: `crf + 4`, clamped to 1 to 51 (0 would
/// mean "automatic" to NVENC, not lossless).
pub fn nvenc_cq(crf: i32) -> i32 {
    crf.saturating_add(NVENC_CQ_OFFSET)
        .clamp(1, MAX_CRF)
}

/// VideoToolbox's quality slider (0 worst to 100 best) for an x264-style crf: `(51 - crf) * 100 /
/// 51` rounded half away from zero, then clamped to the useful band. The linear slope was measured
/// on an M2 Pro to track x264 `veryfast` at the same crf to within about 0.6 VMAF across crf 16 to
/// 24; the clamp is because the slider has no sane top end. A crf past 51 lands on the floor.
pub fn video_toolbox_quality(crf: i32) -> i32 {
    let q = (MAX_CRF.saturating_sub(crf).max(0) as f64 * 100.0 / MAX_CRF as f64).round() as i32;
    q.clamp(VIDEO_TOOLBOX_QUALITY_MIN, VIDEO_TOOLBOX_QUALITY_MAX)
}

/// The Intel-Mac VideoToolbox average bitrate in kbps: a 4 Mbps budget at 3440x1440 and 30 fps,
/// scaled by pixel rate and clamped. A size compromise by design (6 Mbps is still about 1.35 GB
/// per 30 minutes), not an equivalent of the crf tiers. A zero `fps_den` reads as 30 fps; a rate
/// below 1 fps counts as 1.
pub fn video_toolbox_bitrate_kbps(width: u32, height: u32, fps_num: u32, fps_den: u32) -> i32 {
    let fps = if fps_den > 0 { fps_num as f64 / fps_den as f64 } else { 30.0 };
    let pixel_rate = (width as f64 * height as f64 / REFERENCE_PIXELS) * (fps.max(1.0) / 30.0);
    (REFERENCE_KBPS * pixel_rate)
        .round()
        .clamp(VIDEO_TOOLBOX_BITRATE_MIN_KBPS as f64, VIDEO_TOOLBOX_BITRATE_MAX_KBPS as f64) as i32
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn nvenc_is_crf_plus_four_on_its_own_scale() {
        for (crf, cq) in [(21, 25), (0, 4), (47, 51), (51, 51), (60, 51)] {
            assert_eq!(nvenc_cq(crf), cq, "crf {crf}");
        }
    }

    #[test]
    fn video_toolbox_quality_inverts_crf_onto_the_clamped_slider() {
        for (crf, q) in [
            (0, 70),
            (15, 70),
            (16, 69),
            (18, 65),
            (23, 55),
            (24, 53),
            (29, 43),
            (44, 15),
            (51, 15),
            (60, 15),
        ] {
            assert_eq!(video_toolbox_quality(crf), q, "crf {crf}");
        }
        // the presets stay distinguishable
        assert!(video_toolbox_quality(16) > video_toolbox_quality(23));
        assert!(video_toolbox_quality(23) > video_toolbox_quality(29));
    }

    #[test]
    fn video_toolbox_bitrate_scales_by_pixel_rate_within_bounds() {
        for (w, h, num, den, kbps) in [
            (3440, 1440, 30, 1, 4000),
            (1920, 1080, 30, 1, 1674),
            (1920, 1080, 60, 1, 3349),       // 0.4186 * 2 * 4000 = 3348.8
            (1280, 720, 30, 1, 1500),        // floor
            (640, 480, 30, 1, 1500),         // floor
            (3840, 2160, 60, 1, 6000),       // ceiling
            (1920, 1080, 30000, 1001, 1673), // 0.4186 * (29.97 / 30) * 4000 = 1672.7
            (1920, 1080, 0, 1, 1500),        // below 1 fps counts as 1
            (3440, 1440, 30, 0, 4000),       // no denominator reads as 30 fps
        ] {
            assert_eq!(video_toolbox_bitrate_kbps(w, h, num, den), kbps, "{w}x{h}@{num}/{den}");
        }
    }

    #[test]
    fn one_call_maps_every_encoder() {
        assert_eq!(
            h264_quality(23, 1920, 1080, 30000, 1001),
            Ok(H264Quality {
                crf: 23,
                nvenc_cq: 27,
                video_toolbox_quality: 55,
                video_toolbox_bitrate_kbps: 1673
            })
        );
        assert_eq!(
            h264_quality(0, 1, 1, 1, 100)
                .unwrap()
                .nvenc_cq,
            4
        );
    }

    #[test]
    fn bad_input_is_refused() {
        assert!(h264_quality(52, 1920, 1080, 30, 1).is_err());
        assert!(h264_quality(-1, 1920, 1080, 30, 1).is_err());
        assert!(h264_quality(21, 0, 1080, 30, 1).is_err());
        assert!(h264_quality(21, 1920, -1, 30, 1).is_err());
        assert!(h264_quality(21, 1920, 1080, 0, 1).is_err());
        let e = h264_quality(21, 1920, 1080, 30, 0).unwrap_err();
        assert_eq!(e.to_string(), "frame rate 30/0 components must be positive");
    }
}
