use crate::geometry::ScreenPoint;
use crate::system::{CapturedCursor, CursorImage, MonitorInfo};

use objc2_app_kit::NSCursor;
use objc2_foundation::NSPoint;

pub fn capture_cursor(monitors: &[MonitorInfo]) -> Option<CapturedCursor> {
    let position = super::mac_mouse::get_position(monitors);

    let cursor = unsafe { NSCursor::currentSystemCursor() }?;
    let hotspot: NSPoint = unsafe { cursor.hotSpot() };
    let image = unsafe { cursor.image() };

    let size = unsafe { image.size() };
    let width = size.width as u32;
    let height = size.height as u32;
    if width == 0 || height == 0 {
        return None;
    }

    let representations = unsafe { image.representations() };
    if representations.len() == 0 {
        return None;
    }

    let rep = &representations[0];
    let bitmap_rep: &objc2_app_kit::NSBitmapImageRep = match unsafe { objc2::msg_send![rep, bitmapData] } {
        p if !p.is_null() => unsafe { &*(rep as *const _ as *const objc2_app_kit::NSBitmapImageRep) },
        _ => return None,
    };

    let bps = unsafe { bitmap_rep.bitsPerPixel() } as u32;
    if bps != 32 {
        warn!("cursor bitmap has unexpected bpp={}", bps);
        return None;
    }

    let pixels_wide = unsafe { bitmap_rep.pixelsWide() } as u32;
    let pixels_high = unsafe { bitmap_rep.pixelsHigh() } as u32;
    let bytes_per_row = unsafe { bitmap_rep.bytesPerRow() } as usize;

    let data_ptr: *const u8 = unsafe { objc2::msg_send![bitmap_rep, bitmapData] };
    if data_ptr.is_null() {
        return None;
    }

    let mut bgra = vec![0u8; (pixels_wide * pixels_high * 4) as usize];
    for row in 0..pixels_high as usize {
        let src_start = row * bytes_per_row;
        for col in 0..pixels_wide as usize {
            let src = src_start + col * 4;
            let dst = (row * pixels_wide as usize + col) * 4;
            unsafe {
                // NSBitmapImageRep is RGBA; convert to BGRA premultiplied
                let r = *data_ptr.add(src);
                let g = *data_ptr.add(src + 1);
                let b = *data_ptr.add(src + 2);
                let a = *data_ptr.add(src + 3);
                bgra[dst] = b;
                bgra[dst + 1] = g;
                bgra[dst + 2] = r;
                bgra[dst + 3] = a;
            }
        }
    }

    Some(CapturedCursor {
        position,
        hotspot_x: hotspot.x as i32,
        hotspot_y: hotspot.y as i32,
        visible: true,
        image: CursorImage::AlphaBlended {
            bgra,
            width: pixels_wide,
            height: pixels_high,
        },
    })
}
