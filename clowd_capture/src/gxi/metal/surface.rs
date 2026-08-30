//! Presentable surface: NSView/CAMetalLayer setup (main thread), layer
//! configuration, and per-frame acquire.
//!
//! Carries over the wgpu backend's NSView subview code (backdrop below,
//! render subview above) unchanged; only the layer attachment differs -
//! the render subview hosts a `CAMetalLayer` directly (`setLayer` before
//! `setWantsLayer`) instead of letting wgpu-hal splice a sublayer in.
//! The fade-in in `render/window.rs` animates `subview.layer()`, which
//! for a layer-hosting view is this CAMetalLayer itself, so the initial
//! opacity 0.0 lives on the metal layer now.

use std::sync::Arc;
use std::time::Instant;

use anyhow::Result;
use objc2::msg_send;
use objc2::rc::{autoreleasepool, Retained};
use objc2::runtime::AnyObject;
use objc2_foundation::NSSize;
use objc2_metal::{
    MTLClearColor, MTLCommandBuffer as _, MTLCommandQueue as _, MTLCullMode, MTLLoadAction, MTLRenderCommandEncoder as _,
    MTLRenderPassDescriptor, MTLStoreAction, MTLViewport, MTLWinding,
};
use objc2_quartz_core::{CAMetalDrawable as _, CAMetalLayer};
use winit::window::Window;

use crate::gxi::types::{AcquireResult, SurfaceConfig};

use super::device::{Device, Instance, Queue};
use super::frame::Frame;
use super::timing::GpuTimings;
use super::SURFACE_FORMAT;

/// The image handed to `Surface::create` for the macOS backdrop layer:
/// the cropped screenshot, when it is already available at
/// window-creation time.
pub type BackdropImage = core_graphics::image::CGImage;

/// What surface creation hands back beside the surface itself: the two
/// layer-backed views `WindowHandle` keeps driving afterwards - the
/// render subview (faded in by `show`) and the backdrop view below it
/// (filled by `install_backdrop` when the screenshot post-dates window
/// creation).
pub struct SurfaceViews {
    pub render_view: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
    pub backdrop_view: Option<objc2::rc::Retained<objc2_app_kit::NSView>>,
}

/// A window's presentable surface. Created on the main thread (hard
/// requirement - AppKit view manipulation), then moved to its render
/// worker via the existing `WindowHandoff`; `Send` but meaningfully used
/// by one thread at a time.
pub struct Surface {
    /// Keeps the window alive for the surface's whole life (winit
    /// destroys the window when the last `Arc` drops).
    _window: Arc<Window>,
    /// The render subview's hosted layer; `configure` points it at the
    /// device, `acquire` pulls drawables from it.
    layer: Retained<CAMetalLayer>,
    /// The hosting `NSWindow`, retained as an untyped object for the
    /// occlusion guard in `acquire` (its one message send there,
    /// `occlusionState`, needs no typed binding).
    ns_window: Option<Retained<AnyObject>>,
    /// Set by `configure`; acquire needs all of it.
    configured: Option<Configured>,
}

// SAFETY: `Surface` moves to its render worker once (the handoff), and
// every method after `create` runs on that one thread. The AppKit/CA
// objects held here tolerate that: `CAMetalLayer`'s drawable methods and
// property setters are exactly what wgpu-hal's Metal surface drove from
// arbitrary render threads (its layer lived in a plain `Mutex`), and the
// `NSWindow` is only ever sent `occlusionState`, the same off-main-thread
// read wgpu-hal's occlusion workaround performs. Retained refcounting is
// atomic. `Sync` is vacuous in practice - the type has no `&self`
// methods - but is claimed for parity with the other backends' `Surface`,
// and is sound because a shared `&Surface` exposes no operations at all.
unsafe impl Send for Surface {}
unsafe impl Sync for Surface {}

struct Configured {
    queue: Queue,
    width: u32,
    height: u32,
    clear: [f64; 4],
}

