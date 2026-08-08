use crate::system::{CapturedCursor, CursorImage};
use clowd_rust_core::geometry::ScreenPoint;

use std::mem;
use windows::Win32::Graphics::Gdi::{DeleteObject, GetBitmapBits, GetObjectW, BITMAP};
use windows::Win32::UI::WindowsAndMessaging::{CopyIcon, DestroyIcon, GetCursorInfo, GetIconInfo, CURSORINFO, CURSOR_SHOWING, ICONINFO};

pub fn capture_cursor() -> Option<CapturedCursor> {
    unsafe {
        let mut ci = CURSORINFO {
            cbSize: mem::size_of::<CURSORINFO>() as u32,
            ..Default::default()
        };
        if GetCursorInfo(&mut ci).is_err() {
            warn!("GetCursorInfo failed");
            return None;
        }

        let position = ScreenPoint::new(ci.ptScreenPos.x, ci.ptScreenPos.y);
        let showing = (ci.flags.0 & CURSOR_SHOWING.0) != 0;

        if !showing || ci.hCursor.is_invalid() {
            return Some(CapturedCursor {
                position,
                hotspot_x: 0,
                hotspot_y: 0,
                visible: false,
                image: CursorImage::AlphaBlended {
                    bgra: Vec::new(),
                    width: 0,
                    height: 0,
                },
            });
        }

        let icon = CopyIcon(ci.hCursor.into()).ok()?;
        let mut ii: ICONINFO = mem::zeroed();
        let got_info = GetIconInfo(icon, &mut ii).is_ok();
        if !got_info {
            warn!("GetIconInfo failed");
            let _ = DestroyIcon(icon);
            return None;
        }

        let hotspot_x = ii.xHotspot as i32;
        let hotspot_y = ii.yHotspot as i32;

        let result = extract_cursor_image(&ii);

        if !ii.hbmMask.is_invalid() {
            let _ = DeleteObject(ii.hbmMask.into());
        }
        if !ii.hbmColor.is_invalid() {
            let _ = DeleteObject(ii.hbmColor.into());
        }
        let _ = DestroyIcon(icon);

        let image = result?;

        Some(CapturedCursor {
            position,
            hotspot_x,
            hotspot_y,
            visible: true,
            image,
        })
    }
}

unsafe fn get_bitmap_data(hbmp: windows::Win32::Graphics::Gdi::HBITMAP) -> Option<(Vec<u8>, BITMAP)> {
    let mut bmp: BITMAP = mem::zeroed();
    let bmp_size = mem::size_of::<BITMAP>() as i32;
    if GetObjectW(hbmp.into(), bmp_size, Some(&mut bmp as *mut _ as *mut _)) == 0 {
        return None;
    }
    let size = (bmp.bmHeight * bmp.bmWidthBytes) as usize;
    if size == 0 {
        return None;
    }
    let mut data = vec![0u8; size];
    let copied = GetBitmapBits(hbmp, size as i32, data.as_mut_ptr().cast());
    if copied == 0 {
        return None;
    }
    Some((data, bmp))
}

fn bit_to_alpha(data: &[u8], pixel: usize, invert: bool) -> u8 {
    let byte = data[pixel / 8];
    let alpha = (byte >> (7 - (pixel % 8))) & 1 != 0;
    if invert {
        if alpha {
            0xFF
        } else {
            0
        }
    } else if alpha {
        0
    } else {
        0xFF
    }
}

fn bitmap_has_alpha(data: &[u8]) -> bool {
    data.chunks_exact(4).any(|px| px[3] != 0)
}

unsafe fn extract_cursor_image(ii: &ICONINFO) -> Option<CursorImage> {
    if ii.hbmMask.is_invalid() {
        return None;
    }

    let has_color = !ii.hbmColor.is_invalid();
    if has_color {
        copy_from_color(ii)
    } else {
        copy_from_mask(ii)
    }
}

unsafe fn copy_from_color(ii: &ICONINFO) -> Option<CursorImage> {
    let (mut color, bmp_color) = get_bitmap_data(ii.hbmColor)?;

    if bmp_color.bmBitsPixel < 32 {
        return None;
    }

    let width = bmp_color.bmWidth as u32;
    let height = bmp_color.bmHeight as u32;
    let pixels = (width * height) as usize;

    if !bitmap_has_alpha(&color) {
        if let Some((mask, bmp_mask)) = get_bitmap_data(ii.hbmMask) {
            let mask_w_bits = bmp_mask.bmWidthBytes as usize * 8;
            for y in 0..bmp_mask.bmHeight as usize {
                for x in 0..bmp_mask.bmWidth as usize {
                    let mask_pix = y * mask_w_bits + x;
                    let a = bit_to_alpha(&mask, mask_pix, false);
                    let idx = (y * bmp_mask.bmWidth as usize + x) * 4 + 3;
                    if idx < color.len() {
                        color[idx] = a;
                    }
                }
            }
        }
    }

    if pixels == 0 {
        return None;
    }

    Some(CursorImage::AlphaBlended {
        bgra: color,
        width,
        height,
    })
}

unsafe fn copy_from_mask(ii: &ICONINFO) -> Option<CursorImage> {
    let (mask, mut bmp) = get_bitmap_data(ii.hbmMask)?;

    bmp.bmHeight /= 2;
    let width = bmp.bmWidth as u32;
    let height = bmp.bmHeight as u32;
    let pixels = (width * height) as usize;
    if pixels == 0 {
        return None;
    }

    let bottom = (bmp.bmWidthBytes * bmp.bmHeight) as usize;
    let mask_w_bits = bmp.bmWidthBytes as usize * 8;

    let mut and_mask_bgra = vec![0u8; pixels * 4];
    let mut xor_color_bgra = vec![0u8; pixels * 4];

    for y in 0..height as usize {
        for x in 0..width as usize {
            let pix = y * mask_w_bits + x;
            let and_val = bit_to_alpha(&mask, pix, true);
            let xor_val = bit_to_alpha(&mask[bottom..], pix, true);

            let i = (y * width as usize + x) * 4;
            let fill = if and_val == 0 { 0x00 } else { 0xFF };
            and_mask_bgra[i] = fill;
            and_mask_bgra[i + 1] = fill;
            and_mask_bgra[i + 2] = fill;
            and_mask_bgra[i + 3] = fill;

            let fill = if xor_val == 0 { 0x00 } else { 0xFF };
            xor_color_bgra[i] = fill;
            xor_color_bgra[i + 1] = fill;
            xor_color_bgra[i + 2] = fill;
            xor_color_bgra[i + 3] = fill;
        }
    }

    Some(CursorImage::Masked {
        and_mask_bgra,
        xor_color_bgra,
        width,
        height,
    })
}
