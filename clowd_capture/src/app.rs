use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{mpsc, Arc};
use std::time::{Duration, Instant};

use winit::application::ApplicationHandler;
use winit::event::{ElementState, KeyEvent, MouseButton, MouseScrollDelta, TouchPhase, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow};
use winit::keyboard::{Key, NamedKey};
#[cfg(windows)]
use winit::platform::windows::WindowAttributesExtWindows;
use winit::window::{CursorIcon, Window, WindowId};

use crate::capture::session::{spawn_screenshot_job, spawn_walker_job, ScreenshotJobParams};
use crate::capture_output::{copy_text_to_clipboard, copy_to_clipboard_with_peek, ActionResult};
use crate::host::{
    self,
    protocol::{HostCommand, HostEvent, ShowParams},
    AppEvent,
};
use crate::image_extract::{extract_selection_bgra, extract_selection_bgra_with_peek};
use crate::interaction::{
    InteractionController, InteractionEffects, InteractionState, MouseVelocityTracker, OcrNotice, OcrNoticeKind, OcrState,
};
use crate::ocr::{self, OcrError, OcrOutcome, OcrRequest};
use crate::render::protocol::{next_cycle_gen, PeekCommand, RenderMsg, WorkerInput};
use crate::render::window::{set_hardware_cursor_visible, WindowHandle, WindowSet};
use crate::render::worker::WorkerSetup;
use crate::selection::{clamp_to_nearest_monitor, dpi_at_point, hit_test, move_and_crop, resize_with_clamp, DragMode, Hittest};
use crate::session_output::{
    write_color_action, write_ocr_upload_action, write_scroll_action, write_session, write_video_action, SessionAction,
};
use crate::settings::{CaptureMode, CapturerSettings};
use crate::sync::{Latch, VisibleLatch};
use crate::system::{CapturedDesktop, MonitorInfo, SystemInterop, WindowPeekImage, WindowWalker, EXIT_DISPLAY_CHANGED, EXIT_GPU_LOST};
use crate::telemetry::startup::{CaptureTimings, WarmupTimings};
use crate::ui::command::Command;
use crate::ui::components::panel;
use crate::ui::shared::UiMonitor;
use crate::ui_state::{build_ui_shared_state, sample_bgra, UiStateBuildInput};
use clowd_rust_core::geometry::{
    to_screen_point, RectExt, ScreenPoint, ScreenPointF, ScreenRect, ScreenRectExt, ScreenRectRounded, WindowPoint,
};

const ZOOM_STEP: f32 = 2.0;
const TOUCHPAD_PIXELS_PER_DOUBLING: f32 = 200.0;
const MOMENTUM_GAP: Duration = Duration::from_millis(50);
/// How long to coalesce OS display-change notifications before restarting
/// the persistent host: one topology change fans out into several messages
/// (Windows sends one per top-level window; macOS one callback per
/// display), and restarting on the first would race the rest.
const DISPLAY_CHANGE_DEBOUNCE: Duration = Duration::from_millis(500);

/// State that survives across capture cycles ("warm" state): the wgpu
/// instance, monitors, windows and the render-worker channels. Everything
/// specific to a single capture lives in [`CaptureCycle`].
pub struct App {
    /// Retained clones of the per-worker channel senders, usable across
    /// cycles (the `WorkerSetup`s themselves are consumed by the window
    /// handoff in `resumed()`). Declared before `windows`: fields drop in
    /// declaration order, and dropping these senders first disconnects the
    /// input channel so a parked worker wakes up before `WindowHandle::drop`
    /// joins its thread.
    ///
    /// `None` marks a worker torn down after its window creation failed
    /// (`resumed()`): its slot stays so the gate arithmetic
    /// (`ready`/`parked` + `worker_failed` >= `workers.len()`) keeps
    /// covering it, but nothing is ever queued to it again — a worker
    /// stuck before its handoff never drains `render_msg_rx`, so retained
    /// senders would grow its queue by a full blur + peek set per capture.
    workers: Vec<Option<WorkerChannels>>,
    windows: WindowSet,
    monitors: Vec<MonitorInfo>,
    /// `monitors` mapped to the UI-state shape, built once — cloned into
    /// every `UiSharedState` instead of re-collected per mouse event.
    ui_monitors: Arc<[UiMonitor]>,
    vd_bounds: ScreenRect,
    warmup: Arc<WarmupTimings>,
    pinch_monitor: Option<crate::system::PinchMonitor>,
    instance: Arc<wgpu::Instance>,
    /// Consumed in resumed() — each one gets a window + surface handoff.
    worker_setups: Option<Vec<WorkerSetup>>,
    /// Incremented by workers that die without a clean shutdown (and by
    /// window-creation failures here), so the show gate
    /// (`ready + failed >= expected`) can never deadlock on a dead worker.
    worker_failed: Arc<AtomicUsize>,
    /// Whether the process should keep running after a cycle finishes.
    /// False in one-shot mode; `--persistent` host mode flips it (via
    /// [`enable_persistent`](Self::enable_persistent)) so `finish_cycle`
    /// parks instead of exiting.
    persistent: bool,
    /// Persistent-host bookkeeping; `Some` iff `persistent`.
    host: Option<HostState>,
    cycle: Option<CaptureCycle>,
}

/// Warm-state bookkeeping that only exists in `--persistent` mode.
struct HostState {
    /// Incremented by each render worker when it first parks (see
    /// `render_worker_main`); `ready` is emitted once `parked + failed`
    /// covers every worker.
    parked_count: Arc<AtomicUsize>,
    ready_emitted: bool,
    /// Debounce deadline armed by [`AppEvent::DisplayChange`]; when it
    /// expires (`check_display_change`) the host emits `display_changed`
    /// and exits with `EXIT_DISPLAY_CHANGED` for the shell to respawn.
    display_change_deadline: Option<Instant>,
}

struct WorkerChannels {
    /// Retained so cycles after the first can be started without the
    /// consumed `WorkerSetup`s (the screenshot job broadcasts `BeginCycle`
    /// over these) — and so dropping `App.workers` closes the channel,
    /// waking any parked worker for shutdown.
    input_tx: mpsc::Sender<WorkerInput>,
    render_msg_tx: mpsc::Sender<RenderMsg>,
}

/// All one-shot state for a single capture. Created at cycle start
/// ([`App::start_cycle`]) and dropped at cycle end ([`App::finish_cycle`]) —
/// dropping it *is* the reset.
pub struct CaptureCycle {
    settings: Arc<CapturerSettings>,
    /// This cycle's generation (`next_cycle_gen`), stamped on the per-cycle
    /// `RenderMsg`s so workers can discard messages from other cycles.
    cycle_gen: u64,
    /// Shared with `CycleParams`; set in `finish_cycle` so a worker that
    /// receives this cycle's `BeginCycle` after the cycle already ended
    /// discards it instead of wedging on the dead `visible_latch`.
    cancelled: Arc<AtomicBool>,
    /// This cycle's t=0 (`timings.t_start`): when its per-capture jobs were
    /// spawned. The persistent host reports the show-to-visible time as
    /// `shown.elapsed_ms` from here.
    started: Instant,
    /// This cycle's debug timings — allocated fresh per cycle (which is
    /// what keeps the `set_once` fields correct across cycles) and shared
    /// with the screenshot/walker jobs and every render worker.
    timings: Arc<CaptureTimings>,
    desktop_buffer: Option<Arc<CapturedDesktop>>,
    /// This cycle's screenshot job result. One-shot mode resolves it
    /// before `start_cycle`; the persistent host picks it up
    /// non-blockingly in `about_to_wait`, bounded by
    /// `screenshot_deadline`.
    screenshot_latch: Arc<Latch<Arc<CapturedDesktop>>>,
    /// Non-blocking replacement for one-shot mode's 30s screenshot wait:
    /// if the desktop bitmap hasn't arrived by this deadline the cycle is
    /// cancelled with a `fatal_error` event.
    screenshot_deadline: Instant,
    walker: Option<Arc<WindowWalker>>,
    walker_latch: Arc<Latch<Arc<WindowWalker>>>,
    peek_images_latch: Arc<Latch<Vec<Arc<WindowPeekImage>>>>,
    /// Peek images collected from the walker thread, keyed by window_index.
    /// Used to composite the peeked window into the final copy/save image.
    peek_images: HashMap<usize, Arc<WindowPeekImage>>,
    /// Last cursor icon set per window, to skip redundant `set_cursor`
    /// calls on every mouse move.
    last_cursor: HashMap<WindowId, CursorIcon>,
    cached_hovered_title: Option<String>,
    cached_peek_command: Option<PeekCommand>,
    /// Peek command locked at capture time — persists through resize,
    /// cleared on reset.
    locked_peek: Option<PeekCommand>,
    input: InteractionState,
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
    /// Monotonic per-cycle OCR request id. Bumped on every dispatch AND on
    /// every BACK/cancel, so a late result from a superseded request is
    /// discarded on pickup — the cycle_gen tag cannot help here because BACK
    /// leaves the same cycle alive.
    ocr_req: u64,
    /// The in-flight recognition job, if any.
    ocr_job: Option<OcrJob>,
    /// A result being held until its release time: `(release_secs, result)`
    /// where `release_secs` is measured on the Scanning anchor. Computed at
    /// pickup by `anim::scan_release_secs`: failures wait only the
    /// anti-flicker floor (warm OCR is 6-35 ms), while a successful outcome
    /// is held until the looping sweep's current pass WRAPS — the one
    /// instant the band is fully off-screen — so the Lifted reveal pass
    /// (fresh anchor, band entering from the top) continues it seamlessly.
    ocr_ready: Option<(f32, Result<OcrOutcome, OcrError>)>,
    /// Defence against the panel's synchronous set swap turning one
    /// physical double-click into two different commands — see
    /// [`PanelSwapGuard`].
    panel_swap: PanelSwapGuard,
}

/// An in-flight OCR recognition: (request id, one-shot result latch,
/// cooperative cancel flag read by the worker around its blocking call).
type OcrJob = (u64, Arc<Latch<Result<OcrOutcome, OcrError>>>, Arc<AtomicBool>);

struct PendingShow {
    ready_count: Arc<AtomicUsize>,
    expected: usize,
    visible_latch: Arc<VisibleLatch>,
}

/// How a capture cycle ended. Logged always; the persistent host also
/// reports it to the parent process as the `finished` event's `action`
/// (serialized snake_case: `select_color` etc.).
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "snake_case")]
pub enum CycleAction {
    Edit,
    Upload,
    SelectColor,
    Video,
    Scroll,
    Copy,
    Save,
    /// OCR mode's COPY: the recognized text went to the clipboard.
    OcrCopy,
    /// OCR mode's SEARCH: a web search for the recognized text was opened.
    OcrSearch,
    /// OCR mode's UPLOAD: the recognized text was handed to the shell to
    /// upload as a paste.
    OcrUpload,
    Cancelled,
}

/// Everything [`App::start_cycle`] needs to arm a new capture cycle.
pub struct CycleSetup {
    pub settings: Arc<CapturerSettings>,
    pub initial_mouse: ScreenPointF,
    /// The in-flight screenshot job. Already resolved in one-shot mode;
    /// still pending when the persistent host arms a cycle.
    pub screenshot_latch: Arc<Latch<Arc<CapturedDesktop>>>,
    pub walker_latch: Arc<Latch<Arc<WindowWalker>>>,
    pub peek_images_latch: Arc<Latch<Vec<Arc<WindowPeekImage>>>>,
    pub ready_count: Arc<AtomicUsize>,
    pub visible_latch: Arc<VisibleLatch>,
    /// See [`CaptureCycle::cycle_gen`].
    pub cycle_gen: u64,
    /// See [`CaptureCycle::cancelled`]; shared with this cycle's
    /// `CycleParams`.
    pub cancelled: Arc<AtomicBool>,
    /// See [`CaptureCycle::timings`]. Must be the same instance the
    /// screenshot/walker jobs were spawned with.
    pub timings: Arc<CaptureTimings>,
}

// ── Free helpers over (warm, cycle) state ───────────────────────────
// Plain functions (not methods) so callers holding a `&mut CaptureCycle`
// split off `self.cycle` can still use them alongside the warm fields.

fn broadcast_mouse_state(windows: &WindowSet, input: &InteractionState) {
    for h in windows.values() {
        h.update_mouse_state(input.virtual_cursor, input.zoom, input.selection, input.captured);
    }
}

/// Take the overlay off screen on the way out of a cycle, handing our
/// foreground rights back to the shell on the way.
///
/// Every action dispatch hides before it writes its payload, and
/// `finish_cycle` hides again behind it — so this is where the capturer
/// stops owning a foreground window, and therefore the last moment
/// `AllowSetForegroundWindow` can succeed. The shell almost always opens
/// something straight afterwards (an editor, the recorder, the scrolling
/// capture driver) and cannot raise any of it without rights it no longer
/// holds. Handing them back costs nothing when nobody needs them: the grant
/// is consumed by a single `SetForegroundWindow` and otherwise expires.
///
/// Deliberately not folded into [`WindowSet::hide_all`], which also serves
/// the transient hides that a retry dialog re-shows from.
fn hide_overlay_for_action(windows: &WindowSet) {
    SystemInterop::hand_foreground_to_shell();
    windows.hide_all();
}

