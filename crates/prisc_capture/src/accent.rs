//! The accent colour maths, mirrored from the shell's `AccentColors`
//! (clowd_ui/Clowd.Shared/AccentColors.cs) so that the overlay and the C#
//! windows beside it derive the same colours from the same pick.
//!
//! The shell passes the accent exactly as the user chose it
//! (`--accent-color`); everything that paints with it is derived here:
//!
//! - [`ensure_contrast_with_white`]: the fill the panel's buttons and the
//!   selection chrome wear, darkened until the white labels drawn on it are
//!   readable (WCAG AA, issue #48) unless `--no-accent-contrast` is passed.
//! - [`comet_colors`]: the hint comet's body and head, which carry no white
//!   ink and are derived from the pick itself, in OKLCH.
//!
//! Both are ported line for line, in `f64`, with the same 8-bit quantisation,
//! and the tests pin values the C# tests pin too — a drift between the two
//! would paint a capturer beside a recording toolbar in a near-miss colour.

/// The legacy "clowd blue": the shell's stored default pick, and so the
/// default `--accent-color`.
pub const CLOWD_BLUE: [u8; 3] = [0x3B, 0x97, 0xD2];

/// WCAG AA for normal text (4.5:1), measured against the white glyphs drawn
/// on top of the accent.
pub const MINIMUM_CONTRAST_WITH_WHITE: f64 = 4.5;

/// OKLab lightness the comet's body is held between. The band straddles the
/// graphite chip and its dark shadow, so a body below the floor sinks into
/// them; above the ceiling it washes out to a pastel.
pub const COMET_MIN_LIGHTNESS: f64 = 0.62;
pub const COMET_MAX_LIGHTNESS: f64 = 0.72;

/// WCAG relative luminance (0 = black, 1 = white).
pub fn relative_luminance(c: [u8; 3]) -> f64 {
    0.2126 * to_linear(c[0]) + 0.7152 * to_linear(c[1]) + 0.0722 * to_linear(c[2])
}

/// WCAG contrast ratio between this colour and white: 1 for white itself, 21
/// for black.
#[cfg(test)]
pub fn contrast_with_white(c: [u8; 3]) -> f64 {
    1.05 / (relative_luminance(c) + 0.05)
}

/// `c` darkened just enough to reach [`MINIMUM_CONTRAST_WITH_WHITE`] against
/// white, or unchanged when it is dark enough already. Only the brightness
/// moves: luminance is linear in the linear-light channels, so scaling all
/// three by one factor scales it by exactly that factor and keeps the hue.
pub fn ensure_contrast_with_white(c: [u8; 3]) -> [u8; 3] {
    let target = (1.05 / MINIMUM_CONTRAST_WITH_WHITE - 0.05).max(0.0);
    let luminance = relative_luminance(c);
    if luminance <= target {
        return c;
    }
    let scale = target / luminance;
    c.map(|v| encode_floor(to_linear(v) * scale))
}

/// The hint comet's two colours, derived from the accent as the user picked
/// it: the body, and the hot tint its head burns towards. Worked in OKLCH so
/// "lighter" moves only perceived lightness and keeps the user's hue; the body
/// keeps as much of the pick's chroma as sRGB can hold at the clamped
/// lightness, and the head is the same hue, lighter, with chroma eased off.
pub fn comet_colors(picked: [u8; 3]) -> ([u8; 3], [u8; 3]) {
    let (l, c, h) = to_oklch(picked);
    let body_l = l.clamp(COMET_MIN_LIGHTNESS, COMET_MAX_LIGHTNESS);
    let body = from_oklch(body_l, c, h);
    let head = from_oklch((body_l + 0.1).min(0.95), c * 0.6, h);
    (body, head)
}

/// OKLCH (lightness 0..1, chroma, hue in radians) of an sRGB colour.
pub fn to_oklch(c: [u8; 3]) -> (f64, f64, f64) {
    let [r, g, b] = c.map(to_linear);
    let l = (0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b).cbrt();
    let m = (0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b).cbrt();
    let s = (0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b).cbrt();
    let ll = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
    let aa = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
    let bb = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
    (ll, aa.hypot(bb), bb.atan2(aa))
}

/// The sRGB colour at OKLCH (`l`, `c`, `h`). Out of gamut, the chroma is
/// reduced — never the lightness or the hue — until it fits.
pub fn from_oklch(l: f64, c: f64, h: f64) -> [u8; 3] {
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

fn oklch_to_linear(l: f64, c: f64, h: f64) -> Option<[f64; 3]> {
    const EPS: f64 = 1e-4;
    let rgb = oklch_to_linear_unchecked(l, c, h);
    rgb.iter()
        .all(|v| (-EPS..=1.0 + EPS).contains(v))
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

/// Rounds *down*: quantisation must never push a contrast-corrected colour
/// back above its target luminance.
fn encode_floor(linear: f64) -> u8 {
    encode(linear).floor().clamp(0.0, 255.0) as u8
}

fn encode_round(linear: f64) -> u8 {
    encode(linear).round().clamp(0.0, 255.0) as u8
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The value the shell's own default lands on (AccentColors.Default):
    /// both sides correct the same stored pick to the same fill.
    #[test]
    fn clowd_blue_corrects_to_the_shells_default() {
        assert_eq!(ensure_contrast_with_white(CLOWD_BLUE), [0x2F, 0x7C, 0xAE]);
    }

    #[test]
    fn contrast_correction_leaves_dark_colours_alone_and_reaches_aa_otherwise() {
        assert_eq!(ensure_contrast_with_white([0, 0, 0x80]), [0, 0, 0x80]);
        for c in [[0xFF, 0xFF, 0xFF], [0xFF, 0xFF, 0x00], [0xF0, 0xC0, 0xF4], CLOWD_BLUE] {
            let fixed = ensure_contrast_with_white(c);
            let ratio = contrast_with_white(fixed);
            assert!(
                (MINIMUM_CONTRAST_WITH_WHITE..MINIMUM_CONTRAST_WITH_WHITE + 0.1).contains(&ratio),
                "{fixed:?}: {ratio}"
            );
        }
    }

    /// Pinned in AccentColorTests.CometColors_MatchTheCapturer too.
    #[test]
    fn comet_colors_match_the_shell() {
        assert_eq!(comet_colors(CLOWD_BLUE), ([0x3B, 0x97, 0xD2], [0x83, 0xB4, 0xD8]));
        assert_eq!(comet_colors([0x7A, 0x1F, 0xA2]), ([0xAE, 0x59, 0xDA], [0xBE, 0x8E, 0xDA]));
    }

    #[test]
    fn oklch_round_trips() {
        for c in [CLOWD_BLUE, [0x12, 0x34, 0x56], [0xFF, 0x80, 0x00]] {
            let (l, ch, h) = to_oklch(c);
            assert_eq!(from_oklch(l, ch, h), c);
        }
    }
}
