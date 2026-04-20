use std::sync::{mpsc, Arc, Barrier, OnceLock};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use winit::dpi::PhysicalSize;
use winit::window::Window;

use crate::geometry::{RectExt, ScreenPointF, ScreenRect};
use crate::gpu::{WindowGpu, WindowUniforms, WINDOW_UNIFORMS_SIZE};
use crate::settings::CapturerSettings;
use crate::ui::components::debug::perf::{PerfSample, PerfTracker};
use crate::ui::components::debug::startup::StartupTimings;
use crate::ui::gpu::UiRenderer;
use crate::ui::shared::{UiMonitor, UiSharedState};

/// Duration of the colour → grayscale fade after the window first becomes
/// visible. Tuned to feel "snappy but not snap" — under 300ms reads as a
/// pop, over 700ms feels sluggish.
const FADE_DURATION_SECS: f32 = 0.3;

/// MSAA sample count applied to every render pipeline in the frame. 4
/// is the maximum guaranteed across wgpu's supported formats (the
/// WebGPU spec requires support for counts 1 and 4 on Bgra8Unorm; many
/// devices including Apple Silicon top out at 4). Higher counts would
/// mean a runtime-queried value threaded through the pipelines; not
/// worth the complexity until a user reports visibly stepped edges
/// that SSAA can't fix.
pub const MSAA_SAMPLES: u32 = 4;

/// MSAA-resolve color target. Every color attachment in the frame renders
/// into this texture at `MSAA_SAMPLES` samples and resolves into the
/// swapchain view on pass store — the final swapchain texture only ever
/// sees the already-resolved pixels. Recreated on `Resize`.
struct MsaaTarget {
    view: wgpu::TextureView,
    width: u32,
    height: u32,
}

impl MsaaTarget {
    fn new(device: &wgpu::Device, format: wgpu::TextureFormat, width: u32, height: u32) -> Self {
        let tex = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("msaa color target"),
            size: wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: MSAA_SAMPLES,
            dimension: wgpu::TextureDimension::D2,
            format,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            view_formats: &[],
        });
        let view = tex.create_view(&wgpu::TextureViewDescriptor::default());
        Self {
            view,
            width,
            height,
        }
    }

    fn ensure(&mut self, device: &wgpu::Device, format: wgpu::TextureFormat, w: u32, h: u32) {
        if self.width != w || self.height != h {
            *self = Self::new(device, format, w, h);
        }
    }
}

/// Messages the main thread can send to a render thread.
pub enum RenderMsg {
    Resize(PhysicalSize<u32>),
    /// New cursor + zoom + selection state. `pos` is the *virtual* cursor
    /// in virtual-desktop pixels (f32 so sub-pixel motion at high zoom
    /// stays smooth); `zoom` is the current magnifier scale (1.0 .. 256.0);
    /// `selection` is the current mouse-drag rect in virtual-desktop pixel
    /// coords (each render thread maps it through its own VD→window-local
    /// transform every frame); `captured` is true after the user has
    /// finalised a selection. The render thread caches the latest values
    /// and uses them on the next frame — the channel is the only
    /// synchronisation we need. Everything ships in a single message so
    /// state-transition frames (e.g. finalise-selection-and-snap-zoom)
    /// land atomically and the render thread never sees a partially-
    /// updated state across two drains.
    MouseState {
        pos: ScreenPointF,
        zoom: f32,
        selection: Option<ScreenRect>,
        captured: bool,
    },
    /// Shared UI state broadcast from the app thread. Every render thread
    /// receives the same `Arc` on every tick; each one independently
    /// decides which components belong on its monitor using the pure
    /// rules in [`crate::ui::shared`].
    UiState(Arc<UiSharedState>),
    Shutdown,
}

/// Handle to a render thread, held by the main thread. Dropping it sends a
/// `Shutdown` on the channel and joins the thread, so ending the event loop
/// (and thus dropping the App's HashMap) is enough to tear everything down
/// cleanly.
pub struct WindowHandle {
    pub window: Arc<Window>,
    /// This window's monitor in virtual-desktop screen pixels. Cached on
    /// the main thread so `CursorMoved` (which delivers window-local
    /// coords) can be converted to virtual-desktop coords without having
    /// to re-enumerate monitors on every mouse-move.
    pub monitor_bounds: ScreenRect,
    tx: mpsc::Sender<RenderMsg>,
    thread: Option<JoinHandle<()>>,
}

impl WindowHandle {
    pub fn resize(&self, size: PhysicalSize<u32>) {
        let _ = self.tx.send(RenderMsg::Resize(size));
    }

