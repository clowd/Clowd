//! The obs half of the binary: scene + display-capture sources + the
//! `obs_display` that paints the composited canvas into the mirror window.
//!
//! Bootstrap mirrors `Recorder::new` steps 1–7 in obs-express (same ordering
//! constraints, same fail/exit discipline) minus everything recorder-shaped:
//! no audio devices, no encoders, no output, no webcam, no tracker. With no
//! output the pipeline is just capture → compose → swapchain present, which is
//! what makes the live mirror nearly free (SHARE_REGION_PLAN §1).
//!
//! What this module does NOT own is the pixels: the display's draw callback is
//! `crate::obscure::draw`, which lives on the OBS graphics thread and keeps its
//! own state in atomics. Nothing here is reachable from that thread, which is
//! the invariant that lets `Mirror` stay a plain `&mut`-driven UI-thread type.

use std::ffi::{c_void, CString};
use std::fmt::Display;

use obs::audio::AudioInfo;
use obs::context::ObsContext;
use obs::display::ObsDisplay;
use obs::scene::{ObsScene, ObsSceneItem};
use obs::source::ObsSource;
use obs::video::VideoInfo;
use obs_platform::region::{self, Rect, RegionPlan};
use obs_platform::{CaptureMethod, MonitorInfo};

/// Floor on the region width/height accepted by `bootstrap` and `set_region`.
/// A degenerate or one-pixel region would produce a canvas the swapchain and
/// the scene math cannot meaningfully represent (and, on the Windows side, a
/// mirror window smaller than its own minimum tracking size), so both the
/// `--region` flag and the shell's `move` command are clamped rather than
/// trusted. 64 px is the same floor the deleted interactive resize used, kept
/// so the clamp behaviour does not change.
const MIN_REGION: u32 = 64;

/// The one place a requested region is turned into a region this process can
/// actually mirror. Everything that reaches the wire as `sharing_started` or
/// `region_changed`, and everything the platform layer sizes the window from,
/// has been through here.
///
/// Two adjustments, both of which the caller learns about because the adjusted
/// rect is what gets acked:
///
/// * The size is floored at [`MIN_REGION`], for the reasons on that constant.
/// * The size is rounded DOWN to even, because `plan_region` forces the canvas
///   even (`region.w * canvas_scale` masked with `& !1`) and the mirror window
///   is sized from the region. An odd 801-px region would otherwise present an
///   800-px-wide swapchain into an 801-px-wide window, and DXGI would stretch
///   the mirror by 801/800 — a resampled, slightly soft picture — while the
///   `region_changed` ack told Clowd to draw its border at 801. Rounding the
///   region itself keeps the window, the canvas and the ack in one value
///   domain, which is the property `AppEvents::set_region` is built on. (Odd
///   sizes are easy to produce: Clowd computes the region from a border the
///   user drags by hand.) Rounding down rather than up keeps a region that was
///   just inside a display from being pushed a pixel past its edge.
///
/// Order matters: the floor is applied first and [`MIN_REGION`] is itself even,
/// so the rounding can never take a size back below the floor.
fn normalize_region(region: Rect) -> Rect {
    Rect {
        w: region.w.max(MIN_REGION) & !1,
        h: region.h.max(MIN_REGION) & !1,
        ..region
    }
}

/// Runtime/obs failures exit 1. Never unwind: partial OBS state is never torn
/// down (same invariant as obs-express — libobs shutdown is a known crash
/// source; see crates/obs/src/context.rs), so every failure path must leave
/// through `exit_process`.
fn fail(msg: impl Display) -> ! {
    eprintln!("Fatal: {msg}");
    obs_platform::exit_process(1)
}

/// Argument-shaped failures exit 2 (matching obs-express's `fail_args`), so
/// callers can distinguish their own bad input from an obs breakage.
fn fail_args(msg: impl Display) -> ! {
    eprintln!("Fatal: {msg}");
    obs_platform::exit_process(2)
}

