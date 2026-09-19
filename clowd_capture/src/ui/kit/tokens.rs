//! Logical (100 %) values of the floating-tray design, mirrored from the C#
//! `TrayTokens`, and [`Scale`], the ONLY place a logical value becomes device
//! pixels. Consumers scale once, build the tree from integers, and never touch
//! the DPI again.

/// Inset from the tray edge to its first item.
pub const TRAY_PAD: f32 = 4.0;
/// Space between items along the strip.
pub const GAP: f32 = 4.0;
/// Corner radius of the chassis AND of every item box, by user request.
pub const RADIUS: f32 = 8.0;
/// A button is never shorter than this along a row.
pub const BUTTON_MIN_LENGTH: f32 = 40.0;
/// The emblem's slot along the strip.
pub const EMBLEM_LENGTH: f32 = 40.0;
/// The emblem mark, centred in its slot.
pub const EMBLEM_MARK: f32 = 32.0;
/// Button icon cell.
pub const ICON: f32 = 20.0;
/// Clearance kept from the bounds edge in the fit test.
pub const NEAR_MIN_DISTANCE: f32 = 2.0;
/// Gap between the anchor and the chassis.
pub const NEAR_MAX_DISTANCE: f32 = 15.0;
/// `TrayTokens.FillTransition`: 180 ms, linear.
pub const HOVER_FADE_SECS: f32 = 0.18;
/// Hover veil: white at 12 % over an item fill. On an opaque fill,
/// `mix(fill, white, 0.12)` is the same colour, so it rides the rect
/// shader's `lighten` parameter.
pub const HOVER_VEIL: f32 = 0.12;

/// Colours from the C# `TrayTokens` (display space, straight alpha).
pub mod color {
    /// Chassis fill, `#25272B`.
    pub const TRAY: [f32; 4] = [0x25 as f32 / 255.0, 0x27 as f32 / 255.0, 0x2B as f32 / 255.0, 1.0];
    /// Item fill, `#3A3E44`.
    pub const SEG: [f32; 4] = [0x3A as f32 / 255.0, 0x3E as f32 / 255.0, 0x44 as f32 / 255.0, 1.0];
    /// The hairline ring just inside the chassis edge: white at 6 %.
    pub const RING: [f32; 4] = [1.0, 1.0, 1.0, 0.06];
    /// `ShadowCompact` colour `#59000000`: black at 35 %.
    pub const SHADOW: [f32; 4] = [0.0, 0.0, 0.0, 0x59 as f32 / 255.0];
    /// Plain white text.
    pub const FG: [u8; 4] = [0xFF, 0xFF, 0xFF, 0xFF];
    /// White text at 80 %.
    pub const FG_80: [u8; 4] = [0xFF, 0xFF, 0xFF, 204];
}

/// A drop shadow in logical units; the painter scales it by the DPI.
/// `ShadowCompact` is `0 3 10 #59000000`; sigma is Skia's `0.288675 * blur + 0.5`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Shadow {
    pub offset_y: f32,
    pub sigma: f32,
    pub rgba: [f32; 4],
}

/// `ShadowCompact`, the chassis shadow.
pub const SHADOW_COMPACT: Shadow = Shadow {
    offset_y: 3.0,
    sigma: 3.4,
    rgba: color::SHADOW,
};

/// DPI scaling with the rounding rule per token class. Products are taken in
/// `f64` so integer inputs never pick up `f32` drift.
#[derive(Debug, Clone, Copy)]
pub struct Scale {
    dpi: f64,
}

impl Scale {
    pub fn new(dpi: f32) -> Self {
        Self {
            dpi: dpi.max(0.1) as f64,
        }
    }

    /// Sizes floor (48 -> 60 at 1.25): nothing grows past the C# box.
    pub fn size(self, v: f32) -> i32 {
        (v as f64 * self.dpi).floor() as i32
    }

    /// Gaps, paddings and distances ceil (4 -> 5 at 1.25): `TrayInsets.FromLogical` rounds up.
    pub fn space(self, v: f32) -> i32 {
        (v as f64 * self.dpi).ceil() as i32
    }

    /// Icons round to the nearest whole cell, never 0.
    pub fn icon(self, v: f32) -> i32 {
        ((v as f64 * self.dpi).round() as i32).max(1)
    }

    /// Hairlines (ring, underline): `round(dpi).max(1)`, 1 px until 150 %, then 2.
    pub fn hair(self) -> i32 {
        (self.dpi.round() as i32).max(1)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scale_rules_are_pinned_at_each_dpi() {
        let cases = [
            // dpi, space(4), size(48), size(40), icon(20), size(12), hair, space(2), space(15)
            (1.0, 4, 48, 40, 20, 12, 1, 2, 15),
            (1.25, 5, 60, 50, 25, 15, 1, 3, 19),
            (1.5, 6, 72, 60, 30, 18, 2, 3, 23),
            (2.0, 8, 96, 80, 40, 24, 2, 4, 30),
        ];
        for (dpi, space4, size48, size40, icon20, size12, hair, space2, space15) in cases {
            let s = Scale::new(dpi);
            assert_eq!(s.space(4.0), space4, "space(4) at {dpi}");
            assert_eq!(s.size(48.0), size48, "size(48) at {dpi}");
            assert_eq!(s.size(40.0), size40, "size(40) at {dpi}");
            assert_eq!(s.icon(20.0), icon20, "icon(20) at {dpi}");
            assert_eq!(s.size(12.0), size12, "size(12) at {dpi}");
            assert_eq!(s.hair(), hair, "hair at {dpi}");
            assert_eq!(s.space(2.0), space2, "space(2) at {dpi}");
            assert_eq!(s.space(15.0), space15, "space(15) at {dpi}");
        }
    }

    #[test]
    fn hairlines_never_vanish() {
        assert_eq!(Scale::new(0.3).hair(), 1);
        assert_eq!(Scale::new(0.0).icon(ICON), 2);
    }
}
