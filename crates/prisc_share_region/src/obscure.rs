//! The obscure modes and the `obs_display` draw callback that implements them.
//!
//! Clowd asks the mirror to hide what it is showing without tearing the share
//! down — the meeting app must keep seeing the same window, receiving frames,
//! the whole time. So obscuring happens at the very last moment, in the
//! display's draw callback: instead of blitting the composited canvas straight
//! into the swapchain with `obs_render_main_texture`, we route it through a
//! small effect first (blur or pixelate), or replace it outright with a
//! generated "hidden" card. Nothing upstream in the OBS pipeline changes, so
//! switching modes is free and instantaneous and can never drop a frame.
//!
//! # Threading
//!
//! Two disjoint sets of state live in this module, and keeping them disjoint
//! is what makes the whole thing sound without a single lock:
//!
//! * The **atomics** ([`MODE`], [`STRENGTH`], [`FRAMES`]) are touched from any
//!   thread. The stdin thread writes the mode through [`set_mode`]; the status
//!   thread reads [`frames`]; the graphics thread reads the mode and bumps the
//!   frame counter.
//! * The **GPU resources** ([`Gfx`], held in [`GFX`]) are touched *only* from
//!   the OBS graphics thread, exclusively from inside [`draw`]. libobs invokes
//!   every display draw callback from that one thread, serially, between
//!   `gs_begin_scene`/`gs_end_scene` (see `render_display` in
//!   libobs/obs-display.c), so there is exactly one writer and no reader
//!   anywhere else. That is the entire safety argument for the `UnsafeCell`
//!   below; do not reach into it from anywhere but `draw`.
//!
//! Nothing here is ever freed. The effect and the two texrenders are created
//! once, on the first non-`None` frame, and live until the process exits — and
//! the process only ever exits through `obs_platform::exit_process`, which
//! deliberately does not shut libobs down (its teardown is a known crash
//! source). Destroying GPU objects at exit would therefore be strictly more
//! dangerous than leaking them.
//!
//! [`draw`] is an `extern "C"` callback: a panic crossing back into libobs is
//! an immediate abort, so there is not a single `unwrap`, index, arithmetic
//! trap or panicking print macro on runtime data below (`eprintln!` counts —
//! see [`build_gfx`]). Every failure — a missing canvas texture, an effect that
//! will not compile, a texrender that will not start — degrades to plain
//! `obs_render_main_texture()` and shows the user a live, unobscured picture
//! rather than killing the share.
//!
//! When that degradation is permanent rather than momentary, the caller is also
//! told: [`draw`] retracts the mode it cannot honour with an `obscure` status
//! line of its own, because a shell whose toolbar says "obscured" over a live
//! picture is a privacy problem, not a cosmetic one.

use std::cell::UnsafeCell;
use std::ffi::{c_void, CStr, CString};
use std::io::Write;
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};

/// What the mirror is currently showing.
///
/// The `u32` payload of [`Mode::Blur`] and [`Mode::Pixelate`] is the strength
/// on a 1..=100 scale — the scale Clowd's slider speaks, deliberately not a
/// pixel radius, so the mapping from "how hidden" to "how expensive" stays an
/// implementation detail of this file (see [`divisor`] and [`iterations`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Mode {
    /// Pass the composited canvas straight through. The zero-overhead path.
    None,
    /// Gaussian blur, still recognisable as motion but not as content.
    Blur(u32),
    /// Hard-edged blocks. Reads as "deliberately censored" where a blur can be
    /// mistaken for a broken capture.
    Pixelate(u32),
    /// Black card with a struck-through eye. Shows nothing of the region at
    /// all, which is the only honest answer when the user wants privacy rather
    /// than obfuscation.
    Hide,
}

/// The lowest strength Clowd may send. Zero would mean "blur by nothing",
/// which is indistinguishable from [`Mode::None`] but still pays for the
/// texrenders, so the protocol simply excludes it.
pub const MIN_STRENGTH: u32 = 1;
/// The highest strength Clowd may send.
pub const MAX_STRENGTH: u32 = 100;

// Mode discriminants as stored in `MODE`. Kept as plain integers rather than
// a transmuted enum so that a garbage value (which cannot actually happen —
// only `set_mode` writes here) decodes to `None` instead of to UB.
const MODE_NONE: u32 = 0;
const MODE_BLUR: u32 = 1;
const MODE_PIXELATE: u32 = 2;
const MODE_HIDE: u32 = 3;

/// Current mode discriminant. Written by [`set_mode`] from the stdin thread,
/// read by [`draw`] on the graphics thread.
static MODE: AtomicU32 = AtomicU32::new(MODE_NONE);

