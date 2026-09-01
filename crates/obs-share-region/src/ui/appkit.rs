//! macOS AppKit implementation of the share-region UI (see `ui/mod.rs` for the
//! platform-neutral contract). ONE window exists for the life of the process.
//! It opens as an ordinary titled dialog carrying the prompt ("Share this
//! window, then press OK"), and when the user accepts, that very same NSWindow
//! — never a replacement, because the share the meeting app has just started is
//! bound to this window's identity — sheds its title bar, takes the region's
//! size, and is parked entirely off screen, where libobs paints the mirrored
//! region into its contentView.
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
use std::ffi::c_void;

use objc2::rc::Retained;
use objc2::runtime::{AnyObject, NSObject};
use objc2::{define_class, msg_send, sel, MainThreadMarker, MainThreadOnly};
use objc2_app_kit::{
    NSApplication, NSApplicationDidChangeScreenParametersNotification, NSBackingStoreType,
    NSBezelStyle, NSButton, NSButtonType, NSEvent, NSScreen, NSTextField, NSView, NSWindow,
    NSWindowStyleMask, NSWindowWillCloseNotification,
};
use objc2_foundation::{
    NSNotification, NSNotificationCenter, NSPoint, NSRect, NSRunLoop, NSRunLoopCommonModes, NSSize,
    NSString, NSTimer,
};

use obs_platform::region::Rect;

use super::{AppEvents, UiConfig};
use crate::commands::Command;

/// Content size of the prompt window, in points. 3:2 and comfortably bigger
/// than the message needs: this window's whole job in that phase is to be easy
/// to spot in a meeting app's share picker and easy to click, not to be
/// compact. It is nothing to do with the region — the region's size only
/// arrives on the window when the prompt is accepted.
const PROMPT_SIZE: NSSize = NSSize::new(420.0, 280.0);

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

/// How far past the right-hand edge of every display the parked mirror sits,
/// in capture units (points).
///
/// Any positive value would do — the window only has to miss every display —
/// but a generous one means no rounding, no HiDPI backing-scale surprise and no
/// display hot-plug race can leave a sliver of the mirror visible on a real
/// screen, which would immediately show up as the capture photographing itself.
const OFFSCREEN_MARGIN: i32 = 512;

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

