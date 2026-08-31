// Selection overlay pass: the marching-ants border (square integer-slab
// or rounded anti-aliased), the rounded selection's faded outside-the-
// curve corners, and the resize handles. Drawn over the desktop and peek
// passes and under the crosshair (see render/frame.rs), so neither of
// those needs to know any of this geometry — the border and handles
// simply paint over whatever is beneath them.
//
// Cost model: the vertex shader emits sixteen quads that cover only the
// affected pixels — four border slabs, four corner patches (degenerate
// for square selections), eight handle squares (degenerate while the
// handles are hidden) — so the fragment cost scales with the border's
// own area instead of the whole monitor. The pass is skipped entirely
// (no draw call) when there is no selection.
//
// Shared overlay uniforms — one buffer written per frame, bound by both
// this pass and crosshair.wgsl (the struct must stay byte-identical in
// both files and in `gpu::overlay::OverlayUniforms`):
//   viewport.xy       = surface size in physical px.
//   viewport.z        = this monitor's DPI scale factor (1.0 = 100 %).
//   viewport.w        = grayscale fade factor in [0, 1]; the border and
//                       handles fade in with the overlay.
//   cursor            = unused here (crosshair pass).
//   accent_color      = RGBA color of the border dashes and the handles.
//   selection_rect    = selection in window-local physical pixels:
//                       x=left y=top z=right w=bottom (through the
//                       magnifier zoom, CPU-side). Never empty — the
//                       draw is skipped instead.
//   sel_params.x      = elapsed seconds; drives the marching-ants phase.
//   sel_params.y      = dash period in physical px for this frame,
//                       always > 0 (see render/desktop.rs dash_period).
//   sel_params.z      = the selection's corner radius in window-local px
//                       (through the zoom transform). 0 = square: the
//                       border takes the original pixel-exact integer-
//                       slab path. > 0 only for a picked window, whose
//                       OS corner radius it is; the border is then an
//                       anti-aliased rounded rect whose dashes run along
//                       the curved perimeter, and the rect's own corners
//                       — outside the curve — get the faded "outside"
//                       treatment, matching the transparent corners of
//                       the copied / saved image.
//   sel_params.w      = 1.0 when the resize handles are visible
//                       (captured, not picking a scroll point, OCR
//                       idle — decided CPU-side), else 0.0.
//   uv_offset_scale   = window px → desktop-texture UV mapping (zoom
//                       folded in); used by the corner patches to
//                       repaint the outside-the-curve corners with the
//                       faded desktop.
struct OverlayUniforms {
    viewport:        vec4<f32>,
    cursor:          vec4<f32>,
    accent_color:    vec4<f32>,
    selection_rect:  vec4<f32>,
    sel_params:      vec4<f32>,
    uv_offset_scale: vec4<f32>,
};

const PI: f32 = 3.14159265;
const HALF_PI: f32 = 1.57079633;

@group(0) @binding(0) var<uniform> u: OverlayUniforms;
@group(0) @binding(1) var desktop_tex: texture_2d<f32>;
@group(0) @binding(2) var desktop_samp: sampler;

struct VsOut {
    @builtin(position) pos: vec4<f32>,
    // Quad index: 0-3 border slabs (top/right/bottom/left), 4-7 corner
    // patches, 8-15 handles.
    @location(0) @interpolate(flat) qid: u32,
    // Handle quads only: the handle's center in window-local px.
    @location(1) @interpolate(flat) handle_center: vec2<f32>,
};

// The border's half-thickness in physical pixels, stepped on whole-pixel
// DPI boundaries — 2 px stroke at 100–199 %, 4 px at 200–299 %, … — so
// the stroke stays pixel-sharp on every display.
fn border_half() -> f32 {
    return floor(max(u.viewport.z, 1.0));
}

// The rounded selection's radius, clamped the way the window server
// clamps it (a radius past half the shorter side would invert the SDF).
fn clamped_radius() -> f32 {
    let sr = u.selection_rect;
    return min(u.sel_params.z, min(sr.z - sr.x, sr.w - sr.y) * 0.5);
}

