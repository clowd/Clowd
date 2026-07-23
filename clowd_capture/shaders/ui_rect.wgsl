// Instanced colored-rect pipeline for the GPU UI.
//
// One instance = one rectangle with a solid fill, optional 1-pixel-integer
// border, and a lighten-toward-white amount used for hover. Output is
// premultiplied alpha; the caller's blend state must match
// (src=One, dst=OneMinusSrcAlpha).

struct Uniforms {
    viewport_px: vec2<f32>,
    elapsed_secs: f32,
    _pad: f32,
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
    // (border_px, lighten_amount, corner_radius, aa_pad). border_px=0
    // disables border. lighten_amount in [0,1] blends fill toward white
    // (hover effect). aa_pad inflates the quad for AA fringe on rounded rects.
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
    @location(6) corner_radius: f32,
    @location(7) aa_pad: f32,
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

    let pad = inst.params.w;

    var out: VsOut;
    out.pos = vec4<f32>(ndc, 0.0, 1.0);
    out.local_px = c * size - vec2<f32>(pad, pad);
    out.size_px = size - vec2<f32>(pad * 2.0, pad * 2.0);
    out.fill_rgba = inst.fill_rgba;
    out.border_rgba = inst.border_rgba;
    out.border_px = inst.params.x;
    out.lighten = inst.params.y;
    out.corner_radius = inst.params.z;
    out.aa_pad = pad;
    return out;
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let lp = in.local_px;
    let sz = in.size_px;
    let bw = in.border_px;
    let cr = in.corner_radius;

    if (cr > 0.0) {
        // Rounded-rect SDF (Inigo Quilez formulation).
        let half = sz * 0.5;
        let p = abs(lp - half) - (half - vec2<f32>(cr, cr));
        let d = length(max(p, vec2<f32>(0.0))) + min(max(p.x, p.y), 0.0) - cr;
        let w = max(0.5 * fwidth(d), 0.5);

        if (in.lighten > 1.5) {
            // ── Trail mode ─────────────────────────────────────────
            // Animated accent-colored comet orbiting the border.
            // border_rgba = trail accent color, fill is unused.
            let glow_radius = 7.0;
            if (d > glow_radius || d < -(bw + glow_radius)) {
                discard;
            }

            // Glow profile: wide soft band centered on the border edge.
            let glow = clamp(exp(-d * d * 0.15), 0.0, 1.0);

            // Arc-length position along the rounded rect perimeter.
            // Clockwise from where the TL arc meets the top edge.
            let rel = lp - half;
            let cx = half.x - cr;
            let cy = half.y - cr;
            let sw = 2.0 * cx;
            let sh = 2.0 * cy;
            let qa = 1.5707963 * cr;
            let perim = 2.0 * sw + 2.0 * sh + 4.0 * qa;

            var t: f32;
            if (rel.y < -cy) {
                if (rel.x < -cx) {
                    let a = atan2(-(rel.y + cy), -(rel.x + cx));
                    t = 2.0 * sw + 3.0 * qa + 2.0 * sh + a / 1.5707963 * qa;
                } else if (rel.x > cx) {
                    let a = atan2(-(rel.y + cy), rel.x - cx);
                    t = sw + (1.0 - a / 1.5707963) * qa;
                } else {
                    t = rel.x + cx;
                }
            } else if (rel.y > cy) {
                if (rel.x > cx) {
                    let a = atan2(rel.y - cy, rel.x - cx);
                    t = sw + qa + sh + a / 1.5707963 * qa;
                } else if (rel.x < -cx) {
                    let a = atan2(rel.y - cy, -(rel.x + cx));
                    t = 2.0 * sw + 2.0 * qa + sh + (1.0 - a / 1.5707963) * qa;
                } else {
                    t = sw + 2.0 * qa + sh + cx - rel.x;
                }
            } else if (rel.x > cx) {
                t = sw + qa + rel.y + cy;
            } else {
                t = 2.0 * sw + 3.0 * qa + sh + cy - rel.y;
            }
            let norm_t = t / perim;

            // Trail head orbits at constant speed.
            let speed = 0.4;
            let head = fract(u.elapsed_secs * speed);

            // Distance behind the head (wrapping, 0 = at head).
            let behind = fract(head - norm_t + 1.0);

            // Trail covers 40% of the perimeter with a gentle fade.
            let trail_len = 0.4;
            let trail = pow(max(1.0 - behind / trail_len, 0.0), 1.5);

            let intensity = trail * glow;
            if (intensity < 0.004) {
                discard;
            }

            let accent = in.border_rgba;
            let final_a = accent.a * intensity;
            return vec4<f32>(accent.rgb * final_a, final_a);
        }

        if (d > w) {
            discard;
        }
        let alpha_edge = 1.0 - smoothstep(-w, w, d);

        var rgba = in.fill_rgba;
        if (bw > 0.0 && in.border_rgba.a > 0.0) {
            let border_t = smoothstep(-w, w, d + bw);
            rgba = mix(in.fill_rgba, in.border_rgba, border_t);
        }

        let lit = mix(rgba.rgb, vec3<f32>(1.0, 1.0, 1.0), clamp(in.lighten, 0.0, 1.0));
        let final_a = rgba.a * alpha_edge;
        return vec4<f32>(lit * final_a, final_a);
    }

    // Dashed-border mode: lighten < 0 signals dash mode, with
    // -lighten = dash length in pixels. The border alternates between
    // border_rgba and fill_rgba; the interior is transparent.
    if (in.lighten < 0.0) {
        let dash_len = -in.lighten;
        let on_border =
            lp.x < bw ||
            lp.y < bw ||
            lp.x > sz.x - bw ||
            lp.y > sz.y - bw;
        if (!on_border) {
            discard;
        }
        var edge_pos: f32;
        if (lp.y < bw) { edge_pos = lp.x; }
        else if (lp.y > sz.y - bw) { edge_pos = lp.x; }
        else if (lp.x < bw) { edge_pos = lp.y; }
        else { edge_pos = lp.y; }
        let dash_idx = u32(floor(edge_pos / dash_len));
        var rgba: vec4<f32>;
        if (dash_idx % 2u == 0u) { rgba = in.border_rgba; }
        else { rgba = in.fill_rgba; }
        return vec4<f32>(rgba.rgb * rgba.a, rgba.a);
    }

    // Axis-aligned path (no rounding).
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
