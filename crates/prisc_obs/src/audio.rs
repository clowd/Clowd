pub struct AudioInfo {
    pub samples_per_sec: u32,
}

impl AudioInfo {
    pub fn to_raw(&self) -> obs_sys::obs_audio_info {
        let mut oai: obs_sys::obs_audio_info = unsafe { std::mem::zeroed() };
        oai.samples_per_sec = self.samples_per_sec;
        oai.speakers = obs_sys::speaker_layout_SPEAKERS_STEREO;
        oai
    }
}
