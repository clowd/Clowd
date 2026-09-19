//! Per-id hover veil animation.

use super::tree::WidgetId;

struct Entry {
    id: WidgetId,
    amount: f32,
    /// Set by the latest `advance` for every id it saw in `live`.
    seen: bool,
}

/// Per-id linear fade toward 1 (hovered) or 0. Ids absent from `live` are
/// dropped, so a node that leaves the scene takes its veil with it. No
/// allocation per frame beyond growth.
pub struct HoverFade {
    fade_secs: f32,
    entries: Vec<Entry>,
}

impl HoverFade {
    pub fn new(fade_secs: f32) -> Self {
        Self {
            fade_secs,
            entries: Vec::new(),
        }
    }

    pub fn clear(&mut self) {
        self.entries.clear();
    }

    /// Step every live id by `dt_secs / fade_secs` toward its target,
    /// snapping when within one step; `hovered` is the id at target 1.
    pub fn advance(&mut self, dt_secs: f32, hovered: Option<WidgetId>, live: impl Iterator<Item = WidgetId>) {
        for e in &mut self.entries {
            e.seen = false;
        }
        for id in live {
            match self.entries.iter_mut().find(|e| e.id == id) {
                Some(e) => e.seen = true,
                None => self.entries.push(Entry {
                    id,
                    amount: 0.0,
                    seen: true,
                }),
            }
        }
        self.entries.retain(|e| e.seen);
        let step = dt_secs / self.fade_secs;
        for e in &mut self.entries {
            let target = if Some(e.id) == hovered { 1.0 } else { 0.0 };
            let diff = target - e.amount;
            if diff.abs() <= step {
                e.amount = target;
            } else {
                e.amount += step.copysign(diff);
            }
        }
    }

    /// 0 for unknown ids.
    pub fn amount(&self, id: WidgetId) -> f32 {
        self.entries
            .iter()
            .find(|e| e.id == id)
            .map_or(0.0, |e| e.amount)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ui::kit::tokens::HOVER_FADE_SECS;

    const A: WidgetId = WidgetId(1);
    const B: WidgetId = WidgetId(2);

    fn live() -> impl Iterator<Item = WidgetId> {
        [A, B].into_iter()
    }

    #[test]
    fn fades_linearly_and_snaps_within_one_step() {
        let mut h = HoverFade::new(HOVER_FADE_SECS);
        h.advance(0.045, Some(A), live());
        assert!((h.amount(A) - 0.25).abs() < 1e-6, "45 ms of 180 is a quarter");
        assert_eq!(h.amount(B), 0.0);
        h.advance(0.045, Some(A), live());
        h.advance(0.045, Some(A), live());
        assert!((h.amount(A) - 0.75).abs() < 1e-6);
        h.advance(0.03, Some(A), live());
        assert!((h.amount(A) - 0.9167).abs() < 1e-3);
        h.advance(0.03, Some(A), live());
        assert_eq!(h.amount(A), 1.0, "within one step of the target it snaps, never overshoots");
        h.advance(0.09, None, live());
        assert!((h.amount(A) - 0.5).abs() < 1e-6, "and fades back at the same rate");
        h.advance(0.09, Some(B), live());
        assert_eq!((h.amount(A), h.amount(B)), (0.0, 0.5));
    }

    #[test]
    fn unknown_id_is_zero() {
        let mut h = HoverFade::new(HOVER_FADE_SECS);
        assert_eq!(h.amount(A), 0.0);
        h.advance(1.0, Some(WidgetId(9)), live());
        assert_eq!(h.amount(WidgetId(9)), 0.0, "hovered but not live: never tracked");
        assert_eq!(h.amount(A), 0.0);
    }

    #[test]
    fn ids_absent_from_live_are_dropped() {
        let mut h = HoverFade::new(HOVER_FADE_SECS);
        h.advance(1.0, Some(A), live());
        assert_eq!(h.amount(A), 1.0);
        h.advance(0.0, Some(A), [B].into_iter());
        assert_eq!(h.amount(A), 0.0, "A left the scene and took its veil with it");
        h.advance(0.0, Some(A), live());
        assert_eq!(h.amount(A), 0.0, "and comes back at zero");
    }

    #[test]
    fn clear_zeroes_everything() {
        let mut h = HoverFade::new(HOVER_FADE_SECS);
        h.advance(1.0, Some(A), live());
        h.clear();
        assert_eq!((h.amount(A), h.amount(B)), (0.0, 0.0));
        h.advance(0.0, None, live());
        assert_eq!(h.amount(A), 0.0);
    }
}
