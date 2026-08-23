//! Cursor sprite capture: the platform-neutral half of the `cursor_image`
//! sidecar rows (DESIGN §1, jsonl v2).
//!
//! The platform modules rasterize the live cursor into a [`RawSprite`] — an
//! alpha color plane plus, for legacy XOR cursors, a separate mask plane so
//! the editor can recompute screen inversion against the actual underlying
//! pixels. Everything here is pure and unit-testable: decomposing Windows
//! AND/XOR bitmaps into those planes, hashing for the writer's dedupe map,
//! and encoding a sprite as its wire row (PNG + base64).
//!
//! Mask pixel semantics (a faithful XOR plane over the AND=1 region):
//! **white** = screen-invert, **black** = XOR-with-black (a no-op, preserved
//! for fidelity), **transparent** = the mask does not apply (`bmp` owns the
//! pixel). The editor draws `bmp` SrcOver and then the mask with a Difference
//! blend, which realizes all three values in one draw.
//!
//! Colored-XOR approximation: Windows XOR cursors can carry a non-mono color
//! under AND=1, meaning `screen XOR color`. The mask plane only carries
//! white/black, so near-white colors (all channels ≥ 0xC0) become a white
//! (invert) mask pixel and anything else degrades to an opaque `bmp` pixel of
//! that color. Such cursors are vanishingly rare; white (exact invert) and
//! black (exact no-op) — the cases that actually occur — are lossless.

use serde::Serialize;

/// A rasterized cursor, straight off the platform capture. Pixel planes are
/// raw and unencoded (except where noted on [`SpritePixels`]); the writer
/// thread turns a deduped sprite into its wire row via [`encode_row`].
pub struct RawSprite {
    /// The CursorKind wire string classified at capture time — the same value
    /// the frame row's `c` carries (kind is an attribute of the shape).
    pub kind: &'static str,
    /// Native size in physical pixels.
    pub w: u32,
    pub h: u32,
    /// Hotspot in sprite pixels.
    pub hotx: i32,
    pub hoty: i32,
    /// The color/opaque plane (straight alpha).
    pub bmp: SpritePixels,
    /// The XOR plane as BGRA (white / black / transparent, see the module
    /// docs); `None` = plain alpha bitmap cursor.
    pub mask: Option<Vec<u8>>,
}

/// A sprite plane's pixel payload. Windows rasterization hands raw BGRA and
/// defers PNG encoding to the writer thread; macOS gets a finished PNG from
/// AppKit at the seed-gated capture site (at most once per actual cursor
/// change), so re-encoding it would only add cost and a decode dependency.
pub enum SpritePixels {
    /// Raw BGRA, straight alpha, `w * h * 4` bytes.
    #[cfg_attr(not(target_os = "windows"), allow(dead_code))]
    Bgra(Vec<u8>),
    /// An already-encoded PNG.
    #[cfg_attr(not(target_os = "macos"), allow(dead_code))]
    Png(Vec<u8>),
}

impl SpritePixels {
    /// The payload bytes, whichever encoding — the dedupe hash input.
    pub fn bytes(&self) -> &[u8] {
        match self {
            SpritePixels::Bgra(b) | SpritePixels::Png(b) => b,
        }
    }
}

impl RawSprite {
    /// Content hash for the writer's dedupe map. Never on the wire (a u64 in
    /// JSON would hit the C# parser's f64 precision trap), and always keyed
    /// together with `(w, h, byte_len)` so a collision needs matching
    /// dimensions and sizes too.
    pub fn content_hash(&self) -> u64 {
        let mut h = fnv1a64(FNV_OFFSET_BASIS, self.bmp.bytes());
        if let Some(mask) = &self.mask {
            h = fnv1a64(h, mask);
        }
        h
    }

    /// Total payload size — the dedupe key's length component, and what the
    /// writer's cumulative sprite-bytes cap counts.
    pub fn byte_len(&self) -> usize {
        self.bmp.bytes().len() + self.mask.as_ref().map_or(0, |m| m.len())
    }
}

