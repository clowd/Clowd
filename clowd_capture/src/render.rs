use std::collections::HashMap;
use std::sync::atomic::Ordering;
use std::sync::{mpsc, Arc};
use std::time::{Duration, Instant};

use crate::gpu::desktop::{create_placeholder_cursor_view, WindowUniforms, WINDOW_UNIFORMS_SIZE};
use crate::gpu::peek::{PeekUniforms, PEEK_UNIFORMS_SIZE};
use crate::gpu::{self, SURFACE_FORMAT};
use crate::interaction::OcrState;
use crate::sync::ReadyGuard;
use crate::telemetry::perf::{PerfSample, PerfTracker};
use crate::ui::gpu::gpu_timing::GpuTimings;
use crate::ui::gpu::UiRenderer;
use crate::ui::shared::UiMonitor;
use clowd_rust_core::geometry::{screen_to_window, RectExt, ScreenPointF, ScreenRect};

pub mod desktop;
pub mod frame;
pub mod peek;
pub mod protocol;
pub mod window;
pub mod worker;
use desktop::{FrameState, SnapshotState};
use frame::{draw_once, DrawOutcome};
use peek::PeekTextureEntry;
use protocol::{CycleParams, PeekCommand, RenderMsg, WorkerInput};
use worker::RenderWorkerParams;

/// Duration of the colour → grayscale fade after the window first becomes
/// visible.
/// MSAA sample count applied to every render pipeline in the frame.
/// Set to 1 (no multisampling) — all UI geometry is axis-aligned
/// (rects, textured quads, glyph quads) so MSAA adds cost without
/// visual benefit.
pub const MSAA_SAMPLES: u32 = 1;

// ── Messages ────────────────────────────────────────────────────────

// ── WindowHandle (main thread side) ─────────────────────────────────

// Handle to a render thread, held by the main thread. Dropping it sends
// `Shutdown` and joins the thread.
// ── Worker spawn + lifecycle ────────────────────────────────────────

/// Return a worker to the parked state: release everything the finished
/// cycle allocated, then shrink the surface back to 1×1.
///
/// Centralised because there is more than one way back to parking
/// (`EndCycle`, and a cycle cancelled after the visible latch) and both the
/// COMPLETENESS and the ORDER of the release matter. A path that skips a
/// step keeps tens of MB of VRAM per monitor alive for the entire idle gap
/// in persistent mode — precisely what the 1×1 parked surface exists to
/// avoid. Route any future park path through here.
///
/// Order is deliberate:
/// 1. `UiRenderer::end_cycle` FIRST. The renderer outlives the cycle loop,
///    and its lift pass caches a bind group holding a `TextureView` of the
///    virtual-desktop snapshot. Dropping `gpu.snapshot` while that bind
///    group lives releases one of two references and frees nothing.
/// 2. Drop the snapshot `Arc`, now the last reference.
/// 3. Reconfigure at 1×1 to release the swapchain backbuffers.
/// 4. A non-blocking poll: wgpu reclaims a dropped resource during device
///    maintenance, and a parked worker submits nothing until the next
///    `BeginCycle` — without this the texture would sit on the
///    to-be-destroyed list for the whole gap, i.e. still allocated.
fn park_worker(
    surface: &wgpu::Surface<'static>,
    gpu: &mut gpu::WindowGpu,
    parked_config: &wgpu::SurfaceConfiguration,
    ui_renderer: &mut UiRenderer,
) {
    ui_renderer.end_cycle();
    gpu.snapshot = None;
    surface.configure(&gpu.device, parked_config);
    let _ = gpu.device.poll(wgpu::PollType::Poll);
}

