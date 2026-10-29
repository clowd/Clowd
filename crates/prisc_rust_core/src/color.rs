//! Colour maths the capture overlay and Prisc.Ui (through prisc_interop) must compute
//! identically: WCAG luminance and the accent's contrast rule, OKLCH in both directions, the hint
//! comet, the accent palette ([`palette`]) and the colour picker's gamut charts ([`chart`]).
//!
//! The accent arrives exactly as the user picked it. Everything that paints with it is derived
//! here:
//!
//! - [`ensure_contrast_with_white`]: the fill the capture panel's buttons, the selection chrome and
//!   the app's primary buttons wear, darkened until white labels on it are readable (WCAG AA,
//!   issue #48) unless the user turned the correction off.
//! - [`comet_colors`]: the hint comet's body and head, which carry no white ink and are derived
//!   from the pick itself, in OKLCH.
//! - [`palette::AccentPalette`]: the effective accent, the comet and the three ten-step ramps the
//!   app's theme is repainted from.
//!
//! Hue is in radians inside this module ([`to_oklch`], [`from_oklch`]) and in degrees on the
//! [`OkLch`] value type, which is what CSS and the picker show. One gamut tolerance (1e-6 on a
//! linear channel) serves the comet, the picker and the charts: a looser one let impossible dark
//! colours through as near-black.

pub mod chart;
pub mod palette;

use std::f64::consts::PI;

use crate::contract::accent;

/// An sRGB colour as three 8-bit channels.
pub type Rgb8 = [u8; 3];

/// The legacy "prisc blue": the shell's stored default pick, and so the capture overlay's default
/// `--accent-color` (`contract::accent::PRISC_BLUE` as channels).
pub const PRISC_BLUE: Rgb8 = from_u32(accent::PRISC_BLUE.0);

/// WCAG AA for normal text (4.5:1), measured against the white glyphs drawn on top of the accent.
pub const MINIMUM_CONTRAST_WITH_WHITE: f64 = accent::MINIMUM_CONTRAST_WITH_WHITE;

/// OKLab lightness the comet's body is held between (see `contract::accent`).
pub const COMET_MIN_LIGHTNESS: f64 = accent::COMET_MIN_LIGHTNESS;
pub const COMET_MAX_LIGHTNESS: f64 = accent::COMET_MAX_LIGHTNESS;

/// How far outside 0..1 a linear channel may land and still count as in gamut. It keeps float
/// error from flagging an sRGB colour as outside, and is kept tiny: near black every channel is
/// close to zero, and a looser one let impossible dark colours through as near-black.
const GAMUT_TOLERANCE: f64 = 1e-6;

/// `0x00RRGGBB` as channels; the top byte is ignored.
pub const fn from_u32(rgb: u32) -> Rgb8 {
    [(rgb >> 16) as u8, (rgb >> 8) as u8, rgb as u8]
}

/// Channels as `0x00RRGGBB`.
pub const fn to_u32(c: Rgb8) -> u32 {
    (c[0] as u32) << 16 | (c[1] as u32) << 8 | c[2] as u32
}

/// WCAG relative luminance (0 = black, 1 = white).
pub fn relative_luminance(c: Rgb8) -> f64 {
    0.2126 * to_linear(c[0]) + 0.7152 * to_linear(c[1]) + 0.0722 * to_linear(c[2])
}

/// WCAG contrast ratio between this colour and white: 1 for white itself, 21 for black.
pub fn contrast_with_white(c: Rgb8) -> f64 {
    1.05 / (relative_luminance(c) + 0.05)
}

/// `c` darkened just enough to reach [`MINIMUM_CONTRAST_WITH_WHITE`] against white, or unchanged
/// when it is dark enough already. Only the brightness moves: luminance is linear in the
/// linear-light channels, so scaling all three by one factor scales it by exactly that factor and
/// keeps the hue.
pub fn ensure_contrast_with_white(c: Rgb8) -> Rgb8 {
    let target = (1.05 / MINIMUM_CONTRAST_WITH_WHITE - 0.05).max(0.0);
    let luminance = relative_luminance(c);
    if luminance <= target {
        return c;
    }
    let scale = target / luminance;
    c.map(|v| encode_floor(to_linear(v) * scale))
}

