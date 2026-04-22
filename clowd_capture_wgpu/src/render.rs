use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{mpsc, Arc, OnceLock};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use winit::dpi::PhysicalSize;
use winit::window::Window;

use crate::geometry::{RectExt, ScreenPointF, ScreenRect, WindowPoint};
use crate::gpu::{self, WindowGpu, WindowUniforms, PeekUniforms, PEEK_UNIFORMS_SIZE, SURFACE_FORMAT, WINDOW_UNIFORMS_SIZE};
use crate::settings::CapturerSettings;
use crate::sync::{ReadyGuard, VisibleLatch};
use crate::system::{CapturedDesktop, MonitorInfo, WindowPeekImage};
use crate::ui::components::debug::perf::{PerfSample, PerfTracker};
use crate::ui::components::debug::startup::StartupTimings;
use crate::ui::gpu::gpu_timing::GpuTimings;
use crate::ui::gpu::UiRenderer;
use crate::ui::shared::{UiMonitor, UiSharedState};

/// Duration of the colour → grayscale fade after the window first becomes
/// visible.
const FADE_DURATION_SECS: f32 = 0.3;

/// MSAA sample count applied to every render pipeline in the frame.
/// Set to 1 (no multisampling) — all UI geometry is axis-aligned
/// (rects, textured quads, glyph quads) so MSAA adds cost without
/// visual benefit.
pub const MSAA_SAMPLES: u32 = 1;

// ── Messages ────────────────────────────────────────────────────────

/// Messages the main thread sends to a render thread during the frame loop.
pub enum RenderMsg {
    Resize(PhysicalSize<u32>),
    MouseState {
        pos: ScreenPointF,
        zoom: f32,
        selection: Option<ScreenRect>,
        captured: bool,
    },
    UiState(Arc<UiSharedState>),
    BlurredDesktop(Arc<BlurredDesktopImage>),
    PeekImage(Arc<WindowPeekImage>),
    ShowPeek(Option<PeekCommand>),
    Shutdown,
}

pub struct BlurredDesktopImage {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
}

/// Tells render workers which obstructed window to peek at this frame.
#[derive(Debug, Clone, PartialEq)]
pub struct PeekCommand {
    pub window_index: usize,
    pub window_rect: ScreenRect,
    pub captured: bool,
}

/// Bootstrap messages sent to workers before the render loop starts.
pub enum WorkerInput {
    Screenshot(Arc<CapturedDesktop>),
    Handoff(WindowHandoff),
}

/// Window + surface pair created on the main thread and delivered to a
/// render worker via the bootstrap channel.
pub struct WindowHandoff {
    pub window: Arc<Window>,
    pub surface: wgpu::Surface<'static>,
}

// ── WindowHandle (main thread side) ─────────────────────────────────

/// Handle to a render thread, held by the main thread. Dropping it sends
/// `Shutdown` and joins the thread.
pub struct WindowHandle {
    pub window: Arc<Window>,
    pub monitor_bounds: ScreenRect,
    tx: mpsc::Sender<RenderMsg>,
    thread: Option<JoinHandle<()>>,
    #[cfg(target_os = "macos")]
    pub render_subview: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
}

impl WindowHandle {
    pub fn new(
        window: Arc<Window>,
        monitor_bounds: ScreenRect,
        tx: mpsc::Sender<RenderMsg>,
        thread: JoinHandle<()>,
        #[cfg(target_os = "macos")] render_subview: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
    ) -> Self {
        Self {
            window,
            monitor_bounds,
            tx,
            thread: Some(thread),
            #[cfg(target_os = "macos")]
            render_subview,
        }
    }

    pub fn resize(&self, size: PhysicalSize<u32>) {
        let _ = self.tx.send(RenderMsg::Resize(size));
    }

    pub fn update_mouse_state(
        &self,
        pos: ScreenPointF,
        zoom: f32,
        selection: Option<ScreenRect>,
        captured: bool,
    ) {
        let _ = self.tx.send(RenderMsg::MouseState {
            pos,
            zoom,
            selection,
            captured,
        });
    }

    pub fn update_ui_state(&self, state: Arc<UiSharedState>) {
        let _ = self.tx.send(RenderMsg::UiState(state));
    }

    pub fn update_peek_state(&self, cmd: Option<PeekCommand>) {
        let _ = self.tx.send(RenderMsg::ShowPeek(cmd));
    }
}

