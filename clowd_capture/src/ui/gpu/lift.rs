//! OCR "lift" pass: the scanning sweep, plus the pixel-crop FALLBACK for
//! recognized lines the embedded fonts cannot re-render as text bubbles
//! (`ocr::coverage` decides per line; `super::ocr_bubbles` draws the
//! bubbles). The crop lift is kept — not vestigial — because it is the
//! only correct presentation for CJK/Cyrillic/etc. text: the overlay's
//! curated font DB has no coverage there and glyphs would be tofu.
//!
//! Modelled on [`super::icon`]'s textured-quad pipeline, with two OCR-
//! specific twists:
//!
//! * It samples the whole-virtual-desktop snapshot texture (already
//!   resident on every device for the desktop pass), so a line spanning a
//!   monitor seam samples correctly from both workers.
//! * Every piece of animated geometry is a pure CPU-side function of the
//!   phase anchor's elapsed time (`crate::ocr::anim`) — never a per-worker
//!   clock and never dt-integration, because the render workers free-run
//!   at their own monitors' refresh rates and would drift apart.

use bytemuck::{Pod, Zeroable};

use crate::gpu::desktop::DesktopSnapshot;
use crate::interaction::OcrState;
use crate::ocr::anim;
use crate::ocr::coverage::LinePresentation;
use crate::ui::gpu::rect::RectInstance;
use crate::ui::shared::{UiMonitor, UiSharedState};
use clowd_rust_core::geometry::RectExt;

