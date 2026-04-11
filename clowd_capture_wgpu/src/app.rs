use std::collections::HashMap;
use std::sync::{Arc, Barrier};

use winit::application::ApplicationHandler;
use winit::dpi::{PhysicalPosition, PhysicalSize};
use winit::event::{ElementState, KeyEvent, MouseScrollDelta, WindowEvent};
use winit::event_loop::ActiveEventLoop;
use winit::keyboard::{Key, NamedKey};
use winit::window::{Window, WindowId, WindowLevel};

use crate::geometry::{ScreenPoint, ScreenPointF, ScreenRect};
use crate::gpu::{create_desktop_snapshot, GpuCore, SharedGpu};
use crate::platform;
use crate::settings::CapturerSettings;
use crate::system::SystemInterop;
use crate::window_state::{spawn_render_thread, WindowHandle};

/// Minimum zoom. The magnifier only ever enlarges the source.
const ZOOM_MIN: f32 = 1.0;
/// Maximum zoom. Matches the original C++ capturer (Screens.cpp /
/// DxScreenCapture.cpp — `min(max(zoom, 1), 256)`).
const ZOOM_MAX: f32 = 256.0;
/// Multiplicative step per wheel tick. Coarse by design — no modifier-key
/// fine-grained step in v1.
const ZOOM_STEP: f32 = 2.0;

/// Virtual-cursor + magnifier state owned by the event-loop thread.
///
/// When `anchored` is false (the zoom=1 case) the OS cursor is authoritative
/// and `virtual_cursor` mirrors it exactly. When `anchored` is true (zoom>1)
/// the real OS cursor is pinned to `anchor` via SetCursorPos; each
/// CursorMoved event instead produces a `(os - anchor) / zoom` delta that
/// advances the virtual cursor in fractional world pixels. See the
/// reference C++ in clowd_capture_dx/Screens.cpp:MouseAnchorStart /
/// MouseAnchorUpdate / MouseAnchorStop for the original design.
struct InputState {
    /// The logical cursor in virtual-desktop pixels. Always-live: even at
    /// zoom=1 we keep it updated so the zoom-in transition doesn't need
    /// a special "sample the OS cursor" step.
    virtual_cursor: ScreenPointF,
    /// Magnifier scale in [ZOOM_MIN, ZOOM_MAX]. 1.0 = unzoomed (no anchor).
    zoom: f32,
    /// Whether the OS cursor is currently pinned to `anchor`. Implied by
    /// `zoom > 1`, but tracked explicitly so the start/stop transitions
    /// (which need a single SetCursorPos warp) are driven by the wheel
    /// handler and not by every CursorMoved event.
    anchored: bool,
    /// Fixed point in virtual-desktop pixels (== real screen coords, since
    /// the origin of the virtual desktop *is* where primary monitor origin
    /// plus screen = real coords match). Computed once at startup as the
    /// centre of the primary monitor, per Screens.cpp:111-114.
    anchor: ScreenPoint,
}

pub struct App {
    settings: Arc<CapturerSettings>,
    gpu: Option<Arc<SharedGpu>>,
    instance: Option<wgpu::Instance>,
    windows: HashMap<WindowId, WindowHandle>,
    /// Populated once in `resumed()`. Used by `clamp_to_nearest_monitor`
    /// so the virtual cursor can't escape all physical screens while the
    /// OS cursor is pinned to the anchor.
    monitor_bounds: Vec<ScreenRect>,
    input: InputState,
}

impl App {
    pub fn new(settings: Arc<CapturerSettings>) -> Self {
        Self {
            settings,
            gpu: None,
            instance: None,
            windows: HashMap::new(),
            monitor_bounds: Vec::new(),
            // Real values are written in `resumed()` once we know where
            // the primary monitor is and where the cursor currently sits.
            // Zero here is a placeholder that never gets broadcast.
            input: InputState {
                virtual_cursor: ScreenPointF::new(0.0, 0.0),
                zoom: 1.0,
                anchored: false,
                anchor: ScreenPoint::new(0, 0),
            },
        }
    }