/// Strength that pairs with [`MODE`], meaningful only for blur and pixelate.
///
/// This is a second atomic rather than a packed word, so a mode change and a
/// strength change are not atomic *together*. That is fine and intentional:
/// the worst a torn read can do is render exactly one frame at the previous
/// strength, 33 ms of a slightly softer blur that nobody can perceive. The
/// store order in [`set_mode`] (strength first, then mode) at least guarantees
/// a reader that observes a new *mode* never pairs it with a strength older
/// than the previous mode's.
static STRENGTH: AtomicU32 = AtomicU32::new(0);

/// Frames handed to the swapchain since start. Bumped by [`draw`] on the
/// graphics thread; read by the status thread to derive the reported fps. This
/// counts real presented frames rather than a timer, so it reports what the
/// meeting app is actually receiving.
static FRAMES: AtomicU64 = AtomicU64::new(0);

/// Sets the mode. Callable from any thread; takes effect on the next frame.
///
/// Strength is clamped rather than rejected: the command parser already
/// validates the user-facing range, and silently repairing an out-of-range
/// value here is preferable to a graphics-thread branch that can misbehave.
pub fn set_mode(mode: Mode) {
    let (discriminant, strength) = match mode {
        Mode::None => (MODE_NONE, 0),
        Mode::Blur(s) => (MODE_BLUR, s.clamp(MIN_STRENGTH, MAX_STRENGTH)),
        Mode::Pixelate(s) => (MODE_PIXELATE, s.clamp(MIN_STRENGTH, MAX_STRENGTH)),
        Mode::Hide => (MODE_HIDE, 0),
    };
    // Strength before mode; see the note on STRENGTH. Release/Acquire on MODE
    // publishes the strength store to whoever observes the new mode.
    STRENGTH.store(strength, Ordering::Relaxed);
    MODE.store(discriminant, Ordering::Release);
}

/// The mode the next frame will be drawn with. Callable from any thread.
pub fn mode() -> Mode {
    let strength = || {
        STRENGTH
            .load(Ordering::Relaxed)
            .clamp(MIN_STRENGTH, MAX_STRENGTH)
    };
    match MODE.load(Ordering::Acquire) {
        MODE_BLUR => Mode::Blur(strength()),
        MODE_PIXELATE => Mode::Pixelate(strength()),
        MODE_HIDE => Mode::Hide,
        _ => Mode::None,
    }
}

/// The wire name of a mode, as it appears in the `obscure` status line.
pub fn name(mode: Mode) -> &'static str {
    match mode {
        Mode::None => "none",
        Mode::Blur(_) => "blur",
        Mode::Pixelate(_) => "pixelate",
        Mode::Hide => "hide",
    }
}

/// The strength of a mode, as it appears in the `obscure` status line. Modes
/// that have no strength report 0 rather than omitting the field, so Clowd can
/// deserialise every ack into the same shape.
pub fn strength(mode: Mode) -> u32 {
    match mode {
        Mode::Blur(s) | Mode::Pixelate(s) => s,
        Mode::None | Mode::Hide => 0,
    }
}

/// Frames drawn since the process started. The status thread samples this at
/// 1 Hz and reports the delta as fps.
pub fn frames() -> u64 {
    FRAMES.load(Ordering::Relaxed)
}

// ---------------------------------------------------------------------------
// Graphics-thread state
// ---------------------------------------------------------------------------

/// The shader, embedded rather than loaded from disk: this binary ships as a
/// single file next to Clowd with no data directory of its own, and libobs'
/// own effect search path is not somewhere we can install into.
const EFFECT_SOURCE: &str = include_str!("obscure.effect");

/// Everything [`draw`] needs on the GPU, built once and then reused forever.
///
/// The `gs_eparam_t` handles are cached because `gs_effect_get_param_by_name`
/// is a linear scan over the effect's parameter list, and the blur path would
/// otherwise repeat it up to nine times per frame.
struct Gfx {
    effect: *mut obs_sys::gs_effect_t,
    /// The `image` uniform: the texture each blit samples.
    p_image: *mut obs_sys::gs_eparam_t,
    /// The `dir` uniform: one gaussian step in UV units, direction included.
    p_dir: *mut obs_sys::gs_eparam_t,
    /// The `iconScale` uniform: uv -> icon space, for the hide card.
    p_icon_scale: *mut obs_sys::gs_eparam_t,
    /// The `px` uniform: one output pixel in icon space, for antialiasing.
    p_px: *mut obs_sys::gs_eparam_t,
    /// The `multiplier` uniform: the scale applied to the final, swapchain-
    /// bound draw so an scRGB surface gets SDR white rather than 80 nits. 1.0
    /// everywhere else, including every intermediate pass. See
    /// [`present_multiplier`].
    p_multiplier: *mut obs_sys::gs_eparam_t,
    /// Ping-pong render targets. The downsample lands in `a`; each blur
    /// iteration goes a -> b horizontally then b -> a vertically, so the
    /// finished image is always back in `a`. Two are needed because a target
    /// can never also be the texture being sampled.
    a: *mut obs_sys::gs_texrender_t,
    b: *mut obs_sys::gs_texrender_t,
}

