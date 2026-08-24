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
    /// Second renderer over the SAME atlas, for the OCR bubble glyphs.
    ///
    /// It exists because draw order is the layering: bubble text must land
    /// UNDER the panel/hint rects while the main text draw runs above them
    /// (`UiRenderer::draw`), and one glyphon renderer issues one draw.
    /// Lazily created on the first OCR reveal — this overlay is
    /// startup-latency-sensitive (see the startup marks around
    /// `TextStack::new`) and non-OCR sessions never pay for it. Cheap when
    /// it does happen: the pipeline is shared via glyphon's `Cache`, so
    /// this is essentially a vertex-buffer allocation.
    bubble_renderer: Option<TextRenderer>,
    /// Whether [`Self::try_merge_system_fonts`] merged the scan — see its docs.
    fallback_fonts_loaded: bool,
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
            bubble_renderer: None,
            fallback_fonts_loaded: false,
        }
    }

    /// Merge the background system-font scan's result into this stack's
    /// font DB, so cosmic-text's per-script fallback can shape glyphs the
    /// embedded Cascadia faces lack (CJK, Cyrillic, Greek, Arabic, …).
    /// Returns whether the fallback faces are registered.
    ///
    /// The startup DB deliberately contains ONLY the embedded faces — this
    /// overlay is startup-latency-sensitive, and the scan is ~11 ms warm
    /// but SECONDS from a cold page cache (see the perf probe at the bottom
    /// of this file) — so nothing font-related may ever run on a render
    /// thread. The scan runs once per process on a low-priority background
    /// thread that [`begin_system_font_scan`] starts at the first OCR
    /// press; this method only copies the finished scan's `FaceInfo`
    /// records (Arc'd file paths — fontdb parses face data lazily at
    /// raster time) into the per-thread DB, a per-face metadata clone.
    /// Idempotent; after the first successful merge it is a boolean test.
    ///
    /// Safe to do after shaping has already happened: the pre-load shaping
    /// (panel/hint labels) is ASCII the embedded faces fully cover, so no
    /// cached fallback list for those runs can be wrong — and bubble text
    /// is only ever shaped after this returned true (`recognize`'s worker
    /// waits out the scan before publishing a result, and the bubble
    /// layout path calls this again as a belt-and-braces guard).
    pub fn try_merge_system_fonts(&mut self) -> bool {
        if self.fallback_fonts_loaded {
            return true;
        }
        // Defensive: normally the OCR press already started the scan.
        begin_system_font_scan();
        let Some(scanned) = system_font_scan_latch().try_get() else {
            return false;
        };
        let t0 = std::time::Instant::now();
        let db = self.font_system.db_mut();
        for info in scanned.faces() {
            db.push_face_info(info.clone());
        }
        log::info!(
            "merged system fonts for OCR glyph fallback: {} faces in {:?}",
            self.font_system.db().faces().count(),
            t0.elapsed()
        );
        self.fallback_fonts_loaded = true;
        true
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

    /// Prepare the OCR bubble glyphs on the dedicated renderer (created on
    /// first use — see the field docs). Returns whether anything was
    /// staged; the caller MUST gate [`Self::draw_bubbles`] on it, because
    /// a renderer that prepared nothing this frame would re-issue its
    /// previous frame's vertices.
    pub fn prepare_bubbles<'a>(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        text_areas: &[TextArea<'a>],
    ) -> Result<bool, glyphon::PrepareError> {
        if text_areas.is_empty() {
            return Ok(false);
        }
        let renderer = self.bubble_renderer.get_or_insert_with(|| {
            TextRenderer::new(
                &mut self.atlas,
                device,
                wgpu::MultisampleState {
                    count: crate::render::MSAA_SAMPLES,
                    mask: !0,
                    alpha_to_coverage_enabled: false,
                },
                None,
            )
        });
        renderer.prepare(
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

    /// Draw the bubble glyphs. Only called when the same frame's
    /// `prepare_bubbles` returned true — see its docs.
    pub fn draw_bubbles<'a>(&'a self, pass: &mut wgpu::RenderPass<'a>) -> Result<(), glyphon::RenderError> {
        match &self.bubble_renderer {
            Some(r) => r.render(&self.atlas, &self.viewport, pass),
            None => Ok(()),
        }
    }

    pub fn trim(&mut self) {
        self.atlas.trim();
    }
}

