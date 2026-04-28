use std::cell::Cell;
use std::collections::HashMap;
use std::sync::{mpsc, Arc};
use std::thread::JoinHandle;

use anyhow::Result;
use winit::window::{CursorIcon, Window, WindowId};

use crate::capture_output::{save_to_file_with_peek, ActionResult};
use crate::geometry::{ScreenPointF, ScreenRect};
use crate::render::protocol::{PeekCommand, RenderMsg, WindowHandoff, WorkerInput};
use crate::render::worker::WorkerSetup;
use crate::system::{CapturedCursor, CapturedDesktop, WindowPeekImage};
use crate::ui::shared::UiSharedState;

// ── WindowHandle ───────────────────────────────────────────────────

pub struct WindowHandle {
    window: Arc<Window>,
    monitor_bounds: ScreenRect,
    tx: mpsc::Sender<RenderMsg>,
    thread: Option<JoinHandle<()>>,
    shown: Cell<bool>,
    #[cfg(target_os = "macos")]
    render_subview: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
}

impl WindowHandle {
    pub fn new(
        window: Arc<Window>,
        setup: WorkerSetup,
        instance: &wgpu::Instance,
        #[cfg(target_os = "macos")] screenshot: Option<&CapturedDesktop>,
    ) -> Result<Self> {
        apply_capture_window_tweaks(&window);

        #[cfg(not(windows))]
        window.set_cursor_visible(false);
        set_hardware_cursor_visible(false);

        #[cfg(target_os = "macos")]
        let screenshot_image = screenshot.and_then(|s| crop_screenshot_to_cgimage(s, setup.monitor_bounds));
        #[cfg(not(target_os = "macos"))]
        let screenshot_image: Option<()> = None;

        #[cfg(target_os = "macos")]
        let (surface, render_subview) = create_surface(instance, window.clone(), screenshot_image)?;
        #[cfg(not(target_os = "macos"))]
        let surface = create_surface(instance, window.clone(), screenshot_image)?;

        #[cfg(target_os = "macos")]
        show_window_without_focus(&window);

        let _ = setup
            .input_tx
            .send(WorkerInput::Handoff(WindowHandoff {
                window: window.clone(),
                surface,
            }));

        Ok(Self {
            window,
            monitor_bounds: setup.monitor_bounds,
            tx: setup.render_msg_tx,
            thread: Some(setup.thread),
            shown: Cell::new(false),
            #[cfg(target_os = "macos")]
            render_subview,
        })
    }

    pub fn window_id(&self) -> WindowId {
        self.window.id()
    }

    pub fn monitor_bounds(&self) -> ScreenRect {
        self.monitor_bounds
    }

    pub fn show(&self) {
        if !self.shown.get() {
            #[cfg(target_os = "macos")]
            if let Some(ref subview) = self.render_subview {
                if let Some(layer) = subview.layer() {
                    use objc2_foundation::{NSNumber, NSString};
                    use objc2_quartz_core::{
                        CABasicAnimation, CAMediaTiming, CAMediaTimingFunction,
                        kCAMediaTimingFunctionEaseOut,
                    };

                    let key_path = NSString::from_str("opacity");
                    let anim = CABasicAnimation::animationWithKeyPath(Some(&key_path));

                    let from_val = NSNumber::new_f32(0.0);
                    let to_val = NSNumber::new_f32(1.0);
                    unsafe {
                        anim.setFromValue(Some(&from_val));
                        anim.setToValue(Some(&to_val));
                    }
                    anim.setDuration(0.3);

                    let timing_fn = unsafe {
                        CAMediaTimingFunction::functionWithName(kCAMediaTimingFunctionEaseOut)
                    };
                    anim.setTimingFunction(Some(&timing_fn));

                    layer.setOpacity(1.0);
                    let anim_key = NSString::from_str("fadeIn");
                    layer.addAnimation_forKey(&anim, Some(&anim_key));
                }
            }
            #[cfg(not(target_os = "macos"))]
            self.window.set_visible(true);

            self.shown.set(true);
        } else {
            self.window.set_visible(true);
        }
    }

