use std::ffi::CString;

use crate::error::ObsError;
use crate::source::ObsSource;

pub struct ObsScene {
    pub(crate) ptr: *mut obs_sys::obs_scene_t,
}

pub struct ObsSceneItem {
    pub(crate) ptr: *mut obs_sys::obs_sceneitem_t,
}

impl ObsScene {
    pub fn create(name: &str) -> Result<Self, ObsError> {
        let name_c = CString::new(name).unwrap();
        let ptr = unsafe { obs_sys::obs_scene_create(name_c.as_ptr()) };
        if ptr.is_null() {
            return Err(ObsError::NullPointer("obs_scene_create"));
        }
        Ok(Self { ptr })
    }

    pub fn get_source(&self) -> *mut obs_sys::obs_source_t {
        unsafe { obs_sys::obs_scene_get_source(self.ptr as *const _) }
    }

    pub fn add(&self, source: &ObsSource) -> ObsSceneItem {
        let ptr = unsafe { obs_sys::obs_scene_add(self.ptr, source.ptr) };
        unsafe { obs_sys::obs_sceneitem_addref(ptr) };
        ObsSceneItem { ptr }
    }
}

impl Drop for ObsScene {
    fn drop(&mut self) {
        unsafe { obs_sys::obs_scene_release(self.ptr) };
    }
}

impl ObsSceneItem {
    pub fn as_ptr(&self) -> *mut obs_sys::obs_sceneitem_t {
        self.ptr
    }

    pub fn set_visible(&self, visible: bool) {
        unsafe { obs_sys::obs_sceneitem_set_visible(self.ptr, visible) };
    }

    /// Detaches the item from its scene (releasing the scene's reference).
    /// The wrapper's own reference is still released by `Drop`.
    pub fn remove(&self) {
        unsafe { obs_sys::obs_sceneitem_remove(self.ptr) };
    }

    pub fn set_pos(&self, x: f32, y: f32) {
        // Local mirror of `struct vec2` — the bindgen output renders vec2 as an
        // opaque type (anonymous-union member), so it cannot be constructed
        // directly. The layout is two f32s; cast at the call site.
        #[repr(C)]
        struct Vec2 {
            x: f32,
            y: f32,
        }
        let pos = Vec2 { x, y };
        unsafe {
            obs_sys::obs_sceneitem_set_pos(self.ptr, &pos as *const Vec2 as *const obs_sys::vec2)
        };
    }

    pub fn set_scale(&self, x: f32, y: f32) {
        // Same vec2 mirror as set_pos.
        #[repr(C)]
        struct Vec2 {
            x: f32,
            y: f32,
        }
        let scale = Vec2 { x, y };
        unsafe {
            obs_sys::obs_sceneitem_set_scale(
                self.ptr,
                &scale as *const Vec2 as *const obs_sys::vec2,
            )
        };
    }

    pub fn set_bounds_type(&self, bounds_type: obs_sys::obs_bounds_type) {
        unsafe { obs_sys::obs_sceneitem_set_bounds_type(self.ptr, bounds_type) };
    }

    /// `alignment` is an OBS_ALIGN_* bitmask; 0 = centered inside the bounds.
    pub fn set_bounds_alignment(&self, alignment: u32) {
        unsafe { obs_sys::obs_sceneitem_set_bounds_alignment(self.ptr, alignment) };
    }

    /// The bounds box size (px); only meaningful once a bounds type is set.
    pub fn set_bounds(&self, w: f32, h: f32) {
        // Same vec2 mirror as set_pos.
        #[repr(C)]
        struct Vec2 {
            x: f32,
            y: f32,
        }
        let bounds = Vec2 { x: w, y: h };
        unsafe {
            obs_sys::obs_sceneitem_set_bounds(
                self.ptr,
                &bounds as *const Vec2 as *const obs_sys::vec2,
            )
        };
    }

    /// Places the item at the canvas origin with an `OBS_BOUNDS_MAX_ONLY` box
    /// of `w`x`h` anchored top-left: shown 1:1 when it fits, scaled down to
    /// fit when larger.
    ///
    /// Call it only once the canvas has its final size. Since OBS 31 a scene
    /// item stores its position and bounds *relative to the canvas as it was
    /// when they were set* (scenes default to `AbsoluteCoordinates = false`,
    /// libobs/obs-scene.c `pos_from_absolute` / `size_from_absolute`), and
    /// converts them back against the current canvas on every transform
    /// update. A later `obs_reset_video` to another base size therefore
    /// silently moves and rescales the item: (0,0) set on a 1280x720 canvas
    /// reads back as (-170.5, 0) on a 1024x768 one, and a 1024x768 box as
    /// 1092x819. Re-applying everything here after the reset is the fix.
    pub fn place_top_left_bounded(&self, w: f32, h: f32) {
        self.set_pos(0.0, 0.0);
        self.set_bounds_type(obs_sys::obs_bounds_type_OBS_BOUNDS_MAX_ONLY);
        self.set_bounds_alignment(obs_sys::OBS_ALIGN_LEFT | obs_sys::OBS_ALIGN_TOP);
        self.set_bounds(w, h);
    }

    /// Position in canvas pixels, converted against the current canvas (see
    /// [`ObsSceneItem::place_top_left_bounded`]).
    pub fn pos(&self) -> (f32, f32) {
        // Same vec2 mirror as set_pos.
        #[repr(C)]
        struct Vec2 {
            x: f32,
            y: f32,
        }
        let mut v = Vec2 { x: 0.0, y: 0.0 };
        unsafe {
            obs_sys::obs_sceneitem_get_pos(self.ptr, &mut v as *mut Vec2 as *mut obs_sys::vec2)
        };
        (v.x, v.y)
    }

    /// Bounds box size in canvas pixels, converted against the current
    /// canvas.
    pub fn bounds(&self) -> (f32, f32) {
        // Same vec2 mirror as set_pos.
        #[repr(C)]
        struct Vec2 {
            x: f32,
            y: f32,
        }
        let mut v = Vec2 { x: 0.0, y: 0.0 };
        unsafe {
            obs_sys::obs_sceneitem_get_bounds(self.ptr, &mut v as *mut Vec2 as *mut obs_sys::vec2)
        };
        (v.x, v.y)
    }
}

impl Clone for ObsSceneItem {
    fn clone(&self) -> Self {
        unsafe { obs_sys::obs_sceneitem_addref(self.ptr) };
        Self { ptr: self.ptr }
    }
}

impl Drop for ObsSceneItem {
    fn drop(&mut self) {
        unsafe { obs_sys::obs_sceneitem_release(self.ptr) };
    }
}
