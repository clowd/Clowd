//! Embedded SVG button icons.
//!
//! Each `include_bytes!` points at a file in `assets/icons/`; the bytes
//! are parsed with `usvg` + tessellated with `lyon` at render-thread
//! startup — see `ui::gpu::svg` and `ui::gpu::panel`. Fonts live in
//! `ui::gpu::text` because they're consumed by glyphon directly.

pub const SVG_UPLOAD: &[u8] = include_bytes!("../../../../assets/icons/clowd-white.svg");
pub const SVG_EDIT: &[u8] = include_bytes!("../../../../assets/icons/edit_image.svg");
pub const SVG_VIDEO: &[u8] = include_bytes!("../../../../assets/icons/video_camera.svg");
pub const SVG_COPY: &[u8] = include_bytes!("../../../../assets/icons/copy_to_clipboard.svg");
pub const SVG_SAVE: &[u8] = include_bytes!("../../../../assets/icons/save.svg");
pub const SVG_RESET: &[u8] = include_bytes!("../../../../assets/icons/refresh.svg");
pub const SVG_EXIT: &[u8] = include_bytes!("../../../../assets/icons/delete.svg");
