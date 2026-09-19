//! The retained widget tree an owner builds in kit vocabulary. Every extent
//! is an integer device pixel: scale tokens with [`super::tokens::Scale`]
//! first, then build.

use super::tokens::Shadow;

/// Owner-assigned identity of a hittable / animated node. The kit never mints
/// ids; a node with an id is hittable, a node without one is dead.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct WidgetId(pub u64);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Axis {
    Row,
    Column,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Extent {
    Auto,
    Px(i32),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Align {
    Start,
    Center,
    Stretch,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Edges {
    pub left: i32,
    pub right: i32,
    pub top: i32,
    pub bottom: i32,
}

impl Edges {
    pub const fn all(v: i32) -> Self {
        Self {
            left: v,
            right: v,
            top: v,
            bottom: v,
        }
    }

    /// `v` on the left and right, nothing above or below.
    pub const fn horizontal(v: i32) -> Self {
        Self {
            left: v,
            right: v,
            top: 0,
            bottom: 0,
        }
    }
}

/// Layout facts in taffy's own frame (width/height), integer device px.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Layout {
    /// Direction of this node's children.
    pub axis: Axis,
    pub width: Extent,
    pub height: Extent,
    /// 0 = none.
    pub min_width: i32,
    pub min_height: i32,
    pub padding: Edges,
    /// Written to BOTH taffy gap axes so a flipped strip keeps it.
    pub gap: i32,
    /// Cross axis (`align_items`).
    pub align: Align,
    /// Main axis (`justify_content`); `Stretch` reads as `Start`.
    pub justify: Align,
}

impl Default for Layout {
    fn default() -> Self {
        Self {
            axis: Axis::Row,
            width: Extent::Auto,
            height: Extent::Auto,
            min_width: 0,
            min_height: 0,
            padding: Edges::default(),
            gap: 0,
            align: Align::Stretch,
            justify: Align::Start,
        }
    }
}

impl Layout {
    /// Fixed extent ALONG `axis`, auto across: the one orientation-aware size helper.
    pub fn along(self, axis: Axis, px: i32) -> Self {
        match axis {
            Axis::Row => Self {
                width: Extent::Px(px),
                ..self
            },
            Axis::Column => Self {
                height: Extent::Px(px),
                ..self
            },
        }
    }

    /// Minimum extent ACROSS `axis` (a column's width, a row's height).
    pub fn min_across(self, axis: Axis, px: i32) -> Self {
        match axis {
            Axis::Row => Self {
                min_height: px,
                ..self
            },
            Axis::Column => Self {
                min_width: px,
                ..self
            },
        }
    }
}

/// Paint facts of a box. A bare box paints nothing.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Look {
    pub fill: Option<[f32; 4]>,
    /// Colour and thickness in px.
    pub ring: Option<([f32; 4], i32)>,
    pub radius: i32,
    /// Logical; the painter scales it by the DPI.
    pub shadow: Option<Shadow>,
    /// Lighten at hover == 1.0; 0 = never.
    pub hover_veil: f32,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TextSpec {
    pub text: String,
    pub font_px: i32,
    pub bold: bool,
    pub color: [u8; 4],
    /// Byte index of the glyph to underline with a hairline.
    pub underline: Option<usize>,
    /// Underline thickness; rides here so the painter never re-rounds the DPI.
    pub hairline_px: i32,
}

/// `slot` is the caller's atlas index; `px` the cell the mark is drawn in.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct IconSpec {
    pub slot: usize,
    pub px: i32,
}

/// The geometry every button of one strip shares, in device px: `height`
/// tall, never shorter than `min_length` along a row, `pad_h` each side of
/// the label, `gap` between icon and label.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ButtonBox {
    pub height: i32,
    pub min_length: i32,
    pub pad_h: i32,
    pub gap: i32,
}

#[derive(Debug, Clone, PartialEq)]
pub enum Content {
    None,
    Icon(IconSpec),
    Text(TextSpec),
}

#[derive(Debug, Clone, PartialEq)]
pub struct Widget {
    pub id: Option<WidgetId>,
    pub layout: Layout,
    pub look: Look,
    /// Leaves only; a node with children must be `Content::None`.
    pub content: Content,
    pub children: Vec<Widget>,
}

impl Widget {
    /// A strip: children along `axis`, `pad` inside, `gap` between,
    /// cross-stretched, never thinner than `min_across` across the axis
    /// (0 = no minimum).
    pub fn stack(axis: Axis, pad: i32, gap: i32, min_across: i32, look: Look, children: Vec<Widget>) -> Self {
        let layout = Layout {
            axis,
            padding: Edges::all(pad),
            gap,
            align: Align::Stretch,
            justify: Align::Start,
            ..Default::default()
        }
        .min_across(axis, min_across);
        Self::boxed(layout, look, children)
    }

    /// A generic box.
    pub fn boxed(layout: Layout, look: Look, children: Vec<Widget>) -> Self {
        Self {
            id: None,
            layout,
            look,
            content: Content::None,
            children,
        }
    }

    /// Icon stacked over label, both centred, in a [`ButtonBox`]. Hittable.
    /// ONE column stack, not a row wrapping a column: a second nesting level
    /// would add a second fractional centring level for nothing.
    pub fn button(id: WidgetId, size: ButtonBox, look: Look, icon: IconSpec, label: TextSpec) -> Self {
        let layout = Layout {
            axis: Axis::Column,
            width: Extent::Auto,
            height: Extent::Px(size.height),
            min_width: size.min_length,
            min_height: 0,
            padding: Edges::horizontal(size.pad_h),
            gap: size.gap,
            align: Align::Center,
            justify: Align::Center,
        };
        Self {
            id: Some(id),
            layout,
            look,
            content: Content::None,
            children: vec![Self::icon(icon), Self::text(label)],
        }
    }

    /// A `length`-long slot along `axis` (auto across) with `mark` centred in it. Dead.
    pub fn emblem(axis: Axis, length: i32, mark: IconSpec) -> Self {
        let layout = Layout {
            align: Align::Center,
            justify: Align::Center,
            ..Default::default()
        }
        .along(axis, length);
        Self::boxed(layout, Look::default(), vec![Self::icon(mark)])
    }

    pub fn text(spec: TextSpec) -> Self {
        Self {
            id: None,
            layout: Layout::default(),
            look: Look::default(),
            content: Content::Text(spec),
            children: Vec::new(),
        }
    }

    pub fn icon(spec: IconSpec) -> Self {
        Self {
            id: None,
            layout: Layout::default(),
            look: Look::default(),
            content: Content::Icon(spec),
            children: Vec::new(),
        }
    }
}