impl Drop for WindowHandle {
    fn drop(&mut self) {
        let _ = self.tx.send(RenderMsg::Shutdown);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

// ── Worker spawn + lifecycle ────────────────────────────────────────

/// Per-worker parameters built in main() before the event loop starts.
pub struct RenderWorkerParams {
    pub monitor: MonitorInfo,
    pub monitor_index: usize,
    pub settings: Arc<CapturerSettings>,
    pub instance: Arc<wgpu::Instance>,
    pub initial_mouse: ScreenPointF,
    pub startup: Arc<StartupTimings>,
    pub shown_time: Arc<OnceLock<Duration>>,
    pub ready_count: Arc<AtomicUsize>,
    pub visible_latch: Arc<VisibleLatch>,
}

/// Returned from `spawn_render_worker` so the caller can send bootstrap
/// messages and later build a `WindowHandle`.
pub struct WorkerSetup {
    pub input_tx: mpsc::Sender<WorkerInput>,
    pub render_msg_tx: mpsc::Sender<RenderMsg>,
    pub thread: JoinHandle<()>,
    pub monitor_bounds: ScreenRect,
}

pub fn spawn_render_worker(params: RenderWorkerParams) -> WorkerSetup {
    let (input_tx, input_rx) = mpsc::channel();
    let (render_msg_tx, render_msg_rx) = mpsc::channel();
    let monitor_bounds = params.monitor.bounds;
    let thread_name = format!("render-worker-{}", params.monitor_index);
    let thread = thread::Builder::new()
        .name(thread_name)
        .spawn(move || {
            render_worker_main(params, input_rx, render_msg_rx);
        })
        .expect("spawn render worker");
    WorkerSetup {
        input_tx,
        render_msg_tx,
        thread,
        monitor_bounds,
    }
}

fn render_worker_main(
    params: RenderWorkerParams,
    input_rx: mpsc::Receiver<WorkerInput>,
    msg_rx: mpsc::Receiver<RenderMsg>,
) {
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

    let mut snapshot: Option<Arc<gpu::DesktopSnapshot>> = None;
    let mut handoff: Option<WindowHandoff> = None;

    while snapshot.is_none() || handoff.is_none() {
        match input_rx.recv() {
            Ok(WorkerInput::Screenshot(captured)) => {
                snapshot = gpu::stage_b_upload_snapshot(
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
                startup.background.workers[monitor_index]
                    .surface_bind
                    .set_once(startup.t_start.elapsed());
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

    // Verify surface format.
    let caps = surface.get_capabilities(&bundle.adapter);
    let actual_format = caps
        .formats
        .iter()
        .copied()
        .find(|f| !f.is_srgb())
        .unwrap_or(caps.formats[0]);
    assert_eq!(
        actual_format, SURFACE_FORMAT,
        "surface format mismatch on monitor {monitor_index}"
    );

    // ── Stage C: assemble final state, configure surface, draw frame 0 ─

    let gpu = gpu::finalise_window_gpu(bundle, snapshot);

    let mut config = wgpu::SurfaceConfiguration {
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
        let m_x = monitor_bounds.min_x() as f32;
        let m_y = monitor_bounds.min_y() as f32;
        let m_w = monitor_bounds.width() as f32;
        let m_h = monitor_bounds.height() as f32;
        let vd_x = snap.vdesktop_origin[0];
        let vd_y = snap.vdesktop_origin[1];
        let vd_w = snap.vdesktop_size[0];
        let vd_h = snap.vdesktop_size[1];
        let base_uv_offset_scale = [
            (m_x - vd_x) / vd_w,
            (m_y - vd_y) / vd_h,
            m_w / vd_w,
            m_h / vd_h,
        ];

        let init_local = WindowPoint::new(
            initial_mouse.x - monitor_bounds.min_x() as f32,
            initial_mouse.y - monitor_bounds.min_y() as f32,
        );

        let uniforms = WindowUniforms {
            uv_offset_scale: base_uv_offset_scale,
            params: [0.0, init_local.x, init_local.y, scale_factor],
            accent_color: settings.accent_color,
            selection_rect: [0.0, 0.0, -1.0, -1.0],
            selection_params: [0.0, 0.0, 0.0, 0.0],
        };

        let ubo = gpu.device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("window uniforms"),
            size: WINDOW_UNIFORMS_SIZE,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        gpu.queue
            .write_buffer(&ubo, 0, bytemuck::bytes_of(&uniforms));

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

    let peek_ubo = gpu.device.create_buffer(&wgpu::BufferDescriptor {
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
    let mut last_iter = Instant::now();

    loop {
        loop {
            match msg_rx.try_recv() {
                Ok(RenderMsg::Resize(new_size)) => {
                    config.width = new_size.width.max(1);
                    config.height = new_size.height.max(1);
                    surface.configure(&gpu.device, &config);
                }
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
                    ui_renderer.set_state(state);
                }
                Ok(RenderMsg::BlurredDesktop(bd)) => {
                    let max_dim = gpu.device.limits().max_texture_dimension_2d;
                    if bd.width > max_dim || bd.height > max_dim
                        || bd.width == 0 || bd.height == 0
                    {
                        continue;
                    }
                    let size = wgpu::Extent3d {
                        width: bd.width,
                        height: bd.height,
                        depth_or_array_layers: 1,
                    };
                    let texture = gpu.device.create_texture(&wgpu::TextureDescriptor {
                        label: Some("blurred desktop texture"),
                        size,
                        mip_level_count: 1,
                        sample_count: 1,
                        dimension: wgpu::TextureDimension::D2,
                        format: wgpu::TextureFormat::Bgra8Unorm,
                        usage: wgpu::TextureUsages::TEXTURE_BINDING
                            | wgpu::TextureUsages::COPY_DST,
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
                    if peek.width > max_dim || peek.height > max_dim
                        || peek.width == 0 || peek.height == 0
                    {
                        continue;
                    }
                    let size = wgpu::Extent3d {
                        width: peek.width,
                        height: peek.height,
                        depth_or_array_layers: 1,
                    };
                    let texture = gpu.device.create_texture(&wgpu::TextureDescriptor {
                        label: Some("peek window texture"),
                        size,
                        mip_level_count: 1,
                        sample_count: 1,
                        dimension: wgpu::TextureDimension::D2,
                        format: wgpu::TextureFormat::Bgra8Unorm,
                        usage: wgpu::TextureUsages::TEXTURE_BINDING
                            | wgpu::TextureUsages::COPY_DST,
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
                    let view =
                        texture.create_view(&wgpu::TextureViewDescriptor::default());
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
            state.update_uniforms(
                &gpu.queue,
                &FrameState {
                    monitor_bounds,
                    mouse_pos,
                    zoom,
                    selection,
                    captured,
                    overlays_visible,
                    elapsed: start.elapsed().as_secs_f32(),
                    surface_size: (config.width, config.height),
                },
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
            let local_cursor = WindowPoint::new(
                cx - monitor_bounds.min_x() as f32,
                cy - monitor_bounds.min_y() as f32,
            );

            let to_local = |vd_x: f32, vd_y: f32| -> (f32, f32) {
                if zoom <= 1.0 {
                    (
                        vd_x - monitor_bounds.min_x() as f32,
                        vd_y - monitor_bounds.min_y() as f32,
                    )
                } else {
                    (
                        (vd_x - cx) * zoom + local_cursor.x,
                        (vd_y - cy) * zoom + local_cursor.y,
                    )
                }
            };

            let (sl, st) = to_local(sel.left() as f32, sel.top() as f32);
            let (sr, sb) = to_local(sel.right() as f32, sel.bottom() as f32);

            let wr = pt.window_rect;
            let (wl, wt) = to_local(wr.left() as f32, wr.top() as f32);

            // Window texture UV: map selection area to portion of window texture.
            // The texture is captured at raw GetWindowRect dimensions, so we
            // offset by the crop to skip the invisible resize border.
            let tw = pt.width as f32;
            let th = pt.height as f32;
            let crop_x = pt.crop_x as f32;
            let crop_y = pt.crop_y as f32;
            let window_uv = [
                (crop_x + sl - wl) / tw,
                (crop_y + st - wt) / th,
                (sr - sl) / tw,
                (sb - st) / th,
            ];

            let base_uv = snapshot_state.as_ref().map(|s| s.base_uv_offset_scale).unwrap_or([0.0; 4]);

            let mut peek_uniforms = PeekUniforms::zeroed();
            peek_uniforms.selection_rect = [sl, st, sr, sb];
            peek_uniforms.window_uv = window_uv;
            peek_uniforms.desktop_uv = base_uv;

            let n = pt.obstruction_rects.len().min(16);
            peek_uniforms.params = [
                n as f32,
                if cmd.captured { 0.0 } else { 0.45 },
                config.width as f32,
                config.height as f32,
            ];
            peek_uniforms.cursor_params = [
                local_cursor.x,
                local_cursor.y,
                scale_factor,
                0.0,
            ];
            for (i, r) in pt.obstruction_rects.iter().take(16).enumerate() {
                let (rl, rt) = to_local(r.left() as f32, r.top() as f32);
                let (rr, rb) = to_local(r.right() as f32, r.bottom() as f32);
                peek_uniforms.obstruction_rects[i] = [rl, rt, rr, rb];
            }

            gpu.queue.write_buffer(&peek_ubo, 0, bytemuck::bytes_of(&peek_uniforms));

            let bind_group = gpu.device.create_bind_group(&wgpu::BindGroupDescriptor {
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

struct PeekTextureEntry {
    _texture: wgpu::Texture,
    view: wgpu::TextureView,
    window_rect: ScreenRect,
    obstruction_rects: Vec<ScreenRect>,
    width: u32,
    height: u32,
    crop_x: i32,
    crop_y: i32,
}

// ── Per-window snapshot state (unchanged) ───────────────────────────

struct SnapshotState {
    ubo: wgpu::Buffer,
    bind_group: wgpu::BindGroup,
    uniforms: WindowUniforms,
    base_uv_offset_scale: [f32; 4],
}

struct FrameState {
    monitor_bounds: ScreenRect,
    mouse_pos: ScreenPointF,
    zoom: f32,
    selection: Option<ScreenRect>,
    captured: bool,
    overlays_visible: bool,
    elapsed: f32,
    surface_size: (u32, u32),
}

impl SnapshotState {
    fn update_uniforms(&mut self, queue: &wgpu::Queue, frame: &FrameState) {
        let FrameState {
            monitor_bounds,
            mouse_pos,
            zoom,
            selection,
            captured,
            overlays_visible,
            elapsed,
            surface_size,
        } = *frame;

        if !overlays_visible {
            self.uniforms.params[0] = 0.0;
            let local = WindowPoint::new(
                mouse_pos.x - monitor_bounds.min_x() as f32,
                mouse_pos.y - monitor_bounds.min_y() as f32,
            );
            self.uniforms.params[1] = -1.0;
            self.uniforms.params[2] = -1.0;
            if zoom <= 1.0 {
                self.uniforms.uv_offset_scale = self.base_uv_offset_scale;
            } else {
                let w = surface_size.0 as f32;
                let h = surface_size.1 as f32;
                let cu = local.x / w;
                let cv = local.y / h;
                let k = 1.0 - 1.0 / zoom;
                let base = self.base_uv_offset_scale;
                self.uniforms.uv_offset_scale = [
                    base[0] + base[2] * cu * k,
                    base[1] + base[3] * cv * k,
                    base[2] / zoom,
                    base[3] / zoom,
                ];
            }
            self.uniforms.selection_rect = [0.0, 0.0, -1.0, -1.0];
            self.uniforms.selection_params[0] = elapsed;
            self.uniforms.selection_params[1] = 0.0;
            self.uniforms.selection_params[2] = zoom;
            queue.write_buffer(&self.ubo, 0, bytemuck::bytes_of(&self.uniforms));
            return;
        }

        let fade = {
            let t = (elapsed / FADE_DURATION_SECS).clamp(0.0, 1.0);
            let inv = 1.0 - t;
            1.0 - inv * inv * inv * inv
        };
        self.uniforms.params[0] = fade;

        let local = WindowPoint::new(
            mouse_pos.x - monitor_bounds.min_x() as f32,
            mouse_pos.y - monitor_bounds.min_y() as f32,
        );
        self.uniforms.params[1] = local.x;
        self.uniforms.params[2] = local.y;

        if zoom <= 1.0 {
            self.uniforms.uv_offset_scale = self.base_uv_offset_scale;
        } else {
            let w = surface_size.0 as f32;
            let h = surface_size.1 as f32;
            let cu = local.x / w;
            let cv = local.y / h;
            let k = 1.0 - 1.0 / zoom;
            let base = self.base_uv_offset_scale;
            self.uniforms.uv_offset_scale = [
                base[0] + base[2] * cu * k,
                base[1] + base[3] * cv * k,
                base[2] / zoom,
                base[3] / zoom,
            ];
        }

        if let Some(sel) = selection {
            let cx = mouse_pos.x;
            let cy = mouse_pos.y;
            let local_cursor = WindowPoint::new(
                cx - monitor_bounds.min_x() as f32,
                cy - monitor_bounds.min_y() as f32,
            );
            let to_local = |vd_x: f32, vd_y: f32| -> (f32, f32) {
                (
                    (vd_x - cx) * zoom + local_cursor.x,
                    (vd_y - cy) * zoom + local_cursor.y,
                )
            };
            let (l, t) = to_local(sel.left() as f32, sel.top() as f32);
            let (r, b) = to_local(sel.right() as f32, sel.bottom() as f32);
            self.uniforms.selection_rect = [l, t, r, b];
        } else {
            self.uniforms.selection_rect = [0.0, 0.0, -1.0, -1.0];
        }

        self.uniforms.selection_params[0] = elapsed;
        self.uniforms.selection_params[1] = if captured { 1.0 } else { 0.0 };
        self.uniforms.selection_params[2] = zoom;

        queue.write_buffer(&self.ubo, 0, bytemuck::bytes_of(&self.uniforms));
    }
}

// ── draw_once (unchanged) ───────────────────────────────────────────

#[allow(clippy::too_many_arguments)]
fn draw_once(
    surface: &wgpu::Surface<'static>,
    gpu: &WindowGpu,
    config: &wgpu::SurfaceConfiguration,
    snapshot_state: Option<&SnapshotState>,
    peek_bind_group: Option<&wgpu::BindGroup>,
    ui_renderer: &mut UiRenderer,
    perf: &PerfTracker,
    gpu_timing: Option<&GpuTimings>,
    out_sample: &mut Option<PerfSample>,
) {
    let t_wait_start = Instant::now();
    let frame = match surface.get_current_texture() {
        wgpu::CurrentSurfaceTexture::Success(f)
        | wgpu::CurrentSurfaceTexture::Suboptimal(f) => f,
        wgpu::CurrentSurfaceTexture::Timeout | wgpu::CurrentSurfaceTexture::Occluded => return,
        wgpu::CurrentSurfaceTexture::Outdated | wgpu::CurrentSurfaceTexture::Lost => {
            surface.configure(&gpu.device, config);
            return;
        }
        wgpu::CurrentSurfaceTexture::Validation => return,
    };
    let wait = t_wait_start.elapsed();

    let t_draw_start = Instant::now();
    let view = frame
        .texture
        .create_view(&wgpu::TextureViewDescriptor::default());
    let mut encoder = gpu
        .device
        .create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("frame encoder"),
        });

    ui_renderer.prepare(&gpu.device, &gpu.queue, (config.width, config.height), perf);

    let begin_frame = gpu_timing.and_then(|gt| gt.begin_frame());
    let (pass_ts, slot_id) = match &begin_frame {
        Some(bf) => (Some(bf.pass.clone()), Some(bf.id)),
        None => (None, None),
    };

    {
        let mut rpass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("frame pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: &view,
                resolve_target: None,
                depth_slice: None,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Clear(wgpu::Color {
                        r: 0.05,
                        g: 0.05,
                        b: 0.08,
                        a: 1.0,
                    }),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            timestamp_writes: pass_ts,
            occlusion_query_set: None,
            multiview_mask: None,
        });
        rpass.set_pipeline(&gpu.pipeline);
        if let Some(state) = snapshot_state {
            rpass.set_bind_group(0, &state.bind_group, &[]);
            rpass.draw(0..3, 0..1);
        }
        if let Some(peek_bg) = peek_bind_group {
            rpass.set_pipeline(&gpu.peek_pipeline);
            rpass.set_bind_group(0, peek_bg, &[]);
            rpass.draw(0..6, 0..1);
        }
        ui_renderer.draw(&mut rpass);
    }

    if let (Some(gt), Some(id)) = (gpu_timing, slot_id) {
        gt.resolve(&mut encoder, id);
    }

    gpu.queue.submit(std::iter::once(encoder.finish()));
    if let (Some(gt), Some(id)) = (gpu_timing, slot_id) {
        gt.after_submit(id);
    }
    ui_renderer.trim();
    let draw = t_draw_start.elapsed();

    let t_present_start = Instant::now();
    frame.present();
    let present = t_present_start.elapsed();

    *out_sample = Some(PerfSample {
        wait,
        draw,
        present,
        overall: Duration::ZERO,
        gpu: None,
    });
}
