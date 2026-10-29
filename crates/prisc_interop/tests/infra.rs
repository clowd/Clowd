//! The infra exports through the library's public surface (README section 4.1): status mapping,
//! the last-error slot, panics, re-entry, strings, and the ABI hash as the C# loader reads it.

use std::path::PathBuf;

use prisc_interop::ffi::error::{prisc_last_error_code, prisc_last_error_message};
use prisc_interop::ffi::selftest::{prisc_interop_selftest, PriscSelftestKind as K};
use prisc_interop::ffi::status::PriscInteropStatus as S;
use prisc_interop::ffi::string::prisc_string_free;
use prisc_interop::ffi::types::PriscString;
use prisc_interop::ffi::version::{prisc_interop_abi_hash, prisc_interop_version};

fn last_message() -> String {
    let len = unsafe { prisc_last_error_message(std::ptr::null_mut(), 0) };
    let mut buf = vec![0u8; len];
    assert_eq!(unsafe { prisc_last_error_message(buf.as_mut_ptr(), buf.len()) }, len);
    String::from_utf8(buf).unwrap()
}

#[test]
fn a_windows_only_export_answers_unsupported_elsewhere() {
    // Every stub is implemented now (the desktop area's prisc_screens_list and prisc_idle_seconds
    // and the pure area's prisc_encoding_h264_quality used to be called here), so this pins the
    // platform arm instead: on Windows a pointer the crate never handed out is S_NONE.
    let release = unsafe { prisc_interop::ffi::gpu::prisc_gpu_nv12_texture_release_windows(std::ptr::null_mut()) };
    let expected = if cfg!(windows) { S::None } else { S::Unsupported };
    assert_eq!(release, expected as i32);
    if !cfg!(windows) {
        assert!(last_message().contains("prisc_gpu_nv12_texture_release_windows"));
    }
}

#[test]
fn selftest_maps_every_status() {
    let cases = [
        (K::Ok, S::Ok),
        (K::None, S::None),
        (K::Timeout, S::Timeout),
        (K::Closed, S::Closed),
        (K::Unsupported, S::Unsupported),
        (K::InvalidArg, S::InvalidArg),
        (K::Os, S::Os),
        (K::Panic, S::Panic),
        (K::State, S::State),
        (K::Permission, S::Permission),
        (K::Capability, S::Capability),
    ];
    for (kind, status) in cases {
        let mut value = 0i64;
        assert_eq!(
            unsafe { prisc_interop_selftest(kind as i32, &mut value) },
            status as i32,
            "{kind:?}"
        );
        if status as i32 >= 0 {
            assert_eq!(last_message(), "", "{kind:?} succeeded, so the slot is empty");
        } else {
            assert!(!last_message().is_empty(), "{kind:?} leaves a message");
        }
    }
    unsafe { prisc_interop_selftest(K::Ok as i32, std::ptr::null_mut()) };
    let mut value = 0i64;
    unsafe { prisc_interop_selftest(K::Ok as i32, &mut value) };
    assert_eq!(value, 42);
}

#[test]
fn os_failures_carry_the_sign_extended_code() {
    assert_eq!(unsafe { prisc_interop_selftest(K::Os as i32, std::ptr::null_mut()) }, S::Os as i32);
    assert_eq!(unsafe { prisc_last_error_code() }, -2147467259);
    assert_eq!(unsafe { prisc_interop_selftest(K::Ok as i32, std::ptr::null_mut()) }, 0);
    assert_eq!(unsafe { prisc_last_error_code() }, 0);
}

#[test]
fn a_panic_becomes_e_panic_naming_the_export() {
    assert_eq!(
        unsafe { prisc_interop_selftest(K::Panic as i32, std::ptr::null_mut()) },
        S::Panic as i32
    );
    let message = last_message();
    assert!(message.contains("prisc_interop_selftest panicked"), "{message}");
    assert!(message.contains("asked to panic"), "{message}");
}

#[test]
fn a_long_message_is_sized_then_read_whole() {
    assert_eq!(
        unsafe { prisc_interop_selftest(K::LongMessage as i32, std::ptr::null_mut()) },
        S::State as i32
    );
    let len = unsafe { prisc_last_error_message(std::ptr::null_mut(), 0) };
    assert!(len > 1024, "{len}");
    let mut small = vec![0xABu8; 64];
    assert_eq!(unsafe { prisc_last_error_message(small.as_mut_ptr(), small.len()) }, len);
    assert!(small.iter().all(|b| *b == 0xAB), "a message that does not fit writes nothing");
    assert_eq!(last_message().len(), len);
}

#[test]
fn a_nested_guard_entry_keeps_the_outer_last_error() {
    assert_eq!(
        unsafe { prisc_interop_selftest(K::Nested as i32, std::ptr::null_mut()) },
        S::State as i32
    );
    assert_eq!(last_message(), "outer");
}

