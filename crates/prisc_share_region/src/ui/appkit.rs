//! macOS AppKit implementation of the share-region UI (see `ui/mod.rs` for the
//! platform-neutral contract). ONE window exists for the life of the process.
//! It opens as an ordinary titled dialog carrying the prompt ("Share this
//! window, then press OK"), and when the user accepts, that very same NSWindow
//! — never a replacement, because the share the meeting app has just started is
//! bound to this window's identity — sheds its title bar, takes the region's
//! size, and is parked so that exactly ONE POINT of it overlaps the corner of a
//! display, where libobs paints the mirrored region into its contentView.
//!
//! # Why one point, and not fully off screen
//!
//! `ui/mod.rs` describes the Windows design: park the mirror outside every
//! display's bounds, so the region's display capture cannot photograph it. That
//! does not work on macOS, and the failure is silent in exactly the wrong way.
//!
//! A window that intersects no display is still listed by ScreenCaptureKit —
//! `SCWindow.isOnScreen` is true and the frame is reported correctly, so the
//! meeting app's picker shows it and the user can select it — but *starting a
//! stream on it* fails with `SCStreamErrorDomain` -3811 ("failed to start
//! stream due to audio/video capture failure"). Measured on macOS 15 against
//! this very binary: the same window number captures fine while the prompt is
//! on screen and fails the instant it is parked fully off, and a bare borderless
//! window with no Metal layer at all fails the same way, so it is the placement
//! and not the swapchain. One point of overlap with any display is enough to
//! make the capture work again; zero is not.
//!
//! So the mirror keeps the smallest toehold on a display that ScreenCaptureKit
//! will accept, and hangs off a corner for the rest. Everything but that single
//! point is outside every display's bounds, so there is nothing for the region's
//! display capture to photograph and no infinite corridor, and nothing lands on
//! top of Clowd's border or toolbar.
//!
//! [`parked_region`] picks the corner: one that the window can hang off without
//! straying onto a *second* display, preferring the bottom corners, where the
//! surviving point is least likely to be in anyone's way. It is a real point on
//! a real screen, though — see that function for what that does and does not
//! cost.
//!
//! Z-order is a separate axis and is tidiness only: the parked window is
//! `orderBack:`ed so that its one point sits behind every other window. It
//! keeps the NORMAL window level while doing so, deliberately — see the
//! ordering call in [`begin_mirror_phase`] for why a lower level is worse than
//! useless here.
//!
//! Hiding the window by level ALONE — dropping it below the desktop wallpaper
//! and leaving it fully on screen — was tried and does not work: the compositor
//! still draws it over the wallpaper.
//!
//! There is deliberately no other UI here at all: no border, no handles, no
//! buttons. The Clowd shell that spawns this process draws the border around
//! the live region and the floating controls itself (Clowd.Ui/Video/
//! BorderWindow and FloatingToolbarWindow), and anything this process put on
//! screen would land on top of Clowd's own chrome.
//!
//! Threading: everything here runs on the main thread. `main.rs` created the
//! NSApplication (Accessory policy) before the obs bootstrap; we only fetch the
//! shared instance and `run()` it. libobs renders the mirror from its own
//! graphics thread via the ObsDisplay swapchain, which does not contend with
//! the AppKit run loop. Commands parsed on the stdin thread reach this thread
//! through `ui::post_command` and are drained by the repeating timer below.
//!
//! Retention / pointer validity: the process NEVER returns from `run` — every
//! exit path goes through `AppEvents::quit` → `obs_platform::exit_process`. The
//! window (and thus its contentView, whose raw pointer we hand to
//! `AppEvents::mirror_ready` for `obs_display_create`) is retained in the
//! process-global `APP` cell below and deliberately never dropped, so that
//! pointer stays valid for the life of the process. Belt-and-braces we also
//! `setReleasedWhenClosed(false)` so closing the window cannot free it out from
//! under obs before `quit` runs.

use std::cell::RefCell;
use std::cmp;
use std::ffi::c_void;

use objc2::rc::Retained;
use objc2::runtime::{AnyObject, NSObject};
use objc2::{define_class, msg_send, sel, MainThreadMarker, MainThreadOnly};
use objc2_app_kit::{
    NSApplication, NSApplicationDidChangeScreenParametersNotification, NSBackingStoreType,
    NSBezelStyle, NSButton, NSButtonType, NSColor, NSEvent, NSFont, NSLineBreakMode, NSScreen,
    NSTextField, NSView, NSWindow, NSWindowCollectionBehavior, NSWindowStyleMask,
    NSWindowWillCloseNotification,
};
use objc2_foundation::{
    NSNotification, NSNotificationCenter, NSPoint, NSRect, NSRunLoop, NSRunLoopCommonModes, NSSize,
    NSString, NSTimer,
};

use obs_platform::region::Rect;

use super::{AppEvents, UiConfig};
use crate::commands::Command;

/// Content size of the prompt window, in points. Comfortably bigger than the
/// message needs: this window's whole job in that phase is to be easy to spot
/// in a meeting app's share picker and easy to click, not to be compact. It is
/// nothing to do with the region — the region's size only arrives on the window
/// when the prompt is accepted. The same 460x188 as the Win32 prompt
/// (`win32.rs`'s `PROMPT_CLIENT_W`/`_H`), so the two platforms present the same
/// dialog at the same proportions.
const PROMPT_SIZE: NSSize = NSSize::new(460.0, 188.0);

/// Prompt phase (ui/mod.rs "PROMPT PHASE"): what the window's content view
/// shows before it becomes the share surface. Heading + supporting line rather
/// than one sentence, because the two say different things — what this window
/// is, and what the user has to do with it. Same wording as win32.rs.
const PROMPT_HEADING: &str = "Share this window";
const PROMPT_SUBTITLE: &str = "Pick this window in your meeting app's share picker, then press OK.";
const PROMPT_OK: &str = "OK";

/// How often the controller drains `ui::take_commands`.
///
/// macOS has no cheap equivalent of Win32's `PostMessage` for nudging the
/// AppKit run loop from an arbitrary thread (the options are a CFRunLoopSource
/// wired up at startup or `performSelectorOnMainThread:`, both of which mean
/// handing an ObjC object to the stdin thread), so [`wake`] is a no-op and this
/// poll is what actually gets commands onto the UI thread. 30 ms is
/// imperceptible for the two things the shell sends — a region change while the
/// user drags Clowd's border, and quit — and costs one no-op timer callback per
/// frame-ish, which is nothing next to the mirror itself.
const COMMAND_POLL_SECS: f64 = 0.03;

/// How much of the parked window stays on a display, in capture units (points).
///
/// One, because ScreenCaptureKit demands a non-empty intersection with some
/// display and this is the smallest one there is (see the module docs). Every
/// other point of the window is outside every display, which is what keeps the
/// mirror out of the region's display capture.
const TOEHOLD: i32 = 1;

// ---------------------------------------------------------------------------
// Process-global state
// ---------------------------------------------------------------------------

