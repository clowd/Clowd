//! Export-level tests of the devices and audio area (README section 4.6): argument checks, the
//! device lists through the public surface, the output and input lifecycles on macOS (skipping
//! where the machine has no device or no microphone grant), and the Windows answers.

use std::ffi::c_void;
use std::ptr;
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::thread::sleep;
use std::time::{Duration, Instant};

use prisc_interop::ffi::audio_input::*;
use prisc_interop::ffi::audio_output::*;
use prisc_interop::ffi::devices::*;
use prisc_interop::ffi::error::prisc_last_error_message;
use prisc_interop::ffi::status::PriscInteropStatus as S;
use prisc_interop::ffi::types::PriscString;

fn last_message() -> String {
    let len = unsafe { prisc_last_error_message(ptr::null_mut(), 0) };
    let mut buf = vec![0u8; len];
    assert_eq!(unsafe { prisc_last_error_message(buf.as_mut_ptr(), buf.len()) }, len);
    String::from_utf8(buf).unwrap()
}

fn text(s: &PriscString) -> String {
    if s.ptr.is_null() {
        return String::new();
    }
    String::from_utf8(unsafe { std::slice::from_raw_parts(s.ptr, s.len) }.to_vec()).unwrap()
}

/// One listed device, copied out: (id, name, kind, is_default, input_channels).
type Listed = (String, String, i32, u8, u32);

/// Enumerates `kind` through the exports and copies the list out, or the failing status.
fn enumerate(kind: PriscDeviceKind) -> Result<Vec<Listed>, i32> {
    let mut devices = ptr::dangling_mut::<PriscDevice>();
    let mut count = 99usize;
    let status = unsafe { prisc_devices_enumerate(kind as i32, &mut devices, &mut count) };
    if status != S::Ok as i32 {
        assert!(devices.is_null() && count == 0, "a failure must leave null and 0");
        return Err(status);
    }
    if count == 0 {
        assert!(devices.is_null(), "an empty list is a null pointer");
        return Ok(Vec::new());
    }
    let list = unsafe { std::slice::from_raw_parts(devices, count) }
        .iter()
        .map(|d| (text(&d.id), text(&d.name), d.kind, d.is_default, d.input_channels))
        .collect();
    assert_eq!(unsafe { prisc_devices_free(devices, count) }, S::Ok as i32);
    assert_eq!(
        unsafe { prisc_devices_free(devices, count) },
        S::InvalidArg as i32,
        "double free refused"
    );
    Ok(list)
}

#[test]
fn enumerate_checks_its_arguments() {
    let mut devices = ptr::null_mut();
    let mut count = 0usize;
    assert_eq!(
        unsafe { prisc_devices_enumerate(PriscDeviceKind::Video as i32, ptr::null_mut(), &mut count) },
        S::InvalidArg as i32
    );
    assert_eq!(last_message(), "out_devices was null");
    assert_eq!(
        unsafe { prisc_devices_enumerate(PriscDeviceKind::Video as i32, &mut devices, ptr::null_mut()) },
        S::InvalidArg as i32
    );
    for kind in [-1, 3, 99] {
        let mut devices = ptr::dangling_mut::<PriscDevice>();
        let mut count = 5usize;
        assert_eq!(
            unsafe { prisc_devices_enumerate(kind, &mut devices, &mut count) },
            S::InvalidArg as i32
        );
        assert!(devices.is_null() && count == 0, "zeroed before the kind check");
        assert!(last_message().contains("PriscDeviceKind"), "{}", last_message());
    }
    assert_eq!(unsafe { prisc_devices_free(ptr::null_mut(), 0) }, S::Ok as i32);
}

