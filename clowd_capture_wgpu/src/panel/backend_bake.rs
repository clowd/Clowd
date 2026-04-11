//! Backend A — CPU bake the panel to a texture, draw as a textured
//! quad in a second render pass.
//!
//! ## Strategy
//!
//! 1. **Once** (per render thread, at construction): parse all SVGs via
//!    `usvg::Tree::from_data`, load the TTF via `fontdue::Font::from_bytes`,
//!    build a minimal textured-quad `wgpu::RenderPipeline` + bind group
//!    layout.
//!
//! 2. **On state change**: hash `(layout, hover, selection_size, dpi,
//!    accent)` and short-circuit if unchanged. Otherwise allocate a
//!    `tiny_skia::Pixmap` sized to the panel in physical pixels at this
//!    monitor's DPI, fill backgrounds, rasterize SVGs, rasterize text
//!    with fontdue, apply the hover overlay, and upload the result to a
//!    wgpu texture.
//!
//! 3. **On render**: if a texture is cached, begin a `LoadOp::Load`
//!    render pass against the swapchain view, write a small uniform
//!    block describing the panel's destination NDC rect, bind the
//!    cached texture, draw 3 vertices (fullscreen-tri clipped by the
//!    vertex shader to the panel quad).
//!
//! The entire bake path runs on the render thread, which is cheap —
//! the panel pixmap is at most a few thousand pixels, and tiny-skia /
//! resvg / fontdue are all pure-Rust SIMD.

use std::hash::{Hash, Hasher};
use std::sync::Arc;

use tiny_skia::{FillRule, Paint, Pixmap, PixmapMut, Rect as SkRect, Transform};

use crate::geometry::{RectExt, ScreenRect};

use super::model::{button_defs, NUM_SVG_BUTTONS};
use super::state::PanelState;

/// Font size for button labels at 100% DPI. Matches the C++
/// `txtButtonLabel = 10 * myzoom` at DxScreenCapture.cpp:437. The C++
/// `myzoom` is a UI-level zoom that's separate from per-monitor DPI;
/// we collapse it into the per-monitor DPI since our "myzoom" is
/// always 1.
const LABEL_FONT_PX: f32 = 10.0;

/// Font size for the area indicator digits at 100% DPI. Matches
/// `txtInfo = 12 * myzoom` at DxScreenCapture.cpp:438.
const AREA_FONT_PX: f32 = 12.0;

/// SVG icon draw size at 100% DPI (physical pixels). Matches the
/// `UNSCALED_BUTTON_ICON_SIZE` constant at DxScreenCapture.cpp:25.
const ICON_UNSCALED_PX: f32 = 26.0;

/// Gray button background ~ `#373737`. Matches `brushGray` at
/// DxScreenCapture.cpp:446.
const GRAY_RGBA: [u8; 4] = [0x37, 0x37, 0x37, 0xFF];

/// Hover overlay — 30% white. `brushWhite30` at DxScreenCapture.cpp:445.
const HOVER_RGBA: [u8; 4] = [0xFF, 0xFF, 0xFF, (0.30 * 255.0) as u8];

/// Small uniform block for the textured-quad pipeline. Carries the
/// destination NDC rect as `(min_x, min_y, size_x, size_y)`. 16-byte
/// aligned so `bytemuck` is happy without padding.
#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
struct QuadUniforms {
    ndc_rect: [f32; 4],
    _pad: [f32; 4],
}

/// GPU-side resources cached for a single panel bake. Rebuilt whenever
/// the panel texture changes size.
struct CachedPanel {
    texture: wgpu::Texture,
    bind_group: wgpu::BindGroup,
    /// Destination in window-local physical pixels (left, top, right,
    /// bottom). Converted to NDC in `render()` against the current
    /// swapchain size.
    dest_px: [f32; 4],
}

