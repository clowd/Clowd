use clowd_rust_core::geometry::ScreenRect;

use crate::gxi;

pub(crate) struct PeekTextureEntry {
    pub texture: gxi::Texture,
    pub window_rect: ScreenRect,
    pub obstruction_rects: Vec<ScreenRect>,
    pub width: u32,
    pub height: u32,
    pub crop_x: i32,
    pub crop_y: i32,
}
