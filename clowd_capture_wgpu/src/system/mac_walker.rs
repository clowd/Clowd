//! Window walker — enumerates visible top-level windows on macOS and finds
//! the best capture-target rectangle under a given point.
//!
//! Simplified macOS port of `win_walker.rs`: returns whole window rects only,
//! no child-window walking.

use std::ffi::c_void;

use core_foundation::base::TCFType;
use core_foundation::dictionary::{CFDictionary, CFDictionaryRef};
use core_foundation::number::CFNumber;
use core_foundation::string::CFString;
use core_graphics::display::CGDisplay;
use core_graphics::geometry::CGRect;
use core_graphics::window::{
    self, kCGNullWindowID, kCGWindowListExcludeDesktopElements, kCGWindowListOptionOnScreenOnly,
};

use crate::geometry::{RectExt, ScreenPoint, ScreenRect};

/// Minimum top-level window dimension (px) to be considered capturable.
const MIN_WINDOW_SIZE: i32 = 25;

struct WindowEntry {
    rect: ScreenRect,
    /// Window title text.
    title: String,
}

/// Snapshot of the top-level window list in Z-order. Created once at capture
/// startup; queried per cursor-move via [`hit_test`].
pub struct WindowWalker {
    windows: Vec<WindowEntry>,
}

impl WindowWalker {
    /// Enumerate all visible top-level windows on the current desktop.
    ///
    /// Call once at capture startup — after the desktop bitmap is grabbed but
    /// before overlay windows are created, so our own windows are excluded.
    pub fn snapshot() -> Self {
        let options = kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements;
        let window_list = match window::copy_window_info(options, kCGNullWindowID) {
            Some(list) => list,
            None => {
                warn!("CGWindowListCopyWindowInfo returned null");
                return WindowWalker {
                    windows: Vec::new(),
                };
            }
        };

        let ptrs = window_list.get_all_values();
        let mut windows: Vec<WindowEntry> = Vec::new();

        for ptr in ptrs {
            let dict: CFDictionary =
                unsafe { TCFType::wrap_under_get_rule(ptr as CFDictionaryRef) };
            if let Some(entry) = evaluate_window(&dict, &windows) {
                windows.push(entry);
            }
        }

        info!("WindowWalker: captured {} top-level windows", windows.len());
        WindowWalker { windows }
    }

    /// Given a cursor position in virtual-desktop physical pixels, return the
    /// suggested capture rectangle — the topmost window under the cursor.
    ///
    /// Returns `None` if the cursor is over the desktop background.
    pub fn hit_test(&self, point: ScreenPoint) -> Option<ScreenRect> {
        self.windows
            .iter()
            .find(|w| w.rect.contains(point))
            .map(|w| w.rect)
    }

    /// Same as [`hit_test`] but also returns the title of the top-level window
    /// that contains the point.
    pub fn hit_test_with_title(&self, point: ScreenPoint) -> Option<(ScreenRect, String)> {
        self.windows
            .iter()
            .find(|w| w.rect.contains(point))
            .map(|w| (w.rect, w.title.clone()))
    }
}

// ---------------------------------------------------------------------------
// Per-window evaluation
// ---------------------------------------------------------------------------

fn evaluate_window(dict: &CFDictionary, accepted: &[WindowEntry]) -> Option<WindowEntry> {
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
    let bounds_dict: CFDictionary =
        unsafe { TCFType::wrap_under_get_rule(bounds_ptr as CFDictionaryRef) };
    let cg_rect = CGRect::from_dict_representation(&bounds_dict)?;

    // 4. Convert logical CG points → physical pixels.
    let center_x = cg_rect.origin.x + cg_rect.size.width / 2.0;
    let center_y = cg_rect.origin.y + cg_rect.size.height / 2.0;
    let scale = display_scale_at_logical_point(center_x, center_y) as f64;

    let phys_x = (cg_rect.origin.x * scale).round() as i32;
    let phys_y = (cg_rect.origin.y * scale).round() as i32;
    let phys_w = (cg_rect.size.width * scale).round() as i32;
    let phys_h = (cg_rect.size.height * scale).round() as i32;

    // 5. Size threshold.
    if phys_w < MIN_WINDOW_SIZE || phys_h < MIN_WINDOW_SIZE {
        return None;
    }

    let rect = ScreenRect::from_xy_size(phys_x, phys_y, phys_w, phys_h);

    // 6. Fully occluded by a higher-Z window already accepted.
    if is_fully_occluded(&rect, accepted) {
        return None;
    }

    // Read the window title (kCGWindowName), defaulting to empty string.
    let title = get_raw_value(dict, unsafe { window::kCGWindowName })
        .map(|ptr| {
            let cf_str: CFString =
                unsafe { TCFType::wrap_under_get_rule(ptr as core_foundation::string::CFStringRef) };
            cf_str.to_string()
        })
        .unwrap_or_default();

    Some(WindowEntry { rect, title })
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
            if x >= b.origin.x
                && x < b.origin.x + b.size.width
                && y >= b.origin.y
                && y < b.origin.y + b.size.height
            {
                let logical_w = b.size.width as f32;
                if logical_w > 0.0 {
                    return d.pixels_wide() as f32 / logical_w;
                }
            }
        }
    }
    // Fallback: assume Retina 2×.
    2.0
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
