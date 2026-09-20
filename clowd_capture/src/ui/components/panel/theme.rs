//! The one place the tray's design tokens become an egui `Style` and the
//! frames drawn from it. The font families themselves live in
//! [`crate::ui::fonts`], which every context loads before this applies a
//! style over them.
//!
//! The values mirror the C# `TrayTokens` the floating strips share, so the
//! Rust overlay and the WPF windows keep looking like one product. Nothing
//! else in the panel hard-codes a colour, a radius or a font size.

use std::sync::Arc;

use egui::{Color32, FontFamily, FontId, Margin, Shadow, TextStyle};

use super::model::GroupTone;

pub mod tokens {
    use egui::{vec2, Color32, CornerRadius, Shadow, Stroke, Vec2};

    /// Premultiplied white at `a`: `Color32::from_white_alpha` is not a
    /// `const fn`, and on white every premultiplied channel equals the
    /// alpha.
    const fn white_alpha(a: u8) -> Color32 {
        Color32::from_rgba_premultiplied(a, a, a, a)
    }

    /// Chassis fill, `#25272B`.
    pub const TRAY_FILL: Color32 = Color32::from_rgb(0x25, 0x27, 0x2B);
    /// Secondary group fill, `#3A3E44`.
    pub const SEG_FILL: Color32 = Color32::from_rgb(0x3A, 0x3E, 0x44);
    /// The hairline ring just inside the chassis edge, baked opaque so
    /// it never shows the desktop through it. The C# strips paint
    /// `#3B3D40` (`TRAY_FILL` + 10 % white) on a pixel row of its own;
    /// egui feathers a 1 pt stroke across two half-covered rows, so the
    /// same colour read as half the step. `#515255` (`TRAY_FILL` + 20 %)
    /// lands the same visible edge.
    pub const RING: Stroke = Stroke {
        width: 1.0,
        color: Color32::from_rgb(0x51, 0x52, 0x55),
    };
    /// `ShadowCompact`: `0 3 10 #59000000`.
    pub const SHADOW: Shadow = Shadow {
        offset: [0, 3],
        blur: 10,
        spread: 0,
        color: Color32::from_black_alpha(89),
    };
    /// Corner radius of the chassis and of every item box.
    pub const RADIUS: CornerRadius = CornerRadius::same(8);
    /// Inset from the tray edge to its first item. The ring is part of the
    /// frame's margin, so `tray_frame` subtracts its width from this.
    pub const PAD: f32 = 4.0;
    /// Between items inside the tray.
    pub const GAP: f32 = 4.0;
    /// A button is never shorter than this along a row.
    pub const BUTTON_MIN_LENGTH: f32 = 40.0;
    /// The emblem's slot along the strip.
    pub const EMBLEM_SLOT: f32 = 40.0;
    /// The emblem mark, centred in its slot.
    pub const EMBLEM_MARK: f32 = 32.0;
    /// Button icon cell.
    pub const ICON: f32 = 20.0;
    pub const ICON_SIZE: Vec2 = vec2(ICON, ICON);
    /// Clearance kept from the monitor edge in the placement fit test.
    pub const NEAR_MIN: f32 = 2.0;
    /// Gap between the selection and the chassis.
    pub const NEAR_MAX: f32 = 15.0;
    /// Hover fade, linear, in and out.
    pub const HOVER_FADE_SECS: f32 = 0.18;
    /// Hover veil on a grey group: white at 12 % over the group fill
    /// (the C# strips' `WhiteVeil12`).
    pub const HOVER_VEIL: f32 = 0.12;
    /// Hover veil on the accent group: white at 28 %. The accent is a
    /// saturated mid tone, so the grey group's 12 % step reads as barely
    /// a change on it.
    pub const HOVER_VEIL_PRIMARY: f32 = 0.28;

