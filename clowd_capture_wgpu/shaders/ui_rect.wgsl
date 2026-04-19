// Instanced colored-rect pipeline for the GPU UI.
//
// One instance = one rectangle with a solid fill, optional 1-pixel-integer
// border, and a lighten-toward-white amount used for hover. Output is
// premultiplied alpha; the caller's blend state must match
// (src=One, dst=OneMinusSrcAlpha).

struct Uniforms {
    viewport_px: vec2<f32>,
    // std140 padding so the struct aligns to a 16-byte boundary.
    _pad: vec2<f32>,
};

@group(0) @binding(0)
var<uniform> u: Uniforms;

struct Instance {
    // min_x, min_y, max_x, max_y in window-local physical pixels.
    @location(0) dest_px: vec4<f32>,
    // Straight-alpha fill color.
    @location(1) fill_rgba: vec4<f32>,
    // Straight-alpha border color. Alpha 0 disables the border regardless
    // of border_px.
    @location(2) border_rgba: vec4<f32>,
    // (border_px, lighten_amount, _, _). border_px=0 disables border.
    // lighten_amount in [0,1] blends fill toward white (hover effect).
    @location(3) params: vec4<f32>,
};

struct VsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) local_px: vec2<f32>,
    @location(1) size_px: vec2<f32>,
    @location(2) fill_rgba: vec4<f32>,
    @location(3) border_rgba: vec4<f32>,
    @location(4) border_px: f32,
    @location(5) lighten: f32,
};

@vertex
fn vs_main(@builtin(vertex_index) vi: u32, inst: Instance) -> VsOut {
    // Two triangles per quad, vertex order (0,0)(1,0)(1,1)(0,0)(1,1)(0,1).
    var corners = array<vec2<f32>, 6>(
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 0.0),
        vec2<f32>(1.0, 1.0),
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 1.0),
        vec2<f32>(0.0, 1.0),
    );
    let c = corners[vi];
    let min_px = inst.dest_px.xy;
    let max_px = inst.dest_px.zw;
    let size = max_px - min_px;
    let px = min_px + c * size;
    let ndc = vec2<f32>(
        px.x / u.viewport_px.x * 2.0 - 1.0,
        1.0 - px.y / u.viewport_px.y * 2.0,
    );

    var out: VsOut;
    out.pos = vec4<f32>(ndc, 0.0, 1.0);
    out.local_px = c * size;
    out.size_px = size;
    out.fill_rgba = inst.fill_rgba;
    out.border_rgba = inst.border_rgba;
    out.border_px = inst.params.x;
    out.lighten = inst.params.y;
    return out;
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let lp = in.local_px;
    let sz = in.size_px;
    let bw = in.border_px;

    var rgba = in.fill_rgba;
    if (bw > 0.0 && in.border_rgba.a > 0.0) {
        let in_border =
            lp.x < bw ||
            lp.y < bw ||
            lp.x > sz.x - bw ||
            lp.y > sz.y - bw;
        if (in_border) {
            rgba = in.border_rgba;
        }
    }

    let lit_rgb = mix(rgba.rgb, vec3<f32>(1.0, 1.0, 1.0), clamp(in.lighten, 0.0, 1.0));
    // Premultiply for the source-over blend state.
    return vec4<f32>(lit_rgb * rgba.a, rgba.a);
}
