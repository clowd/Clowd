//! GPU-side frame timing - deliberately stubbed on the Metal backend,
//! same as the d3d11 sibling (an optional follow-up could read
//! `MTLCommandBuffer` GPUStartTime/GPUEndTime, ~50 lines).
//!
//! [`GpuTimings::new`] always returns `None`, which is a state every
//! caller already handles: the debug panel renders `n/a`. The type still
//! exists with the exact signatures of the other backends' so
//! `Option<&GpuTimings>` / `Option<GpuTimings>` plumbing compiles
//! identically against both.

use std::convert::Infallible;
use std::time::Duration;

use super::device::{Device, Queue};

pub struct GpuTimings {
    /// Uninhabited: `new` never constructs one, so `poll_completed` is
    /// statically unreachable rather than merely asserted so.
    never: Infallible,
}

impl GpuTimings {
    /// Always `None` - GPU timestamp queries are not implemented on the
    /// Metal backend yet. Callers treat `None` as "no GPU timing
    /// available" (identical to the d3d11 backend's stub).
    pub fn new(device: &Device, queue: &Queue) -> Option<Self> {
        let _ = (device, queue);
        if crate::gxi::gpu_timing_enabled() {
            warn!("--gpu-timing requested but the metal backend does not implement GPU timestamps yet; GPU column will read n/a");
        }
        None
    }

    /// Unreachable ([`GpuTimings`] is uninhabited on this backend).
    pub fn poll_completed(&mut self, device: &Device) -> Vec<Duration> {
        let _ = device;
        match self.never {}
    }
}