pub struct BakePanelBackend {
    pipeline: wgpu::RenderPipeline,
    bgl: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    uniform_buffer: wgpu::Buffer,
    font: fontdue::Font,
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
                        usvg::Tree::from_str(
                            "<svg xmlns=\"http://www.w3.org/2000/svg\"/>",
                            &usvg::Options::default(),
                        )
                        .expect("empty SVG parses"),
                    )
                }
            }
        });

        // --- Load font once -----------------------------------------------
        let font = fontdue::Font::from_bytes(
            super::assets::FONT_ROBOTO,
            fontdue::FontSettings::default(),
        )
        .expect("Roboto is valid TTF at build time");

        // --- Pipeline ------------------------------------------------------
        // Shader lives inline because it's tiny and we don't want a
        // second .wgsl file cluttering the directory. It generates a
        // quad in NDC from the `ndc_rect` uniform and samples the
        // texture; alpha blending composites it over the existing
        // fullscreen pass output.
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("panel bake quad shader"),
            source: wgpu::ShaderSource::Wgsl(QUAD_WGSL.into()),
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
            // Linear mag/min — the panel texture is always rendered
            // near 1:1 (it was baked at the monitor's native DPI), but
            // sub-pixel alignment at the quad edges benefits from a
            // linear filter.
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
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
            font,
            svg_trees,
            cached: None,
            cached_hash: 0,
            state: None,
        }
    }

    /// Hash just the fields that affect the pixmap contents. State-
    /// change messages that only differ in things the backend doesn't
    /// care about (e.g. jitter in `monitor_bounds` position that doesn't
    /// move the panel within the monitor) are still rehashed and may
    /// cause a spurious re-bake; cheap enough that we don't bother
    /// suppressing them.
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
        s.hover_idx.hash(&mut h);
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
    fn bake_pixmap(&self, s: &PanelState) -> Option<Pixmap> {
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
            if Some(i) == s.hover_idx {
                fill_rect(&mut pixmap, l, t, r - l, bt - t, HOVER_RGBA);
            }
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
            let label_metrics = self.measure_line(def.label, label_px);

            // Vertical layout: equal gap above icon, between icon and
            // label, and below label. Three gaps, two stacked items
            // (icon, label) — gap = (bh - icon - label) / 3. Falls back
            // to a non-negative gap if the button is too short.
            let v_gap = ((bh - icon_size - label_metrics.height) / 3.0).max(0.0);
            let icon_left =
                l + (bw / 2.0) - (icon_size / 2.0);
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
            resvg::render(
                tree,
                Transform::from_scale(sx, sy),
                &mut icon_pm.as_mut(),
            );
            blit_pixmap(&mut pixmap, &icon_pm, icon_left as i32, icon_top as i32);

            // Draw the label: rasterize each glyph via fontdue,
            // blit with per-glyph offset. Underline is a 1-logical-px
            // rect beneath the `underline_idx` character, scaled to
            // physical pixels by DPI and snapped to the grid so it
            // stays a single crisp row.
            let label_x =
                l + (bw / 2.0) - (label_metrics.width / 2.0);
            // `draw_text_line` treats `label_y` as the top of the line
            // box (top of ascent), but the visible glyphs sit `ascent -
            // cap_height` below that — for all-caps labels the empty
            // space above the caps has to be subtracted out so the
            // visible text actually lines up with our `v_gap` boundary.
            let label_ascent = self
                .font
                .horizontal_line_metrics(label_px)
                .map(|m| m.ascent)
                .unwrap_or(label_px);
            let label_top_pad = (label_ascent - label_metrics.height).max(0.0);
            // Empirical lift: the SVG icons all have a few pixels of
            // padding inside their viewBox, so the *visible* icon is
            // smaller than its bounding box and the bottom gap looks
            // pinched compared to the others. Pull the label up by
            // ~3 logical pixels (scaled to physical) to compensate.
            let label_lift = (3.0 * dpi).round();
            let label_y =
                icon_top + icon_size + v_gap - label_top_pad - label_lift;
            let underline_thickness = dpi.round().max(1.0);
            self.draw_text_line(
                &mut pixmap,
                def.label,
                label_px,
                label_x,
                label_y,
                white_rgba,
                Some(def.underline_idx),
                underline_thickness,
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
        let mw = self.measure_line(&width_str, area_px);
        let mh = self.measure_line(&height_str, area_px);
        let mx = self.measure_line(x_str, area_px);

        self.draw_text_line(
            &mut pixmap,
            &width_str,
            area_px,
            al + (aw / 2.0) - (mw.width / 2.0),
            at + (ah / 4.0) - (mw.height / 2.0),
            white_rgba,
            None,
            0.0,
        );
        self.draw_text_line(
            &mut pixmap,
            &height_str,
            area_px,
            al + (aw / 2.0) - (mh.width / 2.0),
            at + (ah / 1.34) - (mh.height / 2.0),
            white_rgba,
            None,
            0.0,
        );
        // × glyph: drawn at 70% white to match brushWhite70.
        self.draw_text_line(
            &mut pixmap,
            x_str,
            area_px,
            al + (aw / 2.0) - (mx.width / 2.0),
            at + (ah / 2.0) - (mx.height / 2.0),
            [0xFF, 0xFF, 0xFF, (0.70 * 255.0) as u8],
            None,
            0.0,
        );

        Some(pixmap)
    }

    /// Crude left-to-right horizontal advance sum — fontdue is
    /// per-glyph, we have no shaping needs for ASCII button labels.
    fn measure_line(&self, s: &str, px: f32) -> LineMetrics {
        let mut total_w = 0.0_f32;
        let mut max_h = 0.0_f32;
        for c in s.chars() {
            let (metrics, _) = self.font.rasterize(c, px);
            total_w += metrics.advance_width;
            let h = metrics.height as f32;
            if h > max_h {
                max_h = h;
            }
        }
        // Approximate: use the font's line height if characters didn't
        // report any pixel extent (e.g. spaces).
        if max_h == 0.0 {
            let lm = self.font.horizontal_line_metrics(px);
            max_h = lm.map(|m| m.new_line_size).unwrap_or(px);
        }
        LineMetrics {
            width: total_w,
            height: max_h,
        }
    }

    /// Rasterize a string character-by-character and blit each glyph's
    /// coverage data onto the pixmap at white × coverage. `underline`,
    /// if set, draws a line `underline_thickness` physical pixels tall
    /// under the char at that index. Pass `0.0` for thickness when
    /// `underline` is `None`.
    fn draw_text_line(
        &self,
        pixmap: &mut Pixmap,
        text: &str,
        px: f32,
        mut x: f32,
        y: f32,
        rgba: [u8; 4],
        underline: Option<usize>,
        underline_thickness: f32,
    ) {
        // Baseline alignment: fontdue returns glyph metrics with `ymin`
        // being the distance from the **baseline** to the bottom of
        // the glyph bitmap. For simple text blitting we can treat `y`
        // as the top-left of the line box and compute per-glyph top
        // offset as `(max_ascent - ymin - height)`. fontdue exposes
        // `horizontal_line_metrics` for that.
        let lm = self.font.horizontal_line_metrics(px).unwrap_or_else(|| {
            fontdue::LineMetrics {
                ascent: px,
                descent: 0.0,
                line_gap: 0.0,
                new_line_size: px,
            }
        });
        let ascent = lm.ascent;

        let mut underline_x_start = 0.0_f32;
        let mut underline_x_end = 0.0_f32;
        for (i, c) in text.chars().enumerate() {
            let (metrics, coverage) = self.font.rasterize(c, px);
            let top = y + (ascent - metrics.ymin as f32 - metrics.height as f32);
            let left = x + metrics.xmin as f32;
            // Truncate-to-int for the blit so the underline can use the
            // exact same integer x as the glyph bitmap below.
            let glyph_left_px = left as i32;
            blit_glyph(pixmap, &coverage, metrics.width, metrics.height, glyph_left_px, top as i32, rgba);
            if underline == Some(i) {
                // Match the glyph's actual integer pixel rect — re-rounding
                // `x + xmin` independently can shift it by a pixel relative
                // to the truncate-cast above, which made the underline
                // visibly drift from the letter on certain glyphs.
                underline_x_start = glyph_left_px as f32;
                underline_x_end = underline_x_start + metrics.width as f32;
            }
            x += metrics.advance_width;
        }
        if underline.is_some() && underline_thickness > 0.0 {
            // Snap y to an integer pixel row so the rect doesn't bleed
            // across two rows due to fractional positioning. x and width
            // are already integers from the glyph rect above.
            let uy = (y + ascent + 1.0).round();
            fill_rect(
                pixmap,
                underline_x_start,
                uy,
                underline_x_end - underline_x_start,
                underline_thickness,
                rgba,
            );
        }
    }
}

