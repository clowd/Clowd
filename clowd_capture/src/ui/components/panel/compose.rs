//! The thin composer: the panel's model (sets, features, button tables)
//! meets the generic kit here. This is the one file that knows both the
//! feature nouns and the tray shape. It builds a small widget tree in kit
//! vocabulary, lets the kit arrange and place it beside the selection, and
//! hands back a [`PanelScene`]: plain data that render threads paint and
//! the app thread hit-tests, so nothing downstream re-derives geometry.
//!
//! The tray is one stack holding, in order, the emblem (dead), the "W × H"
//! readout (dead) and one button per visible def. Orientation is the
//! stack's axis and nothing else: in a row every segment is `button` tall
//! and content-sized along the strip; in a column every segment is
//! stretched to the column's inner width, which is the root's `min_across`
//! taken from the union of both sets so a swap or a feature switch can
//! never change it.

use crate::selection::intersect_rects;
use crate::ui::command::Command;
use crate::ui::components::panel::model::{ButtonDef, PanelButtonSet, PanelFeatures, EMBLEM_SLOT};
use crate::ui::kit::arrange::{arrange, mono_measure};
use crate::ui::kit::place::{place, Fit, Footprint, Near, Side};
use crate::ui::kit::scene::Scene;
use crate::ui::kit::tokens::{self, color, Scale, SHADOW_COMPACT};
use crate::ui::kit::tree::{Align, Axis, ButtonBox, Edges, Extent, IconSpec, Layout, Look, TextSpec, Widget, WidgetId};
use crate::ui::shared::UiMonitor;
use clowd_rust_core::geometry::ScreenRect;

// The capture profile's own numbers (the design workbench), not kit tokens:
// the button thickness raised from the C# 40 to 48 so the icon-over-label
// stack fits, the button's horizontal padding, the readout's horizontal
// padding, the label / readout font size and the icon-to-label gap.
const BUTTON_HEIGHT: f32 = 48.0;
const BUTTON_PAD_H: f32 = 8.0;
const READOUT_PAD_H: f32 = 10.0;
const FONT_PX: f32 = 12.0;
const ICON_LABEL_GAP: f32 = 4.0;

/// The arranged, placed panel: what render threads paint and the app thread
/// hit-tests. Plain data behind an `Arc` in `UiSharedState`; the taffy tree
/// never left `arrange`.
#[derive(Debug, Clone)]
pub struct PanelScene {
    /// The one monitor that draws it; the renderer compares `bounds` to its own.
    pub monitor: UiMonitor,
    /// Rects in virtual-desktop px; `scene.live == monitor.bounds`.
    pub scene: Scene,
    /// Atlas key halves: the px the icons and the emblem mark were placed at.
    pub icon_px: u32,
    pub emblem_px: u32,
    /// Row or column. Only tests read it; the renderer has no orientation branch.
    #[cfg_attr(not(test), allow(dead_code))]
    pub side: Side,
    /// The strip as placed, in order: the only map from a hit id to a command.
    buttons: Vec<(WidgetId, &'static ButtonDef)>,
}

impl PanelScene {
    /// The command under a virtual-desktop point, resolved through the
    /// strip this scene was built from, so a hit can never be read against
    /// the other set (the id encodes its set) and a new button can never
    /// silently become a `None` hit.
    pub fn hit_test(&self, x_vd: f32, y_vd: f32) -> Option<Command> {
        let id = self.scene.hit_test(x_vd, y_vd)?;
        self.buttons
            .iter()
            .find(|(i, _)| *i == id)
            .map(|(_, d)| d.command)
    }

    /// On the tray body and on its monitor: the click the dead chassis swallows.
    pub fn contains(&self, x_vd: f32, y_vd: f32) -> bool {
        self.scene.contains(x_vd, y_vd)
    }