fn update_cursor_visibility(windows: &WindowSet, input: &InteractionState) {
    // Picking a scroll point draws its own scope reticle at the cursor, so
    // the OS pointer has to go — it would sit on top of the reticle it is
    // standing in for. Checked ahead of `captured`, which is always set
    // while picking.
    //
    // Gated on `overlays_visible` for the same reason the reticle is
    // (`ui::shared::scroll_pick_visibility`): the two must agree, or a state
    // that suppresses the reticle while the pointer stays hidden would leave
    // the user with no pointer at all. Unreachable today — Q is swallowed in
    // pick mode — and this keeps it that way if a path is ever added.
    if input.scroll_pick_mode && input.overlays_visible {
        windows.hide_cursors();
    } else if input.captured || input.debug_visible {
        windows.show_cursors();
    } else {
        windows.hide_cursors();
    }
}

fn set_cursor_if_changed(windows: &WindowSet, last_cursor: &mut HashMap<WindowId, CursorIcon>, id: WindowId, cursor: CursorIcon) {
    if last_cursor.get(&id) == Some(&cursor) {
        return;
    }
    if let Some(window) = windows.get(&id) {
        window.set_cursor(cursor);
        last_cursor.insert(id, cursor);
    }
}

/// App-thread mirror of [`crate::ui::shared::panel_visibility`] — the two
/// must agree on every gate, or the app would route clicks to buttons the
/// renderers are not drawing.
///
/// The riskiest half of that agreement — *which* button set is up, and
/// therefore which command each index maps to — is not duplicated here at
/// all: both sides call [`crate::ui::shared::active_panel_set`], so a click
/// on the OCR strip's BACK can never fire whatever the Normal strip has at
/// that index.
fn current_panel_layout(cycle: &CaptureCycle, monitors: &[MonitorInfo]) -> Option<crate::ui::components::panel::layout::PanelLayout> {
    let set = crate::ui::shared::active_panel_set(cycle.input.captured, cycle.input.scroll_pick_mode, &cycle.input.ocr)?;
    let sel = cycle.input.selection?;
    let cx = sel.center_x();
    let cy = sel.center_y();
    let mon = monitors.iter().find(|m| {
        let b = m.bounds;
        cx >= b.left() && cx < b.right() && cy >= b.top() && cy < b.bottom()
    })?;
    crate::ui::components::panel::layout::compute_layout(mon.bounds, sel, mon.scale_factor, set, cycle.settings.panel_features)
}

/// The command Return fires — the panel's invisible default button — or
/// `None` when Return must stay inert.
///
/// Return is gated by the same state the panel is:
/// - While a scroll point is being picked the panel is hidden and every
///   accelerator it owns is swallowed, so Return must be inert too. Without
///   the pick-mode gate it would write a plain screenshot session and end
///   the cycle out from under the scroll capture the user is half-way
///   through configuring. Escape remains the only way out of pick mode.
/// - While OCR lines are lifted the default accept is COPY, the most likely
///   reason the user ran OCR at all. Returning `Edit` here would write a
///   plain screenshot session and destroy the OCR result.
/// - Scanning/Retracting are transitions with nothing to accept yet (or
///   any more), so Return waits them out.
fn default_action(input: &InteractionState) -> Option<Command> {
    if !input.captured || input.scroll_pick_mode {
        return None;
    }
    match input.ocr {
        OcrState::Scanning {
            ..
        }
        | OcrState::Retracting {
            ..
        } => None,
        OcrState::Lifted {
            ..
        } => Some(Command::OcrCopy),
        OcrState::Idle => Some(Command::Edit),
    }
}

/// How long panel-aimed mouse dispatch stays ignored after the visible
/// button set changes: the OS double-click time on Windows — exactly the
/// interval within which the second press of one physical double-click can
/// arrive, and it tracks the user's accessibility settings — with the OS
/// default (500 ms) as the fixed fallback elsewhere.
fn panel_swap_guard_window() -> Duration {
    #[cfg(windows)]
    {
        // GetDoubleClickTime never fails; the clamp is belt-and-braces so a
        // hostile/broken registry value can neither disable the guard (0)
        // nor wedge the panel shut for whole seconds.
        let ms = unsafe { windows::Win32::UI::Input::KeyboardAndMouse::GetDoubleClickTime() };
        Duration::from_millis(u64::from(ms.clamp(100, 2000)))
    }
    #[cfg(not(windows))]
    {
        Duration::from_millis(500)
    }
}

/// Watches which button set the panel is showing and when that last
/// changed, to enforce one property: **a single physical double-click on a
/// panel button must never dispatch two different commands.**
///
/// The set swap is synchronous on the first press, and the strip
/// RE-CENTRES with its own width on every swap (`layout::compute_layout`),
/// so the second press of a double-click lands at an arbitrary position in
/// the new strip — possibly a different button, possibly nothing. Which
/// button is a function of strip widths and selection position, i.e.
/// unpinnable; ignoring panel-aimed mouse dispatch for one double-click
/// interval after ANY swap (including None→Some — a panel materialising
/// under the cursor) closes the entire class at once, independent of
/// geometry. Keyboard accelerators never consult this guard: a key press
/// carries no screen position, so a swap cannot redirect it.
struct PanelSwapGuard {
    /// The set last shown (`None` = panel hidden). Only *changes* arm the
    /// guard — the steady-state broadcast on every mouse move must not
    /// keep re-arming it.
    shown: Option<panel::model::PanelButtonSet>,
    /// When `shown` last changed; `None` until the first change.
    changed_at: Option<Instant>,
}

impl PanelSwapGuard {
    fn new() -> Self {
        Self {
            shown: None,
            changed_at: None,
        }
    }

    /// Record the set currently on screen. Called from
    /// `broadcast_ui_state`, the single choke point every renderer-visible
    /// state mutation already flows through — so no transition can swap
    /// the panel without passing here first.
    ///
    /// `None -> Some` arms the guard too, deliberately: a double-click
    /// that CAPTURES a selection materialises the panel under the cursor,
    /// and its second press would otherwise fire whichever button appeared
    /// there.
    fn observe(&mut self, set: Option<panel::model::PanelButtonSet>, now: Instant) {
        if set != self.shown {
            self.shown = set;
            self.changed_at = Some(now);
        }
    }

    /// Whether a panel-aimed click at `now` must be ignored. Split from
    /// [`Self::blocks_click`] so tests can pin the window arithmetic
    /// without a live OS double-click setting.
    fn blocks_click_within(&self, now: Instant, window: Duration) -> bool {
        // duration_since saturates to zero for out-of-order Instants, so a
        // click stamped before the swap is (correctly) still blocked.
        self.changed_at
            .is_some_and(|t| now.duration_since(t) < window)
    }

    fn blocks_click(&self, now: Instant) -> bool {
        self.blocks_click_within(now, panel_swap_guard_window())
    }
}

fn broadcast_ui_state(windows: &WindowSet, monitors: &[MonitorInfo], ui_monitors: &Arc<[UiMonitor]>, cycle: &mut CaptureCycle) {
    // Feed the double-click swap guard from the same decision function the
    // click routing and the renderers use (`active_panel_set`) — deriving
    // it any other way could let the guard and the panel disagree about
    // when a swap happened. Every renderer-visible state mutation flows
    // through this broadcast, so the guard is armed before any click can
    // be routed against the new set.
    cycle.panel_swap.observe(
        crate::ui::shared::active_panel_set(cycle.input.captured, cycle.input.scroll_pick_mode, &cycle.input.ocr),
        Instant::now(),
    );

    let cursor_pt = to_screen_point(cycle.input.virtual_cursor);

    let hovered_monitor_name = monitors
        .iter()
        .find(|m| m.bounds.contains(cursor_pt))
        .map(|m| m.name.clone());

    let hovered_full = cycle
        .walker
        .as_ref()
        .and_then(|w| w.hit_test_full(cursor_pt));
    cycle.cached_hovered_title = hovered_full
        .as_ref()
        .map(|h| h.title.clone());
    let hovered_window_bounds = hovered_full.as_ref().map(|h| h.rect);
    let hovered_window_index = hovered_full.as_ref().map(|h| h.window_index);
    let hovered_window_obstructed = hovered_full
        .as_ref()
        .is_some_and(|h| h.obstructed);

    // Compute peek command first so UI state can use the peeked window bounds.
    // Peek is suppressed in magnifier mode (overlays hidden) and after
    // a selection has been made (peek_suspended).  When captured, keep
    // the locked peek; otherwise follow hover.
    let new_peek = if !cycle.input.overlays_visible || cycle.input.peek_suspended || cycle.input.dragging {
        cycle.locked_peek.clone()
    } else {
        hovered_full
            .as_ref()
            .filter(|hw| hw.obstructed && cycle.settings.obscured_window_peek_enabled)
            .map(|hw| PeekCommand {
                window_index: hw.window_index,
                window_rect: hw.rect,
                captured: false,
            })
    };

    let state = Arc::new(build_ui_shared_state(UiStateBuildInput {
        monitors: ui_monitors.clone(),
        selection: cycle.input.selection,
        captured: cycle.input.captured,
        mouse_down: cycle.input.mouse_down,
        dragging: cycle.input.dragging,
        zoom: cycle.input.zoom,
        virtual_cursor: cycle.input.virtual_cursor,
        accent_color: cycle.settings.accent_color,
        tips_mode: cycle.input.tips_mode,
        debug_visible: cycle.input.debug_visible,
        overlays_visible: cycle.input.overlays_visible,
        hovered_monitor_name,
        hovered_window_title: cycle.cached_hovered_title.clone(),
        hovered_window_bounds,
        hovered_window_index,
        hovered_window_obstructed,
        peek_window_bounds: new_peek.as_ref().map(|p| p.window_rect),
        cursor_overlay_visible: cycle.input.cursor_overlay_visible,
        desktop_buffer: cycle.desktop_buffer.as_deref(),
        show_scroll_hint: cycle.input.show_scroll_hint,
        has_used_magnifier: cycle.input.has_used_magnifier,
        scroll_pick_mode: cycle.input.scroll_pick_mode,
        // The cycle keeps its copy, so this one is a genuine clone — but
        // the only heap content is the outcome behind an Arc, so the cost
        // is one atomic increment (and one decrement when the previous
        // broadcast's Arc is finally dropped), even on the per-mouse-move
        // path. Everything else in the enum is Copy.
        ocr: cycle.input.ocr.clone(),
        ocr_notice: cycle.input.ocr_notice,
        panel_features: cycle.settings.panel_features,
    }));

    for h in windows.values() {
        h.update_ui_state(state.clone());
    }

    if new_peek != cycle.cached_peek_command {
        for h in windows.values() {
            h.update_peek_state(new_peek.clone());
        }
        cycle.cached_peek_command = new_peek;
    }
}

/// Whether the warm-up monitor topology still matches a fresh enumeration.
/// Order-insensitive (enumeration order is not contractual): the counts
/// must agree and every warm monitor needs an exact counterpart in bounds,
/// scale, primary flag and driving adapter.
fn topology_matches(warm: &[MonitorInfo], fresh: &[MonitorInfo]) -> bool {
    warm.len() == fresh.len()
        && warm.iter().all(|w| {
            fresh.iter().any(|f| {
                f.bounds == w.bounds && f.scale_factor == w.scale_factor && f.is_primary == w.is_primary && f.adapter_id == w.adapter_id
            })
        })
}

impl App {
    pub fn new(
        warmup: Arc<WarmupTimings>,
        instance: Arc<wgpu::Instance>,
        monitors: Vec<MonitorInfo>,
        worker_setups: Vec<WorkerSetup>,
        worker_failed: Arc<AtomicUsize>,
    ) -> Self {
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

        let ui_monitors: Arc<[UiMonitor]> = monitors
            .iter()
            .map(|m| UiMonitor {
                bounds: m.bounds,
                dpi_scale: m.scale_factor,
                is_primary: m.is_primary,
            })
            .collect();

        let workers = worker_setups
            .iter()
            .map(|s| {
                Some(WorkerChannels {
                    input_tx: s.input_tx.clone(),
                    render_msg_tx: s.render_msg_tx.clone(),
                })
            })
            .collect();

        Self {
            workers,
            windows: WindowSet::new(),
            monitors,
            ui_monitors,
            vd_bounds,
            warmup,
            pinch_monitor: None,
            instance,
            worker_setups: Some(worker_setups),
            worker_failed,
            persistent: false,
            host: None,
            cycle: None,
        }
    }

    /// Switch this app into persistent-host mode: cycles park instead of
    /// exiting, and protocol events are emitted on stdout. `parked_count`
    /// is the counter the render workers bump when they first park, used
    /// to detect warm-up completion (`ready`).
    pub fn enable_persistent(&mut self, parked_count: Arc<AtomicUsize>) {
        self.persistent = true;
        self.host = Some(HostState {
            parked_count,
            ready_emitted: false,
            display_change_deadline: None,
        });
    }

    /// Window creation for monitor `i` failed, so its worker will never
    /// receive a handoff: tell the thread to shut down (it disarms its
    /// fail guard and exits its pre-handoff loop, dropping any stashed
    /// `BeginCycle`) and drop our retained senders so no future cycle
    /// queues messages it would never drain. The slot stays in `workers`
    /// (as `None`) and the failure is counted, keeping the show/ready
    /// gates (`ready`/`parked` + `failed` >= `workers.len()`) balanced.
    fn teardown_failed_worker(&mut self, i: usize) {
        self.worker_failed
            .fetch_add(1, Ordering::Release);
        if let Some(w) = self.workers[i].take() {
            let _ = w.input_tx.send(WorkerInput::Shutdown);
        }
    }

