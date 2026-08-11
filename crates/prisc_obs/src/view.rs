//! `obs_view` wrapper: an independent source-channel table that can carry its
//! own video mix (`obs_view_add2`), used for secondary video tracks (e.g. a
//! webcam recorded alongside the main canvas).
//!
//! CRITICAL invariant (verified in libobs 32.1.2 source): `obs_reset_video`
//! destroys ALL view mixes. Any code path that calls `obs_reset_video` while
//! an `ObsView` with an added mix exists must drop the view (and everything
//! bound to its `video_t`, e.g. encoders) first, then rebuild and rebind
//! afterwards — otherwise encoders keep a dangling `video_t`.

use std::ptr;

use crate::error::ObsError;
use crate::video::VideoInfo;

pub struct ObsView {
    ptr: *mut obs_sys::obs_view_t,
    added: bool,
}

impl ObsView {
    pub fn new() -> Result<Self, ObsError> {
        let ptr = unsafe { obs_sys::obs_view_create() };
        if ptr.is_null() {
            return Err(ObsError::NullPointer("obs_view_create"));
        }
        Ok(Self { ptr, added: false })
    }

    /// Assigns `source` to `channel` of this view. Raw-pointer variant used
    /// for a scene's source (mirrors `ObsContext::set_output_source_raw`);
    /// the pointer is passed straight to libobs, never dereferenced here.
    #[allow(clippy::not_unsafe_ptr_arg_deref)]
    pub fn set_source_raw(&self, channel: u32, source: *mut obs_sys::obs_source_t) {
        unsafe { obs_sys::obs_view_set_source(self.ptr, channel, source) };
    }

    /// Creates an independent video mix for this view with its own
    /// size/fps (`obs_view_add2`). The returned `video_t` stays valid until
    /// the view is dropped — or until `obs_reset_video` runs (see the module
    /// docs). At most one mix per view is supported by this wrapper.
    pub fn add2(&mut self, info: &VideoInfo) -> Result<*mut obs_sys::video_t, ObsError> {
        let mut raw = info.to_raw();
        let video = unsafe { obs_sys::obs_view_add2(self.ptr, &mut raw) };
        if video.is_null() {
            return Err(ObsError::NullPointer("obs_view_add2"));
        }
        self.added = true;
        Ok(video)
    }
}

impl Drop for ObsView {
    fn drop(&mut self) {
        unsafe {
            if self.added {
                obs_sys::obs_view_remove(self.ptr);
            }
            // Release the view's channel refs before destroying it.
            obs_sys::obs_view_set_source(self.ptr, 0, ptr::null_mut());
            obs_sys::obs_view_destroy(self.ptr);
        }
    }
}
