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
    /// Retained so `drop` can wake a worker parked between cycles
    /// (blocking on `input_rx.recv()`), where `RenderMsg::Shutdown` is
    /// never seen and channel disconnection can be held off indefinitely
    /// by a screenshot/blur job's sender clones.
    input_tx: mpsc::Sender<WorkerInput>,
    thread: Option<JoinHandle<()>>,
    shown: Cell<bool>,
    #[cfg(target_os = "macos")]
    render_subview: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
    /// macOS: layer-backed view behind the render view whose contents are
    /// set to the frozen-desktop screenshot per capture cycle (see
    /// [`set_background_image`](Self::set_background_image)).
    #[cfg(target_os = "macos")]
    background_subview: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
}

impl WindowHandle {
    pub fn new(window: Arc<Window>, setup: WorkerSetup, instance: &wgpu::Instance) -> Result<Self> {
        apply_capture_window_tweaks(&window);

        // Per-window winit cursor hide only; the *hardware* cursor hide is
        // global (CGDisplay on macOS) and happens at capture-cycle start,
        // not window creation.
        #[cfg(not(windows))]
        window.set_cursor_visible(false);

        #[cfg(target_os = "macos")]
        let (surface, render_subview, background_subview) = create_surface(instance, window.clone())?;
        #[cfg(not(target_os = "macos"))]
        let surface = create_surface(instance, window.clone())?;

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
            input_tx: setup.input_tx,
            thread: Some(setup.thread),
            shown: Cell::new(false),
            #[cfg(target_os = "macos")]
            render_subview,
            #[cfg(target_os = "macos")]
            background_subview,
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
            // orderFront happens per show, not at window creation, so a
            // process warming up never has windows on screen.
            #[cfg(target_os = "macos")]
            show_window_without_focus(&self.window);
            #[cfg(target_os = "macos")]
            if let Some(ref subview) = self.render_subview {
                if let Some(layer) = subview.layer() {
                    use objc2_foundation::{NSNumber, NSString};
                    use objc2_quartz_core::{kCAMediaTimingFunctionEaseOut, CABasicAnimation, CAMediaTiming, CAMediaTimingFunction};

                    let key_path = NSString::from_str("opacity");
                    let anim = CABasicAnimation::animationWithKeyPath(Some(&key_path));

                    let from_val = NSNumber::new_f32(0.0);
                    let to_val = NSNumber::new_f32(1.0);
                    unsafe {
                        anim.setFromValue(Some(&from_val));
                        anim.setToValue(Some(&to_val));
                    }
                    anim.setDuration(0.3);

                    let timing_fn = unsafe { CAMediaTimingFunction::functionWithName(kCAMediaTimingFunctionEaseOut) };
                    anim.setTimingFunction(Some(&timing_fn));

                    layer.setOpacity(1.0);
                    let anim_key = NSString::from_str("fadeIn");
                    layer.addAnimation_forKey(&anim, Some(&anim_key));
                }
            }
            #[cfg(not(target_os = "macos"))]
            self.window.set_visible(true);
            #[cfg(windows)]
            raise_window_to_top(&self.window);

            self.shown.set(true);
        } else {
            self.window.set_visible(true);
        }
    }

    pub fn hide(&self) {
        // macOS: winit's set_visible(false) orders the window out.
        self.window.set_visible(false);
    }

    /// Re-arm the first-show path so the next `show()` replays the raise /
    /// fade-in branch. Called at capture-cycle start; on macOS also resets
    /// the render view to transparent so the fade-in starts from black.
    pub fn reset_shown(&self) {
        self.shown.set(false);
        #[cfg(target_os = "macos")]
        if let Some(ref subview) = self.render_subview {
            if let Some(layer) = subview.layer() {
                layer.setOpacity(0.0);
            }
        }
    }

    /// Re-pin the window to its monitor's exact physical bounds. A warm
    /// (hidden) window can drift: Windows may defer a hidden window's
    /// WM_DPICHANGED until it is shown, so a display-scale change while
    /// parked would otherwise surface as a mis-sized overlay. Skips the
    /// two SetWindowPos calls when the geometry already matches — the
    /// common case for every capture after the first.
    #[cfg(windows)]
    pub fn reassert_geometry(&self) {
        let b = self.monitor_bounds;
        let want_pos = winit::dpi::PhysicalPosition::new(b.origin.x, b.origin.y);
        let want_size = winit::dpi::PhysicalSize::new(b.width().max(1) as u32, b.height().max(1) as u32);
        let pos_matches = self
            .window
            .outer_position()
            .is_ok_and(|p| p.x == want_pos.x && p.y == want_pos.y);
        if pos_matches && self.window.inner_size() == want_size {
            return;
        }
        self.window.set_outer_position(want_pos);
        let _ = self.window.request_inner_size(want_size);
    }

    #[cfg(not(windows))]
    pub fn reassert_geometry(&self) {}

    /// Set (or clear) the frozen-desktop image on the CALayer behind the
    /// render view. Installed per capture cycle rather than baked in at
    /// window creation so an idle (hidden) window holds no screenshot.
    /// No-op on other platforms — the wgpu surface is opaque there.
    #[cfg(target_os = "macos")]
    pub fn set_background_image(&self, screenshot: Option<&CapturedDesktop>) {
        let Some(ref bg_view) = self.background_subview else {
            return;
        };
        let Some(layer) = bg_view.layer() else {
            return;
        };
        match screenshot.and_then(|s| crop_screenshot_to_cgimage(s, self.monitor_bounds)) {
            Some(cg_image) => unsafe {
                let cg_ptr: *const std::ffi::c_void = *(&cg_image as *const _ as *const *const std::ffi::c_void);
                layer.setContents(Some(&*(cg_ptr as *const objc2::runtime::AnyObject)));
                layer.setContentsGravity(objc2_quartz_core::kCAGravityResize);
            },
            None => unsafe {
                layer.setContents(None);
            },
        }
    }

    #[cfg(not(target_os = "macos"))]
    pub fn set_background_image(&self, _screenshot: Option<&CapturedDesktop>) {}

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
        #[cfg(windows)]
        {
            let fg = unsafe { windows::Win32::UI::WindowsAndMessaging::GetForegroundWindow() };
            if win32_hwnd(&self.window) == Some(fg) {
                info!("overlay window took foreground focus");
            } else {
                warn!("overlay window was denied foreground focus; keyboard input may go to the previous app until the overlay is clicked");
            }
        }
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
        // Two wakeups, one per channel the worker may be blocked on:
        // RenderMsg::Shutdown reaches a worker inside a cycle's render
        // loop; WorkerInput::Shutdown reaches one parked between cycles.
        let _ = self.tx.send(RenderMsg::Shutdown);
        let _ = self.input_tx.send(WorkerInput::Shutdown);
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
fn win32_hwnd(window: &Window) -> Option<windows::Win32::Foundation::HWND> {
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};

    let Ok(handle) = window.window_handle() else {
        return None;
    };
    let RawWindowHandle::Win32(h) = handle.as_raw() else {
        return None;
    };
    Some(windows::Win32::Foundation::HWND(isize::from(h.hwnd) as *mut _))
}

