//! System preferences (README section 4.2).

use crate::error::Result;
use crate::ffi::status::PRISC_OK;
use crate::{panic, util};

/// Whether the user wants UI animations: Windows' "Animation effects"
/// (`SPI_GETCLIENTAREAANIMATION`; a failed query reads as enabled, as it did in C#), macOS'
/// inverse of Reduce Motion (`NSWorkspace.accessibilityDisplayShouldReduceMotion`). Always 1 on a
/// platform with neither setting. Writes 1 or 0 to `out_enabled`.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `out_enabled`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_system_animations_enabled(out_enabled: *mut u8) -> i32 {
    panic::guard("prisc_system_animations_enabled", || animations_enabled(out_enabled))
}

unsafe fn animations_enabled(out_enabled: *mut u8) -> Result {
    let out_enabled = util::out(out_enabled, "out_enabled")?;
    #[cfg(windows)]
    let enabled = crate::platform::windows::system::animations_enabled();
    #[cfg(target_os = "macos")]
    let enabled = crate::platform::macos::system::animations_enabled();
    #[cfg(not(any(windows, target_os = "macos")))]
    let enabled = true;
    *out_enabled = util::to_flag(enabled);
    Ok(PRISC_OK)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus;

    #[test]
    fn the_answer_is_a_flag() {
        let mut enabled = 7u8;
        assert_eq!(unsafe { prisc_system_animations_enabled(&mut enabled) }, PRISC_OK);
        assert!(enabled <= 1, "{enabled}");
        if !cfg!(any(windows, target_os = "macos")) {
            assert_eq!(enabled, 1);
        }
    }

    #[test]
    fn a_null_out_is_invalid_arg() {
        assert_eq!(
            unsafe { prisc_system_animations_enabled(std::ptr::null_mut()) },
            PriscInteropStatus::InvalidArg as i32
        );
    }
}
