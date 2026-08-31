//! The two overlay feature passes split out of the old monolithic
//! desktop shader: the crosshair (crosshair.wgsl) and the selection
//! border + handles (selection.wgsl). Both draw small vertex-shader-
//! generated quads over the desktop/peek passes with premultiplied
//! source-over blending, so their GPU cost scales with the feature's own
//! pixel area — and each is skipped entirely (no draw call) whenever its
//! feature is not on screen.

use crate::gxi::{self, BlendMode, PipelineDesc, ShaderId};

/// The one uniform block both overlay passes read — written once per
/// frame, bound by two bind groups (one per shader). MUST stay
/// byte-identical to the `OverlayUniforms` struct in selection.wgsl and
/// crosshair.wgsl; the per-field meaning is documented there.
#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct OverlayUniforms {
    /// (viewport_w, viewport_h, dpi_scale, fade).
    pub viewport: [f32; 4],
    /// (cursor_x, cursor_y, 0, 0) in window-local physical px.
    pub cursor: [f32; 4],
    pub accent_color: [f32; 4],
    /// Selection in window-local physical px (l, t, r, b); only read
    /// while the selection pass is drawn, so never observed empty.
    pub selection_rect: [f32; 4],
    /// (elapsed_secs, dash_period_px, corner_radius_px, handles_visible).
    pub sel_params: [f32; 4],
    /// Window px → desktop-texture UV, same values as the desktop pass.
    pub uv_offset_scale: [f32; 4],
}

impl OverlayUniforms {
    pub fn zeroed() -> Self {
        bytemuck::Zeroable::zeroed()
    }
}

pub const OVERLAY_UNIFORMS_SIZE: u64 = std::mem::size_of::<OverlayUniforms>() as u64;

/// The crosshair pass's peek-replication block (its second uniform
/// buffer): the thin cross's black/white contrast is decided from the
/// pixels actually displayed beneath it, which under an active peek quad
/// is the peek composite rather than the desktop snapshot — a fragment
/// shader cannot read the framebuffer, so the composite is replicated
/// from the same inputs peek.wgsl uses. Written zeroed while no peek is
/// on screen. MUST stay byte-identical to `CrosshairPeekUniforms` in
/// crosshair.wgsl; per-field meaning is documented there.
#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct CrosshairPeekUniforms {
    /// (active, ghost_opacity, num_obstruction_rects, 0).
    pub params: [f32; 4],
    /// The peek quad's window-texture UV mapping (peek.wgsl window_uv).
    pub window_uv: [f32; 4],
    /// Window-local px, identical to the peek pass's rects.
    pub obstruction_rects: [[f32; 4]; 16],
}

impl CrosshairPeekUniforms {
    pub fn zeroed() -> Self {
        bytemuck::Zeroable::zeroed()
    }
}

pub const CROSSHAIR_PEEK_UNIFORMS_SIZE: u64 = std::mem::size_of::<CrosshairPeekUniforms>() as u64;

/// Vertex counts for the fixed vertex-shader-generated geometry: the
/// crosshair's 11 quads and the selection pass's 16 (4 border slabs, 4
/// corner patches, 8 handles — unused ones degenerate to zero area).
pub const CROSSHAIR_VERTICES: u32 = 11 * 6;
pub const SELECTION_VERTICES: u32 = 16 * 6;

pub fn create_crosshair_pipeline(device: &gxi::Device) -> gxi::RenderPipeline {
    device.create_pipeline(&PipelineDesc {
        label: "crosshair pipeline",
        shader: ShaderId::Crosshair,
        vertex: None,
        blend: BlendMode::PremultipliedAlpha,
    })
}

pub fn create_selection_pipeline(device: &gxi::Device) -> gxi::RenderPipeline {
    device.create_pipeline(&PipelineDesc {
        label: "selection pipeline",
        shader: ShaderId::Selection,
        vertex: None,
        blend: BlendMode::PremultipliedAlpha,
    })
}
