use image::imageops;

use crate::geometry::{RectExt, ScreenRect};
use crate::system::{CapturedCursor, CapturedDesktop, CursorImage, WindowPeekImage};

/// Blur the desktop bitmap for the peek overlay background.
pub fn blur_desktop_bgra(bgra: &[u8], width: u32, height: u32, sigma: f32) -> (Vec<u8>, u32, u32) {
    let mut rgba = bgra.to_vec();
    for chunk in rgba.chunks_exact_mut(4) {
        chunk.swap(0, 2);
    }
    let img = image::RgbaImage::from_raw(width, height, rgba).expect("buffer size matches dimensions");
    let blurred = imageops::blur(&img, sigma);
    let mut out = blurred.into_raw();
    for chunk in out.chunks_exact_mut(4) {
        chunk.swap(0, 2);
    }
    (out, width, height)
}

/// Extract the selected region from the desktop buffer, converting BGRA to RGBA.
/// Returns `None` if selection is outside bounds.
pub fn extract_selection_rgba(selection: ScreenRect, buffer: &CapturedDesktop) -> Option<(Vec<u8>, u32, u32)> {
    let buf_x = (selection.left() - buffer.bounds.left()).max(0) as u32;
    let buf_y = (selection.top() - buffer.bounds.top()).max(0) as u32;
    let sel_w = selection.width() as u32;
    let sel_h = selection.height() as u32;

    if buf_x >= buffer.width || buf_y >= buffer.height {
        return None;
    }
    let copy_w = sel_w.min(buffer.width - buf_x);
    let copy_h = sel_h.min(buffer.height - buf_y);

    if copy_w == 0 || copy_h == 0 {
        return None;
    }

    let stride = (buffer.width * 4) as usize;
    let mut rgba = Vec::with_capacity((copy_w * copy_h * 4) as usize);

    for row in 0..copy_h {
        let src_start = ((buf_y + row) as usize * stride) + (buf_x as usize * 4);
        let src_end = src_start + (copy_w as usize * 4);
        let src_row = &buffer.bgra[src_start..src_end];

        for chunk in src_row.chunks_exact(4) {
            rgba.push(chunk[2]);
            rgba.push(chunk[1]);
            rgba.push(chunk[0]);
            rgba.push(chunk[3]);
        }
    }

    Some((rgba, copy_w, copy_h))
}

/// Extract selection with a peek window composited on top.
/// The peek image is painted over the desktop buffer within the selection,
/// so the saved/copied result matches the on-screen rendering.
pub fn extract_selection_rgba_with_peek(
    selection: ScreenRect,
    buffer: &CapturedDesktop,
    peek: &WindowPeekImage,
) -> Option<(Vec<u8>, u32, u32)> {
    let mut result = extract_selection_rgba(selection, buffer)?;
    let (ref mut rgba, width, height) = result;

    let sel_left = selection.left();
    let sel_top = selection.top();
    let win_left = peek.window_rect.left();
    let win_top = peek.window_rect.top();

    for row in 0..height {
        for col in 0..width {
            let vd_x = sel_left + col as i32;
            let vd_y = sel_top + row as i32;

            if vd_x < win_left || vd_x >= peek.window_rect.right() || vd_y < win_top || vd_y >= peek.window_rect.bottom() {
                continue;
            }

            let tx = peek.crop_x + (vd_x - win_left);
            let ty = peek.crop_y + (vd_y - win_top);
            if tx < 0 || ty < 0 || tx >= peek.width as i32 || ty >= peek.height as i32 {
                continue;
            }
            let src_idx = (ty as usize * peek.width as usize + tx as usize) * 4;
            if src_idx + 3 >= peek.bgra.len() {
                continue;
            }

            let dst_idx = (row as usize * width as usize + col as usize) * 4;
            rgba[dst_idx] = peek.bgra[src_idx + 2];
            rgba[dst_idx + 1] = peek.bgra[src_idx + 1];
            rgba[dst_idx + 2] = peek.bgra[src_idx];
            rgba[dst_idx + 3] = 255;
        }
    }

    Some(result)
}

