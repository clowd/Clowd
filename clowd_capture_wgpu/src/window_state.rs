use std::sync::{mpsc, Arc, Barrier};
use std::thread::{self, JoinHandle};
use std::time::Instant;

use winit::dpi::PhysicalSize;
use winit::window::Window;

use crate::geometry::{ScreenPointF, ScreenRect};
use crate::gpu::{SharedGpu, WindowUniforms, WINDOW_UNIFORMS_SIZE};
use crate::settings::CapturerSettings;

/// Duration of the colour → grayscale fade after the window first becomes
/// visible. Tuned to feel "snappy but not snap" — under 300ms reads as a
/// pop, over 700ms feels sluggish.
const FADE_DURATION_SECS: f32 = 0.3;

/// Messages the main thread can send to a render thread.
pub enum RenderMsg {
    Resize(PhysicalSize<u32>),
    /// New cursor + zoom state. `pos` is the *virtual* cursor in
    /// virtual-desktop pixels (f32 so sub-pixel motion at high zoom stays
    /// smooth); `zoom` is the current magnifier scale (1.0 .. 256.0). The
    /// render thread caches the latest values and uses them on the next
    /// frame — the channel is the only synchronisation we need. Cursor
    /// moves and zoom changes share a single message so the render thread
    /// never sees a partially-updated state across two drains.
    MouseState { pos: ScreenPointF, zoom: f32 },
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

