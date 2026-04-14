//! Generic GPU backend for rendering UI component overlays.
//!
//! Replaces the panel-specific `BakePanelBackend`. Renders any number of
//! components as textured quads with shader-driven hover overlays, without
//! knowing anything about what those components contain.

use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::time::Instant;

use crate::geometry::{RectExt, ScreenRect};

use super::animation::OverlayAnimator;
use super::component::{ComponentId, ComponentSnapshot};

/// Maximum overlay regions the shader supports per component.
pub const MAX_OVERLAY_REGIONS: usize = 16;

/// Uniform block for the generic textured-quad pipeline.
///
/// WGSL alignment: vec4 is 16-byte aligned. Overlay data is packed into
/// fixed-size arrays matching the shader's expectations.
#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
struct QuadUniforms {
    /// Destination rect in NDC: (min_x, min_y, size_x, size_y).
    ndc_rect: [f32; 4],
    /// Number of active overlay regions (x), padding (yzw).
    region_meta: [f32; 4],
    /// Overlay region UV rects: (u_min, v_min, u_max, v_max) each.
    region_rects: [[f32; 4]; MAX_OVERLAY_REGIONS],
    /// Overlay fade values packed into vec4s (16 floats = 4 vec4s).
    region_fades: [[f32; 4]; MAX_OVERLAY_REGIONS / 4],
}

/// Per-component cached GPU resources.
struct CachedTexture {
    #[allow(dead_code)]
    texture: wgpu::Texture,
    bind_group: wgpu::BindGroup,
    /// Destination in window-local physical pixels (left, top, right, bottom).
    dest_px: [f32; 4],
    /// Overlay region UV rects, mirroring the snapshot's overlay_regions.
    overlay_uv_rects: Vec<[f32; 4]>,
}

/// Per-component state tracked by the backend.
struct OverlayEntry {
    cached: Option<CachedTexture>,
    cached_hash: u64,
    snapshot: Option<ComponentSnapshot>,
    animator: OverlayAnimator,
}

/// Generic GPU backend that renders any component's pixmap as a textured
/// quad. One instance per render thread; manages multiple components via
/// a HashMap keyed by `ComponentId`.
pub struct OverlayBackend {
    pipeline: wgpu::RenderPipeline,
    bgl: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    uniform_buffer: wgpu::Buffer,
    components: HashMap<ComponentId, OverlayEntry>,
    last_render_time: Option<Instant>,
}