// 16 quads × 6 vertices = 96. Rects are in window-local physical pixels
// with EXCLUSIVE max edges; degenerate (zero-area) quads rasterize
// nothing, which is how hidden handles and the square selection's unused
// corner patches cost nothing.
@vertex
fn vs_main(@builtin(vertex_index) vi: u32) -> VsOut {
    var corners = array<vec2<f32>, 6>(
        vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(1.0, 1.0),
        vec2(0.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0),
    );
    let quad = vi / 6u;
    let c = corners[vi % 6u];

    let sr = u.selection_rect;
    let half = border_half();
    let radius = u.sel_params.z;

    var rect = vec4<f32>(0.0, 0.0, 0.0, 0.0);
    var center = vec2<f32>(0.0, 0.0);

    if quad < 8u {
        if radius <= 0.0 {
            // Square: the four slabs reproduce the old integer
            // classification exactly — top/bottom span the full outer
            // width and claim the corner squares, left/right cover only
            // the strip strictly between them. Corner patches (4-7)
            // stay degenerate.
            switch quad {
                case 0u { rect = vec4(sr.x - half, sr.y - half, sr.z + half, sr.y + half); }
                case 1u { rect = vec4(sr.z - half, sr.y + half, sr.z + half, sr.w - half); }
                case 2u { rect = vec4(sr.x - half, sr.w - half, sr.z + half, sr.w + half); }
                case 3u { rect = vec4(sr.x - half, sr.y + half, sr.x + half, sr.w - half); }
                default {}
            }
        } else {
            // Rounded: the slabs cover only the straight border
            // sections (± half + 1 px for the AA fringe); the corner
            // patches cover each corner's quadrant out to where the
            // curve meets the straight edges, which also spans the
            // outside-the-curve corner region they repaint.
            let r = clamped_radius();
            let pad = half + 1.0;
            switch quad {
                case 0u { rect = vec4(sr.x + r, sr.y - pad, sr.z - r, sr.y + pad); }
                case 1u { rect = vec4(sr.z - pad, sr.y + r, sr.z + pad, sr.w - r); }
                case 2u { rect = vec4(sr.x + r, sr.w - pad, sr.z - r, sr.w + pad); }
                case 3u { rect = vec4(sr.x - pad, sr.y + r, sr.x + pad, sr.w - r); }
                case 4u { rect = vec4(sr.x - pad, sr.y - pad, sr.x + r, sr.y + r); }
                case 5u { rect = vec4(sr.z - r, sr.y - pad, sr.z + pad, sr.y + r); }
                case 6u { rect = vec4(sr.z - r, sr.w - r, sr.z + pad, sr.w + pad); }
                default { rect = vec4(sr.x - pad, sr.w - r, sr.x + r, sr.w + pad); }
            }
        }
    } else if u.sel_params.w > 0.5 {
        // Resize handles: 8 quads sized to the handle circle + AA
        // fringe, centered on the corners and edge midpoints.
        let step_f = half;
        let ext = 6.0 * step_f + 1.5;
        let mid_x = (sr.x + sr.z) * 0.5;
        let mid_y = (sr.y + sr.w) * 0.5;
        switch quad - 8u {
            case 0u { center = vec2(sr.x, sr.y); }
            case 1u { center = vec2(sr.z, sr.y); }
            case 2u { center = vec2(sr.x, sr.w); }
            case 3u { center = vec2(sr.z, sr.w); }
            case 4u { center = vec2(mid_x, sr.y); }
            case 5u { center = vec2(mid_x, sr.w); }
            case 6u { center = vec2(sr.x, mid_y); }
            default { center = vec2(sr.z, mid_y); }
        }
        rect = vec4(center - vec2(ext, ext), center + vec2(ext, ext));
    }

    let px = mix(rect.xy, max(rect.zw, rect.xy), c);

    var out: VsOut;
    out.pos = vec4(px.x / u.viewport.x * 2.0 - 1.0, 1.0 - px.y / u.viewport.y * 2.0, 0.0, 1.0);
    out.qid = quad;
    out.handle_center = center;
    return out;
}

// Gamma-2.0 sRGB approximation — same rationale as desktop.wgsl.
fn srgb_to_linear(c: vec3<f32>) -> vec3<f32> {
    return c * c;
}

fn linear_to_srgb(c: vec3<f32>) -> vec3<f32> {
    return sqrt(c);
}

// The colour of a pixel OUTSIDE the selection: the desktop crushed to
// darkened grayscale by `fade` — identical math to desktop.wgsl, applied
// here to the rounded selection's outside-the-curve corners.
fn fade_outside(base: vec3<f32>, fade: f32) -> vec3<f32> {
    if (fade == 0.0) {
        return base;
    }
    let linear = srgb_to_linear(base);
    let luma = dot(linear, vec3<f32>(0.2126, 0.7152, 0.0722)) * 0.42;
    let out_linear = mix(linear, vec3<f32>(luma), fade);
    return linear_to_srgb(out_linear);
}

