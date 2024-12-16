use crate::geometry::*;
use anyhow::Result;
use bevy::log::*;
use image::{self, DynamicImage, ImageBuffer, Rgba, RgbaImage};
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
                if ReleaseDC(hwnd, self.hdc) != 1 {
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
        let hdc = unsafe { GetWindowDC(hwnd) };

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
            if !DeleteObject(self.0).as_bool() {
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
) -> Result<(ImageBuffer<Rgba<u8>, Vec<u8>>, ImageBuffer<Rgba<u8>, Vec<u8>>)> {
    let buffer_size = width * height * 4;
    let mut bitmap_info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height,
            biPlanes: 1,
            biBitCount: 32,
            biSizeImage: buffer_size as u32,
            biCompression: 0,
            ..Default::default()
        },
        ..Default::default()
    };

    let mut buffer = vec![0u8; buffer_size as usize];

    unsafe {
        let is_success = GetDIBits(
            *box_hdc_mem,
            *box_h_bitmap,
            0,
            height as u32,
            Some(buffer.as_mut_ptr().cast()),
            &mut bitmap_info,
            DIB_RGB_COLORS,
        ) == 0;

        if is_success {
            bail!("Get RGBA data failed");
        }
    };

    // let is_old_version = get_os_major_version() < 8;
    // for src in buffer.chunks_exact_mut(4) {
    //     src.swap(0, 2);
    //     // fix https://github.com/nashaofu/xcap/issues/92#issuecomment-1910014951
    //     if src[3] == 0 && is_old_version {
    //         src[3] = 255;
    //     }
    // }

    let mut color_rgba_buffer = vec![0u8; (width * height * 4) as usize];
    let mut gray_rgba_buffer = vec![0u8; (width * height * 4) as usize];

    color_rgba_buffer
        .par_chunks_mut(4)
        .zip(gray_rgba_buffer.par_chunks_mut(4))
        .enumerate()
        .for_each(|(i, (color, gray))| {
            let idx = i * 4;
            let b = buffer[idx] as f32;
            let g = buffer[idx + 1] as f32;
            let r = buffer[idx + 2] as f32;
            let a = buffer[idx + 3];

            // Convert to grayscale
            let gray_val = (0.299 * r + 0.587 * g + 0.114 * b) as u8;
            gray[0] = gray_val;
            gray[1] = gray_val;
            gray[2] = gray_val;
            gray[3] = a;

            // Convert BGRA to RGBA
            color[0] = r as u8; // R
            color[1] = g as u8; // G
            color[2] = b as u8; // B
            color[3] = a; // A
        });

    let bgra_image =
        RgbaImage::from_raw(width as u32, height as u32, color_rgba_buffer).ok_or_else(|| anyhow!("RgbaImage::from_raw failed"))?;

    let gray_image =
        RgbaImage::from_raw(width as u32, height as u32, gray_rgba_buffer).ok_or_else(|| anyhow!("RgbaImage::from_raw failed"))?;

    Ok((bgra_image, gray_image))
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

        let box_hdc_mem = BoxHDC::new(CreateCompatibleDC(*box_hdc_desktop_window), None);
        let box_h_bitmap = BoxHBITMAP::new(CreateCompatibleBitmap(*box_hdc_desktop_window, vw, vh));

        SelectObject(*box_hdc_mem, *box_h_bitmap);

        BitBlt(*box_hdc_mem, 0, 0, vw, vh, *box_hdc_desktop_window, vx, vy, SRCCOPY | CAPTUREBLT)?;

        let capture = to_rgba_image(box_hdc_mem, box_h_bitmap, vw, vh)?;
        Ok((DynamicImage::ImageRgba8(capture.0), DynamicImage::ImageRgba8(capture.1)))
    }
}
