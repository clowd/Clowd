//! Making the target window scroll, and noticing when the user wants it to
//! stop.
//!
//! Two rungs of a ladder, in the order the driver tries them:
//!
//! 1. **Synthetic wheel at a parked cursor** ([`wheel_burst`]). This is what
//!    every scrolling-capture tool ships, because it is the only method that
//!    works on everything that handles a physical wheel — Chromium, Electron,
//!    Firefox, Qt, WinForms, custom-drawn Win32 — without asking the target
//!    to expose anything. Windows routes wheel input by cursor position (the
//!    "scroll inactive windows" default since Win10), so *where the cursor
//!    is* decides which pane scrolls. We park it on the point the user
//!    clicked and leave it there: the well-known failure of ShareX's version
//!    is that it injects wheel events wherever the mouse happens to be.
//! 2. **`WM_MOUSEWHEEL` posted at the window under the point**
//!    ([`wheel_message`]). Needs neither foreground nor cursor, and gets
//!    through when injection does not — most usefully when the target runs
//!    elevated and UIPI eats our `SendInput` silently. Not the default
//!    because plenty of frameworks ignore a synthetic wheel message
//!    (DirectComposition/`WM_POINTERWHEEL` surfaces especially).
//!
//! Nothing here assumes how far one notch scrolls. `WHEEL_DELTA` maps to
//! `SPI_GETWHEELSCROLLLINES` lines only if the target says so, and browsers,
//! editors and zoomed pages all disagree — the driver measures the
//! displacement it actually got and adapts the burst size.

use std::time::{Duration, Instant};

use windows::Win32::{
    Foundation::{LPARAM, POINT, RECT, WPARAM},
    Graphics::Gdi::ScreenToClient,
    UI::{
        Input::KeyboardAndMouse::{GetAsyncKeyState, SendInput, INPUT, INPUT_0, INPUT_MOUSE, MOUSEEVENTF_WHEEL, MOUSEINPUT, VK_ESCAPE},
        WindowsAndMessaging::{
            ChildWindowFromPointEx, GetAncestor, GetCursorPos, GetWindowRect, IsWindow, SendMessageTimeoutW, SetCursorPos,
            SetForegroundWindow, SetWindowPos, WindowFromPoint, CWP_SKIPINVISIBLE, CWP_SKIPTRANSPARENT, GA_ROOT, GA_ROOTOWNER, HWND_TOP,
            SMTO_ABORTIFHUNG, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, WM_MOUSEWHEEL,
        },
    },
};

pub use windows::Win32::Foundation::HWND;

use clowd_rust_core::geometry::{RectExt, ScreenPoint, ScreenRect};

/// One wheel notch, as Windows defines it. How far the target moves per
/// notch is entirely the target's business — see the module note.
const WHEEL_DELTA: i32 = 120;

/// How long we let a `WM_MOUSEWHEEL` sit in the target's queue. `SendMessage`
/// blocks until the window procedure returns, so a target that is busy (or
/// wedged) would otherwise stall the driver indefinitely with the user's
/// cursor pinned in place.
const MESSAGE_TIMEOUT_MS: u32 = 1_000;

/// How deep to chase child windows for the `WM_MOUSEWHEEL` fallback. Same
/// bound as the window walker's own child descent — a hierarchy that
/// somehow points back at itself must not spin.
const MAX_CHILD_DEPTH: usize = 10;

/// How long [`raise_over_point`] keeps re-checking after asking for the
/// target to come forward. A raise is asynchronous — the window has to
/// process the activation and the compositor has to restack — and an app
/// restoring from a minimised or background state can take a beat longer
/// than one that was already visible.
const RAISE_TIMEOUT: Duration = Duration::from_millis(1_200);

/// How often [`raise_over_point`] re-asks who owns the scroll point.
const RAISE_POLL: Duration = Duration::from_millis(40);

/// Settle on the window the wheel should be aimed at.
///
/// The marker `hwnd` wins. It names the window the *user* picked out of the
/// overlay's frozen desktop — the one whose region they selected — and that
/// intent is the only thing that survives the overlay closing. The live
/// `WindowFromPoint` answer cannot stand in for it: whatever is topmost at
/// the point right now may be a window sitting *over* the target, and
/// scrolling that one photographs the obstruction for the whole run. This is
/// exactly what happened when the user aimed the scroll point at a covered
/// part of their window.
///
/// The marker is validated first (the window may have closed since the
/// overlay ran, and Windows recycles handle values; the overlay is also
/// allowed to give up and send `0`), and `WindowFromPoint` stands in when it
/// does not hold up. Whichever wins, [`raise_over_point`] then has to get it
/// on top of the point before a single frame is captured.
pub fn resolve_target(hwnd: i64, point: ScreenPoint) -> Option<HWND> {
    let live = live_root_at(point);
    let marker = validated_marker(hwnd, point);
    if let (Some(live), Some(marker)) = (live, marker) {
        if live != marker {
            info!(
                "the window at the scroll point right now is {:?}, but the user picked marker hwnd {hwnd}; raising theirs",
                live.0
            );
        }
    }
    choose_target(live, marker)
}

