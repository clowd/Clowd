//! Top-level per-monitor UI renderer.
//!
//! One instance per render thread. Every frame the caller first invokes
//! [`UiRenderer::prepare`] to decide what belongs on this monitor and
//! upload all per-frame GPU data (rect/svg instance buffers, glyphon
//! shape+atlas), then hands the renderer an open `RenderPass` via
//! [`UiRenderer::draw`]. The two-phase split lets the caller fold the UI
//! draw into the same render pass as the desktop triangle, avoiding an
//! MSAA tile store+load on M1 TBDR between a separate desktop and UI pass.
//!
//! Draw order inside the pass (see [`UiRenderer::draw`] for the OCR
//! bubble sandwich):
//!   1. `lift` pipeline: the OCR scanning sweep.
//!   2. `rect` pipeline, LEADING range: OCR bubble pills + shadows.
//!   3. glyphon bubble renderer: the bubbles' recognized-text glyphs.
//!   4. `rect` pipeline, TRAILING range: backgrounds, borders, shadow,
//!      color swatch, area indicator brackets, label underlines.
//!   5. `svg` pipeline: button icons (lyon-tessellated meshes).
//!   6. glyphon text: labels, tips body, area-indicator digits.

use std::sync::Arc;
use std::time::Instant;

use crate::telemetry::perf::PerfTracker;
use crate::telemetry::startup::StartupTimings;
use crate::ui::gpu::area::AreaRenderer;
use crate::ui::gpu::debug::DebugRenderer;
use crate::ui::gpu::hints::HintsRenderer;
use crate::ui::gpu::icon::{IconInstance, IconPipeline};
use crate::ui::gpu::lift::LiftPipeline;
use crate::ui::gpu::ocr_bubbles::OcrBubblesRenderer;
use crate::ui::gpu::panel::PanelRenderer;
use crate::ui::gpu::rect::{RectInstance, RectPipeline};
use crate::ui::gpu::text::TextStack;
use crate::ui::gpu::tips::TipsRenderer;
use crate::ui::shared::{UiMonitor, UiSharedState};

pub struct UiRenderer {
    rect: RectPipeline,
    icon: IconPipeline,
    lift: LiftPipeline,
    ocr_bubbles: OcrBubblesRenderer,
    text: TextStack,
    area: AreaRenderer,
    hints: HintsRenderer,
    tips: TipsRenderer,
    panel: PanelRenderer,
    debug: DebugRenderer,
    last_frame_time: Option<Instant>,
    state: Option<Arc<UiSharedState>>,
    this_monitor: UiMonitor,
    /// Stable per-render-thread context for the debug panel — values that
    /// never change after startup (monitor name, adapter, startup
    /// timings). `perf` is the only live source; see `render()`.
    monitor_name: String,
    adapter_name: String,
    startup: Arc<StartupTimings>,
    /// Set by `prepare()`, consumed by `draw()`. `false` when there's
    /// nothing to render (no state yet), so `draw()` becomes a no-op.
    has_prepared: bool,
    /// Set by `prepare()` when at least one text area was shaped — tells
    /// `draw()` whether to issue the glyphon draw.
    any_text: bool,
    /// Set by `prepare()` when at least one OCR bubble text area was
    /// staged on the dedicated bubble renderer. MUST gate the bubble text
    /// draw: an ungated draw would re-issue the renderer's previous
    /// frame's vertices (see `TextStack::prepare_bubbles`).
    any_bubble_text: bool,
    /// How many instances at the FRONT of the rect buffer are OCR bubble
    /// pills/shadows this frame — the split point for the two-range rect
    /// draw (`draw` documents the sandwich).
    bubble_rect_count: u32,
    /// True when the PREVIOUS frame prepared the bubble glyphs while the
    /// bubble scene was at rest (reveal settled — see
    /// `OcrBubblesRenderer::prepare`'s return). Arms the static-Lifted
    /// fast path below.
    bubble_static_prepared: bool,
    /// Viewport the armed preparation was made for — a resize invalidates
    /// the retained vertices, so the fast path requires it unchanged.
    bubble_static_viewport: (u32, u32),
    /// Set per frame: this frame is RE-ISSUING the bubble renderer's
    /// retained vertices instead of re-preparing them. While true, `trim`
    /// must be (and is) skipped — trim evicts every atlas entry no
    /// prepare referenced since the last trim, which would tear the
    /// retained vertices' glyphs out from under them.
    bubble_reuse_active: bool,
    /// Animation clock origin for UI effects (border trail).
    start_time: Instant,
}

