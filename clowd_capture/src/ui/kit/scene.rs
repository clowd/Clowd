//! The arranged tree as plain data: placed nodes with integer
//! virtual-desktop rects. Hit-testing and painting both read it, so the two
//! can never disagree.

use super::tree::{IconSpec, Look, TextSpec, WidgetId};
use clowd_rust_core::geometry::{RectExt, ScreenRect};

#[derive(Debug, Clone, PartialEq)]
pub enum Kind {
    Box(Look),
    Icon(IconSpec),
    Text(TextSpec),
}

#[derive(Debug, Clone, PartialEq)]
pub struct Placed {
    pub id: Option<WidgetId>,
    pub rect: ScreenRect,
    pub kind: Kind,
}

/// The arranged tree, flattened pre-order (a parent precedes its children, so
/// index order is paint order and reverse order is hit priority). Plain data:
/// `Send + Sync`, `Clone`.
#[derive(Debug, Clone, PartialEq)]
pub struct Scene {
    pub nodes: Vec<Placed>,
    /// Points outside this rect are dead to `hit_test` and `contains`, drawn
    /// or not. `arrange` sets it to the root rect; `place` widens it to the
    /// bounds it placed on.
    pub live: ScreenRect,
}

impl Scene {
    /// The root's rect; `arrange` never returns an empty scene.
    pub fn bounds(&self) -> ScreenRect {
        self.nodes[0].rect
    }

    /// Every rect AND `live`.
    pub fn translate(&mut self, dx: i32, dy: i32) {
        for n in &mut self.nodes {
            n.rect.origin.x += dx;
            n.rect.origin.y += dy;
        }
        self.live.origin.x += dx;
        self.live.origin.y += dy;
    }

    /// Id-carrying nodes, pre-order.
    pub fn ids(&self) -> impl Iterator<Item = WidgetId> + '_ {
        self.nodes.iter().filter_map(|n| n.id)
    }

    /// Floor both coords, half-open rects, `live` gate, deepest id-carrying
    /// node wins (the last one in pre-order that holds the point).
    pub fn hit_test(&self, x_vd: f32, y_vd: f32) -> Option<WidgetId> {
        let (px, py) = (x_vd.floor() as i32, y_vd.floor() as i32);
        if !rect_holds(self.live, px, py) {
            return None;
        }
        self.nodes
            .iter()
            .rev()
            .find(|n| n.id.is_some() && rect_holds(n.rect, px, py))
            .and_then(|n| n.id)
    }

    /// On the root box AND inside `live`: the dead-chassis test.
    pub fn contains(&self, x_vd: f32, y_vd: f32) -> bool {
        let (px, py) = (x_vd.floor() as i32, y_vd.floor() as i32);
        rect_holds(self.live, px, py) && rect_holds(self.bounds(), px, py)
    }

    #[cfg(test)]
    pub fn find(&self, id: WidgetId) -> Option<&Placed> {
        self.nodes.iter().find(|n| n.id == Some(id))
    }
}

/// Half-open point-in-rect test on already floored coordinates.
fn rect_holds(r: ScreenRect, px: i32, py: i32) -> bool {
    px >= r.left() && px < r.right() && py >= r.top() && py < r.bottom()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rect(x: i32, y: i32, w: i32, h: i32) -> ScreenRect {
        ScreenRect::from_xy_size(x, y, w, h)
    }

    fn node(id: Option<u64>, r: ScreenRect) -> Placed {
        Placed {
            id: id.map(WidgetId),
            rect: r,
            kind: Kind::Box(Look::default()),
        }
    }

    /// A dead 100 x 50 root at the origin holding an id 1 box at (10, 10)
    /// 30 x 30, itself holding an id 2 box at (15, 15) 10 x 10.
    fn nested() -> Scene {
        let root = rect(0, 0, 100, 50);
        Scene {
            nodes: vec![
                node(None, root),
                node(Some(1), rect(10, 10, 30, 30)),
                node(Some(2), rect(15, 15, 10, 10)),
            ],
            live: root,
        }
    }

    #[test]
    fn hit_returns_the_deepest_id() {
        let s = nested();
        assert_eq!(s.hit_test(16.0, 16.0), Some(WidgetId(2)));
        assert_eq!(s.hit_test(11.0, 11.0), Some(WidgetId(1)));
        assert_eq!(s.hit_test(30.0, 30.0), Some(WidgetId(1)));
    }

    #[test]
    fn hit_is_floor_and_half_open() {
        let s = nested();
        assert_eq!(s.hit_test(39.9, 39.9), Some(WidgetId(1)), "just inside the far edge");
        assert_eq!(s.hit_test(40.0, 20.0), None, "the far edge belongs to the gap");
        assert_eq!(s.hit_test(20.0, 40.0), None);
        assert_eq!(s.hit_test(9.5, 20.0), None, "9.5 floors to 9, outside");
        assert_eq!(s.hit_test(10.5, 10.5), Some(WidgetId(1)), "10.5 floors to 10, inside");
        assert_eq!(s.hit_test(24.5, 24.5), Some(WidgetId(2)));
        assert_eq!(s.hit_test(25.0, 25.0), Some(WidgetId(1)));
    }

    #[test]
    fn nodes_without_an_id_never_hit() {
        let s = nested();
        assert_eq!(s.hit_test(1.0, 1.0), None, "the root");
        assert_eq!(s.hit_test(99.0, 49.0), None, "the root's far corner");
        assert!(s.contains(1.0, 1.0), "but the root still contains the point");
    }

    #[test]
    fn points_outside_live_are_dead_for_hit_and_contains() {
        let mut s = nested();
        s.live = rect(0, 0, 20, 50);
        assert_eq!(s.hit_test(30.0, 30.0), None, "inside id 1 but outside live");
        assert!(!s.contains(30.0, 30.0));
        assert_eq!(s.hit_test(16.0, 16.0), Some(WidgetId(2)), "still live on the near side");
        assert!(s.contains(16.0, 16.0));
    }

    #[test]
    fn contains_covers_the_whole_root() {
        let s = nested();
        assert!(s.contains(0.0, 0.0));
        assert!(s.contains(99.99, 49.99));
        assert!(!s.contains(100.0, 25.0));
        assert!(!s.contains(25.0, 50.0));
        assert!(!s.contains(-0.5, 25.0));
    }

    #[test]
    fn translate_moves_every_rect_and_live() {
        let mut s = nested();
        s.translate(100, -7);
        assert_eq!(s.bounds(), rect(100, -7, 100, 50));
        assert_eq!(s.live, rect(100, -7, 100, 50));
        assert_eq!(s.find(WidgetId(1)).unwrap().rect, rect(110, 3, 30, 30));
        assert_eq!(s.find(WidgetId(2)).unwrap().rect, rect(115, 8, 10, 10));
        assert_eq!(s.hit_test(116.0, 9.0), Some(WidgetId(2)));
    }

    #[test]
    fn ids_lists_id_carrying_nodes_in_order() {
        let ids: Vec<WidgetId> = nested().ids().collect();
        assert_eq!(ids, vec![WidgetId(1), WidgetId(2)]);
    }

    #[test]
    fn find_locates_a_node_by_id() {
        let s = nested();
        assert_eq!(s.find(WidgetId(2)).map(|n| n.rect), Some(rect(15, 15, 10, 10)));
        assert_eq!(s.find(WidgetId(7)), None);
        assert_eq!(s.nodes[0].id, None);
    }
}
