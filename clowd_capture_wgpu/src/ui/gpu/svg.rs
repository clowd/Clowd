//! usvg → lyon tessellation + a wgpu pipeline to draw the resulting
//! triangle meshes at arbitrary on-screen positions and sizes.
//!
//! Meshes are tessellated once (per SVG, per render thread) at
//! `SvgPipeline::load_mesh` time and then reused every frame via the
//! shared pipeline; per-draw scale + offset + alpha ride an instance
//! vertex buffer.

use bytemuck::{Pod, Zeroable};
use lyon::math::Point;
use lyon::path::PathEvent;
use lyon::tessellation::{
    BuffersBuilder, FillOptions, FillTessellator, FillVertex, FillVertexConstructor, VertexBuffers,
};
use usvg::tiny_skia_path;
use wgpu::util::DeviceExt;

/// Per-vertex data uploaded for each tessellated SVG mesh. Position is in
/// the SVG's own coordinate space (viewBox units); the vertex shader
/// scales + offsets into pixels via the per-instance transform.
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug)]
pub struct SvgVertex {
    pub position: [f32; 2],
    pub color: [f32; 4],
}

/// Per-draw instance data. `offset_px` and `scale_px` convert the mesh
/// coordinate to window-local physical pixels; `alpha_mul` multiplies
/// every vertex alpha for fade effects.
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug)]
pub struct SvgInstance {
    pub offset_px: [f32; 2],
    pub scale_px: [f32; 2],
    pub alpha_mul: f32,
    pub _pad: [f32; 3],
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct SvgUniforms {
    viewport_px: [f32; 2],
    _pad: [f32; 2],
}

/// GPU-resident tessellated SVG. Vertex + index buffers live on this
/// render thread's device; the SVG's intrinsic (viewBox) width/height
/// are cached so the caller knows how to scale into a target rect.
pub struct SvgMesh {
    pub vbo: wgpu::Buffer,
    pub ibo: wgpu::Buffer,
    pub index_count: u32,
    /// Intrinsic SVG dimensions (for computing scale into a target rect).
    pub size: [f32; 2],
}

/// Pipeline + shared uniforms. One per render thread.
pub struct SvgPipeline {
    pipeline: wgpu::RenderPipeline,
    bind_group: wgpu::BindGroup,
    uniform_buf: wgpu::Buffer,
    /// Instance buffer reused each frame, grown if needed.
    instance_buf: wgpu::Buffer,
    instance_capacity: u64,
    /// Draw records produced by the most recent `prepare()`. Each entry
    /// references a mesh by index into the caller's mesh list.
    draws: Vec<SvgDraw>,
}

struct SvgDraw {
    mesh_idx: usize,
    instance_offset: u64,
}

impl SvgPipeline {
    pub fn new(device: &wgpu::Device, surface_format: wgpu::TextureFormat) -> Self {
        let shader = device.create_shader_module(wgpu::include_wgsl!(
            "../../../shaders/ui_svg.wgsl"
        ));

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ui_svg bgl"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::VERTEX,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: wgpu::BufferSize::new(
                        std::mem::size_of::<SvgUniforms>() as u64,
                    ),
                },
                count: None,
            }],
        });

        let uniform_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ui_svg uniforms"),
            size: std::mem::size_of::<SvgUniforms>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ui_svg bind group"),
            layout: &bgl,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: uniform_buf.as_entire_binding(),
            }],
        });

        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ui_svg pipeline layout"),
            bind_group_layouts: &[Some(&bgl)],
            immediate_size: 0,
        });

        let vertex_layout = wgpu::VertexBufferLayout {
            array_stride: std::mem::size_of::<SvgVertex>() as u64,
            step_mode: wgpu::VertexStepMode::Vertex,
            attributes: &[
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x2,
                    offset: 0,
                    shader_location: 0,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x4,
                    offset: 8,
                    shader_location: 1,
                },
            ],
        };
        let instance_layout = wgpu::VertexBufferLayout {
            array_stride: std::mem::size_of::<SvgInstance>() as u64,
            step_mode: wgpu::VertexStepMode::Instance,
            attributes: &[
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x2,
                    offset: 0,
                    shader_location: 2,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x2,
                    offset: 8,
                    shader_location: 3,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32,
                    offset: 16,
                    shader_location: 4,
                },
            ],
        };

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ui_svg pipeline"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                buffers: &[vertex_layout, instance_layout],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
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
        });

        let instance_capacity = 16u64;
        let instance_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ui_svg instance buffer"),
            size: instance_capacity * std::mem::size_of::<SvgInstance>() as u64,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        Self {
            pipeline,
            bind_group,
            uniform_buf,
            instance_buf,
            instance_capacity,
            draws: Vec::new(),
        }
    }

    /// Tessellate `tree` and upload an `SvgMesh` to this device.
    pub fn load_mesh(&self, device: &wgpu::Device, tree: &usvg::Tree) -> SvgMesh {
        let (buffers, size) = tessellate_svg(tree);
        let vbo = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("ui_svg vbo"),
            contents: bytemuck::cast_slice(&buffers.vertices),
            usage: wgpu::BufferUsages::VERTEX,
        });
        let ibo = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("ui_svg ibo"),
            contents: bytemuck::cast_slice(&buffers.indices),
            usage: wgpu::BufferUsages::INDEX,
        });
        SvgMesh {
            vbo,
            ibo,
            index_count: buffers.indices.len() as u32,
            size,
        }
    }

    /// Stage the per-frame instance buffer + per-draw record list.
    /// `draws` pairs each mesh index with its per-draw instance data.
    pub fn prepare(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        viewport_px: (u32, u32),
        draws: &[(usize, SvgInstance)],
    ) {
        let uniforms = SvgUniforms {
            viewport_px: [viewport_px.0 as f32, viewport_px.1 as f32],
            _pad: [0.0; 2],
        };
        queue.write_buffer(&self.uniform_buf, 0, bytemuck::bytes_of(&uniforms));

        self.draws.clear();
        if draws.is_empty() {
            return;
        }
        let stride = std::mem::size_of::<SvgInstance>() as u64;
        let needed = draws.len() as u64;
        if needed > self.instance_capacity {
            let mut new_cap = self.instance_capacity.max(1);
            while new_cap < needed {
                new_cap *= 2;
            }
            self.instance_buf = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("ui_svg instance buffer"),
                size: new_cap * stride,
                usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            self.instance_capacity = new_cap;
        }

        let instances: Vec<SvgInstance> = draws.iter().map(|(_, inst)| *inst).collect();
        queue.write_buffer(
            &self.instance_buf,
            0,
            bytemuck::cast_slice(&instances),
        );
        for (i, (mesh_idx, _)) in draws.iter().enumerate() {
            self.draws.push(SvgDraw {
                mesh_idx: *mesh_idx,
                instance_offset: i as u64 * stride,
            });
        }
    }

    /// Draw every prepared mesh. Meshes are looked up by `mesh_idx` into
    /// the `meshes` slice (caller owns them — typically
    /// `panel::PanelRenderer::icons`).
    pub fn draw<'a>(&'a self, rpass: &mut wgpu::RenderPass<'a>, meshes: &'a [SvgMesh]) {
        if self.draws.is_empty() {
            return;
        }
        rpass.set_pipeline(&self.pipeline);
        rpass.set_bind_group(0, &self.bind_group, &[]);
        let stride = std::mem::size_of::<SvgInstance>() as u64;
        for d in &self.draws {
            let Some(mesh) = meshes.get(d.mesh_idx) else {
                continue;
            };
            if mesh.index_count == 0 {
                continue;
            }
            rpass.set_vertex_buffer(0, mesh.vbo.slice(..));
            rpass.set_vertex_buffer(
                1,
                self.instance_buf
                    .slice(d.instance_offset..d.instance_offset + stride),
            );
            rpass.set_index_buffer(mesh.ibo.slice(..), wgpu::IndexFormat::Uint32);
            rpass.draw_indexed(0..mesh.index_count, 0, 0..1);
        }
    }
}

