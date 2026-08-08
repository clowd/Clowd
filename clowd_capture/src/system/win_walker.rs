//! Window walker — enumerates visible top-level windows and walks the child
//! hierarchy to find the best capture-target rectangle under a given point.
//!
//! Port of `clowd_capture_dx/WindowWalker.cpp`.

#![allow(dead_code)]

use std::mem;

use windows::{
    core::PCWSTR,
    Win32::{
        Foundation::{HWND, LPARAM, POINT, RECT, TRUE},
        Graphics::{
            Dwm::{DwmGetWindowAttribute, DWMWA_CLOAKED, DWMWA_EXTENDED_FRAME_BOUNDS},
            Gdi::{ClientToScreen, GetMonitorInfoW, MonitorFromWindow, MONITORINFO, MONITOR_DEFAULTTONEAREST},
        },
        System::Com::{CoCreateInstance, CLSCTX_ALL},
        UI::{
            Shell::{IVirtualDesktopManager, VirtualDesktopManager},
            WindowsAndMessaging::{
                EnumWindows, FindWindowExW, GetClassNameW, GetClientRect, GetForegroundWindow, GetWindowLongPtrW, GetWindowRect,
                GetWindowTextW, IsIconic, IsWindowVisible, IsZoomed, RealChildWindowFromPoint, GWL_EXSTYLE, GWL_STYLE, WS_CAPTION,
                WS_EX_LAYERED, WS_EX_TOOLWINDOW, WS_VISIBLE,
            },
        },
    },
};

use super::{HitTestResult, ObstructedWindow, WindowCaptureRef};
use clowd_rust_core::geometry::{RectExt, ScreenPoint, ScreenRect};

/// Minimum top-level window dimension (px) to be considered capturable.
const MIN_WINDOW_SIZE: i32 = 25;

/// Minimum child window dimension (px) during hit-test child walk.
const MIN_WINDOW_CHILD_SIZE: i32 = 200;

/// If a child rect is within this many pixels of its parent on all four
/// edges, treat it as "same as parent" and keep walking deeper.
const MERGE_WITH_PARENT_THRESHOLD: i32 = 60;

/// Maximum child-hierarchy depth to walk in `hit_test`.
const MAX_CHILD_DEPTH: usize = 10;

/// Window classes that are never valid capture targets. Sorted for binary
/// search (ASCII case-sensitive).
const BLACKLISTED_CLASSES: &[&str] = &[
    "ApplicationManager_ImmersiveShellWindow",
    "EdgeUiInputWndClass",
    "Immersive Chrome Container",
    "ImmersiveBackgroundWindow",
    "ImmersiveLauncher",
    "LauncherTipWndClass",
    "MetroGhostWindow",
    "ModeInputWnd",
    "NativeHWNDHost",
    "Progman",
    "SearchPane",
    "Shell_Dim",
    "Shell_Dialog",
    "Shell_TrayWnd",
    "Snapped Desktop",
    "TaskListThumbnailWnd",
    "Touch Tooltip Window",
    "Windows.UI.Core.CoreWindow",
    "WorkerW",
];

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
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// Snapshot of the top-level window list in Z-order. Created once at capture
/// startup; queried per cursor-move via [`hit_test`].
pub struct WindowWalker {
    windows: Vec<WindowEntry>,
    /// True bounds of the foreground window at snapshot time, if it survived
    /// the enumeration filters. Used by `--capture-mode window` to pre-select
    /// the active window (see [`foreground_capture_rect`]).
    foreground_rect: Option<ScreenRect>,
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
    pub fn snapshot(_monitors: &[super::MonitorInfo], visibility_threshold: f32) -> Self {
        // Grab the foreground window first — before overlay windows exist —
        // so `--capture-mode window` can pre-select the truly active window
        // regardless of where the cursor is. Matched to an enumerated entry
        // below; a foreground window that fails the filters yields None.
        let foreground_hwnd = unsafe { GetForegroundWindow() };

        // COM is per-thread; this may run on a background thread.
        unsafe {
            use windows::Win32::System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED};
            let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        }

