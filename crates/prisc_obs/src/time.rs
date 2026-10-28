/// libobs's monotonic clock (`os_gettime_ns`): the time base of every video
/// frame and encoder timestamp, so anything stamped with it lines up with the
/// recording.
pub fn now_ns() -> u64 {
    unsafe { prisc_obs_sys::os_gettime_ns() }
}

/// The `now_ns` time of the video frame being rendered
/// (`obs_get_video_frame_time`). On the graphics thread, inside a tick
/// callback, that is the frame the tick is for.
pub fn video_frame_time_ns() -> u64 {
    unsafe { prisc_obs_sys::obs_get_video_frame_time() }
}

/// The duration of one video frame at the current output frame rate
/// (`obs_get_frame_interval_ns`), the grid libobs aligns pause and unpause
/// timestamps to.
pub fn frame_interval_ns() -> u64 {
    unsafe { prisc_obs_sys::obs_get_frame_interval_ns() }
}
