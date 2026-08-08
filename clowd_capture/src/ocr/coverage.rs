//! Per-line choice between the two OCR presentations: a re-rendered text
//! bubble (real glyphs, styled like the hint pills) or the original
//! pixel-crop lift.
//!
//! The overlay's glyphon `FontSystem` is built from a curated DB containing
//! ONLY the embedded Cascadia faces (`ui::gpu::text`) — deliberately no
//! system fonts, because the overlay is startup-latency-sensitive. That
//! means re-rendered glyphs have no CJK/Cyrillic/Greek/etc. coverage and
//! would draw as tofu boxes. Rather than load fonts, each recognized line
//! is classified ONCE when the outcome lands (never per frame): lines the
//! embedded fonts can cover become bubbles, everything else falls back to
//! the pixel-crop lift, which is correct for every script by construction.
//!
//! The check is a plain codepoint-range scan, not a shaping pass: false
//! negatives only cost a line the fancier presentation, while a false
//! positive would put tofu on screen — so the whitelist is deliberately
//! conservative and every range in it is verified against the actual
//! embedded font bytes by the shaping test at the bottom of this file.

use crate::ocr::OcrLine;

/// How one recognized line is presented while lifted.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LinePresentation {
    /// Rounded pill containing the recognized string as real glyphs.
    Bubble,
    /// The original lift: a crop of the desktop snapshot texture.
    PixelCrop,
    /// Drawn not at all. Used for fallback-only lines when the recognition
    /// ran against a locked-peek COMPOSITE: the pixel crop samples the raw
    /// desktop snapshot texture, which does not contain the peeked window,
    /// so lifting it would animate the OBSCURING window's pixels. Bubbles
    /// never have that problem — they render recognized glyphs — which is
    /// why only the fallback lines are suppressed.
    Hidden,
}

/// Codepoint ranges the embedded Cascadia faces are trusted to cover.
///
/// Conservative on purpose (see module docs). Each range is pinned by
/// `whitelist_is_actually_covered_by_the_embedded_fonts` below, which
/// shapes every listed codepoint against the real font bytes — extend the
/// list and that test is what proves the extension safe.
const COVERED_RANGES: &[(u32, u32)] = &[
    (0x0020, 0x007E), // ASCII printable
    (0x00A0, 0x00FF), // Latin-1 Supplement (accents, °, ©, ±, …)
    (0x0100, 0x017F), // Latin Extended-A (Œ, š, ż, …)
    // General Punctuation, minus the holes the shaping test found in the
    // embedded faces (U+2012, U+2016, U+201F, U+2023, U+2025, U+2027 —
    // figure dash, double bar, reversed quote, triangular bullet, dot
    // leaders): all vanishingly rare in OCR'd screen text, so excluding
    // them costs those lines nothing but the fancier presentation.
    (0x2010, 0x2011), // hyphens
    (0x2013, 0x2015), // en/em dash, horizontal bar
    (0x2017, 0x201E), // low line, curly single/double quotes
    (0x2020, 0x2022), // daggers, bullet
    (0x2026, 0x2026), // ellipsis
    (0x20AC, 0x20AC), // €
    (0x2122, 0x2122), // ™
];

/// Whether every glyph of `text` is inside the trusted ranges (whitespace
/// always passes — it renders as advance, not ink). Empty/whitespace-only
/// text is NOT bubble-capable: an empty pill floating over the page would
/// be pure noise, while the pixel crop of whatever the engine saw there is
/// at least honest.
pub fn bubble_capable(text: &str) -> bool {
    let mut any_ink = false;
    for ch in text.chars() {
        if ch.is_whitespace() {
            continue;
        }
        any_ink = true;
        let cp = ch as u32;
        if !COVERED_RANGES
            .iter()
            .any(|&(lo, hi)| (lo..=hi).contains(&cp))
        {
            return false;
        }
    }
    any_ink
}

