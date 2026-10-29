//! Capture and audio device lists (WP-AUDIO; README section 4.6).
//!
//! One export answers every kind on every OS: cameras through AVFoundation (macOS) and DirectShow
//! (Windows), audio endpoints through the CoreAudio HAL (macOS). Windows audio stays on NAudio this
//! phase (R15), so the audio kinds answer `PRISC_E_UNSUPPORTED` there.
//!
//! The ids are not display strings and not this library's invention: obs-express (prisc_recorder)
//! consumes them verbatim, so each OS arm reproduces what OBS's own enumerator produces
//! (`AVCaptureDevice.uniqueID`, the CoreAudio device UID, and win-dshow's
//! `encode(FriendlyName) + ":" + encode(DevicePath)`, see [`dshow_device_id`]).

use std::collections::HashMap;
use std::ptr;
use std::sync::{Mutex, OnceLock, PoisonError};

use crate::error::Error;
use crate::ffi::status::PRISC_OK;
use crate::ffi::types::PriscString;
use crate::panic;
use crate::util;

#[cfg(target_os = "macos")]
use crate::platform::macos::devices as os;
#[cfg(windows)]
use crate::platform::windows::devices as os;

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
    /// The id obs-express consumes verbatim, UTF-8, never empty: `AVCaptureDevice.uniqueID` or
    /// the CoreAudio device UID (macOS), win-dshow's encoded id (Windows cameras).
    pub id: PriscString,
    /// The display name, UTF-8. Cameras fall back to `id` when the OS gives none; an audio device
    /// keeps an empty name when its name property reads back empty, and gets its UID when the read
    /// fails.
    pub name: PriscString,
    /// A `PriscDeviceKind`.
    pub kind: i32,
    /// 1 for the system default input or output (audio kinds); 0 for cameras.
    pub is_default: u8,
    pub _reserved: [u8; 3],
    /// Audio inputs only: the channels across the device's input streams; 0 otherwise.
    pub input_channels: u32,
    pub _reserved2: u32,
}

/// One device as an OS arm finds it, before it is published as a `PriscDevice`.
#[allow(dead_code)] // built by the OS arms only
pub(crate) struct DeviceRecord {
    pub(crate) id: String,
    pub(crate) name: String,
    pub(crate) kind: PriscDeviceKind,
    pub(crate) is_default: bool,
    pub(crate) input_channels: u32,
}

/// win-dshow's `encode_dstr` (obs-studio plugins/win-dshow/encode-dstr.hpp). The order is
/// load-bearing: `#` is escaped first, so the `#` the `:` rule introduces is not escaped again.
/// Reversed, `"Cam: A#1"` would become `"Cam#223A A#221"`, an id no camera opens.
fn encode_dstr(value: &str) -> String {
    value.replace('#', "#22").replace(':', "#3A")
}

/// The camera id win-dshow expects back: the escaped friendly name, a colon, the escaped device
/// path. An empty path (some virtual devices) still produces the trailing colon. Cross-platform so
/// its vectors run on every host; only the Windows arm calls it.
#[allow(dead_code)]
pub(crate) fn dshow_device_id(name: &str, path: &str) -> String {
    format!("{}:{}", encode_dstr(name), encode_dstr(path))
}

/// The arrays handed out and not yet freed, by address, with their counts: a free of anything
/// else (a second free, a wrong count, a foreign pointer) is refused instead of being undefined.
fn live_arrays() -> &'static Mutex<HashMap<usize, usize>> {
    static LIVE: OnceLock<Mutex<HashMap<usize, usize>>> = OnceLock::new();
    LIVE.get_or_init(|| Mutex::new(HashMap::new()))
}

/// Publishes `records` as an owned array; an empty list leaves the out values at null and 0.
#[allow(dead_code)] // Linux has no OS arm
fn publish(records: Vec<DeviceRecord>, out_devices: &mut *mut PriscDevice, out_count: &mut usize) {
    if records.is_empty() {
        *out_devices = ptr::null_mut();
        *out_count = 0;
        return;
    }
    let devices: Box<[PriscDevice]> = records
        .into_iter()
        .map(|record| PriscDevice {
            id: PriscString::from_string(record.id),
            name: PriscString::from_string(record.name),
            kind: record.kind as i32,
            is_default: util::to_flag(record.is_default),
            _reserved: [0; 3],
            input_channels: record.input_channels,
            _reserved2: 0,
        })
        .collect();
    let count = devices.len();
    // A boxed slice's capacity is its length, which is the layout the free rebuilds.
    let pointer = Box::into_raw(devices).cast::<PriscDevice>();
    live_arrays()
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
        .insert(pointer as usize, count);
    *out_devices = pointer;
    *out_count = count;
}

/// Every device of `kind` (a `PriscDeviceKind`) as an owned array; free it with
/// `prisc_devices_free`. Both out values are zeroed first, so a failure leaves null and 0, and so
/// does a successful call that found nothing (an empty list is an answer, not a failure). Opens no
/// device, so it neither needs nor triggers a camera or microphone prompt and instantiates no
/// camera driver.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` (null out pointer, unknown kind); `PRISC_E_OS` (the
/// OS could not be asked; the HRESULT or OSStatus is the last-error code); `PRISC_E_UNSUPPORTED`
/// for the audio kinds on Windows (NAudio serves them) and for every kind off Windows and macOS.
///
/// Thread: any thread; the crate scopes its own COM apartment for the duration of the call.
#[no_mangle]
pub unsafe extern "C" fn prisc_devices_enumerate(kind: i32, out_devices: *mut *mut PriscDevice, out_count: *mut usize) -> i32 {
    panic::guard("prisc_devices_enumerate", || {
        let out_devices = util::out(out_devices, "out_devices")?;
        let out_count = util::out(out_count, "out_count")?;
        *out_devices = ptr::null_mut();
        *out_count = 0;
        let kind = PriscDeviceKind::from_raw(kind)?;
        #[cfg(any(target_os = "macos", windows))]
        {
            publish(os::enumerate(kind)?, out_devices, out_count);
            Ok(PRISC_OK)
        }
        #[cfg(not(any(target_os = "macos", windows)))]
        {
            Err(Error::unsupported(format!(
                "prisc_devices_enumerate lists no {kind:?} devices on this OS"
            )))
        }
    })
}

