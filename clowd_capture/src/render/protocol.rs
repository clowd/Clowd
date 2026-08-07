use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::Arc;

use winit::window::Window;

use crate::geometry::{ScreenPointF, ScreenRect};
use crate::sync::VisibleLatch;
use crate::system::{CapturedDesktop, WindowPeekImage};
use crate::telemetry::startup::CaptureTimings;
use crate::ui::shared::UiSharedState;

/// Messages the main thread sends to a render thread during the frame loop.
pub enum RenderMsg {
    MouseState {
        pos: ScreenPointF,
        zoom: f32,
        selection: Option<ScreenRect>,
        captured: bool,
    },
    UiState(Arc<UiSharedState>),
    /// Tagged with the producing cycle's generation: the blur job keeps
    /// running (and sending) after its cycle ends, so a worker must be able
    /// to discard late output that would otherwise leak into the next cycle.
    BlurredDesktop {
        cycle_gen: u64,
        image: Arc<BlurredDesktopImage>,
    },
    /// Tagged like `BlurredDesktop` — the walker's peek captures can land
    /// hundreds of ms after the user has already ended the cycle, and
    /// `window_index` is only meaningful within its own cycle's snapshot.
    PeekImage {
        cycle_gen: u64,
        image: Arc<WindowPeekImage>,
    },
    ShowPeek(Option<PeekCommand>),
    /// The capture cycle is over: drop the snapshot/blur/peek textures and
    /// park on the input channel until the next `WorkerInput::BeginCycle`.
    /// Tagged so the EndCycle of a cycle whose `BeginCycle` a worker never
    /// consumed (see [`CycleParams::cancelled`]) cannot terminate a later
    /// cycle.
    EndCycle {
        cycle_gen: u64,
    },
    Shutdown,
}

/// Monotonic capture-cycle generation. Every cycle gets a fresh value at
/// arm time; per-cycle [`RenderMsg`]s carry it so workers can discard
/// messages produced by a cycle other than the one they are rendering.
pub fn next_cycle_gen() -> u64 {
    static NEXT: AtomicU64 = AtomicU64::new(1);
    NEXT.fetch_add(1, Ordering::Relaxed)
}

pub struct BlurredDesktopImage {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
}

/// Tells render workers which obstructed window to peek at this frame.
#[derive(Debug, Clone, PartialEq)]
pub struct PeekCommand {
    pub window_index: usize,
    pub window_rect: ScreenRect,
    pub captured: bool,
}

/// Everything a render worker needs to run one capture cycle. Built once
/// per cycle (by the screenshot job, once the desktop bitmap is in) and
/// broadcast to every worker. The `ready_count`/`visible_latch` pair is
/// fresh per cycle so no latch ever needs re-arming.
pub struct CycleParams {
    pub snapshot: Arc<CapturedDesktop>,
    pub accent_color: [f32; 4],
    pub initial_mouse: ScreenPointF,
    pub ready_count: Arc<AtomicUsize>,
    pub visible_latch: Arc<VisibleLatch>,
    /// This cycle's generation ([`next_cycle_gen`]); workers compare it
    /// against the tag on incoming [`RenderMsg`]s.
    pub cycle_gen: u64,
    /// Set by `App::finish_cycle` when the cycle ends. A `BeginCycle` can be
    /// delivered *after* its cycle was already finished (cancel / screenshot
    /// timeout before the capture landed) — the worker discards it instead
    /// of wedging on the dead cycle's `visible_latch`. Also re-checked after
    /// `visible_latch.wait()`: `finish_cycle` signals the latch after
    /// setting this, releasing workers already blocked on it.
    pub cancelled: Arc<AtomicBool>,
    /// This cycle's debug timings (fresh per cycle, anchored at the `show`
    /// command); workers record their upload / first-render offsets here.
    pub timings: Arc<CaptureTimings>,
}

/// Messages sent to workers on the bootstrap/cycle channel. `Handoff`
/// arrives exactly once (window + surface); `BeginCycle` starts each
/// capture cycle and is what a parked worker blocks on.
pub enum WorkerInput {
    Handoff(WindowHandoff),
    BeginCycle(Arc<CycleParams>),
    /// Sent by `WindowHandle::drop` so a worker parked between cycles wakes
    /// for teardown — channel disconnection alone cannot be relied on while
    /// a screenshot/blur job still holds `input_tx` clones.
    Shutdown,
}

/// Window + surface pair created on the main thread and delivered to a
/// render worker via the bootstrap channel.
pub struct WindowHandoff {
    pub window: Arc<Window>,
    pub surface: wgpu::Surface<'static>,
}
