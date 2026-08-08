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

use windows::Win32::{
    Foundation::{LPARAM, POINT, RECT, WPARAM},
    Graphics::Gdi::ScreenToClient,
    UI::{
        Input::KeyboardAndMouse::{GetAsyncKeyState, SendInput, INPUT, INPUT_0, INPUT_MOUSE, MOUSEEVENTF_WHEEL, MOUSEINPUT, VK_ESCAPE},
        WindowsAndMessaging::{
            ChildWindowFromPointEx, GetAncestor, GetCursorPos, GetWindowRect, IsWindow, SendMessageTimeoutW, SetCursorPos,
            SetForegroundWindow, WindowFromPoint, CWP_SKIPINVISIBLE, CWP_SKIPTRANSPARENT, GA_ROOT, SMTO_ABORTIFHUNG, WM_MOUSEWHEEL,
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

/// Settle on the window the wheel should be aimed at.
///
/// Wheel routing is positional — whatever window is topmost at the point is
/// what a wheel event there will reach — so the live `WindowFromPoint` root
/// is the authority. The marker `hwnd` the overlay resolved is only a hint:
/// its snapshot filters out untitled and mostly-obscured windows, so it can
/// name a window *behind* the real target, and foregrounding that one would
/// raise the wrong window over the scroll point and photograph it all run.
/// The marker is still validated (the window may have closed since the
/// overlay ran, and Windows recycles handle values; the overlay is also
/// allowed to give up and send `0`) so it can stand in on the rare occasion
/// `WindowFromPoint` itself comes up empty.
pub fn resolve_target(hwnd: i64, point: ScreenPoint) -> Option<HWND> {
    let under = unsafe { WindowFromPoint(as_point(point)) };
    let live = (!under.is_invalid()).then(|| root_of(under));
    let marker = validated_marker(hwnd, point);
    if let (Some(live), Some(marker)) = (live, marker) {
        if live != marker {
            info!(
                "marker hwnd {hwnd} is behind the live window {:?} at the scroll point; scrolling the live one",
                live.0
            );
        }
    }
    choose_target(live, marker)
}

/// The live window wins whenever there is one; the validated marker only
/// stands in when `WindowFromPoint` found nothing at the point at all.
fn choose_target(live: Option<HWND>, marker: Option<HWND>) -> Option<HWND> {
    live.or(marker)
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

/// Bring the target to the foreground. Best-effort: the shell calls
/// `AllowSetForegroundWindow` for us before spawning, but the foreground
/// lock can still refuse, and it does not matter much — with "scroll
/// inactive windows" on (the Win10+ default) the wheel follows the cursor
/// regardless. Focus is insurance for the machines where that is off.
pub fn focus(hwnd: HWND) {
    if !unsafe { SetForegroundWindow(hwnd) }.as_bool() {
        warn!("SetForegroundWindow refused; relying on scroll-inactive-windows routing");
    }
}

/// Move the real cursor to the scroll point. Called once, before the run:
/// every later reading of the cursor is a question about the *user*, so we
/// must not keep re-parking it.
pub fn park_cursor(point: ScreenPoint) {
    if unsafe { SetCursorPos(point.x, point.y) }.is_err() {
        warn!("SetCursorPos({}, {}) failed; wheel events may land elsewhere", point.x, point.y);
    }
}

/// Chebyshev distance from the parked point to where the cursor is now.
/// Non-zero means the user took the mouse back — the driver reads that as
/// "finish now", which turns the classic mis-scroll bug into a deliberate
/// gesture.
pub fn cursor_drift(parked: ScreenPoint) -> i32 {
    let mut pt = POINT::default();
    if unsafe { GetCursorPos(&mut pt) }.is_err() {
        return 0;
    }
    (pt.x - parked.x)
        .abs()
        .max((pt.y - parked.y).abs())
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

/// Inject `ticks` wheel notches at the current (parked) cursor position.
///
/// One `INPUT` record per notch rather than a single record carrying
/// `n * WHEEL_DELTA`: that is the stream a real wheel produces, and apps
/// that quantise per message — or start one smooth-scroll animation per
/// event — behave the same way for us as for a person.
pub fn wheel_burst(ticks: u32) {
    let inputs: Vec<INPUT> = (0..ticks.max(1))
        .map(|_| INPUT {
            r#type: INPUT_MOUSE,
            Anonymous: INPUT_0 {
                mi: MOUSEINPUT {
                    dx: 0,
                    dy: 0,
                    // Negative = scroll away from the user = down the page.
                    mouseData: (-WHEEL_DELTA) as u32,
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
    let root = unsafe { GetAncestor(hwnd, GA_ROOT) };
    if root.is_invalid() {
        hwnd
    } else {
        root
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
    fn the_live_window_outranks_the_marker() {
        // The overlay's snapshot filters untitled and mostly-obscured
        // windows, so its marker can name a window *behind* the one a
        // wheel at the point will actually reach — the live answer must
        // win whenever both exist.
        assert_eq!(choose_target(Some(hwnd(1)), Some(hwnd(2))), Some(hwnd(1)));
        assert_eq!(choose_target(Some(hwnd(1)), Some(hwnd(1))), Some(hwnd(1)));
        assert_eq!(choose_target(Some(hwnd(1)), None), Some(hwnd(1)));
        // The marker is still better than nothing when the point is over
        // no window at all.
        assert_eq!(choose_target(None, Some(hwnd(2))), Some(hwnd(2)));
        assert_eq!(choose_target(None, None), None);
    }
}
