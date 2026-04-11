use std::sync::Arc;

use winit::dpi::PhysicalSize;
use winit::window::Window;

use crate::gpu::GpuContext;

/// Outcome of a single render pass, used by `app.rs` to decide whether to
/// reconfigure the surface, drop the frame, or exit entirely.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RenderOutcome {
    /// Frame was presented successfully.
    Presented,
    /// Surface reported `Timeout` / `Occluded` — skip this frame, try again later.
    Skipped,
    /// Surface needs to be reconfigured before the next attempt.
    NeedsReconfigure,
}

/// Per-window wgpu state: the `Arc<Window>`, its `Surface`, and the
/// `SurfaceConfiguration` currently applied.
pub struct WindowState {
    pub window: Arc<Window>,
    pub surface: wgpu::Surface<'static>,
    pub config: wgpu::SurfaceConfiguration,
    pub size: PhysicalSize<u32>,
}

impl WindowState {
    pub fn new(window: Arc<Window>, surface: wgpu::Surface<'static>, gpu: &GpuContext) -> Self {
        let size = window.inner_size();
        let config = wgpu::SurfaceConfiguration {
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            format: gpu.surface_format,
            width: size.width.max(1),
            height: size.height.max(1),
            present_mode: wgpu::PresentMode::AutoVsync,
            alpha_mode: wgpu::CompositeAlphaMode::Auto,
            view_formats: vec![],
            desired_maximum_frame_latency: 2,
        };
        surface.configure(&gpu.device, &config);
        Self {
            window,
            surface,
            config,
            size,
        }
    }

    pub fn reconfigure(&self, gpu: &GpuContext) {
        self.surface.configure(&gpu.device, &self.config);
    }

    pub fn resize(&mut self, gpu: &GpuContext, new_size: PhysicalSize<u32>) {
        if new_size.width == 0 || new_size.height == 0 {
            return;
        }
        self.size = new_size;
        self.config.width = new_size.width;
        self.config.height = new_size.height;
        self.surface.configure(&gpu.device, &self.config);
    }

    /// Render one frame. Returns a `RenderOutcome` describing what happened so
    /// the caller can decide whether to reconfigure or retry.
    pub fn render(&mut self, gpu: &GpuContext) -> RenderOutcome {
        let frame = match self.surface.get_current_texture() {
            wgpu::CurrentSurfaceTexture::Success(frame)
            | wgpu::CurrentSurfaceTexture::Suboptimal(frame) => frame,
            wgpu::CurrentSurfaceTexture::Timeout | wgpu::CurrentSurfaceTexture::Occluded => {
                return RenderOutcome::Skipped;
            }
            wgpu::CurrentSurfaceTexture::Outdated
            | wgpu::CurrentSurfaceTexture::Lost
            | wgpu::CurrentSurfaceTexture::Validation => {
                return RenderOutcome::NeedsReconfigure;
            }
        };

        let view = frame
            .texture
            .create_view(&wgpu::TextureViewDescriptor::default());
        let mut encoder = gpu
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("frame encoder"),
            });
        {
            let mut rpass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("triangle pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &view,
                    resolve_target: None,
                    depth_slice: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color {
                            r: 0.05,
                            g: 0.05,
                            b: 0.08,
                            a: 1.0,
                        }),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
                multiview_mask: None,
            });
            rpass.set_pipeline(&gpu.pipeline);
            rpass.draw(0..3, 0..1);
        }

        self.window.pre_present_notify();
        gpu.queue.submit(std::iter::once(encoder.finish()));
        frame.present();
        RenderOutcome::Presented
    }
}