/// Per-worker parameters built in main() before the event loop starts.
fn render_worker_main(params: RenderWorkerParams, input_rx: mpsc::Receiver<WorkerInput>, msg_rx: mpsc::Receiver<RenderMsg>) {
    let RenderWorkerParams {
        monitor,
        monitor_index,
        instance,
        warmup,
        memory_hints,
        failed_count,
        parked_count,
        gpu_lost_proxy,
    } = params;

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

    let worker_timings = &warmup.workers[monitor_index];
    let bundle = match gpu::stage_a_create_device(instance, adapter_hint, memory_hints, warmup.t_start, worker_timings) {
        Ok(b) => b,
        Err(e) => {
            error!("render worker {monitor_index}: GPU init failed: {e:?}");
            return;
        }
    };
    let adapter_name = bundle.adapter_name.clone();

    // Persistent mode: a device lost while parked would otherwise go
    // unnoticed until the next `show` failed on a dead device — signal
    // the main thread, which emits `display_changed` and exits with
    // EXIT_GPU_LOST so the shell respawns a fresh host. Complements (does
    // not replace) the uncaptured-error handler installed at device
    // creation.
    if let Some(proxy) = gpu_lost_proxy {
        bundle
            .device
            .set_device_lost_callback(move |reason, message| {
                // Destroyed = we tore the device down ourselves (shutdown);
                // only an unexpected loss should restart the host.
                if matches!(reason, wgpu::DeviceLostReason::Unknown) {
                    error!("render worker {monitor_index}: GPU device lost: {message}");
                    let _ = proxy.send_event(crate::host::AppEvent::GpuLost);
                }
            });
    }

    let mut ui_renderer = UiRenderer::new(
        &bundle.device,
        &bundle.queue,
        SURFACE_FORMAT,
        this_monitor,
        monitor_index,
        monitor_name,
        adapter_name,
        warmup.clone(),
    );

    worker_timings
        .render_prep
        .set_once(warmup.t_start.elapsed());

    // ── Handoff: wait for the window + surface from the main thread ─
    // A BeginCycle can legitimately arrive first (the screenshot job races
    // window creation) — stash it for the cycle loop below.

    let mut pending_cycle: Option<Arc<CycleParams>> = None;
    let handoff = loop {
        match input_rx.recv() {
            Ok(WorkerInput::Handoff(h)) => break h,
            Ok(WorkerInput::BeginCycle(p)) => pending_cycle = Some(p),
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
    let surface = handoff.surface;

    worker_timings
        .handoff
        .set_once(warmup.t_start.elapsed());

    // Verify surface format.
    let caps = surface.get_capabilities(&bundle.adapter);
    let actual_format = caps
        .formats
        .iter()
        .copied()
        .find(|f| !f.is_srgb())
        .unwrap_or(caps.formats[0]);
    assert_eq!(actual_format, SURFACE_FORMAT, "surface format mismatch on monitor {monitor_index}");

    // ── Assemble persistent GPU state, configure the surface once ────
    // Pipelines/layouts/sampler live for the worker's lifetime; only the
    // desktop snapshot (and blur/peek textures) are per capture cycle.

    let mut gpu = gpu::finalise_window_gpu(bundle);

    let config = wgpu::SurfaceConfiguration {
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
        format: SURFACE_FORMAT,
        width: (monitor_bounds.width() as u32).max(1),
        height: (monitor_bounds.height() as u32).max(1),
        present_mode: wgpu::PresentMode::Fifo,
        alpha_mode: wgpu::CompositeAlphaMode::Opaque,
        // Auto reproduces wgpu's pre-30 behaviour for our non-HDR surface format.
        color_space: wgpu::SurfaceColorSpace::Auto,
        view_formats: vec![],
        desired_maximum_frame_latency: 1,
    };
    // A hidden (parked) window must not pin full-screen backbuffers — on a
    // 4K monitor that is tens of MB of VRAM per display doing nothing. The
    // surface sits at 1×1 between cycles and is configured to monitor size
    // for the duration of each cycle.
    let parked_config = wgpu::SurfaceConfiguration {
        width: 1,
        height: 1,
        ..config.clone()
    };
    surface.configure(&gpu.device, &parked_config);

    let mut perf = PerfTracker::new_with_refresh(refresh_hz);
    let mut gpu_timing = GpuTimings::new(&gpu.device, &gpu.queue);

    let peek_ubo = gpu
        .device
        .create_buffer(&wgpu::BufferDescriptor {
            label: Some("peek uniforms"),
            size: PEEK_UNIFORMS_SIZE,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

    // ── Cycle loop: park → BeginCycle → render until EndCycle ───────

    // Fully warm: everything from here on is per-cycle work.
    parked_count.fetch_add(1, Ordering::Release);

    'cycles: loop {
        let cycle = match pending_cycle.take() {
            Some(p) => p,
            None => loop {
                // Parked between cycles: a blocking recv keeps idle CPU at ~0.
                match input_rx.recv() {
                    Ok(WorkerInput::BeginCycle(p)) => break p,
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

        // The cycle may have finished already (cancel / screenshot timeout
        // before the capture landed) — its windows are hidden and the app
        // has dropped it, so render nothing and go straight back to
        // parking. Its stale RenderMsgs are discarded by the cycle_gen
        // checks below.
        if cycle.cancelled.load(Ordering::Acquire) {
            info!("render worker {monitor_index}: discarding BeginCycle for an already-finished cycle");
            // Already parked (no surface to shrink, no snapshot uploaded
            // yet this iteration), so no full `park_worker` — but still
            // assert the invariant that a parked worker's UiRenderer holds
            // nothing snapshot-derived. It is a no-op today because the
            // previous park cleared it; keeping it unconditional means the
            // invariant does not depend on which path got us here.
            ui_renderer.end_cycle();
            continue 'cycles;
        }

        // Frame 0 must not composite the previous cycle's UI (action
        // panel, tips, hovered-window title): this cycle's fresh
        // UiSharedState is only broadcast after the show gate, which is
        // after frame 0 is drawn.
        ui_renderer.begin_cycle(cycle.timings.clone());
        perf.begin_session();

        // `.get()` so a worker-count mismatch (impossible today — both
        // construction sites size by the monitor list) degrades to missing
        // debug rows instead of panicking the render worker.
        let cycle_timings = cycle.timings.workers.get(monitor_index);
        let mark = |field: fn(&crate::telemetry::startup::CaptureWorkerTimings) -> &crate::telemetry::startup::AtomicDuration| {
            if let Some(t) = cycle_timings {
                field(t).set_once(cycle.timings.t_start.elapsed());
            }
        };

        mark(|t| &t.configure_start);
        surface.configure(&gpu.device, &config);
        mark(|t| &t.configure);

        // ── Stage B: upload this cycle's desktop snapshot ───────────

        mark(|t| &t.upload_start);
        gpu.snapshot = gpu::desktop::upload_snapshot(&gpu.device, &gpu.queue, &cycle.snapshot, &gpu.desktop_bgl, &gpu.desktop_sampler);
        mark(|t| &t.upload);

        // ── Stage C: per-cycle uniforms + bind group, draw frame 0 ──
        // Accent colour and initial mouse come from CycleParams, not from
        // settings baked in at worker spawn.

        let mut snapshot_state: Option<SnapshotState> = gpu.snapshot.as_ref().map(|snap| {
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
            };

            let ubo = gpu
                .device
                .create_buffer(&wgpu::BufferDescriptor {
                    label: Some("window uniforms"),
                    size: WINDOW_UNIFORMS_SIZE,
                    usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                });
            gpu.queue
                .write_buffer(&ubo, 0, bytemuck::bytes_of(&uniforms));

            let placeholder_cursor = create_placeholder_cursor_view(&gpu.device, &gpu.queue);
            let (cursor_color_view, cursor_mask_view) = match &snap.cursor {
                Some(ct) => (&ct.color_view, &ct.mask_view),
                None => (&placeholder_cursor, &placeholder_cursor),
            };

            let bind_group = gpu
                .device
                .create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some("window snapshot bind group"),
                    layout: &snap.bind_group_layout,
                    entries: &[
                        wgpu::BindGroupEntry {
                            binding: 0,
                            resource: ubo.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 1,
                            resource: wgpu::BindingResource::TextureView(&snap.view),
                        },
                        wgpu::BindGroupEntry {
                            binding: 2,
                            resource: wgpu::BindingResource::Sampler(&snap.sampler),
                        },
                        wgpu::BindGroupEntry {
                            binding: 3,
                            resource: wgpu::BindingResource::TextureView(cursor_color_view),
                        },
                        wgpu::BindGroupEntry {
                            binding: 4,
                            resource: wgpu::BindingResource::TextureView(cursor_mask_view),
                        },
                    ],
                });

            SnapshotState {
                ubo,
                bind_group,
                uniforms,
                base_uv_offset_scale,
            }
        });

        // A surface that sat hidden for hours can come back Outdated/Lost;
        // draw_once then only reconfigures without presenting, and showing
        // the window would flash the previous cycle's final frame. One
        // bounded retry after the reconfigure so frame 0 really presents
        // before ready_count is bumped.
        mark(|t| &t.first_render_start);
        let outcome = draw_once(
            &surface,
            &gpu,
            &config,
            snapshot_state.as_ref(),
            None,
            &mut ui_renderer,
            &perf,
            None,
            &mut None,
        );
        if outcome == DrawOutcome::Reconfigured {
            draw_once(
                &surface,
                &gpu,
                &config,
                snapshot_state.as_ref(),
                None,
                &mut ui_renderer,
                &perf,
                None,
                &mut None,
            );
        }
        let _ = gpu.device.poll(wgpu::PollType::Wait {
            submission_index: None,
            timeout: Some(Duration::from_secs(5)),
        });

        mark(|t| &t.first_render);

        cycle
            .ready_count
            .fetch_add(1, Ordering::Release);

        cycle.visible_latch.wait();

        // finish_cycle sets `cancelled` and then signals the latch, so a
        // cycle that ended before the overlay was shown releases us here —
        // park instead of rendering the dead cycle.
        if cycle.cancelled.load(Ordering::Acquire) {
            park_worker(&surface, &mut gpu, &parked_config, &mut ui_renderer);
            continue 'cycles;
        }

        // ── Per-cycle peek state ────────────────────────────────────

        let mut peek_textures: HashMap<usize, PeekTextureEntry> = HashMap::new();
        let mut active_peek: Option<PeekCommand> = None;
        let mut blurred_desktop: Option<(wgpu::Texture, wgpu::TextureView)> = None;

        // ── Render loop ─────────────────────────────────────────────

        let start = Instant::now();
        let mut mouse_pos: ScreenPointF = cycle.initial_mouse;
        let mut zoom: f32 = 1.0;
        let mut selection: Option<ScreenRect> = None;
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
            loop {
                match msg_rx.try_recv() {
                    Ok(RenderMsg::MouseState {
                        pos,
                        zoom: z,
                        selection: sel,
                        captured: cap,
                    }) => {
                        mouse_pos = pos;
                        zoom = z;
                        selection = sel;
                        captured = cap;
                    }
                    Ok(RenderMsg::UiState(state)) => {
                        overlays_visible = state.overlays_visible;
                        cursor_overlay_visible = state.cursor_overlay_visible;
                        scroll_pick_mode = state.scroll_pick_mode;
                        ocr = state.ocr.clone();
                        ui_renderer.set_state(state);
                    }
                    Ok(RenderMsg::BlurredDesktop {
                        cycle_gen,
                        image: bd,
                    }) => {
                        if cycle_gen != cycle.cycle_gen {
                            // Late output of a previous cycle's blur job.
                            continue;
                        }
                        let max_dim = gpu.device.limits().max_texture_dimension_2d;
                        if bd.width > max_dim || bd.height > max_dim || bd.width == 0 || bd.height == 0 {
                            continue;
                        }
                        let size = wgpu::Extent3d {
                            width: bd.width,
                            height: bd.height,
                            depth_or_array_layers: 1,
                        };
                        let texture = gpu
                            .device
                            .create_texture(&wgpu::TextureDescriptor {
                                label: Some("blurred desktop texture"),
                                size,
                                mip_level_count: 1,
                                sample_count: 1,
                                dimension: wgpu::TextureDimension::D2,
                                format: wgpu::TextureFormat::Bgra8Unorm,
                                usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                                view_formats: &[],
                            });
                        gpu.queue.write_texture(
                            wgpu::TexelCopyTextureInfo {
                                texture: &texture,
                                mip_level: 0,
                                origin: wgpu::Origin3d::ZERO,
                                aspect: wgpu::TextureAspect::All,
                            },
                            &bd.bgra,
                            wgpu::TexelCopyBufferLayout {
                                offset: 0,
                                bytes_per_row: Some(4 * bd.width),
                                rows_per_image: Some(bd.height),
                            },
                            size,
                        );
                        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
                        blurred_desktop = Some((texture, view));
                    }
                    Ok(RenderMsg::PeekImage {
                        cycle_gen,
                        image: peek,
                    }) => {
                        if cycle_gen != cycle.cycle_gen {
                            // Late output of a previous cycle's walker job —
                            // its window_index would denote a different
                            // window in this cycle's snapshot.
                            continue;
                        }
                        let max_dim = gpu.device.limits().max_texture_dimension_2d;
                        if peek.width > max_dim || peek.height > max_dim || peek.width == 0 || peek.height == 0 {
                            continue;
                        }
                        let size = wgpu::Extent3d {
                            width: peek.width,
                            height: peek.height,
                            depth_or_array_layers: 1,
                        };
                        let texture = gpu
                            .device
                            .create_texture(&wgpu::TextureDescriptor {
                                label: Some("peek window texture"),
                                size,
                                mip_level_count: 1,
                                sample_count: 1,
                                dimension: wgpu::TextureDimension::D2,
                                format: wgpu::TextureFormat::Bgra8Unorm,
                                usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                                view_formats: &[],
                            });
                        gpu.queue.write_texture(
                            wgpu::TexelCopyTextureInfo {
                                texture: &texture,
                                mip_level: 0,
                                origin: wgpu::Origin3d::ZERO,
                                aspect: wgpu::TextureAspect::All,
                            },
                            &peek.bgra,
                            wgpu::TexelCopyBufferLayout {
                                offset: 0,
                                bytes_per_row: Some(4 * peek.width),
                                rows_per_image: Some(peek.height),
                            },
                            size,
                        );
                        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
                        peek_textures.insert(
                            peek.window_index,
                            PeekTextureEntry {
                                _texture: texture,
                                view,
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
                    Ok(RenderMsg::EndCycle {
                        cycle_gen,
                    }) => {
                        if cycle_gen != cycle.cycle_gen {
                            // EndCycle of an earlier cycle whose BeginCycle
                            // was discarded (see the `cancelled` check
                            // above) — not ours to act on.
                            continue;
                        }
                        // Drop the whole-desktop snapshot; the blur/peek
                        // textures and snapshot state fall out of scope with
                        // this cycle iteration. Frees the per-device VRAM
                        // (including the swapchain backbuffers, via the 1×1
                        // parked surface), then park for the next BeginCycle.
                        //
                        // Via `park_worker` because the UiRenderer — built
                        // once per worker, outside this loop — must release
                        // the lift bind group first. Ending a cycle from
                        // OcrState::Lifted (OCR's COPY/SEARCH/UPLOAD and
                        // EXIT all do) leaves it holding a view of the
                        // snapshot, which would keep the texture resident
                        // for the whole parked gap.
                        park_worker(&surface, &mut gpu, &parked_config, &mut ui_renderer);
                        continue 'cycles;
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
                        captured,
                        overlays_visible,
                        cursor_overlay_visible,
                        scroll_pick_mode,
                        ocr_rect: ocr_fx.rect,
                        ocr_dim: ocr_fx.dim,
                        ocr_gray: ocr_fx.gray,
                        ocr_active: ocr_fx.active,
                        elapsed: start.elapsed().as_secs_f32(),
                        surface_size: (config.width, config.height),
                    },
                    cursor_textures,
                );
            }

            // Build per-frame peek draw state if active + texture available.
            let peek_bind_group = active_peek.as_ref().and_then(|cmd| {
                let pt = peek_textures.get(&cmd.window_index)?;
                let snap = gpu.snapshot.as_ref()?;
                let (_, ref blurred_view) = *blurred_desktop.as_ref()?;

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
                peek_uniforms.window_uv = window_uv;
                peek_uniforms.desktop_uv = desktop_uv;

                let n = pt.obstruction_rects.len().min(16);
                peek_uniforms.params = [
                    n as f32,
                    if cmd.captured { 0.0 } else { 0.45 },
                    config.width as f32,
                    config.height as f32,
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

                let bind_group = gpu
                    .device
                    .create_bind_group(&wgpu::BindGroupDescriptor {
                        label: Some("peek bind group"),
                        layout: &gpu.peek_bgl,
                        entries: &[
                            wgpu::BindGroupEntry {
                                binding: 0,
                                resource: peek_ubo.as_entire_binding(),
                            },
                            wgpu::BindGroupEntry {
                                binding: 1,
                                resource: wgpu::BindingResource::TextureView(&pt.view),
                            },
                            wgpu::BindGroupEntry {
                                binding: 2,
                                resource: wgpu::BindingResource::TextureView(blurred_view),
                            },
                            wgpu::BindGroupEntry {
                                binding: 3,
                                resource: wgpu::BindingResource::Sampler(&snap.sampler),
                            },
                        ],
                    });
                Some(bind_group)
            });

            if gpu_timing.is_some() {
                let _ = gpu.device.poll(wgpu::PollType::Poll);
            }

            if let Some(gt) = gpu_timing.as_mut() {
                for gpu_dur in gt.poll_completed() {
                    perf.backfill_next_gpu(gpu_dur);
                }
            }

            let now = Instant::now();
            let overall = now.duration_since(last_iter);
            last_iter = now;

            let mut sample: Option<PerfSample> = None;
            draw_once(
                &surface,
                &gpu,
                &config,
                snapshot_state.as_ref(),
                peek_bind_group.as_ref(),
                &mut ui_renderer,
                &perf,
                gpu_timing.as_ref(),
                &mut sample,
            );
            if let Some(mut s) = sample {
                s.overall = overall;
                perf.record(s);
            }
        }
    }
}

// ── Peek texture storage ────────────────────────────────────────────

// ── Per-window snapshot state (unchanged) ───────────────────────────

// ── draw_once (unchanged) ───────────────────────────────────────────
