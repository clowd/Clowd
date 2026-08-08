//! OCR scanning-sweep pass: the soft highlight band that travels down the
//! selection while recognition runs (Scanning) and once more as the reveal
//! wave when the outcome lands (Lifted). The recognized lines themselves
//! are ALWAYS drawn as text bubbles (`super::ocr_bubbles`) — the old
//! pixel-crop fallback, which sampled the desktop snapshot texture for
//! scripts the embedded fonts couldn't shape, is gone: the bubble path now
//! loads system fonts for fallback (`TextStack::ensure_fallback_fonts`),
//! so every script renders as real glyphs. That also removed this pass's
//! snapshot bind group and the VRAM bracket discipline it required.
//!
//! All animated geometry (band position) is a pure CPU-side function of
//! the phase anchor's elapsed time (`crate::ocr::anim`) — never a
//! per-worker clock and never dt-integration, because the render workers
//! free-run at their own monitors' refresh rates and would drift apart;
//! seam-spanning regions must derive byte-identical geometry on every
//! worker.

use bytemuck::{Pod, Zeroable};

use crate::interaction::OcrState;
use crate::ocr::anim;
use crate::ui::shared::{UiMonitor, UiSharedState};
use clowd_rust_core::geometry::RectExt;

/// One sweep quad — see `ui_lift.wgsl`.
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug)]
struct LiftInstance {
    /// min_x, min_y, max_x, max_y in window-local physical pixels.
    dest_px: [f32; 4],
    /// (alpha, band_centre, sweep σ, unused).
    params: [f32; 4],
    /// Band colour (straight alpha 1.0; the fragment premultiplies).
    tint: [f32; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct LiftUniforms {
    viewport_px: [f32; 2],
    /// Seconds since the current phase's anchor. The shader does not read
    /// it today (the band centre travels per-instance), but the slot is
    /// uploaded anyway so a shader-side effect can use it without a layout
    /// change.
    t: f32,
    _pad: f32,
}

/// One sweep instance per OCR phase is the norm; a couple spare for free.
const INITIAL_INSTANCE_CAPACITY: u64 = 4;

/// Peak opacity of the scanning sweep band.
const SWEEP_ALPHA: f32 = 0.30;

pub struct LiftPipeline {
    pipeline: wgpu::RenderPipeline,
    uniform_buf: wgpu::Buffer,
    instance_buf: wgpu::Buffer,
    instance_capacity: u64,
    bind_group: wgpu::BindGroup,
    pending_count: u32,
    /// Scratch reused across frames to avoid a per-frame allocation.
    instances: Vec<LiftInstance>,
}

impl LiftPipeline {
    pub fn new(device: &wgpu::Device, surface_format: wgpu::TextureFormat) -> Self {
        let shader = crate::gpu::shaders::ui_lift(device);

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ui_lift bgl"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::VERTEX,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: wgpu::BufferSize::new(std::mem::size_of::<LiftUniforms>() as u64),
                },
                count: None,
            }],
        });

        let uniform_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ui_lift uniforms"),
            size: std::mem::size_of::<LiftUniforms>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        // Static bind group: unlike the old snapshot-sampling version there
        // is no per-cycle resource here, so it is built once and never
        // invalidated — no VRAM bracket, no ABA key.
        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ui_lift bind group"),
            layout: &bgl,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: uniform_buf.as_entire_binding(),
            }],
        });

        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ui_lift pipeline layout"),
            bind_group_layouts: &[Some(&bgl)],
            immediate_size: 0,
        });

        let instance_stride = std::mem::size_of::<LiftInstance>() as u64;
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
            ],
        };

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ui_lift pipeline"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                buffers: &[Some(instance_layout)],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format: surface_format,
                    // Premultiplied source-over — NOT the REPLACE blend the
                    // desktop/peek pipelines use: the sweep is translucent
                    // and must composite over the desktop pass.
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
            label: Some("ui_lift instance buffer"),
            size: instance_stride * INITIAL_INSTANCE_CAPACITY,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        Self {
            pipeline,
            uniform_buf,
            instance_buf,
            instance_capacity: INITIAL_INSTANCE_CAPACITY,
            bind_group,
            pending_count: 0,
            instances: Vec::new(),
        }
    }

    /// Stage this frame's sweep instance (if any).
    pub fn prepare(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        viewport_px: (u32, u32),
        state: &UiSharedState,
        this_monitor: &UiMonitor,
    ) {
        self.pending_count = 0;

        // Band geometry per phase. Scanning loops the band; Lifted plays
        // exactly one more pass (the reveal wave the bubbles rise under,
        // wrap-aligned by the app thread so the band re-enters seamlessly);
        // Retracting and Idle draw nothing — the only exit animation is the
        // region's colour fade, which lives in the desktop pass.
        let (anchor, region) = match &state.ocr {
            OcrState::Scanning {
                anchor,
                region,
                ..
            } => (anchor, region),
            OcrState::Lifted {
                anchor,
                region,
                ..
            } => {
                if anchor.elapsed().as_secs_f32() >= anim::reveal_pass_secs() {
                    return;
                }
                (anchor, region)
            }
            OcrState::Idle
            | OcrState::Retracting {
                ..
            } => return,
        };

        let mon_f = this_monitor.bounds.to_f32();
        let rf = region.to_f32();
        let dest = [rf.left(), rf.top(), rf.right(), rf.bottom()];
        if !aabb_intersects(dest, mon_f.left(), mon_f.top(), mon_f.right(), mon_f.bottom()) {
            return;
        }

        // The shared animation clock: elapsed seconds since the CURRENT
        // phase's anchor. Never this worker's own start time — every worker
        // must derive byte-identical geometry for seam-spanning regions.
        let t = anchor.elapsed().as_secs_f32();
        let uniforms = LiftUniforms {
            viewport_px: [viewport_px.0 as f32, viewport_px.1 as f32],
            t,
            _pad: 0.0,
        };
        queue.write_buffer(&self.uniform_buf, 0, bytemuck::bytes_of(&uniforms));

        // Band centre through sweep_band: the phase's fract() makes looping
        // free, and the overshoot puts the wrap entirely off-screen so
        // back-to-back passes are seamless. σ rides along in params.z — see
        // the shader header.
        let band = anim::sweep_band(anim::scan_phase(t));
        self.instances.clear();
        self.instances.push(LiftInstance {
            dest_px: [
                dest[0] - mon_f.left(),
                dest[1] - mon_f.top(),
                dest[2] - mon_f.left(),
                dest[3] - mon_f.top(),
            ],
            params: [SWEEP_ALPHA, band, anim::SWEEP_SIGMA, 0.0],
            tint: [1.0, 1.0, 1.0, 1.0],
        });

        let stride = std::mem::size_of::<LiftInstance>() as u64;
        let needed = self.instances.len() as u64;
        if needed > self.instance_capacity {
            // Grow by doubling, never shrink — keeps allocations stable
            // across frames (same policy as rect/icon).
            let mut new_cap = self.instance_capacity.max(1);
            while new_cap < needed {
                new_cap *= 2;
            }
            self.instance_buf = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("ui_lift instance buffer"),
                size: stride * new_cap,
                usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            self.instance_capacity = new_cap;
        }
        queue.write_buffer(&self.instance_buf, 0, bytemuck::cast_slice(&self.instances));
        self.pending_count = self.instances.len() as u32;
    }

    pub fn draw(&self, rpass: &mut wgpu::RenderPass<'_>) {
        if self.pending_count == 0 {
            return;
        }
        rpass.set_pipeline(&self.pipeline);
        rpass.set_bind_group(0, &self.bind_group, &[]);
        rpass.set_vertex_buffer(0, self.instance_buf.slice(..));
        rpass.draw(0..6, 0..self.pending_count);
    }
}

fn aabb_intersects(a: [f32; 4], left: f32, top: f32, right: f32, bottom: f32) -> bool {
    a[2] > left && a[0] < right && a[3] > top && a[1] < bottom
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Negative-origin virtual desktops (monitor left of primary) are the
    /// case the offset math historically gets wrong.
    #[test]
    fn aabb_intersects_negative_coordinates() {
        let a = [-1920.0, 0.0, -1820.0, 50.0];
        assert!(aabb_intersects(a, -1920.0, 0.0, 0.0, 1080.0));
        assert!(!aabb_intersects(a, 0.0, 0.0, 1920.0, 1080.0));
        // Touching edges do not count — the neighbouring monitor draws it.
        assert!(!aabb_intersects([0.0, 0.0, 10.0, 10.0], 10.0, 0.0, 20.0, 10.0));
    }
}
