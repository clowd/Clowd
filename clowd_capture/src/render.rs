use std::collections::HashMap;
use std::sync::atomic::Ordering;
use std::sync::{mpsc, Arc};
use std::thread;
use std::time::{Duration, Instant};

use crate::gpu::desktop::{create_placeholder_cursor_texture, WindowUniforms, WINDOW_UNIFORMS_SIZE};
use crate::gpu::peek::{PeekUniforms, PEEK_UNIFORMS_SIZE};
use crate::gpu::{self};
use crate::gxi::{self, AcquireResult, BindingRes, GpuTimings, ShaderId, SurfaceConfig, TexFormat, TextureDesc};
use crate::interaction::OcrState;
use crate::sync::ReadyGuard;
use crate::telemetry::perf::{PerfSample, PerfTracker};
use crate::telemetry::startup::{AtomicDuration, StartupTimings, WorkerTimings};
use crate::ui::gpu::renderer::{UiPipelines, UiText};
use crate::ui::gpu::text::TextStack;
use crate::ui::gpu::UiRenderer;
use crate::ui::shared::{UiMonitor, UiSharedState};
use clowd_rust_core::geometry::{screen_to_window, RectExt, ScreenPointF, ScreenRect};

pub mod desktop;
pub mod frame;
pub mod peek;
pub mod protocol;
pub mod window;
pub mod worker;
use desktop::{FrameState, SnapshotState};
use frame::{draw_once, DrawStatus};
use peek::PeekTextureEntry;
use protocol::{CycleParams, PeekCommand, RenderMsg, WorkerInput};
use worker::RenderWorkerParams;

// ── Deferred (post-first-frame) GPU build ───────────────────────────

/// Everything a render worker needs that FRAME 0 DOES NOT.
///
/// Frame 0 draws one triangle with the desktop pipeline: `UiRenderer::draw`
/// is a no-op there (no `UiSharedState` exists until after the visible
/// latch, so `prepare` stages nothing) and the peek quad needs a hovered
/// window, which needs a visible overlay. So the whole UI stack — three
/// pipelines, the glyph atlas + text renderers, ~2.4 MB of embedded fonts
/// into fontdb, 11 usvg parses — plus the peek pipeline used to sit on the
/// critical path buying nothing: ~185 ms cold on macOS, 15-60 ms on every
/// Windows launch.
///
/// It is built on a side thread started the moment the device exists, so it
/// overlaps the screenshot wait, the window handoff and frame 0 itself. It is
/// NEVER blocked on: the render loop starts without it — desktop pass,
/// selection and the software cursor all live in the desktop pipeline — and
/// polls the handle each iteration, folding the stack in the moment it
/// lands. On a warm start that is before the overlay is even visible; on a
/// cold one (driver shader cache empty, the embedded fonts and DLLs not yet
/// paged in) the build can take a second or more, and an earlier revision
/// that joined it right after the show gate produced exactly the symptom
/// this split was meant to kill: overlay on screen, then frozen with no
/// cursor until the compile finished. Until it lands the user sees the
/// desktop, the cursor and the selection responding, with the panel, hints
/// and peek arriving when ready.
struct DeferredStack {
    peek: gpu::PeekGpu,
    ui: UiRenderer,
}

