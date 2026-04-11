use std::sync::{mpsc, Arc, Barrier};
use std::thread::{self, JoinHandle};

use winit::dpi::PhysicalSize;
use winit::window::Window;

use crate::gpu::SharedGpu;

/// Messages the main thread can send to a render thread.
pub enum RenderMsg {
    Resize(PhysicalSize<u32>),
    Shutdown,
}

/// Handle to a render thread, held by the main thread. Dropping it sends a
/// `Shutdown` on the channel and joins the thread, so ending the event loop
/// (and thus dropping the App's HashMap) is enough to tear everything down
/// cleanly.
pub struct WindowHandle {
    pub window: Arc<Window>,
    tx: mpsc::Sender<RenderMsg>,
    thread: Option<JoinHandle<()>>,
}

impl WindowHandle {
    pub fn resize(&self, size: PhysicalSize<u32>) {
        let _ = self.tx.send(RenderMsg::Resize(size));
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
pub fn spawn_render_thread(
    window: Arc<Window>,
    surface: wgpu::Surface<'static>,
    gpu: Arc<SharedGpu>,
    refresh_hz: f32,
    first_frame_barrier: Arc<Barrier>,
) -> WindowHandle {
    let (tx, rx) = mpsc::channel();
    let initial_size = window.inner_size();
    let thread_name = format!("render-{:?}", window.id());
    let thread = thread::Builder::new()
        .name(thread_name)
        .spawn(move || {
            render_thread_main(surface, gpu, rx, initial_size, refresh_hz, first_frame_barrier);
        })
        .expect("spawn render thread");
    WindowHandle {
        window,
        tx,
        thread: Some(thread),
    }
}

fn render_thread_main(
    surface: wgpu::Surface<'static>,
    gpu: Arc<SharedGpu>,
    rx: mpsc::Receiver<RenderMsg>,
    size: PhysicalSize<u32>,
    refresh_hz: f32,
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

    // Frame 0 — the "first render before visible" requirement. Present
    // synchronously, then signal the main thread so it can flip visibility.
    draw_once(&surface, &gpu, &config);
    first_frame_barrier.wait();

    loop {
        // Drain any pending commands non-blockingly before the blocking
        // present. A Shutdown propagates within at most one frame.
        match rx.try_recv() {
            Ok(RenderMsg::Resize(new_size)) => {
                config.width = new_size.width.max(1);
                config.height = new_size.height.max(1);
                surface.configure(&gpu.device, &config);
            }
            Ok(RenderMsg::Shutdown) | Err(mpsc::TryRecvError::Disconnected) => return,
            Err(mpsc::TryRecvError::Empty) => {}
        }

        // This call blocks on the DXGI waitable object (because Backends::DX12
        // + Dx12UseFrameLatencyWaitableObject::Wait). We wake up ~one frame
        // before the next scanout — the last reasonable moment to begin
        // rendering before we must hand a frame to the compositor.
        draw_once(&surface, &gpu, &config);
    }
}

fn draw_once(
    surface: &wgpu::Surface<'static>,
    gpu: &SharedGpu,
    config: &wgpu::SurfaceConfiguration,
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
            label: Some("triangle pass"),
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
            timestamp_writes: None,
            occlusion_query_set: None,
            multiview_mask: None,
        });
        rpass.set_pipeline(&gpu.pipeline);
        rpass.draw(0..3, 0..1);
    }

    gpu.queue.submit(std::iter::once(encoder.finish()));
    frame.present();
}
