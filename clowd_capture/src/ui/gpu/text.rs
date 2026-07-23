//! glyphon wrapper. One stack per render thread.
//!
//! A [`TextStack`] owns the per-device glyphon resources (font system,
//! swash cache, atlas, viewport, renderer). Per-frame: call
//! [`TextStack::update_viewport`] when the surface changes, then `prepare`
//! with all the text areas for the frame, then `draw` inside a render
//! pass.

use glyphon::{Cache, FontSystem, Resolution, SwashCache, TextArea, TextAtlas, TextRenderer, Viewport};

pub const FONT_MONO_REGULAR: &[u8] = include_bytes!("../../../assets/fonts/CascadiaMono-Regular.ttf");
pub const FONT_MONO_BOLD: &[u8] = include_bytes!("../../../assets/fonts/CascadiaMono-Bold.ttf");
pub const FONT_CODE_REGULAR: &[u8] = include_bytes!("../../../assets/fonts/CascadiaCode-Regular.ttf");
pub const FONT_CODE_BOLD: &[u8] = include_bytes!("../../../assets/fonts/CascadiaCode-Bold.ttf");

pub const FAMILY_MONO: &str = "Cascadia Mono";
pub const FAMILY_CODE: &str = "Cascadia Code";

pub struct TextStack {
    pub font_system: FontSystem,
    pub swash_cache: SwashCache,
    pub viewport: Viewport,
    pub atlas: TextAtlas,
    pub renderer: TextRenderer,
}

impl TextStack {
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue, surface_format: wgpu::TextureFormat) -> Self {
        let mut db = glyphon::fontdb::Database::new();
        db.load_font_data(FONT_MONO_REGULAR.to_vec());
        db.load_font_data(FONT_MONO_BOLD.to_vec());
        db.load_font_data(FONT_CODE_REGULAR.to_vec());
        db.load_font_data(FONT_CODE_BOLD.to_vec());
        let font_system = FontSystem::new_with_locale_and_db("en-US".to_string(), db);

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

    pub fn update_viewport(&mut self, queue: &wgpu::Queue, width: u32, height: u32) {
        self.viewport.update(
            queue,
            Resolution {
                width,
                height,
            },
        );
    }

    pub fn prepare<'a>(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        text_areas: &[TextArea<'a>],
    ) -> Result<bool, glyphon::PrepareError> {
        if text_areas.is_empty() {
            return Ok(false);
        }
        self.renderer.prepare(
            device,
            queue,
            &mut self.font_system,
            &mut self.atlas,
            &self.viewport,
            text_areas.iter().cloned(),
            &mut self.swash_cache,
        )?;
        Ok(true)
    }

    pub fn draw<'a>(&'a self, pass: &mut wgpu::RenderPass<'a>) -> Result<(), glyphon::RenderError> {
        self.renderer
            .render(&self.atlas, &self.viewport, pass)
    }

    pub fn trim(&mut self) {
        self.atlas.trim();
    }
}
