use anyhow::Result;
use image::{DynamicImage, RgbaImage};
use rayon::prelude::*;
use std::{mem, ops::Deref, ptr};
use windows::{
    core::PCWSTR,
    Win32::{
        Foundation::HWND,
        Graphics::Gdi::{
            BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, CreateDCW, DeleteDC, DeleteObject, GetDIBits, GetWindowDC, ReleaseDC,
            SelectObject, BITMAPINFO, BITMAPINFOHEADER, CAPTUREBLT, DIB_RGB_COLORS, HBITMAP, HDC, SRCCOPY,
        },
        UI::WindowsAndMessaging::{
            GetDesktopWindow, GetSystemMetrics, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN,
        },
    },
};
use crate::geometry::{ScreenRect, RectExt};

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

fn to_rgba_image(
    box_hdc_mem: BoxHDC,
    box_h_bitmap: BoxHBITMAP,
    width: i32,
    height: i32,
) -> Result<(RgbaImage, RgbaImage)> {
    // Single allocation path: GetDIBits writes BGRA directly into `color`,
    // then the rayon pass converts `color` to RGBA in-place and computes
    // `gray` in a second buffer. Previously this allocated a third full
    // frame buffer and ran rayon with a 4-byte task granularity — fine for
    // correctness but catastrophic for throughput on a 4K capture.
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

    let mut color = vec![0u8; byte_count];
    let mut gray = vec![0u8; byte_count];

    unsafe {
        let scan_lines = GetDIBits(
            *box_hdc_mem,
            *box_h_bitmap,
            0,
            height as u32,
            Some(color.as_mut_ptr().cast()),
            &mut bitmap_info,
            DIB_RGB_COLORS,
        );
        if scan_lines == 0 {
            bail!("GetDIBits failed");
        }
    }

    // Chunk at row granularity so rayon tasks are large enough to amortize
    // scheduling. ~64 rows per task ≈ 1 MB at 4K width — big enough that
    // scheduler overhead is noise, small enough to keep all cores fed.
    let row_bytes = (width as usize) * 4;
    let chunk_bytes = row_bytes.saturating_mul(64).max(row_bytes);

    // Pre-combined BT.601 luma + 35% darken in 8-bit fixed point.
    // 0.299*0.65 ≈ 50/256, 0.587*0.65 ≈ 98/256, 0.114*0.65 ≈ 19/256.
    // Sum 167/256 ≈ 0.6523 — visually indistinguishable from the old 0.65.
    color
        .par_chunks_mut(chunk_bytes)
        .zip(gray.par_chunks_mut(chunk_bytes))
        .for_each(|(color_chunk, gray_chunk)| {
            for (cp, gp) in color_chunk
                .chunks_exact_mut(4)
                .zip(gray_chunk.chunks_exact_mut(4))
            {
                let b = cp[0] as u32;
                let g = cp[1] as u32;
                let r = cp[2] as u32;
                let a = cp[3];

                let gray_val = ((50 * r + 98 * g + 19 * b) >> 8) as u8;
                gp[0] = gray_val;
                gp[1] = gray_val;
                gp[2] = gray_val;
                gp[3] = a;

                // In-place BGRA → RGBA. G and A stay put.
                cp[0] = r as u8;
                cp[2] = b as u8;
            }
        });

    let rgba_image = RgbaImage::from_raw(width as u32, height as u32, color)
        .ok_or_else(|| anyhow!("RgbaImage::from_raw failed"))?;
    let gray_image = RgbaImage::from_raw(width as u32, height as u32, gray)
        .ok_or_else(|| anyhow!("RgbaImage::from_raw failed"))?;

    Ok((rgba_image, gray_image))
}

pub fn virtual_desktop() -> ScreenRect {
    unsafe {
        let vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        let vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        let vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        let vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        ScreenRect::from_xy_size(vx, vy, vw, vh)
    }
}

pub fn capture_desktop() -> Result<(DynamicImage, DynamicImage)> {
    unsafe {
        let rect = virtual_desktop();
        let vx = rect.min_x();
        let vy = rect.min_y();
        let vw = rect.width();
        let vh = rect.height();

        let hwnd = GetDesktopWindow();
        let box_hdc_desktop_window = BoxHDC::from(hwnd);

        let box_hdc_mem = BoxHDC::new(CreateCompatibleDC(Some(*box_hdc_desktop_window)), None);
        let box_h_bitmap = BoxHBITMAP::new(CreateCompatibleBitmap(*box_hdc_desktop_window, vw, vh));

        SelectObject(*box_hdc_mem, (*box_h_bitmap).into());

        BitBlt(*box_hdc_mem, 0, 0, vw, vh, Some(*box_hdc_desktop_window), vx, vy, SRCCOPY | CAPTUREBLT)?;

        let (rgba, gray) = to_rgba_image(box_hdc_mem, box_h_bitmap, vw, vh)?;
        Ok((DynamicImage::ImageRgba8(rgba), DynamicImage::ImageRgba8(gray)))
    }
}
