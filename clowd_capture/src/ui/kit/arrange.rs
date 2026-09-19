//! taffy build + compute + flatten: a [`Widget`] tree in, a [`Scene`] out.
//!
//! DPI and rounding rule, stated once: tokens are scaled to integers BEFORE
//! the tree exists ([`super::tokens::Scale`]); the measure returns integers;
//! the tree is built from integers only. The only fractional values the flex
//! algorithm can then produce are `free_space / 2` centring offsets, exact
//! halves in `f32`, and only one level of centring is ever nested (items are
//! start-justified inside a strip; centring happens inside an item). taffy's
//! own rounding is disabled and [`snap`] floors each node's accumulated
//! absolute origin while keeping the integer sizes, so siblings never gap or
//! overlap by rounding and there is no per-level drift.

use super::scene::{Kind, Placed, Scene};
use super::tree::{Align, Axis, Content, Extent, Layout, TextSpec, Widget};
use clowd_rust_core::geometry::{RectExt, ScreenRect};
use taffy::prelude::TaffyMaxContent;
use taffy::{
    compute_leaf_layout, AlignItems, Dimension, Display, FlexDirection, JustifyContent, LengthPercentage, LengthPercentageAuto, NodeId,
    Rect, Size, Style, TaffyTree, TraversePartialTree,
};

/// Content-box size of a text leaf, `(width, height)` in device px. Must be
/// pure and cheap: taffy calls it several times per leaf per arrange
/// (max-content, min-content, cross, final).
pub type Measure<'a> = &'a mut dyn FnMut(&TextSpec) -> (i32, i32);

/// Build the taffy tree for `root`, lay it out shrink-wrapped at the origin,
/// and flatten it. The `TaffyTree` is created, computed and dropped inside;
/// it is `!Send` and never escapes.
pub fn arrange(root: &Widget, measure: Measure<'_>) -> Scene {
    let mut tree: TaffyTree<Content> = TaffyTree::with_capacity(32);
    // The kit snaps edges itself; see the module doc.
    tree.disable_rounding();
    let root_id = build(&mut tree, root);
    tree.compute_layout_with_measure(root_id, Size::MAX_CONTENT, |inputs, _, ctx, style| {
        compute_leaf_layout(
            inputs,
            style,
            |_, _| 0.0,
            |_known, _avail| match ctx {
                Some(Content::Icon(i)) => Size {
                    width: i.px as f32,
                    height: i.px as f32,
                },
                Some(Content::Text(t)) => {
                    let (w, h) = measure(t);
                    Size {
                        width: w as f32,
                        height: h as f32,
                    }
                }
                // A childless box measures nothing; its own `size` style still applies.
                _ => Size::ZERO,
            },
        )
    })
    .expect("taffy never errs on a tree it built itself");
    let mut nodes = Vec::with_capacity(tree.total_node_count());
    flatten(&tree, root_id, root, 0.0, 0.0, &mut nodes);
    let live = nodes[0].rect;
    Scene {
        nodes,
        live,
    }
}

/// Advance of `chars` glyphs of the bundled Cascadia faces at `font_px`,
/// ceiling (75/128 em per glyph, pinned against cosmic-text by the
/// renderer's font test).
pub const fn mono_text_width(chars: usize, font_px: i32) -> i32 {
    (chars as i32 * font_px * 75 + 127) / 128
}

/// The initial measure: mono advance by the integer line box `floor(font_px * 1.2)`.
pub fn mono_measure(spec: &TextSpec) -> (i32, i32) {
    (mono_text_width(spec.text.chars().count(), spec.font_px), spec.font_px * 6 / 5)
}

fn build(tree: &mut TaffyTree<Content>, w: &Widget) -> NodeId {
    let style = style_of(&w.layout);
    if w.children.is_empty() {
        // Every childless node gets a context (`Content::None` included) so
        // the measure closure's `ctx` is never a surprise `None` for a
        // container-shaped leaf.
        tree.new_leaf_with_context(style, w.content.clone())
            .expect("leaf")
    } else {
        let kids: Vec<NodeId> = w
            .children
            .iter()
            .map(|c| build(tree, c))
            .collect();
        tree.new_with_children(style, &kids)
            .expect("container")
    }
}

