//! Top-level per-monitor UI renderer.
//!
//! One instance per render thread. Every frame the caller first invokes
//! [`UiRenderer::prepare`] to decide what belongs on this monitor and
//! upload all per-frame GPU data (rect instance buffers, glyph
//! shape+atlas), then hands the renderer an open `RenderPass` via
//! [`UiRenderer::draw`]. The two-phase split lets the caller fold the UI
//! draw into the same render pass as the desktop triangle, avoiding an
//! MSAA tile store+load on M1 TBDR between a separate desktop and UI pass.
//!
//! Draw order inside the pass (see [`UiRenderer::draw`] for the OCR
//! bubble sandwich):
//!   1. `lift` pipeline: the OCR scanning sweep.
//!   2. `rect` pipeline, LEADING range: OCR bubble pills + shadows.
//!   3. bubble glyph renderer: the bubbles' recognized-text glyphs.
//!   4. `rect` pipeline, TRAILING range: backgrounds, borders, hint
//!      pills, color swatch.
//!   5. main glyph text: labels, tips body, the "W × H" readout.
//!   6. `egui` painter: the egui-drawn panels, above everything else.

use std::sync::Arc;
use std::time::Instant;

use crate::gxi;
use crate::ui::gpu::area::AreaRenderer;
use crate::ui::gpu::egui_painter::EguiPainter;
use crate::ui::gpu::hints::HintsRenderer;
use crate::ui::gpu::lift::LiftPipeline;
use crate::ui::gpu::ocr_bubbles::OcrBubblesRenderer;
use crate::ui::gpu::rect::{RectInstance, RectPipeline};
use crate::ui::gpu::text::{TextArea, TextStack};
use crate::ui::gpu::tips::TipsRenderer;
use crate::ui::shared::{UiMonitor, UiSharedState};

pub struct UiRenderer {
    rect: RectPipeline,
    egui: EguiPainter,
    lift: LiftPipeline,
    ocr_bubbles: OcrBubblesRenderer,
    text: TextStack,
    area: AreaRenderer,
    hints: HintsRenderer,
    tips: TipsRenderer,
    state: Option<Arc<UiSharedState>>,
    this_monitor: UiMonitor,
    /// Index of this monitor in `UiSharedState.monitors` — the slot this
    /// renderer reads out of the broadcast's per-monitor egui frames.
    monitor_index: usize,
    /// Set by `prepare()`, consumed by `draw()`. `false` when there's
    /// nothing to render (no state yet), so `draw()` becomes a no-op.
    has_prepared: bool,
    /// Set by `prepare()` when at least one text area was shaped — tells
    /// `draw()` whether to issue the main text draw.
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
    egui: EguiPainter,
    lift: LiftPipeline,
}

impl UiPipelines {
    /// Compile the three pipelines concurrently on one shared device.
    ///
    /// `create_pipeline` takes no device-wide lock on either backend
    /// (d3d11's `CreateVertexShader`/`CreateInputLayout` calls are
    /// free-threaded; metal's `newLibraryWithSource`/
    /// `newRenderPipelineState` hold nothing of ours), so the real
    /// serialization point is the platform shader compiler, not this
    /// process. On macOS that means MTLCompilerService's XPC concurrency
    /// (~3-4 in flight) — which is why fanning three compiles out is worth
    /// three threads and fanning out further would not be.
    pub fn build_parallel(device: &gxi::Device) -> Self {
        std::thread::scope(|s| {
            // Deferred-build threads run below normal (the caller already
            // is; spawns do not inherit priority on Windows).
            let rect = s.spawn(|| {
                crate::system::lower_thread_priority();
                RectPipeline::new(device)
            });
            let egui = s.spawn(|| {
                crate::system::lower_thread_priority();
                EguiPainter::new(device)
            });
            // The last compile rides the calling thread: spawning for it
            // would only add a join.
            let lift = LiftPipeline::new(device);
            Self {
                rect: rect.join().expect("ui rect pipeline thread"),
                egui: egui.join().expect("ui egui pipeline thread"),
                lift,
            }
        })
    }
}

/// The text stack plus every component whose construction needs it: they
/// allocate their `CachedBuffer`s out of the font system. Kept together
/// because the `&mut TextStack` borrow makes them inherently sequential,
/// so they are one job for the deferred builder to schedule.
pub struct UiText {
    text: TextStack,
    area: AreaRenderer,
    hints: HintsRenderer,
    tips: TipsRenderer,
}

