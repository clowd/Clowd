//! macOS: the device lists of `prisc_devices_enumerate` (AVFoundation cameras, the CoreAudio HAL
//! for audio endpoints), the CoreAudio HAL reads the microphone input shares, the CoreAudio error
//! text and float32 stream format the audio output and input share, and the AVFoundation
//! authorization status.
//!
//! Every CoreAudio and AVFoundation type here comes from the objc2 framework crates; nothing is
//! redeclared by hand. That matters for `AudioBufferList`, whose inline `AudioBuffer` is
//! pointer-aligned, so `mBuffers[0]` starts four bytes past `mNumberBuffers` (the C# this replaces
//! had to spell those offsets itself).

use std::ffi::{c_void, CStr};
use std::mem;
use std::ptr::{self, NonNull};

use objc2::rc::autoreleasepool;
use objc2_av_foundation::{
    AVAuthorizationStatus, AVCaptureDevice, AVCaptureDeviceDiscoverySession, AVCaptureDevicePosition, AVCaptureDeviceType, AVMediaType,
    AVMediaTypeAudio, AVMediaTypeMuxed, AVMediaTypeVideo,
};
use objc2_core_audio::{
    kAudioDevicePropertyDeviceUID, kAudioDevicePropertyStreamConfiguration, kAudioHardwarePropertyDefaultInputDevice,
    kAudioHardwarePropertyDefaultOutputDevice, kAudioHardwarePropertyDevices, kAudioHardwarePropertyTranslateUIDToDevice,
    kAudioObjectPropertyElementMain, kAudioObjectPropertyName, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyScopeInput,
    kAudioObjectPropertyScopeOutput, kAudioObjectSystemObject, AudioObjectGetPropertyData, AudioObjectGetPropertyDataSize, AudioObjectID,
    AudioObjectPropertyAddress, AudioObjectPropertyScope, AudioObjectPropertySelector,
};
use objc2_core_audio_types::{
    kAudioFormatFlagIsFloat, kAudioFormatFlagIsPacked, kAudioFormatLinearPCM, AudioBuffer, AudioBufferList, AudioStreamBasicDescription,
};
use objc2_core_foundation::{CFRetained, CFString};
use objc2_foundation::{NSArray, NSString};

use crate::error::{Error, Result};
use crate::ffi::devices::{DeviceRecord, PriscDeviceKind};

/// The HAL's system object, the owner of the device list and the defaults.
pub(crate) const SYSTEM: AudioObjectID = kAudioObjectSystemObject as AudioObjectID;

/// `kAudioObjectUnknown`: what a HAL lookup answers for "no such device".
const UNKNOWN: AudioObjectID = 0;

/// The devices of `kind`.
pub(crate) fn enumerate(kind: PriscDeviceKind) -> Result<Vec<DeviceRecord>> {
    match kind {
        PriscDeviceKind::Video => Ok(video()),
        PriscDeviceKind::AudioInput => audio(false),
        PriscDeviceKind::AudioOutput => audio(true),
    }
}

// ------------------------------------------------------------------------------------- cameras

/// The cameras obs-studio's mac-avcapture would offer, in its order (plugin-properties.m): its
/// device-type list, a video pass then a muxed pass (a camera with its own audio appears only in
/// the muxed one), deduplicated by `uniqueID` (the plugin only looks for a match; a picker must
/// not list a camera twice). The name falls back to the id.
fn video() -> Vec<DeviceRecord> {
    // Required: this runs on .NET thread-pool threads, which have no autorelease pool, so without
    // one the sessions, arrays and strings leak.
    autoreleasepool(|_| {
        let types = camera_device_types();
        let types = NSArray::from_slice(&types);
        let mut found = Vec::new();
        // SAFETY: AVFoundation's own constants; they exist on every macOS this app runs on.
        for media in unsafe { [AVMediaTypeVideo, AVMediaTypeMuxed] } {
            // A missing media type would mean "any media type" to the session, microphones
            // included, so it skips that pass instead.
            let Some(media) = media else {
                continue;
            };
            // SAFETY: the types are AVFoundation's device-type constants and the position is a
            // documented value; everything returned is autoreleased into the pool above.
            let session = unsafe {
                AVCaptureDeviceDiscoverySession::discoverySessionWithDeviceTypes_mediaType_position(
                    &types,
                    Some(media),
                    AVCaptureDevicePosition::Unspecified,
                )
            };
            for device in unsafe { session.devices() }.iter() {
                let id = unsafe { device.uniqueID() }.to_string();
                if id.is_empty()
                    || found
                        .iter()
                        .any(|known: &DeviceRecord| known.id == id)
                {
                    continue;
                }
                let name = unsafe { device.localizedName() }.to_string();
                found.push(DeviceRecord {
                    name: if name.is_empty() { id.clone() } else { name },
                    id,
                    kind: PriscDeviceKind::Video,
                    is_default: false,
                    input_channels: 0,
                });
            }
        }
        found
    })
}

