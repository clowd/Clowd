//! Everything one accent colour drives: the effective accent, the hint comet and the three
//! ten-step ramps Prisc.Ui repaints its theme's blue with (`AccentTheme`), computed in one call
//! per accent change.
//!
//! The ramps follow the Semi theme's shape: the stock blue ramp is (to within a rounding step) the
//! base colour blended toward white below step 5 and toward black above it, so the accent's ramp is
//! built the same way and lands in the same places. Light and dark are built separately, because
//! Semi's ramp is not the same in both. The base is the same accent in each (it arrives darkened
//! until white text on it reads, and the solid buttons and checked boxes need that in the dark
//! theme too), lifted only if it would otherwise sink into the dark ground. Dark carries a second,
//! lighter ramp for the tokens that paint accent text, since no one colour can be both a fill under
//! white text and text on a dark ground.

use super::{comet_colors, ensure_contrast_with_white, relative_luminance, Rgb8};

/// The dark theme's window ground (`ApplicationBackgroundColor` in AppResources.axaml).
pub const DARK_GROUND: Rgb8 = [0x20, 0x20, 0x20];

/// The lightest common surface accent text lands on in the dark theme: Semi's `SemiColorFill0`
/// (white at 0.12) over the ground, the default button's fill. Text that only clears the window
/// ground reads as dim there.
pub const DARK_RAISED_GROUND: Rgb8 = [0x3B, 0x3B, 0x3B];

/// The least contrast an accent *fill* may have against [`DARK_GROUND`]: WCAG's floor for UI
/// components. The 4.5:1-against-white rule does not imply this (black passes it and fails here),
/// though any accent with some brightness left clears it as is; the lift mostly bites on
/// near-black picks with the contrast rule off.
pub const MINIMUM_FILL_CONTRAST_WITH_DARK: f64 = 3.0;

/// The least contrast accent *text* may have against [`DARK_RAISED_GROUND`]: WCAG's floor for body
/// text. A fill that sits at 4.5:1 against white always needs a lift to get here: the two targets
/// cannot be met by one colour.
pub const MINIMUM_TEXT_CONTRAST_WITH_DARK: f64 = 4.5;

/// How far steps 0 to 4 are mixed from the base toward white (light theme).
pub const LIGHT_BELOW: [f64; 5] = [0.92, 0.80, 0.60, 0.40, 0.20];

/// How far steps 0 to 4 are mixed from the base toward black (dark theme: dark tints are darker).
pub const DARK_BELOW: [f64; 5] = [0.72, 0.58, 0.42, 0.26, 0.12];

/// How far steps 6 to 9 are mixed from the base toward black, in both themes. Steps 6 and 7 are the
/// pointer-over and pressed fills, still under white text, so they go toward black even in the dark
/// theme: the stock dark ramp runs them lighter, but a fill that sits at 4.5:1 against white has no
/// room to lighten (a 25% lift is 3.3:1, a 50% one 2.1:1). Steps 8 and 9 are not fills anywhere
/// that matters.
pub const ABOVE: [f64; 4] = [0.12, 0.28, 0.44, 0.60];

/// How far the dark text ramp's steps 6 to 9 are mixed from its base toward white: text brightens
/// on hover where a fill darkens.
pub const TEXT_ABOVE: [f64; 4] = [0.15, 0.30, 0.50, 0.70];

const WHITE: Rgb8 = [0xFF; 3];
const BLACK: Rgb8 = [0; 3];

/// One accent's palette (see the module docs).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AccentPalette {
    /// The accent the app paints with: the pick, darkened for white text when the contrast rule is
    /// on.
    pub effective: Rgb8,
    /// The hint comet's body and head, from the pick itself.
    pub comet_body: Rgb8,
    pub comet_head: Rgb8,
    /// The light theme's ramp, the effective accent at step 5.
    pub light: [Rgb8; 10],
    /// The dark theme's fill ramp.
    pub dark: [Rgb8; 10],
    /// The dark theme's text ramp: the fill ramp's tints below step 5, the base lifted to text
    /// contrast on the raised dark surfaces from step 5 up.
    pub dark_text: [Rgb8; 10],
}

impl AccentPalette {
    /// The palette of `picked` (the accent exactly as chosen), with or without the contrast rule.
    pub fn new(picked: Rgb8, maintain_contrast: bool) -> AccentPalette {
        let effective = if maintain_contrast {
            ensure_contrast_with_white(picked)
        } else {
            picked
        };
        let (comet_body, comet_head) = comet_colors(picked);
        let dark = ramp(effective, true);
        AccentPalette {
            effective,
            comet_body,
            comet_head,
            light: ramp(effective, false),
            dark,
            dark_text: dark_text_ramp(&dark),
        }
    }
}