/// The hint comet's two colours, derived from the accent as the user picked it (not the
/// contrast-darkened fill, which reads as muddy once it glows): the body, and the hot tint its
/// head burns towards. Worked in OKLCH so "lighter" moves only perceived lightness and keeps the
/// user's hue (HSL lightening drifts blues toward purple and greys out yellows); the body keeps as
/// much of the pick's chroma as sRGB can hold at the clamped lightness, and the head is the same
/// hue, lighter, with chroma eased off so it reads as a white-hot core of that colour.
pub fn comet_colors(picked: Rgb8) -> (Rgb8, Rgb8) {
    let (l, c, h) = to_oklch(picked);
    let body_l = l.clamp(COMET_MIN_LIGHTNESS, COMET_MAX_LIGHTNESS);
    let body = from_oklch(body_l, c, h);
    let head = from_oklch((body_l + 0.1).min(0.95), c * 0.6, h);
    (body, head)
}

/// OKLCH (lightness 0..1, chroma, hue in radians) of an sRGB colour. The hue of a grey is
/// whatever `atan2` makes of rounding noise; [`OkLch::from_srgb`] zeroes it.
pub fn to_oklch(c: Rgb8) -> (f64, f64, f64) {
    let [r, g, b] = c.map(to_linear);
    let l = (0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b).cbrt();
    let m = (0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b).cbrt();
    let s = (0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b).cbrt();
    let ll = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
    let aa = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
    let bb = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
    (ll, aa.hypot(bb), bb.atan2(aa))
}

/// The sRGB colour at OKLCH (`l`, `c`, `h` in radians). A lightness past either end is white or
/// black. Out of gamut, the chroma is reduced (never the lightness or the hue) until it fits, so
/// the result is the most saturated colour sRGB has at that lightness and hue.
pub fn from_oklch(l: f64, c: f64, h: f64) -> Rgb8 {
    let l = l.clamp(0.0, 1.0);
    let rgb = match oklch_to_linear(l, c, h) {
        Some(rgb) => rgb,
        None => {
            let (mut lo, mut hi) = (0.0, c);
            for _ in 0..24 {
                let mid = (lo + hi) / 2.0;
                if oklch_to_linear(l, mid, h).is_some() {
                    lo = mid;
                } else {
                    hi = mid;
                }
            }
            oklch_to_linear_unchecked(l, lo, h)
        }
    };
    rgb.map(encode_round)
}

/// An OKLCH colour as CSS Color 4 and the colour picker write it: perceived lightness `l` 0 to 1,
/// chroma `c` from 0 (grey) up to about 0.37 at the edge of the widest displays, hue `h_deg` in
/// degrees. It need not be displayable: [`OkLch::to_srgb`] says whether it is.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct OkLch {
    pub l: f64,
    pub c: f64,
    pub h_deg: f64,
}

impl OkLch {
    /// The OKLCH of an sRGB colour. A grey (chroma below `contract::accent::OKLCH_ACHROMATIC_CHROMA`,
    /// where 8-bit sRGB cannot tell a hue) comes back with hue 0.
    pub fn from_srgb(c: Rgb8) -> OkLch {
        let (l, chroma, h) = to_oklch(c);
        let h_deg = if chroma < accent::OKLCH_ACHROMATIC_CHROMA {
            0.0
        } else {
            normalize_hue(h * 180.0 / PI)
        };
        OkLch {
            l,
            c: chroma,
            h_deg,
        }
    }

    /// The nearest sRGB colour, and whether this value is inside sRGB as it stands (before the
    /// lightness is clamped to 0..1 and before the chroma is lowered to fit).
    pub fn to_srgb(self) -> (Rgb8, bool) {
        let in_gamut = self.to_linear_srgb().is_some();
        (from_oklch(self.l, self.c, self.h_deg * PI / 180.0), in_gamut)
    }

    /// Linear-light sRGB, or `None` outside the gamut. The inner loop of the picker's charts.
    pub fn to_linear_srgb(self) -> Option<[f64; 3]> {
        oklch_to_linear(self.l, self.c, self.h_deg * PI / 180.0)
    }
}

/// A hue in degrees folded into 0..360.
pub fn normalize_hue(degrees: f64) -> f64 {
    let h = degrees % 360.0;
    if h < 0.0 {
        h + 360.0
    } else {
        h
    }
}

fn oklch_to_linear(l: f64, c: f64, h: f64) -> Option<[f64; 3]> {
    let rgb = oklch_to_linear_unchecked(l, c, h);
    rgb.iter()
        .all(|v| (-GAMUT_TOLERANCE..=1.0 + GAMUT_TOLERANCE).contains(v))
        .then_some(rgb)
}

