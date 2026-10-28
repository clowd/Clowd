//! The stdout protocol channel: the `capture` catalog of Prisc's helper protocol
//! (crates/prisc_capture/HELPER_PROTOCOL.md §4.5), for both standby and one-shot runs.

use std::fmt::Display;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::OnceLock;

use prisc_ipc::catalog::capture::Event;
use prisc_ipc::catalog::ErrorCode;
use prisc_ipc::Channel;

static OUT: OnceLock<Channel<Event>> = OnceLock::new();

/// Makes `out` the channel every emitter in the process writes to. Called once, first thing in
/// `main`.
pub fn init(out: Channel<Event>) {
    if OUT.set(out).is_err() {
        panic!("protocol::init called twice");
    }
}

/// The process's protocol channel (stdout).
pub fn out() -> &'static Channel<Event> {
    OUT.get()
        .expect("protocol::init runs first thing in main")
}

/// Called once on the way to the terminal line. The quit path ends a capture
/// from a thread of its own while the main thread may be finishing too: the
/// first one to get here writes the run's one terminal line and exits, and
/// any later one parks until that exit takes the process down. Events the
/// other threads emit after the terminal line are dropped by the channel.
pub fn claim_terminal() {
    static CLAIMED: AtomicBool = AtomicBool::new(false);
    if CLAIMED.swap(true, Ordering::SeqCst) {
        loop {
            std::thread::park();
        }
    }
}

/// A best-effort fatal `error`, then exit with `status`: for the exits whose code itself
/// carries the meaning (3, no Screen Recording permission; 4, the desktop screenshot failed),
/// which the plain fatal path would turn into 1.
pub fn fail_exit(code: ErrorCode, message: impl Display, status: i32) -> ! {
    claim_terminal();
    let message = prisc_ipc::single_line(&message.to_string());
    out().emit(&Event::Error {
        code,
        message,
        fatal: true,
    });
    prisc_ipc::lifetime::exit(status)
}
