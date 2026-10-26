// Desktop background pass: the frozen desktop snapshot, the captured
// cursor composited into it, and the region treatment — untouched inside
// the selection (or OCR-dimmed while that mode runs), darkened grayscale
// outside it. NOTHING ELSE: the crosshair, the selection border, the
// resize handles and the peeked window all live in their own passes
// (crosshair.wgsl, selection.wgsl, peek.wgsl), drawn over this one, so
// this fullscreen pass stays close to a plain textured triangle. See
// render/frame.rs for the pass order.
//
// Per-window uniforms.
//   uv_offset_scale.xy = where this monitor begins in the shared desktop
//                        texture, in normalized UV space.
//   uv_offset_scale.zw = the size of this monitor in the same UV space.
//                        The magnifier zoom is folded in CPU-side.
//   params.x           = grayscale fade factor in [0, 1].
//                        0 = original color, 1 = darkened grayscale.
//   selection_rect     = mouse-drag selection in window-local physical
//                        pixels: x=left y=top z=right w=bottom. Empty if
//                        z<=x || w<=y; any such rect means "no selection"
//                        and every pixel takes the outside treatment.
//                        The selection pass's border straddles this rect's
//                        edge and repaints the transition band, so the
//                        plain rect test here never shows: for a rounded
//                        selection the corner regions outside the curve
//                        are likewise repainted by that pass's corner
//                        patches.
//   cursor_rect        = frozen-cursor rect in window-local physical px:
//                        x=left y=top z=right w=bottom. Empty (z <= x)
//                        when the cursor is hidden or off this monitor.
//   cursor_params.x    = cursor type: 0=hidden, 1=alpha_blended, 2=masked.
//   ocr_params.x       = OCR source dim amount in [0, 1]; ramps alongside
//                        the lift animation and reverses during retract.
//   ocr_params.z       = OCR selection desaturation in [0, 1], same
//                        shared clock as the dim (see render/desktop.rs).
struct Uniforms {
    uv_offset_scale: vec4<f32>,
    params:          vec4<f32>,
    selection_rect:  vec4<f32>,
    cursor_rect:     vec4<f32>,
    cursor_params:   vec4<f32>,
    ocr_params:      vec4<f32>,
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
// are both non-sRGB (`Bgra8Unorm`), so the GPU does *no* color-space
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
    let fade = clamp(u.params.x, 0.0, 1.0);

    let color = textureSample(desktop_tex, desktop_samp, in.uv);
    var base = vec4<f32>(color.rgb, 1.0);

    // Composite the captured cursor onto the desktop. Done before the
    // region treatment so the cursor is part of the desktop content —
    // dimmed with it outside the selection, untouched inside. The masked
    // path needs the destination pixel (AND/XOR), which is why the frozen
    // cursor lives in this pass rather than its own: fixed-function
    // blending cannot express an XOR against the framebuffer.
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

    // Selection interior: the desktop untouched, or desaturated + dimmed
    // while OCR mode is active. The `<= 0` early-out keeps the byte-exact
    // passthrough the uncloak invariant depends on (both params are 0
    // outside the mode).
    let px = vec2<i32>(floor(in.pos.xy));
    let sr = u.selection_rect;
    let sr_empty = sr.z <= sr.x || sr.w <= sr.y;
    if (!sr_empty &&
        px.x >= i32(sr.x) && px.x < i32(sr.z) &&
        px.y >= i32(sr.y) && px.y < i32(sr.w)) {
        let dim = clamp(u.ocr_params.x, 0.0, 1.0);
        let gray = clamp(u.ocr_params.z, 0.0, 1.0);
        if (dim <= 0.0 && gray <= 0.0) {
            return base;
        }
        // Same linear-light luma machinery as the outside fade below, but
        // WITHOUT its 0.42 luma crush: the OCR dim (ocr_params.x) is the
        // darkening channel here, and stacking the crush under the dim
        // would land the region at ~27% brightness — "crushed to black",
        // exactly the read this composition is tuned to avoid. Result:
        // selection ≈ luma × (1 - dim), which holds for the WHOLE mode
        // and always sits brighter than the crushed outside, so the
        // region stays the focus of the whole screen.
        let lin = srgb_to_linear(base.rgb);
        let luma = vec3<f32>(dot(lin, vec3<f32>(0.2126, 0.7152, 0.0722)));
        let desat = linear_to_srgb(mix(lin, luma, gray));
        return vec4<f32>(desat * (1.0 - dim), 1.0);
    }

    // fade = 0 is the common case during the hold phase. Pass through
    // bit-exactly — no sRGB math, no lerp rounding — so the rendered
    // window is pixel-identical to the original BitBlt bytes, which
    // themselves are pixel-identical to what DWM was displaying. This
    // is what eliminates the "subtle color shift" at window appearance.
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
