use std::sync::Arc;

use anyhow::Result;
use winit::window::Window;

pub struct SurfaceBundle {
    pub surface: wgpu::Surface<'static>,
    #[cfg(target_os = "macos")]
    pub render_subview: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
}

#[cfg(target_os = "macos")]
pub fn create_surface(
    instance: &wgpu::Instance,
    window: Arc<Window>,
    screenshot_image: Option<core_graphics::image::CGImage>,
) -> Result<SurfaceBundle> {
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

    Ok(SurfaceBundle {
        surface,
        render_subview: Some(subview),
    })
}

#[cfg(not(target_os = "macos"))]
pub fn create_surface(instance: &wgpu::Instance, window: Arc<Window>, _screenshot_image: Option<()>) -> Result<SurfaceBundle> {
    Ok(SurfaceBundle {
        surface: instance.create_surface(window)?,
    })
}
