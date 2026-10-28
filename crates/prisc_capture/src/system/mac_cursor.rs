use crate::system::{CapturedCursor, CursorImage, MonitorInfo};

use objc2_app_kit::NSCursor;
use objc2_core_graphics::{CGBitmapContextCreate, CGColorSpace, CGContext, CGImage};
use prisc_rust_core::desktop::{CGPoint, CGRect, CGSize};

#[allow(deprecated)]
pub fn capture_cursor(monitors: &[MonitorInfo]) -> Option<CapturedCursor> {
    let position = super::mac_mouse::get_position(monitors);

    let cursor = NSCursor::currentSystemCursor()?;
    let hotspot = cursor.hotSpot();
    let image = cursor.image();

    let size = image.size();
    if size.width <= 0.0 || size.height <= 0.0 {
        return None;
    }

    let scale = monitors
        .iter()
        .find(|m| m.bounds.contains(position))
        .map(|m| m.scale_factor)
        .unwrap_or(2.0) as f64;

    let phys_w = (size.width * scale).round() as u32;
    let phys_h = (size.height * scale).round() as u32;
    if phys_w == 0 || phys_h == 0 {
        return None;
    }

    let representations = image.representations();
    if representations.is_empty() {
        return None;
    }
    let rep = unsafe { representations.objectAtIndex_unchecked(0) };
    let cg_ref: *mut CGImage = unsafe { objc2::msg_send![rep, CGImage] };
    // Borrowed from the representation, which outlives this function.
    let cg_image = unsafe { cg_ref.as_ref() }?;

    // Render through a CGBitmapContext at the physical pixel size so the
    // output is always BGRA-premultiplied regardless of the source format.
    // kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst = BGRA in memory.
    let bytes_per_row = phys_w as usize * 4;
    let mut bgra = vec![0u8; bytes_per_row * phys_h as usize];
    let color_space = CGColorSpace::new_device_rgb()?;
    // SAFETY: `bgra` holds `bytes_per_row * phys_h` bytes and outlives the
    // context, which is dropped at the end of this function.
    let ctx = unsafe {
        CGBitmapContextCreate(
            bgra.as_mut_ptr() as *mut _,
            phys_w as usize,
            phys_h as usize,
            8,
            bytes_per_row,
            Some(&color_space),
            (2 << 12) | 2,
        )
    }?;
    CGContext::draw_image(
        Some(&ctx),
        CGRect::new(CGPoint::new(0.0, 0.0), CGSize::new(phys_w as f64, phys_h as f64)),
        Some(cg_image),
    );
    drop(ctx);

    Some(CapturedCursor {
        position,
        hotspot_x: (hotspot.x * scale).round() as i32,
        hotspot_y: (hotspot.y * scale).round() as i32,
        visible: true,
        image: CursorImage::AlphaBlended {
            bgra,
            width: phys_w,
            height: phys_h,
        },
    })
}
