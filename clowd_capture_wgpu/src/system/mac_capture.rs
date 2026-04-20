use anyhow::Result;
use core_graphics::access::ScreenCaptureAccess;
use core_graphics::display::CGDisplay;

use crate::geometry::ScreenRect;
use crate::system::MonitorInfo;

pub struct DesktopBitmap {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
    pub bounds: ScreenRect,
}

pub fn capture_desktop() -> Result<(DesktopBitmap, Vec<MonitorInfo>)> {
    // --- Screen Recording permission gate ---
    let access = ScreenCaptureAccess;
    if !access.preflight() {
        // First attempt: trigger the macOS system prompt (one-time).
        if !access.request() {
            use xdialog::XDialogIcon::Warning;
            let open_settings = xdialog::show_message_ok_cancel(
                "Clowd Capture",
                "Screen Recording Permission Required",
                "Clowd Capture needs Screen Recording permission to capture your screen.\n\n\
                 Click OK to open System Settings, then enable Clowd Capture in the list.\n\
                 You may need to restart the app after granting permission.",
                Warning,
            )
            .unwrap_or(false);

            if open_settings {
                let _ = std::process::Command::new("open")
                    .arg("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")
                    .spawn();
            }

            bail!("Screen Recording permission not granted");
        }
    }

    // --- Enumerate monitors and compute virtual desktop ---
    let monitors = super::mac_monitor::all_monitors()?;
    if monitors.is_empty() {
        bail!("No active displays found");
    }

    let display_ids = CGDisplay::active_displays().map_err(|e| anyhow!("CGGetActiveDisplayList failed: {:?}", e))?;

    let vd = super::mac_monitor::virtual_desktop_bounds(&monitors);
    let vd_w = vd.width() as usize;
    let vd_h = vd.height() as usize;

    if vd_w == 0 || vd_h == 0 {
        bail!("virtual desktop has invalid dimensions: {}x{}", vd_w, vd_h);
    }

    let mut bgra = vec![0u8; vd_w * vd_h * 4];

    // --- Capture each display and composite into the buffer ---
    for (monitor, &display_id) in monitors.iter().zip(display_ids.iter()) {
        let display = CGDisplay::new(display_id);
        let image = match display.image() {
            Some(img) => img,
            None => {
                warn!("CGDisplayCreateImage returned null for display {} — skipping", display_id);
                continue;
            }
        };

        let img_w = image.width();
        let img_h = image.height();
        let bpr = image.bytes_per_row();
        let bpp = image.bits_per_pixel();

        if bpp != 32 {
            warn!("Display {} has unexpected bits_per_pixel={}, skipping", display_id, bpp);
            continue;
        }

        // CGDisplayCreateImage on macOS returns BGRA natively
        // (kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little).
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
    }

    let bitmap = DesktopBitmap {
        bgra,
        width: vd_w as u32,
        height: vd_h as u32,
        bounds: vd,
    };

    Ok((bitmap, monitors))
}
