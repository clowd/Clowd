//! The monitors as every reader of screens sees them: Prisc.Ui (through prisc_interop's
//! `prisc_screens_*`), the capture overlay, the share-region prompt.
//!
//! Coordinates are each platform's native space, untouched, and identical to the C#
//! `DesktopScreen` contract: physical virtual-desktop pixels on Windows, CoreGraphics global
//! points with a top-left origin on macOS.
//!
//! - Windows asks user32 on every call (`EnumDisplayMonitors`, `MonitorFromPoint`,
//!   `MonitorFromRect`, `GetMonitorInfoW`, `GetDpiForMonitor`, `EnumDisplaySettingsW`). Bounds
//!   and work area are `rcMonitor` / `rcWork`; a monitor whose display mode cannot be read keeps
//!   its place with an unknown refresh rate instead of being dropped.
//! - macOS is CoreGraphics only (`CGGetActiveDisplayList`, `CGDisplayBounds`,
//!   `CGDisplayCopyDisplayMode`), which is thread-safe, so every caller may ask from any thread.
//!   CoreGraphics has no work area: [`Screen::work_area`] is `None` there, and the callers that
//!   need one (the share-region prompt, Prisc.Ui through Avalonia) keep reading AppKit's
//!   `visibleFrame` on the main thread. The point and rect lookups run the pure selection
//!   ([`select_at_point`], [`select_at_rect`]) over the list, the way `MonitorFromPoint` and
//!   `MonitorFromRect` answer on Windows.
//! - Elsewhere the list is empty.
//!
//! The refresh rate is `None` when the OS gives no usable rate ([`known_refresh`]: 0, the Windows
//! "hardware default" sentinel 1, NaN, infinity). A caller that needs a number picks its own
//! fallback at its use site (the capture overlay uses 60 Hz, Prisc.Ui reports 0 = unknown).

use crate::geometry::{ScreenPoint, ScreenRect};

/// One monitor.
#[derive(Clone, Debug, PartialEq)]
pub struct Screen {
    /// `HMONITOR` on Windows, `CGDirectDisplayID` on macOS. Valid while the topology lasts.
    pub id: u64,
    /// The whole monitor (`rcMonitor` / `CGDisplayBounds`).
    pub bounds: ScreenRect,
    /// The monitor minus the taskbar and docked app bars (`rcWork`); `None` on macOS, where
    /// CoreGraphics has no work area.
    pub work_area: Option<ScreenRect>,
    /// Effective DPI / 96 on Windows (1.0 when it cannot be read); the display mode's backing
    /// pixels per point on macOS (1.0 when the mode cannot be read).
    pub scale: f64,
    /// The current refresh rate, `None` when unknown ([`known_refresh`]).
    pub refresh_hz: Option<f64>,
    /// The primary monitor (`MONITORINFOF_PRIMARY` / `CGMainDisplayID`, the display at the
    /// origin).
    pub is_primary: bool,
    /// The size of the monitor's backing store in pixels: the bounds on Windows, the display
    /// mode's pixel size on macOS (what a capture of the display measures).
    pub pixel_width: u32,
    pub pixel_height: u32,
    /// The GDI device name on Windows (`\\.\DISPLAY1`, what DXGI outputs are matched on); empty on
    /// macOS.
    pub device_name: String,
}

/// What the point and rect lookups answer when nothing matches.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Fallback {
    /// Nothing.
    None,
    /// The nearest monitor.
    Nearest,
    /// The primary monitor.
    Primary,
}

/// An OS call behind the screen list failed.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct OsError {
    /// The raw OS code (HRESULT / `CGError`), sign-extended; 0 when there was none.
    pub code: i64,
    pub message: String,
}

impl std::fmt::Display for OsError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{} (code {})", self.message, self.code)
    }
}

impl std::error::Error for OsError {}