pub struct Mirror {
    context: ObsContext,
    scene: ObsScene,
    /// One display-capture source + scene item per planned (intersected)
    /// monitor, in `plan.items` order. Rebuilt only when the intersected
    /// monitor set changes (see `set_region`).
    sources: Vec<ObsSource>,
    items: Vec<ObsSceneItem>,
    /// Created lazily by `attach_display` once the UI hands over the mirror
    /// window; `None` only between bootstrap and mirror_ready.
    display: Option<ObsDisplay>,
    /// Monitors as enumerated at bootstrap. Not re-enumerated on display
    /// config changes — the region math plans against this snapshot.
    monitors: Vec<MonitorInfo>,
    /// Indices into `monitors` of the currently planned items (the "monitor
    /// set" `set_region` compares against to decide whether the sources have
    /// to be rebuilt or merely repositioned).
    monitor_set: Vec<usize>,
    /// The region currently being mirrored, after normalisation. Only ever
    /// written by a `set_region` that succeeded, so it is always a rect known
    /// to intersect at least one monitor.
    region: Rect,
    /// Canvas in capture px (== ObsDisplay size, == reset_video base/output).
    /// The plan's `canvas_scale` is deliberately not cached alongside it: it
    /// only ever existed to keep the deleted cheap-drag path honest, and every
    /// `set_region` re-plans from scratch anyway.
    canvas: (u32, u32),
    fps: u32,
    show_cursor: bool,
    /// Display-capture backend, fixed for the process lifetime; re-applied
    /// verbatim whenever `set_region` rebuilds the scene items.
    capture_method: CaptureMethod,
    /// Graphics adapter chosen from the BOOTSTRAP region, and stuck with: the
    /// device is built by the first `obs_reset_video` and libobs ignores the
    /// field on every later one, so a `move` onto a display driven by another
    /// GPU cannot follow it. Harmless under WGC; under `--capture-method dxgi`
    /// that moved-to display will not capture.
    adapter: u32,
}

