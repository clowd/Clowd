//! The H.264 quality mapping (WP-PURE; README section 4.10).

use crate::error::Error;
use crate::panic;

/// One crf mapped onto every encoder (16 bytes).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PriscH264Quality {
    pub crf: i32,
    pub nvenc_cq: i32,
    pub vt_quality: i32,
    pub vt_bitrate_kbps: i32,
}

/// Maps `crf` onto NVENC CQ and VideoToolbox quality and bitrate for a frame size and rational
/// frame rate.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_encoding_h264_quality(
    crf: i32,
    width: i32,
    height: i32,
    fps_num: i32,
    fps_den: i32,
    out: *mut PriscH264Quality,
) -> i32 {
    panic::guard("prisc_encoding_h264_quality", || {
        let _ = (crf, width, height, fps_num, fps_den, out);
        Err(Error::unsupported("prisc_encoding_h264_quality is not implemented yet"))
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    #[test]
    fn layouts_are_pinned() {
        assert_eq!(size_of::<PriscH264Quality>(), 16);
        assert_eq!(offset_of!(PriscH264Quality, crf), 0);
        assert_eq!(offset_of!(PriscH264Quality, nvenc_cq), 4);
        assert_eq!(offset_of!(PriscH264Quality, vt_quality), 8);
        assert_eq!(offset_of!(PriscH264Quality, vt_bitrate_kbps), 12);
    }
}