/// Classify every line of an outcome, exactly once, at the moment the
/// outcome lands on the app thread. The result travels inside `OcrState`
/// so no render worker ever re-derives it per frame.
///
/// `pixel_source_valid` is false when the recognition ran against a
/// locked-peek composite — see [`LinePresentation::Hidden`].
pub fn classify_lines(lines: &[OcrLine], pixel_source_valid: bool) -> Vec<LinePresentation> {
    lines
        .iter()
        .map(|line| {
            if bubble_capable(&line.text) {
                LinePresentation::Bubble
            } else if pixel_source_valid {
                LinePresentation::PixelCrop
            } else {
                LinePresentation::Hidden
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use clowd_rust_core::geometry::{RectExt, ScreenRectF};

    fn line(text: &str) -> OcrLine {
        OcrLine {
            text: text.to_string(),
            rect: ScreenRectF::from_xy_size(0.0, 0.0, 10.0, 10.0),
        }
    }

    #[test]
    fn plain_latin_is_bubble_capable() {
        assert!(bubble_capable("Hello, world!"));
        assert!(bubble_capable("Prix: 42,50 € — «café» ™"));
        assert!(bubble_capable("naïve façade Œuvre"));
        assert!(bubble_capable("curly “quotes” and … ellipsis"));
    }

    #[test]
    fn uncovered_scripts_fall_back() {
        assert!(!bubble_capable("日本語のテキスト"));
        assert!(!bubble_capable("Привет мир"));
        assert!(!bubble_capable("Ελληνικά"));
        assert!(!bubble_capable("مرحبا"));
        // One stray CJK char poisons the whole line — a half-tofu bubble
        // is worse than a pixel crop.
        assert!(!bubble_capable("mixed 日本 latin"));
        // Symbols outside the pinned ranges (arrows, math, emoji).
        assert!(!bubble_capable("a → b"));
        assert!(!bubble_capable("🎉"));
    }

    #[test]
    fn empty_and_whitespace_lines_are_not_bubbles() {
        assert!(!bubble_capable(""));
        assert!(!bubble_capable("   \t "));
    }

    #[test]
    fn classify_maps_capability_and_peek_suppression() {
        let lines = [line("hello"), line("Привет"), line("")];

        let normal = classify_lines(&lines, true);
        assert_eq!(
            normal,
            vec![LinePresentation::Bubble, LinePresentation::PixelCrop, LinePresentation::PixelCrop]
        );

        // Locked peek: bubbles survive (they render glyphs, not texture
        // samples), fallback lines vanish rather than lift the wrong
        // window's pixels.
        let peeked = classify_lines(&lines, false);
        assert_eq!(
            peeked,
            vec![LinePresentation::Bubble, LinePresentation::Hidden, LinePresentation::Hidden]
        );
    }

    /// The load-bearing test: every codepoint the whitelist trusts must
    /// have a real glyph in the EMBEDDED font bytes. Shapes the whole
    /// whitelist through the same curated fontdb the overlay builds
    /// (`ui::gpu::text::TextStack`), with no system fonts, and fails on
    /// any .notdef (glyph id 0) — which is exactly what tofu is.
    #[test]
    fn whitelist_is_actually_covered_by_the_embedded_fonts() {
        use crate::ui::gpu::text::{FAMILY_CODE, FONT_CODE_BOLD, FONT_CODE_REGULAR, FONT_MONO_BOLD, FONT_MONO_REGULAR};
        use glyphon::{Attrs, Buffer, Family, FontSystem, Metrics, Shaping, Wrap};

        let mut db = glyphon::fontdb::Database::new();
        db.load_font_data(FONT_MONO_REGULAR.to_vec());
        db.load_font_data(FONT_MONO_BOLD.to_vec());
        db.load_font_data(FONT_CODE_REGULAR.to_vec());
        db.load_font_data(FONT_CODE_BOLD.to_vec());
        let mut fs = FontSystem::new_with_locale_and_db("en-US".to_string(), db);

        let mut text = String::new();
        for &(lo, hi) in COVERED_RANGES {
            for cp in lo..=hi {
                if let Some(ch) = char::from_u32(cp) {
                    if !ch.is_whitespace() && !ch.is_control() {
                        text.push(ch);
                        text.push('\n'); // one char per line: no ligature/merge ambiguity
                    }
                }
            }
        }

        let mut buffer = Buffer::new(&mut fs, Metrics::new(16.0, 20.0));
        buffer.set_wrap(Wrap::None);
        buffer.set_text(&text, &Attrs::new().family(Family::Name(FAMILY_CODE)), Shaping::Advanced, None);
        buffer.shape_until_scroll(&mut fs, false);

        let mut checked = 0usize;
        let mut missing: Vec<String> = Vec::new();
        for run in buffer.layout_runs() {
            for glyph in run.glyphs {
                if glyph.glyph_id == 0 {
                    missing.push(format!(
                        "U+{:04X}",
                        run.text[glyph.start..glyph.end]
                            .chars()
                            .next()
                            .unwrap() as u32
                    ));
                }
                checked += 1;
            }
        }
        assert!(
            missing.is_empty(),
            "whitelisted codepoints shape to .notdef (tofu): {}",
            missing.join(" ")
        );
        // Belt-and-braces: an accidentally-empty buffer must not pass.
        assert!(checked > 300, "only {checked} glyphs shaped");
    }
}
