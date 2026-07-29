//! Cooperative cancellation: the stdin watcher trips the token and the
//! conversion loops check it between packets, so a cancel takes effect within
//! one packet's worth of work.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

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
pub struct CancelToken(Arc<AtomicBool>);

impl CancelToken {
    pub fn new() -> CancelToken {
        CancelToken(Arc::new(AtomicBool::new(false)))
    }

    pub fn cancel(&self) {
        self.0.store(true, Ordering::SeqCst);
    }

    pub fn is_cancelled(&self) -> bool {
        self.0.load(Ordering::SeqCst)
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
        token.cancel(); // idempotent
        assert!(token.is_cancelled());
    }

    #[test]
    fn clones_share_state() {
        let token = CancelToken::new();
        let clone = token.clone();
        clone.cancel();
        assert!(token.is_cancelled());
    }
}
