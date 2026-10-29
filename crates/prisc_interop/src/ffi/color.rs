//! Colour maths (README section 4.10): the accent palette, relative luminance, a sticky note's
//! ink, OKLCH in both directions and the colour picker's gamut charts, all over
//! `prisc_rust_core::color`, the one implementation the capture overlay uses too. All rgb values
//! are `0x00RRGGBB`; a set top byte is `PRISC_E_INVALID_ARG`, so an ARGB value passed by mistake is
//! refused rather than misread. The note ink's paper is the one ARGB parameter, and says so.

use crate::error::{Error, Result};
use crate::ffi::status::PRISC_OK;
use crate::{panic, util};
use prisc_rust_core::color::{self, chart, palette::AccentPalette, OkLch};

/// An OKLCH colour (24 bytes): lightness 0 to 1, chroma, hue in degrees. A value outside sRGB is
/// valid input; the conversions say whether it is displayable.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct PriscOkLch {
    pub l: f64,
    pub c: f64,
    pub h_deg: f64,
}

/// Everything one accent colour drives (136 bytes, `0x00RRGGBB` throughout): the effective accent
/// (the pick, darkened for white text when the contrast rule is on), the hint comet's body and
/// head (from the pick itself), and the light, dark and dark-text ramps of ten steps each with the
/// base at step 5.
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

/// One of the three OKLCH channels: the one a gamut chart holds fixed (`prisc_oklch_paint_slice`),
/// or the one a slider track sweeps (`prisc_oklch_paint_track`).
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscOkLchChannel {
    Lightness = 0,
    Chroma = 1,
    Hue = 2,
}

crate::convert::ffi_enum!(PriscOkLchChannel: i32 { Lightness, Chroma, Hue });

impl From<PriscOkLchChannel> for chart::Channel {
    fn from(channel: PriscOkLchChannel) -> Self {
        match channel {
            PriscOkLchChannel::Lightness => chart::Channel::Lightness,
            PriscOkLchChannel::Chroma => chart::Channel::Chroma,
            PriscOkLchChannel::Hue => chart::Channel::Hue,
        }
    }
}

/// The effective accent, the comet colours and the three ramps for the accent `accent_rgb` as the
/// user picked it, with the contrast rule on (`maintain_contrast` non-zero) or off. One call per
/// accent change; `out` is written only on `PRISC_OK`.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `out` or an `accent_rgb` past `0xFFFFFF`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_color_accent_palette(accent_rgb: u32, maintain_contrast: u8, out: *mut PriscAccentPalette) -> i32 {
    panic::guard("prisc_color_accent_palette", || {
        let out = util::out(out, "out")?;
        let p = AccentPalette::new(rgb(accent_rgb, "accent_rgb")?, util::flag(maintain_contrast));
        *out = PriscAccentPalette {
            effective: color::to_u32(p.effective),
            comet_body: color::to_u32(p.comet_body),
            comet_head: color::to_u32(p.comet_head),
            _reserved: 0,
            light: p.light.map(color::to_u32),
            dark: p.dark.map(color::to_u32),
            dark_text: p.dark_text.map(color::to_u32),
        };
        Ok(PRISC_OK)
    })
}

/// The WCAG relative luminance of an sRGB colour (0 = black, 1 = white), for the draw-time callers
/// that pick ink or an edge by it.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `out` or an `rgb` past `0xFFFFFF`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_color_relative_luminance(rgb: u32, out: *mut f64) -> i32 {
    panic::guard("prisc_color_relative_luminance", || {
        let out = util::out(out, "out")?;
        *out = color::relative_luminance(self::rgb(rgb, "rgb")?);
        Ok(PRISC_OK)
    })
}

/// The ink a sticky note's text is drawn in on `paper_argb` (`0xAARRGGBB`, straight alpha; a
/// translucent paper is judged as it shows over white): a deep shade of the paper's hue that reaches
/// the note ink contrast against it, a pale tint on a dark paper, black or white when no shade
/// does. One call per paper; `out_rgb` (`0x00RRGGBB`) is written only on `PRISC_OK`. Unlike the
/// other colour exports this one takes the alpha byte.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `out_rgb`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_color_note_ink(paper_argb: u32, out_rgb: *mut u32) -> i32 {
    panic::guard("prisc_color_note_ink", || {
        let out_rgb = util::out(out_rgb, "out_rgb")?;
        *out_rgb = color::to_u32(color::ink::note_ink(paper_argb));
        Ok(PRISC_OK)
    })
}

/// Converts an sRGB colour to OKLCH. A grey (no chroma 8-bit sRGB can tell) comes back with hue 0.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `out` or an `rgb` past `0xFFFFFF`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_oklch_from_srgb(rgb: u32, out: *mut PriscOkLch) -> i32 {
    panic::guard("prisc_oklch_from_srgb", || {
        let out = util::out(out, "out")?;
        let v = OkLch::from_srgb(self::rgb(rgb, "rgb")?);
        *out = PriscOkLch {
            l: v.l,
            c: v.c,
            h_deg: v.h_deg,
        };
        Ok(PRISC_OK)
    })
}

