//! Sub-pixel text rendering pipeline.
//!
//! Replaces glyphon with a direct cosmic-text + swash pipeline that
//! rasterises glyphs with LCD sub-pixel coverage (`Format::Subpixel`)
//! and composites them via wgpu dual-source blending.
//!
//! A [`TextStack`] owns all per-device resources. Per frame: call
//! [`TextStack::update_viewport`], then `prepare` with every text area,
//! then `draw` inside a render pass.

use cosmic_text::{Buffer, CacheKey, CacheKeyFlags, Color, FontSystem};
use etagere::{size2, AllocId, BucketedAtlasAllocator};
use lru::LruCache;
use rustc_hash::FxHasher;
use std::borrow::Cow;
use std::collections::HashSet;
use std::hash::BuildHasherDefault;
use std::num::NonZeroU64;
use std::{mem, slice};
use swash::scale::{Render, ScaleContext, Source, StrikeWith};
use swash::zeno::{Format, Vector};

pub const FONT_MONO_REGULAR: &[u8] =
    include_bytes!("../../../assets/fonts/CascadiaMono-Regular.ttf");
pub const FONT_MONO_BOLD: &[u8] = include_bytes!("../../../assets/fonts/CascadiaMono-Bold.ttf");
pub const FONT_CODE_REGULAR: &[u8] =
    include_bytes!("../../../assets/fonts/CascadiaCode-Regular.ttf");
pub const FONT_CODE_BOLD: &[u8] =
    include_bytes!("../../../assets/fonts/CascadiaCode-Bold.ttf");

pub const FAMILY_MONO: &str = "Cascadia Mono";
pub const FAMILY_CODE: &str = "Cascadia Code";

// ── Public types ───────────────────────────────────────────────────

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TextBounds {
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
}

#[derive(Clone)]
pub struct TextArea<'a> {
    pub buffer: &'a Buffer,
    pub left: f32,
    pub top: f32,
    pub scale: f32,
    pub bounds: TextBounds,
    pub default_color: Color,
}

// ── Internal types ─────────────────────────────────────────────────

const CT_SUBPIXEL: u16 = 0;
const CT_COLOR: u16 = 1;

#[repr(C)]
#[derive(Clone, Copy)]
struct GlyphToRender {
    pos: [i32; 2],
    dim: [u16; 2],
    uv: [u16; 2],
    color: u32,
    content_type: u32,
    depth: f32,
}

#[derive(Clone, Copy)]
struct GlyphDetails {
    width: u16,
    height: u16,
    atlas_x: u16,
    atlas_y: u16,
    top: i16,
    left: i16,
    alloc_id: Option<AllocId>,
    content_type: u16,
}

type Hasher = BuildHasherDefault<FxHasher>;

struct Atlas {
    texture: wgpu::Texture,
    texture_view: wgpu::TextureView,
    packer: BucketedAtlasAllocator,
    size: u32,
    glyph_cache: LruCache<CacheKey, GlyphDetails, Hasher>,
    glyphs_in_use: HashSet<CacheKey, Hasher>,
    max_texture_dimension_2d: u32,
}

impl Atlas {
    const INITIAL_SIZE: u32 = 256;

    fn new(device: &wgpu::Device) -> Self {
        let max_texture_dimension_2d = device.limits().max_texture_dimension_2d;
        let size = Self::INITIAL_SIZE.min(max_texture_dimension_2d);
        let packer = BucketedAtlasAllocator::new(size2(size as i32, size as i32));
        let texture = Self::create_texture(device, size);
        let texture_view = texture.create_view(&wgpu::TextureViewDescriptor::default());

        Self {
            texture,
            texture_view,
            packer,
            size,
            glyph_cache: LruCache::unbounded_with_hasher(Hasher::default()),
            glyphs_in_use: HashSet::with_hasher(Hasher::default()),
            max_texture_dimension_2d,
        }
    }