/// A refresh rate the OS reported, or `None` when it is not a rate: 0 (CoreGraphics' "this
/// display does not say", also a failed Windows query), 1 (the Windows `dmDisplayFrequency`
/// "hardware default" sentinel), anything below that, NaN and infinity.
pub fn known_refresh(hz: f64) -> Option<f64> {
    (hz.is_finite() && hz > 1.0).then_some(hz)
}

/// Every monitor, in the OS's enumeration order (`EnumDisplayMonitors` /
/// `CGGetActiveDisplayList`). Empty on a platform without a display list.
pub fn list() -> Result<Vec<Screen>, OsError> {
    imp::list()
}

/// The monitor containing `point`, or what `fallback` names when none does.
pub fn at_point(point: ScreenPoint, fallback: Fallback) -> Result<Option<Screen>, OsError> {
    imp::at_point(point, fallback)
}

/// The monitor with the largest overlap with `rect`, or what `fallback` names when it overlaps
/// none. An empty rect is looked up by its top-left point (Win32's rule).
pub fn at_rect(rect: ScreenRect, fallback: Fallback) -> Result<Option<Screen>, OsError> {
    imp::at_rect(rect, fallback)
}

/// The primary monitor; `None` only on a desktop without one (a headless session).
pub fn primary() -> Result<Option<Screen>, OsError> {
    imp::primary()
}

/// `MonitorFromPoint` over a list: the screen containing `point` (half-open bounds, first match),
/// else the `fallback`.
pub fn select_at_point(screens: &[Screen], point: ScreenPoint, fallback: Fallback) -> Option<&Screen> {
    screens
        .iter()
        .find(|s| contains(&s.bounds, point))
        .or_else(|| fall_back(screens, fallback, |s| point_distance_sq(&s.bounds, point)))
}

/// `MonitorFromRect` over a list: the screen with the largest intersection (the first on a tie),
/// else the `fallback`. An empty `rect` is looked up by its top-left point.
pub fn select_at_rect(screens: &[Screen], rect: ScreenRect, fallback: Fallback) -> Option<&Screen> {
    if rect.size.width <= 0 || rect.size.height <= 0 {
        return select_at_point(screens, rect.origin, fallback);
    }
    let mut best: Option<(&Screen, i64)> = None;
    for screen in screens {
        let area = overlap_area(&screen.bounds, &rect);
        if area > 0 && best.is_none_or(|(_, a)| area > a) {
            best = Some((screen, area));
        }
    }
    best.map(|(s, _)| s)
        .or_else(|| fall_back(screens, fallback, |s| rect_distance_sq(&s.bounds, &rect)))
}

fn fall_back(screens: &[Screen], fallback: Fallback, distance: impl Fn(&Screen) -> i64) -> Option<&Screen> {
    match fallback {
        Fallback::None => None,
        Fallback::Primary => screens
            .iter()
            .find(|s| s.is_primary)
            .or_else(|| screens.first()),
        Fallback::Nearest => screens.iter().min_by_key(|s| distance(s)),
    }
}

fn contains(r: &ScreenRect, p: ScreenPoint) -> bool {
    p.x >= r.min_x() && p.x < r.max_x() && p.y >= r.min_y() && p.y < r.max_y()
}

/// The gap between two half-open spans on one axis, 0 when they overlap.
fn gap(a_min: i32, a_max: i32, b_min: i32, b_max: i32) -> i64 {
    let (a_min, a_max, b_min, b_max) = (i64::from(a_min), i64::from(a_max), i64::from(b_min), i64::from(b_max));
    (b_min - a_max).max(a_min - b_max).max(0)
}

fn point_distance_sq(r: &ScreenRect, p: ScreenPoint) -> i64 {
    let dx = gap(r.min_x(), r.max_x(), p.x, p.x.saturating_add(1));
    let dy = gap(r.min_y(), r.max_y(), p.y, p.y.saturating_add(1));
    dx * dx + dy * dy
}

fn rect_distance_sq(a: &ScreenRect, b: &ScreenRect) -> i64 {
    let dx = gap(a.min_x(), a.max_x(), b.min_x(), b.max_x());
    let dy = gap(a.min_y(), a.max_y(), b.min_y(), b.max_y());
    dx * dx + dy * dy
}

