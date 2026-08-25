//! Windows platform implementation (DESIGN §2.2) — the recorder-specific
//! remainder: cursor/mouse sampling and audio/webcam helpers. The monitor /
//! paths / display-capture layer moved to the shared `obs-platform` crate
//! (SHARE_REGION_PLAN §4.3).

use std::mem;

use obs::data::ObsData;
use windows::Win32::Foundation::POINT;
use windows::Win32::Graphics::Gdi::{MonitorFromPoint, MONITOR_DEFAULTTONEAREST};
use windows::Win32::UI::HiDpi::{GetDpiForMonitor, MDT_DEFAULT};
use windows::Win32::UI::Input::KeyboardAndMouse::{GetAsyncKeyState, VK_LBUTTON, VK_RBUTTON};
use windows::Win32::UI::WindowsAndMessaging::{
    GetCursorInfo, GetCursorPos, LoadCursorW, CURSORINFO, CURSOR_SHOWING, IDC_APPSTARTING,
    IDC_ARROW, IDC_CROSS, IDC_HAND, IDC_HELP, IDC_IBEAM, IDC_NO, IDC_PERSON, IDC_PIN, IDC_SIZEALL,
    IDC_SIZENESW, IDC_SIZENS, IDC_SIZENWSE, IDC_SIZEWE, IDC_UPARROW, IDC_WAIT,
};

use crate::cursor_sprite::SpriteEvent;

use super::{CursorKind, CursorState, MouseInfo};

pub const AUDIO_INPUT_CAPTURE_ID: &str = "wasapi_input_capture";
/// Webcam capture source (`--webcam` / `--list-cameras`): DirectShow.
pub const WEBCAM_SOURCE_ID: &str = "dshow_input";
/// The `WEBCAM_SOURCE_ID` settings key (and property) holding the device id.
pub const WEBCAM_DEVICE_KEY: &str = "video_device_id";

