//! An in-flight frame: acquired swapchain texture + command encoder + the
//! one render pass every draw of the frame goes into.
//!
//! `Frame` has NO lifetime parameters — `wgpu::RenderPass::forget_lifetime`
//! (wgpu 30) detaches the pass from its encoder borrow so both can be owned
//! side by side. `present(self)` closes the pass, finishes and submits the
//! encoder, and presents, in that order.

use std::ops::Range;
use std::time::{Duration, Instant};

use super::device::{BindGroup, Buffer, Queue};
use super::pipeline::RenderPipeline;
use super::timing::{FrameSlotId, GpuTimings};

pub struct Frame {
    surface_texture: wgpu::SurfaceTexture,
    encoder: wgpu::CommandEncoder,
    rpass: wgpu::RenderPass<'static>,
    queue: Queue,
    /// The GPU-timing ring slot whose timestamps bracket this frame's
    /// pass, when `Surface::acquire` was given a `GpuTimings`.
    timing_slot: Option<FrameSlotId>,
    /// Time `Surface::acquire` spent blocked in the backend's swapchain
    /// acquire (the vsync wait), exposed via [`Frame::acquire_wait`].
    acquire_wait: Duration,
}

impl Frame {
    pub(crate) fn new(
        surface_texture: wgpu::SurfaceTexture,
        encoder: wgpu::CommandEncoder,
        rpass: wgpu::RenderPass<'static>,
        queue: Queue,
        timing_slot: Option<FrameSlotId>,
        acquire_wait: Duration,
    ) -> Self {
        Self {
            surface_texture,
            encoder,
            rpass,
            queue,
            timing_slot,
            acquire_wait,
        }
    }

    /// How long the `Surface::acquire` that produced this frame spent
    /// blocked waiting for a swapchain image — the acquire's encoder and
    /// render-pass setup is excluded, so the perf tracker can bucket it as
    /// draw work (the pre-gxi split).
    pub fn acquire_wait(&self) -> Duration {
        self.acquire_wait
    }

    pub fn set_pipeline(&mut self, pipeline: &RenderPipeline) {
        self.rpass.set_pipeline(&pipeline.raw);
    }

    pub fn set_bind_group(&mut self, index: u32, bind_group: &BindGroup) {
        self.rpass
            .set_bind_group(index, &bind_group.raw, &[]);
    }

    pub fn set_vertex_buffer(&mut self, slot: u32, buffer: &Buffer) {
        self.rpass
            .set_vertex_buffer(slot, buffer.raw.slice(..));
    }

    pub fn draw(&mut self, vertices: Range<u32>, instances: Range<u32>) {
        self.rpass.draw(vertices, instances);
    }

    /// End the pass, submit, and hand the frame to the compositor.
    ///
    /// `timings` must be the same `GpuTimings` (or `None`) that was passed
    /// to the `Surface::acquire` that produced this frame — it appends the
    /// timestamp resolve to the command stream and kicks off the readback
    /// mapping after submit.
    ///
    /// Returns the time spent handing the frame to the compositor (the
    /// backend's present call). The perf tracker buckets that separately
    /// from the submit work preceding it, which the caller measures as
    /// part of its own bracket around this call.
    pub fn present(self, timings: Option<&GpuTimings>) -> Duration {
        let Frame {
            surface_texture,
            mut encoder,
            rpass,
            queue,
            timing_slot,
            acquire_wait: _,
        } = self;
        drop(rpass);
        if let (Some(gt), Some(id)) = (timings, timing_slot) {
            gt.resolve(&mut encoder, id);
        }
        queue
            .raw()
            .submit(std::iter::once(encoder.finish()));
        if let (Some(gt), Some(id)) = (timings, timing_slot) {
            gt.after_submit(id);
        }
        let t_present = Instant::now();
        queue.raw().present(surface_texture);
        t_present.elapsed()
    }
}
