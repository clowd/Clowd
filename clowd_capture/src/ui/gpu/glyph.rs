//! In-crate glyph atlas + instanced glyph-quad renderer on cosmic-text.
//!
//! Replaces glyphon so text no longer depends on a wgpu-only crate (the
//! prerequisite for the D3D11 backend). The positioning, run-visibility
//! and clipping math in [`GlyphRenderer::prepare`] replicates glyphon
//! 0.12's `text_render.rs` verbatim in behavior, so the swap is
//! pixel-identical; the color path is glyphon's `ColorMode::Accurate`
//! (the mode `TextAtlas::new` hardcodes): mask-glyph colors are
//! sRGB-to-linear converted in the shader and the color (emoji) atlas is
//! `Rgba8UnormSrgb`, with the linearized values landing raw in the
//! non-srgb `Bgra8Unorm` surface exactly as they did under glyphon.
//!
//! Structure mirrors `icon.rs`: an etagere-packed texture atlas plus an
//! instanced textured-quad pipeline with a grow-by-doubling instance
//! buffer. Differences from glyphon's atlas, by design:
//!
//!   * **Grow-only, no LRU/trim.** glyphon evicted least-recently-used
//!     glyphs under pressure and `trim()`ed unreferenced ones each frame,
//!     which is why the retained-vertex fast path had to suppress trim.
//!     Here the atlas only ever grows (doubling up to the device's max
//!     texture dimension, the same ceiling glyphon grew to), and every
//!     glyph keeps a CPU-side copy of its bitmap so growth
//!     re-uploads in place without repacking — etagere's `grow` preserves
//!     existing allocations, so retained instances (texel UVs, normalized
//!     in the VS against `textureDimensions()`) stay valid across growth.
//!   * **Reset instead of AtlasFull.** If an allocation fails at the size
//!     cap, the atlas clears its caches and packers and raises a reset
//!     flag ([`GlyphAtlas::take_reset`]) so the caller disarms retained
//!     fast paths and re-prepares; the failing prepare then retries once
//!     into the fresh atlas.

use std::collections::HashMap;

use bytemuck::{Pod, Zeroable};
use cosmic_text::{CacheKey, Color, FontSystem, SwashCache, SwashContent};

use crate::ui::gpu::text::TextArea;

const INITIAL_MASK_SIZE: u32 = 512;
const INITIAL_COLOR_SIZE: u32 = 256;

const INITIAL_INSTANCE_CAPACITY: u64 = 128;

/// One glyph quad. 24 bytes; unpacked by `shaders/ui_text.wgsl`.
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug)]
struct GlyphInstance {
    pos: [i32; 2],
    /// width | height << 16, in pixels.
    dim: u32,
    /// atlas x | y << 16, in texels.
    uv: u32,
    /// 0xAARRGGBB straight-alpha (`cosmic_text::Color`).
    color: u32,
    /// 0 = color atlas, 1 = mask atlas (matches the shader's switch).
    content_type: u32,
}