    /// Arm a new capture cycle: hide the hardware cursor, re-arm each
    /// window's first-show path, install the per-cycle state. All slow
    /// warm-up (adapters, devices, pipelines) has already happened; the
    /// screenshot/walker jobs for this cycle are already in flight.
    pub fn start_cycle(&mut self, setup: CycleSetup) {
        let primary = self
            .monitors
            .iter()
            .find(|m| m.is_primary)
            .or_else(|| self.monitors.first())
            .expect("at least one monitor present");
        let anchor = ScreenPoint::new(primary.bounds.center_x(), primary.bounds.center_y());

        // Every spawned worker, including torn-down (`None`) slots — those
        // are covered by `worker_failed` in the show gate.
        let expected = self.workers.len();
        let tips_mode = setup.settings.tips_mode_at_startup;
        let cursor_overlay_visible = setup.settings.cursor_visible_at_startup;
        let pending_preselect = setup
            .settings
            .capture_mode
            .is_preselect()
            .then_some(setup.settings.capture_mode);

        // Hidden per cycle, not at window creation: on macOS the hide is
        // global, and a warm (parked) process must not blank the user's
        // cursor.
        set_hardware_cursor_visible(false);
        // Resolved already in one-shot mode; usually still pending in
        // persistent mode (picked up in about_to_wait).
        let desktop_buffer = setup.screenshot_latch.try_get();
        for h in self.windows.values() {
            h.reassert_geometry();
            h.reset_shown();
            h.set_background_image(desktop_buffer.as_deref());
        }

        // Warm the OCR backend off-thread so the first OCR press of the
        // process doesn't pay the MNN model parse + session setup mid-scan
        // (a one-time cost, cached for the process lifetime). Once per
        // process, and only when the OCR button exists at all.
        if setup.settings.panel_features.ocr {
            static OCR_WARM: std::sync::Once = std::sync::Once::new();
            OCR_WARM.call_once(|| {
                if let Err(e) = std::thread::Builder::new()
                    .name("ocr-warm".into())
                    .spawn(ocr::warm)
                {
                    log::warn!("failed to spawn the OCR warm-up thread: {e}");
                }
            });
        }

        self.cycle = Some(CaptureCycle {
            settings: setup.settings,
            cycle_gen: setup.cycle_gen,
            cancelled: setup.cancelled,
            started: setup.timings.t_start,
            timings: setup.timings,
            desktop_buffer,
            screenshot_latch: setup.screenshot_latch,
            screenshot_deadline: Instant::now() + Duration::from_secs(30),
            walker: None,
            walker_latch: setup.walker_latch,
            peek_images_latch: setup.peek_images_latch,
            peek_images: HashMap::new(),
            last_cursor: HashMap::new(),
            cached_hovered_title: None,
            cached_peek_command: None,
            locked_peek: None,
            pending_show: Some(PendingShow {
                ready_count: setup.ready_count,
                expected,
                visible_latch: setup.visible_latch,
            }),
            pending_preselect,
            video_dispatched: false,
            ocr_req: 0,
            ocr_job: None,
            ocr_ready: None,
            panel_swap: PanelSwapGuard::new(),
            input: InteractionState {
                virtual_cursor: setup.initial_mouse,
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
                scroll_pick_mode: false,
                ocr: OcrState::Idle,
                ocr_notice: None,
            },
        });
    }

    /// Tear down the current capture cycle: hide every window, restore the
    /// hardware cursor, return the workers to their parked state and drop
    /// the per-cycle state. Exits the event loop unless `persistent`,
    /// where it instead reports `finished` and parks the event loop.
    fn finish_cycle(&mut self, event_loop: &ActiveEventLoop, action: CycleAction) {
        log::info!("capture cycle finished: {:?}", action);
        // Every exit path the capturer has — a shutdown command, the parent
        // dying, a display-topology respawn — comes through here with a live
        // cycle, so this is also the last hide before the process itself
        // goes away. Hence the foreground handback rather than a bare hide,
        // even though the action dispatches have usually done it already.
        hide_overlay_for_action(&self.windows);
        // Idempotent (guarded by a static in window.rs) even when cursors
        // were already restored via update_cursor_visibility.
        set_hardware_cursor_visible(true);
        if let Some(cycle) = self.cycle.take() {
            // A worker may not have consumed this cycle's BeginCycle yet
            // (the screenshot job broadcasts it from its own thread — e.g.
            // cancel/timeout before the capture landed), or may still be
            // blocked in visible_latch.wait(). Mark the cycle cancelled
            // *before* releasing the latch so no worker can wedge on the
            // dead cycle; workers re-check the flag after the wait.
            cycle
                .cancelled
                .store(true, Ordering::Release);
            // A recognition may still be running; flag it cancelled so the
            // worker skips setting a latch nobody will read. NEVER joined:
            // this path also serves ParentGone and topology restarts, and a
            // cold recognize can take hundreds of ms — blocking here would
            // strand a visible overlay behind a dead cycle.
            if let Some((_, _, cancel)) = &cycle.ocr_job {
                cancel.store(true, Ordering::Release);
            }
            if let Some(pending) = &cycle.pending_show {
                pending.visible_latch.signal_all();
            }
            for w in self.workers.iter().flatten() {
                let _ = w.render_msg_tx.send(RenderMsg::EndCycle {
                    cycle_gen: cycle.cycle_gen,
                });
            }
        }
        for h in self.windows.values() {
            h.set_background_image(None);
        }
        if self.persistent {
            host::emit(&HostEvent::Finished {
                action,
            });
            // Nothing animates between cycles — sleep until the next
            // command (or other event) arrives.
            event_loop.set_control_flow(ControlFlow::Wait);
        } else {
            event_loop.exit();
        }
    }

    /// Non-blocking pickup of this cycle's desktop screenshot, with the
    /// 30s bound one-shot mode applies before `start_cycle`. Persistent
    /// mode arms the cycle before the screenshot exists, so it lands here:
    /// no-op once picked up (and always in one-shot mode, where the latch
    /// resolved before the cycle started).
    fn try_pick_up_screenshot(&mut self, event_loop: &ActiveEventLoop) {
        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        if cycle.desktop_buffer.is_some() {
            return;
        }
        if let Some(buf) = cycle.screenshot_latch.try_get() {
            for h in self.windows.values() {
                h.set_background_image(Some(&buf));
            }
            cycle.desktop_buffer = Some(buf);
        } else if Instant::now() >= cycle.screenshot_deadline {
            error!("timed out waiting for the desktop screenshot; cancelling the capture cycle");
            if self.persistent {
                host::emit(&HostEvent::FatalError {
                    message: "timed out waiting for the desktop screenshot".into(),
                });
            }
            self.finish_cycle(event_loop, CycleAction::Cancelled);
        }
    }

    /// Dispatch one parsed stdin command (persistent mode only — the
    /// stdin reader is the sole producer of these).
    fn handle_host_command(&mut self, event_loop: &ActiveEventLoop, cmd: HostCommand) {
        match cmd {
            HostCommand::Show(params) => self.handle_show(event_loop, params),
            HostCommand::Cancel => {
                // Acts like Escape.
                if self.cycle.is_some() {
                    self.finish_cycle(event_loop, CycleAction::Cancelled);
                } else {
                    info!("cancel command ignored: no capture cycle active");
                }
            }
            HostCommand::Ping => host::emit(&HostEvent::Pong),
            HostCommand::Shutdown => {
                info!("shutdown command received; exiting");
                if self.cycle.is_some() {
                    self.finish_cycle(event_loop, CycleAction::Cancelled);
                }
                // Graceful: unwinds the event loop, drops the workers'
                // channels and joins their threads on the way out.
                event_loop.exit();
            }
        }
    }

    /// An OS display-topology notification arrived (`host::display`).
    /// One change produces a burst of messages, so arm/extend a
    /// coalescing deadline instead of restarting immediately;
    /// [`check_display_change`](Self::check_display_change) acts once it
    /// expires.
    fn handle_display_change(&mut self, event_loop: &ActiveEventLoop) {
        // One-shot mode installs no observers, but guard anyway — its
        // display-change behaviour must stay untouched.
        let Some(hs) = self.host.as_mut() else {
            return;
        };
        if hs.display_change_deadline.is_none() {
            info!("display topology change reported; restarting after a {DISPLAY_CHANGE_DEBOUNCE:?} debounce");
        }
        let deadline = Instant::now() + DISPLAY_CHANGE_DEBOUNCE;
        hs.display_change_deadline = Some(deadline);
        if self.cycle.is_none() {
            // Idle: the loop is parked in Wait — make sure it wakes to
            // act on the deadline. A running cycle Polls and gets there
            // on its own.
            event_loop.set_control_flow(ControlFlow::WaitUntil(deadline));
        }
    }

    /// Act on an expired display-change debounce (armed by
    /// [`handle_display_change`](Self::handle_display_change)); called
    /// every `about_to_wait` pass.
    fn check_display_change(&mut self, event_loop: &ActiveEventLoop) {
        let Some(deadline) = self
            .host
            .as_ref()
            .and_then(|hs| hs.display_change_deadline)
        else {
            return;
        };
        if Instant::now() < deadline {
            // Still coalescing: keep the idle loop ticking toward the
            // deadline (re-asserted here because earlier control-flow
            // writes in about_to_wait may have parked the loop).
            if self.cycle.is_none() {
                event_loop.set_control_flow(ControlFlow::WaitUntil(deadline));
            }
            return;
        }
        // Debounce expired — but fullscreen-exclusive transitions, RDP and
        // mode-setting screensavers broadcast display-change notifications
        // whose *final* topology is unchanged. Re-verify before killing a
        // healthy warm host (off the hot path, so the enumeration cost is
        // irrelevant); handle_show applies the same guard.
        let fresh = SystemInterop::all_monitors();
        if topology_matches(&self.monitors, &fresh) {
            info!("display-change debounce expired but the topology still matches; staying warm");
            if let Some(hs) = self.host.as_mut() {
                hs.display_change_deadline = None;
                // Idle again: replace the (now past) WaitUntil so the loop
                // doesn't spin on an expired deadline. During warm-up the
                // ready gate's own WaitUntil tick must survive.
                if self.cycle.is_none() && hs.ready_emitted {
                    event_loop.set_control_flow(ControlFlow::Wait);
                }
            }
            return;
        }
        self.restart_for_topology_change(event_loop, EXIT_DISPLAY_CHANGED, "the display topology changed");
    }

    /// A render worker's wgpu device died (driver reset/update). That
    /// worker can never serve another cycle, so restart the whole host.
    /// No debounce — a single lost device is already fatal to the warm
    /// state.
    fn handle_gpu_lost(&mut self, event_loop: &ActiveEventLoop) {
        // Never signalled in one-shot mode (no callback registered), but
        // guard anyway — its device-loss behaviour must stay untouched.
        if self.host.is_none() {
            return;
        }
        self.restart_for_topology_change(event_loop, EXIT_GPU_LOST, "a render worker's GPU device was lost");
    }

    /// Common exit path for "the warm state no longer matches the world":
    /// finish any active cycle as cancelled (hides windows, restores the
    /// cursor, reports `finished` to the parent), emit `display_changed`
    /// and exit with `code` (`EXIT_DISPLAY_CHANGED` / `EXIT_GPU_LOST`).
    /// The shell respawns us immediately — no backoff — and cold-spawns
    /// any capture requested before the fresh host is ready.
    fn restart_for_topology_change(&mut self, event_loop: &ActiveEventLoop, code: i32, why: &str) -> ! {
        warn!("{why}; exiting for respawn (exit code {code})");
        if self.cycle.is_some() {
            self.finish_cycle(event_loop, CycleAction::Cancelled);
        }
        host::emit(&HostEvent::DisplayChanged);
        std::process::exit(code);
    }