/// The ten-step ramp Semi expects, with the accent at step 5.
pub fn ramp(accent: Rgb8, dark: bool) -> [Rgb8; 10] {
    // The accent as set is the base in both themes (lightening it for dark made the solid buttons
    // pale under their white labels); it is only lifted when it would sink into the dark ground.
    let base = if dark {
        lift_to_contrast(accent, DARK_GROUND, MINIMUM_FILL_CONTRAST_WITH_DARK)
    } else {
        accent
    };
    let (below, toward) = if dark { (DARK_BELOW, BLACK) } else { (LIGHT_BELOW, WHITE) };

    let mut ramp = [base; 10];
    for (step, amount) in below.iter().enumerate() {
        ramp[step] = mix(base, toward, *amount);
    }
    for (i, amount) in ABOVE.iter().enumerate() {
        ramp[6 + i] = mix(base, BLACK, *amount);
    }
    ramp
}

/// The ramp the dark theme paints accent *text* from: the fill base lifted toward white until it
/// reads at text contrast on the raised surfaces text actually sits on (a button's grey fill, not
/// just the window), with the pointer-over and pressed steps lifted a little further. Steps below
/// the base are the fill ramp's: they are tints, not text.
pub fn dark_text_ramp(fills: &[Rgb8; 10]) -> [Rgb8; 10] {
    let text = lift_to_contrast(fills[5], DARK_RAISED_GROUND, MINIMUM_TEXT_CONTRAST_WITH_DARK);
    let mut ramp = *fills;
    ramp[5] = text;
    for (i, amount) in TEXT_ABOVE.iter().enumerate() {
        ramp[6 + i] = mix(text, WHITE, *amount);
    }
    ramp
}

/// `color` mixed toward white just far enough to reach `minimum_contrast` against `ground`, or
/// unchanged when it already does.
pub fn lift_to_contrast(color: Rgb8, ground: Rgb8, minimum_contrast: f64) -> Rgb8 {
    let target = minimum_contrast * (relative_luminance(ground) + 0.05) - 0.05;
    if relative_luminance(color) >= target {
        return color;
    }

    // Luminance climbs monotonically with the mix amount, so bisect it.
    let (mut lo, mut hi) = (0.0, 1.0);
    for _ in 0..16 {
        let mid = (lo + hi) / 2.0;
        if relative_luminance(mix(color, WHITE, mid)) >= target {
            hi = mid;
        } else {
            lo = mid;
        }
    }
    mix(color, WHITE, hi)
}