const _: () = assert!(std::mem::size_of::<GlyphInstance>() == 24);

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct Params {
    screen_resolution: [u32; 2],
    _pad: [u32; 2],
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum GlyphContent {
    /// Rgba8 color bitmap (emoji and other color glyphs).
    Color,
    /// R8 coverage mask (the normal case).
    Mask,
    /// Zero-sized image (e.g. a space); nothing to draw.
    Skip,
}

/// Cached per-glyph rasterization result. The `data` copy is what makes
/// atlas growth repack-free — see the module docs.
struct GlyphDetails {
    x: u16,
    y: u16,
    width: u16,
    height: u16,
    left: i16,
    top: i16,
    content: GlyphContent,
    data: Vec<u8>,
}

/// Borrow-free copy of the fields [`GlyphRenderer::prepare`] needs per
/// staged quad.
#[derive(Clone, Copy)]
struct GlyphSlot {
    x: u16,
    y: u16,
    width: u16,
    height: u16,
    left: i16,
    top: i16,
    content: GlyphContent,
}

/// Raised when a glyph cannot be allocated even at the device's max
/// texture dimension (the atlas size cap).
struct AtlasFull;

struct InnerAtlas {
    texture: wgpu::Texture,
    view: wgpu::TextureView,
    packer: etagere::BucketedAtlasAllocator,
    size: u32,
    format: wgpu::TextureFormat,
    channels: u32,
    label: &'static str,
}

impl InnerAtlas {
    fn new(device: &wgpu::Device, label: &'static str, format: wgpu::TextureFormat, channels: u32, size: u32) -> Self {
        let packer = etagere::BucketedAtlasAllocator::new(etagere::size2(size as i32, size as i32));
        let (texture, view) = Self::create_texture(device, label, format, size);
        Self {
            texture,
            view,
            packer,
            size,
            format,
            channels,
            label,
        }
    }

    fn create_texture(device: &wgpu::Device, label: &str, format: wgpu::TextureFormat, size: u32) -> (wgpu::Texture, wgpu::TextureView) {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some(label),
            size: wgpu::Extent3d {
                width: size,
                height: size,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        (texture, view)
    }

    fn upload(&self, queue: &wgpu::Queue, x: u32, y: u32, width: u32, height: u32, data: &[u8]) {
        queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &self.texture,
                mip_level: 0,
                origin: wgpu::Origin3d {
                    x,
                    y,
                    z: 0,
                },
                aspect: wgpu::TextureAspect::All,
            },
            data,
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(width * self.channels),
                rows_per_image: None,
            },
            wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
        );
    }
}

/// Shared glyph atlas + pipeline. One per [`super::text::TextStack`],
/// shared by its main and bubble [`GlyphRenderer`]s.
pub struct GlyphAtlas {
    mask: InnerAtlas,
    color: InnerAtlas,
    cache: HashMap<CacheKey, GlyphDetails>,
    pipeline: wgpu::RenderPipeline,
    bgl: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    params_buf: wgpu::Buffer,
    bind_group: wgpu::BindGroup,
    screen_resolution: (u32, u32),
    max_size: u32,
    /// Set by a cap-hit reset; consumed via [`Self::take_reset`].
    reset: bool,
}