/// Composite the captured cursor onto an already-extracted RGBA buffer.
/// `selection` is the region in virtual-desktop coords that `rgba` covers.
///
/// For `AlphaBlended` cursors: premultiplied alpha blend (src over dst).
/// For `Masked` cursors: `output = (screen AND and_mask) XOR xor_color`,
/// which correctly handles monochrome screen-inverse pixels.
pub fn composite_cursor_rgba(rgba: &mut [u8], width: u32, height: u32, selection: ScreenRect, cursor: &CapturedCursor) {
    if !cursor.visible {
        return;
    }

    let cx = cursor.position.x - cursor.hotspot_x;
    let cy = cursor.position.y - cursor.hotspot_y;

    match &cursor.image {
        CursorImage::AlphaBlended {
            bgra,
            width: cw,
            height: ch,
        } => {
            composite_alpha_blended(rgba, width, height, selection, bgra, *cw, *ch, cx, cy);
        }
        CursorImage::Masked {
            and_mask_bgra,
            xor_color_bgra,
            width: cw,
            height: ch,
        } => {
            composite_masked(rgba, width, height, selection, and_mask_bgra, xor_color_bgra, *cw, *ch, cx, cy);
        }
    }
}

fn composite_alpha_blended(
    rgba: &mut [u8],
    dst_w: u32,
    dst_h: u32,
    selection: ScreenRect,
    src_bgra: &[u8],
    src_w: u32,
    src_h: u32,
    origin_x: i32,
    origin_y: i32,
) {
    for row in 0..src_h {
        for col in 0..src_w {
            let vd_x = origin_x + col as i32;
            let vd_y = origin_y + row as i32;
            let sx = vd_x - selection.left();
            let sy = vd_y - selection.top();
            if sx < 0 || sy < 0 || sx >= dst_w as i32 || sy >= dst_h as i32 {
                continue;
            }
            let src_idx = (row as usize * src_w as usize + col as usize) * 4;
            if src_idx + 3 >= src_bgra.len() {
                continue;
            }
            let sa = src_bgra[src_idx + 3];
            if sa == 0 {
                continue;
            }
            // Source is BGRA premultiplied; dst is RGBA.
            let sr = src_bgra[src_idx + 2];
            let sg = src_bgra[src_idx + 1];
            let sb = src_bgra[src_idx];

            let dst_idx = (sy as usize * dst_w as usize + sx as usize) * 4;
            let inv_sa = 255 - sa as u16;
            rgba[dst_idx] = (sr as u16 + (rgba[dst_idx] as u16 * inv_sa / 255)).min(255) as u8;
            rgba[dst_idx + 1] = (sg as u16 + (rgba[dst_idx + 1] as u16 * inv_sa / 255)).min(255) as u8;
            rgba[dst_idx + 2] = (sb as u16 + (rgba[dst_idx + 2] as u16 * inv_sa / 255)).min(255) as u8;
            rgba[dst_idx + 3] = (sa as u16 + (rgba[dst_idx + 3] as u16 * inv_sa / 255)).min(255) as u8;
        }
    }
}

