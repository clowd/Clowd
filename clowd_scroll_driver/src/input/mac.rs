//! The macOS half of [`crate::input`]: `CGEventCreateScrollWheelEvent2` for
//! the wheel, `CGEventPostToPid` for the second rung, `NSRunningApplication`
//! for the raise, `CGEventSourceKeyState` for the Esc poll, and
//! `CGWindowListCopyWindowInfo` for every question about who is where.
//!
//! Everything here is in CG points, which is the space the `scroll` marker
//! carries on this platform (`session_output::scroll_action_point`) and the
//! space `kCGWindowBounds`, `CGWarpMouseCursorPosition` and
//! `CGEventGetLocation` all speak. A captured [`Frame`] is *not* in this
//! space on a Retina display — see [`crate::frame`].
//!
//! [`Frame`]: crate::frame::Frame
//!
//! Two permissions stand behind all of it, and neither can be worked around:
//! Screen Recording for the capture and **Accessibility** for the synthetic
//! wheel — an untrusted process's posted events are dropped by the window
//! server without a word, which would present as a one-frame capture with
//! nothing to explain it. [`preflight`] is what turns that into a sentence
//! the user can act on. TCC records both against `Clowd.app` rather than this
//! executable (the shell documents the chain in `MacPermissions`), so the
//! grant the app already asks for covers the driver.

use std::ffi::c_void;
use std::time::Instant;

use core_foundation::base::TCFType;
use core_foundation::dictionary::{CFDictionary, CFDictionaryRef};
use core_foundation::number::{CFNumber, CFNumberRef};
use core_foundation::string::{CFString, CFStringRef};
use core_graphics::access::ScreenCaptureAccess;
use core_graphics::display::CGDisplay;
use core_graphics::event::{CGEvent, CGEventTapLocation, ScrollEventUnit};
use core_graphics::event_source::{CGEventSource, CGEventSourceStateID};
use core_graphics::geometry::{CGPoint, CGRect};
use core_graphics::window::{self, kCGNullWindowID, kCGWindowListExcludeDesktopElements, kCGWindowListOptionOnScreenOnly, CGWindowID};
use objc2_app_kit::{NSApplicationActivationOptions, NSRunningApplication};

use super::{WheelDir, RAISE_POLL, RAISE_TIMEOUT};
use clowd_rust_core::geometry::{RectExt, ScreenPoint, ScreenRect};

/// Lines of scrolling per notch, and *line* units rather than pixel ones.
///
/// macOS has no `WHEEL_DELTA`: a scroll event carries a line or a pixel count
/// directly, and both are delivered verbatim — measured against TextEdit, a
/// three-line event moves exactly three lines and a 40 px event exactly 40 px,
/// with no acceleration applied to either. So the choice is not about
/// distance, it is about what kind of device we are imitating.
///
/// A line event is a wheel detent (`IsContinuous == 0`), which is what the
/// Windows half sends and what the settle loop and the end detection are
/// tuned for. Pixel events are a trackbad's precise deltas
/// (`IsContinuous == 1`), and apps treat those as a gesture: Chromium and
/// AppKit rubber-band at the ends of a document, and a bounce is movement
/// that is not progress — precisely the signal "the document has ended" is
/// read from. Three lines is one detent's worth on this platform, so a notch
/// here and a notch on Windows move about the same distance.
///
/// How far three lines actually is remains the target's business (a zoomed
/// page, a large font, a list of thumbnails), which is why nothing here
/// assumes a distance and `drive::adapt_ticks` steers from what it measured.
const LINES_PER_NOTCH: i32 = 3;

/// Virtual key code for Escape (`kVK_Escape`). Carbon's `Events.h` numbering,
/// which is what `CGEventSourceKeyState` takes.
const KEY_CODE_ESCAPE: u16 = 53;

/// The window the driver is scrolling: a `CGWindowID` and the pid that owns
/// it. The pid is carried alongside because it is what does the work — it
/// names the application to bring forward, it is where the second rung posts
/// its events, and it is how an app's own panel over the scroll point is told
/// apart from a foreign window covering it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Target {
    id: CGWindowID,
    pid: i32,
}

