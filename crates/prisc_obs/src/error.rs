use std::fmt;

#[derive(Debug)]
pub enum ObsError {
    Startup,
    VideoReset(i32),
    AudioReset(bool),
    NullPointer(&'static str),
    OutputStart(String),
    AlreadyInitialized,
}

impl fmt::Display for ObsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ObsError::Startup => write!(f, "obs_startup failed"),
            ObsError::VideoReset(code) => write!(f, "obs_reset_video failed with code {code}"),
            ObsError::AudioReset(_) => write!(f, "obs_reset_audio failed"),
            ObsError::NullPointer(name) => write!(f, "OBS returned null pointer for {name}"),
            ObsError::OutputStart(msg) => write!(f, "obs_output_start failed: {msg}"),
            ObsError::AlreadyInitialized => write!(f, "OBS is already initialized"),
        }
    }
}

impl std::error::Error for ObsError {}