fn composite_masked(
    rgba: &mut [u8],
    dst_w: u32,
    dst_h: u32,
    selection: ScreenRect,
    and_mask_bgra: &[u8],
    xor_color_bgra: &[u8],
    src_w: u32,
    src_h: u32,
    origin_x: i32,
    origin_y: i32,
) {
    for row in 0..src_h {
        for col in 0..src_w {
            let vd_x = origin_x + col as i32;
            let vd_y = origin_y + row as i32;
            let sx = vd_x - selection.left();
            let sy = vd_y - selection.top();
            if sx < 0 || sy < 0 || sx >= dst_w as i32 || sy >= dst_h as i32 {
                continue;
            }
            let src_idx = (row as usize * src_w as usize + col as usize) * 4;
            if src_idx + 3 >= and_mask_bgra.len() || src_idx + 3 >= xor_color_bgra.len() {
                continue;
            }

            let dst_idx = (sy as usize * dst_w as usize + sx as usize) * 4;
            // output = (screen AND and_mask) XOR xor_color
            // Dst is RGBA, masks are BGRA → swap channels during operation.
            rgba[dst_idx] = (rgba[dst_idx] & and_mask_bgra[src_idx + 2]) ^ xor_color_bgra[src_idx + 2];
            rgba[dst_idx + 1] = (rgba[dst_idx + 1] & and_mask_bgra[src_idx + 1]) ^ xor_color_bgra[src_idx + 1];
            rgba[dst_idx + 2] = (rgba[dst_idx + 2] & and_mask_bgra[src_idx]) ^ xor_color_bgra[src_idx];
            rgba[dst_idx + 3] = 255;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::system::{CapturedDesktop, WindowPeekImage};

    fn desktop_2x2() -> CapturedDesktop {
        CapturedDesktop {
            bgra: vec![3, 2, 1, 255, 30, 20, 10, 255, 60, 50, 40, 255, 90, 80, 70, 255],
            width: 2,
            height: 2,
            bounds: ScreenRect::from_xy_size(10, 20, 2, 2),
            monitors: Vec::new(),
            cursor: None,
        }
    }

    #[test]
    fn extracts_selection_and_converts_bgra_to_rgba() {
        let desktop = desktop_2x2();
        let selection = ScreenRect::from_xy_size(10, 20, 2, 1);

        let (rgba, width, height) = extract_selection_rgba(selection, &desktop).expect("selection should extract");

        assert_eq!((width, height), (2, 1));
        assert_eq!(rgba, vec![1, 2, 3, 255, 10, 20, 30, 255]);
    }

    #[test]
    fn composites_peek_image_over_desktop_selection() {
        let desktop = desktop_2x2();
        let peek = WindowPeekImage {
            window_index: 1,
            window_rect: ScreenRect::from_xy_size(11, 20, 1, 1),
            bgra: vec![200, 150, 100, 255],
            width: 1,
            height: 1,
            crop_x: 0,
            crop_y: 0,
            obstruction_rects: Vec::new(),
        };
        let selection = ScreenRect::from_xy_size(10, 20, 2, 1);

        let (rgba, width, height) = extract_selection_rgba_with_peek(selection, &desktop, &peek).expect("selection should extract");

        assert_eq!((width, height), (2, 1));
        assert_eq!(rgba, vec![1, 2, 3, 255, 100, 150, 200, 255]);
    }

    #[test]
    fn returns_none_when_selection_misses_buffer() {
        let desktop = desktop_2x2();
        let selection = ScreenRect::from_xy_size(20, 20, 2, 2);

        assert!(extract_selection_rgba(selection, &desktop).is_none());
    }

    use crate::geometry::ScreenPoint;

    #[test]
    fn composite_cursor_alpha_blended_opaque() {
        // 2x1 RGBA buffer, white pixels
        let mut rgba = vec![255, 255, 255, 255, 255, 255, 255, 255];
        let selection = ScreenRect::from_xy_size(0, 0, 2, 1);
        // 1x1 opaque red cursor (BGRA: B=0, G=0, R=255, A=255) at position (0,0)
        let cursor = CapturedCursor {
            position: ScreenPoint::new(0, 0),
            hotspot_x: 0,
            hotspot_y: 0,
            visible: true,
            image: CursorImage::AlphaBlended {
                bgra: vec![0, 0, 255, 255],
                width: 1,
                height: 1,
            },
        };

        composite_cursor_rgba(&mut rgba, 2, 1, selection, &cursor);

        // First pixel should be red (R=255,G=0,B=0,A=255), second unchanged
        assert_eq!(rgba, vec![255, 0, 0, 255, 255, 255, 255, 255]);
    }

    #[test]
    fn composite_cursor_alpha_blended_semi_transparent() {
        // 1x1 RGBA buffer, white pixel
        let mut rgba = vec![255, 255, 255, 255];
        let selection = ScreenRect::from_xy_size(0, 0, 1, 1);
        // 50% transparent red cursor (premultiplied: B=0, G=0, R=128, A=128)
        let cursor = CapturedCursor {
            position: ScreenPoint::new(0, 0),
            hotspot_x: 0,
            hotspot_y: 0,
            visible: true,
            image: CursorImage::AlphaBlended {
                bgra: vec![0, 0, 128, 128],
                width: 1,
                height: 1,
            },
        };

        composite_cursor_rgba(&mut rgba, 1, 1, selection, &cursor);

        // premultiplied: out = src + dst * (1 - src_a/255)
        // R: 128 + 255 * 127/255 = 128 + 127 = 255
        // G: 0 + 255 * 127/255 = 127
        // B: 0 + 255 * 127/255 = 127
        assert_eq!(rgba[0], 255); // R
        assert_eq!(rgba[1], 127); // G
        assert_eq!(rgba[2], 127); // B
    }

    #[test]
    fn composite_cursor_masked_black_opaque() {
        // 1x1 RGBA buffer, white pixel
        let mut rgba = vec![255, 255, 255, 255];
        let selection = ScreenRect::from_xy_size(0, 0, 1, 1);
        // Monochrome black: AND=0x00 (zero screen), XOR=0x00 (no change) → black
        let cursor = CapturedCursor {
            position: ScreenPoint::new(0, 0),
            hotspot_x: 0,
            hotspot_y: 0,
            visible: true,
            image: CursorImage::Masked {
                and_mask_bgra: vec![0x00, 0x00, 0x00, 0x00],
                xor_color_bgra: vec![0x00, 0x00, 0x00, 0x00],
                width: 1,
                height: 1,
            },
        };

        composite_cursor_rgba(&mut rgba, 1, 1, selection, &cursor);

        assert_eq!(rgba, vec![0, 0, 0, 255]);
    }

    #[test]
    fn composite_cursor_masked_screen_inverse() {
        // 1x1 RGBA buffer, arbitrary color (R=100, G=150, B=200)
        let mut rgba = vec![100, 150, 200, 255];
        let selection = ScreenRect::from_xy_size(0, 0, 1, 1);
        // Monochrome inverse: AND=0xFF (keep screen), XOR=0xFF (invert) → ~screen
        let cursor = CapturedCursor {
            position: ScreenPoint::new(0, 0),
            hotspot_x: 0,
            hotspot_y: 0,
            visible: true,
            image: CursorImage::Masked {
                and_mask_bgra: vec![0xFF, 0xFF, 0xFF, 0xFF],
                xor_color_bgra: vec![0xFF, 0xFF, 0xFF, 0xFF],
                width: 1,
                height: 1,
            },
        };

        composite_cursor_rgba(&mut rgba, 1, 1, selection, &cursor);

        // (screen AND 0xFF) XOR 0xFF = ~screen
        assert_eq!(rgba, vec![155, 105, 55, 255]);
    }

    #[test]
    fn composite_cursor_invisible_is_noop() {
        let mut rgba = vec![100, 150, 200, 255];
        let selection = ScreenRect::from_xy_size(0, 0, 1, 1);
        let cursor = CapturedCursor {
            position: ScreenPoint::new(0, 0),
            hotspot_x: 0,
            hotspot_y: 0,
            visible: false,
            image: CursorImage::AlphaBlended {
                bgra: vec![255, 0, 0, 255],
                width: 1,
                height: 1,
            },
        };

        composite_cursor_rgba(&mut rgba, 1, 1, selection, &cursor);

        assert_eq!(rgba, vec![100, 150, 200, 255]);
    }

    #[test]
    fn composite_cursor_clips_outside_selection() {
        // 1x1 RGBA buffer at position (10,10)
        let mut rgba = vec![100, 150, 200, 255];
        let selection = ScreenRect::from_xy_size(10, 10, 1, 1);
        // Cursor at (0,0) — entirely outside the selection
        let cursor = CapturedCursor {
            position: ScreenPoint::new(0, 0),
            hotspot_x: 0,
            hotspot_y: 0,
            visible: true,
            image: CursorImage::AlphaBlended {
                bgra: vec![255, 0, 0, 255],
                width: 1,
                height: 1,
            },
        };

        composite_cursor_rgba(&mut rgba, 1, 1, selection, &cursor);

        assert_eq!(rgba, vec![100, 150, 200, 255]);
    }
}
