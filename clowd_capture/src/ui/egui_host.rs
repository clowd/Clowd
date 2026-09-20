//! The app-thread egui hosts: one `egui::Context` per monitor.
//!
//! One context per monitor rather than one shared context with a viewport
//! per monitor, because egui prunes non-root viewports at every root
//! `end_pass` unless the pass marked them used, and it hit-tests presses
//! against the PREVIOUS pass's widget rects. Per-monitor contexts have no
//! ordering invariant at all: each host runs when its own inputs change,
//! and nothing a host does can disturb another.
//!
//! Every host works in monitor-local points — `screen_rect` starts at the
//! origin and the pointer is the virtual-desktop cursor minus the
//! monitor's origin, both divided by that monitor's DPI scale — so the
//! painter on the worker side is egui-wgpu verbatim, with no origin to
//! carry anywhere.
//!
//! What a host produces is an [`EguiFrame`] per monitor; `broadcast_ui_state`
//! ships them in `UiSharedState` and the workers paint them.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::{Duration, Instant};

use egui::{Color32, Pos2};

use crate::interaction::{InteractionState, OcrState};
use crate::render::window::WindowSet;
use crate::selection::intersect_rects;
use crate::settings::CapturerSettings;
use crate::system::{CapturedDesktop, MonitorInfo};
use crate::telemetry::perf::PerfSnapshot;
use crate::telemetry::startup::StartupTimings;
use crate::ui::command::Command;
use crate::ui::components::debug::model::{LineBuf, MonitorPanelData, PrimaryPanelData};
use crate::ui::components::debug::resources::ResourcePoller;
use crate::ui::components::panel::model::{ButtonStyle, PanelButtonSet, PanelFeatures, Readout};
use crate::ui::components::panel::theme;
use crate::ui::components::{self, accent_color32, area, hints, ocr, scope, tips, InputCtx, OverlayInputs};
use crate::ui::egui_frame::{EguiFrame, TextureShadow};
use crate::ui::fonts;
use crate::ui::shared::{
    active_panel_set, debug_monitor_visibility, debug_primary_visibility, monitor_index_at, pick_monitor_containing_center, sample_bgra,
    UiMonitor,
};
use clowd_rust_core::geometry::{to_screen_point, RectExt, ScreenPointF, ScreenRect, ScreenRectF};

/// The shortest gap between two scheduled runs of one host. egui asks for
/// those repaints as "immediately", so without a floor a hover fade would
/// drive the app thread as fast as it can run. A host schedules a run only
/// while something is still owed one — an animation in progress, or the
/// settling repaint egui pairs with every event — so a capture where
/// nothing moves schedules nothing at all.
pub const REPAINT_FLOOR: Duration = Duration::from_millis(16);

/// Bound on the passes of one tick. egui wants an event-less pass after
/// every pass that saw an event, and a press tick feeds its events only
/// from the second pass on, so three is the most any tick actually needs;
/// four leaves a pass of slack and still bounds the loop.
const MAX_PASSES: u32 = 4;

/// Everything one host's UI depends on. The host reruns egui only when
/// this changes (or a scheduled repaint comes due), so a mouse move across
/// a monitor that shows nothing costs one comparison.
#[derive(Clone, PartialEq)]
pub struct HostInputs {
    /// The tray, on the one host that shows it.
    pub panel: Option<PanelInputs>,
    /// The debug panels, on every host while the `D` toggle is on and this
    /// monitor's worker has published a perf snapshot.
    pub debug: Option<DebugInputs>,
    pub pointer: Option<Pos2>,
    /// Everything else this monitor draws.
    pub overlays: OverlayInputs,
}

/// Both debug panels' contents, already formatted. The rows are strings
/// because that is what the app thread has to build anyway, and comparing
/// them is what tells a host whether anything it shows actually moved.
#[derive(Clone, PartialEq)]
pub struct DebugInputs {
    pub monitor: MonitorPanelInputs,
    /// Only on the monitor holding the virtual cursor.
    pub primary: Option<PrimaryPanelInputs>,
}

/// The top-left panel: the formatted rows plus the numbers its
/// frame-time plot draws from.
#[derive(Clone, PartialEq)]
pub struct MonitorPanelInputs {
    pub lines: Arc<[(String, Color32)]>,
    /// Newest first: `[overall_ms, cpu_ms, gpu_ms]`, straight off the
    /// worker's snapshot.
    pub bars: Arc<[[f32; 3]]>,
    pub target_period: Option<Duration>,
    pub window_size: usize,
    /// The snapshot's sequence number, so a host reruns when new timings
    /// land even if every formatted row happened to be identical.
    pub seq: u64,
}

/// The top-right panel: rows only.
#[derive(Clone, PartialEq)]
pub struct PrimaryPanelInputs {
    pub lines: Arc<[(String, Color32)]>,
}