/// The user's marker wins whenever it is still valid; the live window only
/// stands in when the marker is missing or no longer holds up.
fn choose_target(live: Option<HWND>, marker: Option<HWND>) -> Option<HWND> {
    marker.or(live)
}

/// Top-level window under `point` as of right now, or `None` over bare
/// desktop.
fn live_root_at(point: ScreenPoint) -> Option<HWND> {
    let under = unsafe { WindowFromPoint(as_point(point)) };
    (!under.is_invalid()).then(|| root_of(under))
}

/// The overlay's marker hwnd as a top-level window, or `None` when it does
/// not hold up any more (or was never resolved).
fn validated_marker(hwnd: i64, point: ScreenPoint) -> Option<HWND> {
    if hwnd == 0 {
        return None;
    }
    let candidate = HWND(hwnd as usize as *mut core::ffi::c_void);
    if !is_window(candidate) {
        warn!("marker hwnd {hwnd} is no longer a window");
        return None;
    }
    let root = root_of(candidate);
    if !window_rect(root).is_some_and(|r| r.contains(point)) {
        warn!("marker hwnd {hwnd} no longer covers the scroll point");
        return None;
    }
    Some(root)
}

/// Get the target on top of the scroll point, and report whether it worked.
///
/// Two things depend on this, and the second is the one that bites. Wheel
/// routing is positional, so a window covering the point eats the scroll;
/// and the capture is a `BitBlt` of the screen, so a window covering the
/// point is *also* what gets photographed. A target that cannot be raised
/// produces a tall picture of the wrong window with nothing about it looking
/// wrong, which is why the caller treats a `false` here as fatal rather than
/// pressing on.
///
/// Two rungs, least invasive first, each verified before the next is tried.
/// `SetForegroundWindow` is the honest one — it activates as well as
/// restacks, so the target behaves as though the user clicked it.
/// `SetWindowPos(HWND_TOP, SWP_NOACTIVATE)` follows: Z-order without focus,
/// which is all the capture strictly needs.
///
/// The second rung carries the common case on its own: restacking a window
/// that is *not* the foreground one is not gated by the foreground lock, so
/// a target buried under an inactive window comes up even when
/// `SetForegroundWindow` was refused (measured — a fully covered target
/// captured all 400 lines of the test page after the first rung failed).
/// What neither rung can do without foreground rights is get past a window
/// that holds the foreground itself. Those rights come from
/// `AllowSetForegroundWindow`: the shell grants them to the driver at spawn,
/// and the chain that keeps the shell entitled to hand them on is documented
/// in `CAPTURE_PROTOCOL.md` §3.5.
///
/// There is deliberately no third rung. `AttachThreadInput` would defeat the
/// lock by borrowing the foreground thread's input queue, and a driver
/// wedged attached to a hung foreground thread is a far worse failure than a
/// capture that declines to run.
///
/// Verification is by asking the same question the wheel and the `BitBlt`
/// will: who is at the point? An owned window of the target counts — a
/// tooltip or a dropdown of the app we are scrolling belongs to it.
pub fn raise_over_point(target: HWND, point: ScreenPoint) -> bool {
    if !unsafe { SetForegroundWindow(target) }.as_bool() {
        warn!("SetForegroundWindow refused; the shell's AllowSetForegroundWindow grant did not land");
    }
    if wait_until_owns_point(target, point, RAISE_TIMEOUT / 2) {
        return true;
    }

    if let Err(e) = unsafe { SetWindowPos(target, Some(HWND_TOP), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE) } {
        warn!("SetWindowPos(HWND_TOP) failed: {e}");
    }
    wait_until_owns_point(target, point, RAISE_TIMEOUT / 2)
}