    pub fn hide(&self) {
        self.window.set_visible(false);
    }

    pub fn show_cursor(&self) {
        #[cfg(not(windows))]
        self.window.set_cursor_visible(true);
        set_hardware_cursor_visible(true);
    }

    pub fn hide_cursor(&self) {
        #[cfg(not(windows))]
        self.window.set_cursor_visible(false);
        set_hardware_cursor_visible(false);
    }

    pub fn set_cursor(&self, cursor: CursorIcon) {
        self.window.set_cursor(cursor);
    }

    pub fn focus(&self) {
        self.window.focus_window();
    }

    pub fn save_to_file_with_peek(
        &self,
        selection: ScreenRect,
        buffer: &CapturedDesktop,
        peek: Option<&WindowPeekImage>,
        cursor: Option<&CapturedCursor>,
        cursor_visible: bool,
    ) -> ActionResult {
        save_to_file_with_peek(selection, buffer, peek, cursor, cursor_visible, &self.window)
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

// ── WindowSet ──────────────────────────────────────────────────────

pub struct WindowSet {
    map: HashMap<WindowId, WindowHandle>,
    order: Vec<WindowId>,
}

impl WindowSet {
    pub fn new() -> Self {
        Self {
            map: HashMap::new(),
            order: Vec::new(),
        }
    }

    pub fn insert(&mut self, handle: WindowHandle) {
        let id = handle.window_id();
        self.order.push(id);
        self.map.insert(id, handle);
    }

    pub fn get(&self, id: &WindowId) -> Option<&WindowHandle> {
        self.map.get(id)
    }

    pub fn is_empty(&self) -> bool {
        self.map.is_empty()
    }

    pub fn values(&self) -> impl Iterator<Item = &WindowHandle> {
        self.map.values()
    }

    pub fn first(&self) -> Option<&WindowHandle> {
        self.order
            .first()
            .and_then(|id| self.map.get(id))
    }

    pub fn show_all(&self) {
        for h in self.map.values() {
            h.show();
        }
    }

    pub fn hide_all(&self) {
        for h in self.map.values() {
            h.hide();
        }
    }

    pub fn show_cursors(&self) {
        for h in self.map.values() {
            h.show_cursor();
        }
    }

    pub fn hide_cursors(&self) {
        for h in self.map.values() {
            h.hide_cursor();
        }
    }
}

// ── Platform: window tweaks (private) ──────────────────────────────

#[cfg(windows)]
fn apply_capture_window_tweaks(window: &Window) {
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
fn apply_capture_window_tweaks(window: &Window) {
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

/// Show a window without activating the app or making it key.
/// The window appears at its configured level (above all other windows)
/// but the previously-focused app retains focus until we explicitly take it.
#[cfg(target_os = "macos")]
fn show_window_without_focus(window: &Window) {
    use objc2_app_kit::NSView;
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};

    let Ok(handle) = window.window_handle() else { return };
    let RawWindowHandle::AppKit(h) = handle.as_raw() else { return };

    unsafe {
        let ns_view: &NSView = &*(h.ns_view.as_ptr() as *const NSView);
        if let Some(ns_window) = ns_view.window() {
            ns_window.orderFrontRegardless();
        }
    }
}

// ── Platform: hardware cursor (private) ────────────────────────────

#[cfg(windows)]
fn set_hardware_cursor_visible(visible: bool) {
    use std::sync::atomic::{AtomicBool, Ordering};
    use windows::Win32::UI::WindowsAndMessaging::ShowCursor;

    static HIDDEN: AtomicBool = AtomicBool::new(false);

    let currently_hidden = HIDDEN.load(Ordering::Relaxed);
    if visible && currently_hidden {
        unsafe { ShowCursor(true) };
        HIDDEN.store(false, Ordering::Relaxed);
    } else if !visible && !currently_hidden {
        unsafe { ShowCursor(false) };
        HIDDEN.store(true, Ordering::Relaxed);
    }
}

#[cfg(target_os = "macos")]
fn set_hardware_cursor_visible(visible: bool) {
    use core_graphics::display::{CGDisplay, CGMainDisplayID};
    use std::sync::atomic::{AtomicBool, Ordering};

    static HIDDEN: AtomicBool = AtomicBool::new(false);

    let currently_hidden = HIDDEN.load(Ordering::Relaxed);
    if visible && currently_hidden {
        unsafe { CGDisplay::new(CGMainDisplayID()).show_cursor() }.ok();
        HIDDEN.store(false, Ordering::Relaxed);
    } else if !visible && !currently_hidden {
        unsafe { CGDisplay::new(CGMainDisplayID()).hide_cursor() }.ok();
        HIDDEN.store(true, Ordering::Relaxed);
    }
}

// ── Platform: surface creation (private) ───────────────────────────

#[cfg(target_os = "macos")]
fn create_surface(
    instance: &wgpu::Instance,
    window: Arc<Window>,
    screenshot_image: Option<core_graphics::image::CGImage>,
) -> Result<(wgpu::Surface<'static>, Option<objc2::rc::Retained<objc2_app_kit::NSView>>)> {
    use objc2::{MainThreadMarker, MainThreadOnly};
    use objc2_app_kit::{NSAutoresizingMaskOptions, NSView};
    use std::ptr::NonNull;
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};

    let mtm = MainThreadMarker::new().expect("create_surface must be called on the main thread");

    let handle = window.window_handle()?;
    let RawWindowHandle::AppKit(h) = handle.as_raw() else {
        anyhow::bail!("expected AppKit window handle");
    };

    let content_view: &NSView = unsafe { &*(h.ns_view.as_ptr() as *const NSView) };
    let frame = content_view.frame();

    if let Some(ref cg_image) = screenshot_image {
        let bg_view = NSView::initWithFrame(NSView::alloc(mtm), frame);
        bg_view.setAutoresizingMask(NSAutoresizingMaskOptions::ViewWidthSizable | NSAutoresizingMaskOptions::ViewHeightSizable);
        bg_view.setWantsLayer(true);
        if let Some(layer) = bg_view.layer() {
            unsafe {
                let cg_ptr: *const std::ffi::c_void = *(&*cg_image as *const _ as *const *const std::ffi::c_void);
                layer.setContents(Some(&*(cg_ptr as *const objc2::runtime::AnyObject)));
                layer.setContentsGravity(objc2_quartz_core::kCAGravityResize);
            }
        }
        content_view.addSubview(&bg_view);
    }

    let subview = NSView::initWithFrame(NSView::alloc(mtm), frame);
    subview.setAutoresizingMask(NSAutoresizingMaskOptions::ViewWidthSizable | NSAutoresizingMaskOptions::ViewHeightSizable);
    content_view.addSubview(&subview);
    subview.setWantsLayer(true);
    if let Some(layer) = subview.layer() {
        layer.setOpacity(0.0);
    }

    let subview_ptr = NonNull::new(objc2::rc::Retained::as_ptr(&subview) as *mut _).expect("subview pointer is non-null");
    let raw_window_handle = RawWindowHandle::AppKit(winit::raw_window_handle::AppKitWindowHandle::new(subview_ptr));
    let raw_display_handle = winit::raw_window_handle::RawDisplayHandle::AppKit(winit::raw_window_handle::AppKitDisplayHandle::new());

    let surface = unsafe {
        instance.create_surface_unsafe(wgpu::SurfaceTargetUnsafe::RawHandle {
            raw_display_handle: Some(raw_display_handle),
            raw_window_handle,
        })?
    };

    Ok((surface, Some(subview)))
}

#[cfg(not(target_os = "macos"))]
fn create_surface(instance: &wgpu::Instance, window: Arc<Window>, _screenshot_image: Option<()>) -> Result<wgpu::Surface<'static>> {
    Ok(instance.create_surface(window)?)
}

// ── Platform: screenshot crop (private) ────────────────────────────

#[cfg(target_os = "macos")]
fn crop_screenshot_to_cgimage(screenshot: &CapturedDesktop, monitor_bounds: ScreenRect) -> Option<core_graphics::image::CGImage> {
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