    /// Push the current `(virtual_cursor, zoom)` to every render thread.
    /// Monitors that don't contain the cursor still need the message so
    /// they can apply the zoom transform uniformly (their crosshair
    /// vanishes via the shader's integer-equality miss).
    fn broadcast_mouse_state(&self) {
        for h in self.windows.values() {
            h.update_mouse_state(self.input.virtual_cursor, self.input.zoom);
        }
    }
}

/// Clamp the point to whichever monitor currently contains it, or to the
/// nearest monitor if it's in the multi-monitor dead zone. A tiny epsilon
/// is subtracted from the max edge so the point never lands on the
/// exclusive right/bottom of a rect (matches Screens.cpp:147-153 which
/// subtracts 0.001 from the right/bottom bounds).
fn clamp_to_nearest_monitor(p: &mut ScreenPointF, monitors: &[ScreenRect]) {
    if monitors.is_empty() {
        return;
    }
    // First try: already inside a monitor? Leave it alone.
    for m in monitors {
        let mx = m.min_x() as f32;
        let my = m.min_y() as f32;
        let mw = m.width() as f32;
        let mh = m.height() as f32;
        if p.x >= mx && p.x < mx + mw && p.y >= my && p.y < my + mh {
            return;
        }
    }
    // Not inside any monitor — pick the one whose centre is closest and
    // clamp into its bounds. Distance-to-centre is good enough as a
    // "nearest monitor" heuristic for a cursor that's just crossed a
    // boundary.
    let (best_ix, _) = monitors
        .iter()
        .enumerate()
        .map(|(i, m)| {
            let cx = m.min_x() as f32 + m.width() as f32 * 0.5;
            let cy = m.min_y() as f32 + m.height() as f32 * 0.5;
            let dx = p.x - cx;
            let dy = p.y - cy;
            (i, dx * dx + dy * dy)
        })
        .fold((0usize, f32::INFINITY), |(bi, bd), (i, d)| {
            if d < bd {
                (i, d)
            } else {
                (bi, bd)
            }
        });
    let m = &monitors[best_ix];
    let min_x = m.min_x() as f32;
    let min_y = m.min_y() as f32;
    let max_x = (m.min_x() + m.width()) as f32 - 0.001;
    let max_y = (m.min_y() + m.height()) as f32 - 0.001;
    p.x = p.x.clamp(min_x, max_x);
    p.y = p.y.clamp(min_y, max_y);
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

        // Snapshot the cursor position before any of our windows exist, so
        // each render thread can seed its frame-0 crosshair uniform without
        // ever having to query the OS itself. After the windows are up the
        // main thread keeps every render thread in sync by translating
        // WindowEvent::CursorMoved into RenderMsg::MouseState.
        let initial_mouse = SystemInterop::get_mouse_position();
        let initial_mouse_f =
            ScreenPointF::new(initial_mouse.x as f32, initial_mouse.y as f32);

        // Populate the InputState we drive the zoom + virtual cursor from.
        // The anchor is the real-screen centre of the primary monitor (per
        // Screens.cpp:111-114) — the fixed point we warp the OS cursor to
        // while anchored. Falls back to the first monitor if no primary
        // is flagged, which should never happen but is cheap to guard.
        let primary = captured
            .monitors
            .iter()
            .find(|m| m.is_primary)
            .or_else(|| captured.monitors.first())
            .expect("at least one monitor present");
        let anchor = ScreenPoint::new(
            primary.bounds.min_x() + (primary.bounds.width() / 2),
            primary.bounds.min_y() + (primary.bounds.height() / 2),
        );
        self.input = InputState {
            virtual_cursor: initial_mouse_f,
            zoom: 1.0,
            anchored: false,
            anchor,
        };
        self.monitor_bounds = captured.monitors.iter().map(|m| m.bounds).collect();

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
                self.settings.clone(),
                m.bounds,
                m.scale_factor,
                hz,
                initial_mouse_f,
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
            WindowEvent::CursorMoved { position, .. } => {
                // winit hands us a position in this window's local physical
                // pixels. Reconstruct the OS cursor in virtual-desktop
                // coords so we can compare against the anchor (itself in
                // virtual-desktop coords).
                let bounds = handle.monitor_bounds;
                let os_vd = ScreenPoint::new(
                    bounds.min_x() + position.x.round() as i32,
                    bounds.min_y() + position.y.round() as i32,
                );

                if self.input.anchored {
                    // Feedback-loop guard: our own SetCursorPos(anchor)
                    // below will trigger a CursorMoved event back at the
                    // anchor. Skip it so we don't re-apply a zero delta
                    // and (worse) re-warp mid-frame. Matches
                    // Screens.cpp:IsAnchorPt + DxScreenCapture.cpp:1389.
                    if os_vd == self.input.anchor {
                        return;
                    }
                    let zoom = self.input.zoom;
                    let dx = (os_vd.x - self.input.anchor.x) as f32 / zoom;
                    let dy = (os_vd.y - self.input.anchor.y) as f32 / zoom;
                    self.input.virtual_cursor.x += dx;
                    self.input.virtual_cursor.y += dy;
                    clamp_to_nearest_monitor(
                        &mut self.input.virtual_cursor,
                        &self.monitor_bounds,
                    );
                    SystemInterop::set_mouse_position(self.input.anchor);
                } else {
                    // Unanchored (zoom == 1): the OS cursor is truth. We
                    // still keep `virtual_cursor` updated so a subsequent
                    // zoom-in transition doesn't need a GetCursorPos.
                    self.input.virtual_cursor =
                        ScreenPointF::new(os_vd.x as f32, os_vd.y as f32);
                }

                self.broadcast_mouse_state();
            }
            WindowEvent::MouseWheel { delta, .. } => {
                // Normalise the two winit delta variants into a single
                // scalar "step" whose sign is all we care about for a
                // coarse ×2/÷2 zoom. LineDelta is the desktop-mouse case;
                // PixelDelta comes from touchpads in physical pixels and
                // needs taming so one scroll gesture isn't twenty zoom
                // steps.
                let step = match delta {
                    MouseScrollDelta::LineDelta(_, y) => y,
                    MouseScrollDelta::PixelDelta(p) => (p.y / 50.0) as f32,
                };
                if step == 0.0 {
                    return;
                }

                let new_zoom = if step > 0.0 {
                    self.input.zoom * ZOOM_STEP
                } else {
                    self.input.zoom / ZOOM_STEP
                };
                let new_zoom = new_zoom.clamp(ZOOM_MIN, ZOOM_MAX);
                if (new_zoom - self.input.zoom).abs() < f32::EPSILON {
                    return;
                }

                let was_anchored = self.input.anchored;
                let will_anchor = new_zoom > 1.0;

                if !was_anchored && will_anchor {
                    // MouseAnchorStart (Screens.cpp:130-137): pin the OS
                    // cursor to the anchor. `virtual_cursor` already tracks
                    // the current cursor position, so the zoom appears
                    // centered on wherever the user was pointing.
                    self.input.anchored = true;
                    SystemInterop::set_mouse_position(self.input.anchor);
                } else if was_anchored && !will_anchor {
                    // MouseAnchorStop (Screens.cpp:161-167): un-pin by
                    // moving the real OS cursor to where the virtual
                    // cursor currently sits — no visual jump because the
                    // virtual cursor was what the user saw.
                    self.input.anchored = false;
                    let restore = ScreenPoint::new(
                        self.input.virtual_cursor.x.floor() as i32,
                        self.input.virtual_cursor.y.floor() as i32,
                    );
                    SystemInterop::set_mouse_position(restore);
                }

                self.input.zoom = new_zoom;
                self.broadcast_mouse_state();
            }
            _ => {}
        }
    }
}