/// Per-tick outcome of the platform's sprite capture, consumed by the writer
/// thread (which owns all dedupe/id state).
pub enum SpriteEvent {
    /// Same cursor as last tick — keep the current sprite id.
    Unchanged,
    /// No sprite available (cursor hidden, or capture failed/degraded): frame
    /// rows drop their `ci` reference until a new candidate lands.
    Hidden,
    /// A freshly rasterized sprite; the writer hashes it and emits a
    /// `cursor_image` row if it has not been seen before.
    Candidate(RawSprite),
}

// ---------------------------------------------------------------------------
// Hashing
// ---------------------------------------------------------------------------

/// FNV-1a 64 offset basis — the same constants as the macOS cursor
/// classifier (`macos.rs`). Note its prime carries one hex zero more than
/// textbook FNV-1a; the value never leaves the process (dedupe keys only), so
/// internal consistency is the whole contract.
pub const FNV_OFFSET_BASIS: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x1000_0000_01b3;

/// FNV-1a 64 over `bytes`, continuing from `state` (start with
/// [`FNV_OFFSET_BASIS`]) so multi-plane hashes chain without concatenating.
pub fn fnv1a64(state: u64, bytes: &[u8]) -> u64 {
    let mut h = state;
    for &b in bytes {
        h ^= b as u64;
        h = h.wrapping_mul(FNV_PRIME);
    }
    h
}

// ---------------------------------------------------------------------------
// Plane decomposition (pure — the Windows rasterizer feeds GetDIBits output
// straight in here)
// ---------------------------------------------------------------------------

// Only the Windows rasterizer calls into this section (the tests below cover
// it on every host), so off Windows each item here is dead but deliberately
// kept — hence the `cfg_attr`s.

/// Scanline stride in bytes of a 1bpp DIB: rows are DWORD-aligned.
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
pub fn mono_stride(w: u32) -> usize {
    (w as usize).div_ceil(32) * 4
}

/// Splits a mono cursor's double-height mask bitmap (AND plane stacked on top
/// of the XOR plane) into its two planes. `h` is the cursor height, i.e. half
/// the bitmap height; `None` if the buffer is too short.
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
pub fn split_mono_planes(data: &[u8], h: u32, stride: usize) -> Option<(&[u8], &[u8])> {
    let half = h as usize * stride;
    if data.len() < half * 2 {
        return None;
    }
    Some((&data[..half], &data[half..half * 2]))
}

/// Decomposes a monochrome cursor's AND/XOR planes (1bpp, MSB-first,
/// `stride` bytes per row) into the sprite's color plane and mask plane, both
/// `w * h * 4` BGRA. GDI draws such cursors as `dst = (dst AND and) XOR xor`:
///
/// | AND | XOR | output                                                  |
/// |-----|-----|---------------------------------------------------------|
/// | 0   | 0   | bmp opaque black, mask transparent                      |
/// | 0   | 1   | bmp opaque white, mask transparent                      |
/// | 1   | 0   | bmp transparent, mask opaque black (no-op, fidelity)    |
/// | 1   | 1   | bmp transparent, mask opaque white (screen invert)      |
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
pub fn decompose_mono(and: &[u8], xor: &[u8], w: u32, h: u32, stride: usize) -> (Vec<u8>, Vec<u8>) {
    let px = (w as usize) * (h as usize) * 4;
    let mut bmp = vec![0u8; px];
    let mut mask = vec![0u8; px];
    for y in 0..h as usize {
        for x in 0..w as usize {
            let byte = y * stride + x / 8;
            let bit = 0x80u8 >> (x % 8);
            let a = and[byte] & bit != 0;
            let m = xor[byte] & bit != 0;
            let i = (y * w as usize + x) * 4;
            let (dst, value) = match (a, m) {
                (false, false) => (&mut bmp, [0, 0, 0, 255]),
                (false, true) => (&mut bmp, [255, 255, 255, 255]),
                (true, false) => (&mut mask, [0, 0, 0, 255]),
                (true, true) => (&mut mask, [255, 255, 255, 255]),
            };
            dst[i..i + 4].copy_from_slice(&value);
        }
    }
    (bmp, mask)
}

