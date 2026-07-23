use crate::geometry::ScreenRect;
use anyhow::Result;
use std::{mem, ops::Deref, ptr};
use windows::{
    core::PCWSTR,
    Win32::{
        Foundation::HWND,
        Graphics::Gdi::{
            BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, CreateDCW, DeleteDC, DeleteObject, GetDIBits, GetWindowDC, ReleaseDC,
            SelectObject, BITMAPINFO, BITMAPINFOHEADER, CAPTUREBLT, DIB_RGB_COLORS, HBITMAP, HDC, SRCCOPY,
        },
        Storage::Xps::{PrintWindow, PRINT_WINDOW_FLAGS},
        UI::WindowsAndMessaging::GetDesktopWindow,
    },
};

/// Raw bitmap product of `capture_desktop`. Public to its own module only;
/// the public `CapturedDesktop` (defined in `system/mod.rs`) wraps this and
/// adds per-monitor metadata.
pub struct DesktopBitmap {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
    pub bounds: ScreenRect,
}

#[derive(Debug)]
pub(super) struct BoxHDC {
    hdc: HDC,
    hwnd: Option<HWND>,
}

impl Deref for BoxHDC {
    type Target = HDC;
    fn deref(&self) -> &Self::Target {
        &self.hdc
    }
}

impl Drop for BoxHDC {
    fn drop(&mut self) {
        // ReleaseDC 与 DeleteDC 的区别
        // https://learn.microsoft.com/zh-cn/windows/win32/api/winuser/nf-winuser-releasedc
        unsafe {
            if let Some(hwnd) = self.hwnd {
                if ReleaseDC(Some(hwnd), self.hdc) != 1 {
                    error!("ReleaseDC {:?} failed", self)
                }
            } else if !DeleteDC(self.hdc).as_bool() {
                error!("DeleteDC {:?} failed", self)
            }
        };
    }
}

impl BoxHDC {
    pub fn new(hdc: HDC, hwnd: Option<HWND>) -> Self {
        BoxHDC {
            hdc,
            hwnd,
        }
    }
}

impl From<&[u16; 32]> for BoxHDC {
    fn from(sz_device: &[u16; 32]) -> Self {
        let sz_device_ptr = sz_device.as_ptr();

        let hdc = unsafe { CreateDCW(PCWSTR(sz_device_ptr), PCWSTR(sz_device_ptr), PCWSTR(ptr::null()), None) };

        BoxHDC::new(hdc, None)
    }
}

impl From<HWND> for BoxHDC {
    fn from(hwnd: HWND) -> Self {
        // GetWindowDC vs GetDC, GetDC 不会绘制窗口边框
        // https://learn.microsoft.com/zh-cn/windows/win32/api/winuser/nf-winuser-getwindowdc
        let hdc = unsafe { GetWindowDC(Some(hwnd)) };

        BoxHDC::new(hdc, Some(hwnd))
    }
}

#[derive(Debug)]
pub(super) struct BoxHBITMAP(HBITMAP);

impl Deref for BoxHBITMAP {
    type Target = HBITMAP;
    fn deref(&self) -> &Self::Target {
        &self.0
    }
}

impl Drop for BoxHBITMAP {
    fn drop(&mut self) {
        // https://learn.microsoft.com/zh-cn/windows/win32/api/wingdi/nf-wingdi-createcompatiblebitmap
        unsafe {
            if !DeleteObject(self.0.into()).as_bool() {
                error!("DeleteObject {:?} failed", self)
            }
        };
    }
}

impl BoxHBITMAP {
    pub fn new(h_bitmap: HBITMAP) -> Self {
        BoxHBITMAP(h_bitmap)
    }
}

