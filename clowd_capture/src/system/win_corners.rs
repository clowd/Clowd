//! Win32 glue for [`super::corners`]: asks DWM and user32 what the
//! capturer cannot decide for itself, then hands the answers to the
//! platform-neutral policy.
//!
//! The policy itself (Windows 10 never rounds; 11 rounds framed top-level
//! windows at 8 px unless maximized / snapped / remote / regioned / opted
//! out) lives in `corners.rs` where it is unit-tested on every host. This
//! file only gathers inputs and scales the result to the window's DPI.

use std::sync::OnceLock;

use windows::core::BOOL;
use windows::Win32::Foundation::HWND;
use windows::Win32::Graphics::Dwm::{
    DwmGetWindowAttribute, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DEFAULT, DWMWCP_DONOTROUND, DWMWCP_ROUND, DWMWCP_ROUNDSMALL,
    DWM_WINDOW_CORNER_PREFERENCE,
};
use windows::Win32::Graphics::Gdi::{CreateRectRgn, DeleteObject, GetWindowRgn, RGN_ERROR};
use windows::Win32::System::SystemInformation::{GetVersionExW, OSVERSIONINFOW};
use windows::Win32::UI::HiDpi::GetDpiForWindow;
use windows::Win32::UI::WindowsAndMessaging::{GetSystemMetrics, IsZoomed, SM_REMOTESESSION, WS_CAPTION, WS_THICKFRAME};

use super::corners::{windows_corner_radius_logical, CornerPreference, WindowsCornerInputs};

// `IsWindowArranged` (Windows 10 2004+) is exported by user32.dll without an
// import library, so it is bound here by name rather than through the
// `windows` crate — whose metadata may or may not carry it in the pinned
// version — which keeps the build independent of that question.
#[link(name = "user32.dll", kind = "raw-dylib")]
extern "system" {
    /// Whether the window is snapped (arranged by Snap / Snap Layouts) —
    /// DWM does not round arranged windows, same as maximized ones.
    fn IsWindowArranged(hwnd: HWND) -> BOOL;
}

/// `dwBuildNumber` of the running OS, read once. The shared app manifest
/// declares Windows 10 support, so `GetVersionExW` reports the real
/// version instead of the 6.2 compatibility lie — which is what makes it
/// usable here without pulling in `RtlGetVersion`.
fn os_build() -> u32 {
    static BUILD: OnceLock<u32> = OnceLock::new();
    *BUILD.get_or_init(|| {
        let mut info = OSVERSIONINFOW {
            dwOSVersionInfoSize: std::mem::size_of::<OSVERSIONINFOW>() as u32,
            ..Default::default()
        };
        // Tolerant of either `BOOL` or `Result` return shapes: a failure
        // leaves `dwBuildNumber` at 0, which the policy reads as "older
        // than Windows 11" — square corners, the safe direction.
        #[allow(deprecated)]
        let _ = unsafe { GetVersionExW(&mut info) };
        info.dwBuildNumber
    })
}

/// Whether this is a remote (RDP / VM-redirected) session, read once.
fn remote_session() -> bool {
    static REMOTE: OnceLock<bool> = OnceLock::new();
    *REMOTE.get_or_init(|| unsafe { GetSystemMetrics(SM_REMOTESESSION) != 0 })
}

/// `DWMWA_WINDOW_CORNER_PREFERENCE` for `hwnd`, or `None` if DWM would not
/// say (older DWM, or a window it has no record of).
fn corner_preference(hwnd: HWND) -> Option<CornerPreference> {
    let mut pref = DWMWCP_DEFAULT;
    let hr = unsafe {
        DwmGetWindowAttribute(
            hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            &mut pref as *mut DWM_WINDOW_CORNER_PREFERENCE as *mut _,
            std::mem::size_of::<DWM_WINDOW_CORNER_PREFERENCE>() as u32,
        )
    };
    if hr.is_err() {
        return None;
    }
    Some(match pref {
        DWMWCP_DONOTROUND => CornerPreference::DoNotRound,
        DWMWCP_ROUND => CornerPreference::Round,
        DWMWCP_ROUNDSMALL => CornerPreference::RoundSmall,
        _ => CornerPreference::Default,
    })
}

/// Whether the window has a `SetWindowRgn` shape. `GetWindowRgn` returns
/// `ERROR` for a window without one, which is the common case.
fn has_window_region(hwnd: HWND) -> bool {
    unsafe {
        let rgn = CreateRectRgn(0, 0, 0, 0);
        if rgn.is_invalid() {
            return false;
        }
        let kind = GetWindowRgn(hwnd, rgn);
        let _ = DeleteObject(rgn.into());
        kind != RGN_ERROR
    }
}

/// Corner radius in physical pixels DWM composites this top-level window
/// with — 0 for square. `style` is the window's `GWL_STYLE`, already read
/// by the walker.
pub fn window_corner_radius(hwnd: HWND, style: u32) -> f32 {
    let build = os_build();
    // Everything below is Windows 11 only; skip the per-window calls on 10.
    if build < super::corners::WINDOWS_11_FIRST_BUILD {
        return 0.0;
    }
    let has_frame = (style & WS_CAPTION.0) == WS_CAPTION.0 || (style & WS_THICKFRAME.0) != 0;
    let logical = windows_corner_radius_logical(WindowsCornerInputs {
        build,
        remote_session: remote_session(),
        maximized: unsafe { IsZoomed(hwnd).as_bool() },
        arranged: unsafe { IsWindowArranged(hwnd).as_bool() },
        preference: corner_preference(hwnd),
        has_region: has_window_region(hwnd),
        has_frame,
    });
    if logical <= 0.0 {
        return 0.0;
    }
    let dpi = unsafe { GetDpiForWindow(hwnd) };
    let dpi = if dpi == 0 { 96 } else { dpi };
    logical * dpi as f32 / 96.0
}
