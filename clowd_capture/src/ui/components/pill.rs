//! The chip style the three pill overlays share.
//!
//! Hint chips, the OCR notice and the OCR text bubbles all read as one
//! family — same fill, border, radius, paddings and text colour — so the
//! values and the painters live here rather than in whichever overlay
//! happened to define them first. That is what keeps them from drifting
//! apart: nothing pulls a colour out of another overlay's module.
//!
//! Everything is in points. The old renderer multiplied each constant by
//! the monitor's DPI and floored it; egui does the pixel rounding itself,
//! so the literals below are the old pre-DPI numbers used as-is.

use egui::epaint::RectShape;
use egui::{pos2, vec2, Align2, Color32, FontFamily, FontId, Painter, Rect, Stroke, StrokeKind, Vec2};

/// Chip body: white at 80 %.
pub const FILL: Color32 = Color32::from_rgba_unmultiplied_const(255, 255, 255, 204);
/// The hairline around it, light grey at the same alpha.
pub const BORDER: Color32 = Color32::from_rgba_unmultiplied_const(191, 191, 191, 204);
/// The hard-edged drop shadow under it.
pub const SHADOW: Color32 = Color32::from_black_alpha(102);
/// The one knob for text weight: the old glyph shader linearised 0x1A
/// before writing to a non-sRGB target, so the old text came out heavier
/// than nominal; egui draws 0x1A as 0x1A. Darken here if the visual pass
/// wants the old weight back.
pub const TEXT: Color32 = Color32::from_rgba_unmultiplied_const(0x1A, 0x1A, 0x1A, 0xE0);

// The keycap is a three-layer bevel: a light base whose bottom-right edge
// shows through as a highlight, a dark under-layer, and the face on top.
pub const KEYCAP_DARK: Color32 = Color32::from_rgba_unmultiplied_const(46, 46, 46, 230);
pub const KEYCAP_LIGHT: Color32 = Color32::from_rgba_unmultiplied_const(133, 133, 133, 230);
pub const KEYCAP_FACE: Color32 = Color32::from_rgba_unmultiplied_const(97, 97, 97, 230);
pub const KEYCAP_BORDER: Color32 = Color32::from_rgba_unmultiplied_const(15, 15, 15, 242);

pub const FONT_PT: f32 = 11.0;
pub const PAD_H: f32 = 6.0;
pub const PAD_V: f32 = 4.0;
pub const RADIUS: f32 = 6.0;
/// The old `dpi.ceil()` hairline, one point wide.
pub const BORDER_W: f32 = 1.0;
pub const SHADOW_OFFSET: f32 = 3.0;
pub const SHADOW_SPREAD: f32 = 2.0;
pub const KEYCAP: f32 = 20.0;
pub const KEYCAP_GAP: f32 = 5.0;
pub const KEYCAP_INSET: f32 = 3.0;
pub const KEYCAP_RADIUS: f32 = 4.0;
pub const KEYCAP_INNER_RADIUS: f32 = 3.0;
/// Gap between a chip's label and the colour swatch that follows it.
pub const SWATCH_GAP: f32 = 6.0;
/// Dash and gap length of the dashed cursor square.
pub const DASH: f32 = 6.0;

pub fn font() -> FontId {
    FontId::new(FONT_PT, FontFamily::Monospace)
}

/// The keycap letter, a shade larger than the label beside it.
pub fn key_font() -> FontId {
    FontId::new(FONT_PT * 1.15, FontFamily::Monospace)
}

/// The chip's shadow: its rect moved down-right and grown, with no blur —
/// the old one had none either.
pub fn shadow(rect: Rect, alpha: f32) -> RectShape {
    RectShape::filled(
        rect.translate(Vec2::splat(SHADOW_OFFSET))
            .expand(SHADOW_SPREAD),
        RADIUS + SHADOW_SPREAD,
        SHADOW.gamma_multiply(alpha),
    )
}

/// Fill plus the inside border. `border = false` for a chip wearing the
/// accent comet, which replaces the border rather than sitting beside it.
pub fn body(rect: Rect, radius: f32, alpha: f32, border: bool) -> RectShape {
    let stroke = if border {
        Stroke::new(BORDER_W, BORDER.gamma_multiply(alpha))
    } else {
        Stroke::NONE
    };
    RectShape::new(rect, radius, FILL.gamma_multiply(alpha), stroke, StrokeKind::Inside)
}

/// The three stacked rounded rects and the letter: the light base, the
/// dark under-layer, the face shifted up by one border width, and the
/// letter centred in the face and nudged back down by half of it.
pub fn keycap(p: &Painter, cap: Rect, letter: &str, alpha: f32) {
    p.rect(
        cap,
        KEYCAP_RADIUS,
        KEYCAP_LIGHT.gamma_multiply(alpha),
        Stroke::new(BORDER_W, KEYCAP_BORDER.gamma_multiply(alpha)),
        StrokeKind::Inside,
    );
    let face_min = pos2(cap.left() + KEYCAP_INSET, cap.top() + KEYCAP_INSET - BORDER_W);
    let dark = Rect::from_min_max(face_min, pos2(cap.right() - BORDER_W, cap.bottom() - BORDER_W));
    p.rect_filled(
        dark,
        ((KEYCAP_RADIUS - KEYCAP_INSET).max(1.0) + KEYCAP_INNER_RADIUS) * 0.5,
        KEYCAP_DARK.gamma_multiply(alpha),
    );
    let face = Rect::from_min_size(face_min, Vec2::splat(KEYCAP - 2.0 * KEYCAP_INSET));
    p.rect(
        face,
        KEYCAP_INNER_RADIUS,
        KEYCAP_FACE.gamma_multiply(alpha),
        Stroke::new(BORDER_W, KEYCAP_BORDER.gamma_multiply(alpha)),
        StrokeKind::Inside,
    );
    p.text(
        face.center() + vec2(0.0, BORDER_W / 2.0),
        Align2::CENTER_CENTER,
        letter,
        key_font(),
        Color32::WHITE.gamma_multiply(alpha),
    );
}
