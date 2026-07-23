use crate::geometry::ScreenRect;

pub(crate) struct PeekTextureEntry {
    pub _texture: wgpu::Texture,
    pub view: wgpu::TextureView,
    pub window_rect: ScreenRect,
    pub obstruction_rects: Vec<ScreenRect>,
    pub width: u32,
    pub height: u32,
    pub crop_x: i32,
    pub crop_y: i32,
}