/// Decomposes a color cursor whose 32bpp plane carries no alpha, using the
/// single-height AND mask (1bpp, `stride` bytes per row) to decide pixel
/// ownership. `color` is `w * h * 4` top-down BGRA.
///
/// AND=0 → the color plane owns the pixel (opaque). AND=1 → an XOR pixel:
/// black is a no-op (mask black), white — and near-white, all channels ≥
/// 0xC0 — is a screen invert (mask white), anything else is approximated as
/// an opaque `bmp` pixel (see the module docs). The mask is `None` when no
/// pixel needed it.
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
pub fn decompose_masked_color(
    color: &[u8],
    and: &[u8],
    w: u32,
    h: u32,
    stride: usize,
) -> (Vec<u8>, Option<Vec<u8>>) {
    let px = (w as usize) * (h as usize) * 4;
    let mut bmp = vec![0u8; px];
    let mut mask = vec![0u8; px];
    let mut any_mask = false;
    for y in 0..h as usize {
        for x in 0..w as usize {
            let a = and[y * stride + x / 8] & (0x80u8 >> (x % 8)) != 0;
            let i = (y * w as usize + x) * 4;
            let (b, g, r) = (color[i], color[i + 1], color[i + 2]);
            if !a {
                bmp[i..i + 4].copy_from_slice(&[b, g, r, 255]);
            } else if b == 0 && g == 0 && r == 0 {
                mask[i..i + 4].copy_from_slice(&[0, 0, 0, 255]);
                any_mask = true;
            } else if b >= 0xC0 && g >= 0xC0 && r >= 0xC0 {
                mask[i..i + 4].copy_from_slice(&[255, 255, 255, 255]);
                any_mask = true;
            } else {
                bmp[i..i + 4].copy_from_slice(&[b, g, r, 255]);
            }
        }
    }
    (bmp, any_mask.then_some(mask))
}

/// Whether a BGRA buffer carries any alpha at all — distinguishes a modern
/// alpha cursor (whose AND mask is vestigial) from a legacy masked one whose
/// 32bpp plane leaves the alpha channel at zero everywhere.
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
pub fn has_alpha(bgra: &[u8]) -> bool {
    bgra.chunks_exact(4).any(|p| p[3] != 0)
}

// ---------------------------------------------------------------------------
// Wire row encoding (writer thread)
// ---------------------------------------------------------------------------

/// The `cursor_image` sidecar row (DESIGN §1 — field names and order are the
/// wire contract; they mirror the Windows hbmColor/hbmMask structure).
#[derive(Serialize)]
struct CursorImageRow {
    #[serde(rename = "type")]
    ty: &'static str,
    id: u32,
    kind: &'static str,
    w: u32,
    h: u32,
    hotx: i32,
    hoty: i32,
    /// Base64 PNG, straight-alpha RGBA — the color/opaque pixels.
    bmp: String,
    /// Base64 PNG XOR plane; omitted for plain alpha cursors.
    #[serde(skip_serializing_if = "Option::is_none")]
    mask: Option<String>,
}

/// PNG-encodes a straight-alpha BGRA buffer. `None` on any encode failure
/// (sprite capture degrades, it never aborts a recording).
fn bgra_to_png(bgra: &[u8], w: u32, h: u32) -> Option<Vec<u8>> {
    if bgra.len() != (w as usize) * (h as usize) * 4 || w == 0 || h == 0 {
        return None;
    }
    let mut rgba = bgra.to_vec();
    for p in rgba.chunks_exact_mut(4) {
        p.swap(0, 2);
    }
    let mut out = Vec::new();
    let mut enc = png::Encoder::new(&mut out, w, h);
    enc.set_color(png::ColorType::Rgba);
    enc.set_depth(png::BitDepth::Eight);
    let mut writer = enc.write_header().ok()?;
    writer.write_image_data(&rgba).ok()?;
    writer.finish().ok()?;
    Some(out)
}

