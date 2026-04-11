use std::collections::HashMap;
use std::sync::{Arc, Barrier};

use winit::application::ApplicationHandler;
use winit::dpi::{PhysicalPosition, PhysicalSize};
use winit::event::{ElementState, KeyEvent, MouseButton, MouseScrollDelta, WindowEvent};
use winit::event_loop::ActiveEventLoop;
use winit::keyboard::{Key, NamedKey};
use winit::window::{Window, WindowId, WindowLevel};

use crate::geometry::{ScreenPoint, ScreenPointF, ScreenRect, ScreenRectRounded};
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

/// Virtual-cursor + magnifier + selection state owned by the event-loop
/// thread.
///
/// When `anchored` is false (the zoom=1 case) the OS cursor is authoritative
/// and `virtual_cursor` mirrors it exactly. When `anchored` is true (zoom>1)
/// the real OS cursor is pinned to `anchor` via SetCursorPos; each
/// CursorMoved event instead produces a `(os - anchor) / zoom` delta that
/// advances the virtual cursor in fractional world pixels. See the
/// reference C++ in clowd_capture_dx/Screens.cpp:MouseAnchorStart /
/// MouseAnchorUpdate / MouseAnchorStop for the original design.
///
/// The selection state machine mirrors the C++ `mc_frame_data` flags from
/// DxScreenCapture.cpp directly (mouse_down / dragging / captured) rather
/// than collapsing them into an enum. Visible states:
///   Idle:           !mouse_down && !dragging && !captured
///   Pending-drag:    mouse_down && !dragging && !captured
///   Dragging:        mouse_down &&  dragging && !captured
///   Captured:       !mouse_down && !dragging &&  captured
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
    /// Left mouse button currently held down. Set on Pressed, cleared on
    /// Released. Cleared regardless of whether a drag was actually
    /// promoted, so a click that never crossed the drag threshold is a
    /// no-op overall.
    mouse_down: bool,
    /// `Some(virtual_cursor)` captured at the moment of mouse-down, in
    /// virtual-desktop pixels. The drag rectangle is computed against
    /// this point on every subsequent CursorMoved.
    mouse_down_pt: Option<ScreenPointF>,
    /// DPI scale of the monitor that contained `mouse_down_pt` at the
    /// moment of mouse-down, captured **once** so the drag-distance
    /// threshold doesn't flicker as the cursor crosses display
    /// boundaries during a drag. Falls back to 1.0 if no monitor
    /// contained the press point.
    mouse_down_dpi: f32,
    /// Promoted from `false` to `true` once the rounded selection
    /// width OR height exceeds `6 / (mouse_down_dpi * zoom)` virtual-
    /// desktop pixels — the same threshold as
    /// DxScreenCapture.cpp:1497.
    dragging: bool,
    /// Current selection rectangle in virtual-desktop pixel coordinates,
    /// or `None` if there is no selection (idle, pre-threshold, or
    /// dragged back to a degenerate rect). Updated continuously while
    /// `dragging`; preserved verbatim once `captured`.
    selection: Option<ScreenRect>,
    /// Becomes `true` once the user releases the mouse with a non-empty
    /// selection. While captured the wheel handler is a no-op (matches
    /// DxScreenCapture.cpp:1527) and a fresh mouse-down is ignored —
    /// resize/move of an existing selection is out of scope for v1.
    captured: bool,
}

pub struct App {
    settings: Arc<CapturerSettings>,
    gpu: Option<Arc<SharedGpu>>,
    instance: Option<wgpu::Instance>,
    windows: HashMap<WindowId, WindowHandle>,
    /// Populated once in `resumed()`. Used by `clamp_to_nearest_monitor`
    /// so the virtual cursor can't escape all physical screens while the
    /// OS cursor is pinned to the anchor, and by the mouse-down handler
    /// to find the monitor under the press point for the drag-threshold
    /// DPI lookup.
    monitor_bounds: Vec<ScreenRect>,
    /// Per-monitor DPI scale, parallel-indexed with `monitor_bounds`.
    /// Read at mouse-down to seed `InputState::mouse_down_dpi` so the
    /// drag-distance threshold matches the C++ `dpizoom` factor at
    /// DxScreenCapture.cpp:1497.
    monitor_dpi: Vec<f32>,
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
            monitor_dpi: Vec::new(),
            // Real values are written in `resumed()` once we know where
            // the primary monitor is and where the cursor currently sits.
            // Zero here is a placeholder that never gets broadcast.
            input: InputState {
                virtual_cursor: ScreenPointF::new(0.0, 0.0),
                zoom: 1.0,
                anchored: false,
                anchor: ScreenPoint::new(0, 0),
                mouse_down: false,
                mouse_down_pt: None,
                mouse_down_dpi: 1.0,
                dragging: false,
                selection: None,
                captured: false,
            },
        }
    }

    /// Push the current `(virtual_cursor, zoom, selection, captured)` to
    /// every render thread. Monitors that don't contain the cursor still
    /// need the message so they can apply the zoom transform uniformly
    /// (their crosshair vanishes via the shader's integer-equality miss),
    /// and so each render thread can run its own VD→window-local pixel
    /// transform on the selection rect.
    fn broadcast_mouse_state(&self) {
        for h in self.windows.values() {
            h.update_mouse_state(
                self.input.virtual_cursor,
                self.input.zoom,
                self.input.selection,
                self.input.captured,
            );
        }
    }
}

