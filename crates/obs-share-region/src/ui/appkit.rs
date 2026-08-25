//! macOS AppKit implementation of the share-region UI (SHARE_REGION_PLAN §6.2,
//! spec "Window behavior"). Three windows: a titled *mirror* (the window
//! meeting apps pick; libobs paints into its contentView), an opaque
//! borderless *mask* pinned exactly over it at the back of the Z-order (so
//! the capture never photographs the mirror — no recursion), and a floating
//! hollow *frame* whose custom NSView draws the accent band + handle cluster
//! and drives move/resize.
//!
//! Threading: everything here runs on the main thread. `main.rs` created the
//! NSApplication (Accessory policy) before obs bootstrap; we only fetch the
//! shared instance and `run()` it. libobs renders the mirror from its own
//! graphics thread via the ObsDisplay swapchain, which does not contend with
//! the AppKit run loop.
//!
//! Retention / pointer validity: the process NEVER returns from `run` — every
//! exit path goes through `AppEvents::quit` → `obs_platform::exit_process`.
//! All windows (and thus the mirror's contentView, whose raw pointer we hand
//! to `AppEvents::mirror_ready` for `obs_display_create`) are retained in the
//! process-global `APP` cell below and deliberately never dropped, so that
//! pointer stays valid for the life of the process. Belt-and-braces we also
//! `setReleasedWhenClosed(false)` so closing the mirror cannot free it out
//! from under obs before `quit` runs.

use std::cell::RefCell;
use std::ffi::c_void;

use objc2::rc::Retained;
use objc2::runtime::{AnyObject, NSObject};
use objc2::{define_class, msg_send, sel, AllocAnyThread, MainThreadMarker, MainThreadOnly, Message};
use objc2_app_kit::{
    NSApplication, NSApplicationDidChangeScreenParametersNotification, NSBackingStoreType,
    NSBezierPath, NSColor, NSCursor, NSEvent, NSFloatingWindowLevel, NSLineCapStyle, NSScreen,
    NSTrackingArea, NSTrackingAreaOptions, NSView, NSWindingRule, NSWindow,
    NSWindowCollectionBehavior, NSWindowDidBecomeKeyNotification,
    NSWindowDidBecomeMainNotification, NSWindowDidMoveNotification, NSWindowOrderingMode,
    NSWindowStyleMask, NSWindowWillCloseNotification,
};
use objc2_foundation::{
    NSNotification, NSNotificationCenter, NSPoint, NSRect, NSSize, NSString, NSTimer,
};

use obs_platform::region::Rect;

use super::{AppEvents, UiConfig};
use crate::geometry::{self, Dir, FrameLayout, Zone};

/// How often the move-commit poller checks `NSEvent::pressedMouseButtons`.
/// `performWindowDragWithEvent:` gives no end-of-drag callback (spec), so a
/// caption drag arms this repeating timer and the tick that first observes
/// "left button up" performs the commit. 30 ms is imperceptible and cheap.
const MOVE_POLL_SECS: f64 = 0.03;

// ---------------------------------------------------------------------------
// Process-global state
// ---------------------------------------------------------------------------

struct App {
    mtm: MainThreadMarker,
    events: Box<dyn AppEvents>,
    cfg: UiConfig,
    /// Last *committed* region (capture space). Live drags derive from it and
    /// only overwrite it via `adopt` after `AppEvents::region_committed`.
    region: Rect,
    /// Layout for the frame window (present iff `cfg.show_frame`). During a
    /// resize drag this holds the live rubber-band layout so drawing,
    /// hit-testing and cursor feedback all agree with what is on screen.
    layout: Option<FrameLayout>,
    mirror: Retained<NSWindow>,
    mask: Retained<NSWindow>,
    frame: Option<Retained<NSWindow>>,
    view: Option<Retained<FrameView>>,
    controller: Retained<Controller>,
    /// A `performWindowDragWithEvent:` caption/handle drag is in flight.
    move_drag: bool,
    move_timer: Option<Retained<NSTimer>>,
    /// A hand-rolled edge/corner resize drag is in flight.
    resize: Option<ResizeDrag>,
    /// mouseDown landed on the close button; quit fires on mouseUp-inside.
    close_armed: bool,
    /// Last hover was over the hollow interior / outside (`Zone::Outside`):
    /// used to restore the arrow exactly once and then leave the cursor to
    /// whatever application is underneath (see `mouseMoved:`).
    hover_outside: bool,
}

struct ResizeDrag {
    /// Region at mouseDown; deltas are always applied to this, not to the
    /// intermediate live rects, so the drag never accumulates rounding.
    start: Rect,
    zone: Zone,
    /// mouseDown position, capture space.
    origin: (i32, i32),
}

/// The one `App` for the process. AppKit delivers every callback that touches
/// this on the main thread, and `run()` never returns, so this is a
/// main-thread singleton with process lifetime.
///
/// SAFETY (the `Sync` impl): no thread other than the main thread ever
/// touches the cell — every access site is either `run()` itself (asserted on
/// the main thread via `MainThreadMarker`) or an ObjC method of a
/// `MainThreadOnly` class.
struct MainThreadCell(RefCell<Option<App>>);
unsafe impl Sync for MainThreadCell {}
static APP: MainThreadCell = MainThreadCell(RefCell::new(None));

