//! Window walker — enumerates visible top-level windows and walks the child
//! hierarchy to find the best capture-target rectangle under a given point.
//!
//! Port of the retired C++ capturer's `WindowWalker.cpp`.

#![allow(dead_code)]

use windows::Win32::{
    Foundation::{HWND, POINT, RECT},
    Graphics::Gdi::ClientToScreen,
    UI::WindowsAndMessaging::{
        GetClientRect, GetForegroundWindow, GetWindowLongPtrW, RealChildWindowFromPoint, GWL_EXSTYLE, WS_EX_TOOLWINDOW,
    },
};

use super::{HitTestResult, MonitorInfo, ObstructedWindow, WindowCaptureRef, WindowTarget};
use prisc_rust_core::desktop::{self, WindowFilter};
use prisc_rust_core::geometry::{RectExt, ScreenPoint, ScreenRect};

/// Minimum child window dimension (px) during hit-test child walk.
const MIN_WINDOW_CHILD_SIZE: i32 = 200;

/// If a child rect is within this many pixels of its parent on all four
/// edges, treat it as "same as parent" and keep walking deeper.
const MERGE_WITH_PARENT_THRESHOLD: i32 = 60;

/// Maximum child-hierarchy depth to walk in `hit_test`.
const MAX_CHILD_DEPTH: usize = 10;

// ---------------------------------------------------------------------------
// Internal types
// ---------------------------------------------------------------------------

/// Minimal per-window data retained after enumeration.
struct WindowEntry {
    hwnd: HWND,
    /// True visual bounds in system (virtual-desktop) coordinates.
    rect: ScreenRect,
    /// Raw GetWindowRect bounds (includes invisible resize border).
    /// Needed for PrintWindow which captures at these dimensions.
    raw_rect: ScreenRect,
    /// Window title text.
    title: String,
    /// Whether this window is partially covered by higher-Z windows.
    obstructed: bool,
    /// Regions of this window covered by higher-Z windows, in virtual-desktop coords.
    obstruction_rects: Vec<ScreenRect>,
    /// Corner radius DWM composites this window with, in physical px
    /// (0 = square; always 0 when the walker was built with rounded
    /// corners off). See `win_corners`.
    corner_radius: f32,
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// Snapshot of the top-level window list in Z-order. Created once at capture
/// startup; queried per cursor-move via [`hit_test_target`].
pub struct WindowWalker {
    windows: Vec<WindowEntry>,
    /// Index into `windows` of the foreground window at snapshot time, if it
    /// survived the enumeration filters. Used by `--capture-mode window` to
    /// pre-select the active window (see [`foreground_capture_target`]).
    foreground_index: Option<usize>,
}

// SAFETY: WindowWalker holds HWND values which are plain integer handles.
// They are safe to send/share across threads on Windows.
unsafe impl Send for WindowWalker {}
unsafe impl Sync for WindowWalker {}

impl WindowWalker {
    /// Enumerate all visible top-level windows on the current virtual desktop.
    ///
    /// Call once at capture startup — after the desktop bitmap is grabbed but
    /// before overlay windows are created, so our own windows are excluded.
    pub fn snapshot(_monitors: &[MonitorInfo], visibility_threshold: f32, rounded_corners: bool) -> Self {
        // Grab the foreground window first — before overlay windows exist —
        // so `--capture-mode window` can pre-select the truly active window
        // regardless of where the cursor is. Matched to an enumerated entry
        // below; a foreground window that fails the filters yields None.
        let foreground_hwnd = unsafe { GetForegroundWindow() };

        // Only the current virtual desktop: the overlay covers it alone.
        let filter = WindowFilter {
            current_virtual_desktop_only: true,
            exclude_own_process: false,
        };
        let mut windows: Vec<WindowEntry> = Vec::new();
        for window in desktop::top_level_windows(&filter) {
            if let Some(entry) = evaluate_window(window, &windows, visibility_threshold, rounded_corners) {
                windows.push(entry);
            }
        }

        // Resolve the foreground window to a capture rect. Only accept it if it
        // survived enumeration (a normal, on-screen app window); otherwise leave
        // it None so the caller can fall back to the active screen.
        let foreground_index = if foreground_hwnd.0.is_null() {
            None
        } else {
            windows
                .iter()
                .position(|w| w.hwnd == foreground_hwnd)
        };

        info!("WindowWalker: captured {} top-level windows", windows.len());
        WindowWalker {
            windows,
            foreground_index,
        }
    }

