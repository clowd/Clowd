//! The tray's four pieces as egui widgets: the two button styles, the
//! size readout and the emblem.
//!
//! Only the buttons are interactive. The readout and the emblem allocate
//! space and paint into it, so the tray body under them is dead in the
//! same way the retired composer's chassis was. A button has no fill of
//! its own at rest: it sits on its group's fill (`theme::group_frame`)
//! and paints a rounded veil in the group's colour lightened only while
//! hovered.

use egui::text::{LayoutJob, TextFormat};
use egui::{
    Align2, AtomExt as _, AtomLayout, Color32, Direction, Frame, Id, Image, ImageSource, Margin, Response, RichText, Sense, TextStyle, Ui,
    Vec2, WidgetText,
};

use super::assets;
use super::model::{ButtonDef, Readout};
use super::theme::{self, tokens};

/// An icon and a piece of text in a rounded segment. The two panel styles
/// are two parameter sets of this one widget: `key` lays them out side by
/// side in a square, `below` stacks them.
pub struct StackedButton {
    pub id: Id,
    pub icon: ImageSource<'static>,
    pub text: WidgetText,
    pub min_size: Vec2,
    pub direction: Direction,
    pub gap: f32,
    pub pad_h: f32,
    /// The fill of the group this button sits in: what the hover veil
    /// lightens, and what the button paints (invisibly) at rest.
    pub base: Color32,
    /// How far the hover lightens `base` (`theme::hover_veil`).
    pub veil: f32,
}

impl StackedButton {
    pub fn show(self, ui: &mut Ui) -> Response {
        // The loader rasterises the mark at the exact pixel size this
        // quad covers on this host, so it is 1:1 at every DPI. Never a
        // spinner: `assets::preload` has already loaded (or logged) it,
        // and the loader is synchronous anyway.
        let icon = Image::new(self.icon.clone())
            .fit_to_exact_size(tokens::ICON_SIZE)
            .show_loading_spinner(false)
            .atom_size(tokens::ICON_SIZE);
        let layout = AtomLayout::new((icon, self.text))
            .id(self.id)
            .direction(self.direction)
            .gap(self.gap)
            .sense(Sense::CLICK)
            .min_size(self.min_size)
            // The contents sit in the middle of the segment however much
            // room `min_size` leaves over, as the retired kit's tile and
            // button boxes did.
            .align2(Align2::CENTER_CENTER)
            .frame(
                Frame::new()
                    .inner_margin(Margin::symmetric(self.pad_h as i8, 0))
                    .corner_radius(tokens::RADIUS),
            );
        // Allocate first so the fill can be chosen from this pass's hover
        // state, then paint: one rect at the blended colour, never a
        // second translucent rect over the group.
        let mut allocated = layout.allocate(ui);
        let t = ui
            .ctx()
            .animate_bool_with_time(self.id.with("hover"), allocated.response.hovered(), tokens::HOVER_FADE_SECS);
        allocated.sized.frame.fill = theme::hover_fill(self.base, self.veil, t);
        allocated.paint(ui).response
    }
}

/// `key` style: a 40 pt square, the icon beside the accelerator letter
/// (white at 80 %: the earlier 45 % tag was too dim to read on either
/// group fill).
pub fn key_hint_button(def: &ButtonDef, id: Id, min_size: Vec2, base: Color32, veil: f32) -> StackedButton {
    let letter = RichText::new(
        def.accel_key()
            .to_ascii_uppercase()
            .to_string(),
    )
    .text_style(TextStyle::Small)
    .color(tokens::FG_80);
    StackedButton {
        id,
        icon: def.icon.source(),
        text: letter.into(),
        min_size,
        direction: Direction::LeftToRight,
        gap: tokens::KEY_GAP,
        pad_h: tokens::KEY_PAD_H,
        base,
        veil,
    }
}

/// `below` style: 48 pt tall, the icon over the Title-case label with the
/// accelerator glyph underlined.
pub fn below_button(def: &ButtonDef, id: Id, min_size: Vec2, base: Color32, veil: f32) -> StackedButton {
    StackedButton {
        id,
        icon: def.icon.source(),
        text: WidgetText::from(underlined_label(def)),
        min_size,
        direction: Direction::TopDown,
        gap: tokens::BELOW_GAP,
        pad_h: tokens::BELOW_PAD_H,
        base,
        veil,
    }
}

/// The tooltip chip's text: what the button does (`ButtonDef::tip`),
/// 11 pt, white.
pub fn tip_job(def: &ButtonDef) -> LayoutJob {
    LayoutJob::simple_singleline(
        def.tip.to_owned(),
        egui::FontId::new(tokens::TIP_FONT, egui::FontFamily::Monospace),
        tokens::FG,
    )
}

/// The tooltip chip at `rect`: the dark fill and the label centred in it.
/// A plain painter call rather than a widget: the chip is never
/// interactive and must not take the hover from the button under it.
pub fn tip(painter: &egui::Painter, rect: egui::Rect, def: &ButtonDef) {
    painter.rect_filled(rect, tokens::TIP_RADIUS, tokens::TIP_FILL);
    let galley = painter.layout_job(tip_job(def));
    // Centred on the box, not the ink: a tip with a descender must not
    // sit higher than one without, and the chip is padded for the box.
    painter.galley(rect.center() - galley.size() / 2.0, galley, tokens::FG);
}

