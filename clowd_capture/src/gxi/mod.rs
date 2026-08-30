//! `gxi` — the capture overlay's thin GPU abstraction.
//!
//! Concrete structs, zero dynamic dispatch, compile-time backend selection.
//! Both backends expose the *same* public API (identical type names and
//! signatures, enforced by the CI compile matrix building both OSes), so
//! the rest of the crate is written against `crate::gxi::*` and never
//! names a backend.
//!
//! Backend selection: Windows ships the `d3d11` backend and macOS ships
//! the `metal` backend. No other platform has a backend: the overlay
//! only supports these two, and any other target fails to compile here.
//! Exactly one backend is compiled into any given binary.

use std::sync::atomic::{AtomicBool, Ordering};

pub mod types;

#[cfg(windows)]
mod d3d11;
#[cfg(target_os = "macos")]
mod metal;

#[cfg(windows)]
pub use self::d3d11::*;
#[cfg(target_os = "macos")]
pub use self::metal::*;
pub use types::*;

// ── GPU-timing master switch ────────────────────────────────────────
// Backend-agnostic policy, which is why it lives here and not in a
// backend module: both backends' `GpuTimings::new` read it.

/// Master switch for GPU frame timing, off by default. When `false`,
/// `GpuTimings::new` returns `None` - nothing is constructed (no query
/// ring on d3d11, no completed handlers on metal) and every per-frame
/// timing hook is a no-op.
static GPU_TIMING: AtomicBool = AtomicBool::new(false);

/// Must be called before any render worker reaches `GpuTimings::new`
/// (each worker calls it once, right before its render loop): flipping
/// the switch afterwards changes nothing for workers that already built
/// (or skipped building) their `GpuTimings`. Relaxed ordering is enough:
/// the worker threads are spawned after this runs, and thread spawn is
/// itself the synchronization edge.
// Called from `main::run` with `--gpu-timing`, before the session spawns
// any render worker.
pub fn set_gpu_timing_enabled(enabled: bool) {
    GPU_TIMING.store(enabled, Ordering::Relaxed);
}

/// Reads the master switch.
pub(crate) fn gpu_timing_enabled() -> bool {
    GPU_TIMING.load(Ordering::Relaxed)
}
