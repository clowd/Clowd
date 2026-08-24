//! Instanced solid/bordered rect pipeline.
//!
//! One draw call per frame issues N quads from a single instance buffer.
//! Each instance carries destination in window-local physical pixels, a
//! straight-alpha fill color, an optional border color + width, and a
//! lighten-toward-white amount (for hover effects). Shader output is
//! premultiplied; pair with the standard source-over blend state.

use bytemuck::{Pod, Zeroable};

/// One rect to draw.
///
/// `dest_px` is `(min_x, min_y, max_x, max_y)` in window-local physical
/// pixels. `params` is `(border_px, lighten, corner_radius, _)`. Set
/// `border_rgba.a` to 0 OR `params.x` to 0 to disable the border.
/// `corner_radius` > 0 enables SDF-based rounded corners with AA.
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default)]
pub struct RectInstance {
    pub dest_px: [f32; 4],
    pub fill_rgba: [f32; 4],
    pub border_rgba: [f32; 4],
    pub params: [f32; 4],
}

impl RectInstance {
    /// Simple filled rect with no border, no hover lighten.
    pub fn filled(min_x: f32, min_y: f32, max_x: f32, max_y: f32, rgba: [f32; 4]) -> Self {
        Self {
            dest_px: [min_x, min_y, max_x, max_y],
            fill_rgba: rgba,
            border_rgba: [0.0; 4],
            params: [0.0; 4],
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct RectUniforms {
    viewport_px: [f32; 2],
    elapsed_secs: f32,
    _pad: f32,
}

/// Minimum instance-buffer capacity. The buffer grows by doubling when a
/// frame exceeds it; it never shrinks (keeps allocations stable across
/// frames with transient spikes).
const INITIAL_INSTANCE_CAPACITY: u64 = 64;

pub struct RectPipeline {
    pipeline: wgpu::RenderPipeline,
    bind_group: wgpu::BindGroup,
    uniform_buf: wgpu::Buffer,
    instance_buf: wgpu::Buffer,
    instance_capacity: u64,
    /// Instance count uploaded by the most recent `prepare()`, consumed
    /// by the next `draw()`. Reset to 0 if no instances were uploaded.
    pending_count: u32,
}

impl RectPipeline {
    pub fn new(device: &wgpu::Device, surface_format: wgpu::TextureFormat) -> Self {
        let shader = crate::gpu::shaders::ui_rect(device);

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ui_rect bgl"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::VERTEX_FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: wgpu::BufferSize::new(std::mem::size_of::<RectUniforms>() as u64),
                },
                count: None,
            }],
        });

        let uniform_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ui_rect uniforms"),
            size: std::mem::size_of::<RectUniforms>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ui_rect bind group"),
            layout: &bgl,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: uniform_buf.as_entire_binding(),
            }],
        });

        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ui_rect pipeline layout"),
            bind_group_layouts: &[Some(&bgl)],
            immediate_size: 0,
        });

        let instance_stride = std::mem::size_of::<RectInstance>() as u64;
        let instance_layout = wgpu::VertexBufferLayout {
            array_stride: instance_stride,
            step_mode: wgpu::VertexStepMode::Instance,
            attributes: &[
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x4,
                    offset: 0,
                    shader_location: 0,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x4,
                    offset: 16,
                    shader_location: 1,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x4,
                    offset: 32,
                    shader_location: 2,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x4,
                    offset: 48,
                    shader_location: 3,
                },
            ],
        };

        let pipeline = crate::gpu::shaders::build_pipeline(device, "ui_rect pipeline", &shader, |shader| {
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("ui_rect pipeline"),
                layout: Some(&pipeline_layout),
                vertex: wgpu::VertexState {
                    module: shader.vs(),
                    entry_point: Some("vs_main"),
                    buffers: &[Some(instance_layout.clone())],
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module: shader.fs(),
                    entry_point: Some("fs_main"),
                    targets: &[Some(wgpu::ColorTargetState {
                        format: surface_format,
                        blend: Some(wgpu::BlendState {
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
                        }),
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
                    count: crate::render::MSAA_SAMPLES,
                    mask: !0,
                    alpha_to_coverage_enabled: false,
                },
                multiview_mask: None,
                cache: None,
            })
        });

        let instance_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ui_rect instance buffer"),
            size: instance_stride * INITIAL_INSTANCE_CAPACITY,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        Self {
            pipeline,
            bind_group,
            uniform_buf,
            instance_buf,
            instance_capacity: INITIAL_INSTANCE_CAPACITY,
            pending_count: 0,
        }
    }

    /// Upload uniforms + instances. Call once per frame before `draw()`.
    /// Empty `instances` is fine — the next `draw()` becomes a no-op.
    pub fn prepare(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        viewport_px: (u32, u32),
        elapsed_secs: f32,
        instances: &[RectInstance],
    ) {
        let uniforms = RectUniforms {
            viewport_px: [viewport_px.0 as f32, viewport_px.1 as f32],
            elapsed_secs,
            _pad: 0.0,
        };
        queue.write_buffer(&self.uniform_buf, 0, bytemuck::bytes_of(&uniforms));

        let stride = std::mem::size_of::<RectInstance>() as u64;
        if !instances.is_empty() {
            let needed = instances.len() as u64;
            if needed > self.instance_capacity {
                let mut new_cap = self.instance_capacity.max(1);
                while new_cap < needed {
                    new_cap *= 2;
                }
                self.instance_buf = device.create_buffer(&wgpu::BufferDescriptor {
                    label: Some("ui_rect instance buffer"),
                    size: stride * new_cap,
                    usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                });
                self.instance_capacity = new_cap;
            }
            queue.write_buffer(&self.instance_buf, 0, bytemuck::cast_slice(instances));
        }
        self.pending_count = instances.len() as u32;
    }

    /// Issue a sub-range of this frame's instances inside an existing
    /// render pass (clamped to what `prepare` uploaded; pass `0..u32::MAX`
    /// for everything). Ranged rather than all-or-nothing so
    /// `UiRenderer::draw` can SPLIT the rect draw around the OCR bubble
    /// text: the bubble pills are the leading range and must sit under
    /// the bubble glyphs, while the panel/hint rects are the trailing
    /// range and must sit over them — one contiguous draw cannot express
    /// that sandwich. Two draws of the same pipeline/buffer cost nothing
    /// measurable.
    pub fn draw_range(&self, rpass: &mut wgpu::RenderPass<'_>, range: std::ops::Range<u32>) {
        let start = range.start.min(self.pending_count);
        let end = range.end.min(self.pending_count);
        if start >= end {
            return;
        }
        rpass.set_pipeline(&self.pipeline);
        rpass.set_bind_group(0, &self.bind_group, &[]);
        rpass.set_vertex_buffer(0, self.instance_buf.slice(..));
        rpass.draw(0..6, start..end);
    }
}