/// Where the mirror lives once the prompt is accepted: `region`'s size, at an
/// origin past the right-hand edge of every display.
///
/// To the *right* rather than above or below because the desktop is a
/// horizontal strip of displays in every normal arrangement, so one number —
/// the largest right edge — puts the window clear of all of them regardless of
/// how they are stacked vertically. The y is the union's top edge, which is
/// arbitrary but keeps the window in a predictable place for anyone inspecting
/// it with a window-list tool.
///
/// Recomputed on every placement rather than cached, because displays are
/// hot-pluggable: an external monitor arriving to the right of the built-in one
/// would otherwise extend the desktop over the parked mirror and re-introduce
/// exactly the recursion the parking exists to prevent.
fn parked_region(mtm: MainThreadMarker, region: Rect) -> Rect {
    let screens = NSScreen::screens(mtm);
    let mut right: Option<i32> = None;
    let mut top: Option<i32> = None;
    for screen in screens.iter() {
        // `frame`, not `visibleFrame`: the menu bar and the Dock are still
        // screen, and a window hiding under either is still on a display the
        // capture can see.
        let r = cocoa_to_capture(mtm, screen.frame());
        let edge = r.x.saturating_add(r.w as i32);
        right = Some(right.map_or(edge, |v| v.max(edge)));
        top = Some(top.map_or(r.y, |v| v.min(r.y)));
    }
    Rect {
        // No screens at all (headless CI, every display asleep): 0 + margin is
        // still a well-defined place to put a window nobody can see.
        x: right.unwrap_or(0).saturating_add(OFFSCREEN_MARGIN),
        y: top.unwrap_or(0),
        w: region.w,
        h: region.h,
    }
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
    // display:false — there is no display to draw on, and the swapchain
    // presents on the graphics thread regardless.
    window.setFrame_display(window.frameRectForContentRect(content), false);
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
        /// This is the single most load-bearing line in the file. AppKit's
        /// default implementation shoves any window whose frame falls outside
        /// the visible frame of a screen back onto that screen, and parking the
        /// mirror off screen is precisely a request to place a window nowhere
        /// near one. Without this override the `setFrame:` in [`park`] would be
        /// silently rewritten to an on-screen rect, and the mirror would
        /// reappear on a display — where the region's display capture would
        /// photograph it, the mirror would show the capture of itself, and the
        /// user would get the infinite corridor the whole off-screen design
        /// exists to avoid. (Measured before the rewrite, when the same
        /// override was protecting an on-screen window: `--region
        /// 756,0,756,491` on a 1512x982 display had every window dragged to
        /// y=33, the first pixel below the menu bar, instead of the modeled
        /// y=-50/-4. AppKit does this to borderless windows too, not only
        /// titled ones.)
        ///
        /// The prompt phase is unaffected: `center()` puts that window well
        /// inside a screen, so there is nothing to constrain.
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
/// Plain AppKit controls rather than something hand-drawn: this is a window
/// the user is about to hunt for in a picker, so it should look like an
/// ordinary dialog, and the button gets focus ring, Return-key activation and
/// accessibility for free.
fn install_prompt(
    mtm: MainThreadMarker,
    content: &NSView,
    controller: &Controller,
) -> Vec<Retained<NSView>> {
    const PAD: f64 = 12.0;
    const GAP: f64 = 14.0;
    const LABEL_H: f64 = 34.0;
    const BTN_W: f64 = 110.0;
    const BTN_H: f64 = 32.0;

    let bounds = content.bounds();
    let (w, h) = (bounds.size.width, bounds.size.height);

    let target: &AnyObject = controller;
    let button = PromptButton::alloc(mtm).set_ivars(());
    let button: Retained<PromptButton> =
        unsafe { msg_send![super(button), initWithFrame: NSRect::ZERO] };
    button.setTitle(&NSString::from_str("OK"));
    button.setBezelStyle(NSBezelStyle::Push);
    button.setButtonType(NSButtonType::MomentaryPushIn);
    unsafe {
        button.setTarget(Some(target));
        button.setAction(Some(sel!(promptAccepted:)));
    }
    // Return activates it, and AppKit paints it as the default button.
    button.setKeyEquivalent(&NSString::from_str("\r"));

    // The content size is a constant now ([`PROMPT_SIZE`]), so the two
    // degradation steps below are defensive rather than load-bearing: they cost
    // a couple of comparisons and they mean shrinking that constant can never
    // push a control outside the client area. Drop the label first, then let
    // the button take the whole view.
    let btn_w = BTN_W.min(w - 2.0 * PAD).max(0.0);
    let btn_h = BTN_H.min(h - 2.0 * PAD).max(0.0);
    let room_for_label = w >= 220.0 && h >= LABEL_H + GAP + BTN_H + 2.0 * PAD;

    let mut out: Vec<Retained<NSView>> = Vec::new();

    if btn_w < 40.0 || btn_h < 20.0 {
        button.setFrame(bounds);
    } else if room_for_label {
        let block_h = LABEL_H + GAP + btn_h;
        let block_bottom = ((h - block_h) / 2.0).max(PAD);
        button.setFrame(NSRect::new(
            NSPoint::new((w - btn_w) / 2.0, block_bottom),
            NSSize::new(btn_w, btn_h),
        ));
        let label = NSTextField::labelWithString(
            &NSString::from_str("Share this window, then press OK"),
            mtm,
        );
        // Centered, wrapping across the full width.
        label.setAlignment(objc2_app_kit::NSTextAlignment::Center);
        label.setFrame(NSRect::new(
            NSPoint::new(PAD, block_bottom + btn_h + GAP),
            NSSize::new(w - 2.0 * PAD, LABEL_H),
        ));
        content.addSubview(&label);
        // NSTextField : NSControl : NSView
        out.push(Retained::into_super(Retained::into_super(label)));
    } else {
        button.setFrame(NSRect::new(
            NSPoint::new((w - btn_w) / 2.0, (h - btn_h) / 2.0),
            NSSize::new(btn_w, btn_h),
        ));
    }

    content.addSubview(&button);
    // PromptButton : NSButton : NSControl : NSView
    out.push(Retained::into_super(Retained::into_super(
        Retained::into_super(button),
    )));
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

    // Park it: the region's size, at an origin past every display (see
    // [`parked_region`] and the `constrainFrameRect:toScreen:` override, which
    // is what makes an off-screen placement stick at all).
    //
    // Note what is NOT done here, and must never be: the window is not
    // miniaturized and not ordered out. Either would take it out of the window
    // server's compositing, its backing surface would stop being updated, and
    // the meeting app's share would freeze on the last frame it saw. The window
    // stays "visible" in AppKit's sense for the rest of the process — it simply
    // sits where no display shows it.
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
/// from one `move` to the next — and an external display arriving to the right
/// of the built-in one extends the desktop straight over the parked window,
/// putting a borderless, region-sized window full of live mirrored content on a
/// real screen. Over the captured region that is the infinite corridor the
/// parking exists to prevent; anywhere else it is still this process drawing on
/// top of Clowd's own border and toolbar. The generous [`OFFSCREEN_MARGIN`] only
/// buys headroom, and `constrainFrameRect:toScreen:` returning the rect verbatim
/// means AppKit will not pull the window back on its own either.
///
/// Does nothing during the prompt phase: that window is meant to be on screen,
/// where the user can find and click it. (win32.rs handles the equivalent
/// WM_DISPLAYCHANGE.)
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
    // Centred rather than placed on the region: a window sized and positioned
    // like the final mirror is awkward to pick, and for a region at a screen
    // edge would be largely off-screen.
    let window = ShareWindow::create(
        mtm,
        NSRect::new(NSPoint::new(0.0, 0.0), PROMPT_SIZE),
        NSWindowStyleMask::Titled | NSWindowStyleMask::Closable,
    );
    window.setTitle(&NSString::from_str(&cfg.title));
    window.center();

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