/// Cursor position (physical px — the process is per-monitor-v2 DPI aware),
/// button state, and the DPI zoom of the monitor under the cursor.
///
/// `GetAsyncKeyState`'s high bit is the *current* physical down state, so a
/// click is caught as long as the button is still held on some tick — the same
/// sampling the C++ original does.
pub fn get_mouse_info() -> MouseInfo {
    let mut p = POINT { x: 0, y: 0 };
    if unsafe { GetCursorPos(&mut p) }.is_err() {
        return MouseInfo {
            x: 0.0,
            y: 0.0,
            pressed: false,
            scale: 1.0,
        };
    }

    let down = |vk: i32| unsafe { (GetAsyncKeyState(vk) as u16 & 0x8000) != 0 };
    let pressed = down(VK_LBUTTON.0 as i32) || down(VK_RBUTTON.0 as i32);

    // Per-monitor DPI: 96 = 100% scaling, so dpi/96 is the zoom factor.
    let mut dpi_x: u32 = 96;
    let mut dpi_y: u32 = 96;
    let hmon = unsafe { MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST) };
    if !hmon.is_invalid() {
        let ok = unsafe { GetDpiForMonitor(hmon, MDT_DEFAULT, &mut dpi_x, &mut dpi_y) };
        if ok.is_err() || dpi_x == 0 {
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
        let ids: [(windows::core::PCWSTR, CursorKind); 16] = [
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
                let h = unsafe { LoadCursorW(None, id) }.ok()?;
                Some((h.0 as isize, kind))
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
    if unsafe { GetCursorInfo(&mut info) }.is_err() {
        return CursorState {
            x: 0,
            y: 0,
            kind: CursorKind::Hidden,
            handle: 0,
        };
    }
    let kind = if info.flags.0 & CURSOR_SHOWING.0 == 0 {
        CursorKind::Hidden
    } else {
        let h = info.hCursor.0 as isize;
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
        handle: info.hCursor.0 as isize,
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

    use windows::core::{s, w};
    use windows::Win32::Graphics::Gdi::{
        CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject, GdiFlush, GetDC, GetDIBits,
        GetObjectW, ReleaseDC, SelectObject, BITMAP, BITMAPINFO, BITMAPINFOHEADER, BI_RGB,
        DIB_RGB_COLORS, HBITMAP, HDC, RGBQUAD,
    };
    use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
    use windows::Win32::UI::WindowsAndMessaging::{
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

        let hcur = HCURSOR(state.handle as *mut c_void);
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
        let Ok(copy) = (unsafe { CopyIcon(hcur.into()) }) else {
            return SpriteEvent::Hidden;
        };
        let sprite = rasterize_icon(copy, kind);
        let _ = unsafe { DestroyIcon(copy) };
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
    /// `istep` and reports the step rate (jiffies) and step count. The export
    /// has no `windows`-crate binding, so it stays a GetProcAddress-resolved
    /// raw fn pointer.
    type GetCursorFrameInfoFn =
        unsafe extern "system" fn(HCURSOR, u32, u32, *mut u32, *mut u32) -> HCURSOR;

    /// Resolved once; `None` pins [`AnimInfo::Unknown`] for the process.
    fn frame_info_fn() -> Option<GetCursorFrameInfoFn> {
        static F: OnceLock<Option<GetCursorFrameInfoFn>> = OnceLock::new();
        *F.get_or_init(|| unsafe {
            // user32 is guaranteed resident (this module links GetCursorInfo).
            let user32 = GetModuleHandleW(w!("user32.dll")).ok()?;
            GetProcAddress(user32, s!("GetCursorFrameInfo")).map(|p| {
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
        if frame.is_invalid() || frames <= 1 {
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
        (!frame.is_invalid()).then_some(frame)
    }

    // -- rasterization (GetIconInfo + GetDIBits) ----------------------------

    /// Exact-pixel rasterization via `GetIconInfo`. The ICONINFO bitmaps are
    /// *copies* owned by the caller, so both are deleted on every path.
    fn rasterize_icon(icon: HICON, kind: CursorKind) -> Option<RawSprite> {
        unsafe {
            let mut ii: ICONINFO = mem::zeroed();
            if GetIconInfo(icon, &mut ii).is_err() {
                return None;
            }
            let sprite = read_icon_planes(icon, &ii, kind);
            if !ii.hbmMask.is_invalid() {
                let _ = DeleteObject(ii.hbmMask.into());
            }
            if !ii.hbmColor.is_invalid() {
                let _ = DeleteObject(ii.hbmColor.into());
            }
            sprite
        }
    }

    unsafe fn read_icon_planes(icon: HICON, ii: &ICONINFO, kind: CursorKind) -> Option<RawSprite> {
        let hdc = GetDC(None);
        if hdc.is_invalid() {
            return None;
        }
        let sprite = read_icon_planes_with_dc(hdc, icon, ii, kind);
        ReleaseDC(None, hdc);
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

        if ii.hbmColor.is_invalid() {
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
            hbm.into(),
            mem::size_of::<BITMAP>() as i32,
            Some(&mut bm as *mut BITMAP as *mut c_void),
        );
        if read == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0 {
            return None;
        }
        Some((bm.bmWidth as u32, bm.bmHeight as u32))
    }

    /// `BITMAPINFO` with room for the 2-entry color table a 1bpp `GetDIBits`
    /// writes back (the `windows`-crate struct only reserves one entry).
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
        bi.header.biCompression = BI_RGB.0;
        GetDIBits(
            hdc,
            hbm,
            0,
            h,
            Some(buf.as_mut_ptr() as *mut c_void),
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
        let memdc = CreateCompatibleDC(Some(hdc));
        if memdc.is_invalid() {
            return None;
        }
        let result = (|| {
            let mut bi: BitmapInfo2 = mem::zeroed();
            bi.header.biSize = mem::size_of::<BITMAPINFOHEADER>() as u32;
            bi.header.biWidth = w as i32;
            bi.header.biHeight = -(h as i32);
            bi.header.biPlanes = 1;
            bi.header.biBitCount = 32;
            bi.header.biCompression = BI_RGB.0;
            let mut bits: *mut c_void = std::ptr::null_mut();
            let dib = CreateDIBSection(
                Some(memdc),
                &bi as *const BitmapInfo2 as *const BITMAPINFO,
                DIB_RGB_COLORS,
                &mut bits,
                None,
                0,
            )
            .ok()?;
            if bits.is_null() {
                return None;
            }
            let len = (w as usize) * (h as usize) * 4;
            let old = SelectObject(memdc, dib.into());
            std::ptr::write_bytes(bits as *mut u8, bg, len);
            let drawn = DrawIconEx(
                memdc,
                0,
                0,
                icon,
                w as i32,
                h as i32,
                0,
                None,
                DI_NORMAL,
            );
            let _ = GdiFlush();
            let out = drawn
                .is_ok()
                .then(|| std::slice::from_raw_parts(bits as *const u8, len).to_vec());
            SelectObject(memdc, old);
            let _ = DeleteObject(dib.into());
            out
        })();
        let _ = DeleteDC(memdc);
        result
    }
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
/// \>30 dB "restoration" of a slider someone parked at 2% is not what they
/// meant).
const MAX_COMPENSATION_DB: f32 = 30.0;

fn compensation_gain_from_db(master_db: f32) -> f32 {
    if !master_db.is_finite() {
        return 1.0;
    }
    let boost_db = (-master_db).clamp(-MAX_COMPENSATION_DB, MAX_COMPENSATION_DB);
    10f32.powf(boost_db / 20.0)
}

/// Core Audio endpoint volume query via the `windows` crate's generated COM
/// bindings (which replaced the hand-rolled vtable prefixes windows-sys
/// forced). The interface wrappers `Release` on drop, so every early return
/// still balances the refcounts.
mod endpoint_volume {
    use std::cell::Cell;

    use windows::core::PCWSTR;
    use windows::Win32::Foundation::RPC_E_CHANGED_MODE;
    use windows::Win32::Media::Audio::Endpoints::IAudioEndpointVolume;
    use windows::Win32::Media::Audio::{
        eConsole, eRender, IMMDeviceEnumerator, MMDeviceEnumerator,
        ENDPOINT_HARDWARE_SUPPORT_VOLUME,
    };
    use windows::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, CLSCTX_ALL, COINIT_MULTITHREADED,
    };

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
            let hr = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
            let ready = hr.is_ok() || hr == RPC_E_CHANGED_MODE;
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
            let enumerator: IMMDeviceEnumerator =
                CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).ok()?;

            let device = if device_id == "default" {
                // eConsole — the role win-wasapi resolves "default" with, so
                // both sides always talk about the same device.
                enumerator.GetDefaultAudioEndpoint(eRender, eConsole).ok()?
            } else {
                let wide: Vec<u16> = device_id.encode_utf16().chain(Some(0)).collect();
                enumerator.GetDevice(PCWSTR::from_raw(wide.as_ptr())).ok()?
            };

            let volume: IAudioEndpointVolume = device.Activate(CLSCTX_ALL, None).ok()?;

            let hw_mask = volume.QueryHardwareSupport().ok()?;
            if hw_mask & ENDPOINT_HARDWARE_SUPPORT_VOLUME != 0 {
                return None;
            }
            if volume.GetMute().ok()?.as_bool() {
                return None;
            }
            volume.GetMasterVolumeLevel().ok()
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

