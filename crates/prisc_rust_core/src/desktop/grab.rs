//! Screen pixels read the one shared way: the eyedropper and the Draw on Screen backdrop
//! (Prisc.Ui, through prisc_interop's `prisc_screen_*`), the capture overlay's desktop and peek
//! images, and the scrolling driver's frames.
//!
//! Every reader gets the same thing: [`Image`], BGRA8, top-down, tightly packed (`width * 4` bytes
//! a row). The coordinate space is the platform capture space ([`super::screens`]): physical
//! virtual-desktop pixels on Windows, CoreGraphics global points on macOS, where an image comes
//! back at the display's backing resolution (a 400x300 point rect on a Retina display is an
//! 800x600 image).
//!
//! - Windows draws into a top-down 32 bpp DIB section ([`render`]): [`grab`] blits the screen DC
//!   into it, the overlay's window peek draws a `PrintWindow` into it. GDI leaves the fourth byte
//!   undefined, so a caller that hands the pixels on as an opaque picture asks for
//!   [`Options::opaque`].
//! - macOS copies a `CGWindowListCreateImage` of the on-screen windows ([`grab`]), or any other
//!   `CGImage` ([`from_cg_image`]), converting whichever 32-bit layout CoreGraphics chose
//!   ([`Layout`]) to BGRA.
//!
//! Permission is the caller's question, not this module's: an unpermitted macOS window-list image
//! succeeds and shows the desktop picture instead of the windows, so every caller checks
//! `prisc_platform::permissions` first (prisc_interop answers `PRISC_E_PERMISSION`, the overlay
//! refuses to start). The mouse cursor is never in the picture on macOS, and on Windows only when
//! [`Options::layered_windows`] composites it in on some drivers.

use super::screens::OsError;
use crate::geometry::{ScreenPoint, ScreenRect};

/// A grabbed picture: BGRA8, top-down, `width * 4` bytes a row.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Image {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
}

impl Image {
    /// Bytes per row (always `width * 4`).
    pub fn stride(&self) -> usize {
        self.width as usize * 4
    }
}

/// How [`grab`] reads the screen.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Options {
    /// Windows: blit with `CAPTUREBLT`, which composites layered windows (and on some drivers the
    /// cursor) into the picture. The overlay and Prisc.Ui want what the user sees; the scrolling
    /// driver does not, because the flag re-composites the whole screen on every blit and its
    /// settle loop blits twenty times a second. macOS ignores it: a window-list image always holds
    /// every on-screen window.
    pub layered_windows: bool,
    /// Force every alpha byte to 255. GDI leaves it undefined and nothing promises CoreGraphics'
    /// is opaque; a consumer that compares frames or encodes a PNG needs it settled.
    pub opaque: bool,
}

/// Why a grab produced no picture.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum GrabError {
    /// The rect has no area.
    Empty { width: i32, height: i32 },
    /// `width * height * 4` does not fit in memory arithmetic.
    TooLarge { width: i64, height: i64 },
    /// An OS call failed (or, on macOS, produced no image).
    Os(OsError),
    /// The OS handed back pixels in a layout this does not convert.
    Format(String),
    /// No screen grab on this platform.
    Unsupported,
}

impl std::fmt::Display for GrabError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            GrabError::Empty {
                width,
                height,
            } => write!(f, "the grab rect is {width}x{height}"),
            GrabError::TooLarge {
                width,
                height,
            } => write!(f, "a {width}x{height} grab overflows"),
            GrabError::Os(e) => e.fmt(f),
            GrabError::Format(what) => write!(f, "unsupported pixel format: {what}"),
            GrabError::Unsupported => f.write_str("there is no screen grab on this platform"),
        }
    }
}

impl std::error::Error for GrabError {}

/// The byte order of one 32-bit pixel in memory, as a CoreGraphics image may lay it out.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Layout {
    /// B G R A: 32-bit little-endian with alpha (or padding) first; every screen capture.
    Bgra,
    /// A B G R: 32-bit little-endian with alpha last.
    Abgr,
    /// A R G B: big-endian (or default order) with alpha first.
    Argb,
    /// R G B A: big-endian (or default order) with alpha last.
    Rgba,
}