/// mac-avcapture's device types, in its order. `AVCaptureDeviceTypeDeskViewCamera` is macOS 13
/// and later while the app runs on 12, so it is looked up at run time: a typed reference to an
/// absent data symbol would make the whole library fail to load. The C# this replaces looked the
/// constants up with an export lookup that throws, so on a macOS without the symbol it lost the
/// whole native enumeration and fell back to the slow recorder listing.
fn camera_device_types() -> Vec<&'static AVCaptureDeviceType> {
    #[allow(deprecated)] // the plugin keeps the older name of AVCaptureDeviceTypeExternal
    // SAFETY: AVFoundation constants present on every supported macOS.
    let mut types = unsafe {
        vec![
            objc2_av_foundation::AVCaptureDeviceTypeBuiltInWideAngleCamera,
            objc2_av_foundation::AVCaptureDeviceTypeExternalUnknown,
        ]
    };
    if let Some(desk_view) = string_constant(c"AVCaptureDeviceTypeDeskViewCamera") {
        types.push(desk_view);
    }
    types
}

/// An exported `NSString *const` of a loaded framework, or `None` when this macOS does not export
/// it.
fn string_constant(symbol: &CStr) -> Option<&'static NSString> {
    // SAFETY: dlsym with RTLD_DEFAULT only looks the name up. AVFoundation is linked (the typed
    // constants above reference it), so its globals are visible.
    let address = unsafe { libc::dlsym(libc::RTLD_DEFAULT, symbol.as_ptr()) }.cast::<*const NSString>();
    if address.is_null() {
        return None;
    }
    // SAFETY: the symbol is an `NSString *const` global of a framework that is never unloaded, so
    // both the global and the constant string it points at live for the process.
    unsafe { (*address).as_ref() }
}

/// What AVFoundation has stored about this process's access to the microphone (`audio`) or the
/// camera. Never prompts. The one reader of that status: the microphone open checks it, and the
/// permission exports can read it from here too.
#[allow(dead_code)] // the camera half has no caller in this area
pub(crate) fn authorization(audio: bool) -> AVAuthorizationStatus {
    // SAFETY: AVMediaTypeAudio / AVMediaTypeVideo are AVFoundation constants; the class method
    // accepts exactly those two.
    let media: Option<&AVMediaType> = unsafe {
        if audio {
            AVMediaTypeAudio
        } else {
            AVMediaTypeVideo
        }
    };
    match media {
        Some(media) => unsafe { AVCaptureDevice::authorizationStatusForMediaType(media) },
        None => AVAuthorizationStatus::NotDetermined,
    }
}

// ---------------------------------------------------------------------------- CoreAudio HAL

/// `PRISC_E_OS` for a failed CoreAudio or AudioToolbox call, worded as the C# this replaces threw
/// it: "CoreAudio error {status} while {what}." The logged teardown failures use the same text.
pub(crate) fn coreaudio_error(status: i32, what: &str) -> Error {
    Error::os(i64::from(status), format!("CoreAudio error {status} while {what}."))
}

/// Interleaved packed float32 linear PCM (no NonInterleaved flag, so one buffer carries every
/// channel): the format the output unit is fed and the input queue delivers. `channels` is at
/// least 1 and small enough that the byte sizes fit, which both callers bound.
pub(crate) fn float32_interleaved(sample_rate_hz: u32, channels: u32) -> AudioStreamBasicDescription {
    let bytes_per_frame = channels * mem::size_of::<f32>() as u32;
    AudioStreamBasicDescription {
        mSampleRate: f64::from(sample_rate_hz),
        mFormatID: kAudioFormatLinearPCM,
        mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
        mBytesPerPacket: bytes_per_frame,
        mFramesPerPacket: 1,
        mBytesPerFrame: bytes_per_frame,
        mChannelsPerFrame: channels,
        mBitsPerChannel: 32,
        mReserved: 0,
    }
}

/// The main element of `selector` in `scope`.
pub(crate) fn address(selector: AudioObjectPropertySelector, scope: AudioObjectPropertyScope) -> AudioObjectPropertyAddress {
    AudioObjectPropertyAddress {
        mSelector: selector,
        mScope: scope,
        mElement: kAudioObjectPropertyElementMain,
    }
}

