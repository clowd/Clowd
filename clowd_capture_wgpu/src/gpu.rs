use std::sync::Arc;

use anyhow::Result;
use winit::window::Window;

use crate::system::CapturedDesktop;

/// 80-byte uniform block written once per render-thread startup (UV region,
/// DPI scale, crosshair colour) and updated every frame by each render
/// thread (fade factor, cursor position, selection rect, animation time).
/// Five `vec4`s — still 16-byte-aligned and a single cache line on x86_64.
#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct WindowUniforms {
    /// xy = UV offset of this monitor in the desktop texture
    /// zw = UV scale of this monitor in the desktop texture
    pub uv_offset_scale: [f32; 4],
    /// x = fade factor in [0, 1]
    /// y = cursor X in window-local physical pixels (out-of-range = cursor
    ///     is on another monitor; the shader's integer-equality test then
    ///     never matches and the vertical crosshair line vanishes here)
    /// z = cursor Y in window-local physical pixels (same convention)
    /// w = this monitor's DPI scale factor (1.0 = 100%, 1.5 = 150%, …);
    ///     used by the shader to size the coloured crosshair arms so
    ///     they stay the same physical size on every display
    pub params: [f32; 4],
    /// RGBA colour (each channel in [0, 1]) used for the coloured
    /// sections of the crosshair — both the inner thin arms and the
    /// outer thick segments, AND the marching-ants dashes on the
    /// selection border. Seeded from `CapturerSettings` at render-
    /// thread startup and currently never updated after that.
    pub crosshair_color: [f32; 4],
    /// Mouse-drag selection rectangle in **window-local physical pixels**
    /// (already transformed from virtual-desktop coords through the same
    /// zoom math the UV pipeline uses, so the rect stays glued to the
    /// selected desktop content under zoom).
    ///   x = left, y = top, z = right, w = bottom
    /// Sentinel for "no selection": `z <= x || w <= y` (the shader treats
    /// any such rect as empty and falls through to the normal grayscale
    /// path). The render thread writes `[0.0, 0.0, -1.0, -1.0]` when
    /// there's no active selection.
    pub selection_rect: [f32; 4],
    /// x = elapsed seconds since the render thread's animation clock
    ///     started (after the first-frame barrier). Drives the
    ///     marching-ants phase on the selection border.
    /// y = `captured` flag as a float (0.0 = not captured, 1.0 =
    ///     selection finalised). When set, the shader stops drawing
    ///     the crosshair and dashed cursor lines entirely so the OS
    ///     cursor takes over.
    /// z = current magnifier zoom level (1.0 .. 256.0). Used by the
    ///     shader to scale the border thickness and dash period by
    ///     `1 / zoom`, mirroring the C++ source's
    ///     `2 / data.zoom` stroke width at
    ///     DxScreenCapture.cpp:644-645.
    /// w = reserved.
    pub selection_params: [f32; 4],
}

pub const WINDOW_UNIFORMS_SIZE: u64 = std::mem::size_of::<WindowUniforms>() as u64;

/// The frozen-desktop snapshot uploaded to the GPU at startup. One per
/// render thread — each thread uploads its own copy to its own device.
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

/// GPU state owned by a single render thread. Each window gets its own
/// device + queue so that swap chain presents are fully independent —
/// the prerequisite for Hardware: Independent Flip on multi-monitor
/// setups. In the C++ version each monitor had its own D3D11 device;
/// this is the wgpu equivalent.
pub struct WindowGpu {
    pub device: wgpu::Device,
    pub queue: wgpu::Queue,
    pub pipeline: wgpu::RenderPipeline,
    pub surface_format: wgpu::TextureFormat,
    /// `None` only if the desktop capture failed or its bitmap is larger
    /// than the adapter's max 2D texture size. The render thread then
    /// falls back to a plain dark clear.
    pub snapshot: Option<Arc<DesktopSnapshot>>,
}