impl Layout {
    /// The byte offsets of blue, green, red and alpha within one pixel.
    const fn offsets(self) -> [usize; 4] {
        match self {
            Layout::Bgra => [0, 1, 2, 3],
            Layout::Abgr => [1, 2, 3, 0],
            Layout::Argb => [3, 2, 1, 0],
            Layout::Rgba => [2, 1, 0, 3],
        }
    }

    /// The layout of a 32-bit, 8-bit-per-component CoreGraphics bitmap from its
    /// `CGBitmapInfo` bits; `None` for float components, packed 16/10-bit formats, a 16-bit byte
    /// order, or no alpha slot at all.
    pub const fn from_cg_bitmap_info(bits: u32) -> Option<Layout> {
        const ALPHA_MASK: u32 = 0x1F;
        const FLOAT: u32 = 1 << 8;
        const ORDER_MASK: u32 = 0x7000;
        const ORDER_DEFAULT: u32 = 0;
        const ORDER_32_LITTLE: u32 = 2 << 12;
        const ORDER_32_BIG: u32 = 4 << 12;
        const PIXEL_FORMAT_MASK: u32 = 0xF_0000;
        if bits & FLOAT != 0 || bits & PIXEL_FORMAT_MASK != 0 {
            return None;
        }
        // kCGImageAlpha: PremultipliedLast 1, PremultipliedFirst 2, Last 3, First 4,
        // NoneSkipLast 5, NoneSkipFirst 6
        let alpha_first = match bits & ALPHA_MASK {
            2 | 4 | 6 => true,
            1 | 3 | 5 => false,
            _ => return None,
        };
        match (bits & ORDER_MASK, alpha_first) {
            (ORDER_32_LITTLE, true) => Some(Layout::Bgra),
            (ORDER_32_LITTLE, false) => Some(Layout::Abgr),
            (ORDER_32_BIG | ORDER_DEFAULT, true) => Some(Layout::Argb),
            (ORDER_32_BIG | ORDER_DEFAULT, false) => Some(Layout::Rgba),
            _ => None,
        }
    }
}

/// `width * height * 4`, or [`GrabError::TooLarge`].
fn byte_len(width: usize, height: usize) -> Result<usize, GrabError> {
    width
        .checked_mul(height)
        .and_then(|n| n.checked_mul(4))
        .ok_or(GrabError::TooLarge {
            width: width as i64,
            height: height as i64,
        })
}

/// Repacks `height` rows of `width` 32-bit pixels, `src_stride` bytes apart in `layout`, into a
/// tightly packed BGRA buffer (the stride padding dropped), forcing alpha to 255 when `opaque`.
/// A source shorter than its last row is a [`GrabError::Format`].
pub fn pack_bgra(src: &[u8], width: usize, height: usize, src_stride: usize, layout: Layout, opaque: bool) -> Result<Vec<u8>, GrabError> {
    let row_len = byte_len(width, 1)?;
    let mut out = vec![0u8; byte_len(width, height)?];
    if row_len == 0 {
        return Ok(out);
    }
    if src_stride < row_len {
        return Err(GrabError::Format(format!(
            "rows are {src_stride} bytes apart but {row_len} bytes long"
        )));
    }
    let [b, g, r, a] = layout.offsets();
    for (y, dst_row) in out.chunks_exact_mut(row_len).enumerate() {
        let start = y * src_stride;
        let Some(src_row) = src.get(start..start + row_len) else {
            return Err(GrabError::Format(format!(
                "the image is short: row {y} of {height} lies past {} bytes",
                src.len()
            )));
        };
        if layout == Layout::Bgra {
            dst_row.copy_from_slice(src_row);
            if opaque {
                force_opaque(dst_row);
            }
            continue;
        }
        for (dst, px) in dst_row
            .chunks_exact_mut(4)
            .zip(src_row.chunks_exact(4))
        {
            dst[0] = px[b];
            dst[1] = px[g];
            dst[2] = px[r];
            dst[3] = if opaque { 0xFF } else { px[a] };
        }
    }
    Ok(out)
}

/// Sets every pixel's alpha byte (BGRA) to 255.
pub fn force_opaque(bgra: &mut [u8]) {
    for px in bgra.chunks_exact_mut(4) {
        px[3] = 0xFF;
    }
}

/// The screen inside `rect` (capture space) as an [`Image`]. `rect` must have area.
pub fn grab(rect: ScreenRect, options: Options) -> Result<Image, GrabError> {
    if rect.size.width <= 0 || rect.size.height <= 0 {
        return Err(GrabError::Empty {
            width: rect.size.width,
            height: rect.size.height,
        });
    }
    imp::grab(rect, options)
}