    pub const FG: Color32 = Color32::WHITE;
    /// White text at 85 %: the readout's number.
    pub const FG_85: Color32 = white_alpha(217);
    /// White text at 80 %: the accelerator letter beside a `key`-style
    /// icon.
    pub const FG_80: Color32 = white_alpha(204);
    /// White text at 70 %: the readout's second line (the × sign or the
    /// "words" caption).
    pub const FG_70: Color32 = white_alpha(179);

    /// `key` style: a 40 pt square holding the icon and the accelerator
    /// letter side by side.
    pub const KEY_TILE: f32 = 40.0;
    pub const KEY_PAD_H: f32 = 4.0;
    pub const KEY_GAP: f32 = 3.0;
    /// 9 pt accelerator letter in the `key` button style.
    pub const KEY_FONT: f32 = 9.0;

    /// `below` style: icon over the label, 48 pt tall.
    pub const BELOW_HEIGHT: f32 = 48.0;
    pub const BELOW_PAD_H: f32 = 8.0;
    pub const BELOW_GAP: f32 = 4.0;
    /// 12 pt button label in the `below` button style.
    pub const LABEL_FONT: f32 = 12.0;

    /// 11 pt "W / × / H" readout, set on 1 em line boxes so the three
    /// lines sit tight.
    pub const READOUT_FONT: f32 = 11.0;
    pub const READOUT_LINE: f32 = 11.0;
    pub const READOUT_PAD_H: f32 = 4.0;

    /// The hover tooltip on a `key`-style button (the C# strips' tip chip,
    /// spec §10): `#F2141619`, white 11 pt text, padding 8 × 4, radius 6,
    /// shown after 350 ms, 7 pt below a row / 8 pt beside a column.
    pub const TIP_FILL: Color32 = Color32::from_rgba_unmultiplied_const(0x14, 0x16, 0x19, 0xF2);
    pub const TIP_FONT: f32 = 11.0;
    pub const TIP_PAD_H: f32 = 8.0;
    pub const TIP_PAD_V: f32 = 4.0;
    pub const TIP_RADIUS: f32 = 6.0;
    pub const TIP_GAP_BOTTOM: f32 = 7.0;
    pub const TIP_GAP_RIGHT: f32 = 8.0;
    pub const TIP_DELAY_SECS: f64 = 0.35;

    /// 12 pt debug-panel body.
    pub const DEBUG_FONT: f32 = 12.0;
    /// Row pitch of the debug body: the C++ panel's 1.4 line spacing.
    pub const DEBUG_ROW: f32 = DEBUG_FONT * 1.4;
    /// Distance from the monitor edge to a debug panel.
    pub const DEBUG_MARGIN: f32 = 50.0;
    /// Width cap on a debug panel's content area.
    pub const DEBUG_MAX_W: f32 = 600.0;
    /// Inner padding on all four sides of a debug panel.
    pub const DEBUG_PAD: f32 = 20.0;
    /// Sparkline height, and the legend strip beneath it.
    pub const DEBUG_GRAPH_H: f32 = 60.0;
    pub const DEBUG_LEGEND_H: f32 = 16.0;
    /// Debug-panel body fill: black at 70 %.
    pub const DEBUG_FILL: Color32 = Color32::from_black_alpha(179);
    /// Frame-time plot area: white at 4 %.
    pub const DEBUG_GRAPH_BG: Color32 = white_alpha(10);
    /// The refresh-rate reference line across the plot: white at 35 %.
    pub const DEBUG_BUDGET_LINE: Color32 = white_alpha(89);
    /// The plot's annotations: `max(floor(DEBUG_FONT * 0.85), 9)`.
    pub const DEBUG_LABEL_FONT: f32 = 10.0;
}

/// `TextStyle::Name` holds an `Arc`, so the readout style cannot be a
/// `const`.
fn readout_style() -> TextStyle {
    TextStyle::Name(Arc::from("readout"))
}

