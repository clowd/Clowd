//! `gxi` — the capture overlay's thin GPU abstraction.
//!
//! Concrete structs, zero dynamic dispatch, compile-time backend selection.
//! Both backends expose the *same* public API (identical type names and
//! signatures, enforced by the CI compile matrix), so the rest of the
//! crate is written against `crate::gxi::*` and never names a backend.
//!
//! Backend selection (Phase D): Windows ships the `d3d11` backend; the
//! `wgpu` backend serves macOS and stays compilable on Windows behind the
//! `backend-wgpu` cargo feature — a CI/parity build that keeps the two
//! public surfaces from drifting, never shipped to users. Exactly one
//! backend is compiled into any given binary.

use std::sync::atomic::{AtomicBool, Ordering};

pub mod types;

#[cfg(all(windows, not(feature = "backend-wgpu")))]
mod d3d11;
#[cfg(any(not(windows), feature = "backend-wgpu"))]
mod wgpu;

#[cfg(all(windows, not(feature = "backend-wgpu")))]
pub use self::d3d11::*;
#[cfg(any(not(windows), feature = "backend-wgpu"))]
pub use self::wgpu::*;
pub use types::*;

// ── GPU-timing master switch ────────────────────────────────────────
// Backend-agnostic policy, which is why it lives here and not in a
// backend module: both the device-creation path (whether to request the
// timestamp feature) and `GpuTimings::new` read it.

/// Master switch for GPU frame timing, off by default. When `false`:
///   * `GpuTimings::new` returns `None` (so nothing is constructed — no
///     query set, no resolve/readback buffers, no per-frame resolve or
///     readback mapping).
///   * Device creation skips requesting the timestamp-query feature —
///     some backends instrument the queue differently when the feature
///     is enabled even if unused.
static GPU_TIMING: AtomicBool = AtomicBool::new(false);

/// Must be called before any render worker reaches `Device::create`: the
/// timestamp feature is a *device creation* parameter, so flipping this
/// afterwards leaves `GpuTimings::new` unable to build anything (it
/// checks the device's granted features, not this flag alone). Relaxed
/// ordering is enough — the worker threads are spawned after this runs,
/// and thread spawn is itself the synchronization edge.
// Called from `main::run` with `--gpu-timing`, before the session spawns
// any render worker.
pub fn set_gpu_timing_enabled(enabled: bool) {
    GPU_TIMING.store(enabled, Ordering::Relaxed);
}

/// Reads the master switch.
pub(crate) fn gpu_timing_enabled() -> bool {
    GPU_TIMING.load(Ordering::Relaxed)
}
