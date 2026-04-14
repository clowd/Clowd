//! Backend A — CPU bake the panel to a texture, draw as a textured
//! quad in a second render pass.
//!
//! ## Strategy
//!
//! 1. **Once** (per render thread, at construction): parse all SVGs via
//!    `usvg::Tree::from_data`, load the TTF via `swash`, build a minimal
//!    textured-quad `wgpu::RenderPipeline` + bind group layout.
//!
//! 2. **On state change**: hash `(layout, selection_size, dpi, accent)`
//!    and short-circuit if unchanged. Otherwise allocate a
//!    `tiny_skia::Pixmap` sized to the panel in physical pixels at this
//!    monitor's DPI, fill backgrounds, rasterize SVGs, rasterize text,
//!    and upload the result to a wgpu texture. Hover state is excluded
//!    from the hash — it's handled by the shader.
//!
//! 3. **On render**: advance the `HoverAnimator` to compute per-button
//!    fade values, write uniforms (NDC rect + button rects + fades),
//!    begin a `LoadOp::Load` render pass, draw the cached texture as a
//!    quad. The fragment shader applies hover overlays based on fades.
//!
//! The entire bake path runs on the render thread, which is cheap —
//! the panel pixmap is at most a few thousand pixels, and tiny-skia /
//! resvg / fontdue are all pure-Rust SIMD.

use std::hash::{Hash, Hasher};
use std::sync::Arc;
use std::time::Instant;

use swash::FontRef;
use tiny_skia::{Pixmap, Transform};

use crate::geometry::{RectExt, ScreenRect};

use super::hover::HoverAnimator;
use super::model::{button_defs, NUM_SVG_BUTTONS};
use super::draw::{blit_pixmap, fill_rect, TextLine, TextRenderer};
use super::state::PanelState;

/// Font size for button labels at 100% DPI. The C++ original used 10
/// (`txtButtonLabel = 10 * myzoom` at DxScreenCapture.cpp:437); we
/// bump it by 1 here for legibility — Roboto at 10px is a hair too
/// thin to feel ClearType-crisp, 11px reads notably better.
const LABEL_FONT_PX: f32 = 11.0;

/// Font size for the area indicator digits at 100% DPI. Matches
/// `txtInfo = 12 * myzoom` at DxScreenCapture.cpp:438.
const AREA_FONT_PX: f32 = 11.0;

/// SVG icon draw size at 100% DPI (physical pixels). Matches the
/// `UNSCALED_BUTTON_ICON_SIZE` constant at DxScreenCapture.cpp:25.
const ICON_UNSCALED_PX: f32 = 26.0;

/// Gray button background ~ `#373737`. Matches `brushGray` at
/// DxScreenCapture.cpp:446.
const GRAY_RGBA: [u8; 4] = [0x37, 0x37, 0x37, 0xFF];

/// Uniform block for the textured-quad pipeline. Carries the destination
/// NDC rect plus per-button hover state for shader-based overlay.
///
/// WGSL alignment: vec4 is 16-byte aligned. We use vec4 arrays for
/// button data to match WGSL's uniform buffer layout requirements.
#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
struct QuadUniforms {
    /// Destination rect in NDC: (min_x, min_y, size_x, size_y).
    ndc_rect: [f32; 4],
    /// Button rects in texture UV coords: (u_min, v_min, u_max, v_max).
    /// 7 buttons, each as a vec4.
    button_rects: [[f32; 4]; NUM_SVG_BUTTONS],
    /// Button fade values packed into vec4s: [0-3] in first, [4-6 + pad]
    /// in second. Each fade is in [0.0, 1.0].
    button_fades_0: [f32; 4],
    button_fades_1: [f32; 4],
}