impl Surface {
    /// Create the surface for `window`. MUST be called on the main thread
    /// (enforced via `MainThreadMarker`).
    ///
    /// The layer does not target the winit content view directly: two
    /// subviews are inserted first - backdrop below, render subview above,
    /// with the `CAMetalLayer` hosted by the render subview - so the
    /// overlay can fade the rendered content in over a screenshot
    /// backdrop. Subview order is fixed here even when no image exists
    /// yet: a screenshot that arrives after window creation only has to
    /// `setContents` on the existing backdrop layer. An empty layer is
    /// transparent and the window stays hidden until the backdrop is
    /// filled, so nothing black ever flashes.
    ///
    /// `backdrop`: the cropped screenshot for the backdrop layer, when it
    /// is already available at window-creation time (pass `None`
    /// otherwise and fill the returned backdrop view later).
    pub fn create(instance: &Instance, window: Arc<Window>, backdrop: Option<BackdropImage>) -> Result<(Self, SurfaceViews)> {
        use objc2::{MainThreadMarker, MainThreadOnly};
        use objc2_app_kit::{NSAutoresizingMaskOptions, NSView};
        use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};

        let _ = instance;
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
        // Layer-hosting attachment: `setLayer` BEFORE `setWantsLayer`, so
        // AppKit adopts this layer instead of creating a backing one.
        // `contentsScale` keeps CA from compositing the (explicitly sized)
        // drawables through a 1x transform on Retina displays. Initial
        // opacity 0.0 is the wgpu-era contract `WindowHandle::show` relies
        // on: the layer stays invisible until the fade-in animates it to 1.
        let layer = CAMetalLayer::new();
        layer.setContentsScale(window.scale_factor());
        layer.setOpacity(0.0);
        subview.setLayer(Some(&layer));
        subview.setWantsLayer(true);

        // The hosting NSWindow, for `acquire`'s occlusion guard. winit
        // has installed the content view in its window by now, so this is
        // only defensively optional.
        let ns_window: Option<Retained<AnyObject>> = unsafe { msg_send![content_view, window] };
        if ns_window.is_none() {
            warn!("metal surface: content view has no window; occlusion guard disabled");
        }