/// One quad. `params.x` selects the fragment mode: 1 = textured line,
/// 2 = scanning sweep — see `ui_lift.wgsl`. (Mode 0 was the drop-shadow
/// SDF, removed with the shadows themselves.)
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug)]
struct LiftInstance {
    /// min_x, min_y, max_x, max_y in window-local physical pixels.
    dest_px: [f32; 4],
    /// u0, v0, u1, v1 into the virtual-desktop snapshot texture.
    src_uv: [f32; 4],
    /// (mode, alpha, band_centre, sweep σ) — z/w are only read by mode 2.
    params: [f32; 4],
    /// Sweep colour (unused by mode 1).
    tint: [f32; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct LiftUniforms {
    viewport_px: [f32; 2],
    /// Seconds since the current phase's anchor. The shader does not read
    /// it today (the sweep's band centre travels per-instance), but the
    /// slot is uploaded anyway so a shader-side effect can use it without
    /// a layout change.
    t: f32,
    _pad: f32,
}

const INITIAL_INSTANCE_CAPACITY: u64 = 64;

/// Defensive ceiling on drawn lines. The engine already truncates at 256
/// lines (`MAX_LINES` in both `ocr::paddle` and `ocr::win`, whose doc
/// comments cite this cap), so this
/// only fires if a future backend forgets to — it bounds the instance
/// buffer, not the recognition.
const MAX_LIFT_LINES: usize = 512;

/// Peak opacity of the scanning sweep band.
const SWEEP_ALPHA: f32 = 0.30;
/// Peak opacity of the per-line accent highlight (an ordinary RectInstance
/// pushed into the shared rect list, so it lands ON TOP of the lifted
/// pixels — rect.draw runs after lift.draw).
///
/// No drop shadows anywhere in this pass (owner call, matching the
/// bubbles): the lifted content already sits on a darkened, desaturated
/// page, so a shadow adds no separation — just mud.
const HIGHLIGHT_ALPHA: f32 = 0.25;

pub struct LiftPipeline {
    pipeline: wgpu::RenderPipeline,
    bgl: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    uniform_buf: wgpu::Buffer,
    instance_buf: wgpu::Buffer,
    instance_capacity: u64,
    bind_group: Option<wgpu::BindGroup>,
    /// Identity of the snapshot the bind group was built from, as a plain
    /// address (`&DesktopSnapshot as usize`). Never dereferenced — it only
    /// answers "is this still the same snapshot?" so the bind group is
    /// rebuilt exactly once per cycle instead of every frame. Stored as
    /// `usize` rather than a raw pointer so the struct stays `Send`.
    ///
    /// SOUNDNESS: the snapshot lives in an `Arc` that is allocated at
    /// `BeginCycle` and dropped when the worker parks, so a LATER cycle's
    /// snapshot can land on the recycled allocation and compare equal to a
    /// dead one (classic ABA). What makes the address safe as a key is
    /// that [`clear_snapshot`](Self::clear_snapshot) brackets the
    /// snapshot's whole lifetime: `UiRenderer::end_cycle` clears when the
    /// worker parks and `UiRenderer::begin_cycle` clears again before the
    /// next snapshot is uploaded, so this field is never `Some` while no
    /// snapshot is alive. Do not drop either call — a stale key that
    /// happened to match would keep a bind group pointing at the previous
    /// cycle's texture view.
    snapshot_key: Option<usize>,
    pending_count: u32,
    /// Scratch reused across frames to avoid a per-frame allocation.
    instances: Vec<LiftInstance>,
}

impl LiftPipeline {
    pub fn new(device: &wgpu::Device, surface_format: wgpu::TextureFormat) -> Self {
        let shader = crate::gpu::shaders::ui_lift(device);

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ui_lift bgl"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::VERTEX,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: wgpu::BufferSize::new(std::mem::size_of::<LiftUniforms>() as u64),
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float {
                            filterable: true,
                        },
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
            label: Some("ui_lift uniforms"),
            size: std::mem::size_of::<LiftUniforms>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        // Own LINEAR sampler: the shared desktop sampler is Nearest (it maps
        // texels 1:1 to screen pixels), but lifted lines scale up to
        // LIFT_SCALE and would look blocky under Nearest. Bgra8Unorm is
        // filterable, so a Filtering binding is legal.
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("ui_lift sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            ..Default::default()
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
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x4,
                    offset: 48,
                    shader_location: 3,
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
                    // desktop/peek pipelines use: shadows and the sweep are
                    // translucent and must composite over the desktop pass.
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
            bgl,
            sampler,
            uniform_buf,
            instance_buf,
            instance_capacity: INITIAL_INSTANCE_CAPACITY,
            bind_group: None,
            snapshot_key: None,
            pending_count: 0,
            instances: Vec::new(),
        }
    }

    /// Drop the snapshot bind group (and the cached identity behind it).
    ///
    /// CRITICAL for VRAM: the bind group holds the snapshot's `TextureView`
    /// alive, and a retained view pins the whole virtual-desktop texture —
    /// tens of MB per 4K monitor — past the parked-surface teardown at the
    /// end of the cycle (`render.rs` frees `gpu.snapshot` and shrinks the
    /// surface to 1×1 on `EndCycle`). Called from `UiRenderer::end_cycle`
    /// (every path that parks a worker) and `UiRenderer::begin_cycle`, and
    /// whenever `prepare` runs without a usable snapshot.
    ///
    /// The identity MUST go with the bind group, not just the bind group:
    /// see the `snapshot_key` field comment — dropping only the handle
    /// would leave a key that a recycled allocation can match.
    pub fn clear_snapshot(&mut self) {
        self.bind_group = None;
        self.snapshot_key = None;
        self.pending_count = 0;
    }

    /// Stage this frame's lift/sweep instances and push the per-line accent
    /// highlights into the shared `rects` list (drawn by the rect pipeline,
    /// which runs AFTER `LiftPipeline::draw` — that ordering is what puts
    /// the highlights on top of the lifted pixels for free).
    #[allow(clippy::too_many_arguments)]
    pub fn prepare(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        viewport_px: (u32, u32),
        state: &UiSharedState,
        this_monitor: &UiMonitor,
        snapshot: Option<&DesktopSnapshot>,
        rects: &mut Vec<RectInstance>,
    ) {
        self.pending_count = 0;

        // No snapshot, or nothing OCR-shaped to draw: release the bind
        // group rather than merely skipping the draw. Holding it while
        // idle would keep the vdesktop texture pinned (see
        // `clear_snapshot`); rebuilding it on the next OCR entry is one
        // cheap create_bind_group.
        let Some(snap) = snapshot else {
            self.clear_snapshot();
            return;
        };
        if !state.ocr.active() {
            self.clear_snapshot();
            return;
        }

        // Rebuild the bind group only when the snapshot's identity changes
        // (one snapshot per cycle, so in practice: once per OCR entry).
        let key = snap as *const DesktopSnapshot as usize;
        if needs_bind_group_rebuild(self.snapshot_key, self.bind_group.is_some(), key) {
            self.bind_group = Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ui_lift bind group"),
                layout: &self.bgl,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: self.uniform_buf.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&snap.view),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: wgpu::BindingResource::Sampler(&self.sampler),
                    },
                ],
            }));
            self.snapshot_key = Some(key);
        }

        // The shared animation clock: elapsed seconds since the CURRENT
        // phase's anchor. Never this worker's own start time — every worker
        // must derive byte-identical geometry for seam-spanning lines.
        let phase_t = match &state.ocr {
            OcrState::Idle => 0.0,
            OcrState::Scanning {
                anchor,
                ..
            }
            | OcrState::Lifted {
                anchor,
                ..
            }
            | OcrState::Retracting {
                anchor,
                ..
            } => anchor.elapsed().as_secs_f32(),
        };

        let uniforms = LiftUniforms {
            viewport_px: [viewport_px.0 as f32, viewport_px.1 as f32],
            t: phase_t,
            _pad: 0.0,
        };
        queue.write_buffer(&self.uniform_buf, 0, bytemuck::bytes_of(&uniforms));

        // Source UVs come from the snapshot's own placement metadata —
        // NEVER from the live uv_offset_scale uniform, which the magnifier
        // rewrites every frame (render/desktop.rs).
        let vd_x = snap.vdesktop_origin[0];
        let vd_y = snap.vdesktop_origin[1];
        let vd_w = snap.vdesktop_size[0];
        let vd_h = snap.vdesktop_size[1];
        let mon_f = this_monitor.bounds.to_f32();
        let to_local = |r: [f32; 4]| -> [f32; 4] { [r[0] - mon_f.left(), r[1] - mon_f.top(), r[2] - mon_f.left(), r[3] - mon_f.top()] };

        self.instances.clear();

        match &state.ocr {
            // Unreachable (the !active() early-return above), kept so the
            // match stays exhaustive if a variant is ever added.
            OcrState::Idle => {}

            OcrState::Scanning {
                anchor,
                region,
                ..
            } => {
                let rf = region.to_f32();
                let dest = [rf.left(), rf.top(), rf.right(), rf.bottom()];
                if aabb_intersects(dest, mon_f.left(), mon_f.top(), mon_f.right(), mon_f.bottom()) {
                    // Band centre through sweep_band: the phase's fract()
                    // makes looping free, and the overshoot puts the wrap
                    // entirely off-screen so back-to-back passes are
                    // seamless. σ rides along in params.w — see the shader
                    // header.
                    let band = anim::sweep_band(anim::scan_phase(anchor.elapsed().as_secs_f32()));
                    self.instances.push(LiftInstance {
                        dest_px: to_local(dest),
                        src_uv: [0.0; 4],
                        params: [2.0, SWEEP_ALPHA, band, anim::SWEEP_SIGMA],
                        tint: [1.0, 1.0, 1.0, 1.0],
                    });
                }
            }

            // Exit: every crop (and bubble) vanishes AT ONCE on the first
            // Retracting frame — no reverse cascade, by explicit owner
            // call. The only exit animation is the region's colour fade,
            // which lives in the desktop pass, so this pass draws nothing.
            OcrState::Retracting {
                ..
            } => {}

            OcrState::Lifted {
                anchor,
                region,
                dpi_scale,
                outcome,
                presentation,
            } => {
                let t = anchor.elapsed().as_secs_f32();
                let rf = region.to_f32();

                // The reveal pass: when the outcome lands (the app thread
                // wrap-aligned the transition, so the band starts off-screen
                // above the region), ONE more top→bottom sweep descends and
                // each line rises exactly as the band passes it. The sweep
                // instance exists only for that single pass; the risen
                // lines outlive it.
                if t < anim::reveal_pass_secs() {
                    let dest = [rf.left(), rf.top(), rf.right(), rf.bottom()];
                    if aabb_intersects(dest, mon_f.left(), mon_f.top(), mon_f.right(), mon_f.bottom()) {
                        let band = anim::sweep_band(anim::scan_phase(t));
                        self.instances.push(LiftInstance {
                            dest_px: to_local(dest),
                            src_uv: [0.0; 4],
                            params: [2.0, SWEEP_ALPHA, band, anim::SWEEP_SIGMA],
                            tint: [1.0, 1.0, 1.0, 1.0],
                        });
                    }
                }

                // dpi_scale is the mode's ONE scale (the monitor containing
                // the region centre) — deliberately not this_monitor's. A
                // line crossing a mixed-DPI seam must move by the same
                // physical amount on both halves or it tears at the seam,
                // the exact bug the hints reticle sizing rule documents.
                let dpi = *dpi_scale;
                let n = outcome.lines.len().min(MAX_LIFT_LINES);
                for (i, line) in outcome.lines.iter().take(n).enumerate() {
                    // This pass draws only the pixel-crop FALLBACK lines:
                    // bubble lines belong to the ocr_bubbles renderer, and
                    // Hidden lines (fallbacks under a locked peek, whose
                    // pixels the snapshot texture doesn't hold) draw
                    // nowhere. A missing entry degrades to the crop — the
                    // presentation slice is built from the same outcome, so
                    // a length mismatch is a bug elsewhere, and the crop is
                    // the render that can't be WRONG, only plain.
                    match presentation
                        .get(i)
                        .copied()
                        .unwrap_or(LinePresentation::PixelCrop)
                    {
                        LinePresentation::PixelCrop => {}
                        LinePresentation::Bubble | LinePresentation::Hidden => continue,
                    }
                    let r = line.rect;
                    // Reveal keyed to the wave, not to the line index: the
                    // crop starts rising when the band centre passes its
                    // top edge.
                    let e = anim::reveal_progress(t, anim::line_rel_top(r.top(), rf.top(), rf.height()));
                    // Before its reveal moment a line shows NOTHING — the
                    // desaturated, dimmed source underneath is the "not yet
                    // scanned" state, and the crop fading in with `e` is
                    // the colour "lifting off the page" as the band
                    // crosses. (The old build emitted an opaque quad at
                    // e == 0 to keep un-lifted text bright; that fought
                    // the new whole-region grayscale, so it is gone.)
                    if e <= 0.0 {
                        continue;
                    }
                    let src = [r.left(), r.top(), r.right(), r.bottom()];
                    let dest = lifted_dest(src, e, dpi);

                    // Emit only lines whose animated quad reaches this
                    // monitor — a pure optimisation (the viewport clips
                    // anyway). This is the scope-reticle precedent, NOT the
                    // panel's single-monitor rule: the whole vdesktop
                    // texture is resident on every device, so a
                    // seam-spanning line samples correctly from both
                    // workers and each draws its half.
                    if !aabb_intersects(dest, mon_f.left(), mon_f.top(), mon_f.right(), mon_f.bottom()) {
                        continue;
                    }

                    // No shadow instance (see HIGHLIGHT_ALPHA's comment):
                    // the crop rises over an already-dark page, fading in
                    // with its rise.
                    self.instances.push(LiftInstance {
                        dest_px: to_local(dest),
                        src_uv: [
                            (src[0] - vd_x) / vd_w,
                            (src[1] - vd_y) / vd_h,
                            (src[2] - vd_x) / vd_w,
                            (src[3] - vd_y) / vd_h,
                        ],
                        params: [1.0, e, 0.0, 0.0],
                        tint: [0.0; 4],
                    });

                    // Accent highlight over the lifted pixels, via the
                    // SHARED rect list: rect.draw runs after lift.draw, so
                    // these land on top without any extra pass.
                    if e > 0.001 {
                        let ac = state.accent_color;
                        let d = to_local(dest);
                        rects.push(RectInstance {
                            dest_px: d,
                            fill_rgba: [ac[0], ac[1], ac[2], HIGHLIGHT_ALPHA * e],
                            border_rgba: [0.0; 4],
                            params: [0.0; 4],
                        });
                    }
                }
            }
        }

        if self.instances.is_empty() {
            return;
        }

        let stride = std::mem::size_of::<LiftInstance>() as u64;
        let needed = self.instances.len() as u64;
        if needed > self.instance_capacity {
            // Grow by doubling, never shrink — keeps allocations stable
            // across frames with transient spikes (same policy as rect/icon).
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
        let Some(bg) = &self.bind_group else {
            return;
        };
        rpass.set_pipeline(&self.pipeline);
        rpass.set_bind_group(0, bg, &[]);
        rpass.set_vertex_buffer(0, self.instance_buf.slice(..));
        rpass.draw(0..6, 0..self.pending_count);
    }
}

