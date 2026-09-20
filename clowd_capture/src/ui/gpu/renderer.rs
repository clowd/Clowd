//! Top-level per-monitor UI renderer.
//!
//! One instance per render thread. Every frame the caller first invokes
//! [`UiRenderer::prepare`] to upload all per-frame GPU data, then hands the
//! renderer an open `RenderPass` via [`UiRenderer::draw`]. The two-phase
//! split lets the caller fold the UI draw into the same render pass as the
//! desktop triangle, avoiding an MSAA tile store+load on M1 TBDR between a
//! separate desktop and UI pass.
//!
//! There is exactly one thing to draw: the egui frame the app thread
//! tessellated for this monitor. Draw order, animation timing and clipping
//! are all decided there, so this type is little more than the bridge
//! between the broadcast and the painter.

use std::sync::Arc;

use crate::gxi;
use crate::ui::gpu::egui_painter::EguiPainter;
use crate::ui::shared::{UiMonitor, UiSharedState};

pub struct UiRenderer {
    egui: EguiPainter,
    state: Option<Arc<UiSharedState>>,
    this_monitor: UiMonitor,
    /// Index of this monitor in the broadcast's per-monitor egui frames.
    monitor_index: usize,
    /// Set by `prepare()`, consumed by `draw()`. `false` when there's
    /// nothing to render (no state yet), so `draw()` becomes a no-op.
    has_prepared: bool,
}

impl UiRenderer {
    /// Take ownership of the painter the deferred builder compiled.
    ///
    /// There is no constructor that compiles it inline: owning a
    /// `UiRenderer` is the proof that the pipeline it draws with exists,
    /// which is what keeps frame 0 — drawn before this type exists at all
    /// — from being able to reference one.
    pub fn new(egui: EguiPainter, this_monitor: UiMonitor, monitor_index: usize) -> Self {
        Self {
            egui,
            state: None,
            this_monitor,
            monitor_index,
            has_prepared: false,
        }
    }

    pub fn set_state(&mut self, state: Arc<UiSharedState>) {
        self.state = Some(state);
    }

    /// Reset leftovers at `BeginCycle`, before frame 0 is drawn. With no
    /// state, `prepare` stages nothing and frame 0 is the clean initial
    /// overlay.
    pub fn begin_cycle(&mut self) {
        self.state = None;
    }

    /// Stage all per-frame work: the egui vertex, index and texture
    /// uploads. After `prepare` returns the caller may open a render pass
    /// and invoke [`UiRenderer::draw`] to issue the UI draw calls into it.
    /// Split from `draw` so the UI can share the same render pass as the
    /// desktop triangle — on M1 TBDR this avoids an MSAA tile store+load
    /// between passes.
    pub fn prepare(&mut self, device: &gxi::Device, queue: &gxi::Queue, viewport_px: (u32, u32)) {
        self.has_prepared = false;

        let Some(state) = self.state.as_ref() else {
            return;
        };

        // The egui frame for THIS monitor, uploaded here so the vertex,
        // index and texture writes happen while the render pass is still
        // closed. There is no visibility gate: the Q toggle now hides the
        // tray inside the pass, by painting it at opacity 0.
        let frame = state
            .egui
            .get(self.monitor_index)
            .and_then(|f| f.as_ref());
        let b = self.this_monitor.bounds;
        self.egui
            .prepare(device, queue, viewport_px, (b.width() as u32, b.height() as u32), frame);

        self.has_prepared = true;
    }

    pub fn draw(&self, frame: &mut gxi::Frame) {
        if !self.has_prepared {
            return;
        }
        self.egui.draw(frame);
    }
}
