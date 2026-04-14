use winit::window::Window;

use crate::geometry::RectExt;
use crate::geometry::ScreenRect;
use crate::system::CapturedDesktop;

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
pub fn extract_selection_rgba(
    selection: ScreenRect,
    buffer: &CapturedDesktop,
) -> Option<(Vec<u8>, u32, u32)> {
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

/// Copy the selected region to the clipboard.
pub fn copy_to_clipboard(selection: ScreenRect, buffer: &CapturedDesktop) -> ActionResult {
    let Some((rgba, width, height)) = extract_selection_rgba(selection, buffer) else {
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
pub fn save_to_file(
    selection: ScreenRect,
    buffer: &CapturedDesktop,
    window: &Window,
) -> ActionResult {
    let Some((rgba, width, height)) = extract_selection_rgba(selection, buffer) else {
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
    let img: image::RgbaImage =
        image::ImageBuffer::from_raw(width, height, rgba).expect("buffer size matches");

    if let Err(e) = img.save_with_format(&path, format) {
        log::error!("save: failed to write {:?}: {e}", path);
        ActionResult::Failed(format!("Failed to save file: {e}"))
    } else {
        log::info!("saved {}x{} image to {:?}", width, height, path);
        ActionResult::Success
    }
}
