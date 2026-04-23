use std::sync::Arc;
use std::time::Instant;

use anyhow::Result;
use winit::window::Window;

use crate::system::CapturedDesktop;
use crate::ui::components::debug::startup::WorkerTimings;

#[cfg(windows)]
mod compiled_shaders {
    pub const DESKTOP_VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/desktop_vs.dxbc"));
    pub const DESKTOP_PS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/desktop_ps.dxbc"));
    pub const PEEK_VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/peek_vs.dxbc"));
    pub const PEEK_PS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/peek_ps.dxbc"));
}

#[cfg(windows)]
pub(crate) unsafe fn create_passthrough_module(device: &wgpu::Device, label: &str, dxbc: &'static [u8]) -> wgpu::ShaderModule {
    unsafe {
        device.create_shader_module_passthrough(wgpu::ShaderModuleDescriptorPassthrough {
            label: Some(label.into()),
            dxil: Some(std::borrow::Cow::Borrowed(dxbc)),
            ..Default::default()
        })
    }
}

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
    pub accent_color: [f32; 4],
    pub selection_rect: [f32; 4],
    pub selection_params: [f32; 4],
}

pub const WINDOW_UNIFORMS_SIZE: u64 = std::mem::size_of::<WindowUniforms>() as u64;

#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct PeekUniforms {
    pub selection_rect: [f32; 4],
    pub window_uv: [f32; 4],
    pub desktop_uv: [f32; 4],
    /// (num_obstruction_rects, ghost_opacity, viewport_w, viewport_h)
    pub params: [f32; 4],
    /// (cursor_x, cursor_y, dpi_scale, 0) in monitor-local pixels
    pub cursor_params: [f32; 4],
    pub obstruction_rects: [[f32; 4]; 16],
}

impl PeekUniforms {
    pub fn zeroed() -> Self {
        bytemuck::Zeroable::zeroed()
    }
}

pub const PEEK_UNIFORMS_SIZE: u64 = std::mem::size_of::<PeekUniforms>() as u64;

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
    pub peek_pipeline: wgpu::RenderPipeline,
    pub peek_bgl: wgpu::BindGroupLayout,
}

/// GPU state used during the render loop. Built from `DeviceBundle` after
/// surface and snapshot are available.
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
    pub snapshot: Option<Arc<DesktopSnapshot>>,
}

// ── Stage A: device + pipelines (no window needed) ──────────────────

pub fn stage_a_create_device(
    instance: Arc<wgpu::Instance>,
    adapter_hint: Option<(u32, u32)>,
    t_start: Instant,
    timings: &WorkerTimings,
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
                info!("adapter hint: vendor=0x{:04X} device=0x{:04X}", vendor, device);
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
        timings
            .prep_adapter
            .set_once(t_start.elapsed());

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
        if crate::ui::gpu::gpu_timing::GPU_TIMING_ENABLED && adapter_features.contains(wgpu::Features::TIMESTAMP_QUERY) {
            required_features |= wgpu::Features::TIMESTAMP_QUERY;
        }
        #[cfg(windows)]
        {
            required_features |= wgpu::Features::PASSTHROUGH_SHADERS;
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

        timings
            .prep_device
            .set_once(t_start.elapsed());

        // Pre-build the desktop snapshot bind-group layout and sampler.
        // These only depend on the Device, not on the actual texture.
        let desktop_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
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
                        sample_type: wgpu::TextureSampleType::Float {
                            filterable: true,
                        },
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
        #[cfg(windows)]
        let (desktop_vs, desktop_fs) = unsafe {
            (
                create_passthrough_module(&device, "desktop VS", compiled_shaders::DESKTOP_VS),
                create_passthrough_module(&device, "desktop FS", compiled_shaders::DESKTOP_PS),
            )
        };
        #[cfg(not(windows))]
        let desktop_shader = device.create_shader_module(wgpu::include_wgsl!("../shaders/desktop.wgsl"));

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("desktop pipeline layout"),
            bind_group_layouts: &[Some(&desktop_bgl)],
            immediate_size: 0,
        });
        let desktop_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("desktop pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                #[cfg(windows)]
                module: &desktop_vs,
                #[cfg(not(windows))]
                module: &desktop_shader,
                entry_point: Some("vs_main"),
                buffers: &[],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                #[cfg(windows)]
                module: &desktop_fs,
                #[cfg(not(windows))]
                module: &desktop_shader,
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

        // Peek window pipeline.
        let peek_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("peek BGL"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::VERTEX_FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: wgpu::BufferSize::new(PEEK_UNIFORMS_SIZE),
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float {
                            filterable: true,
                        },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 2,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float {
                            filterable: true,
                        },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 3,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });

        #[cfg(windows)]
        let (peek_vs, peek_fs) = unsafe {
            (
                create_passthrough_module(&device, "peek VS", compiled_shaders::PEEK_VS),
                create_passthrough_module(&device, "peek FS", compiled_shaders::PEEK_PS),
            )
        };
        #[cfg(not(windows))]
        let peek_shader = device.create_shader_module(wgpu::include_wgsl!("../shaders/peek.wgsl"));

        let peek_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("peek pipeline layout"),
            bind_group_layouts: &[Some(&peek_bgl)],
            immediate_size: 0,
        });
        let peek_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("peek pipeline"),
            layout: Some(&peek_layout),
            vertex: wgpu::VertexState {
                #[cfg(windows)]
                module: &peek_vs,
                #[cfg(not(windows))]
                module: &peek_shader,
                entry_point: Some("vs_main"),
                buffers: &[],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                #[cfg(windows)]
                module: &peek_fs,
                #[cfg(not(windows))]
                module: &peek_shader,
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

        timings
            .prep_pipelines
            .set_once(t_start.elapsed());

        Ok(DeviceBundle {
            instance,
            adapter,
            device,
            queue,
            adapter_name: adapter_info.name.clone(),
            desktop_pipeline,
            desktop_bgl,
            desktop_sampler,
            peek_pipeline,
            peek_bgl,
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
        vdesktop_size: [captured.bounds.width() as f32, captured.bounds.height() as f32],
    }))
}

