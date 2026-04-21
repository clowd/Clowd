use std::sync::Arc;

use anyhow::Result;
use winit::window::Window;

use crate::system::CapturedDesktop;

/// Non-sRGB format used by every pipeline and surface. On DX12 and Metal
/// this is universally supported as a swapchain format. Verified at
/// surface-bind time via an assertion.
pub const SURFACE_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Bgra8Unorm;

/// 80-byte uniform block written once per render-thread startup (UV region,
/// DPI scale, crosshair colour) and updated every frame by each render
/// thread (fade factor, cursor position, selection rect, animation time).
/// Five `vec4`s — still 16-byte-aligned and a single cache line on x86_64.
#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct WindowUniforms {
    pub uv_offset_scale: [f32; 4],
    pub params: [f32; 4],
    pub crosshair_color: [f32; 4],
    pub selection_rect: [f32; 4],
    pub selection_params: [f32; 4],
}

pub const WINDOW_UNIFORMS_SIZE: u64 = std::mem::size_of::<WindowUniforms>() as u64;

/// The frozen-desktop snapshot uploaded to the GPU at startup. One per
/// render thread — each thread uploads its own copy to its own device.
pub struct DesktopSnapshot {
    #[allow(dead_code)]
    pub texture: wgpu::Texture,
    pub view: wgpu::TextureView,
    pub sampler: wgpu::Sampler,
    pub bind_group_layout: wgpu::BindGroupLayout,
    pub vdesktop_origin: [f32; 2],
    pub vdesktop_size: [f32; 2],
}

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

/// GPU state used during the render loop. Built from `DeviceBundle` after
/// surface and snapshot are available.
pub struct WindowGpu {
    pub device: wgpu::Device,
    pub queue: wgpu::Queue,
    pub pipeline: wgpu::RenderPipeline,
    #[allow(dead_code)]
    pub surface_format: wgpu::TextureFormat,
    #[allow(dead_code)]
    pub adapter_name: String,
    pub snapshot: Option<Arc<DesktopSnapshot>>,
}

// ── Stage A: device + pipelines (no window needed) ──────────────────

pub fn stage_a_create_device(
    instance: Arc<wgpu::Instance>,
    adapter_hint: Option<(u32, u32)>,
) -> Result<DeviceBundle> {
    pollster::block_on(async {
        #[cfg(windows)]
        let backends = wgpu::Backends::DX12;
        #[cfg(target_os = "macos")]
        let backends = wgpu::Backends::METAL;
        #[cfg(not(any(windows, target_os = "macos")))]
        let backends = wgpu::Backends::VULKAN;

        let adapter = match adapter_hint {
            Some((vendor, device)) => {
                info!(
                    "adapter hint: vendor=0x{:04X} device=0x{:04X}",
                    vendor, device
                );
                let adapters = instance.enumerate_adapters(backends).await;
                let matched = adapters
                    .into_iter()
                    .find(|a: &wgpu::Adapter| {
                        let info = a.get_info();
                        info.vendor == vendor && info.device == device
                    });
                if matched.is_some() {
                    info!("matched DXGI adapter hint to wgpu adapter");
                } else {
                    warn!("no wgpu adapter matched DXGI hint; falling back to request_adapter");
                }
                matched
            }
            None => {
                info!("no DXGI adapter hint; using request_adapter fallback");
                None
            }
        };
        let adapter = match adapter {
            Some(a) => a,
            None => {
                instance
                    .request_adapter(&wgpu::RequestAdapterOptions {
                        power_preference: wgpu::PowerPreference::HighPerformance,
                        compatible_surface: None,
                        force_fallback_adapter: false,
                    })
                    .await?
            }
        };
        let adapter_info = adapter.get_info();
        info!(
            "selected adapter: \"{}\" (vendor=0x{:04X} device=0x{:04X} type={:?})",
            adapter_info.name, adapter_info.vendor, adapter_info.device, adapter_info.device_type
        );

        let adapter_limits = adapter.limits();
        let required_limits = wgpu::Limits {
            max_texture_dimension_2d: adapter_limits.max_texture_dimension_2d,
            ..wgpu::Limits::default()
        };

        let adapter_features = adapter.features();
        let mut required_features = wgpu::Features::empty();
        if crate::ui::gpu::gpu_timing::GPU_TIMING_ENABLED
            && adapter_features.contains(wgpu::Features::TIMESTAMP_QUERY)
        {
            required_features |= wgpu::Features::TIMESTAMP_QUERY;
        }

        let (device, queue) = adapter
            .request_device(&wgpu::DeviceDescriptor {
                label: Some("clowd_capture_wgpu device"),
                required_features,
                required_limits,
                memory_hints: wgpu::MemoryHints::MemoryUsage,
                trace: wgpu::Trace::Off,
                experimental_features: wgpu::ExperimentalFeatures::disabled(),
            })
            .await?;

        // Pre-build the desktop snapshot bind-group layout and sampler.
        // These only depend on the Device, not on the actual texture.
        let desktop_bgl =
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("desktop snapshot BGL"),
                entries: &[
                    wgpu::BindGroupLayoutEntry {
                        binding: 0,
                        visibility: wgpu::ShaderStages::VERTEX_FRAGMENT,
                        ty: wgpu::BindingType::Buffer {
                            ty: wgpu::BufferBindingType::Uniform,
                            has_dynamic_offset: false,
                            min_binding_size: wgpu::BufferSize::new(WINDOW_UNIFORMS_SIZE),
                        },
                        count: None,
                    },
                    wgpu::BindGroupLayoutEntry {
                        binding: 1,
                        visibility: wgpu::ShaderStages::FRAGMENT,
                        ty: wgpu::BindingType::Texture {
                            sample_type: wgpu::TextureSampleType::Float { filterable: true },
                            view_dimension: wgpu::TextureViewDimension::D2,
                            multisampled: false,
                        },
                        count: None,
                    },
                    wgpu::BindGroupLayoutEntry {
                        binding: 2,
                        visibility: wgpu::ShaderStages::FRAGMENT,
                        ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                        count: None,
                    },
                ],
            });

        let desktop_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("desktop snapshot sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Nearest,
            min_filter: wgpu::FilterMode::Nearest,
            mipmap_filter: wgpu::MipmapFilterMode::Nearest,
            ..Default::default()
        });

        // Desktop fullscreen-triangle pipeline.
        let shader =
            device.create_shader_module(wgpu::include_wgsl!("../shaders/desktop.wgsl"));
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("desktop pipeline layout"),
            bind_group_layouts: &[Some(&desktop_bgl)],
            immediate_size: 0,
        });
        let desktop_pipeline =
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("desktop pipeline"),
                layout: Some(&layout),
                vertex: wgpu::VertexState {
                    module: &shader,
                    entry_point: Some("vs_main"),
                    buffers: &[],
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module: &shader,
                    entry_point: Some("fs_main"),
                    targets: &[Some(wgpu::ColorTargetState {
                        format: SURFACE_FORMAT,
                        blend: Some(wgpu::BlendState::REPLACE),
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                    compilation_options: Default::default(),
                }),
                primitive: wgpu::PrimitiveState::default(),
                depth_stencil: None,
                multisample: wgpu::MultisampleState {
                    count: crate::render::MSAA_SAMPLES,
                    mask: !0,
                    alpha_to_coverage_enabled: false,
                },
                multiview_mask: None,
                cache: None,
            });

        Ok(DeviceBundle {
            instance,
            adapter,
            device,
            queue,
            adapter_name: adapter_info.name.clone(),
            desktop_pipeline,
            desktop_bgl,
            desktop_sampler,
        })
    })
}