    fn create_texture(device: &wgpu::Device, size: u32) -> wgpu::Texture {
        device.create_texture(&wgpu::TextureDescriptor {
            label: Some("text atlas"),
            size: wgpu::Extent3d {
                width: size,
                height: size,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        })
    }

    fn try_allocate(&mut self, width: usize, height: usize) -> Option<etagere::Allocation> {
        let sz = size2(width as i32, height as i32);
        loop {
            if let Some(alloc) = self.packer.allocate(sz) {
                return Some(alloc);
            }
            let (mut key, mut val) = self.glyph_cache.peek_lru()?;
            while val.alloc_id.is_none() {
                if self.glyphs_in_use.contains(key) {
                    return None;
                }
                let _ = self.glyph_cache.pop_lru();
                (key, val) = self.glyph_cache.peek_lru()?;
            }
            if self.glyphs_in_use.contains(key) {
                return None;
            }
            let (_, evicted) = self.glyph_cache.pop_lru().unwrap();
            self.packer.deallocate(evicted.alloc_id.unwrap());
        }
    }

    fn grow(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        font_system: &mut FontSystem,
        scale_context: &mut ScaleContext,
    ) -> bool {
        if self.size >= self.max_texture_dimension_2d {
            return false;
        }
        let new_size = (self.size * 2).min(self.max_texture_dimension_2d);
        self.packer.grow(size2(new_size as i32, new_size as i32));
        self.texture = Self::create_texture(device, new_size);

        for (cache_key, glyph) in self.glyph_cache.iter() {
            if glyph.alloc_id.is_none() {
                continue;
            }
            if let Some(image) = rasterize_subpixel(font_system, scale_context, *cache_key) {
                let data = normalise_to_rgba(&image);
                upload_glyph(queue, &self.texture, glyph.atlas_x, glyph.atlas_y, glyph.width, glyph.height, &data);
            }
        }

        self.texture_view = self.texture.create_view(&wgpu::TextureViewDescriptor::default());
        self.size = new_size;
        true
    }

    fn trim(&mut self) {
        self.glyphs_in_use.clear();
    }
}

// ── Rasterisation ──────────────────────────────────────────────────

fn rasterize_subpixel(
    font_system: &mut FontSystem,
    context: &mut ScaleContext,
    cache_key: CacheKey,
) -> Option<swash::scale::image::Image> {
    let font = font_system.get_font(cache_key.font_id, cache_key.font_weight)?;
    let swash_font = font.as_swash();

    let wght_var = swash_font
        .variations()
        .find_by_tag(swash::Tag::from_be_bytes(*b"wght"));

    let mut sb = context
        .builder(swash_font)
        .size(f32::from_bits(cache_key.font_size_bits))
        .hint(!cache_key.flags.contains(CacheKeyFlags::DISABLE_HINTING));
    if let Some(v) = wght_var {
        sb = sb.variations(std::iter::once(swash::Setting {
            tag: swash::Tag::from_be_bytes(*b"wght"),
            value: f32::from(cache_key.font_weight.0)
                .clamp(v.min_value(), v.max_value()),
        }));
    }
    let mut scaler = sb.build();

    let offset = if cache_key.flags.contains(CacheKeyFlags::PIXEL_FONT) {
        Vector::new(
            cache_key.x_bin.as_float().round() + 1.0,
            cache_key.y_bin.as_float().round(),
        )
    } else {
        Vector::new(cache_key.x_bin.as_float(), cache_key.y_bin.as_float())
    };

    Render::new(&[
        Source::ColorOutline(0),
        Source::ColorBitmap(StrikeWith::BestFit),
        Source::Outline,
    ])
    .format(Format::subpixel_bgra())
    .offset(offset)
    .transform(if cache_key.flags.contains(CacheKeyFlags::FAKE_ITALIC) {
        Some(swash::zeno::Transform::skew(
            swash::zeno::Angle::from_degrees(14.0),
            swash::zeno::Angle::from_degrees(0.0),
        ))
    } else {
        None
    })
    .render(&mut scaler, cache_key.glyph_id)
}

fn normalise_to_rgba(image: &swash::scale::image::Image) -> Vec<u8> {
    use swash::scale::image::Content;
    match image.content {
        Content::SubpixelMask | Content::Color => image.data.clone(),
        Content::Mask => {
            let mut rgba = Vec::with_capacity(image.data.len() * 4);
            for &a in &image.data {
                rgba.extend_from_slice(&[a, a, a, a]);
            }
            rgba
        }
    }
}

fn content_type_for(image: &swash::scale::image::Image) -> u16 {
    use swash::scale::image::Content;
    match image.content {
        Content::SubpixelMask | Content::Mask => CT_SUBPIXEL,
        Content::Color => CT_COLOR,
    }
}

fn upload_glyph(
    queue: &wgpu::Queue,
    texture: &wgpu::Texture,
    x: u16,
    y: u16,
    w: u16,
    h: u16,
    data: &[u8],
) {
    queue.write_texture(
        wgpu::TexelCopyTextureInfo {
            texture,
            mip_level: 0,
            origin: wgpu::Origin3d {
                x: x as u32,
                y: y as u32,
                z: 0,
            },
            aspect: wgpu::TextureAspect::All,
        },
        data,
        wgpu::TexelCopyBufferLayout {
            offset: 0,
            bytes_per_row: Some(w as u32 * 4),
            rows_per_image: None,
        },
        wgpu::Extent3d {
            width: w as u32,
            height: h as u32,
            depth_or_array_layers: 1,
        },
    );
}

// ── TextStack (public API) ─────────────────────────────────────────

pub struct TextStack {
    pub font_system: FontSystem,
    scale_context: ScaleContext,
    atlas: Atlas,
    bind_group_layout_atlas: wgpu::BindGroupLayout,
    #[allow(dead_code)]
    bind_group_layout_params: wgpu::BindGroupLayout,
    bind_group_atlas: wgpu::BindGroup,
    bind_group_params: wgpu::BindGroup,
    pipeline: wgpu::RenderPipeline,
    sampler: wgpu::Sampler,
    params_buffer: wgpu::Buffer,
    vertex_buffer: wgpu::Buffer,
    vertex_buffer_size: u64,
    glyph_vertices: Vec<GlyphToRender>,
}

impl TextStack {
    pub fn new(
        device: &wgpu::Device,
        _queue: &wgpu::Queue,
        surface_format: wgpu::TextureFormat,
    ) -> Self {
        let mut db = cosmic_text::fontdb::Database::new();
        db.load_font_data(FONT_MONO_REGULAR.to_vec());
        db.load_font_data(FONT_MONO_BOLD.to_vec());
        db.load_font_data(FONT_CODE_REGULAR.to_vec());
        db.load_font_data(FONT_CODE_BOLD.to_vec());
        let font_system = FontSystem::new_with_locale_and_db("en-US".to_string(), db);

        let scale_context = ScaleContext::new();
        let atlas = Atlas::new(device);

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("text sampler"),
            min_filter: wgpu::FilterMode::Nearest,
            mag_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("text shader"),
            source: wgpu::ShaderSource::Wgsl(Cow::Borrowed(include_str!(
                "../../../shaders/ui_text.wgsl"
            ))),
        });

