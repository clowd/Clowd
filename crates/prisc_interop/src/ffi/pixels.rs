//! Screen pixels and the screen grab (README section 4.2), a thin layer over
//! `prisc_rust_core::desktop::grab`, the grab the capture overlay and the scrolling driver use
//! too. Coordinates are the `prisc_screens_*` space. On macOS both exports answer
//! `PRISC_E_PERMISSION` first when Screen Recording is missing (`prisc_platform::permissions`, the
//! same check every other reader makes): an unpermitted window-list image succeeds and shows the
//! desktop picture instead of the windows, so it would silently read the wrong pixels.

use prisc_rust_core::desktop::grab::{self, GrabError, Options};
use prisc_rust_core::geometry::ScreenPoint;

use crate::error::{Error, Result};
use crate::ffi::screens::to_rect;
use crate::ffi::status::{PRISC_OK, PRISC_S_NONE};
use crate::ffi::types::PriscRect;
use crate::{panic, util};

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

/// The colour of one screen pixel as `0x00RRGGBB` (the pixel under the point; on a Retina display
/// the top-left of the point's pixels). `out_rgb` is written only on `PRISC_OK`.
///
/// Status: `PRISC_OK`; `PRISC_S_NONE` when the point is on no screen; `PRISC_E_INVALID_ARG` for a
/// null `out_rgb`; `PRISC_E_PERMISSION` (macOS Screen Recording); `PRISC_E_OS`;
/// `PRISC_E_UNSUPPORTED` on a platform without a screen grab.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screen_read_pixel(x: i32, y: i32, out_rgb: *mut u32) -> i32 {
    panic::guard("prisc_screen_read_pixel", || read_pixel(x, y, out_rgb))
}

unsafe fn read_pixel(x: i32, y: i32, out_rgb: *mut u32) -> Result {
    let out_rgb = util::out(out_rgb, "out_rgb")?;
    require_screen_recording()?;
    match grab::pixel(ScreenPoint::new(x, y)).map_err(grab_error)? {
        Some(rgb) => {
            *out_rgb = rgb;
            Ok(PRISC_OK)
        }
        None => Ok(PRISC_S_NONE),
    }
}

/// Grabs `rect` of the screen (layered windows included, alpha forced to 255) into an owned image;
/// free it with `prisc_screen_image_free`. On macOS the image has the display's backing pixels (a
/// 16x16 point rect is 32x32 on a Retina display); `dip_scale` is always the image's pixels per
/// DIP: image pixels per point on macOS, the effective DPI / 96 of the rect's monitor on Windows.
/// `out_image` is written only on `PRISC_OK`.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `rect` or `out_image` or a rect without
/// area; `PRISC_E_PERMISSION` (macOS Screen Recording); `PRISC_E_CAPABILITY` (CoreGraphics handed
/// back a pixel format this does not convert); `PRISC_E_OS`; `PRISC_E_UNSUPPORTED` on a platform
/// without a screen grab.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screen_grab(rect: *const PriscRect, out_image: *mut PriscScreenImage) -> i32 {
    panic::guard("prisc_screen_grab", || grab_rect(rect, out_image))
}

unsafe fn grab_rect(rect: *const PriscRect, out_image: *mut PriscScreenImage) -> Result {
    let rect = to_rect(util::input(rect, "rect")?);
    let out_image = util::out(out_image, "out_image")?;
    if rect.size.width <= 0 || rect.size.height <= 0 {
        return Err(Error::invalid_arg(format!("rect is {}x{}", rect.size.width, rect.size.height)));
    }
    require_screen_recording()?;
    let image = grab::grab(
        rect,
        Options {
            layered_windows: true,
            opaque: true,
        },
    )
    .map_err(grab_error)?;

    // Image pixels per rect unit: the backing scale on macOS, 1 on Windows, where the rect is in
    // physical pixels already and a DIP is the monitor's effective DPI / 96.
    #[allow(unused_mut)]
    let mut dip_scale = f64::from(image.width) / f64::from(rect.size.width);
    #[cfg(windows)]
    {
        use prisc_rust_core::desktop::screens::{self, Fallback};
        if let Ok(Some(screen)) = screens::at_rect(rect, Fallback::Nearest) {
            dip_scale *= screen.scale;
        }
    }

    let (width, height, stride) = (image.width, image.height, image.stride() as u32);
    let pixels = Box::into_raw(image.bgra.into_boxed_slice());
    *out_image = PriscScreenImage {
        pixels: pixels.cast::<u8>(),
        len: pixels.len(),
        width,
        height,
        stride,
        _reserved: 0,
        dip_scale,
    };
    Ok(PRISC_OK)
}