/// Apply the tray's style to one context. Called once per context, at
/// construction.
pub fn apply_style(ctx: &egui::Context) {
    use tokens::*;
    // The text styles go in the literal rather than in an assignment
    // below: clippy rejects a field reassignment straight after a
    // `Default::default()` binding.
    let mut s = egui::Style {
        text_styles: [
            (TextStyle::Small, FontId::new(KEY_FONT, FontFamily::Monospace)),
            (TextStyle::Body, FontId::new(LABEL_FONT, FontFamily::Monospace)),
            (TextStyle::Button, FontId::new(LABEL_FONT, FontFamily::Monospace)),
            (TextStyle::Heading, FontId::new(LABEL_FONT, FontFamily::Monospace)),
            (TextStyle::Monospace, FontId::new(DEBUG_FONT, FontFamily::Monospace)),
            (readout_style(), FontId::new(READOUT_FONT, FontFamily::Monospace)),
        ]
        .into(),
        ..Default::default()
    };
    s.spacing.item_spacing = egui::vec2(GAP, GAP);
    s.spacing.button_padding = egui::Vec2::ZERO;
    s.spacing.interact_size = egui::Vec2::ZERO;
    // An exact dead zone: the default 5 pt interact radius bleeds clicks
    // to widgets the pointer is not over.
    s.interaction.interact_radius = 0.0;
    s.interaction.selectable_labels = false;
    s.animation_time = HOVER_FADE_SECS;
    // The tray's 0.8 pt steps at 125 % are off egui's 1/32 grid, so the
    // orange "unaligned" markers would be permanent noise.
    s.debug.show_unaligned = false;
    // Struct update rather than a full literal: a full `Visuals { .. }`
    // would have to name the deprecated `clip_rect_margin`.
    s.visuals = egui::Visuals {
        override_text_color: Some(FG),
        button_frame: false,
        interact_cursor: None,
        window_shadow: Shadow::NONE,
        ..egui::Visuals::dark()
    };
    ctx.set_global_style(s);
}

/// The tray chassis: fill, hairline ring, radius and the compact shadow.
/// The ring's width is part of the frame's margin, so the inner margin is
/// `PAD` minus it and the edge-to-first-item distance stays `PAD`.
pub fn tray_frame() -> egui::Frame {
    egui::Frame::new()
        .fill(tokens::TRAY_FILL)
        .stroke(tokens::RING)
        .corner_radius(tokens::RADIUS)
        .inner_margin(Margin::same((tokens::PAD - tokens::RING.width) as i8))
        .shadow(tokens::SHADOW)
}

/// A group's fill for its tone: the accent for the primary group, the
/// segment grey for the rest.
pub fn group_fill(tone: GroupTone, accent: Color32) -> Color32 {
    match tone {
        GroupTone::Primary => accent,
        GroupTone::Secondary => tokens::SEG_FILL,
    }
}

/// A group's chassis: its fill, the item radius, no margin — the buttons
/// sit flush inside it and the hovered one paints its own veil over it.
pub fn group_frame(fill: Color32) -> egui::Frame {
    egui::Frame::new()
        .fill(fill)
        .corner_radius(tokens::RADIUS)
}

/// How far a hovered button in a group of this tone lightens toward
/// white.
pub fn hover_veil(tone: GroupTone) -> f32 {
    match tone {
        GroupTone::Primary => tokens::HOVER_VEIL_PRIMARY,
        GroupTone::Secondary => tokens::HOVER_VEIL,
    }
}

/// A button's fill at hover phase `t` over its group's `base`: the base
/// lightened toward white by `veil * t` in gamma space, which is what the
/// old rect shader's `lighten` lane did. One opaque rect, not a
/// translucent one over the group: at `t = 0` it is the group colour
/// exactly, so its anti-aliased corners vanish into the group, and a
/// second translucent rect would feather the fringe twice.
pub fn hover_fill(base: Color32, veil: f32, t: f32) -> Color32 {
    let k = veil * t.clamp(0.0, 1.0);
    let mix = |c: u8| (c as f32 + (255.0 - c as f32) * k).round() as u8;
    Color32::from_rgb(mix(base.r()), mix(base.g()), mix(base.b()))
}

