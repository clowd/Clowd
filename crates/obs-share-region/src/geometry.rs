//! Pure frame geometry: border band, hollow interior, handle-cluster
//! placement and hit-testing. Everything is in capture space (top-left
//! origin, y-down — the same space as `--region` and `MonitorInfo::x/y`).
//! No platform or OBS deps; fully unit-tested like `obs_platform::region`.
//!
//! All arithmetic that combines coordinates runs in i64: virtual-desktop
//! coords can be negative and spans can overflow i32, mirroring
//! `region.rs`'s `rects_intersect`.

use obs_platform::region::Rect;

/// Minimum region width/height a resize drag may shrink to.
pub const MIN_REGION: u32 = 64;
/// Side of the square close/move buttons.
pub const HANDLE_BUTTON: u32 = 30;
/// Gap between the border band and the handle cluster.
pub const HANDLE_GAP: u32 = 8;
/// Cluster inner padding (around and between the buttons).
pub const HANDLE_PAD: u32 = 4;
/// Minimum side of the corner grab squares (a thin border would otherwise
/// make diagonal resizing nearly impossible to hit).
pub const CORNER_GRAB: u32 = 16;

/// Thickness of the white inner line, in logical (DPI-independent) px.
/// Fixed, unlike the accent line, because it is a hairline highlight rather
/// than the border proper — see [`BorderSpec`].
pub const LOGICAL_WHITE: u32 = 1;

/// The border's two lines, in capture units, already DPI-scaled and snapped
/// to whole device pixels by the platform layer.
///
/// Mirrors Clowd's `BorderWindow.axaml`, which nests a 1px white `Border`
/// inside a 2px accent one: reading outward from the captured region you get
/// the white hairline first, then the accent. The white line is what keeps the
/// accent legible against arbitrary desktop content underneath.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BorderSpec {
    /// White hairline, immediately outside the region (inside the accent).
    pub white: u32,
    /// Accent line, outside the white one.
    pub accent: u32,
}

impl BorderSpec {
    /// Scales the logical design (`LOGICAL_WHITE` white + `accent_logical`
    /// accent) by `scale`, where 1.0 is 96 dpi / non-Retina.
    ///
    /// Each line is rounded to whole device pixels *independently* rather than
    /// scaling the total and splitting it, so neither line can round away and
    /// the boundary between them always lands on a pixel edge — a border this
    /// thin looks wrong the moment it is allowed to blur across one. Both are
    /// floored at 1: a line that rounds to zero is a line that vanishes.
    ///
    /// Worked example at 150% (scale 1.5): white 1→2, accent 2→3. The exact
    /// ratio drifts (2:3 rather than 1:2) because there is no way to honour
    /// both the ratio and the pixel grid at fractional scales; sharpness wins,
    /// since the alternative is a grey smear where the two lines meet.
    pub fn scaled(scale: f64, accent_logical: u32) -> Self {
        let snap = |logical: u32| ((logical as f64 * scale).round() as u32).max(1);
        BorderSpec {
            white: snap(LOGICAL_WHITE),
            accent: snap(accent_logical),
        }
    }