struct LineMetrics {
    width: f32,
    height: f32,
}

impl BakePanelBackend {
    pub fn on_state_change(&mut self, state: Option<&PanelState>) {
        self.state = state.cloned();
        if self.state.is_none() {
            self.cached = None;
            self.cached_hash = 0;
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
        let Some(state) = self.state.as_ref() else {
            return;
        };

        // Re-bake if the hash changed or no texture is cached yet.
        let hash = Self::state_hash(state);
        if self.cached.is_none() || hash != self.cached_hash {
            if let Some(pm) = self.bake_pixmap(state) {
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

                self.cached = Some(CachedPanel {
                    texture,
                    bind_group,
                    dest_px,
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
        let uniforms = QuadUniforms {
            ndc_rect: [min_x, min_y, max_x - min_x, max_y - min_y],
            _pad: [0.0; 4],
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

// ---------------------------------------------------------------------------
// Small helpers operating directly on Pixmap bytes. We avoid the higher-
// level `Paint`/`Path` API for simple fills + blits — it's noticeably
// faster to write premultiplied bytes directly, and we don't need path
// rendering anyway.

/// Fill an axis-aligned rect with the given sRGB colour. Premultiplies
/// before writing (tiny-skia stores premultiplied bytes). Clips to
/// pixmap bounds; silently no-ops if the rect is entirely outside.
fn fill_rect(pixmap: &mut Pixmap, x: f32, y: f32, w: f32, h: f32, rgba: [u8; 4]) {
    if w <= 0.0 || h <= 0.0 {
        return;
    }
    let pm_w = pixmap.width() as i32;
    let pm_h = pixmap.height() as i32;
    let x0 = x.floor().max(0.0) as i32;
    let y0 = y.floor().max(0.0) as i32;
    let x1 = ((x + w).ceil() as i32).min(pm_w);
    let y1 = ((y + h).ceil() as i32).min(pm_h);
    if x0 >= x1 || y0 >= y1 {
        return;
    }
    let src_a = rgba[3] as u32;
    // Premultiply: (C * A + 127) / 255 keeps rounding in the
    // middle-bin tie-breaker consistent.
    let src_r = ((rgba[0] as u32 * src_a + 127) / 255) as u8;
    let src_g = ((rgba[1] as u32 * src_a + 127) / 255) as u8;
    let src_b = ((rgba[2] as u32 * src_a + 127) / 255) as u8;
    let src_a_u8 = rgba[3];
    let data = pixmap.data_mut();
    let stride = pm_w * 4;
    for yy in y0..y1 {
        let row_start = (yy * stride) as usize;
        for xx in x0..x1 {
            let idx = row_start + (xx as usize) * 4;
            // Source-over blend in premultiplied space.
            let dst_r = data[idx] as u32;
            let dst_g = data[idx + 1] as u32;
            let dst_b = data[idx + 2] as u32;
            let dst_a = data[idx + 3] as u32;
            let inv_a = 255 - src_a as u32;
            data[idx] = (src_r as u32 + (dst_r * inv_a + 127) / 255) as u8;
            data[idx + 1] = (src_g as u32 + (dst_g * inv_a + 127) / 255) as u8;
            data[idx + 2] = (src_b as u32 + (dst_b * inv_a + 127) / 255) as u8;
            data[idx + 3] = (src_a_u8 as u32 + (dst_a * inv_a + 127) / 255) as u8;
        }
    }
}

/// Composite a smaller pre-rendered pixmap onto the main pixmap at a
/// pixel offset. Both are premultiplied sRGBA. Used for stamping
/// resvg output onto the panel.
fn blit_pixmap(dst: &mut Pixmap, src: &Pixmap, x: i32, y: i32) {
    let dst_w = dst.width() as i32;
    let dst_h = dst.height() as i32;
    let sw = src.width() as i32;
    let sh = src.height() as i32;
    let src_data = src.data();
    let dst_data = dst.data_mut();
    for sy in 0..sh {
        let dy = y + sy;
        if dy < 0 || dy >= dst_h {
            continue;
        }
        for sx in 0..sw {
            let dx = x + sx;
            if dx < 0 || dx >= dst_w {
                continue;
            }
            let si = ((sy * sw + sx) * 4) as usize;
            let di = ((dy * dst_w + dx) * 4) as usize;
            let src_a = src_data[si + 3] as u32;
            if src_a == 0 {
                continue;
            }
            let inv_a = 255 - src_a;
            dst_data[di] = (src_data[si] as u32 + (dst_data[di] as u32 * inv_a + 127) / 255) as u8;
            dst_data[di + 1] =
                (src_data[si + 1] as u32 + (dst_data[di + 1] as u32 * inv_a + 127) / 255) as u8;
            dst_data[di + 2] =
                (src_data[si + 2] as u32 + (dst_data[di + 2] as u32 * inv_a + 127) / 255) as u8;
            dst_data[di + 3] =
                (src_data[si + 3] as u32 + (dst_data[di + 3] as u32 * inv_a + 127) / 255) as u8;
        }
    }
}

/// Blit a fontdue glyph coverage buffer onto the pixmap as `rgba`
/// modulated by per-pixel coverage (0..255). Produces crisp anti-
/// aliased text.
fn blit_glyph(
    dst: &mut Pixmap,
    coverage: &[u8],
    w: usize,
    h: usize,
    x: i32,
    y: i32,
    rgba: [u8; 4],
) {
    let dst_w = dst.width() as i32;
    let dst_h = dst.height() as i32;
    let data = dst.data_mut();
    for gy in 0..(h as i32) {
        let dy = y + gy;
        if dy < 0 || dy >= dst_h {
            continue;
        }
        for gx in 0..(w as i32) {
            let dx = x + gx;
            if dx < 0 || dx >= dst_w {
                continue;
            }
            let c = coverage[(gy as usize) * w + (gx as usize)] as u32;
            if c == 0 {
                continue;
            }
            // Final alpha = rgba.a * coverage / 255, then premultiply
            // colour with final alpha for source-over.
            let final_a = (rgba[3] as u32 * c + 127) / 255;
            let sr = (rgba[0] as u32 * final_a + 127) / 255;
            let sg = (rgba[1] as u32 * final_a + 127) / 255;
            let sb = (rgba[2] as u32 * final_a + 127) / 255;
            let inv_a = 255 - final_a;
            let di = ((dy * dst_w + dx) * 4) as usize;
            data[di] = (sr + (data[di] as u32 * inv_a + 127) / 255) as u8;
            data[di + 1] = (sg + (data[di + 1] as u32 * inv_a + 127) / 255) as u8;
            data[di + 2] = (sb + (data[di + 2] as u32 * inv_a + 127) / 255) as u8;
            data[di + 3] = (final_a + (data[di + 3] as u32 * inv_a + 127) / 255) as u8;
        }
    }
}

// ---------------------------------------------------------------------------
// WGSL — tiny textured quad shader. Six vertices generate two triangles
// covering `ndc_rect`. UVs are [0, 1] across the quad. Fragment samples
// the texture and returns it as-is; alpha blend is in the pipeline.

const QUAD_WGSL: &str = r#"
struct QuadUniforms {
    // xy = bottom-left corner of the quad in NDC (y-up).
    // zw = size in NDC (positive).
    ndc_rect: vec4<f32>,
    _pad:     vec4<f32>,
};

@group(0) @binding(0) var<uniform> u: QuadUniforms;
@group(0) @binding(1) var tex: texture_2d<f32>;
@group(0) @binding(2) var samp: sampler;

struct VsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) idx: u32) -> VsOut {
    // Two triangles: (0,0)-(1,0)-(1,1) and (0,0)-(1,1)-(0,1).
    // `c` is in quad-local coords with (0,0) = bottom-left, (1,1) = top-right.
    var corners = array<vec2<f32>, 6>(
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 0.0),
        vec2<f32>(1.0, 1.0),
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 1.0),
        vec2<f32>(0.0, 1.0),
    );
    let c = corners[idx];

    let ndc = u.ndc_rect.xy + c * u.ndc_rect.zw;
    var out: VsOut;
    out.pos = vec4<f32>(ndc, 0.0, 1.0);
    // Texture v is Y-down; quad c.y is Y-up. Flip so the pixmap
    // appears right-side up in the window.
    out.uv = vec2<f32>(c.x, 1.0 - c.y);
    return out;
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    return textureSample(tex, samp, in.uv);
}
"#;

// These are unused right now but kept available for a later pass that
// wants to draw filled paths (e.g. rounded corners). Suppress warnings.
#[allow(dead_code)]
fn _keep_tiny_skia_path_imports(
    _: FillRule,
    _: Paint<'_>,
    _: PixmapMut<'_>,
    _: SkRect,
    _: Transform,
) {
}
