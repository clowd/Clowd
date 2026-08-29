//! Presentable surface: HWND capture (main thread), swapchain
//! configuration, and per-frame acquire.
//!
//! Swapchain policy (matching the plan's compat targets): flip-discard,
//! 2 buffers, BGRA8, opaque alpha, `FRAME_LATENCY_WAITABLE_OBJECT` with a
//! maximum latency of 1 — the lowest-latency configuration the flip model
//! offers, and the waitable wait doubles as the vsync pacing the render
//! loop relies on (the wgpu path got the same via
//! `Dx12UseFrameLatencyWaitableObject::Wait`).

use std::sync::Arc;
use std::time::{Duration, Instant};

use anyhow::Result;
use windows::core::Interface;
use windows::Win32::Foundation::{CloseHandle, HANDLE, HWND, WAIT_OBJECT_0, WAIT_TIMEOUT};
use windows::Win32::Graphics::Direct3D::D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST;
use windows::Win32::Graphics::Direct3D11::{ID3D11DepthStencilView, ID3D11RenderTargetView, ID3D11Texture2D, D3D11_VIEWPORT};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_ALPHA_MODE_IGNORE, DXGI_FORMAT_UNKNOWN, DXGI_SAMPLE_DESC};
use windows::Win32::Graphics::Dxgi::{
    IDXGIOutput, IDXGISwapChain1, IDXGISwapChain2, DXGI_MWA_NO_ALT_ENTER, DXGI_SCALING_NONE, DXGI_SWAP_CHAIN_DESC1,
    DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT, DXGI_SWAP_EFFECT_FLIP_DISCARD, DXGI_USAGE_RENDER_TARGET_OUTPUT,
};
use windows::Win32::System::Threading::WaitForSingleObject;
use winit::window::Window;

use crate::gxi::types::{AcquireResult, SurfaceConfig};

use super::device::{Device, Instance, Queue};
use super::frame::Frame;
use super::timing::GpuTimings;
use super::SURFACE_FORMAT;

/// The image handed to `Surface::create` for the macOS backdrop layer;
/// unused (and uninstantiable in practice) elsewhere, kept in the
/// signature so both OSes share it.
pub type BackdropImage = ();

/// What surface creation hands back beside the surface itself; only macOS
/// has anything to return (its layer-backed views). Empty here.
#[derive(Default)]
pub struct SurfaceViews {}

/// A window's presentable surface. Created on the main thread, then moved
/// to its render worker via the existing `WindowHandoff`; `Send` but
/// meaningfully used by one thread at a time.
pub struct Surface {
    /// Keeps the HWND below alive for the surface's whole life (winit
    /// destroys the window when the last `Arc` drops).
    _window: Arc<Window>,
    hwnd: isize,
    /// Set by `configure`; acquire/present need all of it.
    configured: Option<Configured>,
}

// SAFETY: before `configure`, `Surface` is an integer HWND plus an
// `Arc<Window>` (itself `Send + Sync`). After `configure` it additionally
// holds the swapchain, RTV and waitable handle, none of which is
// thread-affine: D3D11/DXGI objects may be used from any thread (only
// *concurrent* immediate-context access needs external synchronization —
// MSDN "Multithreading and Direct3D 11" / DXGI "Multithread
// Considerations" — and every context call here goes through the
// [`Queue`] mutex), and kernel waitable handles are process-global
// tokens usable from any thread. `Sync` is vacuous in practice — the
// type has no `&self` methods — but is claimed for parity with the wgpu
// backend's `Surface`, and is sound because a shared `&Surface` exposes
// no operations at all.
unsafe impl Send for Surface {}
unsafe impl Sync for Surface {}

struct Configured {
    device: Device,
    queue: Queue,
    swapchain: IDXGISwapChain1,
    waitable: Waitable,
    /// `None` between a failed `ResizeBuffers` and the next successful
    /// reconfigure — acquire skips frames rather than draw into nothing.
    rtv: Option<ID3D11RenderTargetView>,
    width: u32,
    height: u32,
    clear: [f32; 4],
}

/// The swapchain's frame-latency waitable, owned (the handle returned by
/// `GetFrameLatencyWaitableObject` must be closed by the caller).
struct Waitable(HANDLE);

impl Drop for Waitable {
    fn drop(&mut self) {
        let _ = unsafe { CloseHandle(self.0) };
    }
}