// ── Stage B: upload desktop snapshot texture ─────────────────────────

pub fn stage_b_upload_snapshot(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    captured: &CapturedDesktop,
    bgl: &wgpu::BindGroupLayout,
    sampler: &wgpu::Sampler,
) -> Option<Arc<DesktopSnapshot>> {
    let width = captured.width;
    let height = captured.height;
    let max = device.limits().max_texture_dimension_2d;
    if width > max || height > max {
        error!(
            "virtual desktop {}x{} exceeds max texture dimension {}; skipping snapshot",
            width, height, max
        );
        return None;
    }
    if width == 0 || height == 0 {
        error!("virtual desktop has zero dimension; skipping snapshot");
        return None;
    }

    let size = wgpu::Extent3d {
        width,
        height,
        depth_or_array_layers: 1,
    };
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("desktop snapshot"),
        size,
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::Bgra8Unorm,
        usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });
    queue.write_texture(
        wgpu::TexelCopyTextureInfo {
            texture: &texture,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        &captured.bgra,
        wgpu::TexelCopyBufferLayout {
            offset: 0,
            bytes_per_row: Some(4 * width),
            rows_per_image: Some(height),
        },
        size,
    );
    queue.submit(std::iter::empty());

    let view = texture.create_view(&wgpu::TextureViewDescriptor::default());

    Some(Arc::new(DesktopSnapshot {
        texture,
        view,
        sampler: sampler.clone(),
        bind_group_layout: bgl.clone(),
        vdesktop_origin: [captured.bounds.min_x() as f32, captured.bounds.min_y() as f32],
        vdesktop_size: [
            captured.bounds.width() as f32,
            captured.bounds.height() as f32,
        ],
    }))
}

// ── Surface creation (main thread only) ─────────────────────────────

pub fn create_surface(
    instance: &wgpu::Instance,
    window: Arc<Window>,
) -> Result<wgpu::Surface<'static>> {
    Ok(instance.create_surface(window)?)
}

// ── Assemble final WindowGpu ────────────────────────────────────────

pub fn finalise_window_gpu(
    bundle: DeviceBundle,
    snapshot: Option<Arc<DesktopSnapshot>>,
) -> WindowGpu {
    WindowGpu {
        device: bundle.device,
        queue: bundle.queue,
        pipeline: bundle.desktop_pipeline,
        surface_format: SURFACE_FORMAT,
        adapter_name: bundle.adapter_name,
        snapshot,
    }
}