/// One on-screen window, as far as this module cares.
#[derive(Debug, Clone, Copy)]
struct WindowEntry {
    id: CGWindowID,
    pid: i32,
    /// `kCGWindowBounds`, in CG points.
    rect: ScreenRect,
}

impl WindowEntry {
    fn target(&self) -> Target {
        Target {
            id: self.id,
            pid: self.pid,
        }
    }
}

/// Refuse the run when the OS will silently drop what the driver is about to
/// do.
///
/// Both permissions are handed to a process at launch and cannot be picked up
/// while it runs, so there is nothing to retry and no prompt worth showing
/// from a background helper: the honest move is to say which permission is
/// missing and stop. The message travels to the user as the `fatal_error`
/// event's text, so it is written for them and not for a log.
pub fn preflight() -> anyhow::Result<()> {
    if !ScreenCaptureAccess.preflight() {
        bail!(
            "Clowd needs Screen Recording permission to photograph the page while it scrolls. Enable Clowd under Privacy & \
             Security → Screen & System Audio Recording, then restart Clowd."
        );
    }
    // Without Accessibility the window server drops every event we post and
    // the run would end as a single screenshot with no reason attached.
    if !unsafe { AXIsProcessTrusted() } {
        bail!(
            "Clowd needs Accessibility permission to scroll the window for you. Enable Clowd under Privacy & Security → \
             Accessibility, then restart Clowd."
        );
    }
    Ok(())
}

/// Settle on the window the wheel should be aimed at.
///
/// The marker `window_id` wins. It names the window the *user* picked out of
/// the overlay's frozen desktop — the one whose region they selected — and
/// that intent is the only thing that survives the overlay closing. The live
/// answer cannot stand in for it: whatever is topmost at the point right now
/// may be a window sitting *over* the target, and scrolling that one
/// photographs the obstruction for the whole run.
///
/// The marker is validated first (the window may have closed since the
/// overlay ran, and it may have moved off the point), and the topmost live
/// window stands in when it does not hold up. Whichever wins,
/// [`raise_over_point`] then has to get it on top of the point before a
/// single frame is captured.
pub fn resolve_target(window_id: i64, point: ScreenPoint) -> Option<Target> {
    let windows = on_screen_windows();
    let live = live_at(&windows, point);
    let marker = validated_marker(&windows, window_id, point);
    if let (Some(live), Some(marker)) = (live, marker) {
        if live != marker {
            info!(
                "the window at the scroll point right now is {}, but the user picked window {window_id}; raising theirs",
                live.id
            );
        }
    }
    choose_target(live, marker)
}

/// The user's marker wins whenever it is still valid; the live window only
/// stands in when the marker is missing or no longer holds up.
fn choose_target(live: Option<Target>, marker: Option<Target>) -> Option<Target> {
    marker.or(live)
}

/// Topmost window under `point` as of right now, or `None` over bare desktop.
/// `CGWindowListCopyWindowInfo` returns front-to-back Z-order, so the first
/// hit is the one a click would reach.
fn live_at(windows: &[WindowEntry], point: ScreenPoint) -> Option<Target> {
    windows
        .iter()
        .find(|w| w.rect.contains(point))
        .map(WindowEntry::target)
}

/// The overlay's marker window as a [`Target`], or `None` when it does not
/// hold up any more (or was never resolved).
fn validated_marker(windows: &[WindowEntry], window_id: i64, point: ScreenPoint) -> Option<Target> {
    if window_id <= 0 || window_id > u32::MAX as i64 {
        // 0 is "the overlay could not resolve one"; anything outside the
        // CGWindowID range is a caller bug and gets the same treatment.
        if window_id != 0 {
            warn!("marker window id {window_id} is not a CGWindowID");
        }
        return None;
    }
    let id = window_id as CGWindowID;
    let Some(entry) = windows.iter().find(|w| w.id == id) else {
        warn!("marker window {id} is no longer on screen");
        return None;
    };
    if !entry.rect.contains(point) {
        warn!("marker window {id} no longer covers the scroll point");
        return None;
    }
    Some(entry.target())
}

