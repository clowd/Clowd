//! GPU debug-panel renderer.
//!
//! Mirrors the structure of [`super::tips::TipsRenderer`]: owns a `Vec`
//! of cached glyphon buffers (one per text line we might render), emits
//! rect + text positions in `prepare()`, and hands out `TextArea`s via
//! `text_areas()` for the parent `UiRenderer` to pass into glyphon.
//!
//! Two panels share one renderer:
//!   * **Monitor Info** — drawn on every monitor when `debug_visible`,
//!     anchored top-left.
//!   * **Primary Debug** — drawn only on the cursor's monitor, anchored
//!     top-right.

use glyphon::{Attrs, Buffer, Color, Family, Metrics, Shaping, TextArea, TextBounds, Weight, Wrap};

use crate::geometry::RectExt;
use crate::ui::components::debug::layout::{compute_layout, DebugPanelLayout, PanelAnchor, BODY_FONT_PX};
use crate::ui::components::debug::model::{MonitorPanelData, PrimaryPanelData};
use std::time::Duration;

use crate::ui::components::debug::perf::PerfTracker;
use crate::ui::components::debug::startup::StartupTimings;
use crate::ui::gpu::rect::RectInstance;
use crate::ui::gpu::text::{TextStack, FAMILY_MONO};
use crate::ui::shared::{debug_monitor_visibility, debug_primary_visibility, UiMonitor, UiSharedState};

/// Panel body opacity (dark overlay). Matches C++ `brushOverlay70` at
/// `DxOutputDevice.cpp:252-275`.
const BODY_ALPHA: f32 = 0.70;

/// One cached glyphon buffer + last-rendered content for change detection.
/// Same pattern as `tips::CachedBuffer`, simplified — we only ever use
/// the mono family at the body size.
struct CachedLine {
    buffer: Buffer,
    last_text: String,
}

impl CachedLine {
    fn new(ts: &mut TextStack, font_px: f32) -> Self {
        let metrics = Metrics::new(font_px, font_px * 1.2);
        let mut buffer = Buffer::new(&mut ts.font_system, metrics);
        buffer.set_wrap(&mut ts.font_system, Wrap::None);
        Self {
            buffer,
            last_text: String::new(),
        }
    }

    /// Update content + font size. Returns `true` if shaped this call.
    fn set(&mut self, ts: &mut TextStack, text: &str, font_px: f32) -> bool {
        // Re-set metrics every time — cheap, and keeps font size in sync
        // with a changing DPI (rare, but free to handle).
        let metrics = Metrics::new(font_px, font_px * 1.2);
        self.buffer
            .set_metrics(&mut ts.font_system, metrics);

        if text == self.last_text {
            return false;
        }
        let attrs = Attrs::new()
            .family(Family::Name(FAMILY_MONO))
            .weight(Weight::NORMAL);
        self.buffer
            .set_text(&mut ts.font_system, text, &attrs, Shaping::Advanced, None);
        self.buffer
            .shape_until_scroll(&mut ts.font_system, false);
        self.last_text.clear();
        self.last_text.push_str(text);
        true
    }

    fn width(&self) -> f32 {
        self.buffer
            .layout_runs()
            .map(|r| r.line_w)
            .fold(0.0f32, f32::max)
    }
}

/// Window-local position of one text line.
#[derive(Clone, Copy)]
struct PositionedLine {
    idx: usize,
    x: f32,
    y: f32,
}

pub struct DebugRenderer {
    /// Pool of reusable line buffers, grown on demand. Each frame's
    /// rendering re-uses the first N buffers where N = total lines across
    /// both panels. `CachedLine::set` is a no-op when the text hasn't
    /// changed, so static lines (header, adapter name) don't re-shape.
    lines: Vec<CachedLine>,
    /// Positioned lines produced by the latest `prepare`, consumed by
    /// `text_areas`.
    positions: Vec<PositionedLine>,
    /// Monitor index this renderer was spawned for. Used as the `0:` /
    /// `1:` prefix in the monitor-panel header so multi-display indexing
    /// matches the OS enumeration order.
    monitor_index: usize,
}

impl DebugRenderer {
    pub fn new(monitor_index: usize) -> Self {
        Self {
            lines: Vec::new(),
            positions: Vec::new(),
            monitor_index,
        }
    }

