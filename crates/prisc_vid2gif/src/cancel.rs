//! Cooperative cancellation. The stdin watcher trips the token; each ffmpeg
//! stage checks it on entry and registers its child as the kill target, so a
//! cancel unblocks the stage's stdout read immediately instead of waiting for
//! the stage to finish.

use std::process::Child;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

/// Sentinel error carried through anyhow when the user cancelled; `main`
/// downcasts to it to distinguish cancellation from real failures.
#[derive(Debug)]
pub struct Cancelled;

impl std::fmt::Display for Cancelled {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("cancelled")
    }
}

impl std::error::Error for Cancelled {}

#[derive(Clone)]
pub struct CancelToken(Arc<Inner>);

struct Inner {
    requested: AtomicBool,
    active: Mutex<Option<Child>>,
}

impl CancelToken {
    pub fn new() -> CancelToken {
        CancelToken(Arc::new(Inner {
            requested: AtomicBool::new(false),
            active: Mutex::new(None),
        }))
    }

    /// Trips the token and kills the active ffmpeg child, if any.
    pub fn cancel(&self) {
        self.0.requested.store(true, Ordering::SeqCst);
        self.kill_active();
    }

    pub fn is_cancelled(&self) -> bool {
        self.0.requested.load(Ordering::SeqCst)
    }

    /// Makes `child` the kill target. A cancel that landed between spawn and
    /// registration is applied here, closing that race.
    pub fn register(&self, child: Child) {
        *self.0.active.lock().unwrap() = Some(child);
        if self.is_cancelled() {
            self.kill_active();
        }
    }

    /// Takes the child back for waiting; the token no longer kills it.
    pub fn take(&self) -> Option<Child> {
        self.0.active.lock().unwrap().take()
    }

    fn kill_active(&self) {
        if let Some(child) = self.0.active.lock().unwrap().as_mut() {
            let _ = child.kill();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn starts_untripped_and_trips_once_cancelled() {
        let token = CancelToken::new();
        assert!(!token.is_cancelled());
        token.cancel();
        assert!(token.is_cancelled());
        token.cancel(); // idempotent, no active child to kill
        assert!(token.is_cancelled());
    }

    #[test]
    fn clones_share_state() {
        let token = CancelToken::new();
        let clone = token.clone();
        clone.cancel();
        assert!(token.is_cancelled());
    }

    #[test]
    fn take_without_register_is_none() {
        assert!(CancelToken::new().take().is_none());
    }
}