/// Everything the tray's geometry and contents depend on. The pointer is
/// deliberately absent: hover is animated by egui from the pointer events,
/// and a move that changes nothing else must not re-measure the strip.
#[derive(Clone, Copy, PartialEq)]
pub struct PanelInputs {
    pub set: PanelButtonSet,
    pub features: PanelFeatures,
    pub style: ButtonStyle,
    /// The user's capture accent: the primary group's fill.
    pub accent: Color32,
    /// What the readout slot shows: the selection's size on the capture
    /// strip, the lifted word count on the OCR strip.
    pub readout: Readout,
    /// The selection as the user made it: the size readout prints this,
    /// so a rect straddling two monitors keeps showing its true size.
    pub selection: ScreenRect,
    /// The selection clipped to the host's monitor: what placement
    /// anchors to.
    pub anchor: ScreenRect,
}

/// What a panel run tells the app thread, synchronously, in the same call
/// that produced the primitives.
#[derive(Clone, Copy, Default, Debug, PartialEq, Eq)]
pub struct PanelOutcome {
    /// The command a button reported as clicked during this tick, ORed
    /// over every pass of it. Only [`EguiHosts::panel_press`] can produce
    /// one; the runs a broadcast makes never feed a button event.
    pub clicked: Option<Command>,
    /// The pointer is over a button: the app thread shows the hand cursor.
    pub over_button: bool,
    /// The pointer is anywhere on the tray, dead chassis included.
    pub over_tray: bool,
}

/// Arguments for [`EguiHosts::sync`], grouped because the caller has them
/// spread across the capture cycle.
pub struct SyncArgs<'a> {
    pub input: &'a InteractionState,
    pub settings: &'a CapturerSettings,
    pub monitors: &'a [MonitorInfo],
    pub ui_monitors: &'a [UiMonitor],
    pub desktop_buffer: Option<&'a CapturedDesktop>,
    pub hovered_title: Option<&'a str>,
    /// Name of the monitor under the cursor, which `broadcast_ui_state`
    /// already works out for the shared state.
    pub hovered_monitor_name: Option<&'a str>,
    pub hovered_bounds: Option<ScreenRect>,
    pub hovered_index: Option<usize>,
    pub hovered_obstructed: bool,
    /// The cursor image's rect, already `None` when the peek covers it —
    /// `broadcast_ui_state` works both facts out once per tick.
    pub cursor_image_rect: Option<ScreenRectF>,
    /// Already ANDed with "the peek does not cover the cursor".
    pub cursor_overlay_visible: bool,
}

/// The host index that shows the tray and what it shows, or `None` when
/// there is no panel at all. The set decision is `active_panel_set`, the
/// one place that answers it for the swap guard, the renderers and this.
fn panel_inputs(ui_monitors: &[UiMonitor], input: &InteractionState, settings: &CapturerSettings) -> Option<(usize, PanelInputs)> {
    let set = active_panel_set(input.captured, input.scroll_pick_mode, &input.ocr)?;
    let selection = input.selection?;
    let monitor = pick_monitor_containing_center(ui_monitors, selection)?;
    let index = ui_monitors
        .iter()
        .position(|m| m.bounds == monitor.bounds)?;
    let anchor = intersect_rects(monitor.bounds, selection)?;
    let readout = match (&set, &input.ocr) {
        (
            PanelButtonSet::Ocr,
            OcrState::Lifted {
                outcome,
                ..
            },
        ) => Readout::words_in(&outcome.full_text),
        _ => Readout::Size {
            width: selection.width(),
            height: selection.height(),
        },
    };
    Some((
        index,
        PanelInputs {
            set,
            features: settings.panel_features,
            style: settings.panel_buttons,
            accent: accent_color32(settings.accent_color),
            readout,
            selection,
            anchor,
        },
    ))
}

pub struct EguiHosts {
    /// One per monitor, in `UiMonitor` order.
    monitors: Vec<MonitorHost>,
    /// The last published frame list, rebuilt only after a host has run.
    frames: Option<Arc<[Option<Arc<EguiFrame>>]>>,
    /// Read by the debug panels' startup block, which is the same for every
    /// monitor.
    startup: Arc<StartupTimings>,
    /// RAM/VRAM pollers, one per distinct adapter, built lazily here
    /// because they cache DXGI interfaces and COM pointers cannot leave the
    /// thread that made them.
    pollers: HashMap<Option<(u32, u32)>, ResourcePoller>,
    /// The latest snapshot each monitor's worker has published.
    perf: Vec<Option<Arc<PerfSnapshot>>>,
    /// Reused across every panel of every monitor: the row formatting is
    /// the only allocation-heavy part of a debug tick.
    lines: LineBuf,
    /// Whether the curated system faces have already been pushed onto every
    /// context. One install per process: rebuilding the atlas is the cost,
    /// and the face list never changes within a capture.
    fonts_installed: bool,
}

