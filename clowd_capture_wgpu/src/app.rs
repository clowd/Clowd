use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Barrier};

use winit::application::ApplicationHandler;
use winit::dpi::{PhysicalPosition, PhysicalSize};
use winit::event::{ElementState, KeyEvent, MouseButton, MouseScrollDelta, WindowEvent};
use winit::event_loop::ActiveEventLoop;
use winit::keyboard::{Key, NamedKey};
#[cfg(windows)]
use winit::platform::windows::WindowAttributesExtWindows;
use winit::window::{CursorIcon, Window, WindowId};

use crate::geometry::{ScreenPoint, ScreenPointF, ScreenRect, ScreenRectRounded};
use crate::img::{self, ActionResult};
use crate::gpu::bootstrap_window_gpu;
use crate::ui::command::Command;
use crate::ui::components::panel::{self, ButtonPanelComponent};
use crate::ui::components::tips::TipsPanelComponent;
use crate::platform;
use crate::selection::{
    clamp_to_nearest_monitor, dpi_at_point, hit_test, move_and_crop, resize_with_clamp, DragMode,
    Hittest,
};
use crate::settings::CapturerSettings;
use crate::system::{CapturedDesktop, SystemInterop, WindowWalker};
use crate::render::{spawn_render_thread, RenderThreadParams, WindowHandle};
use crate::ui::component::{AppContext, CursorHint, MonitorInfo, MouseEvent};
use crate::ui::host::ComponentHost;

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
    /// Whether the OS cursor is currently pinned to `anchor`. Once set
    /// (when zoom first exceeds 1.0), persists until capture finalization
    /// regardless of zoom level — "sticky" virtual-cursor mode avoids
    /// glitchy physical↔virtual bouncing on rapid zoom changes.
    anchored: bool,
    /// Set to `true` when `anchored` transitions from false to true.
    /// The CursorMoved handler uses this to discard stale OS cursor
    /// events that arrive before the SetCursorPos warp takes effect.
    /// Cleared once a non-stale CursorMoved processes in anchored mode.
    anchor_just_engaged: bool,
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
    /// DxScreenCapture.cpp:1527), the rendered crosshair is suppressed,
    /// and the cursor switches between resize/move icons based on the
    /// per-frame `hittest`.
    captured: bool,
    /// Latest hit test result against the captured selection rect.
    /// Refreshed on every CursorMoved while `captured`. Determines the
    /// cursor icon (via `Hittest::cursor`) and is the seed for the
    /// drag mode entered on the next mouse-down. Always `Outside`
    /// before capture and reset on un-capture.
    hittest: Hittest,
    /// What kind of drag is currently in progress (only meaningful
    /// while `captured && mouse_down`). `None` between drags.
    drag_mode: Option<DragMode>,
    /// The selection rect at the moment the current drag started, in
    /// virtual-desktop pixel coords. Both `Move` and `Resize` use this
    /// as the anchor against which `(cursor - mouse_down_pt)` deltas
    /// are applied — using a frozen anchor instead of incrementally
    /// updating the rect avoids drift and gives the soft-clamp
    /// "snap back when cursor returns into bounds" behaviour the user
    /// asked for.
    drag_anchor_selection: Option<ScreenRect>,
    /// Whether the Tips & Hotkeys overlay is currently displayed. Toggled
    /// by the T key (`DxScreenCapture.cpp:1248-1251`). Defaults to `true`.
    tips_visible: bool,
}

