//! Windows platform implementation (DESIGN §2.2).

use std::env;
use std::ffi::CStr;
use std::mem;
use std::path::Path;

use obs::data::ObsData;
use windows_sys::core::BOOL;
use windows_sys::Win32::Foundation::{LPARAM, POINT, RECT, TRUE};
use windows_sys::Win32::Graphics::Gdi::{
    EnumDisplayDevicesW, EnumDisplayMonitors, GetMonitorInfoW, MonitorFromPoint, DISPLAY_DEVICEW,
    HDC, HMONITOR, MONITORINFO, MONITORINFOEXW, MONITOR_DEFAULTTONEAREST,
};
use windows_sys::Win32::System::Threading::ExitProcess;
use windows_sys::Win32::UI::HiDpi::{
    GetDpiForMonitor, SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
    MDT_DEFAULT,
};
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{GetAsyncKeyState, VK_LBUTTON, VK_RBUTTON};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    GetCursorInfo, GetCursorPos, LoadCursorW, CURSORINFO, CURSOR_SHOWING, IDC_APPSTARTING,
    IDC_ARROW, IDC_CROSS, IDC_HAND, IDC_HELP, IDC_IBEAM, IDC_NO, IDC_PERSON, IDC_PIN, IDC_SIZEALL,
    IDC_SIZENESW, IDC_SIZENS, IDC_SIZENWSE, IDC_SIZEWE, IDC_UPARROW, IDC_WAIT,
    MONITORINFOF_PRIMARY,
};

use crate::cursor_sprite::SpriteEvent;

use super::{CursorKind, CursorState, MonitorInfo, MouseInfo, ObsPaths};

/// `platform` field of the input-capture header (wire contract).
pub const PLATFORM_NAME: &str = "windows";

pub const GRAPHICS_MODULE: &CStr = c"libobs-d3d11";
pub const DISPLAY_CAPTURE_ID: &str = "monitor_capture";
pub const AUDIO_INPUT_CAPTURE_ID: &str = "wasapi_input_capture";
/// Webcam capture source (`--webcam` / `--list-cameras`): DirectShow.
pub const WEBCAM_SOURCE_ID: &str = "dshow_input";
/// The `WEBCAM_SOURCE_ID` settings key (and property) holding the device id.
pub const WEBCAM_DEVICE_KEY: &str = "video_device_id";

/// `EDD_GET_DEVICE_INTERFACE_NAME` — request the device interface path in
/// `DISPLAY_DEVICEW.DeviceID`.
const EDD_GET_DEVICE_INTERFACE_NAME: u32 = 0x0000_0001;

/// Must run before any monitor enumeration so `EnumDisplayMonitors` rects are
/// physical pixels (per-monitor-v2 DPI awareness).
pub fn init_process() {
    unsafe {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
}

pub fn enumerate_monitors() -> Vec<MonitorInfo> {
    unsafe extern "system" fn enum_proc(
        hmonitor: HMONITOR,
        _hdc: HDC,
        _rect: *mut RECT,
        lparam: LPARAM,
    ) -> BOOL {
        let list = &mut *(lparam as *mut Vec<HMONITOR>);
        list.push(hmonitor);
        TRUE
    }

    let mut handles: Vec<HMONITOR> = Vec::new();
    unsafe {
        EnumDisplayMonitors(
            std::ptr::null_mut(),
            std::ptr::null(),
            Some(enum_proc),
            &mut handles as *mut Vec<HMONITOR> as LPARAM,
        );
    }

    let mut monitors = Vec::new();
    for hmonitor in handles {
        let mut info: MONITORINFOEXW = unsafe { mem::zeroed() };
        info.monitorInfo.cbSize = mem::size_of::<MONITORINFOEXW>() as u32;
        let ok = unsafe {
            GetMonitorInfoW(
                hmonitor,
                &mut info as *mut MONITORINFOEXW as *mut MONITORINFO,
            )
        };
        if ok == 0 {
            continue;
        }

        let device_name = wide_to_string(&info.szDevice);
        let rc = info.monitorInfo.rcMonitor;

        // Resolve the stable device interface path; mirrors win-capture, which
        // matches DeviceID first and falls back to szDevice.
        let mut dd: DISPLAY_DEVICEW = unsafe { mem::zeroed() };
        dd.cb = mem::size_of::<DISPLAY_DEVICEW>() as u32;
        let dd_ok = unsafe {
            EnumDisplayDevicesW(
                info.szDevice.as_ptr(),
                0,
                &mut dd,
                EDD_GET_DEVICE_INTERFACE_NAME,
            )
        };

        let (id, alt_id) = if dd_ok != 0 {
            let device_id = wide_to_string(&dd.DeviceID);
            if device_id.is_empty() {
                (device_name.clone(), None)
            } else {
                (device_id, Some(device_name.clone()))
            }
        } else {
            (device_name.clone(), None)
        };

        monitors.push(MonitorInfo {
            id,
            alt_id,
            x: rc.left,
            y: rc.top,
            width: (rc.right - rc.left).max(0) as u32,
            height: (rc.bottom - rc.top).max(0) as u32,
            scale: 1.0,
            is_primary: info.monitorInfo.dwFlags & MONITORINFOF_PRIMARY != 0,
        });
    }
    monitors
}

pub fn find_monitor(id: &str) -> Option<MonitorInfo> {
    super::match_monitor(id, &enumerate_monitors())
}

/// Cursor position (physical px — the process is per-monitor-v2 DPI aware),
/// button state, and the DPI zoom of the monitor under the cursor.
///
/// `GetAsyncKeyState`'s high bit is the *current* physical down state, so a
/// click is caught as long as the button is still held on some tick — the same
/// sampling the C++ original does.
pub fn get_mouse_info() -> MouseInfo {
    let mut p = POINT { x: 0, y: 0 };
    let got = unsafe { GetCursorPos(&mut p) };
    if got == 0 {
        return MouseInfo {
            x: 0.0,
            y: 0.0,
            pressed: false,
            scale: 1.0,
        };
    }

    let down = |vk: i32| unsafe { (GetAsyncKeyState(vk) as u16 & 0x8000) != 0 };
    let pressed = down(VK_LBUTTON as i32) || down(VK_RBUTTON as i32);

    // Per-monitor DPI: 96 = 100% scaling, so dpi/96 is the zoom factor.
    let mut dpi_x: u32 = 96;
    let mut dpi_y: u32 = 96;
    let hmon = unsafe { MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST) };
    if !hmon.is_null() {
        let hr = unsafe { GetDpiForMonitor(hmon, MDT_DEFAULT, &mut dpi_x, &mut dpi_y) };
        if hr < 0 || dpi_x == 0 {
            dpi_x = 96;
        }
    }

    MouseInfo {
        x: p.x as f64,
        y: p.y as f64,
        pressed,
        scale: dpi_x as f64 / 96.0,
    }
}