struct MonitorHost {
    ctx: egui::Context,
    monitor: UiMonitor,
    shadow: TextureShadow,
    /// What the last run's final pass found under the pointer.
    outcome: PanelOutcome,
    /// What this host last produced, published to its worker.
    last: Option<Arc<EguiFrame>>,
    /// The inputs of the last run — the memo key.
    inputs: Option<HostInputs>,
    /// Origin of this host's `RawInput::time`.
    epoch: Instant,
    /// Where this host has already told egui the pointer is. egui keeps
    /// the last pointer position across passes, so repeating it is not
    /// news — and any event at all makes egui ask to be run again
    /// immediately, which would keep this host, and through it the whole
    /// app thread, running at [`REPAINT_FLOOR`] for as long as the capture
    /// lives. `None` is the state a fresh context starts in.
    fed_pointer: Option<Pos2>,
    /// When egui asked to be run again: a hover fade still in progress, or
    /// the second of the two repaints egui answers every immediate request
    /// with. `None` once nothing is owed a run, which is what lets the app
    /// thread go quiet between mouse events.
    repaint_after: Option<Instant>,
    /// True between a `set_fonts` and the pass that applies it. egui swaps
    /// the font set at the next `begin_pass`, so until this host has run
    /// once more its published frame still carries the old atlas.
    fonts_dirty: bool,
}

impl EguiHosts {
    pub fn new(ui_monitors: &[UiMonitor], startup: Arc<StartupTimings>) -> Self {
        Self {
            monitors: ui_monitors
                .iter()
                .map(|m| MonitorHost::new(*m))
                .collect(),
            frames: None,
            startup,
            pollers: HashMap::new(),
            perf: vec![None; ui_monitors.len()],
            lines: LineBuf::new(),
            fonts_installed: false,
        }
    }

    /// Recompute every host's inputs and run the hosts whose inputs
    /// changed or whose scheduled repaint has come due. Returns the
    /// outcome of the host that shows the tray.
    pub fn sync(&mut self, args: SyncArgs<'_>) -> PanelOutcome {
        let now = Instant::now();
        let cursor = args.input.virtual_cursor;
        let panel = panel_inputs(args.ui_monitors, args.input, args.settings);
        // Everything the overlays' `inputs` builders share, computed once
        // for the whole sync rather than once per monitor.
        let ictx = InputCtx {
            input: args.input,
            monitors: args.ui_monitors,
            cursor_index: monitor_index_at(args.ui_monitors, cursor),
            accent: accent_color32(args.settings.accent_color),
            hovered_window_title: args.hovered_title,
            hovered_monitor_name: args.hovered_monitor_name,
            hovered_pixel_bgra: args
                .desktop_buffer
                .and_then(|b| sample_bgra(b, to_screen_point(cursor))),
            cursor_image_rect: args.cursor_image_rect,
            cursor_overlay_visible: args.cursor_overlay_visible,
        };
        let mut any_ran = false;
        let mut outcome = PanelOutcome::default();
        for index in 0..self.monitors.len() {
            let monitor = self.monitors[index].monitor;
            // Built before the host is borrowed: it reads the pollers, the
            // line buffer and the snapshot table, all of which sit beside
            // the host list rather than on a host.
            let debug = self.debug_inputs(index, monitor, &args);
            let overlays = OverlayInputs {
                overlays_visible: args.input.overlays_visible,
                accent: ictx.accent,
                area: area::show::inputs(index, &monitor, &ictx),
                tips: tips::show::inputs(index, &monitor, &ictx),
                hints: hints::show::inputs(index, &monitor, &ictx),
                notice: hints::show::notice_inputs(index, &monitor, &ictx),
                scope: scope::show::inputs(index, &monitor, &ictx),
                ocr: ocr::show::inputs(index, &monitor, &ictx),
            };
            let host = &mut self.monitors[index];
            let inputs = HostInputs {
                panel: panel
                    .filter(|(i, _)| *i == index)
                    .map(|(_, p)| p),
                debug,
                pointer: host.pointer(cursor),
                overlays,
            };
            let due = host.repaint_after.is_some_and(|t| t <= now);
            if due || host.inputs.as_ref() != Some(&inputs) {
                host.run(&inputs, false);
                any_ran = true;
            }
            if inputs.panel.is_some() {
                outcome = host.outcome;
            }
        }
        if any_ran {
            self.frames = None;
        }
        outcome
    }