fn overlap_area(a: &ScreenRect, b: &ScreenRect) -> i64 {
    let w = i64::from(a.max_x().min(b.max_x())) - i64::from(a.min_x().max(b.min_x()));
    let h = i64::from(a.max_y().min(b.max_y())) - i64::from(a.min_y().max(b.min_y()));
    if w > 0 && h > 0 {
        w * h
    } else {
        0
    }
}

#[cfg(windows)]
mod imp {
    use std::mem;

    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{POINT, RECT};
    use windows::Win32::Graphics::Gdi::{
        EnumDisplaySettingsW, MonitorFromPoint, MonitorFromRect, DEVMODEW, ENUM_CURRENT_SETTINGS, HMONITOR, MONITOR_DEFAULTTONEAREST,
        MONITOR_DEFAULTTONULL, MONITOR_DEFAULTTOPRIMARY, MONITOR_FROM_FLAGS,
    };

    use super::{known_refresh, Fallback, OsError, Screen};
    use crate::desktop::{monitor, monitors, Monitor};
    use crate::geometry::{ScreenPoint, ScreenRect};

    pub(super) fn list() -> Result<Vec<Screen>, OsError> {
        let monitors = monitors().map_err(|e| OsError {
            code: i64::from(e.code().0),
            message: format!("EnumDisplayMonitors failed: {}", e.message()),
        })?;
        Ok(monitors.iter().map(describe).collect())
    }

    pub(super) fn at_point(point: ScreenPoint, fallback: Fallback) -> Result<Option<Screen>, OsError> {
        let handle = unsafe {
            MonitorFromPoint(
                POINT {
                    x: point.x,
                    y: point.y,
                },
                flags(fallback),
            )
        };
        Ok(from_handle(handle))
    }

    pub(super) fn at_rect(rect: ScreenRect, fallback: Fallback) -> Result<Option<Screen>, OsError> {
        // Byte for byte the RECT DesktopScreens.cs built: right = x + width, bottom = y + height.
        let native = RECT {
            left: rect.min_x(),
            top: rect.min_y(),
            right: rect.min_x().saturating_add(rect.size.width),
            bottom: rect.min_y().saturating_add(rect.size.height),
        };
        let handle = unsafe { MonitorFromRect(&native, flags(fallback)) };
        Ok(from_handle(handle))
    }

    pub(super) fn primary() -> Result<Option<Screen>, OsError> {
        // The primary monitor always contains the desktop origin.
        at_point(ScreenPoint::new(0, 0), Fallback::Primary)
    }

    fn flags(fallback: Fallback) -> MONITOR_FROM_FLAGS {
        match fallback {
            Fallback::None => MONITOR_DEFAULTTONULL,
            Fallback::Nearest => MONITOR_DEFAULTTONEAREST,
            Fallback::Primary => MONITOR_DEFAULTTOPRIMARY,
        }
    }

    /// A null handle is the point / rect being on no monitor; a monitor that vanished between the
    /// lookup and `GetMonitorInfoW` reads the same way, as it did in C#.
    fn from_handle(handle: HMONITOR) -> Option<Screen> {
        if handle.is_invalid() {
            return None;
        }
        monitor(handle).as_ref().map(describe)
    }

    fn describe(m: &Monitor) -> Screen {
        let scale = match m.dpi() {
            Some(dpi) if dpi > 0 => f64::from(dpi) / 96.0,
            _ => 1.0,
        };
        Screen {
            id: m.handle.0 as usize as u64,
            bounds: m.bounds,
            work_area: Some(m.work_area),
            scale,
            refresh_hz: refresh(&m.device_name),
            is_primary: m.is_primary,
            pixel_width: m.bounds.size.width.max(0) as u32,
            pixel_height: m.bounds.size.height.max(0) as u32,
            device_name: m.device_name.clone(),
        }
    }

