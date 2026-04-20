//! Top-level per-monitor UI renderer.
//!
//! One instance per render thread. Every frame `render()` decides which
//! components belong on THIS monitor using the shared visibility rules
//! and draws them directly on the GPU — no CPU rasterisation.
//!
//! Pass structure per frame (single render pass, Load + Store):
//!   1. `rect` pipeline: backgrounds, borders, shadow, color swatch,
//!      area indicator brackets, label underlines.
//!   2. `svg` pipeline: button icons (lyon-tessellated meshes).
//!   3. glyphon text: labels, tips body, area-indicator digits.

use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant};

use crate::ui::components::debug::perf::PerfTracker;
use crate::ui::components::debug::startup::StartupTimings;
use crate::ui::gpu::debug::DebugRenderer;
use crate::ui::gpu::panel::PanelRenderer;
use crate::ui::gpu::rect::{RectInstance, RectPipeline};
use crate::ui::gpu::svg::{SvgInstance, SvgPipeline};
use crate::ui::gpu::text::TextStack;
use crate::ui::gpu::tips::TipsRenderer;
use crate::ui::shared::{UiMonitor, UiSharedState};

pub struct UiRenderer {
    rect: RectPipeline,
    svg: SvgPipeline,
    text: TextStack,
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
    /// Shared one-shot holding the "all windows visible" timestamp
    /// (offset from `startup.t_start`). The main thread sets it the
    /// instant `show_windows_atomically` returns; every render thread's
    /// debug panel reads it. Remains `None` until that moment.
    shown_time: Arc<OnceLock<Duration>>,
    /// Offset from `startup.t_start` at which THIS display rendered its
    /// first visible (post-barrier) frame. Captured in
    /// `mark_first_visible_frame` on the render thread, after the
    /// visible barrier releases. `None` until then.
    time_to_first_render: Option<Duration>,
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
        startup: Arc<StartupTimings>,
        shown_time: Arc<OnceLock<Duration>>,
    ) -> Self {
        let rect = RectPipeline::new(device, surface_format);
        let svg = SvgPipeline::new(device, surface_format);
        let mut text = TextStack::new(device, queue, surface_format);
        let tips = TipsRenderer::new(&mut text);
        let panel = PanelRenderer::new(device, &svg, &mut text);
        let debug = DebugRenderer::new(monitor_index);
        Self {
            rect,
            svg,
            text,
            tips,
            panel,
            debug,
            last_frame_time: None,
            state: None,
            this_monitor,
            monitor_name,
            adapter_name,
            startup,
            shown_time,
            time_to_first_render: None,
        }
    }

    pub fn set_state(&mut self, state: Arc<UiSharedState>) {
        self.state = Some(state);
    }

    /// Record the wall-clock offset (from `startup.t_start`) at which
    /// this display rendered its first visible frame. Call exactly once
    /// per render thread, after the visible barrier releases. Subsequent
    /// calls are no-ops so the first-frame time stays captured.
    pub fn mark_first_visible_frame(&mut self) {
        self.time_to_first_render
            .get_or_insert_with(|| self.startup.t_start.elapsed());
    }

    #[allow(clippy::too_many_arguments)]
    pub fn render(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        msaa_view: &wgpu::TextureView,
        resolve_view: &wgpu::TextureView,
        viewport_px: (u32, u32),
        perf: &PerfTracker,
        timestamp_writes: Option<wgpu::RenderPassTimestampWrites<'_>>,
    ) {
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
        let mut svg_draws: Vec<(usize, SvgInstance)> = Vec::with_capacity(16);

        self.tips
            .prepare(&mut self.text, &state, &self.this_monitor, &mut rect_instances);
        self.panel
            .prepare(&mut self.text, &state, &self.this_monitor, &mut rect_instances, &mut svg_draws, dt);
        self.debug.prepare(
            &mut self.text,
            &state,
            &self.this_monitor,
            &self.monitor_name,
            &self.adapter_name,
            perf,
            &self.startup,
            self.shown_time.get().copied(),
            self.time_to_first_render,
            &mut rect_instances,
        );

        self.rect
            .prepare(device, queue, viewport_px, &rect_instances);
        self.svg
            .prepare(device, queue, viewport_px, &svg_draws);

        // Gather text areas. Panel must come before tips so the overlap
        // case (shouldn't happen, but harmless) has panel-on-top ordering
        // matching the rect order.
        let mut text_areas = self.tips.text_areas(viewport_px);
        text_areas.extend(self.panel.text_areas(viewport_px));
        text_areas.extend(self.debug.text_areas(viewport_px));
        let any_text = match self.text.prepare(device, queue, text_areas) {
            Ok(b) => b,
            Err(e) => {
                log::warn!("glyphon prepare error: {:?}", e);
                false
            }
        };

        let mut rpass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("ui pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: msaa_view,
                resolve_target: Some(resolve_view),
                depth_slice: None,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            timestamp_writes,
            occlusion_query_set: None,
            multiview_mask: None,
        });

        self.rect.draw(&mut rpass);
        self.svg.draw(&mut rpass, &self.panel.icons);
        if any_text {
            if let Err(e) = self.text.draw(&mut rpass) {
                log::warn!("glyphon render error: {:?}", e);
            }
        }
        drop(rpass);

        self.text.trim();
    }
}
