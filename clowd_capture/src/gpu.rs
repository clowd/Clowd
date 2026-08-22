use std::sync::Arc;
use std::time::Instant;

use anyhow::Result;

use crate::settings::MemoryHintsMode;
use crate::telemetry::startup::WorkerTimings;

pub mod desktop;
pub mod device;
pub mod peek;
pub mod pipeline;
pub mod shaders;

/// Non-sRGB format used by every pipeline and surface. On DX12 and Metal
/// this is universally supported as a swapchain format. Verified at
/// surface-bind time via an assertion.
pub const SURFACE_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Bgra8Unorm;

/// 80-byte uniform block written once per render-thread startup (UV region,
/// DPI scale, crosshair colour) and updated every frame by each render
/// thread (fade factor, cursor position, selection rect, animation time).
/// Five `vec4`s — still 16-byte-aligned and a single cache line on x86_64.
/// The frozen-desktop snapshot uploaded to the GPU at startup. One per
/// render thread — each thread uploads its own copy to its own device.
/// Stage-A output: GPU device + queue + compiled shaders + format-agnostic
/// resources. Created on the render worker thread with no window or surface.
pub struct DeviceBundle {
    #[allow(dead_code)]
    pub instance: Arc<wgpu::Instance>,
    pub adapter: wgpu::Adapter,
    pub device: wgpu::Device,
    pub queue: wgpu::Queue,
    pub adapter_name: String,
    pub desktop_pipeline: wgpu::RenderPipeline,
    pub desktop_bgl: wgpu::BindGroupLayout,
    pub desktop_sampler: wgpu::Sampler,
}

/// The peek half of the render state: the pipeline that draws the
/// un-obscured window contents inside the selection, plus its layout.
///
/// Deliberately NOT part of Stage A. Frame 0 cannot draw a peek quad —
/// peeking needs a hovered window, which needs the overlay to already be
/// visible — so compiling `peek.wgsl` before the first present is pure
/// pre-visible tax. It is built alongside the UI stack on the deferred
/// thread (`render::spawn_deferred_stack`) and only then folded into
/// `WindowGpu`, which is why `WindowGpu` can keep plain non-optional
/// fields: it does not exist until the peek pipeline does.
pub struct PeekGpu {
    pub pipeline: wgpu::RenderPipeline,
    pub bgl: wgpu::BindGroupLayout,
}

pub fn create_peek_gpu(device: &wgpu::Device) -> PeekGpu {
    let bgl = pipeline::create_peek_bind_group_layout(device);
    let pipeline = pipeline::create_peek_pipeline(device, &bgl);
    PeekGpu {
        pipeline,
        bgl,
    }
}

/// GPU state used during the render loop. Assembled from `DeviceBundle`,
/// `PeekGpu` and the uploaded desktop snapshot *after* frame 0 has been
/// presented — the loop is the first thing that can need any of the three.
pub struct WindowGpu {
    pub device: wgpu::Device,
    pub queue: wgpu::Queue,
    pub pipeline: wgpu::RenderPipeline,
    pub peek_pipeline: wgpu::RenderPipeline,
    pub peek_bgl: wgpu::BindGroupLayout,
    #[allow(dead_code)]
    pub surface_format: wgpu::TextureFormat,
    #[allow(dead_code)]
    pub adapter_name: String,
    pub snapshot: Option<Arc<desktop::DesktopSnapshot>>,
}

// ── Stage A: device + pipelines (no window needed) ──────────────────

pub fn stage_a_create_device(
    instance: Arc<wgpu::Instance>,
    adapter_hint: Option<(u32, u32)>,
    memory_hints: MemoryHintsMode,
    t_start: Instant,
    timings: &WorkerTimings,
) -> Result<DeviceBundle> {
    timings
        .prep_start
        .set_once(t_start.elapsed());

    pollster::block_on(async {
        let (adapter, device, queue, adapter_name) =
            device::request_adapter_device(&instance, adapter_hint, memory_hints, t_start, timings).await?;

        // Exactly what frame 0 draws and nothing more: one triangle
        // sampling the desktop snapshot. Every other pipeline in the
        // process (peek, the UI stack) is compiled off this path.
        let desktop_bgl = pipeline::create_desktop_bind_group_layout(&device);
        let desktop_sampler = pipeline::create_desktop_sampler(&device);
        let desktop_pipeline = pipeline::create_desktop_pipeline(&device, &desktop_bgl);

        timings
            .prep_pipelines
            .set_once(t_start.elapsed());

        Ok(DeviceBundle {
            instance,
            adapter,
            device,
            queue,
            adapter_name,
            desktop_pipeline,
            desktop_bgl,
            desktop_sampler,
        })
    })
}

// ── Stage B: upload desktop snapshot texture ─────────────────────────

// ── Surface creation (main thread only) ─────────────────────────────

// ── Assemble final WindowGpu ────────────────────────────────────────

pub fn finalise_window_gpu(bundle: DeviceBundle, peek: PeekGpu, snapshot: Option<Arc<desktop::DesktopSnapshot>>) -> WindowGpu {
    WindowGpu {
        device: bundle.device,
        queue: bundle.queue,
        pipeline: bundle.desktop_pipeline,
        peek_pipeline: peek.pipeline,
        peek_bgl: peek.bgl,
        surface_format: SURFACE_FORMAT,
        adapter_name: bundle.adapter_name,
        snapshot,
    }
}
