//! Desktop capture on X11: one `GetImage` of the root window.
//!
//! The root window IS the virtual desktop on X11 — every monitor is a
//! RandR rectangle inside it, in physical pixels — so the whole capture is
//! a single request for the monitors' bounding box. A compositing window
//! manager keeps the root window's contents current (it is what the
//! compositor paints into), and without one the server draws it directly;
//! either way `GetImage` returns what is on screen.
//!
//! The reply is normalised here to the packed top-down BGRA every other
//! platform hands the GPU, with an opaque alpha byte: a depth-24 image's
//! fourth byte is padding the server leaves undefined.

use anyhow::{bail, Context, Result};
use x11rb::connection::Connection;
use x11rb::protocol::xproto::{ConnectionExt as _, ImageFormat, ImageOrder, Visualtype};

use clowd_rust_core::geometry::{RectExt, ScreenRect};

/// Raw bitmap product of `capture_desktop`. Public to its own module only;
/// the public `CapturedDesktop` (defined in `system/mod.rs`) wraps this and
/// adds per-monitor metadata.
pub struct DesktopBitmap {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
    pub bounds: ScreenRect,
}

pub fn capture_desktop(vd: &ScreenRect) -> Result<DesktopBitmap> {
    let (conn, screen_num) = x11rb::connect(None).context("cannot connect to the X server")?;
    let screen = &conn.setup().roots[screen_num];

    // Clamp to the root window: a GetImage that reaches outside it is a
    // BadMatch, and RandR can briefly report a monitor past the screen's
    // edge while a hotplug settles.
    let root_rect = ScreenRect::from_xy_size(0, 0, i32::from(screen.width_in_pixels), i32::from(screen.height_in_pixels));
    let Some(area) = vd.intersection(&root_rect) else {
        bail!("virtual desktop {vd:?} lies outside the root window {root_rect:?}");
    };
    let width = area.width();
    let height = area.height();
    if width <= 0 || height <= 0 {
        bail!("virtual desktop {vd:?} is empty");
    }

    let reply = conn
        .get_image(
            ImageFormat::Z_PIXMAP,
            screen.root,
            area.min_x() as i16,
            area.min_y() as i16,
            width as u16,
            height as u16,
            !0,
        )
        .context("GetImage")?
        .reply()
        .context("GetImage")?;

    let visual = find_visual(screen, reply.visual).with_context(|| format!("GetImage returned an unknown visual {:#x}", reply.visual))?;
    let bits_per_pixel = conn
        .setup()
        .pixmap_formats
        .iter()
        .find(|f| f.depth == reply.depth)
        .map(|f| f.bits_per_pixel)
        .with_context(|| format!("no pixmap format for depth {}", reply.depth))?;
    if bits_per_pixel != 32 {
        bail!(
            "root window is {} bpp (depth {}); only 32 bpp is supported",
            bits_per_pixel,
            reply.depth
        );
    }
    let byte_count = (width as usize)
        .checked_mul(height as usize)
        .and_then(|n| n.checked_mul(4))
        .ok_or_else(|| anyhow!("capture dimensions overflow"))?;
    if reply.data.len() < byte_count {
        bail!("GetImage returned {} bytes for a {}x{} image", reply.data.len(), width, height);
    }

    let bgra = to_bgra(reply.data, conn.setup().image_byte_order, &visual);
    Ok(DesktopBitmap {
        bgra,
        width: width as u32,
        height: height as u32,
        bounds: area,
    })
}

/// The visual `id` belongs to, from the screen's depth table.
fn find_visual(screen: &x11rb::protocol::xproto::Screen, id: u32) -> Option<Visualtype> {
    screen
        .allowed_depths
        .iter()
        .flat_map(|d| d.visuals.iter())
        .find(|v| v.visual_id == id)
        .cloned()
}

/// Rewrite 32 bpp ZPixmap rows into packed BGRA with opaque alpha.
///
/// The overwhelmingly common case — little-endian server, the TrueColor
/// visual with red at 0xff0000 — already IS BGRA in memory, so it costs one
/// pass setting the alpha bytes. Anything else (a big-endian server, a
/// visual with the channels in another order, a depth-30 visual with ten
/// bits per channel) goes through the masks.
fn to_bgra(mut data: Vec<u8>, byte_order: ImageOrder, visual: &Visualtype) -> Vec<u8> {
    let standard_masks = visual.red_mask == 0x00ff_0000 && visual.green_mask == 0x0000_ff00 && visual.blue_mask == 0x0000_00ff;
    if byte_order == ImageOrder::LSB_FIRST && standard_masks {
        for px in data.chunks_exact_mut(4) {
            px[3] = 0xff;
        }
        return data;
    }
    let (r, g, b) = (
        Channel::new(visual.red_mask),
        Channel::new(visual.green_mask),
        Channel::new(visual.blue_mask),
    );
    for px in data.chunks_exact_mut(4) {
        let raw = [px[0], px[1], px[2], px[3]];
        let value = if byte_order == ImageOrder::LSB_FIRST {
            u32::from_le_bytes(raw)
        } else {
            u32::from_be_bytes(raw)
        };
        px[0] = b.extract(value);
        px[1] = g.extract(value);
        px[2] = r.extract(value);
        px[3] = 0xff;
    }
    data
}

