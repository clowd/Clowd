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
//! Draw order inside the pass:
//!   1. `rect` pipeline: backgrounds, borders, shadow, color swatch,
//!      area indicator brackets, label underlines.
//!   2. `svg` pipeline: button icons (lyon-tessellated meshes).
//!   3. glyphon text: labels, tips body, area-indicator digits.

use std::sync::Arc;
use std::time::Instant;

use crate::telemetry::perf::PerfTracker;
use crate::telemetry::startup::{CaptureTimings, WarmupTimings};
use crate::ui::gpu::area::AreaRenderer;
use crate::ui::gpu::debug::DebugRenderer;
use crate::ui::gpu::hints::HintsRenderer;
use crate::ui::gpu::icon::{IconInstance, IconPipeline};
use crate::ui::gpu::panel::PanelRenderer;
use crate::ui::gpu::rect::{RectInstance, RectPipeline};
use crate::ui::gpu::text::TextStack;
use crate::ui::gpu::tips::TipsRenderer;
use crate::ui::shared::{UiMonitor, UiSharedState};

pub struct UiRenderer {
    rect: RectPipeline,
    icon: IconPipeline,
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
    /// never change after startup (monitor name, adapter, warm-up
    /// timings). `perf` is the only live source; see `render()`.
    monitor_name: String,
    adapter_name: String,
    warmup: Arc<WarmupTimings>,
    /// The active cycle's timings, installed by [`begin_cycle`](Self::begin_cycle)
    /// (fresh `Arc` per cycle). The debug panel reads its capture section
    /// from here; `None` only before this worker's first cycle.
    capture: Option<Arc<CaptureTimings>>,
    /// Set by `prepare()`, consumed by `draw()`. `false` when there's
    /// nothing to render (no state yet), so `draw()` becomes a no-op.
    has_prepared: bool,
    /// Set by `prepare()` when at least one text area was shaped — tells
    /// `draw()` whether to issue the glyphon draw.
    any_text: bool,
    /// Animation clock origin for UI effects (border trail).
    start_time: Instant,
}

impl UiRenderer {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        surface_format: wgpu::TextureFormat,
        this_monitor: UiMonitor,
        monitor_index: usize,
        monitor_name: String,
        adapter_name: String,
        warmup: Arc<WarmupTimings>,
    ) -> Self {
        let rect = RectPipeline::new(device, surface_format);
        let icon = IconPipeline::new(device, surface_format);
        warmup.workers[monitor_index]
            .prep_ui_pipelines
            .set_once(warmup.t_start.elapsed());
        let mut text = TextStack::new(device, queue, surface_format);
        warmup.workers[monitor_index]
            .prep_fonts
            .set_once(warmup.t_start.elapsed());
        let area = AreaRenderer::new(&mut text);
        let hints = HintsRenderer::new(&mut text);
        let tips = TipsRenderer::new(&mut text);
        let panel = PanelRenderer::new(&mut text);
        let debug = DebugRenderer::new(monitor_index);
        Self {
            rect,
            icon,
            text,
            area,
            hints,
            tips,
            panel,
            debug,
            last_frame_time: None,
            state: None,
            this_monitor,
            monitor_name,
            adapter_name,
            warmup,
            capture: None,
            has_prepared: false,
            any_text: false,
            start_time: Instant::now(),
        }
    }

    pub fn set_state(&mut self, state: Arc<UiSharedState>) {
        self.state = Some(state);
    }

    /// Reset per-cycle leftovers at `BeginCycle`, before frame 0 is drawn.
    /// `state` is warm (it survives the worker's parked gap between
    /// cycles); without this, frame 0 of the next cycle composites the
    /// previous cycle's UI over the new screenshot — the fresh
    /// `UiSharedState` is only broadcast after the show gate. With no
    /// state, `prepare` stages nothing and frame 0 is the clean initial
    /// overlay. Installs the cycle's fresh timings and re-anchors the
    /// animation clock — an `f32` seconds value that has been running
    /// since warm-up loses enough precision after hours of idling to
    /// visibly quantize the border-trail animation.
    pub fn begin_cycle(&mut self, timings: Arc<CaptureTimings>) {
        self.state = None;
        self.last_frame_time = None;
        self.capture = Some(timings);
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
            &self.warmup,
            self.capture.as_deref(),
            &mut rect_instances,
        );

        let elapsed_secs = self.start_time.elapsed().as_secs_f32();
        self.rect
            .prepare(device, queue, viewport_px, elapsed_secs, &rect_instances);
        if let Some(atlas) = self.panel.atlas() {
            self.icon
                .prepare(device, queue, viewport_px, atlas, &icon_draws);
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
        self.rect.draw(rpass);
        self.icon.draw(rpass);
        if self.any_text {
            if let Err(e) = self.text.draw(rpass) {
                log::warn!("glyphon render error: {:?}", e);
            }
        }
    }

    pub fn trim(&mut self) {
        self.text.trim();
    }
}
