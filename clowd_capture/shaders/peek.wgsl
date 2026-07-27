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

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let px = vec2<i32>(floor(in.pos.xy));
    let ghost_opacity = u.params.y;
    let captured = ghost_opacity < 0.01;

    // ── Preserve crosshair and selection border ────────────────────
    // Only when not captured — once captured the desktop shader no
    // longer draws the crosshair, so discarding would punch holes.
    //
    // The inner thin cross is NOT discarded here: the desktop shader
    // picks its black/white contrast colour from the original
    // screenshot, which is wrong wherever the peeked window covers
    // that pixel. Thin-cross pixels fall through to the composite
    // below and are re-drawn from the peek colour actually displayed.
    var on_thin = false;
    if (!captured) {
        let mouse_x = i32(u.cursor_params.x);
        let mouse_y = i32(u.cursor_params.y);
        let dx = px.x - mouse_x;
        let dy = px.y - mouse_y;
        let adx = abs(dx);
        let ady = abs(dy);
        let scale = max(u.cursor_params.z, 1.0);
        let chunk = i32(round(50.0 * scale));
        let chunk2 = chunk * 2;
        let wide_half = clamp(i32(round(2.5 * scale)), 1, 4);

        let on_v_line = dx == 0;
        let on_h_line = dy == 0;
        on_thin = (on_v_line && ady <= chunk) || (on_h_line && adx <= chunk);
        let on_thick_v = adx <= wide_half && ady > chunk && ady <= chunk2;
        let on_thick_h = ady <= wide_half && adx > chunk && adx <= chunk2;

        if (!on_thin && (on_thick_v || on_thick_h || on_v_line || on_h_line)) {
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

    // ── Preserve resize handles (drawn by desktop shader) ─────────
    if (captured) {
        let fpos = in.pos.xy;
        let step_f = f32(half);
        let handle_r = 6.0 * step_f;
        let aa = 0.5;

        let mid_x = (f32(sx) + f32(sz)) * 0.5;
        let mid_y = (f32(sy) + f32(sw)) * 0.5;

        var hd = handle_r + aa + 1.0;
        hd = min(hd, distance(fpos, vec2<f32>(f32(sx), f32(sy))));
        hd = min(hd, distance(fpos, vec2<f32>(f32(sz), f32(sy))));
        hd = min(hd, distance(fpos, vec2<f32>(f32(sx), f32(sw))));
        hd = min(hd, distance(fpos, vec2<f32>(f32(sz), f32(sw))));
        hd = min(hd, distance(fpos, vec2<f32>(mid_x,   f32(sy))));
        hd = min(hd, distance(fpos, vec2<f32>(mid_x,   f32(sw))));
        hd = min(hd, distance(fpos, vec2<f32>(f32(sx), mid_y)));
        hd = min(hd, distance(fpos, vec2<f32>(f32(sz), mid_y)));

        if (hd < handle_r + aa) {
            discard;
        }
    }

    // ── Discard pixels outside the window texture ────────────────────
    if (in.window_uv.x < 0.0 || in.window_uv.x >= 1.0 ||
        in.window_uv.y < 0.0 || in.window_uv.y >= 1.0) {
        discard;
    }

    // ── Peek composite ─────────────────────────────────────────────
    let window_color = textureSample(window_tex, samp, in.window_uv);

    var result = window_color.rgb;
    if (!captured) {
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

        if (is_obstructed) {
            let blur_sum = textureSample(desktop_tex, samp, in.desktop_uv).rgb;
            let luma = dot(blur_sum, vec3(0.2126, 0.7152, 0.0722));
            let gray = vec3(luma);
            result = mix(gray, window_color.rgb, ghost_opacity);
        }
    }

    // Inner thin crosshair cross: same geometry and contrast rule as
    // desktop.wgsl, but the white/black decision is made against the
    // peek pixel this fragment actually displays, not the screenshot.
    if (on_thin) {
        let lum = dot(result, vec3(0.299, 0.587, 0.114));
        return select(vec4(1.0, 1.0, 1.0, 1.0), vec4(0.0, 0.0, 0.0, 1.0), lum > 0.65);
    }

    return vec4(result, 1.0);
}
