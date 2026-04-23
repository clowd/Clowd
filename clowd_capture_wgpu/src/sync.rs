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

    pub fn try_get(&self) -> Option<T> {
        let guard = self.inner.lock().unwrap();
        guard.as_ref().cloned()
    }
}

/// Panic-safe replacement for `std::sync::Barrier`. `signal_all` wakes
/// every thread blocked in `wait`; if a worker panics before reaching
/// `wait`, the remaining workers and the signaller aren't deadlocked.
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
            .wait_while(guard, |signalled| !*signalled)
            .unwrap();
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
