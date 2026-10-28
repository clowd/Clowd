//! macOS: `CGWindowListCopyWindowInfo`, the active displays and the cursor,
//! all in global CG points (top-left origin, the `CGDisplayBounds` space).

use std::ffi::c_void;
use std::ops::RangeInclusive;
use std::ptr::NonNull;

use objc2_core_foundation::{CFData, CFDictionary, CFNumber, CFNumberType, CFRetained, CFString, CFUUID};
pub use objc2_core_foundation::{CGPoint, CGRect, CGSize};
use objc2_core_graphics::{
    kCGWindowAlpha, kCGWindowBounds, kCGWindowLayer, kCGWindowName, kCGWindowNumber, kCGWindowOwnerName, kCGWindowOwnerPID,
    CGAssociateMouseAndMouseCursorPosition, CGDataProvider, CGDirectDisplayID, CGDisplayBounds, CGDisplayCopyDisplayMode, CGDisplayMode,
    CGDisplayPixelsHigh, CGDisplayPixelsWide, CGError, CGEvent, CGGetActiveDisplayList, CGImage, CGMainDisplayID,
    CGWarpMouseCursorPosition, CGWindowID, CGWindowListCopyWindowInfo, CGWindowListOption,
};

// ---------------------------------------------------------------------------
// CFDictionary access
// ---------------------------------------------------------------------------

/// The value behind `key`, or `None` when the key is absent.
///
/// # Safety
/// `dict` must map `CFString` keys to CF objects, and the caller must know
/// the type behind `key` (a window description's schema guarantees it).
unsafe fn dict_value(dict: &CFDictionary, key: &CFString) -> Option<NonNull<c_void>> {
    NonNull::new(dict.value(key as *const CFString as *const c_void) as *mut c_void)
}

/// A `CFNumber` value as an `i64`; `None` when absent or not representable.
///
/// # Safety
/// As [`dict_value`]; the value behind `key` must be a `CFNumber`.
pub unsafe fn dict_i64(dict: &CFDictionary, key: &CFString) -> Option<i64> {
    let number = dict_value(dict, key)?
        .cast::<CFNumber>()
        .as_ref();
    let mut out: i64 = 0;
    number
        .value(CFNumberType::SInt64Type, &mut out as *mut i64 as *mut c_void)
        .then_some(out)
}

/// A `CFNumber` value as an `f64`; `None` when absent.
///
/// # Safety
/// As [`dict_i64`].
pub unsafe fn dict_f64(dict: &CFDictionary, key: &CFString) -> Option<f64> {
    let number = dict_value(dict, key)?
        .cast::<CFNumber>()
        .as_ref();
    let mut out: f64 = 0.0;
    number
        .value(CFNumberType::Float64Type, &mut out as *mut f64 as *mut c_void)
        .then_some(out)
}

/// A `CFString` value; empty when absent.
///
/// # Safety
/// As [`dict_value`]; the value behind `key` must be a `CFString`.
pub unsafe fn dict_string(dict: &CFDictionary, key: &CFString) -> String {
    dict_value(dict, key).map_or_else(String::new, |v| v.cast::<CFString>().as_ref().to_string())
}

/// A nested `CFDictionary` value.
///
/// # Safety
/// As [`dict_value`]; the value behind `key` must be a `CFDictionary`.
pub unsafe fn dict_dict<'a>(dict: &'a CFDictionary, key: &CFString) -> Option<&'a CFDictionary> {
    Some(
        dict_value(dict, key)?
            .cast::<CFDictionary>()
            .as_ref(),
    )
}

// ---------------------------------------------------------------------------
// Window list
// ---------------------------------------------------------------------------

/// `kCGNormalWindowLevel` alone: the document windows the overlay offers as
/// capture targets. The scrolling driver validates the overlay's window id
/// against the same set, so a window one considers real and the other does
/// not would look to the driver like a window that had closed.
pub const NORMAL_WINDOW_LAYERS: RangeInclusive<i64> = 0..=0;

/// The `kCGWindowLayer` range a real, user-facing application window lives
/// in: `kCGNormalWindowLevel` (0) through `kCGModalPanelWindowLevel` (8),
/// which also covers `kCGFloatingWindowLevel` / `kCGTornOffMenuWindowLevel`
/// (both 3).
///
/// The upper bound is what excludes the chrome: the Dock (20), the menu bar
/// (24), status items (25), pop-up menus (101), overlays (102), tooltips
/// (200), drag images (500), the screen saver (1000) and the cursor. The
/// desktop picture and its icon layer sit far below 0 and are already dropped
/// by `ExcludeDesktopElements`.
///
/// Floating panels (3) are deliberately INSIDE the range, for the same reason
/// the Windows filter does not drop `WS_EX_TOOLWINDOW`: a floating tool palette
/// (the Fonts and Colors panels, a Photoshop panel, an IDE float dock) is
/// plainly visible in a recording and must be tracked.
pub const APPLICATION_WINDOW_LAYERS: RangeInclusive<i64> = 0..=8;

