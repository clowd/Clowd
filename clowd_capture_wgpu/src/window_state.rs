use std::sync::{mpsc, Arc, Barrier};
use std::thread::{self, JoinHandle};
use std::time::Instant;

use winit::dpi::PhysicalSize;
use winit::window::Window;

use crate::geometry::{ScreenPoint, ScreenRect};
use crate::gpu::{SharedGpu, WindowUniforms, WINDOW_UNIFORMS_SIZE};
use crate::settings::CapturerSettings;

/// Duration of the colour → grayscale fade after the window first becomes
/// visible. Tuned to feel "snappy but not snap" — under 300ms reads as a
/// pop, over 700ms feels sluggish.
const FADE_DURATION_SECS: f32 = 0.3;

/// Messages the main thread can send to a render thread.
pub enum RenderMsg {
    Resize(PhysicalSize<u32>),
    /// New cursor position in virtual-desktop screen pixels. The render
    /// thread caches the latest value into a local and uses it on the next
    /// frame; the channel is the only synchronisation we need.
    MousePos(ScreenPoint),
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

    pub fn update_mouse(&self, pos: ScreenPoint) {
        let _ = self.tx.send(RenderMsg::MousePos(pos));
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
/// pixels) sampled by the main thread *before* any window was created,
/// so the very first frame can render the crosshair at the correct spot
/// without ever querying the OS from the render thread.
pub fn spawn_render_thread(
    window: Arc<Window>,
    surface: wgpu::Surface<'static>,
    gpu: Arc<SharedGpu>,
    settings: Arc<CapturerSettings>,
    monitor_bounds: ScreenRect,
    scale_factor: f32,
    refresh_hz: f32,
    initial_mouse: ScreenPoint,
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
    initial_mouse: ScreenPoint,
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
        // primary) work without underflow.
        let m_x = monitor_bounds.min_x() as f32;
        let m_y = monitor_bounds.min_y() as f32;
        let m_w = monitor_bounds.width() as f32;
        let m_h = monitor_bounds.height() as f32;
        let vd_x = snap.vdesktop_origin[0];
        let vd_y = snap.vdesktop_origin[1];
        let vd_w = snap.vdesktop_size[0];
        let vd_h = snap.vdesktop_size[1];
        let uv_offset_scale = [
            (m_x - vd_x) / vd_w,
            (m_y - vd_y) / vd_h,
            m_w / vd_w,
            m_h / vd_h,
        ];

        // Seed the cursor position for the very first (frame-0) draw, so
        // the crosshair appears at the correct spot the instant the window
        // becomes visible rather than briefly snapping from the top-left.
        // The value was sampled by the main thread *before* any window
        // existed; from here on the position arrives via RenderMsg::MousePos.
        let init_local_x = (initial_mouse.x - monitor_bounds.min_x()) as f32;
        let init_local_y = (initial_mouse.y - monitor_bounds.min_y()) as f32;

        // params[3] = DPI scale factor. This value is constant for the
        // lifetime of the window (winit delivers ScaleFactorChanged on
        // actual DPI changes, which we don't currently handle), so we
        // only write it here and leave the per-frame path touching
        // params[0..3] for fade + cursor. `crosshair_color` is also
        // immutable from the GPU's POV — copied from settings once at
        // startup, never updated again.
        let uniforms = WindowUniforms {
            uv_offset_scale,
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

    // Latest cursor position the main thread has told us about, in
    // virtual-desktop screen pixels. Seeded from the pre-window snapshot;
    // every subsequent value arrives via RenderMsg::MousePos.
    let mut mouse_pos: ScreenPoint = initial_mouse;

    loop {
        // Drain *all* pending commands non-blockingly before the blocking
        // present. A burst of MousePos events from a fast mouse must collapse
        // to the latest in a single frame, not lag N frames behind. Shutdown
        // propagates within at most one frame.
        loop {
            match rx.try_recv() {
                Ok(RenderMsg::Resize(new_size)) => {
                    config.width = new_size.width.max(1);
                    config.height = new_size.height.max(1);
                    surface.configure(&gpu.device, &config);
                }
                Ok(RenderMsg::MousePos(p)) => {
                    mouse_pos = p;
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

            // Cursor in window-local physical pixels. Out-of-range values
            // (cursor on another monitor) are passed through as-is — the
            // shader's integer-equality test cannot match any framebuffer
            // pixel index, so the line silently vanishes on this window.
            // The position came from the main thread via RenderMsg::MousePos
            // and was cached into `mouse_pos` during the drain above; we
            // already write the uniform every frame for the fade, so adding
            // these two i32 subtractions is free.
            let local_x = mouse_pos.x - monitor_bounds.min_x();
            let local_y = mouse_pos.y - monitor_bounds.min_y();
            state.uniforms.params[1] = local_x as f32;
            state.uniforms.params[2] = local_y as f32;

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