/// GPU-side resources cached for a single panel bake. Rebuilt whenever
/// the panel texture changes size.
struct CachedPanel {
    #[allow(dead_code)]
    texture: wgpu::Texture,
    bind_group: wgpu::BindGroup,
    /// Destination in window-local physical pixels (left, top, right,
    /// bottom). Converted to NDC in `render()` against the current
    /// swapchain size.
    dest_px: [f32; 4],
    /// Button rects in texture UV coords (0..1). Computed once per bake
    /// from the layout; doesn't change until the panel is re-laid-out.
    button_rects_uv: [[f32; 4]; NUM_SVG_BUTTONS],
}

pub struct BakePanelBackend {
    pipeline: wgpu::RenderPipeline,
    bgl: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    uniform_buffer: wgpu::Buffer,
    text: TextRenderer,
    /// Pre-parsed SVG trees, one per button, in `BUTTON_DEFS` order.
    svg_trees: [Arc<usvg::Tree>; NUM_SVG_BUTTONS],
    /// Cached texture + hash of the state that produced it. Skip the
    /// re-bake when the incoming state hashes to the same value.
    cached: Option<CachedPanel>,
    cached_hash: u64,
    /// Latest state we've been told about. Stored so `render()` can
    /// lazily re-bake on first-frame-after-change without forcing the
    /// app thread to double-broadcast.
    state: Option<PanelState>,
    /// Hover animation state. Owns per-button fade values that the
    /// shader uses for smooth crossfade effects. Updated each frame
    /// based on `state.hover_idx`.
    hover_animator: HoverAnimator,
    /// Last render time for computing animation delta.
    last_render_time: Option<Instant>,
}