/// The colour of the screen pixel at `point` (capture space) as `0x00RRGGBB`, or `None` when the
/// point is on no screen.
pub fn pixel(point: ScreenPoint) -> Result<Option<u32>, GrabError> {
    imp::pixel(point)
}

/// `0x00RRGGBB` from the first pixel of a BGRA buffer.
#[cfg(any(target_os = "macos", test))]
fn rgb_of(bgra: &[u8]) -> Option<u32> {
    let px = bgra.get(..4)?;
    Some(u32::from(px[2]) << 16 | u32::from(px[1]) << 8 | u32::from(px[0]))
}

#[cfg(windows)]
pub use imp::render;

#[cfg(target_os = "macos")]
pub use imp::from_cg_image;

#[cfg(windows)]
mod imp {
    use std::ffi::c_void;
    use std::mem;

    use windows::Win32::Foundation::{GetLastError, WIN32_ERROR};
    use windows::Win32::Graphics::Gdi::{
        BitBlt, CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject, GdiFlush, GetDC, GetPixel, ReleaseDC, SelectObject,
        BITMAPINFO, BITMAPINFOHEADER, BI_RGB, CAPTUREBLT, CLR_INVALID, DIB_RGB_COLORS, HDC, HGDIOBJ, SRCCOPY,
    };

    use super::{byte_len, force_opaque, GrabError, Image, Options};
    use crate::desktop::screens::OsError;
    use crate::geometry::{ScreenPoint, ScreenRect};

    fn last_error(what: &str) -> GrabError {
        let WIN32_ERROR(code) = unsafe { GetLastError() };
        GrabError::Os(OsError {
            code: i64::from(code as i32),
            message: format!("{what} failed"),
        })
    }

    fn os(error: windows::core::Error, what: &str) -> GrabError {
        GrabError::Os(OsError {
            code: i64::from(error.code().0),
            message: format!("{what} failed: {}", error.message()),
        })
    }

    /// The screen DC (`GetDC(NULL)`), released on drop.
    struct ScreenDc(HDC);

    impl ScreenDc {
        fn get() -> Result<Self, GrabError> {
            let dc = unsafe { GetDC(None) };
            if dc.is_invalid() {
                return Err(last_error("GetDC(screen)"));
            }
            Ok(ScreenDc(dc))
        }
    }

    impl Drop for ScreenDc {
        fn drop(&mut self) {
            unsafe { ReleaseDC(None, self.0) };
        }
    }

    /// A memory DC with a top-down 32 bpp DIB section selected into it. The previous bitmap goes
    /// back into the DC before both are deleted (with the DIB still selected, `DeleteObject`
    /// declines silently and the pixels leak).
    struct Canvas {
        dc: HDC,
        previous: HGDIOBJ,
        dib: HGDIOBJ,
        bits: *const u8,
    }

    impl Drop for Canvas {
        fn drop(&mut self) {
            unsafe {
                if !self.previous.is_invalid() {
                    SelectObject(self.dc, self.previous);
                }
                if !self.dib.is_invalid() {
                    let _ = DeleteObject(self.dib);
                }
                let _ = DeleteDC(self.dc);
            }
        }
    }