/// The label in three sections, the middle one underlined: `RichText`
/// underlines a whole run, not a substring. Labels are ASCII (a model
/// test pins it), so the byte index is also the glyph index.
pub fn underlined_label(def: &ButtonDef) -> LayoutJob {
    let font = egui::FontId::new(tokens::LABEL_FONT, egui::FontFamily::Monospace);
    let plain = TextFormat {
        font_id: font,
        color: tokens::FG,
        ..Default::default()
    };
    let under = TextFormat {
        underline: egui::Stroke {
            width: 1.0,
            color: tokens::FG,
        },
        ..plain.clone()
    };
    let (a, b) = (def.underline_idx, def.underline_idx + 1);
    let mut job = LayoutJob::default();
    job.append(&def.label[..a], 0.0, plain.clone());
    job.append(&def.label[a..b], 0.0, under);
    job.append(&def.label[b..], 0.0, plain);
    job
}

/// The readout stacked on 1 em line boxes so the lines sit tight. Bold
/// numbers at 85 %; the × sign and the "words" caption regular at 70 % so
/// they read as separators, not as another number.
pub fn readout_job(readout: Readout) -> LayoutJob {
    let f = |family: egui::FontFamily, color: Color32| TextFormat {
        font_id: egui::FontId::new(tokens::READOUT_FONT, family),
        color,
        line_height: Some(tokens::READOUT_LINE),
        ..Default::default()
    };
    let bold = || f(crate::ui::fonts::MONO_BOLD.clone(), tokens::FG_85);
    let dim = || f(egui::FontFamily::Monospace, tokens::FG_70);
    let mut job = LayoutJob {
        halign: egui::Align::Center,
        ..Default::default()
    };
    match readout {
        Readout::Size {
            width,
            height,
        } => {
            job.append(&format!("{width}\n"), 0.0, bold());
            job.append("\u{00D7}\n", 0.0, dim());
            job.append(&height.to_string(), 0.0, bold());
        }
        Readout::Words(n) => {
            job.append(&format!("{n}\n"), 0.0, bold());
            job.append("words", 0.0, dim());
        }
    }
    job
}

/// The instruction's layout job at one wrap width. Regular weight at
/// 85 %, like the readout's numbers: it is the strip's one piece of
/// prose, and it sits straight on the chassis with no fill of its own.
pub fn hint_job(text: &str, wrap_width: f32) -> LayoutJob {
    let mut job = LayoutJob::single_section(
        text.to_owned(),
        TextFormat {
            font_id: egui::FontId::new(tokens::HINT_FONT, egui::FontFamily::Monospace),
            color: tokens::FG_85,
            line_height: Some(tokens::HINT_LINE),
            ..Default::default()
        },
    );
    job.wrap.max_width = wrap_width;
    job
}

/// The instruction in a dead slot, its block centred in the slot. Box
/// centring, not ink centring: a wrapped paragraph must sit on its line
/// boxes, or a line without descenders would ride differently from one
/// with them.
pub fn hint(ui: &mut Ui, text: &str, wrap_width: f32, slot: Vec2) -> egui::Rect {
    let galley = ui
        .painter()
        .layout_job(hint_job(text, wrap_width));
    let (rect, _) = ui.allocate_exact_size(slot, Sense::hover());
    ui.painter()
        .galley(rect.center() - galley.size() / 2.0, galley, tokens::FG_85);
    rect
}

/// The readout in a dead slot, centred on its ink: the galley's box
/// carries Cascadia's tall ascent above the digits, so centring the box
/// leaves the glyphs riding high, more so at every DPI step.
/// `mesh_bounds` is the glyphs' own extent, relative to the galley's
/// origin (`halign: Center` puts that origin on the block's centre line).
pub fn readout(ui: &mut Ui, readout: Readout, slot: Vec2) -> egui::Rect {
    let galley = ui.painter().layout_job(readout_job(readout));
    let (rect, _) = ui.allocate_exact_size(slot, Sense::hover());
    ui.painter()
        .galley(ink_centred(rect, &galley), galley, tokens::FG_85);
    rect
}

/// Where to put a galley so its ink, not its box, is centred in `rect`.
pub fn ink_centred(rect: egui::Rect, galley: &egui::Galley) -> egui::Pos2 {
    let ink = galley.mesh_bounds;
    if ink.is_positive() {
        rect.center() - ink.center().to_vec2()
    } else {
        rect.center() - galley.size() / 2.0
    }
}

/// The emblem in a dead slot: no fill, no hover, the mark centred at its
/// own size (the brand blue is baked into the SVG).
pub fn emblem(ui: &mut Ui, slot: Vec2) -> egui::Rect {
    let (rect, _) = ui.allocate_exact_size(slot, Sense::hover());
    let mark = Vec2::splat(tokens::EMBLEM_MARK);
    // `paint_at` rounds its rect to whole pixels and asks the loader for
    // exactly that many, so the mark is 1:1 at every DPI.
    Image::new(assets::CLOWD_LOGO.source())
        .fit_to_exact_size(mark)
        .show_loading_spinner(false)
        .paint_at(ui, egui::Rect::from_center_size(rect.center(), mark));
    rect
}