/// One-time initialisation outcome for [`Gfx`].
enum GfxState {
    /// Nothing built yet — no non-`None` frame has been drawn.
    Uninit,
    /// The effect would not compile. Recorded permanently: retrying a compile
    /// that has already failed once would just spam the log at 30 Hz.
    Failed,
    Ready(Gfx),
}

/// Wrapper that lets [`GFX`] be a `static` despite holding raw GPU pointers.
///
/// This is *not* a general-purpose cell. The `Sync` promise it makes is only
/// true because of an external invariant that this module enforces by
/// construction: the contents are touched exclusively from inside [`draw`],
/// which libobs calls only from its single graphics thread. Any new caller
/// would break the promise, so there is deliberately no accessor — the one
/// `.get()` in this file is inside `draw`.
struct GraphicsThreadOnly<T>(UnsafeCell<T>);

// Safety: see the type's doc comment. Sound only under the "graphics thread
// is the sole accessor" invariant.
unsafe impl<T> Sync for GraphicsThreadOnly<T> {}

static GFX: GraphicsThreadOnly<GfxState> = GraphicsThreadOnly(UnsafeCell::new(GfxState::Uninit));

/// Downscale divisor for a given strength: the canvas is rendered at 1/D of
/// its size before being blown back up.
///
/// Downsampling is where nearly all of the obscuring happens, and it is almost
/// free — the expensive full-resolution passes are replaced by one cheap
/// filtered blit. For pixelate this divisor *is* the effect: D is literally
/// the block size in canvas pixels. For blur it sets the scale of detail that
/// survives, and the gaussian iterations then smooth over the blockiness the
/// downsample leaves behind.
///
/// The range is deliberately narrow. Below 2 there is nothing to gain, and
/// above about 10 a 1080p canvas is down to 100-odd pixels across, at which
/// point further division stops looking blurrier and starts looking broken.
fn divisor(strength: u32) -> u32 {
    (2 + strength / 14).clamp(2, 10)
}

/// Number of separable gaussian iterations (blur only). Each iteration is a
/// horizontal pass plus a vertical one over the small downsampled image.
///
/// Two knobs rather than one because they do different jobs: the divisor
/// destroys information cheaply, the iterations make what is left look like a
/// blur rather than a mosaic. Turning the divisor up alone drifts towards
/// pixelation; turning the iterations up alone costs real fill rate for a
/// blur that a screen-reader-with-a-camera could still resolve. Four
/// iterations at 1/10 scale is already far past the point of diminishing
/// returns for a 4K canvas.
fn iterations(strength: u32) -> u32 {
    (1 + strength / 25).clamp(1, 4)
}

/// Half-extent of the hide icon in its own coordinate space: the icon is the
/// 24x24 "visibility off" glyph scaled so one icon unit is 12 glyph units, and
/// its widest part — the eye, spanning x 1..23 — reaches +/-0.917. The margin
/// keeps the antialiasing of that edge off the edge of the icon box.
const ICON_EXTENT: f32 = 0.95;

/// The hide icon is drawn at a quarter of the shorter surface dimension, but
/// never smaller than this. The mirror surface is normally hundreds of pixels
/// across, but a user is free to share a 40x40 region, and on such a surface a
/// proportional icon would be a grey smudge. Below the floor the icon simply
/// takes over more of the card, which still reads correctly.
const ICON_MIN_PX: f32 = 24.0;

// ---------------------------------------------------------------------------
// The draw callback
// ---------------------------------------------------------------------------

