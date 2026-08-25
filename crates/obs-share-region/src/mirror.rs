//! The obs half of the binary: scene + display-capture sources + the
//! `obs_display` that paints the composited canvas into the mirror window.
//!
//! Bootstrap mirrors `Recorder::new` steps 1–7 in obs-express (same ordering
//! constraints, same fail/exit discipline) minus everything recorder-shaped:
//! no audio devices, no encoders, no output, no webcam, no tracker. With no
//! output the pipeline is just capture → compose → swapchain present, which is
//! what makes the live mirror nearly free (SHARE_REGION_PLAN §1).

use std::ffi::{c_void, CString};
use std::fmt::Display;

use obs::audio::AudioInfo;
use obs::context::ObsContext;
use obs::display::ObsDisplay;
use obs::scene::{ObsScene, ObsSceneItem};
use obs::source::ObsSource;
use obs::video::VideoInfo;
use obs_platform::region::{self, Rect, RegionPlan};
use obs_platform::MonitorInfo;

use crate::geometry;

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

/// The `obs_display` draw callback. Runs on **OBS's graphics thread**, not the
/// UI thread — it must do nothing but render the main texture (mixing in any
/// app logic here would race the UI thread over `Mirror`).
unsafe extern "C" fn draw_mirror(_param: *mut c_void, _cx: u32, _cy: u32) {
    unsafe { obs_sys::obs_render_main_texture() };
}

pub struct Mirror {
    context: ObsContext,
    scene: ObsScene,
    /// One display-capture source + scene item per planned (intersected)
    /// monitor, in `plan.items` order. Rebuilt only when the intersected
    /// monitor set changes (commit path).
    sources: Vec<ObsSource>,
    items: Vec<ObsSceneItem>,
    /// Created lazily by `attach_display` once the UI hands over the mirror
    /// window; `None` only between bootstrap and mirror_ready.
    display: Option<ObsDisplay>,
    /// Monitors as enumerated at bootstrap. Not re-enumerated on display
    /// config changes — the region math plans against this snapshot.
    monitors: Vec<MonitorInfo>,
    /// Indices into `monitors` of the currently planned items (the "monitor
    /// set" the cheap move path compares against).
    monitor_set: Vec<usize>,
    region: Rect,
    /// Canvas in capture px (== ObsDisplay size, == reset_video base/output).
    canvas: (u32, u32),
    canvas_scale: f64,
    fps: u32,
    show_cursor: bool,
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
    pub fn bootstrap(region: Rect, fps: u32, show_cursor: bool) -> Mirror {
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

        // 3. Resolve the region against the live monitors.
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
        if let Err(e) = context.reset_video(&video_info(plan.canvas, fps)) {
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
        let (sources, items) = build_scene_items(&scene, &plan, &monitors, show_cursor);
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
            canvas_scale: plan.canvas_scale,
            fps,
            show_cursor,
        }
    }

    /// Binds the obs display swapchain to the mirror window and registers the
    /// render callback. Called once, from `AppEvents::mirror_ready`.
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
        // Safety: draw_mirror only calls obs_render_main_texture (graphics
        // thread; see its doc comment), and the null param is never read.
        unsafe { display.add_draw_callback(draw_mirror, std::ptr::null_mut()) };
        self.display = Some(display);
    }

    /// Cheap live path, called repeatedly during a move drag: the canvas size
    /// and the source set are unchanged, so only the scene-item offsets move —
    /// no reset_video, no source churn. If the drag has changed anything
    /// structural (crossed onto/off a monitor, or off every monitor), do
    /// nothing: the commit on release reconciles, and the last cheap update
    /// simply lags a few frames behind the frame window.
    pub fn move_region(&mut self, region: Rect) {
        let plan = match region::plan_region(region, &self.monitors) {
            Ok(p) => p,
            Err(_) => return, // intersects nothing right now — commit decides
        };
        let same_set = plan.items.len() == self.monitor_set.len()
            && plan
                .items
                .iter()
                .zip(&self.monitor_set)
                .all(|(i, &m)| i.monitor_index == m);
        // canvas/canvas_scale can only differ if the set differs (same region
        // size, same monitors ⇒ same scale ⇒ same canvas), but check anyway —
        // this is the guard that keeps the cheap path cheap.
        if !same_set || plan.canvas != self.canvas || plan.canvas_scale != self.canvas_scale {
            return;
        }
        for (item, planned) in self.items.iter().zip(&plan.items) {
            item.set_pos(planned.pos.0, planned.pos.1);
        }
        self.region = region;
    }

    /// Full reconcile on drag release. Clamps to `geometry::MIN_REGION`; a
    /// region that intersects no monitor keeps the previous region instead
    /// (the UI adopts the returned rect, so the frame snaps back). Returns the
    /// region actually applied.
    pub fn commit_region(&mut self, region: Rect) -> Rect {
        let mut region = Rect {
            w: region.w.max(geometry::MIN_REGION),
            h: region.h.max(geometry::MIN_REGION),
            ..region
        };
        let plan = match region::plan_region(region, &self.monitors) {
            Ok(p) => p,
            Err(_) => {
                // Revert to the previous region, which held ≥1 monitor by
                // invariant (bootstrap validated it; every commit re-validates).
                // Re-plan it rather than trusting stale item state: a rejected
                // drag may still have run the cheap move path on the way out.
                region = self.region;
                match region::plan_region(region, &self.monitors) {
                    Ok(p) => p,
                    Err(e) => fail(format_args!("Region invariant broken: {e}")),
                }
            }
        };

        // Canvas first: reset_video is safe HERE, unlike in obs-express —
        // there are no outputs and no obs_view mixes to destroy (see
        // crates/obs/src/view.rs's warning), and obs displays survive
        // reset_video (libobs rebuilds their swapchains).
        if plan.canvas != self.canvas {
            if let Err(e) = self.context.reset_video(&video_info(plan.canvas, self.fps)) {
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
            let (sources, items) =
                build_scene_items(&self.scene, &plan, &self.monitors, self.show_cursor);
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
        self.canvas_scale = plan.canvas_scale;
        self.monitor_set = new_set;
        region
    }
}

/// base == output == canvas: the mirror never downscales (no encoder to feed).
fn video_info(canvas: (u32, u32), fps: u32) -> VideoInfo {
    VideoInfo {
        graphics_module: obs_platform::GRAPHICS_MODULE,
        base_width: canvas.0,
        base_height: canvas.1,
        output_width: canvas.0,
        output_height: canvas.1,
        fps_num: fps,
        fps_den: 1,
    }
}

/// One display-capture source + positioned/scaled scene item per planned
/// monitor (recorder.rs step 7 shape). Exits on source-creation failure.
fn build_scene_items(
    scene: &ObsScene,
    plan: &RegionPlan,
    monitors: &[MonitorInfo],
    show_cursor: bool,
) -> (Vec<ObsSource>, Vec<ObsSceneItem>) {
    let mut sources = Vec::new();
    let mut items = Vec::new();
    for (i, planned) in plan.items.iter().enumerate() {
        let m = &monitors[planned.monitor_index];
        let source_settings = obs_platform::display_capture_settings(m, show_cursor);
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