// Signed distance from pixel centre `p` to the rounded rect
// [rmin, rmax] with corner radius `r`: negative inside, zero on the
// curve, positive outside. The classic Inigo Quilez rounded-box SDF.
fn rounded_rect_sdf(p: vec2<f32>, rmin: vec2<f32>, rmax: vec2<f32>, r: f32) -> f32 {
    let half_size = (rmax - rmin) * 0.5;
    let centre = rmin + half_size;
    let q = abs(p - centre) - (half_size - vec2<f32>(r, r));
    return length(max(q, vec2<f32>(0.0, 0.0))) + min(max(q.x, q.y), 0.0) - r;
}

// Arc length of the clockwise walk around a rounded rect [rmin, rmax]
// with radius `r`, starting at the top of the LEFT edge — where the
// straight left side meets the top-left curve — so the seam where the
// dash pattern wraps (the perimeter is never a whole number of periods)
// sits just before that curve rather than breaking the curve itself:
// TL arc → top → TR arc → right → BR arc → bottom → BL arc → left.
// Evaluated for a pixel by which straight edge or corner quadrant it
// falls in, so every pixel across the border's thickness gets (near) the
// same value and a dash reads as a solid stripe across the stroke — the
// same property the integer path's `arc` has.
fn rounded_rect_arc(p: vec2<f32>, rmin: vec2<f32>, rmax: vec2<f32>, r: f32) -> f32 {
    let lt = (rmax.x - rmin.x) - 2.0 * r;   // straight top / bottom length
    let ls = (rmax.y - rmin.y) - 2.0 * r;   // straight left / right length
    let qa = HALF_PI * r;                   // one quarter arc
    let near_left   = p.x < rmin.x + r;
    let near_right  = p.x > rmax.x - r;
    let near_top    = p.y < rmin.y + r;
    let near_bottom = p.y > rmax.y - r;
    if (near_top && near_left) {
        // phi in (-PI, -PI/2): from the left edge's end round to the top's start.
        let phi = atan2(p.y - (rmin.y + r), p.x - (rmin.x + r));
        return (phi + PI) * r;
    }
    if (near_top && near_right) {
        // phi in (-PI/2, 0).
        let phi = atan2(p.y - (rmin.y + r), p.x - (rmax.x - r));
        return qa + lt + (phi + HALF_PI) * r;
    }
    if (near_bottom && near_right) {
        // phi in (0, PI/2).
        let phi = atan2(p.y - (rmax.y - r), p.x - (rmax.x - r));
        return 2.0 * qa + lt + ls + phi * r;
    }
    if (near_bottom && near_left) {
        // phi in (PI/2, PI).
        let phi = atan2(p.y - (rmax.y - r), p.x - (rmin.x + r));
        return 3.0 * qa + 2.0 * lt + ls + (phi - HALF_PI) * r;
    }
    if (near_top) {
        return qa + (p.x - (rmin.x + r));
    }
    if (near_right) {
        return 2.0 * qa + lt + (p.y - (rmin.y + r));
    }
    if (near_bottom) {
        return 3.0 * qa + lt + ls + ((rmax.x - r) - p.x);
    }
    // left
    return 4.0 * qa + 2.0 * lt + ls + ((rmax.y - r) - p.y);
}