/// The `obs_display` draw callback.
///
/// # Safety
/// Must only be registered with `obs_display_add_draw_callback`, i.e. must
/// only ever be invoked on the OBS graphics thread with the display's render
/// target bound. libobs has already set the render target to the swapchain,
/// set the viewport to `cx` by `cy`, and applied
/// `gs_ortho(0, cx, 0, cy, -100, 100)` before calling us, so a full-surface
/// sprite is just `gs_draw_sprite(tex, 0, cx, cy)`.
///
/// `param` is ignored; the mirror deliberately passes null, because this
/// callback must never be able to reach `Mirror` or any UI state.
pub unsafe extern "C" fn draw(_param: *mut c_void, cx: u32, cy: u32) {
    FRAMES.fetch_add(1, Ordering::Relaxed);

    let mode = mode();
    if mode == Mode::None {
        // The fast path, and the one that runs 99% of the time: no texrender,
        // no effect, no allocation, nothing lazily built. Keeping it textually
        // ahead of every other consideration in this function is the point.
        unsafe { obs_sys::obs_render_main_texture() };
        return;
    }

    // Safety: single graphics thread, see the module docs. The borrow ends
    // before this function returns and nothing re-enters `draw`.
    let state = unsafe { &mut *GFX.0.get() };
    if matches!(state, GfxState::Uninit) {
        // We are already inside the graphics context (libobs entered it before
        // dispatching draw callbacks), so no obs_enter_graphics is needed or
        // wanted here — it is not reentrant-friendly on all backends.
        *state = match unsafe { build_gfx() } {
            Some(gfx) => GfxState::Ready(gfx),
            None => GfxState::Failed,
        };
    }
    let gfx = match &*state {
        GfxState::Ready(gfx) => gfx,
        // Failed (or, impossibly, still Uninit): live picture, no chrome — and
        // the caller is TOLD so.
        //
        // The ack it already received said "blur", and it is about to be shown
        // an unobscured picture it believes is obscured. Retracting locally is
        // not enough: the party that renders the toolbar is the shell, the only
        // other diagnostic is a stderr line it merely buffers, and
        // `GfxState::Failed` is permanent, so it would keep asking and keep
        // being told yes. So the retraction goes on the wire as well.
        //
        // This is the one place a protocol line is written from the graphics
        // thread, and it is legal because `emit_obscure` only takes the stdout
        // lock and writes — it touches no OBS state and cannot block on
        // anything this thread holds. It runs at most once per obscure command,
        // not per frame: `set_mode(None)` sends every subsequent frame down the
        // fast path at the top of this function, and the next command is what
        // brings it back here.
        _ => {
            set_mode(Mode::None);
            crate::status::emit_obscure(Mode::None);
            unsafe { obs_sys::obs_render_main_texture() };
            return;
        }
    };

    match mode {
        Mode::Hide => unsafe { draw_hide(gfx, cx, cy) },
        Mode::Blur(s) => unsafe { draw_filtered(gfx, cx, cy, s, true) },
        Mode::Pixelate(s) => unsafe { draw_filtered(gfx, cx, cy, s, false) },
        // Unreachable (checked above), but written as a real arm rather than
        // an `unreachable!()`: a panic here would abort the process.
        Mode::None => unsafe { obs_sys::obs_render_main_texture() },
    }
}

/// Compiles the effect and allocates the ping-pong targets. Returns `None`
/// after logging, once, on any failure.
///
/// Both diagnostics below go through `writeln!` on a locked stderr rather than
/// `eprintln!`, which is not merely a style choice: `eprintln!` unwraps the
/// write and panics with "failed printing to stderr" on any I/O error, and the
/// whole call chain from [`draw`] is inside an `extern "C"` callback where an
/// unwind is an immediate abort. The failure that would trigger it is not
/// hypothetical — if the shell dies or is killed, the read end of the stderr
/// pipe closes and the next write returns a broken pipe — and it would land on
/// exactly the frame where something has already gone wrong, losing both the
/// exit code the shell is waiting on and the permanent live-picture fallback
/// this function exists to arrange. Nothing reachable from `draw` may panic.
///
/// # Safety
/// Graphics thread, inside the graphics context.
unsafe fn build_gfx() -> Option<Gfx> {
    // EFFECT_SOURCE is a compile-time constant with no interior NUL, so this
    // cannot fail — but it is still written as a fallible match, because the
    // alternative is an `unwrap` inside a call chain that ends at an
    // `extern "C"` boundary.
    let source = match CString::new(EFFECT_SOURCE) {
        Ok(s) => s,
        Err(_) => return None,
    };

    // `filename` is only ever used in log messages; libobs happily takes an
    // invented one for an in-memory effect.
    let mut error: *mut std::os::raw::c_char = std::ptr::null_mut();
    let effect = unsafe {
        obs_sys::gs_effect_create(source.as_ptr(), c"obscure.effect".as_ptr(), &mut error)
    };
    if effect.is_null() {
        let detail = if error.is_null() {
            "no detail from the shader compiler".to_string()
        } else {
            // Deliberately not freed: `bfree` is the matching deallocator and
            // this path runs at most once in the life of the process, so a
            // few hundred leaked bytes are cheaper than another FFI edge.
            unsafe { CStr::from_ptr(error) }
                .to_string_lossy()
                .into_owned()
        };
        let _ = writeln!(
            std::io::stderr(),
            "obscure: the obscure effect failed to compile, falling back to the live picture \
             permanently:\n{detail}"
        );
        return None;
    }

    // Missing params come back null; that is not fatal on its own, because
    // `gs_effect_set_*` tolerates a null param, and a technique that does not
    // reference a uniform legitimately has none. Cache whatever exists.
    let param =
        |name: &CStr| unsafe { obs_sys::gs_effect_get_param_by_name(effect, name.as_ptr()) };

    // GS_ZS_NONE: there is no depth in a 2D blit chain, and skipping the
    // depth/stencil surface halves the memory each target costs.
    let make_target = || unsafe {
        obs_sys::gs_texrender_create(
            obs_sys::gs_color_format_GS_RGBA,
            obs_sys::gs_zstencil_format_GS_ZS_NONE,
        )
    };
    let a = make_target();
    let b = make_target();
    if a.is_null() || b.is_null() {
        let _ = writeln!(
            std::io::stderr(),
            "obscure: could not allocate the obscure render targets, falling back to the live \
             picture permanently"
        );
        return None;
    }

    Some(Gfx {
        effect,
        p_image: param(c"image"),
        p_dir: param(c"dir"),
        p_icon_scale: param(c"iconScale"),
        p_px: param(c"px"),
        p_multiplier: param(c"multiplier"),
        a,
        b,
    })
}

