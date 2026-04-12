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
            Gdi::{
                ClientToScreen, GetMonitorInfoW, MonitorFromWindow, MONITORINFO,
                MONITOR_DEFAULTTONEAREST,
            },
        },
        System::Com::{CoCreateInstance, CLSCTX_ALL},
        UI::{
            Shell::{IVirtualDesktopManager, VirtualDesktopManager},
            WindowsAndMessaging::{
                EnumWindows, FindWindowExW, GetClassNameW, GetClientRect, GetWindowLongPtrW,
                GetWindowRect, GetWindowTextW, IsIconic, IsWindowVisible, IsZoomed,
                RealChildWindowFromPoint, GWL_EXSTYLE, GWL_STYLE, WS_CAPTION, WS_EX_LAYERED,
                WS_EX_TOOLWINDOW, WS_VISIBLE,
            },
        },
    },
};

use crate::geometry::{RectExt, ScreenPoint, ScreenRect};

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
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// Snapshot of the top-level window list in Z-order. Created once at capture
/// startup; queried per cursor-move via [`hit_test`].
pub struct WindowWalker {
    windows: Vec<WindowEntry>,
}

impl WindowWalker {
    /// Enumerate all visible top-level windows on the current virtual desktop.
    ///
    /// Call once at capture startup — after the desktop bitmap is grabbed but
    /// before overlay windows are created, so our own windows are excluded.
    pub fn snapshot() -> Self {
        let vdm: Option<IVirtualDesktopManager> = unsafe {
            CoCreateInstance(&VirtualDesktopManager, None, CLSCTX_ALL).ok()
        };
        if vdm.is_none() {
            warn!("Could not create IVirtualDesktopManager — virtual-desktop filtering disabled");
        }

        // Collect raw HWNDs via EnumWindows (front-to-back Z-order).
        let mut hwnds: Vec<HWND> = Vec::new();
        unsafe {
            let _ = EnumWindows(
                Some(enum_windows_cb),
                LPARAM(&mut hwnds as *mut Vec<HWND> as isize),
            );
        }

        let mut windows: Vec<WindowEntry> = Vec::new();

        for hwnd in hwnds {
            if let Some(entry) = evaluate_window(hwnd, &vdm, &windows) {
                windows.push(entry);
            }
        }

        info!("WindowWalker: captured {} top-level windows", windows.len());
        WindowWalker { windows }
    }

    /// Given a cursor position in virtual-desktop physical pixels, return the
    /// suggested capture rectangle — the deepest meaningful child window
    /// region under the cursor, clipped to the top-level window bounds.
    ///
    /// Returns `None` if the cursor is over the desktop background (no
    /// captured window contains the point).
    pub fn hit_test(&self, point: ScreenPoint) -> Option<ScreenRect> {
        // Find the topmost (first in Z-order) window containing the point.
        let top = self.windows.iter().find(|w| w.rect.contains(point))?;

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
            } else if child_rect.width() < MIN_WINDOW_CHILD_SIZE
                || child_rect.height() < MIN_WINDOW_CHILD_SIZE
            {
                visible = false; // too small
            }

            // "Similar to parent" — child fills nearly the whole parent.
            // Don't update ideal but keep walking for a more specific child.
            let similar_to_parent = (child_rect.min_x() - parent_rect.min_x()).abs()
                < MERGE_WITH_PARENT_THRESHOLD
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

        // 12. Fully occluded by a single higher-Z window.
        if is_fully_occluded(&rect, accepted) {
            return None;
        }

        Some(WindowEntry { hwnd, rect })
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

/// Conservative occlusion test: returns true if any single previously-accepted
/// rect fully contains `rect`.
fn is_fully_occluded(rect: &ScreenRect, accepted: &[WindowEntry]) -> bool {
    accepted.iter().any(|w| {
        w.rect.min_x() <= rect.min_x()
            && w.rect.min_y() <= rect.min_y()
            && w.rect.max_x() >= rect.max_x()
            && w.rect.max_y() >= rect.max_y()
    })
}

/// Check whether an `ApplicationFrameWindow` has a real `CoreWindow` child
/// (meaning it's a live UWP app, not a phantom frame).
fn has_core_window_child(hwnd: HWND) -> bool {
    unsafe {
        FindWindowExW(
            Some(hwnd),
            None,
            PCWSTR::from_raw(
                windows::core::w!("Windows.UI.Core.CoreWindow").as_ptr(),
            ),
            PCWSTR::null(),
        )
        .is_ok()
    }
}
