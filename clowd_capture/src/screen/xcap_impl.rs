use xcap::Monitor as XCapMonitor;

use crate::geometry::{RectExt, ScreenRect};
use anyhow::Result;
use image::{GenericImage, ImageBuffer, Rgba, RgbaImage};
use rayon::prelude::*;

pub fn capture_desktop() -> Result<(ScreenRect, RgbaImage, RgbaImage)> {
    let monitors = XCapMonitor::all()?;

    let mut min_x = std::i32::MAX;
    let mut min_y = std::i32::MAX;
    let mut max_x = std::i32::MIN;
    let mut max_y = std::i32::MIN;

    // Determine the bounding rectangle
    for monitor in &monitors {
        let scale = monitor.scale_factor();
        min_x = min_x.min((monitor.x() as f32 * scale) as i32);
        min_y = min_y.min((monitor.y() as f32 * scale) as i32);
        max_x = max_x.max(((monitor.x() + monitor.width() as i32) as f32 * scale) as i32);
        max_y = max_y.max(((monitor.y() + monitor.height() as i32) as f32 * scale) as i32);

        //max_x = max_x.max(monitor.x() + monitor.width() as i32);
        //max_y = max_y.max(monitor.y() + monitor.height() as i32);
    }

    let desktop_bounds = ScreenRect::from_exact(min_x, min_y, max_x, max_y);

    let desktop_width = (max_x - min_x) as u32;
    let desktop_height = (max_y - min_y) as u32;

    // Create a large image buffer to hold the entire desktop
    let mut desktop_image: ImageBuffer<Rgba<u8>, Vec<u8>> = ImageBuffer::new(desktop_width, desktop_height);

    // For each monitor, capture and copy it into the desktop image
    for monitor in &monitors {
        let captured = monitor.capture_image()?;
        // 'captured' should be something like xcap::image::ImageBuffer<xcap::image::Rgba<u8>, _>
        // Convert that into an image crate buffer. Assume `capture_image()` returns a compatible buffer.

        // Extract raw data
        let raw_data = captured.into_raw();
        let m_width = (monitor.width() as f32 * monitor.scale_factor()) as u32;
        let m_height = (monitor.height() as f32 * monitor.scale_factor()) as u32;
        // let m_height = monitor.height();

        // Convert raw monitor image into an image crate buffer
        let monitor_image: ImageBuffer<Rgba<u8>, Vec<u8>> =
            ImageBuffer::from_raw(m_width, m_height, raw_data).expect("Failed to convert captured image");

        let offset_x = (monitor.x() - min_x) as u32;
        let offset_y = (monitor.y() - min_y) as u32;

        // Copy the monitor image into the correct location on the desktop image
        desktop_image
            .copy_from(&monitor_image, offset_x, offset_y)
            .expect("Failed to copy monitor image into desktop image");
    }

    let buffer = desktop_image.into_raw();

    let mut gray_rgba_buffer = vec![0u8; (desktop_width * desktop_height * 4) as usize];
    gray_rgba_buffer
        .par_chunks_exact_mut(4)
        .enumerate()
        .for_each(|(i, rgba)| {
            let idx = i * 4;
            let b = buffer[idx] as f32;
            let g = buffer[idx + 1] as f32;
            let r = buffer[idx + 2] as f32;
            let a = buffer[idx + 3];
            let gray = (0.299 * r + 0.587 * g + 0.114 * b) as u8;

            rgba[0] = gray;
            rgba[1] = gray;
            rgba[2] = gray;
            rgba[3] = a;
        });

    let bgra_image = ImageBuffer::<Rgba<u8>, Vec<u8>>::from_raw(desktop_width as u32, desktop_height as u32, buffer)
        .ok_or_else(|| anyhow!("RgbaImage::from_raw failed"))?;

    let gray_image = ImageBuffer::<Rgba<u8>, Vec<u8>>::from_raw(desktop_width as u32, desktop_height as u32, gray_rgba_buffer)
        .ok_or_else(|| anyhow!("RgbaImage::from_raw failed"))?;

    Ok((desktop_bounds, bgra_image, gray_image))
}

pub fn all_monitors() -> Result<Vec<XCapMonitor>> {
    Ok(XCapMonitor::all()?)
}