impl BakePanelBackend {
    pub fn new(device: &wgpu::Device, surface_format: wgpu::TextureFormat) -> Self {
        // --- Parse SVGs once -----------------------------------------------
        // `resvg` re-exports `usvg`, but we import `usvg` directly so the
        // `Options<'_>` lifetime shows up without a re-export hop.
        let usvg_opts = usvg::Options::default();
        let defs = button_defs();
        let svg_trees: [Arc<usvg::Tree>; NUM_SVG_BUTTONS] = std::array::from_fn(|i| {
            match usvg::Tree::from_data(defs[i].svg_bytes, &usvg_opts) {
                Ok(t) => Arc::new(t),
                Err(e) => {
                    error!("failed to parse SVG for button {i} ({:?}): {e:?}", defs[i].action);
                    // Empty tree fallback so the array type is still
                    // populated; we'll render the background without
                    // an icon. Using `from_str` with an empty <svg/>.
                    Arc::new(
                        usvg::Tree::from_str("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", &usvg::Options::default())
                            .expect("empty SVG parses"),
                    )
                }
            }
        });

        // --- Load font once -----------------------------------------------
        // `FontRef` borrows the byte slice; since `FONT_ROBOTO` is
        // `&'static [u8]`, the FontRef is `'static` too.
        let font = FontRef::from_index(super::assets::FONT_ROBOTO, 0)
            .expect("Roboto is valid TTF at build time");
        let text = TextRenderer::new(font);

        // --- Pipeline ------------------------------------------------------
        // Quad shader generates a quad in NDC from the `ndc_rect` uniform
        // and samples the texture; alpha blending composites it over the
        // existing fullscreen pass output.
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("panel bake quad shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../../shaders/buttonpanel.wgsl").into()),
        });

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("panel bake BGL"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::VERTEX_FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: wgpu::BufferSize::new(std::mem::size_of::<QuadUniforms>() as u64),
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

        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("panel bake pipeline layout"),
            bind_group_layouts: &[Some(&bgl)],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("panel bake pipeline"),
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
                    // Pre-multiplied alpha: the pixmap is already
                    // premultiplied (tiny-skia convention), so the
                    // blend is `src + dst * (1 - src.a)`.
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
            label: Some("panel bake sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            // Nearest filtering is critical for subpixel text. The baked
            // texture contains per-RGB-channel coverage values that must
            // hit the screen pixels exactly. Linear filtering would
            // interpolate between texels and destroy the subpixel
            // rendering, making text look blurry/washed-out.
            mag_filter: wgpu::FilterMode::Nearest,
            min_filter: wgpu::FilterMode::Nearest,
            mipmap_filter: wgpu::MipmapFilterMode::Nearest,
            ..Default::default()
        });

        let uniform_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("panel bake uniforms"),
            size: std::mem::size_of::<QuadUniforms>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        Self {
            pipeline,
            bgl,
            sampler,
            uniform_buffer,
            text,
            svg_trees,
            cached: None,
            cached_hash: 0,
            state: None,
            hover_animator: HoverAnimator::new(),
            last_render_time: None,
        }
    }

    /// Hash just the fields that affect the pixmap contents. State-
    /// change messages that only differ in things the backend doesn't
    /// care about (e.g. jitter in `monitor_bounds` position that doesn't
    /// move the panel within the monitor) are still rehashed and may
    /// cause a spurious re-bake; cheap enough that we don't bother
    /// suppressing them.
    ///
    /// Note: `hover_idx` is deliberately excluded — hover state is
    /// handled by the `HoverAnimator` and rendered in the shader,
    /// so hover changes don't require a texture re-bake.
    fn state_hash(s: &PanelState) -> u64 {
        let mut h = std::collections::hash_map::DefaultHasher::new();
        let layout = &s.layout;
        layout.area_rect.left().hash(&mut h);
        layout.area_rect.top().hash(&mut h);
        layout.area_rect.right().hash(&mut h);
        layout.area_rect.bottom().hash(&mut h);
        for b in &layout.buttons {
            b.left().hash(&mut h);
            b.top().hash(&mut h);
            b.right().hash(&mut h);
            b.bottom().hash(&mut h);
        }
        // hover_idx excluded — HoverAnimator + shader handle hover overlay
        s.selection_size.0.hash(&mut h);
        s.selection_size.1.hash(&mut h);
        // DPI + accent coerced to integers for stable hashing — `f32`
        // has no `Hash` impl and bit-exact equality is fine here
        // (we just want "changed enough to re-bake").
        ((s.dpi_scale * 10000.0) as i32).hash(&mut h);
        for c in &s.accent_color {
            ((c * 10000.0) as i32).hash(&mut h);
        }
        h.finish()
    }

    /// Rasterize the whole panel into a `tiny_skia::Pixmap` at the
    /// current monitor DPI.
    ///
    /// Layout: all rects in `PanelLayout` are in virtual-desktop pixels.
    /// We translate them into pixmap-local pixels by subtracting the
    /// panel's own top-left. The pixmap is sized exactly to the panel
    /// bounding box.
    fn bake_pixmap(&mut self, s: &PanelState) -> Option<Pixmap> {
        let panel = s.layout.panel_rect;
        let w = panel.width().max(1) as u32;
        let h = panel.height().max(1) as u32;
        let mut pixmap = Pixmap::new(w, h)?;

        let to_local = |r: ScreenRect| -> (f32, f32, f32, f32) {
            let l = (r.left() - panel.left()) as f32;
            let t = (r.top() - panel.top()) as f32;
            let rt = (r.right() - panel.left()) as f32;
            let bt = (r.bottom() - panel.top()) as f32;
            (l, t, rt, bt)
        };

        // --- Button backgrounds -------------------------------------------
        let accent_u8 = [
            (s.accent_color[0].clamp(0.0, 1.0) * 255.0) as u8,
            (s.accent_color[1].clamp(0.0, 1.0) * 255.0) as u8,
            (s.accent_color[2].clamp(0.0, 1.0) * 255.0) as u8,
            (s.accent_color[3].clamp(0.0, 1.0) * 255.0) as u8,
        ];

        for (i, b) in s.layout.buttons.iter().enumerate() {
            let (l, t, r, bt) = to_local(*b);
            let def = &button_defs()[i];
            let fill = if def.primary { accent_u8 } else { GRAY_RGBA };
            fill_rect(&mut pixmap, l, t, r - l, bt - t, fill);
            // Hover overlay is now applied in the shader, not baked here.
        }

        // --- Area indicator background + contents -------------------------
        let (al, at, ar, ab) = to_local(s.layout.area_rect);
        let aw = ar - al;
        let ah = ab - at;
        fill_rect(&mut pixmap, al, at, aw, ah, GRAY_RGBA);

        // Corner brackets: `edge = floor(bw / 3)`, `line = 2` at 100%
        // DPI. See DxScreenCapture.cpp:894-902. We keep the 2 px line
        // thickness regardless of DPI because the C++ does the same
        // (it's in physical pixels).
        let line = 2.0_f32.max(1.0);
        let edge = (aw / 3.0).floor().max(1.0);
        let white_rgba = [0xFF, 0xFF, 0xFF, 0xFF];
        // top-left
        fill_rect(&mut pixmap, al, at, edge, line, white_rgba);
        fill_rect(&mut pixmap, al, at, line, edge, white_rgba);
        // top-right
        fill_rect(&mut pixmap, ar - edge, at, edge, line, white_rgba);
        fill_rect(&mut pixmap, ar - line, at, line, edge, white_rgba);
        // bottom-left
        fill_rect(&mut pixmap, al, ab - line, edge, line, white_rgba);
        fill_rect(&mut pixmap, al, ab - edge, line, edge, white_rgba);
        // bottom-right
        fill_rect(&mut pixmap, ar - edge, ab - line, edge, line, white_rgba);
        fill_rect(&mut pixmap, ar - line, ab - edge, line, edge, white_rgba);

        // --- SVG icons + labels per button --------------------------------
        let dpi = s.dpi_scale.max(0.1);
        let icon_size = (ICON_UNSCALED_PX * dpi).floor();
        let label_px = (LABEL_FONT_PX * dpi).floor();

        for (i, b) in s.layout.buttons.iter().enumerate() {
            let (l, t, r, bt) = to_local(*b);
            let bw = r - l;
            let bh = bt - t;

            // Label text metrics (we compute first so the icon can be
            // centered in the space *above* the label, matching the
            // C++ `br.top + (bh - metricsText.height) / 2 - svgIconSize / 2`
            // offset).
            let def = &button_defs()[i];
            let label_metrics = self.text.measure_line(def.label, label_px);

            // Vertical layout: equal gap above icon, between icon and
            // label, and below label. Three gaps, two stacked items
            // (icon, label) — gap = (bh - icon - label) / 3. Falls back
            // to a non-negative gap if the button is too short.
            let v_gap = ((bh - icon_size - label_metrics.height) / 3.0).max(0.0);
            let icon_left = l + (bw / 2.0) - (icon_size / 2.0);
            let icon_top = t + v_gap;

            // Rasterize the SVG into a sub-pixmap sized to the icon,
            // then blit that sub-pixmap into the main pixmap. We can't
            // render directly into the main pixmap with an arbitrary
            // transform because resvg's `render` bakes into the
            // pixmap at its natural viewbox size — we pre-scale via
            // a Transform.
            let tree = &self.svg_trees[i];
            let vb = tree.size();
            let sx = icon_size / vb.width();
            let sy = icon_size / vb.height();
            let mut icon_pm = match Pixmap::new(icon_size as u32, icon_size as u32) {
                Some(p) => p,
                None => continue,
            };
            resvg::render(tree, Transform::from_scale(sx, sy), &mut icon_pm.as_mut());
            blit_pixmap(&mut pixmap, &icon_pm, icon_left as i32, icon_top as i32);

            // Draw the label. `draw_text_line` interprets `label_y`
            // as the top of the visible cap-height box, so we just
            // pass the y where we want the visible top of the text
            // to be (no ascent-vs-cap-height compensation needed).
            let label_x = l + (bw / 2.0) - (label_metrics.width / 2.0);
            // Empirical lift: the SVG icons all have a few pixels of
            // padding inside their viewBox, so the *visible* icon is
            // smaller than its bounding box and the bottom gap looks
            // pinched compared to the others. Pull the label up by
            // ~3 logical pixels (scaled to physical) to compensate.
            let label_lift = (2.0 * dpi).round();
            let label_y = icon_top + icon_size + v_gap - label_lift;
            let underline_thickness = dpi.round().max(1.0);
            self.text.draw_text_line(
                &mut pixmap,
                TextLine {
                    text: def.label,
                    px: label_px,
                    x: label_x,
                    y: label_y,
                    rgba: white_rgba,
                    underline: Some(def.underline_idx),
                    underline_thickness,
                },
            );
        }

        // --- Area indicator text -------------------------------------------
        // Matches DxScreenCapture.cpp:884-892:
        //   width  at `br.top + bh/4 - metricsWidth.height/2`
        //   height at `br.top + bh/1.34 - metricsHeight.height/2`
        //   × glyph centered at `br.top + bh/2 - metricsX.height/2`
        let area_px = (AREA_FONT_PX * dpi).floor();
        let width_str = s.selection_size.0.to_string();
        let height_str = s.selection_size.1.to_string();
        let x_str = "\u{00D7}"; // multiplication sign
        let mw = self.text.measure_line(&width_str, area_px);
        let mh = self.text.measure_line(&height_str, area_px);
        let mx = self.text.measure_line(x_str, area_px);

        self.text.draw_text_line(
            &mut pixmap,
            TextLine {
                text: &width_str,
                px: area_px,
                x: al + (aw / 2.0) - (mw.width / 2.0),
                y: at + (ah / 4.0) - (mw.height / 2.0),
                rgba: white_rgba,
                underline: None,
                underline_thickness: 0.0,
            },
        );
        self.text.draw_text_line(
            &mut pixmap,
            TextLine {
                text: &height_str,
                px: area_px,
                x: al + (aw / 2.0) - (mh.width / 2.0),
                y: at + (ah / 1.34) - (mh.height / 2.0),
                rgba: white_rgba,
                underline: None,
                underline_thickness: 0.0,
            },
        );
        // × glyph: drawn at 70% white to match brushWhite70.
        self.text.draw_text_line(
            &mut pixmap,
            TextLine {
                text: x_str,
                px: area_px,
                x: al + (aw / 2.0) - (mx.width / 2.0),
                y: at + (ah / 2.0) - (mx.height / 2.0),
                rgba: [0xFF, 0xFF, 0xFF, (0.70 * 255.0) as u8],
                underline: None,
                underline_thickness: 0.0,
            },
        );

        Some(pixmap)
    }

}