/// Look up the DPI scale of whichever monitor currently contains `p`,
/// returning `1.0` if the point falls in the multi-monitor dead zone.
/// Used at mouse-down to seed the drag-distance threshold so that
/// dragging across a monitor boundary doesn't change the threshold mid-
/// gesture (matches the C++ behaviour at DxScreenCapture.cpp:1497, which
/// reads `dpizoom` once from the monitor under `mouseDownPt`).
fn dpi_at_point(p: ScreenPointF, monitors: &[ScreenRect], dpis: &[f32]) -> f32 {
    for (i, m) in monitors.iter().enumerate() {
        let mx = m.min_x() as f32;
        let my = m.min_y() as f32;
        let mw = m.width() as f32;
        let mh = m.height() as f32;
        if p.x >= mx && p.x < mx + mw && p.y >= my && p.y < my + mh {
            return dpis.get(i).copied().unwrap_or(1.0);
        }
    }
    1.0
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
            mouse_down: false,
            mouse_down_pt: None,
            mouse_down_dpi: 1.0,
            dragging: false,
            selection: None,
            captured: false,
        };
        self.monitor_bounds = captured.monitors.iter().map(|m| m.bounds).collect();
        self.monitor_dpi = captured.monitors.iter().map(|m| m.scale_factor).collect();

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

                // Drag tracking: if the user is mid-press and hasn't yet
                // finalised a selection, recompute the rounded selection
                // rect against the start point. The drag is "promoted"
                // from pending to active once the rounded width or height
                // exceeds 6 / (dpi * zoom) virtual-desktop pixels — same
                // threshold as DxScreenCapture.cpp:1493-1499. Once active,
                // every cursor move overwrites `selection`. Note that
                // `from_rounded_threshold` returns `None` if the user
                // drags back onto the start pixel — propagating that
                // `None` briefly hides the rect, matching the C++ feel.
                if self.input.mouse_down && !self.input.captured {
                    if let Some(start) = self.input.mouse_down_pt {
                        let psel = ScreenRect::from_rounded_threshold(
                            start.x,
                            start.y,
                            self.input.virtual_cursor.x,
                            self.input.virtual_cursor.y,
                        );
                        if !self.input.dragging {
                            let threshold = 6.0
                                / (self.input.mouse_down_dpi * self.input.zoom);
                            let crossed = psel.map_or(false, |r| {
                                (r.width() as f32) > threshold
                                    || (r.height() as f32) > threshold
                            });
                            if crossed {
                                self.input.dragging = true;
                            }
                        }
                        if self.input.dragging {
                            self.input.selection = psel;
                        }
                    }
                }

                self.broadcast_mouse_state();
            }
            WindowEvent::MouseInput {
                state,
                button: MouseButton::Left,
                ..
            } => {
                match state {
                    ElementState::Pressed => {
                        // Resize/move of an already-finalised selection is
                        // out of scope for v1 — ignore further mouse-downs
                        // once captured (matches the no-op intent of the
                        // wheel handler at DxScreenCapture.cpp:1527).
                        if self.input.captured {
                            return;
                        }
                        self.input.mouse_down = true;
                        self.input.mouse_down_pt = Some(self.input.virtual_cursor);
                        self.input.mouse_down_dpi = dpi_at_point(
                            self.input.virtual_cursor,
                            &self.monitor_bounds,
                            &self.monitor_dpi,
                        );
                        self.input.dragging = false;
                        // Selection itself is left alone here so a single
                        // tap (no drag) doesn't blow away anything that
                        // was pre-painted by the seed path. In practice
                        // it should already be None at this point.
                    }
                    ElementState::Released => {
                        let finalising =
                            self.input.dragging && self.input.selection.is_some();
                        self.input.mouse_down = false;
                        self.input.mouse_down_pt = None;
                        self.input.dragging = false;
                        if finalising {
                            self.input.captured = true;
                            // Snap zoom back to 1 and tear down the
                            // anchor when a selection is finalised
                            // (matches FrameMakeSelection at
                            // DxScreenCapture.cpp:1816). The unanchor
                            // sequence is the mirror of the wheel
                            // handler's MouseAnchorStop branch below:
                            // warp the OS cursor to the virtual cursor
                            // so there's no visual jump.
                            if self.input.anchored {
                                self.input.anchored = false;
                                let restore = ScreenPoint::new(
                                    self.input.virtual_cursor.x.floor() as i32,
                                    self.input.virtual_cursor.y.floor() as i32,
                                );
                                SystemInterop::set_mouse_position(restore);
                            }
                            self.input.zoom = 1.0;
                        }
                        self.broadcast_mouse_state();
                    }
                }
            }
            WindowEvent::MouseWheel { delta, .. } => {
                // After a selection has been finalised the wheel is a
                // no-op — matches DxScreenCapture.cpp:1527's `if
                // (data.captured) return 0;`. While a drag is *in
                // progress* the wheel is allowed: rough-drag, zoom in
                // for precision, refine is a deliberate workflow.
                // The selection lives in virtual-desktop coords, so
                // zooming during a drag re-renders it under the new
                // transform without moving the underlying selected
                // pixels — and the next CursorMoved naturally
                // refreshes the rounded rect against the new zoom.
                if self.input.captured {
                    return;
                }
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