// Two functions on purpose: `size` is `Size<Dimension>` and `min_size` is
// `Size<LengthPercentageAuto>`, so one closure cannot populate both.
fn dim(e: Extent) -> Dimension {
    match e {
        Extent::Auto => Dimension::auto(),
        Extent::Px(v) => Dimension::length(v as f32),
    }
}

fn min_dim(v: i32) -> LengthPercentageAuto {
    if v > 0 {
        LengthPercentageAuto::length(v as f32)
    } else {
        LengthPercentageAuto::auto()
    }
}

fn style_of(l: &Layout) -> Style {
    let gap = LengthPercentage::length(l.gap as f32);
    Style {
        display: Display::Flex,
        flex_direction: match l.axis {
            Axis::Row => FlexDirection::Row,
            Axis::Column => FlexDirection::Column,
        },
        size: Size {
            width: dim(l.width),
            height: dim(l.height),
        },
        min_size: Size {
            width: min_dim(l.min_width),
            height: min_dim(l.min_height),
        },
        padding: Rect {
            left: LengthPercentage::length(l.padding.left as f32),
            right: LengthPercentage::length(l.padding.right as f32),
            top: LengthPercentage::length(l.padding.top as f32),
            bottom: LengthPercentage::length(l.padding.bottom as f32),
        },
        gap: Size {
            width: gap,
            height: gap,
        },
        align_items: Some(match l.align {
            Align::Start => AlignItems::FLEX_START,
            Align::Center => AlignItems::CENTER,
            Align::Stretch => AlignItems::STRETCH,
        }),
        justify_content: Some(match l.justify {
            Align::Center => JustifyContent::CENTER,
            Align::Start | Align::Stretch => JustifyContent::FLEX_START,
        }),
        // Not load-bearing today: every extent is an explicit `size`, and a
        // content-sized root never runs the shrink pass. Kept so a future
        // fixed-size parent cannot squash items; if anyone ever maps an
        // extent to `flex_basis`, this line becomes required (taffy only
        // reserves a basis under MaxContent when flex_shrink == 0).
        flex_shrink: 0.0,
        // Never exhaustive: `Style<S>` carries a `PhantomData`.
        ..Default::default()
    }
}

fn flatten(tree: &TaffyTree<Content>, node: NodeId, w: &Widget, ox: f32, oy: f32, out: &mut Vec<Placed>) {
    let l = tree.unrounded_layout(node);
    let (ax, ay) = (ox + l.location.x, oy + l.location.y);
    out.push(Placed {
        id: w.id,
        rect: snap(ax, ay, l.size.width, l.size.height),
        kind: kind_of(w),
    });
    // Same insertion order as `build`.
    for (child_id, child) in tree.child_ids(node).zip(&w.children) {
        flatten(tree, child_id, child, ax, ay, out);
    }
}

/// Origin floors to the pixel grid: a half-pixel centring remainder leans to
/// the start edge. Sizes are integers already and are kept, so siblings never
/// gap or overlap by rounding.
fn snap(ax: f32, ay: f32, w: f32, h: f32) -> ScreenRect {
    debug_assert!(
        w.fract() == 0.0 && h.fract() == 0.0,
        "kit sizes must be integers: scale tokens first"
    );
    ScreenRect::from_xy_size(ax.floor() as i32, ay.floor() as i32, w as i32, h as i32)
}

