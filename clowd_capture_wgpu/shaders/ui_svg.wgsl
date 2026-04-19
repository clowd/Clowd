// SVG-mesh pipeline: draws pre-tessellated (lyon) 2D triangles with
// per-icon affine transform + base opacity.
//
// Vertex layout:
//   buf 0 (step=Vertex):   @location(0) pos vec2, @location(1) color vec4
//   buf 1 (step=Instance): @location(2) offset vec2, @location(3) scale vec2,
//                          @location(4) alpha_mul f32

struct Uniforms {
    viewport_px: vec2<f32>,
    _pad: vec2<f32>,
};

@group(0) @binding(0)
var<uniform> u: Uniforms;

struct VIn {
    @location(0) pos: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) offset: vec2<f32>,
    @location(3) scale: vec2<f32>,
    @location(4) alpha_mul: f32,
};

struct VOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) color: vec4<f32>,
};

@vertex
fn vs_main(in: VIn) -> VOut {
    let px = in.pos * in.scale + in.offset;
    let ndc = vec2<f32>(
        px.x / u.viewport_px.x * 2.0 - 1.0,
        1.0 - px.y / u.viewport_px.y * 2.0,
    );
    var out: VOut;
    out.pos = vec4<f32>(ndc, 0.0, 1.0);
    out.color = vec4<f32>(in.color.rgb, in.color.a * in.alpha_mul);
    return out;
}

@fragment
fn fs_main(in: VOut) -> @location(0) vec4<f32> {
    let c = in.color;
    return vec4<f32>(c.rgb * c.a, c.a);
}
