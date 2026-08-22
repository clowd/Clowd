//! The macOS half of [`crate::frame`]: one `CGWindowListCreateImage` of the
//! region.
//!
//! `CGWindowListCreateImage` rather than `CGDisplayCreateImage`, for the same
//! reason the overlay uses it for its peek captures: it takes a rect in the
//! global CG coordinate space, which is exactly the space the `scroll` marker
//! carried, so the region goes straight in with no display lookup and no
//! per-display coordinate conversion. Both APIs are formally deprecated in
//! favor of ScreenCaptureKit; the overlay has not moved either, and moving
//! only the driver would mean two capture stacks with two sets of behavior
//! in one feature.
//!
//! `kCGWindowImageBestResolution` asks for the display's real pixels, so a
//! Retina region comes back at 2× its point dimensions — see
//! [`crate::frame`]'s note on why that is worth the care it costs. The
//! cursor is never part of a window-list image, which is exactly what this
//! driver needs.

use anyhow::Result;

use core_graphics::geometry::{CGPoint, CGRect, CGSize};
use core_graphics::window::{self, kCGNullWindowID, kCGWindowImageBestResolution, kCGWindowListOptionOnScreenOnly};

use super::Frame;
use clowd_rust_core::geometry::ScreenRect;

/// Capture `rect` (CG points). Dimensions are already known to be positive —
/// the shared wrapper in [`crate::frame`] rejects a degenerate rect before
/// this runs.
pub fn capture_region(rect: ScreenRect) -> Result<Frame> {
    let bounds = CGRect::new(
        &CGPoint::new(rect.min_x() as f64, rect.min_y() as f64),
        &CGSize::new(rect.width() as f64, rect.height() as f64),
    );

    // On-screen windows only, and no window id: that combination is "give me
    // whatever is visible in this rect", which is the screenshot we want.
    let image = window::create_image(
        bounds,
        kCGWindowListOptionOnScreenOnly,
        kCGNullWindowID,
        kCGWindowImageBestResolution,
    )
    .ok_or_else(|| anyhow!("CGWindowListCreateImage returned null for {rect:?}; Screen Recording may have been revoked"))?;

    let (width, height) = (image.width(), image.height());
    let bits_per_pixel = image.bits_per_pixel();
    if bits_per_pixel != 32 {
        bail!("captured region is {bits_per_pixel} bits per pixel; expected 32");
    }
    if width == 0 || height == 0 {
        bail!("captured region is {width}x{height} pixels");
    }

    // The image's rows are padded to the graphics stack's alignment, which is
    // rarely `width * 4`, so the copy is row by row into a tightly packed
    // buffer — everything downstream indexes by `Frame::stride`.
    let stride = width
        .checked_mul(4)
        .ok_or_else(|| anyhow!("capture dimensions overflow: {width}x{height}"))?;
    let bytes_per_row = image.bytes_per_row();
    let data = image.data();
    let src = data.bytes();

    let mut bgra = vec![0u8; stride * height];
    for row in 0..height {
        let src_start = row * bytes_per_row;
        let Some(src_row) = src.get(src_start..src_start + stride) else {
            bail!("captured region is short: row {row} of {height} lies past {} bytes", src.len());
        };
        let dst_start = row * stride;
        bgra[dst_start..dst_start + stride].copy_from_slice(src_row);
        // Force the row opaque. Composited screen content is opaque in
        // practice, but nothing in the API promises it, and an alpha channel
        // that wobbled between frames would make the settle comparison see
        // movement in a still page — and land a semi-transparent PNG in the
        // editor.
        for pixel in bgra[dst_start..dst_start + stride]
            .as_chunks_mut::<4>()
            .0
            .iter_mut()
        {
            pixel[3] = 0xFF;
        }
    }

    Ok(Frame {
        bgra,
        width: width as u32,
        height: height as u32,
    })
}