/// The value of the `multiplier` uniform for a draw that goes straight into the
/// display's swapchain, and the reason the final blit is treated differently
/// from every pass before it.
///
/// The mirror's swapchain is NOT always an 8-bit sRGB surface. libobs creates
/// the display with `num_backbuffers = 0`, which its D3D11 backend turns into a
/// flip-model swapchain on any modern Windows, and a flip-model swapchain picks
/// its colour space from the monitor: `GS_CS_SRGB_16F` for anything reporting
/// more than 8 bits per colour, `GS_CS_709_SCRGB` for an HDR monitor
/// (`get_next_space` in d3d11-subsystem.cpp). Both of those make the backbuffer
/// `GS_RGBA16F`, which holds LINEAR values — and parking the mirror off screen
/// does not opt out of it, because the swapchain resolves its monitor with
/// `MONITOR_DEFAULTTONEAREST`.
///
/// libobs' own `obs_render_main_texture` handles this (see
/// `obs_render_canvas_texture_internal` in obs.c): it enables the sRGB
/// framebuffer, binds the canvas through an sRGB view so the encoded texels are
/// linearised on read, and on scRGB scales the result by
/// `obs_get_video_sdr_white_level() / 80`. The obscure path has to do the same
/// or the two modes disagree: switching obscure on would visibly change the
/// brightness of the picture, which is exactly the wrong moment to draw
/// attention to a rendering difference.
///
/// scRGB's unit is 80 nits, so SDR white is `sdr_white_level / 80` rather than
/// 1.0; on every other colour space the shader's output is already in the
/// surface's own units and the multiplier is the identity.
///
/// # Safety
/// Graphics thread: reads the current render target's colour space.
unsafe fn present_multiplier() -> f32 {
    if unsafe { obs_sys::gs_get_color_space() } == obs_sys::gs_color_space_GS_CS_709_SCRGB {
        unsafe { obs_sys::obs_get_video_sdr_white_level() / 80.0 }
    } else {
        1.0
    }
}

