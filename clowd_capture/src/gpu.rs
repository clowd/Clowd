use std::sync::Arc;
use std::time::Instant;

use anyhow::Result;

use crate::gxi::{self, BlendMode, CreateMark, PipelineDesc, ShaderId};
use crate::telemetry::startup::WorkerTimings;

pub mod desktop;
pub mod peek;

/// 80-byte uniform block written once per render-thread startup (UV region,
/// DPI scale, crosshair color) and updated every frame by each render
/// thread (fade factor, cursor position, selection rect, animation time).
/// Five `vec4`s — still 16-byte-aligned and a single cache line on x86_64.
/// The frozen-desktop snapshot uploaded to the GPU at startup. One per
/// render thread — each thread uploads its own copy to its own device.
/// Stage-A output: GPU device + queue + compiled shaders + format-agnostic
/// resources. Created on the render worker thread with no window or surface.
pub struct DeviceBundle {
    pub device: gxi::Device,
    pub queue: gxi::Queue,
    pub adapter_name: String,
    pub desktop_pipeline: gxi::RenderPipeline,
    pub desktop_sampler: gxi::Sampler,
}

/// The peek half of the render state: the pipeline that draws the
/// un-obscured window contents inside the selection.
///
/// Deliberately NOT part of Stage A. Frame 0 cannot draw a peek quad —
/// peeking needs a hovered window, which needs the overlay to already be
/// visible — so compiling `peek.wgsl` before the first present is pure
/// pre-visible tax. It is built alongside the UI stack on the deferred
/// thread (`render::spawn_deferred_stack`) and folded into `WindowGpu`
/// (`WindowGpu::peek`) whenever that build lands — the render loop is
/// already running by then and simply skips the peek quad until it has.
pub struct PeekGpu {
    pub pipeline: gxi::RenderPipeline,
}

pub fn create_peek_gpu(device: &gxi::Device) -> PeekGpu {
    let pipeline = device.create_pipeline(&PipelineDesc {
        label: "peek pipeline",
        shader: ShaderId::Peek,
        vertex: None,
        blend: BlendMode::Replace,
    });
    PeekGpu {
        pipeline,
    }
}

/// GPU state used during the render loop. Assembled from `DeviceBundle`
/// and the uploaded desktop snapshot *after* frame 0 has been presented —
/// the loop is the first thing that can need either.
///
/// `peek` starts out `None`: the peek pipeline is compiled on the deferred
/// thread together with the UI stack, and on a cold start (empty driver
/// shader cache, binary pages not yet resident) that build can outlive the
/// show gate by a wide margin. The loop must not wait for it — a visible
/// overlay whose worker is parked on a join is a frozen desktop with no
/// cursor — so it runs desktop-only until the build lands and then fills
/// this in.
pub struct WindowGpu {
    pub device: gxi::Device,
    pub queue: gxi::Queue,
    pub pipeline: gxi::RenderPipeline,
    pub peek: Option<PeekGpu>,
    #[allow(dead_code)]
    pub adapter_name: String,
    pub snapshot: Option<Arc<desktop::DesktopSnapshot>>,
}

// ── Stage A: device + pipelines (no window needed) ──────────────────

pub fn stage_a_create_device(
    instance: gxi::Instance,
    adapter_hint: Option<(u32, u32)>,
    t_start: Instant,
    timings: &WorkerTimings,
) -> Result<DeviceBundle> {
    timings
        .prep_start
        .set_once(t_start.elapsed());

    let (device, queue) = gxi::Device::create(&instance, adapter_hint, |mark| match mark {
        CreateMark::AdapterSelected => timings
            .prep_adapter
            .set_once(t_start.elapsed()),
        CreateMark::DeviceReady => timings
            .prep_device
            .set_once(t_start.elapsed()),
    })?;
    let adapter_name = device.adapter_name().to_string();

    // Exactly what frame 0 draws and nothing more: one triangle
    // sampling the desktop snapshot. Every other pipeline in the
    // process (peek, the UI stack) is compiled off this path.
    let desktop_sampler = device.create_sampler("desktop snapshot sampler");
    let desktop_pipeline = device.create_pipeline(&PipelineDesc {
        label: "desktop pipeline",
        shader: ShaderId::Desktop,
        vertex: None,
        blend: BlendMode::Replace,
    });

    timings
        .prep_pipelines
        .set_once(t_start.elapsed());

    Ok(DeviceBundle {
        device,
        queue,
        adapter_name,
        desktop_pipeline,
        desktop_sampler,
    })
}

