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
//   crosshair_color    = RGBA colour used for both the inner thin cross,
//                        the outer thick segments, AND the marching-ants
//                        dashes on the selection border. Seeded once from
//                        `CapturerSettings`; never updated per frame.
//   selection_rect     = mouse-drag selection in window-local physical
//                        pixels: x=left y=top z=right w=bottom. Empty if
//                        z<=x || w<=y; the shader treats any such rect
//                        as "no selection" and falls through to the
//                        normal grayscale path.
//   selection_params.x = elapsed seconds since the per-thread animation
//                        clock started; drives the marching-ants phase
//                        on the selection border.
struct Uniforms {
    uv_offset_scale:  vec4<f32>,
    params:           vec4<f32>,
    crosshair_color:  vec4<f32>,
    selection_rect:   vec4<f32>,
    selection_params: vec4<f32>,
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
        return u.crosshair_color;
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

    // Mouse-drag selection. The rect lives in window-local physical
    // pixels (already through the same zoom transform as the UV path,
    // done CPU-side in the render thread), so the comparison is just
    // against the framebuffer pixel index. The order of the
    // selection branches matters: both crosshair returns above must
    // win over the selection fill so the cursor stays visible inside
    // the rect; the desktop fade below catches every non-selection
    // pixel as before.
    let sr = u.selection_rect;
    let sr_empty = sr.z <= sr.x || sr.w <= sr.y;
    if (!sr_empty) {
        let fpx = vec2<f32>(f32(px.x), f32(px.y));
        let inside = fpx.x >= sr.x && fpx.x < sr.z &&
                     fpx.y >= sr.y && fpx.y < sr.w;
        if (inside) {
            // Skip the grayscale fade entirely. Same bit-exact
            // pass-through path as the existing fade==0.0 branch
            // below — sample, drop alpha, return.
            let c = textureSample(desktop_tex, desktop_samp, in.uv);
            return vec4<f32>(c.rgb, 1.0);
        }
        // 2-pixel ring strictly outside the rect. Drawn fully
        // outside (no half-overlap on the edge) so nothing inside
        // the selection is ever obscured — a deliberate divergence
        // from the C++ reference, which strokes centred on the edge.
        let in_border = fpx.x >= sr.x - 2.0 && fpx.x < sr.z + 2.0 &&
                        fpx.y >= sr.y - 2.0 && fpx.y < sr.w + 2.0;
        if (in_border) {
            // Classify the pixel into exactly one of four slabs.
            // Top/bottom span the full outer width (including the
            // 2×2 corner squares); left/right are only the inner
            // strip strictly between them. This makes the
            // membership disjoint, so the clockwise perimeter walk
            // below is unambiguous at the corners.
            let on_top    = fpx.y <  sr.y;
            let on_bottom = fpx.y >= sr.w;
            let on_right  = !on_top && !on_bottom && fpx.x >= sr.z;
            // on_left is the implicit else.

            // Walk the border clockwise from the outside-top-left
            // corner: top → right → bottom → left → back to start.
            // `arc` is the cumulative pixel count along that walk
            // (constant across the 2-px slab thickness — both rows
            // of the top slab at a given x get the same arc, so the
            // dash pattern reads as a single solid stripe). With
            // `phase = arc - t_offset`, increasing `t_offset`
            // shifts dashes toward higher arc, which is clockwise.
            let sx = i32(sr.x);
            let sy = i32(sr.y);
            let sz = i32(sr.z);
            let sw = i32(sr.w);
            let width_outer  = (sz - sx) + 4; // x-span of top/bottom slabs
            let height_inner = sw - sy;       // y-span of left/right slabs

            var arc: i32;
            if (on_top) {
                arc = px.x - (sx - 2);
            } else if (on_right) {
                arc = width_outer + (px.y - sy);
            } else if (on_bottom) {
                arc = width_outer + height_inner + ((sz + 1) - px.x);
            } else { // on_left
                arc = 2 * width_outer + height_inner + ((sw - 1) - px.y);
            }

            // Pattern: 16 px dash, 16 px gap, full 32 px cycle —
            // matches the C++ D2D stroke style at
            // DxScreenCapture.cpp:638-639, where the dash array
            // value of 8 is in stroke-width units (stroke width
            // = 2 px) and the per-second offset is `8 * 2 = 16`
            // dash-array units = 32 physical px. The dashes scroll
            // 32 px/sec around the perimeter — one full cycle per
            // second. WGSL signed `%` can be negative, so
            // normalise into [0, 32). If the perimeter isn't a
            // multiple of 32 there's a slight phase seam at the
            // top-left wrap-around; visually unobjectionable.
            let t_offset = i32(floor(u.selection_params.x * 32.0));
            let phase = ((arc - t_offset) % 32 + 32) % 32;
            if (phase < 16) {
                return u.crosshair_color;
            }
            return vec4<f32>(1.0, 1.0, 1.0, 1.0);
        }
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