/// Blur and pixelate: downsample the composited canvas, optionally blur the
/// small copy, then blow it back up over the whole display surface.
///
/// # Safety
/// Graphics thread, display render target bound and ortho'd to `cx` by `cy`.
unsafe fn draw_filtered(gfx: &Gfx, cx: u32, cy: u32, strength: u32, blur: bool) {
    let src = unsafe { obs_sys::obs_get_main_texture() };
    if src.is_null() {
        // Returns null until the first composite has happened, which is
        // routine for the first frame or two after a `reset_video`. Show the
        // live path (which no-ops identically in that state) and try again
        // next frame rather than presenting an uninitialised target.
        unsafe { obs_sys::obs_render_main_texture() };
        return;
    }
    let sw = unsafe { obs_sys::gs_texture_get_width(src) };
    let sh = unsafe { obs_sys::gs_texture_get_height(src) };
    if sw == 0 || sh == 0 || cx == 0 || cy == 0 {
        unsafe { obs_sys::obs_render_main_texture() };
        return;
    }

    let d = divisor(strength);
    let dw = (sw / d).max(1);
    let dh = (sh / d).max(1);

    unsafe {
        // Every draw here covers its entire target with opaque pixels, so
        // blending is not merely unnecessary, it is wrong: the canvas can
        // carry a non-opaque alpha channel and the default INVSRCALPHA blend
        // would composite it against whatever the target already held.
        // ONE/ZERO makes each pass a straight overwrite.
        //
        // This has to be pushed around the texrenders as well as the final
        // blit: `gs_texrender_end` restores the render target, viewport,
        // projection and matrix stack (verified in
        // libobs/graphics/texture-render.c) but says nothing about blend
        // state, so an unbalanced change here would leak into the next
        // display and into libobs' own rendering.
        obs_sys::gs_blend_state_push();
        obs_sys::gs_blend_function(
            obs_sys::gs_blend_type_GS_BLEND_ONE,
            obs_sys::gs_blend_type_GS_BLEND_ZERO,
        );

        // sRGB handling is switched off for the INTERMEDIATE chain rather than
        // emulated. libobs' own `obs_render_main_texture` enables the sRGB
        // framebuffer and samples through an sRGB view, which linearises on
        // read and re-encodes on write — an exact identity for a plain blit,
        // and the only difference for us is whether the blur averages in
        // linear light or in the encoded space. For an obscuring blur that
        // difference is imperceptible, and doing it this way keeps every pass
        // a bit-exact copy of its input where it does not filter, which is
        // much easier to reason about when something looks wrong.
        //
        // The FINAL blit is a different matter and is handled at step 3: that
        // one writes into the display's swapchain, which is not necessarily an
        // encoded surface at all (see [`present_multiplier`]).
        let prev_srgb = obs_sys::gs_framebuffer_srgb_enabled();
        obs_sys::gs_enable_framebuffer_srgb(false);

        // 1. Downsample the full canvas into A. Linear sampling here is what
        //    averages each DxD block down to one texel, so pixelate gets its
        //    block colours from this step and not from the upsample.
        let mut current = render_into(gfx, gfx.a, src, dw, dh, c"DrawLinear");

        // 2. Blur only: separable gaussian, horizontally then vertically, over
        //    the small image. `dir` is one texel of the *downsampled* target,
        //    which is why the blur radius scales with the divisor for free.
        if blur && !current.is_null() {
            let steps = iterations(strength);
            for _ in 0..steps {
                set_vec2(gfx.p_dir, 1.0 / dw as f32, 0.0);
                let horizontal = render_into(gfx, gfx.b, current, dw, dh, c"Blur");
                if horizontal.is_null() {
                    break;
                }
                set_vec2(gfx.p_dir, 0.0, 1.0 / dh as f32);
                let vertical = render_into(gfx, gfx.a, horizontal, dw, dh, c"Blur");
                if vertical.is_null() {
                    // A is now half-written; `current` still points at a
                    // complete image (B's), so keep showing that.
                    current = horizontal;
                    break;
                }
                current = vertical;
            }
        }

        if current.is_null() {
            // A texrender refused to start (out of video memory, or a zero
            // size we failed to catch). Nothing valid to upscale.
            obs_sys::gs_enable_framebuffer_srgb(prev_srgb);
            obs_sys::gs_blend_state_pop();
            obs_sys::obs_render_main_texture();
            return;
        }

        // 3. Back up to the display. Point sampling is the whole of pixelate:
        //    it reproduces each downsampled texel as a hard-edged block, where
        //    linear sampling would interpolate them into a soft gradient.
        //
        //    This is the pass that writes into the swapchain, so it is the one
        //    pass that has to speak the swapchain's colour space rather than
        //    the encoded space the chain above works in. Enabling the sRGB
        //    framebuffer and binding the intermediate through an sRGB view
        //    (`gs_effect_set_texture_srgb`, which linearises on read) hands the
        //    shader linear values and asks the surface to encode them again on
        //    write. That is an exact identity on an 8-bit sRGB swapchain, and
        //    it is the CORRECTION on a 16F one, where the framebuffer flag does
        //    nothing and the surface holds linear values that our previously
        //    encoded output would have washed out. `multiplier` then puts SDR
        //    white where scRGB expects it. See [`present_multiplier`].
        let technique = if blur { c"DrawLinear" } else { c"DrawPoint" };
        obs_sys::gs_enable_framebuffer_srgb(true);
        obs_sys::gs_effect_set_texture_srgb(gfx.p_image, current);
        obs_sys::gs_effect_set_float(gfx.p_multiplier, present_multiplier());
        while obs_sys::gs_effect_loop(gfx.effect, technique.as_ptr()) {
            obs_sys::gs_draw_sprite(current, 0, cx, cy);
        }

        obs_sys::gs_enable_framebuffer_srgb(prev_srgb);
        obs_sys::gs_blend_state_pop();
    }
}

