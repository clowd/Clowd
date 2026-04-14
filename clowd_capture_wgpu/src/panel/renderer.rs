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
use std::sync::{Arc, OnceLock};
use std::time::Instant;

use swash::scale::image::Content;
use swash::scale::{Render, ScaleContext, Source};
use swash::zeno::Format;
use swash::{FontRef, GlyphId};
use tiny_skia::{Pixmap, Transform};

use crate::geometry::{RectExt, ScreenRect};

use super::hover::HoverAnimator;
use super::model::{button_defs, NUM_SVG_BUTTONS};
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

/// Parameters for drawing a single line of text into a pixmap.
struct TextLine<'a> {
    text: &'a str,
    px: f32,
    x: f32,
    y: f32,
    rgba: [u8; 4],
    underline: Option<usize>,
    underline_thickness: f32,
}

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
    /// Roboto font reference. Borrows `FONT_ROBOTO` (a `&'static [u8]`)
    /// so the lifetime is `'static`. `FontRef` is `Copy`.
    font: FontRef<'static>,
    /// Reusable scaler context. Holds caches and scratch buffers for
    /// glyph rasterization — keep it across renders.
    scale_ctx: ScaleContext,
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
        let font = FontRef::from_index(super::assets::FONT_ROBOTO, 0).expect("Roboto is valid TTF at build time");
        let scale_ctx = ScaleContext::new();

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
            font,
            scale_ctx,
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
            let label_metrics = self.measure_line(def.label, label_px);

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
            self.draw_text_line(
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
        let mw = self.measure_line(&width_str, area_px);
        let mh = self.measure_line(&height_str, area_px);
        let mx = self.measure_line(x_str, area_px);

        self.draw_text_line(
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
        self.draw_text_line(
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
        self.draw_text_line(
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

    /// Sum of advance widths for the string at the given px size, plus
    /// the font's cap-height as a representative "visual" line height.
    /// We don't shape — these are short ASCII labels (button text and
    /// area-indicator digits) where ligatures and kerning aren't worth
    /// the complexity.
    fn measure_line(&self, s: &str, px: f32) -> LineMetrics {
        let charmap = self.font.charmap();
        let gm = self.font.glyph_metrics(&[]).scale(px);
        let total_w: f32 = s
            .chars()
            .map(|c| gm.advance_width(charmap.map(c)))
            .sum();
        // Use cap-height as the "visual" height of all-caps labels, so
        // the vertical layout in `bake_pixmap` keeps centering glyphs
        // by their visible extent (not the full ascent box). Falls back
        // to ascent if the font doesn't expose cap_height.
        let m = self.font.metrics(&[]).scale(px);
        let height = if m.cap_height > 0.0 { m.cap_height } else { m.ascent };
        LineMetrics {
            width: total_w,
            height,
        }
    }

    /// Rasterize a string with swash and stamp each glyph onto the
    /// pixmap with gamma-correct subpixel compositing. `underline`, if
    /// set, draws a line `underline_thickness` physical pixels tall
    /// under the char at that index. Pass `0.0` for thickness when
    /// `underline` is `None`.
    ///
    /// `y` is the top of the **visible cap-height box** — i.e. the y
    /// where the top of capital letters / digits will land. We
    /// deliberately don't use the top of the ascent box: the gap
    /// between ascent and cap-height (space reserved for diacriticals
    /// over capitals) is ~2-3 px at 12px Roboto and would make every
    /// centered string sit visually low. Pass the y you want the
    /// visible top of the text to be at and the math just works.
    fn draw_text_line(&mut self, pixmap: &mut Pixmap, tl: TextLine<'_>) {
        let TextLine {
            text,
            px,
            mut x,
            y,
            rgba,
            underline,
            underline_thickness,
        } = tl;
        let font_metrics = self.font.metrics(&[]).scale(px);
        // Baseline = top of the cap-height box + cap_height. For
        // Latin digits and uppercase letters, the bottom of the glyph
        // sits on the baseline, so this puts the visible top exactly
        // at `y`. Snapped to an integer row so hinted horizontal
        // stems land cleanly on pixel rows.
        let baseline_y: i32 = (y + font_metrics.cap_height).round() as i32;

        let charmap = self.font.charmap();
        let gm = self.font.glyph_metrics(&[]).scale(px);
        // Build the scaler once for the whole line — `Render::render`
        // is called repeatedly with the same scaler.
        let mut scaler = self
            .scale_ctx
            .builder(self.font)
            .size(px)
            .hint(true)
            .build();

        let mut underline_x_start = 0.0_f32;
        let mut underline_x_end = 0.0_f32;
        for (i, c) in text.chars().enumerate() {
            let gid: GlyphId = charmap.map(c);
            let advance = gm.advance_width(gid);

            // Pin the pen to the nearest integer pixel column. We
            // accumulate `x` at float precision (so the running pen
            // position never drifts from where the layout intends),
            // then round per-glyph to land on an integer column.
            //
            // We do NOT pass a fractional offset to swash: this is
            // standard ClearType-style rendering. Hinted TrueType
            // outlines + zeno's `Format::Subpixel` artifact badly when
            // mixed with non-integer X offsets — for `fract_x ≈ 0.5`,
            // the three subpixel rasterizations at offsets ±0.3 + 0.5
            // diverge enough that a 1-px stem ends up split across two
            // columns with chromatic fringes (the "I" in EXIT looking
            // two lines thick). Integer X + hinted outlines + LCD
            // subpixel rendering is exactly how Windows ClearType has
            // always worked.
            let pen_x = x.round() as i32;

            let image = Render::new(&[Source::Outline])
                .format(Format::Subpixel)
                .render(&mut scaler, gid);
            if let Some(image) = image {
                if image.content == Content::SubpixelMask && image.placement.width > 0 && image.placement.height > 0 {
                    // `placement.left` is the offset from the pen x to
                    // the bitmap's left edge — already accounts for
                    // the glyph's bearing. `placement.top` is the
                    // distance from the baseline UP to the top of the
                    // bitmap.
                    let blit_x = pen_x + image.placement.left;
                    let blit_y = baseline_y - image.placement.top;
                    blit_glyph_subpixel(
                        pixmap,
                        &image.data,
                        image.placement.width as usize,
                        image.placement.height as usize,
                        blit_x,
                        blit_y,
                        rgba,
                    );
                    if underline == Some(i) {
                        underline_x_start = blit_x as f32;
                        underline_x_end = underline_x_start + image.placement.width as f32;
                    }
                } else if underline == Some(i) {
                    // No bitmap (e.g. space) — fall back to advance
                    // box for underline width.
                    underline_x_start = x;
                    underline_x_end = x + advance;
                }
            }
            x += advance;
        }
        if underline.is_some() && underline_thickness > 0.0 {
            // Underline sits one row below the (already pixel-snapped)
            // baseline. x and width are already integers from the
            // glyph rect above.
            let uy = (baseline_y + 1) as f32;
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
        // (which now needs to mutate `self.scale_ctx`) without holding
        // an immutable borrow of `self.state` across the call. The
        // clone is cheap and only happens when we're about to re-bake
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
            let inv_a = 255 - src_a;
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
            dst_data[di + 1] = (src_data[si + 1] as u32 + (dst_data[di + 1] as u32 * inv_a + 127) / 255) as u8;
            dst_data[di + 2] = (src_data[si + 2] as u32 + (dst_data[di + 2] as u32 * inv_a + 127) / 255) as u8;
            dst_data[di + 3] = (src_data[si + 3] as u32 + (dst_data[di + 3] as u32 * inv_a + 127) / 255) as u8;
        }
    }
}

// ---------------------------------------------------------------------------
// ClearType-style subpixel text compositing.
//
// swash gives us a 4-byte-per-pixel buffer (`Format::Subpixel`) where
// each pixel holds three independently-rasterized coverage values at
// horizontal offsets -0.3, 0, +0.3 px stored in bytes 0, 1, 2.
// (The 4th byte — A — is never written by zeno and is left at zero.)
// Because the offset shifts the *glyph* (not the sampling grid),
// byte 0 (offset -0.3, glyph shifted left) gives the coverage at the
// *rightmost* subpixel position (blue on an RGB LCD), and byte 2
// (offset +0.3, glyph shifted right) gives the *leftmost* (red).
// `blit_glyph_subpixel` swaps R↔B when reading the coverage buffer
// to match the standard RGB subpixel layout.
//
// This is a clean three-pass rasterization, not the supersample-and-LCD-
// filter approach FreeType uses. As a result we don't need an explicit
// LCD filter — the slight overlap between the ±0.3 offsets naturally
// reduces color fringing.
//
// `blit_glyph_subpixel` then composites those per-channel coverages
// onto the pixmap *in linear light* using sRGB <-> linear LUTs. This
// is what makes small text look solid and on-weight instead of thin
// and washed out — gamma-correct compositing matters as much as
// subpixel rendering for the perceived quality.
//
// Precondition: the destination pixmap is opaque underneath the text
// (alpha = 0xFF). The panel always satisfies this — `GRAY_RGBA` and
// the accent colour are both opaque, the hover overlay leaves dst alpha
// at 0xFF, and the textured-quad pipeline blends the *whole* baked
// pixmap onto the screen with premultiplied alpha. We rely on this so
// we can read the destination RGB as plain sRGB rather than dividing
// out a fractional alpha.

fn srgb_to_linear_lut() -> &'static [f32; 256] {
    static LUT: OnceLock<[f32; 256]> = OnceLock::new();
    LUT.get_or_init(|| {
        let mut lut = [0.0f32; 256];
        for (i, slot) in lut.iter_mut().enumerate() {
            let c = i as f32 / 255.0;
            *slot = if c <= 0.04045 { c / 12.92 } else { ((c + 0.055) / 1.055).powf(2.4) };
        }
        lut
    })
}

fn linear_to_srgb_lut() -> &'static [u8; 4096] {
    static LUT: OnceLock<[u8; 4096]> = OnceLock::new();
    LUT.get_or_init(|| {
        let mut lut = [0u8; 4096];
        for (i, slot) in lut.iter_mut().enumerate() {
            let lin = i as f32 / 4095.0;
            let srgb = if lin <= 0.003_130_8 {
                lin * 12.92
            } else {
                1.055 * lin.powf(1.0 / 2.4) - 0.055
            };
            *slot = (srgb.clamp(0.0, 1.0) * 255.0 + 0.5) as u8;
        }
        lut
    })
}

/// Composite a swash subpixel mask onto the pixmap, blending in
/// linear light. The input buffer is `Format::Subpixel` from
/// `swash::Render`: 4 bytes per pixel `[R, G, B, _A]` where R/G/B are
/// per-channel coverage values and the A byte is unused (always 0).
/// We treat each of R, G, B as the alpha for that channel of the
/// destination pixel.
///
/// Assumes the destination pixel is opaque (alpha = 0xFF) — see the
/// module-level comment for why that always holds inside the panel.
fn blit_glyph_subpixel(dst: &mut Pixmap, coverage_rgba: &[u8], w: usize, h: usize, x: i32, y: i32, text_rgba: [u8; 4]) {
    if w == 0 || h == 0 {
        return;
    }
    let dst_w = dst.width() as i32;
    let dst_h = dst.height() as i32;
    let data = dst.data_mut();
    let s2l = srgb_to_linear_lut();
    let l2s = linear_to_srgb_lut();

    let text_alpha = text_rgba[3] as f32 / 255.0;
    let text_lin = [s2l[text_rgba[0] as usize], s2l[text_rgba[1] as usize], s2l[text_rgba[2] as usize]];

    let src_stride = w * 4;
    for gy in 0..(h as i32) {
        let dy = y + gy;
        if dy < 0 || dy >= dst_h {
            continue;
        }
        let row_off = (gy as usize) * src_stride;
        for gx in 0..(w as i32) {
            let dx = x + gx;
            if dx < 0 || dx >= dst_w {
                continue;
            }
            let pix_off = row_off + (gx as usize) * 4;
            // zeno's Format::Subpixel rasterizes at offsets [-0.3, 0, +0.3]
            // and stores coverage in bytes [0, 1, 2]. Offset -0.3 shifts
            // the glyph LEFT, so each pixel samples 0.3 px to the RIGHT of
            // the glyph — that's the BLUE physical subpixel on an RGB LCD.
            // Offset +0.3 shifts RIGHT → pixel samples LEFT → RED subpixel.
            // So byte 0 = blue coverage, byte 2 = red coverage — we must
            // swap R and B to match the standard RGB subpixel layout.
            let cov_r = coverage_rgba[pix_off + 2] as f32 * (1.0 / 255.0) * text_alpha;
            let cov_g = coverage_rgba[pix_off + 1] as f32 * (1.0 / 255.0) * text_alpha;
            let cov_b = coverage_rgba[pix_off] as f32 * (1.0 / 255.0) * text_alpha;
            if cov_r == 0.0 && cov_g == 0.0 && cov_b == 0.0 {
                continue;
            }
            let di = ((dy * dst_w + dx) * 4) as usize;
            // Dst is premultiplied sRGB. Because dst alpha is 0xFF here
            // (panel precondition), the premultiplied bytes equal the
            // straight-sRGB bytes, so we can index the LUT directly.
            let dst_lin = [s2l[data[di] as usize], s2l[data[di + 1] as usize], s2l[data[di + 2] as usize]];
            let out_lin = [
                text_lin[0] * cov_r + dst_lin[0] * (1.0 - cov_r),
                text_lin[1] * cov_g + dst_lin[1] * (1.0 - cov_g),
                text_lin[2] * cov_b + dst_lin[2] * (1.0 - cov_b),
            ];
            // Quantize linear -> 12-bit LUT index, then look up the
            // sRGB-encoded byte. clamp() handles tiny float overshoot
            // from rounding in the lerp.
            data[di] = l2s[(out_lin[0].clamp(0.0, 1.0) * 4095.0) as usize];
            data[di + 1] = l2s[(out_lin[1].clamp(0.0, 1.0) * 4095.0) as usize];
            data[di + 2] = l2s[(out_lin[2].clamp(0.0, 1.0) * 4095.0) as usize];
            // Leave alpha alone — dst stays opaque so the textured quad
            // fully replaces what's underneath when it's blended onto
            // the swapchain.
        }
    }
}
