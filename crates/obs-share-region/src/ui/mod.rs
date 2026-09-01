//! Platform-neutral UI facade. The platform modules (win32/appkit) own the one
//! window this process ever creates and the event loop that pumps it; the app
//! core (main.rs / mirror.rs) never touches a window handle except through
//! [`AppEvents::mirror_ready`].
//!
//! # Lifecycle: two phases, one window
//!
//! PROMPT PHASE. `run` creates a single ordinary window — titled, frontmost,
//! activated — SMALL and wherever the platform cares to put it, whose client
//! area shows "Share this window, then press OK" and an OK button. That is the
//! entire user interface of this process, and at this point there is no obs
//! display attached to anything: the user is being asked to point their meeting
//! app's share picker at a window that is plainly visible and clickable, which
//! is the only thing the picker flows in most conferencing apps (macOS'
//! click-to-pick UIs especially) can reliably target. Small and unplaced rather
//! than region-shaped because it has to be easy to find and click: a window
//! already sized and positioned like the final mirror is awkward to pick, and
//! for a region at a screen edge would be largely off-screen.
//!
//! MIRROR PHASE (entered when the user presses OK). The SAME window is reused —
//! never recreated — because the share the user just started in the meeting app
//! is bound to that window's *identity* (its HWND on Windows, its NSWindow on
//! macOS). Recreating it, even with identical style and geometry, would silently
//! turn the share into a share of a window that no longer exists. So the
//! platform layer instead mutates the window in place: it strips the prompt
//! controls, drops the title bar and border (a window share captures the whole
//! window frame, so a caption would otherwise appear in the shared output),
//! resizes the client area to exactly the region's size, PUTS THE WINDOW WHERE
//! NOTHING CAN PHOTOGRAPH IT, and hands the client area to
//! [`AppEvents::mirror_ready`]. From then on the window is only ever resized and
//! moved in response to `move` commands arriving on stdin; nothing the user does
//! touches it.
//!
//! # Why the window has to hide, and how each platform hides it
//!
//! The mirror is fed by a display capture of the region. If the mirror window is
//! visible anywhere on a captured display, the capture photographs the mirror,
//! which then shows the capture of itself, and so on — the infinite-corridor
//! effect. The original design fought this with a second, opaque "mask" window
//! pinned exactly over the mirror, plus a third "frame" window drawing a border
//! around the live region; all three are gone.
//!
//! What replaced them is platform-specific, because the two window servers
//! disagree about what is capturable:
//!
//! * **Windows** parks the window entirely outside the virtual desktop's
//!   bounds. There is nothing on screen to photograph, and DWM keeps
//!   compositing the window's redirection surface, so a *window* capture — what
//!   the meeting app is doing — still works.
//! * **macOS** cannot do that: ScreenCaptureKit refuses to start a stream on a
//!   window that intersects no display, even though it lists the window and
//!   reports it as on-screen. So the mirror stays on a display and is hidden by
//!   window LEVEL instead, one step below the desktop wallpaper, which occludes
//!   it for the user and for any display capture alike. See the module docs of
//!   `ui/appkit.rs` for the measurements behind that.
//!
//! Either way the window still exists, is still composited, and is still
//! capturable by a window capture, so the share keeps working while the pixels
//! never re-enter the display capture.
//!
//! The border and the floating controls around the live region are drawn by the
//! Clowd shell that spawns this process (Clowd.Ui/Video/BorderWindow and
//! FloatingToolbarWindow), not here. This binary must therefore put *nothing*
//! on screen once the prompt is dismissed — no frame, no handles, no buttons —
//! or it would draw a second border on top of Clowd's.
//!
//! # Coordinate space
//!
//! All geometry crossing this boundary is in **capture space** (top-left origin,
//! y-down — Windows: physical px on the virtual desktop, the process having
//! opted into per-monitor-v2 DPI awareness in `obs_platform::init_process`;
//! macOS: CG points). Platform layers convert to their native window coordinates
//! (e.g. Cocoa's bottom-left space) internally, at the window boundary only.
//!
//! # Threading contract
//!
//! `run` is called on the main thread and never returns; every [`AppEvents`]
//! callback is delivered on that same main/UI thread, so the app core needs no
//! synchronization. [`post_command`] is the ONLY entry point from another
//! thread: the stdin reader thread pushes parsed commands onto the queue below
//! and wakes the platform event loop, which drains them with [`take_commands`]
//! back on the UI thread. The one other piece of app code running elsewhere is
//! the obs_display draw callback (`obscure::draw`, on the obs graphics thread),
//! which the app registers inside `mirror_ready` and which touches no UI state.

use std::collections::VecDeque;
use std::sync::Mutex;

use obs_platform::region::Rect;

