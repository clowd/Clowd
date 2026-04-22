struct PeekUniforms {
    // Selection rect in monitor-local pixels: (left, top, right, bottom).
    selection_rect: vec4<f32>,
    // UV mapping for window texture: (u_offset, v_offset, u_scale, v_scale).
    window_uv:      vec4<f32>,
    // UV mapping for desktop texture (same space as desktop.wgsl).
    desktop_uv:     vec4<f32>,
    // (num_obstruction_rects, ghost_opacity, viewport_w, viewport_h).
    params:         vec4<f32>,
    // (cursor_x, cursor_y, dpi_scale, 0) in monitor-local pixels.
    cursor_params:  vec4<f32>,
    // Up to 16 obstruction rects in monitor-local pixels.
    obstruction_rects: array<vec4<f32>, 16>,
};

@group(0) @binding(0) var<uniform> u: PeekUniforms;
@group(0) @binding(1) var window_tex:  texture_2d<f32>;
@group(0) @binding(2) var desktop_tex: texture_2d<f32>;
@group(0) @binding(3) var samp: sampler;

struct VsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0)       window_uv:  vec2<f32>,
    @location(1)       desktop_uv: vec2<f32>,
    @location(2)       local_px:   vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) idx: u32) -> VsOut {
    var corners = array<vec2<f32>, 6>(
        vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(1.0, 1.0),
        vec2(0.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0),
    );
    let c = corners[idx];
    let vp = vec2(u.params.z, u.params.w);
    let px = mix(u.selection_rect.xy, u.selection_rect.zw, c);

    var out: VsOut;
    out.pos = vec4(px.x / vp.x * 2.0 - 1.0, 1.0 - px.y / vp.y * 2.0, 0.0, 1.0);
    out.window_uv = u.window_uv.xy + c * u.window_uv.zw;
    out.desktop_uv = u.desktop_uv.xy + (px / vp) * u.desktop_uv.zw;
    out.local_px = px;
    return out;
}

// 1D Gaussian weights for sigma = 3.5, offsets -5 .. +5.
// Normalised so the 1D array sums to 1.
const W0: f32 = 0.13298;
const W1: f32 = 0.12583;
const W2: f32 = 0.10658;
const W3: f32 = 0.08084;
const W4: f32 = 0.05493;
const W5: f32 = 0.03344;

fn gauss_1d(d: i32) -> f32 {
    switch abs(d) {
        case 0 { return W0; }
        case 1 { return W1; }
        case 2 { return W2; }
        case 3 { return W3; }
        case 4 { return W4; }
        default { return W5; }
    }
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let px = vec2<i32>(floor(in.pos.xy));
    let ghost_opacity = u.params.y;
    let captured = ghost_opacity < 0.01;

    // ── Preserve crosshair and selection border ────────────────────
    // Only when not captured — once captured the desktop shader no
    // longer draws the crosshair, so discarding would punch holes.
    if (!captured) {
        let mouse_x = i32(u.cursor_params.x);
        let mouse_y = i32(u.cursor_params.y);

        if (px.x == mouse_x || px.y == mouse_y) {
            discard;
        }
    }

    let dpi_scale = max(u.cursor_params.z, 1.0);
    let half = i32(floor(dpi_scale));
    let sr = u.selection_rect;
    let sx = i32(sr.x);
    let sy = i32(sr.y);
    let sz = i32(sr.z);
    let sw = i32(sr.w);
    let inner_top    = sy + half;
    let inner_bottom = sw - half - 1;
    let inner_left   = sx + half;
    let inner_right  = sz - half - 1;

    if (px.x < inner_left || px.x > inner_right ||
        px.y < inner_top  || px.y > inner_bottom) {
        discard;
    }

    // ── Discard pixels outside the window texture ────────────────────
    if (in.window_uv.x < 0.0 || in.window_uv.x >= 1.0 ||
        in.window_uv.y < 0.0 || in.window_uv.y >= 1.0) {
        discard;
    }

    // ── Peek composite ─────────────────────────────────────────────
    let window_color = textureSample(window_tex, samp, in.window_uv);

    if (captured) {
        return vec4(window_color.rgb, 1.0);
    }

    let n = i32(u.params.x);
    var is_obstructed = false;
    for (var i = 0; i < n; i++) {
        let r = u.obstruction_rects[i];
        if (in.local_px.x >= r.x && in.local_px.x < r.z &&
            in.local_px.y >= r.y && in.local_px.y < r.w) {
            is_obstructed = true;
            break;
        }
    }

    if (!is_obstructed) {
        return vec4(window_color.rgb, 1.0);
    }

    // 11x11 Gaussian blur (sigma ≈ 3.5) of desktop texture.
    let texel_size = vec2(1.0) / vec2<f32>(textureDimensions(desktop_tex));
    var blur_sum = vec3(0.0);
    for (var dy = -5; dy <= 5; dy++) {
        let wy = gauss_1d(dy);
        for (var dx = -5; dx <= 5; dx++) {
            let w = wy * gauss_1d(dx);
            let offset = vec2<f32>(f32(dx), f32(dy)) * texel_size;
            let s = textureSample(desktop_tex, samp, in.desktop_uv + offset);
            blur_sum += s.rgb * w;
        }
    }

    // Gentle grayscale — keep it readable, just softened.
    let luma = dot(blur_sum, vec3(0.2126, 0.7152, 0.0722)) * 0.82;
    let gray = vec3(luma);

    let result = mix(gray, window_color.rgb, ghost_opacity);
    return vec4(result, 1.0);
}