fn oklch_to_linear_unchecked(l: f64, c: f64, h: f64) -> [f64; 3] {
    let (a, b) = (c * h.cos(), c * h.sin());
    let l_ = l + 0.3963377774 * a + 0.2158037573 * b;
    let m_ = l - 0.1055613458 * a - 0.0638541728 * b;
    let s_ = l - 0.0894841775 * a - 1.2914855480 * b;
    let (ll, mm, ss) = (l_ * l_ * l_, m_ * m_ * m_, s_ * s_ * s_);
    [
        4.0767416621 * ll - 3.3077115913 * mm + 0.2309699292 * ss,
        -1.2684380046 * ll + 2.6097574011 * mm - 0.3413193965 * ss,
        -0.0041960863 * ll - 0.7034186147 * mm + 1.7076147010 * ss,
    ]
}

fn to_linear(channel: u8) -> f64 {
    let v = channel as f64 / 255.0;
    if v <= 0.04045 {
        v / 12.92
    } else {
        ((v + 0.055) / 1.055).powf(2.4)
    }
}

fn encode(linear: f64) -> f64 {
    let linear = linear.clamp(0.0, 1.0);
    let v = if linear <= 0.0031308 {
        linear * 12.92
    } else {
        1.055 * linear.powf(1.0 / 2.4) - 0.055
    };
    v * 255.0
}

/// Rounds *down*: quantisation must never push a contrast-corrected colour back above its target
/// luminance, or a colour "fixed" for contrast could still fail the check it was adjusted for.
fn encode_floor(linear: f64) -> u8 {
    encode(linear).floor().clamp(0.0, 255.0) as u8
}

