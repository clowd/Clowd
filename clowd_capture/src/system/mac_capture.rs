use std::sync::Mutex;

use anyhow::Result;
use core_graphics::access::ScreenCaptureAccess;
use core_graphics::display::CGDisplay;
use core_graphics::geometry::{CGPoint, CGRect, CGSize};
use core_graphics::image::CGImage;
use core_graphics::window::{
    self, kCGWindowImageBestResolution, kCGWindowImageBoundsIgnoreFraming, kCGWindowListOptionIncludingWindow, CGWindowID,
};

use crate::system::MonitorInfo;
use clowd_rust_core::geometry::ScreenRect;

pub struct DesktopBitmap {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
}

/// A `CGImage` parked for the main thread. `CGImage` is a bare CF pointer, so
/// Rust considers it neither `Send` nor `Sync` and it cannot ride along inside
/// `CapturedDesktop`, which is `Arc`'d from the screenshot thread to the main
/// thread and to every render worker. The objects themselves are immutable and
/// refcounted atomically, which is precisely the case `unsafe impl Send` is for.
struct DisplayImage(CGImage);
unsafe impl Send for DisplayImage {}

/// Per-display images from the most recent [`capture_bitmap`], keyed by the
/// monitor rect they cover. `capture_bitmap` composites each display into the
/// virtual-desktop buffer and would otherwise drop these; the overlay window
/// for that display wants exactly this image as its backing-layer contents, and
/// reconstructing it from the composite costs a row-by-row crop plus a
/// `CGBitmapContextCreateImage` — both on the main thread, per monitor, while
/// the user is waiting for the first frame.
static DISPLAY_IMAGES: Mutex<Vec<(ScreenRect, DisplayImage)>> = Mutex::new(Vec::new());

/// Claim the untouched capture of the display covering `bounds`, if there was
/// one. Taking rather than borrowing means the backing store (tens of MB for a
/// 4K display) is released as soon as the layer that wants it has retained it.
///
/// Ordering is guaranteed by the screenshot latch: nothing has a
/// `CapturedDesktop` to ask about until `capture_bitmap` has returned.
pub fn take_display_image(bounds: ScreenRect) -> Option<CGImage> {
    let mut images = DISPLAY_IMAGES.lock().ok()?;
    let idx = images
        .iter()
        .position(|(b, _)| *b == bounds)?;
    Some(images.swap_remove(idx).1 .0)
}

/// Whether the process may capture the screen. Reported to `main()` so the
/// capturer can refuse to start; it never prompts and never opens System
/// Settings, because Screen Recording is the shell's to explain and request
/// (Settings → General → Permissions).
pub fn has_screen_recording_permission() -> bool {
    ScreenCaptureAccess.preflight()
}