    /// `EnumDisplaySettingsW`'s current `dmDisplayFrequency` for the device, through
    /// [`known_refresh`]; `None` when the query fails (the monitor is kept either way).
    fn refresh(device_name: &str) -> Option<f64> {
        if device_name.is_empty() {
            return None;
        }
        let name: Vec<u16> = device_name
            .encode_utf16()
            .chain(Some(0))
            .collect();
        let mut mode = DEVMODEW {
            dmSize: mem::size_of::<DEVMODEW>() as u16,
            ..Default::default()
        };
        if !unsafe { EnumDisplaySettingsW(PCWSTR(name.as_ptr()), ENUM_CURRENT_SETTINGS, &mut mode) }.as_bool() {
            return None;
        }
        known_refresh(f64::from(mode.dmDisplayFrequency))
    }
}

#[cfg(target_os = "macos")]
mod imp {
    use super::{known_refresh, select_at_point, select_at_rect, Fallback, OsError, Screen};
    use crate::desktop::{active_displays, Display};
    use crate::geometry::{ScreenPoint, ScreenRect};

    pub(super) fn list() -> Result<Vec<Screen>, OsError> {
        let displays = active_displays().map_err(|e| OsError {
            code: i64::from(e.0),
            message: format!("CGGetActiveDisplayList failed ({e:?})"),
        })?;
        Ok(displays.iter().map(describe).collect())
    }

    pub(super) fn at_point(point: ScreenPoint, fallback: Fallback) -> Result<Option<Screen>, OsError> {
        let screens = list()?;
        Ok(select_at_point(&screens, point, fallback).cloned())
    }

    pub(super) fn at_rect(rect: ScreenRect, fallback: Fallback) -> Result<Option<Screen>, OsError> {
        let screens = list()?;
        Ok(select_at_rect(&screens, rect, fallback).cloned())
    }

    pub(super) fn primary() -> Result<Option<Screen>, OsError> {
        Ok(list()?.into_iter().find(|s| s.is_primary))
    }

    fn describe(display: &Display) -> Screen {
        let b = display.bounds;
        // CGDisplayBounds is whole points on every real display; rounding only guards the type.
        let bounds = ScreenRect::new(
            ScreenPoint::new(b.origin.x.round() as i32, b.origin.y.round() as i32),
            euclid::Size2D::new(b.size.width.round() as i32, b.size.height.round() as i32),
        );
        let (pixel_width, pixel_height) = display.pixel_size();
        Screen {
            id: u64::from(display.id),
            bounds,
            work_area: None,
            scale: display
                .mode
                .and_then(|m| m.backing_scale())
                .unwrap_or(1.0),
            refresh_hz: display
                .mode
                .and_then(|m| known_refresh(m.refresh_hz)),
            is_primary: display.is_main,
            pixel_width: pixel_width as u32,
            pixel_height: pixel_height as u32,
            device_name: String::new(),
        }
    }
}

#[cfg(not(any(windows, target_os = "macos")))]
mod imp {
    use super::{Fallback, OsError, Screen};
    use crate::geometry::{ScreenPoint, ScreenRect};

    pub(super) fn list() -> Result<Vec<Screen>, OsError> {
        Ok(Vec::new())
    }

    pub(super) fn at_point(_: ScreenPoint, _: Fallback) -> Result<Option<Screen>, OsError> {
        Ok(None)
    }

    pub(super) fn at_rect(_: ScreenRect, _: Fallback) -> Result<Option<Screen>, OsError> {
        Ok(None)
    }