/// A linear-light channel as an 8-bit sRGB one, rounded to nearest (ties to even, as .NET's
/// `Math.Round` did): nothing here is a threshold to stay under.
fn encode_round(linear: f64) -> u8 {
    encode(linear)
        .round_ties_even()
        .clamp(0.0, 255.0) as u8
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The value the shell's own default lands on (`SharedDefaults.DefaultAccent`): both sides
    /// correct the same stored pick to the same fill.
    #[test]
    fn prisc_blue_corrects_to_the_shells_default() {
        assert_eq!(ensure_contrast_with_white(PRISC_BLUE), [0x2F, 0x7C, 0xAE]);
    }

    #[test]
    fn contrast_with_white_matches_known_ratios() {
        assert!((contrast_with_white([0xFF; 3]) - 1.0).abs() < 1e-3);
        assert!((contrast_with_white([0; 3]) - 21.0).abs() < 1e-3);
        // the legacy prisc blue is the colour issue #48 is about: readable-ish, but under AA
        assert!((3.2..3.25).contains(&contrast_with_white(PRISC_BLUE)));
    }

    #[test]
    fn contrast_correction_leaves_dark_colours_alone_and_reaches_aa_otherwise() {
        for c in [[0, 0, 0], [0, 0, 0x80], [0x68, 0x00, 0x81]] {
            assert_eq!(ensure_contrast_with_white(c), c);
        }
        for c in [
            [0xFF, 0xFF, 0xFF],
            [0xFF, 0xFF, 0x00],
            [0xF0, 0xC0, 0xF4],
            [0x00, 0x78, 0xD4],
            PRISC_BLUE,
        ] {
            let fixed = ensure_contrast_with_white(c);
            let ratio = contrast_with_white(fixed);
            assert!(
                (MINIMUM_CONTRAST_WITH_WHITE..MINIMUM_CONTRAST_WITH_WHITE + 0.1).contains(&ratio),
                "{fixed:?}: {ratio}"
            );
            // darker on every channel, never darker than it had to be
            assert!(fixed.iter().zip(c).all(|(f, o)| *f <= o), "{c:?} -> {fixed:?}");
        }
        // the hue ordering survives the scaling
        let [r, g, b] = ensure_contrast_with_white([0xFF, 0xC0, 0x40]);
        assert!(r > g && g > b);
    }

    /// Pinned since the C# and Rust copies were two ports of one formula; still the values the
    /// hint comet and the tray comet show for these picks.
    #[test]
    fn comet_colors_are_pinned() {
        assert_eq!(comet_colors(PRISC_BLUE), ([0x3B, 0x97, 0xD2], [0x83, 0xB4, 0xD8]));
        assert_eq!(comet_colors([0x7A, 0x1F, 0xA2]), ([0xAE, 0x59, 0xDA], [0xBE, 0x8E, 0xDA]));
    }

    #[test]
    fn comet_keeps_the_hue_at_a_glowing_lightness() {
        for picked in [
            PRISC_BLUE,
            [0x00, 0x78, 0xD4],
            [0x7A, 0x1F, 0xA2],
            [0xFF, 0xFF, 0x00],
            [0xE8, 0x11, 0x23],
        ] {
            let (body, head) = comet_colors(picked);
            let (_, picked_c, picked_h) = to_oklch(picked);
            let (body_l, body_c, body_h) = to_oklch(body);
            let (head_l, head_c, _) = to_oklch(head);
            assert!(
                (COMET_MIN_LIGHTNESS - 0.01..=COMET_MAX_LIGHTNESS + 0.01).contains(&body_l),
                "{picked:?}"
            );
            if picked_c > 0.05 && body_c > 0.05 {
                let drift = (body_h - picked_h + PI).rem_euclid(2.0 * PI) - PI;
                assert!(drift.abs() < 0.06, "{picked:?}: hue drifted to {body:?}");
            }
            assert!(head_l > body_l, "{picked:?}");
            assert!(head_c < body_c + 1e-3, "{picked:?}");
        }
    }

    #[test]
    fn oklch_round_trips() {
        for c in [PRISC_BLUE, [0x12, 0x34, 0x56], [0xFF, 0x80, 0x00]] {
            let (l, ch, h) = to_oklch(c);
            assert_eq!(from_oklch(l, ch, h), c);
        }
    }

    /// CSS Color 4's worked values.
    #[test]
    fn oklch_from_srgb_matches_reference_values() {
        for (rgb, l, c, h) in [
            ([0xFF, 0x00, 0x00], 0.6280, 0.2577, 29.23),
            ([0x00, 0xFF, 0x00], 0.8664, 0.2948, 142.50),
            ([0x00, 0x00, 0xFF], 0.4520, 0.3132, 264.05),
            ([0xFF, 0xFF, 0xFF], 1.0000, 0.0000, 0.00),
        ] {
            let v = OkLch::from_srgb(rgb);
            assert!((v.l - l).abs() < 5e-4, "{rgb:?} {v:?}");
            assert!((v.c - c).abs() < 5e-4, "{rgb:?} {v:?}");
            assert!((v.h_deg - h).abs() < 0.05, "{rgb:?} {v:?}");
        }
    }

    /// Every sRGB colour (sampled on a grid) is inside the gamut and survives the round trip
    /// exactly, black included: the 1e-6 tolerance is still wide enough for float error.
    #[test]
    fn every_srgb_colour_round_trips_and_is_in_gamut() {
        for r in (0..=255u32).step_by(15) {
            for g in (0..=255u32).step_by(17) {
                for b in (0..=255u32).step_by(5) {
                    let c = [r as u8, g as u8, b as u8];
                    let (back, in_gamut) = OkLch::from_srgb(c).to_srgb();
                    assert!(in_gamut, "{c:?}");
                    assert_eq!(back, c);
                }
            }
        }
    }

    #[test]
    fn greys_have_no_hue() {
        for v in (0..=255u8).step_by(15) {
            let g = OkLch::from_srgb([v, v, v]);
            assert!(g.c < accent::OKLCH_ACHROMATIC_CHROMA, "{v}");
            assert_eq!(g.h_deg, 0.0);
        }
    }

    #[test]
    fn out_of_gamut_lowers_chroma_only() {
        let vivid = OkLch {
            l: 0.7,
            c: 0.35,
            h_deg: 150.0,
        };
        let (rgb, in_gamut) = vivid.to_srgb();
        assert!(!in_gamut);
        let mapped = OkLch::from_srgb(rgb);
        assert!((mapped.l - 0.7).abs() < 0.005, "{mapped:?}");
        assert!((mapped.h_deg - 150.0).abs() < 0.5, "{mapped:?}");
        assert!(mapped.c < 0.35);
    }

    #[test]
    fn lightness_past_either_end_is_white_or_black() {
        assert_eq!(
            OkLch {
                l: 1.2,
                c: 0.1,
                h_deg: 40.0
            }
            .to_srgb(),
            ([0xFF; 3], false)
        );
        assert_eq!(
            OkLch {
                l: -0.1,
                c: 0.1,
                h_deg: 40.0
            }
            .to_srgb(),
            ([0; 3], false)
        );
    }

    #[test]
    fn hues_fold_into_a_turn() {
        assert_eq!(normalize_hue(-40.0), 320.0);
        assert_eq!(normalize_hue(360.0), 0.0);
        assert_eq!(normalize_hue(725.0), 5.0);
    }

    #[test]
    fn channels_pack_as_0x00rrggbb() {
        assert_eq!(from_u32(0xFF3B97D2), [0x3B, 0x97, 0xD2]);
        assert_eq!(to_u32([0x3B, 0x97, 0xD2]), 0x3B97D2);
        assert_eq!(PRISC_BLUE, [0x3B, 0x97, 0xD2]);
    }
}