impl UiText {
    pub fn new(mut text: TextStack) -> Self {
        let area = AreaRenderer::new(&mut text);
        let hints = HintsRenderer::new(&mut text);
        let tips = TipsRenderer::new(&mut text);
        Self {
            text,
            area,
            hints,
            tips,
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
    pub fn from_parts(pipelines: UiPipelines, text: UiText, this_monitor: UiMonitor, monitor_index: usize) -> Self {
        Self {
            rect: pipelines.rect,
            egui: pipelines.egui,
            lift: pipelines.lift,
            ocr_bubbles: OcrBubblesRenderer::new(),
            text: text.text,
            area: text.area,
            hints: text.hints,
            tips: text.tips,
            state: None,
            this_monitor,
            monitor_index,
            has_prepared: false,
            any_text: false,
            any_bubble_text: false,
            bubble_rect_count: 0,
            bubble_static_prepared: false,
            bubble_static_viewport: (0, 0),
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
        // The shaped bubble glyph buffers belong to a dead outcome.
        self.ocr_bubbles.clear();
        self.bubble_static_prepared = false;
        self.start_time = Instant::now();
    }

    /// Stage all per-frame work: component visibility decisions,
    /// rect instance uploads, glyph shape + atlas prep. After
    /// `prepare` returns the caller may open a render pass and invoke
    /// [`UiRenderer::draw`] to issue the UI draw calls into it. Split
    /// from `draw` so the UI can share the same render pass as the
    /// desktop triangle — on M1 TBDR this avoids an MSAA tile
    /// store+load between passes.
    pub fn prepare(&mut self, device: &gxi::Device, queue: &gxi::Queue, viewport_px: (u32, u32)) {
        self.has_prepared = false;
        self.any_text = false;
        self.any_bubble_text = false;
        self.bubble_rect_count = 0;

        let Some(state) = self.state.clone() else {
            return;
        };

        self.text
            .update_viewport(queue, viewport_px.0, viewport_px.1);

        // Pre-size the rect buffer so a typical frame never grows it. What
        // is left on this pipeline after the panel and the debug sparkline
        // moved to egui is the info overlay, the hint pills and the colour
        // swatch — a few dozen rects.
        let mut rect_instances: Vec<RectInstance> = Vec::with_capacity(64);

        // OCR bubbles stage into their OWN rect list: it becomes the
        // LEADING range of the single rect upload below, which is what
        // lets `draw` slip the bubble glyphs between the pills and every
        // other rect (the overlays must cover bubbles, bubbles must cover
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
        // The egui frame for THIS monitor, uploaded here so the vertex,
        // index and texture writes happen while the render pass is still
        // closed. Textures are resynced even when nothing draws, so a
        // panel hidden by the overlay toggle never returns against a stale
        // atlas.
        let egui_frame = state
            .egui
            .get(self.monitor_index)
            .and_then(|f| f.as_deref());
        let b = self.this_monitor.bounds;
        self.egui.prepare(
            device,
            queue,
            viewport_px,
            (b.width() as u32, b.height() as u32),
            egui_frame,
            crate::ui::shared::egui_draw_visible(&state),
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

        // Bubble glyphs ride the dedicated renderer so their draw can be
        // ordered between the two rect ranges — the main renderer below
        // draws last, above the bubbles.
        //
        // Static-Lifted fast path: once the reveal has settled every
        // frame's staging is byte-identical (the animation is a pure
        // clamped function of elapsed time), so the previous frame's
        // prepared vertices are simply re-issued — the glyph renderers
        // retain them — instead of re-shaping and re-uploading the whole
        // page per frame per monitor (a dense page is thousands of glyphs,
        // at up to whatever this monitor's refresh rate is). Correctness
        // leans on two things: the viewport is unchanged, and the glyph
        // atlas never evicts (it only grows in place, or resets — and a
        // reset disarms the path below).
        let bubble_reuse_active = bubbles_at_rest && self.bubble_static_prepared && self.bubble_static_viewport == viewport_px;
        if bubble_reuse_active {
            self.any_bubble_text = true;
        } else {
            let mut bubble_text_areas: Vec<TextArea<'_>> = Vec::with_capacity(16);
            self.ocr_bubbles
                .text_areas(&mut bubble_text_areas);
            self.any_bubble_text = self
                .text
                .prepare_bubbles(device, queue, &bubble_text_areas);
            // Arm the fast path only off a frame that actually staged
            // bubble text at rest; an empty staging (no bubbles reach this
            // monitor) keeps taking the cheap empty prepare instead.
            self.bubble_static_prepared = bubbles_at_rest && self.any_bubble_text;
            self.bubble_static_viewport = viewport_px;
        }

        let mut text_areas: Vec<TextArea<'_>> = Vec::with_capacity(48);
        self.area
            .text_areas(viewport_px, &mut text_areas);
        self.hints
            .text_areas(viewport_px, &mut text_areas);
        self.tips
            .text_areas(viewport_px, &mut text_areas);
        self.any_text = self.text.prepare(device, queue, &text_areas);

        // An atlas cap-hit inside either prepare above cleared the atlas:
        // every retained instance buffer may now reference recycled
        // regions. Disarm the fast path (next frame re-prepares the
        // bubbles) and skip the bubble draw this frame rather than sample
        // stale texels.
        if self.text.take_atlas_reset() {
            self.bubble_static_prepared = false;
            self.any_bubble_text = false;
        }
        self.has_prepared = true;
    }

    pub fn draw(&self, frame: &mut gxi::Frame) {
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
        //   3. bubble glyphs — the recognized text, on its own glyph
        //      renderer precisely so it can be issued here: above its
        //      pill backgrounds, below the other overlays' rects.
        //   4. rect TRAILING range — hint pills, the info overlay:
        //      covers any bubble it overlaps.
        //   5. main glyph text — hint and tips labels, the readout.
        //   6. egui — the egui-drawn panels, above all of it.
        self.lift.draw(frame);
        self.rect
            .draw_range(frame, 0..self.bubble_rect_count);
        if self.any_bubble_text {
            self.text.draw_bubbles(frame);
        }
        self.rect
            .draw_range(frame, self.bubble_rect_count..u32::MAX);
        if self.any_text {
            self.text.draw(frame);
        }
        // Last: the egui panels sit above everything, bubbles included.
        self.egui.draw(frame);
    }
}