    /// Persistent-mode `show`: run the per-capture fast path — everything
    /// `CaptureSession::new` does *after* its warm-up — and arm a cycle.
    /// The screenshot is not waited for here (see
    /// [`try_pick_up_screenshot`](Self::try_pick_up_screenshot)).
    fn handle_show(&mut self, event_loop: &ActiveEventLoop, params: ShowParams) {
        if self.cycle.is_some() {
            warn!("show command ignored: a capture cycle is already active");
            return;
        }

        // Belt-and-braces topology verify: the OS notifications can lag
        // (or be missed outright), and an overlay laid out for monitors
        // that no longer exist must never reach the screen. Exiting
        // *before* any window shows makes the parent cold-spawn this
        // capture and respawn the host against the new topology.
        let fresh = SystemInterop::all_monitors();
        if !topology_matches(&self.monitors, &fresh) {
            warn!(
                "monitor topology changed since warm-up ({} monitors -> {})",
                self.monitors.len(),
                fresh.len()
            );
            self.restart_for_topology_change(event_loop, EXIT_DISPLAY_CHANGED, "the show-time topology check failed");
        } else if let Some(hs) = self.host.as_mut() {
            // A pending debounced notification whose enumeration still
            // matches was a no-op change (or a change-and-revert): drop
            // it rather than tearing down a healthy host mid-capture.
            if hs.display_change_deadline.take().is_some() {
                info!("show-time topology check passed; dropping the pending display-change restart");
            }
        }

        let settings = Arc::new(params.into_settings());
        if let Some(dir) = &settings.session_dir {
            info!("show: session payload will be written to {:?}", dir);
        }

        let initial_mouse = SystemInterop::get_mouse_position(&self.monitors);
        let initial_mouse_f = ScreenPointF::new(initial_mouse.x as f32, initial_mouse.y as f32);
        let captured_cursor = SystemInterop::capture_cursor(&self.monitors);

        // Fresh gate + latch per cycle — nothing ever needs re-arming. The
        // cycle's debug timings anchor here (the `show` command), so the
        // idle gap since warm-up never leaks into a per-cycle metric.
        let timings = Arc::new(CaptureTimings::new(self.monitors.len()));
        let ready_count = Arc::new(AtomicUsize::new(0));
        let visible_latch = Arc::new(VisibleLatch::new());
        let cycle_gen = next_cycle_gen();
        let cancelled = Arc::new(AtomicBool::new(false));
        let input_txs: Vec<_> = self
            .workers
            .iter()
            .flatten()
            .map(|w| w.input_tx.clone())
            .collect();
        let render_msg_txs: Vec<_> = self
            .workers
            .iter()
            .flatten()
            .map(|w| w.render_msg_tx.clone())
            .collect();

        let screenshot_latch = spawn_screenshot_job(ScreenshotJobParams {
            monitors: self.monitors.clone(),
            cursor: captured_cursor,
            input_txs,
            render_msg_txs: render_msg_txs.clone(),
            peek_enabled: settings.obscured_window_peek_enabled,
            accent_color: settings.accent_color,
            initial_mouse: initial_mouse_f,
            ready_count: ready_count.clone(),
            visible_latch: visible_latch.clone(),
            cycle_gen,
            cancelled: cancelled.clone(),
            timings: timings.clone(),
        });
        let (walker_latch, peek_images_latch) = spawn_walker_job(
            self.monitors.clone(),
            render_msg_txs,
            cycle_gen,
            settings.obscured_window_peek_enabled,
            settings.obscured_window_detection_threshold,
            timings.clone(),
        );

        self.start_cycle(CycleSetup {
            settings,
            initial_mouse: initial_mouse_f,
            screenshot_latch,
            walker_latch,
            peek_images_latch,
            ready_count,
            visible_latch,
            cycle_gen,
            cancelled,
            timings,
        });

        // A cycle renders/polls continuously; finish_cycle restores Wait.
        event_loop.set_control_flow(ControlFlow::Poll);
    }

    fn ensure_peek_images(&mut self) {
        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        if cycle.peek_images.is_empty() {
            if let Some(images) = cycle.peek_images_latch.try_get() {
                for img in images.iter() {
                    cycle
                        .peek_images
                        .insert(img.window_index, img.clone());
                }
            }
        }
    }

    fn apply_zoom_factor(&mut self, factor: f32) {
        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        let effects = InteractionController::apply_zoom_factor(&mut cycle.input, factor);
        self.apply_interaction_effects(effects, None);
    }

    fn show_all_windows(&self) {
        self.windows.show_all();
        if let Some(cycle) = self.cycle.as_ref() {
            update_cursor_visibility(&self.windows, &cycle.input);
        }
    }

    /// Try to pick up the walker result (non-blocking). If not ready yet,
    /// selection stays `None` and updates on first cursor move.
    fn try_pick_up_walker(&mut self) {
        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        if cycle.walker.is_none() {
            if let Some(w) = cycle.walker_latch.try_get() {
                let pt = ScreenPoint::new(
                    cycle.input.virtual_cursor.x.round() as i32,
                    cycle.input.virtual_cursor.y.round() as i32,
                );
                cycle.input.selection = w.hit_test(pt);
                cycle.walker = Some(w);
            }
        }
    }

    /// Non-blocking pickup of the OCR worker's result, plus the OCR phase
    /// clock. Polled every `about_to_wait` pass beside the walker/screenshot
    /// pickups — the cycle already runs `ControlFlow::Poll`, so no WaitUntil
    /// machinery is needed for any of the time-gated transitions below.
    fn try_advance_ocr(&mut self) {
        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        // Result pickup. A finished job is always consumed, but whether the
        // result is USED depends on its id still matching the current
        // Scanning phase: BACK bumps `ocr_req` while the same cycle stays
        // alive, which the cycle_gen tag could never detect.
        if let Some((job_req, latch, _)) = cycle.ocr_job.as_ref() {
            if let Some(result) = latch.try_get() {
                let job_req = *job_req;
                cycle.ocr_job = None;
                if let OcrState::Scanning {
                    req,
                    anchor,
                    ..
                } = &cycle.input.ocr
                {
                    if *req == job_req {
                        // Held (not applied) until its release time. A
                        // successful outcome waits for the sweep's current
                        // pass to wrap so the Lifted reveal pass picks up
                        // with the band off-screen (no mid-region
                        // teleport); failures wait only the anti-flicker
                        // floor — nothing is going to be revealed, so
                        // dragging the error out a full pass would be
                        // pure latency.
                        let align = matches!(&result, Ok(o) if !o.lines.is_empty());
                        let release = ocr::anim::scan_release_secs(anchor.elapsed().as_secs_f32(), align);
                        cycle.ocr_ready = Some((release, result));
                    } else {
                        log::info!("discarding OCR result for superseded request {job_req}");
                    }
                } else {
                    log::info!("discarding OCR result for superseded request {job_req}");
                }
            }
        }
        match &cycle.input.ocr {
            OcrState::Scanning {
                anchor,
                req,
                region,
            } => {
                let (anchor, req, region) = (*anchor, *req, *region);
                let elapsed = anchor.elapsed().as_secs_f32();
                if cycle
                    .ocr_ready
                    .as_ref()
                    .is_none_or(|(release, _)| elapsed < *release)
                {
                    return;
                }
                let Some((_, result)) = cycle.ocr_ready.take() else {
                    return;
                };
                match result {
                    Ok(outcome) if !outcome.lines.is_empty() => {
                        // ONE dpi_scale for all lift geometry — the monitor
                        // containing the region's centre — so a line
                        // crossing a mixed-DPI seam moves by the same
                        // physical amount on both halves instead of tearing.
                        let cx = region.center_x();
                        let cy = region.center_y();
                        let dpi_scale = self
                            .monitors
                            .iter()
                            .find(|m| {
                                let b = m.bounds;
                                cx >= b.left() && cx < b.right() && cy >= b.top() && cy < b.bottom()
                            })
                            .map(|m| m.scale_factor)
                            .unwrap_or(1.0);
                        // text_angle is deliberately logged and nowhere else
                        // consumed: the lift draws axis-aligned quads, so a
                        // skewed page still lifts straight. The number is here
                        // so a "the lift looks wrong on this page" report can
                        // be diagnosed from a log instead of a repro.
                        log::info!(
                            "OCR recognized {} lines (text angle {:.2} deg)",
                            outcome.lines.len(),
                            outcome.text_angle
                        );
                        // Fresh anchor: the reveal pass's t=0 is NOW — and
                        // NOW is (poll latency aside) the instant the
                        // scanning sweep wrapped, because the release
                        // above was pass-aligned. The band re-enters from
                        // above the region top exactly as the old pass
                        // exited below its bottom.
                        cycle.input.ocr = OcrState::Lifted {
                            anchor: Instant::now(),
                            // The Scanning phase's request id, carried over:
                            // the render side keys its shaped-bubble cache
                            // on it (see OcrState::Lifted).
                            req,
                            region,
                            dpi_scale,
                            outcome: Arc::new(outcome),
                        };
                        // Entering the mode clears any stale failure pill —
                        // it would sit there contradicting the lifted lines.
                        cycle.input.ocr_notice = None;
                    }
                    // All three failure shapes land back in the plain
                    // captured state — never in OCR mode — with a transient
                    // notice pill instead of a dialog: the overlay is
                    // topmost and fullscreen, so a dialog would open BEHIND
                    // it (every existing dialog call site hides the overlay
                    // first), and a silent return would be indistinguishable
                    // from a broken button.
                    Ok(_) => {
                        log::info!("OCR found no text");
                        cycle.input.ocr = OcrState::Idle;
                        cycle.input.ocr_notice = Some(OcrNotice {
                            anchor: Instant::now(),
                            kind: OcrNoticeKind::NoText,
                        });
                    }
                    Err(OcrError::Unavailable) => {
                        // The recognizer could not be spawned, or its MNN
                        // engine failed to init — either cause is already
                        // logged at error level (ocr::client / clowd_ocr).
                        log::warn!("OCR is unavailable on this machine");
                        cycle.input.ocr = OcrState::Idle;
                        cycle.input.ocr_notice = Some(OcrNotice {
                            anchor: Instant::now(),
                            kind: OcrNoticeKind::Unavailable,
                        });
                    }
                    Err(OcrError::Failed(msg)) => {
                        // The detail goes to the log only — a WinRT HRESULT
                        // string is noise on screen.
                        log::warn!("OCR failed: {msg}");
                        cycle.input.ocr = OcrState::Idle;
                        cycle.input.ocr_notice = Some(OcrNotice {
                            anchor: Instant::now(),
                            kind: OcrNoticeKind::Failed,
                        });
                    }
                }
                broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
            }
            OcrState::Retracting {
                anchor,
                ..
            } => {
                if anchor.elapsed().as_secs_f32() >= ocr::anim::RETRACT_DURATION_SECS {
                    cycle.input.ocr = OcrState::Idle;
                    broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
                }
            }
            OcrState::Idle
            | OcrState::Lifted {
                ..
            } => {}
        }
    }

    /// Leave OCR mode one step: Scanning cancels the job outright, Lifted
    /// drops every bubble/crop AT ONCE and starts the region's colour
    /// fade, Retracting (Escape pressed twice) skips straight to Idle.
    /// Serves both the panel's BACK button and Escape.
    ///
    /// Deliberately narrow, modelled on the scroll-pick Escape arm: it must
    /// NOT touch `InteractionController::reset` (which would destroy the
    /// selection BACK exists to preserve) and must NOT hide the overlay —
    /// the user lands back on the captured panel, not on the desktop.
    fn exit_ocr_mode(&mut self, window_id: WindowId) {
        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        match std::mem::replace(&mut cycle.input.ocr, OcrState::Idle) {
            OcrState::Idle => return,
            OcrState::Scanning {
                ..
            } => {
                // Cooperative cancel: the worker checks the flag around its
                // blocking recognize call and the latch is simply dropped.
                // Never joined — this runs on the winit thread and a cold
                // recognize can take hundreds of ms.
                if let Some((_, _, cancel)) = cycle.ocr_job.take() {
                    cancel.store(true, Ordering::Release);
                }
                cycle.ocr_ready = None;
            }
            OcrState::Lifted {
                region,
                ..
            } => {
                // Fresh anchor: the exit fade starts NOW. The outcome is
                // deliberately dropped here — the text vanishes on this
                // very frame (no reverse animation, by design) and only
                // the region's fade back to colour remains to play.
                cycle.input.ocr = OcrState::Retracting {
                    anchor: Instant::now(),
                    region,
                };
            }
            // Second Escape mid-retract: the user wants out, not a replay —
            // the replace above already snapped to Idle.
            OcrState::Retracting {
                ..
            } => {}
        }
        // Bump the request id on EVERY exit path so a result still in
        // flight is discarded at pickup — within a live cycle this counter
        // is the only stale-result guard there is.
        cycle.ocr_req += 1;
        // The mode forced a Default cursor over the frozen selection;
        // restore whatever the current hit-test says.
        let cursor = cycle.input.hittest.cursor();
        set_cursor_if_changed(&self.windows, &mut cycle.last_cursor, window_id, cursor);
        broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
    }

    fn finalise_selection(&mut self, rect: ScreenRect, event_loop: &ActiveEventLoop, window_id: WindowId) {
        self.finalise_selection_inner(rect, event_loop, window_id, false);
    }

    fn finalise_selection_with_peek(&mut self, rect: ScreenRect, event_loop: &ActiveEventLoop, window_id: WindowId) {
        self.finalise_selection_inner(rect, event_loop, window_id, true);
    }

    fn finalise_selection_inner(&mut self, rect: ScreenRect, event_loop: &ActiveEventLoop, window_id: WindowId, lock_peek: bool) {
        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        if lock_peek {
            cycle.locked_peek = cycle
                .cached_peek_command
                .as_ref()
                .map(|cmd| PeekCommand {
                    window_index: cmd.window_index,
                    window_rect: cmd.window_rect,
                    captured: true,
                });
        } else {
            cycle.locked_peek = None;
        }
        cycle.input.peek_suspended = true;

        let effects = InteractionController::finalize_selection(&mut cycle.input, rect, &self.monitors);
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
        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        if cycle.settings.video_mode && !cycle.video_dispatched {
            cycle.video_dispatched = true;
            self.dispatch_command(Command::Video, event_loop, window_id);
        }
    }