/// `from` moved `amount` (0..1) of the way to `to` per channel, rounded half to even.
pub fn mix(from: Rgb8, to: Rgb8, amount: f64) -> Rgb8 {
    let channel = |a: u8, b: u8| (a as f64 + (b as f64 - a as f64) * amount).round_ties_even() as u8;
    [channel(from[0], to[0]), channel(from[1], to[1]), channel(from[2], to[2])]
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::color::{contrast_with_white, from_u32};

    /// One palette as master's C# `AccentTheme` (BuildRamp, BuildDarkTextRamp) and
    /// `AccentColors` (EnsureContrastWithWhite, CometColors) produced it, captured by reflection
    /// from the C# build before the switch to this module.
    struct Vector {
        picked: u32,
        maintain: bool,
        effective: u32,
        comet: (u32, u32),
        light: [u32; 10],
        dark: [u32; 10],
        dark_text: [u32; 10],
    }

    fn check(v: &Vector) {
        let p = AccentPalette::new(from_u32(v.picked), v.maintain);
        let ctx = format!("{:06X} maintain={}", v.picked, v.maintain);
        assert_eq!(p.effective, from_u32(v.effective), "effective {ctx}");
        assert_eq!(
            (p.comet_body, p.comet_head),
            (from_u32(v.comet.0), from_u32(v.comet.1)),
            "comet {ctx}"
        );
        assert_eq!(p.light, v.light.map(from_u32), "light {ctx}");
        assert_eq!(p.dark, v.dark.map(from_u32), "dark {ctx}");
        assert_eq!(p.dark_text, v.dark_text.map(from_u32), "dark text {ctx}");
    }

    #[test]
    fn palettes_equal_the_csharp_accent_theme() {
        for v in VECTORS {
            check(v);
        }
    }

    #[test]
    fn the_dark_text_base_reads_on_the_raised_ground() {
        for v in VECTORS {
            let p = AccentPalette::new(from_u32(v.picked), v.maintain);
            let text = p.dark_text[5];
            let ratio = (relative_luminance(text) + 0.05) / (relative_luminance(DARK_RAISED_GROUND) + 0.05);
            assert!(ratio >= MINIMUM_TEXT_CONTRAST_WITH_DARK, "{:06X}: {ratio}", v.picked);
            if v.maintain {
                assert!(contrast_with_white(p.effective) >= 4.5, "{:06X}", v.picked);
            }
        }
    }

    #[test]
    fn mix_rounds_half_to_even() {
        // 0 + 255 * 0.5 = 127.5 -> 128 (even), 1 + 254 * 0.5 = 128 exactly, 3 + 252 * 0.5 = 129
        assert_eq!(mix([0, 1, 3], WHITE, 0.5), [128, 128, 129]);
        // 58 + 197 * 0.5 = 156.5 -> 156 (even): away-from-zero rounding would give 157
        assert_eq!(mix([58, 58, 58], WHITE, 0.5), [156, 156, 156]);
    }

    /// Master's C# output for these picks, contrast rule on and off (see [`Vector`]).
    const VECTORS: &[Vector] = &[
        Vector {
            picked: 0x3B97D2,
            maintain: true,
            effective: 0x2F7CAE,
            comet: (0x3B97D2, 0x83B4D8),
            light: [
                0xEEF5F9, 0xD5E5EF, 0xACCBDF, 0x82B0CE, 0x5996BE, 0x2F7CAE, 0x296D99, 0x22597D, 0x1A4561, 0x133246,
            ],
            dark: [
                0x0D2331, 0x143449, 0x1B4865, 0x235C81, 0x296D99, 0x2F7CAE, 0x296D99, 0x22597D, 0x1A4561, 0x133246,
            ],
            dark_text: [
                0x0D2331, 0x143449, 0x1B4865, 0x235C81, 0x296D99, 0x79ABCB, 0x8DB8D3, 0xA1C4DB, 0xBCD5E5, 0xD7E6EF,
            ],
        },
        Vector {
            picked: 0x3B97D2,
            maintain: false,
            effective: 0x3B97D2,
            comet: (0x3B97D2, 0x83B4D8),
            light: [
                0xEFF7FB, 0xD8EAF6, 0xB1D5ED, 0x89C1E4, 0x62ACDB, 0x3B97D2, 0x3485B9, 0x2A6D97, 0x215576, 0x183C54,
            ],
            dark: [
                0x112A3B, 0x193F58, 0x22587A, 0x2C709B, 0x3485B9, 0x3B97D2, 0x3485B9, 0x2A6D97, 0x215576, 0x183C54,
            ],
            dark_text: [
                0x112A3B, 0x193F58, 0x22587A, 0x2C709B, 0x3485B9, 0x62ACDB, 0x7AB8E0, 0x91C5E6, 0xB0D6ED, 0xD0E6F4,
            ],
        },
        Vector {
            picked: 0x0078D4,
            maintain: true,
            effective: 0x0078D4,
            comet: (0x2288E6, 0x75A9E1),
            light: [
                0xEBF4FC, 0xCCE4F6, 0x99C9EE, 0x66AEE5, 0x3393DD, 0x0078D4, 0x006ABB, 0x005699, 0x004377, 0x003055,
            ],
            dark: [
                0x00223B, 0x003259, 0x00467B, 0x00599D, 0x006ABB, 0x0078D4, 0x006ABB, 0x005699, 0x004377, 0x003055,
            ],
            dark_text: [
                0x00223B, 0x003259, 0x00467B, 0x00599D, 0x006ABB, 0x60ABE4, 0x78B8E8, 0x90C4EC, 0xB0D5F2, 0xCFE6F7,
            ],
        },
        Vector {
            picked: 0x0078D4,
            maintain: false,
            effective: 0x0078D4,
            comet: (0x2288E6, 0x75A9E1),
            light: [
                0xEBF4FC, 0xCCE4F6, 0x99C9EE, 0x66AEE5, 0x3393DD, 0x0078D4, 0x006ABB, 0x005699, 0x004377, 0x003055,
            ],
            dark: [
                0x00223B, 0x003259, 0x00467B, 0x00599D, 0x006ABB, 0x0078D4, 0x006ABB, 0x005699, 0x004377, 0x003055,
            ],
            dark_text: [
                0x00223B, 0x003259, 0x00467B, 0x00599D, 0x006ABB, 0x60ABE4, 0x78B8E8, 0x90C4EC, 0xB0D5F2, 0xCFE6F7,
            ],
        },
        Vector {
            picked: 0x7A1FA2,
            maintain: true,
            effective: 0x7A1FA2,
            comet: (0xAE59DA, 0xBE8EDA),
            light: [
                0xF4EDF8, 0xE4D2EC, 0xCAA5DA, 0xAF79C7, 0x954CB5, 0x7A1FA2, 0x6B1B8F, 0x581675, 0x44115B, 0x310C41,
            ],
            dark: [
                0x291532, 0x3E1F4C, 0x552B68, 0x6D3785, 0x81419E, 0x934AB4, 0x81419E, 0x6A3582, 0x522965, 0x3B1E48,
            ],
            dark_text: [
                0x291532, 0x3E1F4C, 0x552B68, 0x6D3785, 0x81419E, 0xBF95D3, 0xC9A5DA, 0xD2B5E0, 0xDFCAE9, 0xECDFF2,
            ],
        },
        Vector {
            picked: 0x7A1FA2,
            maintain: false,
            effective: 0x7A1FA2,
            comet: (0xAE59DA, 0xBE8EDA),
            light: [
                0xF4EDF8, 0xE4D2EC, 0xCAA5DA, 0xAF79C7, 0x954CB5, 0x7A1FA2, 0x6B1B8F, 0x581675, 0x44115B, 0x310C41,
            ],
            dark: [
                0x291532, 0x3E1F4C, 0x552B68, 0x6D3785, 0x81419E, 0x934AB4, 0x81419E, 0x6A3582, 0x522965, 0x3B1E48,
            ],
            dark_text: [
                0x291532, 0x3E1F4C, 0x552B68, 0x6D3785, 0x81419E, 0xBF95D3, 0xC9A5DA, 0xD2B5E0, 0xDFCAE9, 0xECDFF2,
            ],
        },
        Vector {
            picked: 0xFFFF00,
            maintain: true,
            effective: 0x7A7A00,
            comet: (0xACAC00, 0xC9CB64),
            light: [
                0xF4F4EB, 0xE4E4CC, 0xCACA99, 0xAFAF66, 0x959533, 0x7A7A00, 0x6B6B00, 0x585800, 0x444400, 0x313100,
            ],
            dark: [
                0x222200, 0x333300, 0x474700, 0x5A5A00, 0x6B6B00, 0x7A7A00, 0x6B6B00, 0x585800, 0x444400, 0x313100,
            ],
            dark_text: [
                0x222200, 0x333300, 0x474700, 0x5A5A00, 0x6B6B00, 0xA9A959, 0xB6B672, 0xC3C38B, 0xD4D4AC, 0xE5E5CD,
            ],
        },
        Vector {
            picked: 0xFFFF00,
            maintain: false,
            effective: 0xFFFF00,
            comet: (0xACAC00, 0xC9CB64),
            light: [
                0xFFFFEB, 0xFFFFCC, 0xFFFF99, 0xFFFF66, 0xFFFF33, 0xFFFF00, 0xE0E000, 0xB8B800, 0x8F8F00, 0x666600,
            ],
            dark: [
                0x474700, 0x6B6B00, 0x949400, 0xBDBD00, 0xE0E000, 0xFFFF00, 0xE0E000, 0xB8B800, 0x8F8F00, 0x666600,
            ],
            dark_text: [
                0x474700, 0x6B6B00, 0x949400, 0xBDBD00, 0xE0E000, 0xFFFF00, 0xFFFF26, 0xFFFF4C, 0xFFFF80, 0xFFFFB2,
            ],
        },
        Vector {
            picked: 0xE81123,
            maintain: true,
            effective: 0xE81123,
            comet: (0xF3262E, 0xF07F75),
            light: [
                0xFDECED, 0xFACFD3, 0xF6A0A7, 0xF1707B, 0xED414F, 0xE81123, 0xCC0F1F, 0xA70C19, 0x820A14, 0x5D070E,
            ],
            dark: [
                0x41050A, 0x61070F, 0x870A14, 0xAC0D1A, 0xCC0F1F, 0xE81123, 0xCC0F1F, 0xA70C19, 0x820A14, 0x5D070E,
            ],
            dark_text: [
                0x41050A, 0x61070F, 0x870A14, 0xAC0D1A, 0xCC0F1F, 0xF3838D, 0xF5969E, 0xF7A8AF, 0xF9C1C6, 0xFBDADD,
            ],
        },
        Vector {
            picked: 0xE81123,
            maintain: false,
            effective: 0xE81123,
            comet: (0xF3262E, 0xF07F75),
            light: [
                0xFDECED, 0xFACFD3, 0xF6A0A7, 0xF1707B, 0xED414F, 0xE81123, 0xCC0F1F, 0xA70C19, 0x820A14, 0x5D070E,
            ],
            dark: [
                0x41050A, 0x61070F, 0x870A14, 0xAC0D1A, 0xCC0F1F, 0xE81123, 0xCC0F1F, 0xA70C19, 0x820A14, 0x5D070E,
            ],
            dark_text: [
                0x41050A, 0x61070F, 0x870A14, 0xAC0D1A, 0xCC0F1F, 0xF3838D, 0xF5969E, 0xF7A8AF, 0xF9C1C6, 0xFBDADD,
            ],
        },
        Vector {
            picked: 0xFFFFFF,
            maintain: true,
            effective: 0x767676,
            comet: (0xA4A4A4, 0xC4C4C4),
            light: [
                0xF4F4F4, 0xE4E4E4, 0xC8C8C8, 0xADADAD, 0x919191, 0x767676, 0x686868, 0x555555, 0x424242, 0x2F2F2F,
            ],
            dark: [
                0x212121, 0x323232, 0x444444, 0x575757, 0x686868, 0x767676, 0x686868, 0x555555, 0x424242, 0x2F2F2F,
            ],
            dark_text: [
                0x212121, 0x323232, 0x444444, 0x575757, 0x686868, 0xA5A5A5, 0xB2B2B2, 0xC0C0C0, 0xD2D2D2, 0xE4E4E4,
            ],
        },
        Vector {
            picked: 0xFFFFFF,
            maintain: false,
            effective: 0xFFFFFF,
            comet: (0xA4A4A4, 0xC4C4C4),
            light: [
                0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xE0E0E0, 0xB8B8B8, 0x8F8F8F, 0x666666,
            ],
            dark: [
                0x474747, 0x6B6B6B, 0x949494, 0xBDBDBD, 0xE0E0E0, 0xFFFFFF, 0xE0E0E0, 0xB8B8B8, 0x8F8F8F, 0x666666,
            ],
            dark_text: [
                0x474747, 0x6B6B6B, 0x949494, 0xBDBDBD, 0xE0E0E0, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF,
            ],
        },
        Vector {
            picked: 0x000000,
            maintain: true,
            effective: 0x000000,
            comet: (0x868686, 0xA4A4A4),
            light: [
                0xEBEBEB, 0xCCCCCC, 0x999999, 0x666666, 0x333333, 0x000000, 0x000000, 0x000000, 0x000000, 0x000000,
            ],
            dark: [
                0x1E1E1E, 0x2D2D2D, 0x3D3D3D, 0x4E4E4E, 0x5D5D5D, 0x6A6A6A, 0x5D5D5D, 0x4C4C4C, 0x3B3B3B, 0x2A2A2A,
            ],
            dark_text: [
                0x1E1E1E, 0x2D2D2D, 0x3D3D3D, 0x4E4E4E, 0x5D5D5D, 0xA5A5A5, 0xB2B2B2, 0xC0C0C0, 0xD2D2D2, 0xE4E4E4,
            ],
        },
        Vector {
            picked: 0x000000,
            maintain: false,
            effective: 0x000000,
            comet: (0x868686, 0xA4A4A4),
            light: [
                0xEBEBEB, 0xCCCCCC, 0x999999, 0x666666, 0x333333, 0x000000, 0x000000, 0x000000, 0x000000, 0x000000,
            ],
            dark: [
                0x1E1E1E, 0x2D2D2D, 0x3D3D3D, 0x4E4E4E, 0x5D5D5D, 0x6A6A6A, 0x5D5D5D, 0x4C4C4C, 0x3B3B3B, 0x2A2A2A,
            ],
            dark_text: [
                0x1E1E1E, 0x2D2D2D, 0x3D3D3D, 0x4E4E4E, 0x5D5D5D, 0xA5A5A5, 0xB2B2B2, 0xC0C0C0, 0xD2D2D2, 0xE4E4E4,
            ],
        },
        Vector {
            picked: 0x00FF00,
            maintain: true,
            effective: 0x008A00,
            comet: (0x00C700, 0x79E072),
            light: [
                0xEBF6EB, 0xCCE8CC, 0x99D099, 0x66B966, 0x33A133, 0x008A00, 0x007900, 0x006300, 0x004D00, 0x003700,
            ],
            dark: [
                0x002700, 0x003A00, 0x005000, 0x006600, 0x007900, 0x008A00, 0x007900, 0x006300, 0x004D00, 0x003700,
            ],
            dark_text: [
                0x002700, 0x003A00, 0x005000, 0x006600, 0x007900, 0x61B761, 0x79C279, 0x90CD90, 0xB0DBB0, 0xD0E9D0,
            ],
        },
        Vector {
            picked: 0x00FF00,
            maintain: false,
            effective: 0x00FF00,
            comet: (0x00C700, 0x79E072),
            light: [
                0xEBFFEB, 0xCCFFCC, 0x99FF99, 0x66FF66, 0x33FF33, 0x00FF00, 0x00E000, 0x00B800, 0x008F00, 0x006600,
            ],
            dark: [
                0x004700, 0x006B00, 0x009400, 0x00BD00, 0x00E000, 0x00FF00, 0x00E000, 0x00B800, 0x008F00, 0x006600,
            ],
            dark_text: [
                0x004700, 0x006B00, 0x009400, 0x00BD00, 0x00E000, 0x00FF00, 0x26FF26, 0x4CFF4C, 0x80FF80, 0xB2FFB2,
            ],
        },
        Vector {
            picked: 0xF0C0F4,
            maintain: true,
            effective: 0x8A6D8C,
            comet: (0xC092C4, 0xD6B9D8),
            light: [
                0xF6F3F6, 0xE8E2E8, 0xD0C5D1, 0xB9A7BA, 0xA18AA3, 0x8A6D8C, 0x79607B, 0x634E65, 0x4D3D4E, 0x372C38,
            ],
            dark: [
                0x271F27, 0x3A2E3B, 0x503F51, 0x665168, 0x79607B, 0x8A6D8C, 0x79607B, 0x634E65, 0x4D3D4E, 0x372C38,
            ],
            dark_text: [
                0x271F27, 0x3A2E3B, 0x503F51, 0x665168, 0x79607B, 0xB29FB3, 0xBEADBE, 0xC9BCCA, 0xD8CFD9, 0xE8E2E8,
            ],
        },
        Vector {
            picked: 0xF0C0F4,
            maintain: false,
            effective: 0xF0C0F4,
            comet: (0xC092C4, 0xD6B9D8),
            light: [
                0xFEFAFE, 0xFCF2FD, 0xF9E6FB, 0xF6D9F8, 0xF3CDF6, 0xF0C0F4, 0xD3A9D7, 0xAD8AB0, 0x866C89, 0x604D62,
            ],
            dark: [
                0x433644, 0x655166, 0x8B6F8E, 0xB28EB5, 0xD3A9D7, 0xF0C0F4, 0xD3A9D7, 0xAD8AB0, 0x866C89, 0x604D62,
            ],
            dark_text: [
                0x433644, 0x655166, 0x8B6F8E, 0xB28EB5, 0xD3A9D7, 0xF0C0F4, 0xF2C9F6, 0xF4D3F7, 0xF8E0FA, 0xFAECFC,
            ],
        },
        Vector {
            picked: 0x101010,
            maintain: true,
            effective: 0x101010,
            comet: (0x868686, 0xA4A4A4),
            light: [
                0xECECEC, 0xCFCFCF, 0x9F9F9F, 0x707070, 0x404040, 0x101010, 0x0E0E0E, 0x0C0C0C, 0x090909, 0x060606,
            ],
            dark: [
                0x1E1E1E, 0x2D2D2D, 0x3D3D3D, 0x4E4E4E, 0x5D5D5D, 0x6A6A6A, 0x5D5D5D, 0x4C4C4C, 0x3B3B3B, 0x2A2A2A,
            ],
            dark_text: [
                0x1E1E1E, 0x2D2D2D, 0x3D3D3D, 0x4E4E4E, 0x5D5D5D, 0xA5A5A5, 0xB2B2B2, 0xC0C0C0, 0xD2D2D2, 0xE4E4E4,
            ],
        },
        Vector {
            picked: 0x101010,
            maintain: false,
            effective: 0x101010,
            comet: (0x868686, 0xA4A4A4),
            light: [
                0xECECEC, 0xCFCFCF, 0x9F9F9F, 0x707070, 0x404040, 0x101010, 0x0E0E0E, 0x0C0C0C, 0x090909, 0x060606,
            ],
            dark: [
                0x1E1E1E, 0x2D2D2D, 0x3D3D3D, 0x4E4E4E, 0x5D5D5D, 0x6A6A6A, 0x5D5D5D, 0x4C4C4C, 0x3B3B3B, 0x2A2A2A,
            ],
            dark_text: [
                0x1E1E1E, 0x2D2D2D, 0x3D3D3D, 0x4E4E4E, 0x5D5D5D, 0xA5A5A5, 0xB2B2B2, 0xC0C0C0, 0xD2D2D2, 0xE4E4E4,
            ],
        },
        Vector {
            picked: 0x2F7CAE,
            maintain: true,
            effective: 0x2F7CAE,
            comet: (0x428EC1, 0x80AACA),
            light: [
                0xEEF5F9, 0xD5E5EF, 0xACCBDF, 0x82B0CE, 0x5996BE, 0x2F7CAE, 0x296D99, 0x22597D, 0x1A4561, 0x133246,
            ],
            dark: [
                0x0D2331, 0x143449, 0x1B4865, 0x235C81, 0x296D99, 0x2F7CAE, 0x296D99, 0x22597D, 0x1A4561, 0x133246,
            ],
            dark_text: [
                0x0D2331, 0x143449, 0x1B4865, 0x235C81, 0x296D99, 0x79ABCB, 0x8DB8D3, 0xA1C4DB, 0xBCD5E5, 0xD7E6EF,
            ],
        },
        Vector {
            picked: 0x2F7CAE,
            maintain: false,
            effective: 0x2F7CAE,
            comet: (0x428EC1, 0x80AACA),
            light: [
                0xEEF5F9, 0xD5E5EF, 0xACCBDF, 0x82B0CE, 0x5996BE, 0x2F7CAE, 0x296D99, 0x22597D, 0x1A4561, 0x133246,
            ],
            dark: [
                0x0D2331, 0x143449, 0x1B4865, 0x235C81, 0x296D99, 0x2F7CAE, 0x296D99, 0x22597D, 0x1A4561, 0x133246,
            ],
            dark_text: [
                0x0D2331, 0x143449, 0x1B4865, 0x235C81, 0x296D99, 0x79ABCB, 0x8DB8D3, 0xA1C4DB, 0xBCD5E5, 0xD7E6EF,
            ],
        },
        Vector {
            picked: 0x808080,
            maintain: true,
            effective: 0x767676,
            comet: (0x868686, 0xA4A4A4),
            light: [
                0xF4F4F4, 0xE4E4E4, 0xC8C8C8, 0xADADAD, 0x919191, 0x767676, 0x686868, 0x555555, 0x424242, 0x2F2F2F,
            ],
            dark: [
                0x212121, 0x323232, 0x444444, 0x575757, 0x686868, 0x767676, 0x686868, 0x555555, 0x424242, 0x2F2F2F,
            ],
            dark_text: [
                0x212121, 0x323232, 0x444444, 0x575757, 0x686868, 0xA5A5A5, 0xB2B2B2, 0xC0C0C0, 0xD2D2D2, 0xE4E4E4,
            ],
        },
        Vector {
            picked: 0x808080,
            maintain: false,
            effective: 0x808080,
            comet: (0x868686, 0xA4A4A4),
            light: [
                0xF5F5F5, 0xE6E6E6, 0xCCCCCC, 0xB3B3B3, 0x999999, 0x808080, 0x717171, 0x5C5C5C, 0x484848, 0x333333,
            ],
            dark: [
                0x242424, 0x363636, 0x4A4A4A, 0x5F5F5F, 0x717171, 0x808080, 0x717171, 0x5C5C5C, 0x484848, 0x333333,
            ],
            dark_text: [
                0x242424, 0x363636, 0x4A4A4A, 0x5F5F5F, 0x717171, 0xA5A5A5, 0xB2B2B2, 0xC0C0C0, 0xD2D2D2, 0xE4E4E4,
            ],
        },
        Vector {
            picked: 0xFF8C00,
            maintain: true,
            effective: 0xB46100,
            comet: (0xF18400, 0xF9B37D),
            light: [
                0xF9F2EB, 0xF0DFCC, 0xE1C099, 0xD2A066, 0xC38133, 0xB46100, 0x9E5500, 0x824600, 0x653600, 0x482700,
            ],
            dark: [
                0x321B00, 0x4C2900, 0x683800, 0x854800, 0x9E5500, 0xB46100, 0x9E5500, 0x824600, 0x653600, 0x482700,
            ],
            dark_text: [
                0x321B00, 0x4C2900, 0x683800, 0x854800, 0x9E5500, 0xCF9B5D, 0xD6AA75, 0xDDB98E, 0xE7CDAE, 0xF1E1CE,
            ],
        },
        Vector {
            picked: 0xFF8C00,
            maintain: false,
            effective: 0xFF8C00,
            comet: (0xF18400, 0xF9B37D),
            light: [
                0xFFF6EB, 0xFFE8CC, 0xFFD199, 0xFFBA66, 0xFFA333, 0xFF8C00, 0xE07B00, 0xB86500, 0x8F4E00, 0x663800,
            ],
            dark: [
                0x472700, 0x6B3B00, 0x945100, 0xBD6800, 0xE07B00, 0xFF8C00, 0xE07B00, 0xB86500, 0x8F4E00, 0x663800,
            ],
            dark_text: [
                0x472700, 0x6B3B00, 0x945100, 0xBD6800, 0xE07B00, 0xFF8C00, 0xFF9D26, 0xFFAE4C, 0xFFC680, 0xFFDCB2,
            ],
        },
        Vector {
            picked: 0x00B7C3,
            maintain: true,
            effective: 0x00838C,
            comet: (0x00B7C3, 0x87CFD6),
            light: [
                0xEBF5F6, 0xCCE6E8, 0x99CDD1, 0x66B5BA, 0x339CA3, 0x00838C, 0x00737B, 0x005E65, 0x00494E, 0x003438,
            ],
            dark: [
                0x002527, 0x00373B, 0x004C51, 0x006168, 0x00737B, 0x00838C, 0x00737B, 0x005E65, 0x00494E, 0x003438,
            ],
            dark_text: [
                0x002527, 0x00373B, 0x004C51, 0x006168, 0x00737B, 0x5EB1B6, 0x76BDC1, 0x8EC8CC, 0xAED8DA, 0xCFE8E9,
            ],
        },
        Vector {
            picked: 0x00B7C3,
            maintain: false,
            effective: 0x00B7C3,
            comet: (0x00B7C3, 0x87CFD6),
            light: [
                0xEBF9FA, 0xCCF1F3, 0x99E2E7, 0x66D4DB, 0x33C5CF, 0x00B7C3, 0x00A1AC, 0x00848C, 0x00666D, 0x00494E,
            ],
            dark: [
                0x003337, 0x004D52, 0x006A71, 0x008790, 0x00A1AC, 0x00B7C3, 0x00A1AC, 0x00848C, 0x00666D, 0x00494E,
            ],
            dark_text: [
                0x003337, 0x004D52, 0x006A71, 0x008790, 0x00A1AC, 0x00B7C3, 0x26C2CC, 0x4CCDD5, 0x80DBE1, 0xB2E9ED,
            ],
        },
        Vector {
            picked: 0x8764B8,
            maintain: true,
            effective: 0x8764B8,
            comet: (0x9572C7, 0xAD99CE),
            light: [
                0xF5F3F9, 0xE7E0F1, 0xCFC1E3, 0xB7A2D4, 0x9F83C6, 0x8764B8, 0x7758A2, 0x614884, 0x4C3867, 0x36284A,
            ],
            dark: [
                0x261C34, 0x392A4D, 0x4E3A6B, 0x644A88, 0x7758A2, 0x8764B8, 0x7758A2, 0x614884, 0x4C3867, 0x36284A,
            ],
            dark_text: [
                0x261C34, 0x392A4D, 0x4E3A6B, 0x644A88, 0x7758A2, 0xB19BD1, 0xBDAAD8, 0xC8B9DF, 0xD8CDE8, 0xE8E1F1,
            ],
        },
        Vector {
            picked: 0x8764B8,
            maintain: false,
            effective: 0x8764B8,
            comet: (0x9572C7, 0xAD99CE),
            light: [
                0xF5F3F9, 0xE7E0F1, 0xCFC1E3, 0xB7A2D4, 0x9F83C6, 0x8764B8, 0x7758A2, 0x614884, 0x4C3867, 0x36284A,
            ],
            dark: [
                0x261C34, 0x392A4D, 0x4E3A6B, 0x644A88, 0x7758A2, 0x8764B8, 0x7758A2, 0x614884, 0x4C3867, 0x36284A,
            ],
            dark_text: [
                0x261C34, 0x392A4D, 0x4E3A6B, 0x644A88, 0x7758A2, 0xB19BD1, 0xBDAAD8, 0xC8B9DF, 0xD8CDE8, 0xE8E1F1,
            ],
        },
        Vector {
            picked: 0x004000,
            maintain: true,
            effective: 0x004000,
            comet: (0x5D9759, 0x8DB08A),
            light: [
                0xEBF0EB, 0xCCD9CC, 0x99B399, 0x668C66, 0x336633, 0x004000, 0x003800, 0x002E00, 0x002400, 0x001A00,
            ],
            dark: [
                0x142114, 0x1D311D, 0x294429, 0x345734, 0x3E673E, 0x467546, 0x3E673E, 0x325432, 0x274227, 0x1C2F1C,
            ],
            dark_text: [
                0x142114, 0x1D311D, 0x294429, 0x345734, 0x3E673E, 0x8FAC8F, 0xA0B8A0, 0xB1C5B1, 0xC7D6C7, 0xDDE6DD,
            ],
        },
        Vector {
            picked: 0x004000,
            maintain: false,
            effective: 0x004000,
            comet: (0x5D9759, 0x8DB08A),
            light: [
                0xEBF0EB, 0xCCD9CC, 0x99B399, 0x668C66, 0x336633, 0x004000, 0x003800, 0x002E00, 0x002400, 0x001A00,
            ],
            dark: [
                0x142114, 0x1D311D, 0x294429, 0x345734, 0x3E673E, 0x467546, 0x3E673E, 0x325432, 0x274227, 0x1C2F1C,
            ],
            dark_text: [
                0x142114, 0x1D311D, 0x294429, 0x345734, 0x3E673E, 0x8FAC8F, 0xA0B8A0, 0xB1C5B1, 0xC7D6C7, 0xDDE6DD,
            ],
        },
    ];
}
