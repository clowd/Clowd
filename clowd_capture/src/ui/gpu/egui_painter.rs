//! The egui painter: one textured-triangle pipeline, a port of egui-wgpu's
//! `Renderer` onto `gxi`.
//!
//! One painter per render worker. The app thread tessellates; this side
//! only uploads and draws. Vertices are `epaint::Vertex` verbatim (points,
//! uv, premultiplied gamma sRGBA packed as a little-endian `u32`), indices
//! are 32-bit, and every mesh of a frame shares one vertex and one index
//! buffer — d3d11's `write_buffer` is a whole-buffer `WRITE_DISCARD`, so
//! both are written once from offset zero and each mesh draws a range.
//!
//! Textures arrive as a version-stamped [`TextureSnapshot`] rather than as
//! egui's incremental deltas (see [`crate::ui::egui_frame`]): when the
//! version differs from the applied one every GPU texture is replaced,
//! which is correct no matter how many broadcasts this worker missed.

use std::collections::HashMap;
use std::ops::Range;

use egui::epaint::textures::{TextureFilter, TextureWrapMode};
use egui::epaint::{ClippedPrimitive, Primitive, Rect, TextureId, Vertex};

use crate::gxi::{
    self, BindingRes, BlendMode, PipelineDesc, SamplerFilter, ShaderId, TexFormat, TextureDesc, VertexAttr, VertexFormat, VertexLayout,
    VertexStep,
};
use crate::ui::egui_frame::{EguiFrame, TextureSnapshot};

const EGUI_VERTEX_LAYOUT: VertexLayout = VertexLayout {
    stride: std::mem::size_of::<Vertex>() as u64,
    step: VertexStep::Vertex,
    attrs: &[
        // Position, in points.
        VertexAttr {
            format: VertexFormat::Float32x2,
            offset: 0,
            location: 0,
        },
        // Texture coordinates, normalised.
        VertexAttr {
            format: VertexFormat::Float32x2,
            offset: 8,
            location: 1,
        },
        // `Color32` as a little-endian u32, red in the low byte.
        VertexAttr {
            format: VertexFormat::Uint32,
            offset: 16,
            location: 2,
        },
    ],
};

/// egui-wgpu's own start capacities; both buffers grow by recreation.
const VERTEX_START_BYTES: u64 = 20 * 1024;
const INDEX_START_BYTES: u64 = 4 * 1024 * 3;

#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
struct Locals {
    screen_size: [f32; 2],
    _pad: [f32; 2],
}

/// A clip rectangle in physical pixels, already clamped to the target —
/// egui-wgpu's `ScissorRect::new` verbatim.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ScissorRect {
    pub x: u32,
    pub y: u32,
    pub width: u32,
    pub height: u32,
}

impl ScissorRect {
    /// `clip` scaled by `ppp`, each edge rounded, the min corner clamped
    /// into the target and the max corner clamped to at least the min.
    pub fn new(clip: Rect, ppp: f32, target: (u32, u32)) -> Self {
        let clip_min_x = (ppp * clip.min.x).round() as u32;
        let clip_min_y = (ppp * clip.min.y).round() as u32;
        let clip_max_x = (ppp * clip.max.x).round() as u32;
        let clip_max_y = (ppp * clip.max.y).round() as u32;

        let clip_min_x = clip_min_x.clamp(0, target.0);
        let clip_min_y = clip_min_y.clamp(0, target.1);
        let clip_max_x = clip_max_x.clamp(clip_min_x, target.0);
        let clip_max_y = clip_max_y.clamp(clip_min_y, target.1);

        Self {
            x: clip_min_x,
            y: clip_min_y,
            width: clip_max_x - clip_min_x,
            height: clip_max_y - clip_min_y,
        }
    }

    pub fn is_empty(self) -> bool {
        self.width == 0 || self.height == 0
    }
}

/// One mesh of a packed frame: which texture it samples, the clip it would
/// be scissored to, and its slice of the shared index buffer.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DrawCmd {
    pub texture: TextureId,
    pub scissor: ScissorRect,
    pub indices: Range<u32>,
    pub base_vertex: i32,
}

