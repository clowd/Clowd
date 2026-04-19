// Generic overlay quad shader — renders a CPU-baked pixmap as a textured
// quad. Each component can attach up to MAX_REGIONS rectangular regions,
// each tagged with a `mode` and an `amount`. The first matching region
// (in array order) wins for any given pixel; overlap is not blended.
//
// Modes (must match `RegionMode as u32` in src/ui/component.rs):
//   0 LIGHTEN  blend toward white by `amount` (overlay applied AFTER base alpha)
//   1 DARKEN   blend toward black by `amount`
//   2 FADE     replace the component-wide base alpha with `amount`

const MAX_REGIONS: u32 = 16u;

const MODE_LIGHTEN: u32 = 0u;
const MODE_DARKEN:  u32 = 1u;
const MODE_FADE:    u32 = 2u;

struct QuadUniforms {
    // xy = bottom-left corner of the quad in NDC (y-up).
    // zw = size in NDC (positive).
    ndc_rect: vec4<f32>,
    // x = number of active regions.
    // y = base alpha multiplier applied to the sampled pixmap
    //     (1.0 = fully opaque, <1.0 blends the whole component).
    // zw = padding.
    region_meta: vec4<f32>,
    // Region UV rects: (u_min, v_min, u_max, v_max) each.
    region_rects: array<vec4<f32>, 16>,
    // Per-region effect strength (animated). 16 floats in 4 vec4s.
    region_amounts: array<vec4<f32>, 4>,
    // Per-region mode (one of the MODE_* constants). Packed as
    // vec4<u32>; 16 modes in 4 vec4s.
    region_modes: array<vec4<u32>, 4>,
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

fn get_amount(i: u32) -> f32 {
    let vi = i / 4u;
    let ci = i % 4u;
    return u.region_amounts[vi][ci];
}

fn get_mode(i: u32) -> u32 {
    let vi = i / 4u;
    let ci = i % 4u;
    return u.region_modes[vi][ci];
}

fn rect_contains(uv: vec2<f32>, r: vec4<f32>) -> bool {
    return uv.x >= r.x && uv.x < r.z && uv.y >= r.y && uv.y < r.w;
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    var color = textureSample(tex, samp, in.uv);
    let count = u32(u.region_meta.x);

    // Walk regions once, picking the first match for this pixel.
    var matched: bool = false;
    var matched_mode: u32 = 0u;
    var matched_amount: f32 = 0.0;
    for (var i = 0u; i < count && i < MAX_REGIONS; i = i + 1u) {
        if (rect_contains(in.uv, u.region_rects[i])) {
            matched = true;
            matched_mode = get_mode(i);
            matched_amount = get_amount(i);
            break;
        }
    }

    // Apply the base-alpha multiplier. Fade replaces it; everything
    // else uses the component-wide base. Pixmap is premultiplied, so
    // scaling the whole vec4 keeps premultiplication.
    var alpha = u.region_meta.y;
    if (matched && matched_mode == MODE_FADE) {
        alpha = matched_amount;
    }
    color = color * alpha;

    // Apply the overlay (Lighten/Darken). Skipped if the region wasn't
    // matched, or if the strength rounds to zero.
    if (matched && matched_amount > 0.0) {
        if (matched_mode == MODE_LIGHTEN) {
            let a = matched_amount;
            color = vec4<f32>(
                color.rgb * (1.0 - a) + vec3<f32>(a),
                color.a   * (1.0 - a) + a
            );
        } else if (matched_mode == MODE_DARKEN) {
            let a = matched_amount;
            color = vec4<f32>(
                color.rgb * (1.0 - a),
                color.a   * (1.0 - a) + a
            );
        }
    }

    return color;
}