    /// Target rect for a `--capture-mode screen|window` pre-selection.
    /// `Screen` is the monitor under the cursor; `Window` is the foreground
    /// window, falling back to the active screen when no foreground window is
    /// available. `Region` never pre-selects.
    fn preselect_rect(&self, mode: CaptureMode) -> Option<ScreenRect> {
        let cycle = self.cycle.as_ref()?;
        let active_screen = || {
            let pt = to_screen_point(cycle.input.virtual_cursor);
            self.monitors
                .iter()
                .find(|m| m.bounds.contains(pt))
                .map(|m| m.bounds)
        };
        match mode {
            CaptureMode::Region => None,
            CaptureMode::Screen => active_screen(),
            CaptureMode::Window => cycle
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
        let Some(cycle) = self.cycle.as_ref() else {
            return;
        };
        let Some(mode) = cycle.pending_preselect else {
            return;
        };
        // Wait until the overlay is up (panel needs a shown window to render).
        if cycle.pending_show.is_some() {
            return;
        }
        // Window mode targets the foreground window, which comes from the walker.
        if matches!(mode, CaptureMode::Window) && cycle.walker.is_none() {
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
        // finalise_selection may have ended the cycle (--video auto-dispatch).
        if let Some(cycle) = self.cycle.as_mut() {
            cycle.pending_preselect = None;
        }
    }

    fn handle_reset(&mut self, window_id: WindowId) {
        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        cycle.locked_peek = None;
        cycle.input.peek_suspended = false;
        let effects = InteractionController::reset(&mut cycle.input);
        self.apply_interaction_effects(effects, Some(window_id));

        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        let pt = ScreenPoint::new(
            cycle.input.virtual_cursor.x.round() as i32,
            cycle.input.virtual_cursor.y.round() as i32,
        );
        cycle.input.selection = cycle
            .walker
            .as_ref()
            .and_then(|w| w.hit_test(pt));

        broadcast_mouse_state(&self.windows, &cycle.input);

        log::info!("selection reset");
    }

    fn apply_interaction_effects(&mut self, effects: InteractionEffects, window_id: Option<WindowId>) {
        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        if effects.update_cursor_visibility {
            update_cursor_visibility(&self.windows, &cycle.input);
        }
        if let Some(pos) = effects.restore_mouse {
            SystemInterop::set_mouse_position(pos, &self.monitors);
        }
        if let (Some(window_id), Some(cursor)) = (window_id, effects.set_cursor) {
            set_cursor_if_changed(&self.windows, &mut cycle.last_cursor, window_id, cursor);
        }
        if effects.broadcast_ui {
            broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
        }
        if effects.broadcast_mouse {
            broadcast_mouse_state(&self.windows, &cycle.input);
        }
    }

    /// Complete a scroll-point pick — the click that finishes what
    /// [`Command::ScrollCapture`] armed.
    ///
    /// Same shape as the Video dispatch arm (hide the overlay, write the
    /// action payload, finish the cycle), with the picked point and the
    /// window under it added to the marker. A click outside the selection
    /// is ignored and leaves pick mode armed: the driver may only aim the
    /// wheel inside the region it is going to stitch, and re-clicking is a
    /// friendlier correction than dropping the user back to the panel.
    fn dispatch_scroll_pick(&mut self, event_loop: &ActiveEventLoop) {
        use xdialog::XDialogIcon::Error as ErrorIcon;

        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        let Some(session_dir) = cycle.settings.session_dir.clone() else {
            log::info!("scroll pick ignored: no --session-dir provided");
            return;
        };
        let Some(selection) = cycle.input.selection else {
            log::info!("scroll pick ignored: no selection");
            return;
        };
        let point = to_screen_point(cycle.input.virtual_cursor);
        if !selection.contains(point) {
            log::info!("scroll pick ignored: click outside the selection");
            return;
        }
        // A locked peek *is* the answer to "which window did the user mean?".
        // It is only ever set when they selected a window that something else
        // is covering — the peek is what draws that window's real content in
        // the overlay — so it names a window that is not topmost at every
        // point of its own bounds. Asking what is at the scroll point instead
        // would name the obstruction, and the driver raises, scrolls and
        // photographs whatever it is told: a tall, plausible picture of the
        // wrong window.
        //
        // Without a peek, whatever is under the point is exactly what the
        // user saw when they clicked, and it is the right target. No walker
        // (its snapshot is still in flight) is not an error either: `0` tells
        // the driver to resolve the target itself, from the point, once the
        // overlay is out of the way.
        let hwnd = cycle
            .walker
            .as_ref()
            .and_then(|w| {
                cycle
                    .locked_peek
                    .as_ref()
                    // Outside the peeked window the user has aimed at
                    // something else entirely — a selection wider than the
                    // window, say — and the point is the only intent there is.
                    .filter(|peek| peek.window_rect.contains(point))
                    .and_then(|peek| w.hwnd_at_index(peek.window_index))
                    .or_else(|| w.top_level_hwnd_at(point))
            })
            .unwrap_or(0);

        // The foreground handback in here matters most on exactly this
        // action: the shell passes those rights straight to the scrolling
        // capture driver, which needs them to raise the window the user
        // picked over whatever is covering it.
        hide_overlay_for_action(&self.windows);
        match write_scroll_action(&session_dir, selection, point, hwnd, &self.monitors) {
            ActionResult::Success => self.finish_cycle(event_loop, CycleAction::Scroll),
            ActionResult::Cancelled => self.show_all_windows(),
            ActionResult::Failed(msg) => {
                // Retry re-shows the overlay still in pick mode, so the
                // user lands back on the crosshair, not on the panel.
                if xdialog::show_message_retry_cancel("Clowd Capture", "Scrolling Capture Failed", &msg, ErrorIcon).unwrap_or(false) {
                    self.show_all_windows();
                } else {
                    self.finish_cycle(event_loop, CycleAction::Cancelled);
                }
            }
        }
    }

    fn dispatch_command(&mut self, command: Command, event_loop: &ActiveEventLoop, window_id: WindowId) {
        use xdialog::XDialogIcon::Error as ErrorIcon;
        log::info!("dispatch command: {:?}", command);

        self.ensure_peek_images();
        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };
        let active_peek_image = cycle.locked_peek.as_ref().and_then(|cmd| {
            cycle
                .peek_images
                .get(&cmd.window_index)
                .map(|img| img.as_ref())
        });

        let cursor = cycle
            .desktop_buffer
            .as_ref()
            .and_then(|buf| buf.cursor.as_ref());
        let cursor_visible = cycle.input.cursor_overlay_visible;

        match command {
            Command::Copy => {
                hide_overlay_for_action(&self.windows);
                let result = match (cycle.input.selection, cycle.desktop_buffer.as_deref()) {
                    (Some(sel), Some(buf)) => copy_to_clipboard_with_peek(sel, buf, active_peek_image, cursor, cursor_visible),
                    _ => ActionResult::Failed("No selection or buffer".into()),
                };
                match result {
                    ActionResult::Success => self.finish_cycle(event_loop, CycleAction::Copy),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Copy to Clipboard Failed", &msg, ErrorIcon).unwrap_or(false)
                        {
                            self.show_all_windows();
                        } else {
                            self.finish_cycle(event_loop, CycleAction::Cancelled);
                        }
                    }
                }
            }
            Command::Save => {
                hide_overlay_for_action(&self.windows);
                let result = match (cycle.input.selection, cycle.desktop_buffer.as_deref()) {
                    (Some(sel), Some(buf)) => match self.windows.get(&window_id) {
                        Some(handle) => handle.save_to_file_with_peek(sel, buf, active_peek_image, cursor, cursor_visible),
                        None => ActionResult::Failed("No active window".into()),
                    },
                    _ => ActionResult::Failed("No selection or buffer".into()),
                };
                match result {
                    ActionResult::Success => self.finish_cycle(event_loop, CycleAction::Save),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Save Failed", &msg, ErrorIcon).unwrap_or(false) {
                            self.show_all_windows();
                        } else {
                            self.finish_cycle(event_loop, CycleAction::Cancelled);
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
                let Some(session_dir) = cycle.settings.session_dir.clone() else {
                    log::info!("command {:?} ignored: no --session-dir provided", command);
                    return;
                };
                let (action, cycle_action) = if command == Command::Upload {
                    (SessionAction::Upload, CycleAction::Upload)
                } else {
                    (SessionAction::Edit, CycleAction::Edit)
                };
                hide_overlay_for_action(&self.windows);
                let result = match (cycle.input.selection, cycle.desktop_buffer.as_deref()) {
                    (Some(sel), Some(buf)) => write_session(&session_dir, sel, buf, active_peek_image, cursor_visible, action),
                    _ => ActionResult::Failed("No selection or buffer".into()),
                };
                match result {
                    ActionResult::Success => self.finish_cycle(event_loop, cycle_action),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Session Capture Failed", &msg, ErrorIcon).unwrap_or(false) {
                            self.show_all_windows();
                        } else {
                            self.finish_cycle(event_loop, CycleAction::Cancelled);
                        }
                    }
                }
            }
            Command::SelectColor => {
                // H in crosshair mode (DxScreenCapture.cpp:1223): report
                // the pixel under the cursor to the shell — it opens its
                // color viewer. Without a shell there is nothing to show
                // the color in — ignore.
                let Some(session_dir) = cycle.settings.session_dir.clone() else {
                    log::info!("command SelectColor ignored: no --session-dir provided");
                    return;
                };
                let sampled = cycle
                    .desktop_buffer
                    .as_deref()
                    .and_then(|buf| sample_bgra(buf, to_screen_point(cycle.input.virtual_cursor)));
                let Some(bgra) = sampled else {
                    log::warn!("command SelectColor ignored: cursor is not over the desktop bitmap");
                    return;
                };
                hide_overlay_for_action(&self.windows);
                match write_color_action(&session_dir, bgra[2], bgra[1], bgra[0]) {
                    ActionResult::Success => self.finish_cycle(event_loop, CycleAction::SelectColor),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Color Capture Failed", &msg, ErrorIcon).unwrap_or(false) {
                            self.show_all_windows();
                        } else {
                            self.finish_cycle(event_loop, CycleAction::Cancelled);
                        }
                    }
                }
            }
            Command::Reset => self.handle_reset(window_id),
            Command::Exit => {
                self.finish_cycle(event_loop, CycleAction::Cancelled);
            }
            Command::Video => {
                // Mirrors Edit|Upload: writes the video action payload
                // (poster + `action.txt`) for the shell to start recording.
                // Without a --session-dir there is no shell listening —
                // ignore. (DESIGN §3.2/§3.3.)
                let Some(session_dir) = cycle.settings.session_dir.clone() else {
                    log::info!("command Video ignored: no --session-dir provided");
                    return;
                };
                hide_overlay_for_action(&self.windows);
                let result = match (cycle.input.selection, cycle.desktop_buffer.as_deref()) {
                    (Some(sel), Some(buf)) => write_video_action(&session_dir, sel, buf, cursor_visible, &self.monitors),
                    _ => ActionResult::Failed("No selection or buffer".into()),
                };
                match result {
                    ActionResult::Success => self.finish_cycle(event_loop, CycleAction::Video),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Video Capture Failed", &msg, ErrorIcon).unwrap_or(false) {
                            self.show_all_windows();
                        } else {
                            self.finish_cycle(event_loop, CycleAction::Cancelled);
                        }
                    }
                }
            }
            Command::ScrollCapture => {
                // SCROLL needs one more input than every other panel
                // command — the point the driver parks the cursor at — so
                // it does not write its payload here. It arms pick mode
                // and leaves the overlay up; the click handler below is
                // what writes `action.txt` and ends the cycle.
                //
                // Like Video, without a --session-dir there is no shell to
                // hand the session to, and without a captured selection
                // there is no region to scroll inside — ignore either way.
                if cycle.settings.session_dir.is_none() {
                    log::info!("command ScrollCapture ignored: no --session-dir provided");
                    return;
                }
                if !cycle.input.captured || cycle.input.selection.is_none() {
                    log::info!("command ScrollCapture ignored: no captured selection");
                    return;
                }
                // One mode at a time — the symmetric twin of the
                // Command::Ocr guard below. The only live path here is the
                // Normal strip's SCROLL button during Retracting (BACK
                // hands the Normal strip back while the exit fade still
                // owns the frozen selection); honouring it would arm
                // scroll-pick mid-fade. It also backstops the double-click
                // hazard: the strip recentres on the swap, so a stray
                // second press can land on ANY Normal button, and while
                // the swap guard covers the double-click window, the
                // 0.18 s fade can outlive it on slow double-click
                // settings.
                if cycle.input.ocr.active() {
                    log::info!("command ScrollCapture ignored: OCR mode is active");
                    return;
                }
                cycle.input.scroll_pick_mode = true;
                // Crosshair is the fallback if the hardware hide below
                // fails; the reticle is the real pointer from here on.
                set_cursor_if_changed(&self.windows, &mut cycle.last_cursor, window_id, CursorIcon::Crosshair);
                update_cursor_visibility(&self.windows, &cycle.input);
                broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
            }
            Command::Ocr => {
                // OCR reads a frozen region; without a captured selection
                // there is nothing to recognize. No session_dir requirement:
                // COPY and SEARCH work standalone, and UPLOAD checks at its
                // own dispatch (the Command::Video precedent).
                if !cycle.input.captured || cycle.input.selection.is_none() {
                    log::info!("command Ocr ignored: no captured selection");
                    return;
                }
                // One recognition at a time. The only live-mode path here is
                // the Normal strip's OCR button during Retracting; honouring
                // it would tear the retract animation out from under the
                // render workers mid-flight.
                if cycle.input.ocr.active() {
                    log::info!("command Ocr ignored: OCR mode already active");
                    return;
                }
                let (Some(sel), Some(buf)) = (cycle.input.selection, cycle.desktop_buffer.as_deref()) else {
                    log::info!("command Ocr ignored: no selection or buffer");
                    return;
                };
                // A click-locked peek means the overlay is SHOWING the
                // peeked window's pixels composited over the desktop
                // snapshot. Recognition must read that same composite —
                // extracting the raw snapshot would OCR the OBSCURING
                // window and hand COPY/SEARCH/UPLOAD text for content the
                // user cannot even see. Copy/Save/Edit/Upload all
                // composite via their *_with_peek helpers; OCR does the
                // same. (Nothing downstream needs to know: bubbles render
                // recognized glyphs, never desktop-texture samples, so a
                // peeked recognition presents like any other.)
                // `covered` — the rect the crop ACTUALLY produced, clamped
                // to the desktop buffer — is the only valid origin for the
                // result rects. Offsetting by `sel` instead would misplace
                // every lifted quad on negative-origin multi-monitor
                // layouts (extract_selection_bgra documents the clamp).
                let extraction = match active_peek_image {
                    Some(peek) => extract_selection_bgra_with_peek(sel, buf, peek),
                    None => extract_selection_bgra(sel, buf),
                };
                let Some((bgra, width, height, covered)) = extraction else {
                    log::info!("command Ocr ignored: selection is outside the desktop bitmap");
                    return;
                };
                let request = OcrRequest {
                    bgra,
                    width,
                    height,
                    origin: covered,
                };
                // Fresh id + latch + flag per request: the Latch is
                // one-shot, so reusing one would replay the previous run's
                // result forever; the id is what lets a BACK-superseded
                // result be recognised as stale on pickup.
                cycle.ocr_req += 1;
                let req = cycle.ocr_req;
                let latch: Arc<Latch<Result<OcrOutcome, OcrError>>> = Arc::new(Latch::new());
                let cancel = Arc::new(AtomicBool::new(false));
                // Where the recognizer leaves its response file and `ocr.log`
                // (see ocr::recognize). None is normal: OCR has no session_dir
                // requirement, and only UPLOAD needs one.
                let session_dir = cycle.settings.session_dir.clone();
                // A dedicated detached thread, never joined: recognize()
                // blocks for the whole of the child process's run, while this
                // (winit) thread must never block — a join would freeze the
                // overlay for the entire recognition.
                let spawned = {
                    let latch = latch.clone();
                    let cancel = cancel.clone();
                    std::thread::Builder::new()
                        .name("ocr".into())
                        .spawn(move || {
                            // Checked on both sides of the expensive call
                            // (and polled inside it — recognize() kills the
                            // child when the flag goes up): before, to skip
                            // work the user already backed out of; after,
                            // to avoid setting a latch whose reader is
                            // gone, and because a cancelled recognize()
                            // returns a placeholder error that must never
                            // be surfaced as a real outcome.
                            if cancel.load(Ordering::Acquire) {
                                return;
                            }
                            let result = ocr::recognize(&request, &cancel, session_dir.as_deref());
                            if cancel.load(Ordering::Acquire) {
                                return;
                            }
                            latch.set(result);
                        })
                };
                if let Err(e) = spawned {
                    log::warn!("failed to spawn the OCR worker thread: {e}");
                    cycle.input.ocr_notice = Some(OcrNotice {
                        anchor: Instant::now(),
                        kind: OcrNoticeKind::Failed,
                    });
                    broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
                    return;
                }
                // A new request starts with a clean slate: a leftover
                // failure pill under the scan sweep would be read as this
                // attempt's verdict.
                cycle.input.ocr_notice = None;
                cycle.ocr_job = Some((req, latch, cancel));
                cycle.input.ocr = OcrState::Scanning {
                    anchor: Instant::now(),
                    req,
                    region: covered,
                };
                broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
            }
            Command::OcrBack => self.exit_ocr_mode(window_id),
            Command::OcrCopy => {
                // Lifted only: during Scanning there is no text yet (and
                // no strip on screen — this arm is belt-and-braces against
                // a stray dispatch, e.g. Enter's default_action racing a
                // state change).
                let OcrState::Lifted {
                    outcome,
                    ..
                } = &cycle.input.ocr
                else {
                    log::info!("command OcrCopy ignored: no lifted OCR result");
                    return;
                };
                let text = outcome.full_text.clone();
                hide_overlay_for_action(&self.windows);
                match copy_text_to_clipboard(&text) {
                    ActionResult::Success => self.finish_cycle(event_loop, CycleAction::OcrCopy),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Copy to Clipboard Failed", &msg, ErrorIcon).unwrap_or(false)
                        {
                            self.show_all_windows();
                        } else {
                            self.finish_cycle(event_loop, CycleAction::Cancelled);
                        }
                    }
                }
            }
            Command::OcrSearch => {
                let OcrState::Lifted {
                    outcome,
                    ..
                } = &cycle.input.ocr
                else {
                    log::info!("command OcrSearch ignored: no lifted OCR result");
                    return;
                };
                let Some(url) = ocr::search::search_url(&outcome.full_text) else {
                    // Whitespace-only text: nothing to search for. Stay in
                    // the mode — the lifted lines are still on screen and
                    // the other buttons still work.
                    log::info!("command OcrSearch ignored: no searchable text");
                    return;
                };
                // ShellExecuteW may hand the URL to an ALREADY RUNNING
                // browser, which holds no activation rights of its own —
                // the usual hand-to-shell grant names the wrong pid and
                // would leave it flashing in the taskbar. ASFW_ANY lets
                // whoever takes the foreground next have it, and only works
                // while we are still the foreground window, so it must come
                // before the hide.
                SystemInterop::allow_any_foreground();
                // Deliberately NOT hide_overlay_for_action: that would hand
                // the (single-use) foreground grant to Clowd.Ui instead of
                // the browser — see the comment on hide_overlay_for_action.
                self.windows.hide_all();
                // Must stay on this (winit) thread: ShellExecuteW rides the
                // STA COM that SystemInterop::init established here and
                // fails silently from a worker thread.
                if SystemInterop::open_url(&url) {
                    self.finish_cycle(event_loop, CycleAction::OcrSearch);
                } else {
                    // Nothing was launched, so the overlay still owns the
                    // screen — re-show it and stay Lifted.
                    log::warn!("failed to open the browser for OCR search; returning to the overlay");
                    self.show_all_windows();
                }
            }
            Command::OcrUpload => {
                let OcrState::Lifted {
                    outcome,
                    ..
                } = &cycle.input.ocr
                else {
                    log::info!("command OcrUpload ignored: no lifted OCR result");
                    return;
                };
                // The Command::Video precedent: without a --session-dir
                // there is no shell listening for the marker — ignore.
                let Some(session_dir) = cycle.settings.session_dir.clone() else {
                    log::info!("command OcrUpload ignored: no --session-dir provided");
                    return;
                };
                let text = outcome.full_text.clone();
                // The shell treats an ocr-upload marker with whitespace-only
                // text as a cancelled capture; don't emit one at all rather
                // than lean on that fallback. Unreachable from a Lifted
                // outcome (empty results never lift), hence belt-and-braces.
                if text.trim().is_empty() {
                    log::info!("command OcrUpload ignored: recognized text is empty");
                    return;
                }
                hide_overlay_for_action(&self.windows);
                match write_ocr_upload_action(&session_dir, &text) {
                    ActionResult::Success => self.finish_cycle(event_loop, CycleAction::OcrUpload),
                    ActionResult::Cancelled => self.show_all_windows(),
                    ActionResult::Failed(msg) => {
                        if xdialog::show_message_retry_cancel("Clowd Capture", "Text Upload Failed", &msg, ErrorIcon).unwrap_or(false) {
                            self.show_all_windows();
                        } else {
                            self.finish_cycle(event_loop, CycleAction::Cancelled);
                        }
                    }
                }
            }
        }
    }
}