/// The stock cursor table: `(HCURSOR, kind)` pairs cached from
/// `LoadCursorW(NULL, IDC_*)`. Stock cursor handles are process-global
/// constants (LoadCursorW with a null module returns the same shared handle
/// every call), so caching once is sound. Entries whose cursor fails to load
/// are skipped (IDC_PERSON / IDC_PIN are Win10-era additions and may be
/// absent from some cursor themes) — an unmatched handle reads as `custom`.
fn stock_cursor_table() -> &'static Vec<(isize, CursorKind)> {
    use std::sync::OnceLock;
    static TABLE: OnceLock<Vec<(isize, CursorKind)>> = OnceLock::new();
    TABLE.get_or_init(|| {
        // IDC_PIN (the Win10 "location/pin" cursor) is the closest stock match
        // for the wire contract's `pen` kind — there is no IDC_PEN.
        let ids: [(windows_sys::core::PCWSTR, CursorKind); 16] = [
            (IDC_ARROW, CursorKind::Arrow),
            (IDC_IBEAM, CursorKind::IBeam),
            (IDC_WAIT, CursorKind::Wait),
            (IDC_CROSS, CursorKind::Cross),
            (IDC_UPARROW, CursorKind::UpArrow),
            (IDC_SIZENWSE, CursorKind::SizeNwse),
            (IDC_SIZENESW, CursorKind::SizeNesw),
            (IDC_SIZEWE, CursorKind::SizeWe),
            (IDC_SIZENS, CursorKind::SizeNs),
            (IDC_SIZEALL, CursorKind::SizeAll),
            (IDC_NO, CursorKind::No),
            (IDC_HAND, CursorKind::Hand),
            (IDC_APPSTARTING, CursorKind::AppStarting),
            (IDC_HELP, CursorKind::Help),
            (IDC_PIN, CursorKind::Pen),
            (IDC_PERSON, CursorKind::Person),
        ];
        ids.into_iter()
            .filter_map(|(id, kind)| {
                let h = unsafe { LoadCursorW(std::ptr::null_mut(), id) };
                (!h.is_null()).then_some((h as isize, kind))
            })
            .collect()
    })
}

/// Cursor position + classified shape in one `GetCursorInfo` call (position,
/// showing flag and the live HCURSOR come from the same snapshot, so the
/// classification — and the sprite rasterized from `handle` — can never
/// disagree with the sampled position).
pub fn get_cursor_state() -> CursorState {
    let mut info: CURSORINFO = unsafe { mem::zeroed() };
    info.cbSize = mem::size_of::<CURSORINFO>() as u32;
    if unsafe { GetCursorInfo(&mut info) } == 0 {
        return CursorState {
            x: 0,
            y: 0,
            kind: CursorKind::Hidden,
            handle: 0,
        };
    }
    let kind = if info.flags & CURSOR_SHOWING == 0 {
        CursorKind::Hidden
    } else {
        let h = info.hCursor as isize;
        stock_cursor_table()
            .iter()
            .find(|(handle, _)| *handle == h)
            .map(|(_, kind)| *kind)
            .unwrap_or(CursorKind::Custom)
    };
    CursorState {
        x: info.ptScreenPos.x,
        y: info.ptScreenPos.y,
        kind,
        handle: info.hCursor as isize,
    }
}

/// Rasterizes the sampled cursor into a sprite event — see [`sprite`] for the
/// mechanics. Called once per tick with the state returned by
/// [`get_cursor_state`] on the same tick.
pub fn take_cursor_sprite(state: &CursorState) -> SpriteEvent {
    sprite::take(state)
}

