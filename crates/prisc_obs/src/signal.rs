use std::ffi::{c_void, CString};

type Trampoline = unsafe extern "C" fn(*mut c_void, *mut obs_sys::calldata_t);

pub struct SignalConnection {
    handler: *mut obs_sys::signal_handler_t,
    signal: CString,
    trampoline: Trampoline,
    param: *mut c_void,
    /// Owns the boxed closure so it is freed when the connection drops.
    _closure: Box<dyn std::any::Any + Send>,
}

unsafe extern "C" fn signal_callback_trampoline(data: *mut c_void, _cd: *mut obs_sys::calldata_t) {
    let closure = &*(data as *const Box<dyn Fn() + Send>);
    closure();
}

unsafe extern "C" fn signal_code_trampoline(data: *mut c_void, cd: *mut obs_sys::calldata_t) {
    // `calldata_int`/`calldata_get_int` are static-inline in calldata.h and are
    // absent from the bindgen output — mirror the inline here: the "code" slot
    // is a long long, extracted byte-for-byte via calldata_get_data.
    let mut code: i64 = 0;
    obs_sys::calldata_get_data(
        cd,
        c"code".as_ptr(),
        &mut code as *mut i64 as *mut c_void,
        std::mem::size_of::<i64>(),
    );
    let closure = &*(data as *const Box<dyn Fn(i64) + Send>);
    closure(code);
}

impl SignalConnection {
    pub fn connect<F>(handler: *mut obs_sys::signal_handler_t, signal: &str, callback: F) -> Self
    where
        F: Fn() + Send + 'static,
    {
        let boxed: Box<Box<dyn Fn() + Send>> = Box::new(Box::new(callback));
        let raw = Box::into_raw(boxed);
        Self::connect_raw(
            handler,
            signal,
            signal_callback_trampoline,
            raw as *mut c_void,
            unsafe { Box::from_raw(raw) },
        )
    }

    /// Connects a callback that receives the signal's `code` calldata value
    /// (used for the output "stop" signal's stop code).
    pub fn connect_with_code<F>(
        handler: *mut obs_sys::signal_handler_t,
        signal: &str,
        callback: F,
    ) -> Self
    where
        F: Fn(i64) + Send + 'static,
    {
        let boxed: Box<Box<dyn Fn(i64) + Send>> = Box::new(Box::new(callback));
        let raw = Box::into_raw(boxed);
        Self::connect_raw(
            handler,
            signal,
            signal_code_trampoline,
            raw as *mut c_void,
            unsafe { Box::from_raw(raw) },
        )
    }

    fn connect_raw(
        handler: *mut obs_sys::signal_handler_t,
        signal: &str,
        trampoline: Trampoline,
        param: *mut c_void,
        closure: Box<dyn std::any::Any + Send>,
    ) -> Self {
        let signal_c = CString::new(signal).unwrap();
        unsafe {
            obs_sys::signal_handler_connect(handler, signal_c.as_ptr(), Some(trampoline), param);
        }
        SignalConnection {
            handler,
            signal: signal_c,
            trampoline,
            param,
            _closure: closure,
        }
    }
}

impl Drop for SignalConnection {
    fn drop(&mut self) {
        unsafe {
            obs_sys::signal_handler_disconnect(
                self.handler,
                self.signal.as_ptr(),
                Some(self.trampoline),
                self.param,
            );
        }
    }
}
