use std::sync::Arc;

use crate::gxi::{self, TexFormat, TextureDesc};
use crate::system::{CapturedCursor, CapturedDesktop, CursorImage};
use clowd_rust_core::geometry::{RectExt, ScreenPoint};

#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct WindowUniforms {
    pub uv_offset_scale: [f32; 4],
    pub params: [f32; 4],
    pub accent_color: [f32; 4],
    pub selection_rect: [f32; 4],
    pub selection_params: [f32; 4],
    /// Cursor rect in window-local physical pixels: [left, top, right, bottom].
    /// Empty (right<=left) when cursor is hidden or off this monitor.
    pub cursor_rect: [f32; 4],
    /// Cursor compositing type: 0=hidden, 1=alpha_blended, 2=masked.
    pub cursor_params: [f32; 4],
    /// OCR source region in window-local physical pixels: [l, t, r, b].
    /// Empty (right<=left) while OCR mode is idle. Currently informational
    /// (the dim applies to the whole selection fill — the OCR region IS
    /// the selection, modulo edge clamping); plumbed so a future partial
    /// dim needs no uniform-layout change, same precedent as
    /// `selection_params.z`.
    pub ocr_rect: [f32; 4],
    /// x = source-region dim amount 0..1 (ramps with the lift animation),
    /// y = OCR-mode-active flag (suppresses the resize handles: they must
    /// not draw over lifted text), z/w spare.
    pub ocr_params: [f32; 4],
    /// x = the selection's corner radius in window-local physical px
    /// (already through the magnifier zoom, like `selection_rect`); 0 =
    /// square, which keeps the shader on its original integer-slab border
    /// path. Non-zero only for a picked window — see
    /// `InteractionState::selection_radius`.
    /// y = the marching-ants dash period in physical px for this frame —
    /// see `render::desktop::dash_period`: the nominal 32 px × DPI step,
    /// snapped so the border's perimeter holds a whole number of dashes
    /// (no seam where the pattern wraps) except while the selection is
    /// being dragged, when the snap would re-phase every frame. 0 = let
    /// the shader use the nominal period. z/w spare.
    pub selection_shape: [f32; 4],
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

    // No forced empty submit after this upload on purpose. It used to
    // force the pending ~33 MB staging copy into a real submission right
    // away, and that submission was the *only* thing giving
    // `configure_surface`'s `maintain(wait_indefinitely)`
    // (wgpu-core-30.0.0 `device/resource.rs`, "Wait for all work to
    // finish before configuring the surface") anything to wait on — with
    // nothing in flight the dx12 fence wait early-returns on
    // `GetCompletedValue() >= value` (wgpu-hal-30.0.0
    // `dx12/device.rs::wait`). Frame 0's own submit flushes the write
    // regardless, so dropping the forced submit costs nothing in
    // correctness.
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