/// Bootstrap a complete, independent wgpu stack for a single window.
/// Called once per render thread so every swap chain gets its own DX12
/// command queue — without this, multiple swap chains sharing a single
/// device degrade from Hardware: Independent Flip to Composed: Flip
/// because the shared command queue serialises their presents.
///
/// `adapter_hint` is the `(vendor_id, device_id)` of the DXGI adapter
/// physically driving this monitor (populated from DXGI output
/// enumeration). When present, we enumerate all wgpu adapters and
/// prefer the one matching those IDs — so multi-GPU setups create
/// each device on the adapter that actually scans out that monitor.
pub fn bootstrap_window_gpu(
    window: Arc<Window>,
    captured: &CapturedDesktop,
    adapter_hint: Option<(u32, u32)>,
) -> Result<(WindowGpu, wgpu::Surface<'static>)> {
    pollster::block_on(async {
        #[allow(unused_mut)]
        let mut backend_options = wgpu::BackendOptions::default();

        #[cfg(windows)]
        {
            backend_options.dx12.latency_waitable_object =
                wgpu::Dx12UseFrameLatencyWaitableObject::Wait;
        }

        #[cfg(windows)]
        let backends = wgpu::Backends::DX12;
        #[cfg(target_os = "macos")]
        let backends = wgpu::Backends::METAL;
        #[cfg(not(any(windows, target_os = "macos")))]
        let backends = wgpu::Backends::VULKAN;

        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor {
            backends,
            backend_options,
            ..wgpu::InstanceDescriptor::new_without_display_handle()
        });

        let surface = instance.create_surface(window)?;

        // Try to pick the adapter that physically drives this monitor.
        // If we have a DXGI adapter hint, enumerate all wgpu adapters and
        // match by PCI vendor + device IDs. Fall back to request_adapter
        // if the hint is missing or no match is found.
        let adapter = match adapter_hint {
            Some((vendor, device)) => {
                info!(
                    "adapter hint: vendor=0x{:04X} device=0x{:04X}",
                    vendor, device
                );
                let adapters = instance
                    .enumerate_adapters(backends)
                    .await;
                let matched = adapters
                    .into_iter()
                    .find(|a: &wgpu::Adapter| {
                        let info = a.get_info();
                        info.vendor == vendor
                            && info.device == device
                            && !surface.get_capabilities(a).formats.is_empty()
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
                        compatible_surface: Some(&surface),
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

        // Prefer a NON-sRGB swapchain format. The snapshot texture is
        // uploaded as raw bytes with no sRGB decoding, and the shader
        // passes those bytes straight through when fade = 0. Going through
        // an sRGB surface would re-encode them on write, and the
        // sRGB-decode-on-sample + sRGB-encode-on-write round trip is not
        // strictly bit-exact at 8-bit precision — that produced a visible
        // colour shift at the moment of the window uncloaking, because the
        // user's eye has a direct "live desktop vs. our render" reference
        // in that instant. Non-sRGB in and non-sRGB out is byte-identical.
        let caps = surface.get_capabilities(&adapter);
        let surface_format = caps
            .formats
            .iter()
            .copied()
            .find(|f| !f.is_srgb())
            .unwrap_or(caps.formats[0]);

        let snapshot = create_desktop_snapshot(&device, &queue, captured);

        let shader = device
            .create_shader_module(wgpu::include_wgsl!("../shaders/desktop.wgsl"));

        let bind_group_layouts: Vec<Option<&wgpu::BindGroupLayout>> = match &snapshot {
            Some(snap) => vec![Some(&snap.bind_group_layout)],
            None => vec![],
        };
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("desktop pipeline layout"),
            bind_group_layouts: &bind_group_layouts,
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
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
                    format: surface_format,
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

        Ok((
            WindowGpu {
                device,
                queue,
                pipeline,
                surface_format,
                snapshot,
            },
            surface,
        ))
    })
}

/// Build the desktop snapshot: upload the captured BGRA bytes to a
/// `Bgra8UnormSrgb` texture (no CPU channel swap — the GPU sampler reorders
/// to RGBA at fetch time, for free), create a linear-clamp sampler, and
/// define the bind group layout that the render pipeline + per-window bind
/// groups will share. Returns `None` if the image is larger than the
/// device's max 2D texture size — caller falls back to no-snapshot mode.
fn create_desktop_snapshot(
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
        // display is scanning out (sRGB-encoded). We deliberately store
        // them as **non-sRGB** `Bgra8Unorm` so that sampling returns the
        // raw byte values (as floats in [0, 1]) with *no* colour-space
        // conversion. Combined with a non-sRGB surface format, this gives
        // a byte-identical pass-through at fade = 0 (no sRGB decode/encode
        // round-trip). The shader decodes sRGB → linear manually when it
        // actually needs linear light (for the grayscale luma math).
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
    // Force the upload to flush now rather than piggybacking on the first
    // render submission. Keeps "startup texture upload latency"
    // attributable to the bootstrap path.
    queue.submit(std::iter::empty());

    let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
    // Nearest filtering on both axes. Zoom is clamped to >= 1.0, so we only
    // ever magnify; nearest keeps individual source pixels crisp under
    // magnification instead of blending them into each other. At zoom == 1
    // the fragment UV lands exactly on a texel centre, so Nearest is
    // bit-identical to Linear for the unzoomed path — no change in the
    // byte-exact window-uncloaking behaviour.
    let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
        label: Some("desktop snapshot sampler"),
        address_mode_u: wgpu::AddressMode::ClampToEdge,
        address_mode_v: wgpu::AddressMode::ClampToEdge,
        address_mode_w: wgpu::AddressMode::ClampToEdge,
        mag_filter: wgpu::FilterMode::Nearest,
        min_filter: wgpu::FilterMode::Nearest,
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