/// Frees an array from `prisc_devices_enumerate` with its strings; `count` must be the count it
/// wrote. Null is `PRISC_OK` (the empty list). A pointer that is not a live array from
/// `prisc_devices_enumerate` (already freed, or never handed out) or a wrong count is refused
/// with `PRISC_E_INVALID_ARG` and frees nothing, so a double free is harmless.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_devices_free(devices: *mut PriscDevice, count: usize) -> i32 {
    panic::guard("prisc_devices_free", || {
        if devices.is_null() {
            return Ok(PRISC_OK);
        }
        {
            let mut live = live_arrays()
                .lock()
                .unwrap_or_else(PoisonError::into_inner);
            match live.get(&(devices as usize)).copied() {
                Some(expected) if expected == count => {
                    live.remove(&(devices as usize));
                }
                Some(expected) => {
                    return Err(Error::invalid_arg(format!(
                        "prisc_devices_free was given count {count} for an array of {expected}"
                    )));
                }
                None => {
                    return Err(Error::invalid_arg(
                        "prisc_devices_free was given a pointer that is not a live device array (freed already?)",
                    ));
                }
            }
        }
        let mut array = Box::from_raw(ptr::slice_from_raw_parts_mut(devices, count));
        for device in array.iter_mut() {
            device.id.release();
            device.name.release();
        }
        drop(array);
        Ok(PRISC_OK)
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus;
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

    /// The reversed replacement order yields "Cam#223A A#221", a valid-looking id no camera opens.
    #[test]
    fn encode_dstr_escapes_hash_before_colon() {
        assert_eq!(encode_dstr("Cam: A#1"), "Cam#3A A#221");
        assert_eq!(encode_dstr("plain"), "plain");
        assert_eq!(encode_dstr("##::"), "#22#22#3A#3A");
    }

    #[test]
    fn dshow_ids_match_win_dshow_vectors() {
        let path = r"\\?\usb#vid_046d&pid_082d&mi_00#7&2a1f4f2&0&0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global";
        assert_eq!(
            dshow_device_id("Logitech C920", path),
            r"Logitech C920:\\?\usb#22vid_046d&pid_082d&mi_00#227&2a1f4f2&0&0000#22{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global"
        );
        assert_eq!(dshow_device_id("Virtual Cam", ""), "Virtual Cam:");
        assert_eq!(dshow_device_id("OBS: Cam", "a:b"), "OBS#3A Cam:a#3Ab");
    }

    fn record(id: &str, name: &str, is_default: bool, input_channels: u32) -> DeviceRecord {
        DeviceRecord {
            id: id.into(),
            name: name.into(),
            kind: PriscDeviceKind::AudioInput,
            is_default,
            input_channels,
        }
    }

    #[test]
    fn publish_then_free_round_trips_and_a_second_free_is_refused() {
        let mut devices = ptr::null_mut();
        let mut count = 0;
        publish(
            vec![record("uid-1", "Mic", true, 2), record("uid-2", "", false, 1)],
            &mut devices,
            &mut count,
        );
        assert_eq!(count, 2);
        let list = unsafe { std::slice::from_raw_parts(devices, count) };
        assert_eq!(unsafe { list[0].id.as_str() }, "uid-1");
        assert_eq!(unsafe { list[0].name.as_str() }, "Mic");
        assert_eq!(list[0].kind, PriscDeviceKind::AudioInput as i32);
        assert_eq!((list[0].is_default, list[1].is_default), (1, 0));
        assert_eq!((list[0].input_channels, list[1].input_channels), (2, 1));
        assert!(
            list[1].name.ptr.is_null() && list[1].name.len == 0,
            "an empty name allocates nothing"
        );

        let invalid = PriscInteropStatus::InvalidArg as i32;
        assert_eq!(unsafe { prisc_devices_free(devices, 1) }, invalid, "a wrong count must be refused");
        assert_eq!(unsafe { prisc_devices_free(devices, 2) }, PRISC_OK);
        assert_eq!(unsafe { prisc_devices_free(devices, 2) }, invalid, "a second free must be refused");
        let mut foreign = [0u8; 48];
        assert_eq!(unsafe { prisc_devices_free(foreign.as_mut_ptr().cast(), 1) }, invalid);
    }

    #[test]
    fn an_empty_list_is_null_and_zero_and_null_frees_cleanly() {
        let mut devices = ptr::dangling_mut::<PriscDevice>();
        let mut count = 7;
        publish(Vec::new(), &mut devices, &mut count);
        assert!(devices.is_null());
        assert_eq!(count, 0);
        assert_eq!(unsafe { prisc_devices_free(ptr::null_mut(), 0) }, PRISC_OK);
        assert_eq!(unsafe { prisc_devices_free(ptr::null_mut(), 3) }, PRISC_OK);
    }
}