impl ApplicationHandler<AppEvent> for App {
    fn user_event(&mut self, event_loop: &ActiveEventLoop, event: AppEvent) {
        match event {
            AppEvent::Command(cmd) => self.handle_host_command(event_loop, cmd),
            AppEvent::ParentGone => {
                warn!("parent process is gone (stdin EOF); exiting");
                // Hide the overlay / restore the cursor before dying so the
                // user isn't left staring at a frozen screenshot.
                if self.cycle.is_some() {
                    self.finish_cycle(event_loop, CycleAction::Cancelled);
                }
                // Prompt exit rather than unwinding the event loop: with
                // the parent dead nobody reads our events, and orphaned
                // overlay processes must never linger.
                std::process::exit(0);
            }
            AppEvent::DisplayChange => self.handle_display_change(event_loop),
            AppEvent::GpuLost => self.handle_gpu_lost(event_loop),
        }
    }

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

        self.try_pick_up_walker();

        let mut windows = WindowSet::new();
        self.warmup.mark_window_create_start();

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
                // WS_EX_TOPMOST from creation: without it the overlay loses the z-order
                // battle against a fullscreen foreground app (e.g. Discord) on that
                // monitor — only the focused window is ever raised, and SW_SHOWNOACTIVATE
                // never raises the others. Release only: a topmost overlay while paused
                // in a debugger locks up the entire desktop.
                if !cfg!(debug_assertions) {
                    attrs = attrs.with_window_level(winit::window::WindowLevel::AlwaysOnTop);
                }
            }
            let window = match event_loop.create_window(attrs) {
                Ok(w) => Arc::new(w),
                Err(e) => {
                    error!("failed to create window for monitor {i}: {e:?}");
                    self.teardown_failed_worker(i);
                    continue;
                }
            };

            self.warmup.workers[i]
                .surface_start
                .set_once(self.warmup.t_start.elapsed());
            let handle = match WindowHandle::new(window, setup, &self.instance) {
                Ok(h) => h,
                Err(e) => {
                    error!("failed to create window handle for monitor {i}: {e:?}");
                    self.teardown_failed_worker(i);
                    continue;
                }
            };
            self.warmup.workers[i]
                .surface_bind
                .set_once(self.warmup.t_start.elapsed());

            windows.insert(handle);
        }

        if windows.is_empty() {
            error!("no windows created; exiting");
            event_loop.exit();
            return;
        }

        self.warmup.mark_window_create();
        self.windows = windows;

        // Freshly created windows start un-shown, but still need this
        // cycle's per-window state (macOS background screenshot layer).
        if let Some(cycle) = self.cycle.as_ref() {
            if let Some(buf) = cycle.desktop_buffer.as_deref() {
                for h in self.windows.values() {
                    h.set_background_image(Some(buf));
                }
            }
        }
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        // Persistent warm-up gate: emit `ready` once every worker has
        // parked (or failed — a dead worker must not hold `ready` hostage
        // any more than it may hold the show gate).
        if let Some(hs) = self.host.as_mut() {
            if !hs.ready_emitted {
                let parked = hs.parked_count.load(Ordering::Acquire);
                let failed = self.worker_failed.load(Ordering::Acquire);
                if parked + failed >= self.workers.len() {
                    hs.ready_emitted = true;
                    self.warmup.mark_ready();
                    let warmup_ms = self.warmup.t_start.elapsed().as_millis() as u64;
                    info!(
                        "persistent host ready: warmed up in {warmup_ms} ms ({} monitors)",
                        self.monitors.len()
                    );
                    host::emit(&HostEvent::Ready {
                        warmup_ms,
                        monitors: self.monitors.len(),
                    });
                    // Leave the warm-up WaitUntil ticking behind — idle is
                    // a pure Wait until a command arrives (unless a `show`
                    // beat us here and already wants Poll).
                    if self.cycle.is_none() {
                        event_loop.set_control_flow(ControlFlow::Wait);
                    }
                } else if self.cycle.is_none() {
                    // Workers have no way to wake the loop, so tick until
                    // they're all parked; after `ready` the loop settles
                    // into ControlFlow::Wait until a command arrives. (An
                    // early `show` sets Poll — don't override it.)
                    event_loop.set_control_flow(ControlFlow::WaitUntil(Instant::now() + Duration::from_millis(25)));
                }
            }
        }

        if let Some(ref m) = self.pinch_monitor {
            let delta = m.drain();
            if delta != 0.0
                && self
                    .cycle
                    .as_ref()
                    .is_some_and(|c| !c.input.captured)
            {
                self.apply_zoom_factor(1.0 + delta as f32);
            }
        }

        // Try to pick up the walker if it wasn't ready during resumed(),
        // and (persistent mode) this cycle's screenshot.
        self.try_pick_up_walker();
        self.try_pick_up_screenshot(event_loop);
        self.try_advance_ocr();

        if let Some(cycle) = self.cycle.as_mut() {
            if let Some(ref pending) = cycle.pending_show {
                // Failed workers count toward the gate so a dead worker can
                // never hold the overlay hostage.
                let ready = pending.ready_count.load(Ordering::Acquire);
                let failed = self.worker_failed.load(Ordering::Acquire);
                if ready + failed >= pending.expected {
                    cycle.timings.mark_show_start();
                    self.windows.show_all();
                    cycle.timings.mark_shown();
                    if let Some(h) = self.windows.first() {
                        h.focus();
                    }
                    update_cursor_visibility(&self.windows, &cycle.input);
                    pending.visible_latch.signal_all();
                    cycle.pending_show = None;
                    broadcast_mouse_state(&self.windows, &cycle.input);
                    broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
                    if self.persistent {
                        let elapsed_ms = cycle.started.elapsed().as_millis() as u64;
                        info!("overlay shown {elapsed_ms} ms after the show command");
                        host::emit(&HostEvent::Shown {
                            elapsed_ms,
                        });
                    }
                }
            }
        }

        // Pre-select the active screen / foreground window when launched with
        // `--capture-mode screen|window` (no-op in free-region mode).
        self.try_preselect(event_loop);

        // Last so its WaitUntil (idle debounce in progress) survives the
        // control-flow writes above; exits the process once the display-
        // change debounce expires.
        self.check_display_change(event_loop);
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, id: WindowId, event: WindowEvent) {
        let this_monitor_bounds = match self.windows.get(&id) {
            Some(h) => h.monitor_bounds(),
            None => return,
        };
        let handle_monitor_bounds = this_monitor_bounds;

        // Geometry maintenance must run even with no cycle in flight: a warm
        // host creates its windows on the primary monitor and moves them into
        // place during warm-up, so the DPI change that move provokes arrives
        // between cycles. Without this override winit resizes the window by
        // new_scale/old_scale, permanently shrinking any overlay whose monitor
        // has a different DPI than the primary.
        #[cfg(windows)]
        let mut event = event;
        #[cfg(windows)]
        if let WindowEvent::ScaleFactorChanged {
            ref mut inner_size_writer,
            ..
        } = event
        {
            let _ = inner_size_writer.request_inner_size(winit::dpi::PhysicalSize::new(
                handle_monitor_bounds.width() as u32,
                handle_monitor_bounds.height() as u32,
            ));
            return;
        }

        // No active cycle (finishing / between cycles): nothing to do.
        let Some(cycle) = self.cycle.as_mut() else {
            return;
        };

        match event {
            WindowEvent::CloseRequested => {
                self.finish_cycle(event_loop, CycleAction::Cancelled);
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
                // Escape backs out one step at a time: OCR mode unwinds
                // through its own ladder (Scanning cancels, Lifted starts
                // the retract, Retracting skips it), picking a scroll point
                // returns to the panel with the selection intact, and only
                // outside any mode does Escape cancel the whole cycle.
                if cycle.input.ocr.active() {
                    self.exit_ocr_mode(id);
                    return;
                }
                if cycle.input.scroll_pick_mode {
                    cycle.input.scroll_pick_mode = false;
                    let cursor = cycle.input.hittest.cursor();
                    set_cursor_if_changed(&self.windows, &mut cycle.last_cursor, id, cursor);
                    update_cursor_visibility(&self.windows, &cycle.input);
                    broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
                    return;
                }
                self.finish_cycle(event_loop, CycleAction::Cancelled);
            }
            WindowEvent::KeyboardInput {
                event:
                    KeyEvent {
                        state: ElementState::Pressed,
                        logical_key: Key::Named(NamedKey::Enter),
                        ..
                    },
                ..
            } => {
                // Mirrors the Dx capturer: Return acts as the default
                // accept once a selection is made — "open in editor"
                // normally, COPY while OCR lines are lifted. Which (if
                // either) is `default_action`'s decision.
                if let Some(cmd) = default_action(&cycle.input) {
                    self.dispatch_command(cmd, event_loop, id);
                }
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
                        cycle.input.debug_visible = !cycle.input.debug_visible;
                        update_cursor_visibility(&self.windows, &cycle.input);
                        broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
                    } else if cycle.input.scroll_pick_mode {
                        // Panel accelerators are the panel's, and the panel
                        // is hidden while picking — swallow them rather
                        // than let an invisible button fire. Escape (above)
                        // is the only way out.
                        //
                        // M is swallowed here too, ahead of its handler
                        // below. Picking suppresses both of that toggle's
                        // feedback channels — the [M] hint is gone and the
                        // frozen cursor is force-hidden — so honouring the
                        // key would silently change whether the cursor lands
                        // in the saved image, with nothing on screen to say
                        // so. D stays live: it is a developer affordance and
                        // the debug panel is its own feedback.
                    } else if cycle.input.ocr.active() {
                        // Accelerators are scoped to the strip on screen:
                        // only while the OCR strip is up (Lifted) do its
                        // keys dispatch; everything else is swallowed so
                        // an invisible button can never fire. Scanning
                        // shows NO panel (the sweep is still working, so
                        // there is nothing to act on — Escape above is the
                        // only exit) and swallows everything for the same
                        // reason. Retracting swallows everything too — the
                        // strip is mid-swap and the mode ends by itself in
                        // a fraction of a second.
                        //
                        // M is swallowed too, for the scroll-pick reasoning
                        // above: both of that toggle's feedback channels
                        // (the [M] hint and any image output) are suppressed
                        // in this mode, so honouring it would silently
                        // change the eventual image with nothing on screen
                        // to say so. D stays live (the arm above precedes).
                        if cycle.input.ocr.shows_ocr_panel() {
                            let features = cycle.settings.panel_features;
                            if let Some(cmd) = panel::lookup_command_by_key(panel::model::PanelButtonSet::Ocr, features, c) {
                                self.dispatch_command(cmd, event_loop, id);
                            }
                        }
                    } else if c_lower == 'm' {
                        cycle.input.cursor_overlay_visible = !cycle.input.cursor_overlay_visible;
                        broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
                    } else if cycle.input.captured {
                        // Normal stays hardcoded here: the OCR arm above
                        // claims every keypress while ocr.active(), so this
                        // branch can only run with the Normal strip up —
                        // the two sets deliberately reuse letters. The
                        // feature switches are NOT hardcoded: a button the
                        // user turned off must not answer to its letter
                        // either, or the strip would be missing a button
                        // that still fires.
                        let features = cycle.settings.panel_features;
                        if let Some(cmd) = panel::lookup_command_by_key(panel::model::PanelButtonSet::Normal, features, c) {
                            self.dispatch_command(cmd, event_loop, id);
                        }
                    } else if cycle.input.mouse_down {
                        // Mid-drag: swallow keys.
                    } else {
                        match c_lower {
                            't' => {
                                cycle.input.tips_mode = cycle.input.tips_mode.next();
                                broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
                            }
                            'q' => {
                                cycle.input.overlays_visible = !cycle.input.overlays_visible;
                                if !cycle.input.overlays_visible && cycle.input.zoom > 1.0 {
                                    cycle.input.has_used_magnifier = true;
                                }
                                broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
                                broadcast_mouse_state(&self.windows, &cycle.input);
                            }
                            'w' => {
                                let pt = ScreenPoint::new(
                                    cycle.input.virtual_cursor.x.round() as i32,
                                    cycle.input.virtual_cursor.y.round() as i32,
                                );
                                if let Some(rect) = cycle
                                    .walker
                                    .as_ref()
                                    .and_then(|w| w.hit_test(pt))
                                {
                                    self.finalise_selection_with_peek(rect, event_loop, id);
                                }
                            }
                            'f' => {
                                let pt = ScreenPoint::new(
                                    cycle.input.virtual_cursor.x.round() as i32,
                                    cycle.input.virtual_cursor.y.round() as i32,
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
            WindowEvent::CursorMoved {
                position,
                ..
            } => {
                let bounds = handle_monitor_bounds;
                let win_pt = WindowPoint::new(position.x as f32, position.y as f32);
                let os_vd = ScreenPoint::new(bounds.min_x() + win_pt.x.round() as i32, bounds.min_y() + win_pt.y.round() as i32);

                if cycle.input.anchored {
                    if os_vd == cycle.input.anchor {
                        return;
                    }
                    if cycle.input.anchor_just_engaged {
                        const STALE_THRESHOLD: f32 = 75.0;
                        let raw_dx = (os_vd.x - cycle.input.anchor.x) as f32;
                        let raw_dy = (os_vd.y - cycle.input.anchor.y) as f32;
                        if raw_dx * raw_dx + raw_dy * raw_dy > STALE_THRESHOLD * STALE_THRESHOLD {
                            SystemInterop::set_mouse_position(cycle.input.anchor, &self.monitors);
                            return;
                        }
                        cycle.input.anchor_just_engaged = false;
                    }
                    let zoom = cycle.input.zoom;
                    let dx = (os_vd.x - cycle.input.anchor.x) as f32 / zoom;
                    let dy = (os_vd.y - cycle.input.anchor.y) as f32 / zoom;
                    cycle.input.virtual_cursor.x += dx;
                    cycle.input.virtual_cursor.y += dy;
                    clamp_to_nearest_monitor(&mut cycle.input.virtual_cursor, &self.monitors);
                    SystemInterop::set_mouse_position(cycle.input.anchor, &self.monitors);
                } else {
                    cycle.input.virtual_cursor = ScreenPointF::new(os_vd.x as f32, os_vd.y as f32);
                }

                if !cycle.input.mouse_down && !cycle.input.captured {
                    let pt = ScreenPoint::new(
                        cycle.input.virtual_cursor.x.round() as i32,
                        cycle.input.virtual_cursor.y.round() as i32,
                    );
                    cycle.input.selection = cycle
                        .walker
                        .as_ref()
                        .and_then(|w| w.hit_test(pt));
                }

                if cycle.input.mouse_down && !cycle.input.captured {
                    if let Some(start) = cycle.input.mouse_down_pt {
                        let psel = ScreenRect::from_rounded_threshold(
                            start.x,
                            start.y,
                            cycle.input.virtual_cursor.x,
                            cycle.input.virtual_cursor.y,
                        );
                        if !cycle.input.dragging {
                            let threshold = 6.0 / (cycle.input.mouse_down_dpi * cycle.input.zoom);
                            let crossed = psel.is_some_and(|r| (r.width() as f32) > threshold || (r.height() as f32) > threshold);
                            if crossed {
                                cycle.input.dragging = true;
                            }
                        }
                        if cycle.input.dragging {
                            cycle.input.selection = psel;
                        }
                    }
                }

                if cycle.input.captured {
                    if let (Some(mode), Some(anchor), Some(start)) =
                        (cycle.input.drag_mode, cycle.input.drag_anchor_selection, cycle.input.mouse_down_pt)
                    {
                        let cur_x = cycle.input.virtual_cursor.x.floor() as i32;
                        let cur_y = cycle.input.virtual_cursor.y.floor() as i32;
                        let new_sel = match mode {
                            DragMode::Move => {
                                let dx = (cycle.input.virtual_cursor.x - start.x).round() as i32;
                                let dy = (cycle.input.virtual_cursor.y - start.y).round() as i32;
                                move_and_crop(anchor, dx, dy, self.vd_bounds)
                            }
                            DragMode::Resize(handle) => Some(resize_with_clamp(anchor, handle, cur_x, cur_y, self.vd_bounds)),
                        };
                        cycle.input.selection = new_sel;
                        // No broadcast here — the unconditional
                        // broadcast_ui_state at the end of CursorMoved
                        // covers this path.
                    } else if let Some(sel) = cycle.input.selection {
                        let dpi = dpi_at_point(cycle.input.virtual_cursor, &self.monitors);
                        cycle.input.hittest = hit_test(cycle.input.virtual_cursor, sel, dpi);

                        let pos = cycle.input.virtual_cursor;
                        let over_button = current_panel_layout(cycle, &self.monitors)
                            .and_then(|l| l.hit_test(pos.x, pos.y))
                            .is_some();
                        // Pick mode owns the cursor for the whole move: the
                        // panel is gone and every pixel of the selection is
                        // a valid target, so neither the button pointer nor
                        // the move/resize handles apply.
                        let cursor = if cycle.input.scroll_pick_mode {
                            CursorIcon::Crosshair
                        } else if over_button {
                            CursorIcon::Pointer
                        } else if cycle.input.ocr.active() {
                            // The selection is frozen for the whole of OCR
                            // mode: resize arrows would promise an
                            // interaction it no longer offers.
                            CursorIcon::Default
                        } else {
                            cycle.input.hittest.cursor()
                        };
                        set_cursor_if_changed(&self.windows, &mut cycle.last_cursor, id, cursor);
                    }
                }

                if !cycle.input.has_ever_scrolled && !cycle.input.captured {
                    let now = Instant::now();
                    cycle
                        .input
                        .velocity_tracker
                        .record(now, cycle.input.virtual_cursor);
                    cycle.input.show_scroll_hint = cycle
                        .input
                        .velocity_tracker
                        .evaluate(now, cycle.input.show_scroll_hint);
                }

                broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
                broadcast_mouse_state(&self.windows, &cycle.input);
            }
            WindowEvent::MouseInput {
                state,
                button: MouseButton::Left,
                ..
            } => {
                if !cycle.input.overlays_visible {
                    return;
                }
                match state {
                    ElementState::Pressed => {
                        // Ahead of the panel hit-test: while picking a
                        // scroll point the click is the pick, and nothing
                        // else in the overlay may claim it.
                        if cycle.input.scroll_pick_mode {
                            // Pick mode is armed by a panel press (SCROLL)
                            // that also hides the panel — a set change the
                            // swap guard records. Without this check the
                            // second press of a double-click on SCROLL
                            // would be taken as the pick and write the
                            // scroll action aimed at the button's own
                            // location. Same property as the panel guard
                            // below: one physical double-click, one command.
                            if cycle.panel_swap.blocks_click(Instant::now()) {
                                log::info!("scroll pick ignored: within the double-click window of the panel swap");
                                return;
                            }
                            self.dispatch_scroll_pick(event_loop);
                            return;
                        }
                        if cycle.input.captured {
                            let pos = cycle.input.virtual_cursor;
                            if let Some(layout) = current_panel_layout(cycle, &self.monitors) {
                                if let Some(idx) = layout.hit_test(pos.x, pos.y) {
                                    // PROPERTY: a single physical double-
                                    // click on a panel button must never
                                    // dispatch two different commands. The
                                    // set swap is synchronous on the first
                                    // press and the strips are pixel-
                                    // identical, so the second press lands
                                    // on the same rect in the OTHER set —
                                    // e.g. OCR's first press swaps the
                                    // strip and the second would hit the
                                    // OCR strip's EXIT, destroying the
                                    // capture. Swallow (not fall through:
                                    // the click was aimed at a button) any
                                    // press within one double-click
                                    // interval of a swap. See
                                    // PanelSwapGuard for why this guards
                                    // the interval rather than specific
                                    // index collisions.
                                    if cycle.panel_swap.blocks_click(Instant::now()) {
                                        log::info!("panel click ignored: the button set changed within the double-click window");
                                        return;
                                    }
                                    // Resolved through the layout so the
                                    // index can only be read against the
                                    // set it was hit-tested in.
                                    let cmd = layout.command_at(idx);
                                    self.dispatch_command(cmd, event_loop, id);
                                    return;
                                }
                            }
                            // The selection is frozen under the lifted
                            // lines — their geometry was computed against
                            // it — so drag/resize never arms while OCR mode
                            // is active; a click outside the panel simply
                            // does nothing there.
                            if !cycle.input.ocr.active() {
                                let drag_mode = match cycle.input.hittest {
                                    Hittest::Inside => Some(DragMode::Move),
                                    Hittest::Outside => None,
                                    handle => Some(DragMode::Resize(handle)),
                                };
                                if drag_mode.is_some() {
                                    cycle.input.mouse_down = true;
                                    cycle.input.mouse_down_pt = Some(cycle.input.virtual_cursor);
                                    cycle.input.drag_mode = drag_mode;
                                    cycle.input.drag_anchor_selection = cycle.input.selection;
                                }
                            }
                            return;
                        }
                        cycle.input.mouse_down = true;
                        cycle.input.mouse_down_pt = Some(cycle.input.virtual_cursor);
                        cycle.input.mouse_down_dpi = dpi_at_point(cycle.input.virtual_cursor, &self.monitors);
                        cycle.input.dragging = false;
                        broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
                    }
                    ElementState::Released => {
                        let finalising = cycle.input.mouse_down && !cycle.input.captured && cycle.input.selection.is_some();
                        let was_dragging = cycle.input.dragging;
                        let was_move_drag = matches!(cycle.input.drag_mode, Some(DragMode::Move));
                        cycle.input.mouse_down = false;
                        cycle.input.mouse_down_pt = None;
                        cycle.input.dragging = false;
                        cycle.input.drag_mode = None;
                        cycle.input.drag_anchor_selection = None;
                        if was_move_drag && cycle.input.captured && cycle.input.selection.is_none() {
                            cycle.input.captured = false;
                            // Unreachable while OCR is active (drags never
                            // arm there), but this un-capture path bypasses
                            // InteractionController::reset, so clear the
                            // mode and its notice explicitly — both are
                            // anchored to the selection that just ceased to
                            // exist.
                            cycle.input.ocr = OcrState::Idle;
                            cycle.input.ocr_notice = None;
                            cycle.input.hittest = Hittest::Outside;
                            update_cursor_visibility(&self.windows, &cycle.input);
                            set_cursor_if_changed(&self.windows, &mut cycle.last_cursor, id, CursorIcon::Default);
                            broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
                        }
                        if finalising {
                            if !was_dragging {
                                // Click (no drag) on a peeked window → lock it permanently.
                                cycle.locked_peek = cycle
                                    .cached_peek_command
                                    .as_ref()
                                    .map(|cmd| PeekCommand {
                                        window_index: cmd.window_index,
                                        window_rect: cmd.window_rect,
                                        captured: true,
                                    });
                            } else {
                                // Drag-to-select → clear peek entirely.
                                cycle.locked_peek = None;
                            }
                            cycle.input.peek_suspended = true;
                            cycle.input.captured = true;
                            update_cursor_visibility(&self.windows, &cycle.input);
                            if cycle.input.anchored {
                                cycle.input.anchored = false;
                                cycle.input.anchor_just_engaged = false;
                                let restore = ScreenPoint::new(
                                    cycle.input.virtual_cursor.x.floor() as i32,
                                    cycle.input.virtual_cursor.y.floor() as i32,
                                );
                                SystemInterop::set_mouse_position(restore, &self.monitors);
                            }
                            cycle.input.zoom = 1.0;
                            if let Some(sel) = cycle.input.selection {
                                let dpi = dpi_at_point(cycle.input.virtual_cursor, &self.monitors);
                                let ht = hit_test(cycle.input.virtual_cursor, sel, dpi);
                                cycle.input.hittest = ht;
                                set_cursor_if_changed(&self.windows, &mut cycle.last_cursor, id, ht.cursor());
                            }
                            // Mouse-release drag-select captured-transition
                            // site (DESIGN §3.3).
                            self.on_captured(event_loop, id);
                        }
                        // on_captured may have finished the cycle (--video);
                        // re-borrow instead of assuming it is still alive.
                        let Some(cycle) = self.cycle.as_mut() else {
                            return;
                        };
                        broadcast_ui_state(&self.windows, &self.monitors, &self.ui_monitors, cycle);
                        broadcast_mouse_state(&self.windows, &cycle.input);
                    }
                }
            }
            WindowEvent::MouseWheel {
                delta,
                phase,
                ..
            } => {
                if cycle.input.captured {
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
                                cycle.input.scroll_momentum = cycle
                                    .input
                                    .last_scroll_end
                                    .is_some_and(|t| t.elapsed() < MOMENTUM_GAP);
                            }
                            TouchPhase::Ended | TouchPhase::Cancelled => {
                                cycle.input.last_scroll_end = Some(Instant::now());
                            }
                            _ => {}
                        }
                        if cycle.input.scroll_momentum {
                            return;
                        }
                        let dy = p.y as f32;
                        if dy == 0.0 {
                            return;
                        }
                        2_f32.powf(dy / TOUCHPAD_PIXELS_PER_DOUBLING)
                    }
                };
                cycle.input.has_ever_scrolled = true;
                cycle.input.show_scroll_hint = false;
                cycle.input.velocity_tracker.dismiss_hint();
                self.apply_zoom_factor(factor);
            }
            WindowEvent::PinchGesture {
                delta,
                ..
            } => {
                if cycle.input.captured {
                    return;
                }
                if delta == 0.0 {
                    return;
                }
                cycle.input.has_ever_scrolled = true;
                cycle.input.show_scroll_hint = false;
                cycle.input.velocity_tracker.dismiss_hint();
                self.apply_zoom_factor(1.0 + delta as f32);
            }
            _ => {}
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::settings::TipsMode;

    /// A freshly-shown overlay: nothing selected, no modes engaged.
    fn input() -> InteractionState {
        InteractionState {
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
            tips_mode: TipsMode::Off,
            debug_visible: false,
            last_scroll_end: None,
            scroll_momentum: false,
            overlays_visible: true,
            cursor_overlay_visible: false,
            peek_suspended: false,
            has_ever_scrolled: false,
            show_scroll_hint: false,
            velocity_tracker: MouseVelocityTracker::new(),
            has_used_magnifier: false,
            scroll_pick_mode: false,
            ocr: OcrState::Idle,
            ocr_notice: None,
        }
    }

    /// A result-shaped payload for gating assertions; recognition itself is
    /// never exercised here.
    fn dummy_outcome() -> Arc<OcrOutcome> {
        Arc::new(OcrOutcome {
            lines: Vec::new(),
            full_text: String::new(),
            text_angle: 0.0,
        })
    }

    #[test]
    fn enter_opens_editor_once_captured() {
        let mut i = input();
        assert_eq!(default_action(&i), None);
        i.captured = true;
        assert_eq!(default_action(&i), Some(Command::Edit));
    }

    /// The panel is hidden while a scroll point is being picked, and Enter
    /// is its default button: firing Edit here would silently replace the
    /// scrolling capture with a plain screenshot.
    #[test]
    fn enter_inert_while_picking_scroll_point() {
        let mut i = input();
        i.captured = true;
        i.scroll_pick_mode = true;
        assert_eq!(default_action(&i), None);
    }

    /// With OCR lines lifted, the default accept is the recognized text,
    /// not the editor: Edit would write a plain screenshot session and
    /// destroy the OCR result.
    #[test]
    fn enter_copies_while_ocr_lifted() {
        let mut i = input();
        i.captured = true;
        i.ocr = OcrState::Lifted {
            anchor: Instant::now(),
            req: 1,
            region: ScreenRect::from_xy_size(0, 0, 10, 10),
            dpi_scale: 1.0,
            outcome: dummy_outcome(),
        };
        assert_eq!(default_action(&i), Some(Command::OcrCopy));
    }

    /// The transitional phases have nothing to accept: Scanning has no text
    /// yet, Retracting is on its way out of the mode.
    #[test]
    fn enter_inert_while_ocr_scanning() {
        let mut i = input();
        i.captured = true;
        i.ocr = OcrState::Scanning {
            anchor: Instant::now(),
            req: 1,
            region: ScreenRect::from_xy_size(0, 0, 10, 10),
        };
        assert_eq!(default_action(&i), None);

        i.ocr = OcrState::Retracting {
            anchor: Instant::now(),
            region: ScreenRect::from_xy_size(0, 0, 10, 10),
        };
        assert_eq!(default_action(&i), None);
    }

    /// The swap guard's whole contract: a click inside one double-click
    /// interval of a set change is swallowed, one outside it is not, and
    /// only genuine *changes* arm it (the broadcast on every mouse move
    /// re-observes the same set constantly). The strips recentre on every
    /// swap, so without this guard the second press of a double-click on
    /// BACK — or on whatever materialises when a selection is captured by
    /// double-click — fires whichever button happens to land under the
    /// cursor.
    #[test]
    fn panel_swap_guard_blocks_reclick_inside_window_only() {
        use panel::model::PanelButtonSet;
        let window = Duration::from_millis(500);
        let t0 = Instant::now();
        let mut g = PanelSwapGuard::new();

        // Nothing observed yet: nothing to guard.
        assert!(!g.blocks_click_within(t0, window));

        // The panel appears (None -> Normal): armed — a double-click that
        // CAPTURES a selection must not press whatever button materialised
        // under the cursor.
        g.observe(Some(PanelButtonSet::Normal), t0);
        assert!(g.blocks_click_within(t0 + Duration::from_millis(100), window));
        // ...and releases once a full double-click interval has passed.
        assert!(!g.blocks_click_within(t0 + window, window));

        // Steady-state re-observation of the same set must NOT re-arm.
        let t1 = t0 + Duration::from_secs(10);
        g.observe(Some(PanelButtonSet::Normal), t1);
        assert!(!g.blocks_click_within(t1 + Duration::from_millis(1), window));

        // A genuine swap (Normal -> Ocr) re-arms: the double-click-OCR-
        // hits-EXIT case itself.
        let t2 = t0 + Duration::from_secs(20);
        g.observe(Some(PanelButtonSet::Ocr), t2);
        assert!(g.blocks_click_within(t2 + Duration::from_millis(100), window));
        assert!(!g.blocks_click_within(t2 + window, window));

        // Hiding the panel (Some -> None) then re-showing it re-arms too —
        // the scroll-pick round trip.
        let t3 = t0 + Duration::from_secs(30);
        g.observe(None, t3);
        g.observe(Some(PanelButtonSet::Normal), t3 + Duration::from_secs(5));
        assert!(g.blocks_click_within(t3 + Duration::from_secs(5) + Duration::from_millis(100), window));
    }

    /// A click stamped at (or even before) the swap instant is still inside
    /// the window: `duration_since` saturates to zero rather than wrapping
    /// or panicking, so event-timestamp skew cannot punch through the guard.
    #[test]
    fn panel_swap_guard_handles_out_of_order_instants() {
        use panel::model::PanelButtonSet;
        let window = Duration::from_millis(500);
        // Anchored ahead of now so the subtraction below cannot underflow.
        let t0 = Instant::now() + Duration::from_secs(60);
        let mut g = PanelSwapGuard::new();
        g.observe(Some(PanelButtonSet::Normal), t0);
        assert!(g.blocks_click_within(t0, window));
        assert!(g.blocks_click_within(t0 - Duration::from_millis(5), window));
    }

    /// The live window comes from GetDoubleClickTime; the clamp guarantees
    /// a broken registry value can neither disable the guard nor wedge the
    /// panel shut.
    #[cfg(windows)]
    #[test]
    fn panel_swap_guard_window_is_sane() {
        let w = panel_swap_guard_window();
        assert!(w >= Duration::from_millis(100) && w <= Duration::from_millis(2000), "window {w:?}");
    }
}