/// Converts OKLCH to the nearest sRGB colour: a lightness past either end is white or black, and
/// out of gamut the chroma is lowered (never the lightness or the hue) until it fits.
/// `out_in_gamut` (optional) is 1 when `value` is inside sRGB as it stands, before either
/// adjustment.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `value` or `out_rgb`, or a component that
/// is not finite.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_oklch_to_srgb(value: *const PriscOkLch, out_rgb: *mut u32, out_in_gamut: *mut u8) -> i32 {
    panic::guard("prisc_oklch_to_srgb", || {
        let value = oklch(value)?;
        let out_rgb = util::out(out_rgb, "out_rgb")?;
        let (rgb, in_gamut) = value.to_srgb();
        *out_rgb = color::to_u32(rgb);
        util::write_optional(out_in_gamut, util::to_flag(in_gamut));
        Ok(PRISC_OK)
    })
}

/// Paints the colour picker's gamut chart through `value` that holds `fixed_channel` (a
/// `PriscOkLchChannel`) fixed into `pixels`: `width * height` premultiplied BGRA pixels
/// (`0xAARRGGBB` words), top row first, transparent where sRGB cannot show the colour and with an
/// anti-aliased edge. Lightness held: hue across, chroma up; chroma held: hue across, lightness up;
/// hue held: lightness across, chroma up.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for an unknown channel, a null `value`, a component
/// that is not finite, a zero size, or a `pixels` buffer whose `len` is not `width * height`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_oklch_paint_slice(
    fixed_channel: i32,
    value: *const PriscOkLch,
    width: u32,
    height: u32,
    pixels: *mut u32,
    len: usize,
) -> i32 {
    panic::guard("prisc_oklch_paint_slice", || {
        let channel = PriscOkLchChannel::from_raw(fixed_channel)?;
        let value = oklch(value)?;
        if width == 0 || height == 0 {
            return Err(Error::invalid_arg(format!("a {width}x{height} chart has no pixels")));
        }
        let (width, height) = (width as usize, height as usize);
        if width.checked_mul(height) != Some(len) {
            return Err(Error::invalid_arg(format!(
                "a {width}x{height} chart needs that many pixels, not {len}"
            )));
        }
        let pixels = util::slice_mut(pixels, len, "pixels")?;
        chart::paint_slice(channel.into(), value, width, height, pixels);
        Ok(PRISC_OK)
    })
}

/// Paints a slider track into `pixels`: `channel` (a `PriscOkLchChannel`) swept across its range
/// left to right over `len` samples (lightness 0 to 1, chroma 0 to `OklchChromaMax`, hue 0 to 360)
/// at `value`'s other two channels, an opaque BGRA pixel where sRGB has the colour and a
/// transparent one where it does not.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for an unknown channel, a null `value`, a component
/// that is not finite, or a null `pixels` with a length.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_oklch_paint_track(channel: i32, value: *const PriscOkLch, pixels: *mut u32, len: usize) -> i32 {
    panic::guard("prisc_oklch_paint_track", || {
        let channel = PriscOkLchChannel::from_raw(channel)?;
        let value = oklch(value)?;
        let pixels = util::slice_mut(pixels, len, "pixels")?;
        chart::paint_track(channel.into(), value, pixels);
        Ok(PRISC_OK)
    })
}

/// An `0x00RRGGBB` parameter as channels.
fn rgb(value: u32, name: &str) -> Result<color::Rgb8> {
    if value > 0x00FF_FFFF {
        return Err(Error::invalid_arg(format!("{name} 0x{value:08X} is not 0x00RRGGBB")));
    }
    Ok(color::from_u32(value))
}

/// A caller's OKLCH value, every component finite.
unsafe fn oklch(value: *const PriscOkLch) -> Result<OkLch> {
    let v = util::input(value, "value")?;
    if !(v.l.is_finite() && v.c.is_finite() && v.h_deg.is_finite()) {
        return Err(Error::invalid_arg(format!("value {v:?} is not finite")));
    }
    Ok(OkLch {
        l: v.l,
        c: v.c,
        h_deg: v.h_deg,
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

    #[test]
    fn rgb_parameters_refuse_a_top_byte() {
        assert_eq!(rgb(0x3B97D2, "rgb").unwrap(), [0x3B, 0x97, 0xD2]);
        let e = rgb(0xFF3B97D2, "accent_rgb").unwrap_err();
        assert_eq!(e.message, "accent_rgb 0xFF3B97D2 is not 0x00RRGGBB");
    }
}
