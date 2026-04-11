use std::sync::Arc;

use anyhow::Result;
use winit::window::Window;

use crate::system::CapturedDesktop;

/// 32-byte uniform block written by the main thread (UV region) and updated
/// every frame by each render thread (fade factor). Two `vec4`s satisfy the
/// WGSL uniform-address-space rule that the struct size be a multiple of 16.
#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct WindowUniforms {
    /// xy = UV offset of this monitor in the desktop texture
    /// zw = UV scale of this monitor in the desktop texture
    pub uv_offset_scale: [f32; 4],
    /// x = fade factor in [0, 1], yzw unused
    pub fade_pad: [f32; 4],
}

pub const WINDOW_UNIFORMS_SIZE: u64 = std::mem::size_of::<WindowUniforms>() as u64;

/// The frozen-desktop snapshot uploaded to the GPU at startup. One per
/// process — every render thread reads from the same `texture`/`view` and
/// shares the same `bind_group_layout`.
pub struct DesktopSnapshot {
    /// Held to keep the GPU texture alive for the lifetime of the snapshot.
    /// Sampling goes through `view`; we never touch `texture` directly
    /// after construction.
    #[allow(dead_code)]
    pub texture: wgpu::Texture,
    pub view: wgpu::TextureView,
    pub sampler: wgpu::Sampler,
    pub bind_group_layout: wgpu::BindGroupLayout,
    /// Top-left of the virtual desktop in screen coordinates (can be
    /// negative when secondary monitors extend left/up of the primary).
    pub vdesktop_origin: [f32; 2],
    /// Width/height of the virtual desktop in pixels (also = the texture
    /// size, since one texel = one screen pixel).
    pub vdesktop_size: [f32; 2],
}

/// GPU state shared by every render thread. Cheap to clone via `Arc`.
/// The fields are all `Send + Sync` in wgpu 29.
pub struct SharedGpu {
    pub device: wgpu::Device,
    pub queue: wgpu::Queue,
    pub pipeline: wgpu::RenderPipeline,
    pub surface_format: wgpu::TextureFormat,
    /// `None` only if the desktop capture failed or its bitmap is larger
    /// than the adapter's max 2D texture size. The render threads then fall
    /// back to a plain dark clear.
    pub snapshot: Option<Arc<DesktopSnapshot>>,
}

/// Phase 1 of GPU bootstrap: instance / adapter / device / queue / first
/// surface. We need the device + queue to upload the desktop snapshot, but
/// we can't create the render pipeline until we know whether the snapshot
/// exists (because the pipeline layout depends on the snapshot's bind group
/// layout). So bootstrap is split: build a `GpuCore`, optionally build a
/// snapshot from it, then call `finalize` to produce the final `SharedGpu`.
pub struct GpuCore {
    pub device: wgpu::Device,
    pub queue: wgpu::Queue,
    pub instance: wgpu::Instance,
    pub first_surface: wgpu::Surface<'static>,
    pub surface_format: wgpu::TextureFormat,
}

/// Result of `GpuCore::finalize`. The `instance` is retained on the main
/// thread so additional surfaces can be created for windows 1..N, and the
/// `first_surface` (created against `first_window`) can be handed straight
/// to the first render thread without re-creating it.
pub struct GpuBootstrap {
    pub shared: Arc<SharedGpu>,
    pub instance: wgpu::Instance,
    pub first_surface: wgpu::Surface<'static>,
}

impl GpuCore {
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

        // Bump max_texture_dimension_2d to whatever the adapter supports
        // (DX12 reports 16384, vs. the default 8192). Multi-monitor virtual
        // desktops can easily exceed 8192 wide.
        let adapter_limits = adapter.limits();
        let required_limits = wgpu::Limits {
            max_texture_dimension_2d: adapter_limits.max_texture_dimension_2d,
            ..wgpu::Limits::default()
        };