#[test]
fn unknown_kinds_are_invalid_arg() {
    assert_eq!(unsafe { prisc_interop_selftest(1000, std::ptr::null_mut()) }, S::InvalidArg as i32);
    assert!(last_message().contains("1000"));
}

#[test]
fn the_version_is_an_owned_string_freed_once() {
    let mut s = PriscString {
        ptr: std::ptr::null_mut(),
        len: 0,
    };
    assert_eq!(unsafe { prisc_interop_version(&mut s) }, 0);
    let text = unsafe { std::str::from_utf8(std::slice::from_raw_parts(s.ptr, s.len)) }
        .unwrap()
        .to_string();
    assert!(!text.is_empty());
    assert_eq!(unsafe { prisc_string_free(&mut s) }, 0);
    assert!(s.ptr.is_null() && s.len == 0);
    assert_eq!(unsafe { prisc_string_free(&mut s) }, 0, "a second free is a no-op");
    assert_eq!(unsafe { prisc_string_free(std::ptr::null_mut()) }, S::InvalidArg as i32);
}

/// The built cdylib beside this test binary (target/<profile>/deps or target/<profile>).
fn built_library() -> PathBuf {
    let name = format!("{}prisc_interop{}", std::env::consts::DLL_PREFIX, std::env::consts::DLL_SUFFIX);
    let exe = std::env::current_exe().unwrap();
    let deps = exe.parent().unwrap();
    [deps.join(&name), deps.parent().unwrap().join(&name)]
        .into_iter()
        .find(|p| p.is_file())
        .unwrap_or_else(|| panic!("{name} was not built next to {}", deps.display()))
}

/// What the C# loader does: open the library, call `prisc_interop_abi_hash` by name, compare with
/// the generated `InteropAbi.Hash`.
#[test]
fn the_library_hash_equals_the_generated_bindings() {
    let mut linked = 0u64;
    assert_eq!(unsafe { prisc_interop_abi_hash(&mut linked) }, 0);

    let library = unsafe { libloading::Library::new(built_library()) }.expect("load the cdylib");
    let export: libloading::Symbol<unsafe extern "C" fn(*mut u64) -> i32> =
        unsafe { library.get(b"prisc_interop_abi_hash") }.expect("prisc_interop_abi_hash is exported");
    let mut from_library = 0u64;
    assert_eq!(unsafe { export(&mut from_library) }, 0);
    assert_eq!(from_library, linked);

    let generated = std::fs::read_to_string(concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../../ui/Prisc.Interop/Generated/InteropAbi.g.cs"
    ))
    .expect("InteropAbi.g.cs (run cargo codegen)");
    assert!(
        generated.contains(&format!("public const ulong Hash = 0x{from_library:016X}UL;")),
        "InteropAbi.g.cs does not carry the library's hash 0x{from_library:016X}: run cargo codegen"
    );
}

#[test]
fn a_handleless_callback_panic_reaches_the_log_sink_through_the_next_export() {
    use prisc_interop::ffi::log::prisc_log_set_callback;
    use std::sync::Mutex;
    use std::time::{Duration, Instant};

    static LINES: Mutex<Vec<(i32, String)>> = Mutex::new(Vec::new());
    unsafe extern "C" fn record(level: i32, msg: *const u8, len: usize, _: *mut std::ffi::c_void) {
        let text = String::from_utf8_lossy(std::slice::from_raw_parts(msg, len)).into_owned();
        LINES.lock().unwrap().push((level, text));
    }
    let seen = || {
        LINES
            .lock()
            .unwrap()
            .iter()
            .filter(|(_, text)| text.contains("the selftest callback was asked to panic"))
            .cloned()
            .collect::<Vec<_>>()
    };

    assert_eq!(unsafe { prisc_log_set_callback(Some(record), std::ptr::null_mut()) }, 0);
    assert_eq!(
        unsafe { prisc_interop_selftest(K::CallbackPanic as i32, std::ptr::null_mut()) },
        S::Ok as i32
    );
    assert_eq!(last_message(), "", "the callback's panic is not the caller's failure");
    // any export drains it, on whichever thread runs one first
    unsafe { prisc_interop_selftest(K::Ok as i32, std::ptr::null_mut()) };
    let deadline = Instant::now() + Duration::from_secs(5);
    while seen().is_empty() && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(10));
    }
    assert_eq!(unsafe { prisc_log_set_callback(None, std::ptr::null_mut()) }, 0);

    let seen = seen();
    assert_eq!(seen.len(), 1, "{seen:?}");
    assert_eq!(seen[0].0, prisc_interop::ffi::log::PriscInteropLogLevel::Error as i32);
    assert!(seen[0].1.contains("an OS callback panicked"), "{}", seen[0].1);
}