    /// Combined thickness of both lines.
    pub fn total(self) -> u32 {
        self.white + self.accent
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Dir {
    N,
    S,
    E,
    W,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Cor {
    NW,
    NE,
    SW,
    SE,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Zone {
    /// Not ours: outside the frame, or in the hollow interior. The platform
    /// layer must let these clicks fall through to whatever is underneath.
    Outside,
    /// Border band / cluster background → move drag.
    Caption,
    Edge(Dir),
    Corner(Cor),
    CloseButton,
    /// Behaves like Caption for dragging, but shows a SizeAll-style cursor.
    MoveHandle,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FrameLayout {
    /// Frame window bounds = union(band, cluster).
    pub outer: Rect,
    /// Border ring outer rect: region inflated by `1 + border.total()` per
    /// side. The extra 1 unit is rounding slack (plan §6.4): logical-to-physical
    /// rounding must never place a border pixel inside the captured area.
    pub band: Rect,
    /// Outer edge of the white hairline: `hole` inflated by `border.white`.
    /// The two lines are therefore `white_band` minus `hole` (white) and
    /// `band` minus `white_band` (accent).
    pub white_band: Rect,
    /// Hollow interior: region inflated by the 1 slack unit. Strictly
    /// contains the region, so no painted pixel can ever land inside it.
    pub hole: Rect,
    /// Handle cluster background (contains both buttons).
    pub cluster: Rect,
    /// Move handle: left button in the cluster.
    pub move_btn: Rect,
    /// Close "X": right button, outermost like a native caption control.
    pub close_btn: Rect,
}

// ---- i64 rect helpers ------------------------------------------------------
// Rect is foreign (obs_platform), so these are free functions, not methods.

fn x2(r: &Rect) -> i64 {
    r.x as i64 + r.w as i64
}

fn y2(r: &Rect) -> i64 {
    r.y as i64 + r.h as i64
}

/// Half-open containment: [x, x+w) × [y, y+h), consistent with how
/// `rects_intersect` treats touching edges as non-overlapping.
fn contains(r: &Rect, p: (i32, i32)) -> bool {
    let (px, py) = (p.0 as i64, p.1 as i64);
    px >= r.x as i64 && px < x2(r) && py >= r.y as i64 && py < y2(r)
}

/// Builds a Rect from i64 corners, clamping into i32/u32 range so
/// pathological input degrades instead of wrapping. Real screen coords never
/// get near these limits.
fn rect_i64(x1: i64, y1: i64, rx2: i64, ry2: i64) -> Rect {
    let cx1 = x1.clamp(i32::MIN as i64, i32::MAX as i64);
    let cy1 = y1.clamp(i32::MIN as i64, i32::MAX as i64);
    let cx2 = rx2.clamp(i32::MIN as i64, i32::MAX as i64);
    let cy2 = ry2.clamp(i32::MIN as i64, i32::MAX as i64);
    Rect {
        x: cx1 as i32,
        y: cy1 as i32,
        w: (cx2 - cx1).max(0) as u32,
        h: (cy2 - cy1).max(0) as u32,
    }
}

fn inflate(r: Rect, d: u32) -> Rect {
    let d = d as i64;
    rect_i64(r.x as i64 - d, r.y as i64 - d, x2(&r) + d, y2(&r) + d)
}

fn union(a: &Rect, b: &Rect) -> Rect {
    rect_i64(
        (a.x as i64).min(b.x as i64),
        (a.y as i64).min(b.y as i64),
        x2(a).max(x2(b)),
        y2(a).max(y2(b)),
    )
}

fn intersect_area(a: &Rect, b: &Rect) -> i64 {
    let w = x2(a).min(x2(b)) - (a.x as i64).max(b.x as i64);
    let h = y2(a).min(y2(b)) - (a.y as i64).max(b.y as i64);
    if w > 0 && h > 0 {
        w * h
    } else {
        0
    }
}

/// Squared gap between two rects (0 when they touch or overlap). Used only
/// to rank "nearest work area" for the clamp fallback, so squared is fine.
fn rect_distance_sq(a: &Rect, b: &Rect) -> i64 {
    let dx = (b.x as i64 - x2(a)).max(a.x as i64 - x2(b)).max(0);
    let dy = (b.y as i64 - y2(a)).max(a.y as i64 - y2(b)).max(0);
    dx * dx + dy * dy
}

// ---- layout ----------------------------------------------------------------

/// Computes the frame layout for `region`.
///
/// `band` = region inflated outward by `1 + border.total()` per side (1 =
/// rounding slack, plan §6.4: no border pixel may ever land inside the
/// region); `hole` = region inflated by 1; `white_band` = hole inflated by
/// `border.white`, splitting the ring into the white hairline and the accent
/// line outside it.
///
/// The cluster is `HANDLE_PAD*2 + HANDLE_BUTTON*2 + HANDLE_PAD` (between)
/// wide and `HANDLE_BUTTON + 2*HANDLE_PAD` tall, placed `HANDLE_GAP` outside
/// the band. Candidates in priority order:
///   1 above band, right-aligned;    2 below band, right-aligned;
///   3 right of band, top-aligned;   4 left of band, top-aligned;
///   5 above, left-aligned;          6 below, left-aligned;
///   7 right of band, bottom-aligned; 8 left of band, bottom-aligned.
///
/// Visibility score = MAX over `work_areas` of the candidate's intersection
/// area with each one (i64). This deliberately under-counts a candidate that
/// straddles two work areas — computing the true union-area is not worth the
/// complexity, and a straddling cluster looks wrong anyway (half of it may
/// change scale/theme mid-button), so ranking it below a fully-contained
/// candidate is the behavior we want.
///
/// The first candidate whose visible area equals its full area wins;
/// otherwise the best-scoring one (earlier candidate wins ties). If the best
/// score is 0 — e.g. the region fills the whole work area, so every
/// candidate hangs off-screen — candidate 1 is clamped fully into the
/// nearest work area. That last resort MAY overlap the region (the cluster
/// then appears in the capture); accepted, there is nowhere else to put it.
/// With no work areas at all (defensive; enumeration should never be empty)
/// candidate 1 is used unclamped.
pub fn compute_layout(region: Rect, border: BorderSpec, work_areas: &[Rect]) -> FrameLayout {
    let band = inflate(region, 1 + border.total());
    let hole = inflate(region, 1);
    let white_band = inflate(hole, border.white);

    let pad = HANDLE_PAD as i64;
    let btn = HANDLE_BUTTON as i64;
    let gap = HANDLE_GAP as i64;
    let cw = pad + btn + pad + btn + pad; // pad | move | pad | close | pad
    let ch = btn + 2 * pad;

    let (bx1, by1) = (band.x as i64, band.y as i64);
    let (bx2, by2) = (x2(&band), y2(&band));

    let candidates: [(i64, i64); 8] = [
        (bx2 - cw, by1 - gap - ch), // 1: above, right-aligned
        (bx2 - cw, by2 + gap),      // 2: below, right-aligned
        (bx2 + gap, by1),           // 3: right, top-aligned
        (bx1 - gap - cw, by1),      // 4: left, top-aligned
        (bx1, by1 - gap - ch),      // 5: above, left-aligned
        (bx1, by2 + gap),           // 6: below, left-aligned
        (bx2 + gap, by2 - ch),      // 7: right, bottom-aligned
        (bx1 - gap - cw, by2 - ch), // 8: left, bottom-aligned
    ];

    let full = cw * ch;
    let mut chosen: Option<Rect> = None;
    let mut best: Option<(i64, Rect)> = None; // (score, rect); score strictly beats
    for &(cx, cy) in candidates.iter() {
        let cand = rect_i64(cx, cy, cx + cw, cy + ch);
        let score = work_areas
            .iter()
            .map(|wa| intersect_area(&cand, wa))
            .max()
            .unwrap_or(0);
        if score == full {
            chosen = Some(cand);
            break;
        }
        // Strict '>' keeps the earlier (higher-priority) candidate on ties.
        if best.is_none_or(|(s, _)| score > s) {
            best = Some((score, cand));
        }
    }

    let cluster = match (chosen, best) {
        (Some(c), _) => c,
        (None, Some((score, c))) if score > 0 => c,
        _ => {
            // Clamp fallback: nothing is visible anywhere. Pull candidate 1
            // into the nearest work area.
            let (cx, cy) = candidates[0];
            let c1 = rect_i64(cx, cy, cx + cw, cy + ch);
            match work_areas
                .iter()
                .min_by_key(|wa| rect_distance_sq(&c1, wa))
            {
                Some(wa) => {
                    // .max() applied last: if the work area is smaller than
                    // the cluster the top-left corner wins (stays reachable).
                    let x = (c1.x as i64).min(x2(wa) - cw).max(wa.x as i64);
                    let y = (c1.y as i64).min(y2(wa) - ch).max(wa.y as i64);
                    rect_i64(x, y, x + cw, y + ch)
                }
                None => c1,
            }
        }
    };

    let (clx, cly) = (cluster.x as i64, cluster.y as i64);
    let move_btn = rect_i64(clx + pad, cly + pad, clx + pad + btn, cly + pad + btn);
    let close_btn = rect_i64(
        clx + pad + btn + pad,
        cly + pad,
        clx + pad + btn + pad + btn,
        cly + pad + btn,
    );

    FrameLayout {
        outer: union(&band, &cluster),
        band,
        white_band,
        hole,
        cluster,
        move_btn,
        close_btn,
    }
}

// ---- hit testing -----------------------------------------------------------

/// Classifies point `p` (capture space) against the layout.
///
/// Priority: buttons > cluster background > border ring. The buttons and
/// cluster are tested BEFORE the hole so they stay clickable in the clamp
/// fallback, where the cluster may sit on top of the region. The hole
/// interior and everything outside band∪cluster are `Outside` — the
/// platform layer turns those into click-through.
///
/// In the ring, corner grab squares of side `max(CORNER_GRAB, 1 + border)`
/// anchored at the band's corners beat the edge strips (a thin border makes
/// diagonals unhittable otherwise); the part of a corner square that pokes
/// into the hole is still `Outside` — interior clicks always pass through.
/// When `!resizable` every Edge/Corner demotes to `Caption` (still movable).
pub fn hit_test(l: &FrameLayout, resizable: bool, p: (i32, i32)) -> Zone {
    if contains(&l.close_btn, p) {
        return Zone::CloseButton;
    }
    if contains(&l.move_btn, p) {
        return Zone::MoveHandle;
    }
    if contains(&l.cluster, p) {
        return Zone::Caption;
    }
    if !contains(&l.band, p) {
        return Zone::Outside;
    }
    if contains(&l.hole, p) {
        // Hollow interior: the user's own content — click-through.
        return Zone::Outside;
    }
    // p is in the border ring (band minus hole).
    if !resizable {
        return Zone::Caption;
    }

    // band inflation = 1 + border; recover it from the geometry rather than
    // threading `border` through: hole.x - band.x == border, + 1 slack.
    let inflation = (l.hole.x as i64 - l.band.x as i64) + 1;
    let grab = (CORNER_GRAB as i64).max(inflation);
    let (px, py) = (p.0 as i64, p.1 as i64);
    let west = px < l.band.x as i64 + grab;
    let east = px >= x2(&l.band) - grab;
    let north = py < l.band.y as i64 + grab;
    let south = py >= y2(&l.band) - grab;
    match (north, south, west, east) {
        (true, _, true, _) => Zone::Corner(Cor::NW),
        (true, _, _, true) => Zone::Corner(Cor::NE),
        (_, true, true, _) => Zone::Corner(Cor::SW),
        (_, true, _, true) => Zone::Corner(Cor::SE),
        _ => {
            // Not near a corner: classify by which side of the hole the
            // point falls outside of (a ring point is outside exactly one
            // side once corners are excluded).
            if py < l.hole.y as i64 {
                Zone::Edge(Dir::N)
            } else if py >= y2(&l.hole) {
                Zone::Edge(Dir::S)
            } else if px < l.hole.x as i64 {
                Zone::Edge(Dir::W)
            } else {
                Zone::Edge(Dir::E)
            }
        }
    }
}

// ---- resize ----------------------------------------------------------------

/// Applies a drag delta to `start` for the given resize zone: the dragged
/// edges move by (dx, dy), the opposite (anchored) edges stay fixed, and the
/// result is clamped so w/h never drop below `MIN_REGION` — the clamp eats
/// into the dragged edge, so the anchor never moves. Shared by the macOS
/// hand-rolled resize loop and (for parity in behavior) the Windows path.
///
/// Non-resize zones return `start` unchanged: moves are handled natively
/// (HTCAPTION / performWindowDragWithEvent), never through this function.
pub fn resize_region(start: Rect, zone: Zone, dx: i32, dy: i32) -> Rect {
    let (n, s, w, e) = match zone {
        Zone::Edge(Dir::N) => (true, false, false, false),
        Zone::Edge(Dir::S) => (false, true, false, false),
        Zone::Edge(Dir::W) => (false, false, true, false),
        Zone::Edge(Dir::E) => (false, false, false, true),
        Zone::Corner(Cor::NW) => (true, false, true, false),
        Zone::Corner(Cor::NE) => (true, false, false, true),
        Zone::Corner(Cor::SW) => (false, true, true, false),
        Zone::Corner(Cor::SE) => (false, true, false, true),
        _ => return start,
    };

    let mut x1 = start.x as i64;
    let mut y1 = start.y as i64;
    let mut rx2 = x1 + start.w as i64;
    let mut ry2 = y1 + start.h as i64;
    let min = MIN_REGION as i64;

    // min()/max() against the anchored edge is the MIN_REGION clamp: e.g. a
    // north drag may not push y1 past y2 - MIN_REGION.
    if n {
        y1 = (y1 + dy as i64).min(ry2 - min);
    }
    if s {
        ry2 = (ry2 + dy as i64).max(y1 + min);
    }
    if w {
        x1 = (x1 + dx as i64).min(rx2 - min);
    }
    if e {
        rx2 = (rx2 + dx as i64).max(x1 + min);
    }

    rect_i64(x1, y1, rx2, ry2)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn r(x: i32, y: i32, w: u32, h: u32) -> Rect {
        Rect { x, y, w, h }
    }

    /// A spec of a given TOTAL thickness, keeping the 1-unit white hairline.
    /// Band/hole geometry depends only on the total, so the pre-two-tone
    /// expectations below are unchanged by the split.
    fn bs(total: u32) -> BorderSpec {
        assert!(total >= 2, "a two-line border needs at least 1+1");
        BorderSpec {
            white: 1,
            accent: total - 1,
        }
    }

    /// Derived cluster dims, spelled out so the tests break loudly if the
    /// constants change: 4+30+4+30+4 = 72 wide, 30+8 = 38 tall.
    const CW: u32 = HANDLE_PAD * 2 + HANDLE_BUTTON * 2 + HANDLE_PAD;
    const CH: u32 = HANDLE_BUTTON + 2 * HANDLE_PAD;

    fn contains_rect(outer: &Rect, inner: &Rect) -> bool {
        outer.x as i64 <= inner.x as i64
            && outer.y as i64 <= inner.y as i64
            && x2(outer) >= x2(inner)
            && y2(outer) >= y2(inner)
    }

    // Wide-open work area: candidate 1 always fully visible.
    const BIG_WA: Rect = Rect {
        x: -10000,
        y: -10000,
        w: 30000,
        h: 30000,
    };

    #[test]
    fn border_spec_scales_and_snaps() {
        // 100%: the Clowd design verbatim — 1px white, 2px accent.
        assert_eq!(
            BorderSpec::scaled(1.0, 2),
            BorderSpec {
                white: 1,
                accent: 2
            }
        );
        assert_eq!(BorderSpec::scaled(1.0, 2).total(), 3);
        // 150%: each line rounded on its own, so both stay on the pixel grid.
        assert_eq!(
            BorderSpec::scaled(1.5, 2),
            BorderSpec {
                white: 2,
                accent: 3
            }
        );
        // 200% / 300%: exact multiples, ratio preserved.
        assert_eq!(
            BorderSpec::scaled(2.0, 2),
            BorderSpec {
                white: 2,
                accent: 4
            }
        );
        assert_eq!(
            BorderSpec::scaled(3.0, 2),
            BorderSpec {
                white: 3,
                accent: 6
            }
        );
        // Every line survives a downscale: neither may round away to nothing.
        assert_eq!(
            BorderSpec::scaled(0.1, 2),
            BorderSpec {
                white: 1,
                accent: 1
            }
        );
        // 125% rounds the hairline down to 1 rather than up, keeping it a
        // hairline; the accent goes to 3 (2.5 rounds away from zero).
        assert_eq!(
            BorderSpec::scaled(1.25, 2),
            BorderSpec {
                white: 1,
                accent: 3
            }
        );
    }

    #[test]
    fn layout_band_hole_cluster_basic() {
        let l = compute_layout(r(100, 100, 300, 200), bs(3), &[BIG_WA]);
        // band = region + (1 + border) = 4 per side; hole = region + 1.
        assert_eq!(l.band, r(96, 96, 308, 208));
        assert_eq!(l.hole, r(99, 99, 302, 202));
        // Candidate 1: above the band, right-aligned, HANDLE_GAP away.
        assert_eq!(l.cluster, r(404 - CW as i32, 96 - 8 - CH as i32, CW, CH));
        assert_eq!(l.cluster, r(332, 50, 72, 38));
        // Buttons: move left, close right, HANDLE_PAD inset.
        assert_eq!(l.move_btn, r(336, 54, 30, 30));
        assert_eq!(l.close_btn, r(370, 54, 30, 30));
        assert!(contains_rect(&l.cluster, &l.move_btn));
        assert!(contains_rect(&l.cluster, &l.close_btn));
        // outer = union(band, cluster).
        assert_eq!(l.outer, r(96, 50, 308, 254));
    }

    #[test]
    fn layout_avoids_menu_bar() {
        // Work area excludes a 25px menu-bar strip at the top. A region near
        // the top makes the "above" candidates poke into the strip, so the
        // cluster must land below (candidate 2, right-aligned).
        let wa = r(0, 25, 1920, 1055);
        let l = compute_layout(r(100, 40, 400, 300), bs(3), &[wa]);
        // band = (96,36)..(504,344); candidate 2: below, right-aligned.
        assert_eq!(l.cluster, r(504 - CW as i32, 344 + 8, CW, CH));
        assert_eq!(l.cluster, r(432, 352, 72, 38));
        assert!(contains_rect(&wa, &l.cluster));
    }

    #[test]
    fn layout_avoids_dock() {
        // Menu bar (25px) top AND dock (50px) bottom: work area y = 25..1030.
        // The region nearly spans it vertically, so above and below both
        // fail; candidate 3 (right of band, top-aligned) wins.
        let wa = r(0, 25, 1920, 1005);
        let l = compute_layout(r(100, 40, 400, 980), bs(3), &[wa]);
        // band = (96,36)..(504,1024): below would start at y=1032 > 1030.
        assert_eq!(l.cluster, r(504 + 8, 36, CW, CH));
        assert_eq!(l.cluster, r(512, 36, 72, 38));
        assert!(contains_rect(&wa, &l.cluster));
    }

    #[test]
    fn layout_negative_coords_second_monitor() {
        // Region on a monitor left of primary: all-negative x. Candidate 1
        // must be picked with correct i64 math.
        let was = [r(0, 0, 1920, 1080), r(-1920, 0, 1920, 1080)];
        let l = compute_layout(r(-1800, 300, 640, 480), bs(2), &was);
        // band inflation 3: band = (-1803,297)..(-1157,783).
        assert_eq!(l.band, r(-1803, 297, 646, 486));
        assert_eq!(l.cluster, r(-1157 - CW as i32, 297 - 8 - CH as i32, CW, CH));
        assert!(contains_rect(&was[1], &l.cluster));
    }

    #[test]
    fn layout_partial_best_when_none_fully_visible() {
        // No candidate fits fully: the region touches the work-area bottom,
        // spans its full width, and leaves only an 18px sliver visible above
        // the menu bar. The best partial (candidate 1) wins, unclamped.
        let wa = r(0, 25, 1930, 1055); // y = 25..1080
        let region = r(0, 55, 1920, 1025); // bottom = 1080 = wa bottom
        let l = compute_layout(region, bs(3), &[wa]);
        // band = (-4,51)..(1924,1084). Candidate 1: (1852,5)..(1924,43) —
        // 72x18 visible. Candidate 5 loses columns off the left edge; the
        // right/left/below candidates are entirely outside.
        assert_eq!(l.cluster, r(1852, 5, 72, 38));
        // NOT clamped into the work area — it genuinely overhangs the strip.
        assert!(!contains_rect(&wa, &l.cluster));
        // But the partial path must never overlap the region.
        assert_eq!(intersect_area(&l.cluster, &region), 0);
    }

    #[test]
    fn layout_fallback_when_region_fills_work_area() {
        // Region == whole work area: every candidate hangs off-screen with
        // zero visibility, so candidate 1 is clamped into the work area —
        // the documented last resort where cluster ∩ region ≠ ∅.
        let wa = r(0, 0, 1920, 1080);
        let region = r(0, 0, 1920, 1080);
        let l = compute_layout(region, bs(3), &[wa]);
        assert_eq!(l.cluster, r(1920 - CW as i32, 0, CW, CH));
        assert!(contains_rect(&wa, &l.cluster));
        assert!(intersect_area(&l.cluster, &region) > 0); // accepted overlap
    }

    #[test]
    fn layout_no_work_areas_defensive() {
        // Empty work-area list (should never happen): candidate 1 as-is.
        let l = compute_layout(r(100, 100, 300, 200), bs(3), &[]);
        assert_eq!(l.cluster, r(332, 50, 72, 38));
    }

    /// Property: with room available, the cluster never touches the region
    /// or the band (it sits HANDLE_GAP clear of the band), and the hole
    /// strictly contains the region with the 1-unit slack — i.e. no painted
    /// frame pixel can ever land inside the capture.
    #[test]
    fn layout_cluster_and_hole_invariants() {
        let regions = [
            r(0, 0, 64, 64),
            r(100, 100, 300, 200),
            r(-1800, 300, 640, 480),
            r(-50, -50, 5000, 3000),
            r(2000, 100, 1200, 800),
        ];
        for &region in &regions {
            for &border in &[bs(2), bs(3), bs(8), bs(32), BorderSpec { white: 4, accent: 4 }] {
                let l = compute_layout(region, border, &[BIG_WA]);
                let d = (1 + border.total()) as i64;
                // The two lines exactly tile the ring, with no gap and no
                // overlap: white_band is their shared edge.
                assert_eq!(
                    l.white_band.x as i64,
                    l.hole.x as i64 - border.white as i64
                );
                assert_eq!(x2(&l.white_band), x2(&l.hole) + border.white as i64);
                assert_eq!(
                    l.band.x as i64,
                    l.white_band.x as i64 - border.accent as i64
                );
                assert!(contains_rect(&l.band, &l.white_band));
                assert!(contains_rect(&l.white_band, &l.hole));
                // band/hole inflation exact.
                assert_eq!(l.band.x as i64, region.x as i64 - d);
                assert_eq!(x2(&l.band), x2(&region) + d);
                assert_eq!(l.hole.x as i64, region.x as i64 - 1);
                assert_eq!(y2(&l.hole), y2(&region) + 1);
                // hole ⊇ region, strictly (slack on every side).
                assert!(contains_rect(&l.hole, &region));
                // cluster clear of both region and band.
                assert_eq!(intersect_area(&l.cluster, &region), 0);
                assert_eq!(intersect_area(&l.cluster, &l.band), 0);
                // outer covers everything.
                assert!(contains_rect(&l.outer, &l.band));
                assert!(contains_rect(&l.outer, &l.cluster));
                assert!(contains_rect(&l.cluster, &l.move_btn));
                assert!(contains_rect(&l.cluster, &l.close_btn));
            }
        }
    }

    // Shared layout for hit tests: band (96,96)..(404,304), hole
    // (99,99)..(401,301), cluster (332,50)..(404,88), move (336,54)..(366,84),
    // close (370,54)..(400,84). Corner grab = max(16, 4) = 16.
    fn hit_layout() -> FrameLayout {
        compute_layout(r(100, 100, 300, 200), bs(3), &[BIG_WA])
    }

    #[test]
    fn hit_interior_is_click_through() {
        let l = hit_layout();
        assert_eq!(hit_test(&l, true, (250, 200)), Zone::Outside);
        // Hole starts 1 unit outside the region: (99,99) is still interior.
        assert_eq!(hit_test(&l, true, (99, 99)), Zone::Outside);
        // Region's own top-left pixel: interior, click-through.
        assert_eq!(hit_test(&l, true, (100, 100)), Zone::Outside);
    }

    #[test]
    fn hit_outside_frame() {
        let l = hit_layout();
        assert_eq!(hit_test(&l, true, (0, 0)), Zone::Outside);
        assert_eq!(hit_test(&l, true, (500, 500)), Zone::Outside);
        // The gap between cluster bottom (88) and band top (96) is dead air.
        assert_eq!(hit_test(&l, true, (350, 90)), Zone::Outside);
        // Just past the band's half-open right/bottom edge.
        assert_eq!(hit_test(&l, true, (404, 200)), Zone::Outside);
        assert_eq!(hit_test(&l, true, (200, 304)), Zone::Outside);
    }

    #[test]
    fn hit_edges() {
        let l = hit_layout();
        assert_eq!(hit_test(&l, true, (250, 97)), Zone::Edge(Dir::N));
        assert_eq!(hit_test(&l, true, (250, 302)), Zone::Edge(Dir::S));
        assert_eq!(hit_test(&l, true, (97, 200)), Zone::Edge(Dir::W));
        assert_eq!(hit_test(&l, true, (402, 200)), Zone::Edge(Dir::E));
    }

    #[test]
    fn hit_corners_and_precedence() {
        let l = hit_layout();
        assert_eq!(hit_test(&l, true, (97, 97)), Zone::Corner(Cor::NW));
        assert_eq!(hit_test(&l, true, (403, 97)), Zone::Corner(Cor::NE));
        assert_eq!(hit_test(&l, true, (97, 303)), Zone::Corner(Cor::SW));
        assert_eq!(hit_test(&l, true, (403, 303)), Zone::Corner(Cor::SE));
        // Corner-vs-edge precedence: a point in the N strip but within the
        // 16px grab square of the band's corner is a Corner, not Edge(N).
        assert_eq!(hit_test(&l, true, (110, 97)), Zone::Corner(Cor::NW));
        assert_eq!(hit_test(&l, true, (390, 97)), Zone::Corner(Cor::NE));
        // One past the grab square (96+16=112) is an edge again.
        assert_eq!(hit_test(&l, true, (112, 97)), Zone::Edge(Dir::N));
    }

    #[test]
    fn hit_not_resizable_demotes_to_caption() {
        let l = hit_layout();
        assert_eq!(hit_test(&l, false, (250, 97)), Zone::Caption);
        assert_eq!(hit_test(&l, false, (97, 97)), Zone::Caption);
        // Interior stays click-through, buttons stay buttons.
        assert_eq!(hit_test(&l, false, (250, 200)), Zone::Outside);
        assert_eq!(hit_test(&l, false, (380, 60)), Zone::CloseButton);
    }

    #[test]
    fn hit_cluster_and_buttons() {
        let l = hit_layout();
        assert_eq!(hit_test(&l, true, (350, 60)), Zone::MoveHandle);
        assert_eq!(hit_test(&l, true, (380, 60)), Zone::CloseButton);
        // Cluster background (inside cluster, outside both buttons).
        assert_eq!(hit_test(&l, true, (334, 52)), Zone::Caption);
        assert_eq!(hit_test(&l, true, (368, 70)), Zone::Caption); // between buttons
    }

    #[test]
    fn hit_thick_border_grows_corner_grab() {
        // border 32 → inflation 33 > CORNER_GRAB, so the grab square is 33.
        let l = compute_layout(r(200, 200, 300, 300), bs(32), &[BIG_WA]);
        // band = (167,167)..(533,533); (196,180) is 29 in from the corner —
        // inside the 33px grab square, outside a 16px one.
        assert_eq!(hit_test(&l, true, (196, 180)), Zone::Corner(Cor::NW));
        assert_eq!(hit_test(&l, true, (250, 180)), Zone::Edge(Dir::N));
    }

    #[test]
    fn hit_fallback_cluster_wins_over_hole() {
        // Clamp fallback puts the cluster on top of the region; its buttons
        // and background must still hit (checked before the hole).
        let wa = r(0, 0, 1920, 1080);
        let l = compute_layout(r(0, 0, 1920, 1080), bs(3), &[wa]);
        let cx = l.close_btn.x + 5;
        let cy = l.close_btn.y + 5;
        assert_eq!(hit_test(&l, true, (cx, cy)), Zone::CloseButton);
        assert_eq!(
            hit_test(&l, true, (l.cluster.x + 1, l.cluster.y + 1)),
            Zone::Caption
        );
    }

    #[test]
    fn resize_edges_anchor_opposite_side() {
        let start = r(100, 100, 200, 150); // x2=300, y2=250
        assert_eq!(
            resize_region(start, Zone::Edge(Dir::E), 40, 999),
            r(100, 100, 240, 150) // dy ignored, left edge fixed
        );
        assert_eq!(
            resize_region(start, Zone::Edge(Dir::W), 40, 999),
            r(140, 100, 160, 150) // right edge stays at 300
        );
        assert_eq!(
            resize_region(start, Zone::Edge(Dir::N), 999, -30),
            r(100, 70, 200, 180) // bottom stays at 250
        );
        assert_eq!(
            resize_region(start, Zone::Edge(Dir::S), 999, 25),
            r(100, 100, 200, 175)
        );
    }

    #[test]
    fn resize_corners_move_two_edges() {
        let start = r(100, 100, 200, 150);
        assert_eq!(
            resize_region(start, Zone::Corner(Cor::NW), 10, 20),
            r(110, 120, 190, 130) // x2=300, y2=250 anchored
        );
        assert_eq!(
            resize_region(start, Zone::Corner(Cor::NE), 10, 20),
            r(100, 120, 210, 130)
        );
        assert_eq!(
            resize_region(start, Zone::Corner(Cor::SW), -10, -20),
            r(90, 100, 210, 130)
        );
        assert_eq!(
            resize_region(start, Zone::Corner(Cor::SE), 5, 5),
            r(100, 100, 205, 155)
        );
    }

    #[test]
    fn resize_clamps_to_min_region() {
        let start = r(100, 100, 200, 150); // x2=300, y2=250
        let m = MIN_REGION; // 64
                            // Dragging the W edge far right: width pins at MIN_REGION, the
                            // RIGHT edge (anchor) never moves.
        assert_eq!(
            resize_region(start, Zone::Edge(Dir::W), 1000, 0),
            r(300 - m as i32, 100, m, 150)
        );
        assert_eq!(
            resize_region(start, Zone::Edge(Dir::N), 0, 1000),
            r(100, 250 - m as i32, 200, m)
        );
        // Dragging E/S edges far inward: origin (anchor) never moves.
        assert_eq!(
            resize_region(start, Zone::Edge(Dir::E), -1000, 0),
            r(100, 100, m, 150)
        );
        assert_eq!(
            resize_region(start, Zone::Edge(Dir::S), 0, -1000),
            r(100, 100, 200, m)
        );
        // Corner collapse clamps both axes at once.
        assert_eq!(
            resize_region(start, Zone::Corner(Cor::NW), 1000, 1000),
            r(300 - m as i32, 250 - m as i32, m, m)
        );
        assert_eq!(
            resize_region(start, Zone::Corner(Cor::SE), -1000, -1000),
            r(100, 100, m, m)
        );
    }

    #[test]
    fn resize_ignores_non_resize_zones() {
        let start = r(100, 100, 200, 150);
        for z in [
            Zone::Outside,
            Zone::Caption,
            Zone::MoveHandle,
            Zone::CloseButton,
        ] {
            assert_eq!(resize_region(start, z, 50, 50), start);
        }
    }
}