/// One frame's meshes as two flat byte streams plus their draw list.
#[derive(Default)]
pub struct Packed {
    pub vertex_bytes: Vec<u8>,
    pub index_bytes: Vec<u8>,
    pub draws: Vec<DrawCmd>,
}

/// Concatenate every mesh into one vertex stream and one index stream,
/// recording `(first_index, count, base_vertex)` per mesh. Pure.
///
/// Meshes with nothing in them, and meshes whose clip rectangle is empty
/// after rounding, are skipped entirely — their bytes are never appended,
/// so the offsets of the meshes that follow are unaffected.
pub fn pack(prims: &[ClippedPrimitive], ppp: f32, target: (u32, u32)) -> Packed {
    let mut out = Packed::default();
    let (mut vbase, mut ibase) = (0u32, 0u32);
    for p in prims {
        let Primitive::Mesh(mesh) = &p.primitive else {
            unreachable!("clowd never emits egui paint callbacks")
        };
        let scissor = ScissorRect::new(p.clip_rect, ppp, target);
        if mesh.is_empty() || scissor.is_empty() {
            continue;
        }
        out.vertex_bytes
            .extend_from_slice(bytemuck::cast_slice(&mesh.vertices));
        out.index_bytes
            .extend_from_slice(bytemuck::cast_slice(&mesh.indices));
        out.draws.push(DrawCmd {
            texture: mesh.texture_id,
            scissor,
            indices: ibase..ibase + mesh.indices.len() as u32,
            base_vertex: vbase as i32,
        });
        vbase += mesh.vertices.len() as u32;
        ibase += mesh.indices.len() as u32;
    }
    out
}

/// One egui texture on the GPU. The texture itself is never read again
/// after the bind group is built, but it owns the GPU resource the bind
/// group points at, so it is held for the bind group's lifetime.
struct GpuTexture {
    _texture: gxi::Texture,
    bind_group: gxi::BindGroup,
}

pub struct EguiPainter {
    pipeline: gxi::RenderPipeline,
    uniform: gxi::Buffer,
    last_screen: [f32; 2],
    sampler_linear: gxi::Sampler,
    sampler_nearest: gxi::Sampler,
    vertices: gxi::Buffer,
    vertex_cap: u64,
    indices: gxi::Buffer,
    index_cap: u64,
    textures: HashMap<TextureId, GpuTexture>,
    applied_version: Option<u64>,
    draws: Vec<DrawCmd>,
    /// One warning per painter for a surface that does not match its
    /// monitor; the condition is a resize race, not a per-frame event.
    size_warned: bool,
}

impl EguiPainter {
    pub fn new(device: &gxi::Device) -> Self {
        let pipeline = device.create_pipeline(&PipelineDesc {
            label: "ui egui",
            shader: ShaderId::Egui,
            vertex: Some(EGUI_VERTEX_LAYOUT),
            blend: BlendMode::PremultipliedAlpha,
        });
        Self {
            pipeline,
            uniform: device.create_uniform_buffer("ui egui locals", std::mem::size_of::<Locals>() as u64),
            last_screen: [0.0; 2],
            sampler_linear: device.create_sampler("ui egui linear", SamplerFilter::Linear),
            sampler_nearest: device.create_sampler("ui egui nearest", SamplerFilter::Nearest),
            vertices: device.create_instance_buffer("ui egui vertices", VERTEX_START_BYTES),
            vertex_cap: VERTEX_START_BYTES,
            indices: device.create_index_buffer("ui egui indices", INDEX_START_BYTES),
            index_cap: INDEX_START_BYTES,
            textures: HashMap::new(),
            applied_version: None,
            draws: Vec::new(),
            size_warned: false,
        }
    }

