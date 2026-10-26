//! Window walker — enumerates visible top-level windows on macOS and finds
//! the best capture-target rectangle under a given point.
//!
//! Simplified macOS port of `win_walker.rs`: returns whole window rects only,
//! no child-window walking.

use std::ffi::c_void;
use std::sync::Mutex;

use core_foundation::base::TCFType;
use core_foundation::dictionary::{CFDictionary, CFDictionaryRef};
use core_foundation::number::CFNumber;
use core_foundation::string::CFString;
use core_graphics::display::CGDisplay;
use core_graphics::geometry::CGRect;
use core_graphics::window::{self, kCGNullWindowID, kCGWindowListExcludeDesktopElements, kCGWindowListOptionOnScreenOnly};

use super::mac_corners::{self, CgBounds};
use super::{HitTestResult, ObstructedWindow, WindowCaptureRef, WindowTarget};
use crate::system::MonitorInfo;
use clowd_rust_core::geometry::{RectExt, ScreenPoint, ScreenRect};

/// Minimum top-level window dimension (px) to be considered capturable.
const MIN_WINDOW_SIZE: i32 = 25;

struct WindowEntry {
    window_id: u32,
    rect: ScreenRect,
    /// `kCGWindowBounds` as reported — CG points — kept for the corner probe,
    /// which photographs the window in that space.
    cg_bounds: CgBounds,
    /// Backing scale of the display the window was placed on (points →
    /// physical px), for the lookup-table radius.
    scale: f32,
    /// Window title text.
    title: String,
    obstructed: bool,
    obstruction_rects: Vec<ScreenRect>,
}

/// Snapshot of the top-level window list in Z-order. Created once at capture
/// startup; queried per cursor-move via [`hit_test_target`].
pub struct WindowWalker {
    windows: Vec<WindowEntry>,
    /// Corner radius per entry, physical px, 0 = square. Seeded from the OS
    /// lookup table at snapshot, then overwritten by [`probe_corner_radii`]
    /// as the window server answers — behind a mutex because the walker is
    /// already shared (`Arc`) with the main thread by then. All zero when
    /// the walker was built with rounded corners off.
    corner_radii: Mutex<Vec<f32>>,
    /// Whether the corner probe should run at all.
    rounded_corners: bool,
}

impl WindowWalker {
    /// Enumerate all visible top-level windows on the current desktop.
    ///
    /// Call once at capture startup — after the desktop bitmap is grabbed but
    /// before overlay windows are created, so our own windows are excluded.
    pub fn snapshot(monitors: &[MonitorInfo], visibility_threshold: f32, rounded_corners: bool) -> Self {
        let options = kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements;
        let window_list = match window::copy_window_info(options, kCGNullWindowID) {
            Some(list) => list,
            None => {
                warn!("CGWindowListCopyWindowInfo returned null");
                return WindowWalker {
                    windows: Vec::new(),
                    corner_radii: Mutex::new(Vec::new()),
                    rounded_corners,
                };
            }
        };

        let ptrs = window_list.get_all_values();
        let mut windows: Vec<WindowEntry> = Vec::new();

        for ptr in ptrs {
            let dict: CFDictionary = unsafe { TCFType::wrap_under_get_rule(ptr as CFDictionaryRef) };
            if let Some(entry) = evaluate_window(&dict, &windows, monitors, visibility_threshold) {
                windows.push(entry);
            }
        }

        info!("WindowWalker: captured {} top-level windows", windows.len());
        // Seed every entry with the lookup-table radius for this OS; the
        // probe replaces these one by one once the snapshot is published.
        let fallback_pts = if rounded_corners {
            mac_corners::fallback_radius_points()
        } else {
            0.0
        };
        let corner_radii = windows
            .iter()
            .map(|w| fallback_pts * w.scale)
            .collect();
        WindowWalker {
            windows,
            corner_radii: Mutex::new(corner_radii),
            rounded_corners,
        }
    }

    /// Ask the window server for each window's real corner radius and
    /// replace the lookup-table seed with it. Front-to-back, so the windows
    /// the user is most likely aiming at settle first. Run on the walker
    /// thread AFTER the snapshot has been published: it is a ~10 ms
    /// round-trip per window, and hover hit-testing must not wait on it.
    pub fn probe_corner_radii(&self, monitors: &[MonitorInfo]) {
        if !self.rounded_corners {
            return;
        }
        let mut probed = 0usize;
        for (i, w) in self.windows.iter().enumerate() {
            let Some(r) = mac_corners::probe_corner_radius(w.window_id, w.cg_bounds, monitors) else {
                continue;
            };
            debug!(
                "WindowWalker: window {i} {:?} corner radius {r:.1} px (seed {:.1})",
                w.title,
                self.corner_radius(i)
            );
            if let Ok(mut radii) = self.corner_radii.lock() {
                if let Some(slot) = radii.get_mut(i) {
                    *slot = r;
                    probed += 1;
                }
            }
        }
        info!("WindowWalker: measured corner radius of {probed}/{} windows", self.windows.len());
    }