impl GlyphAtlas {
    pub fn new(device: &wgpu::Device, surface_format: wgpu::TextureFormat) -> Self {
        // Same growth ceiling as glyphon's atlas (typically 16384); the
        // reset fallback below only triggers where glyphon would have hit
        // AtlasFull.
        let max_size = device.limits().max_texture_dimension_2d;
        let mask = InnerAtlas::new(
            device,
            "glyph mask atlas",
            wgpu::TextureFormat::R8Unorm,
            1,
            INITIAL_MASK_SIZE.min(max_size),
        );
        let color = InnerAtlas::new(
            device,
            "glyph color atlas",
            // Srgb: glyphon's ColorMode::Accurate color atlas — emoji
            // texels are sRGB-decoded on sample (see the module docs).
            wgpu::TextureFormat::Rgba8UnormSrgb,
            4,
            INITIAL_COLOR_SIZE.min(max_size),
        );

        let shader = crate::gpu::shaders::ui_text(device);

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ui_text bgl"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::VERTEX,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: wgpu::BufferSize::new(std::mem::size_of::<Params>() as u64),
                    },
                    count: None,
                },
                // The two atlas textures are vertex-visible too: the VS
                // reads textureDimensions() to normalize the texel UVs.
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::VERTEX | wgpu::ShaderStages::FRAGMENT,
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
                    visibility: wgpu::ShaderStages::VERTEX | wgpu::ShaderStages::FRAGMENT,
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
                    binding: 3,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });

        let params_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ui_text params"),
            size: std::mem::size_of::<Params>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("ui_text sampler"),
            min_filter: wgpu::FilterMode::Nearest,
            mag_filter: wgpu::FilterMode::Nearest,
            mipmap_filter: wgpu::MipmapFilterMode::Nearest,
            lod_min_clamp: 0.0,
            lod_max_clamp: 0.0,
            ..Default::default()
        });

        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ui_text pipeline layout"),
            bind_group_layouts: &[Some(&bgl)],
            immediate_size: 0,
        });

        let instance_layout = wgpu::VertexBufferLayout {
            array_stride: std::mem::size_of::<GlyphInstance>() as u64,
            step_mode: wgpu::VertexStepMode::Instance,
            attributes: &[
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Sint32x2,
                    offset: 0,
                    shader_location: 0,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Uint32,
                    offset: 8,
                    shader_location: 1,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Uint32,
                    offset: 12,
                    shader_location: 2,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Uint32,
                    offset: 16,
                    shader_location: 3,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Uint32,
                    offset: 20,
                    shader_location: 4,
                },
            ],
        };

        let pipeline = crate::gpu::shaders::build_pipeline(device, "ui_text pipeline", &shader, |shader| {
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("ui_text pipeline"),
                layout: Some(&pipeline_layout),
                vertex: wgpu::VertexState {
                    module: shader.vs(),
                    entry_point: Some("vs_main"),
                    buffers: &[Some(instance_layout.clone())],
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module: shader.fs(),
                    entry_point: Some("fs_main"),
                    targets: &[Some(wgpu::ColorTargetState {
                        format: surface_format,
                        // STRAIGHT alpha (glyphon's blend), not the icon
                        // pipeline's premultiplied source-over.
                        blend: Some(wgpu::BlendState {
                            color: wgpu::BlendComponent {
                                src_factor: wgpu::BlendFactor::SrcAlpha,
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
            })
        });

        let bind_group = create_bind_group(device, &bgl, &params_buf, &color.view, &mask.view, &sampler);

        Self {
            mask,
            color,
            cache: HashMap::new(),
            pipeline,
            bgl,
            sampler,
            params_buf,
            bind_group,
            screen_resolution: (0, 0),
            max_size,
            reset: false,
        }
    }

    /// Write the screen resolution the vertex shader maps pixels with.
    /// Cheap when unchanged.
    pub fn update_viewport(&mut self, queue: &wgpu::Queue, width: u32, height: u32) {
        if self.screen_resolution == (width, height) {
            return;
        }
        self.screen_resolution = (width, height);
        let params = Params {
            screen_resolution: [width, height],
            _pad: [0; 2],
        };
        queue.write_buffer(&self.params_buf, 0, bytemuck::bytes_of(&params));
    }

    /// Whether a cap-hit reset happened since the last call. The caller
    /// must treat every retained instance buffer as invalid when true.
    pub fn take_reset(&mut self) -> bool {
        std::mem::take(&mut self.reset)
    }

    /// Cap-hit fallback: drop every cached glyph and start packing from
    /// scratch. The textures keep their (stale) contents — callers must
    /// not re-issue retained instances after this, which is what the
    /// [`Self::take_reset`] flag enforces.
    fn reset(&mut self) {
        log::warn!(
            "glyph atlas full at {}px cap; resetting ({} cached glyphs dropped)",
            self.max_size,
            self.cache.len()
        );
        self.cache.clear();
        self.mask.packer = etagere::BucketedAtlasAllocator::new(etagere::size2(self.mask.size as i32, self.mask.size as i32));
        self.color.packer = etagere::BucketedAtlasAllocator::new(etagere::size2(self.color.size as i32, self.color.size as i32));
        self.reset = true;
    }

    /// Double one atlas texture (up to the cap) and re-upload its glyphs
    /// from their CPU-side copies. Allocations are preserved in place by
    /// `etagere::BucketedAtlasAllocator::grow`, so cached coordinates and
    /// retained instances stay valid.
    fn grow(&mut self, device: &wgpu::Device, queue: &wgpu::Queue, content: GlyphContent) -> bool {
        let inner = match content {
            GlyphContent::Mask => &mut self.mask,
            GlyphContent::Color => &mut self.color,
            GlyphContent::Skip => return false,
        };
        if inner.size >= self.max_size {
            return false;
        }
        let new_size = (inner.size * 2).min(self.max_size);
        log::info!("growing {} from {} to {}", inner.label, inner.size, new_size);
        inner
            .packer
            .grow(etagere::size2(new_size as i32, new_size as i32));
        let (texture, view) = InnerAtlas::create_texture(device, inner.label, inner.format, new_size);
        inner.texture = texture;
        inner.view = view;
        inner.size = new_size;
        for details in self.cache.values() {
            if details.content == content && details.width > 0 && details.height > 0 {
                inner.upload(
                    queue,
                    details.x as u32,
                    details.y as u32,
                    details.width as u32,
                    details.height as u32,
                    &details.data,
                );
            }
        }
        self.bind_group = create_bind_group(
            device,
            &self.bgl,
            &self.params_buf,
            &self.color.view,
            &self.mask.view,
            &self.sampler,
        );
        true
    }

    /// Look up (rasterizing and packing on miss) the atlas slot for one
    /// glyph. `Ok(None)` means the glyph has no image (swash returned
    /// nothing).
    fn glyph_slot(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        font_system: &mut FontSystem,
        swash_cache: &mut SwashCache,
        cache_key: CacheKey,
    ) -> Result<Option<GlyphSlot>, AtlasFull> {
        if !self.cache.contains_key(&cache_key) {
            let Some(image) = swash_cache.get_image_uncached(font_system, cache_key) else {
                return Ok(None);
            };
            let content = match image.content {
                SwashContent::Color => GlyphContent::Color,
                // Subpixel masks are not implemented (same as glyphon).
                SwashContent::Mask | SwashContent::SubpixelMask => GlyphContent::Mask,
            };
            let width = image.placement.width as u16;
            let height = image.placement.height as u16;

            let details = if width > 0 && height > 0 {
                let alloc_size = etagere::size2(width as i32, height as i32);
                let min = loop {
                    let inner = match content {
                        GlyphContent::Mask => &mut self.mask,
                        GlyphContent::Color => &mut self.color,
                        GlyphContent::Skip => unreachable!(),
                    };
                    match inner.packer.allocate(alloc_size) {
                        Some(alloc) => break alloc.rectangle.min,
                        None => {
                            if !self.grow(device, queue, content) {
                                return Err(AtlasFull);
                            }
                        }
                    }
                };
                let inner = match content {
                    GlyphContent::Mask => &self.mask,
                    GlyphContent::Color => &self.color,
                    GlyphContent::Skip => unreachable!(),
                };
                inner.upload(queue, min.x as u32, min.y as u32, width as u32, height as u32, &image.data);
                GlyphDetails {
                    x: min.x as u16,
                    y: min.y as u16,
                    width,
                    height,
                    left: image.placement.left as i16,
                    top: image.placement.top as i16,
                    content,
                    data: image.data,
                }
            } else {
                GlyphDetails {
                    x: 0,
                    y: 0,
                    width: 0,
                    height: 0,
                    left: 0,
                    top: 0,
                    content: GlyphContent::Skip,
                    data: Vec::new(),
                }
            };
            self.cache.insert(cache_key, details);
        }

        Ok(self
            .cache
            .get(&cache_key)
            .map(|d| GlyphSlot {
                x: d.x,
                y: d.y,
                width: d.width,
                height: d.height,
                left: d.left,
                top: d.top,
                content: d.content,
            }))
    }
}

fn create_bind_group(
    device: &wgpu::Device,
    bgl: &wgpu::BindGroupLayout,
    params_buf: &wgpu::Buffer,
    color_view: &wgpu::TextureView,
    mask_view: &wgpu::TextureView,
    sampler: &wgpu::Sampler,
) -> wgpu::BindGroup {
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ui_text bind group"),
        layout: bgl,
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: params_buf.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: wgpu::BindingResource::TextureView(color_view),
            },
            wgpu::BindGroupEntry {
                binding: 2,
                resource: wgpu::BindingResource::TextureView(mask_view),
            },
            wgpu::BindGroupEntry {
                binding: 3,
                resource: wgpu::BindingResource::Sampler(sampler),
            },
        ],
    })
}

