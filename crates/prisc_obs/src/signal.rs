use std::ffi::CString;

pub struct SignalConnection {
    handler: *mut obs_sys::signal_handler_t,
    signal: CString,
    _closure: Box<dyn std::any::Any + Send>,
}

unsafe extern "C" fn signal_callback_trampoline(
    _data: *mut std::ffi::c_void,
    cd: *mut obs_sys::calldata_t,
) {
    let _ = cd;
    let closure = &*(_data as *const Box<dyn Fn() + Send>);
    closure();
}

impl SignalConnection {
    pub fn connect<F>(
        handler: *mut obs_sys::signal_handler_t,
        signal: &str,
        callback: F,
    ) -> Self
    where
        F: Fn() + Send + 'static,
    {
        let signal_c = CString::new(signal).unwrap();
        let boxed: Box<Box<dyn Fn() + Send>> = Box::new(Box::new(callback));
        let raw = Box::into_raw(boxed);
        unsafe {
            obs_sys::signal_handler_connect(
                handler,
                signal_c.as_ptr(),
                Some(signal_callback_trampoline),
                raw as *mut std::ffi::c_void,
            );
        }
        SignalConnection {
            handler,
            signal: signal_c,
            _closure: unsafe { Box::from_raw(raw) },
        }
    }
}

impl Drop for SignalConnection {
    fn drop(&mut self) {
        let raw = &*self._closure as *const dyn std::any::Any as *const () as *mut std::ffi::c_void;
        unsafe {
            obs_sys::signal_handler_disconnect(
                self.handler,
                self.signal.as_ptr(),
                Some(signal_callback_trampoline),
                raw,
            );
        }
    }
}