/// HCURSOR → [`RawSprite`] rasterization for the sidecar's `cursor_image`
/// rows.
///
/// The tick thread calls [`take`] once per rendered frame with the same
/// `GetCursorInfo` snapshot the frame row records. Steady state is a handle
/// comparison plus one `GetCursorFrameInfo` probe; pixels are only read when
/// the handle changes or the cursor is animated (~50–150 µs — the writer
/// thread's content-hash dedupe keeps repeated animation frames to one row
/// each).
///
/// DPI: `GetIconInfo` yields the size the shared handle holds — the system
/// cursor size (logon DPI × the accessibility size multiplier), which is
/// physical pixels on uniform-DPI machines. Mixed-DPI parity is an open
/// verification item, deliberately not compensated for here.
mod sprite {
    use std::ffi::c_void;
    use std::mem;
    use std::sync::{Mutex, OnceLock};
    use std::time::Instant;

    use windows_sys::Win32::Graphics::Gdi::{
        CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject, GdiFlush, GetDC, GetDIBits,
        GetObjectW, ReleaseDC, SelectObject, BITMAP, BITMAPINFO, BITMAPINFOHEADER, BI_RGB,
        DIB_RGB_COLORS, HBITMAP, HDC, RGBQUAD,
    };
    use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CopyIcon, DestroyIcon, DrawIconEx, GetIconInfo, DI_NORMAL, HCURSOR, HICON, ICONINFO,
    };

    use super::{CursorKind, CursorState};
    use crate::cursor_sprite::{
        decompose_masked_color, decompose_mono, has_alpha, mono_stride, split_mono_planes,
        RawSprite, SpriteEvent, SpritePixels,
    };

    /// The last handle whose sprite actually reached the writer, plus when it
    /// was first seen (the animation clock's epoch, so `istep` starts at
    /// frame 0 whenever the cursor changes). Tick-thread-only in practice; the
    /// mutex just satisfies `static` soundness.
    struct Tracker {
        handle: isize,
        since: Instant,
    }

    static TRACKER: Mutex<Option<Tracker>> = Mutex::new(None);

    pub fn take(state: &CursorState) -> SpriteEvent {
        if state.kind == CursorKind::Hidden || state.handle == 0 {
            // A `Hidden` event makes the writer drop its `ci` ref, and only a
            // fresh `Candidate` can restore it — so the tracker must forget
            // the handle too. Otherwise a cursor that auto-hides and comes
            // back as the same HCURSOR (typing, fullscreen video) would read
            // as `Unchanged` and pin the ref absent until the shape changes.
            *TRACKER.lock().unwrap_or_else(|e| e.into_inner()) = None;
            return SpriteEvent::Hidden;
        }

        let tracker = TRACKER.lock().unwrap_or_else(|e| e.into_inner());
        let (changed, since) = match tracker.as_ref() {
            Some(t) if t.handle == state.handle => (false, t.since),
            _ => (true, Instant::now()),
        };
        drop(tracker);

        let hcur = state.handle as HCURSOR;
        let event = match frame_info(hcur) {
            // A static cursor whose handle did not change is byte-identical to
            // what the writer already holds — skip the pixel work entirely.
            AnimInfo::Static if !changed => return SpriteEvent::Unchanged,
            AnimInfo::Static => rasterize_copied(hcur, state.kind),
            AnimInfo::Animated {
                rate_jiffies,
                frames,
            } => {
                // Frame pick per the ANI clock: rate is in jiffies (1/60 s).
                let elapsed_ms = since.elapsed().as_millis() as u64;
                let istep =
                    ((elapsed_ms * 60) / (rate_jiffies.max(1) as u64 * 1000)) % frames as u64;
                let frame = animation_frame(hcur, istep as u32).unwrap_or(hcur);
                rasterize_copied(frame, state.kind)
            }
            // `GetCursorFrameInfo` unavailable: animation cannot be detected,
            // so degrade to rasterizing every tick — the writer's dedupe makes
            // the steady state one hash per frame, no extra rows.
            AnimInfo::Unknown => rasterize_copied(hcur, state.kind),
        };

        // The tracker only advances once pixels were actually produced; a
        // rasterization failure clears it instead, so the next tick retries
        // rather than reporting `Unchanged` for a sprite the writer never
        // received (or for a stale handle the cursor may flip back to).
        *TRACKER.lock().unwrap_or_else(|e| e.into_inner()) = match event {
            SpriteEvent::Candidate(_) => Some(Tracker {
                handle: state.handle,
                since,
            }),
            _ => None,
        };
        event
    }

    /// `CopyIcon` → rasterize → `DestroyIcon`. The copy is not optional: the
    /// sampled handle belongs to the foreground app, which may destroy it at
    /// any moment. Failure degrades to `Hidden` (frame rows drop their `ci`
    /// ref — the wire contract's "unavailable" case) rather than `Unchanged`,
    /// which would pin the ref to a sprite of the *previous* shape.
    fn rasterize_copied(hcur: HCURSOR, kind: CursorKind) -> SpriteEvent {
        let copy = unsafe { CopyIcon(hcur) };
        if copy.is_null() {
            return SpriteEvent::Hidden;
        }
        let sprite = rasterize_icon(copy, kind);
        unsafe { DestroyIcon(copy) };
        match sprite {
            Some(s) => SpriteEvent::Candidate(s),
            None => SpriteEvent::Hidden,
        }
    }

    // -- animation detection (GetCursorFrameInfo) ---------------------------

    enum AnimInfo {
        /// `GetCursorFrameInfo` is not exported on this Windows build.
        Unknown,
        Static,
        Animated {
            rate_jiffies: u32,
            frames: u32,
        },
    }

    /// `user32!GetCursorFrameInfo` — undocumented but stable since XP (prior
    /// art in OBS-adjacent recorders). Returns the HCURSOR of animation step
    /// `istep` and reports the step rate (jiffies) and step count.
    type GetCursorFrameInfoFn =
        unsafe extern "system" fn(HCURSOR, u32, u32, *mut u32, *mut u32) -> HCURSOR;

    /// Resolved once; `None` pins [`AnimInfo::Unknown`] for the process.
    fn frame_info_fn() -> Option<GetCursorFrameInfoFn> {
        static F: OnceLock<Option<GetCursorFrameInfoFn>> = OnceLock::new();
        *F.get_or_init(|| unsafe {
            // user32 is guaranteed resident (this module links GetCursorInfo).
            let wide: Vec<u16> = "user32.dll".encode_utf16().chain(Some(0)).collect();
            let user32 = GetModuleHandleW(wide.as_ptr());
            if user32.is_null() {
                return None;
            }
            GetProcAddress(user32, c"GetCursorFrameInfo".as_ptr() as *const u8).map(|p| {
                mem::transmute::<unsafe extern "system" fn() -> isize, GetCursorFrameInfoFn>(p)
            })
        })
    }

    fn frame_info(hcur: HCURSOR) -> AnimInfo {
        let Some(f) = frame_info_fn() else {
            return AnimInfo::Unknown;
        };
        let mut rate_jiffies: u32 = 0;
        let mut frames: u32 = 0;
        let frame = unsafe { f(hcur, 0, 0, &mut rate_jiffies, &mut frames) };
        if frame.is_null() || frames <= 1 {
            AnimInfo::Static
        } else {
            AnimInfo::Animated {
                rate_jiffies,
                frames,
            }
        }
    }

    /// The HCURSOR of animation step `istep`, owned by the cursor (not
    /// destroyed here — only its `CopyIcon` copy is).
    fn animation_frame(hcur: HCURSOR, istep: u32) -> Option<HCURSOR> {
        let f = frame_info_fn()?;
        let mut rate: u32 = 0;
        let mut frames: u32 = 0;
        let frame = unsafe { f(hcur, 0, istep, &mut rate, &mut frames) };
        (!frame.is_null()).then_some(frame)
    }

    // -- rasterization (GetIconInfo + GetDIBits) ----------------------------

    /// Exact-pixel rasterization via `GetIconInfo`. The ICONINFO bitmaps are
    /// *copies* owned by the caller, so both are deleted on every path.
    fn rasterize_icon(icon: HICON, kind: CursorKind) -> Option<RawSprite> {
        unsafe {
            let mut ii: ICONINFO = mem::zeroed();
            if GetIconInfo(icon, &mut ii) == 0 {
                return None;
            }
            let sprite = read_icon_planes(icon, &ii, kind);
            if !ii.hbmMask.is_null() {
                DeleteObject(ii.hbmMask);
            }
            if !ii.hbmColor.is_null() {
                DeleteObject(ii.hbmColor);
            }
            sprite
        }
    }

    unsafe fn read_icon_planes(icon: HICON, ii: &ICONINFO, kind: CursorKind) -> Option<RawSprite> {
        let hdc = GetDC(std::ptr::null_mut());
        if hdc.is_null() {
            return None;
        }
        let sprite = read_icon_planes_with_dc(hdc, icon, ii, kind);
        ReleaseDC(std::ptr::null_mut(), hdc);
        sprite
    }

    unsafe fn read_icon_planes_with_dc(
        hdc: HDC,
        icon: HICON,
        ii: &ICONINFO,
        kind: CursorKind,
    ) -> Option<RawSprite> {
        let make = |w: u32, h: u32, bmp: Vec<u8>, mask: Option<Vec<u8>>| RawSprite {
            kind: kind.as_str(),
            w,
            h,
            hotx: ii.xHotspot as i32,
            hoty: ii.yHotspot as i32,
            bmp: SpritePixels::Bgra(bmp),
            mask,
        };

        if ii.hbmColor.is_null() {
            // Mono cursor: hbmMask is double height — the AND plane stacked on
            // top of the XOR plane.
            let (w, full_h) = bitmap_size(ii.hbmMask)?;
            let h = full_h / 2;
            if h == 0 {
                return None;
            }
            let stride = mono_stride(w);
            let mut planes = vec![0u8; stride * full_h as usize];
            if !get_dibits(hdc, ii.hbmMask, w, full_h, 1, &mut planes) {
                return draw_icon_fallback(hdc, icon, w, h, kind, ii);
            }
            let (and, xor) = split_mono_planes(&planes, h, stride)?;
            let (bmp, mask) = decompose_mono(and, xor, w, h, stride);
            return Some(make(w, h, bmp, Some(mask)));
        }

        let (w, h) = bitmap_size(ii.hbmColor)?;
        let mut color = vec![0u8; (w as usize) * (h as usize) * 4];
        if !get_dibits(hdc, ii.hbmColor, w, h, 32, &mut color) {
            return draw_icon_fallback(hdc, icon, w, h, kind, ii);
        }
        if has_alpha(&color) {
            // Modern alpha cursor: the AND mask is vestigial.
            return Some(make(w, h, color, None));
        }
        // Legacy masked color cursor: a single-height AND mask decides pixel
        // ownership (and carries any XOR region).
        let stride = mono_stride(w);
        let mut and = vec![0u8; stride * h as usize];
        if !get_dibits(hdc, ii.hbmMask, w, h, 1, &mut and) {
            return draw_icon_fallback(hdc, icon, w, h, kind, ii);
        }
        let (bmp, mask) = decompose_masked_color(&color, &and, w, h, stride);
        Some(make(w, h, bmp, mask))
    }

    /// Width/height off `GetObjectW` — the only reliable dimension source for
    /// a handle whose creator we do not control.
    unsafe fn bitmap_size(hbm: HBITMAP) -> Option<(u32, u32)> {
        let mut bm: BITMAP = mem::zeroed();
        let read = GetObjectW(
            hbm,
            mem::size_of::<BITMAP>() as i32,
            &mut bm as *mut BITMAP as *mut c_void,
        );
        if read == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0 {
            return None;
        }
        Some((bm.bmWidth as u32, bm.bmHeight as u32))
    }

    /// `BITMAPINFO` with room for the 2-entry color table a 1bpp `GetDIBits`
    /// writes back (the windows-sys struct only reserves one entry).
    #[repr(C)]
    struct BitmapInfo2 {
        header: BITMAPINFOHEADER,
        colors: [RGBQUAD; 2],
    }

    /// Reads a bitmap's pixels top-down (negative `biHeight`) at the given bit
    /// depth into `buf`, which must already have the right stride × height
    /// size (DWORD-aligned rows for 1bpp — see [`mono_stride`]).
    unsafe fn get_dibits(hdc: HDC, hbm: HBITMAP, w: u32, h: u32, bpp: u16, buf: &mut [u8]) -> bool {
        let mut bi: BitmapInfo2 = mem::zeroed();
        bi.header.biSize = mem::size_of::<BITMAPINFOHEADER>() as u32;
        bi.header.biWidth = w as i32;
        bi.header.biHeight = -(h as i32);
        bi.header.biPlanes = 1;
        bi.header.biBitCount = bpp;
        bi.header.biCompression = BI_RGB as u32;
        GetDIBits(
            hdc,
            hbm,
            0,
            h,
            buf.as_mut_ptr() as *mut c_void,
            &mut bi as *mut BitmapInfo2 as *mut BITMAPINFO,
            DIB_RGB_COLORS,
        ) as u32
            == h
    }

    // -- DrawIconEx fallback ------------------------------------------------

    /// Last-resort rasterization when `GetDIBits` fails: render the icon over
    /// black and over white and recover per pixel `alpha = 255 − (W − B)`,
    /// `color = B`; a channel where `W < B` means the icon inverted the
    /// background, so the pixel goes to the mask (white) instead of being
    /// dropped the way `SystemCursorImage` used to.
    unsafe fn draw_icon_fallback(
        hdc: HDC,
        icon: HICON,
        w: u32,
        h: u32,
        kind: CursorKind,
        ii: &ICONINFO,
    ) -> Option<RawSprite> {
        let black = render_on(hdc, icon, w, h, 0x00)?;
        let white = render_on(hdc, icon, w, h, 0xFF)?;

        let px = (w as usize) * (h as usize) * 4;
        let mut bmp = vec![0u8; px];
        let mut mask = vec![0u8; px];
        let mut any_mask = false;
        for i in (0..px).step_by(4) {
            let db = white[i] as i32 - black[i] as i32;
            let dg = white[i + 1] as i32 - black[i + 1] as i32;
            let dr = white[i + 2] as i32 - black[i + 2] as i32;
            // A clearly darker render over white = background inversion. The
            // threshold tolerates DrawIconEx rounding on ordinary pixels.
            if db < -16 || dg < -16 || dr < -16 {
                mask[i..i + 4].copy_from_slice(&[255, 255, 255, 255]);
                any_mask = true;
                continue;
            }
            let alpha = 255 - ((db + dg + dr) / 3).clamp(0, 255);
            if alpha > 0 {
                bmp[i..i + 4].copy_from_slice(&[black[i], black[i + 1], black[i + 2], alpha as u8]);
            }
        }
        Some(RawSprite {
            kind: kind.as_str(),
            w,
            h,
            hotx: ii.xHotspot as i32,
            hoty: ii.yHotspot as i32,
            bmp: SpritePixels::Bgra(bmp),
            mask: any_mask.then_some(mask),
        })
    }

    /// Draws the icon over a solid background (`bg` per channel) into a 32bpp
    /// top-down DIB section and returns its BGRA bytes.
    unsafe fn render_on(hdc: HDC, icon: HICON, w: u32, h: u32, bg: u8) -> Option<Vec<u8>> {
        let memdc = CreateCompatibleDC(hdc);
        if memdc.is_null() {
            return None;
        }
        let result = (|| {
            let mut bi: BitmapInfo2 = mem::zeroed();
            bi.header.biSize = mem::size_of::<BITMAPINFOHEADER>() as u32;
            bi.header.biWidth = w as i32;
            bi.header.biHeight = -(h as i32);
            bi.header.biPlanes = 1;
            bi.header.biBitCount = 32;
            bi.header.biCompression = BI_RGB as u32;
            let mut bits: *mut c_void = std::ptr::null_mut();
            let dib = CreateDIBSection(
                memdc,
                &bi as *const BitmapInfo2 as *const BITMAPINFO,
                DIB_RGB_COLORS,
                &mut bits,
                std::ptr::null_mut(),
                0,
            );
            if dib.is_null() || bits.is_null() {
                return None;
            }
            let len = (w as usize) * (h as usize) * 4;
            let old = SelectObject(memdc, dib);
            std::ptr::write_bytes(bits as *mut u8, bg, len);
            let drawn = DrawIconEx(
                memdc,
                0,
                0,
                icon,
                w as i32,
                h as i32,
                0,
                std::ptr::null_mut(),
                DI_NORMAL,
            );
            GdiFlush();
            let out =
                (drawn != 0).then(|| std::slice::from_raw_parts(bits as *const u8, len).to_vec());
            SelectObject(memdc, old);
            DeleteObject(dib);
            out
        })();
        DeleteDC(memdc);
        result
    }
}

