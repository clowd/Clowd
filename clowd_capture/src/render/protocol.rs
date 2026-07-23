use std::sync::Arc;

use winit::window::Window;

use crate::geometry::{ScreenPointF, ScreenRect};
use crate::system::{CapturedDesktop, WindowPeekImage};
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

/// Bootstrap messages sent to workers before the render loop starts.
pub enum WorkerInput {
    Screenshot(Arc<CapturedDesktop>),
    Handoff(WindowHandoff),
}

/// Window + surface pair created on the main thread and delivered to a
/// render worker via the bootstrap channel.
pub struct WindowHandoff {
    pub window: Arc<Window>,
    pub surface: wgpu::Surface<'static>,
}