#[test]
fn every_kind_answers_truthfully_on_this_os() {
    for kind in [PriscDeviceKind::Video, PriscDeviceKind::AudioInput, PriscDeviceKind::AudioOutput] {
        let result = enumerate(kind);
        if cfg!(target_os = "macos") || (cfg!(windows) && kind == PriscDeviceKind::Video) {
            let list = result.unwrap_or_else(|status| panic!("{kind:?}: status {status}: {}", last_message()));
            let mut ids: Vec<&str> = list.iter().map(|d| d.0.as_str()).collect();
            for device in &list {
                assert!(!device.0.is_empty(), "{kind:?}: an empty id");
                assert_eq!(device.2, kind as i32);
                if kind == PriscDeviceKind::Video {
                    assert!(!device.1.is_empty(), "a camera always has a name (its id at worst)");
                    assert_eq!((device.3, device.4), (0, 0));
                }
                if kind == PriscDeviceKind::AudioOutput {
                    assert_eq!(device.4, 0, "outputs carry no input channels");
                }
            }
            assert!(list.iter().filter(|d| d.3 != 0).count() <= 1);
            ids.sort_unstable();
            ids.dedup();
            assert_eq!(ids.len(), list.len(), "{kind:?}: duplicate ids");
        } else {
            assert_eq!(result.err(), Some(S::Unsupported as i32), "{kind:?}");
        }
    }
}

// ------------------------------------------------------------------------------------- output

unsafe extern "C" fn count_samples(user: *mut c_void, samples: *mut f32, frames: u32, channels: u32) {
    let n = frames as usize * channels as usize;
    ptr::write_bytes(samples, 0, n);
    (*user.cast::<AtomicU64>()).fetch_add(n as u64, Ordering::Relaxed);
}

#[test]
fn output_create_checks_its_arguments() {
    let mut output = ptr::null_mut();
    let mut info = PriscAudioOutputInfo::default();
    let render: PriscAudioRenderCallback = Some(count_samples);
    let cases = [(0, 2, render), (48_000, 0, render), (48_000, 4097, render), (48_000, 2, None)];
    for (rate, channels, render) in cases {
        let status = unsafe { prisc_audio_output_create(rate, channels, render, ptr::null_mut(), &mut output, &mut info) };
        assert_eq!(status, S::InvalidArg as i32, "rate {rate} channels {channels}");
        assert!(output.is_null());
    }
    let status = unsafe { prisc_audio_output_create(48_000, 2, render, ptr::null_mut(), ptr::null_mut(), &mut info) };
    assert_eq!(status, S::InvalidArg as i32);
    assert_eq!(unsafe { prisc_audio_output_destroy(ptr::null_mut()) }, S::Ok as i32);
    assert_eq!(
        unsafe { prisc_audio_output_start(ptr::null_mut()) },
        if cfg!(target_os = "macos") { S::InvalidArg } else { S::Unsupported } as i32
    );
}

fn wait(condition: impl Fn() -> bool) -> bool {
    let deadline = Instant::now() + Duration::from_secs(3);
    while Instant::now() < deadline {
        if condition() {
            return true;
        }
        // A coarse latch on a test thread, not a precise wait.
        sleep(Duration::from_millis(10));
    }
    false
}

#[test]
fn output_pulls_the_callback_and_stops_on_destroy() {
    let pulled: &'static AtomicU64 = Box::leak(Box::new(AtomicU64::new(0)));
    let mut output = ptr::null_mut();
    let mut info = PriscAudioOutputInfo::default();
    let status = unsafe {
        prisc_audio_output_create(
            48_000,
            2,
            Some(count_samples),
            ptr::from_ref(pulled).cast_mut().cast(),
            &mut output,
            &mut info,
        )
    };
    if !cfg!(target_os = "macos") {
        assert_eq!(status, S::Unsupported as i32);
        return;
    }
    if status != S::Ok as i32 {
        eprintln!("skipped: no usable output ({status}: {})", last_message());
        return;
    }
    assert!(info.sample_rate_hz > 0);
    // Under a second of buffering on any sane device (0 = the device publishes none).
    assert!(info.latency_frames < info.sample_rate_hz, "implausible latency {info:?}");
    unsafe {
        if prisc_audio_output_start(output) != S::Ok as i32 {
            eprintln!("skipped: the output would not start: {}", last_message());
            assert_eq!(prisc_audio_output_destroy(output), S::Ok as i32);
            return;
        }
        assert!(wait(|| pulled.load(Ordering::Relaxed) > 0), "never pulled");
        assert_eq!(prisc_audio_output_stop(output), S::Ok as i32);
        assert_eq!(prisc_audio_output_stop(output), S::Ok as i32, "stop is idempotent");
        assert_eq!(prisc_audio_output_start(output), S::Ok as i32);
        assert_eq!(prisc_audio_output_destroy(output), S::Ok as i32);
    }
    sleep(Duration::from_millis(200));
    let latched = pulled.load(Ordering::Relaxed);
    sleep(Duration::from_millis(300));
    assert_eq!(pulled.load(Ordering::Relaxed), latched, "the callback outlived destroy");
}