/// Poll [`owns_point`] until it holds or `timeout` runs out.
fn wait_until_owns_point(target: HWND, point: ScreenPoint, timeout: Duration) -> bool {
    let deadline = Instant::now() + timeout;
    loop {
        if owns_point(target, point) {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        std::thread::sleep(RAISE_POLL);
    }
}

/// Is `target` (or something it owns) the window a click at `point` would
/// hit? `GA_ROOTOWNER` is checked alongside `GA_ROOT` so an app's own popup
/// — a dropdown, a tooltip, an owned tool window — is not mistaken for a
/// foreign window covering the target.
pub fn owns_point(target: HWND, point: ScreenPoint) -> bool {
    let under = unsafe { WindowFromPoint(as_point(point)) };
    if under.is_invalid() {
        return false;
    }
    root_of(under) == target || ancestor(under, GA_ROOTOWNER) == target
}

/// The window currently at `point`, for logging which obstruction won.
pub fn describe_window_at(point: ScreenPoint) -> String {
    match live_root_at(point) {
        Some(hwnd) => format!("{:?}", hwnd.0),
        None => "nothing".to_string(),
    }
}

/// Move the real cursor to the scroll point. Called before the run, and
/// again only when a pause ends — between those, every reading of the
/// cursor is a question about what the *user* is doing, and re-parking it
/// would erase the answer.
pub fn park_cursor(point: ScreenPoint) {
    if unsafe { SetCursorPos(point.x, point.y) }.is_err() {
        warn!("SetCursorPos({}, {}) failed; wheel events may land elsewhere", point.x, point.y);
    }
}

/// Where the cursor is now, or `None` if Windows will not say (a locked
/// desktop, a secure-desktop transition). Callers treat that as "no news",
/// never as "it moved".
pub fn cursor_pos() -> Option<ScreenPoint> {
    let mut pt = POINT::default();
    unsafe { GetCursorPos(&mut pt) }.ok()?;
    Some(ScreenPoint::new(pt.x, pt.y))
}

/// Chebyshev distance from the parked point to where the cursor is now.
/// Past a threshold this means the user has taken the mouse back, and the
/// driver pauses until they give it up again — a scroll injected while they
/// are using the pointer lands somewhere they did not ask for.
pub fn cursor_drift(parked: ScreenPoint) -> i32 {
    cursor_pos().map_or(0, |pt| chebyshev(pt, parked))
}

/// Chebyshev (chessboard) distance between two points. The measure the
/// whole driver uses for "has the pointer moved": axis-independent and
/// integer, so a diagonal nudge counts the same as a horizontal one.
pub fn chebyshev(a: ScreenPoint, b: ScreenPoint) -> i32 {
    (a.x - b.x).abs().max((a.y - b.y).abs())
}

/// Is Escape physically down right now?
///
/// The high bit is the current key state; `GetAsyncKeyState`'s low bit
/// ("pressed since the last call") is deliberately ignored — Esc belongs to
/// the foreground app, which is the *target*, so all we can honestly ask is
/// whether the user is holding it, and racing another poller for the edge
/// bit would lose the answer at random.
pub fn escape_pressed() -> bool {
    (unsafe { GetAsyncKeyState(VK_ESCAPE.0 as i32) } as u16 & 0x8000) != 0
}

/// Which way a burst moves the document.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WheelDir {
    /// Away from the user — further down the page. The capture direction.
    Down,
    /// Toward the user — back up the page. Used only by the rewind.
    Up,
}

impl WheelDir {
    /// The signed `mouseData` delta for one notch.
    fn delta(self) -> i32 {
        match self {
            WheelDir::Down => -WHEEL_DELTA,
            WheelDir::Up => WHEEL_DELTA,
        }
    }
}

/// Inject `ticks` wheel notches at the current (parked) cursor position.
///
/// One `INPUT` record per notch rather than a single record carrying
/// `n * WHEEL_DELTA`: that is the stream a real wheel produces, and apps
/// that quantise per message — or start one smooth-scroll animation per
/// event — behave the same way for us as for a person.
pub fn wheel_burst(ticks: u32, dir: WheelDir) {
    let delta = dir.delta();
    let inputs: Vec<INPUT> = (0..ticks.max(1))
        .map(|_| INPUT {
            r#type: INPUT_MOUSE,
            Anonymous: INPUT_0 {
                mi: MOUSEINPUT {
                    dx: 0,
                    dy: 0,
                    mouseData: delta as u32,
                    dwFlags: MOUSEEVENTF_WHEEL,
                    time: 0,
                    dwExtraInfo: 0,
                },
            },
        })
        .collect();

    let sent = unsafe { SendInput(&inputs, std::mem::size_of::<INPUT>() as i32) } as usize;
    if sent != inputs.len() {
        // The usual cause is UIPI: an elevated foreground window blocks
        // input from our medium-integrity process, and SendInput reports
        // the block by delivering fewer events. The driver notices the
        // absence of movement either way and steps down the ladder.
        warn!("SendInput delivered {sent}/{} wheel events (elevated target?)", inputs.len());
    }
}