/// A `u32` property (global scope unless `scope` says otherwise), or `None` when the object will
/// not answer, which for a device usually means it has just gone away.
pub(crate) fn u32_property(object: AudioObjectID, selector: AudioObjectPropertySelector, scope: AudioObjectPropertyScope) -> Option<u32> {
    let mut address = address(selector, scope);
    let mut value: u32 = 0;
    let mut size = mem::size_of::<u32>() as u32;
    // SAFETY: the out buffer is a u32 and `size` says so.
    let status = unsafe {
        AudioObjectGetPropertyData(
            object,
            NonNull::from(&mut address),
            0,
            ptr::null(),
            NonNull::from(&mut size),
            NonNull::from(&mut value).cast(),
        )
    };
    (status == 0).then_some(value)
}

/// A `CFStringRef` property as an owned string, or `None` when the read failed. The string the HAL
/// returns is +1 retained, so it is adopted and released on every path.
pub(crate) fn string_property(object: AudioObjectID, selector: AudioObjectPropertySelector) -> Option<String> {
    let mut address = address(selector, kAudioObjectPropertyScopeGlobal);
    let mut value: *const CFString = ptr::null();
    let mut size = mem::size_of::<*const CFString>() as u32;
    // SAFETY: the out buffer is one CFStringRef and `size` says so.
    let status = unsafe {
        AudioObjectGetPropertyData(
            object,
            NonNull::from(&mut address),
            0,
            ptr::null(),
            NonNull::from(&mut size),
            NonNull::from(&mut value).cast(),
        )
    };
    let value = NonNull::new(value.cast_mut()).filter(|_| status == 0)?;
    // SAFETY: a CoreAudio string property read hands over one reference (the create rule).
    let value = unsafe { CFRetained::from_raw(value) };
    Some(value.to_string())
}

/// The device's stream configuration in `scope` as (buffer count, channels across the buffers),
/// or `None` when it cannot be read.
fn stream_configuration(device: AudioObjectID, scope: AudioObjectPropertyScope) -> Option<(u32, u32)> {
    let mut address = address(kAudioDevicePropertyStreamConfiguration, scope);
    let mut size: u32 = 0;
    // SAFETY: a size query writes one u32.
    let status = unsafe { AudioObjectGetPropertyDataSize(device, NonNull::from(&mut address), 0, ptr::null(), NonNull::from(&mut size)) };
    if status != 0 || (size as usize) < mem::size_of::<u32>() {
        return None;
    }
    // An AudioBufferList holds pointers, so it needs 8-byte alignment a Vec<u8> does not promise;
    // a Vec<u64> does, and rounding up only over-allocates.
    let mut storage = vec![0u64; (size as usize).div_ceil(mem::size_of::<u64>())];
    let list = storage
        .as_mut_ptr()
        .cast::<AudioBufferList>();
    // SAFETY: the buffer holds at least `size` bytes, suitably aligned.
    let status = unsafe {
        AudioObjectGetPropertyData(
            device,
            NonNull::from(&mut address),
            0,
            ptr::null(),
            NonNull::from(&mut size),
            NonNull::new_unchecked(list.cast::<c_void>()),
        )
    };
    if status != 0 {
        return None;
    }
    // SAFETY: the HAL wrote an AudioBufferList of `size` bytes; every AudioBuffer read below is
    // checked to lie inside it.
    unsafe {
        let buffers = (*list).mNumberBuffers;
        let first = ptr::addr_of!((*list).mBuffers).cast::<AudioBuffer>();
        let mut channels: u32 = 0;
        for index in 0..buffers as usize {
            let buffer = first.add(index);
            let end = (buffer as usize - list as usize) + mem::size_of::<AudioBuffer>();
            if end > size as usize {
                break;
            }
            channels = channels.saturating_add((*buffer).mNumberChannels);
        }
        Some((buffers, channels))
    }
}

/// The channels across the device's input streams; 0 when it has none or the configuration could
/// not be read.
pub(crate) fn input_channels(device: AudioObjectID) -> u32 {
    stream_configuration(device, kAudioObjectPropertyScopeInput).map_or(0, |(_, channels)| channels)
}

/// The system default input (or output) device, or `None` when there is none.
pub(crate) fn default_device(output: bool) -> Option<AudioObjectID> {
    let selector = if output {
        kAudioHardwarePropertyDefaultOutputDevice
    } else {
        kAudioHardwarePropertyDefaultInputDevice
    };
    u32_property(SYSTEM, selector, kAudioObjectPropertyScopeGlobal).filter(|&device| device != UNKNOWN)
}