/// Capture the desktop bitmap using pre-enumerated monitors for
/// positioning. Re-enumerates display IDs (cheap CG call) and composites
/// each display into a single BGRA buffer. Assumes Screen Recording is
/// already granted — `main()` gates on that before anything runs.
pub fn capture_bitmap(monitors: &[MonitorInfo]) -> Result<DesktopBitmap> {
    let vd = super::virtual_desktop_bounds(monitors);
    let vd_w = vd.width() as usize;
    let vd_h = vd.height() as usize;

    if vd_w == 0 || vd_h == 0 {
        bail!("virtual desktop has invalid dimensions: {}x{}", vd_w, vd_h);
    }

    let mut bgra = vec![0u8; vd_w * vd_h * 4];

    let display_ids = CGDisplay::active_displays().map_err(|e| anyhow!("CGGetActiveDisplayList failed: {:?}", e))?;

    // Zip monitors with display_ids; truncate to the shorter list in
    // case a display connected/disconnected between enumeration and capture.
    let count = monitors.len().min(display_ids.len());
    let mut captured = 0;
    let mut display_images: Vec<(ScreenRect, DisplayImage)> = Vec::with_capacity(count);
    for i in 0..count {
        let monitor = &monitors[i];
        let display = CGDisplay::new(display_ids[i]);
        let image = match display.image() {
            Some(img) => img,
            None => {
                warn!("CGDisplayCreateImage returned null for display {} — skipping", display_ids[i]);
                continue;
            }
        };

        let img_w = image.width();
        let img_h = image.height();
        let bpr = image.bytes_per_row();
        let bpp = image.bits_per_pixel();

        if bpp != 32 {
            warn!("Display {} has unexpected bits_per_pixel={}, skipping", display_ids[i], bpp);
            continue;
        }

        let data = image.data();
        let src = data.bytes();

        let dest_x = (monitor.bounds.min_x() - vd.min_x()) as usize;
        let dest_y = (monitor.bounds.min_y() - vd.min_y()) as usize;

        let copy_w = img_w.min(monitor.bounds.width() as usize);
        let copy_h = img_h.min(monitor.bounds.height() as usize);

        for row in 0..copy_h {
            let src_start = row * bpr;
            let src_end = src_start + copy_w * 4;
            let dst_start = ((dest_y + row) * vd_w + dest_x) * 4;
            let dst_end = dst_start + copy_w * 4;

            if src_end <= src.len() && dst_end <= bgra.len() {
                bgra[dst_start..dst_end].copy_from_slice(&src[src_start..src_end]);
            }
        }
        captured += 1;

        // Park the image for `render::window`'s backing layer instead of
        // letting it drop. Only an exact 1:1 match is parked: when the
        // captured image is a different size than the monitor rect the
        // composite path above takes a top-left sub-rect of it, and a layer
        // handed the whole image would resize it instead — different pixels.
        if img_w == monitor.bounds.width() as usize && img_h == monitor.bounds.height() as usize {
            display_images.push((monitor.bounds, DisplayImage(image)));
        }
    }

    if let Ok(mut stash) = DISPLAY_IMAGES.lock() {
        *stash = display_images;
    }

    // A per-display null is tolerable (the region stays black), but zero captures
    // means the overlay would show a fully black desktop — most likely a TCC verdict
    // that differs from the preflight `main()` ran. Fail so the shell reports it.
    if captured == 0 && count > 0 {
        bail!("CGDisplayCreateImage produced no image for any of {count} display(s) — Screen Recording may be denied for this process");
    }

    Ok(DesktopBitmap {
        bgra,
        width: vd_w as u32,
        height: vd_h as u32,
    })
}

/// Capture a single window's image via CGWindowListCreateImage.
/// Returns BGRA pixel bytes and dimensions, or None on failure.
pub fn capture_window_image(window_id: CGWindowID) -> Option<(Vec<u8>, u32, u32)> {
    let cg_null = CGRect::new(&CGPoint::new(f64::INFINITY, f64::INFINITY), &CGSize::new(0.0, 0.0));

    let image = window::create_image(
        cg_null,
        kCGWindowListOptionIncludingWindow,
        window_id,
        kCGWindowImageBestResolution | kCGWindowImageBoundsIgnoreFraming,
    )?;

    let width = image.width();
    let height = image.height();
    let bpr = image.bytes_per_row();
    let bpp = image.bits_per_pixel();

    if bpp != 32 || width == 0 || height == 0 {
        warn!(
            "capture_window_image: unexpected format bpp={} {}x{} for window {}",
            bpp, width, height, window_id
        );
        return None;
    }

    let data = image.data();
    let src = data.bytes();

    let mut bgra = vec![0u8; width * height * 4];
    for row in 0..height {
        let src_start = row * bpr;
        let src_end = src_start + width * 4;
        let dst_start = row * width * 4;
        let dst_end = dst_start + width * 4;
        if src_end <= src.len() {
            bgra[dst_start..dst_end].copy_from_slice(&src[src_start..src_end]);
        }
    }

    Some((bgra, width as u32, height as u32))
}
