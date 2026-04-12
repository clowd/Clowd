// Number of buttons in the panel.
const NUM_BUTTONS: u32 = 7u;
// Hover overlay opacity (30% white).
const HOVER_OPACITY: f32 = 0.30;

struct QuadUniforms {
    // xy = bottom-left corner of the quad in NDC (y-up).
    // zw = size in NDC (positive).
    ndc_rect: vec4<f32>,
    // Button rects in texture UV coords: (u_min, v_min, u_max, v_max).
    button_rects: array<vec4<f32>, 7>,
    // Button fade values packed into vec4s: [0-3] in first, [4-6 + pad] in second.
    button_fades_0: vec4<f32>,
    button_fades_1: vec4<f32>,
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
    // `c` is in quad-local coords with (0,0) = bottom-left, (1,1) = top-right.
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

// Get the fade value for button `i` from the packed vec4s.
fn get_fade(i: u32) -> f32 {
    if (i < 4u) {
        return u.button_fades_0[i];
    } else {
        return u.button_fades_1[i - 4u];
    }
}

// Check if `uv` is inside the button rect at index `i`.
fn in_button(uv: vec2<f32>, i: u32) -> bool {
    let r = u.button_rects[i];
    return uv.x >= r.x && uv.x < r.z && uv.y >= r.y && uv.y < r.w;
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    var color = textureSample(tex, samp, in.uv);

    // Apply hover overlay for any button with a non-zero fade.
    // Multiple buttons can have fades during crossfade transitions.
    for (var i = 0u; i < NUM_BUTTONS; i = i + 1u) {
        let fade = get_fade(i);
        if (fade > 0.0 && in_button(in.uv, i)) {
            // Blend 30% white at the current fade level.
            // Source-over in premultiplied space: out = src + dst * (1 - src.a)
            // src = (1, 1, 1, fade * 0.3) premultiplied = (fade*0.3, fade*0.3, fade*0.3, fade*0.3)
            let overlay_a = fade * HOVER_OPACITY;
            let overlay_rgb = vec3<f32>(overlay_a);
            color = vec4<f32>(
                color.rgb * (1.0 - overlay_a) + overlay_rgb,
                color.a * (1.0 - overlay_a) + overlay_a
            );
        }
    }

    return color;
}