/// Get the target on top of the scroll point, and report whether it worked.
///
/// Two things depend on this, and the second is the one that bites. Wheel
/// routing is positional, so a window covering the point eats the scroll; and
/// the capture is a screenshot of the region, so a window covering the point
/// is *also* what gets photographed. A target that cannot be raised produces
/// a tall picture of the wrong window with nothing about it looking wrong,
/// which is why the caller treats a `false` here as fatal rather than
/// pressing on.
///
/// One rung, and one shortcut in front of it. The shortcut is the common
/// case: the user picked the point off a frozen screenshot of their desktop,
/// so the window they aimed at is usually already the one at that point, and
/// there is nothing to do — taking their focus away to prove it would be
/// gratuitous.
///
/// Otherwise the app that owns the window is asked to activate, with
/// `ActivateAllWindows` so a target that is not the app's key window comes up
/// with the rest. There is deliberately no second rung: raising one specific
/// window of a foreign app means driving its accessibility hierarchy
/// (`AXUIElementPerformAction(kAXRaiseAction)`), which is a great deal of
/// guesswork about which `AXWindow` is the one we photographed, and macOS
/// gives no equivalent of `SetWindowPos(HWND_TOP)` for another process's
/// windows. Since macOS 14 the system may also refuse a cross-application
/// activation outright, which is exactly the case the verification below is
/// here to catch.
///
/// Verification asks the same question the wheel and the capture will: who is
/// at the point? A window belonging to the same application counts — a
/// sheet, a popover or a panel of the app we are scrolling is part of it.
pub fn raise_over_point(target: Target, point: ScreenPoint) -> bool {
    if owns_point(target, point) {
        return true;
    }

    match NSRunningApplication::runningApplicationWithProcessIdentifier(target.pid) {
        Some(app) => {
            if !app.activateWithOptions(NSApplicationActivationOptions::ActivateAllWindows) {
                warn!("NSRunningApplication.activate was refused for pid {}", target.pid);
            }
        }
        None => warn!("no running application for pid {}; cannot raise the target", target.pid),
    }

    wait_until_owns_point(target, point, RAISE_TIMEOUT)
}