/// A debug panel's body: no border, a dark translucent fill and even
/// padding on all four sides.
pub fn debug_frame() -> egui::Frame {
    egui::Frame::NONE
        .fill(tokens::DEBUG_FILL)
        .inner_margin(Margin::same(tokens::DEBUG_PAD as i8))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn styled() -> egui::Context {
        let ctx = egui::Context::default();
        ctx.set_fonts(crate::ui::fonts::font_definitions(&[]));
        apply_style(&ctx);
        ctx
    }

    #[test]
    fn every_text_style_the_panel_uses_resolves() {
        let ctx = styled();
        let style = ctx.global_style();
        for s in [
            TextStyle::Small,
            TextStyle::Body,
            TextStyle::Button,
            TextStyle::Heading,
            TextStyle::Monospace,
            readout_style(),
        ] {
            let font = s.resolve(&style);
            assert_eq!(font.family, FontFamily::Monospace, "{s:?}");
        }
    }

    #[test]
    fn token_font_sizes() {
        let ctx = styled();
        let style = ctx.global_style();
        let size = |s: TextStyle| s.resolve(&style).size;
        assert_eq!(size(TextStyle::Small), 9.0);
        assert_eq!(size(TextStyle::Body), 12.0);
        assert_eq!(size(TextStyle::Button), 12.0);
        assert_eq!(size(TextStyle::Heading), 12.0);
        assert_eq!(size(TextStyle::Monospace), 12.0);
        assert_eq!(size(readout_style()), 11.0);
    }

    #[test]
    fn hover_fill_mixes_toward_white_in_gamma_space() {
        let accent = Color32::from_rgb(0x2F, 0x7C, 0xAE);
        for (base, veil) in [(tokens::SEG_FILL, tokens::HOVER_VEIL), (accent, tokens::HOVER_VEIL_PRIMARY)] {
            assert_eq!(hover_fill(base, veil, 0.0), base);
            let hot = hover_fill(base, veil, 1.0);
            let expect = |c: u8| (c as f32 + (255.0 - c as f32) * veil).round() as u8;
            assert_eq!((hot.r(), hot.g(), hot.b()), (expect(base.r()), expect(base.g()), expect(base.b())));
        }
    }

    /// The accent hover has to be a bigger step than the grey one: 12 %
    /// white on a saturated mid tone was invisible on screen.
    #[test]
    fn primary_hover_veil_is_stronger_than_secondary() {
        assert!(hover_veil(GroupTone::Primary) > hover_veil(GroupTone::Secondary));
        assert_eq!(hover_veil(GroupTone::Primary), tokens::HOVER_VEIL_PRIMARY);
        assert_eq!(hover_veil(GroupTone::Secondary), tokens::HOVER_VEIL);
    }

    #[test]
    fn group_fill_is_the_accent_for_primary_and_grey_otherwise() {
        let accent = Color32::from_rgb(0x2F, 0x7C, 0xAE);
        assert_eq!(group_fill(GroupTone::Primary, accent), accent);
        assert_eq!(group_fill(GroupTone::Secondary, accent), tokens::SEG_FILL);
    }

    /// The buttons are flush with the group edge: any margin here would
    /// leave a rim of group colour the hover veil never covers, and would
    /// shift the analytic strip size `show` computes without it.
    #[test]
    fn group_frame_has_no_margin() {
        assert_eq!(group_frame(tokens::SEG_FILL).total_margin(), Margin::ZERO.into());
    }

    /// The ring is part of the frame's margin, so the distance from the
    /// painted edge to the first item must still be the token's 4 pt.
    #[test]
    fn tray_frame_pad_is_four() {
        assert_eq!(tray_frame().total_margin().left, tokens::PAD);
    }

    #[test]
    fn style_knobs() {
        let ctx = styled();
        let style = ctx.global_style();
        assert_eq!(style.animation_time, tokens::HOVER_FADE_SECS);
        assert_eq!(style.interaction.interact_radius, 0.0);
        assert!(!style.interaction.selectable_labels);
        assert!(!style.debug.show_unaligned);
    }
}