        let vdm: Option<IVirtualDesktopManager> = unsafe {
            match CoCreateInstance(&VirtualDesktopManager, None, CLSCTX_ALL) {
                Ok(v) => Some(v),
                Err(e) => {
                    warn!(
                        "Could not create IVirtualDesktopManager (0x{:08X}: {}) \
                         — virtual-desktop filtering disabled",
                        e.code().0,
                        e
                    );
                    None
                }
            }
        };

        // Collect raw HWNDs via EnumWindows (front-to-back Z-order).
        let mut hwnds: Vec<HWND> = Vec::new();
        unsafe {
            let _ = EnumWindows(Some(enum_windows_cb), LPARAM(&mut hwnds as *mut Vec<HWND> as isize));
        }

        let mut windows: Vec<WindowEntry> = Vec::new();

        for hwnd in hwnds {
            if let Some(entry) = evaluate_window(hwnd, &vdm, &windows, visibility_threshold) {
                windows.push(entry);
            }
        }

        // Resolve the foreground window to a capture rect. Only accept it if it
        // survived enumeration (a normal, on-screen app window); otherwise leave
        // it None so the caller can fall back to the active screen.
        let foreground_rect = if foreground_hwnd.0.is_null() {
            None
        } else {
            windows
                .iter()
                .find(|w| w.hwnd == foreground_hwnd)
                .map(|w| w.rect)
        };

        info!("WindowWalker: captured {} top-level windows", windows.len());
        WindowWalker {
            windows,
            foreground_rect,
        }
    }

    /// Capture rect for `--capture-mode window`: the true bounds of the window
    /// that was in the foreground when the capture began, or `None` if that
    /// window is not a valid capture target (the caller should fall back to the
    /// active screen).
    pub fn foreground_capture_rect(&self) -> Option<ScreenRect> {
        self.foreground_rect
    }

    /// Given a cursor position in virtual-desktop physical pixels, return the
    /// suggested capture rectangle — the deepest meaningful child window
    /// region under the cursor, clipped to the top-level window bounds.
    ///
    /// Returns `None` if the cursor is over the desktop background (no
    /// captured window contains the point).
    pub fn hit_test(&self, point: ScreenPoint) -> Option<ScreenRect> {
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
            Some(clipped)
        } else {
            None
        }
    }

    /// Handle of the top-level window [`hit_test`] would pick for `point`
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

    /// Same as [`hit_test`] but also returns the title of the top-level window
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
// EnumWindows callback
// ---------------------------------------------------------------------------

extern "system" fn enum_windows_cb(hwnd: HWND, lparam: LPARAM) -> windows::core::BOOL {
    unsafe {
        let hwnds = &mut *(lparam.0 as *mut Vec<HWND>);
        hwnds.push(hwnd);
    }
    TRUE
}

// ---------------------------------------------------------------------------
// Per-window evaluation (the filter pipeline)
// ---------------------------------------------------------------------------