pub struct App {
    settings: Arc<CapturerSettings>,
    windows: HashMap<WindowId, WindowHandle>,
    /// Stable, index-order list of window IDs parallel to `monitors`.
    /// Populated in `resumed()` in the same order windows are created;
    /// used by the panel plumbing to look up the `WindowHandle` for a
    /// given monitor index.
    monitor_window_ids: Vec<WindowId>,
    /// Populated once in `resumed()`. Used by `clamp_to_nearest_monitor`
    /// so the virtual cursor can't escape all physical screens while the
    /// OS cursor is pinned to the anchor, and by the mouse-down handler
    /// to find the monitor under the press point for the drag-threshold
    /// DPI lookup.
    /// Monitor topology snapshot taken at capture startup. Single source
    /// of truth for per-monitor bounds, DPI scale, primary flag, display
    /// name, etc. Ordering is stable from capture-time and is what all
    /// `monitor_idx` indices in `monitor_window_ids`/render threads refer to.
    monitors: Vec<crate::system::MonitorInfo>,
    /// Scratch storage for the window title reported by
    /// `WindowWalker::hit_test_with_title` — kept on `self` so
    /// `sync_components` can lend it out as `&str` through `AppContext`.
    cached_hovered_title: Option<String>,
    /// Union rect of the virtual desktop in physical pixels — same value
    /// as `captured.bounds` from the startup snapshot. Used to soft-
    /// clamp the selection during move/resize so it can't be pushed off
    /// screen (matches `WorkspaceBounds()` + `ClipRectBy` at
    /// DxScreenCapture.cpp:1844-1845).
    vd_bounds: ScreenRect,
    input: InputState,
    /// Retained desktop pixel data for Copy/Save operations. Shared
    /// with render threads via Arc (read-only after capture).
    desktop_buffer: Option<Arc<CapturedDesktop>>,
    /// Window walker: pre-detected window rects for click-to-select.
    walker: Option<WindowWalker>,
    /// Generic UI component manager. Owns all registered UI components
    /// (button panel today; future: color picker, zoom readout, …).
    /// `App` does not know what components exist — it only pushes
    /// `AppContext` via `sync_components` and dispatches actions.
    component_host: ComponentHost,
    /// macOS: render threads increment this counter after frame 0.
    /// `about_to_wait` snaps alpha to 1 once all threads have reported.
    /// `None` after the reveal is complete (or on Windows where it's unused).
    pending_show: Option<PendingShow>,
}

struct PendingShow {
    ready_count: Arc<AtomicUsize>,
    expected: usize,
    visible_barrier: Arc<Barrier>,
}