    /// Stage this frame: resync textures, then (when `draw` is set) pack
    /// the meshes and upload them.
    ///
    /// Called from `UiRenderer::prepare`, before any draw of the frame, so
    /// the uploads happen while the d3d11 context mutex is free. `frame ==
    /// None` means nothing to draw at all; textures are still resynced
    /// whenever a frame is present, so a panel hidden by the overlay toggle
    /// never comes back against a stale atlas.
    pub fn prepare(
        &mut self,
        device: &gxi::Device,
        queue: &gxi::Queue,
        viewport_px: (u32, u32),
        monitor_px: (u32, u32),
        frame: Option<&EguiFrame>,
        draw: bool,
    ) {
        self.draws.clear();
        let Some(frame) = frame else {
            return;
        };
        if self.applied_version != Some(frame.textures.version) {
            self.replace_textures(device, queue, &frame.textures);
            self.applied_version = Some(frame.textures.version);
        }
        if !draw {
            return;
        }
        if viewport_px != monitor_px && !self.size_warned {
            log::warn!("egui: surface {viewport_px:?} != monitor {monitor_px:?}; primitives will scale");
            self.size_warned = true;
        }
        let packed = pack(&frame.primitives, frame.pixels_per_point, viewport_px);
        Self::grow_and_write(
            device,
            queue,
            &mut self.vertices,
            &mut self.vertex_cap,
            "ui egui vertices",
            &packed.vertex_bytes,
            gxi::Device::create_instance_buffer,
        );
        Self::grow_and_write(
            device,
            queue,
            &mut self.indices,
            &mut self.index_cap,
            "ui egui indices",
            &packed.index_bytes,
            gxi::Device::create_index_buffer,
        );
        let screen = [
            viewport_px.0 as f32 / frame.pixels_per_point,
            viewport_px.1 as f32 / frame.pixels_per_point,
        ];
        if screen != self.last_screen {
            queue.write_buffer(
                &self.uniform,
                0,
                bytemuck::bytes_of(&Locals {
                    screen_size: screen,
                    _pad: [0.0; 2],
                }),
            );
            self.last_screen = screen;
        }
        self.draws = packed.draws;
    }

    /// Grow `buf` to fit `bytes` (doubling, as the instance buffers do)
    /// and write them from offset zero.
    fn grow_and_write(
        device: &gxi::Device,
        queue: &gxi::Queue,
        buf: &mut gxi::Buffer,
        cap: &mut u64,
        label: &str,
        bytes: &[u8],
        make: fn(&gxi::Device, &str, u64) -> gxi::Buffer,
    ) {
        let needed = bytes.len() as u64;
        if needed > *cap {
            *cap = (*cap * 2).max(needed);
            *buf = make(device, label, *cap);
        }
        if !bytes.is_empty() {
            queue.write_buffer(buf, 0, bytes);
        }
    }

    /// Replace every GPU texture with `snap`. Whole textures only: a
    /// d3d11 texture created with data is immutable, so a changed image is
    /// a new texture AND a new bind group.
    fn replace_textures(&mut self, device: &gxi::Device, queue: &gxi::Queue, snap: &TextureSnapshot) {
        self.textures.clear();
        for (id, img) in &snap.textures {
            debug_assert_eq!(img.options.wrap_mode, TextureWrapMode::ClampToEdge);
            let (w, h) = (img.pixels.width() as u32, img.pixels.height() as u32);
            let texture = device.create_texture(&TextureDesc {
                label: "ui egui texture",
                width: w,
                height: h,
                format: TexFormat::Rgba8Unorm,
            });
            queue.write_texture(&texture, (0, 0), (w, h), img.pixels.as_raw());
            let sampler = if img.options.magnification == TextureFilter::Nearest && img.options.minification == TextureFilter::Nearest {
                &self.sampler_nearest
            } else {
                &self.sampler_linear
            };
            let bind_group = device.create_bind_group(
                "ui egui",
                ShaderId::Egui,
                &[
                    BindingRes::Uniform(&self.uniform),
                    BindingRes::Texture(&texture),
                    BindingRes::Sampler(sampler),
                ],
            );
            self.textures.insert(
                *id,
                GpuTexture {
                    _texture: texture,
                    bind_group,
                },
            );
        }
    }