/// Run the full filter pipeline on a single HWND. Returns `Some(WindowEntry)`
/// if the window passes all checks, `None` otherwise.
fn evaluate_window(
    hwnd: HWND,
    vdm: &Option<IVirtualDesktopManager>,
    accepted: &[WindowEntry],
    visibility_threshold: f32,
) -> Option<WindowEntry> {
    unsafe {
        // 1. Basic visibility.
        if !IsWindowVisible(hwnd).as_bool() {
            return None;
        }

        // 2. Minimized.
        if IsIconic(hwnd).as_bool() {
            return None;
        }

        // 3. Style checks.
        let style = GetWindowLongPtrW(hwnd, GWL_STYLE) as u32;
        let ex_style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32;

        if (style & WS_VISIBLE.0) == 0 {
            return None;
        }
        // Transparent overlay: layered without caption.
        if (style & WS_CAPTION.0) == 0 && (ex_style & WS_EX_LAYERED.0) != 0 {
            return None;
        }

        // 4. Zero-size.
        let mut rc = RECT::default();
        GetWindowRect(hwnd, &mut rc).ok()?;
        if rc.right <= rc.left || rc.bottom <= rc.top {
            return None;
        }
        let raw_rect = ScreenRect::from_exact(rc.left, rc.top, rc.right, rc.bottom);

        // 5. Virtual desktop.
        if let Some(ref vdm) = vdm {
            match vdm.IsWindowOnCurrentVirtualDesktop(hwnd) {
                Ok(on_current) if !on_current.as_bool() => return None,
                Err(_) => {} // Assume current desktop on error.
                _ => {}
            }
        }

        // 6. DWM cloaked.
        if is_cloaked(hwnd) {
            return None;
        }

        // 7. True bounds.
        let rect = get_true_bounds(hwnd)?;

        // 8. Size threshold.
        if rect.width() < MIN_WINDOW_SIZE || rect.height() < MIN_WINDOW_SIZE {
            return None;
        }

        // 9. Class blacklist.
        let class_name = get_class_name(hwnd);
        if BLACKLISTED_CLASSES
            .binary_search(&class_name.as_str())
            .is_ok()
        {
            return None;
        }

        // 10. Empty title.
        if get_window_text(hwnd).is_empty() {
            return None;
        }

        // 11. Phantom ApplicationFrameWindow.
        if class_name == "ApplicationFrameWindow" && !has_core_window_child(hwnd) {
            return None;
        }

        // 12–13. Compute obstruction rects and visible fraction.
        // Drop windows whose visible area falls below the threshold.
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

        let title = get_window_text(hwnd);
        Some(WindowEntry {
            hwnd,
            rect,
            raw_rect,
            title,
            obstructed,
            obstruction_rects,
        })
    }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

fn get_class_name(hwnd: HWND) -> String {
    let mut buf = [0u16; 256];
    let len = unsafe { GetClassNameW(hwnd, &mut buf) };
    String::from_utf16_lossy(&buf[..len as usize])
}

fn get_window_text(hwnd: HWND) -> String {
    let mut buf = [0u16; 512];
    let len = unsafe { GetWindowTextW(hwnd, &mut buf) };
    String::from_utf16_lossy(&buf[..len as usize])
}

fn is_cloaked(hwnd: HWND) -> bool {
    let mut cloaked: u32 = 0;
    let hr = unsafe {
        DwmGetWindowAttribute(
            hwnd,
            DWMWA_CLOAKED,
            &mut cloaked as *mut u32 as *mut _,
            mem::size_of::<u32>() as u32,
        )
    };
    hr.is_ok() && cloaked != 0
}

/// Compute the actual visible bounds for a window. Maximized windows get the
/// monitor's work area; others get the DWM extended frame bounds (which
/// exclude the invisible resize border / drop shadow), falling back to
/// `GetWindowRect`.
fn get_true_bounds(hwnd: HWND) -> Option<ScreenRect> {
    unsafe {
        if IsZoomed(hwnd).as_bool() {
            let hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            let mut mi = MONITORINFO {
                cbSize: mem::size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            if GetMonitorInfoW(hmon, &mut mi).as_bool() {
                let rc = mi.rcWork;
                return Some(ScreenRect::from_exact(rc.left, rc.top, rc.right, rc.bottom));
            }
        }

        // Try DWM extended frame bounds first.
        let mut rc = RECT::default();
        let hr = DwmGetWindowAttribute(
            hwnd,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            &mut rc as *mut RECT as *mut _,
            mem::size_of::<RECT>() as u32,
        );
        if hr.is_err() {
            // Fallback to regular window rect.
            GetWindowRect(hwnd, &mut rc).ok()?;
        }

        let sr = ScreenRect::from_exact(rc.left, rc.top, rc.right, rc.bottom);
        if sr.width() > 0 && sr.height() > 0 {
            Some(sr)
        } else {
            None
        }
    }
}

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

/// Check whether an `ApplicationFrameWindow` has a real `CoreWindow` child
/// (meaning it's a live UWP app, not a phantom frame).
fn has_core_window_child(hwnd: HWND) -> bool {
    unsafe {
        FindWindowExW(
            Some(hwnd),
            None,
            PCWSTR::from_raw(windows::core::w!("Windows.UI.Core.CoreWindow").as_ptr()),
            PCWSTR::null(),
        )
        .is_ok()
    }
}
