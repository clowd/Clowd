// OCR scanning-sweep pass: a soft gaussian highlight band travelling down
// the selection. (This shader used to also draw the pixel-crop fallback
// lines sampled from the desktop snapshot; those went away when the text
// bubbles gained system-font fallback and became the only presentation,
// which is why there is no texture binding here any more.)
//
// The shader is deliberately dumb: ALL animated geometry (band position)
// is computed CPU-side as a pure function of the phase anchor's elapsed
// time, so every render worker derives identical geometry regardless of
// its own refresh rate. Output is premultiplied alpha; pair with
// source-over blend (One/OneMinusSrcAlpha).

struct Uniforms {
    viewport_px: vec2<f32>,
    // Seconds since the current OCR phase's anchor. Currently unread — the
    // band centre travels per-instance (CPU-side, like all other
    // animation) — but kept in the block so a future shader-side effect
    // doesn't need a layout change.
    t: f32,
    _pad: f32,
};

@group(0) @binding(0) var<uniform> u: Uniforms;

struct Instance {
    // min_x, min_y, max_x, max_y in window-local physical pixels.
    @location(0) dest_px: vec4<f32>,
    // (alpha, band_centre, sweep σ, unused). Band centre is in quad-space
    // v (0 = top, 1 = bottom); it travels top → bottom and deliberately
    // OVERSHOOTS both edges — see anim::sweep_band — so the looping wrap
    // happens with the band fully invisible. σ is supplied by the CPU
    // (anim::SWEEP_SIGMA) so the falloff and the overshoot can never
    // disagree.
    @location(1) params: vec4<f32>,
    // Band colour (straight alpha 1.0; the fragment premultiplies).
    @location(2) tint: vec4<f32>,
};

struct VsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) corner: vec2<f32>,
    @location(1) params: vec4<f32>,
    @location(2) tint: vec4<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) vi: u32, inst: Instance) -> VsOut {
    var corners = array<vec2<f32>, 6>(
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 0.0),
        vec2<f32>(1.0, 1.0),
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 1.0),
        vec2<f32>(0.0, 1.0),
    );
    let c = corners[vi];
    let px = mix(inst.dest_px.xy, inst.dest_px.zw, c);
    let ndc = vec2<f32>(
        px.x / u.viewport_px.x * 2.0 - 1.0,
        1.0 - px.y / u.viewport_px.y * 2.0,
    );

    var out: VsOut;
    out.pos = vec4<f32>(ndc, 0.0, 1.0);
    out.corner = c;
    out.params = inst.params;
    out.tint = inst.tint;
    return out;
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    // Soft gaussian band at v = params.y, in `tint` (translucent white).
    // Everything outside the band falls off to fully transparent so the
    // region content stays readable. The max() only guards a zeroed
    // instance against dividing by zero.
    let alpha = in.params.x;
    let band = in.params.y;
    let dv = in.corner.y - band;
    let sigma = max(in.params.z, 1e-3);
    let fall = exp(-(dv * dv) / (2.0 * sigma * sigma));
    let a = alpha * fall;
    return vec4<f32>(in.tint.rgb * a, a);
}
