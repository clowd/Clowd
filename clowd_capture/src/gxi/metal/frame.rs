//! An in-flight frame: the render command encoder `Surface::acquire`
//! opened (drawable cleared, viewport/cull/winding set), plus everything
//! `present` needs.
//!
//! Unlike the d3d11 backend there is no context mutex to juggle: every
//! draw goes through this frame's own `MTLRenderCommandEncoder`, which is
//! single-threaded by construction (one render worker per window), and
//! the queue's upload methods touch shared CPU memory, not the encoder.
//! `present(self)` ends the pass, schedules the drawable, commits, and
//! stores the command buffer into the queue's write fence.

use std::ops::Range;
use std::time::{Duration, Instant};

use objc2::rc::Retained;
use objc2::runtime::ProtocolObject;
use objc2_metal::{
    MTLCommandBuffer as MTLCommandBufferProto, MTLCommandEncoder as _, MTLDrawable as MTLDrawableProto, MTLPrimitiveType,
    MTLRenderCommandEncoder as MTLRenderCommandEncoderProto,
};
use objc2_quartz_core::CAMetalDrawable as CAMetalDrawableProto;

use super::device::{BindGroup, Buffer, Queue};
use super::pipeline::RenderPipeline;
use super::timing::GpuTimings;
use super::VERTEX_BUFFER_INDEX;

pub struct Frame {
    /// `present` stores the committed command buffer into this queue's
    /// write fence.
    queue: Queue,
    cmd: Retained<ProtocolObject<dyn MTLCommandBufferProto>>,
    encoder: Retained<ProtocolObject<dyn MTLRenderCommandEncoderProto>>,
    drawable: Retained<ProtocolObject<dyn CAMetalDrawableProto>>,
    /// Time `Surface::acquire` spent blocked in `nextDrawable`, exposed
    /// via [`Frame::acquire_wait`].
    acquire_wait: Duration,
    /// Whether `present` ran; the drop guard below keys off it.
    presented: bool,
}

/// Drop guard for the abandoned-frame path: a panic anywhere between
/// `Surface::acquire` and `present` (the ui `prepare` calls sit in that
/// window) unwinds through this drop. Releasing a command buffer whose
/// render encoder was never ended trips a Metal assertion and aborts the
/// process, which would defeat the crate's panic containment (the
/// ReadyGuard/failed_count machinery the other backends unwind into), so
/// end the encoding here before the fields drop.
impl Drop for Frame {
    fn drop(&mut self) {
        if !self.presented {
            self.encoder.endEncoding();
        }
    }
}

impl Frame {
    pub(super) fn new(
        queue: Queue,
        cmd: Retained<ProtocolObject<dyn MTLCommandBufferProto>>,
        encoder: Retained<ProtocolObject<dyn MTLRenderCommandEncoderProto>>,
        drawable: Retained<ProtocolObject<dyn CAMetalDrawableProto>>,
        acquire_wait: Duration,
    ) -> Self {
        Self {
            queue,
            cmd,
            encoder,
            drawable,
            acquire_wait,
            presented: false,
        }
    }

    /// How long the `Surface::acquire` that produced this frame spent
    /// blocked waiting for a drawable - the acquire's encoder setup is
    /// excluded, so the perf tracker can bucket it as draw work (the
    /// pre-gxi split).
    pub fn acquire_wait(&self) -> Duration {
        self.acquire_wait
    }

    pub fn set_pipeline(&mut self, pipeline: &RenderPipeline) {
        self.encoder
            .setRenderPipelineState(&pipeline.raw);
    }