struct App {
    mtm: MainThreadMarker,
    events: Box<dyn AppEvents>,
    /// The region actually being mirrored — the last rect that
    /// `AppEvents::set_region` accepted, not the last one that was asked for.
    /// The shell is free to send `move` while the prompt is still up (Clowd
    /// repositions its border before the user has pressed anything), so this
    /// is also what the mirror phase sizes the window from, rather than the
    /// `--region` value `run` was given.
    region: Rect,
    /// The one window: prompt dialog first, parked mirror afterwards.
    window: Retained<NSWindow>,
    /// False while the prompt is up, true from the moment the window has been
    /// restyled, resized and parked. It gates the window geometry updates: a
    /// `move` arriving during the prompt phase must re-plan obs and be acked,
    /// but must NOT resize the dialog the user is currently being asked to
    /// find and pick.
    mirroring: bool,
    /// The prompt phase's controls (label + OK button). Drained when the user
    /// accepts, which is also the re-entrancy guard: a second click on OK finds
    /// this empty and does nothing.
    prompt_controls: Vec<Retained<NSView>>,
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

/// Mutable access for direct event handlers (the OK action, timer ticks).
/// These are never re-entered, so a failed borrow is a programming error.
fn with_app<R>(f: impl FnOnce(&mut App) -> R) -> R {
    let mut guard = APP.0.borrow_mut();
    f(guard.as_mut().expect("APP initialized before NSApp.run()"))
}

/// Mutable access for *notification* handlers, and for the staged steps of
/// [`begin_mirror_phase`]. NSNotificationCenter posts synchronously, so a
/// programmatic `setStyleMask:`/`setFrame:` inside one of our handlers can
/// re-enter another handler while the cell is already borrowed; those echoes
/// are exactly the ones we want to ignore, so a failed borrow is a silent
/// no-op rather than a panic.
fn try_with_app(f: impl FnOnce(&mut App)) {
    if let Ok(mut guard) = APP.0.try_borrow_mut() {
        if let Some(app) = guard.as_mut() {
            f(app);
        }
    }
}

/// Read-only access, for the steps that only need to *look* at the model
/// between two AppKit calls. Returns `None` before `run()` populates the cell
/// or in the unlikely event of re-entry — both of which mean "there is nothing
/// to do yet".
fn read_app<R>(f: impl FnOnce(&App) -> R) -> Option<R> {
    let guard = APP.0.try_borrow().ok()?;
    guard.as_ref().map(f)
}

// ---------------------------------------------------------------------------
// Coordinate conversion (see ui/mod.rs "Coordinate space")
// ---------------------------------------------------------------------------

/// Cocoa's global space and capture space (CG display coords, the space of
/// `--region` and of the `move` command) share units (points) and an origin
/// screen, but Cocoa's origin is the *bottom*-left of the primary screen
/// (`NSScreen.screens[0]`, whose Cocoa frame origin is (0,0)) with y growing
/// up, while capture space is top-left with y growing down. The two are
/// therefore a pure y-flip about the primary screen height:
///
///     cocoa_y = primary_h - (cg_y + h)
///
/// (a Cocoa rect's origin is its bottom-left corner, hence the `+ h`).
/// ALL conversion in this file funnels through the helpers below, so the
/// parking arithmetic can be done in capture space — the space every other
/// module in the crate speaks — and flipped exactly once, at the window.
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

/// Which corner of a display the parked window hangs off, i.e. which single
/// corner point of the display the window keeps covered.
///
/// Named in capture space, so "bottom" is the larger y — the same way a user
/// would describe the screen.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Corner {
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

/// Corner preference order.
///
/// Bottom corners first because the one point that stays visible is then at the
/// bottom of the screen, out of the way of the menu bar and of most window
/// chrome, and — on the usual bottom-anchored Dock — likely to be behind the
/// Dock anyway. Right before left on each row for no stronger reason than that
/// the bottom-right of the primary display is the emptiest corner of a typical
/// desktop.
const CORNER_ORDER: [Corner; 4] = [
    Corner::BottomRight,
    Corner::BottomLeft,
    Corner::TopRight,
    Corner::TopLeft,
];

/// Top-left origin of a `w` by `h` window hung off `corner` of display `m`,
/// overlapping it by exactly [`TOEHOLD`] on both axes.
///
/// Each case pins the window's own opposite corner onto the display's corner
/// point and lets the rest hang outward. Saturating throughout: a display at
/// the far edge of i32 plus a large region must clamp rather than wrap into the
/// middle of the desktop, which is the one arithmetic slip here that would put
/// live mirrored content in the middle of a screen.
fn corner_origin(corner: Corner, m: Rect, w: u32, h: u32) -> (i32, i32) {
    let right = m.x.saturating_add(m.w as i32);
    let bottom = m.y.saturating_add(m.h as i32);
    // The window's leading edge when it hangs off a LEFT/TOP corner: its far
    // edge lands TOEHOLD past the display's near edge.
    let hang_left = m.x.saturating_add(TOEHOLD).saturating_sub(w as i32);
    let hang_up = m.y.saturating_add(TOEHOLD).saturating_sub(h as i32);
    // ...and when it hangs off a RIGHT/BOTTOM corner: its near edge starts
    // TOEHOLD short of the display's far edge.
    let hang_right = right.saturating_sub(TOEHOLD);
    let hang_down = bottom.saturating_sub(TOEHOLD);

    match corner {
        Corner::BottomRight => (hang_right, hang_down),
        Corner::BottomLeft => (hang_left, hang_down),
        Corner::TopRight => (hang_right, hang_up),
        Corner::TopLeft => (hang_left, hang_up),
    }
}

/// Capture-space rectangle overlap, exclusive on the far edges (two displays
/// laid edge to edge do not overlap). i64 throughout: virtual-desktop
/// coordinates are signed and a span can leave i32.
fn rects_overlap(a: Rect, b: Rect) -> bool {
    let (ax2, ay2) = (a.x as i64 + a.w as i64, a.y as i64 + a.h as i64);
    let (bx2, by2) = (b.x as i64 + b.w as i64, b.y as i64 + b.h as i64);
    (a.x as i64) < bx2 && (b.x as i64) < ax2 && (a.y as i64) < by2 && (b.y as i64) < ay2
}

/// Where the mirror lives once the prompt is accepted: `region`'s size, hung
/// off a display corner so that exactly one point of it is on a display and
/// everything else is outside every display's bounds.
///
/// Choosing the corner is the whole job, because a corner is only usable if the
/// window can hang off it into EMPTY SPACE. On a two-monitor desktop the inner
/// corners are not empty at all — hanging off the right edge of the left
/// monitor drops the mirror straight onto the right monitor, fully visible and
/// squarely inside the other display's capture. So every (corner, display) pair
/// is tried in [`CORNER_ORDER`] and the first one whose window touches no OTHER
/// display wins.
///
/// If no pair is clean — a small desktop fully enclosed by a large region, say,
/// or displays arranged in a ring — the pair that spills onto the fewest other
/// displays is used. That is a deliberate ordering of harms: the toehold is
/// what keeps the share alive at all, so it is never given up, while a spill is
/// a visible mirror on some other screen. Ties keep [`CORNER_ORDER`], so the
/// choice stays deterministic and a `move` cannot make the window wander
/// between equally-bad corners.
///
/// What this costs, and it is not nothing: the surviving point IS on a real
/// screen. The user can see it, and if their region happens to include that
/// exact corner point the mirror will photograph it — a one-point-square
/// infinite corridor in the very corner of the shared image. Bottom corners are
/// preferred partly to make that as unlikely and as unobtrusive as possible.
///
/// Recomputed on every placement rather than cached, because displays are
/// hot-pluggable: the display this corner belongs to can be unplugged, and a
/// stale origin then points into empty space — the one state in which the
/// capture stops working entirely.
fn parked_region(mtm: MainThreadMarker, region: Rect) -> Rect {
    // `frame`, not `visibleFrame`: the menu bar and the Dock are still screen,
    // and a window under either is still on a display the capture can see. It
    // is the display's real bounds that ScreenCaptureKit cares about.
    let screens: Vec<Rect> = NSScreen::screens(mtm)
        .iter()
        .map(|s| cocoa_to_capture(mtm, s.frame()))
        .collect();
    choose_parked_rect(&screens, region)
}

/// The corner search itself, split out from [`parked_region`] so it can be
/// tested against arbitrary display layouts — fetching the real ones needs a
/// main thread and a window server, and the layouts worth testing (three in a
/// row, negative origins, a display boxed in below) are not ones a test machine
/// has.
///
/// Only `region`'s SIZE is read; the parked window's position has nothing to do
/// with where the mirrored region is.
fn choose_parked_rect(screens: &[Rect], region: Rect) -> Rect {
    // No screens at all (headless CI, every display asleep): nothing can be
    // captured in that state anyway, and the origin is still well defined.
    if screens.is_empty() {
        return Rect {
            x: 0,
            y: 0,
            w: region.w,
            h: region.h,
        };
    }

    let mut best: Option<(usize, Rect)> = None;
    for corner in CORNER_ORDER {
        for (i, m) in screens.iter().enumerate() {
            let (x, y) = corner_origin(corner, *m, region.w, region.h);
            let candidate = Rect {
                x,
                y,
                w: region.w,
                h: region.h,
            };
            let spills = screens
                .iter()
                .enumerate()
                .filter(|(j, other)| *j != i && rects_overlap(candidate, **other))
                .count();
            if spills == 0 {
                return candidate;
            }
            // Strictly less-than, so the first candidate at a given score wins
            // and CORNER_ORDER breaks every tie.
            if best.is_none_or(|(seen, _)| spills < seen) {
                best = Some((spills, candidate));
            }
        }
    }
    // `best` is populated by the first iteration above; the unwrap_or is for
    // the compiler, not for a reachable state.
    best.map(|(_, r)| r).unwrap_or(Rect {
        x: 0,
        y: 0,
        w: region.w,
        h: region.h,
    })
}

/// Places the window at [`parked_region`] for `region`, sized to it.
///
/// The window is borderless by the time this is ever called, so its frame rect
/// and its content rect are the same thing; `frameRectForContentRect` is still
/// used because the size that has to end up correct is the CONTENT size — that
/// is the surface obs paints and the meeting app captures — and going through
/// the window's own conversion keeps that true even if the style ever changes.
fn park(window: &NSWindow, mtm: MainThreadMarker, region: Rect) {
    let content = capture_to_cocoa(mtm, parked_region(mtm, region));
    // display:false — one point of this window is on screen and it is behind
    // everything, and the swapchain presents on the graphics thread regardless.
    window.setFrame_display(window.frameRectForContentRect(content), false);
}

/// Places the PROMPT window: its own size, centred on the region and clamped
/// into the visible frame of the screen the region is on
/// ([`super::centre_prompt_on`]).
///
/// Only the position follows the region; the size is left exactly as created
/// (PROMPT_SIZE), because the prompt is a dialog to read and click, not a
/// preview — at the mirror's minimum region size a region-sized one would be
/// unreadable and near-unclickable. The region's SIZE reaches the window only
/// at [`begin_mirror_phase`].
///
/// `frame`, not the content rect: the window still has its title bar here, and
/// what has to end up on the screen is the whole window, chrome included.
///
/// Nothing constrains this afterwards — `constrainFrameRect:toScreen:` is
/// overridden to the identity (see [`ShareWindow`]) — so the clamp inside
/// `centre_prompt_on` is the only thing keeping the prompt on a screen. That is
/// deliberate: it clamps to the region's screen, where AppKit would have been
/// free to pick another.
fn place_prompt(window: &NSWindow, mtm: MainThreadMarker, region: Rect) {
    let frame = window.frame();
    let Some(bounds) = prompt_screen(mtm, region) else {
        // No screens at all (every display asleep, or a headless session).
        // There is nowhere to be sensible about; AppKit's own centring is as
        // good an answer as any and cannot fail.
        window.center();
        return;
    };
    let w = frame.size.width.round().max(0.0) as i32;
    let h = frame.size.height.round().max(0.0) as i32;
    let (x, y) = super::centre_prompt_on(region, bounds, w, h);
    let placed = capture_to_cocoa(
        mtm,
        Rect {
            x,
            y,
            w: w as u32,
            h: h as u32,
        },
    );
    // display:false — the window has not been ordered on screen yet at the one
    // call site, so there is nothing to redraw.
    window.setFrame_display(placed, false);
}

/// The visible frame — screen minus the menu bar and the Dock — of the screen
/// the prompt should open on, in capture space. `None` only when there are no
/// screens at all.
///
/// `visibleFrame`, unlike the `frame` [`parked_region`] uses, because these two
/// want opposite things from a screen: parking cares where the capture can see,
/// so the menu bar and the Dock are still screen; the prompt has to be read and
/// clicked, so the area either of them covers is not usable.
fn prompt_screen(mtm: MainThreadMarker, region: Rect) -> Option<Rect> {
    let screens: Vec<Rect> = NSScreen::screens(mtm)
        .iter()
        .map(|s| cocoa_to_capture(mtm, s.visibleFrame()))
        .collect();
    choose_prompt_screen(&screens, region)
}

/// The screen the region is most on, split out from [`prompt_screen`] so it can
/// be tested against arbitrary layouts (fetching the real ones needs a main
/// thread and a window server).
///
/// Most overlap wins, so a region straddling two displays takes the one it is
/// mostly on. Centre distance breaks the zero-overlap case — a region that
/// intersects no screen, which the app core rejects before the window exists
/// but which a screen hot-unplug between the two moments could still produce —
/// and ties keep the first screen, i.e. the primary, so the choice is
/// deterministic.
fn choose_prompt_screen(screens: &[Rect], region: Rect) -> Option<Rect> {
    screens
        .iter()
        .copied()
        // `min_by_key` returns the FIRST minimum, which is what makes ties
        // resolve to the primary; `max_by_key` would return the last.
        .min_by_key(|s| {
            (
                cmp::Reverse(overlap_area(*s, region)),
                centre_distance_sq(*s, region),
            )
        })
}

/// Area of the intersection of two capture-space rects, 0 when they miss.
/// i128: both spans can approach the full u32 range, and their product leaves
/// i64.
fn overlap_area(a: Rect, b: Rect) -> i128 {
    let (ax2, ay2) = (a.x as i64 + a.w as i64, a.y as i64 + a.h as i64);
    let (bx2, by2) = (b.x as i64 + b.w as i64, b.y as i64 + b.h as i64);
    let w = (ax2.min(bx2) - (a.x as i64).max(b.x as i64)).max(0) as i128;
    let h = (ay2.min(by2) - (a.y as i64).max(b.y as i64)).max(0) as i128;
    w * h
}

/// Squared distance between two rects' centres — squared because only the
/// ORDER is ever used and a square root would only cost precision. i128 for the
/// same reason as [`overlap_area`].
fn centre_distance_sq(a: Rect, b: Rect) -> i128 {
    let acx = a.x as i128 + a.w as i128 / 2;
    let acy = a.y as i128 + a.h as i128 / 2;
    let bcx = b.x as i128 + b.w as i128 / 2;
    let bcy = b.y as i128 + b.h as i128 / 2;
    (acx - bcx).pow(2) + (acy - bcy).pow(2)
}

// ---------------------------------------------------------------------------
// ShareWindow: an NSWindow that stays exactly where it is put
// ---------------------------------------------------------------------------

define_class!(
    // SAFETY: NSWindow subclassing with one documented override; no Drop impl
    // and () ivars.
    #[unsafe(super(NSWindow))]
    #[thread_kind = MainThreadOnly]
    #[ivars = ()]
    struct ShareWindow;

    impl ShareWindow {
        /// Returns the proposed rect verbatim, opting this window out of
        /// AppKit's automatic frame constraining.
        ///
        /// AppKit's default implementation shoves any window whose frame falls
        /// outside the VISIBLE frame of a screen — the screen minus the menu
        /// bar and the Dock — back inside it. This is the single most
        /// load-bearing line in the file, because [`parked_region`] is
        /// precisely a request to put all but one point of a window outside
        /// every display. Without the override AppKit would rewrite [`park`]'s
        /// `setFrame:` into a fully on-screen rect, and a region-sized window
        /// full of live mirrored content would appear in the middle of a
        /// display — where the region's capture photographs it, and the user
        /// gets the infinite corridor the parking exists to avoid. (Measured:
        /// `--region 756,0,756,491` on a 1512x982 display had every window
        /// dragged to y=33, the first pixel below the menu bar, instead of the
        /// modelled origin. AppKit does this to borderless windows too, not
        /// only titled ones.)
        ///
        /// The prompt phase relies on the same identity for the opposite
        /// reason: [`place_prompt`] clamps that window into the visible frame
        /// of the REGION's screen, and AppKit's constraining would have been
        /// free to move it to another one.
        #[unsafe(method(constrainFrameRect:toScreen:))]
        fn constrain_frame_rect(&self, rect: NSRect, _screen: Option<&NSScreen>) -> NSRect {
            rect
        }
    }
);

impl ShareWindow {
    /// `initWithContentRect:styleMask:backing:defer:` on the subclass. Returned
    /// as the superclass type: nothing past construction needs the subclass,
    /// and the override is installed on the instance's real class regardless.
    fn create(
        mtm: MainThreadMarker,
        content_rect: NSRect,
        style: NSWindowStyleMask,
    ) -> Retained<NSWindow> {
        let this = Self::alloc(mtm).set_ivars(());
        let win: Retained<Self> = unsafe {
            msg_send![
                super(this),
                initWithContentRect: content_rect,
                styleMask: style,
                backing: NSBackingStoreType::Buffered,
                defer: false,
            ]
        };
        // The window lives for the whole process (see the module docs); letting
        // AppKit also release-on-close would double-free it and free the
        // contentView obs renders into.
        unsafe { win.setReleasedWhenClosed(false) };
        Retained::into_super(win)
    }
}

// ---------------------------------------------------------------------------
// Controller: the OK action, the window's close notification, the command poll
// ---------------------------------------------------------------------------

define_class!(
    // SAFETY: NSObject has no subclassing requirements; Controller has no
    // Drop impl and () ivars.
    #[unsafe(super(NSObject))]
    #[thread_kind = MainThreadOnly]
    #[ivars = ()]
    struct Controller;

    impl Controller {
        /// OK in the prompt phase: the user has pointed their meeting app at
        /// this window, so it can now become the mirror.
        #[unsafe(method(promptAccepted:))]
        fn prompt_accepted(&self, _sender: Option<&AnyObject>) {
            begin_mirror_phase();
        }

        /// The window is closing. Reachable only during the prompt phase (the
        /// close button goes away with the title bar) and there it means the
        /// user declined, which is a clean quit. `quit()` never returns.
        #[unsafe(method(windowWillClose:))]
        fn window_will_close(&self, _n: &NSNotification) {
            with_app(|app| app.events.quit());
        }

        /// Command poll (see [`COMMAND_POLL_SECS`]).
        #[unsafe(method(drainCommands:))]
        fn drain_commands(&self, _t: &NSTimer) {
            on_command_tick();
        }

        /// The desktop changed shape: a display was attached, detached,
        /// rearranged or had its resolution changed.
        #[unsafe(method(screenParamsChanged:))]
        fn screen_params_changed(&self, _n: &NSNotification) {
            on_screen_params_changed();
        }
    }
);

impl Controller {
    fn new(mtm: MainThreadMarker) -> Retained<Self> {
        let this = Self::alloc(mtm).set_ivars(());
        unsafe { msg_send![super(this), init] }
    }
}

// ---------------------------------------------------------------------------
// Prompt phase
// ---------------------------------------------------------------------------

define_class!(
    // SAFETY: NSButton subclassing with one documented override; no Drop impl
    // and () ivars.
    #[unsafe(super(NSButton))]
    #[thread_kind = MainThreadOnly]
    #[ivars = ()]
    struct PromptButton;

    impl PromptButton {
        /// Fire on the click that reactivates the app, instead of swallowing
        /// it. This is the normal path here, not an edge case: accepting the
        /// prompt means going away to a meeting app, picking this window
        /// there, and coming back — so our app is inactive at the moment the
        /// user reaches for OK, and the default behaviour would make them
        /// click it twice.
        #[unsafe(method(acceptsFirstMouse:))]
        fn accepts_first_mouse(&self, _event: Option<&NSEvent>) -> bool {
            true
        }
    }
);

/// Builds the prompt phase's controls into the window's content view and
/// returns them (the caller stores them as the phase flag).
///
/// The shape is the Win32 prompt's (`win32.rs`, "prompt phase"): a heading and
/// a supporting line stacked top-left, and the action button alone in the
/// bottom-right corner. What is deliberately NOT copied is that dialog's
/// hand-painted surface — no Clowd palette, no footer strip, no owner-drawn
/// button. Windows had to paint those because its default dialog chrome is not
/// dark; here the system's own appearance is already the right answer, and
/// stock controls track the user's theme and accent colour for free.
///
/// Plain AppKit controls rather than anything hand-drawn for the same reason:
/// this is a window the user is about to hunt for in a picker, so it should
/// look like an ordinary dialog, and the button gets its focus ring,
/// Return-key activation and accessibility from AppKit.
fn install_prompt(
    mtm: MainThreadMarker,
    content: &NSView,
    controller: &Controller,
) -> Vec<Retained<NSView>> {
    /// Text column inset, and how far below the top of the content view the
    /// heading starts.
    const PAD_X: f64 = 24.0;
    const PAD_TOP: f64 = 26.0;
    /// Gap between the heading and the supporting line.
    const TEXT_GAP: f64 = 8.0;
    const HEADING_PT: f64 = 19.0;
    const SUBTITLE_PT: f64 = 13.0;
    /// The OK button's inset from the bottom-right corner, and the width it is
    /// grown to if its title alone would make it narrower.
    const BTN_MARGIN: f64 = 20.0;
    const BTN_MIN_W: f64 = 96.0;

    let bounds = content.bounds();
    let (w, h) = (bounds.size.width, bounds.size.height);

    let mut out: Vec<Retained<NSView>> = Vec::new();

    // -- OK, bottom right ---------------------------------------------------
    let target: &AnyObject = controller;
    let button = PromptButton::alloc(mtm).set_ivars(());
    let button: Retained<PromptButton> =
        unsafe { msg_send![super(button), initWithFrame: NSRect::ZERO] };
    button.setTitle(&NSString::from_str(PROMPT_OK));
    button.setBezelStyle(NSBezelStyle::Push);
    button.setButtonType(NSButtonType::MomentaryPushIn);
    unsafe {
        button.setTarget(Some(target));
        button.setAction(Some(sel!(promptAccepted:)));
    }
    // Return activates it, and AppKit paints it as the default button.
    button.setKeyEquivalent(&NSString::from_str("\r"));

    // The height comes from the button itself rather than from a constant: the
    // Push bezel is drawn at one natural height per control size, and forcing a
    // taller frame on it stretches the artwork instead of making a bigger
    // button. Only the width is ours, and only as a floor.
    button.sizeToFit();
    let natural = button.frame().size;
    let btn_w = natural.width.max(BTN_MIN_W).min(w);
    let btn_h = natural.height.min(h);
    button.setFrame(NSRect::new(
        NSPoint::new((w - BTN_MARGIN - btn_w).max(0.0), BTN_MARGIN.min(h - btn_h)),
        NSSize::new(btn_w, btn_h),
    ));
    content.addSubview(&button);
    // PromptButton : NSButton : NSControl : NSView
    out.push(Retained::into_super(Retained::into_super(
        Retained::into_super(button),
    )));

    // -- heading + supporting line, top left --------------------------------
    //
    // Both are wrapping labels measured at the column width, and each is placed
    // under whatever height it actually took, so a longer string (or a user
    // running a larger system font) pushes the next line down instead of
    // overlapping it.
    let text_w = (w - 2.0 * PAD_X).max(1.0);
    let mut add_label = |text: &str, font: Retained<NSFont>, color: Retained<NSColor>, top: f64| {
        let label = NSTextField::labelWithString(&NSString::from_str(text), mtm);
        label.setFont(Some(&font));
        label.setTextColor(Some(&color));
        label.setUsesSingleLineMode(false);
        label.setLineBreakMode(NSLineBreakMode::ByWordWrapping);
        label.setPreferredMaxLayoutWidth(text_w);
        // A finite bound rather than f64::MAX: this only has to be taller than
        // any wrap of two short strings, and infinities in AppKit geometry are
        // a reliable way to get NaN back out.
        let text_h = label
            .sizeThatFits(NSSize::new(text_w, 10_000.0))
            .height
            .ceil();
        label.setFrame(NSRect::new(
            NSPoint::new(PAD_X, top - text_h),
            NSSize::new(text_w, text_h),
        ));
        content.addSubview(&label);
        // NSTextField : NSControl : NSView
        out.push(Retained::into_super(Retained::into_super(label)));
        top - text_h
    };

    let after_heading = add_label(
        PROMPT_HEADING,
        NSFont::boldSystemFontOfSize(HEADING_PT),
        NSColor::labelColor(),
        h - PAD_TOP,
    );
    add_label(
        PROMPT_SUBTITLE,
        NSFont::systemFontOfSize(SUBTITLE_PT),
        NSColor::secondaryLabelColor(),
        after_heading - TEXT_GAP,
    );

    out
}

/// Prompt phase → mirror phase, on OK.
///
/// The window is REUSED, never recreated: the share the user just started in
/// their meeting app is bound to this window's identity (its window number),
/// and creating a second window — even an identical one — would turn that share
/// into a share of a window that no longer exists. Everything below therefore
/// mutates the window in place.
fn begin_mirror_phase() {
    // Stage 1, mutable: confirm we are actually still in the prompt phase and
    // take the controls out. Its own borrow because everything after it can
    // post AppKit notifications straight back into handlers that borrow APP.
    // Draining the controls here is also the double-click guard: a second OK
    // finds the vector empty and returns.
    let window = {
        let mut out = None;
        try_with_app(|app| {
            if app.prompt_controls.is_empty() {
                return; // already mirroring
            }
            for c in app.prompt_controls.drain(..) {
                c.removeFromSuperview();
            }
            out = Some(app.window.clone());
        });
        out
    };
    let Some(window) = window else { return };

    // Drop the title bar. A window share captures the whole window frame, so a
    // titled mirror would put its own caption in the shared output; the client
    // area is the only part that is the mirrored region. Borderless also means
    // frame rect == content rect, which is why this happens BEFORE the geometry
    // below and before `mirror_ready` hands out the content view.
    //
    // setStyleMask keeps the same NSWindow — and, load-bearing here, the same
    // window number — so the share stays bound to it.
    window.setStyleMask(NSWindowStyleMask::Borderless);

    // Stage 2, read-only: the region to size the window to. Read from the model
    // rather than captured at `run` time, because the shell may well have sent
    // `move` while the prompt was up.
    let Some((mtm, region)) = read_app(|app| (app.mtm, app.region)) else {
        return;
    };

    // Send it to the back, BEFORE the geometry below: between this call and
    // `park` the window is still at the prompt's centred origin, and a
    // region-sized window there in front of everything is a full-size flash of
    // live mirrored content over whatever the user is looking at — including,
    // if the region is where the window is, one frame of the infinite corridor.
    //
    // Ordering, NOT a lower window level, even though the point of both would
    // be "keep the one on-screen point out of the way". Any level below normal
    // drops the window out of every
    // `SCShareableContent.excludingDesktopWindows(true, …)` enumeration —
    // measured: identical window, listed at level 0 and absent at level -20 —
    // and that is how share pickers list windows. It costs nothing today (the
    // user picks this window during the prompt phase, while it is still a
    // normal front window, and the meeting app's stream stays bound to the
    // window id afterwards) but it would silently break any app that
    // re-enumerates mid-share, in exchange for hiding a single point.
    window.orderBack(None);

    // Follow the user across Spaces. A window belongs to the Space it was
    // opened on, and ScreenCaptureKit's on-screen window list only contains the
    // ACTIVE Space's windows — so without this, switching Spaces would drop the
    // mirror out of the capture and freeze the meeting app's share until the
    // user switched back. `Stationary` additionally keeps it from being dragged
    // around by the Spaces-switch animation.
    window.setCollectionBehavior(
        NSWindowCollectionBehavior::CanJoinAllSpaces | NSWindowCollectionBehavior::Stationary,
    );

    // Park it: the region's size, hung off a display corner with one point
    // still on that display (see [`parked_region`] and the
    // `constrainFrameRect:toScreen:` override, which is what makes such a
    // placement stick at all).
    //
    // Note what is NOT done here, and must never be: the window is not
    // miniaturized, not ordered out, and not moved off screen ENTIRELY. Any of
    // those would take it out of the window server's compositing or out of
    // ScreenCaptureKit's reach, and the meeting app's share would freeze on the
    // last frame it saw — or, for the fully-off-screen case, never start.
    park(&window, mtm, region);

    // Content view last, and only now: the swapchain obs creates is sized from
    // the view, so the view has to already be the region's size. The pointer
    // stays valid forever — the view is retained by the window, the window by
    // APP, and APP is never dropped.
    if let Some(content) = window.contentView() {
        let handle = Retained::as_ptr(&content) as *mut NSView as *mut c_void;
        with_app(|app| {
            // Set before the callback, so that anything the callback reaches
            // back into (and any command tick after it) sees a consistent
            // model: from here on this window is the mirror.
            app.mirroring = true;
            // `sharing_started` is NOT emitted here. main.rs emits it inside
            // its `mirror_ready`, which is the first instant the claim is true
            // (nothing is mirrored until the ObsDisplay exists) and is
            // exactly-once by the ui contract. Emitting it here as well would
            // put two `sharing_started` lines on the wire.
            app.events.mirror_ready(handle);
        });
    }
}

// ---------------------------------------------------------------------------
// Commands from stdin
// ---------------------------------------------------------------------------

/// Nudge the event loop into draining the command queue.
///
/// A deliberate no-op on macOS: see [`COMMAND_POLL_SECS`] for why this platform
/// polls instead. It exists because `ui::post_command` calls it unconditionally
/// and because the Windows side genuinely needs it; the stdin thread must not
/// have to know which platform it is running on.
pub(super) fn wake() {}

/// One tick of the command poll. Drains everything queued — a burst of `move`s
/// from a border drag is handled in one pass — and applies each in order on
/// this, the UI thread, which is the only thread allowed to run `AppEvents`.
fn on_command_tick() {
    for cmd in super::take_commands() {
        match cmd {
            // `quit`, or EOF on stdin (the parent died). Never returns.
            Command::Quit => with_app(|app| app.events.quit()),
            Command::Move(region) => apply_move(region),
            Command::Obscure(mode) => {
                with_app(|app| app.events.set_obscure(mode));
                // The ack carries the mode that was actually stored, read back
                // rather than echoed from the parsed command, so it can never
                // claim a mode the renderer is not in. Obscure state lives in
                // atomics read by the graphics thread, so there is nothing to
                // wait for and nothing that can partially apply.
                crate::status::emit_obscure(crate::obscure::mode());
            }
            // A line the parser refused. Answered here, in the position it
            // arrived in, rather than from the reader thread — see
            // `Command::Error` for why the ordering is load-bearing.
            Command::Error(reason) => crate::status::emit_command_error(&reason),
        }
    }
}

/// A `move` command: re-plan the capture, then make the window agree with it.
fn apply_move(request: Rect) {
    // `set_region` returns what was ACTUALLY applied — the request normalised to
    // the mirror's minimum and to an even size — and that, never the request, is
    // what both the window and the ack are built from, so the window and the
    // canvas cannot disagree and the shell's border cannot drift off the real
    // capture. `Err` means the request was refused and nothing changed.
    let (result, target) = with_app(|app| match app.events.set_region(request) {
        Ok(applied) => {
            app.region = applied;
            // During the prompt phase there is nothing to resize: the window is
            // still the dialog the user is being asked to find and pick, and
            // resizing it to the region (possibly tiny, possibly huge) would
            // sabotage exactly that. `begin_mirror_phase` reads `app.region`
            // when the time comes, so the move is not lost, only deferred.
            let target = app.mirroring.then(|| (app.mtm, app.window.clone()));
            (Ok(applied), target)
        }
        // Nothing was touched, so there is nothing to move the window to.
        Err(reason) => (Err(reason), None),
    });

    let applied = match result {
        Ok(applied) => applied,
        // Refused: no window change, and a `command_error` INSTEAD of the ack —
        // echoing the unchanged region as `region_changed` would be
        // indistinguishable from a successful move to that rect, and Clowd would
        // snap its border back with no explanation.
        Err(reason) => {
            crate::status::emit_command_error(&reason);
            return;
        }
    };

    // Outside the borrow: `setFrame:` posts its notifications synchronously.
    if let Some((mtm, window)) = target {
        park(&window, mtm, applied);
    }

    // Acked last, after the window has actually taken the new size — the move
    // is not fully applied until then, and this line is what Clowd resizes its
    // border to.
    crate::status::emit_region_changed(applied);
}

/// `NSApplicationDidChangeScreenParametersNotification`: a display was
/// attached, detached, rearranged or resized.
///
/// Re-parks the mirror, which is what makes [`parked_region`]'s "recomputed on
/// every placement" promise actually hold. Its only other callers are the OK
/// transition and a `move`, so without this the parked origin would sit stale
/// from one `move` to the next — and the corner it was chosen for is exactly
/// what a display change takes away. Both failure directions are real: the
/// display that corner belongs to can be unplugged, leaving the window over no
/// display at all, which on this platform is a window a meeting app cannot
/// capture (module docs) — the share goes dead and neither we nor the shell is
/// told; or a new display can arrive in the empty space the window was hanging
/// into, which puts a region-sized window of live mirrored content squarely on
/// a real screen. `constrainFrameRect:toScreen:` returning the rect verbatim
/// means AppKit will not pull it back on its own either.
///
/// The window LEVEL needs no maintenance here — it is a property of the window,
/// not of the screen layout, and survives any reconfiguration.
///
/// Does nothing during the prompt phase: that window is meant to be visible and
/// wherever AppKit centred it, where the user can find and click it. (win32.rs
/// handles the equivalent WM_DISPLAYCHANGE.)
fn on_screen_params_changed() {
    // Read out, then act outside the borrow: `setFrame:` posts notifications
    // synchronously and can re-enter a handler that borrows APP, exactly as
    // `apply_move` is careful about.
    let Some((mtm, window, region, mirroring)) =
        read_app(|app| (app.mtm, app.window.clone(), app.region, app.mirroring))
    else {
        return;
    };
    if mirroring {
        park(&window, mtm, region);
    }
}

// ---------------------------------------------------------------------------
// run()
// ---------------------------------------------------------------------------

pub fn run(region: Rect, cfg: UiConfig, events: Box<dyn AppEvents>) -> ! {
    // main.rs already created the shared NSApplication (Accessory policy)
    // before obs bootstrap; we only fetch it and run it.
    let mtm = MainThreadMarker::new()
        .expect("ui::run must be called on the main thread (AppKit requirement)");
    let nsapp = NSApplication::sharedApplication(mtm);

    // Retained by the scheduled timer below (which the run loop retains) and,
    // more simply, by this binding: `run` never returns, so nothing in this
    // frame is ever dropped. That matters because NSNotificationCenter does not
    // retain its observers.
    let controller = Controller::new(mtm);

    // The one window. Titled + closable for the prompt phase — the title is
    // what list-style share pickers show, and closing it is a legitimate way to
    // decline. Deliberately not miniaturizable (a minimized window stops being
    // composited, which would freeze the share the moment it started) and not
    // resizable (its size is dictated by the region, never by the user).
    // Prompt-sized rather than region-sized — a window sized like the final
    // mirror is awkward to pick, and at the mirror's minimum region size it
    // would be unreadable — but placed ON the region by `place_prompt` below,
    // because that is where the user is looking and, on a multi-display desktop,
    // the screen they expect to be asked on.
    let window = ShareWindow::create(
        mtm,
        NSRect::new(NSPoint::new(0.0, 0.0), PROMPT_SIZE),
        NSWindowStyleMask::Titled | NSWindowStyleMask::Closable,
    );
    window.setTitle(&NSString::from_str(&cfg.title));
    place_prompt(&window, mtm, region);

    // Registered before the window is ordered on screen. The handler borrows
    // APP unconditionally, which is safe even though APP is populated a few
    // lines further down: this notification can only be posted by a close, and
    // nothing — user or code — can close a window that has not been shown yet.
    let center = NSNotificationCenter::defaultCenter();
    let observer: &AnyObject = &controller;
    let window_obj: &AnyObject = &window;
    unsafe {
        center.addObserver_selector_name_object(
            observer,
            sel!(windowWillClose:),
            Some(NSWindowWillCloseNotification),
            Some(window_obj),
        );
        // Display-config changes, so the parked mirror can be moved back out of
        // a desktop that grew over it (see `on_screen_params_changed`). Posted
        // by the NSApplication, but observed with object=None: the shared-app
        // pointer is the same either way and nil keeps this registration
        // independent of activation-policy quirks.
        center.addObserver_selector_name_object(
            observer,
            sel!(screenParamsChanged:),
            Some(NSApplicationDidChangeScreenParametersNotification),
            None,
        );
    }

    let content = window
        .contentView()
        .expect("titled NSWindow always has a contentView");
    let prompt_controls = install_prompt(mtm, &content, &controller);

    // Key + front + activated: the whole point of this phase is that the window
    // is easy to see and click, both directly and in a share picker.
    window.makeKeyAndOrderFront(None);
    #[allow(deprecated)] // activate() is macOS 14+; this works everywhere.
    nsapp.activateIgnoringOtherApps(true);

    // Only now is `initialized` true in both halves of what it promises: libobs
    // is up (main.rs bootstrapped the mirror before calling us) AND the prompt
    // window exists, is showing and is activated. Emitting it any earlier would
    // race the shell's out-of-band reactions — looking the window up by title to
    // point the user at it, for instance — against a window that does not exist
    // yet. It cannot be emitted after `run` returns, because `run` never does.
    crate::status::emit_initialized();

    *APP.0.borrow_mut() = Some(App {
        mtm,
        events,
        region,
        window,
        mirroring: false,
        prompt_controls,
    });

    // Started after APP is populated. Strictly belt-and-braces — a timer can
    // only fire from inside the run loop below — but it costs nothing and means
    // the "APP exists before any callback" invariant holds by construction
    // rather than by scheduling luck. The run loop retains the timer, and the
    // timer retains the controller; neither is ever invalidated, because the
    // poll has to keep working right up to the moment the process exits.
    //
    // Built and added by hand rather than with the `scheduledTimer...`
    // convenience constructor, because that one installs the timer in
    // NSDefaultRunLoopMode ONLY. During the prompt phase the window is titled
    // with a live NSButton, so AppKit routinely pushes
    // NSEventTrackingRunLoopMode — a held mouse-down on OK, a title-bar drag —
    // and while that mode is current a default-mode timer does not fire at all.
    // Since `wake()` is a no-op on this platform, this timer is the ONLY thing
    // that ever calls `take_commands`, so every stdin command would stall for as
    // long as the user held the mouse down: including `quit`, and including the
    // EOF-synthesised `quit` that is this crate's orphan-safety mechanism. A
    // caller that writes `quit` and waits for the process to exit would time out
    // and kill the child instead. NSRunLoopCommonModes covers event tracking and
    // modal panels as well as the default mode.
    let target: &AnyObject = &controller;
    let _timer = unsafe {
        let timer = NSTimer::timerWithTimeInterval_target_selector_userInfo_repeats(
            COMMAND_POLL_SECS,
            target,
            sel!(drainCommands:),
            None,
            true,
        );
        NSRunLoop::currentRunLoop().addTimer_forMode(&timer, NSRunLoopCommonModes);
        timer
    };

    nsapp.run();
    // run() only returns if something stops the app (nothing here does — all
    // exit paths go through events.quit()); treat a stop as a clean quit and
    // keep the "never shut libobs down" invariant via exit_process.
    obs_platform::exit_process(0)
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

/// Corner selection is pure geometry, so it is tested without AppKit: the only
/// thing `parked_region` adds on top of [`choose_parked_rect`] is fetching the
/// screen rects, which needs a main thread and a window server.
#[cfg(test)]
mod tests {
    use super::*;

    fn rect(x: i32, y: i32, w: u32, h: u32) -> Rect {
        Rect { x, y, w, h }
    }

    /// The defining property, over a spread of layouts: the parked window
    /// touches the desktop in exactly one point, on exactly one display.
    #[test]
    fn parked_window_keeps_exactly_a_one_point_toehold() {
        let layouts: [&[Rect]; 5] = [
            // Single laptop display.
            &[rect(0, 0, 1512, 982)],
            // Laptop with an external monitor to the right, tops aligned.
            &[rect(0, 0, 1512, 982), rect(1512, 0, 1920, 1080)],
            // External to the LEFT and slightly higher (negative origins).
            &[rect(0, 0, 1512, 982), rect(-1920, -200, 1920, 1080)],
            // Stacked vertically.
            &[rect(0, 0, 1512, 982), rect(0, -1080, 1920, 1080)],
            // Three in a row.
            &[
                rect(-1920, 0, 1920, 1080),
                rect(0, 0, 1512, 982),
                rect(1512, 0, 1920, 1080),
            ],
        ];
        for screens in layouts {
            for size in [(64u32, 64u32), (756, 490), (1512, 982), (3840, 2160)] {
                let region = rect(0, 0, size.0, size.1);
                let parked = choose_parked_rect(screens, region);
                assert_eq!(
                    (parked.w, parked.h),
                    size,
                    "parking must not resize the window"
                );
                let total: i128 = screens.iter().map(|m| overlap_area(parked, *m)).sum();
                assert_eq!(
                    total, 1,
                    "layout {screens:?} region {size:?} -> {parked:?} covers {total} points"
                );
            }
        }
    }

    /// On a plain single display the preferred corner is the bottom-right one,
    /// with the window hanging down and to the right.
    #[test]
    fn single_display_prefers_the_bottom_right_corner() {
        let screens = [rect(0, 0, 1512, 982)];
        let parked = choose_parked_rect(&screens, rect(0, 0, 756, 490));
        assert_eq!(parked, rect(1511, 981, 756, 490));
    }

    /// The inner corners of a side-by-side pair are unusable — hanging off the
    /// left display's right edge drops the window onto the right display — so
    /// the chosen corner must be an outer one, and the window must not land on
    /// the second screen.
    #[test]
    fn side_by_side_displays_avoid_the_inner_corner() {
        let screens = [rect(0, 0, 1512, 982), rect(1512, 0, 1920, 1080)];
        let parked = choose_parked_rect(&screens, rect(0, 0, 756, 490));
        // Bottom-right of the RIGHT-hand display is the first clean candidate.
        assert_eq!(parked, rect(3431, 1079, 756, 490));
        assert_eq!(overlap_area(parked, screens[1]), 1);
        assert_eq!(overlap_area(parked, screens[0]), 0);
    }

    /// Each corner hangs the window the right way: the window's own opposite
    /// corner lands on the display's corner point, and the rest goes outward.
    /// This is what the search relies on, so it is checked per-corner rather
    /// than only through whichever corner a layout happens to select.
    #[test]
    fn every_corner_hangs_outward_from_its_display_corner() {
        let m = rect(100, 200, 1000, 800); // right = 1100, bottom = 1000
        let (w, h) = (500u32, 400u32);
        for (corner, expected) in [
            // Near edge starts one point short of the far edge; hangs +x/+y.
            (Corner::BottomRight, rect(1099, 999, w, h)),
            // Far edge lands one point past the near edge; hangs -x, +y.
            (Corner::BottomLeft, rect(-399, 999, w, h)),
            (Corner::TopRight, rect(1099, -199, w, h)),
            (Corner::TopLeft, rect(-399, -199, w, h)),
        ] {
            let (x, y) = corner_origin(corner, m, w, h);
            assert_eq!(rect(x, y, w, h), expected, "{corner:?}");
            // ...and every one of them keeps exactly the one-point toehold.
            assert_eq!(overlap_area(rect(x, y, w, h), m), 1, "{corner:?}");
        }
    }

    /// Bottom corners are preferred over top ones when both are clean.
    #[test]
    fn bottom_corners_win_over_top_corners() {
        // One display, nothing around it: all four corners are clean, so the
        // preference order alone decides, and it must land on a bottom one.
        let screens = [rect(0, 0, 1512, 982)];
        let parked = choose_parked_rect(&screens, rect(0, 0, 400, 300));
        assert!(
            parked.y > 0 && parked.y + parked.h as i32 > 982,
            "expected a bottom corner, got {parked:?}"
        );
    }

    /// A region so large that it swallows the whole desktop from every corner
    /// cannot avoid spilling. The toehold is still never given up — that is
    /// what keeps the share alive — and the choice stays deterministic.
    #[test]
    fn an_unavoidable_spill_still_keeps_the_toehold() {
        // Two small displays close together, and a region far larger than the
        // gap between them: every corner of either one hangs over the other.
        let screens = [rect(0, 0, 200, 200), rect(300, 0, 200, 200)];
        let parked = choose_parked_rect(&screens, rect(0, 0, 4000, 4000));
        let touched: Vec<i128> = screens.iter().map(|m| overlap_area(parked, *m)).collect();
        assert!(
            touched.contains(&1),
            "one display must still be touched by exactly one point: {touched:?}"
        );
        assert_eq!(choose_parked_rect(&screens, rect(0, 0, 4000, 4000)), parked);
    }

    /// No displays at all is not a crash, and the size still survives.
    #[test]
    fn no_displays_still_yields_a_defined_rect() {
        let parked = choose_parked_rect(&[], rect(7, 9, 640, 480));
        assert_eq!((parked.w, parked.h), (640, 480));
    }

    /// Parking is a pure function of (screens, size): re-parking the same
    /// region must not walk the window from one corner to another, or a burst
    /// of `move`s would make it wander.
    #[test]
    fn parking_is_deterministic() {
        let screens = [rect(0, 0, 1512, 982), rect(1512, 0, 1920, 1080)];
        let region = rect(100, 200, 756, 490);
        let first = choose_parked_rect(&screens, region);
        for _ in 0..8 {
            assert_eq!(choose_parked_rect(&screens, region), first);
        }
        // ...and the region's own origin is irrelevant to where it parks.
        assert_eq!(choose_parked_rect(&screens, rect(-50, 0, 756, 490)), first);
    }

    /// Which screen the prompt opens on, over the layouts that make the choice
    /// non-trivial.
    #[test]
    fn prompt_screen_follows_the_region() {
        // Laptop plus an external monitor to the right.
        let screens = [rect(0, 0, 1512, 982), rect(1512, 0, 1920, 1080)];

        // Squarely on the external one.
        assert_eq!(
            choose_prompt_screen(&screens, rect(2000, 300, 400, 300)),
            Some(screens[1])
        );
        // Squarely on the laptop.
        assert_eq!(
            choose_prompt_screen(&screens, rect(100, 100, 400, 300)),
            Some(screens[0])
        );
        // Straddling the seam, mostly on the external: most overlap wins.
        assert_eq!(
            choose_prompt_screen(&screens, rect(1412, 100, 400, 300)),
            Some(screens[1])
        );
        // On no screen at all (a display unplugged under us): nearest centre.
        assert_eq!(
            choose_prompt_screen(&screens, rect(4000, 300, 100, 100)),
            Some(screens[1])
        );
        // No screens: the caller falls back to AppKit's own centring.
        assert_eq!(choose_prompt_screen(&[], rect(0, 0, 100, 100)), None);
    }
}
