use std::sync::Arc;

use crate::gxi::{self, TexFormat, TextureDesc};
use crate::system::{CapturedCursor, CapturedDesktop, CursorImage};
use clowd_rust_core::geometry::{RectExt, ScreenPoint};

/// Uniforms for the desktop background pass (desktop.wgsl): snapshot UV
/// mapping, opening fade, selection interior + OCR treatment, and the
/// frozen cursor composite. Everything overlay-shaped that used to live
/// here (crosshair, selection border, handles) moved to
/// [`crate::gpu::overlay::OverlayUniforms`] with its own passes.
#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct WindowUniforms {
    pub uv_offset_scale: [f32; 4],
    /// x = grayscale fade factor in [0, 1]; y/z/w spare.
    pub params: [f32; 4],
    /// Selection in window-local physical px (l, t, r, b); empty
    /// (right<=left) when there is no selection. The interior shows the
    /// desktop untouched (or OCR-treated), everything else fades.
    pub selection_rect: [f32; 4],
    /// Cursor rect in window-local physical pixels: [left, top, right, bottom].
    /// Empty (right<=left) when cursor is hidden or off this monitor.
    pub cursor_rect: [f32; 4],
    /// Cursor compositing type: 0=hidden, 1=alpha_blended, 2=masked.
    pub cursor_params: [f32; 4],
    /// x = source-region dim amount 0..1 (ramps with the lift animation),
    /// z = OCR selection desaturation 0..1; y/w spare.
    pub ocr_params: [f32; 4],
}

pub const WINDOW_UNIFORMS_SIZE: u64 = std::mem::size_of::<WindowUniforms>() as u64;

pub struct CursorTextures {
    pub color: gxi::Texture,
    pub mask: gxi::Texture,
    pub cursor_type: u32,
    pub position: ScreenPoint,
    pub hotspot_x: i32,
    pub hotspot_y: i32,
    pub width: u32,
    pub height: u32,
    pub visible: bool,
}

pub struct DesktopSnapshot {
    pub texture: gxi::Texture,
    pub sampler: gxi::Sampler,
    pub vdesktop_origin: [f32; 2],
    pub vdesktop_size: [f32; 2],
    pub cursor: Option<CursorTextures>,
}

pub fn upload_snapshot(
    device: &gxi::Device,
    queue: &gxi::Queue,
    captured: &CapturedDesktop,
    sampler: &gxi::Sampler,
) -> Option<Arc<DesktopSnapshot>> {
    let width = captured.width;
    let height = captured.height;
    let max = device.max_texture_dimension_2d();
    if width > max || height > max {
        error!(
            "virtual desktop {}x{} exceeds max texture dimension {}; skipping snapshot",
            width, height, max
        );
        return None;
    }
    if width == 0 || height == 0 {
        error!("virtual desktop has zero dimension; skipping snapshot");
        return None;
    }

    // No forced empty submit after this upload on purpose. Under the old
    // wgpu backend one existed to force the pending ~33 MB staging copy
    // into a real submission right away, purely so wgpu-core's
    // "wait for all work" step inside `configure_surface` had something
    // to wait on; it was dropped once measurement showed frame 0's own
    // submit flushes the write regardless. Neither current backend
    // stages uploads behind a submission at all (d3d11 UpdateSubresource
    // and Metal replaceRegion write directly), so there is nothing to
    // force anymore either.
    //
    // TRADEOFF, and it is a real one: the upload no longer starts early
    // enough to overlap window creation, so the copy is serialized into
    // frame 0 instead of hidden behind it. Whether that is a net win
    // depends on how long window creation actually takes on the machine.
    // A/B it against the `upload` -> `first_render` per-worker deltas in
    // the start-up report before assuming either way.
    let texture = device.create_texture_with_data(
        queue,
        &TextureDesc {
            label: "desktop snapshot",
            width,
            height,
            format: TexFormat::Bgra8Unorm,
        },
        &captured.bgra,
    );

    let cursor = captured.cursor.as_ref().and_then(|c| {
        if !c.visible {
            return None;
        }
        upload_cursor_textures(device, queue, c)
    });

    let bounds_f = captured.bounds.to_f32();
    Some(Arc::new(DesktopSnapshot {
        texture,
        sampler: sampler.clone(),
        vdesktop_origin: [bounds_f.left(), bounds_f.top()],
        vdesktop_size: [bounds_f.width(), bounds_f.height()],
        cursor,
    }))
}

fn create_small_texture(device: &gxi::Device, queue: &gxi::Queue, bgra: &[u8], width: u32, height: u32, label: &str) -> gxi::Texture {
    device.create_texture_with_data(
        queue,
        &TextureDesc {
            label,
            width,
            height,
            format: TexFormat::Bgra8Unorm,
        },
        bgra,
    )
}

pub fn create_placeholder_cursor_texture(device: &gxi::Device, queue: &gxi::Queue) -> gxi::Texture {
    create_small_texture(device, queue, &[0, 0, 0, 0], 1, 1, "cursor placeholder")
}

fn upload_cursor_textures(device: &gxi::Device, queue: &gxi::Queue, cursor: &CapturedCursor) -> Option<CursorTextures> {
    match &cursor.image {
        CursorImage::AlphaBlended {
            bgra,
            width,
            height,
        } => {
            if *width == 0 || *height == 0 || bgra.is_empty() {
                return None;
            }
            let color = create_small_texture(device, queue, bgra, *width, *height, "cursor color");
            let mask = create_small_texture(device, queue, &[0, 0, 0, 0], 1, 1, "cursor mask placeholder");
            Some(CursorTextures {
                color,
                mask,
                cursor_type: 1,
                position: cursor.position,
                hotspot_x: cursor.hotspot_x,
                hotspot_y: cursor.hotspot_y,
                width: *width,
                height: *height,
                visible: true,
            })
        }
        CursorImage::Masked {
            and_mask_bgra,
            xor_color_bgra,
            width,
            height,
        } => {
            if *width == 0 || *height == 0 || and_mask_bgra.is_empty() || xor_color_bgra.is_empty() {
                return None;
            }
            let color = create_small_texture(device, queue, xor_color_bgra, *width, *height, "cursor xor color");
            let mask = create_small_texture(device, queue, and_mask_bgra, *width, *height, "cursor and mask");
            Some(CursorTextures {
                color,
                mask,
                cursor_type: 2,
                position: cursor.position,
                hotspot_x: cursor.hotspot_x,
                hotspot_y: cursor.hotspot_y,
                width: *width,
                height: *height,
                visible: true,
            })
        }
    }
}
