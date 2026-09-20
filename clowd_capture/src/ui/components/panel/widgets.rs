//! The tray's four pieces as egui widgets: the two button styles, the
//! size readout and the emblem.
//!
//! Only the buttons are interactive. The readout and the emblem allocate
//! space and paint into it, so the tray body under them is dead in the
//! same way the retired composer's chassis was.

use egui::text::{LayoutJob, TextFormat};
use egui::{
    Align2, AtomExt as _, AtomLayout, Color32, Direction, Frame, Id, Image, ImageSource, Margin, Response, RichText, Sense, TextStyle, Ui,
    Vec2, WidgetText,
};

use super::assets;
use super::model::ButtonDef;
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
        // second translucent rect over the first.
        let mut allocated = layout.allocate(ui);
        let t = ui
            .ctx()
            .animate_bool_with_time(self.id.with("hover"), allocated.response.hovered(), tokens::HOVER_FADE_SECS);
        allocated.sized.frame.fill = theme::seg_fill(t);
        allocated.paint(ui).response
    }
}

/// `key` style: a 40 pt square, the icon beside the dim accelerator
/// letter.
pub fn key_hint_button(def: &ButtonDef, id: Id, min_size: Vec2) -> StackedButton {
    let letter = RichText::new(
        def.accel_key()
            .to_ascii_uppercase()
            .to_string(),
    )
    .text_style(TextStyle::Small)
    .color(tokens::FG_45);
    StackedButton {
        id,
        icon: def.icon.source(),
        text: letter.into(),
        min_size,
        direction: Direction::LeftToRight,
        gap: tokens::KEY_GAP,
        pad_h: tokens::KEY_PAD_H,
    }
}

/// `below` style: 48 pt tall, the icon over the Title-case label with the
/// accelerator glyph underlined.
pub fn below_button(def: &ButtonDef, id: Id, min_size: Vec2) -> StackedButton {
    StackedButton {
        id,
        icon: def.icon.source(),
        text: WidgetText::from(underlined_label(def)),
        min_size,
        direction: Direction::TopDown,
        gap: tokens::BELOW_GAP,
        pad_h: tokens::BELOW_PAD_H,
    }
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

/// Width, the multiplication sign and height stacked on 1 em line boxes
/// so the three lines sit tight. Bold digits at 80 %; the sign regular and
/// a shade dimmer so it reads as a separator, not a fourth digit.
pub fn readout_job(size: (i32, i32)) -> LayoutJob {
    let f = |family: egui::FontFamily, color: Color32| TextFormat {
        font_id: egui::FontId::new(tokens::READOUT_FONT, family),
        color,
        line_height: Some(tokens::READOUT_LINE),
        ..Default::default()
    };
    let mut job = LayoutJob {
        halign: egui::Align::Center,
        ..Default::default()
    };
    job.append(&format!("{}\n", size.0), 0.0, f(crate::ui::fonts::MONO_BOLD.clone(), tokens::FG_80));
    job.append("\u{00D7}\n", 0.0, f(egui::FontFamily::Monospace, tokens::FG_70));
    job.append(&size.1.to_string(), 0.0, f(crate::ui::fonts::MONO_BOLD.clone(), tokens::FG_80));
    job
}

/// The readout in a dead slot. `halign: Center` makes the galley's x
/// origin its centre line, so the block is centred by placing that origin
/// on the slot's centre.
pub fn readout(ui: &mut Ui, size: (i32, i32), slot: Vec2) -> egui::Rect {
    let galley = ui.painter().layout_job(readout_job(size));
    let (rect, _) = ui.allocate_exact_size(slot, Sense::hover());
    ui.painter().galley(
        egui::pos2(rect.center().x, rect.center().y - galley.size().y / 2.0),
        galley,
        tokens::FG_80,
    );
    rect
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
