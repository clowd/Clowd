//! glyphon wrapper. One stack per render thread.
//!
//! A [`TextStack`] owns the per-device glyphon resources (font system,
//! swash cache, atlas, viewport, renderer). Per-frame: call
//! [`TextStack::update_viewport`] when the surface changes, then `prepare`
//! with all the text areas for the frame, then `draw` inside a render
//! pass.

use glyphon::{
    Cache, FontSystem, Resolution, SwashCache, TextArea, TextAtlas, TextRenderer, Viewport,
};

// Embedded fonts. Loaded into the per-thread FontSystem at construction
// time so downstream components can reference them by family name.
pub const FONT_MONO_REGULAR: &[u8] =
    include_bytes!("../../../assets/fonts/CascadiaMono-Regular.ttf");
pub const FONT_MONO_BOLD: &[u8] =
    include_bytes!("../../../assets/fonts/CascadiaMono-Bold.ttf");
pub const FONT_ROBOTO_REGULAR: &[u8] =
    include_bytes!("../../../assets/fonts/Roboto-Regular.ttf");

/// Family name to pass in `Attrs::family(Family::Name(...))` for each
/// font. We rely on cosmic-text to distinguish Regular vs Bold by the
/// attribute weight rather than a separate family string.
pub const FAMILY_MONO: &str = "Cascadia Mono";
pub const FAMILY_ROBOTO: &str = "Roboto";

pub struct TextStack {
    pub font_system: FontSystem,
    pub swash_cache: SwashCache,
    pub viewport: Viewport,
    pub atlas: TextAtlas,
    pub renderer: TextRenderer,
}

impl TextStack {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        surface_format: wgpu::TextureFormat,
    ) -> Self {
        let mut font_system = FontSystem::new();
        font_system
            .db_mut()
            .load_font_data(FONT_MONO_REGULAR.to_vec());
        font_system.db_mut().load_font_data(FONT_MONO_BOLD.to_vec());
        font_system
            .db_mut()
            .load_font_data(FONT_ROBOTO_REGULAR.to_vec());

        let swash_cache = SwashCache::new();
        let cache = Cache::new(device);
        let viewport = Viewport::new(device, &cache);
        let mut atlas = TextAtlas::new(device, queue, &cache, surface_format);
        let renderer = TextRenderer::new(
            &mut atlas,
            device,
            wgpu::MultisampleState {
                count: crate::render::MSAA_SAMPLES,
                mask: !0,
                alpha_to_coverage_enabled: false,
            },
            None,
        );

        Self {
            font_system,
            swash_cache,
            viewport,
            atlas,
            renderer,
        }
    }

    /// Update the viewport resolution. Cheap; call every frame before
    /// `prepare` to account for window resize.
    pub fn update_viewport(&mut self, queue: &wgpu::Queue, width: u32, height: u32) {
        self.viewport.update(queue, Resolution { width, height });
    }

    /// Shape-and-upload all text areas for this frame. Returns `Ok(true)`
    /// when at least one area was prepared (so a `draw()` should happen),
    /// `Ok(false)` when there was nothing to draw.
    pub fn prepare<'a>(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        text_areas: impl IntoIterator<Item = TextArea<'a>>,
    ) -> Result<bool, glyphon::PrepareError> {
        let areas: Vec<TextArea<'a>> = text_areas.into_iter().collect();
        if areas.is_empty() {
            return Ok(false);
        }
        self.renderer.prepare(
            device,
            queue,
            &mut self.font_system,
            &mut self.atlas,
            &self.viewport,
            areas,
            &mut self.swash_cache,
        )?;
        Ok(true)
    }

    /// Issue the glyphon draw. Call inside an existing render pass after
    /// `prepare()` returned `Ok(true)`.
    pub fn draw<'a>(&'a self, pass: &mut wgpu::RenderPass<'a>) -> Result<(), glyphon::RenderError> {
        self.renderer.render(&self.atlas, &self.viewport, pass)
    }

    /// Free cached glyph atlas entries for texture variants no longer in
    /// use. Call at the end of the frame after `draw` to keep the atlas
    /// from growing unboundedly across long sessions.
    pub fn trim(&mut self) {
        self.atlas.trim();
    }
}
