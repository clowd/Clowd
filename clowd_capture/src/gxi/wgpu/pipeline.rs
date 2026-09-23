//! Render pipeline construction from [`PipelineDesc`] + the
//! `shader_bindings.rs` binding tables, for the wgpu backend.
//!
//! A wgpu pipeline bakes in the shader pair, the vertex layout, the
//! blend state and the fixed-function rasterizer state, so like Metal
//! there is no bundle of loose objects — [`RenderPipeline`] wraps the one
//! `wgpu::RenderPipeline`.

use std::borrow::Cow;

use crate::gxi::types::{BlendMode, PipelineDesc, ShaderId, VertexFormat, VertexStep};
use crate::shader_bindings::ResourceKind;

use super::device::Device;
use super::{shaders, SURFACE_FORMAT};

/// A compiled render pipeline (immutable; shareable by reference).
pub struct RenderPipeline {
    pub(super) raw: wgpu::RenderPipeline,
}

impl Device {
    /// Build one render pipeline: shader module compiled from the embedded
    /// WGSL (`shaders.rs`), bind layout from the shader's binding table,
    /// vertex layout from the `PipelineDesc`, color target = the surface
    /// format, triangle list, cull none / CCW front, MSAA 1, no depth.
    ///
    /// Failure PANICS in every build, matching the other backends: the
    /// WGSL is validated against the binding tables by `build.rs`, so a
    /// module or pipeline the driver rejects can only mean a broken build
    /// or a driver bug — never a legitimate runtime condition. wgpu
    /// reports such failures through the device's error sink rather than
    /// a return value, so creation runs inside an error scope
    /// ([`Device::scoped`]) that turns them into the panic. Containment
    /// depends on the call site: the desktop pipeline (worker Stage A)
    /// panics with the fail guard armed and rides `ReadyGuard` →
    /// `failed_count` → show gate → the shell's error dialog; the other
    /// pipelines (peek + the UI stack) are built on the deferred builder
    /// thread, whose panic is absorbed in `render.rs` — that monitor keeps
    /// a desktop-only overlay, no failure is counted, and the only
    /// evidence is the log.
    pub fn create_pipeline(&self, desc: &PipelineDesc) -> RenderPipeline {
        let src = shaders::source(desc.shader);
        let bgl = self.bgl(desc.shader);

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
        // `None` for the fullscreen-triangle passes (desktop, peek), which
        // are driven by `@builtin(vertex_index)` alone.
        let buffers: Vec<Option<wgpu::VertexBufferLayout>> = desc
            .vertex
            .map(|v| {
                vec![Some(wgpu::VertexBufferLayout {
                    array_stride: v.stride,
                    step_mode: match v.step {
                        VertexStep::Vertex => wgpu::VertexStepMode::Vertex,
                    },
                    attributes: &attributes,
                })]
            })
            .unwrap_or_default();
        let blend = blend_state(desc.blend);

        let raw = self
            .scoped(
                &[
                    wgpu::ErrorFilter::Validation,
                    wgpu::ErrorFilter::Internal,
                    wgpu::ErrorFilter::OutOfMemory,
                ],
                || {
                    let module = self
                        .raw()
                        .create_shader_module(wgpu::ShaderModuleDescriptor {
                            label: Some(src.label),
                            source: wgpu::ShaderSource::Wgsl(Cow::Borrowed(src.wgsl)),
                        });
                    let layout = self
                        .raw()
                        .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                            label: Some(desc.label),
                            bind_group_layouts: &[Some(&bgl)],
                            immediate_size: 0,
                        });
                    self.raw()
                        .create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                            label: Some(desc.label),
                            layout: Some(&layout),
                            vertex: wgpu::VertexState {
                                module: &module,
                                entry_point: Some("vs_main"),
                                buffers: &buffers,
                                compilation_options: Default::default(),
                            },
                            fragment: Some(wgpu::FragmentState {
                                module: &module,
                                entry_point: Some("fs_main"),
                                targets: &[Some(wgpu::ColorTargetState {
                                    format: SURFACE_FORMAT,
                                    blend: Some(blend),
                                    write_mask: wgpu::ColorWrites::ALL,
                                })],
                                compilation_options: Default::default(),
                            }),
                            // Cull none + CCW front are wgpu's defaults —
                            // the convention every pipeline in the crate
                            // relies on (the d3d11 backend has to set them
                            // explicitly).
                            primitive: wgpu::PrimitiveState {
                                topology: wgpu::PrimitiveTopology::TriangleList,
                                ..Default::default()
                            },
                            depth_stencil: None,
                            // No multisampling: all UI geometry is
                            // axis-aligned, so MSAA adds cost without
                            // visual benefit.
                            multisample: wgpu::MultisampleState {
                                count: 1,
                                mask: !0,
                                alpha_to_coverage_enabled: false,
                            },
                            multiview_mask: None,
                            cache: None,
                        })
                },
            )
            .unwrap_or_else(|e| panic!("pipeline '{}': shader '{}' rejected: {e}", desc.label, src.label));

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
/// other backends have no equivalent check at all.
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
        VertexFormat::Float32x2 => wgpu::VertexFormat::Float32x2,
        VertexFormat::Uint32 => wgpu::VertexFormat::Uint32,
    }
}

fn blend_state(mode: BlendMode) -> wgpu::BlendState {
    match mode {
        // No blending (desktop and peek own every pixel).
        BlendMode::Replace => wgpu::BlendState::REPLACE,
        // Source-over with premultiplied source, both channels.
        BlendMode::PremultipliedAlpha => wgpu::BlendState::PREMULTIPLIED_ALPHA_BLENDING,
    }
}