    /// Replay the bind group's pre-resolved slot lists onto the encoder
    /// (see `Device::create_bind_group`) - including the vertex-stage
    /// lists: ui_text samples its atlases from the vertex shader too.
    pub fn set_bind_group(&mut self, index: u32, bind_group: &BindGroup) {
        assert_eq!(index, 0, "gxi uses a single bind group");
        // SAFETY (all six loops): slots come from the build-time binding
        // walk (all well under Metal's 31-slot tables), and every bound
        // object is retained by the `BindGroup`, which the caller keeps
        // alive for the frame.
        unsafe {
            for (slot, buf) in &bind_group.vs_buffers {
                self.encoder
                    .setVertexBuffer_offset_atIndex(Some(buf), 0, *slot);
            }
            for (slot, buf) in &bind_group.fs_buffers {
                self.encoder
                    .setFragmentBuffer_offset_atIndex(Some(buf), 0, *slot);
            }
            for (slot, tex) in &bind_group.vs_textures {
                self.encoder
                    .setVertexTexture_atIndex(Some(tex), *slot);
            }
            for (slot, tex) in &bind_group.fs_textures {
                self.encoder
                    .setFragmentTexture_atIndex(Some(tex), *slot);
            }
            for (slot, sam) in &bind_group.vs_samplers {
                self.encoder
                    .setVertexSamplerState_atIndex(Some(sam), *slot);
            }
            for (slot, sam) in &bind_group.fs_samplers {
                self.encoder
                    .setFragmentSamplerState_atIndex(Some(sam), *slot);
            }
        }
    }

    pub fn set_vertex_buffer(&mut self, slot: u32, buffer: &Buffer) {
        // The caller's slot is wgpu's vertex-buffer slot 0; on Metal the
        // buffer lives at the pinned index the vertex descriptor's layout
        // was registered under (see `VERTEX_BUFFER_INDEX`).
        assert_eq!(slot, 0, "gxi uses a single vertex buffer");
        // SAFETY: the pinned index is within Metal's 31-slot buffer
        // table, and the buffer is retained by the caller for the frame.
        unsafe {
            self.encoder
                .setVertexBuffer_offset_atIndex(Some(&buffer.raw), 0, VERTEX_BUFFER_INDEX);
        }
    }

    pub fn draw(&mut self, vertices: Range<u32>, instances: Range<u32>) {
        let vcount = vertices.end - vertices.start;
        let icount = instances.end - instances.start;
        // The baseInstance variant is wgpu's `draw` semantics verbatim:
        // `instances.start` offsets per-instance attribute fetch (no
        // shader reads `@builtin(instance_index)`, where Metal and D3D
        // would differ). Used even when both offsets are zero so every
        // draw goes down one path.
        // SAFETY: counts and offsets come from the callers' own instance
        // bookkeeping, validated against their buffers.
        unsafe {
            self.encoder
                .drawPrimitives_vertexStart_vertexCount_instanceCount_baseInstance(
                    MTLPrimitiveType::Triangle,
                    vertices.start as usize,
                    vcount as usize,
                    icount as usize,
                    instances.start as usize,
                );
        }
    }

    /// End the pass, commit, and hand the frame to the compositor.
    ///
    /// `timings`, when present, registers this command buffer's completed
    /// handler so the frame's GPU duration lands in the queue
    /// `GpuTimings::poll_completed` drains.
    ///
    /// Returns the time spent handing the frame to the compositor
    /// (presentDrawable + commit); the `endEncoding` before it is
    /// encoding work, which the caller's own bracket around this call
    /// absorbs into its draw bucket, matching the other backends.
    pub fn present(mut self, timings: Option<&GpuTimings>) -> Duration {
        self.encoder.endEncoding();
        // Disarm the drop guard: the encoder is ended for good.
        self.presented = true;
        if let Some(gt) = timings {
            // Before commit: Metal rejects handlers added afterwards.
            gt.observe(&self.cmd);
        }
        let drawable: &ProtocolObject<dyn MTLDrawableProto> = ProtocolObject::from_ref(&*self.drawable);
        let t_present = Instant::now();
        self.cmd.presentDrawable(drawable);
        self.cmd.commit();
        let elapsed = t_present.elapsed();
        // Arm the write fence: the next `Queue::write_*` must not touch
        // shared memory this command buffer may still be reading. The
        // clone is a refcount bump (`Drop` types cannot move fields out).
        self.queue.store_submitted(self.cmd.clone());
        elapsed
    }
}
