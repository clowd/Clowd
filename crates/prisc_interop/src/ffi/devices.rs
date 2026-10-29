//! Capture and audio device lists (WP-AUDIO; README section 4.6).

use crate::error::Error;
use crate::ffi::types::PriscString;
use crate::panic;

/// Which devices `prisc_devices_enumerate` lists.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscDeviceKind {
    Video = 0,
    AudioInput = 1,
    AudioOutput = 2,
}

crate::convert::ffi_enum!(PriscDeviceKind: i32 { Video, AudioInput, AudioOutput });

/// One device (48 bytes). `id` and `name` are mode 2: owned by the array, freed by
/// `prisc_devices_free`.
#[repr(C)]
#[derive(Debug)]
pub struct PriscDevice {
    pub id: PriscString,
    pub name: PriscString,
    /// A `PriscDeviceKind`.
    pub kind: i32,
    pub is_default: u8,
    pub _reserved: [u8; 3],
    /// Audio inputs only; 0 otherwise.
    pub input_channels: u32,
    pub _reserved2: u32,
}

/// Every device of `kind` (a `PriscDeviceKind`) as an owned array; free it with
/// `prisc_devices_free`. Audio kinds answer `PRISC_E_UNSUPPORTED` on Windows (NAudio serves them).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED`.
///
/// Thread: any thread; the crate scopes its own COM apartment for the duration of the call.
#[no_mangle]
pub unsafe extern "C" fn prisc_devices_enumerate(kind: i32, out_devices: *mut *mut PriscDevice, out_count: *mut usize) -> i32 {
    panic::guard("prisc_devices_enumerate", || {
        let _ = (kind, out_devices, out_count);
        Err(Error::unsupported("prisc_devices_enumerate is not implemented yet"))
    })
}

/// Frees an array from `prisc_devices_enumerate` with its strings; null is `PRISC_OK`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_devices_free(devices: *mut PriscDevice, count: usize) -> i32 {
    panic::guard("prisc_devices_free", || {
        let _ = (devices, count);
        Err(Error::unsupported("prisc_devices_free is not implemented yet"))
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    #[test]
    fn layouts_are_pinned() {
        assert_eq!(size_of::<PriscDevice>(), 48);
        assert_eq!(offset_of!(PriscDevice, id), 0);
        assert_eq!(offset_of!(PriscDevice, name), 16);
        assert_eq!(offset_of!(PriscDevice, kind), 32);
        assert_eq!(offset_of!(PriscDevice, is_default), 36);
        assert_eq!(offset_of!(PriscDevice, _reserved), 37);
        assert_eq!(offset_of!(PriscDevice, input_channels), 40);
        assert_eq!(offset_of!(PriscDevice, _reserved2), 44);
    }
}