fn kind_of(w: &Widget) -> Kind {
    match &w.content {
        Content::Icon(i) if w.children.is_empty() => Kind::Icon(*i),
        Content::Text(t) if w.children.is_empty() => Kind::Text(t.clone()),
        _ => Kind::Box(w.look),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ui::kit::tokens::{self, color, Scale};
    use crate::ui::kit::tree::{ButtonBox, Edges, IconSpec, Look, WidgetId};

    /// Width = one px per char, height = the font size: every number in
    /// the non-mono tests is chosen by the string length.
    fn stub(t: &TextSpec) -> (i32, i32) {
        (t.text.len() as i32, t.font_px)
    }

    fn label(chars: usize, font_px: i32) -> TextSpec {
        TextSpec {
            text: "x".repeat(chars),
            font_px,
            bold: false,
            color: color::FG,
            underline: Some(0),
            hairline_px: 1,
        }
    }

    fn square(px: i32) -> Widget {
        Widget::boxed(
            Layout {
                width: Extent::Px(px),
                height: Extent::Px(px),
                ..Default::default()
            },
            Look::default(),
            vec![],
        )
    }

    fn rect(x: i32, y: i32, w: i32, h: i32) -> ScreenRect {
        ScreenRect::from_xy_size(x, y, w, h)
    }

    fn rects(scene: &Scene) -> Vec<ScreenRect> {
        scene.nodes.iter().map(|n| n.rect).collect()
    }

    #[test]
    fn row_places_children_pad_then_gap_apart() {
        let tree = Widget::stack(Axis::Row, 4, 4, 0, Look::default(), vec![square(10), square(10), square(10)]);
        let scene = arrange(&tree, &mut stub);
        assert_eq!(
            rects(&scene),
            vec![rect(0, 0, 46, 18), rect(4, 4, 10, 10), rect(18, 4, 10, 10), rect(32, 4, 10, 10)]
        );
        assert_eq!(scene.live, scene.bounds());
    }

    #[test]
    fn same_tree_flips_with_the_axis() {
        let tree = Widget::stack(Axis::Column, 4, 4, 0, Look::default(), vec![square(10), square(10), square(10)]);
        let scene = arrange(&tree, &mut stub);
        assert_eq!(
            rects(&scene),
            vec![rect(0, 0, 18, 46), rect(4, 4, 10, 10), rect(4, 18, 10, 10), rect(4, 32, 10, 10)]
        );
    }

    #[test]
    fn along_is_width_in_a_row_and_height_in_a_column() {
        let mark = IconSpec {
            slot: 3,
            px: 32,
        };
        let row = arrange(
            &Widget::stack(Axis::Row, 0, 0, 48, Look::default(), vec![Widget::emblem(Axis::Row, 40, mark)]),
            &mut stub,
        );
        assert_eq!(rects(&row), vec![rect(0, 0, 40, 48), rect(0, 0, 40, 48), rect(4, 8, 32, 32)]);
        assert_eq!(row.nodes[2].kind, Kind::Icon(mark));
        let col = arrange(
            &Widget::stack(
                Axis::Column,
                0,
                0,
                84,
                Look::default(),
                vec![Widget::emblem(Axis::Column, 40, mark)],
            ),
            &mut stub,
        );
        assert_eq!(rects(&col), vec![rect(0, 0, 84, 40), rect(0, 0, 84, 40), rect(26, 4, 32, 32)]);
    }

    fn two_buttons(axis: Axis, min_across: i32) -> Widget {
        let size = ButtonBox {
            height: 48,
            min_length: 40,
            pad_h: 8,
            gap: 4,
        };
        let icon = IconSpec {
            slot: 0,
            px: 20,
        };
        Widget::stack(
            axis,
            4,
            4,
            min_across,
            Look::default(),
            vec![
                Widget::button(WidgetId(1), size, Look::default(), icon, label(24, 14)),
                Widget::button(WidgetId(2), size, Look::default(), icon, label(60, 14)),
            ],
        )
    }

    #[test]
    fn column_stretches_children_to_the_widest() {
        let row = arrange(&two_buttons(Axis::Row, 0), &mut stub);
        assert_eq!(row.find(WidgetId(1)).unwrap().rect, rect(4, 4, 40, 48));
        assert_eq!(row.find(WidgetId(2)).unwrap().rect, rect(48, 4, 76, 48));
        let col = arrange(&two_buttons(Axis::Column, 0), &mut stub);
        assert_eq!(col.bounds(), rect(0, 0, 84, 108));
        assert_eq!(
            col.find(WidgetId(1)).unwrap().rect,
            rect(4, 4, 76, 48),
            "the narrow one is stretched to the widest"
        );
        assert_eq!(col.find(WidgetId(2)).unwrap().rect, rect(4, 56, 76, 48));
    }

    #[test]
    fn min_across_widens_a_column() {
        let col = arrange(&two_buttons(Axis::Column, 100), &mut stub);
        assert_eq!(col.bounds(), rect(0, 0, 100, 108));
        for id in [WidgetId(1), WidgetId(2)] {
            assert_eq!(col.find(id).unwrap().rect.width(), 100 - 2 * 4, "{id:?}");
        }
        let row = arrange(&two_buttons(Axis::Row, 100), &mut stub);
        assert_eq!(row.bounds().height(), 100, "a row's min_across is its height");
        assert_eq!(
            row.find(WidgetId(1)).unwrap().rect.height(),
            48,
            "a fixed-height item is not stretched"
        );
    }

    #[test]
    fn leaf_padding_adds_to_the_measured_text_and_min_length_binds() {
        let size = ButtonBox {
            height: 48,
            min_length: 40,
            pad_h: 8,
            gap: 4,
        };
        let icon = IconSpec {
            slot: 0,
            px: 20,
        };
        let wide = Widget::button(WidgetId(1), size, Look::default(), icon, label(29, 14));
        assert_eq!(arrange(&wide, &mut stub).bounds(), rect(0, 0, 45, 48));
        let narrow = Widget::button(WidgetId(1), size, Look::default(), icon, label(22, 14));
        assert_eq!(arrange(&narrow, &mut stub).bounds(), rect(0, 0, 40, 48));
    }

    #[test]
    fn icon_over_label_is_centred_and_floors_the_half_pixel() {
        let size = ButtonBox {
            height: 48,
            min_length: 40,
            pad_h: 8,
            gap: 4,
        };
        let icon = IconSpec {
            slot: 0,
            px: 20,
        };
        let button = Widget::button(WidgetId(1), size, Look::default(), icon, label(29, 14));
        let scene = arrange(&button, &mut stub);
        // Content block 20 + 4 + 14 = 38 in 48: free 10, so the icon sits at
        // +5; across, (45 - 20) / 2 = 12.5 floors to 12.
        assert_eq!(rects(&scene), vec![rect(0, 0, 45, 48), rect(12, 5, 20, 20), rect(8, 29, 29, 14)]);
        assert_eq!(scene.nodes[1].kind, Kind::Icon(icon));
        assert_eq!(scene.nodes[2].kind, Kind::Text(label(29, 14)));
    }

    /// A strip shaped like the tray (emblem, caption box, buttons) with
    /// every token scaled by `Scale`.
    fn strip(axis: Axis, s: Scale, min_across: i32) -> Widget {
        let pad = s.space(tokens::TRAY_PAD);
        let gap = s.space(tokens::GAP);
        let button = s.size(48.0);
        let font_px = s.size(12.0);
        let hair = s.hair();
        let look = Look {
            fill: Some(color::TRAY),
            ring: Some((color::RING, hair)),
            radius: s.size(tokens::RADIUS),
            shadow: Some(tokens::SHADOW_COMPACT),
            hover_veil: 0.0,
        };
        let seg = Look {
            fill: Some(color::SEG),
            radius: s.size(tokens::RADIUS),
            hover_veil: tokens::HOVER_VEIL,
            ..Default::default()
        };
        let size = ButtonBox {
            height: button,
            min_length: s.size(tokens::BUTTON_MIN_LENGTH),
            pad_h: s.space(8.0),
            gap: s.space(4.0),
        };
        let caption = Widget::boxed(
            Layout {
                height: Extent::Px(button),
                padding: Edges::horizontal(s.space(10.0)),
                align: Align::Center,
                justify: Align::Center,
                ..Default::default()
            },
            Look::default(),
            vec![Widget::text(TextSpec {
                text: "100 \u{00D7} 100".into(),
                font_px,
                bold: true,
                color: color::FG_80,
                underline: None,
                hairline_px: hair,
            })],
        );
        let mut children = vec![
            Widget::emblem(
                axis,
                s.size(tokens::EMBLEM_LENGTH),
                IconSpec {
                    slot: 9,
                    px: s.size(tokens::EMBLEM_MARK),
                },
            ),
            caption,
        ];
        for (i, chars) in [6, 4, 5, 3, 7].into_iter().enumerate() {
            let text = TextSpec {
                text: "x".repeat(chars),
                font_px,
                bold: false,
                color: color::FG,
                underline: Some(0),
                hairline_px: hair,
            };
            children.push(Widget::button(
                WidgetId(i as u64),
                size,
                seg,
                IconSpec {
                    slot: i,
                    px: s.icon(tokens::ICON),
                },
                text,
            ));
        }
        Widget::stack(axis, pad, gap, min_across, look, children)
    }

    /// Walk `w` against the pre-order `nodes` starting at `*i`, asserting
    /// the gap-free invariants at every level, and return this node's rect.
    fn check(w: &Widget, nodes: &[Placed], i: &mut usize, dpi: f32) -> ScreenRect {
        let me = nodes[*i].rect;
        *i += 1;
        assert!(me.width() > 0 && me.height() > 0, "zero-size node at {dpi}: {me:?}");
        let mut prev: Option<ScreenRect> = None;
        for child in &w.children {
            let c = check(child, nodes, i, dpi);
            assert!(
                c.left() >= me.left() && c.right() <= me.right() && c.top() >= me.top() && c.bottom() <= me.bottom(),
                "child {c:?} escapes {me:?} at {dpi}"
            );
            if let Some(p) = prev {
                let apart = match w.layout.axis {
                    Axis::Row => c.left() - p.right(),
                    Axis::Column => c.top() - p.bottom(),
                };
                assert_eq!(apart, w.layout.gap, "siblings {p:?} and {c:?} at {dpi}");
            }
            prev = Some(c);
        }
        me
    }

    #[test]
    fn every_dpi_yields_integer_gap_free_rects() {
        for dpi in [1.0, 1.25, 1.5, 2.0] {
            let s = Scale::new(dpi);
            let pad = s.space(tokens::TRAY_PAD);
            for axis in [Axis::Row, Axis::Column] {
                let tree = strip(axis, s, 0);
                let scene = arrange(&tree, &mut mono_measure);
                let mut i = 0;
                let root = check(&tree, &scene.nodes, &mut i, dpi);
                assert_eq!(i, scene.nodes.len(), "every node visited");
                let last = scene
                    .nodes
                    .iter()
                    .rev()
                    .find(|n| n.id.is_some())
                    .unwrap()
                    .rect;
                let (far_child, far_root) = match axis {
                    Axis::Row => (last.right(), root.right()),
                    Axis::Column => (last.bottom(), root.bottom()),
                };
                assert_eq!(
                    far_root - far_child,
                    pad,
                    "last item is `pad` from the root's far edge at {dpi} {axis:?}"
                );
                assert_eq!((root.left(), root.top()), (0, 0), "arranged at the origin");
            }
            let row = arrange(&strip(Axis::Row, s, 0), &mut mono_measure);
            assert_eq!(
                row.bounds().height(),
                2 * pad + s.size(48.0),
                "row thickness is pad + item + pad at {dpi}"
            );
        }
    }

    #[test]
    fn mono_text_width_is_the_ceiling_of_the_advance() {
        for chars in 3..=13 {
            let exact = chars as f64 * 12.0 * 75.0 / 128.0;
            assert_eq!(mono_text_width(chars, 12), exact.ceil() as i32, "{chars} glyphs at 12");
        }
        assert_eq!(mono_text_width(6, 15), 53);
        assert_eq!(mono_text_width(6, 18), 64);
        assert_eq!(mono_text_width(6, 24), 85);
    }

    #[test]
    fn mono_line_is_the_floor_of_1_2_em() {
        for (font_px, line) in [(12, 14), (15, 18), (18, 21), (24, 28)] {
            let spec = label(6, font_px);
            assert_eq!(mono_measure(&spec), (mono_text_width(6, font_px), line));
        }
    }
}
