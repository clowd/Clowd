//! The shipped accent, the contrast rules and the OKLCH limits both sides read (C#: `SharedDefaults`).

use super::Rgb;

crate::contract! {
    /// The accent the app ships with, the legacy "prisc blue" (sRGB 0x00RRGGBB): the stored default
    /// pick, and the capture overlay's default --accent-color.
    pub const PRISC_BLUE: Rgb = Rgb(0x3B97D2);
    /// WCAG AA for normal text (4.5:1), measured against the white glyphs drawn on top of the accent.
    pub const MINIMUM_CONTRAST_WITH_WHITE: f64 = 4.5;
    /// The shipped accent after the contrast rule (sRGB 0x00RRGGBB): prisc blue taken down to the
    /// minimum contrast with white.
    computed DEFAULT_ACCENT: Rgb = Rgb(crate::color::to_u32(crate::color::ensure_contrast_with_white(crate::color::PRISC_BLUE)));
    /// The WCAG relative luminance above which a colour wants dark ink rather than white: about
    /// where the two have equal contrast on it.
    pub const LIGHT_LUMINANCE: f64 = 0.18;
    /// The contrast a sticky note's ink reaches against its paper (WCAG AAA for body text).
    pub const NOTE_INK_CONTRAST: f64 = 7.0;
    /// The lowest OKLab lightness of the hint comet's body. The band straddles the graphite tray
    /// (L about 0.27) and its dark shadow, so a body below the floor sinks into them.
    pub const COMET_MIN_LIGHTNESS: f64 = 0.62;
    /// The highest OKLab lightness of the hint comet's body: above it there is too little room left
    /// for chroma and the body washes out to a pastel.
    pub const COMET_MAX_LIGHTNESS: f64 = 0.72;
    /// The OKLCH chroma scale's top, as oklch.com draws it: just past the most saturated colour P3
    /// can show (sRGB tops out near 0.32, at a deep blue).
    pub const OKLCH_CHROMA_MAX: f64 = 0.37;
    /// OKLCH chroma below this is a grey as far as 8-bit sRGB can tell, and its hue is noise.
    pub const OKLCH_ACHROMATIC_CHROMA: f64 = 2e-4;
}
