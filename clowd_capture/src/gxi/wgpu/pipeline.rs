//! Render pipeline construction from [`PipelineDesc`] + the
//! `shader_bindings.rs` slot tables.

use crate::gxi::types::{BlendMode, PipelineDesc, ShaderId, VertexFormat};
use crate::shader_bindings::ResourceKind;

use super::device::Device;
use super::{shaders, MSAA_SAMPLES, SURFACE_FORMAT};

/// A compiled render pipeline (immutable; shareable by reference).
pub struct RenderPipeline {
    pub(super) raw: wgpu::RenderPipeline,
}

impl Device {
    /// Build one render pipeline: shader from the registry (passthrough
    /// DXBC on Windows with the debug-panic / release-WGSL-fallback
    /// machinery, runtime WGSL elsewhere), bind layout from the shader's
    /// binding table, color target = the surface format, triangle list,
    /// MSAA 1, no depth.
    pub fn create_pipeline(&self, desc: &PipelineDesc) -> RenderPipeline {
        let shader = shaders::load_shader(self.raw(), desc.shader);
        let bgl = self.bgl(desc.shader);

        let layout = self
            .raw()
            .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some(desc.label),
                bind_group_layouts: &[Some(&bgl)],
                immediate_size: 0,
            });

        // Built outside the closure: `build_pipeline` may call it twice
        // (once with the passthrough pair, once with the WGSL fallback).
        let attributes: Vec<wgpu::VertexAttribute> = desc
            .vertex
            .map(|v| {
                v.attrs
                    .iter()
                    .map(|a| wgpu::VertexAttribute {
                        format: vertex_format(a.format),
                        offset: a.offset,
                        shader_location: a.location,
                    })
                    .collect()
            })
            .unwrap_or_default();
        let blend = blend_state(desc.blend);

        let raw = shaders::build_pipeline(self.raw(), desc.label, &shader, |shader| {
            let buffers: Vec<Option<wgpu::VertexBufferLayout>> = match desc.vertex {
                Some(v) => vec![Some(wgpu::VertexBufferLayout {
                    array_stride: v.stride,
                    step_mode: wgpu::VertexStepMode::Instance,
                    attributes: &attributes,
                })],
                None => vec![],
            };
            self.raw()
                .create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                    label: Some(desc.label),
                    layout: Some(&layout),
                    vertex: wgpu::VertexState {
                        module: shader.vs(),
                        entry_point: Some("vs_main"),
                        buffers: &buffers,
                        compilation_options: Default::default(),
                    },
                    fragment: Some(wgpu::FragmentState {
                        module: shader.fs(),
                        entry_point: Some("fs_main"),
                        targets: &[Some(wgpu::ColorTargetState {
                            format: SURFACE_FORMAT,
                            blend: Some(blend),
                            write_mask: wgpu::ColorWrites::ALL,
                        })],
                        compilation_options: Default::default(),
                    }),
                    primitive: wgpu::PrimitiveState {
                        topology: wgpu::PrimitiveTopology::TriangleList,
                        ..Default::default()
                    },
                    depth_stencil: None,
                    multisample: wgpu::MultisampleState {
                        count: MSAA_SAMPLES,
                        mask: !0,
                        alpha_to_coverage_enabled: false,
                    },
                    multiview_mask: None,
                    cache: None,
                })
        });
        RenderPipeline {
            raw,
        }
    }
}

/// Derive the wgpu bind group layout from a shader's binding table.
///
/// `min_binding_size` is deliberately `None` (the tables don't carry
/// uniform sizes): wgpu then validates buffer sizes at draw time instead
/// of bind-group creation — identical behavior for correct code, and the
/// d3d11 backend has no equivalent check at all.
pub(super) fn create_bind_group_layout(device: &wgpu::Device, id: ShaderId) -> wgpu::BindGroupLayout {
    let entries: Vec<wgpu::BindGroupLayoutEntry> = id
        .bindings()
        .iter()
        .map(|e| {
            let mut visibility = wgpu::ShaderStages::NONE;
            if e.vertex {
                visibility |= wgpu::ShaderStages::VERTEX;
            }
            if e.fragment {
                visibility |= wgpu::ShaderStages::FRAGMENT;
            }
            let ty = match e.kind {
                ResourceKind::UniformBuffer => wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: None,
                },
                ResourceKind::Texture2D => wgpu::BindingType::Texture {
                    sample_type: wgpu::TextureSampleType::Float {
                        filterable: true,
                    },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled: false,
                },
                ResourceKind::Sampler => wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
            };
            wgpu::BindGroupLayoutEntry {
                binding: e.binding,
                visibility,
                ty,
                count: None,
            }
        })
        .collect();
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some(id.name()),
        entries: &entries,
    })
}

fn vertex_format(f: VertexFormat) -> wgpu::VertexFormat {
    match f {
        VertexFormat::Float32 => wgpu::VertexFormat::Float32,
        VertexFormat::Float32x4 => wgpu::VertexFormat::Float32x4,
        VertexFormat::Sint32x2 => wgpu::VertexFormat::Sint32x2,
        VertexFormat::Uint32 => wgpu::VertexFormat::Uint32,
    }
}

fn blend_state(mode: BlendMode) -> wgpu::BlendState {
    match mode {
        BlendMode::Replace => wgpu::BlendState::REPLACE,
        BlendMode::PremultipliedAlpha => wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::One,
                dst_factor: wgpu::BlendFactor::OneMinusSrcAlpha,
                operation: wgpu::BlendOperation::Add,
            },
            alpha: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::One,
                dst_factor: wgpu::BlendFactor::OneMinusSrcAlpha,
                operation: wgpu::BlendOperation::Add,
            },
        },
        BlendMode::StraightAlpha => wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::SrcAlpha,
                dst_factor: wgpu::BlendFactor::OneMinusSrcAlpha,
                operation: wgpu::BlendOperation::Add,
            },
            alpha: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::One,
                dst_factor: wgpu::BlendFactor::OneMinusSrcAlpha,
                operation: wgpu::BlendOperation::Add,
            },
        },
    }
}