/// The display's DPI zoom (dpi/96) — the input-capture header's per-monitor
/// `scale`, which the editor uses to size themed cursors like the OS does.
/// Distinct from `MonitorInfo::scale` (capture px per coordinate unit, always
/// 1.0 on Windows).
pub fn monitor_display_scale(m: &MonitorInfo) -> f64 {
    let center = POINT {
        x: m.x + (m.width as i32 / 2),
        y: m.y + (m.height as i32 / 2),
    };
    let hmon = unsafe { MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST) };
    if hmon.is_null() {
        return 1.0;
    }
    let mut dpi_x: u32 = 96;
    let mut dpi_y: u32 = 96;
    let hr = unsafe { GetDpiForMonitor(hmon, MDT_DEFAULT, &mut dpi_x, &mut dpi_y) };
    if hr < 0 || dpi_x == 0 {
        return 1.0;
    }
    dpi_x as f64 / 96.0
}

pub fn display_capture_settings(m: &MonitorInfo, show_cursor: bool) -> ObsData {
    let settings = ObsData::new();
    settings.set_string("monitor_id", &m.id);
    // 2 = WGC. Deliberate deviation from the design's `0` (auto): auto prefers
    // the DXGI duplicator, which was verified to produce black frames on this
    // Win11 26H1 + NVIDIA machine, while WGC captures correctly. Requesting
    // WGC is safe everywhere — win-capture's choose_method() force-falls back
    // to DXGI when WGC is unsupported (duplicator-monitor-capture.c).
    settings.set_int("method", 2);
    settings.set_bool("capture_cursor", show_cursor);
    settings
}