/// The one process-wide system-font scan. `Latch` (not `OnceLock`) because
/// the OCR worker thread needs a blocking-with-timeout wait
/// ([`wait_for_system_font_scan`]) while render threads need a non-blocking
/// peek (`try_get`).
fn system_font_scan_latch() -> &'static crate::sync::Latch<std::sync::Arc<glyphon::fontdb::Database>> {
    static LATCH: std::sync::OnceLock<crate::sync::Latch<std::sync::Arc<glyphon::fontdb::Database>>> = std::sync::OnceLock::new();
    LATCH.get_or_init(crate::sync::Latch::new)
}

/// Start the system-font scan on a background thread, once per process.
///
/// Called at the first OCR press (`app.rs`), overlapping the scan with the
/// recognizer child's own cold start — by decision, NOTHING OCR-related
/// (this scan, the `clowd_ai` child) loads before OCR is actually used, so
/// capture startup never pays for a rarely-used feature. Low priority: on
/// a cold page cache the scan is disk-bound for seconds (font files +
/// on-access AV scanning) and must not contend with rendering.
pub fn begin_system_font_scan() {
    static STARTED: std::sync::Once = std::sync::Once::new();
    STARTED.call_once(|| {
        let spawned = std::thread::Builder::new()
            .name("font-scan".into())
            .spawn(|| {
                // Background, not just below-normal: the scan is DISK-bound
                // (cold page cache, on-access AV), and only the background
                // tier lowers I/O priority too.
                crate::system::background_thread_priority();
                let t0 = std::time::Instant::now();
                let mut db = glyphon::fontdb::Database::new();
                db.load_system_fonts();
                log::info!("system font scan: {} faces in {:?}", db.faces().count(), t0.elapsed());
                system_font_scan_latch().set(std::sync::Arc::new(db));
            });
        if let Err(e) = spawned {
            log::warn!("failed to spawn the font-scan thread: {e}");
        }
    });
}

/// Block until the system-font scan lands (or `timeout`). For the OCR
/// worker thread only — it holds the recognition result back until the
/// fallback faces are mergeable, so the reveal never shapes non-ASCII
/// lines against an embedded-only DB (tofu that would stay cached for the
/// life of the request). Render threads must never call this.
pub fn wait_for_system_font_scan(timeout: std::time::Duration) {
    if system_font_scan_latch()
        .wait_timeout(timeout)
        .is_none()
    {
        log::warn!("system font scan did not finish within {timeout:?}; OCR bubbles may lack non-ASCII glyphs");
    }
}

#[cfg(test)]
mod tests {
    /// Perf probe, kept as the record of why the scan lives on a
    /// background thread (`begin_system_font_scan`) and nothing on a
    /// render thread may ever call `load_system_fonts`: ~11 ms / 363
    /// faces warm on the dev box (fontdb only parses name tables), but
    /// SECONDS from a cold page cache — see below. Prints with
    /// --nocapture; asserts only a sanity bound.
    ///
    /// The bound is deliberately enormous relative to that measurement, and
    /// it has to be: a hosted Windows runner was seen taking 5.11 s over 176
    /// faces — three orders of magnitude off the dev box, from cold disk and
    /// on-access scanning rather than from anything in this code. What the
    /// assertion is for is a catastrophic regression (a scan that walks glyph
    /// tables, or rescans per frame), and that shows up as minutes, not as
    /// the difference between 5 and 30 seconds. Timing the machine instead of
    /// the code is how a probe becomes a flake, so read the printed number,
    /// not the bound.
    #[test]
    fn probe_system_font_load_cost() {
        let mut db = glyphon::fontdb::Database::new();
        let t = std::time::Instant::now();
        db.load_system_fonts();
        eprintln!("load_system_fonts: {} faces in {:?}", db.faces().count(), t.elapsed());
        assert!(t.elapsed().as_secs() < 60, "system font scan took {:?}", t.elapsed());
    }
}