    #[cfg(test)]
    pub fn buttons(&self) -> &[(WidgetId, &'static ButtonDef)] {
        &self.buttons
    }
}

/// Set-encoded id: a swap yields all-new ids, so hover state and hit-tests
/// can never resolve an id against the other strip. Table index (not
/// visible index) so a switched-off feature does not renumber its neighbours.
fn button_id(set: PanelButtonSet, table_index: usize) -> WidgetId {
    WidgetId(((set as u64) << 32) | table_index as u64)
}

/// The buttons `set` shows under `features`, in strip order, each with its
/// id. Read through the model's one filtered view, so the strip on screen
/// and the accelerator lookup can never disagree about which buttons exist.
fn strip(set: PanelButtonSet, features: PanelFeatures) -> Vec<(WidgetId, &'static ButtonDef)> {
    set.visible_defs(features)
        .map(|(i, d)| (button_id(set, i), d))
        .collect()
}

/// `None` when the selection does not overlap the monitor.
pub fn compose(monitor: UiMonitor, selection: ScreenRect, set: PanelButtonSet, features: PanelFeatures) -> Option<PanelScene> {
    // Clipped for placement; the readout prints the UNCLIPPED size, so a
    // selection straddling two monitors keeps showing its true size.
    let anchor = intersect_rects(monitor.bounds, selection)?;
    let readout = format!("{} \u{00D7} {}", selection.width(), selection.height());
    let m = Metrics::new(Scale::new(monitor.dpi_scale));
    let fit = union_fit(&m, &readout);
    let buttons = strip(set, features);
    let (scene, side) = place(anchor, monitor.bounds, fit, m.near, |axis| {
        arrange(&tray(&m, axis, &readout, &buttons, fit.thick(axis)), &mut mono_measure)
    });
    Some(PanelScene {
        monitor,
        scene,
        icon_px: m.icon as u32,
        emblem_px: m.mark as u32,
        side,
        buttons,
    })
}

/// Every device-pixel integer the tree is built from, scaled once.
struct Metrics {
    pad: i32,
    gap: i32,
    emblem: i32,
    mark: i32,
    icon: i32,
    font_px: i32,
    readout_pad_h: i32,
    radius: i32,
    hair: i32,
    button: ButtonBox,
    near: Near,
}

impl Metrics {
    fn new(s: Scale) -> Self {
        Self {
            pad: s.space(tokens::TRAY_PAD),
            gap: s.space(tokens::GAP),
            emblem: s.size(tokens::EMBLEM_LENGTH),
            mark: s.size(tokens::EMBLEM_MARK),
            icon: s.icon(tokens::ICON),
            font_px: s.size(FONT_PX),
            readout_pad_h: s.space(READOUT_PAD_H),
            radius: s.size(tokens::RADIUS),
            hair: s.hair(),
            button: ButtonBox {
                height: s.size(BUTTON_HEIGHT),
                min_length: s.size(tokens::BUTTON_MIN_LENGTH),
                pad_h: s.space(BUTTON_PAD_H),
                gap: s.space(ICON_LABEL_GAP),
            },
            near: Near {
                min_distance: s.space(tokens::NEAR_MIN_DISTANCE),
                max_distance: s.space(tokens::NEAR_MAX_DISTANCE),
            },
        }
    }