    /// Format one monitor's debug panels, or `None` when the toggle is off
    /// or this monitor's worker has not published a snapshot yet. Every
    /// live number on the monitor panel comes from that snapshot, so there
    /// is nothing worth showing until the first one lands, which is the
    /// first frame the worker draws with the toggle on.
    fn debug_inputs(&mut self, index: usize, monitor: UiMonitor, args: &SyncArgs<'_>) -> Option<DebugInputs> {
        let input = args.input;
        if !debug_monitor_visibility(input.overlays_visible, input.debug_visible) {
            return None;
        }
        let snapshot = self.perf.get(index)?.clone()?;
        let info = args.monitors.get(index)?;
        let readings = self
            .pollers
            .entry(info.adapter_id)
            .or_insert_with(|| ResourcePoller::new(info.adapter_id))
            .readings();

        MonitorPanelData {
            index,
            name: &info.name,
            is_primary: monitor.is_primary,
            adapter: &snapshot.adapter_name,
            vram: readings.vram_adapter,
            dpi: (monitor.dpi_scale * 96.0).round() as u32,
            bounds: monitor.bounds,
            time_to_first_render: self
                .startup
                .background
                .workers
                .get(index)
                .and_then(|w| w.first_render.get()),
            perf: &snapshot,
            target_period: snapshot.target_period,
        }
        .write_lines(&mut self.lines);
        let monitor_panel = MonitorPanelInputs {
            lines: self
                .lines
                .iter()
                .map(|(text, color)| (text.to_owned(), color))
                .collect(),
            bars: snapshot.bars.as_slice().into(),
            target_period: snapshot.target_period,
            window_size: snapshot.window_size,
            seq: snapshot.seq,
        };

        let mut primary = None;
        if debug_primary_visibility(input.overlays_visible, input.debug_visible, input.virtual_cursor, monitor.bounds) {
            PrimaryPanelData {
                startup: &self.startup,
                zoom: input.zoom,
                cursor: input.virtual_cursor,
                color_bgra: args
                    .desktop_buffer
                    .and_then(|buf| sample_bgra(buf, to_screen_point(input.virtual_cursor))),
                dragging: input.dragging,
                captured: input.captured,
                selection: input.selection,
                hovered_window_title: args.hovered_title,
                hovered_window_bounds: args.hovered_bounds,
                hovered_window_index: args.hovered_index,
                hovered_window_obstructed: args.hovered_obstructed,
                ram: readings.ram,
                vram_total: readings.vram_total,
            }
            .write_lines(&mut self.lines);
            primary = Some(PrimaryPanelInputs {
                lines: self
                    .lines
                    .iter()
                    .map(|(text, color)| (text.to_owned(), color))
                    .collect(),
            });
        }

        Some(DebugInputs {
            monitor: monitor_panel,
            primary,
        })
    }

    /// Read every worker's perf slot and report whether any monitor's
    /// timings moved. The caller broadcasts on `true`, which is what makes
    /// the panels tick while nothing else in the session changes. Every
    /// call reads: the workers publish a snapshot per frame while the
    /// panels are up, and a poll that quantised that to its own cadence is
    /// what used to make the plot step rather than scroll. The cost is one
    /// uncontended mutex per monitor per tick.
    pub fn debug_poll(&mut self, windows: &WindowSet, ui_monitors: &[UiMonitor], debug_visible: bool) -> bool {
        if !debug_visible {
            return false;
        }
        let mut advanced = false;
        for handle in windows.values() {
            let Some(snapshot) = handle.perf_snapshot() else {
                continue;
            };
            let bounds = handle.monitor_bounds();
            let Some(index) = ui_monitors
                .iter()
                .position(|m| m.bounds == bounds)
            else {
                continue;
            };
            let Some(slot) = self.perf.get_mut(index) else {
                continue;
            };
            if slot.as_ref().map(|s| s.seq) != Some(snapshot.seq) {
                *slot = Some(snapshot);
                advanced = true;
            }
        }
        advanced
    }

    /// Feed the tray a press and read the click back in the same call,
    /// because the overlay fires commands on the OS press. Only the host
    /// that shows the tray is touched; every other host keeps what the
    /// last [`Self::sync`] gave it.
    ///
    /// This always runs, memo or not: the press events are not part of the
    /// memo key, and it is the run that answers the click. A move needs no
    /// counterpart here — the broadcast that ends every cursor move runs
    /// the same host through [`Self::sync`] and returns the same outcome,
    /// with this move's debug rows instead of the previous tick's.
    pub fn panel_press(&mut self, input: &InteractionState, settings: &CapturerSettings, ui_monitors: &[UiMonitor]) -> PanelOutcome {
        let Some((index, panel)) = panel_inputs(ui_monitors, input, settings) else {
            return PanelOutcome::default();
        };
        let Some(host) = self.monitors.get_mut(index) else {
            return PanelOutcome::default();
        };
        let inputs = HostInputs {
            panel: Some(panel),
            // A press never re-derives the debug rows: whatever the last
            // `sync` gave this host stays, and no broadcast follows a
            // captured press anyway.
            debug: host
                .inputs
                .as_ref()
                .and_then(|i| i.debug.clone()),
            pointer: host.pointer(input.virtual_cursor),
            // Same for the overlays: a press changes nothing they draw, so
            // whatever the last run gave this host stays on screen.
            overlays: host
                .inputs
                .as_ref()
                .map(|i| i.overlays.clone())
                .unwrap_or_default(),
        };
        let outcome = host.run(&inputs, true);
        self.frames = None;
        outcome
    }

    /// The per-monitor frames to publish. Cached: unchanged hosts hand
    /// every worker the same `Arc` they already hold.
    pub fn frames(&mut self) -> Arc<[Option<Arc<EguiFrame>>]> {
        match &self.frames {
            Some(frames) => frames.clone(),
            None => {
                let frames: Arc<[Option<Arc<EguiFrame>>]> = self
                    .monitors
                    .iter()
                    .map(|h| h.last.clone())
                    .collect();
                self.frames = Some(frames.clone());
                frames
            }
        }
    }

    /// True when any host asked to be run again by now. The event loop
    /// polls this: a scheduled repaint deliberately bypasses the input
    /// memo, which is what lets an animation advance while nothing moves.
    pub fn repaint_due(&self, now: Instant) -> bool {
        self.monitors
            .iter()
            .any(|h| h.repaint_after.is_some_and(|t| t <= now))
    }