use crate::commands::Command;

/// Everything the platform layer needs from the CLI. Deliberately one field:
/// the whole point of the rewrite is that this process owns no appearance.
pub struct UiConfig {
    /// Window title — the string the user has to find in the meeting app's
    /// window picker during the prompt phase, so callers should set something
    /// recognisable. It stays the window's title after the caption is dropped,
    /// because pickers list the title, not the caption bar.
    pub title: String,
}

/// Implemented in main.rs over `Mirror`. All calls arrive on the UI/main thread.
pub trait AppEvents {
    /// The window's client area is now the share surface; `handle` is HWND
    /// (win) / NSView* (mac). Create the ObsDisplay here, sized to the canvas
    /// px. Fires exactly once, when the user accepts the prompt and the window
    /// has already been restyled, resized and moved off screen — never before
    /// the window exists, and never a second time.
    fn mirror_ready(&mut self, handle: *mut std::ffi::c_void);

    /// Apply a new region (a `move` command from stdin). The app re-plans the
    /// capture and returns the region it ACTUALLY applied, which may differ from
    /// the request — sizes below the mirror's minimum are clamped, and odd sizes
    /// are rounded to the even size the canvas will have. The platform layer
    /// must adopt the returned rect for the window's size, never the requested
    /// one, so that the window and the canvas cannot disagree.
    ///
    /// `Err` means the move was REFUSED and nothing changed (the request
    /// intersects no display); its payload is the reason, which the platform
    /// layer emits as `command_error` INSTEAD of a `region_changed` ack. One
    /// line per command either way: a refusal that also echoed the unchanged
    /// region would look exactly like a successful move to that region.
    fn set_region(&mut self, region: Rect) -> Result<Rect, String>;

    /// Apply a new obscure mode (an `obscure`/`unobscure` command from stdin).
    /// Purely a preview-side effect: the region and the window are unchanged.
    fn set_obscure(&mut self, mode: crate::obscure::Mode);

    /// Quit requested — a `quit` command, EOF on stdin, or the user closing the
    /// window. Must not return; the app exits the process here.
    fn quit(&mut self) -> !;
}

/// Commands parsed off stdin, waiting for the UI thread to pick them up —
/// including the lines that failed to parse, which travel as `Command::Error`
/// so that every protocol response is written by this one thread, in the order
/// the commands arrived (see that variant's doc comment).
///
/// Process-global rather than owned by the platform layer because the stdin
/// thread starts before, and outlives, any particular window: it must be able
/// to enqueue a `quit` even if it arrives while the prompt is still up. The
/// lock is held only for a push or a drain — never across a wake, and never
/// across an `AppEvents` call — so it can never deadlock against the event
/// loop.
static QUEUE: Mutex<VecDeque<Command>> = Mutex::new(VecDeque::new());

/// Queue a command and wake the platform event loop so it drains it. Safe to
/// call from any thread; this is the only cross-thread entry point into the UI.
///
/// The wake happens after the lock is released, because on both platforms it
/// can synchronously re-enter the event loop (a posted message is dispatched,
/// a run-loop source fires) and would otherwise deadlock on our own mutex.
pub fn post_command(cmd: Command) {
    // A poisoned queue means a previous holder panicked while pushing or
    // draining. There is nothing to recover — the deque itself is always in a
    // consistent state at every await-free push/pop — so take it regardless
    // rather than turning a stray panic into a process that stops responding
    // to `quit`.
    QUEUE
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .push_back(cmd);

    #[cfg(windows)]
    win32::wake();
    #[cfg(target_os = "macos")]
    appkit::wake();
}

/// Drain every queued command, oldest first. Called by the platform layer on
/// the UI thread when it is woken.
///
/// Drains the whole queue rather than one command per wake so that a burst
/// (several `move`s while the user drags Clowd's border, say) is handled in one
/// pass, and so a lost or coalesced wake can never strand a command in the
/// queue indefinitely — the next wake picks up everything.
pub(crate) fn take_commands() -> Vec<Command> {
    let mut queue = QUEUE.lock().unwrap_or_else(|e| e.into_inner());
    queue.drain(..).collect()
}

// Each platform file implements
// `pub fn run(region: Rect, cfg: UiConfig, events: Box<dyn AppEvents>) -> !`,
// which creates the prompt window, runs the two phases described at the top of
// this file, and never returns (exit happens via `events.quit()`), plus
// `pub(super) fn wake()`, which nudges its event loop into calling
// `take_commands` on the UI thread.

#[cfg(windows)]
mod win32;
#[cfg(windows)]
pub use win32::run;

#[cfg(target_os = "macos")]
mod appkit;
#[cfg(target_os = "macos")]
pub use appkit::run;
