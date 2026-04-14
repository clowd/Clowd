//! Embedded SVG icons and TTF font.
//!
//! All assets are `include_bytes!`'d at compile time so the compiled
//! binary is self-contained — no filesystem lookups, no relative-path
//! hazards, no startup I/O. The asset files live under
//! `clowd_capture_wgpu/assets/` so this crate has no build-time
//! dependency on its sibling crates.
//!
//! Filenames map to button actions per the C++ `app.rc` /
//! `captureButtonDetails`:
//!
//!   IDR_SVG7 = clowd-white.svg       → UPLOAD
//!   IDR_SVG3 = edit_image.svg        → EDIT
//!   IDR_SVG6 = video_camera.svg      → VIDEO
//!   IDR_SVG1 = copy_to_clipboard.svg → COPY
//!   IDR_SVG5 = save.svg              → SAVE
//!   IDR_SVG4 = refresh.svg           → RESET
//!   IDR_SVG2 = delete.svg            → EXIT
//!
//! The TTF is Roboto Regular (Apache 2.0). It's a good general-purpose
//! sans — clean at small sizes, unambiguous digits for the area
//! indicator's `WIDTH × HEIGHT` display.

// --- SVG button icons ------------------------------------------------------
pub const SVG_UPLOAD: &[u8] = include_bytes!("../../../../assets/icons/clowd-white.svg");
pub const SVG_EDIT: &[u8] = include_bytes!("../../../../assets/icons/edit_image.svg");
pub const SVG_VIDEO: &[u8] = include_bytes!("../../../../assets/icons/video_camera.svg");
pub const SVG_COPY: &[u8] = include_bytes!("../../../../assets/icons/copy_to_clipboard.svg");
pub const SVG_SAVE: &[u8] = include_bytes!("../../../../assets/icons/save.svg");
pub const SVG_RESET: &[u8] = include_bytes!("../../../../assets/icons/refresh.svg");
pub const SVG_EXIT: &[u8] = include_bytes!("../../../../assets/icons/delete.svg");

// --- Font ------------------------------------------------------------------
/// Roboto Regular. Licensed under Apache 2.0, safe to embed in a
/// distributed binary.
pub const FONT_ROBOTO: &[u8] = include_bytes!("../../../../assets/fonts/Roboto-Regular.ttf");