/// Mutable access for direct event handlers (mouse methods, timer ticks).
/// These are never re-entered, so a failed borrow is a programming error.
fn with_app<R>(f: impl FnOnce(&mut App) -> R) -> R {
    let mut guard = APP.0.borrow_mut();
    f(guard.as_mut().expect("APP initialized before NSApp.run()"))
}

/// Mutable access for *notification* handlers. NSNotificationCenter posts
/// synchronously, so a programmatic `setFrame:` inside one of our handlers
/// re-enters `frameDidMove:` while the cell is already borrowed; those echoes
/// are exactly the ones we want to ignore, so a failed borrow is a silent
/// no-op rather than a panic.
fn try_with_app(f: impl FnOnce(&mut App)) {
    if let Ok(mut guard) = APP.0.try_borrow_mut() {
        if let Some(app) = guard.as_mut() {
            f(app);
        }
    }
}

/// Read-only access for AppKit render/query callbacks (`drawRect:`,
/// `hitTest:`, `resetCursorRects`). Returns `None` before `run()` populates
/// the cell (e.g. a first display pass while the windows are being ordered)
/// or in the unlikely event of re-entry — both cases mean "draw/hit nothing".
fn read_app<R>(f: impl FnOnce(&App) -> R) -> Option<R> {
    let guard = APP.0.try_borrow().ok()?;
    guard.as_ref().map(f)
}

// ---------------------------------------------------------------------------
// Coordinate conversion (spec "Coordinate space")
// ---------------------------------------------------------------------------

/// Cocoa's global space and capture space (CG display coords, the space of
/// `--region` / `MonitorInfo` / geometry.rs) share units (points) and an
/// origin screen, but Cocoa's origin is the *bottom*-left of the primary
/// screen (`NSScreen.screens[0]`, whose Cocoa frame origin is (0,0)) with y
/// growing up, while capture space is top-left with y growing down. The two
/// are therefore a pure y-flip about the primary screen height:
///
///     cocoa_y = primary_h - (cg_y + h)
///
/// (a Cocoa rect's origin is its bottom-left corner, hence the `+ h`).
/// ALL conversion in this file funnels through the helpers below.
fn primary_screen_height(mtm: MainThreadMarker) -> f64 {
    NSScreen::screens(mtm)
        .firstObject()
        .map(|s| s.frame().size.height)
        .unwrap_or(0.0) // headless: degenerate but well-defined
}

fn capture_to_cocoa(mtm: MainThreadMarker, r: Rect) -> NSRect {
    let ph = primary_screen_height(mtm);
    NSRect::new(
        NSPoint::new(r.x as f64, ph - (r.y as f64 + r.h as f64)),
        NSSize::new(r.w as f64, r.h as f64),
    )
}

fn cocoa_to_capture(mtm: MainThreadMarker, r: NSRect) -> Rect {
    let ph = primary_screen_height(mtm);
    Rect {
        x: r.origin.x.round() as i32,
        y: (ph - (r.origin.y + r.size.height)).round() as i32,
        w: r.size.width.round().max(0.0) as u32,
        h: r.size.height.round().max(0.0) as u32,
    }
}

/// Point (not rect) flavor: `cg_y = primary_h - cocoa_y`.
fn cocoa_point_to_capture(mtm: MainThreadMarker, p: NSPoint) -> (i32, i32) {
    let ph = primary_screen_height(mtm);
    (p.x.round() as i32, (ph - p.y).round() as i32)
}

/// Work areas for the cluster placement scoring (spec `compute_layout`):
/// `visibleFrame` already excludes the menu bar and the Dock.
fn work_areas(mtm: MainThreadMarker) -> Vec<Rect> {
    NSScreen::screens(mtm)
        .iter()
        .map(|s| cocoa_to_capture(mtm, s.visibleFrame()))
        .collect()
}

// ---------------------------------------------------------------------------
// Controller: notification + timer target (exists even with --no-frame,
// because the mirror's close/activate notifications always need a receiver)
// ---------------------------------------------------------------------------

define_class!(
    // SAFETY: NSObject has no subclassing requirements; Controller has no
    // Drop impl and () ivars.
    #[unsafe(super(NSObject))]
    #[thread_kind = MainThreadOnly]
    #[ivars = ()]
    struct Controller;

    impl Controller {
        #[unsafe(method(mirrorWillClose:))]
        fn mirror_will_close(&self, _n: &NSNotification) {
            // Closing the mirror is one of the two quit gestures (the other
            // is the frame's X button). quit() never returns.
            with_app(|app| app.events.quit());
        }

        #[unsafe(method(mirrorDidActivate:))]
        fn mirror_did_activate(&self, _n: &NSNotification) {
            on_mirror_activated();
        }

        #[unsafe(method(frameDidMove:))]
        fn frame_did_move(&self, _n: &NSNotification) {
            on_frame_moved();
        }

        #[unsafe(method(screenParamsChanged:))]
        fn screen_params_changed(&self, _n: &NSNotification) {
            on_screen_params_changed();
        }

        #[unsafe(method(moveTick:))]
        fn move_tick(&self, _t: &NSTimer) {
            on_move_tick();
        }
    }
);