    fn corner_radius(&self, index: usize) -> f32 {
        self.corner_radii
            .lock()
            .ok()
            .and_then(|r| r.get(index).copied())
            .unwrap_or(0.0)
    }

    /// Given a cursor position in virtual-desktop physical pixels, return the
    /// suggested capture target — the topmost window under the cursor, with
    /// the corner radius to draw and crop it with.
    ///
    /// Returns `None` if the cursor is over the desktop background.
    pub fn hit_test_target(&self, point: ScreenPoint) -> Option<WindowTarget> {
        let (idx, w) = self
            .windows
            .iter()
            .enumerate()
            .find(|(_, w)| w.rect.contains(point))?;
        Some(WindowTarget {
            rect: w.rect,
            corner_radius: self.corner_radius(idx),
        })
    }

    /// The scrolling-capture target under `point`: the topmost enumerated
    /// window, as a `CGWindowID` widened to the `isize` the `scroll` marker
    /// carries on both platforms (`win_walker` puts an `HWND` in the same
    /// field). The driver re-validates whatever it is given, and `0` — which
    /// is what "no window here" becomes at the call site — tells it to
    /// resolve the target from the point itself.
    pub fn top_level_hwnd_at(&self, point: ScreenPoint) -> Option<isize> {
        self.windows
            .iter()
            .find(|w| w.rect.contains(point))
            .map(|w| w.window_id as isize)
    }