/// Poll [`owns_point`] until it holds or `timeout` runs out.
fn wait_until_owns_point(target: Target, point: ScreenPoint, timeout: std::time::Duration) -> bool {
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

/// Is `target` (or another window of the same application) the window a click
/// at `point` would hit? The pid comparison is the counterpart of Windows'
/// `GA_ROOTOWNER` check: an app's own sheet or popover is not a foreign
/// window covering the target.
pub fn owns_point(target: Target, point: ScreenPoint) -> bool {
    match live_at(&on_screen_windows(), point) {
        Some(under) => under.id == target.id || under.pid == target.pid,
        None => false,
    }
}

/// The window currently at `point`, for logging which obstruction won.
pub fn describe_window_at(point: ScreenPoint) -> String {
    match live_at(&on_screen_windows(), point) {
        Some(t) => format!("{} (pid {})", t.id, t.pid),
        None => "nothing".to_string(),
    }
}

/// Is this window still on screen? A window that has closed — or been
/// minimized, or sent to another Space, neither of which can be photographed
/// either — drops out of the on-screen list, and the driver stops.
pub fn is_window(target: Target) -> bool {
    on_screen_windows()
        .iter()
        .any(|w| w.id == target.id)
}

/// `kCGWindowBounds` as a `ScreenRect`. The driver samples this once at the
/// start and compares on every step: a window that has moved or resized has
/// invalidated the fixed region we are photographing.
pub fn window_rect(target: Target) -> Option<ScreenRect> {
    on_screen_windows()
        .iter()
        .find(|w| w.id == target.id)
        .map(|w| w.rect)
}

/// Move the real cursor to the scroll point. Called before the run, and again
/// only when a pause ends — between those, every reading of the cursor is a
/// question about what the *user* is doing, and re-parking it would erase the
/// answer.
pub fn park_cursor(point: ScreenPoint) {
    if let Err(e) = CGDisplay::warp_mouse_cursor_position(as_cg_point(point)) {
        warn!(
            "CGWarpMouseCursorPosition({}, {}) failed: {e:?}; wheel events may land elsewhere",
            point.x, point.y
        );
    }
    // A warp moves the cursor without moving the mouse, and the two can be
    // left uncoupled: re-associating is what makes the user's next physical
    // movement continue from where the cursor now is instead of snapping back
    // to where their hand left it — which the driver would then read as drift
    // and pause over. Same call, same reason, as the overlay's own
    // `mac_mouse::set_position`.
    unsafe {
        CGAssociateMouseAndMouseCursorPosition(1);
    }
}

/// Where the cursor is now, or `None` if the window server will not say.
/// Callers treat that as "no news", never as "it moved".
pub fn cursor_pos() -> Option<ScreenPoint> {
    let event = CGEvent::new(event_source()?).ok()?;
    let pt = event.location();
    Some(ScreenPoint::new(pt.x.round() as i32, pt.y.round() as i32))
}

/// Is Escape physically down right now?
///
/// The combined session state is the same thing the front application sees,
/// which is the honest question to ask: Esc belongs to the *target*, not to
/// us, and this driver has no window and no event loop to receive a key
/// through. A `false` from a system that declines to answer is the safe
/// reading — the HUD's FINISH button and the automatic end detection both
/// still end the run.
pub fn escape_pressed() -> bool {
    unsafe { CGEventSourceKeyState(CGEventSourceStateID::CombinedSessionState as i32, KEY_CODE_ESCAPE) }
}

/// Inject `ticks` wheel notches at `point`.
///
/// The cursor is already parked there ([`park_cursor`]) because that is what
/// decides which pane scrolls; the location is stamped onto every event as
/// well, so nothing depends on the window server and this driver agreeing
/// about where the cursor is at the instant of the post.
///
/// One event per notch rather than one carrying `n * LINES_PER_NOTCH`: that
/// is the stream a real wheel produces, and apps that quantize per event —
/// or start one smooth-scroll animation per event — behave the same way for
/// us as for a person.
pub fn wheel_burst(point: ScreenPoint, ticks: u32, dir: WheelDir) {
    post_notches(point, ticks, dir, None);
}

/// Post `ticks` notches straight to the process that owns the target, which
/// needs neither the cursor nor the foreground. Returns false when nothing
/// could be posted.
pub fn wheel_message(target: Target, point: ScreenPoint, ticks: u32) -> bool {
    post_notches(point, ticks, WheelDir::Down, Some(target.pid))
}

/// The shared body of both rungs: `ticks` scroll events at `point`, either
/// posted to the HID tap (where the window server routes them like a real
/// wheel) or straight into one process's queue.
fn post_notches(point: ScreenPoint, ticks: u32, dir: WheelDir, to_pid: Option<i32>) -> bool {
    let Some(source) = event_source() else {
        warn!("CGEventSourceCreate failed; no wheel events could be posted");
        return false;
    };
    let lines = dir.sign() * LINES_PER_NOTCH;
    let location = as_cg_point(point);

    for _ in 0..ticks.max(1) {
        // wheel1 is the vertical axis; the other two (horizontal, Z) stay 0.
        let event = match CGEvent::new_scroll_event(source.clone(), ScrollEventUnit::LINE, 1, lines, 0, 0) {
            Ok(event) => event,
            Err(()) => {
                warn!("CGEventCreateScrollWheelEvent2 failed; the target will not have moved");
                return false;
            }
        };
        event.set_location(location);
        match to_pid {
            Some(pid) => event.post_to_pid(pid),
            None => event.post(CGEventTapLocation::HID),
        }
    }
    true
}

/// An event source for the events we create and the state we read. Failure
/// is only plausible under resource exhaustion, and every caller degrades
/// rather than dying.
fn event_source() -> Option<CGEventSource> {
    CGEventSource::new(CGEventSourceStateID::CombinedSessionState).ok()
}

/// Every visible, normal, on-screen window in front-to-back Z-order.
///
/// Re-walked per question rather than cached: the whole point of the checks
/// built on it is to notice that something moved, closed or came forward
/// since the last step. It costs a millisecond or so beside a settle loop
/// that photographs the region twenty times a second.
///
/// The filter is the overlay walker's (`mac_walker::evaluate_window`): layer
/// 0 and non-zero alpha, desktop elements excluded. Agreeing with it matters
/// — the marker id this module validates was resolved by exactly that walk,
/// so a window it considers real and this one does not would look to the
/// driver like a window that had closed.
fn on_screen_windows() -> Vec<WindowEntry> {
    let options = kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements;
    let Some(list) = window::copy_window_info(options, kCGNullWindowID) else {
        warn!("CGWindowListCopyWindowInfo returned null");
        return Vec::new();
    };

    list.get_all_values()
        .into_iter()
        .filter_map(|ptr| {
            let dict: CFDictionary = unsafe { TCFType::wrap_under_get_rule(ptr as CFDictionaryRef) };
            window_entry(&dict)
        })
        .collect()
}

/// One `CGWindowListCopyWindowInfo` dictionary as a [`WindowEntry`], or
/// `None` for anything that is not a normal visible window.
fn window_entry(dict: &CFDictionary) -> Option<WindowEntry> {
    let id = number_i64(dict, unsafe { window::kCGWindowNumber })? as CGWindowID;
    if number_i64(dict, unsafe { window::kCGWindowLayer })? != 0 {
        return None;
    }
    if number_f64(dict, unsafe { window::kCGWindowAlpha })? <= 0.0 {
        return None;
    }
    let pid = number_i64(dict, unsafe { window::kCGWindowOwnerPID })? as i32;

    let bounds_ptr = raw_value(dict, unsafe { window::kCGWindowBounds })?;
    let bounds_dict: CFDictionary = unsafe { TCFType::wrap_under_get_rule(bounds_ptr as CFDictionaryRef) };
    let cg_rect = CGRect::from_dict_representation(&bounds_dict)?;

    Some(WindowEntry {
        id,
        pid,
        rect: rect_from_cg(cg_rect),
    })
}

/// A `CGRect` of CG points as the integer rect the rest of the driver uses.
/// Rounded rather than truncated, and by edge rather than by size, so a
/// window at a fractional position keeps both of its edges where they are —
/// this rect is compared for equality on every step to detect a window that
/// moved.
fn rect_from_cg(rect: CGRect) -> ScreenRect {
    ScreenRect::from_exact(
        rect.origin.x.round() as i32,
        rect.origin.y.round() as i32,
        (rect.origin.x + rect.size.width).round() as i32,
        (rect.origin.y + rect.size.height).round() as i32,
    )
}

fn as_cg_point(point: ScreenPoint) -> CGPoint {
    CGPoint::new(point.x as f64, point.y as f64)
}

/// Get a raw value out of a CFDictionary by `CFStringRef` key. Same helper as
/// `mac_walker`'s; the window list is a dictionary-of-anything and there is
/// no typed accessor for it.
fn raw_value(dict: &CFDictionary, key: CFStringRef) -> Option<*const c_void> {
    let key_cfstr: CFString = unsafe { TCFType::wrap_under_get_rule(key) };
    unsafe {
        let mut value: *const c_void = std::ptr::null();
        if core_foundation::dictionary::CFDictionaryGetValueIfPresent(
            dict.as_concrete_TypeRef(),
            key_cfstr.as_concrete_TypeRef() as *const c_void,
            &mut value,
        ) != 0
        {
            Some(value)
        } else {
            None
        }
    }
}

fn number_i64(dict: &CFDictionary, key: CFStringRef) -> Option<i64> {
    let ptr = raw_value(dict, key)?;
    let number: CFNumber = unsafe { TCFType::wrap_under_get_rule(ptr as CFNumberRef) };
    number.to_i64()
}

fn number_f64(dict: &CFDictionary, key: CFStringRef) -> Option<f64> {
    let ptr = raw_value(dict, key)?;
    let number: CFNumber = unsafe { TCFType::wrap_under_get_rule(ptr as CFNumberRef) };
    number.to_f64()
}

#[link(name = "ApplicationServices", kind = "framework")]
extern "C" {
    /// Whether this process may drive other applications — the Accessibility
    /// permission. Without it `CGEventPost` is a no-op (see [`preflight`]).
    fn AXIsProcessTrusted() -> bool;
}

#[link(name = "CoreGraphics", kind = "framework")]
extern "C" {
    /// Level-read of one key's state. Not bound by the `core-graphics` crate.
    fn CGEventSourceKeyState(state: i32, key: u16) -> bool;

    /// Re-couples the hardware mouse to the cursor after a warp. Declared
    /// here for the same reason the overlay's `mac_mouse` declares it: the
    /// crate binds the warp but not its companion.
    fn CGAssociateMouseAndMouseCursorPosition(connected: i32) -> i32;
}

#[cfg(test)]
mod tests {
    use super::*;

    fn target(id: CGWindowID, pid: i32) -> Target {
        Target {
            id,
            pid,
        }
    }

    fn entry(id: CGWindowID, pid: i32, rect: ScreenRect) -> WindowEntry {
        WindowEntry {
            id,
            pid,
            rect,
        }
    }

    #[test]
    fn the_marker_outranks_the_live_window() {
        // The marker is the window the user picked in the overlay. Whatever
        // is topmost at the point *now* may be sitting over it, and both the
        // wheel and the capture would go to that one — so the user's choice
        // wins and gets raised.
        assert_eq!(choose_target(Some(target(1, 10)), Some(target(2, 20))), Some(target(2, 20)));
        assert_eq!(choose_target(Some(target(1, 10)), Some(target(1, 10))), Some(target(1, 10)));
        assert_eq!(choose_target(None, Some(target(2, 20))), Some(target(2, 20)));
        // The live window still stands in when the overlay never resolved a
        // marker, or the one it resolved no longer holds up.
        assert_eq!(choose_target(Some(target(1, 10)), None), Some(target(1, 10)));
        assert_eq!(choose_target(None, None), None);
    }

    #[test]
    fn the_topmost_window_containing_the_point_wins() {
        // Front-to-back order, and the front window overlaps the back one:
        // a click at the shared point reaches the front one, so that is what
        // the wheel and the capture will see.
        let windows = [
            entry(1, 10, ScreenRect::from_xy_size(0, 0, 100, 100)),
            entry(2, 20, ScreenRect::from_xy_size(50, 50, 200, 200)),
        ];
        assert_eq!(live_at(&windows, ScreenPoint::new(60, 60)), Some(target(1, 10)));
        assert_eq!(live_at(&windows, ScreenPoint::new(150, 150)), Some(target(2, 20)));
        assert_eq!(live_at(&windows, ScreenPoint::new(500, 500)), None);
    }

    #[test]
    fn a_marker_is_only_honored_while_it_still_covers_the_point() {
        let windows = [entry(7, 70, ScreenRect::from_xy_size(0, 0, 100, 100))];
        assert_eq!(validated_marker(&windows, 7, ScreenPoint::new(10, 10)), Some(target(7, 70)));
        // Moved off the point since the overlay ran.
        assert_eq!(validated_marker(&windows, 7, ScreenPoint::new(400, 400)), None);
        // Gone from the on-screen list entirely.
        assert_eq!(validated_marker(&windows, 8, ScreenPoint::new(10, 10)), None);
        // "The overlay could not resolve one", and out-of-range junk.
        assert_eq!(validated_marker(&windows, 0, ScreenPoint::new(10, 10)), None);
        assert_eq!(validated_marker(&windows, -1, ScreenPoint::new(10, 10)), None);
        assert_eq!(validated_marker(&windows, i64::from(u32::MAX) + 1, ScreenPoint::new(10, 10)), None);
    }

    #[test]
    fn window_bounds_round_to_whole_points_by_edge() {
        // A window at a fractional origin must not lose or gain a point of
        // width, or `stop_requested` reads it as having been resized on the
        // very first step.
        let rect = rect_from_cg(CGRect::new(
            &CGPoint::new(10.5, 20.4),
            &core_graphics::geometry::CGSize::new(100.0, 200.0),
        ));
        assert_eq!(rect, ScreenRect::from_xy_size(11, 20, 100, 200));
    }
}