    /// Prepare the debug panels for this frame. No-op when
    /// `debug_visible` is off.
    ///
    /// All inputs borrowed — the renderer never holds onto them past this
    /// call.
    #[allow(clippy::too_many_arguments)]
    pub fn prepare(
        &mut self,
        ts: &mut TextStack,
        state: &UiSharedState,
        this_monitor: &UiMonitor,
        monitor_name: &str,
        adapter_name: &str,
        perf: &PerfTracker,
        startup: &StartupTimings,
        shown_time: Option<Duration>,
        time_to_first_render: Option<Duration>,
        rects: &mut Vec<RectInstance>,
    ) {
        self.positions.clear();

        if !debug_monitor_visibility(state, this_monitor) && !debug_primary_visibility(state, this_monitor) {
            return;
        }

        let dpi = this_monitor.dpi_scale.max(0.1);
        let font_px = (BODY_FONT_PX * dpi).floor();

        // --- Monitor panel (every monitor when debug_visible) ---
        if debug_monitor_visibility(state, this_monitor) {
            let latest_overall = perf
                .latest()
                .map(|s| s.draw + s.present)
                .unwrap_or_default();
            let data = MonitorPanelData {
                index: self.monitor_index,
                name: monitor_name,
                is_primary: this_monitor.is_primary,
                adapter: adapter_name,
                dpi: (dpi * 96.0).round() as u32,
                bounds: this_monitor.bounds,
                time_to_render: latest_overall,
                time_to_first_render,
                perf,
            };
            let text_lines = data.lines();
            self.render_panel(ts, this_monitor, dpi, font_px, &text_lines, PanelAnchor::TopLeft, rects);
        }

        // --- Primary panel (cursor monitor only) ---
        if debug_primary_visibility(state, this_monitor) {
            let data = PrimaryPanelData {
                startup,
                shown_time,
                zoom: state.zoom,
                cursor: state.virtual_cursor,
                color_bgra: state.hovered_pixel_bgra,
                dragging: state.dragging,
                captured: state.captured,
                selection: state.selection,
                hovered_window_title: state.hovered_window_title.as_deref(),
                hovered_window_bounds: state.hovered_window_bounds,
            };
            let text_lines = data.lines();
            self.render_panel(ts, this_monitor, dpi, font_px, &text_lines, PanelAnchor::TopRight, rects);
        }
    }

    /// Shared path for both panels: shape all lines, measure widest,
    /// compute layout, emit one background rect + N line positions.
    #[allow(clippy::too_many_arguments)]
    fn render_panel(
        &mut self,
        ts: &mut TextStack,
        this_monitor: &UiMonitor,
        _dpi: f32,
        font_px: f32,
        text_lines: &[String],
        anchor: PanelAnchor,
        rects: &mut Vec<RectInstance>,
    ) {
        // Ensure we have enough cached buffers for this panel's lines on
        // top of whatever the previous panel in this frame already used.
        let first_idx = self.positions.len();
        self.ensure_capacity(ts, first_idx + text_lines.len(), font_px);

        let mut longest = 0.0f32;
        for (i, text) in text_lines.iter().enumerate() {
            let line = &mut self.lines[first_idx + i];
            line.set(ts, text, font_px);
            longest = longest.max(line.width());
        }

        let layout = compute_layout(
            this_monitor.bounds,
            this_monitor.dpi_scale.max(0.1),
            longest,
            text_lines.len(),
            anchor,
        );

        emit_background_rect(&layout, this_monitor, rects);

        // Position each line, converting panel VD coords to window-local
        // physical pixels for glyphon. Lines stack top-down inside the
        // padding.
        let mon_left = this_monitor.bounds.left() as f32;
        let mon_top = this_monitor.bounds.top() as f32;
        let local_panel_left = layout.panel_rect.left() as f32 - mon_left;
        let local_panel_top = layout.panel_rect.top() as f32 - mon_top;
        let x = local_panel_left + layout.padding_px;
        let mut y = local_panel_top + layout.padding_px;
        for i in 0..text_lines.len() {
            self.positions.push(PositionedLine {
                idx: first_idx + i,
                x,
                y,
            });
            y += layout.row_height;
        }
    }

    fn ensure_capacity(&mut self, ts: &mut TextStack, n: usize, font_px: f32) {
        while self.lines.len() < n {
            self.lines.push(CachedLine::new(ts, font_px));
        }
    }

    /// Produce `TextArea`s for glyphon. Must be called AFTER `prepare()`
    /// and consumed before the next `&mut self` call.
    pub fn text_areas(&self, viewport_px: (u32, u32)) -> Vec<TextArea<'_>> {
        let (vw, vh) = (viewport_px.0 as i32, viewport_px.1 as i32);
        self.positions
            .iter()
            .map(|p| TextArea {
                buffer: &self.lines[p.idx].buffer,
                left: p.x,
                top: p.y,
                scale: 1.0,
                bounds: TextBounds {
                    left: 0,
                    top: 0,
                    right: vw,
                    bottom: vh,
                },
                default_color: Color::rgba(0xFF, 0xFF, 0xFF, 0xFF),
                custom_glyphs: &[],
            })
            .collect()
    }
}

/// Emit the dark-overlay background rect for a debug panel at the
/// final window-local coordinates.
fn emit_background_rect(layout: &DebugPanelLayout, this_monitor: &UiMonitor, rects: &mut Vec<RectInstance>) {
    let mon_left = this_monitor.bounds.left() as f32;
    let mon_top = this_monitor.bounds.top() as f32;
    let l = layout.panel_rect.left() as f32 - mon_left;
    let t = layout.panel_rect.top() as f32 - mon_top;
    let r = l + layout.panel_rect.width() as f32;
    let b = t + layout.panel_rect.height() as f32;
    rects.push(RectInstance::filled(l, t, r, b, [0.0, 0.0, 0.0, BODY_ALPHA]));
}
