use std::collections::HashMap;
use std::sync::{Arc, Barrier};

use winit::application::ApplicationHandler;
use winit::dpi::{PhysicalPosition, PhysicalSize};
use winit::event::{ElementState, KeyEvent, WindowEvent};
use winit::event_loop::ActiveEventLoop;
use winit::keyboard::{Key, NamedKey};
use winit::window::{Window, WindowId, WindowLevel};

use crate::gpu::{create_desktop_snapshot, GpuCore, SharedGpu};
use crate::platform;
use crate::system::SystemInterop;
use crate::window_state::{spawn_render_thread, WindowHandle};

#[derive(Default)]
pub struct App {
    gpu: Option<Arc<SharedGpu>>,
    instance: Option<wgpu::Instance>,
    windows: HashMap<WindowId, WindowHandle>,
}

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        // `resumed` can fire more than once on some platforms; only bootstrap once.
        if self.gpu.is_some() {
            return;
        }

        // 1. Capture the virtual desktop FIRST, before any winit window
        //    exists. Hidden windows are not normally composited by DWM, but
        //    capturing before any window creation eliminates the possibility
        //    entirely. The capture is a synchronous Win32 BitBlt; it must
        //    happen on the event loop thread. The returned bytes are raw
        //    BGRA — no CPU swizzle — so this call is essentially BitBlt +
        //    GetDIBits + a single Vec allocation. The bundled `monitors`
        //    field is the topology snapshot taken at the same instant.
        let captured = SystemInterop::capture_desktop();

        if captured.monitors.is_empty() {
            error!("no monitors detected; nothing to render to");
            event_loop.exit();
            return;
        }

        // 2. Create one hidden, borderless window per monitor.
        let mut created: Vec<(Arc<Window>, f32)> = Vec::with_capacity(captured.monitors.len());
        for (i, m) in captured.monitors.iter().enumerate() {
            let width = m.bounds.size.width.max(1) as u32;
            let height = m.bounds.size.height.max(1) as u32;
            let attrs = Window::default_attributes()
                .with_title("clowd capture")
                .with_decorations(false)
                .with_resizable(false)
                .with_visible(false)
                .with_transparent(false)
                .with_active(i == 0)
                .with_window_level(WindowLevel::AlwaysOnTop)
                .with_position(PhysicalPosition::new(m.bounds.origin.x, m.bounds.origin.y))
                .with_inner_size(PhysicalSize::new(width, height));
            match event_loop.create_window(attrs) {
                Ok(w) => {
                    let w = Arc::new(w);
                    platform::apply_capture_window_tweaks(&w);
                    created.push((w, m.refresh_hz));
                }
                Err(e) => error!("failed to create window for monitor {i}: {e:?}"),
            }
        }

        if created.is_empty() {
            error!("no windows created; exiting");
            event_loop.exit();
            return;
        }

        // 3. Bootstrap wgpu core against the first window. We need the
        //    device + queue before we can upload the snapshot, but we can't
        //    build the pipeline yet because its layout depends on whether
        //    the snapshot exists.
        let first_window = created[0].0.clone();
        let core = match pollster::block_on(GpuCore::new(first_window.clone())) {
            Ok(c) => c,
            Err(e) => {
                error!("failed to initialize wgpu: {e:?}");
                event_loop.exit();
                return;
            }
        };

        // 4. Upload the captured desktop into a shared GPU texture. Returns
        //    `None` if the bitmap is larger than the adapter's max 2D
        //    texture dimension — in that case the render threads fall back
        //    to a plain dark clear.
        let snapshot = create_desktop_snapshot(&core.device, &core.queue, &captured);

        // 5. Finalise: build the pipeline using the snapshot's bind group
        //    layout (or no bind groups in the fallback path).
        let bootstrap = core.finalize(snapshot);

        // 6. Build surfaces for windows 1..N on the main thread.
        //    wgpu's raw-window-handle retrieval happens here, still on the
        //    thread that owns the Window.
        let mut per_window: Vec<(Arc<Window>, wgpu::Surface<'static>, f32)> =
            Vec::with_capacity(created.len());
        per_window.push((first_window.clone(), bootstrap.first_surface, created[0].1));
        for (w, hz) in created.iter().skip(1) {
            match bootstrap.instance.create_surface(w.clone()) {
                Ok(s) => per_window.push((w.clone(), s, *hz)),
                Err(e) => error!("failed to create surface for extra window: {e:?}"),
            }
        }

        // 7. Spawn render threads behind a Barrier so the main thread waits
        //    until every swapchain has a valid first frame before any window
        //    is flipped visible. Each thread receives its monitor's bounds
        //    so it can compute its slice of the shared snapshot texture.
        //
        //    `captured.monitors[i]`, `created[i]`, and `per_window[i]` are
        //    aligned by construction (we built each list in the same order,
        //    only ever skipping on error), so zipping is safe.
        let barrier = Arc::new(Barrier::new(per_window.len() + 1));
        let mut handles: HashMap<WindowId, WindowHandle> = HashMap::with_capacity(per_window.len());
        for ((w, surface, hz), m) in per_window.into_iter().zip(captured.monitors.iter()) {
            let id = w.id();
            let handle = spawn_render_thread(
                w,
                surface,
                bootstrap.shared.clone(),
                m.bounds,
                hz,
                barrier.clone(),
            );
            handles.insert(id, handle);
        }

        // 8. Wait until every render thread reports "frame 0 done". If any
        //    thread panics before hitting the barrier this would block
        //    forever — but draw_once handles all wgpu errors without
        //    panicking, so that's not a real concern in normal operation.
        barrier.wait();

        // 9. Flip every window visible in one pass, then focus the first.
        //    `first_window` is still in scope from step 2, so we can focus
        //    it directly without round-tripping through the handles map.
        for handle in handles.values() {
            handle.window.set_visible(true);
        }
        first_window.focus_window();

        self.gpu = Some(bootstrap.shared);
        self.instance = Some(bootstrap.instance);
        self.windows = handles;
    }

    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        id: WindowId,
        event: WindowEvent,
    ) {
        let Some(handle) = self.windows.get(&id) else {
            return;
        };

        match event {
            WindowEvent::CloseRequested => {
                event_loop.exit();
            }
            WindowEvent::KeyboardInput {
                event:
                    KeyEvent {
                        state: ElementState::Pressed,
                        logical_key: Key::Named(NamedKey::Escape),
                        ..
                    },
                ..
            } => {
                event_loop.exit();
            }
            WindowEvent::Resized(new_size) => handle.resize(new_size),
            _ => {}
        }
    }
}
