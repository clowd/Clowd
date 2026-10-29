//! The pure exports through the library's public surface (README section 4.10): the accent
//! palette, luminance, OKLCH and the picker's charts, the share-region rules and the H.264
//! mapping. One success, the documented failures and a round trip per family; the behaviour
//! vectors themselves live next to the single implementation in `prisc_rust_core`.

use std::ptr;

use prisc_interop::ffi::color::{
    prisc_color_accent_palette, prisc_color_note_ink, prisc_color_relative_luminance, prisc_oklch_from_srgb, prisc_oklch_paint_slice,
    prisc_oklch_paint_track, prisc_oklch_to_srgb, PriscAccentPalette, PriscOkLch, PriscOkLchChannel as Channel,
};
use prisc_interop::ffi::encoding::{prisc_encoding_h264_quality, PriscH264Quality};
use prisc_interop::ffi::error::prisc_last_error_message;
use prisc_interop::ffi::share_region::{prisc_share_region_apply_drag, prisc_share_region_normalize, PriscShareRegionHandle as H};
use prisc_interop::ffi::status::PriscInteropStatus as S;
use prisc_interop::ffi::types::PriscRect;

const OK: i32 = S::Ok as i32;
const INVALID: i32 = S::InvalidArg as i32;

fn last_error() -> String {
    let len = unsafe { prisc_last_error_message(ptr::null_mut(), 0) };
    let mut buf = vec![0u8; len];
    assert_eq!(unsafe { prisc_last_error_message(buf.as_mut_ptr(), buf.len()) }, len);
    String::from_utf8(buf).unwrap()
}

fn palette(accent: u32, maintain: u8) -> PriscAccentPalette {
    let mut p = PriscAccentPalette::default();
    assert_eq!(unsafe { prisc_color_accent_palette(accent, maintain, &mut p) }, OK);
    p
}

#[test]
fn the_palette_of_prisc_blue_is_the_shipped_default() {
    let p = palette(0x3B97D2, 1);
    assert_eq!(p.effective, 0x2F7CAE);
    assert_eq!((p.comet_body, p.comet_head), (0x3B97D2, 0x83B4D8));
    assert_eq!(p.light[5], 0x2F7CAE);
    assert_eq!(p.dark[5], 0x2F7CAE);
    assert_eq!(p.dark_text[..5], p.dark[..5]);
    assert_eq!(p._reserved, 0);

    // the rule off hands the pick back; the comet never depends on it
    let raw = palette(0x3B97D2, 0);
    assert_eq!(raw.effective, 0x3B97D2);
    assert_eq!((raw.comet_body, raw.comet_head), (p.comet_body, p.comet_head));
    // any non-zero flag is true
    assert_eq!(palette(0x3B97D2, 0xFF), p);
}

#[test]
fn colour_exports_refuse_bad_arguments() {
    let mut p = PriscAccentPalette::default();
    assert_eq!(unsafe { prisc_color_accent_palette(0x3B97D2, 1, ptr::null_mut()) }, INVALID);
    assert_eq!(last_error(), "out was null");
    assert_eq!(unsafe { prisc_color_accent_palette(0xFF3B97D2, 1, &mut p) }, INVALID);
    assert_eq!(last_error(), "accent_rgb 0xFF3B97D2 is not 0x00RRGGBB");
    assert_eq!(p, PriscAccentPalette::default(), "out is written only on success");

    let mut l = 0.0;
    assert_eq!(unsafe { prisc_color_relative_luminance(0x1000000, &mut l) }, INVALID);
    assert_eq!(unsafe { prisc_color_relative_luminance(0xFFFFFF, ptr::null_mut()) }, INVALID);
    let mut v = PriscOkLch::default();
    assert_eq!(unsafe { prisc_oklch_from_srgb(0x1000000, &mut v) }, INVALID);
    let mut rgb = 0u32;
    assert_eq!(unsafe { prisc_oklch_to_srgb(ptr::null(), &mut rgb, ptr::null_mut()) }, INVALID);
    let nan = PriscOkLch {
        l: f64::NAN,
        c: 0.1,
        h_deg: 0.0,
    };
    assert_eq!(unsafe { prisc_oklch_to_srgb(&nan, &mut rgb, ptr::null_mut()) }, INVALID);
}