/// Partial `obs_source_update` payload toggling cursor capture on an existing
/// display source (win-capture applies it live).
pub fn cursor_update_settings(show_cursor: bool) -> ObsData {
    let settings = ObsData::new();
    settings.set_bool("capture_cursor", show_cursor);
    settings
}

/// Source id + settings for a speaker (output) capture source. Must be called
/// after modules are loaded.
pub fn audio_output_capture(device_id: &str) -> (&'static str, ObsData) {
    let settings = ObsData::new();
    settings.set_string("device_id", device_id);
    ("wasapi_output_capture", settings)
}

/// Settings for a `WEBCAM_SOURCE_ID` instance capturing `device_id` (a value
/// exactly as printed by `--list-cameras`, i.e. win-dshow's escaped
/// `<name>:<path>` form — no re-escaping happens here).
pub fn webcam_settings(device_id: &str) -> ObsData {
    let settings = ObsData::new();
    settings.set_string(WEBCAM_DEVICE_KEY, device_id);
    // 0 = the device's preferred resolution/format.
    settings.set_int("res_type", 0);
    settings.set_bool("active", true);
    // Hidden win-dshow knob: block source creation until the device is
    // actually running, so the caller's frame-size poll usually succeeds at
    // once.
    settings.set_bool("synchronous_activate", true);
    settings
}