        Ok((
            Self {
                _window: window,
                layer,
                ns_window,
                configured: None,
            },
            SurfaceViews {
                render_view: Some(subview),
                backdrop_view: Some(bg_view),
            },
        ))
    }

    /// Build (or rebuild) the layer configuration: BGRA8 non-sRGB,
    /// display-synced, opaque, 2 drawables. Stores a `queue` clone so
    /// `acquire` can open frames on its own.
    ///
    /// `maximumDrawableCount` 2 with `displaySyncEnabled` is exactly the
    /// swapchain wgpu-hal computed for the wgpu backend's fifo /
    /// frame-latency-1 configuration, so frame pacing is unchanged.
    /// `allowsNextDrawableTimeout` false because a nil drawable would be
    /// indistinguishable from the transient hiccups `acquire` maps to
    /// `Skip`; the occlusion guard covers the one case where blocking in
    /// `nextDrawable` would actually wedge.
    pub fn configure(&mut self, device: &Device, queue: &Queue, config: &SurfaceConfig) {
        let width = config.width.max(1);
        let height = config.height.max(1);
        self.layer.setDevice(Some(device.raw()));
        self.layer.setPixelFormat(SURFACE_FORMAT);
        self.layer.setFramebufferOnly(true);
        self.layer.setOpaque(true);
        self.layer.setDrawableSize(NSSize {
            width: width as f64,
            height: height as f64,
        });
        self.layer.setMaximumDrawableCount(2);
        self.layer.setDisplaySyncEnabled(true);
        self.layer
            .setAllowsNextDrawableTimeout(false);
        self.configured = Some(Configured {
            queue: queue.clone(),
            width,
            height,
            clear: config.clear_color,
        });
    }

    /// Acquire the next drawable and open this frame's render pass,
    /// cleared to the configured clear color.
    ///
    /// The occlusion guard runs FIRST (reproducing wgpu-hal's workaround
    /// for gfx-rs/wgpu#8309): on an occluded window presented drawables
    /// get stuck waiting for vsync and `nextDrawable` wedges for ~1 s, so
    /// the window's `occlusionState` is checked and [`AcquireResult::
    /// Occluded`] returned while it is not visible. Load-bearing for the
    /// frame-0 show-gate choreography in `app.rs`/`render.rs`, which
    /// retries `Occluded` in a bounded loop around the early order-front.
    pub fn acquire(&mut self, timings: Option<&GpuTimings>) -> AcquireResult {
        // `GpuTimings::new` returns `None` on this backend (stub), so
        // there is never a slot to reserve here.
        let _ = timings;
        let cfg = self
            .configured
            .as_ref()
            .expect("Surface::acquire before configure");

        if let Some(win) = &self.ns_window {
            // NSWindowOcclusionStateVisible; untyped so the binding needs
            // no NSWindow feature.
            const NS_WINDOW_OCCLUSION_STATE_VISIBLE: usize = 1 << 1;
            // SAFETY: `occlusionState` is a plain NSUInteger-returning
            // getter on NSWindow, readable from any thread (the same
            // off-main-thread read wgpu-hal's guard performs).
            let state: usize = unsafe { msg_send![&**win, occlusionState] };
            if state & NS_WINDOW_OCCLUSION_STATE_VISIBLE == 0 {
                return AcquireResult::Occluded;
            }
        }

        // The autoreleasepool spans the whole acquire body, not just
        // `nextDrawable`: render workers are plain threads with no pool
        // of their own, and every Objective-C call below returns
        // autoreleased objects when the ARC return-value handoff misses
        // (wgpu-hal pooled the same call sites). The `Retained` results
        // escape the pool safely; only what the handoff dropped in the
        // pool is drained. `acquire_wait` still brackets the drawable
        // acquire alone - this is where the layer blocks on vsync - so
        // `Frame::acquire_wait` reports pure wait time and the
        // encoder/pass setup below stays in the caller's draw bucket
        // (matching the pre-gxi PerfSample split).
        autoreleasepool(|_| {
            let t_wait = Instant::now();
            let Some(drawable) = self.layer.nextDrawable() else {
                // Transient (display reconfigure, sleep/wake): skip and
                // let the next acquire self-heal.
                return AcquireResult::Skip;
            };
            let acquire_wait = t_wait.elapsed();

            let Some(cmd) = cfg.queue.raw().commandBuffer() else {
                warn!("metal acquire: commandBuffer returned nil; skipping frame");
                return AcquireResult::Skip;
            };

            let desc = MTLRenderPassDescriptor::new();
            // SAFETY (objectAtIndexedSubscript): index 0 always exists -
            // the attachment array is a fixed 8-slot table.
            let attachment = unsafe {
                desc.colorAttachments()
                    .objectAtIndexedSubscript(0)
            };
            attachment.setTexture(Some(&drawable.texture()));
            attachment.setLoadAction(MTLLoadAction::Clear);
            attachment.setClearColor(MTLClearColor {
                red: cfg.clear[0],
                green: cfg.clear[1],
                blue: cfg.clear[2],
                alpha: cfg.clear[3],
            });
            attachment.setStoreAction(MTLStoreAction::Store);

            let Some(encoder) = cmd.renderCommandEncoderWithDescriptor(&desc) else {
                warn!("metal acquire: renderCommandEncoderWithDescriptor returned nil; skipping frame");
                return AcquireResult::Skip;
            };

            // Fixed per-frame encoder state, identical across every
            // pipeline in the crate (cull/winding are encoder state on
            // Metal, not pipeline state - see `pipeline.rs`).
            encoder.setViewport(MTLViewport {
                originX: 0.0,
                originY: 0.0,
                width: cfg.width as f64,
                height: cfg.height as f64,
                znear: 0.0,
                zfar: 1.0,
            });
            encoder.setCullMode(MTLCullMode::None);
            encoder.setFrontFacingWinding(MTLWinding::CounterClockwise);

            AcquireResult::Frame(Box::new(Frame::new(cfg.queue.clone(), cmd, encoder, drawable, acquire_wait)))
        })
    }
}
