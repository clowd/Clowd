//! Presentable surface: window binding (main thread), swapchain
//! configuration, and per-frame acquire.
//!
//! Swapchain policy (the shared backend contract): BGRA8 non-sRGB, fifo
//! (vsync), opaque alpha, frame latency 1 — the lowest-latency
//! configuration wgpu offers, and the fifo acquire doubles as the vsync
//! pacing the render loop relies on (the d3d11 backend gets the same from
//! its frame-latency waitable).

use std::sync::Arc;
use std::time::{Duration, Instant};

use anyhow::Result;
use winit::window::Window;

use crate::gxi::types::{AcquireResult, SurfaceConfig};

use super::device::{Device, Instance, Queue};
use super::frame::Frame;
use super::timing::GpuTimings;
use super::SURFACE_FORMAT;

/// The image handed to `Surface::create` for the macOS backdrop layer;
/// unused (and uninstantiable in practice) elsewhere, kept in the
/// signature so every OS shares it.
pub type BackdropImage = ();

/// What surface creation hands back beside the surface itself; only macOS
/// has anything to return (its layer-backed views). Empty here.
#[derive(Default)]
pub struct SurfaceViews {}

/// A window's presentable surface. Created on the main thread (the shared
/// `create` contract — macOS requires it), then moved to its render
/// worker via the existing `WindowHandoff`; `Send` but meaningfully used
/// by one thread at a time.
///
/// The `wgpu::Surface` keeps the `Arc<Window>` alive for the surface's
/// whole life (it owns the handle it was created from).
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
    /// Create the surface for `window`. MUST be called on the main thread
    /// (winit hands out window handles freely, but the shared `create`
    /// contract is main-thread — macOS requires it).
    ///
    /// `backdrop` is macOS-only and inert here.
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

    /// Build (or rebuild) the swapchain: BGRA8 non-sRGB, fifo, opaque,
    /// frame latency 1. Stores `device`/`queue` clones so `acquire` can
    /// open frames and reconfigure on its own.
    ///
    /// Panics if the adapter does not present our fixed format: every
    /// pipeline's color target is baked to it, so a swapchain in any other
    /// format could not be drawn into at all, and the worker's fail path
    /// turns the panic into a counted failure (like a failed swapchain
    /// creation on d3d11).
    pub fn configure(&mut self, device: &Device, queue: &Queue, config: &SurfaceConfig) {
        let caps = self
            .surface
            .get_capabilities(device.raw_adapter());
        assert!(
            caps.formats.contains(&SURFACE_FORMAT),
            "surface does not support {SURFACE_FORMAT:?} (offered: {:?})",
            caps.formats
        );
        // Opaque where the driver offers it (X11 Vulkan WSI normally
        // does); otherwise `Auto` takes the first mode the driver lists —
        // on an opaque overlay window every mode composites the same.
        let alpha_mode = if caps
            .alpha_modes
            .contains(&wgpu::CompositeAlphaMode::Opaque)
        {
            wgpu::CompositeAlphaMode::Opaque
        } else {
            info!("surface offers no opaque alpha mode ({:?}); using Auto", caps.alpha_modes);
            wgpu::CompositeAlphaMode::Auto
        };

        let raw_config = wgpu::SurfaceConfiguration {
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            format: SURFACE_FORMAT,
            width: config.width.max(1),
            height: config.height.max(1),
            present_mode: wgpu::PresentMode::Fifo,
            alpha_mode,
            // Auto is the plain (non-HDR) behavior for our surface format.
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
    /// An outdated or lost swapchain is reconfigured here and the frame
    /// skipped; the next acquire runs on the fresh one. A lost *device*
    /// (the callback `Device::create` installs) is reported as
    /// [`AcquireResult::DeviceLost`] before anything is acquired, so the
    /// worker exits via its fail path instead of skipping forever.
    pub fn acquire(&mut self, timings: Option<&GpuTimings>) -> AcquireResult {
        let cfg = self
            .configured
            .as_ref()
            .expect("Surface::acquire before configure");

        if cfg.device.is_lost() {
            error!("wgpu device lost; reporting DeviceLost");
            return AcquireResult::DeviceLost;
        }

        // Bracket the swapchain acquire alone — this is where fifo blocks
        // on vsync — so `Frame::acquire_wait` reports pure wait time and
        // the encoder/pass setup below stays in the caller's draw bucket
        // (matching the pre-gxi PerfSample split).
        let t_wait = Instant::now();
        let surface_texture = match self.surface.get_current_texture() {
            wgpu::CurrentSurfaceTexture::Success(f) | wgpu::CurrentSurfaceTexture::Suboptimal(f) => f,
            // The backend's own acquire timeout elapsed (a second or so):
            // no pacing sleep needed, the wait itself was the pacing.
            wgpu::CurrentSurfaceTexture::Timeout => return AcquireResult::Skip,
            wgpu::CurrentSurfaceTexture::Occluded => return AcquireResult::Occluded,
            wgpu::CurrentSurfaceTexture::Outdated | wgpu::CurrentSurfaceTexture::Lost => {
                info!("wgpu surface outdated/lost; reconfiguring");
                self.surface
                    .configure(cfg.device.raw(), &cfg.config);
                return AcquireResult::Skip;
            }
            // A validation failure returns instantly — sleep so a
            // permanently broken surface neither hot-spins the loop nor
            // spams the log at loop rate (the d3d11 backend paces its
            // degraded paths the same way).
            wgpu::CurrentSurfaceTexture::Validation => {
                warn!("wgpu surface acquire failed validation; skipping frame");
                std::thread::sleep(Duration::from_millis(10));
                return AcquireResult::Skip;
            }
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

        // Reserve a timing slot first so the clear below is inside the
        // measured window; `Frame::present` resolves the bracket.
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
