//! Screen pixels and the screen grab (WP-DESKTOP; README section 4.2).

use crate::error::Error;
use crate::ffi::types::PriscRect;
use crate::panic;

/// A grabbed screen image (40 bytes). The pixels are mode 2: owned by this struct and freed by
/// `prisc_screen_image_free`.
#[repr(C)]
#[derive(Debug)]
pub struct PriscScreenImage {
    /// BGRA8, top-down, alpha forced to 255.
    pub pixels: *mut u8,
    pub len: usize,
    pub width: u32,
    pub height: u32,
    pub stride: u32,
    pub _reserved: u32,
    /// Image pixels per DIP for this rect.
    pub dip_scale: f64,
}

/// The colour of one screen pixel as `0x00RRGGBB`. On macOS it answers `PRISC_E_PERMISSION` first
/// when Screen Recording is missing (an unpermitted read silently returns the desktop picture).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_PERMISSION`, `PRISC_E_OS`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screen_read_pixel(x: i32, y: i32, out_rgb: *mut u32) -> i32 {
    panic::guard("prisc_screen_read_pixel", || {
        let _ = (x, y, out_rgb);
        Err(Error::unsupported("prisc_screen_read_pixel is not implemented yet"))
    })
}

/// Grabs `rect` of the screen into an owned image; free it with `prisc_screen_image_free`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_PERMISSION`, `PRISC_E_CAPABILITY` (an unconvertible CoreGraphics
/// format), `PRISC_E_OS`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screen_grab(rect: *const PriscRect, out_image: *mut PriscScreenImage) -> i32 {
    panic::guard("prisc_screen_grab", || {
        let _ = (rect, out_image);
        Err(Error::unsupported("prisc_screen_grab is not implemented yet"))
    })
}

/// Frees a grabbed image and zeroes the struct; a second free is a no-op.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG` for a null `image`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screen_image_free(image: *mut PriscScreenImage) -> i32 {
    panic::guard("prisc_screen_image_free", || {
        let _ = image;
        Err(Error::unsupported("prisc_screen_image_free is not implemented yet"))
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    #[test]
    fn layouts_are_pinned() {
        assert_eq!(size_of::<PriscScreenImage>(), 40);
        assert_eq!(offset_of!(PriscScreenImage, pixels), 0);
        assert_eq!(offset_of!(PriscScreenImage, len), 8);
        assert_eq!(offset_of!(PriscScreenImage, width), 16);
        assert_eq!(offset_of!(PriscScreenImage, height), 20);
        assert_eq!(offset_of!(PriscScreenImage, stride), 24);
        assert_eq!(offset_of!(PriscScreenImage, _reserved), 28);
        assert_eq!(offset_of!(PriscScreenImage, dip_scale), 32);
    }
}