impl BakePanelBackend {
    pub fn on_state_change(&mut self, state: Option<&PanelState>) {
        self.state = state.cloned();
        if self.state.is_none() {
            self.cached = None;
            self.cached_hash = 0;
            // Reset hover animation when panel is hidden.
            self.hover_animator = HoverAnimator::new();
            self.last_render_time = None;
        }
    }

    pub fn render(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        target_view: &wgpu::TextureView,
        monitor_size_px: (u32, u32),
    ) {
        // Clone the state so we can hand `&mut self` to `bake_pixmap`
        // (which needs to mutate `self.text`) without holding an
        // immutable borrow of `self.state` across the call. The clone
        // is cheap and only happens when we're about to re-bake
        // anyway.
        let Some(state) = self.state.clone() else {
            return;
        };

        // Advance hover animation. Compute dt from last render time.
        let now = Instant::now();
        let dt = self
            .last_render_time
            .map(|t| now.duration_since(t).as_secs_f32())
            .unwrap_or(0.0);
        self.last_render_time = Some(now);

        // Update hover state and advance animation.
        self.hover_animator
            .set_hover(state.hover_idx);
        self.hover_animator.advance(dt);

        // Re-bake if the hash changed or no texture is cached yet.
        let hash = Self::state_hash(&state);
        if self.cached.is_none() || hash != self.cached_hash {
            if let Some(pm) = self.bake_pixmap(&state) {
                let w = pm.width();
                let h = pm.height();

                let texture = device.create_texture(&wgpu::TextureDescriptor {
                    label: Some("panel bake texture"),
                    size: wgpu::Extent3d {
                        width: w,
                        height: h,
                        depth_or_array_layers: 1,
                    },
                    mip_level_count: 1,
                    sample_count: 1,
                    dimension: wgpu::TextureDimension::D2,
                    format: wgpu::TextureFormat::Rgba8Unorm,
                    usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                    view_formats: &[],
                });
                queue.write_texture(
                    wgpu::TexelCopyTextureInfo {
                        texture: &texture,
                        mip_level: 0,
                        origin: wgpu::Origin3d::ZERO,
                        aspect: wgpu::TextureAspect::All,
                    },
                    pm.data(),
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

                let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
                let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some("panel bake bind group"),
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
                            resource: wgpu::BindingResource::Sampler(&self.sampler),
                        },
                    ],
                });

