use std::ffi::CString;
use std::path::Path;
use std::ptr;
use std::sync::atomic::{AtomicBool, Ordering};

use crate::audio::AudioInfo;
use crate::error::ObsError;
use crate::source::ObsSource;
use crate::video::VideoInfo;

static INITIALIZED: AtomicBool = AtomicBool::new(false);

/// Handle to the global libobs state.
///
/// NOTE: there is intentionally NO `Drop` impl — `obs_shutdown` is never called
/// (known OBS shutdown crashes). The context is effectively a leaked singleton
/// and every process exit routes through `platform::exit_process`.
pub struct ObsContext {
    _not_send_sync: std::marker::PhantomData<*mut ()>,
}

impl ObsContext {
    pub fn new(locale: &str) -> Result<Self, ObsError> {
        if INITIALIZED.swap(true, Ordering::SeqCst) {
            return Err(ObsError::AlreadyInitialized);
        }
        let locale_c = CString::new(locale).unwrap();
        let ok = unsafe { obs_sys::obs_startup(locale_c.as_ptr(), ptr::null(), ptr::null_mut()) };
        if !ok {
            INITIALIZED.store(false, Ordering::SeqCst);
            return Err(ObsError::Startup);
        }
        Ok(ObsContext {
            _not_send_sync: std::marker::PhantomData,
        })
    }

    pub fn reset_video(&self, info: &VideoInfo) -> Result<(), ObsError> {
        let mut raw = info.to_raw();
        let ret = unsafe { obs_sys::obs_reset_video(&mut raw) };
        if ret != 0 {
            return Err(ObsError::VideoReset(ret as i32));
        }
        // libobs never initialises its SDR white / HDR nominal peak levels:
        // `obs_core` is zero-allocated and only `obs_set_video_levels` writes
        // them, which OBS Studio's frontend calls after every video reset
        // (OBSBasic.cpp, 300 / 1000 nit defaults). Left at zero, the
        // HDR-to-SDR capture paths divide by `obs_get_video_sdr_white_level()`
        // (win-capture and libobs-winrt DrawMultiplyTonemap: 80 / 0 = inf, the
        // tonemap saturates the NaN to black) and the SDR-to-scRGB presentation
        // path multiplies by it (obs_render_main_texture on an HDR swapchain:
        // zero). obs_reset_video does not touch the levels, so after the first
        // reset this re-applies the same values; done after every reset for
        // parity with the frontend. The graphics thread is already rendering
        // (and reading the levels) by the time obs_reset_video returns, so
        // take the graphics context around the plain float stores.
        unsafe {
            obs_sys::obs_enter_graphics();
            obs_sys::obs_set_video_levels(300.0, 1000.0);
            obs_sys::obs_leave_graphics();
        }
        Ok(())
    }

    pub fn reset_audio(&self, info: &AudioInfo) -> Result<(), ObsError> {
        let raw = info.to_raw();
        let ok = unsafe { obs_sys::obs_reset_audio(&raw) };
        if !ok {
            return Err(ObsError::AudioReset(false));
        }
        Ok(())
    }

    /// Registers a libobs core data path (`obs_add_data_path`). libobs stores the
    /// string verbatim and concatenates file names onto it, so it must end with a
    /// path separator — this wrapper appends `/` when missing.
    pub fn add_data_path(&self, path: &Path) {
        let mut s = path.to_string_lossy().into_owned();
        if !s.ends_with('/') && !s.ends_with('\\') {
            s.push('/');
        }
        let c = CString::new(s.replace('\0', "")).unwrap();
        unsafe { obs_sys::obs_add_data_path(c.as_ptr()) };
    }

    pub fn add_module_path(&self, bin: &str, data: &str) {
        let bin_c = CString::new(bin).unwrap();
        let data_c = CString::new(data).unwrap();
        unsafe { obs_sys::obs_add_module_path(bin_c.as_ptr(), data_c.as_ptr()) };
    }

    pub fn load_all_modules(&self) {
        unsafe {
            obs_sys::obs_load_all_modules();
            obs_sys::obs_post_load_modules();
        }
    }

    pub fn set_output_source(&self, channel: u32, source: Option<&ObsSource>) {
        let ptr = source.map_or(ptr::null_mut(), |s| s.as_ptr());
        unsafe { obs_sys::obs_set_output_source(channel, ptr) };
    }

    /// Raw-pointer variant, used for the scene's source on channel 0.
    /// (Pointer is passed straight to libobs, never dereferenced here.)
    #[allow(clippy::not_unsafe_ptr_arg_deref)]
    pub fn set_output_source_raw(&self, channel: u32, source: *mut obs_sys::obs_source_t) {
        unsafe { obs_sys::obs_set_output_source(channel, source) };
    }

    pub fn get_video(&self) -> *mut obs_sys::video_t {
        unsafe { obs_sys::obs_get_video() }
    }

    pub fn get_audio(&self) -> *mut obs_sys::audio_t {
        unsafe { obs_sys::obs_get_audio() }
    }
}
