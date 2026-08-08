//! One screenshot of the fixed capture region.
//!
//! This is a near-copy of the desktop grab in `system/win_capture.rs`
//! (`capture_desktop`, and `read_dibits_bgra` below it): screen DC →
//! `CreateCompatibleDC` → `BitBlt` → `GetDIBits` into a top-down 32-bit
//! DIB. It is duplicated rather than shared because the RAII guards there
//! (`BoxHDC`, `BoxHBITMAP`) and the `GetDIBits` wrapper are `pub(super)` to
//! `system` — reaching them from here would mean widening three types'
//! visibility for one caller.
//!
//! Two deliberate differences from the original:
//!
//! - The blit is of a sub-rect of the virtual desktop, not all of it. Same
//!   coordinate space (physical virtual-desktop pixels), so the region from
//!   `action.txt` goes straight in.
//! - No `CAPTUREBLT`. That flag re-composites layered windows into the
//!   result — a screen-wide redraw the settle loop would provoke twenty
//!   times a second — and can drag the mouse cursor into the captured bits.
//!   The driver parks the real cursor *inside* the region for the whole run,
//!   so a cursor blob baked into every frame is exactly what we must not
//!   have: the stitcher would have to register around it.

use anyhow::Result;
use std::mem;
use windows::Win32::{
    Foundation::HWND,
    Graphics::Gdi::{
        BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject, GetDIBits, GetWindowDC, ReleaseDC, SelectObject,
        BITMAPINFO, BITMAPINFOHEADER, DIB_RGB_COLORS, HBITMAP, HDC, SRCCOPY,
    },
    UI::WindowsAndMessaging::GetDesktopWindow,
};

use crate::geometry::ScreenRect;

/// One capture of the region: raw BGRA, top-down, `width * height * 4`
/// bytes. Kept in BGRA — the order `GetDIBits` hands back — because
/// everything upstream of the final PNG only ever compares and copies whole
/// rows, so the channel order is irrelevant until [`super::output`] encodes
/// the composite.
#[derive(Clone)]
pub struct Frame {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
}

impl Frame {
    /// Bytes per row. All the row arithmetic in the stitcher and the settle
    /// comparison goes through this rather than open-coding `w * 4`.
    pub fn stride(&self) -> usize {
        self.width as usize * 4
    }
}

/// Capture `rect` off the screen. Costs one round of GDI object churn per
/// call (~a millisecond at typical region sizes); the settle loop leans on
/// that being cheap enough to run at 20 Hz.
pub fn capture_region(rect: ScreenRect) -> Result<Frame> {
    let (x, y, w, h) = (rect.min_x(), rect.min_y(), rect.width(), rect.height());
    if w <= 0 || h <= 0 {
        bail!("capture region has invalid dimensions: {w}x{h}");
    }

    unsafe {
        let screen = ScreenDc::desktop();
        if screen.0.is_invalid() {
            bail!("GetWindowDC(desktop) failed");
        }
        let mem_dc = MemDc(CreateCompatibleDC(Some(screen.0)));
        if mem_dc.0.is_invalid() {
            bail!("CreateCompatibleDC failed");
        }
        let bitmap = MemBitmap(CreateCompatibleBitmap(screen.0, w, h));
        if bitmap.0.is_invalid() {
            bail!("CreateCompatibleBitmap({w}x{h}) failed");
        }

        // The previous bitmap has to go back into the DC before the DC is
        // deleted, or the one we just created is still selected and
        // DeleteObject silently declines to free it — a leak of w*h*4 bytes
        // per captured frame, and there are up to a few hundred of those.
        let previous = SelectObject(mem_dc.0, bitmap.0.into());
        let bits = BitBlt(mem_dc.0, 0, 0, w, h, Some(screen.0), x, y, SRCCOPY)
            .map_err(anyhow::Error::from)
            .and_then(|()| read_dibits_bgra(mem_dc.0, bitmap.0, w, h));
        SelectObject(mem_dc.0, previous);

        Ok(Frame {
            bgra: bits?,
            width: w as u32,
            height: h as u32,
        })
    }
}

/// Read the DIB bits out as BGRA. Verbatim from
/// `system/win_capture.rs::read_dibits_bgra`, including the negative
/// `biHeight` that asks for a top-down DIB (pixel 0 = top-left).
unsafe fn read_dibits_bgra(hdc: HDC, bitmap: HBITMAP, width: i32, height: i32) -> Result<Vec<u8>> {
    let byte_count = (width as usize)
        .checked_mul(height as usize)
        .and_then(|n| n.checked_mul(4))
        .ok_or_else(|| anyhow!("capture dimensions overflow"))?;

    let mut bitmap_info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height,
            biPlanes: 1,
            biBitCount: 32,
            biSizeImage: byte_count as u32,
            biCompression: 0,
            ..Default::default()
        },
        ..Default::default()
    };

    let mut bgra = vec![0u8; byte_count];
    let scan_lines = GetDIBits(
        hdc,
        bitmap,
        0,
        height as u32,
        Some(bgra.as_mut_ptr().cast()),
        &mut bitmap_info,
        DIB_RGB_COLORS,
    );
    if scan_lines == 0 {
        bail!("GetDIBits failed");
    }
    Ok(bgra)
}

/// A screen DC obtained with `GetWindowDC` — released, never deleted (they
/// are the OS's, not ours; see the note in `system/win_capture.rs`).
struct ScreenDc(HDC, HWND);

impl ScreenDc {
    unsafe fn desktop() -> Self {
        let hwnd = GetDesktopWindow();
        ScreenDc(GetWindowDC(Some(hwnd)), hwnd)
    }
}

impl Drop for ScreenDc {
    fn drop(&mut self) {
        unsafe {
            if !self.0.is_invalid() && ReleaseDC(Some(self.1), self.0) != 1 {
                error!("ReleaseDC(screen) failed");
            }
        }
    }
}

/// A memory DC from `CreateCompatibleDC` — deleted, never released.
struct MemDc(HDC);

impl Drop for MemDc {
    fn drop(&mut self) {
        unsafe {
            if !self.0.is_invalid() && !DeleteDC(self.0).as_bool() {
                error!("DeleteDC failed");
            }
        }
    }
}

struct MemBitmap(HBITMAP);

impl Drop for MemBitmap {
    fn drop(&mut self) {
        unsafe {
            if !self.0.is_invalid() && !DeleteObject(self.0.into()).as_bool() {
                error!("DeleteObject(bitmap) failed");
            }
        }
    }
}
