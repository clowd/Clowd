//! Physical key state and Secure Event Input (WP-KEYS; README section 4.9), read through
//! prisc_platform::input, the one reader every crate shares.

use crate::error::{Error, Result};
use crate::ffi::status::PRISC_OK;
use crate::{panic, util};

/// The most codes one `prisc_keyboard_keys_down` call answers: one bit each in a u64.
const MAX_CODES: usize = 64;

/// Refuses a `count` above [`MAX_CODES`]. The export checks it before it builds the slice, so a
/// caller's oversized count never becomes a slice longer than its buffer.
fn check_count(count: usize) -> Result<()> {
    if count > MAX_CODES {
        return Err(Error::invalid_arg(format!("{count} codes asked at once; at most {MAX_CODES}")));
    }
    Ok(())
}

/// The down mask for `codes` (at most [`MAX_CODES`], checked by the caller): bit i is set when
/// `codes[i]` is down, or when the OS cannot say.
fn keys_down(codes: &[u32]) -> u64 {
    debug_assert!(codes.len() <= MAX_CODES);
    codes
        .iter()
        .enumerate()
        .filter(|&(_, &code)| prisc_platform::input::is_key_down(code).unwrap_or(true))
        .fold(0u64, |mask, (i, _)| mask | 1 << i)
}

/// Bit i of `out_mask` = whether `codes[i]` is physically down right now: a Windows virtual-key
/// code (mouse-button VKs included, `GetAsyncKeyState`) or a macOS CGKeyCode
/// (`CGEventSourceKeyState`, combined session state), the numbering the hotkey hook reports as
/// `raw_code`. At most 64 codes, so a caller asks once for every key it is unsure of. A code the
/// OS cannot answer for (out of its range, or every code on an OS without a backend: Linux) reads
/// as down, the reading that never releases a key without evidence. `count` 0 writes 0.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for more than 64 codes or a null pointer. A count
/// above 64 is refused before `codes` is read, whatever the buffer's length.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_keyboard_keys_down(codes: *const u32, count: usize, out_mask: *mut u64) -> i32 {
    panic::guard("prisc_keyboard_keys_down", || {
        let out_mask = util::out(out_mask, "out_mask")?;
        check_count(count)?;
        let codes = util::slice(codes, count, "codes")?;
        *out_mask = keys_down(codes);
        Ok(PRISC_OK)
    })
}

/// Whether macOS Secure Event Input is on now (a focused password field, a terminal's "Secure
/// Keyboard Entry", in any app): while it is, the OS hides every key from event taps, the hotkey
/// hook's included. 0 off macOS, which is the truthful answer there.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null pointer.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_keyboard_secure_input_enabled(out_enabled: *mut u8) -> i32 {
    panic::guard("prisc_keyboard_secure_input_enabled", || {
        util::write(
            out_enabled,
            "out_enabled",
            util::to_flag(prisc_platform::input::secure_input_enabled()),
        )?;
        Ok(PRISC_OK)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn masks_one_bit_per_code_and_refuse_more_than_64() {
        assert_eq!(keys_down(&[]), 0);
        // Codes no OS numbering has: unknown everywhere, so they read as down.
        let unknown = [0x1_0000u32, 0x2_0000, 0xFFFF_FFFF];
        assert_eq!(keys_down(&unknown), 0b111);
        assert_eq!(keys_down(&[0x1_0000; 64]), u64::MAX);
        assert!(check_count(64).is_ok());
        assert!(check_count(65).is_err());
        assert!(check_count(usize::MAX).is_err());
    }
}
