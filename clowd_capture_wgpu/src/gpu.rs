use std::sync::Arc;

use anyhow::Result;
use winit::window::Window;

/// GPU state shared by every render thread. Cheap to clone via `Arc`.
/// The fields are all `Send + Sync` in wgpu 29.
pub struct SharedGpu {
    pub device: wgpu::Device,
    pub queue: wgpu::Queue,
    pub pipeline: wgpu::RenderPipeline,
    pub surface_format: wgpu::TextureFormat,
}

/// Result of `GpuBootstrap::new`. The `instance` is retained on the main
/// thread so additional surfaces can be created for windows 1..N, and the
/// `first_surface` (created against `first_window`) can be handed straight
/// to the first render thread without re-creating it.
pub struct GpuBootstrap {
    pub shared: Arc<SharedGpu>,
    pub instance: wgpu::Instance,
    pub first_surface: wgpu::Surface<'static>,
}

impl GpuBootstrap {
    /// Bootstrap wgpu from the first window. DX12-only and explicitly
    /// configured with `Dx12UseFrameLatencyWaitableObject::Wait` so that
    /// `Surface::get_current_texture()` blocks on DXGI's frame-latency
    /// waitable — the render thread's natural pacing source.
    pub async fn new(first_window: Arc<Window>) -> Result<Self> {
        // Explicit for reader clarity: `Wait` is already the Default, but we
        // pin it so the intent survives future wgpu upgrades.
        let mut backend_options = wgpu::BackendOptions::default();
        backend_options.dx12.latency_waitable_object =
            wgpu::Dx12UseFrameLatencyWaitableObject::Wait;

        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor {
            // We rely on DX12 for the frame-latency waitable object, so
            // force the backend rather than accepting whatever PRIMARY picks.
            backends: wgpu::Backends::DX12,
            backend_options,
            ..wgpu::InstanceDescriptor::new_without_display_handle()
        });

        let first_surface = instance.create_surface(first_window)?;

        let adapter = instance
            .request_adapter(&wgpu::RequestAdapterOptions {
                power_preference: wgpu::PowerPreference::HighPerformance,
                compatible_surface: Some(&first_surface),
                force_fallback_adapter: false,
            })
            .await?;

        let (device, queue) = adapter
            .request_device(&wgpu::DeviceDescriptor {
                label: Some("clowd_capture_wgpu device"),
                required_features: wgpu::Features::empty(),
                required_limits: wgpu::Limits::default(),
                memory_hints: wgpu::MemoryHints::MemoryUsage,
                trace: wgpu::Trace::Off,
                experimental_features: wgpu::ExperimentalFeatures::disabled(),
            })
            .await?;

        let caps = first_surface.get_capabilities(&adapter);
        let surface_format = caps
            .formats
            .iter()
            .copied()
            .find(|f| f.is_srgb())
            .unwrap_or(caps.formats[0]);

        let shader = device.create_shader_module(wgpu::include_wgsl!("shader.wgsl"));
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("triangle pipeline layout"),
            bind_group_layouts: &[],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("triangle pipeline"),
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
                    format: surface_format,
                    blend: Some(wgpu::BlendState::REPLACE),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState::default(),
            multiview_mask: None,
            cache: None,
        });

        Ok(Self {
            shared: Arc::new(SharedGpu {
                device,
                queue,
                pipeline,
                surface_format,
            }),
            instance,
            first_surface,
        })
    }
}
