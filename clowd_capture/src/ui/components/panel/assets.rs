//! Embedded SVG assets for the panel.
//!
//! Each `include_bytes!` points at a file in `assets/icons/`. The button
//! icons are parsed with `usvg` and rasterised into the
//! `ui::gpu::icon::IconAtlas` by `ui::gpu::panel` on the render thread;
//! `PANEL_ICONS` in `model.rs` is the deduped table that decides atlas
//! slot order. Fonts live in `ui::gpu::text` because they're consumed by
//! the text stack directly.

/// Paper-plane "send" mark for the UPLOAD button (24-unit canvas, white).
pub const SVG_UPLOAD: &[u8] = include_bytes!("../../../../assets/icons/upload.svg");
pub const SVG_EDIT: &[u8] = include_bytes!("../../../../assets/icons/edit_image.svg");
pub const SVG_VIDEO: &[u8] = include_bytes!("../../../../assets/icons/video_camera.svg");
pub const SVG_SHARE: &[u8] = include_bytes!("../../../../assets/icons/share.svg");
pub const SVG_SCROLL: &[u8] = include_bytes!("../../../../assets/icons/scroll.svg");
pub const SVG_OCR: &[u8] = include_bytes!("../../../../assets/icons/ocr.svg");
pub const SVG_COPY: &[u8] = include_bytes!("../../../../assets/icons/copy_to_clipboard.svg");
pub const SVG_SAVE: &[u8] = include_bytes!("../../../../assets/icons/save.svg");
pub const SVG_RESET: &[u8] = include_bytes!("../../../../assets/icons/refresh.svg");
pub const SVG_EXIT: &[u8] = include_bytes!("../../../../assets/icons/delete.svg");
pub const SVG_SEARCH: &[u8] = include_bytes!("../../../../assets/icons/search.svg");
pub const SVG_BACK: &[u8] = include_bytes!("../../../../assets/icons/back.svg");

/// The Clowd logo drawn as the tray emblem at the head of the strip
/// (16-unit canvas, brand blue baked in). NOT a button icon: it is
/// deliberately absent from `PANEL_ICONS` because it has no button, no
/// hover and no click, and is rasterised at its own (larger) size into
/// the last slot of the panel's icon atlas.
pub const SVG_CLOWD_LOGO: &[u8] = include_bytes!("../../../../assets/icons/clowd-logo.svg");
