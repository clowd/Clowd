use std::ffi::CStr;

pub struct VideoInfo {
    /// Graphics backend module, e.g. `c"libobs-d3d11"` or `c"libobs-metal.dylib"`.
    /// Caller-provided; there is no baked default.
    pub graphics_module: &'static CStr,
    pub base_width: u32,
    pub base_height: u32,
    pub output_width: u32,
    pub output_height: u32,
    pub fps_num: u32,
    pub fps_den: u32,
    /// Graphics adapter index (`CreateDXGIFactory1` + `EnumAdapters1` order on
    /// Windows; ignored elsewhere). 0 is libobs's default. NOTE: libobs creates
    /// the graphics device on the FIRST `obs_reset_video` only — a later reset
    /// with a different index is silently ignored (`obs_reset_video` guards
    /// `obs_init_graphics` behind `if (!obs->video.graphics)`), so this is
    /// effectively process-fixed.
    pub adapter: u32,
}

impl VideoInfo {
    pub fn to_raw(&self) -> obs_sys::obs_video_info {
        // Every field is set explicitly — no zeroed remainder.
        obs_sys::obs_video_info {
            graphics_module: self.graphics_module.as_ptr(),
            fps_num: self.fps_num,
            fps_den: self.fps_den,
            base_width: self.base_width,
            base_height: self.base_height,
            output_width: self.output_width,
            output_height: self.output_height,
            output_format: obs_sys::video_format_VIDEO_FORMAT_NV12,
            adapter: self.adapter,
            gpu_conversion: true,
            colorspace: obs_sys::video_colorspace_VIDEO_CS_709,
            range: obs_sys::video_range_type_VIDEO_RANGE_PARTIAL,
            scale_type: obs_sys::obs_scale_type_OBS_SCALE_BICUBIC,
        }
    }
}
