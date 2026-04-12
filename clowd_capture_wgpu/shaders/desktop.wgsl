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
//   selection_params.y = `captured` flag (0 = not captured, 1 = the
//                        selection has been finalised). When set, the
//                        shader stops drawing the crosshair entirely
//                        so the OS cursor takes over the visual role.
//   selection_params.z = current magnifier zoom (1 .. 256). Currently
//                        unused — the selection border stays a fixed
//                        2 physical px and the dash period scales
//                        with `params.w` (DPI), not zoom. Plumbed
//                        through anyway so a future version can e.g.
//                        grow the crosshair arms with zoom without a
//                        uniform-layout change.
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
    let px = vec2<i32>(floor(in.pos.xy));
    let captured = u.selection_params.y > 0.5;

    // Crosshair: only when not captured. Once the user has finalised
    // a selection, the OS cursor takes over and the rendered crosshair
    // (both the coloured arms and the dashed b/w long lines) is
    // suppressed entirely. Mirrors the C++ behaviour: `data.crosshair`
    // is gated on the same captured/not-captured distinction at
    // DxScreenCapture.cpp:526.
    if (!captured) {
        // White line with a black dashed pattern overlaid on top, so
        // the cursor stays visible on both light and dark backgrounds
        // (white survives the dark stretches, black survives the
        // light ones). Drawn over everything else, including the
        // fade. Comparing integer pixel indices guarantees exactly 1
        // physical pixel thickness on every display, regardless of
        // DPI scale, because the swapchain is sized in physical
        // pixels and `@builtin(position)` is the framebuffer pixel
        // coordinate (centred on .5).
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
        // `chunk` is the length of one arm of the inner cross; the
        // outer thick segments extend from `chunk` to `2*chunk` out
        // from the cursor along each axis. Everything scales with
        // the monitor's DPI so the feature is the same physical size
        // on every display. `UNSCALED_CURSOR_PART_LENGTH` in the
        // original C++ source = 50.
        let chunk = i32(round(50.0 * scale));
        let chunk2 = chunk * 2;
        // Thick-segment half-width; total pixel count = 2*wide_half
        // + 1, always odd so the segment sits pixel-sharp on the
        // cursor column/row. ~5 physical pixels wide at 100 %,
        // capped at 9.
        let wide_half = clamp(i32(round(2.5 * scale)), 1, 4);

        // Inner thin coloured cross (1 pixel wide, radius `chunk`).
        let on_thin_colour = (on_v_line && ady <= chunk) || (on_h_line && adx <= chunk);
        // Outer thick coloured segments: a wide slab on each arm,
        // lying between the inner thin cross and the long dashed
        // line. The `adx <= wide_half` / `ady <= wide_half` tests
        // naturally clip the slab if the cursor is well off this
        // monitor, since both axes have to be close to the cursor
        // for red to appear.
        let on_thick_v_colour = adx <= wide_half && ady > chunk && ady <= chunk2;
        let on_thick_h_colour = ady <= wide_half && adx > chunk && adx <= chunk2;
        if (on_thin_colour || on_thick_v_colour || on_thick_h_colour) {
            return u.crosshair_color;
        }

        if (on_v_line || on_h_line) {
            // Dash runs ALONG the line: along Y for the vertical
            // line, along X for the horizontal one. 4 black + 4
            // white pixels per period, anchored to absolute window
            // coordinates so the dashes feel screen-fixed rather
            // than swimming with the cursor. At the intersection
            // both axes are on the line; we arbitrarily pick the
            // vertical line's phase — the pixel only gets one colour
            // anyway.
            let dash_coord = select(px.x, px.y, on_v_line);
            let phase = dash_coord % 8;
            if (phase < 4) {
                return vec4<f32>(0.0, 0.0, 0.0, 1.0);
            }
            return vec4<f32>(1.0, 1.0, 1.0, 1.0);
        }
    }

    // Mouse-drag selection. The rect lives in window-local physical
    // pixels (already through the same zoom transform as the UV path,
    // done CPU-side in the render thread), so the comparison is just
    // against the framebuffer pixel index. The 2-pixel border
    // straddles the rect's edge (1 px inside + 1 px outside),
    // matching the C++ D2D centred stroke at
    // DxScreenCapture.cpp:644-645. The dash period scales with DPI
    // (`params.w`) so the dashes are the same physical size on every
    // display.
    let sr = u.selection_rect;
    let sr_empty = sr.z <= sr.x || sr.w <= sr.y;
    if (!sr_empty) {
        let sx = i32(sr.x);
        let sy = i32(sr.y);
        let sz = i32(sr.z);
        let sw = i32(sr.w);

        // Classify the pixel into one of the four border slabs.
        // Each slab is 2 px thick. Top/bottom claim the 2×2 corner
        // squares so the left/right slabs can be strictly interior
        // on the y-axis — gives a disjoint classification, which
        // the clockwise perimeter walk below relies on.
        let slab_top    = px.y == sy - 1 || px.y == sy;
        let slab_bottom = px.y == sw - 1 || px.y == sw;
        let slab_left   = px.x == sx - 1 || px.x == sx;
        let slab_right  = px.x == sz - 1 || px.x == sz;

        let on_top    = slab_top    && px.x >= sx - 1 && px.x <= sz;
        let on_bottom = slab_bottom && px.x >= sx - 1 && px.x <= sz;
        let on_right  = !on_top && !on_bottom && slab_right
                        && px.y >= sy + 1 && px.y <= sw - 2;
        let on_left   = !on_top && !on_bottom && slab_left
                        && px.y >= sy + 1 && px.y <= sw - 2;
        let in_border = on_top || on_bottom || on_right || on_left;

        if (in_border) {
            // Walk the border clockwise from the outside-top-left
            // corner: top → right → bottom → left → back to start.
            // `arc` is the cumulative pixel count along that walk,
            // constant across the 2-px slab thickness so both rows
            // (or both columns) of a slab at a given axis position
            // get the same arc value and the dash reads as a solid
            // stripe. `phase = arc - t_offset`; increasing
            // `t_offset` shifts dashes toward higher arc, which is
            // clockwise.
            let outer_left = sx - 1;
            let outer_right = sz;
            // Top/bottom slabs span the full outer width (including
            // the 2×2 corners). Left/right slabs span only the
            // inner-y strip strictly between them.
            let top_len = (outer_right - outer_left) + 1;       // inclusive
            let side_len = (sw - 2) - (sy + 1) + 1;             // inclusive

            var arc: i32;
            if (on_top) {
                arc = px.x - outer_left;
            } else if (on_right) {
                arc = top_len + (px.y - (sy + 1));
            } else if (on_bottom) {
                arc = top_len + side_len + (outer_right - px.x);
            } else { // on_left
                arc = 2 * top_len + side_len + ((sw - 2) - px.y);
            }

            // Dash pattern: 16 DIPs on, 16 DIPs off, 32 DIPs per
            // full cycle — matches the C++ D2D stroke style values
            // of {8, 8} in stroke-width units × 2 DIPs stroke width
            // at DxScreenCapture.cpp:638-645. Multiply by DPI so
            // the dashes are the same physical size on every
            // display (1× at 96 DPI, 2× at 192 DPI, etc). The
            // animation completes one full cycle per second.
            // WGSL has no f32 `%`, so fold with floor() instead.
            let dpi = max(u.params.w, 1.0);
            let period = 32.0 * dpi;
            let half = period * 0.5;
            let t_offset = u.selection_params.x * period;
            let raw = f32(arc) - t_offset;
            let phase = raw - period * floor(raw / period);
            if (phase < half) {
                return u.crosshair_color;
            }
            return vec4<f32>(1.0, 1.0, 1.0, 1.0);
        }

        // Fill area: the rect minus the 1-px inner ring reserved
        // for the "inside half" of the straddling border. Border
        // cells win above; everything strictly interior to them
        // gets the un-faded desktop colour.
        let in_fill = px.x >= sx + 1 && px.x <= sz - 2
                   && px.y >= sy + 1 && px.y <= sw - 2;
        if (in_fill) {
            // Skip the grayscale fade entirely — same bit-exact
            // pass-through path as the existing fade==0.0 branch
            // below.
            let c = textureSample(desktop_tex, desktop_samp, in.uv);
            return vec4<f32>(c.rgb, 1.0);
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