impl Controller {
    fn new(mtm: MainThreadMarker) -> Retained<Self> {
        let this = Self::alloc(mtm).set_ivars(());
        unsafe { msg_send![super(this), init] }
    }
}

/// The user selected the mirror in a window picker / clicked its Dock-less
/// taskbar presence and macOS raised it: push it straight back to the bottom
/// (mask at the very back, mirror inserted below the mask) so it never
/// actually shows. Window refs are cloned out first so the ordering calls run
/// outside the APP borrow (ordering can post further notifications).
fn on_mirror_activated() {
    let mut wins = None;
    try_with_app(|app| wins = Some((app.mask.clone(), app.mirror.clone())));
    if let Some((mask, mirror)) = wins {
        mask.orderBack(None);
        mirror.orderWindow_relativeTo(NSWindowOrderingMode::Below, mask.windowNumber());
    }
}

/// Live tick of a `performWindowDragWithEvent:` move: translate the frame
/// window's new origin back into a region rect (same size — a move never
/// resizes) and let the app follow cheaply; the mirror + mask windows follow
/// on screen too. Programmatic `setFrame:` echoes are filtered two ways:
/// `try_with_app` drops re-entrant posts, and `move_drag` gates posts that
/// arrive outside a caption drag (e.g. our own adopt() after a resize).
fn on_frame_moved() {
    let mut follow = None;
    try_with_app(|app| {
        if !app.move_drag {
            return;
        }
        let (Some(frame), Some(layout)) = (app.frame.clone(), app.layout.as_ref()) else {
            return;
        };
        let outer_now = cocoa_to_capture(app.mtm, frame.frame());
        let (dx, dy) = (outer_now.x - layout.outer.x, outer_now.y - layout.outer.y);
        if dx == 0 && dy == 0 {
            return;
        }
        let live = Rect { x: app.region.x + dx, y: app.region.y + dy, ..app.region };
        app.events.region_moved(live);
        follow = Some((app.mtm, app.mirror.clone(), app.mask.clone(), live));
    });
    if let Some((mtm, mirror, mask, live)) = follow {
        let frame_rect = mirror.frameRectForContentRect(capture_to_cocoa(mtm, live));
        mirror.setFrame_display(frame_rect, false);
        mask.setFrame_display(frame_rect, false);
    }
}

/// Commit poller for caption drags (see `MOVE_POLL_SECS`). The frame
/// window's *current* position is authoritative — the last didMove
/// notification can predate the actual release.
fn on_move_tick() {
    if NSEvent::pressedMouseButtons() & 1 != 0 {
        return; // left button still down: drag still running
    }
    let adoption = with_app(|app| {
        if !app.move_drag {
            return None; // stale tick racing the invalidate below
        }
        app.move_drag = false;
        if let Some(t) = app.move_timer.take() {
            t.invalidate();
        }
        let frame = app.frame.clone()?;
        let layout = app.layout.as_ref()?;
        let outer_now = cocoa_to_capture(app.mtm, frame.frame());
        let (dx, dy) = (outer_now.x - layout.outer.x, outer_now.y - layout.outer.y);
        let proposed = Rect { x: app.region.x + dx, y: app.region.y + dy, ..app.region };
        let committed = app.events.region_committed(proposed);
        Some(adopt(app, committed))
    });
    if let Some(a) = adoption {
        apply_adoption(a);
    }
}

/// Display configuration changed (resolution, monitor add/remove, Dock/menu
/// bar geometry — spec: frame geometry is recomputed "on every
/// move/resize/commit and display-config change", mirroring win32.rs's
/// WM_DISPLAYCHANGE). Two things go stale at once: the work areas (the
/// cluster may now hide under a relocated Dock) and `primary_screen_height`
/// (the capture↔Cocoa y-flip base — the windows' stale Cocoa frames no
/// longer sit on the capture region being shared). Re-adopting the committed
/// region recomputes both: `adopt` re-derives layout from fresh work areas
/// and `apply_adoption` re-places mirror/mask/frame through the fresh flip.
fn on_screen_params_changed() {
    let mut adoption = None;
    try_with_app(|app| {
        if app.move_drag || app.resize.is_some() {
            // Mid-drag: don't yank windows out from under the pointer; the
            // commit on release re-derives everything from fresh geometry.
            return;
        }
        adoption = Some(adopt(app, app.region));
    });
    if let Some(a) = adoption {
        apply_adoption(a);
    }
}

// ---------------------------------------------------------------------------
// Commit adoption
// ---------------------------------------------------------------------------

/// Window geometry to apply *after* the APP borrow is released: `setFrame:`
/// posts its notifications synchronously and can trigger a synchronous
/// display, so calling it while the cell is borrowed would make the echo
/// handlers / drawRect: hit a locked RefCell.
struct Adoption {
    mirror: (Retained<NSWindow>, NSRect),
    mask: (Retained<NSWindow>, NSRect),
    frame: Option<(Retained<NSWindow>, NSRect, Retained<FrameView>)>,
}

