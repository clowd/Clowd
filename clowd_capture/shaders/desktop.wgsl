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
//   accent_color    = RGBA colour used for both the inner thin cross,
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
//   selection_params.w = scroll-point pick flag (0 = normal, 1 = the user
//                        pressed SCROLL and is picking the point the wheel
//                        will be aimed at). Suppresses the resize handles:
//                        the picker owns the next click, so nothing that
//                        looks draggable may be on screen. The dashed
//                        border stays — it is what shows the region being
//                        captured.
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
    accent_color:  vec4<f32>,
    selection_rect:   vec4<f32>,
    selection_params: vec4<f32>,
    // cursor_rect.xyzw = left, top, right, bottom in window-local px.
    // Empty (z <= x) when cursor is hidden or off this monitor.
    cursor_rect:      vec4<f32>,
    // cursor_params.x = cursor type: 0=hidden, 1=alpha_blended, 2=masked.
    cursor_params:    vec4<f32>,
    // ocr_rect.xyzw = OCR source region in window-local px (l, t, r, b).
    // Empty (z <= x) while OCR mode is idle. Currently unread — the dim
    // below applies to the whole selection fill, which IS the OCR region
    // modulo edge clamping — but mirrored here so the Rust struct and this
    // block cannot diverge in layout.
    ocr_rect:         vec4<f32>,
    // ocr_params.x = OCR source dim amount in [0, 1]; ramps alongside the
    //                lift animation and reverses during retract.
    // ocr_params.y = OCR-mode-active flag; suppresses the resize handles
    //                so nothing draws over (or looks draggable under) the
    //                lifted text.
    // ocr_params.z = OCR selection desaturation in [0, 1]. Ramps on the
    //                same shared-anchor clock as the dim (CPU-side, the
    //                same quartic curve as the opening fade — see
    //                render/desktop.rs) so the selection interior joins
    //                the monochrome page when OCR starts and colour
    //                returns with the retract.
    ocr_params:       vec4<f32>,
};

@group(0) @binding(0) var<uniform> u: Uniforms;
@group(0) @binding(1) var desktop_tex: texture_2d<f32>;
@group(0) @binding(2) var desktop_samp: sampler;
@group(0) @binding(3) var cursor_color_tex: texture_2d<f32>;
@group(0) @binding(4) var cursor_mask_tex: texture_2d<f32>;

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

// Gamma-2.0 approximation of the sRGB transfer. The texture and surface
// are both non-sRGB (`Bgra8Unorm`), so wgpu does *no* colour-space
// conversion on sample or store — values go in and out as raw byte / 255.
// We only need linear light for the grayscale luma math, and the output
// gets crushed to luma × 0.42 × fade anyway, so the ~0.01-in-8-bit error
// from using `c*c` / `sqrt(c)` instead of real sRGB 2.4 is well below
// anything the eye can pick up in that context. Avoiding the two
// `pow(vec3, 2.4)` calls per pixel cuts the fragment shader cost by 3–4 ms
// on a 5 MP framebuffer on M1. The byte-exact uncloak invariant is
// preserved by the `fade == 0.0` early-out below — these approximations
// only run on pixels where fade > 0.
fn srgb_to_linear(c: vec3<f32>) -> vec3<f32> {
    return c * c;
}

