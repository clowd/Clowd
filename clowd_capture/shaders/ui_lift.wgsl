// OCR lift pass: instanced textured quads that raise pixel crops of the
// recognized lines off the page, and the scanning sweep. (No shadows —
// the crops rise over an already darkened, desaturated page, so a drop
// shadow adds nothing; the old mode 0 shadow SDF was removed with them.)
//
// The shader is deliberately dumb: ALL animated geometry (lift offset,
// scale, sweep band position) is computed CPU-side as a pure function of
// the phase anchor's elapsed time, so every render worker derives
// identical geometry regardless of its own refresh rate. Output is
// premultiplied alpha; pair with source-over blend (One/OneMinusSrcAlpha).
//
// Instance modes (params.x):
//   1 = textured line — samples the whole-virtual-desktop snapshot through
//                 src_uv at params.y alpha.
//   2 = sweep   — soft horizontal highlight band across the quad, band
//                 centre at params.z in quad-space v (0 = top, 1 = bottom;
//                 travels top → bottom, and deliberately OVERSHOOTS both
//                 edges — see anim::sweep_band — so the looping wrap
//                 happens with the band fully invisible). params.w is the
//                 gaussian σ, supplied by the CPU (anim::SWEEP_SIGMA) so
//                 the falloff and the overshoot can never disagree.

struct Uniforms {
    viewport_px: vec2<f32>,
    // Seconds since the current OCR phase's anchor. Currently unread — the
    // sweep's band centre travels per-instance (CPU-side, like all other
    // animation) — but kept in the block so a future shader-side effect
    // doesn't need a layout change.
    t: f32,
    _pad: f32,
};

@group(0) @binding(0) var<uniform> u: Uniforms;
@group(0) @binding(1) var snap_tex: texture_2d<f32>;
@group(0) @binding(2) var snap_samp: sampler;

struct Instance {
    // min_x, min_y, max_x, max_y in window-local physical pixels.
    @location(0) dest_px: vec4<f32>,
    // u0, v0, u1, v1 into the virtual-desktop snapshot texture.
    @location(1) src_uv: vec4<f32>,
    // (mode, alpha, band_centre, sweep σ) — see header.
    @location(2) params: vec4<f32>,
    // Sweep colour (straight alpha 1.0; the fragment premultiplies).
    @location(3) tint: vec4<f32>,
};

struct VsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) corner: vec2<f32>,
    @location(1) src_uv: vec4<f32>,
    @location(2) params: vec4<f32>,
    @location(3) tint: vec4<f32>,
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
    out.src_uv = inst.src_uv;
    out.params = inst.params;
    out.tint = inst.tint;
    return out;
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let mode = i32(round(in.params.x));
    let alpha = in.params.y;

    // Sampled unconditionally: `textureSample` uses implicit derivatives and
    // must sit in uniform control flow, and `mode` varies per instance — a
    // sample inside the `mode == 1` branch would fail naga validation. The
    // sweep mode simply ignores the texel (its src_uv is zeroed).
    let uv = mix(in.src_uv.xy, in.src_uv.zw, in.corner);
    let texel = textureSample(snap_tex, snap_samp, uv);

    if (mode == 2) {
        // Scanning sweep: a soft gaussian band at v = params.z, in `tint`
        // (translucent white). Everything outside the band falls off to
        // fully transparent so the region content stays readable. σ comes
        // per-instance from anim::SWEEP_SIGMA (the max() only guards a
        // zeroed instance against dividing by zero).
        let band = in.params.z;
        let dv = in.corner.y - band;
        let sigma = max(in.params.w, 1e-3);
        let fall = exp(-(dv * dv) / (2.0 * sigma * sigma));
        let a = alpha * fall;
        return vec4<f32>(in.tint.rgb * a, a);
    }

    // Mode 1: the lifted line itself. Force rgb straight from the texture
    // and NEVER multiply by texel.a — the BitBlt'd desktop frequently
    // carries a == 0 and `texel * alpha` would render nothing (the same
    // forced-alpha treatment desktop.wgsl:114 applies to the base pass).
    return vec4<f32>(texel.rgb * alpha, alpha);
}