    pub fn update_mouse_state(&self, pos: ScreenPointF, zoom: f32, selection: Option<ScreenRect>, captured: bool) {
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
}

impl Drop for WindowHandle {
    fn drop(&mut self) {
        // Send Shutdown first so the render thread sees an explicit message
        // rather than a Disconnected error on the next try_recv.
        let _ = self.tx.send(RenderMsg::Shutdown);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

/// Per-monitor parameters for spawning a render thread.
pub struct RenderThreadParams {
    pub window: Arc<Window>,
    pub surface: wgpu::Surface<'static>,
    pub gpu: WindowGpu,
    pub settings: Arc<CapturerSettings>,
    /// This window's monitor in virtual-desktop screen coordinates.
    pub monitor_bounds: ScreenRect,
    /// Full UI-facing description of this window's monitor (bounds, DPI,
    /// primary flag). Passed to the `UiRenderer` so each render thread
    /// can run the shared visibility rules against its own monitor.
    pub this_monitor: UiMonitor,
    /// Zero-based index of this monitor in the enumeration order, shown
    /// as the `N:` prefix in the debug monitor-info panel.
    pub monitor_index: usize,
    /// Human-readable monitor name (e.g. `\\.\DISPLAY1` on Windows).
    /// Shown in the debug panel header.
    pub monitor_name: String,
    /// DPI scale (1.0 = 100%). Written into the uniform so the shader
    /// can size the coloured crosshair arms in physical pixels.
    pub scale_factor: f32,
    /// Monitor refresh rate. Accepted for future non-DX12 backends.
    pub refresh_hz: f32,
    /// Cursor position (virtual-desktop pixels, f32) sampled *before*
    /// any window was created, so frame 0 renders the crosshair correctly.
    pub initial_mouse: ScreenPointF,
    /// Startup timing markers, frozen at spawn time. Used by the debug
    /// panel to show the same startup breakdown as the C++ version.
    pub startup: Arc<StartupTimings>,
    /// Shared one-shot for the "all windows visible" timestamp. Set by
    /// the main thread in `about_to_wait` right after
    /// `show_windows_atomically`. Render threads only read.
    pub shown_time: Arc<OnceLock<Duration>>,
    /// Atomically incremented when this thread finishes frame 0.
    pub ready_count: Arc<std::sync::atomic::AtomicUsize>,
    /// Blocks until the main thread reveals all windows.
    pub visible_barrier: Arc<Barrier>,
}

/// Spawn a render thread for a single window.
///
/// The render thread takes ownership of `surface` and `gpu` — one complete
/// GPU stack per window. Each window gets its own DX12 device + command
/// queue so swap chain presents are fully independent across monitors
/// (Hardware: Independent Flip). The GPU bootstrap happens on the main
/// thread (winit's window handle is only available there); the render
/// thread configures the surface and enters the blocking-present loop.
pub fn spawn_render_thread(params: RenderThreadParams) -> WindowHandle {
    let (tx, rx) = mpsc::channel();
    let monitor_bounds = params.monitor_bounds;
    let window = params.window.clone();
    let thread_name = format!("render-{:?}", window.id());
    let thread = thread::Builder::new()
        .name(thread_name)
        .spawn(move || {
            render_thread_main(params, rx);
        })
        .expect("spawn render thread");
    WindowHandle {
        window,
        monitor_bounds,
        tx,
        thread: Some(thread),
    }
}

fn render_thread_main(params: RenderThreadParams, rx: mpsc::Receiver<RenderMsg>) {
    let RenderThreadParams {
        window: _,
        surface,
        gpu,
        settings,
        monitor_bounds,
        this_monitor,
        monitor_index,
        monitor_name,
        scale_factor,
        refresh_hz,
        initial_mouse,
        startup,
        shown_time,
        ready_count,
        visible_barrier,
    } = params;
    let adapter_name = gpu.adapter_name.clone();
    // `refresh_hz` is accepted (and logged) for future use — DXGI's waitable
    // object handles the actual pacing, so we don't need the value for timing.
    // If we ever port to a non-DX12 backend we'll need an explicit sleep
    // fallback here that uses it.
    let _ = refresh_hz;

    let mut config = wgpu::SurfaceConfiguration {
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
        format: gpu.surface_format,
        width: (monitor_bounds.width() as u32).max(1),
        height: (monitor_bounds.height() as u32).max(1),
        // Fifo + DX12 waitable gives vsynced presentation while
        // get_current_texture() wakes us right before the next scanout.
        present_mode: wgpu::PresentMode::Fifo,
        // Opaque — tells DWM this surface has no transparency, matching
        // the C++ version's DXGI_ALPHA_MODE_IGNORE. `Auto` can resolve
        // to PreMultiplied on some configurations, which forces DWM to
        // compose rather than direct-flip.
        alpha_mode: wgpu::CompositeAlphaMode::Opaque,
        view_formats: vec![],
        desired_maximum_frame_latency: 1,
    };
    surface.configure(&gpu.device, &config);

    // Build the per-window uniform buffer + bind group, if (and only if) we
    // have a snapshot to sample from. Without a snapshot the loop runs but
    // skips the draw and just clears.
    let mut snapshot_state: Option<SnapshotState> = gpu.snapshot.as_ref().map(|snap| {
        // UV math: where this monitor begins inside the virtual-desktop
        // texture, and how much of that texture it covers. Done in f32 so
        // negative monitor origins (secondary monitors left of/above the
        // primary) work without underflow. This is the **baseline** — the
        // zoom-around-cursor transform is folded into it per frame below,
        // and the uniform's `uv_offset_scale` field carries the post-zoom
        // result to the shader. At zoom=1 the folded value is identical to
        // the baseline.
        let m_x = monitor_bounds.min_x() as f32;
        let m_y = monitor_bounds.min_y() as f32;
        let m_w = monitor_bounds.width() as f32;
        let m_h = monitor_bounds.height() as f32;
        let vd_x = snap.vdesktop_origin[0];
        let vd_y = snap.vdesktop_origin[1];
        let vd_w = snap.vdesktop_size[0];
        let vd_h = snap.vdesktop_size[1];
        let base_uv_offset_scale = [(m_x - vd_x) / vd_w, (m_y - vd_y) / vd_h, m_w / vd_w, m_h / vd_h];

        // Seed the cursor position for the very first (frame-0) draw, so
        // the crosshair appears at the correct spot the instant the window
        // becomes visible rather than briefly snapping from the top-left.
        // The value was sampled by the main thread *before* any window
        // existed; from here on the position arrives via RenderMsg::MouseState.
        let init_local_x = initial_mouse.x - monitor_bounds.min_x() as f32;
        let init_local_y = initial_mouse.y - monitor_bounds.min_y() as f32;

        let uniforms = WindowUniforms {
            uv_offset_scale: base_uv_offset_scale,
            params: [0.0, init_local_x, init_local_y, scale_factor],
            crosshair_color: settings.crosshair_color,
            selection_rect: [0.0, 0.0, -1.0, -1.0],
            selection_params: [0.0, 0.0, 0.0, 0.0],
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

    // Construct this monitor's UI renderer. Each render thread owns its
    // own because each owns its own wgpu device and associated pipelines
    // / buffers. The renderer decides per-component visibility for THIS
    // monitor every frame using the pure rules in `ui::shared`.
    let mut ui_renderer = UiRenderer::new(
        &gpu.device,
        &gpu.queue,
        gpu.surface_format,
        this_monitor,
        monitor_index,
        monitor_name.clone(),
        adapter_name,
        startup,
        shown_time,
    );

    // Frame-timing tracker for the debug panel. Thread-local: this
    // render thread both produces and consumes the samples, so there's
    // no reason to route them through the cross-thread broadcast.
    let mut perf = PerfTracker::new();

    let mut msaa = MsaaTarget::new(&gpu.device, gpu.surface_format, config.width, config.height);

    // Frame 0 — render once before the window is revealed so the first
    // visible frame has content. On macOS the window starts at alpha=0;
    // on Windows it starts hidden. Either way, this frame is never seen
    // directly — it just primes the swap chain.
    draw_once(
        &surface,
        &gpu,
        &config,
        snapshot_state.as_ref(),
        &mut ui_renderer,
        &perf,
        &msaa,
        &mut None,
    );
    // Wait for all submitted GPU work to complete so the presented
    // drawable is fully resolved before the window becomes visible.
    let _ = gpu.device.poll(wgpu::PollType::Wait {
        submission_index: None,
        timeout: Some(std::time::Duration::from_secs(5)),
    });

    // Record "this display's frame 0 is fully rendered" the instant GPU
    // work completes. Matches C++ `trender` semantics (`DxScreenCapture.cpp:1018`),
    // which is set after frame 0's EndDraw — BEFORE `ShowWindow`. The
    // Monitor Info panel's `first render` readout is this minus
    // `startup.t_start`.
    ui_renderer.mark_first_visible_frame();

    // Signal the main thread that this render thread's frame 0 is done.
    ready_count.fetch_add(1, std::sync::atomic::Ordering::Release);

    // Wait for the main thread to actually reveal the windows (snap alpha
    // to 1 / set_visible). Only then start the animation clock — otherwise
    // the colour→grayscale fade runs while the window is still invisible.
    visible_barrier.wait();

    // Start the animation clock AFTER the visible barrier. The barrier
    // waits are unbounded intervals and we don't want any of that to eat
    // into the fade budget.
    let start = Instant::now();

    // Latest cursor position (virtual-desktop pixels, f32), zoom level,
    // and selection rect the main thread has told us about. Seeded from
    // the pre-window snapshot at zoom=1, no selection; every subsequent
    // value arrives via RenderMsg::MouseState.
    let mut mouse_pos: ScreenPointF = initial_mouse;
    let mut zoom: f32 = 1.0;
    let mut selection: Option<ScreenRect> = None;
    // Once captured the shader stops drawing the crosshair entirely
    // — the OS cursor takes over the visual role. Plumbed via
    // `selection_params.y` (0/1 float).
    let mut captured: bool = false;

    // Marker used to compute the `overall` frame-time (wall-clock gap
    // between consecutive loop iterations). Initialised to `now` so the
    // first sample has a sane-but-discardable value.
    let mut last_iter = Instant::now();

    loop {
        // Drain *all* pending commands non-blockingly before the blocking
        // present. A burst of MouseState events from a fast mouse must
        // collapse to the latest in a single frame, not lag N frames
        // behind. Shutdown propagates within at most one frame.
        loop {
            match rx.try_recv() {
                Ok(RenderMsg::Resize(new_size)) => {
                    config.width = new_size.width.max(1);
                    config.height = new_size.height.max(1);
                    surface.configure(&gpu.device, &config);
                    msaa.ensure(&gpu.device, gpu.surface_format, config.width, config.height);
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
                Ok(RenderMsg::UiState(state)) => ui_renderer.set_state(state),
                Ok(RenderMsg::Shutdown) | Err(mpsc::TryRecvError::Disconnected) => return,
                Err(mpsc::TryRecvError::Empty) => break,
            }
        }

        // Update the fade factor and push it to the GPU. Cheap; the
        // staging ring buffer absorbs this without blocking.
        if let Some(state) = snapshot_state.as_mut() {
            state.update_uniforms(
                &gpu.queue,
                &FrameState {
                    monitor_bounds,
                    mouse_pos,
                    zoom,
                    selection,
                    captured,
                    elapsed: start.elapsed().as_secs_f32(),
                    surface_size: (config.width, config.height),
                },
            );
        }

        // Timing bracket: `overall` is the gap between consecutive loop
        // iterations (reciprocal ≈ FPS). Sampled here so it survives
        // early-returns from `draw_once`.
        let now = Instant::now();
        let overall = now.duration_since(last_iter);
        last_iter = now;

        // `draw_once` produces a `PerfSample` covering wait / draw /
        // present; we fill in `overall` and record it afterwards.
        let mut sample: Option<PerfSample> = None;
        // This call blocks on the DXGI waitable object (because Backends::DX12
        // + Dx12UseFrameLatencyWaitableObject::Wait). We wake up ~one frame
        // before the next scanout — the last reasonable moment to begin
        // rendering before we must hand a frame to the compositor.
        draw_once(
            &surface,
            &gpu,
            &config,
            snapshot_state.as_ref(),
            &mut ui_renderer,
            &perf,
            &msaa,
            &mut sample,
        );
        if let Some(mut s) = sample {
            s.overall = overall;
            perf.record(s);
        }
    }
}

/// Per-window GPU resources that only exist when we have a desktop snapshot.
struct SnapshotState {
    ubo: wgpu::Buffer,
    bind_group: wgpu::BindGroup,
    uniforms: WindowUniforms,
    /// This monitor's un-zoomed UV region of the shared desktop texture:
    /// [u_offset, v_offset, u_scale, v_scale]. The per-frame loop folds
    /// the current zoom-around-cursor transform into this to produce
    /// `uniforms.uv_offset_scale`. At zoom=1 they're identical.
    base_uv_offset_scale: [f32; 4],
}

/// Per-frame input for uniform computation.
struct FrameState {
    monitor_bounds: ScreenRect,
    mouse_pos: ScreenPointF,
    zoom: f32,
    selection: Option<ScreenRect>,
    captured: bool,
    elapsed: f32,
    surface_size: (u32, u32),
}

impl SnapshotState {
    /// Recompute all per-frame uniform values (fade, cursor, zoom,
    /// selection rect) and write them to the GPU buffer.
    fn update_uniforms(&mut self, queue: &wgpu::Queue, frame: &FrameState) {
        let FrameState {
            monitor_bounds,
            mouse_pos,
            zoom,
            selection,
            captured,
            elapsed,
            surface_size,
        } = *frame;
        let fade = if cfg!(target_os = "macos") {
            // macOS: skip the GPU fade — the window fades in via alpha.
            1.0
        } else {
            let t = (elapsed / FADE_DURATION_SECS).clamp(0.0, 1.0);
            let inv = 1.0 - t;
            1.0 - inv * inv * inv * inv
        };
        self.uniforms.params[0] = fade;

        let local_x = mouse_pos.x - monitor_bounds.min_x() as f32;
        let local_y = mouse_pos.y - monitor_bounds.min_y() as f32;
        self.uniforms.params[1] = local_x;
        self.uniforms.params[2] = local_y;

        // Fold the zoom-around-cursor transform into uv_offset_scale.
        if zoom <= 1.0 {
            self.uniforms.uv_offset_scale = self.base_uv_offset_scale;
        } else {
            let w = surface_size.0 as f32;
            let h = surface_size.1 as f32;
            let cu = local_x / w;
            let cv = local_y / h;
            let k = 1.0 - 1.0 / zoom;
            let base = self.base_uv_offset_scale;
            self.uniforms.uv_offset_scale = [
                base[0] + base[2] * cu * k,
                base[1] + base[3] * cv * k,
                base[2] / zoom,
                base[3] / zoom,
            ];
        }

        // Selection rect (if any) → window-local physical pixels.
        if let Some(sel) = selection {
            let cx = mouse_pos.x;
            let cy = mouse_pos.y;
            let local_cx = cx - monitor_bounds.min_x() as f32;
            let local_cy = cy - monitor_bounds.min_y() as f32;
            let to_local = |vd_x: f32, vd_y: f32| -> (f32, f32) { ((vd_x - cx) * zoom + local_cx, (vd_y - cy) * zoom + local_cy) };
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

#[allow(clippy::too_many_arguments)]
fn draw_once(
    surface: &wgpu::Surface<'static>,
    gpu: &WindowGpu,
    config: &wgpu::SurfaceConfiguration,
    snapshot_state: Option<&SnapshotState>,
    ui_renderer: &mut UiRenderer,
    perf: &PerfTracker,
    msaa: &MsaaTarget,
    out_sample: &mut Option<PerfSample>,
) {
    // Wait sample — `get_current_texture()` blocks on the DXGI waitable
    // object on Windows, so this is where the wait-for-vsync time lives.
    // On other backends it's effectively instantaneous; the sample just
    // reads near-zero and is still useful for regression comparisons.
    let t_wait_start = Instant::now();
    let frame = match surface.get_current_texture() {
        wgpu::CurrentSurfaceTexture::Success(f) | wgpu::CurrentSurfaceTexture::Suboptimal(f) => f,
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
    // Both passes write into the MSAA color target and resolve into
    // the swapchain view on Store. Doing it in both passes (instead of
    // only the final one) keeps the MSAA target self-consistent while
    // the render pass is open — the resolve on Store is what actually
    // fills the swapchain texture.
    {
        let mut rpass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("desktop pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: &msaa.view,
                resolve_target: Some(&view),
                depth_slice: None,
                ops: wgpu::Operations {
                    // Dark fallback for the no-snapshot path. With a
                    // snapshot the fullscreen triangle covers every pixel,
                    // so the clear is never visible.
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
            timestamp_writes: None,
            occlusion_query_set: None,
            multiview_mask: None,
        });
        rpass.set_pipeline(&gpu.pipeline);
        if let Some(state) = snapshot_state {
            rpass.set_bind_group(0, &state.bind_group, &[]);
            rpass.draw(0..3, 0..1);
        }
    }

    // Second render pass: the UI renderer draws all components that
    // belong on this monitor on top of the just-drawn desktop/selection
    // layer. We pass the MSAA view; the UI renderer sets up its own
    // color attachment with `resolve_target: Some(&view)` internally.
    ui_renderer.render(
        &gpu.device,
        &gpu.queue,
        &mut encoder,
        &msaa.view,
        &view,
        (config.width, config.height),
        perf,
    );

    gpu.queue
        .submit(std::iter::once(encoder.finish()));
    let draw = t_draw_start.elapsed();

    let t_present_start = Instant::now();
    frame.present();
    let present = t_present_start.elapsed();

    *out_sample = Some(PerfSample {
        wait,
        draw,
        present,
        overall: std::time::Duration::ZERO,
    });
}
