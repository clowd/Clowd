//! Instanced solid/bordered rect pipeline.
//!
//! One draw call per frame issues N quads from a single instance buffer.
//! Each instance carries destination in window-local physical pixels, a
//! straight-alpha fill color, an optional border color + width, and a
//! lighten-toward-white amount (for hover effects). Shader output is
//! premultiplied; pair with the standard source-over blend state.

use bytemuck::{Pod, Zeroable};

use crate::gxi::{self, BindingRes, BlendMode, PipelineDesc, ShaderId, VertexAttr, VertexFormat, VertexLayout};

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
    pipeline: gxi::RenderPipeline,
    bind_group: gxi::BindGroup,
    uniform_buf: gxi::Buffer,
    instance_buf: gxi::Buffer,
    instance_capacity: u64,
    /// Instance count uploaded by the most recent `prepare()`, consumed
    /// by the next `draw()`. Reset to 0 if no instances were uploaded.
    pending_count: u32,
}

const RECT_INSTANCE_LAYOUT: VertexLayout = VertexLayout {
    stride: std::mem::size_of::<RectInstance>() as u64,
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
            format: VertexFormat::Float32x4,
            offset: 32,
            location: 2,
        },
        VertexAttr {
            format: VertexFormat::Float32x4,
            offset: 48,
            location: 3,
        },
    ],
};

impl RectPipeline {
    pub fn new(device: &gxi::Device) -> Self {
        let uniform_buf = device.create_uniform_buffer("ui_rect uniforms", std::mem::size_of::<RectUniforms>() as u64);
        let bind_group = device.create_bind_group("ui_rect bind group", ShaderId::UiRect, &[BindingRes::Uniform(&uniform_buf)]);

        let pipeline = device.create_pipeline(&PipelineDesc {
            label: "ui_rect pipeline",
            shader: ShaderId::UiRect,
            vertex: Some(RECT_INSTANCE_LAYOUT),
            blend: BlendMode::PremultipliedAlpha,
        });

        let instance_stride = std::mem::size_of::<RectInstance>() as u64;
        let instance_buf = device.create_instance_buffer("ui_rect instance buffer", instance_stride * INITIAL_INSTANCE_CAPACITY);

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
        device: &gxi::Device,
        queue: &gxi::Queue,
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
                self.instance_buf = device.create_instance_buffer("ui_rect instance buffer", stride * new_cap);
                self.instance_capacity = new_cap;
            }
            queue.write_buffer(&self.instance_buf, 0, bytemuck::cast_slice(instances));
        }
        self.pending_count = instances.len() as u32;
    }

    /// Issue a sub-range of this frame's instances into the open frame
    /// (clamped to what `prepare` uploaded; pass `0..u32::MAX`
    /// for everything). Ranged rather than all-or-nothing so
    /// `UiRenderer::draw` can SPLIT the rect draw around the OCR bubble
    /// text: the bubble pills are the leading range and must sit under
    /// the bubble glyphs, while the panel/hint rects are the trailing
    /// range and must sit over them — one contiguous draw cannot express
    /// that sandwich. Two draws of the same pipeline/buffer cost nothing
    /// measurable.
    pub fn draw_range(&self, frame: &mut gxi::Frame, range: std::ops::Range<u32>) {
        let start = range.start.min(self.pending_count);
        let end = range.end.min(self.pending_count);
        if start >= end {
            return;
        }
        frame.set_pipeline(&self.pipeline);
        frame.set_bind_group(0, &self.bind_group);
        frame.set_vertex_buffer(0, &self.instance_buf);
        frame.draw(0..6, start..end);
    }
}