/// Raise the window to the top of its z-band without activating it.
/// SW_SHOWNOACTIVATE never raises, so on secondary monitors the overlay could
/// otherwise stay below the current foreground window (e.g. a fullscreen app).
/// Z-order raises are not subject to the foreground lock — only activation is.
/// Release builds are additionally WS_EX_TOPMOST, which makes this belt-and-braces
/// there, but it is the only raise debug builds get.
#[cfg(windows)]
fn raise_window_to_top(window: &Window) {
    use windows::Win32::UI::WindowsAndMessaging::{SetWindowPos, HWND_TOP, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE};

    let Some(hwnd) = win32_hwnd(window) else {
        return;
    };
    unsafe {
        let _ = SetWindowPos(hwnd, Some(HWND_TOP), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}

#[cfg(windows)]
fn apply_capture_window_tweaks(window: &Window) {
    use windows::Win32::Graphics::Dwm::{DwmSetWindowAttribute, DWMWA_EXCLUDED_FROM_PEEK, DWMWA_TRANSITIONS_FORCEDISABLED};

    let Some(hwnd) = win32_hwnd(window) else {
        return;
    };
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

/// Show/hide the OS hardware cursor. Guarded by a static so repeated calls
/// are idempotent — hidden at capture-cycle start (`App::start_cycle`),
/// restored at cycle end (`App::finish_cycle`) and around dialogs.
#[cfg(windows)]
pub(crate) fn set_hardware_cursor_visible(visible: bool) {
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

/// See the Windows variant: idempotent global cursor show/hide, invoked per
/// capture cycle (never at window creation — `CGDisplay::hide_cursor` is
/// global and must not blank the user's cursor while warming up).
#[cfg(target_os = "macos")]
pub(crate) fn set_hardware_cursor_visible(visible: bool) {
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
type MacSurfaceViews = (
    wgpu::Surface<'static>,
    Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
    Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
);

#[cfg(target_os = "macos")]
fn create_surface(instance: &wgpu::Instance, window: Arc<Window>) -> Result<MacSurfaceViews> {
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

    // Empty layer at creation; the frozen-desktop contents are installed
    // per capture cycle via `WindowHandle::set_background_image`.
    let bg_view = NSView::initWithFrame(NSView::alloc(mtm), frame);
    bg_view.setAutoresizingMask(NSAutoresizingMaskOptions::ViewWidthSizable | NSAutoresizingMaskOptions::ViewHeightSizable);
    bg_view.setWantsLayer(true);
    content_view.addSubview(&bg_view);

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

    Ok((surface, Some(subview), Some(bg_view)))
}

#[cfg(not(target_os = "macos"))]
fn create_surface(instance: &wgpu::Instance, window: Arc<Window>) -> Result<wgpu::Surface<'static>> {
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