    /// Corner radii on Windows are resolved synchronously during
    /// [`snapshot`] (a handful of user32/DWM calls per window), so there is
    /// nothing to refine afterwards. Exists so the walker thread can call
    /// it on both platforms; macOS does the expensive window-server probe
    /// here.
    pub fn probe_corner_radii(&self, _monitors: &[MonitorInfo]) {}

    /// Capture rect for `--capture-mode window`: the true bounds of the window
    /// that was in the foreground when the capture began, or `None` if that
    /// window is not a valid capture target (the caller should fall back to the
    /// active screen).
    pub fn foreground_capture_target(&self) -> Option<WindowTarget> {
        let w = self.windows.get(self.foreground_index?)?;
        Some(WindowTarget {
            rect: w.rect,
            corner_radius: w.corner_radius,
        })
    }

    /// Given a cursor position in virtual-desktop physical pixels, return the
    /// suggested capture target — the deepest meaningful child window
    /// region under the cursor, clipped to the top-level window bounds —
    /// with the corner radius to draw and crop it with. The radius is the
    /// top-level window's only when the suggested rect IS the whole window;
    /// a child region (a browser viewport, a pane) has square corners
    /// regardless.
    ///
    /// Returns `None` if the cursor is over the desktop background (no
    /// captured window contains the point).
    pub fn hit_test_target(&self, point: ScreenPoint) -> Option<WindowTarget> {
        // Find the topmost (first in Z-order) window containing the point.
        let top = self
            .windows
            .iter()
            .find(|w| w.rect.contains(point))?;

        let top_rect = top.rect;
        let mut ideal_rect = top_rect;

        let sys_pt = POINT {
            x: point.x,
            y: point.y,
        };

        let mut current_hwnd = top.hwnd;
        let mut parent_rect = top_rect;
        let mut visible = true;

        for _ in 0..MAX_CHILD_DEPTH {
            // Convert the screen point to client coordinates of current window.
            let mut client_pt = sys_pt;
            unsafe {
                let _ = ClientToScreen(current_hwnd, &mut client_pt);
            }
            // ClientToScreen goes client→screen; we need screen→client, so
            // compute the offset and invert.
            let offset_x = client_pt.x - sys_pt.x;
            let offset_y = client_pt.y - sys_pt.y;
            let child_pt = POINT {
                x: sys_pt.x - offset_x,
                y: sys_pt.y - offset_y,
            };

            let child = unsafe { RealChildWindowFromPoint(current_hwnd, child_pt) };
            if child.0.is_null() || child == current_hwnd {
                break;
            }

            // Get child's client rect in screen coordinates.
            let Some(child_rect) = child_screen_rect(child) else {
                break;
            };

            // Evaluate visibility of this child.
            let ex_style = unsafe { GetWindowLongPtrW(child, GWL_EXSTYLE) } as u32;

            if !visible {
                // Inherited invisibility — parent was a toolbar or too small.
            } else if (ex_style & WS_EX_TOOLWINDOW.0) != 0 {
                visible = false; // floating toolbar
            } else if child_rect.width() < MIN_WINDOW_CHILD_SIZE || child_rect.height() < MIN_WINDOW_CHILD_SIZE {
                visible = false; // too small
            }

            // "Similar to parent" — child fills nearly the whole parent.
            // Don't update ideal but keep walking for a more specific child.
            let similar_to_parent = (child_rect.min_x() - parent_rect.min_x()).abs() < MERGE_WITH_PARENT_THRESHOLD
                && (child_rect.min_y() - parent_rect.min_y()).abs() < MERGE_WITH_PARENT_THRESHOLD
                && (child_rect.max_x() - parent_rect.max_x()).abs() < MERGE_WITH_PARENT_THRESHOLD
                && (child_rect.max_y() - parent_rect.max_y()).abs() < MERGE_WITH_PARENT_THRESHOLD;

            if visible && !similar_to_parent {
                ideal_rect = child_rect;
            }

            current_hwnd = child;
            parent_rect = child_rect;
        }

        // Clip to the top-level window bounds.
        let clipped = ideal_rect.intersection(&top_rect)?;
        if clipped.width() > 0 && clipped.height() > 0 {
            let corner_radius = if clipped == top_rect { top.corner_radius } else { 0.0 };
            Some(WindowTarget {
                rect: clipped,
                corner_radius,
            })
        } else {
            None
        }
    }

