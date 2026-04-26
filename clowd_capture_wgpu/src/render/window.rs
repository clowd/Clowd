use std::sync::{mpsc, Arc};
use std::thread::JoinHandle;

use winit::dpi::PhysicalSize;
use winit::window::{CursorIcon, Window};

use crate::capture_output::{save_to_file_with_peek, ActionResult};
use crate::geometry::{ScreenPointF, ScreenRect};
use crate::render::protocol::{PeekCommand, RenderMsg};
use crate::system::{CapturedDesktop, WindowPeekImage};
use crate::ui::shared::UiSharedState;

pub struct WindowHandle {
    window: Arc<Window>,
    monitor_bounds: ScreenRect,
    tx: mpsc::Sender<RenderMsg>,
    thread: Option<JoinHandle<()>>,
    #[cfg(target_os = "macos")]
    render_subview: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
}

impl WindowHandle {
    pub fn new(
        window: Arc<Window>,
        monitor_bounds: ScreenRect,
        tx: mpsc::Sender<RenderMsg>,
        thread: JoinHandle<()>,
        #[cfg(target_os = "macos")] render_subview: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
    ) -> Self {
        Self {
            window,
            monitor_bounds,
            tx,
            thread: Some(thread),
            #[cfg(target_os = "macos")]
            render_subview,
        }
    }

    pub fn monitor_bounds(&self) -> ScreenRect {
        self.monitor_bounds
    }

    pub fn set_visible(&self, visible: bool) {
        self.window.set_visible(visible);
    }

    #[cfg(not(windows))]
    pub fn set_cursor_visible(&self, visible: bool) {
        self.window.set_cursor_visible(visible);
    }

    pub fn set_cursor(&self, cursor: CursorIcon) {
        self.window.set_cursor(cursor);
    }

    pub fn focus(&self) {
        self.window.focus_window();
    }

    pub fn save_to_file_with_peek(&self, selection: ScreenRect, buffer: &CapturedDesktop, peek: Option<&WindowPeekImage>) -> ActionResult {
        save_to_file_with_peek(selection, buffer, peek, &self.window)
    }

    pub fn resize(&self, size: PhysicalSize<u32>) {
        let _ = self.tx.send(RenderMsg::Resize(size));
    }

    pub fn update_mouse_state(&self, pos: ScreenPointF, zoom: f32, selection: Option<ScreenRect>, captured: bool) {
        let _ = self.tx.send(RenderMsg::MouseState {
            pos,
            zoom,
            selection,
            captured,
        });
    }

    pub fn update_ui_state(&self, state: Arc<UiSharedState>) {
        let _ = self.tx.send(RenderMsg::UiState(state));
    }

    pub fn update_peek_state(&self, cmd: Option<PeekCommand>) {
        let _ = self.tx.send(RenderMsg::ShowPeek(cmd));
    }
}

impl Drop for WindowHandle {
    fn drop(&mut self) {
        let _ = self.tx.send(RenderMsg::Shutdown);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

#[cfg(windows)]
pub fn apply_capture_window_tweaks(window: &Window) {
    use windows::Win32::Foundation::HWND;
    use windows::Win32::Graphics::Dwm::{DwmSetWindowAttribute, DWMWA_EXCLUDED_FROM_PEEK, DWMWA_TRANSITIONS_FORCEDISABLED};
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};

    let Ok(handle) = window.window_handle() else {
        return;
    };
    let RawWindowHandle::Win32(h) = handle.as_raw() else {
        return;
    };

    let hwnd = HWND(isize::from(h.hwnd) as *mut _);
    let enable: i32 = 1;
    let ptr = &enable as *const i32 as *const core::ffi::c_void;
    unsafe {
        let _ = DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ptr, 4);
        let _ = DwmSetWindowAttribute(hwnd, DWMWA_EXCLUDED_FROM_PEEK, ptr, 4);
    }
}

#[cfg(target_os = "macos")]
pub fn apply_capture_window_tweaks(window: &Window) {
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};

    let Ok(handle) = window.window_handle() else {
        return;
    };
    let RawWindowHandle::AppKit(h) = handle.as_raw() else {
        return;
    };

    unsafe {
        use objc2_app_kit::{NSColor, NSView, NSWindowAnimationBehavior, NSWindowCollectionBehavior};

        let ns_view: &NSView = &*(h.ns_view.as_ptr() as *const NSView);
        let Some(ns_window) = ns_view.window() else {
            return;
        };

        ns_window.setLevel(25);
        ns_window.setAnimationBehavior(NSWindowAnimationBehavior::None);
        ns_window.setCollectionBehavior(
            NSWindowCollectionBehavior::Stationary
                | NSWindowCollectionBehavior::CanJoinAllSpaces
                | NSWindowCollectionBehavior::FullScreenAuxiliary
                | NSWindowCollectionBehavior::IgnoresCycle,
        );

        let black = NSColor::blackColor();
        ns_window.setBackgroundColor(Some(&black));
        ns_window.setOpaque(true);
    }
}

#[cfg(target_os = "macos")]
pub fn crop_screenshot_to_cgimage(
    screenshot: &crate::system::CapturedDesktop,
    monitor_bounds: crate::geometry::ScreenRect,
) -> Option<core_graphics::image::CGImage> {
    use crate::geometry::RectExt;
    use core_graphics::color_space::CGColorSpace;
    use core_graphics::context::CGContext;

    let vd = screenshot.bounds;
    let crop_x = (monitor_bounds.min_x() - vd.min_x()) as usize;
    let crop_y = (monitor_bounds.min_y() - vd.min_y()) as usize;
    let crop_w = monitor_bounds.width() as usize;
    let crop_h = monitor_bounds.height() as usize;
    if crop_w == 0 || crop_h == 0 {
        return None;
    }
    let vd_stride = screenshot.width as usize * 4;

    let mut crop_buf = vec![0u8; crop_w * crop_h * 4];
    for row in 0..crop_h {
        let src_off = (crop_y + row) * vd_stride + crop_x * 4;
        let dst_off = row * crop_w * 4;
        let end = (src_off + crop_w * 4).min(screenshot.bgra.len());
        let len = end.saturating_sub(src_off).min(crop_w * 4);
        if len > 0 {
            crop_buf[dst_off..dst_off + len].copy_from_slice(&screenshot.bgra[src_off..src_off + len]);
        }
    }

    let color_space = CGColorSpace::create_device_rgb();
    let bitmap_info: u32 = (2 << 12) | 6;
    let ctx = CGContext::create_bitmap_context(
        Some(crop_buf.as_mut_ptr() as *mut _),
        crop_w,
        crop_h,
        8,
        crop_w * 4,
        &color_space,
        bitmap_info,
    );
    ctx.create_image()
}

#[cfg(target_os = "macos")]
pub fn show_windows_atomically<'a>(handles: impl Iterator<Item = &'a WindowHandle>) {
    for h in handles {
        if let Some(ref subview) = h.render_subview {
            if let Some(layer) = subview.layer() {
                layer.setOpacity(1.0);
            }
        }
    }
}

#[cfg(not(target_os = "macos"))]
pub fn show_windows_atomically<'a>(handles: impl Iterator<Item = &'a WindowHandle>) {
    for h in handles {
        h.set_visible(true);
    }
}