/// Hide: no source texture at all, just the generated card over the whole
/// display surface.
///
/// # Safety
/// Graphics thread, display render target bound and ortho'd to `cx` by `cy`.
unsafe fn draw_hide(gfx: &Gfx, cx: u32, cy: u32) {
    if cx == 0 || cy == 0 {
        return;
    }
    let (scale_x, scale_y, px) = icon_uniforms(cx, cy);

    unsafe {
        obs_sys::gs_blend_state_push();
        obs_sys::gs_blend_function(
            obs_sys::gs_blend_type_GS_BLEND_ONE,
            obs_sys::gs_blend_type_GS_BLEND_ZERO,
        );

        // Same swapchain colour-space handling as the final blit in
        // `draw_filtered`, for the same reason: the card's colours are written
        // straight into the display surface. There is no source texture to
        // linearise here — the shader generates its own black and white — so
        // the only two parts that apply are the framebuffer encode (which makes
        // the shader's output linear by definition, and is a no-op on a 16F
        // surface, which is linear already) and the scRGB multiplier, without
        // which "white" would be 1.0 linear, i.e. 80 nits: a grey card on an
        // HDR display rather than the SDR white the icon is meant to read as.
        let prev_srgb = obs_sys::gs_framebuffer_srgb_enabled();
        obs_sys::gs_enable_framebuffer_srgb(true);

        set_vec2(gfx.p_icon_scale, scale_x, scale_y);
        obs_sys::gs_effect_set_float(gfx.p_px, px);
        obs_sys::gs_effect_set_float(gfx.p_multiplier, present_multiplier());
        // Null texture: the technique never samples `image`, and
        // `gs_draw_sprite` only needs a texture when it has to infer the
        // sprite's size, which the explicit cx/cy supply here.
        while obs_sys::gs_effect_loop(gfx.effect, c"Hide".as_ptr()) {
            obs_sys::gs_draw_sprite(std::ptr::null_mut(), 0, cx, cy);
        }

        obs_sys::gs_enable_framebuffer_srgb(prev_srgb);
        obs_sys::gs_blend_state_pop();
    }
}

/// Renders `src` into `target` at `w` by `h` with `technique`, and
/// returns the resulting texture (null if the target could not be started).
///
/// The projection is set explicitly inside the begin/end pair because the
/// display's ortho, sized to the swapchain, is meaningless for a target of a
/// different size — and `gs_texrender_end` pops ours back off afterwards, so
/// this cannot disturb the caller.
///
/// # Safety
/// Graphics thread. `src` must be a live 2D texture and must not be
/// `target`'s own texture (a target can never also be a sampled source).
unsafe fn render_into(
    gfx: &Gfx,
    target: *mut obs_sys::gs_texrender_t,
    src: *mut obs_sys::gs_texture_t,
    w: u32,
    h: u32,
    technique: &CStr,
) -> *mut obs_sys::gs_texture_t {
    unsafe {
        // A texrender refuses to begin twice without a reset — it latches a
        // "rendered" flag on end so that a cache-shaped caller can skip
        // redundant work. We re-render every frame, so we reset every time.
        obs_sys::gs_texrender_reset(target);
        if !obs_sys::gs_texrender_begin(target, w, h) {
            return std::ptr::null_mut();
        }
        obs_sys::gs_ortho(0.0, w as f32, 0.0, h as f32, -100.0, 100.0);
        // No clear: the sprite below covers all w*h texels with an opaque
        // ONE/ZERO write, so whatever the target held is fully replaced.
        obs_sys::gs_effect_set_texture(gfx.p_image, src);
        // Identity: `multiplier` belongs to the swapchain-bound draw alone, and
        // every intermediate target holds the picture in its own encoded space.
        // Set on every pass rather than once, because the same effect object —
        // and therefore the same uniform — is what the final blit scales.
        obs_sys::gs_effect_set_float(gfx.p_multiplier, 1.0);
        while obs_sys::gs_effect_loop(gfx.effect, technique.as_ptr()) {
            obs_sys::gs_draw_sprite(src, 0, w, h);
        }
        obs_sys::gs_texrender_end(target);
        obs_sys::gs_texrender_get_texture(target)
    }
}