/// Frees a grabbed image's pixels and zeroes the struct; a second free (or a zeroed struct) is a
/// no-op.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `image`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screen_image_free(image: *mut PriscScreenImage) -> i32 {
    panic::guard("prisc_screen_image_free", || free(image))
}

unsafe fn free(image: *mut PriscScreenImage) -> Result {
    let image = util::out(image, "image")?;
    if !image.pixels.is_null() {
        // SAFETY: (pixels, len) is the boxed slice prisc_screen_grab leaked, freed once: the
        // struct is zeroed right after.
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(image.pixels, image.len)));
    }
    *image = PriscScreenImage::empty();
    Ok(PRISC_OK)
}

impl PriscScreenImage {
    fn empty() -> Self {
        PriscScreenImage {
            pixels: std::ptr::null_mut(),
            len: 0,
            width: 0,
            height: 0,
            stride: 0,
            _reserved: 0,
            dip_scale: 0.0,
        }
    }
}

fn require_screen_recording() -> Result<()> {
    if prisc_platform::permissions::screen_recording() {
        Ok(())
    } else {
        Err(Error::permission("Screen Recording is not granted, so the screen cannot be read"))
    }
}

fn grab_error(e: GrabError) -> Error {
    match e {
        GrabError::Empty {
            ..
        }
        | GrabError::TooLarge {
            ..
        } => Error::invalid_arg(e.to_string()),
        GrabError::Os(os) => Error::os(os.code, os.message),
        GrabError::Format(_) => Error::capability(e.to_string()),
        GrabError::Unsupported => Error::unsupported(e.to_string()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus;
    use std::mem::{offset_of, size_of};
    use std::ptr;

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

    #[test]
    fn null_arguments_and_empty_rects_are_invalid_arg() {
        let invalid = PriscInteropStatus::InvalidArg as i32;
        let mut image = PriscScreenImage::empty();
        let empty = PriscRect {
            x: 0,
            y: 0,
            width: 0,
            height: 5,
        };
        assert_eq!(unsafe { prisc_screen_read_pixel(0, 0, ptr::null_mut()) }, invalid);
        assert_eq!(unsafe { prisc_screen_grab(ptr::null(), &mut image) }, invalid);
        assert_eq!(unsafe { prisc_screen_grab(&empty, ptr::null_mut()) }, invalid);
        assert_eq!(unsafe { prisc_screen_grab(&empty, &mut image) }, invalid);
        assert!(image.pixels.is_null());
        assert_eq!(unsafe { prisc_screen_image_free(ptr::null_mut()) }, invalid);
    }

    #[test]
    fn freeing_twice_is_a_no_op() {
        let pixels = Box::into_raw(vec![1u8; 16].into_boxed_slice());
        let mut image = PriscScreenImage {
            pixels: pixels.cast::<u8>(),
            len: 16,
            width: 2,
            height: 2,
            stride: 8,
            _reserved: 0,
            dip_scale: 1.0,
        };
        assert_eq!(unsafe { prisc_screen_image_free(&mut image) }, 0);
        assert!(image.pixels.is_null());
        assert_eq!((image.len, image.width, image.height, image.stride), (0, 0, 0, 0));
        assert_eq!(unsafe { prisc_screen_image_free(&mut image) }, 0);
    }

    #[test]
    fn grab_errors_map_to_their_statuses() {
        use prisc_rust_core::desktop::screens::OsError;
        let status = |e: GrabError| grab_error(e).status;
        assert_eq!(
            status(GrabError::Empty {
                width: 0,
                height: 0
            }),
            PriscInteropStatus::InvalidArg
        );
        assert_eq!(
            status(GrabError::TooLarge {
                width: 1,
                height: 1
            }),
            PriscInteropStatus::InvalidArg
        );
        assert_eq!(status(GrabError::Format("x".into())), PriscInteropStatus::Capability);
        assert_eq!(status(GrabError::Unsupported), PriscInteropStatus::Unsupported);
        let os = grab_error(GrabError::Os(OsError {
            code: -5,
            message: "GetDC failed".into(),
        }));
        assert_eq!((os.status, os.code), (PriscInteropStatus::Os, -5));
    }
}
