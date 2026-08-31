// Crosshair overlay pass: the long dashed black/white lines, the accent
// colored thick segments and the inner thin contrast cross. Drawn OVER
// the desktop, peek and selection passes (see render/frame.rs for the
// order), so no other shader needs to know the crosshair's geometry.
//
// Cost model: instead of a fullscreen pass testing every pixel against
// the crosshair (what desktop.wgsl used to do), the vertex shader emits
// eleven small quads that exactly cover the crosshair's pixels — two
// 1-px lines split around the cursor, four thick arm slabs, three thin
// arm strips — so the fragment cost scales with the crosshair's own
// area, a few thousand pixels. The pass is skipped entirely (no draw
// call) once a capture is made or while overlays are hidden.
//
// The quads are mutually disjoint, reproducing the old shader's
// precedence (thin > thick > dashed line) by construction: the dashed
// line quads stop `2*chunk` short of the cursor, where the thick slabs
// take over, and the thin strips own the innermost `chunk`.
//
// Colors blend as premultiplied alpha with alpha = fade, matching the
// old `mix(base, color, fade)` against whatever the earlier passes put
// in the framebuffer.
//
// Shared overlay uniforms — one buffer written per frame, bound by both
// this pass and selection.wgsl (fields the other pass owns are unused
// here; the struct must stay byte-identical in both files and in
// `gpu::overlay::OverlayUniforms`):
//   viewport.xy       = surface size in physical px.
//   viewport.z        = this monitor's DPI scale factor (1.0 = 100 %).
//   viewport.w        = grayscale fade factor in [0, 1]; the crosshair
//                       fades in with the overlay.
//   cursor.xy         = cursor position in window-local physical px.
//                       Out-of-range values simply push the quads off
//                       this monitor's surface; a still-in-range axis
//                       keeps its long line, matching the old integer
//                       equality test's behavior across monitors.
//   accent_color      = the thick segments' color.
//   selection_rect    = unused here (selection pass).
//   sel_params        = unused here (selection pass).
//   uv_offset_scale   = window px → desktop-texture UV mapping, same
//                       values as the desktop pass (zoom folded in);
//                       used to sample the snapshot for the thin cross's
//                       black/white contrast choice.
struct OverlayUniforms {
    viewport:        vec4<f32>,
    cursor:          vec4<f32>,
    accent_color:    vec4<f32>,
    selection_rect:  vec4<f32>,
    sel_params:      vec4<f32>,
    uv_offset_scale: vec4<f32>,
};

// The thin cross's color is a per-pixel black/white choice made from
// whatever is DISPLAYED beneath it — and a fragment shader cannot read
// the framebuffer it draws into, so being later in the pass order only
// fixes occlusion, not this decision. Under an active peek the displayed
// pixels are the peek composite, not the snapshot (they differ exactly
// inside the obstruction rects), so the pass carries enough of the peek
// pass's inputs to replicate that composite for the handful of thin-
// cross pixels: the window texture + its UV mapping, the ghost's
// blurred-desktop texture, and the obstruction rects. All zero (and the
// textures 1×1 placeholders) while no peek is on screen.
//   params.x           = 1.0 while a peek quad is being drawn, else 0.
//   params.y           = ghost_opacity (peek.wgsl params.y).
//   params.z           = number of obstruction rects.
//   window_uv          = the peek quad's window-texture UV mapping,
//                        identical values to peek.wgsl's window_uv; the
//                        quad itself is `selection_rect` above.
//   obstruction_rects  = window-local px, identical to peek.wgsl's.
struct CrosshairPeekUniforms {
    params:            vec4<f32>,
    window_uv:         vec4<f32>,
    obstruction_rects: array<vec4<f32>, 16>,
};

@group(0) @binding(0) var<uniform> u: OverlayUniforms;
@group(0) @binding(1) var<uniform> pk: CrosshairPeekUniforms;
@group(0) @binding(2) var desktop_tex: texture_2d<f32>;
@group(0) @binding(3) var window_tex: texture_2d<f32>;
@group(0) @binding(4) var blur_tex: texture_2d<f32>;
@group(0) @binding(5) var desktop_samp: sampler;