/// Adopt a committed region (spec: "After any commit, adopt the returned
/// region"): store it, recompute the frame layout (the cluster may jump
/// sides), and stage the mirror/mask/frame window moves.
fn adopt(app: &mut App, committed: Rect) -> Adoption {
    app.region = committed;
    let mtm = app.mtm;
    // The mask covers the mirror's whole window frame (title bar included) so
    // no sliver of the mirror is ever visible.
    let mirror_rect = app.mirror.frameRectForContentRect(capture_to_cocoa(mtm, committed));
    let frame_upd = app.frame.as_ref().map(|fw| {
        let layout = geometry::compute_layout(committed, app.cfg.border, &work_areas(mtm));
        let rect = capture_to_cocoa(mtm, layout.outer);
        app.layout = Some(layout);
        (fw.clone(), rect, app.view.clone().expect("frame window implies view"))
    });
    Adoption {
        mirror: (app.mirror.clone(), mirror_rect),
        mask: (app.mask.clone(), mirror_rect),
        frame: frame_upd,
    }
}

fn apply_adoption(a: Adoption) {
    a.mirror.0.setFrame_display(a.mirror.1, false);
    a.mask.0.setFrame_display(a.mask.1, false);
    if let Some((fw, rect, view)) = a.frame {
        fw.setFrame_display(rect, false);
        view.setNeedsDisplay(true);
        // Cursor rects are keyed to the (possibly relocated) zone geometry.
        fw.invalidateCursorRectsForView(&view);
    }
}

// ---------------------------------------------------------------------------
// FrameView: the hollow accent frame (draw, hit-test, drag, cursors)
// ---------------------------------------------------------------------------

define_class!(
    // SAFETY: NSView subclassing with only documented overrides; no Drop
    // impl, () ivars (all state lives in APP — the view has process lifetime
    // and there is exactly one).
    #[unsafe(super(NSView))]
    #[thread_kind = MainThreadOnly]
    #[ivars = ()]
    struct FrameView;

    impl FrameView {
        /// Flipped so view-local coordinates are top-left/y-down like capture
        /// space: local = capture − layout.outer.origin, a pure translation,
        /// which keeps every draw/hit/cursor computation in one space.
        #[unsafe(method(isFlipped))]
        fn is_flipped(&self) -> bool {
            true
        }

        /// The frame floats without ever becoming key; the first click must
        /// act (start a drag / press a button), not just get swallowed by
        /// window activation.
        #[unsafe(method(acceptsFirstMouse:))]
        fn accepts_first_mouse(&self, _event: Option<&NSEvent>) -> bool {
            true
        }

        /// THE click-through mechanism (plan §6.2): returning nil for the
        /// hollow interior (and anything else `Zone::Outside`) makes AppKit
        /// route the event — and cursor management — to whatever window is
        /// underneath, exactly as if the frame were not there.
        // method_id (not method): the return is a Retained object, which the
        // macro autoreleases per the selector's (plain) method family.
        #[unsafe(method_id(hitTest:))]
        fn hit_test(&self, point: NSPoint) -> Option<Retained<NSView>> {
            // `point` arrives in the superview's coordinate system.
            let sup = unsafe { self.superview() };
            let local = self.convertPoint_fromView(point, sup.as_deref());
            let zone = read_app(|app| {
                let l = app.layout.as_ref()?;
                let p = (l.outer.x + local.x.round() as i32, l.outer.y + local.y.round() as i32);
                Some(geometry::hit_test(l, app.cfg.resizable, p))
            })
            .flatten();
            match zone {
                None | Some(Zone::Outside) => None,
                Some(_) => Some(Retained::into_super(self.retain())),
            }
        }

        #[unsafe(method(drawRect:))]
        fn draw_rect(&self, _dirty: NSRect) {
            draw_frame();
        }

        /// Per-zone cursors for the key-window case; the tracking-area
        /// mouseMoved: path below covers the (usual) non-key case, where
        /// AppKit disables cursor rects.
        #[unsafe(method(resetCursorRects))]
        fn reset_cursor_rects(&self) {
            reset_cursor_rects_impl(self);
        }

        #[unsafe(method(mouseMoved:))]
        fn mouse_moved(&self, event: &NSEvent) {
            on_mouse_moved(self, event);
        }

        #[unsafe(method(mouseDown:))]
        fn mouse_down(&self, event: &NSEvent) {
            on_mouse_down(self, event);
        }

        #[unsafe(method(mouseDragged:))]
        fn mouse_dragged(&self, event: &NSEvent) {
            on_mouse_dragged(self, event);
        }

        #[unsafe(method(mouseUp:))]
        fn mouse_up(&self, event: &NSEvent) {
            on_mouse_up(self, event);
        }
    }
);