    /// Lets `draw` paint a `width` x `height` memory DC (a top-down 32 bpp DIB section, so pixel 0
    /// is the top-left) and returns what it painted. The shared half of every Windows grab: the
    /// screen blit below and the overlay's `PrintWindow` peek.
    pub fn render(width: i32, height: i32, opaque: bool, draw: impl FnOnce(HDC) -> windows::core::Result<()>) -> Result<Image, GrabError> {
        if width <= 0 || height <= 0 {
            return Err(GrabError::Empty {
                width,
                height,
            });
        }
        let len = byte_len(width as usize, height as usize)?;
        unsafe {
            let dc = CreateCompatibleDC(None);
            if dc.is_invalid() {
                return Err(last_error("CreateCompatibleDC"));
            }
            let mut canvas = Canvas {
                dc,
                previous: HGDIOBJ::default(),
                dib: HGDIOBJ::default(),
                bits: std::ptr::null(),
            };
            let info = BITMAPINFO {
                bmiHeader: BITMAPINFOHEADER {
                    biSize: mem::size_of::<BITMAPINFOHEADER>() as u32,
                    biWidth: width,
                    // negative: a top-down DIB
                    biHeight: -height,
                    biPlanes: 1,
                    biBitCount: 32,
                    biCompression: BI_RGB.0,
                    ..Default::default()
                },
                ..Default::default()
            };
            let mut bits: *mut c_void = std::ptr::null_mut();
            let dib = CreateDIBSection(Some(dc), &info, DIB_RGB_COLORS, &mut bits, None, 0).map_err(|e| os(e, "CreateDIBSection"))?;
            canvas.dib = dib.into();
            if bits.is_null() {
                return Err(last_error("CreateDIBSection"));
            }
            canvas.bits = bits as *const u8;
            canvas.previous = SelectObject(dc, canvas.dib);

            draw(dc).map_err(|e| os(e, "drawing into the grab canvas"))?;
            // GDI may batch; the bits are read directly, so the batch has to land first.
            let _ = GdiFlush();

            let mut bgra = std::slice::from_raw_parts(canvas.bits, len).to_vec();
            if opaque {
                force_opaque(&mut bgra);
            }
            Ok(Image {
                bgra,
                width: width as u32,
                height: height as u32,
            })
        }
    }

    pub(super) fn grab(rect: ScreenRect, options: Options) -> Result<Image, GrabError> {
        let screen = ScreenDc::get()?;
        let rop = if options.layered_windows { SRCCOPY | CAPTUREBLT } else { SRCCOPY };
        let (x, y, w, h) = (rect.min_x(), rect.min_y(), rect.size.width, rect.size.height);
        render(w, h, options.opaque, |canvas| unsafe {
            BitBlt(canvas, 0, 0, w, h, Some(screen.0), x, y, rop)
        })
    }

    pub(super) fn pixel(point: ScreenPoint) -> Result<Option<u32>, GrabError> {
        let screen = ScreenDc::get()?;
        let value = unsafe { GetPixel(screen.0, point.x, point.y) };
        // CLR_INVALID for a coordinate outside the screen DC
        if value.0 == CLR_INVALID {
            return Ok(None);
        }
        // COLORREF is 0x00BBGGRR
        let (r, g, b) = (value.0 & 0xFF, (value.0 >> 8) & 0xFF, (value.0 >> 16) & 0xFF);
        Ok(Some(r << 16 | g << 8 | b))
    }
}

#[cfg(target_os = "macos")]
mod imp {
    #[allow(deprecated)]
    use objc2_core_graphics::{CGImage, CGWindowImageOption, CGWindowListCreateImage, CGWindowListOption};

    use super::{pack_bgra, rgb_of, GrabError, Image, Layout, Options};
    use crate::desktop::screens::{self, Fallback, OsError};
    use crate::desktop::{CGPoint, CGRect, CGSize, ImageBytes};
    use crate::geometry::{ScreenPoint, ScreenRect};

    /// A `CGImage`'s pixels as an [`Image`], converted from whichever 32-bit, 8-bit-per-component
    /// layout CoreGraphics chose; [`GrabError::Format`] for anything else.
    pub fn from_cg_image(image: &CGImage, opaque: bool) -> Result<Image, GrabError> {
        let bits = CGImage::bitmap_info(Some(image)).0;
        let bpp = CGImage::bits_per_pixel(Some(image));
        let bpc = CGImage::bits_per_component(Some(image));
        let layout = match (bpp, bpc) {
            (32, 8) => Layout::from_cg_bitmap_info(bits),
            _ => None,
        }
        .ok_or_else(|| GrabError::Format(format!("{bpp} bits per pixel, {bpc} per component, bitmap info 0x{bits:X}")))?;
        let pixels = ImageBytes::of(image).ok_or_else(|| GrabError::Format("the image has no readable pixels".into()))?;
        if pixels.width == 0 || pixels.height == 0 {
            return Err(GrabError::Format(format!("the image is {}x{}", pixels.width, pixels.height)));
        }
        let bgra = pack_bgra(pixels.bytes(), pixels.width, pixels.height, pixels.bytes_per_row, layout, opaque)?;
        Ok(Image {
            bgra,
            width: pixels.width as u32,
            height: pixels.height as u32,
        })
    }

