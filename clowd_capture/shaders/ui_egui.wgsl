// egui triangle-list pipeline (port of egui-wgpu's egui.wgsl, gamma-framebuffer path only).
// Vertex colours are gamma-space PREMULTIPLIED sRGBA (epaint::Vertex); textures are
// Rgba8Unorm holding the same encoding; the surface is BGRA8 non-sRGB, so the product
// is written raw, the same convention as ui_rect.wgsl. Pair with BlendMode::PremultipliedAlpha.

struct Locals {
    screen_size: vec2<f32>,   // POINTS (surface px / pixels_per_point)
    _pad: vec2<f32>,
};

@group(0) @binding(0) var<uniform> r_locals: Locals;
@group(0) @binding(1) var r_tex: texture_2d<f32>;
@group(0) @binding(2) var r_sampler: sampler;

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) tex_coord: vec2<f32>,
    @location(1) color: vec4<f32>,
};

fn unpack_color(color: u32) -> vec4<f32> {
    return vec4<f32>(
        f32(color & 255u),
        f32((color >> 8u) & 255u),
        f32((color >> 16u) & 255u),
        f32((color >> 24u) & 255u),
    ) / 255.0;
}

@vertex
fn vs_main(
    @location(0) a_pos: vec2<f32>,
    @location(1) a_tex_coord: vec2<f32>,
    @location(2) a_color: u32,
) -> VertexOutput {
    var out: VertexOutput;
    out.tex_coord = a_tex_coord;
    out.color = unpack_color(a_color);
    out.position = vec4<f32>(
        2.0 * a_pos.x / r_locals.screen_size.x - 1.0,
        1.0 - 2.0 * a_pos.y / r_locals.screen_size.y,
        0.0,
        1.0,
    );
    return out;
}

@fragment
fn fs_main(in: VertexOutput) -> @location(0) vec4<f32> {
    return in.color * textureSample(r_tex, r_sampler, in.tex_coord);
}