impl App {
    pub fn new(settings: Arc<CapturerSettings>) -> Self {
        // Register every component once at startup. The host drives their
        // visibility from `AppContext` on each `sync_components` call.
        // Adding a new component = one more `component_host.add(...)` line.
        let mut component_host = ComponentHost::new();
        component_host.add(ButtonPanelComponent::new());
        component_host.add(TipsPanelComponent::new());

        Self {
            settings,
            windows: HashMap::new(),
            monitor_window_ids: Vec::new(),
            monitors: Vec::new(),
            cached_hovered_title: None,
            vd_bounds: ScreenRect::zero(),
            desktop_buffer: None,
            walker: None,
            component_host,
            pending_show: None,
            // Real values are written in `resumed()` once we know where
            // the primary monitor is and where the cursor currently sits.
            // Zero here is a placeholder that never gets broadcast.
            input: InputState {
                virtual_cursor: ScreenPointF::new(0.0, 0.0),
                zoom: 1.0,
                anchored: false,
                anchor_just_engaged: false,
                anchor: ScreenPoint::new(0, 0),
                mouse_down: false,
                mouse_down_pt: None,
                mouse_down_dpi: 1.0,
                dragging: false,
                selection: None,
                captured: false,
                hittest: Hittest::Outside,
                drag_mode: None,
                drag_anchor_selection: None,
                tips_visible: true,
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

    /// Hide all capture windows.
    fn hide_all_windows(&self) {
        for h in self.windows.values() {
            h.window.set_visible(false);
        }
    }

    /// Show all capture windows.
    fn show_all_windows(&self) {
        for h in self.windows.values() {
            h.window.set_visible(true);
        }
    }

    /// Push the latest app state to all registered components. Each
    /// component re-evaluates its placement and re-bakes as needed,
    /// and the host ships snapshots to the appropriate render threads.
    ///
    /// This is the *only* mechanism by which components observe app-level
    /// state. If a new component needs new data to decide its layout,
    /// add a field to `AppContext` here and populate it below.
    fn sync_components(&mut self) {
        let ui_monitors: Vec<MonitorInfo> = self
            .monitors
            .iter()
            .map(|m| MonitorInfo {
                bounds: m.bounds,
                dpi_scale: m.scale_factor,
            })
            .collect();

        let cursor = self.input.virtual_cursor;
        let cursor_pt = ScreenPoint::new(cursor.x.round() as i32, cursor.y.round() as i32);

        let primary_monitor_idx = self.monitors.iter().position(|m| m.is_primary);

        let hovered_monitor_name = self
            .monitors
            .iter()
            .find(|m| m.bounds.contains(cursor_pt))
            .map(|m| m.name.as_str());

        // Top-level window title under the cursor — `None` over the
        // desktop background or before the walker is initialised. Stash
        // the owned String on `self` so we can hand out a borrow that
        // lives as long as `self` during this call.
        self.cached_hovered_title = self
            .walker
            .as_ref()
            .and_then(|w| w.hit_test_with_title(cursor_pt))
            .map(|(_, title)| title);
        let hovered_window_title = self.cached_hovered_title.as_deref();

        // Sample the BGRA pixel under the cursor from the captured desktop.
        let hovered_pixel_bgra = self
            .desktop_buffer
            .as_deref()
            .and_then(|buf| sample_bgra(buf, cursor_pt));

        let ctx = AppContext {
            monitors: &ui_monitors,
            selection: self.input.selection,
            captured: self.input.captured,
            mouse_down: self.input.mouse_down,
            virtual_cursor: self.input.virtual_cursor,
            accent_color: self.settings.crosshair_color,
            primary_monitor_idx,
            tips_visible: self.input.tips_visible,
            hovered_monitor_name,
            hovered_window_title,
            hovered_pixel_bgra,
        };
        self.component_host.sync(
            &ctx,
            &self.windows,
            &self.monitor_window_ids,
        );
    }

    /// Reset the selection and return to draw mode.
    fn handle_reset(&mut self, window: &Window) {
        // Clear selection state
        self.input.selection = None;
        self.input.captured = false;
        self.input.hittest = Hittest::Outside;
        self.input.drag_mode = None;
        self.input.drag_anchor_selection = None;

        // Components observe the cleared state and hide themselves.
        self.sync_components();

        // Reset cursor
        window.set_cursor(CursorIcon::Default);

        // Re-query the walker so the highlight reappears immediately
        // under the cursor without waiting for the next mouse-move.
        let pt = ScreenPoint::new(
            self.input.virtual_cursor.x.round() as i32,
            self.input.virtual_cursor.y.round() as i32,
        );
        self.input.selection = self.walker.as_ref().and_then(|w| w.hit_test(pt));

        // Broadcast cleared state to render threads
        self.broadcast_mouse_state();

        log::info!("selection reset");
    }

    /// Dispatch a `Command` emitted by a component (or by a keyboard
    /// accelerator). The single central place where the app maps the
    /// shared `Command` vocabulary to concrete app-level effects.
    fn dispatch_command(
        &mut self,
        command: Command,
        event_loop: &ActiveEventLoop,
        window: &Window,
    ) {
        use xdialog::XDialogIcon::Error as ErrorIcon;
        log::info!("dispatch command: {:?}", command);
        match command {
            Command::Copy => {
                self.hide_all_windows();
                let result = match (self.input.selection, self.desktop_buffer.as_deref()) {
                    (Some(sel), Some(buf)) => img::copy_to_clipboard(sel, buf),
                    _ => ActionResult::Failed("No selection or buffer".into()),
                };
                match result {
                    ActionResult::Success => event_loop.exit(),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Copy to Clipboard Failed", &msg, ErrorIcon).unwrap_or(false) {
                            self.show_all_windows();
                        } else {
                            event_loop.exit();
                        }
                    }
                }
            }
            Command::Save => {
                self.hide_all_windows();
                let result = match (self.input.selection, self.desktop_buffer.as_deref()) {
                    (Some(sel), Some(buf)) => img::save_to_file(sel, buf, window),
                    _ => ActionResult::Failed("No selection or buffer".into()),
                };
                match result {
                    ActionResult::Success => event_loop.exit(),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Save Failed", &msg, ErrorIcon).unwrap_or(false) {
                            self.show_all_windows();
                        } else {
                            event_loop.exit();
                        }
                    }
                }
            }
            Command::Reset => self.handle_reset(window),
            Command::Exit => event_loop.exit(),
            Command::Upload | Command::Edit | Command::Video => {
                log::info!("command {:?} not yet implemented", command);
            }
        }
    }
}


impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        // `resumed` can fire more than once on some platforms; only bootstrap once.
        if !self.windows.is_empty() {
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
            anchor_just_engaged: false,
            anchor,
            mouse_down: false,
            mouse_down_pt: None,
            mouse_down_dpi: 1.0,
            dragging: false,
            selection: None,
            captured: false,
            hittest: Hittest::Outside,
            drag_mode: None,
            drag_anchor_selection: None,
            tips_visible: true,
        };
        self.monitors = captured.monitors.clone();
        self.vd_bounds = captured.bounds;
        self.monitor_window_ids.clear();

        // Snapshot visible windows for click-to-select. Done after the
        // desktop capture but before our overlay windows are created so
        // our own windows aren't in the enumeration.
        let walker = SystemInterop::snapshot_windows();
        self.input.selection = walker.hit_test(initial_mouse);
        self.walker = Some(walker);

        // 2. Create one borderless window per monitor.
        //    On Windows: hidden (visible=false) — WS_EX_NOREDIRECTIONBITMAP
        //    makes the later set_visible(true) instantaneous.
        //    On macOS: visible but alpha=0 so Metal can render into it
        //    without the hidden→visible compositor lag.
        let mut created: Vec<(Arc<Window>, f32)> = Vec::with_capacity(captured.monitors.len());
        for (i, m) in captured.monitors.iter().enumerate() {
            let width = m.bounds.size.width.max(1) as u32;
            let height = m.bounds.size.height.max(1) as u32;
            #[allow(unused_mut)]
            let mut attrs = Window::default_attributes()
                .with_title("clowd capture")
                .with_decorations(false)
                .with_resizable(false)
                .with_visible(cfg!(target_os = "macos"))
                .with_transparent(false)
                .with_active(i == 0)
                // .with_window_level(winit::window::WindowLevel::AlwaysOnTop)
                .with_position(PhysicalPosition::new(m.bounds.origin.x, m.bounds.origin.y))
                .with_inner_size(PhysicalSize::new(width, height));
            // WS_EX_NOREDIRECTIONBITMAP: tells DWM not to create a redirection
            // surface. Required for proper DXGI flip-model swap chain timing.
            #[cfg(windows)]
            {
                attrs = attrs.with_no_redirection_bitmap(true);
            }
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

        // 3. Wrap the captured desktop in Arc — shared read-only with
        //    GPU bootstrap (for texture upload) and retained by the main
        //    thread for Copy/Save operations. No copies; each consumer
        //    just bumps the refcount.
        let captured = Arc::new(captured);
        self.desktop_buffer = Some(captured.clone());

        // 4. Bootstrap a separate wgpu Instance / Adapter / Device /
        //    Queue per window **on the main thread** (winit's window
        //    handle is only available here). Each window gets its own
        //    DX12 command queue so swap chain presents are fully
        //    independent — the prerequisite for Hardware: Independent
        //    Flip on multi-monitor setups.
        let monitors = &captured.monitors;
        let mut gpu_setups: Vec<_> = Vec::with_capacity(created.len());
        for ((w, _hz), m) in created.iter().zip(monitors.iter()) {
            match bootstrap_window_gpu(w.clone(), &captured, m.adapter_id) {
                Ok(pair) => gpu_setups.push(Some(pair)),
                Err(e) => {
                    error!("failed to bootstrap GPU for monitor {:?}: {e:?}", m.bounds);
                    gpu_setups.push(None);
                }
            }
        }

        // 5. Spawn render threads behind a Barrier so the main thread
        //    waits until every swapchain has a valid first frame before
        //    any window is flipped visible.
        let ok_count = gpu_setups.iter().filter(|s| s.is_some()).count();
        if ok_count == 0 {
            error!("no GPU could be bootstrapped; exiting");
            event_loop.exit();
            return;
        }
        // Atomic counter: each render thread increments after frame 0.
        let ready_count = Arc::new(AtomicUsize::new(0));
        // Visible barrier: render threads wait here until the main thread
        // confirms the windows are on screen, so the fade animation doesn't
        // run while the window is still invisible.
        let visible_barrier = Arc::new(Barrier::new(ok_count + 1));

        let mut handles: HashMap<WindowId, WindowHandle> = HashMap::with_capacity(ok_count);
        for (((w, hz), m), gpu_setup) in created
            .into_iter()
            .zip(monitors.iter())
            .zip(gpu_setups.into_iter())
        {
            let (gpu, surface) = match gpu_setup {
                Some(pair) => pair,
                None => continue,
            };
            let id = w.id();
            let handle = spawn_render_thread(RenderThreadParams {
                window: w,
                surface,
                gpu,
                settings: self.settings.clone(),
                monitor_bounds: m.bounds,
                scale_factor: m.scale_factor,
                refresh_hz: hz,
                initial_mouse: initial_mouse_f,
                ready_count: ready_count.clone(),
                visible_barrier: visible_barrier.clone(),
            });
            handles.insert(id, handle);
            self.monitor_window_ids.push(id);
        }

        // Don't block — return to the event loop immediately. The loop
        // is in Poll mode so `about_to_wait` fires on the next tick,
        // checks if all render threads have finished frame 0, and reveals
        // the windows + releases the visible barrier at that point. This
        // keeps the run loop responsive on all platforms (critical on macOS
        // where blocking prevents visual changes from taking effect).
        self.pending_show = Some(PendingShow {
            ready_count,
            expected: ok_count,
            visible_barrier,
        });

        self.windows = handles;
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        // Check if all render threads have finished frame 0. If so,
        // reveal the windows (set_visible / snap alpha), release the
        // visible barrier so render threads start the colour→grayscale
        // fade, then switch back to Wait mode so the loop idles.
        if let Some(ref pending) = self.pending_show {
            if pending.ready_count.load(Ordering::Acquire) >= pending.expected {
                platform::show_windows_atomically(self.windows.values().map(|h| &h.window));
                if let Some(first_id) = self.monitor_window_ids.first() {
                    if let Some(h) = self.windows.get(first_id) {
                        h.window.focus_window();
                    }
                }
                pending.visible_barrier.wait();
                self.pending_show = None;
                // Send the initial selection (from the pre-window walker
                // hit-test) to every render thread so the highlight appears
                // on the very first visible frame.
                self.broadcast_mouse_state();
                // And do the first component sync so pre-capture overlays
                // (Tips & Hotkeys panel, …) render on the first frame
                // rather than waiting for the first cursor move.
                self.sync_components();
                event_loop.set_control_flow(winit::event_loop::ControlFlow::Wait);
            }
        }
    }

    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        id: WindowId,
        event: WindowEvent,
    ) {
        // Extract the bits of the window handle we need upfront and
        // drop the `self.windows` borrow immediately. Keeping a live
        // `&WindowHandle` across the match blocks `sync_components`
        // from running because it needs `&mut self`. `Arc<Window>` is
        // cheap to clone and `ScreenRect` is Copy, so this is effectively free.
        let (window, this_monitor_bounds) = match self.windows.get(&id) {
            Some(h) => (h.window.clone(), h.monitor_bounds),
            None => return,
        };
        // Alias to keep the downstream code readable without touching
        // every call site that used `handle.window` and
        // `handle.monitor_bounds`.
        let handle_window = &window;
        let handle_monitor_bounds = this_monitor_bounds;

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
            WindowEvent::KeyboardInput {
                event:
                    KeyEvent {
                        state: ElementState::Pressed,
                        logical_key: Key::Character(ref ch),
                        repeat: false,
                        ..
                    },
                ..
            } => {
                if let Some(c) = ch.chars().next() {
                    let c_lower = c.to_ascii_lowercase();
                    if self.input.captured {
                        if let Some(cmd) = panel::lookup_command_by_key(c) {
                            self.dispatch_command(cmd, event_loop, handle_window);
                        }
                    } else if c_lower == 't' {
                        // Toggle the Tips & Hotkeys overlay. Mirrors
                        // `DxScreenCapture.cpp:1248-1251` — the T key
                        // is a no-op once a selection has been captured.
                        self.input.tips_visible = !self.input.tips_visible;
                        self.sync_components();
                    }
                }
            }
            WindowEvent::Resized(new_size) => {
                // Re-borrow `self.windows` briefly. No other mutation is
                // in flight here, so this is safe.
                if let Some(h) = self.windows.get(&id) {
                    h.resize(new_size);
                }
            }
            WindowEvent::CursorMoved { position, .. } => {
                // winit hands us a position in this window's local physical
                // pixels. Reconstruct the OS cursor in virtual-desktop
                // coords so we can compare against the anchor (itself in
                // virtual-desktop coords).
                let bounds = handle_monitor_bounds;
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
                    // Stale-event guard: the first CursorMoved after
                    // engaging the anchor may carry the *pre-warp* OS
                    // cursor position. Detect by checking whether the
                    // raw (unscaled) distance from anchor exceeds a
                    // reasonable single-frame mouse displacement.
                    if self.input.anchor_just_engaged {
                        const STALE_THRESHOLD: f32 = 75.0;
                        let raw_dx = (os_vd.x - self.input.anchor.x) as f32;
                        let raw_dy = (os_vd.y - self.input.anchor.y) as f32;
                        if raw_dx * raw_dx + raw_dy * raw_dy
                            > STALE_THRESHOLD * STALE_THRESHOLD
                        {
                            // Almost certainly a stale pre-warp event.
                            SystemInterop::set_mouse_position(self.input.anchor);
                            return;
                        }
                        // Small delta — real post-warp movement.
                        self.input.anchor_just_engaged = false;
                    }
                    let zoom = self.input.zoom;
                    let dx = (os_vd.x - self.input.anchor.x) as f32 / zoom;
                    let dy = (os_vd.y - self.input.anchor.y) as f32 / zoom;
                    self.input.virtual_cursor.x += dx;
                    self.input.virtual_cursor.y += dy;
                    clamp_to_nearest_monitor(
                        &mut self.input.virtual_cursor,
                        &self.monitors,
                    );
                    SystemInterop::set_mouse_position(self.input.anchor);
                } else {
                    // Unanchored (zoom == 1): the OS cursor is truth. We
                    // still keep `virtual_cursor` updated so a subsequent
                    // zoom-in transition doesn't need a GetCursorPos.
                    self.input.virtual_cursor =
                        ScreenPointF::new(os_vd.x as f32, os_vd.y as f32);
                }

                // Walker hover: when idle (no button held, no finalised
                // selection) ask the window walker for the best capture
                // target under the cursor. The result becomes the
                // pre-highlight selection that a single click finalises.
                if !self.input.mouse_down && !self.input.captured {
                    let pt = ScreenPoint::new(
                        self.input.virtual_cursor.x.round() as i32,
                        self.input.virtual_cursor.y.round() as i32,
                    );
                    self.input.selection = self
                        .walker
                        .as_ref()
                        .and_then(|w| w.hit_test(pt));
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
                            let crossed = psel.is_some_and(|r| {
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

                // Captured-state input. Two distinct sub-modes:
                //   * No active drag → hit-test the cursor against
                //     the selection and swap the OS cursor icon.
                //   * Active drag → apply the move/resize math
                //     against the snapshotted anchor selection,
                //     soft-clamping to virtual desktop bounds.
                // Mirrors `FrameUpdateHitTest`/`FrameSetCursor` and
                // the WM_MOUSEMOVE handlers at
                // DxScreenCapture.cpp:1402-1490/1670/1732.
                if self.input.captured {
                    if let (Some(mode), Some(anchor), Some(start)) = (
                        self.input.drag_mode,
                        self.input.drag_anchor_selection,
                        self.input.mouse_down_pt,
                    ) {
                        let cur_x = self.input.virtual_cursor.x.floor() as i32;
                        let cur_y = self.input.virtual_cursor.y.floor() as i32;
                        let new_sel = match mode {
                            DragMode::Move => {
                                // No soft-clamp: the logical rect
                                // follows the cursor freely via
                                // `anchor + delta`, and the *displayed*
                                // rect is the intersection with vd
                                // bounds. Dragging the selection
                                // fully off-screen produces `None`
                                // and makes the selection disappear;
                                // dragging back brings it back.
                                let dx = (self.input.virtual_cursor.x
                                    - start.x)
                                    .round()
                                    as i32;
                                let dy = (self.input.virtual_cursor.y
                                    - start.y)
                                    .round()
                                    as i32;
                                move_and_crop(
                                    anchor,
                                    dx,
                                    dy,
                                    self.vd_bounds,
                                )
                            }
                            DragMode::Resize(handle) => Some(resize_with_clamp(
                                anchor,
                                handle,
                                cur_x,
                                cur_y,
                                self.vd_bounds,
                            )),
                        };
                        self.input.selection = new_sel;
                        // Selection geometry changed during a drag after
                        // capture → let every component observe the new
                        // state and re-layout itself as needed.
                        self.sync_components();
                    } else if let Some(sel) = self.input.selection {
                        // Hover hit-test only when no drag is active.
                        // The cursor is determined by the hover state;
                        // during a drag the OS cursor stays whatever
                        // it was at mouse-down (matches Windows native
                        // resize-drag feel).
                        let dpi = dpi_at_point(
                            self.input.virtual_cursor,
                            &self.monitors,
                        );
                        let ht = hit_test(self.input.virtual_cursor, sel, dpi);
                        if ht != self.input.hittest {
                            self.input.hittest = ht;
                            handle_window.set_cursor(ht.cursor());
                        }

                        // Component hover tracking. The host decides
                        // whether any component claims the cursor and
                        // self-refreshes any component whose state
                        // changed — the app just reads back the cursor
                        // hint to pick an icon.
                        let pos = self.input.virtual_cursor;
                        let hint = self
                            .component_host
                            .hit_test(pos)
                            .map(|(_id, h)| h)
                            .unwrap_or(CursorHint::Default);
                        let cursor = match hint {
                            CursorHint::Pointer => CursorIcon::Pointer,
                            CursorHint::Default => self.input.hittest.cursor(),
                        };
                        handle_window.set_cursor(cursor);
                        // Deliver move to every visible component so hover
                        // state can clear when the cursor leaves. The
                        // host re-bakes/re-ships on NeedsOverlayUpdate/
                        // NeedsRedraw internally; the app only cares
                        // about Action/Dismiss here (neither fires on
                        // a pure Move today).
                        let _ = self.component_host.route_mouse_event(
                            MouseEvent::Move { pos },
                            &self.windows,
                            &self.monitor_window_ids,
                        );
                    }
                }

                // Re-sync components on every cursor move so overlays
                // that depend on cursor-driven context (the Tips &
                // Hotkeys panel's hovered window/monitor/pixel color,
                // and its anchor-flip when the cursor gets near it)
                // pick up the new state. The host's hash-based dedup
                // means unchanged components don't re-ship.
                self.sync_components();

                self.broadcast_mouse_state();
            }
            WindowEvent::MouseInput {
                state,
                button: MouseButton::Left,
                ..
            } => {
                match state {
                    ElementState::Pressed => {
                        if self.input.captured {
                            // Component click: route to the component
                            // host. If a component emits a Command,
                            // dispatch it.
                            let pos = self.input.virtual_cursor;
                            if let Some(cmd) = self.component_host.route_mouse_event(
                                MouseEvent::Press { pos },
                                &self.windows,
                                &self.monitor_window_ids,
                            ) {
                                self.dispatch_command(cmd, event_loop, handle_window);
                                return;
                            }
                            // Captured: this mouse-down enters either
                            // Move (clicked inside the rect) or
                            // Resize (clicked on a handle). Anywhere
                            // else is a no-op — clicking outside the
                            // selection doesn't deselect in v1.
                            let drag_mode = match self.input.hittest {
                                Hittest::Inside => Some(DragMode::Move),
                                Hittest::Outside => None,
                                handle => Some(DragMode::Resize(handle)),
                            };
                            if drag_mode.is_some() {
                                self.input.mouse_down = true;
                                self.input.mouse_down_pt =
                                    Some(self.input.virtual_cursor);
                                self.input.drag_mode = drag_mode;
                                self.input.drag_anchor_selection =
                                    self.input.selection;
                            }
                            return;
                        }
                        // Pre-capture: starting a fresh draw-selection
                        // gesture. The pending-drag state is promoted
                        // to active dragging by the threshold check in
                        // CursorMoved.
                        self.input.mouse_down = true;
                        self.input.mouse_down_pt = Some(self.input.virtual_cursor);
                        self.input.mouse_down_dpi = dpi_at_point(
                            self.input.virtual_cursor,
                            &self.monitors,
                        );
                        self.input.dragging = false;
                        // Hide the Tips & Hotkeys panel while the user is
                        // actively dragging — matches the C++
                        // `!data.mouseDown` gate on the tips draw path.
                        self.sync_components();
                        // Selection itself is left alone here so a single
                        // click on a walker-highlighted window keeps the
                        // walker rect visible during the pending-drag
                        // state. If the drag threshold is crossed the
                        // selection switches to freeform; if the user
                        // releases without crossing it the walker rect
                        // is finalised as-is.
                    }
                    ElementState::Released => {
                        // Finalise if we have a selection, haven't
                        // captured yet, and a real press was tracked.
                        // The `mouse_down` guard prevents panel button
                        // clicks (which `return` before setting
                        // `mouse_down`) from accidentally finalising
                        // the walker rect that reset just restored.
                        let finalising = self.input.mouse_down
                            && !self.input.captured
                            && self.input.selection.is_some();
                        let was_move_drag = matches!(
                            self.input.drag_mode,
                            Some(DragMode::Move),
                        );
                        self.input.mouse_down = false;
                        self.input.mouse_down_pt = None;
                        self.input.dragging = false;
                        self.input.drag_mode = None;
                        self.input.drag_anchor_selection = None;
                        // A Move drag that ended with the selection
                        // fully off-screen means the user effectively
                        // cancelled the selection by shoving it into
                        // the void — un-capture so the wheel handler
                        // re-enables zoom and the next mouse-down
                        // starts a fresh draw instead of an impossible
                        // move/resize. Mirrors the C++ `rEmpty`
                        // branch at DxScreenCapture.cpp:1820-1830.
                        if was_move_drag
                            && self.input.captured
                            && self.input.selection.is_none()
                        {
                            self.input.captured = false;
                            self.input.hittest = Hittest::Outside;
                            handle_window.set_cursor(CursorIcon::Default);
                            // Selection dragged off-screen: let components
                            // observe the cleared state and hide themselves.
                            self.sync_components();
                        }
                        if finalising {
                            self.input.captured = true;
                            // Snap zoom back to 1 and tear down the
                            // anchor when a selection is finalised
                            // (matches FrameMakeSelection at
                            // DxScreenCapture.cpp:1816). This is the
                            // sole exit from sticky virtual-cursor
                            // mode: warp the OS cursor to the virtual
                            // cursor so there's no visual jump.
                            if self.input.anchored {
                                self.input.anchored = false;
                                self.input.anchor_just_engaged = false;
                                let restore = ScreenPoint::new(
                                    self.input.virtual_cursor.x.floor() as i32,
                                    self.input.virtual_cursor.y.floor() as i32,
                                );
                                SystemInterop::set_mouse_position(restore);
                            }
                            self.input.zoom = 1.0;
                            // Immediately hit-test against the just-
                            // finalised selection so the cursor flips
                            // to the right resize/move icon without
                            // having to wiggle the mouse.
                            if let Some(sel) = self.input.selection {
                                let dpi = dpi_at_point(
                                    self.input.virtual_cursor,
                                    &self.monitors,
                                );
                                let ht = hit_test(
                                    self.input.virtual_cursor,
                                    sel,
                                    dpi,
                                );
                                self.input.hittest = ht;
                                handle_window.set_cursor(ht.cursor());
                            }
                            // Capture finalised: let every component see
                            // the new state and place/bake itself.
                            self.sync_components();
                        } else if self.input.captured
                            && self.input.selection.is_some()
                        {
                            // A move/resize drag just ended with the
                            // selection still alive — re-sync so
                            // components settle on the final rect.
                            self.sync_components();
                        } else {
                            // Release without finalising / while uncaptured
                            // (e.g. click-and-release without a drag): the
                            // mouse_down flag just cleared, so the Tips
                            // panel should reappear.
                            self.sync_components();
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

                if !self.input.anchored && new_zoom > 1.0 {
                    // MouseAnchorStart (Screens.cpp:130-137): pin the OS
                    // cursor to the anchor. `virtual_cursor` already tracks
                    // the current cursor position, so the zoom appears
                    // centered on wherever the user was pointing.
                    self.input.anchored = true;
                    self.input.anchor_just_engaged = true;
                    SystemInterop::set_mouse_position(self.input.anchor);
                }
                // Virtual cursor mode is sticky: once engaged it persists
                // until capture finalization (mouse-up with a selection).
                // At zoom=1 the delta math is (os-anchor)/1.0 — equivalent
                // to physical tracking but via the anchor warp loop.

                self.input.zoom = new_zoom;
                self.broadcast_mouse_state();
            }
            _ => {}
        }
    }
}

/// Sample the BGRA pixel from a captured-desktop snapshot at the given
/// virtual-desktop coordinate. Returns `None` when the point falls
/// outside the captured bounds (multi-monitor gaps, etc.). The returned
/// byte order matches the raw BGRA in `CapturedDesktop::bgra` — callers
/// that want `#RRGGBB` must re-order bytes themselves.
fn sample_bgra(buf: &CapturedDesktop, p: ScreenPoint) -> Option<[u8; 4]> {
    let dx = p.x - buf.bounds.min_x();
    let dy = p.y - buf.bounds.min_y();
    if dx < 0 || dy < 0 {
        return None;
    }
    let (w, h) = (buf.width as i32, buf.height as i32);
    if dx >= w || dy >= h {
        return None;
    }
    let idx = ((dy * w + dx) as usize) * 4;
    let s = buf.bgra.get(idx..idx + 4)?;
    Some([s[0], s[1], s[2], s[3]])
}