fn linear_to_srgb(c: vec3<f32>) -> vec3<f32> {
    return sqrt(c);
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let px = vec2<i32>(floor(in.pos.xy));
    let captured = u.selection_params.y > 0.5;
    let fade = clamp(u.params.x, 0.0, 1.0);

    // Sample the desktop texture once — needed both for the grayscale
    // path and as the base colour that overlay elements (crosshair,
    // selection border) blend on top of during the fade-in.
    let color = textureSample(desktop_tex, desktop_samp, in.uv);
    var base = vec4<f32>(color.rgb, 1.0);

    // Composite captured cursor onto the desktop. Done before crosshair,
    // selection border, and fade so the cursor is part of the desktop content.
    let cr = u.cursor_rect;
    let cursor_type = u32(u.cursor_params.x);
    if cursor_type != 0u && cr.z > cr.x && cr.w > cr.y {
        let fpos = in.pos.xy;
        if fpos.x >= cr.x && fpos.x < cr.z && fpos.y >= cr.y && fpos.y < cr.w {
            let cursor_uv = (fpos - cr.xy) / (cr.zw - cr.xy);
            let cur_color = textureSample(cursor_color_tex, desktop_samp, cursor_uv);
            if cursor_type == 1u {
                // Alpha blended (premultiplied): out = src + dst * (1 - src.a)
                base = vec4<f32>(
                    cur_color.rgb + base.rgb * (1.0 - cur_color.a),
                    1.0,
                );
            } else {
                // Masked (AND/XOR): output = (screen * and_mask) xor'd with xor_color.
                // Since and_mask values are 0.0 or 1.0 in unorm:
                //   AND with 0.0 → 0.0, AND with 1.0 → keep
                //   XOR with 0.0 → keep, XOR with 1.0 → invert
                // Float-safe: AND = multiply, XOR = abs(a - b) when operands are 0 or 1.
                let and_mask = textureSample(cursor_mask_tex, desktop_samp, cursor_uv);
                let masked = base.rgb * and_mask.rgb;
                base = vec4<f32>(abs(masked - cur_color.rgb), 1.0);
            }
        }
    }

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
        // Stepped DPI factor in whole pixels: 1 at 100–199 %, 2 at
        // 200–299 %, … . `floor()` rather than `round()` so the jump
        // happens AT the DPI boundary, not half-way — matches the
        // selection-border treatment below.
        let dpi_step = i32(floor(scale));
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
        if (on_thin_colour) {
            let lum = dot(base.rgb, vec3<f32>(0.299, 0.587, 0.114));
            let contrast = select(vec4<f32>(1.0, 1.0, 1.0, 1.0), vec4<f32>(0.0, 0.0, 0.0, 1.0), lum > 0.65);
            return mix(base, contrast, fade);
        }
        if (on_thick_v_colour || on_thick_h_colour) {
            return mix(base, u.accent_color, fade);
        }

        if (on_v_line || on_h_line) {
            // Dash runs ALONG the line: along Y for the vertical
            // line, along X for the horizontal one. 6 black + 6
            // white pixels per period at 100 %, scaled by the
            // whole-pixel DPI step so the dashes are the same
            // physical size on every display (12-px period at
            // 100 %, 24 at 200 %, …). Anchored to absolute window
            // coordinates so the dashes feel screen-fixed rather
            // than swimming with the cursor. At the intersection
            // both axes are on the line; we arbitrarily pick the
            // vertical line's phase — the pixel only gets one colour
            // anyway.
            let dash_coord = select(px.x, px.y, on_v_line);
            let period = 12 * dpi_step;
            let half_period = period / 2;
            // WGSL signed `%` preserves sign of the dividend; add
            // `period` before the second `%` so negative window
            // coordinates still yield a non-negative phase.
            let phase = ((dash_coord % period) + period) % period;
            if (phase < half_period) {
                return mix(base, vec4<f32>(0.0, 0.0, 0.0, 1.0), fade);
            }
            return mix(base, vec4<f32>(1.0, 1.0, 1.0, 1.0), fade);
        }
    }

    // Mouse-drag selection. The rect lives in window-local physical
    // pixels (already through the same zoom transform as the UV path,
    // done CPU-side in the render thread), so the comparison is just
    // against the framebuffer pixel index. The border straddles the
    // rect's edge evenly (`half` px inside + `half` px outside), with
    // `half` stepped on whole-pixel DPI boundaries — 2 px at 100–199 %,
    // 4 px at 200–299 %, … — so the stroke stays pixel-sharp on every
    // display. The dash period scales on the same step so dashes and
    // stroke width grow together.
    let sr = u.selection_rect;
    let sr_empty = sr.z <= sr.x || sr.w <= sr.y;
    if (!sr_empty) {
        let sx = i32(sr.x);
        let sy = i32(sr.y);
        let sz = i32(sr.z);
        let sw = i32(sr.w);

        let dpi_scale = max(u.params.w, 1.0);
        let sel_step = i32(floor(dpi_scale));               // 1 / 1 / 2 / …
        // `half` is the border's half-thickness in physical pixels.
        // Total stroke width = 2*half (2 / 2 / 4 / 6 / …). Using
        // `floor()` means 150 % stays at 2 px and 200 % is where the
        // step fires; matches the crosshair handle treatment above.
        let half = sel_step;
        let inner_top    = sy + half;
        let inner_bottom = sw - half - 1;
        let inner_left   = sx + half;
        let inner_right  = sz - half - 1;
        let outer_top    = sy - half;
        let outer_bottom = sw + half - 1;
        let outer_left   = sx - half;
        let outer_right  = sz + half - 1;

        // Resize handles: 8 anti-aliased circles (corners + edge
        // midpoints), shown only after capture and never while a scroll
        // point is being picked.  From edge inward: sel_step px accent,
        // sel_step px white ring, rest accent.
        let scroll_pick = u.selection_params.w > 0.5;
        // The OCR gate mirrors the scroll_pick one: while lines are lifted
        // (or lifting/retracting), the selection is frozen and the handles
        // would draw right across the raised text.
        if (captured && !scroll_pick && u.ocr_params.y < 0.5) {
            let fpos = in.pos.xy;
            let step_f = f32(sel_step);
            let handle_r    = 6.0 * step_f;
            let white_outer = handle_r - step_f;
            let white_inner = handle_r - 3.0 * step_f;
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
                let outer_a = 1.0 - smoothstep(handle_r - aa, handle_r + aa, hd);
                let white_a = smoothstep(white_inner - aa, white_inner + aa, hd)
                            * (1.0 - smoothstep(white_outer - aa, white_outer + aa, hd));
                let hcol = mix(u.accent_color, vec4<f32>(1.0, 1.0, 1.0, 1.0), white_a);
                return mix(base, hcol, fade * outer_a);
            }
        }

        // Classify the pixel into one of the four border slabs.
        // Each slab is 2*half px thick. Top/bottom claim the
        // (2*half)×(2*half) corner squares so the left/right slabs
        // can be strictly interior on the y-axis — gives a disjoint
        // classification, which the clockwise perimeter walk below
        // relies on.
        let slab_top    = px.y >= outer_top    && px.y <= outer_top    + 2 * half - 1;
        let slab_bottom = px.y >= outer_bottom - 2 * half + 1 && px.y <= outer_bottom;
        let slab_left   = px.x >= outer_left   && px.x <= outer_left   + 2 * half - 1;
        let slab_right  = px.x >= outer_right  - 2 * half + 1 && px.x <= outer_right;

        let on_top    = slab_top    && px.x >= outer_left && px.x <= outer_right;
        let on_bottom = slab_bottom && px.x >= outer_left && px.x <= outer_right;
        let on_right  = !on_top && !on_bottom && slab_right
                        && px.y >= inner_top && px.y <= inner_bottom;
        let on_left   = !on_top && !on_bottom && slab_left
                        && px.y >= inner_top && px.y <= inner_bottom;
        let in_border = on_top || on_bottom || on_right || on_left;

        if (in_border) {
            // Walk the border clockwise from the outside-top-left
            // corner: top → right → bottom → left → back to start.
            // `arc` is the cumulative pixel count along that walk,
            // constant across each slab's thickness so every row
            // (or column) at a given axis position gets the same
            // arc value and the dash reads as a solid stripe.
            // `phase = arc - t_offset`; increasing `t_offset` shifts
            // dashes toward higher arc, which is clockwise.
            //
            // Top/bottom slabs span the full outer width (including
            // the corner squares). Left/right slabs span only the
            // inner-y strip strictly between them.
            let top_len  = (outer_right - outer_left) + 1;
            let side_len = (inner_bottom - inner_top) + 1;

            var arc: i32;
            if (on_top) {
                arc = px.x - outer_left;
            } else if (on_right) {
                arc = top_len + (px.y - inner_top);
            } else if (on_bottom) {
                arc = top_len + side_len + (outer_right - px.x);
            } else { // on_left
                arc = 2 * top_len + side_len + (inner_bottom - px.y);
            }

            // Dash pattern: 16 px on, 16 px off at 100 %, stepped
            // on the same whole-pixel DPI ladder as the stroke
            // width so dashes and stroke grow together (32 px
            // period at 100 %, 64 at 200 %, …). Matches the C++
            // D2D stroke style values of {8, 8} in stroke-width
            // units × 2 DIPs stroke width at
            // DxScreenCapture.cpp:638-645. The animation completes
            // one full cycle per second. WGSL has no f32 `%`, so
            // fold with floor() instead.
            let period = 32.0 * f32(sel_step);
            let half_period = period * 0.5;
            let t_offset = u.selection_params.x * period;
            let raw = f32(arc) - t_offset;
            let phase = raw - period * floor(raw / period);
            if (phase < half_period) {
                return mix(base, u.accent_color, fade);
            }
            return mix(base, vec4<f32>(1.0, 1.0, 1.0, 1.0), fade);
        }

        // Fill area: the rect minus the `half`-px inner ring
        // reserved for the "inside half" of the straddling border.
        // Border cells win above; everything strictly interior to
        // them gets the un-faded desktop colour.
        let in_fill = px.x >= inner_left && px.x <= inner_right
                   && px.y >= inner_top  && px.y <= inner_bottom;
        if (in_fill) {
            // OCR treatment of the selection interior: desaturate, then
            // darken, so the raised copies read as the live layer over a
            // monochrome page. Done here rather than as a RectInstance
            // because rect.draw runs AFTER lift.draw — a rect dim would
            // land on top of the lifted lines. The `<= 0` early-out keeps
            // the byte-exact passthrough the uncloak invariant depends on
            // (and is the non-OCR fast path: both params are 0 outside the
            // mode, so this branch is behaviourally identical to before).
            let dim = clamp(u.ocr_params.x, 0.0, 1.0);
            let gray = clamp(u.ocr_params.z, 0.0, 1.0);
            if (dim <= 0.0 && gray <= 0.0) {
                return base;
            }
            // Same linear-light luma machinery as the outside fade below,
            // but WITHOUT its 0.42 luma crush: the OCR dim (ocr_params.x)
            // is the darkening channel here, and stacking the crush under
            // the dim would land the region at ~27% brightness —
            // "crushed to black", exactly the read this composition is
            // tuned to avoid. Result: selection ≈ luma × (1 - dim), which
            // holds at 65% for the WHOLE mode (one darkening, on entry —
            // deepening again when the text renders read as dimming
            // twice) and always sits brighter than the crushed outside,
            // so the region stays the focus of the whole screen.
            let lin = srgb_to_linear(base.rgb);
            let luma = vec3<f32>(dot(lin, vec3<f32>(0.2126, 0.7152, 0.0722)));
            let desat = linear_to_srgb(mix(lin, luma, gray));
            return vec4<f32>(desat * (1.0 - dim), 1.0);
        }
    }

    // fade = 0 is the common case during the hold phase. Pass through
    // bit-exactly — no sRGB math, no lerp rounding — so the rendered
    // window is pixel-identical to the original BitBlt bytes, which
    // themselves are pixel-identical to what DWM was displaying. This
    // is what eliminates the "subtle colour shift" at window appearance.
    if (fade == 0.0) {
        return base;
    }

    // fade > 0: decode to linear light, apply the grayscale + darken,
    // re-encode to sRGB. Rec.709 linear-light luma coefficients (NOT
    // BT.601, which are defined for gamma-encoded values). The 0.42
    // multiplier reproduces the old "35% darken" effect: 0.65 in sRGB
    // space is roughly 0.65^2 ≈ 0.42 in linear space.
    let linear = srgb_to_linear(base.rgb);
    let luma = dot(linear, vec3<f32>(0.2126, 0.7152, 0.0722)) * 0.42;
    let gray_linear = vec3<f32>(luma);
    let out_linear = mix(linear, gray_linear, fade);
    let out_srgb = linear_to_srgb(out_linear);
    return vec4<f32>(out_srgb, 1.0);
}