/// Which entries of the window list a caller wants. Fully transparent windows
/// and entries without an owning process are always dropped.
#[derive(Debug, Clone)]
pub struct WindowFilter {
    /// `kCGWindowLayer` values to keep: [`NORMAL_WINDOW_LAYERS`] or
    /// [`APPLICATION_WINDOW_LAYERS`].
    pub layers: RangeInclusive<i64>,
    /// Drop this process's own windows.
    pub exclude_own_process: bool,
    /// Drop windows narrower or shorter than this many points (0 keeps every
    /// size).
    pub min_size: f64,
}

impl WindowFilter {
    /// The pure decision, given one description's values.
    ///
    /// An absent `kCGWindowAlpha` counts as opaque: a missing key is not
    /// evidence of invisibility (and `CGWindow.h` lists the key as always
    /// present anyway). The size test is written positively so a NaN extent
    /// falls out too.
    fn admits(&self, layer: i64, alpha: Option<f64>, pid: i64, own_pid: i64, width: f64, height: f64) -> bool {
        self.layers.contains(&layer)
            && alpha.unwrap_or(1.0) > 0.0
            && pid > 0
            && !(self.exclude_own_process && pid == own_pid)
            && width >= self.min_size
            && height >= self.min_size
    }
}

/// One on-screen window from `CGWindowListCopyWindowInfo`.
#[derive(Debug, Clone)]
pub struct WindowDescription {
    pub id: CGWindowID,
    pub pid: i32,
    pub layer: i64,
    /// `kCGWindowBounds`, in global CG points.
    pub bounds: CGRect,
    /// `kCGWindowName`. Absent, so empty, for *every* window unless this
    /// process holds Screen Recording permission.
    pub title: String,
    /// `kCGWindowOwnerName`: the application name.
    pub owner_name: String,
}

/// On-screen windows that pass `filter`, front to back (the order
/// `CGWindowListCopyWindowInfo` returns). Desktop elements (the desktop
/// picture and icons, which cover every display) are always excluded.
pub fn window_list(filter: &WindowFilter) -> Vec<WindowDescription> {
    let options = CGWindowListOption::OptionOnScreenOnly | CGWindowListOption::ExcludeDesktopElements;
    // 0 = kCGNullWindowID: not relative to any window.
    let Some(list) = CGWindowListCopyWindowInfo(options, 0) else {
        log::warn!("CGWindowListCopyWindowInfo returned null");
        return Vec::new();
    };

    // The four bounds sub-keys, built once per walk: `from_str` allocates and
    // the recorder walks up to 60 times a second.
    let keys = [
        CFString::from_str("X"),
        CFString::from_str("Y"),
        CFString::from_str("Width"),
        CFString::from_str("Height"),
    ];
    let own_pid = std::process::id() as i64;
    let mut out = Vec::new();
    for index in 0..list.count() {
        // SAFETY: every entry of the window list is a window description
        // dictionary with the documented value types.
        unsafe {
            let entry = list.value_at_index(index);
            if entry.is_null() {
                continue;
            }
            let dict = &*(entry as *const CFDictionary);
            let (Some(id), Some(layer), Some(pid)) = (
                dict_i64(dict, kCGWindowNumber),
                dict_i64(dict, kCGWindowLayer),
                dict_i64(dict, kCGWindowOwnerPID),
            ) else {
                continue;
            };
            let Some(bounds) = dict_dict(dict, kCGWindowBounds).and_then(|b| {
                let [x, y, w, h] = [&keys[0], &keys[1], &keys[2], &keys[3]].map(|k| dict_f64(b, k));
                Some(CGRect::new(CGPoint::new(x?, y?), CGSize::new(w?, h?)))
            }) else {
                continue;
            };
            let alpha = dict_f64(dict, kCGWindowAlpha);
            if !filter.admits(layer, alpha, pid, own_pid, bounds.size.width, bounds.size.height) {
                continue;
            }
            out.push(WindowDescription {
                id: id as CGWindowID,
                pid: pid as i32,
                layer,
                bounds,
                title: dict_string(dict, kCGWindowName),
                owner_name: dict_string(dict, kCGWindowOwnerName),
            });
        }
    }
    out
}

// ---------------------------------------------------------------------------
// Displays
// ---------------------------------------------------------------------------

/// One active display.
#[derive(Debug, Clone)]
pub struct Display {
    pub id: CGDirectDisplayID,
    /// `CGDisplayBounds`: global CG points, rotation applied.
    pub bounds: CGRect,
    /// The current display mode, `None` when it cannot be copied.
    pub mode: Option<DisplayMode>,
    /// `CGMainDisplayID`: the display with the menu bar, whose origin is
    /// (0, 0).
    pub is_main: bool,
}

