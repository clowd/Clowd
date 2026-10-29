use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, Ordering};

type TickClosure = Box<dyn FnMut(f32) + Send>;

/// A closure libobs runs once per rendered frame on its graphics thread
/// (`obs_add_tick_callback`), with the seconds since the previous tick.
/// Deregistered by [`remove`](Self::remove) or on drop.
///
/// A panic inside the closure is not caught here: unwinding out of the
/// `extern "C"` trampoline aborts the process. Callers that must survive one
/// catch it themselves.
pub struct TickCallback {
    /// The boxed closure registered as libobs's `param`. Only a raw pointer
    /// (no live `Box`) while the graphics thread may call it; `Drop`
    /// reconstitutes and frees it after deregistering.
    param: *mut TickClosure,
    registered: AtomicBool,
}

unsafe extern "C" fn tick_trampoline(param: *mut c_void, seconds: f32) {
    let closure = &mut *(param as *mut TickClosure);
    closure(seconds);
}

impl TickCallback {
    pub fn add<F>(callback: F) -> Self
    where
        F: FnMut(f32) + Send + 'static,
    {
        let boxed: Box<TickClosure> = Box::new(Box::new(callback));
        let param = Box::into_raw(boxed);
        unsafe { prisc_obs_sys::obs_add_tick_callback(Some(tick_trampoline), param as *mut c_void) };
        TickCallback {
            param,
            registered: AtomicBool::new(true),
        }
    }

    /// Deregisters the callback; later calls do nothing. Once this returns the
    /// closure is not running and never runs again: libobs calls and removes
    /// tick callbacks under the same mutex.
    pub fn remove(&self) {
        if self.registered.swap(false, Ordering::AcqRel) {
            unsafe {
                prisc_obs_sys::obs_remove_tick_callback(Some(tick_trampoline), self.param as *mut c_void)
            };
        }
    }
}

impl Drop for TickCallback {
    fn drop(&mut self) {
        self.remove();
        // Safe to free: deregistered above, and nothing else holds the box.
        drop(unsafe { Box::from_raw(self.param) });
    }
}