/// Post `ticks` notches straight to the window under `point` as
/// `WM_MOUSEWHEEL`. Returns false when no window could be addressed.
pub fn wheel_message(root: HWND, point: ScreenPoint, ticks: u32) -> bool {
    let Some(target) = deepest_child_at(root, point) else {
        return false;
    };
    let delta = -(WHEEL_DELTA * ticks.max(1) as i32);

    // wParam: HIWORD = the wheel delta, LOWORD = modifier key state (none).
    let wparam = WPARAM(((delta as i16 as u16 as usize) << 16) & 0xFFFF_0000);
    // lParam carries *screen* coordinates packed as two signed 16-bit words.
    // A monitor left of or above the primary one has negative coordinates,
    // hence the cast through i16 — masking would send the target off to
    // x = 64000.
    let lparam = LPARAM((((point.y as i16 as u16 as u32) << 16) | (point.x as i16 as u16 as u32)) as i32 as isize);

    unsafe {
        SendMessageTimeoutW(target, WM_MOUSEWHEEL, wparam, lparam, SMTO_ABORTIFHUNG, MESSAGE_TIMEOUT_MS, None);
    }
    true
}

/// The deepest child window under `point`.
///
/// Chromium and Electron hand wheel input to a child of the top-level frame
/// (`Chrome_RenderWidgetHostHWND`); a message posted at the root is simply
/// dropped there, so walking down to the leaf is most of what makes this
/// fallback worth having.
fn deepest_child_at(root: HWND, point: ScreenPoint) -> Option<HWND> {
    let mut current = root;
    for _ in 0..MAX_CHILD_DEPTH {
        // ChildWindowFromPointEx wants the point in `current`'s client
        // space, so it has to be re-converted at every level.
        let mut pt = as_point(point);
        if !unsafe { ScreenToClient(current, &mut pt) }.as_bool() {
            break;
        }
        let child = unsafe { ChildWindowFromPointEx(current, pt, CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT) };
        if child.is_invalid() || child == current {
            break;
        }
        current = child;
    }
    (!current.is_invalid()).then_some(current)
}

/// Top-level ancestor of `hwnd` (itself, if it is already one).
fn root_of(hwnd: HWND) -> HWND {
    ancestor(hwnd, GA_ROOT)
}

/// `GetAncestor`, falling back to `hwnd` itself when there is no such
/// ancestor — the answer every caller here wants for a window that is
/// already the thing being asked for.
fn ancestor(hwnd: HWND, flag: windows::Win32::UI::WindowsAndMessaging::GET_ANCESTOR_FLAGS) -> HWND {
    let found = unsafe { GetAncestor(hwnd, flag) };
    if found.is_invalid() {
        hwnd
    } else {
        found
    }
}

pub fn is_window(hwnd: HWND) -> bool {
    unsafe { IsWindow(Some(hwnd)) }.as_bool()
}

/// `GetWindowRect` as a `ScreenRect`. The driver samples this once at the
/// start and compares on every step: a window that has moved or resized has
/// invalidated the fixed region we are photographing.
pub fn window_rect(hwnd: HWND) -> Option<ScreenRect> {
    let mut rect = RECT::default();
    unsafe { GetWindowRect(hwnd, &mut rect) }.ok()?;
    Some(ScreenRect::from_exact(rect.left, rect.top, rect.right, rect.bottom))
}

fn as_point(p: ScreenPoint) -> POINT {
    POINT {
        x: p.x,
        y: p.y,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hwnd(v: usize) -> HWND {
        HWND(v as *mut core::ffi::c_void)
    }

    #[test]
    fn the_marker_outranks_the_live_window() {
        // The marker is the window the user picked in the overlay. Whatever
        // is topmost at the point *now* may be sitting over it, and both the
        // wheel and the BitBlt would go to that one — so the user's choice
        // wins and gets raised.
        assert_eq!(choose_target(Some(hwnd(1)), Some(hwnd(2))), Some(hwnd(2)));
        assert_eq!(choose_target(Some(hwnd(1)), Some(hwnd(1))), Some(hwnd(1)));
        assert_eq!(choose_target(None, Some(hwnd(2))), Some(hwnd(2)));
        // The live window still stands in when the overlay never resolved a
        // marker, or the one it resolved no longer holds up.
        assert_eq!(choose_target(Some(hwnd(1)), None), Some(hwnd(1)));
        assert_eq!(choose_target(None, None), None);
    }

    #[test]
    fn chebyshev_is_the_larger_axis() {
        assert_eq!(chebyshev(ScreenPoint::new(10, 10), ScreenPoint::new(10, 10)), 0);
        assert_eq!(chebyshev(ScreenPoint::new(10, 10), ScreenPoint::new(13, 11)), 3);
        assert_eq!(chebyshev(ScreenPoint::new(10, 10), ScreenPoint::new(9, 4)), 6);
        // Negative coordinates (a monitor left of or above the primary) are
        // just as valid a place to park the cursor.
        assert_eq!(chebyshev(ScreenPoint::new(-100, -50), ScreenPoint::new(-90, -50)), 10);
    }
}
