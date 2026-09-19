//! Scene -> draw instances in window-local physical px. The painter emits
//! rect instances directly and describes icons and text as plain draws, so
//! it needs no GPU device and never learns what an atlas or a font is.

use super::scene::{Kind, Scene};
use super::tree::{TextSpec, WidgetId};
use crate::ui::gpu::hints::AA;
use crate::ui::gpu::rect::RectInstance;
use clowd_rust_core::geometry::{RectExt, ScreenRect};

/// An icon cell: `slot` is the owner's atlas index, `dest_px` the
/// `[l, t, r, b]` it fills.
pub struct IconDraw {
    pub slot: usize,
    pub dest_px: [f32; 4],
}

/// A text run at an integer origin (cosmic-text keeps a fractional x and
/// truncates y, so integers keep every glyph on the grid).
pub struct TextDraw<'s> {
    pub x: f32,
    pub y: f32,
    pub spec: &'s TextSpec,
}

/// One frame of instances for `scene` in window-local physical px. `origin`
/// is the window's top-left in virtual-desktop px; `dpi` is read only for
/// the shadow blur; `hover(id)` is the veil amount in [0, 1]. Nodes are
/// visited in pre-order, so a box's instances precede its children's; per
/// box the order is shadow, fill, ring.
pub fn paint<'s>(
    scene: &'s Scene,
    origin: (i32, i32),
    dpi: f32,
    hover: &dyn Fn(WidgetId) -> f32,
    rects: &mut Vec<RectInstance>,
    icons: &mut Vec<IconDraw>,
    texts: &mut Vec<TextDraw<'s>>,
) {
    let local = |r: ScreenRect| -> (f32, f32, f32, f32) {
        (
            (r.left() - origin.0) as f32,
            (r.top() - origin.1) as f32,
            (r.right() - origin.0) as f32,
            (r.bottom() - origin.1) as f32,
        )
    };
    for node in &scene.nodes {
        match &node.kind {
            Kind::Box(look) => {
                let body = local(node.rect);
                let (l, t, r, b) = body;
                let radius = look.radius as f32;
                if let Some(shadow) = look.shadow {
                    let off = shadow.offset_y * dpi;
                    let (sigma, pad) = shadow_params(shadow.sigma, dpi);
                    rects.push(RectInstance::blurred_shadow(
                        (l, t + off, r, b + off),
                        radius,
                        sigma,
                        pad,
                        shadow.rgba,
                    ));
                }
                if let Some(fill) = look.fill {
                    let lighten = look.hover_veil * node.id.map_or(0.0, hover);
                    rects.push(rounded(body, radius, fill, [0.0; 4], 0.0, lighten));
                }
                // The ring is its own transparent-fill instance: the rounded
                // mode's border REPLACES the fill under the band, so a
                // translucent border on the fill instance would punch a
                // see-through ring.
                if let Some((rgba, px)) = look.ring {
                    rects.push(rounded(body, radius, [0.0; 4], rgba, px as f32, 0.0));
                }
            }
            Kind::Icon(icon) => {
                let (l, t, r, b) = local(node.rect);
                icons.push(IconDraw {
                    slot: icon.slot,
                    dest_px: [l, t, r, b],
                });
            }
            Kind::Text(spec) => {
                let (x, y, _, _) = local(node.rect);
                texts.push(TextDraw {
                    x,
                    y,
                    spec,
                });
            }
        }
    }
}

/// `(sigma, pad)` for a shadow at `dpi`: the blur's gaussian sigma in
/// physical px, and how far the shadow quad must extend past its body for
/// the falloff to reach ~0 (`ceil(3 sigma) + 1`). Unrounded on purpose:
/// these parametrise a blurred shape, not hit-tested geometry, and Skia
/// paints the C# strips at the fractional `3 * scale` too.
fn shadow_params(sigma_logical: f32, dpi: f32) -> (f32, f32) {
    let sigma = sigma_logical * dpi;
    (sigma, (3.0 * sigma).ceil() + 1.0)
}