/// Per-glyph clip window (glyphon's `Bounds`/`GlyphBounds`): the text
/// area's `TextBounds` clamped to the screen.
#[derive(Clone, Copy)]
struct ClipBounds {
    x_min: i32,
    x_max: i32,
    y_min: i32,
    y_max: i32,
}

/// One instanced glyph draw over the shared [`GlyphAtlas`]. Instantiated
/// twice per `TextStack` (main + OCR bubbles) so the two draws can be
/// ordered independently within the frame's layering.
///
/// The instance buffer and count are RETAINED across frames: `draw`
/// re-issues whatever the last `prepare` staged, which is what the
/// static-Lifted bubble fast path relies on.
pub struct GlyphRenderer {
    instance_buf: wgpu::Buffer,
    instance_capacity: u64,
    instances: Vec<GlyphInstance>,
    count: u32,
}

impl GlyphRenderer {
    pub fn new(device: &wgpu::Device) -> Self {
        let instance_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ui_text instance buffer"),
            size: std::mem::size_of::<GlyphInstance>() as u64 * INITIAL_INSTANCE_CAPACITY,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        Self {
            instance_buf,
            instance_capacity: INITIAL_INSTANCE_CAPACITY,
            instances: Vec::new(),
            count: 0,
        }
    }

    /// Stage every glyph of `text_areas` into the atlas and the retained
    /// instance buffer. Returns whether anything was staged.
    ///
    /// An atlas cap-hit resets the atlas and retries the whole staging
    /// once into the fresh atlas (raising the atlas reset flag for the
    /// caller — see [`GlyphAtlas::take_reset`]).
    pub fn prepare(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        atlas: &mut GlyphAtlas,
        font_system: &mut FontSystem,
        swash_cache: &mut SwashCache,
        text_areas: &[TextArea<'_>],
    ) -> bool {
        let mut attempts = 0;
        loop {
            match self.try_stage(device, queue, atlas, font_system, swash_cache, text_areas) {
                Ok(()) => break,
                Err(AtlasFull) => {
                    attempts += 1;
                    if attempts >= 2 {
                        // Even a fresh max-size atlas cannot hold this
                        // frame's glyphs. Keep the partial staging (its
                        // allocations are valid); the rest of the text is
                        // dropped this frame.
                        log::error!("glyph atlas full even after reset; some text will be missing this frame");
                        break;
                    }
                    atlas.reset();
                }
            }
        }

        self.count = self.instances.len() as u32;
        if self.instances.is_empty() {
            return false;
        }

        let stride = std::mem::size_of::<GlyphInstance>() as u64;
        let needed = self.instances.len() as u64;
        if needed > self.instance_capacity {
            let mut new_cap = self.instance_capacity.max(1);
            while new_cap < needed {
                new_cap *= 2;
            }
            self.instance_buf = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("ui_text instance buffer"),
                size: stride * new_cap,
                usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            self.instance_capacity = new_cap;
        }
        queue.write_buffer(&self.instance_buf, 0, bytemuck::cast_slice(&self.instances));
        true
    }

    /// One staging pass over `text_areas`; the layout-run walk, physical
    /// positioning and 4-edge clip replicate glyphon 0.12's
    /// `TextRenderer::prepare` exactly.
    fn try_stage(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        atlas: &mut GlyphAtlas,
        font_system: &mut FontSystem,
        swash_cache: &mut SwashCache,
        text_areas: &[TextArea<'_>],
    ) -> Result<(), AtlasFull> {
        self.instances.clear();
        let resolution = atlas.screen_resolution;

        for text_area in text_areas {
            let x_min = text_area.bounds.left.max(0);
            let y_min = text_area.bounds.top.max(0);
            let bounds = ClipBounds {
                x_min,
                x_max: text_area
                    .bounds
                    .right
                    .min(resolution.0 as i32)
                    .max(x_min),
                y_min,
                y_max: text_area
                    .bounds
                    .bottom
                    .min(resolution.1 as i32)
                    .max(y_min),
            };

            let is_run_visible = |run: &cosmic_text::LayoutRun| {
                let start_y_physical = (text_area.top + (run.line_top * text_area.scale)) as i32;
                let end_y_physical = start_y_physical + (run.line_height * text_area.scale) as i32;
                start_y_physical <= text_area.bounds.bottom && text_area.bounds.top <= end_y_physical
            };

            let layout_runs = text_area
                .buffer
                .layout_runs()
                .skip_while(|run| !is_run_visible(run))
                .take_while(is_run_visible);

            for run in layout_runs {
                for glyph in run.glyphs.iter() {
                    let physical_glyph = glyph.physical((text_area.left, text_area.top), text_area.scale);
                    let color = glyph
                        .color_opt
                        .unwrap_or(text_area.default_color);
                    let Some(slot) = atlas.glyph_slot(device, queue, font_system, swash_cache, physical_glyph.cache_key)? else {
                        continue;
                    };
                    if let Some(instance) = stage_quad(
                        &slot,
                        physical_glyph.x,
                        physical_glyph.y,
                        run.line_y,
                        text_area.scale,
                        color,
                        bounds,
                    ) {
                        self.instances.push(instance);
                    }
                }
            }
        }
        Ok(())
    }

    /// Issue the retained instances. Caller gates this on the matching
    /// `prepare`'s return (or the armed fast path), exactly as with
    /// glyphon: a renderer that staged nothing would re-issue stale
    /// vertices.
    pub fn draw(&self, atlas: &GlyphAtlas, rpass: &mut wgpu::RenderPass<'_>) {
        if self.count == 0 {
            return;
        }
        rpass.set_pipeline(&atlas.pipeline);
        rpass.set_bind_group(0, &atlas.bind_group, &[]);
        rpass.set_vertex_buffer(0, self.instance_buf.slice(..));
        rpass.draw(0..6, 0..self.count);
    }
}

/// Position + clip one glyph quad — glyphon's `prepare_glyph` tail,
/// verbatim in behavior.
fn stage_quad(
    slot: &GlyphSlot,
    phys_x: i32,
    phys_y: i32,
    line_y: f32,
    scale: f32,
    color: Color,
    bounds: ClipBounds,
) -> Option<GlyphInstance> {
    let content_type = match slot.content {
        GlyphContent::Color => 0u32,
        GlyphContent::Mask => 1u32,
        GlyphContent::Skip => return None,
    };

    let mut x = phys_x + slot.left as i32;
    let mut y = (line_y * scale).round() as i32 + phys_y - slot.top as i32;
    let mut atlas_x = slot.x;
    let mut atlas_y = slot.y;
    let mut width = slot.width as i32;
    let mut height = slot.height as i32;

    // Starts beyond right edge or ends beyond left edge
    let max_x = x + width;
    if x > bounds.x_max || max_x < bounds.x_min {
        return None;
    }

    // Starts beyond bottom edge or ends beyond top edge
    let max_y = y + height;
    if y > bounds.y_max || max_y < bounds.y_min {
        return None;
    }

    // Clip left edge
    if x < bounds.x_min {
        let right_shift = bounds.x_min - x;
        x = bounds.x_min;
        width = max_x - bounds.x_min;
        atlas_x += right_shift as u16;
    }

    // Clip right edge
    if x + width > bounds.x_max {
        width = bounds.x_max - x;
    }

    // Clip top edge
    if y < bounds.y_min {
        let bottom_shift = bounds.y_min - y;
        y = bounds.y_min;
        height = max_y - bounds.y_min;
        atlas_y += bottom_shift as u16;
    }

    // Clip bottom edge
    if y + height > bounds.y_max {
        height = bounds.y_max - y;
    }

    Some(GlyphInstance {
        pos: [x, y],
        dim: (width as u32 & 0xffff) | ((height as u32 & 0xffff) << 16),
        uv: (atlas_x as u32) | ((atlas_y as u32) << 16),
        color: color.0,
        content_type,
    })
}