    /// Window id at `window_index` — the index [`Self::hit_test_full`]
    /// reports and a peek carries.
    ///
    /// The scrolling capture needs it: a peeked window is by definition
    /// partly covered, so asking what is at the scroll point would name the
    /// window on top instead of the one the user selected.
    pub fn hwnd_at_index(&self, window_index: usize) -> Option<isize> {
        self.windows
            .get(window_index)
            .map(|w| w.window_id as isize)
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

    /// Return metadata for all obstructed windows (for CGWindowListCreateImage capture).
    pub fn obstructed_windows(&self) -> Vec<ObstructedWindow> {
        self.windows
            .iter()
            .enumerate()
            .filter(|(_, w)| w.obstructed)
            .map(|(i, w)| ObstructedWindow {
                window_index: i,
                capture_ref: WindowCaptureRef::from_window_id(w.window_id),
                rect: w.rect,
                raw_rect: w.rect,
                obstruction_rects: w.obstruction_rects.clone(),
            })
            .collect()
    }

    #[allow(dead_code)]
    pub fn hit_test_with_title(&self, point: ScreenPoint) -> Option<(ScreenRect, String)> {
        self.windows
            .iter()
            .find(|w| w.rect.contains(point))
            .map(|w| (w.rect, w.title.clone()))
    }

    /// Capture rect for `--capture-mode window`: best-effort active window,
    /// taken as the topmost enumerated window (CGWindowList is front-to-back
    /// Z-order). `None` if no windows were captured, so the caller falls back
    /// to the active screen.
    pub fn foreground_capture_target(&self) -> Option<WindowTarget> {
        let w = self.windows.first()?;
        Some(WindowTarget {
            rect: w.rect,
            corner_radius: self.corner_radius(0),
        })
    }
}

// ---------------------------------------------------------------------------
// Per-window evaluation
// ---------------------------------------------------------------------------

fn evaluate_window(
    dict: &CFDictionary,
    accepted: &[WindowEntry],
    monitors: &[MonitorInfo],
    visibility_threshold: f32,
) -> Option<WindowEntry> {
    // 0. Extract the CG window ID (needed for CGWindowListCreateImage).
    let window_id = get_number_i64(dict, unsafe { window::kCGWindowNumber })? as u32;

    // 1. Layer == 0 (normal windows only; skip menu bar, dock, overlays).
    let layer = get_number_i64(dict, unsafe { window::kCGWindowLayer })?;
    if layer != 0 {
        return None;
    }

    // 2. Alpha > 0.
    let alpha = get_number_f64(dict, unsafe { window::kCGWindowAlpha })?;
    if alpha <= 0.0 {
        return None;
    }

    // 3. Parse bounds from the kCGWindowBounds sub-dictionary.
    let bounds_ptr = get_raw_value(dict, unsafe { window::kCGWindowBounds })?;
    let bounds_dict: CFDictionary = unsafe { TCFType::wrap_under_get_rule(bounds_ptr as CFDictionaryRef) };
    let cg_rect = CGRect::from_dict_representation(&bounds_dict)?;

    // 4. Convert logical CG points → physical pixels using MonitorInfo.
    let center_x = cg_rect.origin.x + cg_rect.size.width / 2.0;
    let center_y = cg_rect.origin.y + cg_rect.size.height / 2.0;

    let (phys_x, phys_y, phys_w, phys_h, scale) = if let Some(m) = find_monitor_for_cg_point(center_x, center_y, monitors) {
        let ox = m.logical_origin.x;
        let oy = m.logical_origin.y;
        let s = m.scale_factor as f64;
        (
            m.bounds.min_x() + ((cg_rect.origin.x - ox) * s).round() as i32,
            m.bounds.min_y() + ((cg_rect.origin.y - oy) * s).round() as i32,
            (cg_rect.size.width * s).round() as i32,
            (cg_rect.size.height * s).round() as i32,
            m.scale_factor,
        )
    } else {
        let scale = display_scale_at_logical_point(center_x, center_y);
        let s = scale as f64;
        (
            (cg_rect.origin.x * s).round() as i32,
            (cg_rect.origin.y * s).round() as i32,
            (cg_rect.size.width * s).round() as i32,
            (cg_rect.size.height * s).round() as i32,
            scale,
        )
    };

    // 5. Size threshold.
    if phys_w < MIN_WINDOW_SIZE || phys_h < MIN_WINDOW_SIZE {
        return None;
    }

    let rect = ScreenRect::from_xy_size(phys_x, phys_y, phys_w, phys_h);

    // 6. Visibility threshold — drop windows with too little visible area.
    //    Also collect obstruction rects for the peek feature.
    let mut obstruction_rects = Vec::new();
    let window_area = (phys_w as i64) * (phys_h as i64);
    if window_area > 0 {
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
        let obstructed_frac = obstructed_area as f64 / window_area as f64;
        if obstructed_frac > visibility_threshold as f64 {
            return None;
        }
    }
    let obstructed = !obstruction_rects.is_empty();

    // Read the window title (kCGWindowName), defaulting to empty string.
    let title = get_raw_value(dict, unsafe { window::kCGWindowName })
        .map(|ptr| {
            let cf_str: CFString = unsafe { TCFType::wrap_under_get_rule(ptr as core_foundation::string::CFStringRef) };
            cf_str.to_string()
        })
        .unwrap_or_default();

    Some(WindowEntry {
        window_id,
        rect,
        cg_bounds: CgBounds {
            x: cg_rect.origin.x,
            y: cg_rect.origin.y,
            w: cg_rect.size.width,
            h: cg_rect.size.height,
        },
        scale,
        title,
        obstructed,
        obstruction_rects,
    })
}

fn find_monitor_for_cg_point(x: f64, y: f64, monitors: &[MonitorInfo]) -> Option<&MonitorInfo> {
    monitors.iter().find(|m| {
        let ox = m.logical_origin.x;
        let oy = m.logical_origin.y;
        let lw = m.bounds.width() as f64 / m.scale_factor as f64;
        let lh = m.bounds.height() as f64 / m.scale_factor as f64;
        x >= ox && x < ox + lw && y >= oy && y < oy + lh
    })
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Get a raw `*const c_void` value from a CFDictionary by CFStringRef key.
fn get_raw_value(dict: &CFDictionary, key: core_foundation::string::CFStringRef) -> Option<*const c_void> {
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

fn get_number_i64(dict: &CFDictionary, key: core_foundation::string::CFStringRef) -> Option<i64> {
    let ptr = get_raw_value(dict, key)?;
    let number: CFNumber = unsafe { TCFType::wrap_under_get_rule(ptr as core_foundation::number::CFNumberRef) };
    number.to_i64()
}

fn get_number_f64(dict: &CFDictionary, key: core_foundation::string::CFStringRef) -> Option<f64> {
    let ptr = get_raw_value(dict, key)?;
    let number: CFNumber = unsafe { TCFType::wrap_under_get_rule(ptr as core_foundation::number::CFNumberRef) };
    number.to_f64()
}

/// Find the scale factor of the display whose logical bounds contain the
/// given CG point.
fn display_scale_at_logical_point(x: f64, y: f64) -> f32 {
    if let Ok(ids) = CGDisplay::active_displays() {
        for id in ids {
            let d = CGDisplay::new(id);
            let b = d.bounds();
            if x >= b.origin.x && x < b.origin.x + b.size.width && y >= b.origin.y && y < b.origin.y + b.size.height {
                // Use CGDisplayMode::pixel_width() for the true physical
                // pixel count. CGDisplayPixelsWide is deprecated and returns
                // the logical resolution on modern Retina displays.
                let logical_w = b.size.width as f32;
                if logical_w > 0.0 {
                    if let Some(mode) = d.display_mode() {
                        return mode.pixel_width() as f32 / logical_w;
                    }
                }
            }
        }
    }
    // Fallback: assume Retina 2×.
    2.0
}