/// Rounded rect on the intended `(l, t, r, b)`: the quad is inflated by
/// [`AA`] on every side and `AA` is passed as `aa_pad` so the SDF edge
/// lands exactly on the rect. `border_px` of 0 (or a transparent
/// `border`) disables the border; `lighten` mixes the fill toward white.
fn rounded(rect: (f32, f32, f32, f32), radius: f32, fill: [f32; 4], border: [f32; 4], border_px: f32, lighten: f32) -> RectInstance {
    let (l, t, r, b) = rect;
    RectInstance {
        dest_px: [l - AA, t - AA, r + AA, b + AA],
        fill_rgba: fill,
        border_rgba: border,
        params: [border_px, lighten, radius, AA],
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ui::kit::scene::Placed;
    use crate::ui::kit::tokens::{self, color};
    use crate::ui::kit::tree::{IconSpec, Look};

    fn rect(x: i32, y: i32, w: i32, h: i32) -> ScreenRect {
        ScreenRect::from_xy_size(x, y, w, h)
    }

    fn scene(nodes: Vec<Placed>) -> Scene {
        let live = nodes[0].rect;
        Scene {
            nodes,
            live,
        }
    }

    struct Frame {
        rects: Vec<RectInstance>,
        icons: Vec<IconDraw>,
        texts: Vec<(f32, f32, TextSpec)>,
    }

    /// Paint `scene` for a window at (100, 50) at 100 %.
    fn run(scene: &Scene, hover: &dyn Fn(WidgetId) -> f32) -> Frame {
        let (mut rects, mut icons, mut texts) = (Vec::new(), Vec::new(), Vec::new());
        paint(scene, (100, 50), 1.0, hover, &mut rects, &mut icons, &mut texts);
        Frame {
            rects,
            icons,
            texts: texts
                .into_iter()
                .map(|t| (t.x, t.y, t.spec.clone()))
                .collect(),
        }
    }

    #[test]
    fn box_emits_shadow_fill_ring_in_that_order() {
        let look = Look {
            fill: Some(color::TRAY),
            ring: Some((color::RING, 1)),
            radius: 8,
            shadow: Some(tokens::SHADOW_COMPACT),
            hover_veil: 0.0,
        };
        let s = scene(vec![Placed {
            id: None,
            rect: rect(165, 765, 670, 56),
            kind: Kind::Box(look),
        }]);
        let Frame {
            rects,
            icons,
            texts,
        } = run(&s, &|_| 1.0);
        assert!(icons.is_empty() && texts.is_empty());
        assert_eq!(rects.len(), 3);
        // Shadow: body offset 3 down, padded by ceil(3 * 3.4) + 1 = 12, sigma carried negative.
        assert_eq!(
            rects[0].dest_px,
            [65.0 - 12.0, 715.0 + 3.0 - 12.0, 735.0 + 12.0, 771.0 + 3.0 + 12.0]
        );
        assert_eq!(rects[0].fill_rgba, color::SHADOW);
        assert_eq!(rects[0].params, [-3.4, 0.0, 8.0, 12.0]);
        // Fill: inflated by AA, no border, no lighten (a dead box has no hover).
        assert_eq!(rects[1].dest_px, [65.0 - AA, 715.0 - AA, 735.0 + AA, 771.0 + AA]);
        assert_eq!(rects[1].fill_rgba, color::TRAY);
        assert_eq!(rects[1].params, [0.0, 0.0, 8.0, AA]);
        // Ring: transparent fill, the ring colour as a 1 px border.
        assert_eq!(rects[2].fill_rgba, [0.0; 4]);
        assert_eq!(rects[2].border_rgba, color::RING);
        assert_eq!(rects[2].params, [1.0, 0.0, 8.0, AA]);
        assert_eq!(rects[2].dest_px, rects[1].dest_px);
    }

    #[test]
    fn hover_veil_rides_lighten() {
        let seg = Look {
            fill: Some(color::SEG),
            radius: 8,
            hover_veil: tokens::HOVER_VEIL,
            ..Default::default()
        };
        let s = scene(vec![
            Placed {
                id: None,
                rect: rect(100, 50, 200, 100),
                kind: Kind::Box(seg),
            },
            Placed {
                id: Some(WidgetId(1)),
                rect: rect(110, 60, 40, 40),
                kind: Kind::Box(seg),
            },
            Placed {
                id: Some(WidgetId(2)),
                rect: rect(160, 60, 40, 40),
                kind: Kind::Box(seg),
            },
        ]);
        let rects = run(&s, &|id| if id == WidgetId(1) { 0.5 } else { 1.0 }).rects;
        assert_eq!(rects.len(), 3, "fill only: no shadow, no ring");
        assert_eq!(rects[0].params[1], 0.0, "a node without an id never lightens");
        assert!((rects[1].params[1] - 0.06).abs() < 1e-6, "0.12 * 0.5");
        assert!((rects[2].params[1] - 0.12).abs() < 1e-6);
        let bare = scene(vec![Placed {
            id: Some(WidgetId(1)),
            rect: rect(0, 0, 10, 10),
            kind: Kind::Box(Look::default()),
        }]);
        assert!(run(&bare, &|_| 1.0).rects.is_empty(), "a bare box paints nothing");
    }

    #[test]
    fn icons_fill_their_node_rect() {
        let s = scene(vec![
            Placed {
                id: None,
                rect: rect(100, 50, 200, 100),
                kind: Kind::Box(Look::default()),
            },
            Placed {
                id: None,
                rect: rect(110, 60, 20, 20),
                kind: Kind::Icon(IconSpec {
                    slot: 4,
                    px: 20,
                }),
            },
        ]);
        let Frame {
            rects,
            icons,
            ..
        } = run(&s, &|_| 0.0);
        assert!(rects.is_empty());
        assert_eq!(icons.len(), 1);
        assert_eq!((icons[0].slot, icons[0].dest_px), (4, [10.0, 10.0, 30.0, 30.0]));
    }

    #[test]
    fn texts_are_placed_at_the_node_origin_in_local_px() {
        let spec = TextSpec {
            text: "Label".into(),
            font_px: 12,
            bold: true,
            color: color::FG_80,
            underline: Some(1),
            hairline_px: 1,
        };
        let s = scene(vec![
            Placed {
                id: None,
                rect: rect(100, 50, 200, 100),
                kind: Kind::Box(Look::default()),
            },
            Placed {
                id: None,
                rect: rect(123, 86, 29, 14),
                kind: Kind::Text(spec.clone()),
            },
        ]);
        let texts = run(&s, &|_| 0.0).texts;
        assert_eq!(texts, vec![(23.0, 36.0, spec)]);
    }

    /// The shadow quad reaches three sigma past its body at every standard
    /// DPI, so the falloff is ~0 before the quad edge.
    #[test]
    fn shadow_reach_covers_three_sigma() {
        for (dpi, sigma, pad) in [(1.0, 3.4, 12.0), (1.25, 4.25, 14.0), (1.5, 5.1, 17.0), (2.0, 6.8, 22.0)] {
            let (s, p) = shadow_params(tokens::SHADOW_COMPACT.sigma, dpi);
            assert!((s - sigma).abs() < 1e-5, "dpi {dpi}: sigma {s} != {sigma}");
            assert_eq!(p, pad, "dpi {dpi}: pad");
        }
    }
}