/// Read the bits out of the GDI bitmap into a freshly-allocated `Vec<u8>`
/// in raw BGRA order. We deliberately *don't* convert to RGBA — the GPU
/// uploads this buffer straight into a `Bgra8UnormSrgb` texture, where the
/// sampler hardware does the channel reorder for free. Skipping the CPU
/// swap removes ~50 MB of memory traffic on a 4K capture and lops the
/// rayon dispatch overhead off startup latency entirely.
pub(super) fn read_dibits_bgra(box_hdc_mem: BoxHDC, box_h_bitmap: BoxHBITMAP, width: i32, height: i32) -> Result<Vec<u8>> {
    let byte_count = (width as usize)
        .checked_mul(height as usize)
        .and_then(|n| n.checked_mul(4))
        .ok_or_else(|| anyhow!("capture dimensions overflow"))?;

    let mut bitmap_info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            // Negative height = top-down DIB. Pixel (0,0) is screen
            // top-left, which matches wgpu's texture coordinate convention,
            // so the GPU upload needs no Y flip.
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

    unsafe {
        let scan_lines = GetDIBits(
            *box_hdc_mem,
            *box_h_bitmap,
            0,
            height as u32,
            Some(bgra.as_mut_ptr().cast()),
            &mut bitmap_info,
            DIB_RGB_COLORS,
        );
        if scan_lines == 0 {
            bail!("GetDIBits failed");
        }
    }

    Ok(bgra)
}

pub fn capture_desktop(bounds: &ScreenRect) -> Result<DesktopBitmap> {
    unsafe {
        let bounds = *bounds;
        let vx = bounds.min_x();
        let vy = bounds.min_y();
        let vw = bounds.width();
        let vh = bounds.height();

        if vw <= 0 || vh <= 0 {
            bail!("virtual desktop has invalid dimensions: {}x{}", vw, vh);
        }

        let hwnd = GetDesktopWindow();
        let box_hdc_desktop_window = BoxHDC::from(hwnd);

        let box_hdc_mem = BoxHDC::new(CreateCompatibleDC(Some(*box_hdc_desktop_window)), None);
        let box_h_bitmap = BoxHBITMAP::new(CreateCompatibleBitmap(*box_hdc_desktop_window, vw, vh));

        SelectObject(*box_hdc_mem, (*box_h_bitmap).into());

        BitBlt(
            *box_hdc_mem,
            0,
            0,
            vw,
            vh,
            Some(*box_hdc_desktop_window),
            vx,
            vy,
            SRCCOPY | CAPTUREBLT,
        )?;

        let bgra = read_dibits_bgra(box_hdc_mem, box_h_bitmap, vw, vh)?;
        Ok(DesktopBitmap {
            bgra,
            width: vw as u32,
            height: vh as u32,
            bounds,
        })
    }
}

/// Capture a single window's content via PrintWindow.
/// `raw_rect` should be the GetWindowRect bounds (includes invisible border).
/// Returns BGRA bytes at those dimensions, or None on failure.
pub fn capture_window_image(hwnd: HWND, raw_rect: &ScreenRect) -> Option<(Vec<u8>, u32, u32)> {
    let w = raw_rect.width();
    let h = raw_rect.height();
    if w <= 0 || h <= 0 {
        return None;
    }

    unsafe {
        let desktop_hwnd = GetDesktopWindow();
        let hdc_screen = BoxHDC::from(desktop_hwnd);
        let hdc_mem = BoxHDC::new(CreateCompatibleDC(Some(*hdc_screen)), None);
        let hbitmap = BoxHBITMAP::new(CreateCompatibleBitmap(*hdc_screen, w, h));
        SelectObject(*hdc_mem, (*hbitmap).into());

        // PW_RENDERFULLCONTENT = 0x0002
        let ok = PrintWindow(hwnd, *hdc_mem, PRINT_WINDOW_FLAGS(2));
        if !ok.as_bool() {
            warn!("PrintWindow failed for hwnd {:?}", hwnd);
            return None;
        }

        match read_dibits_bgra(hdc_mem, hbitmap, w, h) {
            Ok(bgra) => Some((bgra, w as u32, h as u32)),
            Err(e) => {
                warn!("GetDIBits failed after PrintWindow: {e}");
                None
            }
        }
    }
}
