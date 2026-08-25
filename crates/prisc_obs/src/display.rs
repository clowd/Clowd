//! `obs_display` wrapper: a swapchain bound to a platform window/view that
//! libobs renders into on its own graphics thread (no encoder, no output —
//! just a live GPU-composited view of whatever the draw callback renders).
//!
//! The draw callback registered via [`ObsDisplay::add_draw_callback`] runs on
//! **OBS's graphics thread**, not the UI thread. It must do nothing but call
//! `obs_render_main_texture()`; anything that needs the UI thread must be
//! posted across.

use std::ffi::c_void;

use crate::error::ObsError;

pub struct ObsDisplay {
    ptr: *mut obs_sys::obs_display_t,
}

impl ObsDisplay {
    /// Creates a display swapchain on `window`: an HWND on Windows, an
    /// NSView* on macOS (matching libobs `graphics/graphics.h`'s `gs_window`,
    /// whose single field is platform-dependent).
    ///
    /// `gs_init_data`: `cx`/`cy` as given; `format = GS_BGRA` and
    /// `zsformat = GS_ZS_NONE` (no depth buffer — 2D composition only);
    /// `num_backbuffers = 0` lets libobs pick its default; `adapter = 0`
    /// (the GPU libobs itself initialized on).
    ///
    /// # Safety
    /// `window` must be a valid platform window/view pointer that outlives
    /// the display.
    pub unsafe fn new(window: *mut c_void, cx: u32, cy: u32) -> Result<Self, ObsError> {
        // bindgen renders the platform-specific gs_window field per target:
        // Windows `hwnd: *mut c_void`, macOS `view: id` (id = *mut
        // objc_object, the __unsafe_unretained ObjC pointer — no retain is
        // taken, hence the outlives requirement above).
        #[cfg(windows)]
        let gs_window = obs_sys::gs_window { hwnd: window };
        #[cfg(target_os = "macos")]
        let gs_window = obs_sys::gs_window {
            view: window.cast::<obs_sys::objc_object>(),
        };

        let init = obs_sys::gs_init_data {
            window: gs_window,
            cx,
            cy,
            num_backbuffers: 0,
            format: obs_sys::gs_color_format_GS_BGRA,
            zsformat: obs_sys::gs_zstencil_format_GS_ZS_NONE,
            adapter: 0,
        };
        let ptr = unsafe { obs_sys::obs_display_create(&init, 0) };
        if ptr.is_null() {
            return Err(ObsError::NullPointer("obs_display_create"));
        }
        Ok(Self { ptr })
    }

    pub fn resize(&self, cx: u32, cy: u32) {
        unsafe { obs_sys::obs_display_resize(self.ptr, cx, cy) };
    }

    pub fn set_enabled(&self, enabled: bool) {
        unsafe { obs_sys::obs_display_set_enabled(self.ptr, enabled) };
    }

    /// Background color as 0xRRGGBB, painted where the rendered texture does
    /// not cover the swapchain.
    pub fn set_background_color(&self, color: u32) {
        unsafe { obs_sys::obs_display_set_background_color(self.ptr, color) };
    }

    /// Registers `cb` to be invoked each frame with `(param, cx, cy)`.
    ///
    /// # Safety
    /// `cb` runs on the OBS graphics thread (see module docs — it must only
    /// call `obs_render_main_texture`), and `param` must outlive the display.
    pub unsafe fn add_draw_callback(
        &self,
        cb: unsafe extern "C" fn(*mut c_void, u32, u32),
        param: *mut c_void,
    ) {
        unsafe { obs_sys::obs_display_add_draw_callback(self.ptr, Some(cb), param) };
    }
}

impl Drop for ObsDisplay {
    fn drop(&mut self) {
        unsafe { obs_sys::obs_display_destroy(self.ptr) };
    }
}