impl FrameView {
    fn new(mtm: MainThreadMarker) -> Retained<Self> {
        let this = Self::alloc(mtm).set_ivars(());
        let this: Retained<Self> = unsafe { msg_send![super(this), initWithFrame: NSRect::ZERO] };
        // ActiveAlways is the load-bearing option: the frame window normally
        // never becomes key, and cursor rects only work for key windows, so
        // hover cursor feedback comes from tracking-area mouseMoved events
        // instead. InVisibleRect keeps the area auto-sized to the view.
        let opts = NSTrackingAreaOptions::MouseMoved
            | NSTrackingAreaOptions::ActiveAlways
            | NSTrackingAreaOptions::InVisibleRect;
        let owner: &AnyObject = &this;
        let area = unsafe {
            NSTrackingArea::initWithRect_options_owner_userInfo(
                NSTrackingArea::alloc(),
                NSRect::ZERO,
                opts,
                Some(owner),
                None,
            )
        };
        this.addTrackingArea(&area);
        this
    }

    /// Pointer position of `event` in capture space. Deliberately via screen
    /// coordinates (`convertPointToScreen`) rather than view-local ones: the
    /// frame window itself moves/resizes mid-drag, so window-relative
    /// positions would measure a moving target.
    fn event_capture_point(&self, event: &NSEvent) -> Option<(i32, i32)> {
        let window = self.window()?;
        let mtm = MainThreadMarker::new()?;
        let p = window.convertPointToScreen(event.locationInWindow());
        Some(cocoa_point_to_capture(mtm, p))
    }
}

// The classic resize cursors are deprecated in favor of macOS 15's
// frameResizeCursor family, but that family is unavailable pre-15 and the
// deployment target here predates it; the deprecated singletons still render
// the correct arrows on every supported macOS.
#[allow(deprecated)]
fn cursor_for_zone(zone: Zone) -> Retained<NSCursor> {
    match zone {
        Zone::Edge(Dir::E) | Zone::Edge(Dir::W) => NSCursor::resizeLeftRightCursor(),
        Zone::Edge(Dir::N) | Zone::Edge(Dir::S) => NSCursor::resizeUpDownCursor(),
        // macOS has no public diagonal resize cursors; crosshair is the
        // closest stock shape (spec).
        Zone::Corner(_) => NSCursor::crosshairCursor(),
        Zone::MoveHandle => NSCursor::openHandCursor(),
        Zone::CloseButton | Zone::Caption | Zone::Outside => NSCursor::arrowCursor(),
    }
}

fn on_mouse_moved(view: &FrameView, event: &NSEvent) {
    let Some(p) = view.event_capture_point(event) else { return };
    let mut set: Option<Retained<NSCursor>> = None;
    try_with_app(|app| {
        let Some(l) = app.layout.as_ref() else { return };
        let zone = geometry::hit_test(l, app.cfg.resizable, p);
        if matches!(zone, Zone::Outside) {
            // Restore the arrow exactly once when leaving our band, then
            // stop touching the cursor: over the hollow interior the app
            // underneath owns it (hitTest: nil hands AppKit's cursor
            // management to that window) and we must not fight it.
            if !app.hover_outside {
                app.hover_outside = true;
                set = Some(NSCursor::arrowCursor());
            }
        } else {
            app.hover_outside = false;
            set = Some(cursor_for_zone(zone));
        }
    });
    if let Some(c) = set {
        c.set();
    }
}

fn on_mouse_down(view: &FrameView, event: &NSEvent) {
    let Some(p) = view.event_capture_point(event) else { return };
    let start_window_drag = with_app(|app| {
        let Some(l) = app.layout.as_ref() else { return false };
        match geometry::hit_test(l, app.cfg.resizable, p) {
            Zone::CloseButton => {
                app.close_armed = true;
                false
            }
            // MoveHandle behaves exactly like the caption band for dragging
            // (it differs only in cursor). hit_test has already demoted
            // Edge/Corner to Caption when !resizable.
            Zone::Caption | Zone::MoveHandle => {
                app.move_drag = true;
                let target: &AnyObject = &app.controller;
                let timer = unsafe {
                    NSTimer::scheduledTimerWithTimeInterval_target_selector_userInfo_repeats(
                        MOVE_POLL_SECS,
                        target,
                        sel!(moveTick:),
                        None,
                        true,
                    )
                };
                app.move_timer = Some(timer);
                true
            }
            zone @ (Zone::Edge(_) | Zone::Corner(_)) => {
                app.resize = Some(ResizeDrag { start: app.region, zone, origin: p });
                false
            }
            Zone::Outside => false, // unreachable: hitTest: returned nil there
        }
    });
    if start_window_drag {
        // Outside the borrow: the native drag session may synchronously post
        // its first willMove/didMove notifications.
        if let Some(window) = view.window() {
            window.performWindowDragWithEvent(event);
        }
    }
}