pub fn default_obs_paths(exe_dir: &Path) -> ObsPaths {
    let module_bin = env::var("OBS_PLUGIN_PATH").unwrap_or_else(|_| {
        exe_dir
            .join("obs-plugins")
            .join("64bit")
            .to_string_lossy()
            .into_owned()
    });
    let module_data_base = env::var("OBS_PLUGIN_DATA_PATH").unwrap_or_else(|_| {
        exe_dir
            .join("data")
            .join("obs-plugins")
            .to_string_lossy()
            .into_owned()
    });
    let libobs_data = env::var("OBS_DATA_PATH")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|_| exe_dir.join("data").join("libobs"));
    ObsPaths {
        module_bin,
        // The %module% suffix is appended internally (§1.6).
        module_data: format!("{module_data_base}/%module%"),
        libobs_data: Some(libobs_data),
    }
}

/// Linear gain that undoes the endpoint's software master volume for a
/// loopback (speaker) capture. On endpoints without hardware volume, Windows
/// applies the volume slider in the audio engine *before* the loopback tap, so
/// every recorder receives pre-attenuated samples; multiplying the source by
/// the inverse restores the played content's level. Returns `1.0` (no
/// compensation) when the device has hardware volume, is muted, or cannot be
/// queried.
pub fn speaker_compensation_gain(device_id: &str) -> f32 {
    match endpoint_volume::software_master_volume_db(device_id) {
        Some(db) => compensation_gain_from_db(db),
        None => 1.0,
    }
}

