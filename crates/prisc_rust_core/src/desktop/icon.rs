//! The Windows cursor bitmaps `read_cursor` hands back, and pure helpers for
//! reading them. Nothing here carries a Win32 type, so the overlay's and the
//! recorder's conversions can be tested on every host.

/// A cursor's pixels, read exactly as GDI stores them. Every plane is
/// top-down; 1bpp planes are MSB-first with [`mono_stride`] bytes per row.
pub enum IconPixels {
    /// A 32bpp color cursor as BGRA. `and_mask` is the 1bpp AND plane, read
    /// only when the color plane carries no alpha (a legacy masked cursor);
    /// a modern alpha cursor's mask is vestigial and left unread.
    Color { bgra: Vec<u8>, and_mask: Option<Vec<u8>> },
    /// A monochrome cursor: GDI draws it as `dst = (dst AND and) XOR xor`.
    Mono { and: Vec<u8>, xor: Vec<u8> },
    /// The last resort when `GetDIBits` refuses: the cursor drawn by
    /// `DrawIconEx` over solid black and over solid white, both BGRA. The
    /// difference between the two recovers alpha and screen inversion.
    Rendered { over_black: Vec<u8>, over_white: Vec<u8> },
}

pub struct IconBitmap {
    pub width: u32,
    pub height: u32,
    /// Hotspot in bitmap pixels.
    pub hotspot_x: i32,
    pub hotspot_y: i32,
    pub pixels: IconPixels,
}

/// Scanline stride in bytes of a 1bpp DIB as `GetDIBits` writes it: rows are
/// DWORD-aligned.
pub fn mono_stride(w: u32) -> usize {
    (w as usize).div_ceil(32) * 4
}

/// Whether the 1bpp `plane` (MSB-first, `stride` bytes per row) has pixel
/// (`x`, `y`) set.
pub fn mono_bit(plane: &[u8], stride: usize, x: usize, y: usize) -> bool {
    plane[y * stride + x / 8] & (0x80u8 >> (x % 8)) != 0
}

/// Splits a mono cursor's double-height mask bitmap (AND plane stacked on top
/// of the XOR plane) into its two planes. `h` is the cursor height, i.e. half
/// the bitmap height; `None` if the buffer is too short.
pub fn split_mono_planes(data: &[u8], h: u32, stride: usize) -> Option<(&[u8], &[u8])> {
    let half = h as usize * stride;
    if data.len() < half * 2 {
        return None;
    }
    Some((&data[..half], &data[half..half * 2]))
}

/// Whether a BGRA buffer carries any alpha at all: distinguishes a modern
/// alpha cursor (whose AND mask is vestigial) from a legacy masked one whose
/// 32bpp plane leaves the alpha channel at zero everywhere.
pub fn has_alpha(bgra: &[u8]) -> bool {
    bgra.as_chunks::<4>()
        .0
        .iter()
        .any(|p| p[3] != 0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mono_rows_are_dword_aligned() {
        assert_eq!(mono_stride(1), 4);
        assert_eq!(mono_stride(32), 4);
        assert_eq!(mono_stride(33), 8);
    }

    #[test]
    fn mono_bits_read_msb_first_within_the_stride() {
        // 33 px wide: pixel (32, 1) lives at row offset 8 + byte 4, bit 0x80.
        let stride = mono_stride(33);
        let mut plane = vec![0u8; stride * 2];
        plane[stride + 4] = 0x80;
        assert!(mono_bit(&plane, stride, 32, 1));
        assert!(!mono_bit(&plane, stride, 32, 0));
        assert!(!mono_bit(&plane, stride, 31, 1));
    }

    #[test]
    fn double_height_mask_splits_into_and_over_xor() {
        let stride = mono_stride(8);
        let buf = [
            0xFFu8, 0, 0, 0, // AND row 0
            0x00, 0, 0, 0, // AND row 1
            0x0F, 0, 0, 0, // XOR row 0
            0xF0, 0, 0, 0, // XOR row 1
        ];
        let (and, xor) = split_mono_planes(&buf, 2, stride).unwrap();
        assert_eq!(and, &buf[..8]);
        assert_eq!(xor, &buf[8..]);
        // A short buffer must refuse rather than mis-split.
        assert!(split_mono_planes(&buf[..12], 2, stride).is_none());
    }

    #[test]
    fn has_alpha_detects_any_nonzero_alpha() {
        assert!(!has_alpha(&[10, 20, 30, 0, 40, 50, 60, 0]));
        assert!(has_alpha(&[10, 20, 30, 0, 40, 50, 60, 1]));
        assert!(!has_alpha(&[]));
    }
}
