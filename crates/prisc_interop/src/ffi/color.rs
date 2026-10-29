//! Colour maths: the accent palette, relative luminance and OKLCH (WP-PURE; README section 4.10).
//! All rgb values are `0x00RRGGBB`.

use crate::error::Error;
use crate::panic;

/// An OKLCH colour (24 bytes); hue in degrees.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct PriscOkLch {
    pub l: f64,
    pub c: f64,
    pub h_deg: f64,
}

/// Everything one accent colour drives (136 bytes).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PriscAccentPalette {
    pub effective: u32,
    pub comet_body: u32,
    pub comet_head: u32,
    pub _reserved: u32,
    pub light: [u32; 10],
    pub dark: [u32; 10],
    pub dark_text: [u32; 10],
}

/// The plane `prisc_oklch_paint_slice` paints.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscOkLchPlane {
    LightnessChroma = 0,
    LightnessHue = 1,
    ChromaHue = 2,
}

/// The channel `prisc_oklch_paint_track` paints.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscOkLchChannel {
    Lightness = 0,
    Chroma = 1,
    Hue = 2,
}

/// The effective accent, the comet colours and the three ramps for one accent colour.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_color_accent_palette(accent_rgb: u32, maintain_contrast: u8, out: *mut PriscAccentPalette) -> i32 {
    panic::guard("prisc_color_accent_palette", || {
        let _ = (accent_rgb, maintain_contrast, out);
        Err(Error::unsupported("prisc_color_accent_palette is not implemented yet"))
    })
}

/// The WCAG relative luminance of an sRGB colour.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_color_relative_luminance(rgb: u32, out: *mut f64) -> i32 {
    panic::guard("prisc_color_relative_luminance", || {
        let _ = (rgb, out);
        Err(Error::unsupported("prisc_color_relative_luminance is not implemented yet"))
    })
}

/// Converts an sRGB colour to OKLCH.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_oklch_from_srgb(rgb: u32, out: *mut PriscOkLch) -> i32 {
    panic::guard("prisc_oklch_from_srgb", || {
        let _ = (rgb, out);
        Err(Error::unsupported("prisc_oklch_from_srgb is not implemented yet"))
    })
}

/// Converts OKLCH to sRGB (gamut-clipped) and reports whether it was in gamut.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_oklch_to_srgb(value: *const PriscOkLch, out_rgb: *mut u32, out_in_gamut: *mut u8) -> i32 {
    panic::guard("prisc_oklch_to_srgb", || {
        let _ = (value, out_rgb, out_in_gamut);
        Err(Error::unsupported("prisc_oklch_to_srgb is not implemented yet"))
    })
}

/// Paints a `PriscOkLchPlane` slice through `value` into `pixels` (BGRA premultiplied).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_oklch_paint_slice(
    plane: i32,
    value: *const PriscOkLch,
    width: u32,
    height: u32,
    pixels: *mut u32,
    len: usize,
) -> i32 {
    panic::guard("prisc_oklch_paint_slice", || {
        let _ = (plane, value, width, height, pixels, len);
        Err(Error::unsupported("prisc_oklch_paint_slice is not implemented yet"))
    })
}

/// Paints a `PriscOkLchChannel` slider track through `value` into `pixels`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_oklch_paint_track(channel: i32, value: *const PriscOkLch, pixels: *mut u32, len: usize) -> i32 {
    panic::guard("prisc_oklch_paint_track", || {
        let _ = (channel, value, pixels, len);
        Err(Error::unsupported("prisc_oklch_paint_track is not implemented yet"))
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    #[test]
    fn layouts_are_pinned() {
        assert_eq!(size_of::<PriscOkLch>(), 24);
        assert_eq!(offset_of!(PriscOkLch, l), 0);
        assert_eq!(offset_of!(PriscOkLch, c), 8);
        assert_eq!(offset_of!(PriscOkLch, h_deg), 16);
        assert_eq!(size_of::<PriscAccentPalette>(), 136);
        assert_eq!(offset_of!(PriscAccentPalette, effective), 0);
        assert_eq!(offset_of!(PriscAccentPalette, comet_body), 4);
        assert_eq!(offset_of!(PriscAccentPalette, comet_head), 8);
        assert_eq!(offset_of!(PriscAccentPalette, _reserved), 12);
        assert_eq!(offset_of!(PriscAccentPalette, light), 16);
        assert_eq!(offset_of!(PriscAccentPalette, dark), 56);
        assert_eq!(offset_of!(PriscAccentPalette, dark_text), 96);
    }
}
