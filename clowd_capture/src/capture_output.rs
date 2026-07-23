use winit::window::Window;

use crate::geometry::ScreenRect;
use crate::image_extract::{composite_cursor_rgba, extract_selection_rgba, extract_selection_rgba_with_peek};
use crate::system::{CapturedCursor, CapturedDesktop, WindowPeekImage};

/// Result of a Copy or Save action.
pub enum ActionResult {
    /// Operation completed successfully.
    Success,
    /// User cancelled the operation (e.g. dismissed save dialog).
    Cancelled,
    /// Operation failed with an error message.
    Failed(String),
}

/// Copy the selected region to the clipboard.
pub fn copy_to_clipboard_with_peek(
    selection: ScreenRect,
    buffer: &CapturedDesktop,
    peek: Option<&WindowPeekImage>,
    cursor: Option<&CapturedCursor>,
    cursor_visible: bool,
) -> ActionResult {
    let extracted = match peek {
        Some(p) => extract_selection_rgba_with_peek(selection, buffer, p),
        None => extract_selection_rgba(selection, buffer),
    };
    let Some((mut rgba, width, height)) = extracted else {
        log::warn!("copy: no selection or failed to extract");
        return ActionResult::Failed("No selection to copy".to_string());
    };
    if cursor_visible {
        if let Some(cur) = cursor {
            composite_cursor_rgba(&mut rgba, width, height, selection, cur);
        }
    }

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
    cursor: Option<&CapturedCursor>,
    cursor_visible: bool,
    window: &Window,
) -> ActionResult {
    let extracted = match peek {
        Some(p) => extract_selection_rgba_with_peek(selection, buffer, p),
        None => extract_selection_rgba(selection, buffer),
    };
    let Some((mut rgba, width, height)) = extracted else {
        log::warn!("save: no selection or failed to extract");
        return ActionResult::Failed("No selection to save".to_string());
    };
    if cursor_visible {
        if let Some(cur) = cursor {
            composite_cursor_rgba(&mut rgba, width, height, selection, cur);
        }
    }

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

    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .map(|s| s.to_lowercase());
    let format = match ext.as_deref() {
        Some("png") => image::ImageFormat::Png,
        Some("jpg") | Some("jpeg") => image::ImageFormat::Jpeg,
        _ => {
            path.set_extension("png");
            image::ImageFormat::Png
        }
    };

    let img: image::RgbaImage = image::ImageBuffer::from_raw(width, height, rgba).expect("buffer size matches");

    if let Err(e) = img.save_with_format(&path, format) {
        log::error!("save: failed to write {:?}: {e}", path);
        ActionResult::Failed(format!("Failed to save file: {e}"))
    } else {
        log::info!("saved {}x{} image to {:?}", width, height, path);
        ActionResult::Success
    }
}