#[test]
fn note_ink_is_one_call_per_paper() {
    // butter, and the same paper at half alpha (judged over white); the vectors live in prisc_rust_core
    let mut ink = 0u32;
    assert_eq!(unsafe { prisc_color_note_ink(0xFFFFF1B8, &mut ink) }, OK);
    assert_eq!(ink, 0x594A0D);
    assert_eq!(unsafe { prisc_color_note_ink(0x80FFF1B8, &mut ink) }, OK);
    assert_eq!(ink, 0x62520E);
    assert_eq!(unsafe { prisc_color_note_ink(0xFFFFF1B8, ptr::null_mut()) }, INVALID);
    assert_eq!(last_error(), "out_rgb was null");
}

#[test]
fn luminance_spans_black_to_white() {
    let mut l = -1.0;
    assert_eq!(unsafe { prisc_color_relative_luminance(0x000000, &mut l) }, OK);
    assert_eq!(l, 0.0);
    assert_eq!(unsafe { prisc_color_relative_luminance(0xFFFFFF, &mut l) }, OK);
    assert!((l - 1.0).abs() < 1e-12);
}

#[test]
fn oklch_round_trips_and_reports_the_gamut() {
    let mut v = PriscOkLch::default();
    assert_eq!(unsafe { prisc_oklch_from_srgb(0xFF0000, &mut v) }, OK);
    assert!(
        (v.l - 0.628).abs() < 5e-4 && (v.c - 0.2577).abs() < 5e-4 && (v.h_deg - 29.23).abs() < 0.05,
        "{v:?}"
    );

    let (mut rgb, mut in_gamut) = (0u32, 9u8);
    assert_eq!(unsafe { prisc_oklch_to_srgb(&v, &mut rgb, &mut in_gamut) }, OK);
    assert_eq!((rgb, in_gamut), (0xFF0000, 1));

    // outside sRGB: chroma lowered to fit, and said so; the gamut flag is optional
    let vivid = PriscOkLch {
        l: 0.7,
        c: 0.35,
        h_deg: 150.0,
    };
    assert_eq!(unsafe { prisc_oklch_to_srgb(&vivid, &mut rgb, &mut in_gamut) }, OK);
    assert_eq!(in_gamut, 0);
    assert_eq!(unsafe { prisc_oklch_to_srgb(&vivid, &mut rgb, ptr::null_mut()) }, OK);

    // a grey has no hue
    assert_eq!(unsafe { prisc_oklch_from_srgb(0x808080, &mut v) }, OK);
    assert_eq!(v.h_deg, 0.0);
}

#[test]
fn a_chart_paints_the_gamut_and_leaves_the_rest_transparent() {
    let value = PriscOkLch {
        l: 0.7,
        c: 0.15,
        h_deg: 250.0,
    };
    for channel in [Channel::Lightness, Channel::Chroma, Channel::Hue] {
        let mut pixels = vec![0xDEADBEEFu32; 32 * 16];
        let status = unsafe { prisc_oklch_paint_slice(channel as i32, &value, 32, 16, pixels.as_mut_ptr(), pixels.len()) };
        assert_eq!(status, OK, "{channel:?}");
        assert!(pixels.iter().all(|p| *p != 0xDEADBEEF), "{channel:?}: a pixel was not painted");
        assert!(pixels.iter().any(|p| p >> 24 == 0xFF), "{channel:?}: nothing opaque");
        assert!(pixels.contains(&0), "{channel:?}: nothing outside the gamut");
    }
}

#[test]
fn a_chart_refuses_a_mismatched_buffer_and_an_unknown_channel() {
    let value = PriscOkLch {
        l: 0.7,
        c: 0.15,
        h_deg: 250.0,
    };
    let mut pixels = vec![0u32; 16];
    let paint = |channel: i32, w: u32, h: u32, len: usize, pixels: &mut [u32]| unsafe {
        prisc_oklch_paint_slice(channel, &value, w, h, pixels.as_mut_ptr(), len)
    };
    assert_eq!(paint(0, 4, 4, 16, &mut pixels), OK);
    assert_eq!(paint(0, 4, 4, 15, &mut pixels), INVALID);
    assert_eq!(last_error(), "a 4x4 chart needs that many pixels, not 15");
    assert_eq!(paint(0, 0, 4, 0, &mut pixels), INVALID);
    assert_eq!(paint(3, 4, 4, 16, &mut pixels), INVALID);
    assert_eq!(last_error(), "3 is not a PriscOkLchChannel value");
    assert_eq!(unsafe { prisc_oklch_paint_slice(0, &value, 4, 4, ptr::null_mut(), 16) }, INVALID);
    assert_eq!(
        unsafe { prisc_oklch_paint_slice(0, ptr::null(), 4, 4, pixels.as_mut_ptr(), 16) },
        INVALID
    );
}

