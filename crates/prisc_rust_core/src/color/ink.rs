//! The ink a sticky note's text is drawn in: a deep shade of the paper's own hue (dark gold on
//! butter, berry on rose, ...), darkened until it reaches [`NOTE_INK_CONTRAST`] against the paper.
//! A dark paper gets a pale tint of its hue instead; a grey one, a neutral. Prisc.Drawing asks for
//! it through `prisc_color_note_ink`, one call per paper.

use super::{relative_luminance, Rgb8};
use crate::contract::accent;

/// The WCAG relative luminance above which a colour wants dark ink (C#: `AccentColors.IsLight`).
pub const LIGHT_LUMINANCE: f64 = accent::LIGHT_LUMINANCE;

/// The contrast a note's ink reaches against its paper (WCAG AAA for body text).
pub const NOTE_INK_CONTRAST: f64 = accent::NOTE_INK_CONTRAST;

/// A colour with straight (not premultiplied) alpha, as `0xAARRGGBB`.
pub type Argb = u32;

/// The ink for `paper` (`0xAARRGGBB`; a translucent paper is judged as it shows over white). The
/// search walks the paper's hue down from HSL lightness 0.32 (or up from 0.86 on a dark paper) in
/// steps of 0.02, saturation capped at 0.75, and takes the first shade that reaches
/// [`NOTE_INK_CONTRAST`]; when none does, the ink is black (or white).
pub fn note_ink(paper: Argb) -> Rgb8 {
    let rgb = [(paper >> 16) as u8, (paper >> 8) as u8, paper as u8];
    let ground = relative_luminance(over_white(rgb, (paper >> 24) as u8));
    let dark_ink = ground > LIGHT_LUMINANCE;
    let (h, s, _) = to_hsl(rgb);
    let s = s.min(0.75);

    let (mut l, step) = if dark_ink { (0.32, -0.02) } else { (0.86, 0.02) };
    while (0.0..=1.0).contains(&l) {
        let candidate = from_hsl(h, s, l);
        if contrast_ratio(relative_luminance(candidate), ground) >= NOTE_INK_CONTRAST {
            return candidate;
        }
        l += step;
    }
    if dark_ink {
        [0, 0, 0]
    } else {
        [255, 255, 255]
    }
}

/// The WCAG contrast ratio between two relative luminances: 1 (none) to 21 (black on white).
pub fn contrast_ratio(a: f64, b: f64) -> f64 {
    (a.max(b) + 0.05) / (a.min(b) + 0.05)
}

/// `rgb` at straight alpha `a` composited over white, rounded half to even.
fn over_white(rgb: Rgb8, a: u8) -> Rgb8 {
    let a = a as f64;
    rgb.map(|v| ((v as f64 * a + 255.0 * (255.0 - a)) / 255.0).round_ties_even() as u8)
}

/// Hue in degrees (0 to 360), saturation and lightness (0 to 1).
fn to_hsl(c: Rgb8) -> (f64, f64, f64) {
    let [r, g, b] = c.map(|v| v as f64 / 255.0);
    let (max, min) = (r.max(g).max(b), r.min(g).min(b));
    let (l, d) = ((max + min) / 2.0, max - min);
    if d == 0.0 {
        return (0.0, 0.0, l);
    }
    let s = d / (1.0 - (2.0 * l - 1.0).abs());
    let h = if max == r {
        (g - b) / d % 6.0
    } else if max == g {
        (b - r) / d + 2.0
    } else {
        (r - g) / d + 4.0
    };
    ((h * 60.0 + 360.0) % 360.0, s, l)
}