    pub(super) fn grab(rect: ScreenRect, options: Options) -> Result<Image, GrabError> {
        let bounds = CGRect::new(
            CGPoint::new(f64::from(rect.min_x()), f64::from(rect.min_y())),
            CGSize::new(f64::from(rect.size.width), f64::from(rect.size.height)),
        );
        // On-screen windows only and no window id (kCGNullWindowID): whatever is visible in the
        // rect, at the display's real pixels. Deprecated in favour of ScreenCaptureKit, which no
        // Prisc capture path has moved to.
        #[allow(deprecated)]
        let image = CGWindowListCreateImage(
            bounds,
            CGWindowListOption::OptionOnScreenOnly,
            0,
            CGWindowImageOption::BestResolution,
        )
        .ok_or_else(|| {
            GrabError::Os(OsError {
                code: 0,
                message: format!(
                    "CGWindowListCreateImage returned no image for {},{} {}x{} (Screen Recording may have been revoked)",
                    rect.min_x(),
                    rect.min_y(),
                    rect.size.width,
                    rect.size.height
                ),
            })
        })?;
        from_cg_image(&image, options.opaque)
    }

    pub(super) fn pixel(point: ScreenPoint) -> Result<Option<u32>, GrabError> {
        // A window-list image of a rect on no display still comes back (opaque black), so the
        // point is checked against the displays first, the way GetPixel answers CLR_INVALID.
        if screens::at_point(point, Fallback::None)
            .map_err(GrabError::Os)?
            .is_none()
        {
            return Ok(None);
        }
        // A 1x1 point rect is 2x2 pixels on a Retina display; the first is the one at the point.
        let image = grab(
            ScreenRect::new(point, euclid::Size2D::new(1, 1)),
            Options {
                layered_windows: true,
                opaque: true,
            },
        )?;
        Ok(rgb_of(&image.bgra))
    }
}

#[cfg(not(any(windows, target_os = "macos")))]
mod imp {
    use super::{GrabError, Image, Options};
    use crate::geometry::{ScreenPoint, ScreenRect};

    pub(super) fn grab(_: ScreenRect, _: Options) -> Result<Image, GrabError> {
        Err(GrabError::Unsupported)
    }

    pub(super) fn pixel(_: ScreenPoint) -> Result<Option<u32>, GrabError> {
        Err(GrabError::Unsupported)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::geometry::RectExt;

    /// One pixel, blue 0x11 green 0x22 red 0x33 alpha 0x44, in each layout.
    const PIXEL_IN: [(Layout, [u8; 4]); 4] = [
        (Layout::Bgra, [0x11, 0x22, 0x33, 0x44]),
        (Layout::Abgr, [0x44, 0x11, 0x22, 0x33]),
        (Layout::Argb, [0x44, 0x33, 0x22, 0x11]),
        (Layout::Rgba, [0x33, 0x22, 0x11, 0x44]),
    ];

    #[test]
    fn every_layout_converts_to_bgra() {
        for (layout, px) in PIXEL_IN {
            assert_eq!(
                pack_bgra(&px, 1, 1, 4, layout, false).unwrap(),
                [0x11, 0x22, 0x33, 0x44],
                "{layout:?}"
            );
            assert_eq!(
                pack_bgra(&px, 1, 1, 4, layout, true).unwrap(),
                [0x11, 0x22, 0x33, 0xFF],
                "{layout:?} opaque"
            );
        }
    }

    #[test]
    fn row_padding_is_dropped() {
        // 2x2 BGRA with 4 padding bytes a row (stride 12)
        let src = [
            1, 2, 3, 4, 5, 6, 7, 8, 0xEE, 0xEE, 0xEE, 0xEE, 9, 10, 11, 12, 13, 14, 15, 16, 0xEE, 0xEE, 0xEE, 0xEE,
        ];
        assert_eq!(
            pack_bgra(&src, 2, 2, 12, Layout::Bgra, false).unwrap(),
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]
        );
        assert_eq!(
            pack_bgra(&src, 2, 2, 12, Layout::Argb, true).unwrap(),
            [4, 3, 2, 0xFF, 8, 7, 6, 0xFF, 12, 11, 10, 0xFF, 16, 15, 14, 0xFF]
        );
    }

