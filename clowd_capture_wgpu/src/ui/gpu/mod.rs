//! Pure-GPU UI stack. One `UiRenderer` per render thread. Replaces the
//! CPU-baked `OverlayBackend` pipeline.
//!
//! Submodules:
//!   * [`rect`]   — instanced colored/bordered rect pipeline
//!   * [`text`]   — glyphon wrapper (Phase 3)
//!   * [`svg`]    — usvg + lyon path tessellation (Phase 4)
//!   * [`panel`]  — per-frame button-panel draw (Phase 4)
//!   * [`tips`]   — per-frame tips-panel draw (Phase 3)
//!   * [`renderer`] — the top-level `UiRenderer` (Phase 2)

pub mod panel;
pub mod rect;
pub mod renderer;
pub mod svg;
pub mod text;
pub mod tips;

pub use renderer::UiRenderer;
