//! Windows: system preferences (`prisc_system_animations_enabled`).

use windows::core::BOOL;
use windows::Win32::UI::WindowsAndMessaging::{SystemParametersInfoW, SPI_GETCLIENTAREAANIMATION, SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS};

/// Windows' "Animation effects" setting; a failed query reads as enabled.
pub(crate) fn animations_enabled() -> bool {
    let mut enabled = BOOL(1);
    let queried = unsafe {
        SystemParametersInfoW(
            SPI_GETCLIENTAREAANIMATION,
            0,
            Some((&mut enabled as *mut BOOL).cast()),
            SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
        )
    };
    queried.is_err() || enabled.as_bool()
}
