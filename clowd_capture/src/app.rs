use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant};

use winit::application::ApplicationHandler;
use winit::event::{ElementState, KeyEvent, MouseButton, MouseScrollDelta, TouchPhase, WindowEvent};
use winit::event_loop::ActiveEventLoop;
use winit::keyboard::{Key, NamedKey};
#[cfg(windows)]
use winit::platform::windows::WindowAttributesExtWindows;
use winit::window::{CursorIcon, Window, WindowId};

use crate::capture_output::{copy_to_clipboard_with_peek, ActionResult};
use crate::geometry::{to_screen_point, RectExt, ScreenPoint, ScreenPointF, ScreenRect, ScreenRectExt, ScreenRectRounded, WindowPoint};
use crate::interaction::{InteractionController, InteractionEffects, InteractionState, MouseVelocityTracker};
use crate::render::protocol::PeekCommand;
use crate::render::window::{WindowHandle, WindowSet};
use crate::render::worker::WorkerSetup;
use crate::selection::{clamp_to_nearest_monitor, dpi_at_point, hit_test, move_and_crop, resize_with_clamp, DragMode, Hittest};
use crate::session_output::{write_color_action, write_session, write_video_action, SessionAction};
use crate::settings::{CaptureMode, CapturerSettings};
use crate::sync::{Latch, VisibleLatch};
use crate::system::{CapturedDesktop, MonitorInfo, SystemInterop, WindowPeekImage, WindowWalker};
use crate::telemetry::startup::StartupTimings;
use crate::ui::command::Command;
use crate::ui::components::panel;
use crate::ui_state::{build_ui_shared_state, sample_bgra, UiStateBuildInput};

const ZOOM_STEP: f32 = 2.0;
const TOUCHPAD_PIXELS_PER_DOUBLING: f32 = 200.0;
const MOMENTUM_GAP: Duration = Duration::from_millis(50);

pub struct App {
    settings: Arc<CapturerSettings>,
    windows: WindowSet,
    monitors: Vec<MonitorInfo>,
    cached_hovered_title: Option<String>,
    cached_peek_command: Option<PeekCommand>,
    /// Peek command locked at capture time — persists through resize,
    /// cleared on reset.
    locked_peek: Option<PeekCommand>,
    vd_bounds: ScreenRect,
    input: InteractionState,
    desktop_buffer: Option<Arc<CapturedDesktop>>,
    walker: Option<Arc<WindowWalker>>,
    /// Peek images collected from the walker thread, keyed by window_index.
    /// Used to composite the peeked window into the final copy/save image.
    peek_images: HashMap<usize, Arc<WindowPeekImage>>,
    pending_show: Option<PendingShow>,
    /// When launched with `--capture-mode screen|window`, the mode to
    /// pre-select once the overlay is up. Consumed (set to `None`) after the
    /// one-time pre-selection fires. `None` for free-region mode.
    pending_preselect: Option<CaptureMode>,
    /// One-shot guard for `--video` mode: set the first time a selection
    /// becomes captured so `Command::Video` is auto-dispatched exactly
    /// once, regardless of which capture path (drag / keyboard / preselect)
    /// fired (DESIGN §3.3).
    video_dispatched: bool,
    startup: Arc<StartupTimings>,
    shown_time: Arc<OnceLock<Duration>>,
    pinch_monitor: Option<crate::system::PinchMonitor>,

