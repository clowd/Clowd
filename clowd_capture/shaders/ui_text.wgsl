// Instanced glyph-quad pipeline over the shared glyph atlases
// (ui/gpu/glyph.rs). A behavioral port of glyphon 0.12's shader.wgsl with
// the parts this crate never used removed: no per-instance depth (always
// 0.0) and no per-instance srgb flag — glyphon's `TextAtlas::new`
// hardcodes ColorMode::Accurate, so the flag was always ConvertToLinear
// and the conversion here is unconditional. (The surface is non-srgb
// Bgra8Unorm, so the linearized values land in the framebuffer raw,
// exactly as they did under glyphon.)
//
// Instance UVs are in TEXELS; the VS divides by textureDimensions() so the
// retained instance buffers stay valid across atlas growth (the atlas
// grows in place — allocations keep their texel coordinates).
//
// Output is STRAIGHT (non-premultiplied) alpha, unlike ui_icon.wgsl; pair
// with src=SrcAlpha/dst=OneMinusSrcAlpha color blend and
// One/OneMinusSrcAlpha alpha blend.

struct Params {
    screen_resolution: vec2<u32>,
    _pad: vec2<u32>,
};

@group(0) @binding(0) var<uniform> params: Params;
@group(0) @binding(1) var color_atlas_texture: texture_2d<f32>;
@group(0) @binding(2) var mask_atlas_texture: texture_2d<f32>;
@group(0) @binding(3) var atlas_sampler: sampler;

fn srgb_to_linear(c: f32) -> f32 {
    if c <= 0.04045 {
        return c / 12.92;
    } else {
        return pow((c + 0.055) / 1.055, 2.4);
    }
}

struct Instance {
    @location(0) pos: vec2<i32>,
    // width | height << 16, in pixels.
    @location(1) dim: u32,
    // atlas x | y << 16, in texels.
    @location(2) uv: u32,
    // 0xAARRGGBB straight-alpha (cosmic_text::Color).
    @location(3) color: u32,
    // 0 = color atlas, 1 = mask atlas.
    @location(4) content_type: u32,
};

struct VsOut {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) uv: vec2<f32>,
    @location(2) @interpolate(flat) content_type: u32,
};

@vertex
fn vs_main(@builtin(vertex_index) vi: u32, inst: Instance) -> VsOut {
    let width = inst.dim & 0xffffu;
    let height = (inst.dim & 0xffff0000u) >> 16u;
    var uv = vec2<u32>(inst.uv & 0xffffu, (inst.uv & 0xffff0000u) >> 16u);

    // Two-triangle quad from the vertex index, like ui_icon.wgsl.
    var corners = array<vec2<u32>, 6>(
        vec2<u32>(0u, 0u),
        vec2<u32>(1u, 0u),
        vec2<u32>(1u, 1u),
        vec2<u32>(0u, 0u),
        vec2<u32>(1u, 1u),
        vec2<u32>(0u, 1u),
    );
    let corner_offset = vec2<u32>(width, height) * corners[vi];

    uv = uv + corner_offset;
    let pos = inst.pos + vec2<i32>(corner_offset);

    var out: VsOut;
    out.position = vec4<f32>(
        2.0 * vec2<f32>(pos) / vec2<f32>(params.screen_resolution) - 1.0,
        0.0,
        1.0,
    );
    out.position.y *= -1.0;

    out.color = vec4<f32>(
        srgb_to_linear(f32((inst.color & 0x00ff0000u) >> 16u) / 255.0),
        srgb_to_linear(f32((inst.color & 0x0000ff00u) >> 8u) / 255.0),
        srgb_to_linear(f32(inst.color & 0x000000ffu) / 255.0),
        f32((inst.color & 0xff000000u) >> 24u) / 255.0,
    );

    var dim = vec2<u32>(0u);
    switch inst.content_type {
        case 0u: {
            dim = textureDimensions(color_atlas_texture);
            break;
        }
        case 1u: {
            dim = textureDimensions(mask_atlas_texture);
            break;
        }
        default: {}
    }
    out.content_type = inst.content_type;
    out.uv = vec2<f32>(uv) / vec2<f32>(dim);
    return out;
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    switch in.content_type {
        case 0u: {
            return textureSampleLevel(color_atlas_texture, atlas_sampler, in.uv, 0.0);
        }
        case 1u: {
            return vec4<f32>(in.color.rgb, in.color.a * textureSampleLevel(mask_atlas_texture, atlas_sampler, in.uv, 0.0).x);
        }
        default: {
            return vec4<f32>(0.0);
        }
    }
}