// ── Surface creation (main thread only) ─────────────────────────────

pub struct SurfaceBundle {
    pub surface: wgpu::Surface<'static>,
    #[cfg(target_os = "macos")]
    pub render_subview: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
}

#[cfg(target_os = "macos")]
pub fn create_surface(
    instance: &wgpu::Instance,
    window: Arc<Window>,
    screenshot_image: Option<core_graphics::image::CGImage>,
) -> Result<SurfaceBundle> {
    use objc2::{MainThreadMarker, MainThreadOnly};
    use objc2_app_kit::{NSAutoresizingMaskOptions, NSView};
    use std::ptr::NonNull;
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};

    let mtm = MainThreadMarker::new().expect("create_surface must be called on the main thread");

    let handle = window.window_handle()?;
    let RawWindowHandle::AppKit(h) = handle.as_raw() else {
        anyhow::bail!("expected AppKit window handle");
    };

    let content_view: &NSView = unsafe { &*(h.ns_view.as_ptr() as *const NSView) };
    let frame = content_view.frame();

    // Background subview: displays the static screenshot so the window
    // opens looking identical to the desktop.
    if let Some(ref cg_image) = screenshot_image {
        let bg_view = NSView::initWithFrame(NSView::alloc(mtm), frame);
        bg_view.setAutoresizingMask(NSAutoresizingMaskOptions::ViewWidthSizable | NSAutoresizingMaskOptions::ViewHeightSizable);
        bg_view.setWantsLayer(true);
        if let Some(layer) = bg_view.layer() {
            unsafe {
                let cg_ptr: *const std::ffi::c_void = *(&*cg_image as *const _ as *const *const std::ffi::c_void);
                layer.setContents(Some(&*(cg_ptr as *const objc2::runtime::AnyObject)));
                layer.setContentsGravity(objc2_quartz_core::kCAGravityResize);
            }
        }
        content_view.addSubview(&bg_view);
    }

    // Render subview: wgpu renders into this, starts invisible.
    let subview = NSView::initWithFrame(NSView::alloc(mtm), frame);
    subview.setAutoresizingMask(NSAutoresizingMaskOptions::ViewWidthSizable | NSAutoresizingMaskOptions::ViewHeightSizable);
    content_view.addSubview(&subview);
    subview.setWantsLayer(true);
    if let Some(layer) = subview.layer() {
        layer.setOpacity(0.0);
    }

    let subview_ptr = NonNull::new(objc2::rc::Retained::as_ptr(&subview) as *mut _).expect("subview pointer is non-null");
    let raw_window_handle = RawWindowHandle::AppKit(winit::raw_window_handle::AppKitWindowHandle::new(subview_ptr));
    let raw_display_handle = winit::raw_window_handle::RawDisplayHandle::AppKit(winit::raw_window_handle::AppKitDisplayHandle::new());

    let surface = unsafe {
        instance.create_surface_unsafe(wgpu::SurfaceTargetUnsafe::RawHandle {
            raw_display_handle: Some(raw_display_handle),
            raw_window_handle,
        })?
    };

    Ok(SurfaceBundle {
        surface,
        render_subview: Some(subview),
    })
}

#[cfg(not(target_os = "macos"))]
pub fn create_surface(instance: &wgpu::Instance, window: Arc<Window>, _screenshot_image: Option<()>) -> Result<SurfaceBundle> {
    Ok(SurfaceBundle {
        surface: instance.create_surface(window)?,
    })
}

// ── Assemble final WindowGpu ────────────────────────────────────────

pub fn finalise_window_gpu(bundle: DeviceBundle, snapshot: Option<Arc<DesktopSnapshot>>) -> WindowGpu {
    WindowGpu {
        device: bundle.device,
        queue: bundle.queue,
        pipeline: bundle.desktop_pipeline,
        peek_pipeline: bundle.peek_pipeline,
        peek_bgl: bundle.peek_bgl,
        surface_format: SURFACE_FORMAT,
        adapter_name: bundle.adapter_name,
        snapshot,
    }
}