    #[test]
    fn a_short_source_or_a_narrow_stride_is_refused() {
        let src = [0u8; 12];
        assert!(matches!(pack_bgra(&src, 2, 2, 8, Layout::Bgra, false), Err(GrabError::Format(_))));
        assert!(matches!(pack_bgra(&src, 2, 1, 4, Layout::Bgra, false), Err(GrabError::Format(_))));
        assert!(matches!(
            pack_bgra(&src, usize::MAX, 2, 8, Layout::Bgra, false),
            Err(GrabError::TooLarge { .. })
        ));
    }

    #[test]
    fn the_coregraphics_bitmap_info_names_the_layout() {
        const LITTLE: u32 = 2 << 12;
        const BIG: u32 = 4 << 12;
        // what screen captures carry: kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little
        assert_eq!(Layout::from_cg_bitmap_info(2 | LITTLE), Some(Layout::Bgra));
        assert_eq!(Layout::from_cg_bitmap_info(6 | LITTLE), Some(Layout::Bgra));
        assert_eq!(Layout::from_cg_bitmap_info(1 | LITTLE), Some(Layout::Abgr));
        assert_eq!(Layout::from_cg_bitmap_info(4 | BIG), Some(Layout::Argb));
        assert_eq!(Layout::from_cg_bitmap_info(4), Some(Layout::Argb));
        assert_eq!(Layout::from_cg_bitmap_info(5), Some(Layout::Rgba));
        assert_eq!(Layout::from_cg_bitmap_info(3 | BIG), Some(Layout::Rgba));
        // no alpha slot, alpha only, float components, a packed format, 16-bit order
        assert_eq!(Layout::from_cg_bitmap_info(0), None);
        assert_eq!(Layout::from_cg_bitmap_info(7), None);
        assert_eq!(Layout::from_cg_bitmap_info(2 | LITTLE | 1 << 8), None);
        assert_eq!(Layout::from_cg_bitmap_info(2 | LITTLE | 2 << 16), None);
        assert_eq!(Layout::from_cg_bitmap_info(2 | 1 << 12), None);
    }

    #[test]
    fn the_rgb_of_a_bgra_pixel() {
        assert_eq!(rgb_of(&[0x11, 0x22, 0x33, 0xFF]), Some(0x33_22_11));
        assert_eq!(rgb_of(&[1, 2]), None);
    }

    #[test]
    fn an_empty_rect_is_refused_before_the_os() {
        assert_eq!(
            grab(ScreenRect::from_xy_size(0, 0, 0, 10), Options::default()),
            Err(GrabError::Empty {
                width: 0,
                height: 10
            })
        );
    }

    /// Screen Recording is the caller's to check; on macOS without it the grab would succeed with
    /// the desktop picture, which still has the right size, so only the size is asserted.
    #[test]
    fn a_live_grab_has_the_right_shape() {
        if !cfg!(any(windows, target_os = "macos")) {
            assert_eq!(
                grab(ScreenRect::from_xy_size(0, 0, 64, 64), Options::default()),
                Err(GrabError::Unsupported)
            );
            return;
        }
        let Ok(Some(primary)) = crate::desktop::screens::primary() else {
            eprintln!("skipping: no primary screen");
            return;
        };
        let rect = ScreenRect::from_xy_size(primary.bounds.min_x(), primary.bounds.min_y(), 64, 64);
        let image = match grab(
            rect,
            Options {
                layered_windows: true,
                opaque: true,
            },
        ) {
            Ok(image) => image,
            Err(e) => {
                eprintln!("skipping: the grab failed ({e}); a session without a screen to read");
                return;
            }
        };
        let per_point = image.width / 64;
        assert!(per_point >= 1, "{}x{}", image.width, image.height);
        assert_eq!(image.width, 64 * per_point);
        assert_eq!(image.height, 64 * per_point);
        assert_eq!(image.bgra.len(), image.stride() * image.height as usize);
        assert!(image
            .bgra
            .chunks_exact(4)
            .all(|px| px[3] == 0xFF));
    }

    #[test]
    fn a_live_pixel_off_every_screen_is_none() {
        if !cfg!(any(windows, target_os = "macos")) {
            assert_eq!(pixel(ScreenPoint::new(0, 0)), Err(GrabError::Unsupported));
            return;
        }
        match pixel(ScreenPoint::new(-1_000_000, -1_000_000)) {
            Ok(value) => assert_eq!(value, None),
            Err(e) => eprintln!("skipping: no screen DC ({e})"),
        }
    }
}