/// Animated destination of a line rect at lift progress `e`: scaled by
/// `LIFT_SCALE` about its own centre and raised by `LIFT_PX` physical px,
/// both proportional to `e`. Pure so it is testable and provably identical
/// across workers.
fn lifted_dest(rect: [f32; 4], e: f32, dpi_scale: f32) -> [f32; 4] {
    let cx = (rect[0] + rect[2]) * 0.5;
    let cy = (rect[1] + rect[3]) * 0.5;
    let scale = 1.0 + e * (anim::LIFT_SCALE - 1.0);
    let hw = (rect[2] - rect[0]) * 0.5 * scale;
    let hh = (rect[3] - rect[1]) * 0.5 * scale;
    let dy = -e * anim::LIFT_PX * dpi_scale;
    [cx - hw, cy + dy - hh, cx + hw, cy + dy + hh]
}

fn aabb_intersects(a: [f32; 4], left: f32, top: f32, right: f32, bottom: f32) -> bool {
    a[2] > left && a[0] < right && a[3] > top && a[1] < bottom
}

/// Must `prepare` (re)build the snapshot bind group this frame?
///
/// Pure and free-standing so the cache-invalidation rule is testable
/// without a GPU device — the rule is load-bearing for VRAM (a cache that
/// never invalidates pins the virtual-desktop texture across the parked
/// gap) and for correctness (a cache that invalidates too little can bind
/// a dead texture view). The `have_bind_group` term is not redundant with
/// the key: it is what guarantees that dropping the handle alone still
/// forces a rebuild, so no combination of clearing can leave `draw`
/// sampling the previous cycle's snapshot.
fn needs_bind_group_rebuild(cached_key: Option<usize>, have_bind_group: bool, key: usize) -> bool {
    cached_key != Some(key) || !have_bind_group
}