        let bind_group_layout_atlas =
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("text atlas layout"),
                entries: &[
                    wgpu::BindGroupLayoutEntry {
                        binding: 0,
                        visibility: wgpu::ShaderStages::VERTEX | wgpu::ShaderStages::FRAGMENT,
                        ty: wgpu::BindingType::Texture {
                            multisampled: false,
                            view_dimension: wgpu::TextureViewDimension::D2,
                            sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        },
                        count: None,
                    },
                    wgpu::BindGroupLayoutEntry {
                        binding: 1,
                        visibility: wgpu::ShaderStages::FRAGMENT,
                        ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                        count: None,
                    },
                ],
            });

        let bind_group_layout_params =
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("text params layout"),
                entries: &[wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::VERTEX,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: NonZeroU64::new(16),
                    },
                    count: None,
                }],
            });

        let params_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("text params"),
            size: 16,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let bind_group_atlas = Self::create_atlas_bind_group(
            device,
            &bind_group_layout_atlas,
            &atlas.texture_view,
            &sampler,
        );

        let bind_group_params = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("text params bg"),
            layout: &bind_group_layout_params,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: params_buffer.as_entire_binding(),
            }],
        });

        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("text pipeline layout"),
            bind_group_layouts: &[Some(&bind_group_layout_atlas), Some(&bind_group_layout_params)],
            ..Default::default()
        });

        let vertex_buffer_layout = wgpu::VertexBufferLayout {
            array_stride: mem::size_of::<GlyphToRender>() as wgpu::BufferAddress,
            step_mode: wgpu::VertexStepMode::Instance,
            attributes: &[
                wgpu::VertexAttribute { format: wgpu::VertexFormat::Sint32x2, offset: 0, shader_location: 0 },
                wgpu::VertexAttribute { format: wgpu::VertexFormat::Uint32, offset: 8, shader_location: 1 },
                wgpu::VertexAttribute { format: wgpu::VertexFormat::Uint32, offset: 12, shader_location: 2 },
                wgpu::VertexAttribute { format: wgpu::VertexFormat::Uint32, offset: 16, shader_location: 3 },
                wgpu::VertexAttribute { format: wgpu::VertexFormat::Uint32, offset: 20, shader_location: 4 },
                wgpu::VertexAttribute { format: wgpu::VertexFormat::Float32, offset: 24, shader_location: 5 },
            ],
        };

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("text pipeline"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                buffers: &[vertex_buffer_layout],
                compilation_options: wgpu::PipelineCompilationOptions::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format: surface_format,
                    blend: Some(wgpu::BlendState {
                        color: wgpu::BlendComponent {
                            src_factor: wgpu::BlendFactor::One,
                            dst_factor: wgpu::BlendFactor::OneMinusSrc1,
                            operation: wgpu::BlendOperation::Add,
                        },
                        alpha: wgpu::BlendComponent {
                            src_factor: wgpu::BlendFactor::One,
                            dst_factor: wgpu::BlendFactor::OneMinusSrc1Alpha,
                            operation: wgpu::BlendOperation::Add,
                        },
                    }),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: wgpu::PipelineCompilationOptions::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology: wgpu::PrimitiveTopology::TriangleStrip,
                ..Default::default()
            },
            depth_stencil: None,
            multisample: wgpu::MultisampleState {
                count: crate::render::MSAA_SAMPLES,
                mask: !0,
                alpha_to_coverage_enabled: false,
            },
            cache: None,
            multiview_mask: None,
        });

        let vertex_buffer_size = 4096u64;
        let vertex_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("text vertices"),
            size: vertex_buffer_size,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        Self {
            font_system,
            scale_context,
            atlas,
            bind_group_layout_atlas,
            bind_group_layout_params,
            bind_group_atlas,
            bind_group_params,
            pipeline,
            sampler,
            params_buffer,
            vertex_buffer,
            vertex_buffer_size,
            glyph_vertices: Vec::new(),
        }
    }

    pub fn update_viewport(&mut self, queue: &wgpu::Queue, width: u32, height: u32) {
        let params: [u32; 4] = [width, height, 0, 0];
        queue.write_buffer(&self.params_buffer, 0, bytemuck::cast_slice(&params));
    }

    pub fn prepare(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        text_areas: &[TextArea<'_>],
    ) -> anyhow::Result<bool> {
        if text_areas.is_empty() {
            return Ok(false);
        }
        self.glyph_vertices.clear();

        for text_area in text_areas {
            let bx_min = text_area.bounds.left.max(0);
            let bx_max = text_area.bounds.right;
            let by_min = text_area.bounds.top.max(0);
            let by_max = text_area.bounds.bottom;

            let is_visible = |run: &cosmic_text::LayoutRun| {
                let y0 = (text_area.top + run.line_top * text_area.scale) as i32;
                let y1 = y0 + (run.line_height * text_area.scale) as i32;
                y0 <= by_max && by_min <= y1
            };

            let runs = text_area
                .buffer
                .layout_runs()
                .skip_while(|r| !is_visible(r))
                .take_while(is_visible);

            for run in runs {
                for glyph in run.glyphs.iter() {
                    let physical =
                        glyph.physical((text_area.left, text_area.top), text_area.scale);
                    let color = glyph.color_opt.unwrap_or(text_area.default_color);

                    let details = self.lookup_or_rasterize(device, queue, physical.cache_key)?;
                    let details = match details {
                        Some(d) => d,
                        None => continue,
                    };

                    if details.width == 0 || details.height == 0 {
                        continue;
                    }

                    let mut x = physical.x + details.left as i32;
                    let mut y = (run.line_y * text_area.scale).round() as i32 + physical.y
                        - details.top as i32;
                    let mut ax = details.atlas_x;
                    let mut ay = details.atlas_y;
                    let mut w = details.width as i32;
                    let mut h = details.height as i32;

                    let mx = x + w;
                    let my = y + h;
                    if x > bx_max || mx < bx_min || y > by_max || my < by_min {
                        continue;
                    }
                    if x < bx_min {
                        let d = bx_min - x;
                        x = bx_min;
                        w = mx - bx_min;
                        ax += d as u16;
                    }
                    if x + w > bx_max {
                        w = bx_max - x;
                    }
                    if y < by_min {
                        let d = by_min - y;
                        y = by_min;
                        h = my - by_min;
                        ay += d as u16;
                    }
                    if y + h > by_max {
                        h = by_max - y;
                    }

                    self.glyph_vertices.push(GlyphToRender {
                        pos: [x, y],
                        dim: [w as u16, h as u16],
                        uv: [ax, ay],
                        color: color.0,
                        content_type: details.content_type as u32,
                        depth: 0.0,
                    });
                }
            }
        }

        if self.glyph_vertices.is_empty() {
            return Ok(false);
        }

        let raw = unsafe {
            let verts = self.glyph_vertices.as_slice();
            slice::from_raw_parts(verts as *const _ as *const u8, mem::size_of_val(verts))
        };

        if self.vertex_buffer_size >= raw.len() as u64 {
            queue.write_buffer(&self.vertex_buffer, 0, raw);
        } else {
            self.vertex_buffer.destroy();
            let new_size = (raw.len() as u64).next_power_of_two();
            self.vertex_buffer = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("text vertices"),
                size: new_size,
                usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: true,
            });
            self.vertex_buffer
                .slice(..raw.len() as u64)
                .get_mapped_range_mut()
                .copy_from_slice(raw);
            self.vertex_buffer.unmap();
            self.vertex_buffer_size = new_size;
        }

        Ok(true)
    }

    pub fn draw<'a>(&'a self, pass: &mut wgpu::RenderPass<'a>) {
        if self.glyph_vertices.is_empty() {
            return;
        }
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, &self.bind_group_atlas, &[]);
        pass.set_bind_group(1, &self.bind_group_params, &[]);
        pass.set_vertex_buffer(0, self.vertex_buffer.slice(..));
        pass.draw(0..4, 0..self.glyph_vertices.len() as u32);
    }

    pub fn trim(&mut self) {
        self.atlas.trim();
    }

    fn lookup_or_rasterize(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        cache_key: CacheKey,
    ) -> anyhow::Result<Option<GlyphDetails>> {
        if let Some(d) = self.atlas.glyph_cache.get(&cache_key) {
            self.atlas.glyphs_in_use.insert(cache_key);
            return Ok(Some(*d));
        }

        let image = match rasterize_subpixel(
            &mut self.font_system,
            &mut self.scale_context,
            cache_key,
        ) {
            Some(img) => img,
            None => return Ok(None),
        };

        let w = image.placement.width as u16;
        let h = image.placement.height as u16;
        let ct = content_type_for(&image);

        if w == 0 || h == 0 {
            let d = GlyphDetails {
                width: 0,
                height: 0,
                atlas_x: 0,
                atlas_y: 0,
                top: image.placement.top as i16,
                left: image.placement.left as i16,
                alloc_id: None,
                content_type: ct,
            };
            self.atlas.glyphs_in_use.insert(cache_key);
            self.atlas.glyph_cache.get_or_insert(cache_key, || d);
            return Ok(Some(d));
        }

        let data = normalise_to_rgba(&image);

        let allocation = loop {
            match self.atlas.try_allocate(w as usize, h as usize) {
                Some(a) => break a,
                None => {
                    if !self.atlas.grow(
                        device,
                        queue,
                        &mut self.font_system,
                        &mut self.scale_context,
                    ) {
                        anyhow::bail!("text atlas full");
                    }
                    self.rebind_atlas(device);
                }
            }
        };

        let ax = allocation.rectangle.min.x as u16;
        let ay = allocation.rectangle.min.y as u16;
        upload_glyph(queue, &self.atlas.texture, ax, ay, w, h, &data);

        let d = GlyphDetails {
            width: w,
            height: h,
            atlas_x: ax,
            atlas_y: ay,
            top: image.placement.top as i16,
            left: image.placement.left as i16,
            alloc_id: Some(allocation.id),
            content_type: ct,
        };
        self.atlas.glyphs_in_use.insert(cache_key);
        self.atlas.glyph_cache.get_or_insert(cache_key, || d);
        Ok(Some(d))
    }

    fn create_atlas_bind_group(
        device: &wgpu::Device,
        layout: &wgpu::BindGroupLayout,
        view: &wgpu::TextureView,
        sampler: &wgpu::Sampler,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("text atlas bg"),
            layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(view),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(sampler),
                },
            ],
        })
    }

    fn rebind_atlas(&mut self, device: &wgpu::Device) {
        self.bind_group_atlas = Self::create_atlas_bind_group(
            device,
            &self.bind_group_layout_atlas,
            &self.atlas.texture_view,
            &self.sampler,
        );
    }
}
