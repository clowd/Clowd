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

use std::sync::Arc;
use std::time::Instant;

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
    last_frame_time: Option<Instant>,
    state: Option<Arc<UiSharedState>>,
    this_monitor: UiMonitor,
}

impl UiRenderer {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        surface_format: wgpu::TextureFormat,
        this_monitor: UiMonitor,
    ) -> Self {
        let rect = RectPipeline::new(device, surface_format);
        let svg = SvgPipeline::new(device, surface_format);
        let mut text = TextStack::new(device, queue, surface_format);
        let tips = TipsRenderer::new(&mut text);
        let panel = PanelRenderer::new(device, &svg, &mut text);
        Self {
            rect,
            svg,
            text,
            tips,
            panel,
            last_frame_time: None,
            state: None,
            this_monitor,
        }
    }

    pub fn set_state(&mut self, state: Arc<UiSharedState>) {
        self.state = Some(state);
    }

    pub fn render(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        target: &wgpu::TextureView,
        viewport_px: (u32, u32),
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

        let mut rect_instances: Vec<RectInstance> = Vec::new();
        let mut svg_draws: Vec<(usize, SvgInstance)> = Vec::new();

        self.tips
            .prepare(&mut self.text, &state, &self.this_monitor, &mut rect_instances);
        self.panel.prepare(
            &mut self.text,
            &state,
            &self.this_monitor,
            &mut rect_instances,
            &mut svg_draws,
            dt,
        );

        self.rect
            .prepare(device, queue, viewport_px, &rect_instances);
        self.svg.prepare(device, queue, viewport_px, &svg_draws);

        // Gather text areas. Panel must come before tips so the overlap
        // case (shouldn't happen, but harmless) has panel-on-top ordering
        // matching the rect order.
        let mut text_areas = self.tips.text_areas(viewport_px);
        text_areas.extend(self.panel.text_areas(viewport_px));
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
                view: target,
                resolve_target: None,
                depth_slice: None,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            timestamp_writes: None,
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