fn from_hsl(h: f64, s: f64, l: f64) -> Rgb8 {
    let c = (1.0 - (2.0 * l - 1.0).abs()) * s;
    let x = c * (1.0 - (h / 60.0 % 2.0 - 1.0).abs());
    let m = l - c / 2.0;
    let (r, g, b) = if h < 60.0 {
        (c, x, 0.0)
    } else if h < 120.0 {
        (x, c, 0.0)
    } else if h < 180.0 {
        (0.0, c, x)
    } else if h < 240.0 {
        (0.0, x, c)
    } else if h < 300.0 {
        (x, 0.0, c)
    } else {
        (c, 0.0, x)
    };
    let channel = |v: f64| (v.clamp(0.0, 1.0) * 255.0).round_ties_even() as u8;
    [channel(r + m), channel(g + m), channel(b + m)]
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::color::{from_u32, to_u32};

    /// Papers and the ink master's C# `GraphicStickyNote.GetInk` chose for them, captured from the
    /// C# build before the rule moved here: the four shipped papers, dark and grey ones, translucent
    /// ones, and papers where no shade of the hue reaches the contrast and the ink falls back.
    const VECTORS: &[(u32, u32)] = &[
        (0xFFFFF1B8, 0x594A0D), // butter
        (0xFFFBD5E2, 0x86133A), // rose
        (0xFFCDEFDF, 0x1B553A), // mint
        (0xFFD3E6FD, 0x134786), // sky
        (0xFF302060, 0xD2C9ED),
        (0xFFFFFFFF, 0x525252),
        (0xFF000000, 0xDBDBDB),
        (0xFF808080, 0x000000),
        (0xFF777777, 0x000000),
        (0xFFFF0000, 0x000000),
        (0xFF00FF00, 0x0B500B),
        (0xFF0000FF, 0xEDEDFC),
        (0xFFFFFF00, 0x50500B),
        (0xFF3B97D2, 0x000000),
        (0xFF1E1E1E, 0xDBDBDB),
        (0xFFB0B0B0, 0x242424),
        (0x80FFF1B8, 0x62520E),
        (0x00000000, 0x525252),
        (0x40302060, 0x3D297A),
        (0xC0FF00FF, 0x120312),
        (0xFF6A5ACD, 0xFFFFFF),
        (0xFF2E8B57, 0x000000),
        (0xFFA0522D, 0xFFFFFF),
        (0xFF8B0000, 0xF9D2D2),
        (0xFFF5F5DC, 0x4F4F17),
        (0xFF0B0B0B, 0xDBDBDB),
        (0xFF7F7F7F, 0x000000),
        (0xFF858585, 0x000000),
    ];

    #[test]
    fn ink_matches_the_csharp_rule_it_replaced() {
        for &(paper, ink) in VECTORS {
            assert_eq!(to_u32(note_ink(paper)), ink, "paper 0x{paper:08X}");
        }
    }

    #[test]
    fn a_shade_found_reaches_the_contrast_and_keeps_the_hue() {
        for paper in [0xFFFFF1B8, 0xFFFBD5E2, 0xFFCDEFDF, 0xFFD3E6FD, 0xFF302060] {
            let ground = relative_luminance(from_u32(paper));
            let ink = note_ink(paper);
            assert!(
                contrast_ratio(relative_luminance(ink), ground) >= NOTE_INK_CONTRAST,
                "paper 0x{paper:08X}"
            );
            let hue = |c: Rgb8| to_hsl(c).0;
            assert!((hue(ink) - hue(from_u32(paper))).abs() < 12.0, "paper 0x{paper:08X}");
        }
    }

    #[test]
    fn contrast_ratio_spans_one_to_twenty_one() {
        assert_eq!(contrast_ratio(1.0, 0.0), 21.0);
        assert_eq!(contrast_ratio(0.0, 1.0), 21.0);
        assert_eq!(contrast_ratio(0.4, 0.4), 1.0);
    }

    #[test]
    fn translucent_papers_are_judged_over_white() {
        assert_eq!(over_white([0, 0, 0], 0), [255, 255, 255]);
        assert_eq!(over_white([0, 0, 0], 255), [0, 0, 0]);
        assert_eq!(over_white([0, 0, 0], 0x80), [127, 127, 127]);
    }
}