    pub fn update_mouse_state(&self, pos: ScreenPointF, zoom: f32) {
        let _ = self.tx.send(RenderMsg::MouseState { pos, zoom });
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

/// Spawn a render thread for a single window.
///
/// The render thread takes ownership of `surface`, an `Arc<SharedGpu>`, a
/// receiver, and the `Barrier`. It renders frame 0, hits the barrier, and
/// then enters a blocking-present loop. It never touches `Window` — that
/// Arc stays parked on the main thread.
///
/// `monitor_bounds` is this window's monitor in virtual-desktop screen
/// coordinates. The thread uses it (combined with the snapshot's
/// virtual-desktop bounds) to compute its slice of the shared texture.
/// `scale_factor` is this monitor's DPI scale (1.0 = 100 %) and is
/// written once into the uniform so the shader can size the coloured
/// crosshair arms in physical pixels. `settings` is the shared
/// `CapturerSettings`; the render thread reads it once at startup
/// (currently only `crosshair_color`) and stashes the values into the
/// per-window uniform.
///
/// `initial_mouse` is the cursor position (virtual-desktop screen
/// pixels, as f32) sampled by the main thread *before* any window was
/// created, so the very first frame can render the crosshair at the
/// correct spot without ever querying the OS from the render thread.
pub fn spawn_render_thread(
    window: Arc<Window>,
    surface: wgpu::Surface<'static>,
    gpu: Arc<SharedGpu>,
    settings: Arc<CapturerSettings>,
    monitor_bounds: ScreenRect,
    scale_factor: f32,
    refresh_hz: f32,
    initial_mouse: ScreenPointF,
    first_frame_barrier: Arc<Barrier>,
) -> WindowHandle {
    let (tx, rx) = mpsc::channel();
    let initial_size = window.inner_size();
    let thread_name = format!("render-{:?}", window.id());
    let thread = thread::Builder::new()
        .name(thread_name)
        .spawn(move || {
            render_thread_main(
                surface,
                gpu,
                settings,
                rx,
                initial_size,
                monitor_bounds,
                scale_factor,
                refresh_hz,
                initial_mouse,
                first_frame_barrier,
            );
        })
        .expect("spawn render thread");
    WindowHandle {
        window,
        monitor_bounds,
        tx,
        thread: Some(thread),
    }
}

fn render_thread_main(
    surface: wgpu::Surface<'static>,
    gpu: Arc<SharedGpu>,
    settings: Arc<CapturerSettings>,
    rx: mpsc::Receiver<RenderMsg>,
    size: PhysicalSize<u32>,
    monitor_bounds: ScreenRect,
    scale_factor: f32,
    refresh_hz: f32,
    initial_mouse: ScreenPointF,
    first_frame_barrier: Arc<Barrier>,
) {
    // `refresh_hz` is accepted (and logged) for future use — DXGI's waitable
    // object handles the actual pacing, so we don't need the value for timing.
    // If we ever port to a non-DX12 backend we'll need an explicit sleep
    // fallback here that uses it.
    let _ = refresh_hz;

    let mut config = wgpu::SurfaceConfiguration {
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
        format: gpu.surface_format,
        width: size.width.max(1),
        height: size.height.max(1),
        // Fifo + DX12 waitable gives vsynced presentation while
        // get_current_texture() wakes us right before the next scanout.
        present_mode: wgpu::PresentMode::Fifo,
        alpha_mode: wgpu::CompositeAlphaMode::Auto,
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
        let base_uv_offset_scale = [
            (m_x - vd_x) / vd_w,
            (m_y - vd_y) / vd_h,
            m_w / vd_w,
            m_h / vd_h,
        ];

        // Seed the cursor position for the very first (frame-0) draw, so
        // the crosshair appears at the correct spot the instant the window
        // becomes visible rather than briefly snapping from the top-left.
        // The value was sampled by the main thread *before* any window
        // existed; from here on the position arrives via RenderMsg::MouseState.
        let init_local_x = initial_mouse.x - monitor_bounds.min_x() as f32;
        let init_local_y = initial_mouse.y - monitor_bounds.min_y() as f32;

        // params[3] = DPI scale factor. This value is constant for the
        // lifetime of the window (winit delivers ScaleFactorChanged on
        // actual DPI changes, which we don't currently handle), so we
        // only write it here and leave the per-frame path touching
        // params[0..3] for fade + cursor. `crosshair_color` is also
        // immutable from the GPU's POV — copied from settings once at
        // startup, never updated again.
        // Frame-0 uniforms: zoom=1 (which means the folded uv_offset_scale
        // equals the baseline), fade=0, cursor at the pre-window position.
        let uniforms = WindowUniforms {
            uv_offset_scale: base_uv_offset_scale,
            params: [0.0, init_local_x, init_local_y, scale_factor],
            crosshair_color: settings.crosshair_color,
        };

        let ubo = gpu.device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("window uniforms"),
            size: WINDOW_UNIFORMS_SIZE,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        gpu.queue.write_buffer(&ubo, 0, bytemuck::bytes_of(&uniforms));

        let bind_group = gpu.device.create_bind_group(&wgpu::BindGroupDescriptor {
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

    // Frame 0 — the "first render before visible" requirement. Present
    // synchronously, then signal the main thread so it can flip visibility.
    // Fade is 0.0 here so the user's first glimpse is the original colour.
    draw_once(&surface, &gpu, &config, snapshot_state.as_ref());
    first_frame_barrier.wait();

    // Start the animation clock AFTER the barrier. The barrier wait is an
    // unbounded interval (slowest render thread + main-thread set_visible
    // hop), and we don't want any of that to eat into the 500ms budget.
    let start = Instant::now();

    // Latest cursor position (virtual-desktop pixels, f32) and zoom level
    // the main thread has told us about. Seeded from the pre-window
    // snapshot at zoom=1; every subsequent value arrives via
    // RenderMsg::MouseState.
    let mut mouse_pos: ScreenPointF = initial_mouse;
    let mut zoom: f32 = 1.0;

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
                }
                Ok(RenderMsg::MouseState { pos, zoom: z }) => {
                    mouse_pos = pos;
                    zoom = z;
                }
                Ok(RenderMsg::Shutdown) | Err(mpsc::TryRecvError::Disconnected) => return,
                Err(mpsc::TryRecvError::Empty) => break,
            }
        }

        // Update the fade factor and push it to the GPU. Cheap; the
        // staging ring buffer absorbs this without blocking.
        //
        // Ease-out quart (1 - (1 - t)^4): the sweet spot between a
        // mechanical-feeling cubic and the near-instant expo — enough
        // snap at the front that the darken feels like a deliberate
        // gesture, with room in the tail to actually settle into the
        // grayscale rather than slamming into it. If you want to retune:
        //   ease-out cubic:  1 - inv*inv*inv           (gentler snap)
        //   ease-out quint:  1 - inv*inv*inv*inv*inv   (more snap)
        //   ease-out expo:   1 - (-10 * t).exp2()      (near-instant)
        if let Some(state) = snapshot_state.as_mut() {
            let elapsed = start.elapsed().as_secs_f32();
            let t = (elapsed / FADE_DURATION_SECS).clamp(0.0, 1.0);
            let inv = 1.0 - t;
            let fade = 1.0 - inv * inv * inv * inv;
            state.uniforms.params[0] = fade;

            // Cursor in window-local physical pixels (f32 so sub-pixel
            // motion at high zoom is preserved for the UV math below).
            // Out-of-range values (cursor on another monitor) are passed
            // through as-is — the crosshair shader floors to int and then
            // integer-compares, so no line appears on windows that the
            // cursor isn't over. The position came from the main thread
            // via RenderMsg::MouseState and was cached into `mouse_pos`
            // during the drain above.
            let local_x = mouse_pos.x - monitor_bounds.min_x() as f32;
            let local_y = mouse_pos.y - monitor_bounds.min_y() as f32;
            state.uniforms.params[1] = local_x;
            state.uniforms.params[2] = local_y;

            // Fold the zoom-around-cursor transform into uv_offset_scale.
            //
            //   window_uv' = (window_uv - cursor_win_uv) / zoom + cursor_win_uv
            //   vd_uv      = base_offset + window_uv' * base_scale
            //
            //   new_offset = base_offset + base_scale * cursor_win_uv * (1 - 1/zoom)
            //   new_scale  = base_scale / zoom
            //
            // At zoom=1 the formulas degenerate to the baseline, so the
            // zoom=1 path is byte-identical to the pre-zoom build. The
            // shader still computes `vd_uv = offset + window_uv * scale`;
            // all the zoom math lives here on the CPU side.
            //
            // Per the "all monitors zoom uniformly" design: even monitors
            // that don't contain the cursor apply the same transform, so
            // cursor_win_uv can land outside [0, 1] on those. The sampler's
            // ClampToEdge address mode means any off-region sample just
            // hugs the texture edge on those monitors.
            if zoom <= 1.0 {
                state.uniforms.uv_offset_scale = state.base_uv_offset_scale;
            } else {
                let w = config.width as f32;
                let h = config.height as f32;
                let cu = local_x / w;
                let cv = local_y / h;
                let k = 1.0 - 1.0 / zoom;
                let base = state.base_uv_offset_scale;
                state.uniforms.uv_offset_scale = [
                    base[0] + base[2] * cu * k,
                    base[1] + base[3] * cv * k,
                    base[2] / zoom,
                    base[3] / zoom,
                ];
            }

            gpu.queue
                .write_buffer(&state.ubo, 0, bytemuck::bytes_of(&state.uniforms));
        }

        // This call blocks on the DXGI waitable object (because Backends::DX12
        // + Dx12UseFrameLatencyWaitableObject::Wait). We wake up ~one frame
        // before the next scanout — the last reasonable moment to begin
        // rendering before we must hand a frame to the compositor.
        draw_once(&surface, &gpu, &config, snapshot_state.as_ref());
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

fn draw_once(
    surface: &wgpu::Surface<'static>,
    gpu: &SharedGpu,
    config: &wgpu::SurfaceConfiguration,
    snapshot_state: Option<&SnapshotState>,
) {
    let frame = match surface.get_current_texture() {
        wgpu::CurrentSurfaceTexture::Success(f) | wgpu::CurrentSurfaceTexture::Suboptimal(f) => f,
        wgpu::CurrentSurfaceTexture::Timeout | wgpu::CurrentSurfaceTexture::Occluded => return,
        wgpu::CurrentSurfaceTexture::Outdated | wgpu::CurrentSurfaceTexture::Lost => {
            surface.configure(&gpu.device, config);
            return;
        }
        wgpu::CurrentSurfaceTexture::Validation => return,
    };

    let view = frame
        .texture
        .create_view(&wgpu::TextureViewDescriptor::default());
    let mut encoder = gpu
        .device
        .create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("frame encoder"),
        });
    {
        let mut rpass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("desktop pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: &view,
                resolve_target: None,
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
        // Without a snapshot the pipeline has no bind group layout, so
        // calling `set_pipeline` is fine but we don't issue a draw — the
        // clear colour is what the user sees.
    }

    gpu.queue.submit(std::iter::once(encoder.finish()));
    frame.present();
}