impl Surface {
    /// Create the surface for `window`. MUST be called on the main thread
    /// (winit hands out window handles freely, but the shared `create`
    /// contract is main-thread — macOS requires it).
    ///
    /// `backdrop` is macOS-only and inert here.
    pub fn create(instance: &Instance, window: Arc<Window>, backdrop: Option<BackdropImage>) -> Result<(Self, SurfaceViews)> {
        let _ = (instance, backdrop);
        use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};
        let handle = window.window_handle()?;
        let RawWindowHandle::Win32(h) = handle.as_raw() else {
            anyhow::bail!("expected Win32 window handle");
        };
        let hwnd = h.hwnd.get();
        Ok((
            Self {
                _window: window,
                hwnd,
                configured: None,
            },
            SurfaceViews::default(),
        ))
    }

    /// Build (or rebuild) the swapchain: BGRA8 non-sRGB, vsync, opaque,
    /// frame latency 1. Stores `device`/`queue` clones so `acquire` can
    /// open frames on its own.
    pub fn configure(&mut self, device: &Device, queue: &Queue, config: &SurfaceConfig) {
        let width = config.width.max(1);
        let height = config.height.max(1);
        let clear = [
            config.clear_color[0] as f32,
            config.clear_color[1] as f32,
            config.clear_color[2] as f32,
            config.clear_color[3] as f32,
        ];

        if let Some(cfg) = self.configured.as_mut() {
            // Reconfigure: drop every reference to the old buffers (the
            // RTV, plus whatever the context still has bound) before
            // ResizeBuffers, which fails on outstanding references.
            cfg.rtv = None;
            {
                let ctx = cfg.queue.lock();
                unsafe {
                    ctx.0
                        .OMSetRenderTargets(Some(&[None]), None::<&ID3D11DepthStencilView>);
                    ctx.0.Flush();
                }
            }
            let resized = unsafe {
                cfg.swapchain.ResizeBuffers(
                    0,
                    width,
                    height,
                    DXGI_FORMAT_UNKNOWN,
                    DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT,
                )
            };
            match resized {
                Ok(()) => {
                    cfg.rtv = create_rtv(&cfg.device, &cfg.swapchain);
                    cfg.width = width;
                    cfg.height = height;
                    cfg.clear = clear;
                }
                // Leave `rtv` as None: acquire returns Skip until a later
                // reconfigure succeeds, and a removed device is caught by
                // acquire's device-removed check.
                Err(e) => error!("d3d11 swapchain resize failed: {e}"),
            }
            return;
        }

        let desc = DXGI_SWAP_CHAIN_DESC1 {
            Width: width,
            Height: height,
            Format: SURFACE_FORMAT,
            Stereo: false.into(),
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: 2,
            Scaling: DXGI_SCALING_NONE,
            SwapEffect: DXGI_SWAP_EFFECT_FLIP_DISCARD,
            AlphaMode: DXGI_ALPHA_MODE_IGNORE,
            Flags: DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT.0 as u32,
        };
        let hwnd = HWND(self.hwnd as *mut core::ffi::c_void);
        // Panic on failure, like the wgpu backend's configure (whose
        // internal validation also panics the worker): a swapchain that
        // cannot be created at all leaves nothing to render to, and the
        // worker's fail path turns the panic into a counted failure.
        let swapchain = unsafe {
            device
                .factory()
                .CreateSwapChainForHwnd(device.raw(), hwnd, &desc, None, None::<&IDXGIOutput>)
        }
        .expect("CreateSwapChainForHwnd");
        // Alt+Enter must never flip a capture overlay to exclusive
        // fullscreen; ignore failure (purely defensive).
        let _ = unsafe {
            device
                .factory()
                .MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER)
        };

        let sc2: IDXGISwapChain2 = swapchain
            .cast()
            .expect("IDXGISwapChain2 (Win 8.1+; required for the waitable object the swapchain was created with)");
        unsafe { sc2.SetMaximumFrameLatency(1) }.expect("SetMaximumFrameLatency(1)");
        let waitable = Waitable(unsafe { sc2.GetFrameLatencyWaitableObject() });

        let rtv = create_rtv(device, &swapchain);
        self.configured = Some(Configured {
            device: device.clone(),
            queue: queue.clone(),
            swapchain,
            waitable,
            rtv,
            width,
            height,
            clear,
        });
    }

    /// Acquire the next swapchain image and open this frame's pass state,
    /// cleared to the configured clear color.
    ///
    /// The waitable wait happens FIRST, immediately before the frame is
    /// built (the flip-model low-latency pattern), and is measured alone —
    /// `Frame::acquire_wait` reports pure wait time so the perf tracker's
    /// bucketing matches the wgpu backend's. The clear / RTV bind /
    /// viewport / topology / rasterizer set that follows is deliberately
    /// redone every frame: it is nearly free, and it makes the frame
    /// independent of whatever state uploads or a previous frame left on
    /// the context.
    pub fn acquire(&mut self, timings: Option<&GpuTimings>) -> AcquireResult {
        // `GpuTimings::new` returns `None` on this backend (stub), so
        // there is never a slot to reserve here.
        let _ = timings;
        let cfg = self
            .configured
            .as_mut()
            .expect("Surface::acquire before configure");

        // Device-removed check BEFORE the wait: a dead device may never
        // signal the waitable again, and mapping that to a 1 s timeout →
        // Skip loop would hide the loss from the render loop forever.
        if let Err(reason) = unsafe { cfg.device.raw().GetDeviceRemovedReason() } {
            error!("d3d11 device removed (reason {reason}); reporting DeviceLost");
            return AcquireResult::DeviceLost;
        }

        let Some(rtv) = cfg.rtv.clone() else {
            // Between a failed resize and the next reconfigure. Checked
            // BEFORE the waitable wait: the latency semaphore is only
            // replenished by presents retiring, so a successful wait
            // followed by a Skip (no Present) would consume its one
            // signal for good and wedge every later acquire on a 1 s
            // timeout — even after a reconfigure restores the RTV.
            //
            // The sleep is the loop's pacing for this path: the render
            // loop has no sleep of its own (its pacing normally comes
            // from the waitable wait below), so an immediate Skip here
            // would hot-spin a core — permanently, if `create_rtv` failed
            // at startup and nothing ever reconfigures.
            std::thread::sleep(Duration::from_millis(10));
            return AcquireResult::Skip;
        };

        let t_wait = Instant::now();
        match unsafe { WaitForSingleObject(cfg.waitable.0, 1000) } {
            WAIT_OBJECT_0 => {}
            WAIT_TIMEOUT => return AcquireResult::Skip,
            other => {
                warn!("d3d11 frame-latency wait returned {other:?}; skipping frame");
                // WAIT_FAILED returns instantly (e.g. an invalid waitable
                // handle) — sleep so a permanently broken wait neither
                // hot-spins the loop nor spams the warn above at loop rate.
                std::thread::sleep(Duration::from_millis(10));
                return AcquireResult::Skip;
            }
        }
        let acquire_wait = t_wait.elapsed();

        {
            let ctx = cfg.queue.lock();
            let viewport = D3D11_VIEWPORT {
                TopLeftX: 0.0,
                TopLeftY: 0.0,
                Width: cfg.width as f32,
                Height: cfg.height as f32,
                MinDepth: 0.0,
                MaxDepth: 1.0,
            };
            unsafe {
                ctx.0.ClearRenderTargetView(&rtv, &cfg.clear);
                ctx.0
                    .OMSetRenderTargets(Some(&[Some(rtv.clone())]), None::<&ID3D11DepthStencilView>);
                ctx.0.RSSetViewports(Some(&[viewport]));
                ctx.0
                    .IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                ctx.0
                    .RSSetState(&cfg.device.states().rasterizer);
            }
        }

        AcquireResult::Frame(Box::new(Frame::new(
            cfg.device.clone(),
            cfg.queue.clone(),
            cfg.swapchain.clone(),
            acquire_wait,
        )))
    }
}

fn create_rtv(device: &Device, swapchain: &IDXGISwapChain1) -> Option<ID3D11RenderTargetView> {
    let backbuffer: ID3D11Texture2D = match unsafe { swapchain.GetBuffer(0) } {
        Ok(t) => t,
        Err(e) => {
            error!("d3d11 swapchain GetBuffer(0) failed: {e}");
            return None;
        }
    };
    let mut rtv: Option<ID3D11RenderTargetView> = None;
    if let Err(e) = unsafe {
        device
            .raw()
            .CreateRenderTargetView(&backbuffer, None, Some(&mut rtv))
    } {
        error!("d3d11 CreateRenderTargetView failed: {e}");
        return None;
    }
    rtv
}
