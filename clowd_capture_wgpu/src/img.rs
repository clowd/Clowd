use image::imageops;
use winit::window::Window;

use crate::geometry::RectExt;
use crate::geometry::ScreenRect;
use crate::system::{CapturedDesktop, WindowPeekImage};

pub fn blur_desktop_bgra(bgra: &[u8], width: u32, height: u32, sigma: f32) -> Vec<u8> {
    let mut rgba = bgra.to_vec();
    for chunk in rgba.chunks_exact_mut(4) {
        chunk.swap(0, 2);
    }
    let img = image::RgbaImage::from_raw(width, height, rgba)
        .expect("buffer size matches dimensions");
    let blurred = imageops::blur(&img, sigma);
    let mut out = blurred.into_raw();
    for chunk in out.chunks_exact_mut(4) {
        chunk.swap(0, 2);
    }
    out
}

/// Result of a Copy or Save action.
pub enum ActionResult {
    /// Operation completed successfully.
    Success,
    /// User cancelled the operation (e.g. dismissed save dialog).
    Cancelled,
    /// Operation failed with an error message.
    Failed(String),
}

/// Extract the selected region from the desktop buffer, converting BGRA to RGBA.
/// Returns `None` if selection is outside bounds.
pub fn extract_selection_rgba(selection: ScreenRect, buffer: &CapturedDesktop) -> Option<(Vec<u8>, u32, u32)> {
    // Convert selection from virtual-desktop coords to buffer-local coords
    let buf_x = (selection.left() - buffer.bounds.left()).max(0) as u32;
    let buf_y = (selection.top() - buffer.bounds.top()).max(0) as u32;
    let sel_w = selection.width() as u32;
    let sel_h = selection.height() as u32;

    // Clamp to buffer bounds
    if buf_x >= buffer.width || buf_y >= buffer.height {
        return None;
    }
    let copy_w = sel_w.min(buffer.width - buf_x);
    let copy_h = sel_h.min(buffer.height - buf_y);

    if copy_w == 0 || copy_h == 0 {
        return None;
    }

    // Extract sub-region with BGRA→RGBA conversion
    let stride = (buffer.width * 4) as usize;
    let mut rgba = Vec::with_capacity((copy_w * copy_h * 4) as usize);

    for row in 0..copy_h {
        let src_start = ((buf_y + row) as usize * stride) + (buf_x as usize * 4);
        let src_end = src_start + (copy_w as usize * 4);
        let src_row = &buffer.bgra[src_start..src_end];

        for chunk in src_row.chunks_exact(4) {
            // BGRA → RGBA: swap B and R
            rgba.push(chunk[2]); // R
            rgba.push(chunk[1]); // G
            rgba.push(chunk[0]); // B
            rgba.push(chunk[3]); // A
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

    // For each pixel in the output, check if it falls within the peek
    // window rect. If so, sample from the peek BGRA buffer (with crop offset).
    for row in 0..height {
        for col in 0..width {
            let vd_x = sel_left + col as i32;
            let vd_y = sel_top + row as i32;

            // Is this pixel within the peek window's visual bounds?
            if vd_x < win_left
                || vd_x >= peek.window_rect.right()
                || vd_y < win_top
                || vd_y >= peek.window_rect.bottom()
            {
                continue;
            }

            // Sample from the peek texture with crop offset.
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
            // BGRA → RGBA
            rgba[dst_idx] = peek.bgra[src_idx + 2];
            rgba[dst_idx + 1] = peek.bgra[src_idx + 1];
            rgba[dst_idx + 2] = peek.bgra[src_idx];
            rgba[dst_idx + 3] = 255;
        }
    }

    Some(result)
}

/// Copy the selected region to the clipboard.
pub fn copy_to_clipboard_with_peek(
    selection: ScreenRect,
    buffer: &CapturedDesktop,
    peek: Option<&WindowPeekImage>,
) -> ActionResult {
    let extracted = match peek {
        Some(p) => extract_selection_rgba_with_peek(selection, buffer, p),
        None => extract_selection_rgba(selection, buffer),
    };
    let Some((rgba, width, height)) = extracted else {
        log::warn!("copy: no selection or failed to extract");
        return ActionResult::Failed("No selection to copy".to_string());
    };

    match arboard::Clipboard::new() {
        Ok(mut clipboard) => {
            let img = arboard::ImageData {
                width: width as usize,
                height: height as usize,
                bytes: std::borrow::Cow::Owned(rgba),
            };
            if let Err(e) = clipboard.set_image(img) {
                log::error!("copy: clipboard set_image failed: {e}");
                ActionResult::Failed(format!("Failed to copy to clipboard: {e}"))
            } else {
                log::info!("copied {}x{} image to clipboard", width, height);
                ActionResult::Success
            }
        }
        Err(e) => {
            log::error!("copy: failed to open clipboard: {e}");
            ActionResult::Failed(format!("Failed to open clipboard: {e}"))
        }
    }
}

/// Save the selected region to a file via save dialog.
pub fn save_to_file_with_peek(
    selection: ScreenRect,
    buffer: &CapturedDesktop,
    peek: Option<&WindowPeekImage>,
    window: &Window,
) -> ActionResult {
    let extracted = match peek {
        Some(p) => extract_selection_rgba_with_peek(selection, buffer, p),
        None => extract_selection_rgba(selection, buffer),
    };
    let Some((rgba, width, height)) = extracted else {
        log::warn!("save: no selection or failed to extract");
        return ActionResult::Failed("No selection to save".to_string());
    };

    // Show save dialog (parented to capture window so it can't fall behind)
    let path = rfd::FileDialog::new()
        .add_filter("PNG Image", &["png"])
        .add_filter("JPEG Image", &["jpg", "jpeg"])
        .set_file_name("screenshot.png")
        .set_parent(window)
        .save_file();

    let Some(mut path) = path else {
        log::info!("save: dialog cancelled");
        return ActionResult::Cancelled;
    };

    // Auto-append extension if missing
    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .map(|s| s.to_lowercase());

    let format = match ext.as_deref() {
        Some("png") => image::ImageFormat::Png,
        Some("jpg") | Some("jpeg") => image::ImageFormat::Jpeg,
        _ => {
            // Default to PNG if no/unknown extension
            path.set_extension("png");
            image::ImageFormat::Png
        }
    };

    // Create image and save
    let img: image::RgbaImage = image::ImageBuffer::from_raw(width, height, rgba).expect("buffer size matches");

    if let Err(e) = img.save_with_format(&path, format) {
        log::error!("save: failed to write {:?}: {e}", path);
        ActionResult::Failed(format!("Failed to save file: {e}"))
    } else {
        log::info!("saved {}x{} image to {:?}", width, height, path);
        ActionResult::Success
    }
}
