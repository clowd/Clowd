// Per-window uniforms.
//   uv_offset_scale.xy = where this monitor begins in the shared desktop
//                        texture, in normalised UV space.
//   uv_offset_scale.zw = the size of this monitor in the same UV space.
//   params.x           = grayscale fade factor in [0, 1].
//                        0 = original colour, 1 = darkened grayscale.
//   params.yz          = cursor position in window-local physical pixels.
//                        Out-of-range values mean the cursor is on another
//                        monitor; the integer-equality test below silently
//                        misses and no line is drawn for that axis.
//   params.w           = this monitor's DPI scale factor (1.0 = 100 %,
//                        1.5 = 150 %, …). Used to size the coloured
//                        crosshair arms so they stay the same physical
//                        size on every display.
struct Uniforms {
    uv_offset_scale: vec4<f32>,
    params:          vec4<f32>,
};

@group(0) @binding(0) var<uniform> u: Uniforms;
@group(0) @binding(1) var desktop_tex: texture_2d<f32>;
@group(0) @binding(2) var desktop_samp: sampler;

struct VsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0)       uv:  vec2<f32>,
};

// Fullscreen triangle covering [-1, 1]^2:
//   idx 0 -> (-1, -1)
//   idx 1 -> ( 3, -1)
//   idx 2 -> (-1,  3)
@vertex
fn vs_main(@builtin(vertex_index) idx: u32) -> VsOut {
    let x = f32((idx << 1u) & 2u) * 2.0 - 1.0;
    let y = f32(idx & 2u) * 2.0 - 1.0;
    var out: VsOut;
    out.pos = vec4<f32>(x, y, 0.0, 1.0);
    // Clip space is Y-up; texture v is Y-down. Flip here.
    let window_uv = vec2<f32>(x * 0.5 + 0.5, 0.5 - y * 0.5);
    out.uv = u.uv_offset_scale.xy + window_uv * u.uv_offset_scale.zw;
    return out;
}

// Standard sRGB transfer functions. The texture and surface are both
// non-sRGB (`Bgra8Unorm`), so wgpu does *no* colour-space conversion on
// sample or store — values go in and out as raw byte / 255. We do the
// sRGB ↔ linear conversion manually here, only when the grayscale math
// actually needs linear light.
fn srgb_to_linear(c: vec3<f32>) -> vec3<f32> {
    let cutoff = vec3<f32>(0.04045);
    let lo = c / 12.92;
    let hi = pow((c + 0.055) / 1.055, vec3<f32>(2.4));
    return select(hi, lo, c <= cutoff);
}

