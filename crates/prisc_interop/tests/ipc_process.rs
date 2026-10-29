//! The IPC and process exports through the library's public surface (README section 4.4): the
//! stdout guard (in a respawned child, since it takes the process's stdout), the Windows-only
//! process exports' answers off Windows, and the FFmpeg directory preparation on the recorder
//! payload when `PRISC_HELPERS_DIR` names one.

use std::io::{Read, Write};
use std::process::{Command, Stdio};
use std::ptr;

use prisc_interop::ffi::error::prisc_last_error_message;
use prisc_interop::ffi::ipc::prisc_ipc_claim_stdout;
use prisc_interop::ffi::process::{
    prisc_process_allow_foreground_windows, prisc_process_assign_kill_on_close_job_windows, prisc_process_package_full_name_windows,
    prisc_process_prepare_ffmpeg_dir,
};
use prisc_interop::ffi::status::PriscInteropStatus as S;
use prisc_interop::ffi::string::prisc_string_free;
use prisc_interop::ffi::types::PriscString;

const ROLE_VAR: &str = "PRISC_INTEROP_IPC_TEST_ROLE";

fn last_error() -> String {
    let len = unsafe { prisc_last_error_message(ptr::null_mut(), 0) };
    let mut buf = vec![0u8; len];
    unsafe { prisc_last_error_message(buf.as_mut_ptr(), buf.len()) };
    String::from_utf8_lossy(&buf).into_owned()
}

/// The child: claims stdout twice through the export, writes the results through the private
/// handle, and prints a stray line through fd 1.
#[test]
fn claim_child_role() {
    if std::env::var(ROLE_VAR).as_deref() != Ok("claim") {
        return;
    }
    let (mut handle, mut redirected) = (0isize, 0u8);
    let first = unsafe { prisc_ipc_claim_stdout(&mut handle, &mut redirected) };
    let (mut second_handle, mut second_redirected) = (-7isize, 9u8);
    let second = unsafe { prisc_ipc_claim_stdout(&mut second_handle, &mut second_redirected) };
    let second_error = last_error();

    #[cfg(unix)]
    let mut private = unsafe { <std::fs::File as std::os::fd::FromRawFd>::from_raw_fd(handle as i32) };
    #[cfg(windows)]
    let mut private = unsafe { <std::fs::File as std::os::windows::io::FromRawHandle>::from_raw_handle(handle as _) };
    writeln!(private, "FIRST {first} redirected={redirected}").unwrap();
    writeln!(
        private,
        "SECOND {second} untouched={} error={second_error}",
        second_handle == -7 && second_redirected == 9
    )
    .unwrap();
    println!("STRAY");
    std::process::exit(0);
}

#[test]
fn claim_stdout_moves_fd1_to_stderr_and_a_second_claim_is_state() {
    let output = Command::new(std::env::current_exe().unwrap())
        .args(["claim_child_role", "--exact", "--nocapture", "--test-threads=1"])
        .env(ROLE_VAR, "claim")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let mut child = output;
    let mut stdout = String::new();
    child
        .stdout
        .take()
        .unwrap()
        .read_to_string(&mut stdout)
        .unwrap();
    let mut stderr = String::new();
    child
        .stderr
        .take()
        .unwrap()
        .read_to_string(&mut stderr)
        .unwrap();
    assert!(child.wait().unwrap().success(), "stderr: {stderr}");

    assert!(
        stdout.contains(&format!("FIRST {} redirected=1\n", S::Ok as i32)),
        "stdout: {stdout:?}"
    );
    assert!(
        stdout.contains(&format!("SECOND {} untouched=true error=", S::State as i32)),
        "stdout: {stdout:?}"
    );
    assert!(stdout.contains("already claimed"), "stdout: {stdout:?}");
    assert!(!stdout.contains("STRAY"), "fd 1 still reaches the pipe: {stdout:?}");
    assert!(stderr.contains("STRAY"), "fd 1 did not reach stderr: {stderr:?}");
}

#[test]
fn the_foreground_grant_is_windows_only() {
    let any = unsafe { prisc_process_allow_foreground_windows(0) };
    let one = unsafe { prisc_process_allow_foreground_windows(std::process::id()) };
    if cfg!(windows) {
        // Windows grants or refuses depending on who owns the foreground; either is an answer.
        for status in [any, one] {
            assert!(status == S::Ok as i32 || status == S::Os as i32, "{status}: {}", last_error());
        }
    } else {
        assert_eq!(any, S::Unsupported as i32);
        assert_eq!(one, S::Unsupported as i32);
    }
}