// ---- usvg → lyon tessellation -------------------------------------------

struct Ctor {
    color: [f32; 4],
    transform: usvg::Transform,
}

impl FillVertexConstructor<SvgVertex> for Ctor {
    fn new_vertex(&mut self, vertex: FillVertex) -> SvgVertex {
        let p = vertex.position();
        let t = self.transform;
        let x = t.sx * p.x + t.kx * p.y + t.tx;
        let y = t.ky * p.x + t.sy * p.y + t.ty;
        SvgVertex {
            position: [x, y],
            color: self.color,
        }
    }
}

struct PathConvIter<'a> {
    iter: tiny_skia_path::PathSegmentsIter<'a>,
    prev: Point,
    first: Point,
    needs_end: bool,
    deferred: Option<PathEvent>,
}

impl<'a> Iterator for PathConvIter<'a> {
    type Item = PathEvent;

    fn next(&mut self) -> Option<PathEvent> {
        if let Some(d) = self.deferred.take() {
            return Some(d);
        }
        match self.iter.next() {
            Some(tiny_skia_path::PathSegment::MoveTo(pt)) => {
                let at = Point::new(pt.x, pt.y);
                if self.needs_end {
                    let last = self.prev;
                    let first = self.first;
                    self.needs_end = false;
                    self.prev = at;
                    self.first = at;
                    self.deferred = Some(PathEvent::Begin { at });
                    Some(PathEvent::End {
                        last,
                        first,
                        close: false,
                    })
                } else {
                    self.first = at;
                    self.prev = at;
                    self.needs_end = true;
                    Some(PathEvent::Begin { at })
                }
            }
            Some(tiny_skia_path::PathSegment::LineTo(pt)) => {
                self.needs_end = true;
                let from = self.prev;
                self.prev = Point::new(pt.x, pt.y);
                Some(PathEvent::Line {
                    from,
                    to: self.prev,
                })
            }
            Some(tiny_skia_path::PathSegment::QuadTo(c, p)) => {
                self.needs_end = true;
                let from = self.prev;
                self.prev = Point::new(p.x, p.y);
                Some(PathEvent::Quadratic {
                    from,
                    ctrl: Point::new(c.x, c.y),
                    to: self.prev,
                })
            }
            Some(tiny_skia_path::PathSegment::CubicTo(c1, c2, p)) => {
                self.needs_end = true;
                let from = self.prev;
                self.prev = Point::new(p.x, p.y);
                Some(PathEvent::Cubic {
                    from,
                    ctrl1: Point::new(c1.x, c1.y),
                    ctrl2: Point::new(c2.x, c2.y),
                    to: self.prev,
                })
            }
            Some(tiny_skia_path::PathSegment::Close) => {
                self.needs_end = false;
                let last = self.prev;
                let first = self.first;
                self.prev = self.first;
                Some(PathEvent::End {
                    last,
                    first,
                    close: true,
                })
            }
            None => {
                if self.needs_end {
                    self.needs_end = false;
                    Some(PathEvent::End {
                        last: self.prev,
                        first: self.first,
                        close: false,
                    })
                } else {
                    None
                }
            }
        }
    }
}