fn linear_to_srgb(c: vec3<f32>) -> vec3<f32> {
    let cutoff = vec3<f32>(0.0031308);
    let lo = c * 12.92;
    let hi = 1.055 * pow(c, vec3<f32>(1.0 / 2.4)) - 0.055;
    return select(hi, lo, c <= cutoff);
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    // Crosshair: a white line with a black dashed pattern overlaid on
    // top, so the cursor stays visible on both light and dark
    // backgrounds (white survives the dark stretches, black survives
    // the light ones). Drawn over everything else, including the fade.
    // Comparing integer pixel indices guarantees exactly 1 physical
    // pixel thickness on every display, regardless of DPI scale,
    // because the swapchain is sized in physical pixels and
    // `@builtin(position)` is the framebuffer pixel coordinate
    // (centred on .5).
    let px = vec2<i32>(floor(in.pos.xy));
    let mouse_x = i32(u.params.y);
    let mouse_y = i32(u.params.z);
    let scale = max(u.params.w, 1.0);
    let dx = px.x - mouse_x;
    let dy = px.y - mouse_y;
    let adx = abs(dx);
    let ady = abs(dy);
    let on_v_line = dx == 0;
    let on_h_line = dy == 0;

    // Coloured section geometry.
    //
    //     ┊     ▌         ┊
    //     ┊     ▌         ┊  <- outer thick segment (red, ~5 px at 100 %)
    //     ┊     │         ┊
    //     ┊     │         ┊  <- inner thin arm (red, 1 px)
    //   ──┴─────┼─────────┴──  <- main long crosshair (b/w dashed)
    //     ┊     │         ┊
    //     ┊     │         ┊
    //     ┊     ▌         ┊
    //     ┊     ▌         ┊
    //
    //            └─ chunk ─┘
    //     └───── chunk2 ───┘
    //
    // `chunk` is the length of one arm of the inner cross; the outer
    // thick segments extend from `chunk` to `2*chunk` out from the
    // cursor along each axis. Everything scales with the monitor's
    // DPI so the feature is the same physical size on every display.
    // `UNSCALED_CURSOR_PART_LENGTH` in the original C++ source = 50.
    let chunk = i32(round(50.0 * scale));
    let chunk2 = chunk * 2;
    // Thick-segment half-width; total pixel count = 2*wide_half + 1,
    // always odd so the segment sits pixel-sharp on the cursor
    // column/row. ~5 physical pixels wide at 100 %, capped at 9.
    let wide_half = clamp(i32(round(2.5 * scale)), 1, 4);

    // Inner thin coloured cross (1 pixel wide, radius `chunk`).
    let on_thin_colour = (on_v_line && ady <= chunk) || (on_h_line && adx <= chunk);
    // Outer thick coloured segments: a wide slab on each arm, lying
    // between the inner thin cross and the long dashed line. The
    // `adx <= wide_half` / `ady <= wide_half` tests naturally clip
    // the slab if the cursor is well off this monitor, since both
    // axes have to be close to the cursor for red to appear.
    let on_thick_v_colour = adx <= wide_half && ady > chunk && ady <= chunk2;
    let on_thick_h_colour = ady <= wide_half && adx > chunk && adx <= chunk2;
    if (on_thin_colour || on_thick_v_colour || on_thick_h_colour) {
        // Hard-coded red for now; lift to a uniform when we want to
        // theme the crosshair.
        return vec4<f32>(1.0, 0.0, 0.0, 1.0);
    }

    if (on_v_line || on_h_line) {
        // Dash runs ALONG the line: along Y for the vertical line,
        // along X for the horizontal one. 4 black + 4 white pixels
        // per period, anchored to absolute window coordinates so the
        // dashes feel screen-fixed rather than swimming with the
        // cursor. At the intersection both axes are on the line; we
        // arbitrarily pick the vertical line's phase — the pixel only
        // gets one colour anyway.
        let dash_coord = select(px.x, px.y, on_v_line);
        let phase = dash_coord % 8;
        if (phase < 4) {
            return vec4<f32>(0.0, 0.0, 0.0, 1.0);
        }
        return vec4<f32>(1.0, 1.0, 1.0, 1.0);
    }

    let color = textureSample(desktop_tex, desktop_samp, in.uv);
    let fade = clamp(u.params.x, 0.0, 1.0);

    // fade = 0 is the common case during the hold phase. Pass through
    // bit-exactly — no sRGB math, no lerp rounding — so the rendered
    // window is pixel-identical to the original BitBlt bytes, which
    // themselves are pixel-identical to what DWM was displaying. This
    // is what eliminates the "subtle colour shift" at window appearance.
    if (fade == 0.0) {
        return vec4<f32>(color.rgb, 1.0);
    }

    // fade > 0: decode to linear light, apply the grayscale + darken,
    // re-encode to sRGB. Rec.709 linear-light luma coefficients (NOT
    // BT.601, which are defined for gamma-encoded values). The 0.42
    // multiplier reproduces the old "35% darken" effect: 0.65 in sRGB
    // space is roughly 0.65^2 ≈ 0.42 in linear space.
    let linear = srgb_to_linear(color.rgb);
    let luma = dot(linear, vec3<f32>(0.2126, 0.7152, 0.0722)) * 0.42;
    let gray_linear = vec3<f32>(luma);
    let out_linear = mix(linear, gray_linear, fade);
    let out_srgb = linear_to_srgb(out_linear);
    return vec4<f32>(out_srgb, 1.0);
}
