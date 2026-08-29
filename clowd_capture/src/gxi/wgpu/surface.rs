//! Presentable surface: creation (main thread), swapchain configuration,
//! and per-frame acquire.
//!
//! Absorbed the surface half of `render/window.rs` (including the macOS
//! NSView-subview path) and the swapchain-configuration block of
//! `render.rs`.

use std::sync::Arc;

use anyhow::Result;
use winit::window::Window;

use crate::gxi::types::{AcquireResult, SurfaceConfig};

use super::device::{Device, Instance, Queue};
use super::frame::Frame;
use super::timing::GpuTimings;
use super::SURFACE_FORMAT;

/// The image handed to `Surface::create` for the macOS backdrop layer;
/// unused (and uninstantiable in practice) elsewhere, kept in the
/// signature so both OSes share it.
#[cfg(target_os = "macos")]
pub type BackdropImage = core_graphics::image::CGImage;
#[cfg(not(target_os = "macos"))]
pub type BackdropImage = ();

/// What surface creation hands back beside the surface itself. On macOS:
/// the two layer-backed views `WindowHandle` keeps driving afterwards —
/// the render subview (faded in by `show`) and the backdrop view below it
/// (filled by `install_backdrop` when the screenshot post-dates window
/// creation). Empty on every other platform.
#[cfg(target_os = "macos")]
pub struct SurfaceViews {
    pub render_view: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
    pub backdrop_view: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
}

#[cfg(not(target_os = "macos"))]
#[derive(Default)]
pub struct SurfaceViews {}

/// A window's presentable surface. Created on the main thread (hard
/// requirement on macOS), then moved to its render worker via the existing
/// `WindowHandoff`; `Send` but meaningfully used by one thread at a time.
pub struct Surface {
    surface: wgpu::Surface<'static>,
    /// Set by `configure`; acquire/present need all of it.
    configured: Option<Configured>,
}

struct Configured {
    device: Device,
    queue: Queue,
    config: wgpu::SurfaceConfiguration,
    clear: wgpu::Color,
}

impl Surface {
    /// Create the surface for `window`. MUST be called on the main thread.
    ///
    /// `backdrop`: macOS only — the cropped screenshot for the backdrop
    /// layer, when it is already available at window-creation time (pass
    /// `None` otherwise and fill the returned backdrop view later).
    #[cfg(not(target_os = "macos"))]
    pub fn create(instance: &Instance, window: Arc<Window>, backdrop: Option<BackdropImage>) -> Result<(Self, SurfaceViews)> {
        let _ = backdrop;
        let surface = instance.raw().create_surface(window)?;
        Ok((
            Self {
                surface,
                configured: None,
            },
            SurfaceViews::default(),
        ))
    }

    /// Create the surface for `window`. MUST be called on the main thread
    /// (enforced via `MainThreadMarker`).
    ///
    /// wgpu's surface does not target the winit content view directly: two
    /// subviews are inserted first — backdrop below, render subview above,
    /// with the surface bound to the render subview — so the overlay can
    /// fade the rendered content in over a screenshot backdrop. Subview
    /// order is fixed here even when no image exists yet: a screenshot
    /// that arrives after window creation only has to `setContents` on the
    /// existing backdrop layer. An empty layer is transparent and the
    /// window stays hidden until the backdrop is filled, so nothing black
    /// ever flashes.
    #[cfg(target_os = "macos")]
    pub fn create(instance: &Instance, window: Arc<Window>, backdrop: Option<BackdropImage>) -> Result<(Self, SurfaceViews)> {
        use objc2::{MainThreadMarker, MainThreadOnly};
        use objc2_app_kit::{NSAutoresizingMaskOptions, NSView};
        use std::ptr::NonNull;
        use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};

        let mtm = MainThreadMarker::new().expect("Surface::create must be called on the main thread");

        let handle = window.window_handle()?;
        let RawWindowHandle::AppKit(h) = handle.as_raw() else {
            anyhow::bail!("expected AppKit window handle");
        };

        let content_view: &NSView = unsafe { &*(h.ns_view.as_ptr() as *const NSView) };
        let frame = content_view.frame();

