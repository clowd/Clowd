//! GPU-side frame timing via `MTLCommandBuffer` GPUStartTime/GPUEndTime
//! (macOS 10.15+). Always on: every render worker constructs one.
//!
//! Per render thread. The backend encodes exactly one command buffer per
//! frame, so the whole-frame GPU duration is simply that buffer's
//! `GPUEndTime - GPUStartTime`. No query objects, no readback ring:
//! `Frame::present` registers a completed handler on the command buffer
//! (see [`GpuTimings::observe`]) which pushes the duration into a shared
//! queue, and [`GpuTimings::poll_completed`] drains it at the start of
//! each frame. Callers feed each duration to
//! `PerfTracker::backfill_next_gpu`.

use std::collections::VecDeque;
use std::ptr::NonNull;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use block2::RcBlock;
use objc2::runtime::ProtocolObject;
use objc2_metal::MTLCommandBuffer;

use super::device::{Device, Queue};

/// Samples held between polls before the oldest is dropped. Polling runs
/// every frame, so this only matters if the render loop stalls while
/// completed handlers keep landing; dropping the oldest keeps the queue
/// bounded and the freshest samples flowing.
const MAX_PENDING: usize = 8;

pub struct GpuTimings {
    /// Durations pushed by completed handlers (which Metal invokes on an
    /// internal completion thread) and drained by `poll_completed` (the
    /// render worker). `Arc` because each handler block captures its own
    /// clone and may outlive a dropped `GpuTimings`.
    completed: Arc<Mutex<VecDeque<Duration>>>,
}

impl GpuTimings {
    /// Never `None` on this backend - no device feature gate, since
    /// command-buffer GPU start/end times exist on every macOS version
    /// this crate supports. The `Option` return is signature parity with
    /// the d3d11 backend, whose query creation can fail.
    pub fn new(device: &Device, queue: &Queue) -> Option<Self> {
        let _ = (device, queue);
        Some(Self {
            completed: Arc::new(Mutex::new(VecDeque::new())),
        })
    }

    /// Drain the samples whose completed handlers have fired since the
    /// last poll, oldest first. Callers feed each duration to
    /// `PerfTracker::backfill_next_gpu`.
    pub fn poll_completed(&mut self, device: &Device) -> Vec<Duration> {
        let _ = device;
        let mut ring = self
            .completed
            .lock()
            .expect("gpu timing sample mutex poisoned");
        ring.drain(..).collect()
    }

    /// Register the completed handler that measures `cmd`'s GPU execution
    /// window. Called by `Frame::present`, and MUST run before `commit`
    /// (Metal rejects handlers added afterwards).
    pub(super) fn observe(&self, cmd: &ProtocolObject<dyn MTLCommandBuffer>) {
        let completed = Arc::clone(&self.completed);
        let block = RcBlock::new(move |cb: NonNull<ProtocolObject<dyn MTLCommandBuffer>>| {
            // SAFETY: Metal invokes the handler with the completed command
            // buffer, valid for the duration of the call.
            let cb = unsafe { cb.as_ref() };
            let start = cb.GPUStartTime();
            let end = cb.GPUEndTime();
            // Zero means the driver never recorded the mark (device loss
            // or an errored buffer); drop the sample rather than emit a
            // nonsense duration.
            if start > 0.0 && end >= start {
                if let Ok(mut ring) = completed.lock() {
                    if ring.len() >= MAX_PENDING {
                        ring.pop_front();
                    }
                    ring.push_back(Duration::from_secs_f64(end - start));
                }
            }
        });
        // SAFETY: the block pointer is valid for the call, and
        // `addCompletedHandler` copies the block, so it outlives the
        // `RcBlock` dropped at return. The closure's captured state (an
        // `Arc<Mutex<..>>`) is safe to touch from Metal's completion
        // thread.
        unsafe { cmd.addCompletedHandler(RcBlock::as_ptr(&block)) };
    }
}