impl OverlayBackend {
    /// Construct the backend. Creates the generic quad pipeline — no
    /// component-specific resources.
    pub fn new(device: &wgpu::Device, surface_format: wgpu::TextureFormat) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("overlay quad shader"),
            source: wgpu::ShaderSource::Wgsl(
                include_str!("../../shaders/overlay_quad.wgsl").into(),
            ),
        });

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("overlay BGL"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::VERTEX_FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: wgpu::BufferSize::new(
                            std::mem::size_of::<QuadUniforms>() as u64,
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

        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("overlay pipeline layout"),
            bind_group_layouts: &[Some(&bgl)],
            immediate_size: 0,
        });

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("overlay pipeline"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                buffers: &[],
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
            multisample: wgpu::MultisampleState::default(),
            multiview_mask: None,
            cache: None,
        });

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("overlay sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Nearest,
            min_filter: wgpu::FilterMode::Nearest,
            mipmap_filter: wgpu::MipmapFilterMode::Nearest,
            ..Default::default()
        });

        let uniform_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("overlay uniforms"),
            size: std::mem::size_of::<QuadUniforms>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        Self {
            pipeline,
            bgl,
            sampler,
            uniform_buffer,
            components: HashMap::new(),
            last_render_time: None,
        }
    }

    /// Receive a component snapshot from the app thread.
    pub fn on_snapshot(&mut self, snapshot: ComponentSnapshot) {
        let entry = self
            .components
            .entry(snapshot.id)
            .or_insert_with(|| OverlayEntry {
                cached: None,
                cached_hash: 0,
                snapshot: None,
                animator: OverlayAnimator::new(),
            });
        entry.animator.update_targets(&snapshot.overlay_regions);
        entry.snapshot = Some(snapshot);
    }

    /// Remove a component.
    pub fn remove(&mut self, id: ComponentId) {
        self.components.remove(&id);
    }

    /// Hash the pixmap-affecting parts of a snapshot. Overlay regions
    /// are excluded — they're handled by the animator/shader.
    fn snapshot_hash(snap: &ComponentSnapshot) -> u64 {
        let mut h = std::collections::hash_map::DefaultHasher::new();
        if let Some(ref baked) = snap.pixmap {
            baked.width.hash(&mut h);
            baked.height.hash(&mut h);
            baked.dest_vd.left().hash(&mut h);
            baked.dest_vd.top().hash(&mut h);
            baked.dest_vd.right().hash(&mut h);
            baked.dest_vd.bottom().hash(&mut h);
            // Hash a sample of the data rather than all of it — the
            // data only changes when dest/size changes anyway (same
            // bake produces same pixels for same inputs).
            baked.data.len().hash(&mut h);
            if baked.data.len() >= 16 {
                baked.data[..8].hash(&mut h);
                baked.data[baked.data.len() - 8..].hash(&mut h);
            }
        }
        h.finish()
    }

    /// Render all active components. Called once per frame after the
    /// desktop pass.
    pub fn render(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        target_view: &wgpu::TextureView,
        monitor_size_px: (u32, u32),
        monitor_bounds: ScreenRect,
    ) {
        let now = Instant::now();
        let dt = self
            .last_render_time
            .map(|t| now.duration_since(t).as_secs_f32())
            .unwrap_or(0.0);
        self.last_render_time = Some(now);

        // Collect IDs so we can iterate without borrowing self.
        let ids: Vec<ComponentId> = self.components.keys().copied().collect();

        for id in ids {
            let entry = self.components.get_mut(&id).unwrap();

            let Some(snapshot) = entry.snapshot.as_ref() else {
                continue;
            };
            let Some(ref baked) = snapshot.pixmap else {
                continue;
            };

            // Advance animation.
            entry.animator.advance(dt);

            // Re-upload texture if needed.
            let hash = Self::snapshot_hash(snapshot);
            if entry.cached.is_none() || hash != entry.cached_hash {
                let w = baked.width;
                let h = baked.height;

                let texture = device.create_texture(&wgpu::TextureDescriptor {
                    label: Some("overlay texture"),
                    size: wgpu::Extent3d {
                        width: w,
                        height: h,
                        depth_or_array_layers: 1,
                    },
                    mip_level_count: 1,
                    sample_count: 1,
                    dimension: wgpu::TextureDimension::D2,
                    format: wgpu::TextureFormat::Rgba8Unorm,
                    usage: wgpu::TextureUsages::TEXTURE_BINDING
                        | wgpu::TextureUsages::COPY_DST,
                    view_formats: &[],
                });
                queue.write_texture(
                    wgpu::TexelCopyTextureInfo {
                        texture: &texture,
                        mip_level: 0,
                        origin: wgpu::Origin3d::ZERO,
                        aspect: wgpu::TextureAspect::All,
                    },
                    &baked.data,
                    wgpu::TexelCopyBufferLayout {
                        offset: 0,
                        bytes_per_row: Some(4 * w),
                        rows_per_image: Some(h),
                    },
                    wgpu::Extent3d {
                        width: w,
                        height: h,
                        depth_or_array_layers: 1,
                    },
                );

                let view =
                    texture.create_view(&wgpu::TextureViewDescriptor::default());
                let bind_group =
                    device.create_bind_group(&wgpu::BindGroupDescriptor {
                        label: Some("overlay bind group"),
                        layout: &self.bgl,
                        entries: &[
                            wgpu::BindGroupEntry {
                                binding: 0,
                                resource: self.uniform_buffer.as_entire_binding(),
                            },
                            wgpu::BindGroupEntry {
                                binding: 1,
                                resource: wgpu::BindingResource::TextureView(&view),
                            },
                            wgpu::BindGroupEntry {
                                binding: 2,
                                resource: wgpu::BindingResource::Sampler(
                                    &self.sampler,
                                ),
                            },
                        ],
                    });

                // VD → window-local physical pixels.
                let dest = baked.dest_vd;
                let mon = monitor_bounds;
                let dest_px = [
                    (dest.left() - mon.left()) as f32,
                    (dest.top() - mon.top()) as f32,
                    (dest.right() - mon.left()) as f32,
                    (dest.bottom() - mon.top()) as f32,
                ];

                let overlay_uv_rects: Vec<[f32; 4]> = snapshot
                    .overlay_regions
                    .iter()
                    .take(MAX_OVERLAY_REGIONS)
                    .map(|r| r.uv_rect)
                    .collect();

                entry.cached = Some(CachedTexture {
                    texture,
                    bind_group,
                    dest_px,
                    overlay_uv_rects,
                });
                entry.cached_hash = hash;
            }

            // Update overlay UV rects from latest snapshot (they can
            // change without a texture re-bake, e.g. hover moved).
            if let Some(ref mut cached) = entry.cached {
                cached.overlay_uv_rects = snapshot
                    .overlay_regions
                    .iter()
                    .take(MAX_OVERLAY_REGIONS)
                    .map(|r| r.uv_rect)
                    .collect();
            }

            let Some(cached) = entry.cached.as_ref() else {
                continue;
            };

            // NDC conversion.
            let mw = monitor_size_px.0 as f32;
            let mh = monitor_size_px.1 as f32;
            let min_x = (cached.dest_px[0] / mw) * 2.0 - 1.0;
            let min_y = 1.0 - (cached.dest_px[3] / mh) * 2.0;
            let max_x = (cached.dest_px[2] / mw) * 2.0 - 1.0;
            let max_y = 1.0 - (cached.dest_px[1] / mh) * 2.0;

            // Build uniforms.
            let region_count = cached.overlay_uv_rects.len().min(MAX_OVERLAY_REGIONS);
            let mut region_rects = [[0.0f32; 4]; MAX_OVERLAY_REGIONS];
            for (i, r) in cached.overlay_uv_rects.iter().enumerate().take(MAX_OVERLAY_REGIONS) {
                region_rects[i] = *r;
            }

            let mut region_fades = [[0.0f32; 4]; MAX_OVERLAY_REGIONS / 4];
            for i in 0..region_count {
                let vec_idx = i / 4;
                let comp_idx = i % 4;
                region_fades[vec_idx][comp_idx] = entry.animator.fade_at(i);
            }

            let uniforms = QuadUniforms {
                ndc_rect: [min_x, min_y, max_x - min_x, max_y - min_y],
                region_meta: [region_count as f32, 0.0, 0.0, 0.0],
                region_rects,
                region_fades,
            };
            queue.write_buffer(&self.uniform_buffer, 0, bytemuck::bytes_of(&uniforms));

            // Draw the quad.
            let mut rpass =
                encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                    label: Some("overlay pass"),
                    color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                        view: target_view,
                        resolve_target: None,
                        depth_slice: None,
                        ops: wgpu::Operations {
                            load: wgpu::LoadOp::Load,
                            store: wgpu::StoreOp::Store,
                        },
                    })],
                    depth_stencil_attachment: None,
                    timestamp_writes: None,
                    occlusion_query_set: None,
                    multiview_mask: None,
                });
            rpass.set_pipeline(&self.pipeline);
            rpass.set_bind_group(0, &cached.bind_group, &[]);
            rpass.draw(0..6, 0..1);
        }
    }
}