/// Start the deferred build. Call immediately after Stage A; poll it from
/// the render loop (never block on it before the first visible frames).
///
/// `Device` is a refcounted handle, so the builder gets its own clone and
/// the worker thread keeps using the original meanwhile.
#[allow(clippy::too_many_arguments)]
fn spawn_deferred_stack(
    device: gxi::Device,
    this_monitor: UiMonitor,
    monitor_index: usize,
    monitor_name: String,
    adapter_name: String,
    adapter_id: Option<(u32, u32)>,
    startup: Arc<StartupTimings>,
) -> thread::JoinHandle<DeferredStack> {
    thread::Builder::new()
        .name(format!("ui-build-{monitor_index}"))
        .spawn(move || {
            // Below normal, and so is every thread this build fans out to
            // (spawns do not inherit priority on Windows): on a cold start
            // this build is still compiling when the overlay shows, and it
            // must lose every contested core to the render loop — the UI
            // chrome arriving a beat later is invisible next to the frame
            // cadence stuttering.
            crate::system::lower_thread_priority();
            let mark = |field: fn(&WorkerTimings) -> &AtomicDuration| {
                if let Some(t) = startup.background.workers.get(monitor_index) {
                    field(t).set_once(startup.t_start.elapsed());
                }
            };

            // Three concurrent jobs, longest first on this thread: the
            // glyph/SVG job is the heavy one (font parse + atlas + ~30
            // shaped buffers + 11 SVG trees), so it runs here while the
            // pipeline compiles and the peek compile occupy their own
            // threads. `scope` lets all of them borrow the one device.
            let (peek, pipelines, text) = thread::scope(|s| {
                let peek = s.spawn(|| {
                    crate::system::lower_thread_priority();
                    gpu::create_peek_gpu(&device)
                });
                let pipelines = s.spawn(|| {
                    crate::system::lower_thread_priority();
                    UiPipelines::build_parallel(&device)
                });
                let text_stack = TextStack::new(&device);
                mark(|t| &t.prep_fonts);
                let text = UiText::new(text_stack);
                (
                    peek.join().expect("peek pipeline thread"),
                    pipelines.join().expect("ui pipeline thread"),
                    text,
                )
            });
            // Both marks now measure DEFERRED work running beside the
            // critical path, not work on it — they no longer sit between
            // `prep_pipelines` and `render_prep` in wall-clock order, and
            // because the two jobs run concurrently `prep_fonts` can land
            // before `prep_ui_pipelines`.
            mark(|t| &t.prep_ui_pipelines);

            DeferredStack {
                peek,
                ui: UiRenderer::from_parts(
                    pipelines,
                    text,
                    this_monitor,
                    monitor_index,
                    monitor_name,
                    adapter_name,
                    adapter_id,
                    startup,
                ),
            }
        })
        .expect("spawn ui builder")
}

// ── Frame 0 ─────────────────────────────────────────────────────────

/// Present frame 0: the desktop triangle and nothing else.
///
/// Deliberately not routed through [`draw_once`]. At this point the UI
/// stack and the peek pipeline are still compiling on the deferred thread,
/// so there is no `WindowGpu` to hand it — which is the point: the type
/// system, not a comment, is what stops frame 0 referencing a pipeline that
/// does not exist yet. Nothing is lost, because `draw_once`'s extra work is
/// all inert on frame 0 (`peek_bind_group` is `None`, `UiRenderer::prepare`
/// bails on the absent state, and there is no perf history to sample).
///
/// Returns whether the frame actually reached `queue.present()` — a
/// lost/outdated/timed-out surface returns early — so the caller's
/// `first_present` mark means "pixels handed to the compositor", not
/// "a draw was attempted". [`FirstFrame::DeviceLost`] is terminal: the
/// worker must exit with its fail guard still armed so the show gate
/// counts the display as failed instead of waiting on it.
fn present_first_frame(surface: &mut gxi::Surface, pipeline: &gxi::RenderPipeline, snapshot_state: Option<&SnapshotState>) -> FirstFrame {
    // macOS: the early order-front (`app.rs::order_window_front_early`) is
    // asynchronous — `orderFrontRegardless` returns before AppKit has heard
    // back from the window server, and the metal backend's `acquire`
    // returns `Occluded` until the NSWindow's occlusionState gains
    // `NSWindowOcclusionStateVisible` (the guard carried over from
    // wgpu-hal's gfx-rs/wgpu#8309 workaround). On
    // a warm run this worker reaches the acquire within a millisecond or
    // two of the order-front — and can even beat it, since the main thread
    // only orders front once its screenshot pickup poll fires — so the
    // FIRST attempt reliably fails; treating that as
    // terminal (as `draw_once` correctly does mid-loop) silently turned
    // frame 0 into a 0.01 ms no-op on every warm launch, and the show gate
    // then faded in a surface nothing had ever painted. So frame 0 — and
    // only frame 0 — waits the transition out. Bounded well under the 5 s
    // `SHOW_GATE_TIMEOUT`: a window that genuinely never becomes visible
    // (covered, wedged WindowServer) falls back to the old skip-and-continue
    // path with the render loop picking up the first paint. The common case
    // where the surface is already acquirable — every non-macOS platform,
    // and any macOS run slow enough that AppKit caught up — breaks out of
    // the loop on the first attempt and never sleeps.
    let occluded_deadline = Instant::now() + Duration::from_millis(500);
    let mut frame = loop {
        match surface.acquire(None) {
            AcquireResult::Frame(f) => break f,
            AcquireResult::Occluded if cfg!(target_os = "macos") && Instant::now() < occluded_deadline => {
                thread::sleep(Duration::from_millis(1));
            }
            // Like the other misses, the render loop picks up the first
            // paint.
            AcquireResult::Skip | AcquireResult::Occluded => return FirstFrame::Skipped,
            AcquireResult::DeviceLost => return FirstFrame::DeviceLost,
        }
    };

    frame.set_pipeline(pipeline);
    if let Some(state) = snapshot_state {
        frame.set_bind_group(0, &state.bind_group);
        frame.draw(0..3, 0..1);
    }
    frame.present(None);
    FirstFrame::Presented
}