        let (device, queue) = adapter
            .request_device(&wgpu::DeviceDescriptor {
                label: Some("clowd_capture_wgpu device"),
                required_features: wgpu::Features::empty(),
                required_limits,
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

        Ok(Self {
            device,
            queue,
            instance,
            first_surface,
            surface_format,
        })
    }

    /// Build the render pipeline (against the snapshot's BGL if present)
    /// and assemble the shared GPU state. Consumes the core.
    pub fn finalize(self, snapshot: Option<Arc<DesktopSnapshot>>) -> GpuBootstrap {
        let shader = self
            .device
            .create_shader_module(wgpu::include_wgsl!("shader.wgsl"));

        // The pipeline must reference whichever bind groups the shader
        // expects. With a snapshot we use the snapshot's BGL (binding 0/1/2
        // = uniform/texture/sampler). Without a snapshot the render threads
        // skip the draw entirely and rely on the clear colour, so the
        // pipeline can be built with no bind groups — but it still has to
        // be a valid pipeline because the render thread reaches `set_pipeline`
        // unconditionally.
        let bind_group_layouts: Vec<Option<&wgpu::BindGroupLayout>> = match &snapshot {
            Some(snap) => vec![Some(&snap.bind_group_layout)],
            None => vec![],
        };
        let layout = self
            .device
            .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("desktop pipeline layout"),
                bind_group_layouts: &bind_group_layouts,
                immediate_size: 0,
            });
        let pipeline = self
            .device
            .create_render_pipeline(&wgpu::RenderPipelineDescriptor {
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
                        format: self.surface_format,
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

        GpuBootstrap {
            shared: Arc::new(SharedGpu {
                device: self.device,
                queue: self.queue,
                pipeline,
                surface_format: self.surface_format,
                snapshot,
            }),
            instance: self.instance,
            first_surface: self.first_surface,
        }
    }
}

/// Build the desktop snapshot: upload the captured BGRA bytes to a
/// `Bgra8UnormSrgb` texture (no CPU channel swap — the GPU sampler reorders
/// to RGBA at fetch time, for free), create a linear-clamp sampler, and
/// define the bind group layout that the render pipeline + per-window bind
/// groups will share. Returns `None` if the image is larger than the
/// device's max 2D texture size — caller falls back to no-snapshot mode.
pub fn create_desktop_snapshot(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    captured: &CapturedDesktop,
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
        // GetDIBits hands us raw BGRA bytes that are exactly what the
        // display was scanning out — i.e. sRGB-encoded. Sampling through
        // Bgra8UnormSrgb decodes to linear *and* swizzles BGRA→RGBA in the
        // texture unit, so the shader sees plain `(R, G, B, A)` linear.
        // Writing to our sRGB swapchain re-encodes on the way out, giving
        // a pixel-identical round-trip when fade = 0.
        format: wgpu::TextureFormat::Bgra8UnormSrgb,
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
    // Force the upload to flush now rather than piggybacking on the first
    // render thread's submission. Keeps "startup texture upload latency"
    // attributable to the main thread.
    queue.submit(std::iter::empty());

    let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
    let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
        label: Some("desktop snapshot sampler"),
        address_mode_u: wgpu::AddressMode::ClampToEdge,
        address_mode_v: wgpu::AddressMode::ClampToEdge,
        address_mode_w: wgpu::AddressMode::ClampToEdge,
        mag_filter: wgpu::FilterMode::Linear,
        min_filter: wgpu::FilterMode::Linear,
        mipmap_filter: wgpu::MipmapFilterMode::Nearest,
        ..Default::default()
    });
    let bind_group_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
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

    Some(Arc::new(DesktopSnapshot {
        texture,
        view,
        sampler,
        bind_group_layout,
        vdesktop_origin: [
            captured.bounds.min_x() as f32,
            captured.bounds.min_y() as f32,
        ],
        vdesktop_size: [
            captured.bounds.width() as f32,
            captured.bounds.height() as f32,
        ],
    }))
}