/// Hand-rolled resize loop (macOS has no native one for borderless windows):
/// accumulate the drag delta from the mouseDown origin, run it through
/// `geometry::resize_region`, and rubber-band the FRAME window only. The
/// mirror/mask/scene are untouched until release — a resize implies
/// `obs_reset_video` on commit, far too expensive per mouse delta
/// (plan §6.3).
fn on_mouse_dragged(view: &FrameView, event: &NSEvent) {
    let Some(p) = view.event_capture_point(event) else { return };
    let update = with_app(|app| {
        let drag = app.resize.as_ref()?;
        let live =
            geometry::resize_region(drag.start, drag.zone, p.0 - drag.origin.0, p.1 - drag.origin.1);
        let layout = geometry::compute_layout(live, app.cfg.border, &work_areas(app.mtm));
        let rect = capture_to_cocoa(app.mtm, layout.outer);
        // Publish the live layout so drawRect:/hitTest: track the rubber-band.
        app.layout = Some(layout);
        Some((app.frame.clone()?, rect, app.view.clone()?))
    });
    if let Some((fw, rect, v)) = update {
        // display:true — synchronous redraw keeps the band glued to the
        // cursor; safe here because the APP borrow is already released.
        fw.setFrame_display(rect, true);
        v.setNeedsDisplay(true);
    }
}

fn on_mouse_up(view: &FrameView, event: &NSEvent) {
    let Some(p) = view.event_capture_point(event) else { return };
    let adoption = with_app(|app| {
        if app.close_armed {
            app.close_armed = false;
            // Standard button semantics: fire only when released inside.
            if let Some(l) = app.layout.as_ref() {
                if matches!(geometry::hit_test(l, app.cfg.resizable, p), Zone::CloseButton) {
                    app.events.quit(); // -> ! (exit_process)
                }
            }
        }
        let drag = app.resize.take()?;
        let live =
            geometry::resize_region(drag.start, drag.zone, p.0 - drag.origin.0, p.1 - drag.origin.1);
        let committed = app.events.region_committed(live);
        Some(adopt(app, committed))
    });
    if let Some(a) = adoption {
        apply_adoption(a);
    }
}

// ---------------------------------------------------------------------------
// Drawing
// ---------------------------------------------------------------------------

fn srgb(r: u8, g: u8, b: u8) -> Retained<NSColor> {
    NSColor::colorWithSRGBRed_green_blue_alpha(
        r as f64 / 255.0,
        g as f64 / 255.0,
        b as f64 / 255.0,
        1.0,
    )
}

/// Paint the accent band ring, the darker handle cluster, and the two white
/// glyphs (X, four-arrow move). Everything is derived from the FrameLayout in
/// capture space and translated by `outer.origin` into the (flipped) view.
fn draw_frame() {
    let Some((layout, accent)) =
        read_app(|app| app.layout.clone().map(|l| (l, app.cfg.accent))).flatten()
    else {
        return; // first display pass before APP is populated: draw nothing
    };
    let outer = layout.outer;
    let local = |r: Rect| {
        NSRect::new(
            NSPoint::new((r.x - outer.x) as f64, (r.y - outer.y) as f64),
            NSSize::new(r.w as f64, r.h as f64),
        )
    };

    // Band ring: band minus hole via one even-odd path (the window background
    // is clear, so only the ring itself gets pixels).
    srgb(accent.0, accent.1, accent.2).setFill();
    let ring = NSBezierPath::bezierPath();
    ring.appendBezierPathWithRect(local(layout.band));
    ring.appendBezierPathWithRect(local(layout.hole));
    ring.setWindingRule(NSWindingRule::EvenOdd);
    ring.fill();

    // Cluster: a darker shade of the accent so the buttons read as controls.
    let darker = |c: u8| (c as f64 * 0.55) as u8;
    srgb(darker(accent.0), darker(accent.1), darker(accent.2)).setFill();
    NSBezierPath::fillRect(local(layout.cluster));

    // Glyphs: simple white polylines, ~2 px, round caps (plan §6.1's GDI
    // equivalent — no icon resources).
    NSColor::whiteColor().setStroke();
    let glyphs = NSBezierPath::bezierPath();
    glyphs.setLineWidth(2.0);
    glyphs.setLineCapStyle(NSLineCapStyle::Round);

    // Close: an X inset into its button square.
    let c = local(layout.close_btn);
    let inset = 9.0;
    let (x0, y0) = (c.origin.x + inset, c.origin.y + inset);
    let (x1, y1) = (
        c.origin.x + c.size.width - inset,
        c.origin.y + c.size.height - inset,
    );
    glyphs.moveToPoint(NSPoint::new(x0, y0));
    glyphs.lineToPoint(NSPoint::new(x1, y1));
    glyphs.moveToPoint(NSPoint::new(x0, y1));
    glyphs.lineToPoint(NSPoint::new(x1, y0));

    // Move: a four-arrow cross centered in its button square.
    let m = local(layout.move_btn);
    let (cx, cy) = (
        m.origin.x + m.size.width / 2.0,
        m.origin.y + m.size.height / 2.0,
    );
    let arm = m.size.width.min(m.size.height) / 2.0 - 6.0;
    let head = 4.0;
    glyphs.moveToPoint(NSPoint::new(cx - arm, cy));
    glyphs.lineToPoint(NSPoint::new(cx + arm, cy));
    glyphs.moveToPoint(NSPoint::new(cx, cy - arm));
    glyphs.lineToPoint(NSPoint::new(cx, cy + arm));
    // Arrowheads: chevrons at the four line ends. (bx, by) points from the
    // tip back toward the center; the two wings are that vector rotated ±90°.
    for ((tx, ty), (bx, by)) in [
        ((cx + arm, cy), (-head, 0.0)), // E
        ((cx - arm, cy), (head, 0.0)),  // W
        ((cx, cy + arm), (0.0, -head)), // S (flipped view: +y is down)
        ((cx, cy - arm), (0.0, head)),  // N
    ] {
        glyphs.moveToPoint(NSPoint::new(tx + bx - by, ty + by + bx));
        glyphs.lineToPoint(NSPoint::new(tx, ty));
        glyphs.lineToPoint(NSPoint::new(tx + bx + by, ty + by - bx));
    }
    glyphs.stroke();
}

