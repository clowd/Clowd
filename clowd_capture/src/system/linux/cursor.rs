//! Cursor capture on X11, through XFixes.
//!
//! `XFixesGetCursorImage` returns the cursor the server is showing right
//! now — image, hotspot and position in root-window pixels — as 32-bit
//! premultiplied ARGB. That is the `AlphaBlended` form the shader wants,
//! so it is repacked to BGRA bytes and nothing else. X11 has no AND/XOR
//! cursors: a legacy bitmap cursor arrives already rendered to ARGB.

use x11rb::protocol::xfixes::ConnectionExt as _;

use crate::system::{CapturedCursor, CursorImage};
use clowd_rust_core::geometry::ScreenPoint;

/// The XFixes version the cursor image request first shipped in (1.0);
/// the server refuses every XFixes request until a client has negotiated.
const XFIXES_VERSION: (u32, u32) = (1, 0);

pub fn capture_cursor() -> Option<CapturedCursor> {
    let (conn, _) = match x11rb::connect(None) {
        Ok(c) => c,
        Err(err) => {
            warn!("cursor capture: cannot connect to the X server: {err}");
            return None;
        }
    };
    if let Err(err) = conn
        .xfixes_query_version(XFIXES_VERSION.0, XFIXES_VERSION.1)
        .ok()?
        .reply()
    {
        warn!("cursor capture: XFixes is unavailable: {err}");
        return None;
    }
    let reply = match conn.xfixes_get_cursor_image().ok()?.reply() {
        Ok(reply) => reply,
        Err(err) => {
            warn!("XFixesGetCursorImage failed: {err}");
            return None;
        }
    };

    let position = ScreenPoint::new(i32::from(reply.x), i32::from(reply.y));
    let width = u32::from(reply.width);
    let height = u32::from(reply.height);
    // A hidden pointer shows up as an empty (or fully transparent) image;
    // report it invisible rather than compositing a 0x0 sprite.
    let visible = width > 0
        && height > 0
        && reply
            .cursor_image
            .iter()
            .any(|argb| argb >> 24 != 0);
    if !visible {
        return Some(CapturedCursor {
            position,
            hotspot_x: 0,
            hotspot_y: 0,
            visible: false,
            image: CursorImage::AlphaBlended {
                bgra: Vec::new(),
                width: 0,
                height: 0,
            },
        });
    }

    Some(CapturedCursor {
        position,
        hotspot_x: i32::from(reply.xhot),
        hotspot_y: i32::from(reply.yhot),
        visible: true,
        image: CursorImage::AlphaBlended {
            bgra: argb_to_bgra(&reply.cursor_image, width, height),
            width,
            height,
        },
    })
}

/// Premultiplied ARGB words to packed BGRA bytes: the little-endian byte
/// order of each word is exactly B, G, R, A.
fn argb_to_bgra(pixels: &[u32], width: u32, height: u32) -> Vec<u8> {
    let count = (width as usize) * (height as usize);
    let mut bgra = Vec::with_capacity(count * 4);
    for argb in pixels.iter().take(count) {
        bgra.extend_from_slice(&argb.to_le_bytes());
    }
    // A short reply (never seen, but the protocol does not forbid it) is
    // padded transparent rather than left as a wrong-sized buffer.
    bgra.resize(count * 4, 0);
    bgra
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn argb_words_become_bgra_bytes() {
        let out = argb_to_bgra(&[0x80ff_0000, 0x0000_ff00], 2, 1);
        assert_eq!(out, [0x00, 0x00, 0xff, 0x80, 0x00, 0xff, 0x00, 0x00]);
    }

    #[test]
    fn short_replies_are_padded_and_long_ones_truncated() {
        assert_eq!(argb_to_bgra(&[0xffff_ffff], 2, 1), [0xff, 0xff, 0xff, 0xff, 0, 0, 0, 0]);
        assert_eq!(argb_to_bgra(&[0xffff_ffff, 0xffff_ffff], 1, 1), [0xff, 0xff, 0xff, 0xff]);
    }
}