/// The parts of a `CGDisplayMode` Prisc reads.
#[derive(Debug, Clone, Copy)]
pub struct DisplayMode {
    /// Backing-store size in pixels: what a capture of the display measures.
    pub pixel_width: usize,
    pub pixel_height: usize,
    /// Mode size in points.
    pub width: usize,
    /// 0 for displays that do not report one (most built-in panels).
    pub refresh_hz: f64,
}

impl DisplayMode {
    /// Backing-store pixels per point (2.0 on a Retina panel), or `None`
    /// when the mode reports an empty size.
    pub fn backing_scale(&self) -> Option<f64> {
        (self.pixel_width > 0 && self.width > 0).then(|| self.pixel_width as f64 / self.width as f64)
    }
}

impl Display {
    /// The display's pixel size: the mode's backing store, or
    /// `CGDisplayPixelsWide`/`High` (which report points on Retina panels)
    /// when there is no mode.
    pub fn pixel_size(&self) -> (usize, usize) {
        match self.mode {
            Some(m) => (m.pixel_width, m.pixel_height),
            None => (CGDisplayPixelsWide(self.id), CGDisplayPixelsHigh(self.id)),
        }
    }

    /// The display UUID string libobs's screen capture source takes. `None`
    /// when CoreGraphics has no UUID for the display; empty when it has one
    /// but cannot format it.
    pub fn uuid(&self) -> Option<String> {
        // Create rule: CFRetained releases the UUID and its string on drop.
        let uuid = unsafe { CGDisplayCreateUUIDFromDisplayID(self.id) }.map(|p| unsafe { CFRetained::from_raw(p) })?;
        Some(
            CFUUID::new_string(None, Some(&uuid))
                .map(|s| s.to_string())
                .unwrap_or_default(),
        )
    }
}

// objc2-core-graphics 0.3 does not bind this one (it is declared by ColorSync,
// not in the translated CoreGraphics headers).
#[link(name = "ColorSync", kind = "framework")]
extern "C-unwind" {
    fn CGDisplayCreateUUIDFromDisplayID(display: u32) -> Option<NonNull<CFUUID>>;
}

/// Every active display id, in `CGGetActiveDisplayList` order. The cheap half
/// of [`active_displays`], for callers on a latency-critical path that need
/// neither bounds nor mode.
pub fn active_display_ids() -> Result<Vec<CGDirectDisplayID>, CGError> {
    let mut count: u32 = 0;
    // SAFETY: a null list with a zero capacity only asks for the count.
    let err = unsafe { CGGetActiveDisplayList(0, std::ptr::null_mut(), &mut count) };
    if err != CGError::Success {
        return Err(err);
    }
    let mut ids = vec![0 as CGDirectDisplayID; count as usize];
    // SAFETY: `ids` holds `count` entries.
    let err = unsafe { CGGetActiveDisplayList(count, ids.as_mut_ptr(), &mut count) };
    if err != CGError::Success {
        return Err(err);
    }
    ids.truncate(count as usize);
    Ok(ids)
}

/// Every active display, in `CGGetActiveDisplayList` order.
pub fn active_displays() -> Result<Vec<Display>, CGError> {
    let main = CGMainDisplayID();
    Ok(active_display_ids()?
        .into_iter()
        .map(|id| Display {
            id,
            bounds: CGDisplayBounds(id),
            // CFRetained releases the copied mode on drop (Create rule).
            mode: CGDisplayCopyDisplayMode(id).map(|mode| DisplayMode {
                pixel_width: CGDisplayMode::pixel_width(Some(&mode)),
                pixel_height: CGDisplayMode::pixel_height(Some(&mode)),
                width: CGDisplayMode::width(Some(&mode)),
                refresh_hz: CGDisplayMode::refresh_rate(Some(&mode)),
            }),
            is_main: id == main,
        })
        .collect())
}

// ---------------------------------------------------------------------------
// Images
// ---------------------------------------------------------------------------

/// A `CGImage`'s pixels, copied out once (`CGDataProviderCopyData`). Rows are
/// `bytes_per_row` apart, which is rarely `width * 4`: the graphics stack pads
/// them to its own alignment.
pub struct ImageBytes {
    pub width: usize,
    pub height: usize,
    pub bytes_per_row: usize,
    pub bits_per_pixel: usize,
    data: CFRetained<CFData>,
}

impl ImageBytes {
    pub fn of(image: &CGImage) -> Option<ImageBytes> {
        let provider = CGImage::data_provider(Some(image))?;
        Some(ImageBytes {
            width: CGImage::width(Some(image)),
            height: CGImage::height(Some(image)),
            bytes_per_row: CGImage::bytes_per_row(Some(image)),
            bits_per_pixel: CGImage::bits_per_pixel(Some(image)),
            data: CGDataProvider::data(Some(&provider))?,
        })
    }

