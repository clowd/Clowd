use std::collections::HashMap;
use std::sync::atomic::Ordering;
use std::sync::{mpsc, Arc};
use std::time::{Duration, Instant};

use crate::geometry::{screen_to_window, RectExt, ScreenPointF, ScreenRect};
use crate::gpu::desktop::{create_placeholder_cursor_view, WindowUniforms, WINDOW_UNIFORMS_SIZE};
use crate::gpu::peek::{PeekUniforms, PEEK_UNIFORMS_SIZE};
use crate::gpu::{self, SURFACE_FORMAT};
use crate::sync::ReadyGuard;
use crate::telemetry::perf::{PerfSample, PerfTracker};
use crate::ui::gpu::gpu_timing::GpuTimings;
use crate::ui::gpu::UiRenderer;
use crate::ui::shared::UiMonitor;

pub mod desktop;
pub mod frame;
pub mod peek;
pub mod protocol;
pub mod window;
pub mod worker;
use desktop::{FrameState, SnapshotState};
use frame::draw_once;
use peek::PeekTextureEntry;
use protocol::{PeekCommand, RenderMsg, WindowHandoff, WorkerInput};
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

/// Handle to a render thread, held by the main thread. Dropping it sends
/// `Shutdown` and joins the thread.
// ── Worker spawn + lifecycle ────────────────────────────────────────

/// Per-worker parameters built in main() before the event loop starts.
fn render_worker_main(params: RenderWorkerParams, input_rx: mpsc::Receiver<WorkerInput>, msg_rx: mpsc::Receiver<RenderMsg>) {
    let RenderWorkerParams {
        monitor,
        monitor_index,
        settings,
        instance,
        initial_mouse,
        startup,
        shown_time,
        ready_count,
        visible_latch,
    } = params;

    let mut guard = ReadyGuard::new(ready_count.clone());
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

    let mut ui_renderer = UiRenderer::new(
        &bundle.device,
        &bundle.queue,
        SURFACE_FORMAT,
        this_monitor,
        monitor_index,
        monitor_name,
        adapter_name,
        startup.clone(),
        shown_time.clone(),
    );

    worker_timings
        .render_prep
        .set_once(startup.t_start.elapsed());

    // ── Event-driven wait for Screenshot + Handoff ──────────────────

    let mut snapshot: Option<Arc<gpu::desktop::DesktopSnapshot>> = None;
    let mut handoff: Option<WindowHandoff> = None;

    while snapshot.is_none() || handoff.is_none() {
        match input_rx.recv() {
            Ok(WorkerInput::Screenshot(captured)) => {
                startup.background.workers[monitor_index]
                    .upload_start
                    .set_once(startup.t_start.elapsed());
                snapshot = gpu::desktop::upload_snapshot(
                    &bundle.device,
                    &bundle.queue,
                    &captured,
                    &bundle.desktop_bgl,
                    &bundle.desktop_sampler,
                );
                startup.background.workers[monitor_index]
                    .upload
                    .set_once(startup.t_start.elapsed());
            }
            Ok(WorkerInput::Handoff(h)) => {
                handoff = Some(h);
            }
            Err(_) => {
                error!("render worker {monitor_index}: input channel closed");
                return;
            }
        }
    }

    let handoff = handoff.unwrap();
    let _window = handoff.window;
    let surface = handoff.surface;

    startup.background.workers[monitor_index]
        .first_render_start
        .set_once(startup.t_start.elapsed());

    // Verify surface format.
    let caps = surface.get_capabilities(&bundle.adapter);
    let actual_format = caps
        .formats
        .iter()
        .copied()
        .find(|f| !f.is_srgb())
        .unwrap_or(caps.formats[0]);
    assert_eq!(actual_format, SURFACE_FORMAT, "surface format mismatch on monitor {monitor_index}");

    // ── Stage C: assemble final state, configure surface, draw frame 0 ─

    let gpu = gpu::finalise_window_gpu(bundle, snapshot);

    let config = wgpu::SurfaceConfiguration {
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
        format: SURFACE_FORMAT,
        width: (monitor_bounds.width() as u32).max(1),
        height: (monitor_bounds.height() as u32).max(1),
        present_mode: wgpu::PresentMode::Fifo,
        alpha_mode: wgpu::CompositeAlphaMode::Opaque,
        view_formats: vec![],
        desired_maximum_frame_latency: 1,
    };
    surface.configure(&gpu.device, &config);

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

        let init_local = screen_to_window(monitor_bounds, initial_mouse);

        let uniforms = WindowUniforms {
            uv_offset_scale: base_uv_offset_scale,
            params: [0.0, init_local.x, init_local.y, scale_factor],
            accent_color: settings.accent_color,
            selection_rect: [0.0, 0.0, -1.0, -1.0],
            selection_params: [0.0, 0.0, 0.0, 0.0],
            cursor_rect: [0.0, 0.0, -1.0, -1.0],
            cursor_params: [0.0, 0.0, 0.0, 0.0],
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

    let mut perf = PerfTracker::new_with_refresh(refresh_hz);
    let mut gpu_timing = GpuTimings::new(&gpu.device, &gpu.queue);

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
    let _ = gpu.device.poll(wgpu::PollType::Wait {
        submission_index: None,
        timeout: Some(Duration::from_secs(5)),
    });

    ui_renderer.mark_first_visible_frame();

    startup.background.workers[monitor_index]
        .first_render
        .set_once(startup.t_start.elapsed());

    ready_count.fetch_add(1, Ordering::Release);
    guard.disarm();

    visible_latch.wait();

    // ── Peek state ───────────────────────────────────────────────────

    let mut peek_textures: HashMap<usize, PeekTextureEntry> = HashMap::new();
    let mut active_peek: Option<PeekCommand> = None;
    let mut blurred_desktop: Option<(wgpu::Texture, wgpu::TextureView)> = None;

    let peek_ubo = gpu
        .device
        .create_buffer(&wgpu::BufferDescriptor {
            label: Some("peek uniforms"),
            size: PEEK_UNIFORMS_SIZE,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

    // ── Render loop ─────────────────────────────────────────────────

    let start = Instant::now();
    let mut mouse_pos: ScreenPointF = initial_mouse;
    let mut zoom: f32 = 1.0;
    let mut selection: Option<ScreenRect> = None;
    let mut captured: bool = false;
    let mut overlays_visible: bool = true;
    let mut cursor_overlay_visible: bool = true;
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
                    ui_renderer.set_state(state);
                }
                Ok(RenderMsg::BlurredDesktop(bd)) => {
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
                Ok(RenderMsg::PeekImage(peek)) => {
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
                Ok(RenderMsg::Shutdown) | Err(mpsc::TryRecvError::Disconnected) => return,
                Err(mpsc::TryRecvError::Empty) => break,
            }
        }

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

// ── Peek texture storage ────────────────────────────────────────────

// ── Per-window snapshot state (unchanged) ───────────────────────────

// ── draw_once (unchanged) ───────────────────────────────────────────
