use image::imageops;

use crate::geometry::{RectExt, ScreenRect};
use crate::system::{CapturedDesktop, WindowPeekImage};

pub fn blur_desktop_bgra(bgra: &[u8], width: u32, height: u32, sigma: f32) -> Vec<u8> {
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
    out
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
}
