//! One screenshot of the fixed capture region.
//!
//! The whole of the driver's imaging: a single call that hands back the
//! region's pixels as BGRA, top-down, tightly packed. [`win`] does it with
//! `BitBlt` + `GetDIBits`, [`mac`] with `CGWindowListCreateImage`; both
//! deliberately leave the mouse cursor out, because the driver parks the real
//! cursor *inside* the region for the whole run and a cursor blob baked into
//! every frame is something the stitcher would have to register around.
//!
//! Kept in BGRA — the order both platforms hand back — because everything
//! upstream of the final PNG only ever compares and copies whole rows, so the
//! channel order is irrelevant until [`crate::output`] encodes the composite.
//!
//! ## Pixels are not points
//!
//! `capture_region` takes a rect in the platform capture space
//! ([`crate::input`]) and returns pixels, and on macOS those are not the same
//! unit: a 400×800 point region on a Retina display comes back 800×1600.
//! That is deliberate — the composite is the artefact the user keeps, and
//! throwing away half its resolution to make two numbers match would be a
//! poor trade — but it means the *frame's* dimensions, never the region's,
//! are what may be compared against a displacement measured by the stitcher.
//! `drive::adapt_ticks` is the one place that matters.

use anyhow::Result;

use clowd_rust_core::geometry::ScreenRect;

#[cfg(target_os = "macos")]
mod mac;
#[cfg(windows)]
mod win;

/// One capture of the region: raw BGRA, top-down, `width * height * 4`
/// bytes.
#[derive(Clone)]
pub struct Frame {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
}

impl Frame {
    /// Bytes per row. All the row arithmetic in the stitcher and the settle
    /// comparison goes through this rather than open-coding `w * 4`.
    pub fn stride(&self) -> usize {
        self.width as usize * 4
    }
}

/// Capture `rect` off the screen. Costs one round of graphics-object churn
/// per call (a millisecond or two at typical region sizes); the settle loop
/// leans on that being cheap enough to run at 20 Hz.
///
/// Every call in a run passes the same rect and so returns the same
/// dimensions — the stitcher and the settle comparison both require it, and
/// both reject a pair that disagrees rather than trusting this.
pub fn capture_region(rect: ScreenRect) -> Result<Frame> {
    // Rejected here rather than deeper in: a zero-width blit fails with an OS
    // error that says nothing about where the bad rect came from.
    if rect.width() <= 0 || rect.height() <= 0 {
        bail!("capture region has invalid dimensions: {}x{}", rect.width(), rect.height());
    }

    #[cfg(windows)]
    let frame = win::capture_region(rect)?;
    #[cfg(target_os = "macos")]
    let frame = mac::capture_region(rect)?;

    Ok(frame)
}