// The dash color at arc position `arc` this frame: accent for the first
// half-period, white for the second. The animation completes one full
// cycle per second (`t_offset = elapsed * period`); increasing `t_offset`
// shifts dashes toward higher arc, which is clockwise. WGSL has no f32
// `%`, so fold with floor() instead.
fn dash_color(arc: f32) -> vec4<f32> {
    let period = u.sel_params.y;
    let half_period = period * 0.5;
    let t_offset = u.sel_params.x * period;
    let raw = arc - t_offset;
    let phase = raw - period * floor(raw / period);
    return select(vec4(1.0, 1.0, 1.0, 1.0), u.accent_color, phase < half_period);
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let fade = clamp(u.viewport.w, 0.0, 1.0);
    let sr = u.selection_rect;
    let half = border_half();
    let radius = u.sel_params.z;

    if in.qid >= 8u {
        // Resize handle: an anti-aliased circle — from the edge inward:
        // `half` px accent, `half` px white ring, rest accent. One
        // distance per pixel (the quad knows its own center), where the
        // old fullscreen pass evaluated all eight.
        let step_f = half;
        let handle_r    = 6.0 * step_f;
        let white_outer = handle_r - step_f;
        let white_inner = handle_r - 3.0 * step_f;
        let aa = 0.5;
        let hd = distance(in.pos.xy, in.handle_center);
        if (hd >= handle_r + aa) {
            discard;
        }
        let outer_a = 1.0 - smoothstep(handle_r - aa, handle_r + aa, hd);
        let white_a = smoothstep(white_inner - aa, white_inner + aa, hd)
                    * (1.0 - smoothstep(white_outer - aa, white_outer + aa, hd));
        let hcol = mix(u.accent_color, vec4(1.0, 1.0, 1.0, 1.0), white_a);
        let a = fade * outer_a;
        return vec4(hcol.rgb * a, a);
    }

    if radius <= 0.0 {
        // Square border slab. The geometry guarantees the pixel is in
        // the border, and the quad index says which slab — the old
        // shader's disjoint classification by construction. Walk the
        // border clockwise from the outside-top-left corner: `arc` is
        // the cumulative pixel count along that walk, constant across
        // each slab's thickness so a dash reads as a solid stripe.
        //
        // Top/bottom slabs span the full outer width (including the
        // corner squares). Left/right slabs span only the inner-y strip
        // strictly between them.
        let px = vec2<i32>(floor(in.pos.xy));
        let ihalf = i32(half);
        let sx = i32(sr.x);
        let sy = i32(sr.y);
        let sz = i32(sr.z);
        let sw = i32(sr.w);
        let inner_top    = sy + ihalf;
        let inner_bottom = sw - ihalf - 1;
        let outer_left   = sx - ihalf;
        let outer_right  = sz + ihalf - 1;
        let top_len  = (outer_right - outer_left) + 1;
        let side_len = (inner_bottom - inner_top) + 1;

        var arc: i32;
        switch in.qid {
            case 0u { arc = px.x - outer_left; }
            case 1u { arc = top_len + (px.y - inner_top); }
            case 2u { arc = top_len + side_len + (outer_right - px.x); }
            default { arc = 2 * top_len + side_len + (inner_bottom - px.y); }
        }
        let dash = dash_color(f32(arc));
        return vec4(dash.rgb * fade, fade);
    }

    // Rounded border. The same 2*half stroke straddling the rect's edge,
    // run around a rounded rect and anti-aliased over one pixel, with
    // the dash phase measured along the curved perimeter.
    let fpos = in.pos.xy;
    let rmin = sr.xy;
    let rmax = sr.zw;
    let r = clamped_radius();
    let d = rounded_rect_sdf(fpos, rmin, rmax, r);
    // Straight edges at integer coordinates land pixel centres at
    // half-integer distances, so these two coverages are exactly 0 or 1
    // there and reproduce the integer path's classification; only the
    // curves see fractional values.
    let border_a = clamp(half + 0.5 - abs(d), 0.0, 1.0);
    let inside_a = clamp(-(d + half) + 0.5, 0.0, 1.0);
    let dash = dash_color(rounded_rect_arc(fpos, rmin, rmax, r));
    let dash_w = border_a * fade;

    if in.qid < 4u {
        // Straight slab: everything outside the stroke is already
        // correct in the framebuffer (fill inside, faded desktop
        // outside), so this is just the dash blended by its coverage.
        return vec4(dash.rgb * dash_w, dash_w);
    }

    // Corner patch: the quadrant around one corner circle. Inside the
    // curve the framebuffer already holds the selection fill; outside it
    // — the rect's square corner, which the desktop pass filled as
    // selection interior — must be repainted as faded desktop, exactly
    // the pixels the copied / saved image leaves transparent. Composed
    // as premultiplied source-over so one output expresses all three
    // regions: with dest = fill,
    //   final = mix(mix(fade_outside, fill, inside_a), dash, dash_w)
    // (Explicit-LOD sample: non-uniform control flow.)
    let uv = u.uv_offset_scale.xy + (fpos / u.viewport.xy) * u.uv_offset_scale.zw;
    let base = textureSampleLevel(desktop_tex, desktop_samp, uv, 0.0).rgb;
    let outside = fade_outside(base, fade);
    let src = outside * (1.0 - dash_w) * (1.0 - inside_a) + dash.rgb * dash_w;
    let alpha = 1.0 - (1.0 - dash_w) * inside_a;
    return vec4(src, alpha);
}
