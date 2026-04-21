enable dual_source_blending;

struct VertexInput {
    @builtin(vertex_index) vertex_idx: u32,
    @location(0) pos: vec2<i32>,
    @location(1) dim: u32,
    @location(2) uv: u32,
    @location(3) color: u32,
    @location(4) content_type: u32,
    @location(5) depth: f32,
}

struct VertexOutput {
    @invariant @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) uv: vec2<f32>,
    @location(2) @interpolate(flat) content_type: u32,
}

struct Params {
    screen_resolution: vec2<u32>,
    _pad: vec2<u32>,
}

@group(0) @binding(0)
var atlas_texture: texture_2d<f32>;

@group(0) @binding(1)
var atlas_sampler: sampler;

@group(1) @binding(0)
var<uniform> params: Params;

@vertex
fn vs_main(in_vert: VertexInput) -> VertexOutput {
    var pos = in_vert.pos;
    let width = in_vert.dim & 0xffffu;
    let height = (in_vert.dim & 0xffff0000u) >> 16u;
    var uv = vec2<u32>(in_vert.uv & 0xffffu, (in_vert.uv & 0xffff0000u) >> 16u);

    let corner = vec2<u32>(
        in_vert.vertex_idx & 1u,
        (in_vert.vertex_idx >> 1u) & 1u,
    );
    let offset = vec2<u32>(width, height) * corner;
    uv = uv + offset;
    pos = pos + vec2<i32>(offset);

    var out: VertexOutput;
    out.position = vec4<f32>(
        2.0 * vec2<f32>(pos) / vec2<f32>(params.screen_resolution) - 1.0,
        in_vert.depth,
        1.0,
    );
    out.position.y *= -1.0;

    out.color = vec4<f32>(
        f32((in_vert.color & 0x00ff0000u) >> 16u) / 255.0,
        f32((in_vert.color & 0x0000ff00u) >> 8u) / 255.0,
        f32(in_vert.color & 0x000000ffu) / 255.0,
        f32((in_vert.color & 0xff000000u) >> 24u) / 255.0,
    );

    out.content_type = in_vert.content_type & 0xffffu;
    out.uv = vec2<f32>(uv) / vec2<f32>(textureDimensions(atlas_texture));

    return out;
}

struct FragOutput {
    @location(0) @blend_src(0) color: vec4<f32>,
    @location(0) @blend_src(1) blend: vec4<f32>,
}

@fragment
fn fs_main(in_frag: VertexOutput) -> FragOutput {
    var out: FragOutput;
    let tex = textureSampleLevel(atlas_texture, atlas_sampler, in_frag.uv, 0.0);

    switch in_frag.content_type {
        case 0u: {
            // Sub-pixel mask: per-channel coverage in RGB
            let rgb_cov = tex.rgb * in_frag.color.a;
            let max_cov = max(max(rgb_cov.r, rgb_cov.g), rgb_cov.b);
            out.color = vec4<f32>(in_frag.color.rgb * rgb_cov, max_cov);
            out.blend = vec4<f32>(rgb_cov, max_cov);
        }
        case 1u: {
            // Color glyph: premultiplied RGBA from atlas
            out.color = tex;
            out.blend = vec4<f32>(tex.a);
        }
        default: {
            out.color = vec4<f32>(0.0);
            out.blend = vec4<f32>(0.0);
        }
    }
    return out;
}