fn convert_path(p: &usvg::Path) -> PathConvIter<'_> {
    PathConvIter {
        iter: p.data().segments(),
        prev: Point::zero(),
        first: Point::zero(),
        needs_end: false,
        deferred: None,
    }
}

fn walk_group(
    group: &usvg::Group,
    tess: &mut FillTessellator,
    mesh: &mut VertexBuffers<SvgVertex, u32>,
) {
    for node in group.children() {
        match node {
            usvg::Node::Group(g) => walk_group(g, tess, mesh),
            usvg::Node::Path(p) => {
                let Some(fill) = p.fill() else { continue };
                let usvg::Paint::Color(c) = fill.paint() else {
                    continue;
                };
                let a = fill.opacity().get();
                let color = [
                    c.red as f32 / 255.0,
                    c.green as f32 / 255.0,
                    c.blue as f32 / 255.0,
                    a,
                ];
                let transform = node.abs_transform();
                // tolerance is in SVG viewBox units (typically ~100
                // per icon); 0.02 keeps curve approximation error below
                // ~0.005 px at the 26-px icon render size. Higher values
                // produce visible chords on the tight bezier curves in
                // the refresh/upload icons.
                let _ = tess.tessellate(
                    convert_path(p),
                    &FillOptions::tolerance(0.02),
                    &mut BuffersBuilder::new(mesh, Ctor { color, transform }),
                );
            }
            _ => {}
        }
    }
}

fn tessellate_svg(tree: &usvg::Tree) -> (VertexBuffers<SvgVertex, u32>, [f32; 2]) {
    let mut mesh: VertexBuffers<SvgVertex, u32> = VertexBuffers::new();
    let mut tess = FillTessellator::new();
    walk_group(tree.root(), &mut tess, &mut mesh);
    let size = tree.size();
    (mesh, [size.width(), size.height()])
}