// ---------------------------------------------------------------------------
// Cursor rects (key-window path)
// ---------------------------------------------------------------------------

#[allow(deprecated)] // resize cursors: see cursor_for_zone
fn reset_cursor_rects_impl(view: &FrameView) {
    let Some((layout, resizable)) =
        read_app(|app| app.layout.clone().map(|l| (l, app.cfg.resizable))).flatten()
    else {
        return;
    };
    let outer = layout.outer;
    let local = |x: i64, y: i64, w: i64, h: i64| {
        NSRect::new(
            NSPoint::new((x - outer.x as i64) as f64, (y - outer.y as i64) as f64),
            NSSize::new(w.max(0) as f64, h.max(0) as f64),
        )
    };
    let add = |r: NSRect, c: &NSCursor| {
        if r.size.width > 0.0 && r.size.height > 0.0 {
            view.addCursorRect_cursor(r, c);
        }
    };

    let move_btn = layout.move_btn;
    add(
        local(move_btn.x as i64, move_btn.y as i64, move_btn.w as i64, move_btn.h as i64),
        &NSCursor::openHandCursor(),
    );
    let close_btn = layout.close_btn;
    add(
        local(close_btn.x as i64, close_btn.y as i64, close_btn.w as i64, close_btn.h as i64),
        &NSCursor::arrowCursor(),
    );

    if resizable {
        // Mirrors hit_test's band subdivision: corner squares of side
        // max(CORNER_GRAB, band thickness), edge strips between them. Only
        // the cursor shape depends on this — behavior stays with hit_test —
        // so the small duplication is acceptable.
        let (bx, by) = (layout.band.x as i64, layout.band.y as i64);
        let (bw, bh) = (layout.band.w as i64, layout.band.h as i64);
        // Ring thickness t = band inflation (1+border) − hole inflation (1)
        // = border; hit_test's corner grab is max(CORNER_GRAB, 1+border) =
        // max(CORNER_GRAB, t+1), NOT max(CORNER_GRAB, t) — using t here
        // would disagree with the hit zones by 1 px whenever border >= 16.
        let t = (layout.hole.y as i64 - by).max(0);
        let s = (geometry::CORNER_GRAB as i64).max(t + 1);
        let cross = NSCursor::crosshairCursor();
        add(local(bx, by, s, s), &cross); // NW
        add(local(bx + bw - s, by, s, s), &cross); // NE
        add(local(bx, by + bh - s, s, s), &cross); // SW
        add(local(bx + bw - s, by + bh - s, s, s), &cross); // SE
        let ud = NSCursor::resizeUpDownCursor();
        add(local(bx + s, by, bw - 2 * s, t), &ud); // N
        add(local(bx + s, by + bh - t, bw - 2 * s, t), &ud); // S
        let lr = NSCursor::resizeLeftRightCursor();
        add(local(bx, by + s, t, bh - 2 * s), &lr); // W
        add(local(bx + bw - t, by + s, t, bh - 2 * s), &lr); // E
    }
}

// ---------------------------------------------------------------------------
// run()
// ---------------------------------------------------------------------------

