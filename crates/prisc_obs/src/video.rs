pub struct VideoInfo {
    pub base_width: u32,
    pub base_height: u32,
    pub output_width: u32,
    pub output_height: u32,
    pub fps_num: u32,
    pub fps_den: u32,
}

impl VideoInfo {
    pub fn to_raw(&self) -> obs_sys::obs_video_info {
        let mut ovi: obs_sys::obs_video_info = unsafe { std::mem::zeroed() };
        ovi.base_width = self.base_width;
        ovi.base_height = self.base_height;
        ovi.output_width = self.output_width;
        ovi.output_height = self.output_height;
        ovi.fps_num = self.fps_num;
        ovi.fps_den = self.fps_den;
        ovi.graphics_module = b"libobs-metal.dylib\0".as_ptr() as *const _;
        ovi.output_format = obs_sys::video_format_VIDEO_FORMAT_NV12;
        ovi
    }
}
