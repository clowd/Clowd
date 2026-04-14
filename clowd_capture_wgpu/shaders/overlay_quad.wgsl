// Generic overlay quad shader — renders a CPU-baked pixmap as a textured
// quad with per-region hover overlays. Generalization of the button panel
// shader to support up to MAX_REGIONS interactive regions.

const MAX_REGIONS: u32 = 16u;
const HOVER_OPACITY: f32 = 0.30;

struct QuadUniforms {
    // xy = bottom-left corner of the quad in NDC (y-up).
    // zw = size in NDC (positive).
    ndc_rect: vec4<f32>,
    // x = number of active overlay regions, yzw = padding.
    region_meta: vec4<f32>,
    // Overlay region UV rects: (u_min, v_min, u_max, v_max) each.
    region_rects: array<vec4<f32>, 16>,
    // Fade values packed into vec4s: 16 floats in 4 vec4s.
    region_fades: array<vec4<f32>, 4>,
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

// Get the fade value for region `i` from the packed vec4s.
fn get_fade(i: u32) -> f32 {
    let vec_idx = i / 4u;
    let comp_idx = i % 4u;
    return u.region_fades[vec_idx][comp_idx];
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    var color = textureSample(tex, samp, in.uv);

    let count = u32(u.region_meta.x);
    for (var i = 0u; i < count && i < MAX_REGIONS; i = i + 1u) {
        let fade = get_fade(i);
        if (fade > 0.0) {
            let r = u.region_rects[i];
            if (in.uv.x >= r.x && in.uv.x < r.z && in.uv.y >= r.y && in.uv.y < r.w) {
                let overlay_a = fade * HOVER_OPACITY;
                let overlay_rgb = vec3<f32>(overlay_a);
                color = vec4<f32>(
                    color.rgb * (1.0 - overlay_a) + overlay_rgb,
                    color.a * (1.0 - overlay_a) + overlay_a
                );
            }
        }
    }

    return color;
}