// ── Assemble final WindowGpu ────────────────────────────────────────

pub fn finalize_window_gpu(bundle: DeviceBundle, snapshot: Option<Arc<desktop::DesktopSnapshot>>) -> WindowGpu {
    WindowGpu {
        device: bundle.device,
        queue: bundle.queue,
        pipeline: bundle.desktop_pipeline,
        peek: None,
        adapter_name: bundle.adapter_name,
        snapshot,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Headless smoke test: create a real device on the active backend and
    /// compile every production pipeline. On D3D11 this validates the
    /// precompiled SM 5.0 blobs, the input layouts against each VS
    /// signature, and the blend/rasterizer state setup — the failure modes
    /// that otherwise only surface on the first live overlay frame. Skips
    /// (rather than fails) on machines with no usable GPU so CI runners
    /// without an adapter stay green.
    #[test]
    fn create_device_and_all_pipelines() {
        let instance = gxi::Instance::new();
        let (device, queue) = match gxi::Device::create(&instance, None, |_| {}) {
            Ok(pair) => pair,
            Err(err) => {
                eprintln!("skipping: no usable GPU device ({err:#})");
                return;
            }
        };

        let _desktop = device.create_pipeline(&PipelineDesc {
            label: "desktop pipeline (test)",
            shader: ShaderId::Desktop,
            vertex: None,
            blend: BlendMode::Replace,
        });
        let _peek = create_peek_gpu(&device);
        let _rect = crate::ui::gpu::rect::RectPipeline::new(&device);
        let _icon = crate::ui::gpu::icon::IconPipeline::new(&device);
        let _lift = crate::ui::gpu::lift::LiftPipeline::new(&device);
        let _atlas = crate::ui::gpu::glyph::GlyphAtlas::new(&device);
        let _glyphs = crate::ui::gpu::glyph::GlyphRenderer::new(&device);

        // The bind-group tables the constructors above do NOT build —
        // Desktop (the largest register walk, mixed VS/PS visibility, and
        // the frame-0 critical path), Peek and UiIcon — plus both queue
        // upload paths (write_buffer: d3d11 Map/WRITE_DISCARD incl. its
        // size assert; write_texture: UpdateSubresource with a
        // sub-rectangle D3D11_BOX, the atlas path). On d3d11 this
        // validates the runtime b/t/s register recomputation and
        // per-stage slot split against each table — the exact contract
        // shared with build.rs — not just the three smallest tables.
        use crate::gxi::{BindingRes, TexFormat, TextureDesc};
        let tex_desc = |label| TextureDesc {
            label,
            width: 2,
            height: 2,
            format: TexFormat::Bgra8Unorm,
        };
        let immutable = device.create_texture_with_data(&queue, &tex_desc("smoke immutable tex"), &[0u8; 16]);
        let atlas = device.create_texture(&tex_desc("smoke atlas tex"));
        queue.write_texture(&atlas, (1, 1), (1, 1), &[0u8; 4]);
        let ubo = device.create_uniform_buffer("smoke ubo", 80);
        queue.write_buffer(&ubo, 0, &[0u8; 80]);
        let sampler = device.create_sampler("smoke sampler");
        let _desktop_bg = device.create_bind_group(
            "smoke desktop bind group",
            ShaderId::Desktop,
            &[
                BindingRes::Uniform(&ubo),
                BindingRes::Texture(&immutable),
                BindingRes::Sampler(&sampler),
                BindingRes::Texture(&atlas),
                BindingRes::Texture(&atlas),
            ],
        );
        let _peek_bg = device.create_bind_group(
            "smoke peek bind group",
            ShaderId::Peek,
            &[
                BindingRes::Uniform(&ubo),
                BindingRes::Texture(&immutable),
                BindingRes::Texture(&atlas),
                BindingRes::Sampler(&sampler),
            ],
        );
        let _icon_bg = device.create_bind_group(
            "smoke icon bind group",
            ShaderId::UiIcon,
            &[
                BindingRes::Uniform(&ubo),
                BindingRes::Texture(&atlas),
                BindingRes::Sampler(&sampler),
            ],
        );

        // GPU timing is always on, so `GpuTimings::new` must build on
        // the active backend (headlessly there is no surface to drive a
        // frame through, so constructibility plus an empty poll is what
        // this test can pin).
        let mut timings = gxi::GpuTimings::new(&device, &queue).expect("GpuTimings::new failed on a healthy device");
        assert!(
            timings.poll_completed(&device).is_empty(),
            "no frames were submitted, so no samples can have landed"
        );
    }
}