    pub fn bytes(&self) -> &[u8] {
        // SAFETY: the data is this struct's own immutable copy.
        unsafe { self.data.as_bytes_unchecked() }
    }
}

// ---------------------------------------------------------------------------
// Cursor
// ---------------------------------------------------------------------------

/// The cursor position in global CG points: the location of a fresh
/// `CGEventCreate` snapshot. `None` if the window server will not make one.
pub fn cursor_location() -> Option<CGPoint> {
    let event = CGEvent::new(None)?;
    Some(CGEvent::location(Some(&event)))
}

/// Moves the cursor to `point` (CG points), then re-couples the mouse to it.
///
/// A warp moves the cursor without moving the mouse, and the two can be left
/// uncoupled: re-associating is what makes the user's next physical movement
/// continue from where the cursor now is instead of snapping back to where
/// their hand left it. Done even when the warp fails.
pub fn warp_cursor(point: CGPoint) -> Result<(), CGError> {
    let warped = CGWarpMouseCursorPosition(point);
    let _ = CGAssociateMouseAndMouseCursorPosition(true);
    if warped == CGError::Success {
        Ok(())
    } else {
        Err(warped)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn filter(layers: RangeInclusive<i64>, exclude_own_process: bool, min_size: f64) -> WindowFilter {
        WindowFilter {
            layers,
            exclude_own_process,
            min_size,
        }
    }

    /// Both ends of the application range are load-bearing: narrowing it back
    /// to layer 0 silently drops every floating tool palette, and widening it
    /// past the modal-panel level pulls the Dock and menu bar in as
    /// full-screen "windows".
    #[test]
    fn the_application_layers_admit_panels_and_exclude_the_chrome() {
        // kCGNormalWindowLevel, kCGFloatingWindowLevel /
        // kCGTornOffMenuWindowLevel, kCGModalPanelWindowLevel.
        for level in [0, 3, 8] {
            assert!(APPLICATION_WINDOW_LAYERS.contains(&level), "layer {level} must be tracked");
        }
        // kCGDockWindowLevel, kCGMainMenuWindowLevel, kCGStatusWindowLevel,
        // kCGPopUpMenuWindowLevel, kCGOverlayWindowLevel, kCGHelpWindowLevel,
        // kCGDraggingWindowLevel, kCGScreenSaverWindowLevel, and the desktop
        // levels below zero.
        for level in [-2_147_483_623, -1, 20, 24, 25, 101, 102, 200, 500, 1000] {
            assert!(!APPLICATION_WINDOW_LAYERS.contains(&level), "layer {level} must be skipped");
        }
        assert!(NORMAL_WINDOW_LAYERS.contains(&0));
        assert!(!NORMAL_WINDOW_LAYERS.contains(&3));
    }

    #[test]
    fn backing_scale_is_pixels_per_point() {
        let mode = |pixel_width, width| DisplayMode {
            pixel_width,
            pixel_height: 0,
            width,
            refresh_hz: 0.0,
        };
        assert_eq!(mode(3456, 1728).backing_scale(), Some(2.0));
        assert_eq!(mode(1920, 1920).backing_scale(), Some(1.0));
        assert_eq!(mode(0, 1920).backing_scale(), None);
        assert_eq!(mode(1920, 0).backing_scale(), None);
    }

    #[test]
    fn transparent_and_ownerless_windows_never_pass() {
        let f = filter(APPLICATION_WINDOW_LAYERS, false, 0.0);
        assert!(f.admits(0, Some(1.0), 42, 7, 100.0, 100.0));
        assert!(!f.admits(0, Some(0.0), 42, 7, 100.0, 100.0));
        assert!(!f.admits(0, Some(1.0), 0, 7, 100.0, 100.0));
        // An absent alpha reads as opaque.
        assert!(f.admits(0, None, 42, 7, 100.0, 100.0));
        assert!(!f.admits(20, Some(1.0), 42, 7, 100.0, 100.0));
    }

    #[test]
    fn own_windows_and_small_windows_follow_the_filter() {
        let keep_all = filter(NORMAL_WINDOW_LAYERS, false, 0.0);
        let recorder = filter(APPLICATION_WINDOW_LAYERS, true, 25.0);
        assert!(keep_all.admits(0, Some(1.0), 7, 7, 10.0, 10.0));
        assert!(!recorder.admits(0, Some(1.0), 7, 7, 100.0, 100.0));
        assert!(recorder.admits(0, Some(1.0), 8, 7, 25.0, 25.0));
        assert!(!recorder.admits(0, Some(1.0), 8, 7, 24.9, 100.0));
        assert!(!recorder.admits(0, Some(1.0), 8, 7, f64::NAN, 100.0));
    }
}