#[test]
fn a_hue_track_sweeps_the_wheel_opaquely_at_a_modest_chroma() {
    let value = PriscOkLch {
        l: 0.7,
        c: 0.05,
        h_deg: 0.0,
    };
    let mut pixels = [0u32; 256];
    assert_eq!(
        unsafe { prisc_oklch_paint_track(Channel::Hue as i32, &value, pixels.as_mut_ptr(), pixels.len()) },
        OK
    );
    assert!(pixels.iter().all(|p| p >> 24 == 0xFF));
    assert_ne!(pixels[0], pixels[128]);
    // an empty track is fine; a null buffer with a length is not
    assert_eq!(unsafe { prisc_oklch_paint_track(0, &value, ptr::null_mut(), 0) }, OK);
    assert_eq!(unsafe { prisc_oklch_paint_track(0, &value, ptr::null_mut(), 4) }, INVALID);
    assert_eq!(unsafe { prisc_oklch_paint_track(-1, &value, pixels.as_mut_ptr(), 4) }, INVALID);
}

fn rect(x: i32, y: i32, width: i32, height: i32) -> PriscRect {
    PriscRect {
        x,
        y,
        width,
        height,
    }
}

#[test]
fn share_region_normalizes_and_drags() {
    let mut out = PriscRect::default();
    assert_eq!(unsafe { prisc_share_region_normalize(&rect(10, -20, 5, 801), &mut out) }, OK);
    assert_eq!(out, rect(10, -20, 64, 800));
    assert_eq!(unsafe { prisc_share_region_normalize(&out, &mut out) }, OK, "in place, idempotent");
    assert_eq!(out, rect(10, -20, 64, 800));

    let anchor = rect(100, 100, 400, 300);
    assert_eq!(
        unsafe { prisc_share_region_apply_drag(&anchor, H::BottomRight as i32, 50, -20, &mut out) },
        OK
    );
    assert_eq!(out, rect(100, 100, 450, 280));
    assert_eq!(
        unsafe { prisc_share_region_apply_drag(&anchor, H::Body as i32, -150, 35, &mut out) },
        OK
    );
    assert_eq!(out, rect(-50, 135, 400, 300));
    // the right edge dragged past the left: the old left edge is the right one now
    assert_eq!(
        unsafe { prisc_share_region_apply_drag(&anchor, H::Right as i32, -500, 0, &mut out) },
        OK
    );
    assert_eq!(out, rect(0, 100, 100, 300));
}

#[test]
fn share_region_refuses_bad_arguments() {
    let mut out = PriscRect::default();
    let anchor = rect(0, 0, 100, 100);
    assert_eq!(unsafe { prisc_share_region_normalize(ptr::null(), &mut out) }, INVALID);
    assert_eq!(unsafe { prisc_share_region_normalize(&anchor, ptr::null_mut()) }, INVALID);
    assert_eq!(unsafe { prisc_share_region_apply_drag(&anchor, 9, 0, 0, &mut out) }, INVALID);
    assert_eq!(last_error(), "9 is not a PriscShareRegionHandle value");
    assert_eq!(unsafe { prisc_share_region_apply_drag(ptr::null(), 0, 0, 0, &mut out) }, INVALID);
}

#[test]
fn h264_quality_maps_one_crf_onto_every_encoder() {
    let mut q = PriscH264Quality::default();
    assert_eq!(unsafe { prisc_encoding_h264_quality(23, 1920, 1080, 30000, 1001, &mut q) }, OK);
    assert_eq!(
        q,
        PriscH264Quality {
            crf: 23,
            nvenc_cq: 27,
            vt_quality: 55,
            vt_bitrate_kbps: 1673
        }
    );

    for (crf, w, h, num, den) in [
        (52, 1920, 1080, 30, 1),
        (-1, 1920, 1080, 30, 1),
        (21, 0, 1080, 30, 1),
        (21, 1920, 1080, 30, 0),
    ] {
        let mut q = PriscH264Quality::default();
        assert_eq!(
            unsafe { prisc_encoding_h264_quality(crf, w, h, num, den, &mut q) },
            INVALID,
            "{crf} {w}x{h} {num}/{den}"
        );
        assert_eq!(q, PriscH264Quality::default());
    }
    assert_eq!(last_error(), "frame rate 30/0 components must be positive");
    assert_eq!(
        unsafe { prisc_encoding_h264_quality(23, 1920, 1080, 30, 1, ptr::null_mut()) },
        INVALID
    );
}