/// The three UI render pipelines, built as one unit so the caller cannot
/// end up holding a half-built stack.
///
/// Nothing here is on the pre-first-frame path — frame 0 draws only the
/// desktop triangle — so these are compiled by the deferred builder
/// (`render::spawn_deferred_stack`) rather than by Stage A.
pub struct UiPipelines {
    rect: RectPipeline,
    icon: IconPipeline,
    lift: LiftPipeline,
}

impl UiPipelines {
    /// Compile the three pipelines concurrently on one shared device.
    ///
    /// `create_render_pipeline` takes no device-wide lock in wgpu 30
    /// (wgpu-core's `Device::create_render_pipeline` locks nothing; the
    /// DX12 backend holds its shader cache only across the map probe, with
    /// `compile()` outside it; the Metal backend takes none), so the real
    /// serialization point is the platform shader compiler, not this
    /// process. On macOS that means MTLCompilerService's XPC concurrency
    /// (~3-4 in flight) — which is why fanning three compiles out is worth
    /// three threads and fanning out further would not be.
    pub fn build_parallel(device: &wgpu::Device, surface_format: wgpu::TextureFormat) -> Self {
        std::thread::scope(|s| {
            // Deferred-build threads run below normal (the caller already
            // is; spawns do not inherit priority on Windows).
            let rect = s.spawn(|| {
                crate::system::lower_thread_priority();
                RectPipeline::new(device, surface_format)
            });
            let icon = s.spawn(|| {
                crate::system::lower_thread_priority();
                IconPipeline::new(device, surface_format)
            });
            // The third compile rides the calling thread: spawning for it
            // would only add a join.
            let lift = LiftPipeline::new(device, surface_format);
            Self {
                rect: rect.join().expect("ui rect pipeline thread"),
                icon: icon.join().expect("ui icon pipeline thread"),
                lift,
            }
        })
    }
}

/// The glyphon stack plus every component whose construction needs it —
/// they all allocate their `CachedBuffer`s out of the font system, and the
/// panel additionally parses its 11 icon SVGs. Kept together because the
/// `&mut TextStack` borrow makes them inherently sequential, so they are
/// one job for the deferred builder to schedule.
pub struct UiText {
    text: TextStack,
    area: AreaRenderer,
    hints: HintsRenderer,
    tips: TipsRenderer,
    panel: PanelRenderer,
}

impl UiText {
    pub fn new(mut text: TextStack) -> Self {
        let area = AreaRenderer::new(&mut text);
        let hints = HintsRenderer::new(&mut text);
        let tips = TipsRenderer::new(&mut text);
        let panel = PanelRenderer::new(&mut text);
        Self {
            text,
            area,
            hints,
            tips,
            panel,
        }
    }
}

impl UiRenderer {
    /// Assemble the renderer from parts the deferred builder produced.
    ///
    /// There is no constructor that builds those parts inline: owning a
    /// `UiRenderer` is the proof that every pipeline it can draw with has
    /// been compiled, which is what keeps frame 0 — drawn before this type
    /// exists at all — from being able to reference one.
    pub fn from_parts(
        pipelines: UiPipelines,
        text: UiText,
        this_monitor: UiMonitor,
        monitor_index: usize,
        monitor_name: String,
        adapter_name: String,
        startup: Arc<StartupTimings>,
    ) -> Self {
        let debug = DebugRenderer::new(monitor_index);
        Self {
            rect: pipelines.rect,
            icon: pipelines.icon,
            lift: pipelines.lift,
            ocr_bubbles: OcrBubblesRenderer::new(),
            text: text.text,
            area: text.area,
            hints: text.hints,
            tips: text.tips,
            panel: text.panel,
            debug,
            last_frame_time: None,
            state: None,
            this_monitor,
            monitor_name,
            adapter_name,
            startup,
            has_prepared: false,
            any_text: false,
            any_bubble_text: false,
            bubble_rect_count: 0,
            bubble_static_prepared: false,
            bubble_static_viewport: (0, 0),
            bubble_reuse_active: false,
            start_time: Instant::now(),
        }
    }

