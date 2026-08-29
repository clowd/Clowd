//! GPU-side frame timing — deliberately stubbed on the D3D11 backend
//! (v1 of the Phase D plan defers real `D3D11_QUERY_TIMESTAMP` /
//! `TIMESTAMP_DISJOINT` ring plumbing).
//!
//! [`GpuTimings::new`] always returns `None`, which is a state every
//! caller already handles: the wgpu backend returns `None` whenever
//! timing is switched off or the device lacks `TIMESTAMP_QUERY`, and the
//! debug panel renders `n/a`. The type still exists with the exact
//! signatures of the wgpu backend's so `Option<&GpuTimings>` /
//! `Option<GpuTimings>` plumbing compiles identically against both.

use std::convert::Infallible;
use std::time::Duration;

use super::device::{Device, Queue};

pub struct GpuTimings {
    /// Uninhabited: `new` never constructs one, so `poll_completed` is
    /// statically unreachable rather than merely asserted so.
    never: Infallible,
}

impl GpuTimings {
    /// Always `None` — GPU timestamp queries are not implemented on the
    /// D3D11 backend yet. Callers treat `None` as "no GPU timing
    /// available" (identical to a device without `TIMESTAMP_QUERY` on the
    /// wgpu backend).
    pub fn new(device: &Device, queue: &Queue) -> Option<Self> {
        let _ = (device, queue);
        if crate::gxi::gpu_timing_enabled() {
            warn!("--gpu-timing requested but the d3d11 backend does not implement GPU timestamps yet; GPU column will read n/a");
        }
        None
    }

    /// Unreachable ([`GpuTimings`] is uninhabited on this backend).
    pub fn poll_completed(&mut self, device: &Device) -> Vec<Duration> {
        let _ = device;
        match self.never {}
    }
}