    /// Push the curated system faces onto every host and force one pass on
    /// each, so the atlas rebuild happens now rather than on whichever frame
    /// first asks for a glyph Cascadia lacks.
    ///
    /// Every host, not just the one showing the region: an OCR bubble can
    /// straddle a seam, so both monitors have to be able to draw the same
    /// text. Idempotent, and it never touches `ctx.fonts` — reading the font
    /// set outside a pass panics on a context that has not run one, whereas
    /// `set_fonts` merely stages the swap for the next `begin_pass`, which
    /// the forced repaint guarantees on the next `about_to_wait`.
    pub fn install_fallback_fonts(&mut self) -> FontInstall {
        if self.fonts_installed {
            return FontInstall::AlreadyInstalled;
        }
        let Some(faces) = fonts::system_faces() else {
            return FontInstall::ScanPending;
        };
        let defs = fonts::font_definitions(&faces);
        let now = Instant::now();
        for host in &mut self.monitors {
            host.ctx.set_fonts(defs.clone());
            host.fonts_dirty = true;
            host.repaint_after = Some(now);
        }
        self.fonts_installed = true;
        FontInstall::Installed
    }

    /// True once no host still owes the pass that applies a `set_fonts`. The
    /// OCR release waits on this so the reveal never lands on a frame whose
    /// atlas is still the Cascadia-only one.
    pub fn fonts_settled(&self) -> bool {
        !self.monitors.iter().any(|h| h.fonts_dirty)
    }
}

/// What [`EguiHosts::install_fallback_fonts`] did.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FontInstall {
    /// The faces were pushed onto every host by this call.
    Installed,
    /// An earlier call already pushed them.
    AlreadyInstalled,
    /// The background scan has not landed yet, so nothing was installed.
    ScanPending,
}

impl MonitorHost {
    fn new(monitor: UiMonitor) -> Self {
        let ctx = egui::Context::default();
        // The tray's marks are SVGs loaded through egui: one loader set per
        // context, so one set of textures per monitor DPI. Idempotent.
        egui_extras::install_image_loaders(&ctx);
        ctx.set_fonts(fonts::font_definitions(&[]));
        theme::apply_style(&ctx);
        ctx.options_mut(|o| {
            // Nothing here reads the keyboard: egui must not swallow the
            // overlay's own accelerators, and it must not quit the process.
            o.zoom_with_keyboard = false;
            o.quit_shortcuts.clear();
        });
        // `about_to_wait` polls `repaint_after`; there is no thread to wake.
        ctx.set_request_repaint_callback(|_| {});
        Self {
            ctx,
            monitor,
            shadow: TextureShadow::default(),
            outcome: PanelOutcome::default(),
            last: None,
            inputs: None,
            epoch: Instant::now(),
            repaint_after: None,
            fed_pointer: None,
            fonts_dirty: false,
        }
    }

    /// This monitor's DPI scale, which is also egui's `pixels_per_point`.
    fn ppp(&self) -> f32 {
        self.monitor.dpi_scale.max(0.1)
    }

    /// The virtual-desktop cursor in this monitor's points, or `None` when
    /// it is over some other monitor (half-open bounds, the same rule
    /// `shared::monitor_at` uses).
    fn pointer(&self, cursor: ScreenPointF) -> Option<Pos2> {
        let b = self.monitor.bounds;
        let (x, y) = (cursor.x.round() as i32, cursor.y.round() as i32);
        if x < b.left() || x >= b.right() || y < b.top() || y >= b.bottom() {
            return None;
        }
        let ppp = self.ppp();
        Some(egui::pos2((cursor.x - b.left() as f32) / ppp, (cursor.y - b.top() as f32) / ppp))
    }

    /// Build one pass's input, reporting the pointer only when it has
    /// actually moved since the last pass this host fed egui — see
    /// [`MonitorHost::fed_pointer`] for why a repeat is not free.
    fn raw_input(&mut self, pointer: Option<Pos2>, extra: &[egui::Event]) -> egui::RawInput {
        let ppp = self.ppp();
        let b = self.monitor.bounds;
        let mut events = Vec::with_capacity(extra.len() + 1);
        if self.fed_pointer != pointer {
            events.push(match pointer {
                Some(p) => egui::Event::PointerMoved(p),
                None => egui::Event::PointerGone,
            });
            self.fed_pointer = pointer;
        }
        events.extend_from_slice(extra);
        egui::RawInput {
            viewport_id: egui::ViewportId::ROOT,
            viewports: [(
                egui::ViewportId::ROOT,
                egui::ViewportInfo {
                    native_pixels_per_point: Some(ppp),
                    ..Default::default()
                },
            )]
            .into_iter()
            .collect(),
            screen_rect: Some(egui::Rect::from_min_size(
                egui::Pos2::ZERO,
                egui::vec2(b.width() as f32, b.height() as f32) / ppp,
            )),
            // The font atlas is at least this wide, and the whole atlas is
            // re-uploaded whenever it grows, so this bounds that upload. A
            // recognised CJK page at several bubble sizes needs the taller
            // sheet; the atlas still starts at 2048 x 32 and only grows on
            // demand, so the higher ceiling costs nothing until it is used.
            max_texture_side: Some(2048),
            time: Some(self.epoch.elapsed().as_secs_f64()),
            predicted_dt: 1.0 / 60.0,
            focused: true,
            events,
            ..Default::default()
        }
    }