// Quad kinds, one per fragment path.
const KIND_DASH_V: u32 = 0u;  // long dashed vertical line
const KIND_DASH_H: u32 = 1u;  // long dashed horizontal line
const KIND_THICK:  u32 = 2u;  // accent-colored thick arm
const KIND_THIN:   u32 = 3u;  // 1-px contrast cross arm

struct VsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) @interpolate(flat) kind: u32,
};

// 11 quads × 6 vertices = 66. Rects are computed in window-local
// physical pixels from the uniforms; a rect's max edge is EXCLUSIVE
// (covers integer pixel columns/rows [min, max)), which lands exactly on
// the old shader's integer comparisons. Geometry constants mirror the
// old fragment logic:
//   chunk     = one thin arm's length     (round(50 * scale))
//   chunk2    = thick arms end            (2 * chunk)
//   wide_half = thick arm half-width      (clamp(round(2.5 * scale), 1, 4))
@vertex
fn vs_main(@builtin(vertex_index) vi: u32) -> VsOut {
    var corners = array<vec2<f32>, 6>(
        vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(1.0, 1.0),
        vec2(0.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0),
    );
    let quad = vi / 6u;
    let c = corners[vi % 6u];

    // trunc() matches the old `i32(f32)` cast of the cursor position.
    let mx = trunc(u.cursor.x);
    let my = trunc(u.cursor.y);
    let scale = max(u.viewport.z, 1.0);
    let chunk = round(50.0 * scale);
    let chunk2 = chunk * 2.0;
    let wide_half = clamp(round(2.5 * scale), 1.0, 4.0);
    let w = u.viewport.x;
    let h = u.viewport.y;

    var rect = vec4<f32>(0.0, 0.0, 0.0, 0.0);
    var kind = KIND_DASH_V;
    switch quad {
        // Long dashed lines, stopping short of the thick arms
        // (|d| > chunk2 in the old shader).
        case 0u { rect = vec4(mx, 0.0, mx + 1.0, my - chunk2); }
        case 1u { rect = vec4(mx, my + chunk2 + 1.0, mx + 1.0, h); }
        case 2u { rect = vec4(0.0, my, mx - chunk2, my + 1.0); kind = KIND_DASH_H; }
        case 3u { rect = vec4(mx + chunk2 + 1.0, my, w, my + 1.0); kind = KIND_DASH_H; }
        // Thick arms: chunk < |d| <= chunk2, half-width wide_half.
        case 4u { rect = vec4(mx - wide_half, my - chunk2, mx + wide_half + 1.0, my - chunk); kind = KIND_THICK; }
        case 5u { rect = vec4(mx - wide_half, my + chunk + 1.0, mx + wide_half + 1.0, my + chunk2 + 1.0); kind = KIND_THICK; }
        case 6u { rect = vec4(mx - chunk2, my - wide_half, mx - chunk, my + wide_half + 1.0); kind = KIND_THICK; }
        case 7u { rect = vec4(mx + chunk + 1.0, my - wide_half, mx + chunk2 + 1.0, my + wide_half + 1.0); kind = KIND_THICK; }
        // Thin cross: the vertical strip owns the shared center pixel;
        // the horizontal arm splits around it so the quads stay disjoint.
        case 8u { rect = vec4(mx, my - chunk, mx + 1.0, my + chunk + 1.0); kind = KIND_THIN; }
        case 9u { rect = vec4(mx - chunk, my, mx, my + 1.0); kind = KIND_THIN; }
        default { rect = vec4(mx + 1.0, my, mx + chunk + 1.0, my + 1.0); kind = KIND_THIN; }
    }

    // Clamp to the surface so off-monitor cursors produce empty quads
    // rather than huge clipped ones (the rasterizer would clip them
    // anyway; this just keeps the math finite).
    let min_px = clamp(rect.xy, vec2(0.0, 0.0), vec2(w, h));
    let max_px = clamp(max(rect.zw, rect.xy), vec2(0.0, 0.0), vec2(w, h));
    let px = mix(min_px, max_px, c);

    var out: VsOut;
    out.pos = vec4(px.x / w * 2.0 - 1.0, 1.0 - px.y / h * 2.0, 0.0, 1.0);
    out.kind = kind;
    return out;
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let fade = clamp(u.viewport.w, 0.0, 1.0);
    let px = vec2<i32>(floor(in.pos.xy));

    var color: vec3<f32>;
    switch in.kind {
        case 0u, 1u {
            // Dash runs ALONG the line: along Y for the vertical line,
            // along X for the horizontal one. 6 black + 6 white pixels
            // per period at 100 %, scaled by the whole-pixel DPI step so
            // the dashes are the same physical size on every display.
            // Anchored to absolute window coordinates so the dashes feel
            // screen-fixed rather than swimming with the cursor.
            let dpi_step = i32(floor(max(u.viewport.z, 1.0)));
            let dash_coord = select(px.x, px.y, in.kind == KIND_DASH_V);
            let period = 12 * dpi_step;
            let half_period = period / 2;
            // WGSL signed `%` preserves sign of the dividend; add
            // `period` before the second `%` so negative window
            // coordinates still yield a non-negative phase.
            let phase = ((dash_coord % period) + period) % period;
            color = select(vec3(1.0, 1.0, 1.0), vec3(0.0, 0.0, 0.0), phase < half_period);
        }
        case 2u {
            color = u.accent_color.rgb;
        }
        default {
            // Thin cross: black on light backgrounds, white on dark,
            // decided from the pixels actually displayed beneath this
            // pass. Baseline is the desktop snapshot; under an active
            // peek quad the displayed pixel is the peek composite
            // instead, replicated here from the same inputs peek.wgsl
            // uses (see CrosshairPeekUniforms above). All samples are
            // explicit-LOD: the switch is non-uniform control flow,
            // where implicit-derivative sampling is not allowed (every
            // texture here has a single mip level anyway).
            let fpos = in.pos.xy;
            let uv = u.uv_offset_scale.xy + (fpos / u.viewport.xy) * u.uv_offset_scale.zw;
            var base = textureSampleLevel(desktop_tex, desktop_samp, uv, 0.0).rgb;

            let sr = u.selection_rect;
            if (pk.params.x > 0.5 &&
                fpos.x >= sr.x && fpos.x < sr.z &&
                fpos.y >= sr.y && fpos.y < sr.w) {
                // Same interpolation the peek vertex shader produces:
                // quad-relative fraction through the window UV window.
                let frac = (fpos - sr.xy) / (sr.zw - sr.xy);
                let wuv = pk.window_uv.xy + frac * pk.window_uv.zw;
                // Outside the window texture the peek fragment discards
                // and the snapshot stays visible — keep the baseline.
                if (wuv.x >= 0.0 && wuv.x < 1.0 && wuv.y >= 0.0 && wuv.y < 1.0) {
                    var col = textureSampleLevel(window_tex, desktop_samp, wuv, 0.0).rgb;
                    let n = i32(pk.params.z);
                    var obstructed = false;
                    for (var i = 0; i < n; i++) {
                        let r = pk.obstruction_rects[i];
                        if (fpos.x >= r.x && fpos.x < r.z &&
                            fpos.y >= r.y && fpos.y < r.w) {
                            obstructed = true;
                            break;
                        }
                    }
                    if (obstructed) {
                        // peek.wgsl's ghost treatment, byte for byte:
                        // grayscale of the blurred desktop with the
                        // window ghosted over it.
                        let blur = textureSampleLevel(blur_tex, desktop_samp, uv, 0.0).rgb;
                        let gray = vec3(dot(blur, vec3(0.2126, 0.7152, 0.0722)));
                        col = mix(gray, col, pk.params.y);
                    }
                    base = col;
                }
            }

            let lum = dot(base, vec3(0.299, 0.587, 0.114));
            color = select(vec3(1.0, 1.0, 1.0), vec3(0.0, 0.0, 0.0), lum > 0.65);
        }
    }
    // Premultiplied source-over with alpha = fade reproduces the old
    // pass's `mix(base, color, fade)`.
    return vec4(color * fade, fade);
}
