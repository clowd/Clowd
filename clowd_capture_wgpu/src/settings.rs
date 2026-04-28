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
    /// RGBA (each channel in [0, 1]) accent colour used for crosshair
    /// arms, selection borders, and UI highlights. Written into the
    /// per-window uniform buffer once, at render-thread startup.
    pub accent_color: [f32; 4],
    /// Whether the Tips & Hotkeys panel is visible when the capturer
    /// first opens. The user can still toggle it with the `T` key.
    pub tips_visible_at_startup: bool,
    /// When enabled, obstructed windows are captured via PrintWindow
    /// and a peek-through composite is shown when hovering them.
    pub obscured_window_peek_enabled: bool,
    /// Maximum fraction of a window's area that can be obstructed by
    /// higher-Z windows before it is dropped from hit-test results.
    /// 0.80 = windows up to 80% covered are still selectable. Range 0.0–1.0.
    pub obscured_window_detection_threshold: f32,
    /// Whether the captured OS cursor is rendered in the preview and
    /// included when saving/copying. The cursor image is always captured
    /// regardless; this only controls visibility. Toggled at runtime
    /// with the `M` key.
    pub cursor_visible_at_startup: bool,
}

impl Default for CapturerSettings {
    fn default() -> Self {
        Self {
            // #3B97D2 — the legacy "clowd blue" accent.
            accent_color: [0x3B as f32 / 255.0, 0x97 as f32 / 255.0, 0xD2 as f32 / 255.0, 1.0],
            tips_visible_at_startup: true,
            obscured_window_peek_enabled: true,
            obscured_window_detection_threshold: 0.80,
            cursor_visible_at_startup: true,
        }
    }
}
