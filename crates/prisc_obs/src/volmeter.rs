use std::ffi::c_void;

use crate::error::ObsError;
use crate::source::ObsSource;

// libobs media-io/audio-io.h (a #define, absent from bindgen output)
pub const MAX_AUDIO_CHANNELS: usize = 8;

type VolmeterClosure = Box<dyn Fn(&[f32], &[f32], &[f32]) + Send>;

pub struct ObsVolmeter {
    ptr: *mut obs_sys::obs_volmeter_t,
    /// Raw pointer registered with libobs; only a raw pointer (no live `Box`)
    /// while the audio thread may dereference it — remove_callback
    /// reconstitutes and drops the Box after deregistration.
    callback: Option<*mut c_void>,
}

unsafe extern "C" fn volmeter_trampoline(
    param: *mut c_void,
    magnitude: *const f32,
    peak: *const f32,
    input_peak: *const f32,
) {
    let closure = &*(param as *const VolmeterClosure);
    let magnitude = std::slice::from_raw_parts(magnitude, MAX_AUDIO_CHANNELS);
    let peak = std::slice::from_raw_parts(peak, MAX_AUDIO_CHANNELS);
    let input_peak = std::slice::from_raw_parts(input_peak, MAX_AUDIO_CHANNELS);
    closure(magnitude, peak, input_peak);
}

impl ObsVolmeter {
    pub fn new() -> Result<Self, ObsError> {
        let ptr = unsafe { obs_sys::obs_volmeter_create(obs_sys::obs_fader_type_OBS_FADER_LOG) };
        if ptr.is_null() {
            return Err(ObsError::NullPointer("obs_volmeter_create"));
        }
        Ok(ObsVolmeter {
            ptr,
            callback: None,
        })
    }

    pub fn attach_source(&self, source: &ObsSource) -> bool {
        unsafe { obs_sys::obs_volmeter_attach_source(self.ptr, source.as_ptr()) }
    }

    pub fn detach_source(&self) {
        unsafe { obs_sys::obs_volmeter_detach_source(self.ptr) }
    }

    /// Registers the callback, replacing any previous one. It is invoked on
    /// the obs audio thread with dB levels for all `MAX_AUDIO_CHANNELS`.
    pub fn add_callback<F>(&mut self, callback: F)
    where
        F: Fn(&[f32], &[f32], &[f32]) + Send + 'static,
    {
        self.remove_callback();
        let boxed: Box<VolmeterClosure> = Box::new(Box::new(callback));
        let raw = Box::into_raw(boxed);
        unsafe {
            obs_sys::obs_volmeter_add_callback(
                self.ptr,
                Some(volmeter_trampoline),
                raw as *mut c_void,
            );
        }
        self.callback = Some(raw as *mut c_void);
    }

    pub fn remove_callback(&mut self) {
        if let Some(param) = self.callback.take() {
            unsafe {
                obs_sys::obs_volmeter_remove_callback(self.ptr, Some(volmeter_trampoline), param);
                // Safe to reclaim and drop: remove_callback blocks until no
                // callback is in flight.
                drop(Box::from_raw(param as *mut VolmeterClosure));
            }
        }
    }
}

impl Drop for ObsVolmeter {
    fn drop(&mut self) {
        self.remove_callback();
        unsafe { obs_sys::obs_volmeter_destroy(self.ptr) };
    }
}
