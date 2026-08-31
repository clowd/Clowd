#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct PeekUniforms {
    pub selection_rect: [f32; 4],
    pub window_uv: [f32; 4],
    pub desktop_uv: [f32; 4],
    /// (num_obstruction_rects, ghost_opacity, viewport_w, viewport_h)
    pub params: [f32; 4],
    /// (ocr_dim, ocr_gray, ocr_active, 0) — the OCR mode's region dim /
    /// desaturation / active flag, identical values to the desktop pass's
    /// `ocr_params`. The peek quad draws OVER the desktop pass inside the
    /// selection, so without its own copy the region would stay bright
    /// under a locked peek while everything the recognition ran against
    /// was supposed to be monochrome. All zero outside OCR mode — the
    /// non-OCR peek path is byte-identical to before.
    pub ocr_params: [f32; 4],
    pub obstruction_rects: [[f32; 4]; 16],
}

impl PeekUniforms {
    pub fn zeroed() -> Self {
        bytemuck::Zeroable::zeroed()
    }
}

pub const PEEK_UNIFORMS_SIZE: u64 = std::mem::size_of::<PeekUniforms>() as u64;
