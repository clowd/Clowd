//! Pure frame geometry: the two-tone border band, its hollow interior, the
//! eight resize handles and their cutouts, the button panel and hit-testing.
//! Everything is in capture space (top-left origin, y-down — the same space as
//! `--region` and `MonitorInfo::x/y`). No platform or OBS deps; fully
//! unit-tested like `obs_platform::region`.
//!
//! `DESIGN.md` is the spec this file implements; section references below point
//! at it. The one rule worth stating up front: capture units are physical
//! pixels on Windows and CG points on macOS, so every "scale with DPI" constant
//! here is live on Windows and a no-op (scale 1.0) on macOS. That is why the
//! platform layer, not this module, decides the scale — see [`FrameSpec`].
//!
//! All arithmetic that combines coordinates runs in i64: virtual-desktop
//! coords can be negative and spans can overflow i32, mirroring
//! `region.rs`'s `rects_intersect`.

use obs_platform::region::Rect;

/// Minimum region width/height a resize drag may shrink to.
pub const MIN_REGION: u32 = 64;

/// Total border thickness at 100% scale, in logical px (DESIGN §1).
// Dead on the macOS build: main.rs spells the same number into clap's
// `default_value`/`range`, which cannot take a const. Kept here because it is
// the value's definition and the tests assert against it.
#[allow(dead_code)]
pub const BASE_BORDER: u32 = 4;
/// Resize-handle side at 100% scale (DESIGN §2). Twelve because that is
/// Clowd's handle size everywhere else and this frame should not be the odd
/// one out: `Clowd.Drawing/Graphics/GraphicBase.cs:175`
/// (`UnscaledControlSize = 12.0`),
/// `Clowd.Ui/VideoEditor/TransformGizmoControl.cs:56` (`HandleSize = 12`) and
/// the capture overlay's `shaders/desktop.wgsl:421` (`handle_r = 6.0`, a 12px
/// diameter) all agree on it, and the last works in physical pixels at 100%,
/// which is exactly our capture unit. At 12 the three-ring fill is a 2px
/// accent rim, a 2px white ring and a 4x4 accent core.
///
/// Deliberately DECOUPLED from `--border`, which keeps its own 4px floor; the
/// `max` in [`FrameSpec::scaled`] only stops the handle being *thinner* than
/// the band it straddles when someone passes a fat `--border`.
pub const BASE_HANDLE: u32 = 12;
/// Panel button square at 100% scale (DESIGN §5).
pub const BASE_BUTTON: u32 = 30;
/// Distance from the band's outer edge to the panel at 100% scale.
pub const BASE_GAP: u32 = 8;
/// Margin reserved between the panel and the work-area edge at 100% scale.
/// Only the *space tests* honour it; the final clamp does not, so a panel that
/// has nowhere else to go still ends up fully on screen.
pub const BASE_EDGE_MARGIN: u32 = 2;

/// The border's two lines, in capture units, already DPI-scaled and snapped to
/// whole device pixels.
///
/// Reading outward from the captured region: white first, then accent — the
/// ordering Clowd's `BorderWindow.axaml` uses, and for its reason. The white
/// line is what keeps the accent legible against arbitrary desktop content
/// underneath, so it has to be the one adjacent to that content's boundary.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BorderSpec {
    /// White hairline, immediately outside the region (inside the accent).
    pub white: u32,
    /// Accent line, outside the white one.
    pub accent: u32,
}

impl BorderSpec {
    /// Scales a total thickness and splits it (DESIGN §1):
    ///
    /// ```text
    /// total  = max(base_total, round(base_total * scale))
    /// white  = total / 2          (floor)
    /// accent = total - white      (gets the odd pixel)
    /// ```
    ///
    /// The *total* is what scales and snaps, not each line independently: the
    /// two lines have to tile the ring exactly, and rounding them separately
    /// lets their sum drift off the value the band was inflated by.
    ///
    /// The odd pixel goes to the accent because the accent is the border the
    /// user is meant to see; the white line is a legibility backing and may be
    /// the thinner of the two, never the thicker. `total` is floored at
    /// `base_total` so a *downscaled* display (scale < 1, which happens on
    /// Windows when the user picks a scale below 100%) still gets a border
    /// thick enough to grab.
    ///
    /// `base_total` is expected to be >= 2 — one unit per line. The CLI's
    /// `--border` floor of `BASE_BORDER` (4) guarantees it.
    pub fn scaled(scale: f64, base_total: u32) -> Self {
        let total = ((base_total as f64 * scale).round() as u32).max(base_total);
        let white = total / 2;
        BorderSpec {
            white,
            accent: total - white,
        }
    }

    /// Combined thickness of both lines. Also the ring thickness `band - hole`
    /// and the handle cutout `gap` (§2).
    pub fn total(self) -> u32 {
        self.white + self.accent
    }
}

/// Every DPI-derived measurement the frame needs, resolved once by the
/// platform layer and then threaded through layout unchanged.
///
/// It exists so that scaling happens in exactly one place. Deriving e.g. the
/// panel outline from the border inside `compute_layout` would be fine; having
/// *two* call sites round `30 * scale` independently would not, and that is
/// the class of drift this struct removes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FrameSpec {
    pub border: BorderSpec,
    /// Resize-handle side, `S` in DESIGN §2. Independent of the border except
    /// for a floor at `border.total()`.
    pub handle: u32,
    /// Panel button square side.
    pub button: u32,
    /// Panel outline and inter-button separators. Equal to `border.white` on
    /// purpose: the panel floats over the same arbitrary desktop content the
    /// border does and needs the identical white-against-accent treatment, at
    /// the identical weight.
    pub outline: u32,
    /// Band outer edge -> panel.
    pub gap: u32,
    /// Margin reserved from the work-area edge, honoured by the placement
    /// space tests only (DESIGN §5).
    pub edge_margin: u32,
    /// Number of buttons in the panel. Currently 1 (close); the panel is sized
    /// and laid out for N so adding one is a constant change, not a redesign.
    pub buttons: u32,
}

impl FrameSpec {
    /// Builds the whole spec at `scale`, where 1.0 is 96 dpi / a CG point.
    ///
    /// `handle` is floored at `border.total()`: the handle straddles the ring
    /// and a handle *thinner* than the line it interrupts would read as a
    /// notch rather than a grip. That floor only ever bites for a fat
    /// `--border` — at every ordinary scale `12 * scale` is the larger number.
    ///
    /// `button` is floored at 1 because a zero-side button is unclickable and
    /// undrawable; `gap` and `edge_margin` are allowed to round to 0, which is
    /// the correct degradation for pure spacing at extreme downscales.
    pub fn scaled(scale: f64, base_border: u32, buttons: u32) -> Self {
        let snap = |base: u32| (base as f64 * scale).round().max(0.0) as u32;
        let border = BorderSpec::scaled(scale, base_border);
        FrameSpec {
            border,
            handle: snap(BASE_HANDLE).max(border.total()),
            button: snap(BASE_BUTTON).max(1),
            outline: border.white,
            gap: snap(BASE_GAP),
            edge_margin: snap(BASE_EDGE_MARGIN),
            buttons,
        }
    }

    /// How far a centred handle reaches INWARD past the band's inner edge
    /// (DESIGN §1/§2). Floors, so the odd unit goes to the outward side, away
    /// from the region.
    fn handle_overhang_in(&self) -> u32 {
        (self.handle - self.border.total()) / 2
    }

    /// How far a centred handle reaches OUTWARD past the band's outer edge.
    /// Gets the odd unit: outward is the direction with nothing to protect.
    fn handle_overhang_out(&self) -> u32 {
        self.handle - self.border.total() - self.handle_overhang_in()
    }

