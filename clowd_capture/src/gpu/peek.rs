#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct PeekUniforms {
    pub selection_rect: [f32; 4],
    pub window_uv: [f32; 4],
    pub desktop_uv: [f32; 4],
    /// (num_obstruction_rects, ghost_opacity, viewport_w, viewport_h)
    pub params: [f32; 4],
    /// (cursor_x, cursor_y, dpi_scale, 0) in monitor-local pixels
    pub cursor_params: [f32; 4],
    pub obstruction_rects: [[f32; 4]; 16],
}

impl PeekUniforms {
    pub fn zeroed() -> Self {
        bytemuck::Zeroable::zeroed()
    }
}

pub const PEEK_UNIFORMS_SIZE: u64 = std::mem::size_of::<PeekUniforms>() as u64;
