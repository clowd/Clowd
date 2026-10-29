//! The overlay's Windows grabs, both through the shared GDI grab (`desktop::grab`, the one
//! Prisc.Ui and the scrolling driver use): the whole virtual desktop for the frozen screenshot,
//! and one window drawn with `PrintWindow` for the peek.

use anyhow::Result;
use prisc_rust_core::desktop::grab::{self, Options};
use prisc_rust_core::geometry::ScreenRect;
use windows::Win32::{
    Foundation::HWND,
    Storage::Xps::{PrintWindow, PRINT_WINDOW_FLAGS},
};

/// Raw bitmap product of `capture_desktop`. Public to its own module only;
/// the public `CapturedDesktop` (defined in `system/mod.rs`) wraps this and
/// adds per-monitor metadata.
pub struct DesktopBitmap {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
    pub bounds: ScreenRect,
}

/// The virtual desktop inside `bounds` as raw BGRA, top-down (pixel (0,0) is the screen's
/// top-left, the texture convention the shaders assume, so the upload needs no Y flip).
/// `CAPTUREBLT` composites layered windows in. Alpha is left as GDI wrote it: the GPU uploads
/// this buffer straight into a `Bgra8UnormSrgb` texture and never reads it, so a CPU pass over
/// ~33 MB at 4K would buy nothing.
pub fn capture_desktop(bounds: &ScreenRect) -> Result<DesktopBitmap> {
    let bounds = *bounds;
    if bounds.width() <= 0 || bounds.height() <= 0 {
        bail!("virtual desktop has invalid dimensions: {}x{}", bounds.width(), bounds.height());
    }
    let image = grab::grab(
        bounds,
        Options {
            layered_windows: true,
            opaque: false,
        },
    )?;
    Ok(DesktopBitmap {
        bgra: image.bgra,
        width: image.width,
        height: image.height,
        bounds,
    })
}

/// Capture a single window's content via PrintWindow.
/// `raw_rect` should be the GetWindowRect bounds (includes invisible border).
/// Returns BGRA bytes at those dimensions, or None on failure.
pub fn capture_window_image(hwnd: HWND, raw_rect: &ScreenRect) -> Option<(Vec<u8>, u32, u32)> {
    let (w, h) = (raw_rect.width(), raw_rect.height());
    if w <= 0 || h <= 0 {
        return None;
    }
    // PW_RENDERFULLCONTENT = 0x0002
    match grab::render(w, h, false, |canvas| {
        unsafe { PrintWindow(hwnd, canvas, PRINT_WINDOW_FLAGS(2)) }.ok()
    }) {
        Ok(image) => Some((image.bgra, image.width, image.height)),
        Err(e) => {
            warn!("PrintWindow failed for hwnd {hwnd:?}: {e}");
            None
        }
    }
}
