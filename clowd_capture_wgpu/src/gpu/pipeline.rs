use crate::gpu::desktop::WINDOW_UNIFORMS_SIZE;
use crate::gpu::peek::PEEK_UNIFORMS_SIZE;
use crate::gpu::SURFACE_FORMAT;

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

pub(crate) fn create_desktop_bind_group_layout(device: &wgpu::Device) -> wgpu::BindGroupLayout {
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
            wgpu::BindGroupLayoutEntry {
                binding: 3,
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
                binding: 4,
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
        ],
    })
}

pub(crate) fn create_desktop_sampler(device: &wgpu::Device) -> wgpu::Sampler {
    device.create_sampler(&wgpu::SamplerDescriptor {
        label: Some("desktop snapshot sampler"),
        address_mode_u: wgpu::AddressMode::ClampToEdge,
        address_mode_v: wgpu::AddressMode::ClampToEdge,
        address_mode_w: wgpu::AddressMode::ClampToEdge,
        mag_filter: wgpu::FilterMode::Nearest,
        min_filter: wgpu::FilterMode::Nearest,
        mipmap_filter: wgpu::MipmapFilterMode::Nearest,
        ..Default::default()
    })
}

pub(crate) fn create_desktop_pipeline(device: &wgpu::Device, desktop_bgl: &wgpu::BindGroupLayout) -> wgpu::RenderPipeline {
    #[cfg(windows)]
    let (desktop_vs, desktop_fs) = unsafe {
        (
            create_passthrough_module(device, "desktop VS", compiled_shaders::DESKTOP_VS),
            create_passthrough_module(device, "desktop FS", compiled_shaders::DESKTOP_PS),
        )
    };
    #[cfg(not(windows))]
    let desktop_shader = device.create_shader_module(wgpu::include_wgsl!("../../shaders/desktop.wgsl"));

    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("desktop pipeline layout"),
        bind_group_layouts: &[Some(desktop_bgl)],
        immediate_size: 0,
    });
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
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
    })
}

pub(crate) fn create_peek_bind_group_layout(device: &wgpu::Device) -> wgpu::BindGroupLayout {
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
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
    })
}

pub(crate) fn create_peek_pipeline(device: &wgpu::Device, peek_bgl: &wgpu::BindGroupLayout) -> wgpu::RenderPipeline {
    #[cfg(windows)]
    let (peek_vs, peek_fs) = unsafe {
        (
            create_passthrough_module(device, "peek VS", compiled_shaders::PEEK_VS),
            create_passthrough_module(device, "peek FS", compiled_shaders::PEEK_PS),
        )
    };
    #[cfg(not(windows))]
    let peek_shader = device.create_shader_module(wgpu::include_wgsl!("../../shaders/peek.wgsl"));

    let peek_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("peek pipeline layout"),
        bind_group_layouts: &[Some(peek_bgl)],
        immediate_size: 0,
    });
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
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
    })
}