        let bg_view = NSView::initWithFrame(NSView::alloc(mtm), frame);
        bg_view.setAutoresizingMask(NSAutoresizingMaskOptions::ViewWidthSizable | NSAutoresizingMaskOptions::ViewHeightSizable);
        bg_view.setWantsLayer(true);
        if let Some(ref cg_image) = backdrop {
            if let Some(layer) = bg_view.layer() {
                unsafe {
                    let cg_ptr: *const std::ffi::c_void = *(cg_image as *const _ as *const *const std::ffi::c_void);
                    layer.setContents(Some(&*(cg_ptr as *const objc2::runtime::AnyObject)));
                    layer.setContentsGravity(objc2_quartz_core::kCAGravityResize);
                }
            }
        }
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
            instance
                .raw()
                .create_surface_unsafe(wgpu::SurfaceTargetUnsafe::RawHandle {
                    raw_display_handle: Some(raw_display_handle),
                    raw_window_handle,
                })?
        };

        Ok((
            Self {
                surface,
                configured: None,
            },
            SurfaceViews {
                render_view: Some(subview),
                backdrop_view: Some(bg_view),
            },
        ))
    }

    /// Build (or rebuild) the swapchain: BGRA8 non-sRGB, fifo, opaque,
    /// frame latency 1. Asserts the adapter actually presents our fixed
    /// format. Stores `device`/`queue` clones so `acquire` can open frames
    /// and reconfigure on its own.
    pub fn configure(&mut self, device: &Device, queue: &Queue, config: &SurfaceConfig) {
        // Verify surface format.
        let caps = self
            .surface
            .get_capabilities(device.raw_adapter());
        let actual_format = caps
            .formats
            .iter()
            .copied()
            .find(|f| !f.is_srgb())
            .unwrap_or(caps.formats[0]);
        assert_eq!(actual_format, SURFACE_FORMAT, "surface format mismatch");

        let raw_config = wgpu::SurfaceConfiguration {
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            format: SURFACE_FORMAT,
            width: config.width.max(1),
            height: config.height.max(1),
            present_mode: wgpu::PresentMode::Fifo,
            alpha_mode: wgpu::CompositeAlphaMode::Opaque,
            // Auto reproduces wgpu's pre-30 behavior for our non-HDR
            // surface format.
            color_space: wgpu::SurfaceColorSpace::Auto,
            view_formats: vec![],
            desired_maximum_frame_latency: 1,
        };
        self.surface
            .configure(device.raw(), &raw_config);
        self.configured = Some(Configured {
            device: device.clone(),
            queue: queue.clone(),
            config: raw_config,
            clear: wgpu::Color {
                r: config.clear_color[0],
                g: config.clear_color[1],
                b: config.clear_color[2],
                a: config.clear_color[3],
            },
        });
    }

    /// Acquire the next swapchain image and open this frame's render pass,
    /// cleared to the configured clear color. Pass the worker's
    /// `GpuTimings` to bracket the pass with GPU timestamps (the same
    /// reference must then be handed to `Frame::present`).
    ///
    /// On an outdated/lost swapchain the surface reconfigures itself and
    /// returns [`AcquireResult::Reconfigured`] — the caller just skips the
    /// frame.
    pub fn acquire(&mut self, timings: Option<&GpuTimings>) -> AcquireResult {
        let cfg = self
            .configured
            .as_ref()
            .expect("Surface::acquire before configure");
        // Bracket the swapchain acquire alone — this is where fifo blocks
        // on vsync — so `Frame::acquire_wait` reports pure wait time and
        // the encoder/pass setup below stays in the caller's draw bucket
        // (matching the pre-gxi PerfSample split).
        let t_wait = std::time::Instant::now();
        let surface_texture = match self.surface.get_current_texture() {
            wgpu::CurrentSurfaceTexture::Success(f) | wgpu::CurrentSurfaceTexture::Suboptimal(f) => f,
            wgpu::CurrentSurfaceTexture::Timeout => return AcquireResult::Skip,
            wgpu::CurrentSurfaceTexture::Occluded => return AcquireResult::Occluded,
            wgpu::CurrentSurfaceTexture::Outdated | wgpu::CurrentSurfaceTexture::Lost => {
                self.surface
                    .configure(cfg.device.raw(), &cfg.config);
                return AcquireResult::Reconfigured;
            }
            wgpu::CurrentSurfaceTexture::Validation => return AcquireResult::Skip,
        };
        let acquire_wait = t_wait.elapsed();

        let view = surface_texture
            .texture
            .create_view(&wgpu::TextureViewDescriptor::default());
        let mut encoder = cfg
            .device
            .raw()
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("frame encoder"),
            });

        let begin_frame = timings.and_then(|gt| gt.begin_frame());
        let (pass_ts, slot_id) = match &begin_frame {
            Some(bf) => (Some(bf.pass.clone()), Some(bf.id)),
            None => (None, None),
        };

        let rpass = encoder
            .begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("frame pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &view,
                    resolve_target: None,
                    depth_slice: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(cfg.clear),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: None,
                timestamp_writes: pass_ts,
                occlusion_query_set: None,
                multiview_mask: None,
            })
            .forget_lifetime();

        AcquireResult::Frame(Box::new(Frame::new(
            surface_texture,
            encoder,
            rpass,
            cfg.queue.clone(),
            slot_id,
            acquire_wait,
        )))
    }
}
