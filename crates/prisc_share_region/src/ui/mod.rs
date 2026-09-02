//! Platform-neutral UI facade. The platform modules (win32/appkit) own the one
//! window this process ever creates and the event loop that pumps it; the app
//! core (main.rs / mirror.rs) never touches a window handle except through
//! [`AppEvents::mirror_ready`].
//!
//! # Lifecycle: two phases, one window
//!
//! PROMPT PHASE. `run` creates a single ordinary window — titled, frontmost,
//! activated — SMALL and centred on the region, whose client area shows "Share
//! this window, then press OK" and an OK button. That is the entire user
//! interface of this process, and at this point there is no obs display
//! attached to anything: the user is being asked to point their meeting app's
//! share picker at a window that is plainly visible and clickable, which is the
//! only thing the picker flows in most conferencing apps (macOS' click-to-pick
//! UIs especially) can reliably target.
//!
//! Small rather than region-shaped because it has to be easy to find and click:
//! a window sized like the final mirror is awkward to pick, and at the mirror's
//! minimum region size unreadable. Placed on the region rather than left
//! wherever the window manager cascades it because that is where the user is
//! looking — they have just drawn that rectangle — and on a multi-display
//! desktop an unplaced prompt can open on a screen they are not even watching.
//! [`centre_prompt_on`] does the arithmetic for both platforms: the window is
//! centred on the region's centre and then clamped whole into the usable area
//! of the region's display, so a tiny region, or one against a screen edge,
//! still gets a fully visible and fully clickable prompt beside it.
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

/// Top-left for a `w` x `h` prompt window: centred on `region`, then pushed
/// whole inside `bounds`. Both rects and the result are in capture space
/// (see "Coordinate space" above); `bounds` is the usable area of the display
/// the prompt is going to (Windows: the monitor's work area; macOS: the
/// screen's visible frame), so the clamp also keeps the window clear of the
/// taskbar / menu bar and Dock.
///
/// Centred ON the region, not fitted INSIDE it: the region is frequently
/// smaller than the prompt and frequently at a screen edge, so containment has
/// no answer for most regions while a centre has one for all of them. The clamp
/// is what makes that safe — every pixel of the window lands in `bounds`, so
/// all of it is visible and clickable.
///
/// i64 throughout: a region near the far edge of capture space plus its own
/// size overflows i32, and capture space genuinely carries large and negative
/// coordinates (displays left of and above the primary).
pub(crate) fn centre_prompt_on(region: Rect, bounds: Rect, w: i32, h: i32) -> (i32, i32) {
    let cx = region.x as i64 + region.w as i64 / 2;
    let cy = region.y as i64 + region.h as i64 / 2;
    let right = bounds.x as i64 + bounds.w as i64;
    let bottom = bounds.y as i64 + bounds.h as i64;
    (
        clamp_span(cx - w as i64 / 2, w as i64, bounds.x as i64, right),
        clamp_span(cy - h as i64 / 2, h as i64, bounds.y as i64, bottom),
    )
}

/// `v` moved so that `v..v + size` lies inside `lo..hi`, then narrowed back to
/// i32 — which cannot truncate, because the result is one of `lo`, `hi - size`
/// or `v`, and the first two came from i32 rects.
///
/// `lo` wins when the window is larger than the span, because the parts the
/// user needs — the title bar, and the OK button on the side the text reads
/// from — sit nearer the top-left than the bottom-right, and pinning the far
/// edge would push both off screen.
fn clamp_span(v: i64, size: i64, lo: i64, hi: i64) -> i32 {
    v.min(hi - size).max(lo) as i32
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

#[cfg(test)]
mod tests {
    use super::*;

    /// 1920x1080 with a 40px taskbar along the bottom.
    fn work() -> Rect {
        Rect {
            x: 0,
            y: 0,
            w: 1920,
            h: 1040,
        }
    }

    fn rect(x: i32, y: i32, w: u32, h: u32) -> Rect {
        Rect { x, y, w, h }
    }

    #[test]
    fn centres_on_a_region_with_room_around_it() {
        // 400x300 region centred at (1000, 600); a 460x220 prompt centres there
        // too and touches no edge.
        let placed = centre_prompt_on(rect(800, 450, 400, 300), work(), 460, 220);
        assert_eq!(placed, (1000 - 230, 600 - 110));
    }

    #[test]
    fn centres_on_a_region_smaller_than_the_prompt() {
        // The prompt overhangs the region on every side, which is the point: it
        // is placed AT the region, not inside it.
        let placed = centre_prompt_on(rect(900, 500, 40, 40), work(), 460, 220);
        assert_eq!(placed, (920 - 230, 520 - 110));
    }

    #[test]
    fn pushes_a_corner_region_fully_into_the_bounds() {
        // Top-left corner: the centred prompt would start at negative
        // coordinates and hang off two edges.
        let placed = centre_prompt_on(rect(0, 0, 100, 100), work(), 460, 220);
        assert_eq!(placed, (0, 0));

        // Bottom-right corner: now it is the far edges that overflow, and the
        // clamp must respect the taskbar the work area excludes.
        let placed = centre_prompt_on(rect(1820, 940, 100, 100), work(), 460, 220);
        assert_eq!(placed, (1920 - 460, 1040 - 220));
    }

    #[test]
    fn clamps_into_a_display_left_of_the_primary() {
        // Capture space has negative coordinates; the clamp is to the display
        // the region is on, not to the primary.
        let left = rect(-1920, 0, 1920, 1040);
        let placed = centre_prompt_on(rect(-1920, 0, 100, 100), left, 460, 220);
        assert_eq!(placed, (-1920, 0));
    }

    #[test]
    fn pins_the_near_edge_when_the_prompt_is_larger_than_the_bounds() {
        // Degenerate but reachable on a small or heavily-scaled display: keep
        // the title bar and the near edge on screen, not the far corner.
        let tiny = rect(10, 20, 200, 100);
        let placed = centre_prompt_on(rect(50, 50, 10, 10), tiny, 460, 220);
        assert_eq!(placed, (10, 20));
    }

    #[test]
    fn does_not_overflow_at_the_far_edge_of_capture_space() {
        // An i32::MAX-ish origin plus the region's own size: the i64 math has to
        // clamp rather than wrap round to a negative coordinate.
        let placed = centre_prompt_on(
            rect(i32::MAX - 10, i32::MAX - 10, 4000, 4000),
            work(),
            460,
            220,
        );
        assert_eq!(placed, (1920 - 460, 1040 - 220));
    }
}