    /// Region -> `hole` inflation. `1` is the rounding slack from plan §6.4
    /// (logical-to-physical rounding must never put a border unit inside the
    /// captured area); the `handle_overhang_in` term is what a centred handle
    /// will spend reaching back in, so that after it does, exactly the 1 unit
    /// of slack survives. See `compute_layout` for why that cancellation is
    /// the whole point.
    fn slack(&self) -> u32 {
        1 + self.handle_overhang_in()
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
pub enum HandleKind {
    Edge(Dir),
    Corner(Cor),
}

/// Which way the button panel runs. Chosen by the placement cascade (§5), not
/// by the caller, and deliberately independent of the button count.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Orientation {
    Vertical,
    Horizontal,
}

/// One resize handle: the painted square and the unpainted stretch of border
/// around it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Handle {
    pub kind: HandleKind,
    /// The painted square, side `spec.handle`. Straddles the ring, centred on
    /// it, so it sticks out past `band` — and, by construction, stops exactly
    /// one unit short of the region on the inward side.
    pub rect: Rect,
    /// The stretch of ring left unpainted around `rect`, so the handle reads
    /// as a separate object rather than a bump in the line.
    ///
    /// NOT a grab area: per DESIGN §3 the hit surface is exactly the painted
    /// surface, so the gap between `rect` and the edge of `cutout` is a real
    /// hole — `hit_test` returns `Outside` there, and on Windows the window
    /// region excludes it (§6, where an unpainted region pixel keeps stale
    /// screen content). It reaches into the hole and past the band; those
    /// parts are simply invisible, since neither is painted anyway.
    pub cutout: Rect,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Zone {
    /// Not ours: outside the frame, or in the hollow interior. The platform
    /// layer must let these clicks fall through to whatever is underneath.
    Outside,
    /// Border ring or panel background -> move drag. The whole border means
    /// "drag" now that resizing is the eight handles only (DESIGN §7), which
    /// is what let the old dedicated move-handle button go away.
    Caption,
    Edge(Dir),
    Corner(Cor),
    /// Index into [`FrameLayout::buttons`].
    Button(usize),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FrameLayout {
    /// The region this layout was computed for.
    ///
    /// Carried rather than re-derived. Inverting the `hole` inflation to
    /// recover it looks trivial and is exactly the bug that DESIGN §6 removes:
    /// the inflation is `spec.slack()`, not the constant 1 it used to be, so
    /// any caller that assumed 1 silently returned a rect a few units too
    /// large and poisoned every drag translation downstream of it.
    pub region: Rect,
    /// Frame window bounds = union(band, panel, every handle cutout) (§2).
    /// The cutouts are in because handles overhang the band (§2) and the
    /// window has to cover them or they are neither paintable nor clickable;
    /// a cutout contains its handle, so unioning cutouts covers that with one
    /// term. The margin a cutout adds beyond its handle is inert — nothing
    /// paints it and `hit_test` answers `Outside` there.
    pub outer: Rect,
    /// Border ring outer rect: `hole` inflated by `border.total()`.
    pub band: Rect,
    /// Outer edge of the white hairline: `hole` inflated by `border.white`.
    /// The two lines are therefore `white_band` minus `hole` (white) and
    /// `band` minus `white_band` (accent).
    pub white_band: Rect,
    /// Hollow interior: region inflated by `spec.slack()`. Strictly contains
    /// the region, so no painted unit can ever land inside it.
    pub hole: Rect,
    /// Up to eight handles; fewer once an edge is too short to carry its
    /// mid-edge one (§2). Corners come first, so a caller that scans in order
    /// resolves any (pathologically small) overlap in favour of the diagonal.
    pub handles: Vec<Handle>,
    /// Button panel background, including its outline.
    pub panel: Rect,
    pub orientation: Orientation,
    /// One rect per button, in order. `len() == spec.buttons`.
    pub buttons: Vec<Rect>,
}

impl FrameLayout {
    /// Rigid translation of EVERY rect in the layout, `region` included.
    ///
    /// Exists so callers cannot forget one. A layout with a stale sub-rect is
    /// the worst failure mode this module has: drawing and hit-testing then
    /// disagree, and the frame goes inert or grabs the wrong zone with nothing
    /// visibly wrong on screen.
    // Used by the win32 layer, which shifts the layout into window-local
    // coordinates before building the frame window's region; the AppKit layer
    // keeps capture coordinates and translates at draw time, so this reads as
    // dead on the macOS build.
    #[allow(dead_code)]
    pub fn translate(&mut self, dx: i32, dy: i32) {
        let shift = |r: &mut Rect| *r = translated(*r, dx, dy);
        shift(&mut self.region);
        shift(&mut self.outer);
        shift(&mut self.band);
        shift(&mut self.white_band);
        shift(&mut self.hole);
        shift(&mut self.panel);
        for h in &mut self.handles {
            shift(&mut h.rect);
            shift(&mut h.cutout);
        }
        for b in &mut self.buttons {
            shift(b);
        }
    }
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
    contains_i64(r, p.0 as i64, p.1 as i64)
}

fn contains_i64(r: &Rect, px: i64, py: i64) -> bool {
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

/// Shrinks by `d` per side. Collapses to an empty rect rather than inverting.
fn deflate(r: Rect, d: u32) -> Rect {
    let d = d as i64;
    let (x1, y1) = (r.x as i64 + d, r.y as i64 + d);
    rect_i64(x1, y1, (x2(&r) - d).max(x1), (y2(&r) - d).max(y1))
}

#[allow(dead_code)] // only reachable through FrameLayout::translate; see there
fn translated(r: Rect, dx: i32, dy: i32) -> Rect {
    let (dx, dy) = (dx as i64, dy as i64);
    rect_i64(r.x as i64 + dx, r.y as i64 + dy, x2(&r) + dx, y2(&r) + dy)
}

fn union(a: &Rect, b: &Rect) -> Rect {
    rect_i64(
        (a.x as i64).min(b.x as i64),
        (a.y as i64).min(b.y as i64),
        x2(a).max(x2(b)),
        y2(a).max(y2(b)),
    )
}

fn intersect(a: &Rect, b: &Rect) -> Rect {
    rect_i64(
        (a.x as i64).max(b.x as i64),
        (a.y as i64).max(b.y as i64),
        x2(a).min(x2(b)),
        y2(a).min(y2(b)),
    )
}

/// Squared gap between two rects (0 when they touch or overlap). Used only to
/// rank "nearest work area", so squared is fine — no sqrt, no overflow risk.
fn rect_distance_sq(a: &Rect, b: &Rect) -> i64 {
    let dx = (b.x as i64 - x2(a)).max(a.x as i64 - x2(b)).max(0);
    let dy = (b.y as i64 - y2(a)).max(a.y as i64 - y2(b)).max(0);
    dx * dx + dy * dy
}

// ---- handles ---------------------------------------------------------------

/// Ring thickness for a handle of side `side`: `max(1, side / 6)`.
///
/// The 1/6 keeps the 6x6 reference drawing's proportions (1 accent, 1 white,
/// 2 core, 1 white, 1 accent) at every size. INTEGER division, not rounding:
/// rounding jumps `r` from 1 to 2 at `side = 9`, which collapses the core from
/// 4x4 to 1x1 for a one-unit change in `side`. Flooring holds `r` at 1 until
/// 12, so the core grows monotonically and the ring only thickens when there
/// is genuinely room. The `max(1)` keeps the rings from vanishing below 6.
pub fn handle_ring(side: u32) -> u32 {
    (side / 6).max(1)
}

/// Paint recipe for one handle (DESIGN §2 "Fill"), as `(white, core)`.
///
/// The caller fills `handle` with the accent first, then the returned white
/// rect, then the returned accent core — three nested filled rects, no path
/// arithmetic. `None` means "this layer has no room at this size". At
/// `BASE_HANDLE = 12` both are always present (they are for every `side >= 5`);
/// the `None` arms exist so the function is total for any input, including the
/// degenerate sizes a caller could construct by hand.
pub fn handle_layers(handle: Rect) -> (Option<Rect>, Option<Rect>) {
    let side = handle.w.min(handle.h);
    let r = handle_ring(side);
    let nonempty = |rc: Rect| (rc.w > 0 && rc.h > 0).then_some(rc);
    let white = nonempty(deflate(handle, r));
    // Strictly greater: at equality the "core" would be exactly as thick as
    // the white ring around it, which reads as a smudge rather than a core.
    let core = (side.saturating_sub(2 * r) > 2 * r).then(|| deflate(handle, 2 * r));
    (white, core.and_then(nonempty))
}

/// Per-axis outcome of the degradation ladder (DESIGN §2).
struct AxisPlan {
    /// Extent, along this axis, of each corner cutout. Corner cutouts are
    /// anchored at their corner and shrink inward only, so the two axes'
    /// answers combine into one (usually non-square) corner rect.
    corner_cut: i64,
    /// Whether the mid-edge handles that sit on this axis survive.
    mid: bool,
}

/// `l` is the band extent along the axis, `cut` the cutout's along-edge extent
/// (`S + 2*gap`), `run` the shortest stub of painted band worth keeping
/// (`gap`). The ladder exists because at high DPI on a small region three
/// cutouts plus the stubs between them can exceed the edge, at which point the
/// "border" along that edge is entirely gap and stops reading as a frame.
///
/// Note the ladder measures cutouts against the BAND extent even though a
/// cutout starts `gap` outside the band. That is conservative in the safe
/// direction — the stub that actually survives is wider than `run`, never
/// narrower — and keeps the test in one obvious unit.
fn axis_plan(l: i64, s: i64, gap: i64, cut: i64, run: i64) -> AxisPlan {
    if l >= 3 * cut + 2 * run {
        AxisPlan {
            corner_cut: cut,
            mid: true,
        }
    } else if l >= 2 * cut + run {
        // Two full corner cutouts and one stub still fit; the mid-edge handle
        // is the one that goes, because corners are the only diagonal resize
        // affordance and are never dropped.
        AxisPlan {
            corner_cut: cut,
            mid: false,
        }
    } else {
        // Shrink both corner cutouts until a stub of band survives between
        // them, but never below `s + gap`.
        //
        // The floor is `s + gap`, not `s`, because the cutout is anchored
        // `gap` OUTSIDE the band: that much extent is spent before the cutout
        // even reaches the handle. At `--border 32`, scale 3, a minimum-size
        // region, an `s` floor produces a cutout that misses its handle
        // entirely — the handle is painted onto unbroken band and the gap that
        // should isolate it appears somewhere else. `s + gap` is the extent
        // that actually reaches the handle's inner end (the outward overhang
        // cancels). DESIGN §2 tier 3 states it this way.
        //
        // The stub is what gives when the two cannot both be had. That is the
        // right trade: a missing stub is cosmetic, an unreachable handle is
        // not.
        AxisPlan {
            corner_cut: ((l - run) / 2).max(s + gap),
            mid: false,
        }
    }
}

/// Which side(s) of the band a handle sits on. Used for the centre-line clamp
/// in [`compute_layout`]; `None` on an axis means the handle straddles that
/// axis's centre (a mid-edge handle does, along its own edge).
fn handle_sides(kind: HandleKind) -> (Option<bool>, Option<bool>) {
    // (west?, north?) — `Some(true)` = low side of the axis, `Some(false)` =
    // high side, `None` = centred.
    match kind {
        HandleKind::Corner(Cor::NW) => (Some(true), Some(true)),
        HandleKind::Corner(Cor::NE) => (Some(false), Some(true)),
        HandleKind::Corner(Cor::SW) => (Some(true), Some(false)),
        HandleKind::Corner(Cor::SE) => (Some(false), Some(false)),
        HandleKind::Edge(Dir::N) => (None, Some(true)),
        HandleKind::Edge(Dir::S) => (None, Some(false)),
        HandleKind::Edge(Dir::W) => (Some(true), None),
        HandleKind::Edge(Dir::E) => (Some(false), None),
    }
}

// ---- layout ----------------------------------------------------------------

/// Computes the frame layout for `region`.
///
/// Band/hole geometry (DESIGN §1):
///
/// ```text
/// hole       = region inflated by spec.slack()      // 1 + handle_overhang_in
/// white_band = hole   inflated by border.white
/// band       = hole   inflated by border.total()
/// ```
///
/// Handles are centred on the ring (§2), which means they reach
/// `handle_overhang_in` back inside the band's inner edge. That is precisely
/// the term `slack()` was widened by, so the two cancel:
///
/// ```text
/// handle inner edge = hole edge - overhang_in
///                   = (region + 1 + overhang_in) - overhang_in
///                   = region + 1
/// ```
///
/// at EVERY scale. The one-unit clearance around the capture is therefore a
/// structural property of how the two constants are derived, not an arithmetic
/// coincidence that has to be re-verified per DPI. `layout_handles_clear_the_region`
/// is the test that pins it.
///
/// Panel: DESIGN §5, the Clowd cascade — see [`place_panel`].
pub fn compute_layout(region: Rect, spec: &FrameSpec, work_areas: &[Rect]) -> FrameLayout {
    let total = spec.border.total();
    let hole = inflate(region, spec.slack());
    let white_band = inflate(hole, spec.border.white);
    let band = inflate(hole, total);

    let s = spec.handle as i64;
    let gap = total as i64; // cutout gap is the border thickness (§2)
    let out = spec.handle_overhang_out() as i64;
    let (bx1, by1) = (band.x as i64, band.y as i64);
    let (bx2, by2) = (x2(&band), y2(&band));
    // A handle's outer edge sits `out` beyond the band; its cutout adds
    // another `gap`. Those are the anchors the corner cutouts hang from.
    let (cx1, cy1) = (bx1 - out - gap, by1 - out - gap);
    let (cx2, cy2) = (bx2 + out + gap, by2 + out + gap);

    let cut = s + 2 * gap;
    let horiz = axis_plan(band.w as i64, s, gap, cut, gap);
    let vert = axis_plan(band.h as i64, s, gap, cut, gap);
    let (hc, vc) = (horiz.corner_cut, vert.corner_cut);

    // Corner handles straddle the band's corner; their cutouts are anchored
    // there and shrink inward only, so a corner keeps its clear space whatever
    // the ladder decides. Width comes from the horizontal ladder, height from
    // the vertical one, independently clamped — a corner cutout is routinely
    // not square.
    let corner = |kind, hx: i64, hy: i64, kx1: i64, ky1: i64| Handle {
        kind: HandleKind::Corner(kind),
        rect: rect_i64(hx, hy, hx + s, hy + s),
        cutout: rect_i64(kx1, ky1, kx1 + hc, ky1 + vc),
    };
    let mut handles = vec![
        corner(Cor::NW, bx1 - out, by1 - out, cx1, cy1),
        corner(Cor::NE, bx2 + out - s, by1 - out, cx2 - hc, cy1),
        corner(Cor::SW, bx1 - out, by2 + out - s, cx1, cy2 - vc),
        corner(Cor::SE, bx2 + out - s, by2 + out - s, cx2 - hc, cy2 - vc),
    ];

    // Mid-edge handles are centred on their edge and their cutouts are
    // centred on them, so `inflate(rect, gap)` is the whole story.
    let mut mid = |kind: HandleKind, x: i64, y: i64| {
        let rect = rect_i64(x, y, x + s, y + s);
        handles.push(Handle {
            kind,
            rect,
            cutout: inflate(rect, total),
        });
    };
    let mid_x = bx1 + (band.w as i64 - s) / 2;
    let mid_y = by1 + (band.h as i64 - s) / 2;
    if horiz.mid {
        mid(HandleKind::Edge(Dir::N), mid_x, by1 - out);
        mid(HandleKind::Edge(Dir::S), mid_x, by2 + out - s);
    }
    if vert.mid {
        mid(HandleKind::Edge(Dir::W), bx1 - out, mid_y);
        mid(HandleKind::Edge(Dir::E), bx2 + out - s, mid_y);
    }

    // No cutout may cross the band's centre line on an axis it does not
    // straddle. This is what makes the cutouts PAIRWISE DISJOINT, which both
    // platform painters rely on: they erase the cutouts with a single even-odd
    // path, and an overlap flips parity back on and paints a sliver of band
    // inside the gap.
    //
    // Two cases actually reach it, both needing a fat `--border` on a small
    // region. Along one edge, the two corner cutouts close on each other; and
    // across a narrow band, the two opposite MID-edge cutouts do — each
    // reaches `s + gap` in from its own edge, so they collide once the
    // perpendicular extent drops below `2*(s + gap)`, which the per-axis
    // ladder never looks at because it governs the *other* axis.
    //
    // Clamping them to meet at the centre is also simply what should be drawn
    // there: two clear spaces merging, not one eating the other.
    let split_x = bx1 + band.w as i64 / 2;
    let split_y = by1 + band.h as i64 / 2;
    for h in &mut handles {
        let (west, north) = handle_sides(h.kind);
        let (mut x1, mut y1) = (h.cutout.x as i64, h.cutout.y as i64);
        let (mut cx2, mut cy2) = (x2(&h.cutout), y2(&h.cutout));
        match west {
            Some(true) => cx2 = cx2.min(split_x),
            Some(false) => x1 = x1.max(split_x),
            None => {}
        }
        match north {
            Some(true) => cy2 = cy2.min(split_y),
            Some(false) => y1 = y1.max(split_y),
            None => {}
        }
        h.cutout = rect_i64(x1, y1, cx2, cy2);
    }

    let (panel, orientation) = place_panel(&band, spec, work_areas);
    let buttons = panel_buttons(&panel, spec, orientation);

    // The window must cover every cutout, not just the band: a handle sticks
    // out past the band, and its cutout sticks out further still. Unioning
    // the cutouts covers both, since a cutout contains its handle.
    let outer = handles
        .iter()
        .fold(union(&band, &panel), |acc, h| union(&acc, &h.cutout));

    FrameLayout {
        region,
        outer,
        band,
        white_band,
        hole,
        handles,
        panel,
        orientation,
        buttons,
    }
}

/// Panel size for an orientation: `short` is count-independent, which is the
/// whole reason the placement cascade below can decide orientation before it
/// knows how many buttons there are.
fn panel_size(spec: &FrameSpec, orientation: Orientation) -> (i64, i64) {
    let (btn, outline, n) = (
        spec.button as i64,
        spec.outline as i64,
        spec.buttons as i64,
    );
    let short = btn + 2 * outline;
    let long = n * btn + (n + 1) * outline;
    match orientation {
        Orientation::Vertical => (short, long),
        Orientation::Horizontal => (long, short),
    }
}

/// Clowd's floating-buttons placement (DESIGN §5), re-ordered vertical-first.
///
/// Two details are load-bearing and both come straight from Clowd:
///
/// - **Only `short` is tested.** The long axis is never checked for fit;
///   overflow is handled by the final clamp. Testing it would make the chosen
///   side depend on the button count, so adding a button could flip the panel
///   to the other side of the region.
/// - **The gap collapses rather than overflowing.** `min(wa.right, sel.right +
///   GAP + short) - short` yields the full gap when there is room and sits
///   flush against the work-area edge when there is not.
///
/// The monitor is picked by the region's *centre*, not by intersection area:
/// a region straddling two displays has one obvious "home" and the panel
/// should not hop between displays as the region is nudged.
fn place_panel(band: &Rect, spec: &FrameSpec, work_areas: &[Rect]) -> (Rect, Orientation) {
    let gap = spec.gap as i64;
    let edge = spec.edge_margin as i64;
    // `short` = min(w, h) for either orientation, and independent of the
    // button count — the only dimension the cascade below is allowed to test.
    let short = panel_size(spec, Orientation::Vertical).0;
    let long = panel_size(spec, Orientation::Horizontal).0;

    // The band is a symmetric inflation of the region, so the two share a
    // centre exactly (including the odd-width case); using the band's saves
    // threading the region through, and the band is what everything else here
    // is anchored to anyway.
    let cx = band.x as i64 + band.w as i64 / 2;
    let cy = band.y as i64 + band.h as i64 / 2;
    let wa = work_areas
        .iter()
        .find(|w| contains_i64(w, cx, cy))
        .or_else(|| {
            // No work area contains the centre (a region dragged into the gap
            // between two non-aligned displays, or onto a disconnected one):
            // fall back to the nearest rather than to nothing, so the panel
            // still lands somewhere the user can reach.
            work_areas.iter().min_by_key(|w| rect_distance_sq(band, w))
        });

    let Some(wa) = wa else {
        // Defensive: monitor enumeration should never be empty. Branch 1
        // unclamped — right of the band, top-aligned, full gap.
        let (w, h) = panel_size(spec, Orientation::Vertical);
        let x = x2(band) + gap;
        let y = band.y as i64;
        return (rect_i64(x, y, x + w, y + h), Orientation::Vertical);
    };

    // `sel` is the BAND clipped to the work area: the panel must clear the
    // border, not just the region, and clipping is what keeps the space tests
    // meaningful when the band hangs off the display (which it routinely does
    // — the band is inflated outward, so a region flush with a screen edge
    // puts it partly off-screen). An empty intersection means the band is
    // wholly outside this work area; the unclipped band is then the only
    // sensible anchor.
    let clipped = intersect(band, wa);
    let sel = if clipped.w == 0 || clipped.h == 0 {
        *band
    } else {
        clipped
    };
    let (sl, st, sr, sb) = (sel.x as i64, sel.y as i64, x2(&sel), y2(&sel));
    let (wl, wt, wr, wb) = (wa.x as i64, wa.y as i64, x2(wa), y2(wa));

    let right_space = (wr - sr).max(0) - edge;
    let left_space = (sl - wl).max(0) - edge;
    let bottom_space = (wb - sb).max(0) - edge;
    let top_space = (st - wt).max(0) - edge;

    let (orientation, x, y) = if right_space >= short {
        // The stated preference: a column hugging the outer edge of the right
        // border, top-aligned — "top right".
        (Orientation::Vertical, wr.min(sr + gap + short) - short, st)
    } else if left_space >= short {
        (Orientation::Vertical, (sl - gap - short).max(wl), st)
    } else if bottom_space >= short {
        // Flipped to a row: right-aligned, so the panel stays in the same
        // visual corner it occupied as a column.
        (
            Orientation::Horizontal,
            sr - long,
            wb.min(sb + gap + short) - short,
        )
    } else if top_space >= short {
        (Orientation::Horizontal, sr - long, (st - gap - short).max(wt))
    } else {
        // Last resort: the region fills the work area, so the panel goes
        // INSIDE it and will appear in the shared output. Accepted — there is
        // nowhere else — and matches both Clowd and the previous
        // implementation. §3 ranks the panel above the hole in hit-testing
        // precisely so it stays clickable here.
        (Orientation::Horizontal, sr - long, st + gap)
    };

    let (w, h) = panel_size(spec, orientation);
    // `.max()` applied last: for a panel larger than the work area the
    // top-left corner wins, which keeps it reachable instead of pushing it off
    // the opposite edge.
    let x = x.min(wr - w).max(wl);
    let y = y.min(wb - h).max(wt);
    (rect_i64(x, y, x + w, y + h), orientation)
}

/// Buttons flush inside the panel, separated by an `outline`-thick hairline
/// (which is the panel's own white showing through between them).
fn panel_buttons(panel: &Rect, spec: &FrameSpec, orientation: Orientation) -> Vec<Rect> {
    let (btn, outline) = (spec.button as i64, spec.outline as i64);
    let (px, py) = (panel.x as i64 + outline, panel.y as i64 + outline);
    (0..spec.buttons as i64)
        .map(|i| {
            let step = i * (btn + outline);
            let (x, y) = match orientation {
                Orientation::Vertical => (px, py + step),
                Orientation::Horizontal => (px + step, py),
            };
            rect_i64(x, y, x + btn, y + btn)
        })
        .collect()
}

// ---- hit testing -----------------------------------------------------------

/// Classifies point `p` (capture space) against the layout, in DESIGN §3's
/// order — which is NOT the paint order:
///
/// ```text
/// 1. a button rect      -> Button(i)
/// 2. panel background   -> Caption
/// 3. in hole            -> Outside
/// 4. in a handle RECT   -> Edge(dir) / Corner(cor)
/// 5. in a cutout        -> Outside
/// 6. in band            -> Caption
/// 7. otherwise          -> Outside
/// ```
///
/// Steps 1-2 precede everything so the panel stays clickable in the §5
/// last-resort placement, where it sits on top of the region.
///
/// Step 3 before step 4 is the load-bearing one: a handle reaches
/// `overhang_in` back inside the band's inner edge, and testing the hole first
/// is what stops it stealing a click meant for the application underneath. The
/// clipping is implicit — there is no arithmetic to keep in sync — which is
/// exactly why it is done this way. (`slack` makes the handle stop one unit
/// short of the region regardless, so today this only ever protects the
/// clearance margin; it costs nothing and removes the class of bug.)
///
/// Step 4 before steps 5 and 6 is the other half. A handle lies inside its own
/// cutout and overhangs the band (§2), so either later step would swallow it:
/// step 5 would call it `Outside` and step 6 would never be reached out past
/// the band's edge.
///
/// **The hit surface is exactly the painted surface** (§3). The cutout gap is
/// visibly a gap, so step 5 makes it behave like one — click-through, like the
/// hollow interior. That is also forced on Windows, where the window region
/// must equal the painted area (§6): a region pixel no `WM_PAINT` writes keeps
/// stale screen content and smears as the frame is dragged.
///
/// When `!resizable`, a handle demotes to `Caption` — it is still painted, and
/// the frame still moves — but the cutout around it stays a hole, because it
/// is still unpainted.
pub fn hit_test(l: &FrameLayout, resizable: bool, p: (i32, i32)) -> Zone {
    for (i, b) in l.buttons.iter().enumerate() {
        if contains(b, p) {
            return Zone::Button(i);
        }
    }
    if contains(&l.panel, p) {
        return Zone::Caption;
    }
    if contains(&l.hole, p) {
        // Hollow interior: the user's own content — click-through.
        return Zone::Outside;
    }
    for h in &l.handles {
        if contains(&h.rect, p) {
            return match (resizable, h.kind) {
                (false, _) => Zone::Caption,
                (true, HandleKind::Edge(d)) => Zone::Edge(d),
                (true, HandleKind::Corner(c)) => Zone::Corner(c),
            };
        }
    }
    for h in &l.handles {
        // The clear space around a handle is unpainted, so it is a hole.
        if contains(&h.cutout, p) {
            return Zone::Outside;
        }
    }
    if contains(&l.band, p) {
        // The painted ring — every hole in it has already been answered
        // above. Drag.
        return Zone::Caption;
    }
    Zone::Outside
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

    /// The canonical spec: 100%, the default 4-unit border, one button.
    /// border 2+2, S = 12, overhang 4 in / 4 out, slack 5, gap 4, button 30,
    /// outline 2, panel gap 8, edge 2.
    fn spec1() -> FrameSpec {
        FrameSpec::scaled(1.0, BASE_BORDER, 1)
    }

    fn spec_n(scale: f64, base: u32, buttons: u32) -> FrameSpec {
        FrameSpec::scaled(scale, base, buttons)
    }

    fn contains_rect(outer: &Rect, inner: &Rect) -> bool {
        outer.x as i64 <= inner.x as i64
            && outer.y as i64 <= inner.y as i64
            && x2(outer) >= x2(inner)
            && y2(outer) >= y2(inner)
    }

    fn intersect_area(a: &Rect, b: &Rect) -> i64 {
        let i = intersect(a, b);
        i.w as i64 * i.h as i64
    }

    fn handle(l: &FrameLayout, kind: HandleKind) -> Option<&Handle> {
        l.handles.iter().find(|h| h.kind == kind)
    }

    /// Wide-open work area: branch 1 always applies with room to spare.
    const BIG_WA: Rect = Rect {
        x: -10000,
        y: -10000,
        w: 30000,
        h: 30000,
    };

    /// A 1920x1080 display with the macOS menu bar (25) and Dock (50)
    /// excluded — i.e. what `NSScreen::visibleFrame` reports.
    const MAC_WA: Rect = Rect {
        x: 0,
        y: 25,
        w: 1920,
        h: 1005,
    };

    /// Every (scale, --border) pair the invariant tests sweep. Covers the
    /// scales in DESIGN §2's table plus both ends of `--border`'s range.
    const SWEEP: [(f64, u32); 12] = [
        (1.0, 4),
        (1.25, 4),
        (1.5, 4),
        (2.0, 4),
        (3.0, 4),
        (1.0, 32),
        (1.25, 32),
        (1.5, 32),
        (2.0, 32),
        (3.0, 32),
        (1.75, 4),
        (0.5, 4),
    ];

    // ---- §1 border ---------------------------------------------------------

    #[test]
    fn border_spec_scale_table() {
        // DESIGN §1's table verbatim, BASE = 4. The odd unit always goes to
        // the accent, so the accent is never thinner than the white hairline.
        for (scale, white, accent) in [
            (1.00, 2, 2),
            (1.25, 2, 3),
            (1.50, 3, 3),
            (1.75, 3, 4),
            (2.00, 4, 4),
            (3.00, 6, 6),
        ] {
            let b = BorderSpec::scaled(scale, BASE_BORDER);
            assert_eq!(b, BorderSpec { white, accent }, "scale {scale}");
            assert!(b.accent >= b.white, "accent gets the odd unit");
        }
        // Downscale floor: `total` never drops below BASE, so a sub-100%
        // display still gets a border thick enough to grab.
        assert_eq!(
            BorderSpec::scaled(0.5, 4),
            BorderSpec {
                white: 2,
                accent: 2
            }
        );
        assert_eq!(BorderSpec::scaled(0.1, 4).total(), 4);
        // A non-default --border scales the same way: 8 at 150% -> 12.
        assert_eq!(
            BorderSpec::scaled(1.5, 8),
            BorderSpec {
                white: 6,
                accent: 6
            }
        );
    }

    #[test]
    fn frame_spec_handle_and_overhang_table() {
        // DESIGN §2's size table, and §1/§2's overhang/slack consequences.
        for (scale, total, s, overhang_in, slack) in [
            (1.00, 4u32, 12u32, 4u32, 5u32),
            (1.25, 5, 15, 5, 6),
            (1.50, 6, 18, 6, 7),
            (2.00, 8, 24, 8, 9),
            (3.00, 12, 36, 12, 13),
        ] {
            let spec = spec_n(scale, BASE_BORDER, 1);
            assert_eq!(spec.border.total(), total, "scale {scale}");
            assert_eq!(spec.handle, s, "scale {scale}");
            assert_eq!(spec.handle_overhang_in(), overhang_in, "scale {scale}");
            assert_eq!(spec.slack(), slack, "scale {scale}");
            // The odd unit goes OUTWARD, away from the region.
            assert!(spec.handle_overhang_out() >= spec.handle_overhang_in());
            assert_eq!(
                spec.handle_overhang_in() + spec.handle_overhang_out(),
                s - total
            );
        }
        // A fat --border is the only thing that makes the `max` bite: the
        // handle may never be thinner than the line it interrupts.
        let fat = spec_n(1.0, 32, 1);
        assert_eq!((fat.border.total(), fat.handle), (32, 32));
        assert_eq!((fat.handle_overhang_in(), fat.slack()), (0, 1));
    }

    #[test]
    fn frame_spec_scales_every_measurement() {
        let s = FrameSpec::scaled(1.5, BASE_BORDER, 1);
        assert_eq!(
            s.border,
            BorderSpec {
                white: 3,
                accent: 3
            }
        );
        assert_eq!(s.button, 45); // 30 * 1.5
        assert_eq!(s.outline, 3); // == border.white
        assert_eq!(s.gap, 12); // 8 * 1.5
        assert_eq!(s.edge_margin, 3); // 2 * 1.5
        // macOS: capture units are already points, so nothing scales.
        let s = spec1();
        assert_eq!(
            (s.button, s.outline, s.gap, s.edge_margin),
            (BASE_BUTTON, 2, BASE_GAP, BASE_EDGE_MARGIN)
        );
        // A button never rounds away to nothing, however far down we scale.
        assert!(FrameSpec::scaled(0.01, 4, 1).button >= 1);
    }

    // ---- §2 handle fill ----------------------------------------------------

    #[test]
    fn handle_ring_and_layers_table() {
        // DESIGN §2's table: S, r, and the resulting white/core sides. Integer
        // division is what keeps the core growing monotonically across S = 9.
        // The S = 12 row is the 100% anchor now that BASE_HANDLE is 12: a 2px
        // accent rim, a 2px white ring and a 4x4 accent core.
        for (side, ring, white_side, core_side) in [
            (6u32, 1u32, 4u32, Some(2u32)),
            (8, 1, 6, Some(4)),
            (9, 1, 7, Some(5)),
            (12, 2, 8, Some(4)),
            (18, 3, 12, Some(6)),
        ] {
            assert_eq!(handle_ring(side), ring, "ring for S={side}");
            let h = r(100, 200, side, side);
            let (white, core) = handle_layers(h);
            let white = white.expect("every size in the table has a white ring");
            assert_eq!(
                white,
                r(
                    100 + ring as i32,
                    200 + ring as i32,
                    white_side,
                    white_side
                )
            );
            let cs = core_side.expect("every size in the table has a core");
            let c = core.expect("core expected");
            assert_eq!(c, r(100 + 2 * ring as i32, 200 + 2 * ring as i32, cs, cs));
            // Every layer nests strictly inside the previous one.
            assert!(contains_rect(&white, &c));
            assert!(contains_rect(&h, &white));
        }
        // Flooring, not rounding: r stays at 1 right up to 12. With `round`,
        // S=9 would jump to r=2 and collapse the core from 5x5 to 1x1.
        assert_eq!(handle_ring(9), 1);
        assert_eq!(handle_ring(11), 1);
        assert_eq!(handle_ring(12), 2);
        assert_eq!(handle_ring(17), 2);
        // Floored at 1 so the rings never vanish below the base size.
        assert_eq!(handle_ring(1), 1);
        assert_eq!(handle_ring(0), 1);
        // Total for degenerate input: the `inner <= 2r` branch is unreachable
        // at BASE_HANDLE = 6 but must still not panic.
        assert_eq!(handle_layers(r(0, 0, 4, 4)), (Some(r(1, 1, 2, 2)), None));
        assert_eq!(handle_layers(r(0, 0, 1, 1)), (None, None));
    }

    // ---- §1/§2 layout geometry --------------------------------------------

    #[test]
    fn layout_band_hole_and_seam() {
        let l = compute_layout(r(100, 100, 300, 200), &spec1(), &[BIG_WA]);
        assert_eq!(l.region, r(100, 100, 300, 200)); // carried, not re-derived
        // slack 5, white 2, total 4.
        assert_eq!(l.hole, r(95, 95, 310, 210));
        assert_eq!(l.white_band, r(93, 93, 314, 214));
        assert_eq!(l.band, r(91, 91, 318, 218));
        assert_eq!(l.hole.x - l.band.x, spec1().border.total() as i32);
    }

    /// THE invariant this whole design exists to protect: nothing painted may
    /// land inside the captured region, and the clearance is exactly one unit
    /// on all four sides at every scale — because `slack` and the inward
    /// overhang are two readings of the same expression, not because the
    /// numbers happen to work out at the DPIs someone tried.
    #[test]
    fn layout_handles_clear_the_region_by_exactly_one_unit() {
        let regions = [
            r(0, 0, MIN_REGION, MIN_REGION),
            r(100, 100, 300, 200),
            r(-1800, 300, 640, 480),
            r(2000, 100, 1201, 801), // odd extents: centring must still floor
        ];
        for &region in &regions {
            for (scale, base) in SWEEP {
                let spec = spec_n(scale, base, 1);
                let l = compute_layout(region, &spec, &[BIG_WA]);
                let label = format!("region {region:?} scale {scale} border {base}");
                let (rx1, ry1) = (region.x as i64, region.y as i64);
                let (rx2, ry2) = (x2(&region), y2(&region));
                for h in &l.handles {
                    // No overlap at all...
                    assert_eq!(intersect_area(&h.rect, &region), 0, "{label} {:?}", h.kind);
                    // ...and specifically, the inner edge is region ∓ 1 on
                    // whichever sides this handle faces. A handle on the west
                    // side ends at region.x - 1; one on the east starts at
                    // region.right + 1.
                    let (hx1, hy1) = (h.rect.x as i64, h.rect.y as i64);
                    let (hx2, hy2) = (x2(&h.rect), y2(&h.rect));
                    if hx2 <= rx1 {
                        assert_eq!(hx2, rx1 - 1, "{label} {:?} west edge", h.kind);
                    }
                    if hx1 >= rx2 {
                        assert_eq!(hx1, rx2 + 1, "{label} {:?} east edge", h.kind);
                    }
                    if hy2 <= ry1 {
                        assert_eq!(hy2, ry1 - 1, "{label} {:?} north edge", h.kind);
                    }
                    if hy1 >= ry2 {
                        assert_eq!(hy1, ry2 + 1, "{label} {:?} south edge", h.kind);
                    }
                }
                // Every corner handle faces two sides, so all four edges are
                // covered by the assertions above on every run.
                assert!(handle(&l, HandleKind::Corner(Cor::NW)).is_some());
                assert!(handle(&l, HandleKind::Corner(Cor::SE)).is_some());
            }
        }
    }

    #[test]
    fn layout_handle_positions_and_cutouts() {
        // S = 12, overhang 4 out, gap 4; band (91,91)..(409,309), hole
        // (95,95)..(405,305). Both axes comfortably in tier 1.
        let l = compute_layout(r(100, 100, 300, 200), &spec1(), &[BIG_WA]);
        assert_eq!(l.handles.len(), 8);

        // Corners straddle the band's corner: 4 units out, 4 units in.
        let nw = handle(&l, HandleKind::Corner(Cor::NW)).unwrap();
        assert_eq!(nw.rect, r(87, 87, 12, 12)); // right edge 99 = region.x - 1
        assert_eq!(nw.cutout, r(83, 83, 20, 20)); // S + 2*gap = 20
        let se = handle(&l, HandleKind::Corner(Cor::SE)).unwrap();
        assert_eq!(se.rect, r(401, 301, 12, 12)); // 401 = region.right + 1
        assert_eq!(se.cutout, r(397, 297, 20, 20));
        assert_eq!(
            handle(&l, HandleKind::Corner(Cor::NE)).unwrap().rect,
            r(401, 87, 12, 12)
        );
        assert_eq!(
            handle(&l, HandleKind::Corner(Cor::SW)).unwrap().rect,
            r(87, 301, 12, 12)
        );

        // Mid-edge handles centred on their edge, cutouts centred on them.
        let n = handle(&l, HandleKind::Edge(Dir::N)).unwrap();
        assert_eq!(n.rect, r(244, 87, 12, 12)); // 91 + (318-12)/2
        assert_eq!(n.cutout, r(240, 83, 20, 20));
        let e = handle(&l, HandleKind::Edge(Dir::E)).unwrap();
        assert_eq!(e.rect, r(401, 194, 12, 12)); // 91 + (218-12)/2
        assert_eq!(e.cutout, r(397, 190, 20, 20));
        assert_eq!(
            handle(&l, HandleKind::Edge(Dir::S)).unwrap().rect,
            r(244, 301, 12, 12)
        );
        assert_eq!(
            handle(&l, HandleKind::Edge(Dir::W)).unwrap().rect,
            r(87, 194, 12, 12)
        );

        // In tier 1 a cutout always fully contains its handle.
        for h in &l.handles {
            assert!(contains_rect(&h.cutout, &h.rect), "{:?}", h.kind);
        }
        // outer covers the overhang, not just the band.
        assert_eq!(l.outer.x, 83);
        assert_eq!(l.outer.y, 83);
        assert_eq!(y2(&l.outer), 317);
    }

    #[test]
    fn layout_cutouts_are_pairwise_disjoint() {
        // The AppKit painter erases the cutouts with one even-odd path, which
        // silently mis-fills if two cutouts overlap. They never do in the
        // reachable parameter space; this pins that.
        for (scale, base) in SWEEP {
            for region in [
                r(0, 0, MIN_REGION, MIN_REGION),
                r(10, 10, MIN_REGION, 4000),
                r(10, 10, 4000, MIN_REGION),
                r(100, 100, 300, 200),
            ] {
                let l = compute_layout(region, &spec_n(scale, base, 1), &[BIG_WA]);
                for (i, a) in l.handles.iter().enumerate() {
                    for b in &l.handles[i + 1..] {
                        assert_eq!(
                            intersect_area(&a.cutout, &b.cutout),
                            0,
                            "{:?} vs {:?} at scale {scale} border {base} region {region:?}",
                            a.kind,
                            b.kind
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn handle_degradation_tier_two_drops_the_mid_edge() {
        // --border 32: S = 32 (the floor bites), gap = 32, CUT = 96, RUN = 32,
        // overhang 0 so slack is back to 1. Tier 1 needs L >= 352, tier 2
        // L >= 224. Region 200x400 -> band 266x466: horizontal in tier 2,
        // vertical in tier 1. So N/S go, W/E stay — the ladder is per-axis.
        let spec = spec_n(1.0, 32, 1);
        let l = compute_layout(r(500, 500, 200, 400), &spec, &[BIG_WA]);
        assert_eq!((l.band.w, l.band.h), (266, 466));
        assert_eq!(l.handles.len(), 6);
        assert!(handle(&l, HandleKind::Edge(Dir::N)).is_none());
        assert!(handle(&l, HandleKind::Edge(Dir::S)).is_none());
        assert!(handle(&l, HandleKind::Edge(Dir::W)).is_some());
        assert!(handle(&l, HandleKind::Edge(Dir::E)).is_some());
        // Both corners keep FULL cutouts in tier 2 — nothing is clamped — and
        // they still contain their handles.
        let nw = handle(&l, HandleKind::Corner(Cor::NW)).unwrap();
        assert_eq!(nw.cutout, r(l.band.x - 32, l.band.y - 32, 96, 96));
        assert!(contains_rect(&nw.cutout, &nw.rect));
    }

    #[test]
    fn handle_degradation_tier_three_clamps_corner_cutouts() {
        // S = 32, region at the MIN_REGION floor -> band 130x130, below tier
        // 2's 224. (L - RUN)/2 = 49 is below the `s + gap` = 64 floor, so the
        // floor wins: the cutout keeps just enough extent to reach its
        // handle's inner end, and the stub is what shrinks.
        let spec = spec_n(1.0, 32, 1);
        let l = compute_layout(r(500, 500, MIN_REGION, MIN_REGION), &spec, &[BIG_WA]);
        assert_eq!((l.band.w, l.band.h), (130, 130));
        assert_eq!(l.handles.len(), 4); // corners only, never dropped
        let nw = handle(&l, HandleKind::Corner(Cor::NW)).unwrap();
        // Anchored at the corner (gap outside the band) and shrunk inward.
        assert_eq!(nw.cutout, r(l.band.x - 32, l.band.y - 32, 64, 64));
        let se = handle(&l, HandleKind::Corner(Cor::SE)).unwrap();
        assert_eq!(se.cutout, r(l.band.x + 98, l.band.y + 98, 64, 64));
        assert_eq!(intersect_area(&nw.cutout, &se.cutout), 0);
        // Even clamped, the cutout still covers its handle — that is what the
        // `s + gap` floor buys, and what DESIGN §2's `s` floor did not.
        assert!(contains_rect(&nw.cutout, &nw.rect));
        // A stub of band survives between the two cutouts, just a narrower one
        // than RUN: 130 - 2*(64 - 32) = 66 units of painted band.
        assert_eq!(
            (l.band.x as i64 + 98) - (l.band.x as i64 - 32 + 64),
            66
        );
    }

    #[test]
    fn opposite_mid_edge_cutouts_meet_at_the_centre_line() {
        // The cross-axis collision the per-axis ladder cannot see: a tall,
        // narrow region at 125% with --border 32 gives total = S = gap = 40
        // and a band only 146 wide, while the VERTICAL ladder is comfortably
        // in tier 1 and so keeps the W/E handles. Each of their cutouts wants
        // to reach S + gap = 80 in from its own edge; 2*80 > 146, so without
        // the clamp they would overlap by 14 and the painter would fill a
        // sliver of band inside the merged gap.
        let spec = spec_n(1.25, 32, 1);
        assert_eq!((spec.border.total(), spec.handle), (40, 40));
        let l = compute_layout(r(10, 10, MIN_REGION, 4000), &spec, &[BIG_WA]);
        assert_eq!(l.band.w, 146);
        let split = l.band.x as i64 + 73;
        let w = handle(&l, HandleKind::Edge(Dir::W)).unwrap();
        let e = handle(&l, HandleKind::Edge(Dir::E)).unwrap();
        assert_eq!(x2(&w.cutout), split);
        assert_eq!(e.cutout.x as i64, split);
        assert_eq!(intersect_area(&w.cutout, &e.cutout), 0);
        // Both still fully cover their handles.
        assert!(contains_rect(&w.cutout, &w.rect));
        assert!(contains_rect(&e.cutout, &e.rect));
    }

    #[test]
    fn handle_cutouts_are_not_square_when_the_axes_disagree() {
        // Horizontal tier 3 (band.w 130), vertical tier 1 (band.h 466): the
        // corner cutout takes its width from one ladder and its height from
        // the other, so it is emphatically not a square.
        let spec = spec_n(1.0, 32, 1);
        let l = compute_layout(r(500, 500, MIN_REGION, 400), &spec, &[BIG_WA]);
        assert_eq!((l.band.w, l.band.h), (130, 466));
        let nw = handle(&l, HandleKind::Corner(Cor::NW)).unwrap();
        assert_eq!((nw.cutout.w, nw.cutout.h), (64, 96)); // tier 3 floor / full
        // W/E survive (vertical tier 1); N/S do not.
        assert!(handle(&l, HandleKind::Edge(Dir::W)).is_some());
        assert!(handle(&l, HandleKind::Edge(Dir::N)).is_none());
    }

    #[test]
    fn min_region_stays_in_tier_one_at_the_default_border() {
        // The default border must never degrade on a minimum-size region:
        // band 82x82 against tier 1's threshold of 3*20 + 2*4 = 68. The
        // margin narrowed from 26 to 14 when BASE_HANDLE went 6 -> 12, but a
        // 64x64 region at 100% still keeps all eight handles.
        let l = compute_layout(r(0, 0, MIN_REGION, MIN_REGION), &spec1(), &[BIG_WA]);
        assert_eq!((l.band.w, l.band.h), (82, 82));
        assert_eq!(l.handles.len(), 8);
    }

    // ---- §5 panel placement ------------------------------------------------

    #[test]
    fn panel_branch_1_right_top_aligned() {
        // The preferred placement: a column GAP outside the right border,
        // top-aligned with the band. short = long = 34 at one button.
        let l = compute_layout(r(100, 100, 300, 200), &spec1(), &[BIG_WA]);
        assert_eq!(l.orientation, Orientation::Vertical);
        assert_eq!(l.panel, r(417, 91, 34, 34)); // band.right 409 + gap 8
        assert_eq!(l.buttons, vec![r(419, 93, 30, 30)]); // outline-inset
        assert_eq!(l.outer, r(83, 83, 368, 234)); // covers panel AND cutouts
    }

    #[test]
    fn panel_branch_2_left_when_right_is_tight() {
        // Region hugging the right edge of a mac work area: 9 units of room
        // on the right (after EDGE), so the column flips to the left.
        let l = compute_layout(r(1600, 300, 300, 200), &spec1(), &[MAC_WA]);
        assert_eq!(l.orientation, Orientation::Vertical);
        assert_eq!(l.band, r(1591, 291, 318, 218));
        assert_eq!(l.panel, r(1549, 291, 34, 34)); // 1591 - 8 - 34
    }

    #[test]
    fn panel_branch_3_below_when_both_sides_are_tight() {
        // Region spanning nearly the full width: neither side fits a column,
        // so the panel becomes a row below the band, right-aligned.
        let l = compute_layout(r(20, 300, 1875, 200), &spec1(), &[MAC_WA]);
        assert_eq!(l.orientation, Orientation::Horizontal);
        assert_eq!(l.band, r(11, 291, 1893, 218));
        assert_eq!(l.panel, r(1870, 517, 34, 34)); // right-aligned, 509 + 8
    }

    #[test]
    fn panel_branch_4_above_when_below_is_tight_too() {
        // Same, but the region also reaches down into the Dock's margin, so
        // the row goes above the band instead.
        let l = compute_layout(r(20, 300, 1875, 700), &spec1(), &[MAC_WA]);
        assert_eq!(l.orientation, Orientation::Horizontal);
        assert_eq!(l.band, r(11, 291, 1893, 718));
        assert_eq!(l.panel, r(1870, 249, 34, 34)); // 291 - 8 - 34
    }

    #[test]
    fn panel_branch_5_falls_back_inside_the_region() {
        // Region == the whole work area. Every space test fails, so the panel
        // lands inside the region and will appear in the shared output — the
        // documented last resort. This also exercises the `sel` clip: the band
        // is inflated outward and so hangs off the work area on all four
        // sides, and the space tests must be run against the clipped rect.
        let region = r(0, 25, 1920, 1005);
        let l = compute_layout(region, &spec1(), &[MAC_WA]);
        assert_eq!(l.orientation, Orientation::Horizontal);
        assert_eq!(l.band, r(-9, 16, 1938, 1023));
        assert_eq!(l.panel, r(1886, 33, 34, 34)); // sel.top 25 + gap 8
        assert!(contains_rect(&MAC_WA, &l.panel));
        assert!(intersect_area(&l.panel, &region) > 0); // accepted overlap
    }

    #[test]
    fn panel_clamp_applies_min_before_max_on_both_axes() {
        // Work area smaller than the panel itself: `min` alone would push the
        // panel off the top-left, so the trailing `max` has to win on BOTH
        // axes and park it at the work-area origin.
        let tiny = r(0, 0, 20, 20);
        let l = compute_layout(r(0, 0, MIN_REGION, MIN_REGION), &spec1(), &[tiny]);
        assert_eq!(l.panel, r(0, 0, 34, 34));
        // And with a work area at negative coordinates, the same clamp lands
        // on that origin rather than on 0,0.
        let tiny_neg = r(-500, -400, 20, 20);
        let l = compute_layout(r(-500, -400, MIN_REGION, MIN_REGION), &spec1(), &[tiny_neg]);
        assert_eq!(l.panel, r(-500, -400, 34, 34));
    }

    #[test]
    fn panel_picks_the_work_area_containing_the_region_centre() {
        // Two displays, the left one at negative x. The region's centre is on
        // the left display, so that work area drives placement — and all the
        // arithmetic stays correct through negative coordinates.
        let was = [r(0, 0, 1920, 1080), r(-1920, 0, 1920, 1080)];
        let l = compute_layout(r(-1800, 300, 640, 480), &spec1(), &was);
        assert_eq!(l.band, r(-1809, 291, 658, 498));
        assert_eq!(l.orientation, Orientation::Vertical);
        assert_eq!(l.panel, r(-1143, 291, 34, 34)); // band.right -1151 + gap
        assert!(contains_rect(&was[1], &l.panel));
    }

    #[test]
    fn panel_falls_back_to_the_nearest_work_area() {
        // Centre lands in no work area at all (a gap between two displays of
        // different heights). The nearest one is used rather than none.
        let was = [r(0, 0, 800, 600), r(2000, 0, 800, 600)];
        let l = compute_layout(r(2200, 100, 200, 200), &spec1(), &was);
        assert!(contains_rect(&was[1], &l.panel));
        // Centre (1200, 700) is outside both; the left display is nearer.
        let l = compute_layout(r(1100, 620, 200, 160), &spec1(), &was);
        assert!(contains_rect(&was[0], &l.panel));
    }

    #[test]
    fn panel_no_work_areas_defensive() {
        // Empty enumeration (should never happen): branch 1, unclamped.
        let l = compute_layout(r(100, 100, 300, 200), &spec1(), &[]);
        assert_eq!(l.orientation, Orientation::Vertical);
        assert_eq!(l.panel, r(417, 91, 34, 34));
    }

    #[test]
    fn panel_orientation_is_independent_of_button_count() {
        // The whole point of testing only `short`: adding a button must not
        // move the panel to a different side of the region. Every branch,
        // 1 through 5, with 1..=4 buttons.
        let cases = [
            (r(100, 100, 300, 200), BIG_WA),
            (r(1600, 300, 300, 200), MAC_WA),
            (r(20, 300, 1875, 200), MAC_WA),
            (r(20, 300, 1875, 700), MAC_WA),
            (r(0, 25, 1920, 1005), MAC_WA),
        ];
        for (region, wa) in cases {
            let base = compute_layout(region, &spec_n(1.0, BASE_BORDER, 1), &[wa]);
            for n in 2..=4u32 {
                let spec = spec_n(1.0, BASE_BORDER, n);
                let l = compute_layout(region, &spec, &[wa]);
                assert_eq!(l.orientation, base.orientation, "{region:?} with {n} buttons");
                assert_eq!(l.buttons.len(), n as usize);
                // The short axis is count-independent by construction.
                let short = spec.button + 2 * spec.outline;
                match l.orientation {
                    Orientation::Vertical => assert_eq!(l.panel.w, short),
                    Orientation::Horizontal => assert_eq!(l.panel.h, short),
                }
                // Buttons tile the panel's long axis with hairline gaps.
                for b in &l.buttons {
                    assert!(contains_rect(&l.panel, b));
                    assert_eq!((b.w, b.h), (spec.button, spec.button));
                }
                for pair in l.buttons.windows(2) {
                    let gap = match l.orientation {
                        Orientation::Vertical => pair[1].y as i64 - y2(&pair[0]),
                        Orientation::Horizontal => pair[1].x as i64 - x2(&pair[0]),
                    };
                    assert_eq!(gap, spec.outline as i64);
                }
            }
        }
    }

    // ---- invariants --------------------------------------------------------

    /// The rest of the "nothing painted lands in the capture" story: the ring
    /// tiles exactly, the hole strictly contains the region, and every handle
    /// layer stays inside its handle. (The panel is the documented exception —
    /// §5 branch 5 — so it is checked here only where there is room.)
    #[test]
    fn painted_geometry_never_touches_the_region() {
        let regions = [
            r(0, 0, MIN_REGION, MIN_REGION),
            r(100, 100, 300, 200),
            r(-1800, 300, 640, 480),
            r(-50, -50, 5000, 3000),
            r(2000, 100, 1200, 800),
        ];
        for &region in &regions {
            for (scale, base) in SWEEP {
                let spec = spec_n(scale, base, 1);
                let l = compute_layout(region, &spec, &[BIG_WA]);
                let b = spec.border;
                let label = format!("region {region:?} scale {scale} border {base}");
                // The two lines exactly tile the ring: white_band is the seam,
                // with no gap and no overlap.
                assert_eq!(l.white_band.x as i64, l.hole.x as i64 - b.white as i64);
                assert_eq!(x2(&l.white_band), x2(&l.hole) + b.white as i64);
                assert_eq!(l.band.x as i64, l.white_band.x as i64 - b.accent as i64);
                assert!(contains_rect(&l.band, &l.white_band));
                assert!(contains_rect(&l.white_band, &l.hole));
                // hole ⊇ region with `slack` on every side.
                assert!(contains_rect(&l.hole, &region));
                assert_eq!(l.hole.x as i64, region.x as i64 - spec.slack() as i64);
                assert_eq!(y2(&l.hole), y2(&region) + spec.slack() as i64);
                assert!(!l.handles.is_empty());
                for h in &l.handles {
                    assert_eq!((h.rect.w, h.rect.h), (spec.handle, spec.handle));
                    assert_eq!(intersect_area(&h.rect, &region), 0, "{label}");
                    let (white, core) = handle_layers(h.rect);
                    for layer in [white, core].into_iter().flatten() {
                        assert!(contains_rect(&h.rect, &layer));
                        assert_eq!(intersect_area(&layer, &region), 0, "{label}");
                    }
                    // A cutout always fully covers its own handle — through
                    // both degradation tiers and the centre-line clamp.
                    // Otherwise part of the handle is painted onto unbroken
                    // band and is not in its own grab rect.
                    assert!(contains_rect(&h.cutout, &h.rect), "{label} {:?}", h.kind);
                    // outer must cover every cutout, or the overhanging part
                    // of a handle is unpaintable and unclickable.
                    assert!(contains_rect(&l.outer, &h.cutout), "{label}");
                }
                // With room to spare the panel clears the band entirely.
                assert_eq!(intersect_area(&l.panel, &region), 0, "{label}");
                assert_eq!(intersect_area(&l.panel, &l.band), 0, "{label}");
                assert!(contains_rect(&l.outer, &l.band));
                assert!(contains_rect(&l.outer, &l.panel));
            }
        }
    }

    #[test]
    fn translate_moves_every_rect() {
        let mut l = compute_layout(r(100, 100, 300, 200), &spec_n(1.0, 4, 3), &[BIG_WA]);
        let before = l.clone();
        l.translate(-40, 17);
        let shift = |r: &Rect| translated(*r, -40, 17);
        assert_eq!(l.region, shift(&before.region));
        assert_eq!(l.outer, shift(&before.outer));
        assert_eq!(l.band, shift(&before.band));
        assert_eq!(l.white_band, shift(&before.white_band));
        assert_eq!(l.hole, shift(&before.hole));
        assert_eq!(l.panel, shift(&before.panel));
        for (a, b) in l.handles.iter().zip(&before.handles) {
            assert_eq!(a.kind, b.kind);
            assert_eq!(a.rect, shift(&b.rect));
            assert_eq!(a.cutout, shift(&b.cutout));
        }
        for (a, b) in l.buttons.iter().zip(&before.buttons) {
            assert_eq!(*a, shift(b));
        }
        // A translation is rigid: hit-testing the moved point gives the same
        // zone it did before. (93,93) is on the NW handle, not just anywhere.
        assert_eq!(hit_test(&before, true, (93, 93)), Zone::Corner(Cor::NW));
        assert_eq!(
            hit_test(&l, true, (93 - 40, 93 + 17)),
            hit_test(&before, true, (93, 93))
        );
    }

    // ---- §3 hit testing ----------------------------------------------------

    /// band (91,91)..(409,309); hole (95,95)..(405,305); handles 12x12 with
    /// 20x20 cutouts; panel (417,91,34,34); button 0 (419,93,30,30).
    ///
    /// The NW handle is (87,87)..(99,99) and its cutout (83,83)..(103,103);
    /// note that the handle straddles BOTH the band's outer edge (91) and its
    /// inner edge (95, where the hole starts), which is what makes the
    /// ordering tests below non-trivial.
    fn hit_layout() -> FrameLayout {
        compute_layout(r(100, 100, 300, 200), &spec1(), &[BIG_WA])
    }

    #[test]
    fn hit_buttons_and_panel_background() {
        let l = hit_layout();
        assert_eq!(hit_test(&l, true, (420, 100)), Zone::Button(0));
        assert_eq!(hit_test(&l, true, (419, 93)), Zone::Button(0)); // inclusive corner
        assert_eq!(hit_test(&l, true, (449, 123)), Zone::Caption); // half-open: past it
        // Panel background (the outline) drags, like the border does.
        assert_eq!(hit_test(&l, true, (417, 91)), Zone::Caption);
        assert_eq!(hit_test(&l, true, (450, 124)), Zone::Caption);
    }

    #[test]
    fn hit_interior_is_click_through() {
        let l = hit_layout();
        assert_eq!(hit_test(&l, true, (250, 200)), Zone::Outside);
        // The hole extends `slack` outside the region, so even (95,95) falls
        // through — that margin is clearance, not border.
        assert_eq!(hit_test(&l, true, (95, 95)), Zone::Outside);
        assert_eq!(hit_test(&l, true, (100, 100)), Zone::Outside);
    }

    #[test]
    fn hit_handle_poking_into_the_hole_still_falls_through() {
        // THE ordering test. A handle is centred on the ring, so it reaches
        // `overhang_in` (4) back past the band's inner edge and overlaps the
        // hole: the NW handle spans (87,87)..(99,99) while the hole starts at
        // (95,95). (96,96) is inside both, and the HOLE must win — nothing may
        // steal a click that belongs to the application underneath.
        let l = hit_layout();
        let nw = handle(&l, HandleKind::Corner(Cor::NW)).unwrap();
        assert!(contains(&nw.rect, (96, 96)));
        assert!(contains(&l.hole, (96, 96)));
        assert_eq!(hit_test(&l, true, (96, 96)), Zone::Outside);
        // The other half of the ordering: the handle also reaches OUTSIDE the
        // band (87 < 91), and out there it must still win — a
        // `!band -> Outside` test ahead of the handles would kill it.
        assert!(!contains(&l.band, (88, 88)));
        assert_eq!(hit_test(&l, true, (88, 88)), Zone::Corner(Cor::NW));
        // Past the handle but still in its cutout: the gap is a real hole.
        assert!(contains(&nw.cutout, (85, 85)));
        assert!(!contains(&nw.rect, (85, 85)));
        assert_eq!(hit_test(&l, true, (85, 85)), Zone::Outside);
        // Past the cutout entirely: nothing of ours. (150 is clear of the NW
        // cutout's 83..103 and the W cutout's 190..210 y-ranges.)
        assert_eq!(hit_test(&l, true, (82, 82)), Zone::Outside);
        assert_eq!(hit_test(&l, true, (85, 150)), Zone::Outside);
    }

    #[test]
    fn hit_handles_and_ring() {
        let l = hit_layout();
        assert_eq!(hit_test(&l, true, (93, 93)), Zone::Corner(Cor::NW));
        assert_eq!(hit_test(&l, true, (405, 93)), Zone::Corner(Cor::NE));
        assert_eq!(hit_test(&l, true, (93, 305)), Zone::Corner(Cor::SW));
        assert_eq!(hit_test(&l, true, (405, 305)), Zone::Corner(Cor::SE));
        // Mid-edge handles.
        assert_eq!(hit_test(&l, true, (250, 93)), Zone::Edge(Dir::N));
        assert_eq!(hit_test(&l, true, (250, 305)), Zone::Edge(Dir::S));
        assert_eq!(hit_test(&l, true, (93, 200)), Zone::Edge(Dir::W));
        assert_eq!(hit_test(&l, true, (405, 200)), Zone::Edge(Dir::E));
        // Plain ring, clear of every cutout: drag, not resize. That is the
        // redesign — the whole border means "move".
        assert_eq!(hit_test(&l, true, (200, 93)), Zone::Caption);
        assert_eq!(hit_test(&l, true, (93, 150)), Zone::Caption);
    }

    /// DESIGN §3's reversal: the grab rect is the HANDLE, not its cutout. The
    /// clear space you can see the desktop through behaves like what it looks
    /// like, and on Windows the window region can then equal the painted area
    /// (§6) instead of carrying unpainted, smear-prone pixels.
    #[test]
    fn hit_cutout_gap_is_click_through_not_a_grab_extension() {
        let l = hit_layout();
        for kind in [
            HandleKind::Corner(Cor::NW),
            HandleKind::Corner(Cor::NE),
            HandleKind::Corner(Cor::SW),
            HandleKind::Corner(Cor::SE),
            HandleKind::Edge(Dir::N),
            HandleKind::Edge(Dir::S),
            HandleKind::Edge(Dir::W),
            HandleKind::Edge(Dir::E),
        ] {
            let h = handle(&l, kind).unwrap();
            // Every unit of the gap between the handle and the cutout edge is
            // a hole, on all four sides — scanned rather than sampled so a
            // one-sided regression cannot hide.
            for y in h.cutout.y..y2(&h.cutout) as i32 {
                for x in h.cutout.x..x2(&h.cutout) as i32 {
                    if contains(&h.rect, (x, y)) {
                        continue;
                    }
                    assert_eq!(
                        hit_test(&l, true, (x, y)),
                        Zone::Outside,
                        "{kind:?} gap at ({x},{y})"
                    );
                }
            }
        }
        // The band immediately past a cutout is back to Caption, so the hole
        // is the cutout and not one unit more.
        assert_eq!(hit_test(&l, true, (103, 93)), Zone::Caption);
        assert_eq!(hit_test(&l, true, (102, 93)), Zone::Outside);
    }

    #[test]
    fn hit_handle_rect_is_the_whole_grab_rect_including_the_overhang() {
        let l = hit_layout();
        for (kind, zone) in [
            (HandleKind::Corner(Cor::NW), Zone::Corner(Cor::NW)),
            (HandleKind::Corner(Cor::NE), Zone::Corner(Cor::NE)),
            (HandleKind::Corner(Cor::SW), Zone::Corner(Cor::SW)),
            (HandleKind::Corner(Cor::SE), Zone::Corner(Cor::SE)),
            (HandleKind::Edge(Dir::N), Zone::Edge(Dir::N)),
            (HandleKind::Edge(Dir::S), Zone::Edge(Dir::S)),
            (HandleKind::Edge(Dir::W), Zone::Edge(Dir::W)),
            (HandleKind::Edge(Dir::E), Zone::Edge(Dir::E)),
        ] {
            let h = handle(&l, kind).unwrap();
            let mut outside_the_band = 0;
            for y in h.rect.y..y2(&h.rect) as i32 {
                for x in h.rect.x..x2(&h.rect) as i32 {
                    if contains(&l.hole, (x, y)) {
                        continue; // §3 step 3 wins there, by design
                    }
                    assert_eq!(hit_test(&l, true, (x, y)), zone, "{kind:?} at ({x},{y})");
                    if !contains(&l.band, (x, y)) {
                        outside_the_band += 1;
                    }
                }
            }
            // `overhang_out` is 4 at 100%, so a good third of every handle is
            // outside the band and is resolved purely by step 4's precedence.
            assert!(outside_the_band > 0, "{kind:?} should overhang the band");
        }
    }

    #[test]
    fn hit_outside_frame() {
        let l = hit_layout();
        assert_eq!(hit_test(&l, true, (0, 0)), Zone::Outside);
        assert_eq!(hit_test(&l, true, (500, 500)), Zone::Outside);
        // The gap between the band (right edge 409) and the panel (417),
        // clear of any cutout.
        assert_eq!(hit_test(&l, true, (413, 150)), Zone::Outside);
        // Just past the band's half-open right/bottom edge, away from a
        // handle's overhang.
        assert_eq!(hit_test(&l, true, (409, 150)), Zone::Outside);
        assert_eq!(hit_test(&l, true, (200, 309)), Zone::Outside);
    }

    #[test]
    fn hit_not_resizable_demotes_handles_to_caption() {
        let l = hit_layout();
        assert_eq!(hit_test(&l, false, (93, 93)), Zone::Caption);
        assert_eq!(hit_test(&l, false, (250, 93)), Zone::Caption);
        assert_eq!(hit_test(&l, false, (200, 93)), Zone::Caption);
        // The part of a handle that overhangs the band is still PAINTED, so
        // it is still ours and still drags — only its meaning changes.
        assert!(!contains(&l.band, (88, 88)));
        assert_eq!(hit_test(&l, false, (88, 88)), Zone::Caption);
        // The cutout gap is unpainted either way, so it stays a hole.
        assert_eq!(hit_test(&l, false, (85, 85)), Zone::Outside);
        assert_eq!(hit_test(&l, false, (102, 93)), Zone::Outside);
        // Interior stays click-through, buttons stay buttons.
        assert_eq!(hit_test(&l, false, (250, 200)), Zone::Outside);
        assert_eq!(hit_test(&l, false, (420, 100)), Zone::Button(0));
    }

    #[test]
    fn hit_panel_beats_the_hole_in_the_fallback_placement() {
        // Branch 5 puts the panel on top of the region; steps 1-2 run before
        // the hole test precisely so it stays clickable there.
        let l = compute_layout(r(0, 25, 1920, 1005), &spec1(), &[MAC_WA]);
        assert!(intersect_area(&l.panel, &l.hole) > 0);
        let b = l.buttons[0];
        assert_eq!(hit_test(&l, true, (b.x + 5, b.y + 5)), Zone::Button(0));
        assert_eq!(hit_test(&l, true, (l.panel.x, l.panel.y)), Zone::Caption);
    }

    #[test]
    fn hit_multi_button_panel_indexes_in_order() {
        let l = compute_layout(r(100, 100, 300, 200), &spec_n(1.0, 4, 3), &[BIG_WA]);
        for (i, b) in l.buttons.iter().enumerate() {
            assert_eq!(hit_test(&l, true, (b.x + 1, b.y + 1)), Zone::Button(i));
        }
        // The hairline between two buttons is panel background.
        let seam = y2(&l.buttons[0]) as i32;
        assert_eq!(hit_test(&l, true, (l.buttons[0].x, seam)), Zone::Caption);
    }

    /// The painted surface, stated independently of `hit_test` and exactly as
    /// DESIGN §6 states the Windows window region:
    ///
    /// ```text
    /// (band − hole − every cutout) ∪ every handle rect ∪ panel
    /// ```
    ///
    /// Both painters produce this: AppKit clips the ring to `band` even-odd
    /// `hole`, then even-odd every cutout, and fills the handles and panel
    /// afterwards; GDI fills the ring `band − hole` with every cutout
    /// `ExcludeClipRect`ed away, then the handles and panel. So this one
    /// predicate is the contract all three implementations meet.
    fn painted(l: &FrameLayout, p: (i32, i32)) -> bool {
        if contains(&l.panel, p) || l.handles.iter().any(|h| contains(&h.rect, p)) {
            return true;
        }
        contains(&l.band, p)
            && !contains(&l.hole, p)
            && !l.handles.iter().any(|h| contains(&h.cutout, p))
    }

    /// Coordinates worth probing: every rect boundary in the layout, ±1, plus
    /// a coarse sweep of `outer`. Boundary±1 is what actually catches
    /// half-open/off-by-one errors; the sweep is there so a whole missing
    /// region cannot slip between two boundaries.
    fn probe_axes(l: &FrameLayout) -> (Vec<i32>, Vec<i32>) {
        let mut rects = vec![l.outer, l.band, l.white_band, l.hole, l.panel];
        rects.extend(l.buttons.iter().copied());
        for h in &l.handles {
            rects.push(h.rect);
            rects.push(h.cutout);
        }
        let (mut xs, mut ys) = (Vec::new(), Vec::new());
        for rc in &rects {
            for d in -1..=1i64 {
                xs.push((rc.x as i64 + d) as i32);
                xs.push((x2(rc) + d) as i32);
                ys.push((rc.y as i64 + d) as i32);
                ys.push((y2(rc) + d) as i32);
            }
        }
        for i in 0..=16i64 {
            xs.push((l.outer.x as i64 + l.outer.w as i64 * i / 16) as i32);
            ys.push((l.outer.y as i64 + l.outer.h as i64 * i / 16) as i32);
        }
        xs.sort_unstable();
        xs.dedup();
        ys.sort_unstable();
        ys.dedup();
        (xs, ys)
    }

    /// **The hit surface is the painted surface** (DESIGN §3). Two directions,
    /// and the asymmetry between them is deliberate:
    ///
    /// 1. Anything `hit_test` claims (`Edge`/`Corner`/`Caption`/`Button`) is
    ///    painted. This is the direction Windows forces: the window region is
    ///    the painted set, and a claimed pixel outside it would be a click on
    ///    a window that does not extend there.
    /// 2. Anything painted OUTSIDE the hole is claimed. No painted pixel is
    ///    dead: if you can see it, you can grab it.
    ///
    /// The hole is excluded from (2) because a handle straddles the band's
    /// inner edge and so is painted `slack - 1` units into the hole, while §3
    /// step 3 deliberately falls through there — the application underneath
    /// outranks a resize target. That sliver is painted-but-click-through, and
    /// it is the only such place; the test below pins that it is non-empty
    /// rather than letting the exemption quietly cover nothing (or everything).
    #[test]
    fn hit_surface_is_the_painted_surface() {
        let regions = [
            r(0, 0, MIN_REGION, MIN_REGION),
            r(100, 100, 300, 200),
            r(-1800, 300, 640, 480),
            r(2000, 100, 1201, 801),
        ];
        for &region in &regions {
            for (scale, base) in SWEEP {
                for resizable in [true, false] {
                    let spec = spec_n(scale, base, 1);
                    let l = compute_layout(region, &spec, &[BIG_WA]);
                    let label = format!(
                        "region {region:?} scale {scale} border {base} resizable {resizable}"
                    );
                    let (xs, ys) = probe_axes(&l);
                    let mut sliver = 0u32;
                    for &y in &ys {
                        for &x in &xs {
                            let p = (x, y);
                            let claimed = hit_test(&l, resizable, p) != Zone::Outside;
                            let painted = painted(&l, p);
                            if claimed {
                                assert!(painted, "{label}: claimed but unpainted at {p:?}");
                            }
                            if painted && !contains(&l.hole, p) {
                                assert!(claimed, "{label}: painted but dead at {p:?}");
                            }
                            if painted && !claimed {
                                // The only exemption: a handle inside the hole.
                                assert!(
                                    contains(&l.hole, p)
                                        && l.handles.iter().any(|h| contains(&h.rect, p)),
                                    "{label}: unexplained dead paint at {p:?}"
                                );
                                sliver += 1;
                            }
                        }
                    }
                    // `slack = 1 + overhang_in`, so the sliver is empty only
                    // when overhang_in is 0 (the fat-`--border` case where the
                    // handle floors at the band thickness).
                    if spec.handle > spec.border.total() {
                        assert!(sliver > 0, "{label}: handles should reach into the hole");
                    }
                }
            }
        }
    }

    // ---- resize (behaviour unchanged) -------------------------------------

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
        for z in [Zone::Outside, Zone::Caption, Zone::Button(0)] {
            assert_eq!(resize_region(start, z, 50, 50), start);
        }
    }
}
