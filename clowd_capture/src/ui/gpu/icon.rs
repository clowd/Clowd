//! CPU-rasterized icon atlas + instanced textured-quad pipeline.
//!
//! Icons are rasterized once via `resvg`, each entry at its own physical
//! pixel size, packed into a texture atlas via `etagere`, and drawn as
//! instanced quads. The atlas is rebuilt when any target size (DPI)
//! changes.

use bytemuck::{Pod, Zeroable};

use crate::gxi::{self, BindingRes, BlendMode, PipelineDesc, ShaderId, TexFormat, TextureDesc, VertexAttr, VertexFormat, VertexLayout};

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
    pub texture: gxi::Texture,
    /// One entry per tree handed to [`Self::build`], in the same order:
    /// the atlas cell and the square size (in px) the tree was rasterised
    /// at. Entries differ in size (button icons vs. the tray emblem), so
    /// the extent lives here rather than on the atlas.
    slots: Vec<(etagere::Rectangle, u32)>,
    atlas_size: u32,
}

impl IconAtlas {
    /// Rasterise each `(tree, px)` entry at its own square size — the
    /// viewBox is stretched to the box (the C# canvas-fit rule; never
    /// ink-fit) — and pack them into one texture. Index the result with
    /// [`Self::uv_for`] by position in `entries`.
    pub fn build(device: &gxi::Device, queue: &gxi::Queue, entries: &[(&usvg::Tree, u32)]) -> Self {
        let max_px = entries
            .iter()
            .map(|e| e.1)
            .max()
            .unwrap_or(1);
        let atlas_size = if max_px <= 36 { 256u32 } else { 512 };
        let mut allocator = etagere::AtlasAllocator::new(etagere::size2(atlas_size as i32, atlas_size as i32));

        let texture = device.create_texture(&TextureDesc {
            label: "icon atlas",
            width: atlas_size,
            height: atlas_size,
            format: TexFormat::Rgba8Unorm,
        });

        let mut slots = Vec::with_capacity(entries.len());
        for &(tree, px) in entries {
            let Some(mut pm) = resvg::tiny_skia::Pixmap::new(px, px) else {
                slots.push((
                    etagere::Rectangle {
                        min: etagere::point2(0, 0),
                        max: etagere::point2(0, 0),
                    },
                    px,
                ));
                continue;
            };
            let vb = tree.size();
            let sx = px as f32 / vb.width();
            let sy = px as f32 / vb.height();
            resvg::render(tree, resvg::tiny_skia::Transform::from_scale(sx, sy), &mut pm.as_mut());
            let alloc = allocator
                .allocate(etagere::size2(px as i32, px as i32))
                .expect("icon atlas too small");
            queue.write_texture(
                &texture,
                (alloc.rectangle.min.x as u32, alloc.rectangle.min.y as u32),
                (px, px),
                pm.data(),
            );
            slots.push((alloc.rectangle, px));
        }

        Self {
            texture,
            slots,
            atlas_size,
        }
    }

    /// Normalised `(u0, v0, u1, v1)` of entry `index`, sized by the px the
    /// entry was rasterised at.
    pub fn uv_for(&self, index: usize) -> [f32; 4] {
        let (r, px) = &self.slots[index];
        let s = self.atlas_size as f32;
        let px = *px as f32;
        [
            r.min.x as f32 / s,
            r.min.y as f32 / s,
            (r.min.x as f32 + px) / s,
            (r.min.y as f32 + px) / s,
        ]
    }
}

const ICON_INSTANCE_LAYOUT: VertexLayout = VertexLayout {
    stride: std::mem::size_of::<IconInstance>() as u64,
    attrs: &[
        VertexAttr {
            format: VertexFormat::Float32x4,
            offset: 0,
            location: 0,
        },
        VertexAttr {
            format: VertexFormat::Float32x4,
            offset: 16,
            location: 1,
        },
        VertexAttr {
            format: VertexFormat::Float32,
            offset: 32,
            location: 2,
        },
    ],
};

pub struct IconPipeline {
    pipeline: gxi::RenderPipeline,
    sampler: gxi::Sampler,
    uniform_buf: gxi::Buffer,
    instance_buf: gxi::Buffer,
    instance_capacity: u64,
    bind_group: Option<gxi::BindGroup>,
    pending_count: u32,
}

impl IconPipeline {
    pub fn new(device: &gxi::Device) -> Self {
        let uniform_buf = device.create_uniform_buffer("ui_icon uniforms", std::mem::size_of::<IconUniforms>() as u64);
        let sampler = device.create_sampler("ui_icon sampler");

        let pipeline = device.create_pipeline(&PipelineDesc {
            label: "ui_icon pipeline",
            shader: ShaderId::UiIcon,
            vertex: Some(ICON_INSTANCE_LAYOUT),
            blend: BlendMode::PremultipliedAlpha,
        });

        let instance_stride = std::mem::size_of::<IconInstance>() as u64;
        let instance_buf = device.create_instance_buffer("ui_icon instance buffer", instance_stride * INITIAL_INSTANCE_CAPACITY);

        Self {
            pipeline,
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
        device: &gxi::Device,
        queue: &gxi::Queue,
        viewport_px: (u32, u32),
        atlas: &IconAtlas,
        instances: &[IconInstance],
    ) {
        let uniforms = IconUniforms {
            viewport_px: [viewport_px.0 as f32, viewport_px.1 as f32],
            _pad: [0.0; 2],
        };
        queue.write_buffer(&self.uniform_buf, 0, bytemuck::bytes_of(&uniforms));

        self.bind_group = Some(device.create_bind_group(
            "ui_icon bind group",
            ShaderId::UiIcon,
            &[
                BindingRes::Uniform(&self.uniform_buf),
                BindingRes::Texture(&atlas.texture),
                BindingRes::Sampler(&self.sampler),
            ],
        ));

        let stride = std::mem::size_of::<IconInstance>() as u64;
        if !instances.is_empty() {
            let needed = instances.len() as u64;
            if needed > self.instance_capacity {
                let mut new_cap = self.instance_capacity.max(1);
                while new_cap < needed {
                    new_cap *= 2;
                }
                self.instance_buf = device.create_instance_buffer("ui_icon instance buffer", stride * new_cap);
                self.instance_capacity = new_cap;
            }
            queue.write_buffer(&self.instance_buf, 0, bytemuck::cast_slice(instances));
        }
        self.pending_count = instances.len() as u32;
    }

    pub fn draw(&self, frame: &mut gxi::Frame) {
        if self.pending_count == 0 {
            return;
        }
        let Some(bg) = &self.bind_group else {
            return;
        };
        frame.set_pipeline(&self.pipeline);
        frame.set_bind_group(0, bg);
        frame.set_vertex_buffer(0, &self.instance_buf);
        frame.draw(0..6, 0..self.pending_count);
    }
}