    fn text(&self, text: String, bold: bool, color: [u8; 4], underline: Option<usize>) -> TextSpec {
        TextSpec {
            text,
            font_px: self.font_px,
            bold,
            color,
            underline,
            hairline_px: self.hair,
        }
    }
}

/// The tray tree for one orientation: emblem, readout, then `buttons`,
/// never thinner than `min_thick` across `axis`.
fn tray(m: &Metrics, axis: Axis, readout: &str, buttons: &[(WidgetId, &'static ButtonDef)], min_thick: i32) -> Widget {
    let chassis = Look {
        fill: Some(color::TRAY),
        ring: Some((color::RING, m.hair)),
        radius: m.radius,
        shadow: Some(SHADOW_COMPACT),
        hover_veil: 0.0,
    };
    let segment = Look {
        fill: Some(color::SEG),
        radius: m.radius,
        hover_veil: tokens::HOVER_VEIL,
        ..Look::default()
    };
    let readout = Widget::boxed(
        Layout {
            height: Extent::Px(m.button.height),
            padding: Edges::horizontal(m.readout_pad_h),
            align: Align::Center,
            justify: Align::Center,
            ..Layout::default()
        },
        Look::default(),
        vec![Widget::text(m.text(readout.to_owned(), true, color::FG_80, None))],
    );
    let mut children = vec![
        Widget::emblem(
            axis,
            m.emblem,
            IconSpec {
                slot: EMBLEM_SLOT,
                px: m.mark,
            },
        ),
        readout,
    ];
    children.extend(buttons.iter().map(|&(id, def)| {
        Widget::button(
            id,
            m.button,
            segment,
            IconSpec {
                slot: def.icon_id,
                px: m.icon,
            },
            m.text(def.label.to_owned(), false, color::FG, Some(def.underline_idx)),
        )
    }));
    Widget::stack(axis, m.pad, m.gap, min_thick, chassis, children)
}

/// The longest box either set can become in each orientation, with every
/// feature on, so the side choice and the column thickness never depend on
/// the set or the switches.
fn union_fit(m: &Metrics, readout: &str) -> Fit {
    let mut fit = Fit {
        row: Footprint {
            len: 0,
            thick: 0,
        },
        col: Footprint {
            len: 0,
            thick: 0,
        },
    };
    for &set in PanelButtonSet::ALL {
        let buttons = strip(set, PanelFeatures::ALL);
        let row = arrange(&tray(m, Axis::Row, readout, &buttons, 0), &mut mono_measure).bounds();
        let col = arrange(&tray(m, Axis::Column, readout, &buttons, 0), &mut mono_measure).bounds();
        fit.row.len = fit.row.len.max(row.width());
        fit.row.thick = fit.row.thick.max(row.height());
        fit.col.len = fit.col.len.max(col.height());
        fit.col.thick = fit.col.thick.max(col.width());
    }
    fit
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ui::components::panel::model::FEATURE_COMBINATIONS;
    use crate::ui::kit::scene::{Kind, Placed};
    use clowd_rust_core::geometry::RectExt;

    /// `(x, y, width, height)` in virtual-desktop px.
    type Xywh = (i32, i32, i32, i32);

    const DPIS: [f32; 4] = [1.0, 1.25, 1.5, 2.0];
    const HD: Xywh = (0, 0, 1920, 1080);
    /// The reference selection: 600 x 400 at (500, 300), comfortably inset,
    /// so the row goes beneath it.
    const SEL: Xywh = (500, 300, 600, 400);
    /// A monitor tall enough for the capture column at every standard DPI
    /// (620 / 775 / 930 / 1240 px) with a selection hugging its bottom,
    /// which forces the column for every set.
    const COL_MON: Xywh = (0, 0, 1920, 1300);
    const COL_SEL: Xywh = (100, 300, 400, 998);

    fn rect(t: Xywh) -> ScreenRect {
        ScreenRect::from_xy_size(t.0, t.1, t.2, t.3)
    }

    fn monitor(bounds: Xywh, dpi: f32) -> UiMonitor {
        UiMonitor {
            bounds: rect(bounds),
            dpi_scale: dpi,
            is_primary: true,
        }
    }

    fn panel(mon: Xywh, sel: Xywh, dpi: f32, set: PanelButtonSet, f: PanelFeatures) -> PanelScene {
        compose(monitor(mon, dpi), rect(sel), set, f).expect("the selection overlaps the monitor")
    }

    fn row(set: PanelButtonSet, dpi: f32) -> PanelScene {
        panel(HD, SEL, dpi, set, PanelFeatures::ALL)
    }

    fn column(set: PanelButtonSet, dpi: f32) -> PanelScene {
        panel(COL_MON, COL_SEL, dpi, set, PanelFeatures::ALL)
    }

    fn centre(r: ScreenRect) -> (f32, f32) {
        ((r.left() + r.width() / 2) as f32, (r.top() + r.height() / 2) as f32)
    }

    fn is_box(n: &Placed) -> bool {
        matches!(n.kind, Kind::Box(_))
    }

    /// The emblem mark: the one icon node in the emblem's atlas slot.
    fn emblem_mark(p: &PanelScene) -> ScreenRect {
        node_at(p, |n| matches!(n.kind, Kind::Icon(i) if i.slot == EMBLEM_SLOT)).rect
    }

    /// The readout text: the one bold text node.
    fn readout_text(p: &PanelScene) -> &Placed {
        node_at(p, |n| matches!(&n.kind, Kind::Text(t) if t.bold))
    }

    /// The box holding `inner`: the innermost box whose rect contains it.
    /// Both dead segments are one-child boxes, so this is their slot.
    fn slot_around(p: &PanelScene, inner: ScreenRect) -> ScreenRect {
        p.scene
            .nodes
            .iter()
            .filter(|n| is_box(n) && n.rect.contains_rect(&inner))
            .map(|n| n.rect)
            .min_by_key(|r| r.area())
            .expect("a box holds every leaf")
    }

    fn emblem_slot(p: &PanelScene) -> ScreenRect {
        slot_around(p, emblem_mark(p))
    }

    fn readout_box(p: &PanelScene) -> ScreenRect {
        slot_around(p, readout_text(p).rect)
    }

    fn node_at(p: &PanelScene, pred: impl Fn(&Placed) -> bool) -> &Placed {
        let mut it = p.scene.nodes.iter().filter(|n| pred(n));
        let found = it.next().expect("node present");
        assert!(it.next().is_none(), "node is unique");
        found
    }

    fn button_rects(p: &PanelScene) -> Vec<ScreenRect> {
        p.buttons()
            .iter()
            .map(|(id, _)| {
                p.scene
                    .find(*id)
                    .expect("every strip id is placed")
                    .rect
            })
            .collect()
    }

    fn button_by_label(p: &PanelScene, label: &str) -> ScreenRect {
        let (id, _) = p
            .buttons()
            .iter()
            .find(|(_, d)| d.label == label)
            .unwrap_or_else(|| panic!("{label} is on the strip"));
        p.scene.find(*id).unwrap().rect
    }

    /// The icon and the label of the button drawn at `button`, by kind and
    /// containment, never by pre-order index.
    fn button_content(p: &PanelScene, button: ScreenRect) -> (ScreenRect, &Placed) {
        let icon = node_at(p, |n| matches!(n.kind, Kind::Icon(_)) && button.contains_rect(&n.rect));
        let label = node_at(p, |n| matches!(n.kind, Kind::Text(_)) && button.contains_rect(&n.rect));
        (icon.rect, label)
    }

    fn pad(dpi: f32) -> i32 {
        Scale::new(dpi).space(tokens::TRAY_PAD)
    }

    fn side_axis(p: &PanelScene) -> Axis {
        p.side.axis()
    }

    #[test]
    fn segments_are_emblem_readout_then_visible_buttons_in_order() {
        for &set in PanelButtonSet::ALL {
            for f in FEATURE_COMBINATIONS {
                let p = panel(HD, SEL, 1.0, set, f);
                let placed: Vec<Command> = p
                    .buttons()
                    .iter()
                    .map(|(_, d)| d.command)
                    .collect();
                let visible: Vec<Command> = set
                    .visible_defs(f)
                    .map(|(_, d)| d.command)
                    .collect();
                assert_eq!(placed, visible, "{set:?} {f:?}");
                let ids: Vec<WidgetId> = p.scene.ids().collect();
                let strip_ids: Vec<WidgetId> = p
                    .buttons()
                    .iter()
                    .map(|(id, _)| *id)
                    .collect();
                assert_eq!(ids, strip_ids, "{set:?} {f:?}: only the buttons carry ids, in strip order");

                // Paint order after the root: emblem, readout, then the buttons.
                let boxes: Vec<ScreenRect> = p.scene.nodes[1..]
                    .iter()
                    .filter(|n| is_box(n))
                    .map(|n| n.rect)
                    .collect();
                let mut expected = vec![emblem_slot(&p), readout_box(&p)];
                expected.extend(button_rects(&p));
                assert_eq!(boxes, expected, "{set:?} {f:?}");
                match &readout_text(&p).kind {
                    Kind::Text(t) => assert_eq!(t.text, "600 \u{00D7} 400"),
                    other => panic!("{other:?}"),
                }
            }
        }
    }

    #[test]
    fn switched_off_buttons_have_no_node_and_the_strip_recentres() {
        let full = row(PanelButtonSet::Normal, 1.0);
        let p = panel(
            HD,
            SEL,
            1.0,
            PanelButtonSet::Normal,
            PanelFeatures {
                upload: false,
                ..PanelFeatures::ALL
            },
        );
        assert_eq!(
            p.scene
                .find(button_id(PanelButtonSet::Normal, 0)),
            None,
            "Upload has no node"
        );
        assert!(p
            .buttons()
            .iter()
            .all(|(_, d)| d.command != Command::Upload));
        let first = button_rects(&p)[0];
        let (cx, cy) = centre(first);
        assert_eq!(p.hit_test(cx, cy), Some(Command::Edit));
        assert!(p.scene.bounds().width() < full.scene.bounds().width());
        let sel_mid = SEL.0 + SEL.2 / 2;
        let mid = p.scene.bounds().left() + p.scene.bounds().width() / 2;
        assert!((mid - sel_mid).abs() <= 1, "strip midpoint {mid} vs selection {sel_mid}");
    }

    #[test]
    fn each_strip_is_centred_with_its_own_width() {
        let sel_mid = SEL.0 + SEL.2 / 2;
        for dpi in DPIS {
            let mut lefts = Vec::new();
            for &set in PanelButtonSet::ALL {
                let p = row(set, dpi);
                let tray = p.scene.bounds();
                assert_eq!(p.side, Side::Below, "{set:?} at {dpi}");
                let last = *button_rects(&p).last().unwrap();
                assert_eq!(last.right() + pad(dpi), tray.right(), "{set:?} at {dpi}");
                let mid = tray.left() + tray.width() / 2;
                assert!((mid - sel_mid).abs() <= 1, "{set:?} at {dpi}: midpoint {mid} vs {sel_mid}");
                lefts.push(tray.left());
            }
            assert!(lefts[1] > lefts[0], "at {dpi} the shorter Ocr strip starts further right");
        }
    }

    #[test]
    fn column_children_share_left_and_width_and_the_thickness_ignores_set_and_features() {
        let all_off = PanelFeatures {
            upload: false,
            share: false,
            scroll_capture: false,
            video: false,
            ocr: false,
        };
        for dpi in DPIS {
            let s = Scale::new(dpi);
            let (emblem, button, gap) = (s.size(tokens::EMBLEM_LENGTH), s.size(BUTTON_HEIGHT), s.space(tokens::GAP));
            for &set in PanelButtonSet::ALL {
                let p = column(set, dpi);
                let tray = p.scene.bounds();
                assert_eq!(p.side, Side::Right, "{set:?} at {dpi}");
                assert_eq!(tray.bottom(), rect(COL_SEL).bottom(), "{set:?} at {dpi}: bottom-anchored");
                let mut segments = vec![emblem_slot(&p), readout_box(&p)];
                segments.extend(button_rects(&p));
                for (i, r) in segments.iter().enumerate() {
                    assert_eq!(r.left(), tray.left() + pad(dpi), "{set:?} at {dpi}: segment {i} left");
                    assert_eq!(r.width(), tray.width() - 2 * pad(dpi), "{set:?} at {dpi}: segment {i} width");
                    assert_eq!(
                        r.height(),
                        if i == 0 { emblem } else { button },
                        "{set:?} at {dpi}: segment {i} height"
                    );
                    if i > 0 {
                        assert_eq!(r.top() - segments[i - 1].bottom(), gap, "{set:?} at {dpi}: segment {i} gap");
                    }
                }
            }
            let width = |set, f, sel| {
                panel(COL_MON, sel, dpi, set, f)
                    .scene
                    .bounds()
                    .width()
            };
            let normal = width(PanelButtonSet::Normal, PanelFeatures::ALL, COL_SEL);
            assert_eq!(width(PanelButtonSet::Ocr, PanelFeatures::ALL, COL_SEL), normal, "at {dpi}");
            assert_eq!(width(PanelButtonSet::Normal, all_off, COL_SEL), normal, "at {dpi}");
            // A tiny selection prints a short readout; the union's widest
            // label then sets the thickness, still for every set.
            let tiny = (100, 1293, 5, 5);
            let tiny_normal = width(PanelButtonSet::Normal, PanelFeatures::ALL, tiny);
            assert_eq!(width(PanelButtonSet::Ocr, PanelFeatures::ALL, tiny), tiny_normal, "at {dpi}");
            assert_eq!(width(PanelButtonSet::Normal, all_off, tiny), tiny_normal, "at {dpi}");
        }
    }

    #[test]
    fn side_is_independent_of_set_and_features() {
        let side = |mon, sel, dpi, set, f| panel(mon, sel, dpi, set, f).side;
        for &set in PanelButtonSet::ALL {
            for f in FEATURE_COMBINATIONS {
                // 1920 - 1827 - 2 = 91 < 92: no room on the right, so left.
                assert_eq!(side(HD, (927, 100, 900, 960), 1.0, set, f), Side::Left, "{set:?} {f:?}");
                // 600 wide cannot hold the 670 row: a column beside it.
                assert_eq!(
                    side((0, 0, 600, 700), (100, 100, 280, 100), 1.0, set, f),
                    Side::Right,
                    "{set:?} {f:?}"
                );
                // Portrait at 200 %: the 1330 row does not fit 1080, the column does.
                let portrait = panel((0, 0, 1080, 1920), (100, 100, 400, 400), 2.0, set, f);
                assert_eq!(portrait.side, Side::Right, "{set:?} {f:?}");
                assert!(rect((0, 0, 1080, 1920)).contains_rect(&portrait.scene.bounds()), "{set:?} {f:?}");
                // 420 - 400 - 2 = 18 leaves no row below and 420 tall cannot
                // hold the 620 column, so the row is pulled inside.
                assert_eq!(
                    side((0, 0, 1920, 420), (100, 100, 400, 300), 1.0, set, f),
                    Side::Inside,
                    "{set:?} {f:?}"
                );
                // At 200 % the 1240 column outgrows a 1080 display.
                assert_eq!(side(HD, COL_SEL, 2.0, set, f), Side::Inside, "{set:?} {f:?}");
            }
        }
    }

    #[test]
    fn off_monitor_points_are_dead_but_the_tail_stays_reachable() {
        let cases = [
            ("row on 600x300", (0, 0, 600, 300), (100, 50, 400, 100), 1.0, Axis::Row),
            ("column on 600x500", (0, 0, 600, 500), (100, 100, 400, 380), 1.0, Axis::Column),
            ("row on 960x600 at 150 %", (0, 0, 960, 600), (200, 100, 500, 300), 1.5, Axis::Row),
        ];
        for (name, mon, sel, dpi, axis) in cases {
            let p = panel(mon, sel, dpi, PanelButtonSet::Normal, PanelFeatures::ALL);
            let (mon, tray) = (rect(mon), p.scene.bounds());
            assert_eq!(side_axis(&p), axis, "{name}");
            let last = *button_rects(&p).last().unwrap();
            assert!(mon.contains_rect(&last), "{name}: the last button is whole");
            let (off, on) = match axis {
                Axis::Row => {
                    assert!(
                        tray.left() < mon.left() && tray.right() == mon.right(),
                        "{name}: the head overflows"
                    );
                    assert!(emblem_slot(&p).left() < mon.left(), "{name}");
                    let y = centre(tray).1;
                    (((mon.left() - 1) as f32, y), (mon.left() as f32, y))
                }
                Axis::Column => {
                    assert!(
                        tray.top() < mon.top() && tray.bottom() == mon.bottom(),
                        "{name}: the head overflows"
                    );
                    assert!(emblem_slot(&p).top() < mon.top(), "{name}");
                    let x = centre(tray).0;
                    ((x, (mon.top() - 1) as f32), (x, mon.top() as f32))
                }
            };
            assert_eq!(p.hit_test(off.0, off.1), None, "{name}");
            assert!(!p.contains(off.0, off.1), "{name}: inside the tray but off the monitor");
            assert!(p.contains(on.0, on.1), "{name}: the first on-monitor pixel");
            for (i, b) in button_rects(&p).iter().enumerate() {
                let (cx, cy) = centre(*b);
                let (vx, vy) = (cx.max(mon.left() as f32), cy.max(mon.top() as f32));
                assert_eq!(p.hit_test(vx, vy), Some(p.buttons()[i].1.command), "{name}: button {i}");
                if b.left() < mon.left() {
                    assert_eq!(p.hit_test(b.left() as f32, cy), None, "{name}: button {i} off-monitor part");
                }
                if b.top() < mon.top() {
                    assert_eq!(p.hit_test(cx, b.top() as f32), None, "{name}: button {i} off-monitor part");
                }
            }
        }
    }

    #[test]
    fn emblem_and_readout_are_contained_but_dead() {
        for p in [row(PanelButtonSet::Normal, 1.0), column(PanelButtonSet::Normal, 1.0)] {
            let tray = p.scene.bounds();
            let first = button_rects(&p)[0];
            let gap_pt = match side_axis(&p) {
                Axis::Row => (first.right() as f32, centre(first).1),
                Axis::Column => (centre(first).0, first.bottom() as f32),
            };
            for r in [emblem_slot(&p), readout_box(&p)] {
                assert!(tray.contains_rect(&r), "{:?}", p.side);
            }
            let probes = [
                centre(emblem_slot(&p)),
                centre(readout_box(&p)),
                ((tray.left() + 1) as f32, (tray.top() + 1) as f32),
                gap_pt,
            ];
            for (i, (x, y)) in probes.into_iter().enumerate() {
                assert_eq!(p.hit_test(x, y), None, "{:?} probe {i} hit a button", p.side);
                assert!(p.contains(x, y), "{:?} probe {i} is not on the tray", p.side);
            }
            let (left, top, right) = (tray.left() as f32, tray.top() as f32, tray.right() as f32);
            assert!(p.contains(left, top));
            assert!(!p.contains(left - 1.0, top));
            assert!(!p.contains(right, top));
            assert!(p.contains(right - 0.5, top));
        }
    }

    #[test]
    fn emblem_mark_is_centred_in_its_slot() {
        for dpi in DPIS {
            for p in [row(PanelButtonSet::Normal, dpi), column(PanelButtonSet::Normal, dpi)] {
                let (slot, mark) = (emblem_slot(&p), emblem_mark(&p));
                let s = Scale::new(dpi);
                assert_eq!(mark.size, euclid::size2(s.size(tokens::EMBLEM_MARK), s.size(tokens::EMBLEM_MARK)));
                assert_eq!(
                    mark.left(),
                    slot.left() + (slot.width() - mark.width()) / 2,
                    "{:?} at {dpi}",
                    p.side
                );
                assert_eq!(
                    mark.top(),
                    slot.top() + (slot.height() - mark.height()) / 2,
                    "{:?} at {dpi}",
                    p.side
                );
                assert_eq!(p.emblem_px, mark.width() as u32);
            }
        }
    }

    #[test]
    fn hit_test_agrees_with_every_button_rect_edge() {
        for dpi in DPIS {
            for &set in PanelButtonSet::ALL {
                for p in [row(set, dpi), column(set, dpi)] {
                    for (i, (r, (_, def))) in button_rects(&p)
                        .iter()
                        .zip(p.buttons())
                        .enumerate()
                    {
                        let want = Some(def.command);
                        let tag = format!("{set:?} {:?} at {dpi}: button {i}", p.side);
                        let (l, t, rt, b) = (r.left() as f32, r.top() as f32, r.right() as f32, r.bottom() as f32);
                        assert_eq!(p.hit_test(l, t), want, "{tag} top-left");
                        assert_eq!(p.hit_test(rt - 1.0, b - 1.0), want, "{tag} bottom-right pixel");
                        assert_eq!(p.hit_test(rt - 0.01, b - 0.01), want, "{tag} sub-pixel inside");
                        assert_eq!(p.hit_test(l + 0.5, t + 0.5), want, "{tag} half-pixel inside");
                        assert_eq!(p.hit_test(rt, t), None, "{tag} right edge is the gap");
                        assert_eq!(p.hit_test(l, b), None, "{tag} bottom edge is the gap");
                        assert_ne!(p.hit_test(l - 0.5, t), want, "{tag} just before the left edge");
                        assert_ne!(p.hit_test(l, t - 0.5), want, "{tag} just above the top edge");
                    }
                }
            }
        }
    }

    #[test]
    fn compose_is_none_when_the_selection_misses_the_monitor() {
        let mon = monitor(HD, 1.0);
        assert!(compose(mon, rect((2000, 100, 50, 50)), PanelButtonSet::Normal, PanelFeatures::ALL).is_none());
        assert!(
            compose(mon, rect((1920, 100, 50, 50)), PanelButtonSet::Normal, PanelFeatures::ALL).is_none(),
            "edge-adjacent"
        );
        assert!(compose(mon, rect((1919, 100, 50, 50)), PanelButtonSet::Normal, PanelFeatures::ALL).is_some());
    }

    #[test]
    fn readout_uses_the_unclipped_selection() {
        // Straddles the left edge: placed for the 4000 px clipped part, but
        // prints the true 10000.
        let p = panel(HD, (-6000, 300, 10000, 400), 1.0, PanelButtonSet::Normal, PanelFeatures::ALL);
        match &readout_text(&p).kind {
            Kind::Text(t) => assert_eq!(t.text, "10000 \u{00D7} 400"),
            other => panic!("{other:?}"),
        }
        // 11 glyphs at 12 px = 78, plus 2 x 10 of padding.
        assert_eq!(readout_box(&p).width(), 98);
        assert!(rect(HD).contains_rect(&p.scene.bounds()));
    }

    #[test]
    fn tray_thickness_is_pad_item_pad() {
        for (dpi, want) in DPIS.into_iter().zip([56, 70, 84, 112]) {
            let s = Scale::new(dpi);
            let p = row(PanelButtonSet::Normal, dpi);
            assert_eq!(p.scene.bounds().height(), 2 * pad(dpi) + s.size(BUTTON_HEIGHT), "at {dpi}");
            assert_eq!(p.scene.bounds().height(), want, "at {dpi}");
        }
    }

    #[test]
    fn button_ids_encode_the_set() {
        assert_ne!(button_id(PanelButtonSet::Ocr, 0), button_id(PanelButtonSet::Normal, 0));
        assert_eq!(button_id(PanelButtonSet::Normal, 3), button_id(PanelButtonSet::Normal, 3));
        let normal = row(PanelButtonSet::Normal, 1.0);
        let ocr = row(PanelButtonSet::Ocr, 1.0);
        let upload = |p: &PanelScene| {
            p.buttons()
                .iter()
                .find(|(_, d)| d.label == "Upload")
                .unwrap()
                .0
        };
        assert_ne!(upload(&normal), upload(&ocr));
        assert_eq!(upload(&normal), button_id(PanelButtonSet::Normal, 0));
    }

    #[test]
    fn atlas_key_and_monitor_ride_on_the_scene() {
        for dpi in DPIS {
            let s = Scale::new(dpi);
            let p = row(PanelButtonSet::Normal, dpi);
            assert_eq!(p.icon_px, s.icon(tokens::ICON) as u32, "at {dpi}");
            assert_eq!(p.emblem_px, s.size(tokens::EMBLEM_MARK) as u32, "at {dpi}");
            assert_eq!(p.monitor.bounds, rect(HD));
            assert_eq!(p.monitor.dpi_scale, dpi);
            assert_eq!(p.scene.live, p.monitor.bounds);
            let (icon, _) = button_content(&p, button_rects(&p)[0]);
            assert_eq!(icon.width() as u32, p.icon_px, "at {dpi}");
        }
    }

    /// The one literal table: the pinned 100 % geometry of the tray design.
    /// Every other test asserts a relation; a number that moves here is a
    /// design change to be documented, not an assertion to be loosened.
    #[test]
    fn parity_at_100_percent() {
        let p = row(PanelButtonSet::Normal, 1.0);
        assert_eq!(p.side, Side::Below);
        assert_eq!(p.scene.bounds(), rect((465, 715, 670, 56)));
        assert_eq!(emblem_slot(&p), rect((469, 719, 40, 48)));
        assert_eq!(emblem_mark(&p), rect((473, 727, 32, 32)));
        assert_eq!(readout_box(&p), rect((513, 719, 84, 48)));
        assert_eq!(readout_text(&p).rect.origin, euclid::point2(523, 736));

        const BUTTONS: [(&str, i32, i32); 10] = [
            ("Upload", 601, 59),
            ("Edit", 664, 45),
            ("Video", 713, 52),
            ("Share", 769, 52),
            ("Scroll", 825, 59),
            ("Copy", 888, 45),
            ("Save", 937, 45),
            ("OCR", 986, 40),
            ("Reset", 1030, 52),
            ("Exit", 1086, 45),
        ];
        assert_eq!(p.buttons().len(), BUTTONS.len());
        for (label, x, w) in BUTTONS {
            assert_eq!(button_by_label(&p, label), rect((x, 719, w, 48)), "{label}");
        }
        let (icon, label) = button_content(&p, button_by_label(&p, "Upload"));
        assert_eq!(icon, rect((620, 724, 20, 20)));
        assert_eq!(label.rect.origin, euclid::point2(609, 748));
        assert_eq!(button_by_label(&p, "Exit").right(), 1131);
        assert_eq!(p.scene.bounds().right(), 1135);

        assert_eq!(row(PanelButtonSet::Ocr, 1.0).scene.bounds(), rect((596, 715, 409, 56)));

        let c = column(PanelButtonSet::Normal, 1.0);
        assert_eq!(c.side, Side::Right);
        assert_eq!(c.scene.bounds().size, euclid::size2(92, 620));
    }
}
