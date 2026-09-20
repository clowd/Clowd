//! The worker-side UI stack: one `UiRenderer` per render thread.
//!
//! Submodules:
//!   * [`egui_painter`] — the egui triangle-list painter
//!   * [`renderer`] — the top-level `UiRenderer`
//!
//! Every overlay is laid out and tessellated by egui on the app thread, so
//! a render thread has exactly one thing to draw: this monitor's egui
//! frame. The hand-written rect, glyph and text pipelines that used to
//! draw the panel, hints, tips, reticle, sweep and OCR bubbles are gone
//! along with their shaders.

pub mod egui_painter;
pub mod renderer;

pub use renderer::UiRenderer;