    pub fn draw(&self, frame: &mut gxi::Frame) {
        if self.draws.is_empty() {
            return;
        }
        frame.set_pipeline(&self.pipeline);
        frame.set_vertex_buffer(0, &self.vertices);
        frame.set_index_buffer(&self.indices);
        let mut bound: Option<TextureId> = None;
        for d in &self.draws {
            // A newer snapshot may have freed the id between the pack and
            // this draw; skipping is the right answer, never a panic.
            let Some(tex) = self.textures.get(&d.texture) else {
                continue;
            };
            if bound != Some(d.texture) {
                frame.set_bind_group(0, &tex.bind_group);
                bound = Some(d.texture);
            }
            frame.draw_indexed(d.indices.clone(), d.base_vertex);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use egui::epaint::Mesh;
    use egui::{pos2, Color32};

    fn mesh(texture: TextureId, vertices: usize, indices: usize, color: Color32) -> Mesh {
        let mut m = Mesh::with_texture(texture);
        for i in 0..vertices {
            m.vertices.push(Vertex {
                pos: pos2(i as f32, 0.0),
                uv: pos2(0.0, 0.0),
                color,
            });
        }
        m.indices = (0..indices as u32).collect();
        m
    }

    fn clipped(clip: Rect, mesh: Mesh) -> ClippedPrimitive {
        ClippedPrimitive {
            clip_rect: clip,
            primitive: Primitive::Mesh(mesh),
        }
    }

    fn full_clip() -> Rect {
        Rect::from_min_max(pos2(0.0, 0.0), pos2(100.0, 100.0))
    }

    #[test]
    fn pack_keeps_color32_byte_order() {
        let color = Color32::from_rgba_premultiplied(1, 2, 3, 4);
        let packed = pack(&[clipped(full_clip(), mesh(TextureId::Managed(0), 3, 3, color))], 1.0, (100, 100));
        assert_eq!(&packed.vertex_bytes[16..20], &[1, 2, 3, 4]);
        assert_eq!(packed.vertex_bytes.len(), 3 * 20);
    }

    #[test]
    fn pack_concatenates_two_meshes() {
        let a = mesh(TextureId::Managed(0), 4, 6, Color32::WHITE);
        let b = mesh(TextureId::Managed(1), 3, 3, Color32::WHITE);
        let packed = pack(&[clipped(full_clip(), a), clipped(full_clip(), b)], 1.0, (100, 100));
        assert_eq!(packed.draws.len(), 2);
        assert_eq!(packed.draws[0].indices, 0..6);
        assert_eq!(packed.draws[0].base_vertex, 0);
        assert_eq!(packed.draws[1].indices, 6..9);
        assert_eq!(packed.draws[1].base_vertex, 4);
        assert_eq!(packed.index_bytes.len(), 9 * 4);
    }

    #[test]
    fn pack_skips_empty_and_offscreen_meshes() {
        let empty = Mesh::with_texture(TextureId::Managed(0));
        let offscreen = Rect::from_min_max(pos2(-50.0, -50.0), pos2(-10.0, -10.0));
        let packed = pack(
            &[
                clipped(full_clip(), empty),
                clipped(offscreen, mesh(TextureId::Managed(0), 3, 3, Color32::WHITE)),
                clipped(full_clip(), mesh(TextureId::Managed(1), 3, 3, Color32::WHITE)),
            ],
            1.0,
            (100, 100),
        );
        assert_eq!(packed.draws.len(), 1);
        assert_eq!(packed.draws[0].texture, TextureId::Managed(1));
        assert_eq!(packed.draws[0].base_vertex, 0);
        assert_eq!(packed.draws[0].indices, 0..3);
    }

    #[test]
    fn scissor_rounds_and_clamps_like_egui_wgpu() {
        let s = ScissorRect::new(Rect::from_min_max(pos2(10.4, 10.4), pos2(20.6, 20.6)), 1.25, (100, 100));
        assert_eq!((s.x, s.y, s.width, s.height), (13, 13, 13, 13));

        let negative = ScissorRect::new(Rect::from_min_max(pos2(-30.0, -30.0), pos2(10.0, 10.0)), 1.0, (100, 100));
        assert_eq!((negative.x, negative.y, negative.width, negative.height), (0, 0, 10, 10));

        let beyond = ScissorRect::new(Rect::from_min_max(pos2(50.0, 50.0), pos2(500.0, 500.0)), 1.0, (100, 100));
        assert_eq!((beyond.x, beyond.y, beyond.width, beyond.height), (50, 50, 50, 50));

        let degenerate = ScissorRect::new(Rect::from_min_max(pos2(200.0, 200.0), pos2(300.0, 300.0)), 1.0, (100, 100));
        assert!(degenerate.is_empty());
    }
}