/// One colour channel of a TrueColor visual, as its mask describes it,
/// reduced to the 8 bits the GPU texture holds.
#[derive(Clone, Copy)]
struct Channel {
    mask: u32,
    shift: u32,
    width: u32,
}

impl Channel {
    fn new(mask: u32) -> Self {
        Self {
            mask,
            shift: mask.trailing_zeros(),
            width: mask.count_ones(),
        }
    }

    /// The channel's top 8 bits: a 10-bit (depth 30) value keeps its high
    /// byte rather than its low one, and a narrower channel is scaled up
    /// to full range. An empty mask (a visual with no such channel) is 0.
    fn extract(self, value: u32) -> u8 {
        let bits = (value & self.mask) >> self.shift;
        match self.width {
            0 => 0,
            w if w >= 8 => (bits >> (w - 8)) as u8,
            w => ((bits * 255) / ((1 << w) - 1)) as u8,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn visual(red_mask: u32, green_mask: u32, blue_mask: u32) -> Visualtype {
        Visualtype {
            visual_id: 0x21,
            class: x11rb::protocol::xproto::VisualClass::TRUE_COLOR,
            bits_per_rgb_value: 8,
            colormap_entries: 256,
            red_mask,
            green_mask,
            blue_mask,
        }
    }

    /// The fast path: bytes already in BGRA order, only the padding byte
    /// changes.
    #[test]
    fn little_endian_standard_visual_only_sets_alpha() {
        let data = vec![0x11, 0x22, 0x33, 0x00, 0xaa, 0xbb, 0xcc, 0x7f];
        let out = to_bgra(data, ImageOrder::LSB_FIRST, &visual(0xff0000, 0xff00, 0xff));
        assert_eq!(out, [0x11, 0x22, 0x33, 0xff, 0xaa, 0xbb, 0xcc, 0xff]);
    }

    /// A big-endian server stores the same pixel value as X,R,G,B.
    #[test]
    fn big_endian_is_swizzled_to_bgra() {
        let data = vec![0x00, 0x33, 0x22, 0x11];
        let out = to_bgra(data, ImageOrder::MSB_FIRST, &visual(0xff0000, 0xff00, 0xff));
        assert_eq!(out, [0x11, 0x22, 0x33, 0xff]);
    }

    /// A visual with red in the low byte lands in the same BGRA layout.
    #[test]
    fn unusual_masks_go_through_the_shifts() {
        let data = vec![0x33, 0x22, 0x11, 0x00];
        let out = to_bgra(data, ImageOrder::LSB_FIRST, &visual(0xff, 0xff00, 0xff0000));
        assert_eq!(out, [0x11, 0x22, 0x33, 0xff]);
    }

    /// A depth-30 root (ten bits per channel, `DefaultDepth 30`) keeps each
    /// channel's high byte: mid grey must stay mid grey, not become black.
    #[test]
    fn ten_bit_channels_keep_their_high_bits() {
        let v = visual(0x3ff0_0000, 0x000f_fc00, 0x0000_03ff);
        let white = 0x3fff_ffffu32.to_le_bytes();
        let grey = ((0x200 << 20) | (0x200 << 10) | 0x200u32).to_le_bytes();
        let red = (0x3ffu32 << 20).to_le_bytes();
        let mut data = Vec::new();
        data.extend_from_slice(&white);
        data.extend_from_slice(&grey);
        data.extend_from_slice(&red);
        let out = to_bgra(data, ImageOrder::LSB_FIRST, &v);
        assert_eq!(out, [0xff, 0xff, 0xff, 0xff, 0x80, 0x80, 0x80, 0xff, 0x00, 0x00, 0xff, 0xff]);
    }

    /// A channel narrower than a byte is scaled to full range rather than
    /// left in its low bits.
    #[test]
    fn narrow_channels_scale_up() {
        let c = Channel::new(0b0111_0000);
        assert_eq!(c.extract(0b0111_0000), 0xff);
        assert_eq!(c.extract(0b0000_0000), 0x00);
        assert_eq!(Channel::new(0).extract(0xffff_ffff), 0);
    }
}
