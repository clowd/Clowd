use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant};

use winit::application::ApplicationHandler;
#[cfg(not(target_os = "macos"))]
use winit::dpi::{PhysicalPosition, PhysicalSize};
use winit::event::{ElementState, KeyEvent, MouseButton, MouseScrollDelta, TouchPhase, WindowEvent};
use winit::event_loop::ActiveEventLoop;
use winit::keyboard::{Key, NamedKey};
#[cfg(windows)]
use winit::platform::windows::WindowAttributesExtWindows;
use winit::window::{CursorIcon, Window, WindowId};

use crate::geometry::{RectExt, ScreenPoint, ScreenPointF, ScreenRect, ScreenRectRounded};
use crate::gpu;
use crate::img::{self, ActionResult};
use crate::platform;
use crate::render::{WindowHandle, WorkerInput, WorkerSetup, WindowHandoff};
use crate::selection::{clamp_to_nearest_monitor, dpi_at_point, hit_test, move_and_crop, resize_with_clamp, DragMode, Hittest};
use crate::settings::CapturerSettings;
use crate::sync::{Latch, VisibleLatch};
use crate::system::{CapturedDesktop, MonitorInfo, SystemInterop, WindowWalker};
use crate::ui::command::Command;
use crate::ui::components::debug::startup::StartupTimings;
use crate::ui::components::panel;
use crate::ui::shared::{UiMonitor, UiSharedState};

const ZOOM_MIN: f32 = 1.0;
const ZOOM_MAX: f32 = 256.0;
const ZOOM_STEP: f32 = 2.0;
const TOUCHPAD_PIXELS_PER_DOUBLING: f32 = 200.0;
const MOMENTUM_GAP: Duration = Duration::from_millis(50);

struct InputState {
    virtual_cursor: ScreenPointF,
    zoom: f32,
    anchored: bool,
    anchor_just_engaged: bool,
    anchor: ScreenPoint,
    mouse_down: bool,
    mouse_down_pt: Option<ScreenPointF>,
    mouse_down_dpi: f32,
    dragging: bool,
    selection: Option<ScreenRect>,
    captured: bool,
    hittest: Hittest,
    drag_mode: Option<DragMode>,
    drag_anchor_selection: Option<ScreenRect>,
    tips_visible: bool,
    debug_visible: bool,
    last_scroll_end: Option<Instant>,
    scroll_momentum: bool,
    overlays_visible: bool,
}

pub struct App {
    settings: Arc<CapturerSettings>,
    windows: HashMap<WindowId, WindowHandle>,
    monitor_window_ids: Vec<WindowId>,
    monitors: Vec<MonitorInfo>,
    cached_hovered_title: Option<String>,
    vd_bounds: ScreenRect,
    input: InputState,
    desktop_buffer: Option<Arc<CapturedDesktop>>,
    walker: Option<Arc<WindowWalker>>,
    pending_show: Option<PendingShow>,
    startup: Arc<StartupTimings>,
    shown_time: Arc<OnceLock<Duration>>,
    pinch_monitor: Option<platform::PinchMonitor>,

    // Parallel bootstrap state — consumed in resumed().
    instance: Arc<wgpu::Instance>,
    worker_setups: Option<Vec<WorkerSetup>>,
    screenshot_latch: Arc<Latch<Arc<CapturedDesktop>>>,
    walker_latch: Arc<Latch<Arc<WindowWalker>>>,
}

struct PendingShow {
    ready_count: Arc<AtomicUsize>,
    expected: usize,
    visible_latch: Arc<VisibleLatch>,
}

