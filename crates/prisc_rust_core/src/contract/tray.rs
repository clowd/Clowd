//! The capture tray's design tokens as raw numbers, shared by the capture overlay's egui panel and
//! the Avalonia floating strips (C#: `TrayTokens`), so the two keep looking like one product. Each
//! side maps them onto its own colour, size and shadow types.

use std::time::Duration;

use super::Rgb;

crate::contract! {
    /// The chassis fill.
    pub const TRAY_FILL: Rgb = Rgb(0x25272B);
    /// The secondary group (segment) fill.
    pub const SEG_FILL: Rgb = Rgb(0x3A3E44);
    /// The 1 px highlight ring inside the tray edge as Avalonia draws it: the tray fill plus 10 %
    /// white, baked opaque so the ring never shows the desktop through it, on a pixel row of its own.
    pub const RING_AVALONIA: Rgb = Rgb(0x3B3D40);
    /// The same ring as egui draws it. egui feathers a 1 pt stroke across two half-covered rows, so
    /// the Avalonia colour read as half the step there; the tray fill plus 20 % white lands the same
    /// visible edge.
    pub const RING_EGUI: Rgb = Rgb(0x515255);
    /// The corner radius of the tray and of every button in it.
    pub const RADIUS: f32 = 8.0;
    /// The tray's padding: the inset from its edge to its first item, the 1 px ring included.
    pub const TRAY_PAD: f32 = 4.0;
    /// The gap between items inside the tray.
    pub const GAP: f32 = 4.0;
    /// A button's height in a row (the row height of the tray's button lane).
    pub const BUTTON_HEIGHT: f32 = 40.0;
    /// A button is never shorter than this along a row.
    pub const BUTTON_MIN_WIDTH: f32 = 40.0;
    /// A button's icon cell.
    pub const ICON_SIZE: f32 = 20.0;
    /// The app mark's slot along the strip.
    pub const EMBLEM_LENGTH: f32 = 40.0;
    /// The hover veil over a segment: white at this alpha.
    pub const HOVER_VEIL_ALPHA: f32 = 0.12;
    /// The hover veil over a button sitting straight on the darker chassis: white at this alpha, a
    /// step heavier so the button still reads as lit.
    pub const HOVER_VEIL_BARE_ALPHA: f32 = 0.18;
    /// How long a hover or state fill takes to fade, in and out.
    pub const FILL_TRANSITION: Duration = Duration::from_millis(180);
    /// The accelerator letter's font size.
    pub const KEY_HINT_FONT_SIZE: f32 = 9.0;
    /// The tooltip fill's colour.
    pub const TIP_FILL_RGB: Rgb = Rgb(0x141619);
    /// The tooltip fill's alpha (0..255).
    pub const TIP_FILL_ALPHA: u32 = 0xF2;
    /// The tooltip's clearance below the strip.
    pub const TOOL_TIP_GAP_BOTTOM: f32 = 7.0;
    /// The tooltip's clearance beside a column.
    pub const TOOL_TIP_GAP_RIGHT: f32 = 8.0;
    /// How long the pointer rests on an item before its tooltip shows.
    pub const TOOL_TIP_SHOW_DELAY: Duration = Duration::from_millis(350);
    /// The shipped (compact) drop shadow's vertical offset. Every transparent pixel of a window eats
    /// clicks, so the spec's larger shadow left a dead band under the strip.
    pub const SHADOW_OFFSET_Y: f32 = 3.0;
    /// The shipped drop shadow's blur.
    pub const SHADOW_BLUR: f32 = 10.0;
    /// The shipped drop shadow's black alpha (0..255).
    pub const SHADOW_ALPHA: u32 = 0x59;
}