                // Convert virtual-desktop panel rect → this monitor's
                // window-local physical pixels by subtracting the
                // monitor origin. The selection rect path in the
                // existing shader does the same thing for zoom=1,
                // which is always the case post-capture.
                let panel = state.layout.panel_rect;
                let mon = state.monitor_bounds;
                let dest_px = [
                    (panel.left() - mon.left()) as f32,
                    (panel.top() - mon.top()) as f32,
                    (panel.right() - mon.left()) as f32,
                    (panel.bottom() - mon.top()) as f32,
                ];

                // Compute button rects in texture UV coords (0..1).
                // These are stable until the layout changes.
                let panel_w = panel.width() as f32;
                let panel_h = panel.height() as f32;
                let button_rects_uv: [[f32; 4]; NUM_SVG_BUTTONS] = std::array::from_fn(|i| {
                    let b = state.layout.buttons[i];
                    let l = (b.left() - panel.left()) as f32 / panel_w;
                    let t = (b.top() - panel.top()) as f32 / panel_h;
                    let r = (b.right() - panel.left()) as f32 / panel_w;
                    let bt = (b.bottom() - panel.top()) as f32 / panel_h;
                    [l, t, r, bt]
                });

                self.cached = Some(CachedPanel {
                    texture,
                    bind_group,
                    dest_px,
                    button_rects_uv,
                });
                self.cached_hash = hash;
            } else {
                return;
            }
        }

        let Some(cached) = self.cached.as_ref() else {
            return;
        };

        // Convert dest_px → NDC. NDC is [-1, 1] with Y-up, our pixel
        // space is Y-down, so Y flips sign.
        let mw = monitor_size_px.0 as f32;
        let mh = monitor_size_px.1 as f32;
        let min_x = (cached.dest_px[0] / mw) * 2.0 - 1.0;
        let min_y = 1.0 - (cached.dest_px[3] / mh) * 2.0;
        let max_x = (cached.dest_px[2] / mw) * 2.0 - 1.0;
        let max_y = 1.0 - (cached.dest_px[1] / mh) * 2.0;

        // Pack button fades into two vec4s for WGSL alignment.
        // Fades come from the hover animator, not the state.
        let fades = self.hover_animator.fades();
        let uniforms = QuadUniforms {
            ndc_rect: [min_x, min_y, max_x - min_x, max_y - min_y],
            button_rects: cached.button_rects_uv,
            button_fades_0: [fades[0], fades[1], fades[2], fades[3]],
            button_fades_1: [fades[4], fades[5], fades[6], 0.0],
        };
        queue.write_buffer(&self.uniform_buffer, 0, bytemuck::bytes_of(&uniforms));

        let mut rpass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("panel bake pass"),
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
        // Two-tri quad via six vertices generated in the vertex shader.
        rpass.draw(0..6, 0..1);
    }
}

