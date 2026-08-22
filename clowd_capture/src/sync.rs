use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};

/// One-shot value slot. Writers call `set(v)` once; readers block on
/// `wait()` until the value arrives, then receive a clone.
pub struct Latch<T: Clone> {
    inner: Mutex<Option<T>>,
    cv: Condvar,
}

impl<T: Clone> Latch<T> {
    pub fn new() -> Self {
        Self {
            inner: Mutex::new(None),
            cv: Condvar::new(),
        }
    }

    pub fn set(&self, val: T) {
        let mut guard = self.inner.lock().unwrap();
        *guard = Some(val);
        self.cv.notify_all();
    }

    #[allow(dead_code)]
    pub fn wait(&self) -> T {
        let guard = self.inner.lock().unwrap();
        let guard = self
            .cv
            .wait_while(guard, |v| v.is_none())
            .unwrap();
        guard.as_ref().unwrap().clone()
    }

    /// Like [`wait`](Self::wait), but gives up after `timeout` and returns `None`
    /// if no value has arrived — the writer thread may have died or wedged.
    pub fn wait_timeout(&self, timeout: std::time::Duration) -> Option<T> {
        let guard = self.inner.lock().unwrap();
        let (guard, _) = self
            .cv
            .wait_timeout_while(guard, timeout, |v| v.is_none())
            .unwrap();
        guard.as_ref().cloned()
    }

    pub fn try_get(&self) -> Option<T> {
        let guard = self.inner.lock().unwrap();
        guard.as_ref().cloned()
    }
}

/// Panic-safe replacement for `std::sync::Barrier`. `signal_all` wakes
/// every thread blocked in `wait`; if a worker panics before reaching
/// `wait`, the remaining workers and the signaler aren't deadlocked.
pub struct VisibleLatch {
    inner: Mutex<bool>,
    cv: Condvar,
}

impl VisibleLatch {
    pub fn new() -> Self {
        Self {
            inner: Mutex::new(false),
            cv: Condvar::new(),
        }
    }

    pub fn signal_all(&self) {
        let mut guard = self.inner.lock().unwrap();
        *guard = true;
        self.cv.notify_all();
    }

    pub fn wait(&self) {
        let guard = self.inner.lock().unwrap();
        let _guard = self
            .cv
            .wait_while(guard, |signaled| !*signaled)
            .unwrap();
    }

    /// Like [`wait`](Self::wait), but gives up after `timeout`; returns whether
    /// the latch was actually signaled. For background work that only wants to
    /// stay off the critical path — the signal comes from the show gate (or from
    /// `finish_cycle` on a cancel), and the exit paths that reach neither (window
    /// creation failing on every monitor) would otherwise park the waiter forever.
    pub fn wait_timeout(&self, timeout: std::time::Duration) -> bool {
        let guard = self.inner.lock().unwrap();
        let (guard, _) = self
            .cv
            .wait_timeout_while(guard, timeout, |signaled| !*signaled)
            .unwrap();
        *guard
    }
}

/// RAII guard that increments `ready_count` on drop unless disarmed.
/// Ensures a panicking worker still unblocks `about_to_wait`.
pub struct ReadyGuard {
    counter: Arc<AtomicUsize>,
    armed: bool,
}

impl ReadyGuard {
    pub fn new(counter: Arc<AtomicUsize>) -> Self {
        Self {
            counter,
            armed: true,
        }
    }

    pub fn disarm(&mut self) {
        self.armed = false;
    }
}

impl Drop for ReadyGuard {
    fn drop(&mut self) {
        if self.armed {
            self.counter.fetch_add(1, Ordering::Release);
        }
    }
}
