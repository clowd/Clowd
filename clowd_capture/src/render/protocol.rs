use std::sync::atomic::AtomicUsize;
use std::sync::Arc;

use winit::window::Window;

use crate::sync::VisibleLatch;
use crate::system::{CapturedDesktop, WindowPeekImage};
use crate::ui::shared::UiSharedState;
use clowd_rust_core::geometry::{ScreenPointF, ScreenRect};

/// Messages the main thread sends to a render thread during the frame loop.
pub enum RenderMsg {
    MouseState {
        pos: ScreenPointF,
        zoom: f32,
        selection: Option<ScreenRect>,
        /// Corner radius of `selection` in physical (virtual-desktop) px,
        /// 0 = square — see `InteractionState::selection_radius`.
        selection_radius: f32,
        /// A mouse button is down — the selection is being dragged out,
        /// moved or resized and its geometry changes every frame.
        selection_dragging: bool,
        captured: bool,
    },
    UiState(Arc<UiSharedState>),
    BlurredDesktop(Arc<BlurredDesktopImage>),
    PeekImage(Arc<WindowPeekImage>),
    ShowPeek(Option<PeekCommand>),
    Shutdown,
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

/// Everything a render worker needs to run the capture. Built by the
/// screenshot job, once the desktop bitmap is in, and broadcast to every
/// worker.
pub struct CycleParams {
    pub snapshot: Arc<CapturedDesktop>,
    pub accent_color: [f32; 4],
    pub initial_mouse: ScreenPointF,
    pub ready_count: Arc<AtomicUsize>,
    pub visible_latch: Arc<VisibleLatch>,
}

/// Messages sent to workers on the bootstrap channel. `Handoff` carries the
/// window + surface; `BeginCycle` starts the capture.
pub enum WorkerInput {
    Handoff(WindowHandoff),
    BeginCycle(Arc<CycleParams>),
    /// Sent by `WindowHandle::drop` so a worker still waiting on `BeginCycle`
    /// after its handoff wakes for teardown — channel disconnection alone
    /// cannot be relied on while a screenshot/blur job holds `input_tx`
    /// clones.
    Shutdown,
}

/// Window + surface pair created on the main thread and delivered to a
/// render worker via the bootstrap channel.
pub struct WindowHandoff {
    pub window: Arc<Window>,
    pub surface: wgpu::Surface<'static>,
}