/// The device behind a UID from the list, or `None` when no such device is connected.
pub(crate) fn device_for_uid(uid: &str) -> Option<AudioObjectID> {
    if uid.is_empty() {
        return None;
    }
    let uid = CFString::from_str(uid);
    // The qualifier is the CFStringRef itself, passed by address.
    let qualifier: *const CFString = &*uid;
    let mut address = address(kAudioHardwarePropertyTranslateUIDToDevice, kAudioObjectPropertyScopeGlobal);
    let mut device: AudioObjectID = UNKNOWN;
    let mut size = mem::size_of::<AudioObjectID>() as u32;
    // SAFETY: the qualifier is one CFStringRef that `uid` keeps alive; the out buffer is one id.
    let status = unsafe {
        AudioObjectGetPropertyData(
            SYSTEM,
            NonNull::from(&mut address),
            mem::size_of::<*const CFString>() as u32,
            ptr::addr_of!(qualifier).cast(),
            NonNull::from(&mut size),
            NonNull::from(&mut device).cast(),
        )
    };
    (status == 0 && device != UNKNOWN).then_some(device)
}

/// Every device id the HAL knows, or `PRISC_E_OS` when the list cannot be read.
fn device_ids() -> Result<Vec<AudioObjectID>> {
    let mut address = address(kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal);
    let mut size: u32 = 0;
    // SAFETY: a size query writes one u32.
    let status = unsafe { AudioObjectGetPropertyDataSize(SYSTEM, NonNull::from(&mut address), 0, ptr::null(), NonNull::from(&mut size)) };
    if status != 0 {
        return Err(coreaudio_error(status, "sizing the device list"));
    }
    let mut devices = vec![UNKNOWN; size as usize / mem::size_of::<AudioObjectID>()];
    if devices.is_empty() {
        return Ok(devices);
    }
    // SAFETY: the buffer holds `size` bytes of ids.
    let status = unsafe {
        AudioObjectGetPropertyData(
            SYSTEM,
            NonNull::from(&mut address),
            0,
            ptr::null(),
            NonNull::from(&mut size),
            NonNull::new_unchecked(devices.as_mut_ptr().cast()),
        )
    };
    if status != 0 {
        return Err(coreaudio_error(status, "reading the device list"));
    }
    // What the read reported, not what the size query promised: a device unplugged in between
    // would otherwise leave stale ids in the tail.
    devices.truncate(size as usize / mem::size_of::<AudioObjectID>());
    Ok(devices)
}

/// The input (or output) devices, as OBS's mac enumerator (audio-device-enum.c) sees them: a device
/// is kept when its stream configuration in that scope has at least one buffer and its UID reads
/// back non-empty (the UID is what the recorder's coreaudio sources take as `device_id`). A device
/// that will not answer is skipped, never fatal. The name falls back to the UID only when the read
/// fails.
fn audio(output: bool) -> Result<Vec<DeviceRecord>> {
    let scope = if output {
        kAudioObjectPropertyScopeOutput
    } else {
        kAudioObjectPropertyScopeInput
    };
    let default = default_device(output);
    let mut found = Vec::new();
    for device in device_ids()? {
        let Some((buffers, channels)) = stream_configuration(device, scope) else {
            continue;
        };
        if buffers == 0 {
            continue;
        }
        let Some(uid) = string_property(device, kAudioDevicePropertyDeviceUID).filter(|uid| !uid.is_empty()) else {
            continue;
        };
        let name = string_property(device, kAudioObjectPropertyName).unwrap_or_else(|| uid.clone());
        found.push(DeviceRecord {
            id: uid,
            name,
            kind: if output {
                PriscDeviceKind::AudioOutput
            } else {
                PriscDeviceKind::AudioInput
            },
            is_default: default == Some(device),
            input_channels: if output { 0 } else { channels },
        });
    }
    Ok(found)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_old_camera_types_resolve_and_desk_view_is_looked_up_at_run_time() {
        let types = camera_device_types();
        assert!(types.len() >= 2);
        // This Mac runs a macOS newer than 13, so the run-time lookup finds the third type.
        assert!(string_constant(c"AVCaptureDeviceTypeDeskViewCamera").is_some());
        assert!(string_constant(c"PriscNoSuchConstant").is_none());
    }

    #[test]
    fn audio_lists_are_self_consistent() {
        for output in [false, true] {
            let devices = audio(output).expect("the HAL device list");
            let defaults = devices
                .iter()
                .filter(|d| d.is_default)
                .count();
            assert!(defaults <= 1, "more than one default device");
            for device in &devices {
                assert!(!device.id.is_empty());
                assert!(device_for_uid(&device.id).is_some(), "{} does not translate back", device.id);
                if output {
                    assert_eq!(device.input_channels, 0);
                } else {
                    assert!(device.input_channels > 0, "{} has input buffers but no channels", device.id);
                }
            }
            let ids: std::collections::HashSet<_> = devices
                .iter()
                .map(|d| d.id.as_str())
                .collect();
            assert_eq!(ids.len(), devices.len(), "duplicate UIDs");
        }
    }

    #[test]
    fn an_unknown_uid_translates_to_nothing() {
        assert_eq!(device_for_uid("prisc-no-such-device-uid"), None);
        assert_eq!(device_for_uid(""), None);
    }
}