    pub fn set_state(&mut self, state: Arc<UiSharedState>) {
        self.state = Some(state);
    }

    /// Reset leftovers at `BeginCycle`, before frame 0 is drawn. With no
    /// state, `prepare` stages nothing and frame 0 is the clean initial
    /// overlay. Also re-anchors the animation clock.
    pub fn begin_cycle(&mut self) {
        self.state = None;
        self.last_frame_time = None;
        // The shaped bubble glyph buffers belong to a dead outcome.
        self.ocr_bubbles.clear();
        self.bubble_static_prepared = false;
        self.start_time = Instant::now();
    }

    /// Stage all per-frame work: component visibility decisions,
    /// rect/svg instance uploads, glyphon shape + atlas prep. After
    /// `prepare` returns the caller may open a render pass and invoke
    /// [`UiRenderer::draw`] to issue the UI draw calls into it. Split
    /// from `draw` so the UI can share the same render pass as the
    /// desktop triangle — on M1 TBDR this avoids an MSAA tile
    /// store+load between passes.
    pub fn prepare(&mut self, device: &wgpu::Device, queue: &wgpu::Queue, viewport_px: (u32, u32), perf: &PerfTracker) {
        self.has_prepared = false;
        self.any_text = false;
        self.any_bubble_text = false;
        self.bubble_rect_count = 0;
        self.bubble_reuse_active = false;

        let now = Instant::now();
        let dt = self
            .last_frame_time
            .map(|t| now.duration_since(t).as_secs_f32())
            .unwrap_or(0.0);
        self.last_frame_time = Some(now);

        let Some(state) = self.state.clone() else {
            return;
        };

        self.text
            .update_viewport(queue, viewport_px.0, viewport_px.1);

        // Pre-size the rect buffer. The debug sparkline alone emits a
        // few hundred rects (bars × segments + legend + reference
        // lines). Starting at zero means the vector grows-and-copies
        // 8-9 times per frame just to reach that size — one extra
        // memcpy per growth of the pushed bytes, which compounds into
        // a measurable CPU cost on a hot path. A fixed upper starting
        // capacity means zero growth allocations for the typical
        // frame.
        let mut rect_instances: Vec<RectInstance> = Vec::with_capacity(512);
        let mut icon_draws: Vec<IconInstance> = Vec::with_capacity(16);

        // OCR bubbles stage into their OWN rect list: it becomes the
        // LEADING range of the single rect upload below, which is what
        // lets `draw` slip the bubble glyphs between the pills and every
        // other rect (the panel must cover bubbles, bubbles must cover
        // the dimmed desktop).
        let mut bubble_rects: Vec<RectInstance> = Vec::with_capacity(32);
        let bubbles_at_rest = self
            .ocr_bubbles
            .prepare(&mut self.text, &state, &self.this_monitor, &mut bubble_rects);

        self.lift
            .prepare(device, queue, viewport_px, &state, &self.this_monitor);
        self.area
            .prepare(&mut self.text, &state, &self.this_monitor, &mut rect_instances);
        self.hints
            .prepare(&mut self.text, &state, &self.this_monitor, &mut rect_instances);
        self.tips
            .prepare(&mut self.text, &state, &self.this_monitor, &mut rect_instances);
        self.panel.prepare(
            device,
            queue,
            &mut self.text,
            &state,
            &self.this_monitor,
            &mut rect_instances,
            &mut icon_draws,
            dt,
        );
        self.debug.prepare(
            &mut self.text,
            &state,
            &self.this_monitor,
            &self.monitor_name,
            &self.adapter_name,
            perf,
            &self.startup,
            &mut rect_instances,
        );

        let elapsed_secs = self.start_time.elapsed().as_secs_f32();
        // One upload, two draw ranges: bubble pills lead, everything else
        // trails. Appending into `bubble_rects` (usually empty) rather
        // than the other way round keeps the common non-OCR frame to a
        // single already-sized allocation move.
        self.bubble_rect_count = bubble_rects.len() as u32;
        bubble_rects.append(&mut rect_instances);
        self.rect
            .prepare(device, queue, viewport_px, elapsed_secs, &bubble_rects);
        if let Some(atlas) = self.panel.atlas() {
            self.icon
                .prepare(device, queue, viewport_px, atlas, &icon_draws);
        }

        // Bubble glyphs ride the dedicated renderer so their draw can be
        // ordered between the two rect ranges — the main renderer below
        // draws last, above the panel.
        //
        // Static-Lifted fast path: once the reveal has settled every
        // frame's staging is byte-identical (the animation is a pure
        // clamped function of elapsed time), so the previous frame's
        // prepared vertices are simply re-issued — glyphon renderers
        // retain them — instead of re-shaping and re-uploading the whole
        // page per frame per monitor (a dense page is thousands of glyphs,
        // at up to whatever this monitor's refresh rate is). Correctness
        // leans on two things: the viewport is unchanged, and `trim` is
        // suppressed while the path is active (see [`Self::trim`]).
        self.bubble_reuse_active = bubbles_at_rest && self.bubble_static_prepared && self.bubble_static_viewport == viewport_px;
        if self.bubble_reuse_active {
            self.any_bubble_text = true;
        } else {
            let mut bubble_text_areas: Vec<glyphon::TextArea<'_>> = Vec::with_capacity(16);
            self.ocr_bubbles
                .text_areas(&mut bubble_text_areas);
            self.any_bubble_text = match self
                .text
                .prepare_bubbles(device, queue, &bubble_text_areas)
            {
                Ok(b) => b,
                Err(e) => {
                    log::warn!("glyphon bubble prepare error: {:?}", e);
                    false
                }
            };
            // Arm the fast path only off a frame that actually staged
            // bubble text at rest; an empty staging (no bubbles reach this
            // monitor) keeps taking the cheap empty prepare instead.
            self.bubble_static_prepared = bubbles_at_rest && self.any_bubble_text;
            self.bubble_static_viewport = viewport_px;
        }

        let mut text_areas: Vec<glyphon::TextArea<'_>> = Vec::with_capacity(48);
        self.area
            .text_areas(viewport_px, &mut text_areas);
        self.hints
            .text_areas(viewport_px, &mut text_areas);
        self.tips
            .text_areas(viewport_px, &mut text_areas);
        self.panel
            .text_areas(viewport_px, &mut text_areas);
        self.debug
            .text_areas(viewport_px, &mut text_areas);
        self.any_text = match self.text.prepare(device, queue, &text_areas) {
            Ok(b) => b,
            Err(e) => {
                log::warn!("glyphon prepare error: {:?}", e);
                false
            }
        };
        self.has_prepared = true;
    }

