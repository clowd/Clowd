//! Pure-GPU UI stack. One `UiRenderer` per render thread.
//!
//! Submodules:
//!   * [`rect`]   — instanced colored/bordered rect pipeline
//!   * [`icon`]   — CPU-rasterised icon atlas + textured-quad pipeline
//!   * [`text`]   — glyphon wrapper
//!   * [`panel`]  — per-frame button-panel draw
//!   * [`tips`]   — per-frame tips-panel draw
//!   * [`renderer`] — the top-level `UiRenderer`

pub mod area;
pub mod debug;
pub mod gpu_timing;
pub mod icon;
pub mod panel;
pub mod rect;
pub mod renderer;
pub mod text;
pub mod tips;

pub use renderer::UiRenderer;
