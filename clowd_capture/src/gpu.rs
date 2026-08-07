use std::sync::Arc;
use std::time::Instant;

use anyhow::Result;

use crate::settings::MemoryHintsMode;
use crate::telemetry::startup::WarmupWorkerTimings;

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
    pub peek_pipeline: wgpu::RenderPipeline,
    pub peek_bgl: wgpu::BindGroupLayout,
}

/// GPU state used during the render loop. Built from `DeviceBundle` once
/// the surface is available. Pipelines, bind-group layouts and the sampler
/// persist across capture cycles; `snapshot` (the whole-desktop texture) is
/// set at the start of each cycle and dropped at its end.
pub struct WindowGpu {
    pub device: wgpu::Device,
    pub queue: wgpu::Queue,
    pub pipeline: wgpu::RenderPipeline,
    pub peek_pipeline: wgpu::RenderPipeline,
    pub peek_bgl: wgpu::BindGroupLayout,
    pub desktop_bgl: wgpu::BindGroupLayout,
    pub desktop_sampler: wgpu::Sampler,
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
    timings: &WarmupWorkerTimings,
) -> Result<DeviceBundle> {
    timings
        .prep_start
        .set_once(t_start.elapsed());

    pollster::block_on(async {
        let (adapter, device, queue, adapter_name) =
            device::request_adapter_device(&instance, adapter_hint, memory_hints, t_start, timings).await?;

        let desktop_bgl = pipeline::create_desktop_bind_group_layout(&device);
        let desktop_sampler = pipeline::create_desktop_sampler(&device);
        let desktop_pipeline = pipeline::create_desktop_pipeline(&device, &desktop_bgl);
        let peek_bgl = pipeline::create_peek_bind_group_layout(&device);
        let peek_pipeline = pipeline::create_peek_pipeline(&device, &peek_bgl);

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
            peek_pipeline,
            peek_bgl,
        })
    })
}

// ── Stage B: upload desktop snapshot texture ─────────────────────────

// ── Surface creation (main thread only) ─────────────────────────────

// ── Assemble final WindowGpu ────────────────────────────────────────

pub fn finalise_window_gpu(bundle: DeviceBundle) -> WindowGpu {
    WindowGpu {
        device: bundle.device,
        queue: bundle.queue,
        pipeline: bundle.desktop_pipeline,
        peek_pipeline: bundle.peek_pipeline,
        peek_bgl: bundle.peek_bgl,
        desktop_bgl: bundle.desktop_bgl,
        desktop_sampler: bundle.desktop_sampler,
        surface_format: SURFACE_FORMAT,
        adapter_name: bundle.adapter_name,
        snapshot: None,
    }
}