impl App {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        settings: Arc<CapturerSettings>,
        startup: Arc<StartupTimings>,
        instance: Arc<wgpu::Instance>,
        monitors: Vec<MonitorInfo>,
        initial_mouse: ScreenPointF,
        worker_setups: Vec<WorkerSetup>,
        screenshot_latch: Arc<Latch<Arc<CapturedDesktop>>>,
        walker_latch: Arc<Latch<Arc<WindowWalker>>>,
        ready_count: Arc<AtomicUsize>,
        visible_latch: Arc<VisibleLatch>,
        shown_time: Arc<OnceLock<Duration>>,
    ) -> Self {
        let primary = monitors
            .iter()
            .find(|m| m.is_primary)
            .or_else(|| monitors.first())
            .expect("at least one monitor present");
        let anchor = ScreenPoint::new(
            primary.bounds.min_x() + (primary.bounds.width() / 2),
            primary.bounds.min_y() + (primary.bounds.height() / 2),
        );

        let vd_bounds = {
            let mut min_x = i32::MAX;
            let mut min_y = i32::MAX;
            let mut max_x = i32::MIN;
            let mut max_y = i32::MIN;
            for m in &monitors {
                min_x = min_x.min(m.bounds.min_x());
                min_y = min_y.min(m.bounds.min_y());
                max_x = max_x.max(m.bounds.max_x());
                max_y = max_y.max(m.bounds.max_y());
            }
            ScreenRect::from_exact(min_x, min_y, max_x, max_y)
        };

        let expected = worker_setups.len();

        Self {
            settings,
            windows: HashMap::new(),
            monitor_window_ids: Vec::new(),
            monitors,
            cached_hovered_title: None,
            vd_bounds,
            desktop_buffer: None,
            walker: None,
            pending_show: Some(PendingShow {
                ready_count,
                expected,
                visible_latch,
            }),
            startup,
            shown_time,
            pinch_monitor: None,
            instance,
            worker_setups: Some(worker_setups),
            screenshot_latch,
            walker_latch,
            input: InputState {
                virtual_cursor: initial_mouse,
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
                debug_visible: false,
                last_scroll_end: None,
                scroll_momentum: false,
                overlays_visible: true,
            },
        }
    }

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

    fn apply_zoom_factor(&mut self, factor: f32) {
        if !factor.is_finite() || factor <= 0.0 {
            return;
        }
        let new_zoom = (self.input.zoom * factor).clamp(ZOOM_MIN, ZOOM_MAX);
        if (new_zoom - self.input.zoom).abs() < f32::EPSILON {
            return;
        }

        if !self.input.anchored && new_zoom > 1.0 {
            self.input.anchored = true;
            self.input.anchor_just_engaged = true;
            SystemInterop::set_mouse_position(self.input.anchor, &self.monitors);
        }

        self.input.zoom = new_zoom;
        self.broadcast_mouse_state();
        if self.input.debug_visible {
            self.broadcast_ui_state();
        }
    }

    fn hide_all_windows(&self) {
        for h in self.windows.values() {
            h.window.set_visible(false);
        }
    }

    fn show_all_windows(&self) {
        for h in self.windows.values() {
            h.window.set_visible(true);
        }
        self.update_cursor_visibility();
    }

    fn update_cursor_visibility(&self) {
        let visible = self.input.captured || self.input.debug_visible;
        // Windows: winit's set_cursor_visible races when broadcast across
        // overlapping desktop-spanning windows — skip it and rely on the
        // global Win32 ShowCursor call below. See platform.rs.
        #[cfg(not(windows))]
        for h in self.windows.values() {
            h.window.set_cursor_visible(visible);
        }
        platform::set_hardware_cursor_visible(visible);
    }

    fn current_panel_layout(&self) -> Option<crate::ui::components::panel::layout::PanelLayout> {
        if !self.input.captured {
            return None;
        }
        let sel = self.input.selection?;
        let cx = (sel.left() + sel.right()) / 2;
        let cy = (sel.top() + sel.bottom()) / 2;
        let mon = self.monitors.iter().find(|m| {
            let b = m.bounds;
            cx >= b.left() && cx < b.right() && cy >= b.top() && cy < b.bottom()
        })?;
        crate::ui::components::panel::layout::compute_layout(mon.bounds, sel, mon.scale_factor)
    }

    fn broadcast_ui_state(&mut self) {
        if self.desktop_buffer.is_none() {
            self.desktop_buffer = self.screenshot_latch.try_get();
        }

        let cursor = self.input.virtual_cursor;
        let cursor_pt = ScreenPoint::new(cursor.x.floor() as i32, cursor.y.floor() as i32);

        let hovered_monitor_name = self
            .monitors
            .iter()
            .find(|m| m.bounds.contains(cursor_pt))
            .map(|m| m.name.clone());

        let hovered_window = self
            .walker
            .as_ref()
            .and_then(|w| w.hit_test_with_title(cursor_pt));
        self.cached_hovered_title = hovered_window.as_ref().map(|(_, t)| t.clone());
        let hovered_window_bounds = hovered_window.as_ref().map(|(r, _)| *r);

        let hovered_pixel_bgra = self
            .desktop_buffer
            .as_deref()
            .and_then(|buf| sample_bgra(buf, cursor_pt));

        let monitors: Arc<[UiMonitor]> = self
            .monitors
            .iter()
            .map(|m| UiMonitor {
                bounds: m.bounds,
                dpi_scale: m.scale_factor,
                is_primary: m.is_primary,
            })
            .collect();

        let state = Arc::new(UiSharedState {
            monitors,
            selection: self.input.selection,
            captured: self.input.captured,
            mouse_down: self.input.mouse_down,
            dragging: self.input.dragging,
            zoom: self.input.zoom,
            virtual_cursor: self.input.virtual_cursor,
            accent_color: self.settings.crosshair_color,
            tips_visible: self.input.tips_visible,
            debug_visible: self.input.debug_visible,
            overlays_visible: self.input.overlays_visible,
            hovered_monitor_name,
            hovered_window_title: self.cached_hovered_title.clone(),
            hovered_window_bounds,
            hovered_pixel_bgra,
        });

        for h in self.windows.values() {
            h.update_ui_state(state.clone());
        }
    }

    fn finalise_selection(&mut self, rect: ScreenRect, window: &Window) {
        self.input.selection = Some(rect);
        self.input.mouse_down = false;
        self.input.mouse_down_pt = None;
        self.input.dragging = false;
        self.input.drag_mode = None;
        self.input.drag_anchor_selection = None;
        self.input.captured = true;
        self.input.overlays_visible = true;
        self.update_cursor_visibility();

        if self.input.anchored {
            self.input.anchored = false;
            self.input.anchor_just_engaged = false;
            let restore = ScreenPoint::new(
                self.input.virtual_cursor.x.floor() as i32,
                self.input.virtual_cursor.y.floor() as i32,
            );
            SystemInterop::set_mouse_position(restore, &self.monitors);
        }
        self.input.zoom = 1.0;

        let dpi = dpi_at_point(self.input.virtual_cursor, &self.monitors);
        let ht = hit_test(self.input.virtual_cursor, rect, dpi);
        self.input.hittest = ht;
        window.set_cursor(ht.cursor());

        self.broadcast_ui_state();
        self.broadcast_mouse_state();
    }

    fn handle_reset(&mut self, window: &Window) {
        self.input.selection = None;
        self.input.captured = false;
        self.input.hittest = Hittest::Outside;
        self.input.drag_mode = None;
        self.input.drag_anchor_selection = None;
        self.update_cursor_visibility();

        self.broadcast_ui_state();

        window.set_cursor(CursorIcon::Default);

        let pt = ScreenPoint::new(
            self.input.virtual_cursor.x.round() as i32,
            self.input.virtual_cursor.y.round() as i32,
        );
        self.input.selection = self.walker.as_ref().and_then(|w| w.hit_test(pt));

        self.broadcast_mouse_state();

        log::info!("selection reset");
    }

    fn dispatch_command(
        &mut self,
        command: Command,
        event_loop: &ActiveEventLoop,
        window: &Window,
    ) {
        use xdialog::XDialogIcon::Error as ErrorIcon;
        log::info!("dispatch command: {:?}", command);

        // Lazily load the desktop buffer from the screenshot latch.
        if self.desktop_buffer.is_none() {
            self.desktop_buffer = self.screenshot_latch.try_get();
        }

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
                        if xdialog::show_message_retry_cancel(
                            "Clowd Capture",
                            "Copy to Clipboard Failed",
                            &msg,
                            ErrorIcon,
                        )
                        .unwrap_or(false)
                        {
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
                        if xdialog::show_message_retry_cancel(
                            "Clowd Capture",
                            "Save Failed",
                            &msg,
                            ErrorIcon,
                        )
                        .unwrap_or(false)
                        {
                            self.show_all_windows();
                        } else {
                            event_loop.exit();
                        }
                    }
                }
            }
            Command::Reset => self.handle_reset(window),
            Command::Exit => {
                self.hide_all_windows();
                event_loop.exit();
            }
            Command::Upload | Command::Edit | Command::Video => {
                log::info!("command {:?} not yet implemented", command);
            }
        }
    }
}

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if !self.windows.is_empty() {
            return;
        }

        if self.pinch_monitor.is_none() {
            self.pinch_monitor = platform::install_pinch_monitor();
        }

        // Consume worker setups — each one gets a window + surface handoff.
        let worker_setups = match self.worker_setups.take() {
            Some(s) => s,
            None => return,
        };

        // Try to pick up the walker result (non-blocking). If not ready
        // yet, selection will start as None and update on first cursor move.
        if self.walker.is_none() {
            if let Some(w) = self.walker_latch.try_get() {
                let pt = ScreenPoint::new(
                    self.input.virtual_cursor.x.round() as i32,
                    self.input.virtual_cursor.y.round() as i32,
                );
                self.input.selection = w.hit_test(pt);
                self.walker = Some(w);
            }
        }

        // Create one borderless window per monitor, create a surface for
        // it, and send the (window, surface) pair to the corresponding
        // render worker.
        let mut handles: HashMap<WindowId, WindowHandle> =
            HashMap::with_capacity(worker_setups.len());

        for (i, setup) in worker_setups.into_iter().enumerate() {
            let m = &self.monitors[i];
            let width = m.bounds.size.width.max(1) as u32;
            let height = m.bounds.size.height.max(1) as u32;

            // On macOS, window frames are in CG logical points. Use the
            // stored CG origin directly and derive logical size from the
            // physical pixel dimensions.
            #[cfg(target_os = "macos")]
            let (win_pos, win_size) = {
                let s = m.scale_factor as f64;
                let pos: winit::dpi::Position = winit::dpi::LogicalPosition::new(
                    m.logical_origin.0,
                    m.logical_origin.1,
                )
                .into();
                let size: winit::dpi::Size = winit::dpi::LogicalSize::new(
                    width as f64 / s,
                    height as f64 / s,
                )
                .into();
                (pos, size)
            };
            #[cfg(not(target_os = "macos"))]
            let (win_pos, win_size) = {
                let pos: winit::dpi::Position =
                    PhysicalPosition::new(m.bounds.origin.x, m.bounds.origin.y).into();
                let size: winit::dpi::Size = PhysicalSize::new(width, height).into();
                (pos, size)
            };

            #[allow(unused_mut)]
            let mut attrs = Window::default_attributes()
                .with_title("clowd capture")
                .with_decorations(false)
                .with_resizable(false)
                .with_visible(cfg!(target_os = "macos"))
                .with_transparent(false)
                .with_active(i == 0)
                .with_position(win_pos)
                .with_inner_size(win_size);
            #[cfg(windows)]
            {
                attrs = attrs.with_no_redirection_bitmap(true);
            }
            let window = match event_loop.create_window(attrs) {
                Ok(w) => Arc::new(w),
                Err(e) => {
                    error!("failed to create window for monitor {i}: {e:?}");
                    continue;
                }
            };
            platform::apply_capture_window_tweaks(&window);
            #[cfg(not(windows))]
            window.set_cursor_visible(false);

            let surface = match gpu::create_surface(&self.instance, window.clone()) {
                Ok(s) => s,
                Err(e) => {
                    error!("failed to create surface for monitor {i}: {e:?}");
                    continue;
                }
            };

            let id = window.id();
            let _ = setup
                .input_tx
                .send(WorkerInput::Handoff(WindowHandoff {
                    window: window.clone(),
                    surface,
                }));

            handles.insert(
                id,
                WindowHandle::new(window, setup.monitor_bounds, setup.render_msg_tx, setup.thread),
            );
            self.monitor_window_ids.push(id);
        }

        if handles.is_empty() {
            error!("no windows created; exiting");
            event_loop.exit();
            return;
        }

        self.startup.mark_window_create();
        self.windows = handles;
        platform::set_hardware_cursor_visible(false);
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        if let Some(ref m) = self.pinch_monitor {
            let delta = m.drain();
            if delta != 0.0 && !self.input.captured {
                self.apply_zoom_factor(1.0 + delta as f32);
            }
        }

        // Try to pick up the walker if it wasn't ready during resumed().
        if self.walker.is_none() {
            if let Some(w) = self.walker_latch.try_get() {
                let pt = ScreenPoint::new(
                    self.input.virtual_cursor.x.round() as i32,
                    self.input.virtual_cursor.y.round() as i32,
                );
                self.input.selection = w.hit_test(pt);
                self.walker = Some(w);
            }
        }

        if let Some(ref pending) = self.pending_show {
            if pending.ready_count.load(Ordering::Acquire) >= pending.expected {
                platform::show_windows_atomically(self.windows.values().map(|h| &h.window));
                let _ = self.shown_time.set(self.startup.t_start.elapsed());
                if let Some(first_id) = self.monitor_window_ids.first() {
                    if let Some(h) = self.windows.get(first_id) {
                        h.window.focus_window();
                    }
                }
                self.update_cursor_visibility();
                pending.visible_latch.signal_all();
                self.pending_show = None;
                self.broadcast_mouse_state();
                self.broadcast_ui_state();
                event_loop.set_control_flow(winit::event_loop::ControlFlow::Wait);
            }
        }
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, id: WindowId, event: WindowEvent) {
        let (window, this_monitor_bounds) = match self.windows.get(&id) {
            Some(h) => (h.window.clone(), h.monitor_bounds),
            None => return,
        };
        let handle_window = &window;
        let handle_monitor_bounds = this_monitor_bounds;

        match event {
            WindowEvent::CloseRequested => {
                self.hide_all_windows();
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
                self.hide_all_windows();
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
                    if c_lower == 'd' {
                        self.input.debug_visible = !self.input.debug_visible;
                        self.update_cursor_visibility();
                        self.broadcast_ui_state();
                    } else if self.input.captured {
                        if let Some(cmd) = panel::lookup_command_by_key(c) {
                            self.dispatch_command(cmd, event_loop, handle_window);
                        }
                    } else if self.input.mouse_down {
                        // Mid-drag: swallow keys.
                    } else {
                        match c_lower {
                            't' => {
                                self.input.tips_visible = !self.input.tips_visible;
                                self.broadcast_ui_state();
                            }
                            'q' => {
                                self.input.overlays_visible = !self.input.overlays_visible;
                                self.broadcast_ui_state();
                                self.broadcast_mouse_state();
                            }
                            'w' => {
                                let pt = ScreenPoint::new(
                                    self.input.virtual_cursor.x.round() as i32,
                                    self.input.virtual_cursor.y.round() as i32,
                                );
                                if let Some(rect) =
                                    self.walker.as_ref().and_then(|w| w.hit_test(pt))
                                {
                                    self.finalise_selection(rect, handle_window);
                                }
                            }
                            'f' => {
                                let pt = ScreenPoint::new(
                                    self.input.virtual_cursor.x.round() as i32,
                                    self.input.virtual_cursor.y.round() as i32,
                                );
                                if let Some(bounds) = self
                                    .monitors
                                    .iter()
                                    .find(|m| m.bounds.contains(pt))
                                    .map(|m| m.bounds)
                                {
                                    self.finalise_selection(bounds, handle_window);
                                }
                            }
                            'a' => {
                                self.finalise_selection(self.vd_bounds, handle_window);
                            }
                            _ => {}
                        }
                    }
                }
            }
            WindowEvent::Resized(new_size) => {
                if let Some(h) = self.windows.get(&id) {
                    h.resize(new_size);
                }
            }
            WindowEvent::CursorMoved { position, .. } => {
                let bounds = handle_monitor_bounds;
                let os_vd = ScreenPoint::new(
                    bounds.min_x() + position.x.round() as i32,
                    bounds.min_y() + position.y.round() as i32,
                );

                if self.input.anchored {
                    if os_vd == self.input.anchor {
                        return;
                    }
                    if self.input.anchor_just_engaged {
                        const STALE_THRESHOLD: f32 = 75.0;
                        let raw_dx = (os_vd.x - self.input.anchor.x) as f32;
                        let raw_dy = (os_vd.y - self.input.anchor.y) as f32;
                        if raw_dx * raw_dx + raw_dy * raw_dy
                            > STALE_THRESHOLD * STALE_THRESHOLD
                        {
                            SystemInterop::set_mouse_position(self.input.anchor, &self.monitors);
                            return;
                        }
                        self.input.anchor_just_engaged = false;
                    }
                    let zoom = self.input.zoom;
                    let dx = (os_vd.x - self.input.anchor.x) as f32 / zoom;
                    let dy = (os_vd.y - self.input.anchor.y) as f32 / zoom;
                    self.input.virtual_cursor.x += dx;
                    self.input.virtual_cursor.y += dy;
                    clamp_to_nearest_monitor(&mut self.input.virtual_cursor, &self.monitors);
                    SystemInterop::set_mouse_position(self.input.anchor, &self.monitors);
                } else {
                    self.input.virtual_cursor =
                        ScreenPointF::new(os_vd.x as f32, os_vd.y as f32);
                }

                if !self.input.mouse_down && !self.input.captured {
                    let pt = ScreenPoint::new(
                        self.input.virtual_cursor.x.round() as i32,
                        self.input.virtual_cursor.y.round() as i32,
                    );
                    self.input.selection = self.walker.as_ref().and_then(|w| w.hit_test(pt));
                }

                if self.input.mouse_down && !self.input.captured {
                    if let Some(start) = self.input.mouse_down_pt {
                        let psel = ScreenRect::from_rounded_threshold(
                            start.x,
                            start.y,
                            self.input.virtual_cursor.x,
                            self.input.virtual_cursor.y,
                        );
                        if !self.input.dragging {
                            let threshold =
                                6.0 / (self.input.mouse_down_dpi * self.input.zoom);
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
                                let dx =
                                    (self.input.virtual_cursor.x - start.x).round() as i32;
                                let dy =
                                    (self.input.virtual_cursor.y - start.y).round() as i32;
                                move_and_crop(anchor, dx, dy, self.vd_bounds)
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
                        self.broadcast_ui_state();
                    } else if let Some(sel) = self.input.selection {
                        let dpi = dpi_at_point(self.input.virtual_cursor, &self.monitors);
                        let ht = hit_test(self.input.virtual_cursor, sel, dpi);
                        if ht != self.input.hittest {
                            self.input.hittest = ht;
                            handle_window.set_cursor(ht.cursor());
                        }

                        let pos = self.input.virtual_cursor;
                        let over_button = self
                            .current_panel_layout()
                            .and_then(|l| l.hit_test(pos.x, pos.y))
                            .is_some();
                        let cursor = if over_button {
                            CursorIcon::Pointer
                        } else {
                            self.input.hittest.cursor()
                        };
                        handle_window.set_cursor(cursor);
                    }
                }

                self.broadcast_ui_state();
                self.broadcast_mouse_state();
            }
            WindowEvent::MouseInput {
                state,
                button: MouseButton::Left,
                ..
            } => {
                if !self.input.overlays_visible {
                    return;
                }
                match state {
                    ElementState::Pressed => {
                        if self.input.captured {
                            let pos = self.input.virtual_cursor;
                            if let Some(layout) = self.current_panel_layout() {
                                if let Some(idx) = layout.hit_test(pos.x, pos.y) {
                                    let cmd = panel::model::button_defs()[idx].command;
                                    self.dispatch_command(cmd, event_loop, handle_window);
                                    return;
                                }
                            }
                            let drag_mode = match self.input.hittest {
                                Hittest::Inside => Some(DragMode::Move),
                                Hittest::Outside => None,
                                handle => Some(DragMode::Resize(handle)),
                            };
                            if drag_mode.is_some() {
                                self.input.mouse_down = true;
                                self.input.mouse_down_pt = Some(self.input.virtual_cursor);
                                self.input.drag_mode = drag_mode;
                                self.input.drag_anchor_selection = self.input.selection;
                            }
                            return;
                        }
                        self.input.mouse_down = true;
                        self.input.mouse_down_pt = Some(self.input.virtual_cursor);
                        self.input.mouse_down_dpi =
                            dpi_at_point(self.input.virtual_cursor, &self.monitors);
                        self.input.dragging = false;
                        self.broadcast_ui_state();
                    }
                    ElementState::Released => {
                        let finalising = self.input.mouse_down
                            && !self.input.captured
                            && self.input.selection.is_some();
                        let was_move_drag =
                            matches!(self.input.drag_mode, Some(DragMode::Move));
                        self.input.mouse_down = false;
                        self.input.mouse_down_pt = None;
                        self.input.dragging = false;
                        self.input.drag_mode = None;
                        self.input.drag_anchor_selection = None;
                        if was_move_drag
                            && self.input.captured
                            && self.input.selection.is_none()
                        {
                            self.input.captured = false;
                            self.input.hittest = Hittest::Outside;
                            self.update_cursor_visibility();
                            handle_window.set_cursor(CursorIcon::Default);
                            self.broadcast_ui_state();
                        }
                        if finalising {
                            self.input.captured = true;
                            self.update_cursor_visibility();
                            if self.input.anchored {
                                self.input.anchored = false;
                                self.input.anchor_just_engaged = false;
                                let restore = ScreenPoint::new(
                                    self.input.virtual_cursor.x.floor() as i32,
                                    self.input.virtual_cursor.y.floor() as i32,
                                );
                                SystemInterop::set_mouse_position(restore, &self.monitors);
                            }
                            self.input.zoom = 1.0;
                            if let Some(sel) = self.input.selection {
                                let dpi = dpi_at_point(
                                    self.input.virtual_cursor,
                                    &self.monitors,
                                );
                                let ht = hit_test(self.input.virtual_cursor, sel, dpi);
                                self.input.hittest = ht;
                                handle_window.set_cursor(ht.cursor());
                            }
                            self.broadcast_ui_state();
                        } else if self.input.captured && self.input.selection.is_some() {
                            self.broadcast_ui_state();
                        } else {
                            self.broadcast_ui_state();
                        }
                        self.broadcast_mouse_state();
                    }
                }
            }
            WindowEvent::MouseWheel { delta, phase, .. } => {
                if self.input.captured {
                    return;
                }
                let factor = match delta {
                    MouseScrollDelta::LineDelta(_, y) => {
                        if y > 0.0 {
                            ZOOM_STEP
                        } else if y < 0.0 {
                            1.0 / ZOOM_STEP
                        } else {
                            return;
                        }
                    }
                    MouseScrollDelta::PixelDelta(p) => {
                        match phase {
                            TouchPhase::Started => {
                                self.input.scroll_momentum = self
                                    .input
                                    .last_scroll_end
                                    .is_some_and(|t| t.elapsed() < MOMENTUM_GAP);
                            }
                            TouchPhase::Ended | TouchPhase::Cancelled => {
                                self.input.last_scroll_end = Some(Instant::now());
                            }
                            _ => {}
                        }
                        if self.input.scroll_momentum {
                            return;
                        }
                        let dy = p.y as f32;
                        if dy == 0.0 {
                            return;
                        }
                        2_f32.powf(dy / TOUCHPAD_PIXELS_PER_DOUBLING)
                    }
                };
                self.apply_zoom_factor(factor);
            }
            WindowEvent::PinchGesture { delta, .. } => {
                if self.input.captured {
                    return;
                }
                if delta == 0.0 {
                    return;
                }
                self.apply_zoom_factor(1.0 + delta as f32);
            }
            _ => {}
        }
    }
}

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
