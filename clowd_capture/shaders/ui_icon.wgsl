// Instanced textured-quad pipeline for CPU-rasterized icon atlas.
//
// Each instance places one icon from the atlas at a screen-space rect.
// Output is premultiplied alpha (tiny_skia produces premultiplied RGBA);
// pair with source-over blend (src=One, dst=OneMinusSrcAlpha).

struct Uniforms {
    viewport_px: vec2<f32>,
    _pad: vec2<f32>,
};

@group(0) @binding(0) var<uniform> u: Uniforms;
@group(0) @binding(1) var atlas_tex: texture_2d<f32>;
@group(0) @binding(2) var atlas_samp: sampler;

struct Instance {
    @location(0) dest_px: vec4<f32>,
    @location(1) uv: vec4<f32>,
    @location(2) alpha_mul: f32,
};

struct VsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) alpha_mul: f32,
};

@vertex
fn vs_main(@builtin(vertex_index) vi: u32, inst: Instance) -> VsOut {
    var corners = array<vec2<f32>, 6>(
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 0.0),
        vec2<f32>(1.0, 1.0),
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 1.0),
        vec2<f32>(0.0, 1.0),
    );
    let c = corners[vi];
    let px = mix(inst.dest_px.xy, inst.dest_px.zw, c);
    let ndc = vec2<f32>(
        px.x / u.viewport_px.x * 2.0 - 1.0,
        1.0 - px.y / u.viewport_px.y * 2.0,
    );
    let uv = mix(inst.uv.xy, inst.uv.zw, c);

    var out: VsOut;
    out.pos = vec4<f32>(ndc, 0.0, 1.0);
    out.uv = uv;
    out.alpha_mul = inst.alpha_mul;
    return out;
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let texel = textureSample(atlas_tex, atlas_samp, in.uv);
    return texel * in.alpha_mul;
}