    /// Handle of the top-level window [`hit_test_target`] would pick for `point`
    /// — the first entry in Z-order whose true bounds contain it — as a
    /// plain integer, or `None` over the desktop background.
    ///
    /// Deliberately stops at the top level: consumers (the scrolling
    /// capture driver) resolve the child under the point themselves, live,
    /// once the overlay is gone and the Z-order is the real one again.
    pub fn top_level_hwnd_at(&self, point: ScreenPoint) -> Option<isize> {
        self.windows
            .iter()
            .find(|w| w.rect.contains(point))
            .map(|w| w.hwnd.0 as isize)
    }

    /// Handle of the window at `window_index` — the index
    /// [`hit_test_full`] reports and a [`PeekCommand`] carries.
    ///
    /// The scrolling capture needs it: a peeked window is by definition
    /// partly covered, so asking what is at the scroll point would name the
    /// window on top instead of the one the user selected.
    ///
    /// [`PeekCommand`]: crate::render::protocol::PeekCommand
    pub fn hwnd_at_index(&self, window_index: usize) -> Option<isize> {
        self.windows
            .get(window_index)
            .map(|w| w.hwnd.0 as isize)
    }

    /// Full hit-test returning window index and obstruction info.
    pub fn hit_test_full(&self, point: ScreenPoint) -> Option<HitTestResult> {
        let (idx, top) = self
            .windows
            .iter()
            .enumerate()
            .find(|(_, w)| w.rect.contains(point))?;

        Some(HitTestResult {
            rect: top.rect,
            title: top.title.clone(),
            window_index: idx,
            obstructed: top.obstructed,
        })
    }

    /// Return metadata for all obstructed windows (for PrintWindow capture).
    pub fn obstructed_windows(&self) -> Vec<ObstructedWindow> {
        self.windows
            .iter()
            .enumerate()
            .filter(|(_, w)| w.obstructed)
            .map(|(i, w)| ObstructedWindow {
                window_index: i,
                capture_ref: WindowCaptureRef::from_hwnd(w.hwnd),
                rect: w.rect,
                raw_rect: w.raw_rect,
                obstruction_rects: w.obstruction_rects.clone(),
            })
            .collect()
    }