// -------------------------------------------------------------------------------------- input

#[derive(Default)]
struct Seen {
    data: AtomicU32,
    stopped: AtomicU32,
}

unsafe extern "C" fn record(user: *mut c_void, event: *const PriscAudioInputEvent) -> u8 {
    let seen = &*user.cast::<Seen>();
    if (*event).kind == PriscAudioInputEventKind::Data as u32 {
        assert!(!(*event).data.is_null() && (*event).data_len > 0);
        seen.data.fetch_add(1, Ordering::AcqRel);
        1
    } else {
        seen.stopped.fetch_add(1, Ordering::AcqRel);
        0
    }
}

fn empty_info() -> PriscAudioInputInfo {
    PriscAudioInputInfo {
        channels: 0,
        sample_rate_hz: 0,
        used_default: 9,
        _reserved: [0; 7],
        name: PriscString {
            ptr: ptr::null_mut(),
            len: 0,
        },
    }
}

#[test]
fn input_open_checks_its_arguments() {
    let mut input = ptr::null_mut();
    let mut info = empty_info();
    let bad = [0xFFu8, 0xFE];
    unsafe {
        let status = prisc_audio_input_open(
            bad.as_ptr(),
            bad.len(),
            48_000,
            50,
            Some(record),
            ptr::null_mut(),
            &mut input,
            &mut info,
        );
        assert_eq!(status, S::InvalidArg as i32);
        assert!(last_message().starts_with("device_uid is not UTF-8"), "{}", last_message());
        let status = prisc_audio_input_open(ptr::null(), 0, 48_000, 50, None, ptr::null_mut(), &mut input, &mut info);
        assert_eq!(status, S::InvalidArg as i32);
        let status = prisc_audio_input_open(ptr::null(), 0, 0, 50, Some(record), ptr::null_mut(), &mut input, &mut info);
        assert_eq!(status, S::InvalidArg as i32);
        let status = prisc_audio_input_open(
            ptr::null(),
            0,
            48_000,
            50,
            Some(record),
            ptr::null_mut(),
            ptr::null_mut(),
            &mut info,
        );
        assert_eq!(status, S::InvalidArg as i32);
        assert!(input.is_null());
        assert_eq!(prisc_audio_input_destroy(ptr::null_mut()), S::Ok as i32);
        let expected = if cfg!(target_os = "macos") { S::InvalidArg } else { S::Unsupported } as i32;
        assert_eq!(prisc_audio_input_start(ptr::null_mut(), ptr::null_mut()), expected);
        assert_eq!(prisc_audio_input_request_stop(ptr::null_mut(), ptr::null_mut()), expected);
    }
}