impl Mirror {
    /// Builds the whole OBS pipeline. Order matters, same as `Recorder::new`:
    /// the libobs data path MUST be registered before `obs_reset_video`
    /// (graphics init loads `default.effect` etc. through
    /// `obs_find_data_file`, whose built-in fallback is CWD-relative and
    /// resolves nowhere in our layout).
    ///
    /// Exits (never unwinds) on any failure: exit 2 for a region that
    /// intersects no monitor (caller input), exit 1 for everything else.
    /// `obs_platform::init_process()` must already have run (main.rs does it
    /// before the platform app setup).
    ///
    /// The `--region` rect goes through [`normalize_region`] exactly like a
    /// `move` does, so the region reported by `sharing_started` is drawn from
    /// the same value domain as every later `region_changed`. Without that, a
    /// `--region 0,0,10,10` would be mirrored (and announced) at 10x10 — a size
    /// no `move` command can ever reproduce, so a shell that echoed the region
    /// it was given straight back would get a different one in return. Read the
    /// applied rect back with [`Mirror::region`].
    pub fn bootstrap(
        region: Rect,
        fps: u32,
        show_cursor: bool,
        capture_method: CaptureMethod,
    ) -> Mirror {
        // 1. Context (log/crash handlers were installed first thing in main).
        let context = match ObsContext::new("en-US") {
            Ok(c) => c,
            Err(e) => fail(format_args!("Failed to initialize OBS: {e}")),
        };

        // 2. Paths first (see the ordering note above).
        let exe_dir = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|p| p.to_path_buf()))
            .unwrap_or_else(|| fail("Could not determine the executable directory"));
        let paths = obs_platform::default_obs_paths(&exe_dir);
        if let Some(ref libobs_data) = paths.libobs_data {
            context.add_data_path(libobs_data);
        }

        // 3. Resolve the region against the live monitors. Normalised first,
        //    so everything below — the canvas, the scene offsets, the region
        //    this reports back — is built from the rect that will actually be
        //    mirrored rather than the one that was asked for.
        let region = normalize_region(region);
        let monitors = obs_platform::enumerate_monitors();
        if monitors.is_empty() {
            fail("No displays found");
        }
        let plan = match region::plan_region(region, &monitors) {
            Ok(p) => p,
            Err(e) => fail_args(e),
        };

        // 4. Video. base == output == the region canvas: there is no encoder
        //    to feed, so downscaling would only blur the mirror.
        // Run libobs on the GPU driving the display the region mostly covers —
        // required by the DXGI duplicator, which only sees monitors attached to
        // the current device's adapter, and a free cross-adapter copy saved
        // under WGC.
        let adapter = match obs_platform::region_adapter_index(region, &plan, &monitors) {
            Some(0) | None => 0,
            Some(n) => {
                eprintln!(
                    "Using graphics adapter {n}: it drives the display the region mostly covers"
                );
                n
            }
        };
        if let Err(e) = context.reset_video(&video_info(plan.canvas, fps, adapter)) {
            fail(format_args!("Failed to reset OBS video: {e}"));
        }

        // 5. Audio. No audio source is ever attached, but obs_reset_audio is
        //    part of core init and cheap; skipping it is the untested path.
        if let Err(e) = context.reset_audio(&AudioInfo {
            samples_per_sec: 44100,
        }) {
            fail(format_args!("Failed to reset OBS audio: {e}"));
        }

        // 6. Modules + display-capture sanity check. NOT obs_source_create !=
        //    null: libobs creates a placeholder source for unknown ids;
        //    get_display_name returns null exactly when unregistered.
        context.add_module_path(&paths.module_bin, &paths.module_data);
        context.load_all_modules();
        let display_capture_c = CString::new(obs_platform::DISPLAY_CAPTURE_ID).unwrap();
        let display_name =
            unsafe { obs_sys::obs_source_get_display_name(display_capture_c.as_ptr()) };
        if display_name.is_null() {
            fail(format_args!(
                "Display capture source '{}' is not registered — the capture plugin failed to \
                 load.\n  module bin:  {}\n  module data: {}",
                obs_platform::DISPLAY_CAPTURE_ID,
                paths.module_bin,
                paths.module_data
            ));
        }

        // 7. Scene: one display-capture item per intersected monitor, offset
        //    onto the region canvas.
        let scene = match ObsScene::create("main") {
            Ok(s) => s,
            Err(e) => fail(format_args!("Failed to create scene: {e}")),
        };
        let (sources, items) =
            build_scene_items(&scene, &plan, &monitors, show_cursor, capture_method);
        context.set_output_source_raw(0, scene.get_source());

        let monitor_set = plan.items.iter().map(|i| i.monitor_index).collect();
        Mirror {
            context,
            scene,
            sources,
            items,
            display: None,
            monitors,
            monitor_set,
            region,
            canvas: plan.canvas,
            fps,
            show_cursor,
            capture_method,
            adapter,
        }
    }

    /// Binds the obs display swapchain to the mirror window and registers
    /// `obscure::draw` as its render callback. Called once, from
    /// `AppEvents::mirror_ready` — i.e. after the user pressed OK and the
    /// prompt window became the (off-screen) mirror surface.
    pub fn attach_display(&mut self, window: *mut c_void) {
        // Safety: the UI layer hands us the mirror window's HWND / contentView
        // NSView*, which lives until the process exits (the mirror window is
        // never destroyed; closing it exits via events.quit → exit_process).
        let display = match unsafe { ObsDisplay::new(window, self.canvas.0, self.canvas.1) } {
            Ok(d) => d,
            Err(e) => fail(format_args!("Failed to create the OBS display: {e}")),
        };
        // Black backing where the texture does not cover (transient, e.g.
        // mid-resize before the swapchain catches up).
        display.set_background_color(0);
        // Safety: `obscure::draw` runs on the graphics thread and touches only
        // its own atomics and its own graphics resources — never `Mirror` —
        // which is exactly why the param is null: the callback has no way to
        // reach us even by accident. The obscure module also owns the frame
        // counter that feeds the 1 Hz status line, so this must stay the only
        // draw callback on the display.
        unsafe { display.add_draw_callback(crate::obscure::draw, std::ptr::null_mut()) };
        self.display = Some(display);
    }

    /// The region currently being mirrored, i.e. the last rect this actually
    /// applied. Read once after `bootstrap` so the rest of the process starts
    /// from the normalised `--region` rather than the requested one.
    pub fn region(&self) -> Rect {
        self.region
    }

    /// Full reconcile, driven by the shell's `move X,Y,W,H` stdin command
    /// (there are no drags any more — Clowd owns the border and tells us where
    /// it ended up). The request is [`normalize_region`]d, and `Ok` carries the
    /// rect that was actually applied — which is what the caller acks back over
    /// stdout as `region_changed`, so the shell always learns the real region,
    /// not the requested one.
    ///
    /// `Err` means the request was refused and NOTHING changed: the previous
    /// region is still being mirrored. The reason is worded for
    /// `status::emit_command_error`, because a refusal has to be visible as a
    /// refusal — echoing the unchanged region back as `region_changed` would be
    /// indistinguishable on the wire from a successful move to that rect, and
    /// Clowd (which resizes its border to whatever `region_changed` reports)
    /// would see its border snap back with no explanation. This is reachable in
    /// normal operation, not just from a nonsense request: `self.monitors` is
    /// the snapshot taken at bootstrap, so after a display is unplugged or
    /// rearranged a `move` onto the new layout is validated against the old one.
    pub fn set_region(&mut self, region: Rect) -> Result<Rect, String> {
        let region = normalize_region(region);
        // Planned BEFORE anything is mutated, so a refusal is a clean no-op
        // rather than a half-applied move that has to be unwound.
        let plan = match region::plan_region(region, &self.monitors) {
            Ok(p) => p,
            Err(e) => return Err(format!("move rejected: {e}")),
        };

        // Canvas first: reset_video is safe HERE, unlike in obs-express —
        // there are no outputs and no obs_view mixes to destroy (see
        // crates/obs/src/view.rs's warning), and obs displays survive
        // reset_video (libobs rebuilds their swapchains).
        if plan.canvas != self.canvas {
            if let Err(e) =
                self.context
                    .reset_video(&video_info(plan.canvas, self.fps, self.adapter))
            {
                fail(format_args!("Failed to reset OBS video: {e}"));
            }
            if let Some(ref display) = self.display {
                display.resize(plan.canvas.0, plan.canvas.1);
            }
        }

        let new_set: Vec<usize> = plan.items.iter().map(|i| i.monitor_index).collect();
        if new_set != self.monitor_set {
            // Monitor set changed: rebuild sources/items. Removing the item
            // detaches it from the scene; dropping source + item releases our
            // refs (see crates/obs/src/scene.rs).
            for item in self.items.drain(..) {
                item.remove();
            }
            self.sources.clear();
            let (sources, items) = build_scene_items(
                &self.scene,
                &plan,
                &self.monitors,
                self.show_cursor,
                self.capture_method,
            );
            self.sources = sources;
            self.items = items;
        } else {
            // Same sources; only offsets (and, pedantically, scale) move.
            for (item, planned) in self.items.iter().zip(&plan.items) {
                item.set_pos(planned.pos.0, planned.pos.1);
                item.set_scale(planned.scale, planned.scale);
            }
        }

        self.region = region;
        self.canvas = plan.canvas;
        self.monitor_set = new_set;
        Ok(region)
    }
}