/// ±30 dB compensation cap: keeps a near-zero volume slider from requesting an
/// absurd boost (the captured signal is float, so the math is lossless, but a
/// >30 dB "restoration" of a slider someone parked at 2% is not what they
/// meant).
const MAX_COMPENSATION_DB: f32 = 30.0;

fn compensation_gain_from_db(master_db: f32) -> f32 {
    if !master_db.is_finite() {
        return 1.0;
    }
    let boost_db = (-master_db).clamp(-MAX_COMPENSATION_DB, MAX_COMPENSATION_DB);
    10f32.powf(boost_db / 20.0)
}

/// Minimal hand-rolled Core Audio endpoint COM client. windows-sys exposes the
/// plain-C COM entry points but not interface vtables, and the full `windows`
/// crate is a heavy dependency for three method calls — so the three vtable
/// prefixes used here are declared manually.
mod endpoint_volume {
    use std::cell::Cell;
    use std::ffi::c_void;
    use std::ptr;

    use windows_sys::core::{GUID, HRESULT, PCWSTR};
    use windows_sys::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, CLSCTX_ALL, COINIT_MULTITHREADED,
    };

    const CLSID_MM_DEVICE_ENUMERATOR: GUID = GUID {
        data1: 0xBCDE0395,
        data2: 0xE52F,
        data3: 0x467C,
        data4: [0x8E, 0x3D, 0xC4, 0x57, 0x92, 0x91, 0x69, 0x2E],
    };
    const IID_IMM_DEVICE_ENUMERATOR: GUID = GUID {
        data1: 0xA95664D2,
        data2: 0x9614,
        data3: 0x4F35,
        data4: [0xA7, 0x46, 0xDE, 0x8D, 0xB6, 0x36, 0x17, 0xE6],
    };
    const IID_IAUDIO_ENDPOINT_VOLUME: GUID = GUID {
        data1: 0x5CDF2C82,
        data2: 0x841E,
        data3: 0x4546,
        data4: [0x97, 0x22, 0x0C, 0xF7, 0x40, 0x78, 0x22, 0x9A],
    };

    const E_RENDER: i32 = 0; // EDataFlow::eRender
    /// ERole::eConsole — the role win-wasapi resolves "default" with, so both
    /// sides always talk about the same device.
    const E_CONSOLE: i32 = 0;
    const ENDPOINT_HARDWARE_SUPPORT_VOLUME: u32 = 0x1;
    const RPC_E_CHANGED_MODE: HRESULT = 0x80010106u32 as HRESULT;

    /// Leading vtable entries shared by every COM interface.
    #[repr(C)]
    struct IUnknownPrefix {
        query_interface: usize,
        add_ref: usize,
        release: unsafe extern "system" fn(*mut c_void) -> u32,
    }

    #[repr(C)]
    struct IMMDeviceEnumeratorVtbl {
        unknown: IUnknownPrefix,
        enum_audio_endpoints: usize,
        get_default_audio_endpoint:
            unsafe extern "system" fn(*mut c_void, i32, i32, *mut *mut c_void) -> HRESULT,
        get_device: unsafe extern "system" fn(*mut c_void, PCWSTR, *mut *mut c_void) -> HRESULT,
    }

    #[repr(C)]
    struct IMMDeviceVtbl {
        unknown: IUnknownPrefix,
        activate: unsafe extern "system" fn(
            *mut c_void,
            *const GUID,
            u32,
            *mut c_void,
            *mut *mut c_void,
        ) -> HRESULT,
    }

    #[repr(C)]
    struct IAudioEndpointVolumeVtbl {
        unknown: IUnknownPrefix,
        register_control_change_notify: usize,
        unregister_control_change_notify: usize,
        get_channel_count: usize,
        set_master_volume_level: usize,
        set_master_volume_level_scalar: usize,
        get_master_volume_level: unsafe extern "system" fn(*mut c_void, *mut f32) -> HRESULT,
        get_master_volume_level_scalar: usize,
        set_channel_volume_level: usize,
        set_channel_volume_level_scalar: usize,
        get_channel_volume_level: usize,
        get_channel_volume_level_scalar: usize,
        set_mute: usize,
        get_mute: unsafe extern "system" fn(*mut c_void, *mut i32) -> HRESULT,
        get_volume_step_info: usize,
        volume_step_up: usize,
        volume_step_down: usize,
        query_hardware_support: unsafe extern "system" fn(*mut c_void, *mut u32) -> HRESULT,
    }

    unsafe fn vtbl<T>(obj: *mut c_void) -> *const T {
        *(obj as *mut *const T)
    }

    unsafe fn com_release(obj: *mut c_void) {
        ((*vtbl::<IUnknownPrefix>(obj)).release)(obj);
    }

    /// Owned COM pointer so every early return releases.
    struct ComPtr(*mut c_void);
    impl Drop for ComPtr {
        fn drop(&mut self) {
            if !self.0.is_null() {
                unsafe { com_release(self.0) };
            }
        }
    }

    /// Per-thread one-time CoInitializeEx. `RPC_E_CHANGED_MODE` (already
    /// initialized STA by someone else) still leaves COM usable. Never
    /// uninitialized — callers are process-lifetime threads and the process
    /// exits via `ExitProcess` anyway.
    fn com_ready() -> bool {
        thread_local! {
            static STATE: Cell<Option<bool>> = const { Cell::new(None) };
        }
        STATE.with(|state| {
            if let Some(ready) = state.get() {
                return ready;
            }
            let hr = unsafe { CoInitializeEx(ptr::null(), COINIT_MULTITHREADED as u32) };
            let ready = hr >= 0 || hr == RPC_E_CHANGED_MODE;
            state.set(Some(ready));
            ready
        })
    }

    /// The endpoint's master volume in dB — but only when Windows applies that
    /// volume in software (no `ENDPOINT_HARDWARE_SUPPORT_VOLUME`), which is
    /// exactly when it lands inside the loopback stream. `None` means "do not
    /// compensate": hardware volume, muted (the capture is silent anyway),
    /// unknown device id, or any COM failure.
    pub fn software_master_volume_db(device_id: &str) -> Option<f32> {
        if !com_ready() {
            return None;
        }
        unsafe {
            let mut enumerator: *mut c_void = ptr::null_mut();
            let hr = CoCreateInstance(
                &CLSID_MM_DEVICE_ENUMERATOR,
                ptr::null_mut(),
                CLSCTX_ALL,
                &IID_IMM_DEVICE_ENUMERATOR,
                &mut enumerator,
            );
            if hr < 0 || enumerator.is_null() {
                return None;
            }
            let enumerator = ComPtr(enumerator);

            let mut device: *mut c_void = ptr::null_mut();
            let ev = vtbl::<IMMDeviceEnumeratorVtbl>(enumerator.0);
            let hr = if device_id == "default" {
                ((*ev).get_default_audio_endpoint)(enumerator.0, E_RENDER, E_CONSOLE, &mut device)
            } else {
                let wide: Vec<u16> = device_id.encode_utf16().chain(Some(0)).collect();
                ((*ev).get_device)(enumerator.0, wide.as_ptr(), &mut device)
            };
            if hr < 0 || device.is_null() {
                return None;
            }
            let device = ComPtr(device);

            let mut volume: *mut c_void = ptr::null_mut();
            let hr = ((*vtbl::<IMMDeviceVtbl>(device.0)).activate)(
                device.0,
                &IID_IAUDIO_ENDPOINT_VOLUME,
                CLSCTX_ALL,
                ptr::null_mut(),
                &mut volume,
            );
            if hr < 0 || volume.is_null() {
                return None;
            }
            let volume = ComPtr(volume);
            let vv = vtbl::<IAudioEndpointVolumeVtbl>(volume.0);

            let mut hw_mask: u32 = 0;
            if ((*vv).query_hardware_support)(volume.0, &mut hw_mask) < 0
                || hw_mask & ENDPOINT_HARDWARE_SUPPORT_VOLUME != 0
            {
                return None;
            }
            let mut muted: i32 = 0;
            if ((*vv).get_mute)(volume.0, &mut muted) < 0 || muted != 0 {
                return None;
            }
            let mut db: f32 = 0.0;
            if ((*vv).get_master_volume_level)(volume.0, &mut db) < 0 {
                return None;
            }
            Some(db)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::compensation_gain_from_db;

    #[test]
    fn compensation_inverts_the_master_volume() {
        // 0 dB (full volume) → unity.
        assert!((compensation_gain_from_db(0.0) - 1.0).abs() < 1e-6);
        // -6.02 dB → ×2.
        assert!((compensation_gain_from_db(-6.0206) - 2.0).abs() < 1e-3);
        // -12.44 dB (the 44% slider that motivated this) → ×4.18.
        assert!((compensation_gain_from_db(-12.444) - 4.18).abs() < 0.01);
        // A boosted endpoint (positive dB) is compensated *down*.
        assert!((compensation_gain_from_db(6.0206) - 0.5).abs() < 1e-3);
    }

    #[test]
    fn compensation_is_capped_at_30_db() {
        assert!((compensation_gain_from_db(-96.0) - 31.6228).abs() < 1e-3);
        assert!((compensation_gain_from_db(96.0) - 0.0316228).abs() < 1e-6);
        // Non-finite input must not produce a NaN gain.
        assert_eq!(compensation_gain_from_db(f32::NAN), 1.0);
        assert_eq!(compensation_gain_from_db(f32::NEG_INFINITY), 1.0);
    }
}

pub fn exit_process(code: i32) -> ! {
    unsafe {
        ExitProcess(code as u32);
    }
    #[allow(unreachable_code)]
    loop {
        std::thread::park();
    }
}

fn wide_to_string(buf: &[u16]) -> String {
    let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..len])
}