/// Computes `(iconScale.x, iconScale.y, px)` for the hide card.
///
/// The shader works in an "icon space" where the drawing occupies
/// +/-[`ICON_EXTENT`] on both axes, and reaches it with
/// `p = (uv - 0.5) * iconScale`. Deriving both components from a single
/// pixels-per-icon-unit figure is what keeps the eye circular on a wide
/// surface: the *same* number of output pixels maps to one icon unit
/// horizontally and vertically, so the aspect distortion of uv space is
/// exactly undone.
fn icon_uniforms(cx: u32, cy: u32) -> (f32, f32, f32) {
    let short = cx.min(cy).max(1) as f32;
    // A quarter of the shorter side, floored so a tiny region still shows a
    // legible icon, and capped so the floor can never push the icon off the
    // surface it is drawn on.
    let size = (short * 0.25).max(ICON_MIN_PX).min(short);
    let unit = size / (2.0 * ICON_EXTENT);
    (cx as f32 / unit, cy as f32 / unit, 1.0 / unit)
}

/// Sets a `float2` uniform.
///
/// `vec2` is a union in the generated bindings, so it cannot be built with a
/// struct literal; going through the `ptr` arm is the tidiest way to fill it,
/// and matches libobs' own memory layout (`{float x, y}` overlaid on
/// `float[2]`).
///
/// # Safety
/// Graphics thread. A null `param` is fine — `gs_effect_set_val` checks.
unsafe fn set_vec2(param: *mut obs_sys::gs_eparam_t, x: f32, y: f32) {
    let value = obs_sys::vec2 {
        __bindgen_anon_1: obs_sys::vec2__bindgen_ty_1 { ptr: [x, y] },
    };
    unsafe { obs_sys::gs_effect_set_vec2(param, &value) };
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The strength knobs must stay inside the ranges the render path assumes:
    /// a divisor below 2 would make the downsample a no-op that still pays for
    /// two texrenders, and an iteration count of 0 would leave blur looking
    /// exactly like pixelate.
    #[test]
    fn strength_knobs_stay_in_range() {
        for strength in MIN_STRENGTH..=MAX_STRENGTH {
            let d = divisor(strength);
            let p = iterations(strength);
            assert!((2..=10).contains(&d), "divisor({strength}) = {d}");
            assert!((1..=4).contains(&p), "iterations({strength}) = {p}");
        }
        // Monotonic: a higher strength must never obscure less.
        for strength in MIN_STRENGTH..MAX_STRENGTH {
            assert!(divisor(strength) <= divisor(strength + 1));
            assert!(iterations(strength) <= iterations(strength + 1));
        }
    }

    /// The mode round-trips through the atomics, including the strength clamp
    /// that protects the graphics thread from an out-of-range value.
    #[test]
    fn mode_round_trips_through_atomics() {
        set_mode(Mode::Blur(40));
        assert_eq!(mode(), Mode::Blur(40));
        assert_eq!(name(mode()), "blur");
        assert_eq!(strength(mode()), 40);

        set_mode(Mode::Pixelate(9999));
        assert_eq!(mode(), Mode::Pixelate(MAX_STRENGTH));

        set_mode(Mode::Pixelate(0));
        assert_eq!(mode(), Mode::Pixelate(MIN_STRENGTH));

        set_mode(Mode::Hide);
        assert_eq!(mode(), Mode::Hide);
        assert_eq!(strength(mode()), 0);

        set_mode(Mode::None);
        assert_eq!(mode(), Mode::None);
        assert_eq!(name(mode()), "none");
    }

    /// The icon keeps its aspect ratio (one icon unit is the same number of
    /// pixels on both axes) and never overflows the surface it is drawn on.
    #[test]
    fn icon_fits_and_keeps_aspect() {
        for (cx, cy) in [(1920u32, 1080u32), (100, 400), (64, 64), (8, 8), (1, 1)] {
            let (sx, sy, px) = icon_uniforms(cx, cy);
            // px is 1/unit, so cx/unit == cx*px: same unit on both axes.
            assert!((sx - cx as f32 * px).abs() < 1e-3, "{cx}x{cy} x aspect");
            assert!((sy - cy as f32 * px).abs() < 1e-3, "{cx}x{cy} y aspect");
            // The icon must fit inside the surface on its tightest axis: the
            // half-surface in icon units is min(sx, sy) / 2, and the icon's
            // half-extent must not exceed it.
            let tightest = sx.min(sy);
            assert!(
                ICON_EXTENT / tightest <= 0.5 + 1e-3,
                "{cx}x{cy} icon overflows the surface"
            );
        }
    }
}
