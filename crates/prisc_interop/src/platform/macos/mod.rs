//! macOS halves of the exports, one module per area.

pub(crate) mod app;
pub(crate) mod audio_input;
pub(crate) mod audio_output;
pub(crate) mod devices;
pub(crate) mod dylib;
pub(crate) mod gpu;
pub(crate) mod idle;
pub(crate) mod permissions;
pub(crate) mod pixels;
pub(crate) mod screens;
pub(crate) mod services;
pub(crate) mod system;
pub(crate) mod window;

use objc2::MainThreadMarker;

use crate::error::{Error, Result};

/// The main-thread marker, or `PRISC_E_STATE` naming the export when called from another thread
/// (README D5: `Thread: must be the macOS main thread.`).
#[allow(dead_code)] // the main-thread exports use it
pub(crate) fn main_thread(export: &str) -> Result<MainThreadMarker> {
    MainThreadMarker::new().ok_or_else(|| Error::state(format!("{export} must be called on the macOS main thread")))
}