/// What [`present_first_frame`] achieved.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum FirstFrame {
    /// Pixels were handed to the compositor.
    Presented,
    /// Transient miss — the render loop picks up the first paint.
    Skipped,
    /// The device is gone (see [`frame::DrawStatus::DeviceLost`]) — the
    /// worker must exit via its fail path. Dead on the metal backend.
    DeviceLost,
}

// ── Messages ────────────────────────────────────────────────────────

// ── WindowHandle (main thread side) ─────────────────────────────────

// Handle to a render thread, held by the main thread. Dropping it sends
// `Shutdown` and joins the thread.
// ── Worker spawn + lifecycle ────────────────────────────────────────

/// Per-worker parameters built in main() before the event loop starts.
fn render_worker_main(params: RenderWorkerParams, input_rx: mpsc::Receiver<WorkerInput>, msg_rx: mpsc::Receiver<RenderMsg>) {
    let RenderWorkerParams {
        monitor,
        monitor_index,
        instance,
        startup,
        failed_count,
    } = params;

    // This thread paints a monitor every vsync; it outranks everything
    // else in the process, including background work whose priority is
    // out of our hands (libblur's pool). See the helper's docs.
    crate::system::raise_render_thread_priority();

    // Armed for the worker's whole life: any exit that isn't a clean
    // shutdown bumps `failed_count`, so the app's show gate
    // (ready + failed >= expected) is never deadlocked by a dead worker.
    let mut fail_guard = ReadyGuard::new(failed_count);
    let monitor_bounds = monitor.bounds;
    let scale_factor = monitor.scale_factor;
    let refresh_hz = monitor.refresh_hz;
    let adapter_hint = monitor.adapter_id;
    let this_monitor = UiMonitor {
        bounds: monitor.bounds,
        dpi_scale: monitor.scale_factor,
        is_primary: monitor.is_primary,
    };
    let monitor_name = monitor.name.clone();

    // ── Stage A: eager GPU prep (no window/surface/screenshot) ──────

    let worker_timings = &startup.background.workers[monitor_index];
    let bundle = match gpu::stage_a_create_device(instance, adapter_hint, startup.t_start, worker_timings) {
        Ok(b) => b,
        Err(e) => {
            error!("render worker {monitor_index}: GPU init failed: {e:?}");
            return;
        }
    };
    let adapter_name = bundle.adapter_name.clone();

    // Started here, before the handoff wait, and collected by the render
    // loop whenever it finishes: that is the whole overlap this buys — the
    // UI stack and the peek pipeline compile *beside* the screenshot wait,
    // window creation, frame 0 and the first visible frames instead of in
    // front of any of them.
    let deferred = spawn_deferred_stack(
        bundle.device.clone(),
        this_monitor,
        monitor_index,
        monitor_name,
        adapter_name,
        adapter_hint,
        startup.clone(),
    );

    worker_timings
        .render_prep
        .set_once(startup.t_start.elapsed());

    // ── Handoff: wait for the window + surface from the main thread ─
    // A BeginCycle can legitimately arrive first (the screenshot job races
    // window creation), and in one-shot mode it usually does. Upload its
    // snapshot here rather than stashing it untouched: the upload needs only
    // the device, so doing it now runs it *against* the main thread's window
    // creation instead of after it. The snapshot rides along with the cycle so
    // the two can never be paired up wrongly.

    let mut pending_cycle: Option<(Arc<CycleParams>, Option<Arc<gpu::desktop::DesktopSnapshot>>)> = None;
    let handoff = loop {
        match input_rx.recv() {
            Ok(WorkerInput::Handoff(h)) => break *h,
            Ok(WorkerInput::BeginCycle(p)) => {
                let stamp = |field: fn(&WorkerTimings) -> &AtomicDuration| {
                    if let Some(t) = startup.background.workers.get(monitor_index) {
                        field(t).set_once(startup.t_start.elapsed());
                    }
                };
                stamp(|t| &t.upload_start);
                let snapshot = gpu::desktop::upload_snapshot(&bundle.device, &bundle.queue, &p.snapshot, &bundle.desktop_sampler);
                stamp(|t| &t.upload);
                pending_cycle = Some((p, snapshot));
            }
            Ok(WorkerInput::Shutdown) => {
                fail_guard.disarm();
                return;
            }
            Err(_) => {
                error!("render worker {monitor_index}: input channel closed before handoff");
                return;
            }
        }
    };
    let _window = handoff.window;
    let mut surface = handoff.surface;

    worker_timings
        .handoff
        .set_once(startup.t_start.elapsed());

    // ── Configure the surface ───────────────────────────────────────
    // Still working straight off the Stage-A bundle: `WindowGpu` cannot be
    // assembled until the deferred build lands its peek pipeline, and
    // nothing before frame 0 needs it. Swapchain policy (BGRA8 non-sRGB —
    // asserted against the adapter — fifo, opaque, latency 1) lives in the
    // gxi backend.

    let surface_size = ((monitor_bounds.width() as u32).max(1), (monitor_bounds.height() as u32).max(1));
    let config = SurfaceConfig {
        width: surface_size.0,
        height: surface_size.1,
        clear_color: [0.05, 0.05, 0.08, 1.0],
    };
    // Configure at monitor size right away, while the main thread is still
    // finishing its own window setup — this is the one swapchain create we
    // need, and doing it here keeps it off the critical path between the
    // screenshot landing and frame 0.
    surface.configure(&bundle.device, &bundle.queue, &config);

    // `preloaded_snapshot` is `Some` only for a cycle that arrived during
    // the handoff wait and was uploaded there. Otherwise wait for it: the
    // screenshot job sets the latch before broadcasting `BeginCycle`, so the
    // main thread can build the windows and deliver `Handoff` first.
    let (cycle, preloaded_snapshot) = match pending_cycle.take() {
        Some(pair) => pair,
        None => loop {
            match input_rx.recv() {
                Ok(WorkerInput::BeginCycle(p)) => break (p, None),
                Ok(WorkerInput::Handoff(_)) => {
                    warn!("render worker {monitor_index}: unexpected handoff ignored");
                }
                Ok(WorkerInput::Shutdown) => {
                    // WindowHandle::drop — clean shutdown. (Channel
                    // disconnection can't be relied on here: a still-
                    // running screenshot/blur job holds sender clones.)
                    fail_guard.disarm();
                    return;
                }
                Err(_) => {
                    // Main thread dropped its senders — clean shutdown.
                    fail_guard.disarm();
                    return;
                }
            }
        },
    };

    // `.get()` so a worker-count mismatch (impossible today — the monitor
    // list sizes both) degrades to missing debug rows instead of panicking
    // the render worker.
    let mark = |field: fn(&WorkerTimings) -> &AtomicDuration| {
        if let Some(t) = startup.background.workers.get(monitor_index) {
            field(t).set_once(startup.t_start.elapsed());
        }
    };

    // ── Stage B: upload this cycle's desktop snapshot ───────────
    // Done during the handoff wait when the BeginCycle beat the window,
    // which is the common case. `set_once` keeps the marks stamped there.

    mark(|t| &t.upload_start);
    let snapshot = preloaded_snapshot
        .or_else(|| gpu::desktop::upload_snapshot(&bundle.device, &bundle.queue, &cycle.snapshot, &bundle.desktop_sampler));
    mark(|t| &t.upload);

    // ── Stage C: per-cycle uniforms + bind group, draw frame 0 ──
    // Accent color and initial mouse come from CycleParams, not from
    // settings baked in at worker spawn.

    let mut snapshot_state: Option<SnapshotState> = snapshot.as_ref().map(|snap| {
        let mf = monitor_bounds.to_f32();
        let vd_x = snap.vdesktop_origin[0];
        let vd_y = snap.vdesktop_origin[1];
        let vd_w = snap.vdesktop_size[0];
        let vd_h = snap.vdesktop_size[1];
        let base_uv_offset_scale = [
            (mf.left() - vd_x) / vd_w,
            (mf.top() - vd_y) / vd_h,
            mf.width() / vd_w,
            mf.height() / vd_h,
        ];

        let init_local = screen_to_window(monitor_bounds, cycle.initial_mouse);

        let uniforms = WindowUniforms {
            uv_offset_scale: base_uv_offset_scale,
            params: [0.0, init_local.x, init_local.y, scale_factor],
            accent_color: cycle.accent_color,
            selection_rect: [0.0, 0.0, -1.0, -1.0],
            selection_params: [0.0, 0.0, 0.0, 0.0],
            cursor_rect: [0.0, 0.0, -1.0, -1.0],
            cursor_params: [0.0, 0.0, 0.0, 0.0],
            ocr_rect: [0.0, 0.0, -1.0, -1.0],
            ocr_params: [0.0, 0.0, 0.0, 0.0],
            selection_shape: [0.0, 0.0, 0.0, 0.0],
        };

        let ubo = bundle
            .device
            .create_uniform_buffer("window uniforms", WINDOW_UNIFORMS_SIZE);
        bundle
            .queue
            .write_buffer(&ubo, 0, bytemuck::bytes_of(&uniforms));

        let placeholder_cursor = create_placeholder_cursor_texture(&bundle.device, &bundle.queue);
        let (cursor_color, cursor_mask) = match &snap.cursor {
            Some(ct) => (&ct.color, &ct.mask),
            None => (&placeholder_cursor, &placeholder_cursor),
        };

        let bind_group = bundle.device.create_bind_group(
            "window snapshot bind group",
            ShaderId::Desktop,
            &[
                BindingRes::Uniform(&ubo),
                BindingRes::Texture(&snap.texture),
                BindingRes::Sampler(&snap.sampler),
                BindingRes::Texture(cursor_color),
                BindingRes::Texture(cursor_mask),
            ],
        );

        SnapshotState {
            ubo,
            bind_group,
            uniforms,
            base_uv_offset_scale,
        }
    });

    mark(|t| &t.first_render_start);
    let presented = present_first_frame(&mut surface, &bundle.desktop_pipeline, snapshot_state.as_ref());
    if presented == FirstFrame::DeviceLost {
        // Exit with `fail_guard` still armed: the bump lets the show gate
        // count this display as failed instead of waiting on it.
        error!("render worker {monitor_index}: GPU device lost at frame 0");
        return;
    }
    // Marks pixels actually handed to the compositor rather than merely a
    // draw attempt — unlike `first_render` below, which also waits out the
    // device poll.
    if presented == FirstFrame::Presented {
        mark(|t| &t.first_present);
    }
    bundle
        .device
        .wait_idle(Duration::from_secs(5));

    mark(|t| &t.first_render);

    // Reported ready BEFORE joining the deferred build: the show gate must
    // never wait on work whose output the user cannot see yet.
    //
    // Disarmed first so this worker contributes to the gate EXACTLY ONCE.
    // The gate opens on `ready + failed >= expected`, and everything below
    // this point is fallible (the deferred join can carry a panic). Leaving
    // the guard armed across the bump would let one worker count on both
    // sides and open the gate while a sibling is still creating its device —
    // inverting the guard's purpose from "a dead worker cannot hold the
    // overlay hostage" into "a dying worker shows the overlay early".
    fail_guard.disarm();
    cycle
        .ready_count
        .fetch_add(1, Ordering::Release);

    // ── Assemble the render-loop state ──────────────────────────
    // Deliberately WITHOUT the deferred build. It is collected inside the
    // loop (see `DeferredStack`): blocking on it here — between the show
    // gate opening and the first loop iteration — is what made a cold start
    // feel worse than the old blocking layout. The overlay was on screen
    // (frame 0, hardware cursor hidden) while this thread sat in `join()`
    // for however long a cold shader compile + font/SVG parse took, so
    // nothing followed the mouse and nothing drew. Warm the build is long
    // done by now and the first iteration picks it up immediately.
    let mut gpu = gpu::finalize_window_gpu(bundle, snapshot);
    let mut deferred: Option<thread::JoinHandle<DeferredStack>> = Some(deferred);
    let mut ui_renderer: Option<UiRenderer> = None;
    // The latest UiSharedState that arrived while the UI stack was still
    // building. Only the newest matters — every broadcast is a full state
    // (`set_state` replaces, it does not merge) — and it is applied the
    // moment the stack lands so the first UI frame is already current.
    let mut pending_ui_state: Option<Arc<UiSharedState>> = None;

    let mut perf = PerfTracker::new_with_refresh(refresh_hz);
    let mut gpu_timing = GpuTimings::new(&gpu.device, &gpu.queue);
    let peek_ubo = gpu
        .device
        .create_uniform_buffer("peek uniforms", PEEK_UNIFORMS_SIZE);

    cycle.visible_latch.wait();

    // ── Per-cycle peek state ────────────────────────────────────

    let mut peek_textures: HashMap<usize, PeekTextureEntry> = HashMap::new();
    let mut active_peek: Option<PeekCommand> = None;
    let mut blurred_desktop: Option<gxi::Texture> = None;

    // ── Render loop ─────────────────────────────────────────────

    let start = Instant::now();
    let mut mouse_pos: ScreenPointF = cycle.initial_mouse;
    let mut zoom: f32 = 1.0;
    let mut selection: Option<ScreenRect> = None;
    let mut selection_radius: f32 = 0.0;
    let mut selection_dragging: bool = false;
    let mut captured: bool = false;
    let mut overlays_visible: bool = true;
    let mut cursor_overlay_visible: bool = true;
    let mut scroll_pick_mode: bool = false;
    // Mirrored whole (not decomposed into flags) so the dim, the handle
    // suppression and the lift geometry all derive from the same
    // broadcast — the same reason UiSharedState carries the enum.
    let mut ocr: OcrState = OcrState::Idle;
    let mut last_iter = Instant::now();

    loop {
        // Fold the deferred build in the iteration it finishes. `is_finished`
        // is a non-blocking flag read; the join that follows is then
        // immediate. Degraded rather than `.expect` on a panicked builder:
        // readiness is already published, so unwinding here would run
        // `ReadyGuard::drop` and double-count — and the desktop-only loop is
        // still a working (if chrome-less) overlay for this monitor.
        if deferred
            .as_ref()
            .is_some_and(|h| h.is_finished())
        {
            match deferred.take().map(|h| h.join()) {
                Some(Ok(DeferredStack {
                    peek,
                    mut ui,
                })) => {
                    info!("render worker {monitor_index}: deferred UI stack folded in");
                    // No previous cycle's UI may composite into the first UI
                    // frames, and the animation clock (border trail) starts
                    // from the moment the chrome appears rather than from
                    // build time. The state that arrived meanwhile goes in
                    // AFTER the reset, which would otherwise clear it.
                    ui.begin_cycle();
                    if let Some(state) = pending_ui_state.take() {
                        ui.set_state(state);
                    }
                    gpu.peek = Some(peek);
                    ui_renderer = Some(ui);
                }
                Some(Err(_)) => {
                    error!("render worker {monitor_index}: UI builder thread panicked; this monitor keeps a desktop-only overlay (no panel, hints or peek)");
                }
                None => {}
            }
        }

        loop {
            match msg_rx.try_recv() {
                Ok(RenderMsg::MouseState {
                    pos,
                    zoom: z,
                    selection: sel,
                    selection_radius: radius,
                    selection_dragging: dragging,
                    captured: cap,
                }) => {
                    mouse_pos = pos;
                    zoom = z;
                    selection = sel;
                    selection_radius = radius;
                    selection_dragging = dragging;
                    captured = cap;
                }
                Ok(RenderMsg::UiState(state)) => {
                    overlays_visible = state.overlays_visible;
                    cursor_overlay_visible = state.cursor_overlay_visible;
                    scroll_pick_mode = state.scroll_pick_mode;
                    ocr = state.ocr.clone();
                    match ui_renderer.as_mut() {
                        Some(ui) => ui.set_state(state),
                        None => pending_ui_state = Some(state),
                    }
                }
                Ok(RenderMsg::BlurredDesktop(bd)) => {
                    let max_dim = gpu.device.max_texture_dimension_2d();
                    if bd.width > max_dim || bd.height > max_dim || bd.width == 0 || bd.height == 0 {
                        continue;
                    }
                    // Fallible: this runs mid-loop with the fail guard
                    // long disarmed, and the texture is an optional
                    // cosmetic — an OOM on a huge desktop must be a
                    // logged skip, not a dead render worker.
                    let texture = gpu.device.try_create_texture_with_data(
                        &gpu.queue,
                        &TextureDesc {
                            label: "blurred desktop texture",
                            width: bd.width,
                            height: bd.height,
                            format: TexFormat::Bgra8Unorm,
                        },
                        &bd.bgra,
                    );
                    match texture {
                        Ok(t) => blurred_desktop = Some(t),
                        Err(e) => error!("blurred desktop texture creation failed (skipping): {e:#}"),
                    }
                }
                Ok(RenderMsg::PeekImage(peek)) => {
                    let max_dim = gpu.device.max_texture_dimension_2d();
                    if peek.width > max_dim || peek.height > max_dim || peek.width == 0 || peek.height == 0 {
                        continue;
                    }
                    // Fallible for the same reason as BlurredDesktop above.
                    let texture = match gpu.device.try_create_texture_with_data(
                        &gpu.queue,
                        &TextureDesc {
                            label: "peek window texture",
                            width: peek.width,
                            height: peek.height,
                            format: TexFormat::Bgra8Unorm,
                        },
                        &peek.bgra,
                    ) {
                        Ok(t) => t,
                        Err(e) => {
                            error!("peek window texture creation failed (skipping): {e:#}");
                            continue;
                        }
                    };
                    peek_textures.insert(
                        peek.window_index,
                        PeekTextureEntry {
                            texture,
                            window_rect: peek.window_rect,
                            obstruction_rects: peek.obstruction_rects.clone(),
                            width: peek.width,
                            height: peek.height,
                            crop_x: peek.crop_x,
                            crop_y: peek.crop_y,
                        },
                    );
                }
                Ok(RenderMsg::ShowPeek(cmd)) => {
                    active_peek = cmd;
                }
                Ok(RenderMsg::Shutdown) | Err(mpsc::TryRecvError::Disconnected) => {
                    fail_guard.disarm();
                    return;
                }
                Err(mpsc::TryRecvError::Empty) => break,
            }
        }

        // Evaluated every frame (not only on UiState receipt): the
        // dim/desaturation are animations on the phase's shared
        // anchor clock, and frames keep flowing between broadcasts.
        // Hoisted above the snapshot block because the PEEK uniforms
        // below need the same values — the peek quad covers the
        // desktop pass inside the selection, so it re-applies the
        // identical treatment (see peek.wgsl).
        let ocr_fx = desktop::ocr_overlay(&ocr);

        if let Some(state) = snapshot_state.as_mut() {
            let cursor_textures = gpu
                .snapshot
                .as_ref()
                .and_then(|s| s.cursor.as_ref());
            state.update_uniforms(
                &gpu.queue,
                &FrameState {
                    monitor_bounds,
                    mouse_pos,
                    zoom,
                    selection,
                    selection_radius,
                    selection_dragging,
                    captured,
                    overlays_visible,
                    cursor_overlay_visible,
                    scroll_pick_mode,
                    ocr_rect: ocr_fx.rect,
                    ocr_dim: ocr_fx.dim,
                    ocr_gray: ocr_fx.gray,
                    ocr_active: ocr_fx.active,
                    elapsed: start.elapsed().as_secs_f32(),
                    surface_size,
                },
                cursor_textures,
            );
        }

        // Build per-frame peek draw state if active + texture available.
        let peek_bind_group = active_peek.as_ref().and_then(|cmd| {
            // No peek pipeline yet (deferred build still running): skip the
            // quad this frame; it appears once the build lands.
            let _peek = gpu.peek.as_ref()?;
            let pt = peek_textures.get(&cmd.window_index)?;
            let snap = gpu.snapshot.as_ref()?;
            let blurred_tex = blurred_desktop.as_ref()?;

            // Compute peek uniforms in monitor-local coords.
            let sel = selection?;
            let cx = mouse_pos.x;
            let cy = mouse_pos.y;
            let local_cursor = screen_to_window(monitor_bounds, ScreenPointF::new(cx, cy));
            let mon_f = monitor_bounds.to_f32();

            let to_local = |vd_x: f32, vd_y: f32| -> (f32, f32) {
                if zoom <= 1.0 {
                    (vd_x - mon_f.left(), vd_y - mon_f.top())
                } else {
                    ((vd_x - cx) * zoom + local_cursor.x, (vd_y - cy) * zoom + local_cursor.y)
                }
            };

            let sel_f = sel.to_f32();
            let (sl, st) = to_local(sel_f.left(), sel_f.top());
            let (sr, sb) = to_local(sel_f.right(), sel_f.bottom());

            let wr = pt.window_rect.to_f32();

            // Window texture UV: map selection area to portion of window texture.
            // Use un-zoomed virtual-desktop coordinates so the UV range stays
            // within [0,1] regardless of zoom level.
            let tw = pt.width as f32;
            let th = pt.height as f32;
            let crop_x = pt.crop_x as f32;
            let crop_y = pt.crop_y as f32;
            let raw_sl = sel_f.left() - wr.left();
            let raw_st = sel_f.top() - wr.top();
            let raw_sw = sel_f.width();
            let raw_sh = sel_f.height();
            let window_uv = [(crop_x + raw_sl) / tw, (crop_y + raw_st) / th, raw_sw / tw, raw_sh / th];

            let desktop_uv = snapshot_state
                .as_ref()
                .map(|s| s.uniforms.uv_offset_scale)
                .unwrap_or([0.0; 4]);

            let mut peek_uniforms = PeekUniforms::zeroed();
            peek_uniforms.selection_rect = [sl, st, sr, sb];
            // Same window-local scaling as the desktop pass's
            // selection_shape: the radius rides the magnifier zoom with
            // the rect it belongs to.
            peek_uniforms.selection_shape = [selection_radius * zoom.max(1.0), 0.0, 0.0, 0.0];
            peek_uniforms.window_uv = window_uv;
            peek_uniforms.desktop_uv = desktop_uv;

            let n = pt.obstruction_rects.len().min(16);
            peek_uniforms.params = [
                n as f32,
                if cmd.captured { 0.0 } else { 0.45 },
                surface_size.0 as f32,
                surface_size.1 as f32,
            ];
            peek_uniforms.cursor_params = [local_cursor.x, local_cursor.y, scale_factor, 0.0];
            // Same values the desktop pass got this frame — the two
            // shaders must dim/desaturate in lockstep or the peeked
            // region visibly detaches from the rest of the selection.
            peek_uniforms.ocr_params = [ocr_fx.dim, ocr_fx.gray, if ocr_fx.active { 1.0 } else { 0.0 }, 0.0];
            for (i, r) in pt
                .obstruction_rects
                .iter()
                .take(16)
                .enumerate()
            {
                let rf = r.to_f32();
                let (rl, rt) = to_local(rf.left(), rf.top());
                let (rr, rb) = to_local(rf.right(), rf.bottom());
                peek_uniforms.obstruction_rects[i] = [rl, rt, rr, rb];
            }

            gpu.queue
                .write_buffer(&peek_ubo, 0, bytemuck::bytes_of(&peek_uniforms));

            let bind_group = gpu.device.create_bind_group(
                "peek bind group",
                ShaderId::Peek,
                &[
                    BindingRes::Uniform(&peek_ubo),
                    BindingRes::Texture(&pt.texture),
                    BindingRes::Texture(blurred_tex),
                    BindingRes::Sampler(&snap.sampler),
                ],
            );
            Some(bind_group)
        });

        if let Some(gt) = gpu_timing.as_mut() {
            for gpu_dur in gt.poll_completed(&gpu.device) {
                perf.backfill_next_gpu(gpu_dur);
            }
        }

        let now = Instant::now();
        let overall = now.duration_since(last_iter);
        last_iter = now;
        // The cold-start freeze this loop must never reproduce is a
        // multi-hundred-ms gap in the seconds right after show. The debug
        // overlay only shows live sessions; this leaves the evidence in
        // the log where a report can carry it.
        if overall > Duration::from_millis(250) && startup.t_start.elapsed() < Duration::from_secs(12) {
            warn!("render worker {monitor_index}: {overall:?} inter-frame gap during startup window");
        }

        let mut sample: Option<PerfSample> = None;
        let status = draw_once(
            &mut surface,
            &gpu,
            surface_size,
            snapshot_state.as_ref(),
            peek_bind_group.as_ref(),
            ui_renderer.as_mut(),
            &perf,
            gpu_timing.as_ref(),
            &mut sample,
        );
        if status == DrawStatus::DeviceLost {
            // Terminal: a dead device fails every acquire instantly, so
            // looping would hot-spin. The show gate has already been
            // satisfied by this worker (`fail_guard` was disarmed before
            // the loop), so exiting is the whole fail path here.
            error!("render worker {monitor_index}: GPU device lost; exiting render loop");
            return;
        }
        if let Some(mut s) = sample {
            s.overall = overall;
            perf.record(s);
        }
    }
}

// ── Peek texture storage ────────────────────────────────────────────

// ── Per-window snapshot state (unchanged) ───────────────────────────

// ── draw_once (unchanged) ───────────────────────────────────────────
