//! CPU-rasterised icon atlas + instanced textured-quad pipeline.
//!
//! Icons are rasterised once via `resvg` at physical pixel size, packed
//! into a texture atlas via `etagere`, and drawn as instanced quads.
//! The atlas is rebuilt when the target icon size (DPI) changes.

use bytemuck::{Pod, Zeroable};

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug)]
pub struct IconInstance {
    pub dest_px: [f32; 4],
    pub uv: [f32; 4],
    pub alpha_mul: f32,
    pub _pad: [f32; 3],
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct IconUniforms {
    viewport_px: [f32; 2],
    _pad: [f32; 2],
}

const INITIAL_INSTANCE_CAPACITY: u64 = 16;

pub struct IconAtlas {
    pub view: wgpu::TextureView,
    pub icon_px: u32,
    rects: Vec<etagere::Rectangle>,
    atlas_size: u32,
    _texture: wgpu::Texture,
}

impl IconAtlas {
    pub fn build(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        trees: &[usvg::Tree],
        icon_px: u32,
    ) -> Self {
        let atlas_size = if icon_px <= 36 { 256u32 } else { 512 };
        let mut allocator =
            etagere::AtlasAllocator::new(etagere::size2(atlas_size as i32, atlas_size as i32));

        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("icon atlas"),
            size: wgpu::Extent3d {
                width: atlas_size,
                height: atlas_size,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });

        let mut rects = Vec::with_capacity(trees.len());
        for tree in trees {
            let Some(mut pm) = tiny_skia::Pixmap::new(icon_px, icon_px) else {
                rects.push(etagere::Rectangle {
                    min: etagere::point2(0, 0),
                    max: etagere::point2(0, 0),
                });
                continue;
            };
            let vb = tree.size();
            let sx = icon_px as f32 / vb.width();
            let sy = icon_px as f32 / vb.height();
            resvg::render(
                tree,
                tiny_skia::Transform::from_scale(sx, sy),
                &mut pm.as_mut(),
            );
            let alloc = allocator
                .allocate(etagere::size2(icon_px as i32, icon_px as i32))
                .expect("icon atlas too small");
            queue.write_texture(
                wgpu::TexelCopyTextureInfo {
                    texture: &texture,
                    mip_level: 0,
                    origin: wgpu::Origin3d {
                        x: alloc.rectangle.min.x as u32,
                        y: alloc.rectangle.min.y as u32,
                        z: 0,
                    },
                    aspect: wgpu::TextureAspect::All,
                },
                pm.data(),
                wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(icon_px * 4),
                    rows_per_image: None,
                },
                wgpu::Extent3d {
                    width: icon_px,
                    height: icon_px,
                    depth_or_array_layers: 1,
                },
            );
            rects.push(alloc.rectangle);
        }

        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        Self {
            view,
            icon_px,
            rects,
            atlas_size,
            _texture: texture,
        }
    }

    pub fn uv_for(&self, index: usize) -> [f32; 4] {
        let r = &self.rects[index];
        let s = self.atlas_size as f32;
        let px = self.icon_px as f32;
        [
            r.min.x as f32 / s,
            r.min.y as f32 / s,
            (r.min.x as f32 + px) / s,
            (r.min.y as f32 + px) / s,
        ]
    }
}

pub struct IconPipeline {
    pipeline: wgpu::RenderPipeline,
    bgl: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    uniform_buf: wgpu::Buffer,
    instance_buf: wgpu::Buffer,
    instance_capacity: u64,
    bind_group: Option<wgpu::BindGroup>,
    pending_count: u32,
}

impl IconPipeline {
    pub fn new(device: &wgpu::Device, surface_format: wgpu::TextureFormat) -> Self {
        let shader =
            device.create_shader_module(wgpu::include_wgsl!("../../../shaders/ui_icon.wgsl"));

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ui_icon bgl"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::VERTEX,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: wgpu::BufferSize::new(
                            std::mem::size_of::<IconUniforms>() as u64,
                        ),
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

        let uniform_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ui_icon uniforms"),
            size: std::mem::size_of::<IconUniforms>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("ui_icon sampler"),
            mag_filter: wgpu::FilterMode::Nearest,
            min_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ui_icon pipeline layout"),
            bind_group_layouts: &[Some(&bgl)],
            immediate_size: 0,
        });

        let instance_stride = std::mem::size_of::<IconInstance>() as u64;
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
                    format: wgpu::VertexFormat::Float32,
                    offset: 32,
                    shader_location: 2,
                },
            ],
        };

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ui_icon pipeline"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                buffers: &[instance_layout],
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

        let instance_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ui_icon instance buffer"),
            size: instance_stride * INITIAL_INSTANCE_CAPACITY,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        Self {
            pipeline,
            bgl,
            sampler,
            uniform_buf,
            instance_buf,
            instance_capacity: INITIAL_INSTANCE_CAPACITY,
            bind_group: None,
            pending_count: 0,
        }
    }

    pub fn prepare(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        viewport_px: (u32, u32),
        atlas: &IconAtlas,
        instances: &[IconInstance],
    ) {
        let uniforms = IconUniforms {
            viewport_px: [viewport_px.0 as f32, viewport_px.1 as f32],
            _pad: [0.0; 2],
        };
        queue.write_buffer(&self.uniform_buf, 0, bytemuck::bytes_of(&uniforms));

        self.bind_group = Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ui_icon bind group"),
            layout: &self.bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: self.uniform_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(&atlas.view),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::Sampler(&self.sampler),
                },
            ],
        }));

        let stride = std::mem::size_of::<IconInstance>() as u64;
        if !instances.is_empty() {
            let needed = instances.len() as u64;
            if needed > self.instance_capacity {
                let mut new_cap = self.instance_capacity.max(1);
                while new_cap < needed {
                    new_cap *= 2;
                }
                self.instance_buf = device.create_buffer(&wgpu::BufferDescriptor {
                    label: Some("ui_icon instance buffer"),
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

    pub fn draw(&self, rpass: &mut wgpu::RenderPass<'_>) {
        if self.pending_count == 0 {
            return;
        }
        let Some(bg) = &self.bind_group else {
            return;
        };
        rpass.set_pipeline(&self.pipeline);
        rpass.set_bind_group(0, bg, &[]);
        rpass.set_vertex_buffer(0, self.instance_buf.slice(..));
        rpass.draw(0..6, 0..self.pending_count);
    }
}
