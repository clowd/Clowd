//! Persistent-host mode (`--persistent`): the process warms up once
//! (adapters, devices, pipelines, hidden windows) and then serves capture
//! cycles on demand, driven by an NDJSON protocol on stdin/stdout — see
//! [`protocol`] for the wire types and `stdin` for the reader thread.

pub mod display;
pub mod protocol;
pub mod stdin;

use std::io::Write;

use protocol::{HostCommand, HostEvent};

/// Events injected into the winit user-event channel by background
/// threads (the stdin reader, the display-change observers in
/// [`display`], and the per-worker wgpu device-lost callbacks).
pub enum AppEvent {
    /// A command line arrived on stdin.
    Command(HostCommand),
    /// stdin hit EOF — the parent process is gone. Cancel any active
    /// cycle and exit.
    ParentGone,
    /// The OS reported a display topology change (resolution, monitor
    /// add/remove, DPI). Debounced by the app, then acted on: emit
    /// `display_changed` and exit with `EXIT_DISPLAY_CHANGED` so the
    /// shell respawns us against the new topology.
    DisplayChange,
    /// A render worker's wgpu device was lost (driver reset/update).
    /// The worker can never serve another cycle — emit `display_changed`
    /// and exit with `EXIT_GPU_LOST`.
    GpuLost,
}

/// Serialize `event` as one NDJSON line on stdout and flush it. Callers
/// gate on persistent mode themselves — in one-shot mode stdout is plain
/// terminal output and must not carry protocol lines. The std stdout
/// handle's internal mutex keeps concurrent emits line-atomic.
pub fn emit(event: &HostEvent) {
    let line = match serde_json::to_string(event) {
        Ok(l) => l,
        Err(e) => {
            // Only possible via a serializer bug; never poison the stream
            // with a half-written line.
            error!("failed to serialize host event: {e}");
            return;
        }
    };
    let mut out = std::io::stdout().lock();
    // A dead parent makes this write fail (broken pipe); shutdown is
    // handled by the stdin reader's EOF signal, so just log it.
    if let Err(e) = writeln!(out, "{line}").and_then(|()| out.flush()) {
        warn!("failed to emit host event: {e}");
    }
}
