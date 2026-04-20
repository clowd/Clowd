//! User-configurable capturer settings.
//!
//! `CapturerSettings` is constructed once in `main.rs`, wrapped in an
//! `Arc`, and handed to `App` / the render threads. Today only the
//! crosshair colour is wired to the GPU, but the struct exists so more
//! knobs (line width, dim amount, hotkeys, …) can be added without
//! touching every signature in the pipeline.

/// Settings that influence how the capturer renders. Cheap to clone
/// via `Arc` — we never mutate it after construction.
#[derive(Debug, Clone)]
pub struct CapturerSettings {
    /// RGBA (each channel in [0, 1]) used for both the inner thin
    /// crosshair arms and the outer thick segments. Written into the
    /// per-window uniform buffer once, at render-thread startup.
    pub crosshair_color: [f32; 4],
}

impl Default for CapturerSettings {
    fn default() -> Self {
        Self {
            // #3B97D2 — the legacy "clowd blue" accent.
            crosshair_color: [0x3B as f32 / 255.0, 0x97 as f32 / 255.0, 0xD2 as f32 / 255.0, 1.0],
        }
    }
}
