//! Windows: the system idle time (`prisc_idle_seconds`).

use std::mem;

use windows::Win32::System::SystemInformation::GetTickCount;
use windows::Win32::UI::Input::KeyboardAndMouse::{GetLastInputInfo, LASTINPUTINFO};

use crate::error::{Error, Result};
use crate::ffi::idle::elapsed_ms;

/// Seconds since the last input, from `GetLastInputInfo` against `GetTickCount`.
pub(crate) fn idle_seconds() -> Result<Option<f64>> {
    let mut info = LASTINPUTINFO {
        cbSize: mem::size_of::<LASTINPUTINFO>() as u32,
        dwTime: 0,
    };
    if !unsafe { GetLastInputInfo(&mut info) }.as_bool() {
        let error = windows_core::Error::from_thread();
        return Err(Error::os(
            i64::from(error.code().0),
            format!("GetLastInputInfo failed: {}", error.message()),
        ));
    }
    let now = unsafe { GetTickCount() };
    Ok(Some(f64::from(elapsed_ms(now, info.dwTime)) / 1000.0))
}
