//! The `cursor_image` sidecar row (DESIGN §1, jsonl v2): what the writer
//! thread makes of a deduped [`RawSprite`] that the platform layer
//! (`prisc_obs_platform::cursor_sprite`) captured.

use prisc_obs_platform::cursor_sprite::{RawSprite, SpritePixels};
use serde::Serialize;

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
    use image::ImageEncoder;

    let mut rgba = bgra.to_vec();
    for p in rgba.chunks_exact_mut(4) {
        p.swap(0, 2);
    }
    let mut out = Vec::new();
    image::codecs::png::PngEncoder::new(&mut out)
        .write_image(&rgba, w, h, image::ExtendedColorType::Rgba8)
        .ok()?;
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
    use prisc_obs_platform::cursor_sprite::{RawSprite, SpritePixels};

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