    pub fn draw<'a>(&'a self, rpass: &mut wgpu::RenderPass<'a>) {
        if !self.has_prepared {
            return;
        }
        // Ordering contract, bottom to top (each item relies on the ones
        // before it having already painted):
        //   1. lift — the OCR scanning sweep, over the desktop/peek
        //      passes that already ran in this render pass and under
        //      everything below.
        //   2. rect LEADING range — OCR bubble pills + shadows, over the
        //      dimmed desktop.
        //   3. bubble glyphs — the recognized text, on its own glyphon
        //      renderer precisely so it can be issued here: above its
        //      pill backgrounds, below the panel's rects.
        //   4. rect TRAILING range — hint pills, the button panel: covers
        //      any bubble it overlaps.
        //   5. icons, then 6. main glyphon text (panel/hint labels) —
        //      the panel and its labels end up above EVERYTHING, bubbles
        //      included.
        self.lift.draw(rpass);
        self.rect
            .draw_range(rpass, 0..self.bubble_rect_count);
        if self.any_bubble_text {
            if let Err(e) = self.text.draw_bubbles(rpass) {
                log::warn!("glyphon bubble render error: {:?}", e);
            }
        }
        self.rect
            .draw_range(rpass, self.bubble_rect_count..u32::MAX);
        self.icon.draw(rpass);
        if self.any_text {
            if let Err(e) = self.text.draw(rpass) {
                log::warn!("glyphon render error: {:?}", e);
            }
        }
    }

    pub fn trim(&mut self) {
        // While the static-Lifted fast path re-issues retained bubble
        // vertices without re-preparing them (see `prepare`), trim would
        // evict their glyphs — trim drops every atlas entry no prepare
        // referenced since the last trim — and the retained vertices
        // would sample whatever replaced them. Skipping it costs nothing
        // there: the page is static and the generic warmup pauses during
        // Lifted, so no new glyphs are entering the atlas anyway.
        if self.bubble_reuse_active {
            return;
        }
        self.text.trim();
    }
}
