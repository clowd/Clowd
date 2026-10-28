// Peek pass: the un-obscured contents of the hovered window, drawn as
// one quad over the selection's interior. Knows NOTHING about the
// crosshair, the selection border or the resize handles — those passes
// draw after this one (see render/frame.rs for the order) and simply
// paint over the quad's edges, so this shader's only concerns are its
// own composite: the window texture, the ghost treatment inside
// obstruction rects, and the OCR dim that must track the desktop pass.
struct PeekUniforms {
    // Selection rect in monitor-local pixels: (left, top, right, bottom).
    selection_rect: vec4<f32>,
    // UV mapping for window texture: (u_offset, v_offset, u_scale, v_scale).
    window_uv:      vec4<f32>,
    // UV mapping for desktop texture (same space as desktop.wgsl).
    desktop_uv:     vec4<f32>,
    // (num_obstruction_rects, ghost_opacity, viewport_w, viewport_h).
    params:         vec4<f32>,
    // (ocr_dim, ocr_gray, ocr_active, 0) — same values the desktop pass
    // receives in its own ocr uniforms. The peek quad covers the desktop
    // pass inside the selection, so the OCR dim/desaturation must be
    // re-applied HERE to the pixels actually displayed; sampling the
    // desktop snapshot instead would show the OBSCURING window (the
    // snapshot has no peek composite). All zero outside OCR mode.
    ocr_params:     vec4<f32>,
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
    let ghost_opacity = u.params.y;
    let captured = ghost_opacity < 0.01;

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

    // ── OCR dim + desaturation ────────────────────────────────────
    // The identical treatment desktop.wgsl's selection interior applies
    // (desaturate in gamma-2 linear light WITHOUT the outside fade's
    // 0.42 crush, then darken), re-applied to the peek pixels actually
    // on screen. The `<= 0` guard keeps the non-OCR path byte-exact.
    let ocr_dim = clamp(u.ocr_params.x, 0.0, 1.0);
    let ocr_gray = clamp(u.ocr_params.y, 0.0, 1.0);
    if (ocr_dim > 0.0 || ocr_gray > 0.0) {
        let lin = result * result;
        let luma = vec3(dot(lin, vec3(0.2126, 0.7152, 0.0722)));
        result = sqrt(mix(lin, luma, ocr_gray)) * (1.0 - ocr_dim);
    }

    return vec4(result, 1.0);
}