/// Opens the default input and checks the first DATA arrives; `request_stop` reports `out_first`
/// once. Skips where the machine has no input; a refused microphone must answer
/// `PRISC_E_PERMISSION` (with the reason in the last error).
#[test]
fn input_delivers_data_and_request_stop_reports_first_once() {
    let seen: &'static Seen = Box::leak(Box::default());
    let mut input = ptr::null_mut();
    let mut info = empty_info();
    let status = unsafe {
        prisc_audio_input_open(
            ptr::null(),
            0,
            48_000,
            50,
            Some(record),
            ptr::from_ref(seen).cast_mut().cast(),
            &mut input,
            &mut info,
        )
    };
    if !cfg!(target_os = "macos") {
        assert_eq!(status, S::Unsupported as i32);
        return;
    }
    if status == S::Permission as i32 {
        assert!(last_message().contains("Microphone access is denied"), "{}", last_message());
        eprintln!("skipped: no microphone grant");
        return;
    }
    if status == S::Capability as i32 {
        eprintln!("skipped: {}", last_message());
        return;
    }
    assert_eq!(status, S::Ok as i32, "{}", last_message());
    assert_eq!(info.used_default, 1);
    assert_eq!(info.sample_rate_hz, 48_000);
    assert!(info.channels >= 1);
    assert!(!text(&info.name).is_empty());
    unsafe {
        let mut watching = 7u8;
        assert_eq!(prisc_audio_input_start(input, &mut watching), S::Ok as i32, "{}", last_message());
        assert!(watching <= 1);
        let mut skipped = false;
        if !wait(|| seen.data.load(Ordering::Acquire) > 0) {
            // A machine whose grant is NotDetermined runs the queue but may deliver nothing.
            assert!(std::env::var_os("CI").is_some(), "no data within 3 s");
            skipped = true;
        }
        let mut first = 7u8;
        assert_eq!(prisc_audio_input_request_stop(input, &mut first), S::Ok as i32);
        assert_eq!(first, 1);
        assert_eq!(prisc_audio_input_request_stop(input, &mut first), S::Ok as i32);
        assert_eq!(first, 0);
        let mut again = 0u8;
        assert_eq!(prisc_audio_input_start(input, &mut again), S::State as i32);
        assert_eq!(prisc_audio_input_destroy(input), S::Ok as i32);
        if skipped {
            eprintln!("skipped: the input delivered nothing on this CI machine");
        }
    }
    assert_eq!(seen.stopped.load(Ordering::Acquire), 0);
}

#[cfg(windows)]
#[test]
fn every_audio_export_is_unsupported_on_windows() {
    let mut output = ptr::null_mut();
    let mut output_info = PriscAudioOutputInfo::default();
    let mut input = ptr::null_mut();
    let mut input_info = empty_info();
    let fake = ptr::dangling_mut::<u8>();
    unsafe {
        assert_eq!(
            prisc_audio_output_create(48_000, 2, Some(count_samples), ptr::null_mut(), &mut output, &mut output_info),
            S::Unsupported as i32
        );
        assert!(output.is_null());
        assert_eq!(prisc_audio_output_start(fake.cast()), S::Unsupported as i32);
        assert_eq!(prisc_audio_output_stop(fake.cast()), S::Unsupported as i32);
        assert_eq!(prisc_audio_output_destroy(fake.cast()), S::Unsupported as i32);
        assert_eq!(
            prisc_audio_input_open(
                ptr::null(),
                0,
                48_000,
                50,
                Some(record),
                ptr::null_mut(),
                &mut input,
                &mut input_info
            ),
            S::Unsupported as i32
        );
        assert!(input.is_null());
        assert_eq!(prisc_audio_input_start(fake.cast(), ptr::null_mut()), S::Unsupported as i32);
        assert_eq!(prisc_audio_input_request_stop(fake.cast(), ptr::null_mut()), S::Unsupported as i32);
        assert_eq!(prisc_audio_input_destroy(fake.cast()), S::Unsupported as i32);
    }
    assert_eq!(enumerate(PriscDeviceKind::AudioInput).err(), Some(S::Unsupported as i32));
    assert_eq!(enumerate(PriscDeviceKind::AudioOutput).err(), Some(S::Unsupported as i32));
}