/// Serializes a deduped sprite as its `cursor_image` JSON line (no trailing
/// newline). Runs on the writer thread only — PNG + base64 are the expensive
/// part of the pipeline and must stay off the graphics thread. `None` if
/// encoding fails.
pub fn encode_row(id: u32, sprite: &RawSprite) -> Option<String> {
    use base64::engine::general_purpose::STANDARD;
    use base64::Engine;

    let bmp_png = match &sprite.bmp {
        SpritePixels::Bgra(bgra) => bgra_to_png(bgra, sprite.w, sprite.h)?,
        SpritePixels::Png(png) => png.clone(),
    };
    let mask = match &sprite.mask {
        Some(bgra) => Some(STANDARD.encode(bgra_to_png(bgra, sprite.w, sprite.h)?)),
        None => None,
    };
    let row = CursorImageRow {
        ty: "cursor_image",
        id,
        kind: sprite.kind,
        w: sprite.w,
        h: sprite.h,
        hotx: sprite.hotx,
        hoty: sprite.hoty,
        bmp: STANDARD.encode(bmp_png),
        mask,
    };
    serde_json::to_string(&row).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    const TRANSPARENT: [u8; 4] = [0, 0, 0, 0];
    const BLACK: [u8; 4] = [0, 0, 0, 255];
    const WHITE: [u8; 4] = [255, 255, 255, 255];

    fn px(buf: &[u8], w: u32, x: usize, y: usize) -> [u8; 4] {
        let i = (y * w as usize + x) * 4;
        buf[i..i + 4].try_into().unwrap()
    }

    // -- decompose_mono ------------------------------------------------------

    #[test]
    fn mono_truth_table_covers_all_four_cells() {
        // One 4-pixel row, MSB-first: pixels 0..3 = the four (AND, XOR)
        // combinations 00, 01, 10, 11.
        let and = [0b0011_0000u8, 0, 0, 0];
        let xor = [0b0101_0000u8, 0, 0, 0];
        let (bmp, mask) = decompose_mono(&and, &xor, 4, 1, 4);

        // AND=0 XOR=0: opaque black, no mask.
        assert_eq!(px(&bmp, 4, 0, 0), BLACK);
        assert_eq!(px(&mask, 4, 0, 0), TRANSPARENT);
        // AND=0 XOR=1: opaque white, no mask.
        assert_eq!(px(&bmp, 4, 1, 0), WHITE);
        assert_eq!(px(&mask, 4, 1, 0), TRANSPARENT);
        // AND=1 XOR=0: transparent, mask black (the preserved no-op).
        assert_eq!(px(&bmp, 4, 2, 0), TRANSPARENT);
        assert_eq!(px(&mask, 4, 2, 0), BLACK);
        // AND=1 XOR=1: transparent, mask white (screen invert — the I-beam).
        assert_eq!(px(&bmp, 4, 3, 0), TRANSPARENT);
        assert_eq!(px(&mask, 4, 3, 0), WHITE);
    }

    #[test]
    fn mono_respects_the_dword_aligned_stride() {
        // 33 px wide: 1bpp rows need 5 bytes but the DIB stride is 8. Pixel
        // (32, 1) lives at row_offset + byte 4, bit 0x80 — misreading the
        // stride as ceil(33/8)=5 would land it in the wrong row.
        let w = 33u32;
        let stride = mono_stride(w);
        assert_eq!(stride, 8);
        let h = 2u32;
        let mut and = vec![0u8; stride * h as usize];
        let mut xor = vec![0u8; stride * h as usize];
        // Row 0: all AND=1 (fully transparent). Row 1: pixel 32 inverts.
        for b in and.iter_mut().take(stride) {
            *b = 0xFF;
        }
        and[stride + 4] = 0x80;
        xor[stride + 4] = 0x80;
        let (bmp, mask) = decompose_mono(&and, &xor, w, h, stride);

        assert_eq!(px(&mask, w, 0, 0), BLACK); // row 0 no-op mask
        assert_eq!(px(&mask, w, 32, 0), BLACK);
        assert_eq!(px(&mask, w, 32, 1), WHITE); // the invert pixel
        assert_eq!(px(&bmp, w, 32, 1), TRANSPARENT);
        // Row 1's other pixels: AND=0 XOR=0 -> opaque black.
        assert_eq!(px(&bmp, w, 0, 1), BLACK);
        assert_eq!(px(&mask, w, 0, 1), TRANSPARENT);
    }

    #[test]
    fn double_height_mask_splits_into_and_over_xor() {
        // A 8x2 mono cursor: the GetDIBits buffer is 8x4 (AND on top).
        let stride = mono_stride(8);
        let buf = [
            0xFFu8, 0, 0, 0, // AND row 0: all masked
            0x00, 0, 0, 0, // AND row 1: all opaque
            0xFF, 0, 0, 0, // XOR row 0: invert
            0xFF, 0, 0, 0, // XOR row 1: white
        ];
        let (and, xor) = split_mono_planes(&buf, 2, stride).unwrap();
        let (bmp, mask) = decompose_mono(and, xor, 8, 2, stride);
        assert_eq!(px(&mask, 8, 3, 0), WHITE); // AND=1 XOR=1
        assert_eq!(px(&bmp, 8, 3, 0), TRANSPARENT);
        assert_eq!(px(&bmp, 8, 3, 1), WHITE); // AND=0 XOR=1
        assert_eq!(px(&mask, 8, 3, 1), TRANSPARENT);

        // A short buffer must refuse rather than mis-split.
        assert!(split_mono_planes(&buf[..12], 2, stride).is_none());
    }

    // -- decompose_masked_color ----------------------------------------------

    #[test]
    fn masked_color_covers_opaque_noop_invert_and_approximation() {
        // 4x1: [opaque color, XOR black, XOR white, XOR colored].
        let color = [
            10u8, 20, 30, 0, // AND=0: owned by the color plane
            0, 0, 0, 0, // AND=1, black: no-op
            255, 255, 255, 0, // AND=1, white: exact invert
            0, 0, 200, 0, // AND=1, red: approximated as opaque
        ];
        let and = [0b0111_0000u8, 0, 0, 0];
        let (bmp, mask) = decompose_masked_color(&color, &and, 4, 1, 4);
        let mask = mask.expect("XOR pixels present");

        assert_eq!(px(&bmp, 4, 0, 0), [10, 20, 30, 255]);
        assert_eq!(px(&mask, 4, 0, 0), TRANSPARENT);
        assert_eq!(px(&mask, 4, 1, 0), BLACK);
        assert_eq!(px(&bmp, 4, 1, 0), TRANSPARENT);
        assert_eq!(px(&mask, 4, 2, 0), WHITE);
        assert_eq!(px(&bmp, 4, 2, 0), TRANSPARENT);
        // The colored-XOR approximation: opaque bmp pixel, no mask.
        assert_eq!(px(&bmp, 4, 3, 0), [0, 0, 200, 255]);
        assert_eq!(px(&mask, 4, 3, 0), TRANSPARENT);
    }

    #[test]
    fn masked_color_treats_near_white_as_invert() {
        // All channels >= 0xC0 counts as white (screen invert).
        let color = [0xC0u8, 0xD0, 0xFF, 0];
        let and = [0x80u8, 0, 0, 0];
        let (bmp, mask) = decompose_masked_color(&color, &and, 1, 1, 4);
        assert_eq!(px(&mask.unwrap(), 1, 0, 0), WHITE);
        assert_eq!(px(&bmp, 1, 0, 0), TRANSPARENT);
    }

    #[test]
    fn masked_color_without_xor_pixels_has_no_mask() {
        // Every pixel AND=0 -> a plain opaque cursor, mask omitted.
        let color = [1u8, 2, 3, 0, 4, 5, 6, 0];
        let and = [0u8, 0, 0, 0];
        let (bmp, mask) = decompose_masked_color(&color, &and, 2, 1, 4);
        assert!(mask.is_none());
        assert_eq!(px(&bmp, 2, 1, 0), [4, 5, 6, 255]);
    }

    // -- has_alpha / fnv1a64 -------------------------------------------------

    #[test]
    fn has_alpha_detects_any_nonzero_alpha() {
        assert!(!has_alpha(&[10, 20, 30, 0, 40, 50, 60, 0]));
        assert!(has_alpha(&[10, 20, 30, 0, 40, 50, 60, 1]));
        assert!(!has_alpha(&[]));
    }

    #[test]
    fn fnv1a64_is_consistent_and_chains() {
        // Empty input leaves the state untouched (true of any FNV variant).
        assert_eq!(fnv1a64(FNV_OFFSET_BASIS, b""), FNV_OFFSET_BASIS);
        // One xor-multiply round, spelled out — guards both constants. (The
        // prime is macos.rs's, not textbook FNV; see the constant's docs.)
        assert_eq!(
            fnv1a64(FNV_OFFSET_BASIS, b"a"),
            (FNV_OFFSET_BASIS ^ b'a' as u64).wrapping_mul(0x1000_0000_01b3)
        );
        // Chaining two slices equals hashing the concatenation.
        let ab = fnv1a64(fnv1a64(FNV_OFFSET_BASIS, b"foo"), b"bar");
        assert_eq!(ab, fnv1a64(FNV_OFFSET_BASIS, b"foobar"));
        assert_ne!(ab, fnv1a64(FNV_OFFSET_BASIS, b"barfoo"));
    }

    #[test]
    fn content_hash_distinguishes_mask_presence() {
        let base = RawSprite {
            kind: "arrow",
            w: 1,
            h: 1,
            hotx: 0,
            hoty: 0,
            bmp: SpritePixels::Bgra(vec![1, 2, 3, 255]),
            mask: None,
        };
        let masked = RawSprite {
            mask: Some(vec![255, 255, 255, 255]),
            bmp: SpritePixels::Bgra(vec![1, 2, 3, 255]),
            ..base
        };
        assert_ne!(base.content_hash(), masked.content_hash());
        assert_eq!(base.byte_len(), 4);
        assert_eq!(masked.byte_len(), 8);
    }

    // -- encode_row ----------------------------------------------------------

    #[test]
    fn encode_row_produces_the_wire_row() {
        use base64::engine::general_purpose::STANDARD;
        use base64::Engine;

        let sprite = RawSprite {
            kind: "ibeam",
            w: 1,
            h: 1,
            hotx: 0,
            hoty: 1,
            bmp: SpritePixels::Bgra(vec![0, 0, 255, 255]), // opaque red
            mask: Some(vec![255, 255, 255, 255]),
        };
        let line = encode_row(3, &sprite).unwrap();
        let v: serde_json::Value = serde_json::from_str(&line).unwrap();
        assert_eq!(v["type"], "cursor_image");
        assert_eq!(v["id"], 3);
        assert_eq!(v["kind"], "ibeam");
        assert_eq!(v["w"], 1);
        assert_eq!(v["h"], 1);
        assert_eq!(v["hotx"], 0);
        assert_eq!(v["hoty"], 1);
        // Both planes decode to real PNGs (magic bytes).
        for field in ["bmp", "mask"] {
            let bytes = STANDARD.decode(v[field].as_str().unwrap()).unwrap();
            assert_eq!(&bytes[..8], b"\x89PNG\r\n\x1a\n");
        }
        // Field order is part of the contract.
        assert!(line.starts_with(
            r#"{"type":"cursor_image","id":3,"kind":"ibeam","w":1,"h":1,"hotx":0,"hoty":1,"bmp":""#
        ));
    }

    #[test]
    fn encode_row_omits_an_absent_mask_and_passes_png_through() {
        let png = bgra_to_png(&[0, 0, 0, 255], 1, 1).unwrap();
        let sprite = RawSprite {
            kind: "custom",
            w: 1,
            h: 1,
            hotx: 0,
            hoty: 0,
            bmp: SpritePixels::Png(png.clone()),
            mask: None,
        };
        let line = encode_row(1, &sprite).unwrap();
        assert!(!line.contains("\"mask\""));
        // The pre-encoded plane is embedded verbatim, not re-encoded.
        use base64::engine::general_purpose::STANDARD;
        use base64::Engine;
        let v: serde_json::Value = serde_json::from_str(&line).unwrap();
        assert_eq!(STANDARD.decode(v["bmp"].as_str().unwrap()).unwrap(), png);
    }

    #[test]
    fn encode_row_rejects_a_malformed_plane() {
        // Length/dimension mismatch must degrade to None, never panic.
        let sprite = RawSprite {
            kind: "arrow",
            w: 2,
            h: 2,
            hotx: 0,
            hoty: 0,
            bmp: SpritePixels::Bgra(vec![0; 4]), // 2x2 needs 16 bytes
            mask: None,
        };
        assert!(encode_row(1, &sprite).is_none());
    }
}
