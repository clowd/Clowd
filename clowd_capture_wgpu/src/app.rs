use std::collections::HashMap;
use std::sync::Arc;

use winit::application::ApplicationHandler;
use winit::dpi::{PhysicalPosition, PhysicalSize};
use winit::event::{ElementState, KeyEvent, WindowEvent};
use winit::event_loop::ActiveEventLoop;
use winit::keyboard::{Key, NamedKey};
use winit::window::{Window, WindowId};

use crate::gpu::GpuContext;
use crate::platform;
use crate::system::SystemInterop;
use crate::window_state::{RenderOutcome, WindowState};

#[derive(Default)]
pub struct App {
    gpu: Option<GpuContext>,
    windows: HashMap<WindowId, WindowState>,
}

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        // `resumed` can fire more than once on some platforms; only bootstrap once.
        if self.gpu.is_some() {
            return;
        }

        let monitors = SystemInterop::all_monitor_bounds();
        if monitors.is_empty() {
            error!("no monitors detected; nothing to render to");
            event_loop.exit();
            return;
        }

        // 1. Create one hidden, borderless window per monitor.
        let mut raw_windows: Vec<Arc<Window>> = Vec::with_capacity(monitors.len());
        for (i, (bounds, _scale, _primary)) in monitors.iter().enumerate() {
            let width = bounds.size.width.max(1) as u32;
            let height = bounds.size.height.max(1) as u32;
            let attrs = Window::default_attributes()
                .with_title("clowd capture")
                .with_decorations(false)
                .with_resizable(false)
                .with_visible(false)
                .with_transparent(false)
                .with_active(i == 0)
                .with_position(PhysicalPosition::new(bounds.origin.x, bounds.origin.y))
                .with_inner_size(PhysicalSize::new(width, height));
            let window = match event_loop.create_window(attrs) {
                Ok(w) => Arc::new(w),
                Err(e) => {
                    error!("failed to create window for monitor {i}: {e:?}");
                    continue;
                }
            };
            platform::apply_capture_window_tweaks(&window);
            raw_windows.push(window);
        }

        if raw_windows.is_empty() {
            error!("no windows created; exiting");
            event_loop.exit();
            return;
        }

        // 2. Bootstrap wgpu using the first window's surface as the compatible target.
        let first = raw_windows[0].clone();
        let (gpu, first_surface) = match pollster::block_on(GpuContext::new(first.clone())) {
            Ok(pair) => pair,
            Err(e) => {
                error!("failed to initialize wgpu: {e:?}");
                event_loop.exit();
                return;
            }
        };

        // 3. Build per-window state. First window reuses the surface we already created.
        let mut windows: HashMap<WindowId, WindowState> = HashMap::new();
        let first_id = first.id();
        windows.insert(first_id, WindowState::new(first, first_surface, &gpu));

        for w in raw_windows.iter().skip(1) {
            let surface = match gpu.instance.create_surface(w.clone()) {
                Ok(s) => s,
                Err(e) => {
                    error!("failed to create surface for extra window: {e:?}");
                    continue;
                }
            };
            windows.insert(w.id(), WindowState::new(w.clone(), surface, &gpu));
        }

        // 4. Render one frame into every window BEFORE any of them become visible.
        //    render() submits + presents synchronously, so by the time the loop ends
        //    every swapchain has a valid first frame.
        for state in windows.values_mut() {
            match state.render(&gpu) {
                RenderOutcome::Presented => {}
                RenderOutcome::NeedsReconfigure => {
                    state.reconfigure(&gpu);
                    let _ = state.render(&gpu);
                }
                RenderOutcome::Skipped => {
                    warn!("initial frame skipped by compositor");
                }
            }
        }

        // 5. Flip every window visible in one pass, then focus the first.
        for state in windows.values() {
            state.window.set_visible(true);
        }
        if let Some(first_state) = windows.get(&first_id) {
            first_state.window.focus_window();
        }

        self.gpu = Some(gpu);
        self.windows = windows;
    }

    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        id: WindowId,
        event: WindowEvent,
    ) {
        let Some(gpu) = self.gpu.as_ref() else {
            return;
        };
        let Some(state) = self.windows.get_mut(&id) else {
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
            WindowEvent::Resized(new_size) => state.resize(gpu, new_size),
            WindowEvent::RedrawRequested => match state.render(gpu) {
                RenderOutcome::Presented | RenderOutcome::Skipped => {}
                RenderOutcome::NeedsReconfigure => {
                    state.reconfigure(gpu);
                    state.window.request_redraw();
                }
            },
            _ => {}
        }
    }
}