#[cfg(test)]
mod tests {
    use super::*;

    /// At rest the quad must be byte-identical to the source rect: any
    /// deviation would visibly displace the text on the first frame of the
    /// lift (and the last frame of the retract).
    #[test]
    fn lifted_dest_identity_at_zero_progress() {
        let r = [10.0, 20.0, 110.0, 40.0];
        assert_eq!(lifted_dest(r, 0.0, 1.5), r);
    }

    #[test]
    fn lifted_dest_scales_about_centre_and_raises() {
        let r = [0.0, 0.0, 100.0, 20.0];
        let d = lifted_dest(r, 1.0, 2.0);
        // Width/height grown by LIFT_SCALE, centre x unchanged (50).
        assert!((d[2] - d[0] - 100.0 * anim::LIFT_SCALE).abs() < 1e-3);
        assert!((d[3] - d[1] - 20.0 * anim::LIFT_SCALE).abs() < 1e-3);
        assert!(((d[0] + d[2]) * 0.5 - 50.0).abs() < 1e-3);
        // Centre y raised by LIFT_PX * dpi.
        assert!(((d[1] + d[3]) * 0.5 - (10.0 - anim::LIFT_PX * 2.0)).abs() < 1e-3);
    }

    /// The steady state inside one cycle: same snapshot, bind group in
    /// hand — exactly one `create_bind_group` per cycle, not one per frame.
    #[test]
    fn bind_group_is_cached_within_a_cycle() {
        assert!(!needs_bind_group_rebuild(Some(0xdead_beef), true, 0xdead_beef));
        assert!(needs_bind_group_rebuild(Some(0xdead_beef), true, 0x1234_5678));
    }

    /// After `clear_snapshot` (the state `UiRenderer::end_cycle` and
    /// `begin_cycle` leave behind) the next `prepare` MUST rebuild. If it
    /// did not, the first OCR frame of the next cycle would draw with a
    /// bind group referencing the previous cycle's virtual-desktop texture.
    #[test]
    fn cleared_cache_forces_a_rebuild() {
        assert!(needs_bind_group_rebuild(None, false, 0xdead_beef));
    }

    /// The ABA guard. The snapshot lives in a per-cycle `Arc`, so a later
    /// cycle's allocation can reuse the freed address and match a stale
    /// key. Dropping only the bind-group handle (leaving the key set) must
    /// still rebuild — and `clear_snapshot` additionally drops the key, so
    /// the two together make a recycled address unable to resurrect a dead
    /// `TextureView`.
    #[test]
    fn matching_key_without_a_bind_group_still_rebuilds() {
        assert!(needs_bind_group_rebuild(Some(0xdead_beef), false, 0xdead_beef));
    }

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