    /// Run egui until it stops asking for another pass (bounded by
    /// [`MAX_PASSES`]), publishing the last pass's primitives.
    ///
    /// With `press`, a press-and-release pair is fed in the SECOND pass,
    /// never the first: egui hit-tests a press against the previous
    /// pass's widget rects, and the first pass is what gives it this
    /// strip's. `clicked` is ORed over every pass — egui reports the
    /// click in exactly one of them — while the `over_*` flags come from
    /// the last pass, which is the one that saw the settled pointer.
    fn run(&mut self, inputs: &HostInputs, press: bool) -> PanelOutcome {
        // A press with no pointer on this monitor has nowhere to land, so
        // it is simply not fed; the plan's `expect` would panic the app
        // thread for a cursor resting in a gap between monitors.
        let press_events: Vec<egui::Event> = match inputs.pointer.filter(|_| press) {
            Some(pos) => [true, false]
                .into_iter()
                .map(|pressed| egui::Event::PointerButton {
                    pos,
                    button: egui::PointerButton::Primary,
                    pressed,
                    modifiers: egui::Modifiers::NONE,
                })
                .collect(),
            None => Vec::new(),
        };
        let mut pending_press = !press_events.is_empty();
        let mut acc = PanelOutcome::default();
        let mut passes = 0u32;
        loop {
            let extra: &[egui::Event] = if pending_press && passes >= 1 {
                pending_press = false;
                &press_events
            } else {
                &[]
            };
            let raw = self.raw_input(inputs.pointer, extra);
            let fed_events = !raw.events.is_empty();
            let mut pass = PanelOutcome::default();
            let monitor = self.monitor;
            let mut full = self.ctx.run_ui(raw, |ui| {
                pass = components::compose(ui.ctx(), inputs, monitor);
            });
            self.shadow.apply(&mut full.textures_delta);
            let primitives = self
                .ctx
                .tessellate(full.shapes, full.pixels_per_point);
            self.last = Some(Arc::new(EguiFrame {
                pixels_per_point: full.pixels_per_point,
                primitives,
                textures: self.shadow.snapshot(),
            }));
            acc = PanelOutcome {
                clicked: acc.clicked.or(pass.clicked),
                over_button: pass.over_button,
                over_tray: pass.over_tray,
            };
            passes += 1;
            let delay = full
                .viewport_output
                .get(&egui::ViewportId::ROOT)
                .map_or(Duration::MAX, |v| v.repaint_delay);
            // Another pass is owed only to events: egui hit-tests and
            // hovers against the pass that saw one and asks for a settling
            // pass afterwards. A pass that fed nothing has nothing left to
            // settle, and any repaint it asked for is an animation, which
            // belongs to the next scheduled tick rather than to three more
            // passes of this one.
            if (pending_press || (fed_events && delay == Duration::ZERO)) && passes < MAX_PASSES {
                continue;
            }
            self.repaint_after = (delay != Duration::MAX).then(|| Instant::now() + delay.max(REPAINT_FLOOR));
            self.inputs = Some(inputs.clone());
            self.fonts_dirty = false;
            // A click belongs to the run that answered it, never to the
            // stored flags: a later memo hit must not hand the same
            // command out a second time.
            self.outcome = PanelOutcome {
                clicked: None,
                ..acc
            };
            return acc;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use clowd_rust_core::geometry::ScreenRect;

    fn monitor(x: i32, dpi: f32) -> UiMonitor {
        UiMonitor {
            bounds: ScreenRect::from_xy_size(x, 0, 1920, 1080),
            dpi_scale: dpi,
            is_primary: x == 0,
        }
    }

    /// The whole point of the step: a host survives a real egui pass with
    /// the bundled fonts, and publishes something a worker can paint. A
    /// font-definition mistake panics inside `run_ui`.
    #[test]
    fn a_run_publishes_a_frame_carrying_the_font_atlas() {
        let mut host = MonitorHost::new(monitor(0, 1.0));
        let inputs = HostInputs {
            panel: None,
            debug: None,
            pointer: Some(egui::pos2(10.0, 10.0)),
            overlays: OverlayInputs::default(),
        };
        host.run(&inputs, false);
        let frame = host
            .last
            .clone()
            .expect("a run publishes a frame");
        assert_eq!(frame.pixels_per_point, 1.0);
        // egui allocates the font atlas on the first pass even for an
        // empty UI, so the snapshot is never empty after a run.
        assert!(!frame.textures.textures.is_empty());
        assert!(host.inputs == Some(inputs));
    }

    /// egui keeps the last pointer position across passes, and any event
    /// at all makes it ask to be run again immediately
    /// (`InputState::wants_repaint_after`). Reporting a pointer that has
    /// not moved would therefore arm [`REPAINT_FLOOR`] on every tick and
    /// keep the app thread rebroadcasting for the life of the capture.
    #[test]
    fn only_a_pointer_that_moved_is_an_event() {
        let mut host = MonitorHost::new(monitor(0, 1.0));
        let p = egui::pos2(10.0, 10.0);
        assert_eq!(host.raw_input(Some(p), &[]).events, vec![egui::Event::PointerMoved(p)]);
        assert!(host
            .raw_input(Some(p), &[])
            .events
            .is_empty());
        let q = egui::pos2(11.0, 10.0);
        assert_eq!(host.raw_input(Some(q), &[]).events, vec![egui::Event::PointerMoved(q)]);
        assert_eq!(host.raw_input(None, &[]).events, vec![egui::Event::PointerGone]);
        // A context starts with no pointer, so a repeated `None` is no
        // more news than a repeated position.
        assert!(host.raw_input(None, &[]).events.is_empty());
    }

    /// A tick that tells egui nothing new costs one pass and leaves
    /// nothing scheduled. `about_to_wait` polls `repaint_after` and a
    /// scheduled run bypasses the input memo, so a host that always asked
    /// for one would rerun every context and ship a fresh `UiSharedState`
    /// to every worker at the repaint floor, forever.
    #[test]
    fn a_tick_that_changes_nothing_costs_one_pass_and_schedules_nothing() {
        let mut host = MonitorHost::new(monitor(0, 1.0));
        let inputs = HostInputs {
            panel: None,
            debug: None,
            pointer: Some(egui::pos2(10.0, 10.0)),
            overlays: OverlayInputs::default(),
        };
        host.run(&inputs, false);
        let before = host.ctx.cumulative_pass_nr();
        host.run(&inputs, false);
        assert_eq!(host.ctx.cumulative_pass_nr() - before, 1);
        assert!(host.repaint_after.is_none());
    }

    /// A move costs the pass that saw it plus the settling pass egui asks
    /// for afterwards, and arms exactly one more tick — egui answers every
    /// immediate repaint request twice, to give frame-delayed responses
    /// time to settle — after which the host is quiet again. The property
    /// under test is that the armed tick ends the work rather than arming
    /// the next one.
    #[test]
    fn a_move_settles_in_two_passes_and_one_scheduled_tick() {
        let mut host = MonitorHost::new(monitor(0, 1.0));
        let mut inputs = HostInputs {
            panel: None,
            debug: None,
            pointer: Some(egui::pos2(10.0, 10.0)),
            overlays: OverlayInputs::default(),
        };
        host.run(&inputs, false);
        let before = host.ctx.cumulative_pass_nr();
        inputs.pointer = Some(egui::pos2(40.0, 10.0));
        host.run(&inputs, false);
        assert_eq!(host.ctx.cumulative_pass_nr() - before, 2);
        assert!(host.repaint_after.is_some());

        let before = host.ctx.cumulative_pass_nr();
        host.run(&inputs, false);
        assert_eq!(host.ctx.cumulative_pass_nr() - before, 1);
        assert!(host.repaint_after.is_none());
    }

    /// Every animation in the migrated stack — the sweep, the reveal, the
    /// comet, the notice fade, the debug plot — rides on one mechanism: a
    /// show function asks for a tick inside the pass, and the host turns
    /// the viewport's repaint delay into its own `repaint_after`. Break
    /// that link and the sweep freezes at its first band position with
    /// every other test still green, so it is pinned here at both ends: an
    /// overlay still animating arms a tick, a settled one arms nothing.
    #[test]
    fn an_animated_overlay_schedules_a_tick_and_a_settled_one_does_not() {
        let region = ScreenRect::from_xy_size(200, 200, 800, 400);
        let lifted = |anchor: Instant| HostInputs {
            panel: None,
            debug: None,
            pointer: None,
            overlays: OverlayInputs {
                overlays_visible: true,
                ocr: Some(ocr::show::OcrInputs {
                    region,
                    radius: 8.0,
                    phase: ocr::show::Phase::Lifted {
                        anchor,
                        req: 1,
                        dpi: 1.0,
                        outcome: Arc::new(crate::ocr::OcrOutcome {
                            lines: Vec::new(),
                            full_text: String::new(),
                            text_angle: 0.0,
                        }),
                    },
                }),
                ..Default::default()
            },
        };

        // egui sends a theme command on a context's very first pass, which
        // arms an immediate repaint of its own, and every immediate repaint
        // is answered twice. Running the empty host until it goes quiet
        // settles that, so what the assertions below see is the overlay's
        // own request and nothing else.
        let quiet = HostInputs {
            panel: None,
            debug: None,
            pointer: None,
            overlays: OverlayInputs::default(),
        };
        let mut host = MonitorHost::new(monitor(0, 1.0));
        for _ in 0..4 {
            host.run(&quiet, false);
            if host.repaint_after.is_none() {
                break;
            }
        }
        assert!(host.repaint_after.is_none(), "a host with nothing on it settles");

        host.run(&lifted(Instant::now()), false);
        assert!(host.repaint_after.is_some(), "the reveal is still in flight");

        // Two seconds is well past the whole reveal (1.38 s), so the
        // bubbles are at rest and nothing more is owed.
        if let Some(settled) = Instant::now().checked_sub(Duration::from_secs(2)) {
            host.run(&lifted(settled), false);
            assert!(host.repaint_after.is_none(), "a settled overlay schedules nothing");
        }
    }

    /// Installing the fallback faces marks every host dirty and arms it to
    /// run; the pass that applies the new font set is what clears the mark.
    /// The OCR release gates on `fonts_settled`, so a host that never
    /// cleared its mark would stall the reveal, and one that cleared it
    /// without running would let the reveal draw against the old atlas.
    #[test]
    fn installing_fonts_forces_one_pass_and_settles() {
        // The scan is what the install waits on; running it here is also the
        // only way this test can be deterministic about `Installed`.
        fonts::begin_system_font_scan();
        fonts::wait_for_system_font_scan(Duration::from_secs(60));
        let ui_monitors = [monitor(0, 1.0), monitor(1920, 1.5)];
        let mut hosts = EguiHosts::new(&ui_monitors, Arc::new(StartupTimings::new(Instant::now(), ui_monitors.len())));
        assert!(hosts.fonts_settled(), "nothing is owed a pass before an install");

        assert_eq!(hosts.install_fallback_fonts(), FontInstall::Installed);
        assert!(!hosts.fonts_settled());
        assert!(hosts.repaint_due(Instant::now()), "every host is armed to run now");

        let inputs = HostInputs {
            panel: None,
            debug: None,
            pointer: None,
            overlays: OverlayInputs::default(),
        };
        for host in &mut hosts.monitors {
            host.run(&inputs, false);
        }
        assert!(hosts.fonts_settled());
        assert_eq!(hosts.install_fallback_fonts(), FontInstall::AlreadyInstalled);
        assert!(hosts.fonts_settled(), "a second install is a no-op");
    }

    /// Q hides the tray by painting it at opacity 0, not by skipping it:
    /// the strip still runs, so the pointer still finds its buttons and a
    /// press still routes, while nothing of it reaches the frame. Skipping
    /// the run instead would drop the clicks the overlay has always kept.
    #[test]
    fn the_tray_is_hit_tested_but_invisible_under_q() {
        let monitor = monitor(0, 1.0);
        let selection = ScreenRect::from_xy_size(500, 300, 600, 400);
        let panel = PanelInputs {
            set: PanelButtonSet::Normal,
            features: PanelFeatures::ALL,
            style: ButtonStyle::KeyHint,
            accent: Color32::from_rgb(0x2F, 0x7C, 0xAE),
            readout: Readout::Size {
                width: selection.width(),
                height: selection.height(),
            },
            selection,
            anchor: selection,
        };
        let visible = |on: bool, pointer: Option<Pos2>| HostInputs {
            panel: Some(panel),
            debug: None,
            pointer,
            overlays: OverlayInputs {
                overlays_visible: on,
                ..Default::default()
            },
        };
        // Two layout runs first: an `Area` allocates a provisional
        // full-height rect on its very first pass, so the buttons' own
        // rects only exist from the second one. The smallest of them is a
        // button; the largest is the chassis, which senses clicks whole.
        let mut host = MonitorHost::new(monitor);
        host.run(&visible(true, None), false);
        host.run(&visible(true, None), false);
        let button = host
            .ctx
            .interactive_rects_last_pass()
            .into_iter()
            .min_by(|a, b| a.area().total_cmp(&b.area()))
            .expect("the strip registered its buttons");

        let shown = host.run(&visible(true, Some(button.center())), false);
        assert!(shown.over_button && shown.over_tray, "{shown:?}");
        assert!(vertices(&host) > 0, "a visible tray paints");

        let hidden = host.run(&visible(false, Some(button.center())), false);
        assert!(hidden.over_button, "the button is still under the pointer");
        assert!(hidden.over_tray);
        assert_eq!(vertices(&host), 0, "a hidden tray paints nothing");
    }

    /// Every vertex this host's last published frame carries.
    fn vertices(host: &MonitorHost) -> usize {
        host.last.as_ref().map_or(0, |f| {
            f.primitives
                .iter()
                .map(|p| match &p.primitive {
                    egui::epaint::Primitive::Mesh(m) => m.vertices.len(),
                    egui::epaint::Primitive::Callback(_) => 0,
                })
                .sum()
        })
    }

    /// Decision 2: every host works in its own monitor's points, and a
    /// cursor on another monitor is simply gone.
    #[test]
    fn the_pointer_is_monitor_local_and_gone_off_monitor() {
        let host = MonitorHost::new(monitor(1920, 1.25));
        assert_eq!(host.pointer(ScreenPointF::new(1000.0, 500.0)), None);
        let local = host
            .pointer(ScreenPointF::new(2020.0, 500.0))
            .expect("the cursor is on this monitor");
        assert_eq!(local, egui::pos2(80.0, 400.0));
    }
}
