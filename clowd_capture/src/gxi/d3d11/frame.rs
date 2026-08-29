//! An in-flight frame: the immediate context set up by
//! `Surface::acquire` (RTV bound and cleared, viewport/topology/
//! rasterizer set), plus everything `present` needs.
//!
//! Unlike the wgpu backend there is no command encoder to own — D3D11
//! draws execute directly on the immediate context — so `Frame` is just
//! the per-frame bookkeeping around it. Each method takes the context
//! mutex for its own duration rather than holding it acquire→present:
//! the queue's upload methods (`write_buffer`, called by
//! `UiRenderer::prepare` *between* acquire and present on the same
//! thread) lock the same mutex, and a held lock would self-deadlock
//! there. The lock is uncontended in practice — every context touch
//! happens on the owning render worker's thread — so this costs nothing
//! and keeps the lock-ordering story trivial: one mutex, never nested.

use std::ops::Range;
use std::time::{Duration, Instant};

use windows::Win32::Graphics::Direct3D11::ID3D11Buffer;
use windows::Win32::Graphics::Dxgi::{IDXGISwapChain1, DXGI_PRESENT};

use super::device::{BindGroup, Buffer, Device, Queue};
use super::pipeline::RenderPipeline;
use super::timing::GpuTimings;

pub struct Frame {
    /// For the device-removed log on a failed present (the next
    /// `Surface::acquire` is what actually reports `DeviceLost`).
    device: Device,
    queue: Queue,
    swapchain: IDXGISwapChain1,
    /// Per-instance stride of the current pipeline's vertex layout,
    /// captured by [`Frame::set_pipeline`] — D3D11 wants it at
    /// vertex-buffer bind time, wgpu baked it into the pipeline.
    stride: u32,
    /// Time `Surface::acquire` spent blocked in the swapchain's
    /// frame-latency wait, exposed via [`Frame::acquire_wait`].
    acquire_wait: Duration,
}

impl Frame {
    pub(super) fn new(device: Device, queue: Queue, swapchain: IDXGISwapChain1, acquire_wait: Duration) -> Self {
        Self {
            device,
            queue,
            swapchain,
            stride: 0,
            acquire_wait,
        }
    }

    /// How long the `Surface::acquire` that produced this frame spent
    /// blocked waiting for a swapchain image — the acquire's state setup
    /// is excluded, so the perf tracker can bucket it as draw work (the
    /// pre-gxi split).
    pub fn acquire_wait(&self) -> Duration {
        self.acquire_wait
    }

    pub fn set_pipeline(&mut self, pipeline: &RenderPipeline) {
        self.stride = pipeline.stride;
        let ctx = self.queue.lock();
        unsafe {
            ctx.0
                .IASetInputLayout(pipeline.input_layout.as_ref());
            ctx.0.VSSetShader(&pipeline.vs, None);
            ctx.0.PSSetShader(&pipeline.ps, None);
            ctx.0
                .OMSetBlendState(&pipeline.blend, None, u32::MAX);
        }
    }

    pub fn set_bind_group(&mut self, index: u32, bind_group: &BindGroup) {
        assert_eq!(index, 0, "gxi uses a single bind group");
        let ctx = self.queue.lock();
        unsafe {
            for (slot, buf) in &bind_group.vs_cbufs {
                ctx.0
                    .VSSetConstantBuffers(*slot, Some(&[Some(buf.clone())]));
            }
            for (slot, buf) in &bind_group.ps_cbufs {
                ctx.0
                    .PSSetConstantBuffers(*slot, Some(&[Some(buf.clone())]));
            }
            for (slot, srv) in &bind_group.vs_srvs {
                ctx.0
                    .VSSetShaderResources(*slot, Some(&[Some(srv.clone())]));
            }
            for (slot, srv) in &bind_group.ps_srvs {
                ctx.0
                    .PSSetShaderResources(*slot, Some(&[Some(srv.clone())]));
            }
            for (slot, sam) in &bind_group.vs_samplers {
                ctx.0
                    .VSSetSamplers(*slot, Some(&[Some(sam.clone())]));
            }
            for (slot, sam) in &bind_group.ps_samplers {
                ctx.0
                    .PSSetSamplers(*slot, Some(&[Some(sam.clone())]));
            }
        }
    }

    pub fn set_vertex_buffer(&mut self, slot: u32, buffer: &Buffer) {
        let ctx = self.queue.lock();
        let buffers: [Option<ID3D11Buffer>; 1] = [Some(buffer.raw.clone())];
        let strides = [self.stride];
        let offsets = [0u32];
        unsafe {
            ctx.0
                .IASetVertexBuffers(slot, 1, Some(buffers.as_ptr()), Some(strides.as_ptr()), Some(offsets.as_ptr()));
        }
    }

    pub fn draw(&mut self, vertices: Range<u32>, instances: Range<u32>) {
        let vcount = vertices.end - vertices.start;
        let icount = instances.end - instances.start;
        let ctx = self.queue.lock();
        unsafe {
            if self.stride == 0 && instances.start == 0 && icount == 1 {
                // The fullscreen-triangle / single-quad passes (null input
                // layout, `SV_VertexID`-driven): plain Draw, per the plan.
                ctx.0.Draw(vcount, vertices.start);
            } else {
                // Every pipeline with per-instance attributes goes through
                // DrawInstanced even for a single instance, so instance
                // data is always fetched under the same rules.
                // `StartInstanceLocation` offsets per-instance attribute
                // fetch exactly like wgpu's `first_instance` (no shader
                // reads `@builtin(instance_index)`, where the two APIs
                // would differ).
                ctx.0
                    .DrawInstanced(vcount, icount, vertices.start, instances.start);
            }
        }
    }

    /// Hand the frame to the compositor: `Present(1, 0)` (vsync).
    ///
    /// `timings` is accepted for signature parity; `GpuTimings::new`
    /// returns `None` on this backend, so it is always `None` here.
    ///
    /// Returns the time spent in the present call itself; there is no
    /// separate submit on D3D11 (draws executed eagerly on the immediate
    /// context), so the caller's draw bucket naturally absorbed that work
    /// already. A `DXGI_ERROR_DEVICE_REMOVED/RESET` result is logged here
    /// and *reported* by the next `Surface::acquire`, which returns
    /// [`crate::gxi::AcquireResult::DeviceLost`] — `present` has no error
    /// channel in its signature, by (Phase B) design.
    pub fn present(self, timings: Option<&GpuTimings>) -> Duration {
        let _ = timings;
        let t_present = Instant::now();
        let hr = {
            // Present drives the immediate context (implicit flush), so it
            // is serialized like every other context touch.
            let _ctx = self.queue.lock();
            unsafe { self.swapchain.Present(1, DXGI_PRESENT(0)) }
        };
        if hr.is_err() {
            let reason = unsafe { self.device.raw().GetDeviceRemovedReason() };
            error!("d3d11 Present failed: {hr} (device-removed reason: {reason:?})");
        }
        t_present.elapsed()
    }
}
