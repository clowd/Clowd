use std::time::{Duration, Instant};

use crate::gpu::WindowGpu;
use crate::render::desktop::SnapshotState;
use crate::telemetry::perf::{PerfSample, PerfTracker};
use crate::ui::gpu::gpu_timing::GpuTimings;
use crate::ui::gpu::UiRenderer;

#[allow(clippy::too_many_arguments)]
pub(crate) fn draw_once(
    surface: &wgpu::Surface<'static>,
    gpu: &WindowGpu,
    config: &wgpu::SurfaceConfiguration,
    snapshot_state: Option<&SnapshotState>,
    peek_bind_group: Option<&wgpu::BindGroup>,
    ui_renderer: &mut UiRenderer,
    perf: &PerfTracker,
    gpu_timing: Option<&GpuTimings>,
    out_sample: &mut Option<PerfSample>,
) {
    let t_wait_start = Instant::now();
    let frame = match surface.get_current_texture() {
        wgpu::CurrentSurfaceTexture::Success(f) | wgpu::CurrentSurfaceTexture::Suboptimal(f) => f,
        wgpu::CurrentSurfaceTexture::Timeout | wgpu::CurrentSurfaceTexture::Occluded => return,
        wgpu::CurrentSurfaceTexture::Outdated | wgpu::CurrentSurfaceTexture::Lost => {
            surface.configure(&gpu.device, config);
            return;
        }
        wgpu::CurrentSurfaceTexture::Validation => return,
    };
    let wait = t_wait_start.elapsed();

    let t_draw_start = Instant::now();
    let view = frame
        .texture
        .create_view(&wgpu::TextureViewDescriptor::default());
    let mut encoder = gpu
        .device
        .create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("frame encoder"),
        });

    ui_renderer.prepare(&gpu.device, &gpu.queue, (config.width, config.height), perf);

    let begin_frame = gpu_timing.and_then(|gt| gt.begin_frame());
    let (pass_ts, slot_id) = match &begin_frame {
        Some(bf) => (Some(bf.pass.clone()), Some(bf.id)),
        None => (None, None),
    };

    {
        let mut rpass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("frame pass"),
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
            timestamp_writes: pass_ts,
            occlusion_query_set: None,
            multiview_mask: None,
        });
        rpass.set_pipeline(&gpu.pipeline);
        if let Some(state) = snapshot_state {
            rpass.set_bind_group(0, &state.bind_group, &[]);
            rpass.draw(0..3, 0..1);
        }
        if let Some(peek_bg) = peek_bind_group {
            rpass.set_pipeline(&gpu.peek_pipeline);
            rpass.set_bind_group(0, peek_bg, &[]);
            rpass.draw(0..6, 0..1);
        }
        ui_renderer.draw(&mut rpass);
    }

    if let (Some(gt), Some(id)) = (gpu_timing, slot_id) {
        gt.resolve(&mut encoder, id);
    }

    gpu.queue
        .submit(std::iter::once(encoder.finish()));
    if let (Some(gt), Some(id)) = (gpu_timing, slot_id) {
        gt.after_submit(id);
    }
    ui_renderer.trim();
    let draw = t_draw_start.elapsed();

    let t_present_start = Instant::now();
    frame.present();
    let present = t_present_start.elapsed();

    *out_sample = Some(PerfSample {
        wait,
        draw,
        present,
        overall: Duration::ZERO,
        gpu: None,
    });
}