    /// Same as [`hit_test_target`] but also returns the title of the top-level window
    /// that contains the point.
    pub fn hit_test_with_title(&self, point: ScreenPoint) -> Option<(ScreenRect, String)> {
        // Find the topmost (first in Z-order) window containing the point.
        let top = self
            .windows
            .iter()
            .find(|w| w.rect.contains(point))?;

        let top_rect = top.rect;
        let top_title = top.title.clone();
        let mut ideal_rect = top_rect;

        let sys_pt = POINT {
            x: point.x,
            y: point.y,
        };

        let mut current_hwnd = top.hwnd;
        let mut parent_rect = top_rect;
        let mut visible = true;

        for _ in 0..MAX_CHILD_DEPTH {
            // Convert the screen point to client coordinates of current window.
            let mut client_pt = sys_pt;
            unsafe {
                let _ = ClientToScreen(current_hwnd, &mut client_pt);
            }
            // ClientToScreen goes client->screen; we need screen->client, so
            // compute the offset and invert.
            let offset_x = client_pt.x - sys_pt.x;
            let offset_y = client_pt.y - sys_pt.y;
            let child_pt = POINT {
                x: sys_pt.x - offset_x,
                y: sys_pt.y - offset_y,
            };

            let child = unsafe { RealChildWindowFromPoint(current_hwnd, child_pt) };
            if child.0.is_null() || child == current_hwnd {
                break;
            }

            // Get child's client rect in screen coordinates.
            let Some(child_rect) = child_screen_rect(child) else {
                break;
            };

            // Evaluate visibility of this child.
            let ex_style = unsafe { GetWindowLongPtrW(child, GWL_EXSTYLE) } as u32;

            if !visible {
                // Inherited invisibility — parent was a toolbar or too small.
            } else if (ex_style & WS_EX_TOOLWINDOW.0) != 0 {
                visible = false; // floating toolbar
            } else if child_rect.width() < MIN_WINDOW_CHILD_SIZE || child_rect.height() < MIN_WINDOW_CHILD_SIZE {
                visible = false; // too small
            }

            // "Similar to parent" — child fills nearly the whole parent.
            // Don't update ideal but keep walking for a more specific child.
            let similar_to_parent = (child_rect.min_x() - parent_rect.min_x()).abs() < MERGE_WITH_PARENT_THRESHOLD
                && (child_rect.min_y() - parent_rect.min_y()).abs() < MERGE_WITH_PARENT_THRESHOLD
                && (child_rect.max_x() - parent_rect.max_x()).abs() < MERGE_WITH_PARENT_THRESHOLD
                && (child_rect.max_y() - parent_rect.max_y()).abs() < MERGE_WITH_PARENT_THRESHOLD;

            if visible && !similar_to_parent {
                ideal_rect = child_rect;
            }

            current_hwnd = child;
            parent_rect = child_rect;
        }

        // Clip to the top-level window bounds.
        let clipped = ideal_rect.intersection(&top_rect)?;
        if clipped.width() > 0 && clipped.height() > 0 {
            Some((clipped, top_title))
        } else {
            None
        }
    }
}

// ---------------------------------------------------------------------------
// Per-window evaluation
// ---------------------------------------------------------------------------

/// The overlay's own filter on top of the shared one: drop windows whose
/// visible area falls below the threshold, and record what covers the rest
/// for the peek feature. `accepted` is every window above this one in
/// Z-order that survived.
fn evaluate_window(
    window: desktop::TopLevelWindow,
    accepted: &[WindowEntry],
    visibility_threshold: f32,
    rounded_corners: bool,
) -> Option<WindowEntry> {
    let rect = window.rect;
    let mut obstruction_rects = Vec::new();
    let window_area = (rect.width() as i64) * (rect.height() as i64);
    let mut obstructed_area: i64 = 0;
    for w in accepted.iter() {
        if let Some(isect) = w.rect.intersection(&rect) {
            if isect.width() > 0 && isect.height() > 0 {
                obstructed_area += (isect.width() as i64) * (isect.height() as i64);
                if obstruction_rects.len() < 16 {
                    obstruction_rects.push(isect);
                }
            }
        }
    }
    if window_area > 0 {
        let obstructed_frac = obstructed_area as f64 / window_area as f64;
        if obstructed_frac > visibility_threshold as f64 {
            return None;
        }
    }
    let obstructed = !obstruction_rects.is_empty();

    // Last, once the window is known to be a capture target: a few more
    // user32/DWM round-trips per window, skipped entirely when the feature
    // is off.
    let corner_radius = if rounded_corners {
        super::win_corners::window_corner_radius(window.hwnd, window.style)
    } else {
        0.0
    };
    Some(WindowEntry {
        hwnd: window.hwnd,
        rect,
        raw_rect: window.raw_rect,
        title: window.title,
        obstructed,
        obstruction_rects,
        corner_radius,
    })
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Get a child window's client rect in screen coordinates.
fn child_screen_rect(hwnd: HWND) -> Option<ScreenRect> {
    unsafe {
        let mut rc = RECT::default();
        GetClientRect(hwnd, &mut rc).ok()?;

        // Convert client (0,0)-(w,h) to screen coordinates.
        let mut top_left = POINT {
            x: rc.left,
            y: rc.top,
        };
        let mut bottom_right = POINT {
            x: rc.right,
            y: rc.bottom,
        };
        let _ = ClientToScreen(hwnd, &mut top_left);
        let _ = ClientToScreen(hwnd, &mut bottom_right);

        let sr = ScreenRect::from_exact(top_left.x, top_left.y, bottom_right.x, bottom_right.y);
        if sr.width() > 0 && sr.height() > 0 {
            Some(sr)
        } else {
            None
        }
    }
}