#[test]
fn the_kill_on_close_job_is_windows_only() {
    #[cfg(not(windows))]
    assert_eq!(unsafe { prisc_process_assign_kill_on_close_job_windows(42) }, S::Unsupported as i32);
    #[cfg(windows)]
    {
        use std::os::windows::io::AsRawHandle;
        let mut sleeper = Command::new("cmd.exe")
            .args(["/c", "ping", "-n", "30", "127.0.0.1"])
            .stdout(Stdio::null())
            .spawn()
            .unwrap();
        let status = unsafe { prisc_process_assign_kill_on_close_job_windows(sleeper.as_raw_handle() as isize) };
        // a host job that forbids nesting is the documented failure; anything else is a bug
        assert!(status == S::Ok as i32 || status == S::Os as i32, "{status}: {}", last_error());
        let again = unsafe { prisc_process_assign_kill_on_close_job_windows(sleeper.as_raw_handle() as isize) };
        assert!(again == S::Ok as i32 || again == S::Os as i32, "{again}: {}", last_error());
        let _ = sleeper.kill();
        let _ = sleeper.wait();
    }
}

#[test]
fn the_package_identity_is_windows_only_and_none_in_a_test_host() {
    let mut name = PriscString {
        ptr: ptr::null_mut(),
        len: 99,
    };
    let status = unsafe { prisc_process_package_full_name_windows(&mut name) };
    if cfg!(windows) {
        assert_eq!(status, S::None as i32, "{}", last_error());
    } else {
        assert_eq!(status, S::Unsupported as i32);
    }
    assert!(name.ptr.is_null() && name.len == 0, "the output is the empty string unless OK");
    unsafe { prisc_string_free(&mut name) };
}

fn prepare(dir: &str) -> (i32, u32) {
    let mut loaded = u32::MAX;
    let status = unsafe { prisc_process_prepare_ffmpeg_dir(dir.as_ptr(), dir.len(), &mut loaded) };
    (status, loaded)
}

#[test]
fn preparing_a_directory_without_ffmpeg_loads_nothing() {
    let dir = std::env::temp_dir().join(format!("prisc_interop_noffmpeg_{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let (status, loaded) = prepare(&dir.to_string_lossy());
    assert_eq!(status, S::Ok as i32, "{}", last_error());
    assert_eq!(loaded, 0);
    let _ = std::fs::remove_dir_all(&dir);

    // empty: Windows restores the default search order, elsewhere there is nothing to do
    assert_eq!(prepare(""), (S::Ok as i32, 0), "{}", last_error());
    if !cfg!(windows) {
        assert_eq!(prepare("/nonexistent/prisc"), (S::Ok as i32, 0));
    }
}

/// The recorder payload's FFmpeg directory, from `PRISC_HELPERS_DIR`.
fn payload_ffmpeg_dir() -> Option<std::path::PathBuf> {
    let helpers = std::env::var_os(prisc_ipc::locate::HELPERS_DIR_VAR)?;
    std::env::split_paths(&helpers)
        .flat_map(|dir| [dir.join("recorder/Frameworks"), dir.join("recorder"), dir.join("Frameworks"), dir])
        .find(|dir| dir.join("libavcodec.dylib").is_file())
}

#[test]
fn preparing_the_payload_loads_the_ffmpeg_closure_on_macos() {
    if !cfg!(target_os = "macos") {
        eprintln!("skipping: the dylib closure is macOS only");
        return;
    }
    let Some(dir) = payload_ffmpeg_dir() else {
        eprintln!("skipping: PRISC_HELPERS_DIR names no recorder payload with FFmpeg");
        return;
    };
    let (status, loaded) = prepare(&dir.to_string_lossy());
    assert_eq!(status, S::Ok as i32, "{}", last_error());
    assert!(loaded >= 7, "loaded {loaded} images from {}", dir.display());
    eprintln!("loaded {loaded} images from {}", dir.display());

    #[cfg(unix)]
    {
        // The loaded avformat answers through the default namespace, as FFmpeg.AutoGen's would.
        let symbol = unsafe { libc::dlsym(libc::RTLD_DEFAULT, c"avformat_version".as_ptr()) };
        assert!(!symbol.is_null(), "avformat_version is not loaded");
        let avformat_version: unsafe extern "C" fn() -> u32 = unsafe { std::mem::transmute(symbol) };
        let major = unsafe { avformat_version() } >> 16;
        assert!(major >= 60, "avformat major {major}");
    }
}