pub fn run(region: Rect, cfg: UiConfig, mut events: Box<dyn AppEvents>) -> ! {
    // main.rs already created the shared NSApplication (Accessory policy)
    // before obs bootstrap; we only fetch it and run it.
    let mtm = MainThreadMarker::new()
        .expect("ui::run must be called on the main thread (AppKit requirement)");
    let nsapp = NSApplication::sharedApplication(mtm);

    let controller = Controller::new(mtm);

    // --- Mirror: the window meeting apps pick. Titled + closable only: not
    // miniaturizable (a minimized mirror stops presenting) and not resizable
    // (its client size is dictated by the region, never by the user).
    let style = NSWindowStyleMask::Titled | NSWindowStyleMask::Closable;
    let mirror = unsafe {
        NSWindow::initWithContentRect_styleMask_backing_defer(
            NSWindow::alloc(mtm),
            capture_to_cocoa(mtm, region), // content rect = region, in points
            style,
            NSBackingStoreType::Buffered,
            false,
        )
    };
    // We keep every window alive in APP for the life of the process; letting
    // AppKit also release-on-close would double-free the mirror and, worse,
    // free the contentView obs is rendering into.
    unsafe { mirror.setReleasedWhenClosed(false) };
    mirror.setTitle(&NSString::from_str(&cfg.title));

    // --- Mask: opaque, borderless, exactly covering the mirror's window
    // frame (title bar included). Borderless windows can never become
    // key/main, which is exactly what we want.
    let mask = unsafe {
        NSWindow::initWithContentRect_styleMask_backing_defer(
            NSWindow::alloc(mtm),
            mirror.frame(),
            NSWindowStyleMask::Borderless,
            NSBackingStoreType::Buffered,
            false,
        )
    };
    unsafe { mask.setReleasedWhenClosed(false) };
    mask.setOpaque(true);
    // Solid dark grey, matching the Windows implementation's 0x202020 (plan
    // §1: any opaque color works).
    mask.setBackgroundColor(Some(&srgb(0x20, 0x20, 0x20)));
    mask.setHasShadow(false);

    // --- Frame (optional): borderless, transparent, floating, following the
    // user across Spaces and over fullscreen apps.
    let (frame_win, view, layout) = if cfg.show_frame {
        let layout = geometry::compute_layout(region, cfg.border, &work_areas(mtm));
        let fw = unsafe {
            NSWindow::initWithContentRect_styleMask_backing_defer(
                NSWindow::alloc(mtm),
                capture_to_cocoa(mtm, layout.outer),
                NSWindowStyleMask::Borderless,
                NSBackingStoreType::Buffered,
                false,
            )
        };
        unsafe { fw.setReleasedWhenClosed(false) };
        fw.setOpaque(false);
        fw.setBackgroundColor(Some(&NSColor::clearColor()));
        fw.setHasShadow(false);
        fw.setLevel(NSFloatingWindowLevel);
        fw.setCollectionBehavior(
            NSWindowCollectionBehavior::CanJoinAllSpaces
                | NSWindowCollectionBehavior::FullScreenAuxiliary,
        );
        fw.setAcceptsMouseMovedEvents(true);
        let v = FrameView::new(mtm);
        fw.setContentView(Some(&v));
        (Some(fw), Some(v), Some(layout))
    } else {
        (None, None, None)
    };

    // --- Notifications. Registered before any window is ordered on screen;
    // the handlers no-op via try_with_app until APP is populated below.
    let center = NSNotificationCenter::defaultCenter();
    let observer: &AnyObject = &controller;
    let mirror_obj: &AnyObject = &mirror;
    unsafe {
        center.addObserver_selector_name_object(
            observer,
            sel!(mirrorWillClose:),
            Some(NSWindowWillCloseNotification),
            Some(mirror_obj),
        );
        // Both notifications: which one fires first depends on how the
        // mirror got raised (picker, Cmd-Tab, click), so re-assert on either.
        center.addObserver_selector_name_object(
            observer,
            sel!(mirrorDidActivate:),
            Some(NSWindowDidBecomeKeyNotification),
            Some(mirror_obj),
        );
        center.addObserver_selector_name_object(
            observer,
            sel!(mirrorDidActivate:),
            Some(NSWindowDidBecomeMainNotification),
            Some(mirror_obj),
        );
        if let Some(fw) = &frame_win {
            let frame_obj: &AnyObject = fw;
            center.addObserver_selector_name_object(
                observer,
                sel!(frameDidMove:),
                Some(NSWindowDidMoveNotification),
                Some(frame_obj),
            );
        }
        // Display-config changes (win32.rs handles the equivalent
        // WM_DISPLAYCHANGE). Posted by the NSApplication, but observed with
        // object=None: the shared-app pointer is the same either way and nil
        // keeps this registration independent of activation policy quirks.
        center.addObserver_selector_name_object(
            observer,
            sel!(screenParamsChanged:),
            Some(NSApplicationDidChangeScreenParametersNotification),
            None,
        );
    }

    // --- Show mirror + mask at the bottom of the Z-order: mask at the very
    // back, mirror inserted directly below it (plan §1: the mask is what
    // keeps the mirror from photographing itself).
    mask.orderBack(None);
    mirror.orderWindow_relativeTo(NSWindowOrderingMode::Below, mask.windowNumber());

    // --- Hand the mirror's contentView to the app so it can create the
    // ObsDisplay. Must happen after the mirror is shown (the swapchain needs
    // a realized view) and strictly before NSApp.run(). The pointer stays
    // valid forever: the view is retained by the mirror window, which is
    // retained by APP, which is never dropped.
    let content = mirror.contentView().expect("titled NSWindow always has a contentView");
    let handle = Retained::as_ptr(&content) as *mut NSView as *mut c_void;
    events.mirror_ready(handle);

    *APP.0.borrow_mut() = Some(App {
        mtm,
        events,
        cfg,
        region,
        layout,
        mirror,
        mask,
        frame: frame_win.clone(),
        view: view.clone(),
        controller,
        move_drag: false,
        move_timer: None,
        resize: None,
        close_armed: false,
        hover_outside: true,
    });

    // Order the frame front only now: its first draw pass reads APP.
    if let Some(fw) = &frame_win {
        fw.orderFrontRegardless();
    }
    if let Some(v) = &view {
        v.setNeedsDisplay(true);
    }

    nsapp.run();
    // run() only returns if something stops the app (nothing here does — all
    // exit paths go through events.quit()); treat a stop as a clean quit and
    // keep the "never shut libobs down" invariant via exit_process.
    obs_platform::exit_process(0)
}
