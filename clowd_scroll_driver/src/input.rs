//! Making the target window scroll, and noticing when the user wants it to
//! stop.
//!
//! The OS half of the driver. Everything here has a [`win`] and a [`mac`]
//! implementation of the same eight questions — who is at the scroll point,
//! get them in front of it, park the cursor, is the cursor still there, is
//! Esc down, scroll, is the target still the shape it was — and the [`drive`]
//! loop is written against that surface alone.
//!
//! [`drive`]: crate::drive
//!
//! ## Why the wheel, and why a parked cursor
//!
//! Both platforms take the same two rungs, in the order the driver tries
//! them:
//!
//! 1. **Synthetic wheel at a parked cursor** ([`wheel_burst`]). This is what
//!    every scrolling-capture tool ships, because it is the only method that
//!    works on everything that handles a physical wheel — Chromium,
//!    Electron, Firefox, Qt, WinForms, AppKit, custom-drawn — without asking
//!    the target to expose anything. Both platforms route wheel input by
//!    cursor position, so *where the cursor is* decides which pane scrolls.
//!    We park it on the point the user clicked and leave it there: the
//!    well-known failure of ShareX's version is that it injects wheel events
//!    wherever the mouse happens to be.
//! 2. **The same notches addressed to the target process instead of the
//!    screen** ([`wheel_message`]) — `WM_MOUSEWHEEL` on Windows,
//!    `CGEventPostToPid` on macOS. Needs neither foreground nor cursor, and
//!    gets through when injection does not. Not the default because plenty
//!    of frameworks ignore a wheel event that did not arrive through the
//!    normal path.
//!
//! Nothing here assumes how far one notch scrolls. A notch maps to a line
//! count only if the target says so, and browsers, editors and zoomed pages
//! all disagree — the driver measures the displacement it actually got and
//! adapts the burst size.
//!
//! ## Coordinates
//!
//! Every point and rect crossing this boundary is in the platform capture
//! space the marker used (`CAPTURE_PROTOCOL.md` §1.2): physical
//! virtual-desktop pixels on Windows, CG points on macOS. Both platforms'
//! cursor and window APIs speak exactly that space, which is why there is no
//! conversion anywhere in this module — but it is *not* the space a captured
//! [`Frame`] is measured in on a Retina display.
//!
//! [`Frame`]: crate::frame::Frame

use std::time::Duration;

use clowd_rust_core::geometry::ScreenPoint;

#[cfg(target_os = "macos")]
mod mac;
#[cfg(windows)]
mod win;

#[cfg(target_os = "macos")]
pub use mac::*;
#[cfg(windows)]
pub use win::*;

/// How long [`raise_over_point`] keeps re-checking after asking for the
/// target to come forward. A raise is asynchronous on both platforms — the
/// window has to process the activation and the compositor has to restack —
/// and an app restoring from a minimised or background state can take a beat
/// longer than one that was already visible.
pub(crate) const RAISE_TIMEOUT: Duration = Duration::from_millis(1_200);

/// How often [`raise_over_point`] re-asks who owns the scroll point.
pub(crate) const RAISE_POLL: Duration = Duration::from_millis(40);

/// Which way a burst moves the document.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WheelDir {
    /// Away from the user — further down the page. The capture direction.
    Down,
    /// Toward the user — back up the page. Used only by the rewind.
    Up,
}

impl WheelDir {
    /// The sign a notch's delta carries. Both platforms spell a scroll
    /// *down* the page as a negative wheel delta.
    pub(crate) fn sign(self) -> i32 {
        match self {
            WheelDir::Down => -1,
            WheelDir::Up => 1,
        }
    }
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn chebyshev_is_the_larger_axis() {
        assert_eq!(chebyshev(ScreenPoint::new(10, 10), ScreenPoint::new(10, 10)), 0);
        assert_eq!(chebyshev(ScreenPoint::new(10, 10), ScreenPoint::new(13, 11)), 3);
        assert_eq!(chebyshev(ScreenPoint::new(10, 10), ScreenPoint::new(9, 4)), 6);
        // Negative coordinates (a monitor left of or above the primary) are
        // just as valid a place to park the cursor.
        assert_eq!(chebyshev(ScreenPoint::new(-100, -50), ScreenPoint::new(-90, -50)), 10);
    }

    #[test]
    fn a_downward_burst_is_a_negative_delta_on_every_platform() {
        // The stitcher only ever composites downward movement, and both
        // backends derive their notch delta from this sign — a flip here
        // would rewind the document during the capture phase.
        assert_eq!(WheelDir::Down.sign(), -1);
        assert_eq!(WheelDir::Up.sign(), 1);
    }

    /// The raise loop has to be able to poll more than once inside its own
    /// budget, or a target that comes forward 100 ms late is reported as
    /// unraisable and the run refuses to start.
    #[test]
    fn the_raise_budget_leaves_room_to_poll() {
        assert!(RAISE_POLL * 4 < RAISE_TIMEOUT);
    }
}