    pub(super) fn primary() -> Result<Option<Screen>, OsError> {
        Ok(None)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::geometry::RectExt;

    fn screen(id: u64, x: i32, y: i32, w: i32, h: i32, primary: bool) -> Screen {
        Screen {
            id,
            bounds: ScreenRect::from_xy_size(x, y, w, h),
            work_area: None,
            scale: 1.0,
            refresh_hz: None,
            is_primary: primary,
            pixel_width: w as u32,
            pixel_height: h as u32,
            device_name: String::new(),
        }
    }

    /// A primary 1920x1080 at the origin, a 1280x1024 to its right, and a 1920x1080 above-left.
    fn desktop() -> Vec<Screen> {
        vec![
            screen(1, 0, 0, 1920, 1080, true),
            screen(2, 1920, 0, 1280, 1024, false),
            screen(3, -1920, -1080, 1920, 1080, false),
        ]
    }

    fn id(screen: Option<&Screen>) -> Option<u64> {
        screen.map(|s| s.id)
    }

    #[test]
    fn the_refresh_sentinels_are_unknown() {
        assert_eq!(known_refresh(0.0), None);
        assert_eq!(known_refresh(1.0), None);
        assert_eq!(known_refresh(-60.0), None);
        assert_eq!(known_refresh(f64::NAN), None);
        assert_eq!(known_refresh(f64::INFINITY), None);
        assert_eq!(known_refresh(59.94), Some(59.94));
        assert_eq!(known_refresh(120.0), Some(120.0));
    }

    #[test]
    fn a_point_finds_its_screen_with_half_open_edges() {
        let d = desktop();
        assert_eq!(id(select_at_point(&d, ScreenPoint::new(0, 0), Fallback::None)), Some(1));
        assert_eq!(id(select_at_point(&d, ScreenPoint::new(1919, 1079), Fallback::None)), Some(1));
        assert_eq!(id(select_at_point(&d, ScreenPoint::new(1920, 0), Fallback::None)), Some(2));
        assert_eq!(id(select_at_point(&d, ScreenPoint::new(-1, -1), Fallback::None)), Some(3));
    }

    #[test]
    fn an_off_screen_point_follows_the_fallback() {
        let d = desktop();
        // below the right-hand monitor's shorter bottom edge
        let p = ScreenPoint::new(2000, 1050);
        assert_eq!(select_at_point(&d, p, Fallback::None), None);
        assert_eq!(id(select_at_point(&d, p, Fallback::Nearest)), Some(2));
        assert_eq!(id(select_at_point(&d, p, Fallback::Primary)), Some(1));
        let far = ScreenPoint::new(-5000, -5000);
        assert_eq!(id(select_at_point(&d, far, Fallback::Nearest)), Some(3));
    }

    #[test]
    fn a_rect_takes_the_screen_it_overlaps_most() {
        let d = desktop();
        // 100 px wide on the primary, 300 px on the right-hand monitor
        let r = ScreenRect::from_xy_size(1820, 100, 400, 200);
        assert_eq!(id(select_at_rect(&d, r, Fallback::None)), Some(2));
        let r = ScreenRect::from_xy_size(1620, 100, 400, 200);
        assert_eq!(id(select_at_rect(&d, r, Fallback::None)), Some(1));
        // an exact tie goes to the first in the list
        let r = ScreenRect::from_xy_size(1720, 100, 400, 200);
        assert_eq!(id(select_at_rect(&d, r, Fallback::None)), Some(1));
    }

    #[test]
    fn a_one_pixel_rect_agrees_with_its_point() {
        let d = desktop();
        for (x, y) in [(0, 0), (1919, 1079), (1920, 5), (-1, -1), (-1920, -1080), (5000, 5000)] {
            for fallback in [Fallback::None, Fallback::Nearest, Fallback::Primary] {
                let by_point = id(select_at_point(&d, ScreenPoint::new(x, y), fallback));
                let by_rect = id(select_at_rect(&d, ScreenRect::from_xy_size(x, y, 1, 1), fallback));
                assert_eq!(by_point, by_rect, "({x}, {y}) with {fallback:?}");
            }
        }
    }

    #[test]
    fn an_empty_rect_is_its_top_left_point() {
        let d = desktop();
        let r = ScreenRect::from_xy_size(1925, 10, 0, 0);
        assert_eq!(id(select_at_rect(&d, r, Fallback::None)), Some(2));
    }

    #[test]
    fn an_off_screen_rect_follows_the_fallback() {
        let d = desktop();
        let r = ScreenRect::from_xy_size(4000, 100, 50, 50);
        assert_eq!(select_at_rect(&d, r, Fallback::None), None);
        assert_eq!(id(select_at_rect(&d, r, Fallback::Nearest)), Some(2));
        assert_eq!(id(select_at_rect(&d, r, Fallback::Primary)), Some(1));
    }

    #[test]
    fn an_empty_list_answers_nothing() {
        for fallback in [Fallback::None, Fallback::Nearest, Fallback::Primary] {
            assert_eq!(select_at_point(&[], ScreenPoint::new(0, 0), fallback), None);
            assert_eq!(select_at_rect(&[], ScreenRect::from_xy_size(0, 0, 10, 10), fallback), None);
        }
    }

    #[test]
    fn primary_falls_back_to_the_first_screen_when_none_is_flagged() {
        let d = vec![screen(7, 100, 100, 10, 10, false), screen(8, 300, 100, 10, 10, false)];
        assert_eq!(id(select_at_point(&d, ScreenPoint::new(0, 0), Fallback::Primary)), Some(7));
    }

    // The live answers. Thread-agnostic on both OSes (R9), so plain libtest runs them.

    #[test]
    fn the_live_list_has_one_primary_and_sane_screens() {
        let screens = list().expect("the screen list");
        if screens.is_empty() {
            eprintln!("skipping: this session reports no screens");
            return;
        }
        assert_eq!(
            screens
                .iter()
                .filter(|s| s.is_primary)
                .count(),
            1,
            "{screens:?}"
        );
        for s in &screens {
            assert!(s.bounds.size.width > 0 && s.bounds.size.height > 0, "{s:?}");
            assert!(s.scale > 0.0, "{s:?}");
            assert!(s.pixel_width > 0 && s.pixel_height > 0, "{s:?}");
            if let Some(hz) = s.refresh_hz {
                assert!(hz > 1.0, "{s:?}");
            }
            if let Some(work) = s.work_area {
                assert!(work.min_x() >= s.bounds.min_x() && work.max_x() <= s.bounds.max_x(), "{s:?}");
                assert!(work.min_y() >= s.bounds.min_y() && work.max_y() <= s.bounds.max_y(), "{s:?}");
            }
            assert_eq!(s.work_area.is_some(), cfg!(windows), "{s:?}");
        }
    }

    #[test]
    fn the_live_primary_is_at_the_origin() {
        let Some(primary) = primary().expect("the primary screen") else {
            eprintln!("skipping: this session has no primary screen");
            return;
        };
        assert!(primary.is_primary);
        // the primary contains the desktop origin on Windows; the main display's origin IS (0, 0)
        // on macOS
        assert!(contains(&primary.bounds, ScreenPoint::new(0, 0)), "{primary:?}");
        if cfg!(target_os = "macos") {
            assert_eq!(primary.bounds.origin, ScreenPoint::new(0, 0));
        }
    }

    #[test]
    fn the_live_lookups_agree_with_each_other() {
        let Some(primary) = primary().expect("the primary screen") else {
            eprintln!("skipping: this session has no primary screen");
            return;
        };
        let p = ScreenPoint::new(primary.bounds.min_x() + 1, primary.bounds.min_y() + 1);
        let by_point = at_point(p, Fallback::None)
            .unwrap()
            .expect("a screen under a primary pixel");
        let by_rect = at_rect(ScreenRect::from_xy_size(p.x, p.y, 1, 1), Fallback::None)
            .unwrap()
            .expect("a screen under a primary pixel");
        assert_eq!(by_point.bounds, by_rect.bounds);
        assert_eq!(by_point.bounds, primary.bounds);

        let far = ScreenPoint::new(-1_000_000, -1_000_000);
        assert_eq!(at_point(far, Fallback::None).unwrap(), None);
        assert!(at_point(far, Fallback::Nearest)
            .unwrap()
            .is_some());
        assert!(at_point(far, Fallback::Primary)
            .unwrap()
            .is_some_and(|s| s.is_primary));
    }
}