    // Parallel bootstrap state — consumed in resumed().
    instance: Arc<wgpu::Instance>,
    worker_setups: Option<Vec<WorkerSetup>>,
    walker_latch: Arc<Latch<Arc<WindowWalker>>>,
    peek_images_latch: Arc<Latch<Vec<Arc<WindowPeekImage>>>>,
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
        desktop_buffer: Arc<CapturedDesktop>,
        walker_latch: Arc<Latch<Arc<WindowWalker>>>,
        peek_images_latch: Arc<Latch<Vec<Arc<WindowPeekImage>>>>,
        ready_count: Arc<AtomicUsize>,
        visible_latch: Arc<VisibleLatch>,
        shown_time: Arc<OnceLock<Duration>>,
    ) -> Self {
        let primary = monitors
            .iter()
            .find(|m| m.is_primary)
            .or_else(|| monitors.first())
            .expect("at least one monitor present");
        let anchor = ScreenPoint::new(primary.bounds.center_x(), primary.bounds.center_y());

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
        let tips_mode = settings.tips_mode_at_startup;
        let cursor_overlay_visible = settings.cursor_visible_at_startup;
        let pending_preselect = settings
            .capture_mode
            .is_preselect()
            .then_some(settings.capture_mode);

        Self {
            settings,
            windows: WindowSet::new(),
            monitors,
            cached_hovered_title: None,
            cached_peek_command: None,
            locked_peek: None,
            vd_bounds,
            desktop_buffer: Some(desktop_buffer),
            walker: None,
            peek_images: HashMap::new(),
            pending_show: Some(PendingShow {
                ready_count,
                expected,
                visible_latch,
            }),
            pending_preselect,
            video_dispatched: false,
            startup,
            shown_time,
            pinch_monitor: None,
            instance,
            worker_setups: Some(worker_setups),
            walker_latch,
            peek_images_latch,
            input: InteractionState {
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
                tips_mode,
                debug_visible: false,
                last_scroll_end: None,
                scroll_momentum: false,
                overlays_visible: true,
                cursor_overlay_visible,
                peek_suspended: false,
                has_ever_scrolled: false,
                show_scroll_hint: false,
                velocity_tracker: MouseVelocityTracker::new(),
                has_used_magnifier: false,
            },
        }
    }

    fn ensure_peek_images(&mut self) {
        if self.peek_images.is_empty() {
            if let Some(images) = self.peek_images_latch.try_get() {
                for img in images.iter() {
                    self.peek_images
                        .insert(img.window_index, img.clone());
                }
            }
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
        let effects = InteractionController::apply_zoom_factor(&mut self.input, factor);
        self.apply_interaction_effects(effects, None);
    }

    fn hide_all_windows(&self) {
        self.windows.hide_all();
    }

    fn show_all_windows(&self) {
        self.windows.show_all();
        self.update_cursor_visibility();
    }

    fn update_cursor_visibility(&self) {
        if self.input.captured || self.input.debug_visible {
            self.windows.show_cursors();
        } else {
            self.windows.hide_cursors();
        }
    }

    fn current_panel_layout(&self) -> Option<crate::ui::components::panel::layout::PanelLayout> {
        if !self.input.captured {
            return None;
        }
        let sel = self.input.selection?;
        let cx = sel.center_x();
        let cy = sel.center_y();
        let mon = self.monitors.iter().find(|m| {
            let b = m.bounds;
            cx >= b.left() && cx < b.right() && cy >= b.top() && cy < b.bottom()
        })?;
        crate::ui::components::panel::layout::compute_layout(mon.bounds, sel, mon.scale_factor)
    }

    fn broadcast_ui_state(&mut self) {
        let cursor_pt = to_screen_point(self.input.virtual_cursor);

        let hovered_monitor_name = self
            .monitors
            .iter()
            .find(|m| m.bounds.contains(cursor_pt))
            .map(|m| m.name.clone());

        let hovered_full = self
            .walker
            .as_ref()
            .and_then(|w| w.hit_test_full(cursor_pt));
        self.cached_hovered_title = hovered_full
            .as_ref()
            .map(|h| h.title.clone());
        let hovered_window_bounds = hovered_full.as_ref().map(|h| h.rect);
        let hovered_window_index = hovered_full.as_ref().map(|h| h.window_index);
        let hovered_window_obstructed = hovered_full
            .as_ref()
            .is_some_and(|h| h.obstructed);

        // Compute peek command first so UI state can use peek_active.
        // Peek is suppressed in magnifier mode (overlays hidden) and after
        // a selection has been made (peek_suspended).  When captured, keep
        // the locked peek; otherwise follow hover.
        let new_peek = if !self.input.overlays_visible || self.input.peek_suspended || self.input.dragging {
            self.locked_peek.clone()
        } else {
            hovered_full
                .as_ref()
                .filter(|hw| hw.obstructed && self.settings.obscured_window_peek_enabled)
                .map(|hw| PeekCommand {
                    window_index: hw.window_index,
                    window_rect: hw.rect,
                    captured: false,
                })
        };

        let state = Arc::new(build_ui_shared_state(UiStateBuildInput {
            monitors: &self.monitors,
            selection: self.input.selection,
            captured: self.input.captured,
            mouse_down: self.input.mouse_down,
            dragging: self.input.dragging,
            zoom: self.input.zoom,
            virtual_cursor: self.input.virtual_cursor,
            accent_color: self.settings.accent_color,
            tips_mode: self.input.tips_mode,
            debug_visible: self.input.debug_visible,
            overlays_visible: self.input.overlays_visible,
            hovered_monitor_name,
            hovered_window_title: self.cached_hovered_title.clone(),
            hovered_window_bounds,
            hovered_window_index,
            hovered_window_obstructed,
            peek_active: new_peek.is_some(),
            cursor_overlay_visible: self.input.cursor_overlay_visible,
            desktop_buffer: self.desktop_buffer.as_deref(),
            show_scroll_hint: self.input.show_scroll_hint,
            has_used_magnifier: self.input.has_used_magnifier,
        }));

        for h in self.windows.values() {
            h.update_ui_state(state.clone());
        }

        if new_peek != self.cached_peek_command {
            for h in self.windows.values() {
                h.update_peek_state(new_peek.clone());
            }
            self.cached_peek_command = new_peek;
        }
    }

    fn finalise_selection(&mut self, rect: ScreenRect, event_loop: &ActiveEventLoop, window_id: WindowId) {
        self.finalise_selection_inner(rect, event_loop, window_id, false);
    }

    fn finalise_selection_with_peek(&mut self, rect: ScreenRect, event_loop: &ActiveEventLoop, window_id: WindowId) {
        self.finalise_selection_inner(rect, event_loop, window_id, true);
    }

    fn finalise_selection_inner(&mut self, rect: ScreenRect, event_loop: &ActiveEventLoop, window_id: WindowId, lock_peek: bool) {
        if lock_peek {
            self.locked_peek = self
                .cached_peek_command
                .as_ref()
                .map(|cmd| PeekCommand {
                    window_index: cmd.window_index,
                    window_rect: cmd.window_rect,
                    captured: true,
                });
        } else {
            self.locked_peek = None;
        }
        self.input.peek_suspended = true;

        let effects = InteractionController::finalize_selection(&mut self.input, rect, &self.monitors);
        self.apply_interaction_effects(effects, Some(window_id));

        // Keyboard / preselect captured-transition site (DESIGN §3.3).
        self.on_captured(event_loop, window_id);
    }

    /// Auto-dispatch `Command::Video` the first time a selection becomes
    /// captured, when the overlay was launched with `--video`. Called from
    /// both captured-transition sites (mouse-release drag-select and the
    /// keyboard/preselect `finalise_selection` path) so video mode works
    /// for every entry path (DESIGN §3.3).
    fn on_captured(&mut self, event_loop: &ActiveEventLoop, window_id: WindowId) {
        if self.settings.video_mode && !self.video_dispatched {
            self.video_dispatched = true;
            self.dispatch_command(Command::Video, event_loop, window_id);
        }
    }

    /// Target rect for a `--capture-mode screen|window` pre-selection.
    /// `Screen` is the monitor under the cursor; `Window` is the foreground
    /// window, falling back to the active screen when no foreground window is
    /// available. `Region` never pre-selects.
    fn preselect_rect(&self, mode: CaptureMode) -> Option<ScreenRect> {
        let active_screen = || {
            let pt = to_screen_point(self.input.virtual_cursor);
            self.monitors
                .iter()
                .find(|m| m.bounds.contains(pt))
                .map(|m| m.bounds)
        };
        match mode {
            CaptureMode::Region => None,
            CaptureMode::Screen => active_screen(),
            CaptureMode::Window => self
                .walker
                .as_ref()
                .and_then(|w| w.foreground_capture_rect())
                .or_else(active_screen),
        }
    }

    /// Fire the one-time `--capture-mode screen|window` pre-selection once the
    /// overlay is visible and (for window mode) the walker has resolved. Enters
    /// the captured state with the target rect so the action panel is shown for
    /// the user to confirm or adjust — the same state pressing `F` / `W` yields.
    fn try_preselect(&mut self, event_loop: &ActiveEventLoop) {
        let Some(mode) = self.pending_preselect else {
            return;
        };
        // Wait until the overlay is up (panel needs a shown window to render).
        if self.pending_show.is_some() {
            return;
        }
        // Window mode targets the foreground window, which comes from the walker.
        if matches!(mode, CaptureMode::Window) && self.walker.is_none() {
            return;
        }
        let Some(window_id) = self.windows.first().map(|h| h.window_id()) else {
            return;
        };

        match self.preselect_rect(mode) {
            Some(rect) => {
                log::info!("--capture-mode {:?}: pre-selecting {:?}", mode, rect);
                self.finalise_selection(rect, event_loop, window_id);
            }
            None => log::info!("--capture-mode {:?}: no target found; leaving free selection", mode),
        }
        self.pending_preselect = None;
    }

    fn handle_reset(&mut self, window_id: WindowId) {
        self.locked_peek = None;
        self.input.peek_suspended = false;
        let effects = InteractionController::reset(&mut self.input);
        self.apply_interaction_effects(effects, Some(window_id));

        let pt = ScreenPoint::new(
            self.input.virtual_cursor.x.round() as i32,
            self.input.virtual_cursor.y.round() as i32,
        );
        self.input.selection = self
            .walker
            .as_ref()
            .and_then(|w| w.hit_test(pt));

        self.broadcast_mouse_state();

        log::info!("selection reset");
    }

    fn apply_interaction_effects(&mut self, effects: InteractionEffects, window_id: Option<WindowId>) {
        if effects.update_cursor_visibility {
            self.update_cursor_visibility();
        }
        if let Some(pos) = effects.restore_mouse {
            SystemInterop::set_mouse_position(pos, &self.monitors);
        }
        if let (Some(window_id), Some(cursor)) = (window_id, effects.set_cursor) {
            if let Some(window) = self.windows.get(&window_id) {
                window.set_cursor(cursor);
            }
        }
        if effects.broadcast_ui {
            self.broadcast_ui_state();
        }
        if effects.broadcast_mouse {
            self.broadcast_mouse_state();
        }
    }

    fn dispatch_command(&mut self, command: Command, event_loop: &ActiveEventLoop, window_id: WindowId) {
        use xdialog::XDialogIcon::Error as ErrorIcon;
        log::info!("dispatch command: {:?}", command);

        self.ensure_peek_images();
        let active_peek_image = self.locked_peek.as_ref().and_then(|cmd| {
            self.peek_images
                .get(&cmd.window_index)
                .map(|img| img.as_ref())
        });

        let cursor = self
            .desktop_buffer
            .as_ref()
            .and_then(|buf| buf.cursor.as_ref());
        let cursor_visible = self.input.cursor_overlay_visible;

        match command {
            Command::Copy => {
                self.hide_all_windows();
                let result = match (self.input.selection, self.desktop_buffer.as_deref()) {
                    (Some(sel), Some(buf)) => copy_to_clipboard_with_peek(sel, buf, active_peek_image, cursor, cursor_visible),
                    _ => ActionResult::Failed("No selection or buffer".into()),
                };
                match result {
                    ActionResult::Success => event_loop.exit(),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Copy to Clipboard Failed", &msg, ErrorIcon).unwrap_or(false)
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
                    (Some(sel), Some(buf)) => match self.windows.get(&window_id) {
                        Some(handle) => handle.save_to_file_with_peek(sel, buf, active_peek_image, cursor, cursor_visible),
                        None => ActionResult::Failed("No active window".into()),
                    },
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
            Command::Edit | Command::Upload => {
                // EDIT and UPLOAD write the same session payload for the
                // shell (CAPTURE_PROTOCOL.md); UPLOAD adds the action.txt
                // marker so the shell uploads instead of opening the
                // editor. Without a --session-dir there is no shell
                // listening — ignore.
                let Some(session_dir) = self.settings.session_dir.clone() else {
                    log::info!("command {:?} ignored: no --session-dir provided", command);
                    return;
                };
                let action = if command == Command::Upload {
                    SessionAction::Upload
                } else {
                    SessionAction::Edit
                };
                self.hide_all_windows();
                let result = match (self.input.selection, self.desktop_buffer.as_deref()) {
                    (Some(sel), Some(buf)) => write_session(&session_dir, sel, buf, active_peek_image, cursor_visible, action),
                    _ => ActionResult::Failed("No selection or buffer".into()),
                };
                match result {
                    ActionResult::Success => event_loop.exit(),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Session Capture Failed", &msg, ErrorIcon).unwrap_or(false) {
                            self.show_all_windows();
                        } else {
                            event_loop.exit();
                        }
                    }
                }
            }
            Command::SelectColor => {
                // H in crosshair mode (DxScreenCapture.cpp:1223): report
                // the pixel under the cursor to the shell — it opens its
                // color viewer. Without a shell there is nothing to show
                // the color in — ignore.
                let Some(session_dir) = self.settings.session_dir.clone() else {
                    log::info!("command SelectColor ignored: no --session-dir provided");
                    return;
                };
                let sampled = self
                    .desktop_buffer
                    .as_deref()
                    .and_then(|buf| sample_bgra(buf, to_screen_point(self.input.virtual_cursor)));
                let Some(bgra) = sampled else {
                    log::warn!("command SelectColor ignored: cursor is not over the desktop bitmap");
                    return;
                };
                self.hide_all_windows();
                match write_color_action(&session_dir, bgra[2], bgra[1], bgra[0]) {
                    ActionResult::Success => event_loop.exit(),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Color Capture Failed", &msg, ErrorIcon).unwrap_or(false) {
                            self.show_all_windows();
                        } else {
                            event_loop.exit();
                        }
                    }
                }
            }
            Command::Reset => self.handle_reset(window_id),
            Command::Exit => {
                self.hide_all_windows();
                event_loop.exit();
            }
            Command::Video => {
                // Mirrors Edit|Upload: writes the video action payload
                // (poster + `action.txt`) for the shell to start recording.
                // Without a --session-dir there is no shell listening —
                // ignore. (DESIGN §3.2/§3.3.)
                let Some(session_dir) = self.settings.session_dir.clone() else {
                    log::info!("command Video ignored: no --session-dir provided");
                    return;
                };
                self.hide_all_windows();
                let result = match (self.input.selection, self.desktop_buffer.as_deref()) {
                    (Some(sel), Some(buf)) => write_video_action(&session_dir, sel, buf, cursor_visible, &self.monitors),
                    _ => ActionResult::Failed("No selection or buffer".into()),
                };
                match result {
                    ActionResult::Success => event_loop.exit(),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Video Capture Failed", &msg, ErrorIcon).unwrap_or(false) {
                            self.show_all_windows();
                        } else {
                            event_loop.exit();
                        }
                    }
                }
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
            self.pinch_monitor = SystemInterop::install_pinch_monitor();
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

        let mut windows = WindowSet::new();
        self.startup.mark_window_create_start();

        for (i, setup) in worker_setups.into_iter().enumerate() {
            let m = &self.monitors[i];
            let width = m.bounds.size.width.max(1) as u32;
            let height = m.bounds.size.height.max(1) as u32;

            #[cfg(windows)]
            let (win_pos, win_size): (winit::dpi::Position, winit::dpi::Size) = (
                winit::dpi::PhysicalPosition::new(m.bounds.origin.x, m.bounds.origin.y).into(),
                winit::dpi::PhysicalSize::new(width, height).into(),
            );
            #[cfg(target_os = "macos")]
            let (win_pos, win_size): (winit::dpi::Position, winit::dpi::Size) = {
                let logical_pos = m.screen_to_logical(ScreenPoint::new(m.bounds.origin.x, m.bounds.origin.y));
                let logical_size = m.physical_to_logical_size(width, height);
                (
                    winit::dpi::LogicalPosition::new(logical_pos.x, logical_pos.y).into(),
                    winit::dpi::LogicalSize::new(logical_size.width, logical_size.height).into(),
                )
            };

            #[allow(unused_mut)]
            let mut attrs = Window::default_attributes()
                .with_title("clowd capture")
                .with_decorations(false)
                .with_resizable(false)
                .with_visible(false)
                .with_active(false)
                .with_transparent(false)
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

            self.startup.background.workers[i]
                .surface_start
                .set_once(self.startup.t_start.elapsed());
            let handle = match WindowHandle::new(
                window,
                setup,
                &self.instance,
                #[cfg(target_os = "macos")]
                self.desktop_buffer.as_deref(),
            ) {
                Ok(h) => h,
                Err(e) => {
                    error!("failed to create window handle for monitor {i}: {e:?}");
                    continue;
                }
            };
            self.startup.background.workers[i]
                .surface_bind
                .set_once(self.startup.t_start.elapsed());

            windows.insert(handle);
        }

        if windows.is_empty() {
            error!("no windows created; exiting");
            event_loop.exit();
            return;
        }

        self.startup.mark_window_create();
        self.windows = windows;
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
                self.startup.mark_show_start();
                self.windows.show_all();
                let _ = self
                    .shown_time
                    .set(self.startup.t_start.elapsed());
                if let Some(h) = self.windows.first() {
                    h.focus();
                }
                self.update_cursor_visibility();
                pending.visible_latch.signal_all();
                self.pending_show = None;
                self.broadcast_mouse_state();
                self.broadcast_ui_state();
            }
        }

        // Pre-select the active screen / foreground window when launched with
        // `--capture-mode screen|window` (no-op in free-region mode).
        self.try_preselect(event_loop);
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, id: WindowId, event: WindowEvent) {
        let this_monitor_bounds = match self.windows.get(&id) {
            Some(h) => h.monitor_bounds(),
            None => return,
        };
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
                        logical_key: Key::Named(NamedKey::Enter),
                        ..
                    },
                ..
            }
                // Mirrors the Dx capturer: Return acts as the default
                // accept ("open in editor") once a selection is made.
                if self.input.captured => {
                    self.dispatch_command(Command::Edit, event_loop, id);
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
                    } else if c_lower == 'm' {
                        self.input.cursor_overlay_visible = !self.input.cursor_overlay_visible;
                        self.broadcast_ui_state();
                    } else if self.input.captured {
                        if let Some(cmd) = panel::lookup_command_by_key(c) {
                            self.dispatch_command(cmd, event_loop, id);
                        }
                    } else if self.input.mouse_down {
                        // Mid-drag: swallow keys.
                    } else {
                        match c_lower {
                            't' => {
                                self.input.tips_mode = self.input.tips_mode.next();
                                self.broadcast_ui_state();
                            }
                            'q' => {
                                self.input.overlays_visible = !self.input.overlays_visible;
                                if !self.input.overlays_visible && self.input.zoom > 1.0 {
                                    self.input.has_used_magnifier = true;
                                }
                                self.broadcast_ui_state();
                                self.broadcast_mouse_state();
                            }
                            'w' => {
                                let pt = ScreenPoint::new(
                                    self.input.virtual_cursor.x.round() as i32,
                                    self.input.virtual_cursor.y.round() as i32,
                                );
                                if let Some(rect) = self
                                    .walker
                                    .as_ref()
                                    .and_then(|w| w.hit_test(pt))
                                {
                                    self.finalise_selection_with_peek(rect, event_loop, id);
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
                                    self.finalise_selection(bounds, event_loop, id);
                                }
                            }
                            'a' => {
                                self.finalise_selection(self.vd_bounds, event_loop, id);
                            }
                            'h' => {
                                // color-sampler row in the tips panel.
                                self.dispatch_command(Command::SelectColor, event_loop, id);
                            }
                            _ => {}
                        }
                    }
                }
            }
            #[cfg(windows)]
            WindowEvent::ScaleFactorChanged {
                mut inner_size_writer,
                ..
            } => {
                let _ = inner_size_writer.request_inner_size(winit::dpi::PhysicalSize::new(
                    handle_monitor_bounds.width() as u32,
                    handle_monitor_bounds.height() as u32,
                ));
            }
            WindowEvent::CursorMoved {
                position,
                ..
            } => {
                let bounds = handle_monitor_bounds;
                let win_pt = WindowPoint::new(position.x as f32, position.y as f32);
                let os_vd = ScreenPoint::new(bounds.min_x() + win_pt.x.round() as i32, bounds.min_y() + win_pt.y.round() as i32);

                if self.input.anchored {
                    if os_vd == self.input.anchor {
                        return;
                    }
                    if self.input.anchor_just_engaged {
                        const STALE_THRESHOLD: f32 = 75.0;
                        let raw_dx = (os_vd.x - self.input.anchor.x) as f32;
                        let raw_dy = (os_vd.y - self.input.anchor.y) as f32;
                        if raw_dx * raw_dx + raw_dy * raw_dy > STALE_THRESHOLD * STALE_THRESHOLD {
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
                    self.input.virtual_cursor = ScreenPointF::new(os_vd.x as f32, os_vd.y as f32);
                }

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

                if self.input.mouse_down && !self.input.captured {
                    if let Some(start) = self.input.mouse_down_pt {
                        let psel =
                            ScreenRect::from_rounded_threshold(start.x, start.y, self.input.virtual_cursor.x, self.input.virtual_cursor.y);
                        if !self.input.dragging {
                            let threshold = 6.0 / (self.input.mouse_down_dpi * self.input.zoom);
                            let crossed = psel.is_some_and(|r| (r.width() as f32) > threshold || (r.height() as f32) > threshold);
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
                    if let (Some(mode), Some(anchor), Some(start)) =
                        (self.input.drag_mode, self.input.drag_anchor_selection, self.input.mouse_down_pt)
                    {
                        let cur_x = self.input.virtual_cursor.x.floor() as i32;
                        let cur_y = self.input.virtual_cursor.y.floor() as i32;
                        let new_sel = match mode {
                            DragMode::Move => {
                                let dx = (self.input.virtual_cursor.x - start.x).round() as i32;
                                let dy = (self.input.virtual_cursor.y - start.y).round() as i32;
                                move_and_crop(anchor, dx, dy, self.vd_bounds)
                            }
                            DragMode::Resize(handle) => Some(resize_with_clamp(anchor, handle, cur_x, cur_y, self.vd_bounds)),
                        };
                        self.input.selection = new_sel;
                        self.broadcast_ui_state();
                    } else if let Some(sel) = self.input.selection {
                        let dpi = dpi_at_point(self.input.virtual_cursor, &self.monitors);
                        let ht = hit_test(self.input.virtual_cursor, sel, dpi);
                        if ht != self.input.hittest {
                            self.input.hittest = ht;
                            if let Some(handle) = self.windows.get(&id) {
                                handle.set_cursor(ht.cursor());
                            }
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
                        if let Some(handle) = self.windows.get(&id) {
                            handle.set_cursor(cursor);
                        }
                    }
                }

                if !self.input.has_ever_scrolled && !self.input.captured {
                    let now = Instant::now();
                    self.input
                        .velocity_tracker
                        .record(now, self.input.virtual_cursor);
                    self.input.show_scroll_hint = self
                        .input
                        .velocity_tracker
                        .evaluate(now, self.input.show_scroll_hint);
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
                                    self.dispatch_command(cmd, event_loop, id);
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
                        self.input.mouse_down_dpi = dpi_at_point(self.input.virtual_cursor, &self.monitors);
                        self.input.dragging = false;
                        self.broadcast_ui_state();
                    }
                    ElementState::Released => {
                        let finalising = self.input.mouse_down && !self.input.captured && self.input.selection.is_some();
                        let was_dragging = self.input.dragging;
                        let was_move_drag = matches!(self.input.drag_mode, Some(DragMode::Move));
                        self.input.mouse_down = false;
                        self.input.mouse_down_pt = None;
                        self.input.dragging = false;
                        self.input.drag_mode = None;
                        self.input.drag_anchor_selection = None;
                        if was_move_drag && self.input.captured && self.input.selection.is_none() {
                            self.input.captured = false;
                            self.input.hittest = Hittest::Outside;
                            self.update_cursor_visibility();
                            if let Some(handle) = self.windows.get(&id) {
                                handle.set_cursor(CursorIcon::Default);
                            }
                            self.broadcast_ui_state();
                        }
                        if finalising {
                            if !was_dragging {
                                // Click (no drag) on a peeked window → lock it permanently.
                                self.locked_peek = self
                                    .cached_peek_command
                                    .as_ref()
                                    .map(|cmd| PeekCommand {
                                        window_index: cmd.window_index,
                                        window_rect: cmd.window_rect,
                                        captured: true,
                                    });
                            } else {
                                // Drag-to-select → clear peek entirely.
                                self.locked_peek = None;
                            }
                            self.input.peek_suspended = true;
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
                                let dpi = dpi_at_point(self.input.virtual_cursor, &self.monitors);
                                let ht = hit_test(self.input.virtual_cursor, sel, dpi);
                                self.input.hittest = ht;
                                if let Some(handle) = self.windows.get(&id) {
                                    handle.set_cursor(ht.cursor());
                                }
                            }
                            // Mouse-release drag-select captured-transition
                            // site (DESIGN §3.3).
                            self.on_captured(event_loop, id);
                        }
                        self.broadcast_ui_state();
                        self.broadcast_mouse_state();
                    }
                }
            }
            WindowEvent::MouseWheel {
                delta,
                phase,
                ..
            } => {
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
                self.input.has_ever_scrolled = true;
                self.input.show_scroll_hint = false;
                self.input.velocity_tracker.dismiss_hint();
                self.apply_zoom_factor(factor);
            }
            WindowEvent::PinchGesture {
                delta,
                ..
            } => {
                if self.input.captured {
                    return;
                }
                if delta == 0.0 {
                    return;
                }
                self.input.has_ever_scrolled = true;
                self.input.show_scroll_hint = false;
                self.input.velocity_tracker.dismiss_hint();
                self.apply_zoom_factor(1.0 + delta as f32);
            }
            _ => {}
        }
    }
}