/// base == output == canvas: the mirror never downscales (no encoder to feed).
fn video_info(canvas: (u32, u32), fps: u32, adapter: u32) -> VideoInfo {
    VideoInfo {
        graphics_module: obs_platform::GRAPHICS_MODULE,
        base_width: canvas.0,
        base_height: canvas.1,
        output_width: canvas.0,
        output_height: canvas.1,
        fps_num: fps,
        fps_den: 1,
        adapter,
    }
}

/// One display-capture source + positioned/scaled scene item per planned
/// monitor (recorder.rs step 7 shape). Exits on source-creation failure.
fn build_scene_items(
    scene: &ObsScene,
    plan: &RegionPlan,
    monitors: &[MonitorInfo],
    show_cursor: bool,
    capture_method: CaptureMethod,
) -> (Vec<ObsSource>, Vec<ObsSceneItem>) {
    let mut sources = Vec::new();
    let mut items = Vec::new();
    for (i, planned) in plan.items.iter().enumerate() {
        let m = &monitors[planned.monitor_index];
        let source_settings =
            obs_platform::display_capture_settings(m, show_cursor, capture_method);
        let source = match ObsSource::create(
            obs_platform::DISPLAY_CAPTURE_ID,
            &format!("display_{i}"),
            Some(&source_settings),
        ) {
            Ok(s) => s,
            Err(e) => fail(format_args!(
                "Failed to create display capture for monitor '{}': {e}",
                m.id
            )),
        };
        let item = scene.add(&source);
        item.set_pos(planned.pos.0, planned.pos.1);
        item.set_scale(planned.scale, planned.scale);
        sources.push(source);
        items.push(item);
    }
    (sources, items)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rect(w: u32, h: u32) -> Rect {
        Rect {
            x: 10,
            y: -20,
            w,
            h,
        }
    }

    /// Every rect that leaves `normalize_region` must be one the mirror can
    /// actually present: at least `MIN_REGION` on both axes, and even, so that
    /// the canvas `plan_region` computes (which is masked even) is the same
    /// size as the window the platform layer builds from the region.
    #[test]
    fn normalize_region_is_a_fixed_point_of_the_canvas_math() {
        for w in [1u32, 2, 63, 64, 65, 800, 801, 1919, 3840] {
            for h in [1u32, 2, 63, 64, 65, 600, 601, 1081, 2160] {
                let n = normalize_region(rect(w, h));
                assert!(n.w >= MIN_REGION && n.h >= MIN_REGION, "{w}x{h} -> {n:?}");
                assert_eq!((n.w % 2, n.h % 2), (0, 0), "{w}x{h} -> {n:?}");
                // What `plan_region` would make of it at canvas_scale 1.0.
                assert_eq!(((n.w & !1).max(2), (n.h & !1).max(2)), (n.w, n.h));
                // Idempotent: re-sending an acked region must not move it again,
                // or Clowd's border would creep by a pixel per round trip.
                assert_eq!(normalize_region(n), n);
                // Position is never touched — only a display change can move
                // the region, and that is the shell's business, not ours.
                assert_eq!((n.x, n.y), (10, -20));
            }
        }
    }

    /// The floor is applied before the rounding, so it cannot be rounded away.
    #[test]
    fn normalize_region_floor_survives_the_even_rounding() {
        assert_eq!(normalize_region(rect(1, 1)).w, MIN_REGION);
        assert_eq!(normalize_region(rect(MIN_REGION + 1, 1)).w, MIN_REGION);
        assert_eq!(normalize_region(rect(2, 2)).h, MIN_REGION);
    }
}
