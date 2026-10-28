//! The stdout guard (HELPER_PROTOCOL.md §3.2): the protocol pipe becomes a private handle only
//! the channel writes to, and everything else that reaches fd 1 goes to stderr.
//!
//! The rule that stdout carries protocol lines and nothing else is one our own code keeps
//! easily and native libraries do not keep at all: libobs, FFmpeg, CoreML and ONNX Runtime all
//! print to fd 1 through the C runtime, at moments of their own choosing (CoreML's model
//! compilation complaints once landed in the middle of a prisc_ai matte frame). There is no way
//! to ask them to stop, so the pipe simply stops being reachable through fd 1.

use std::fs::File;
use std::io;

/// Duplicates the real stdout into a private handle, then points fd 1 (and on Windows the
/// process's `STD_OUTPUT_HANDLE`) at stderr. Returns the private handle.
///
/// The duplicate is made with std's `try_clone_to_owned`, so it is never inheritable
/// (`F_DUPFD_CLOEXEC` on Unix, `DuplicateHandle` without inheritance on Windows). An inheritable
/// one would leak into every grandchild (obs-ffmpeg-mux, prisc_ai, a browser), and the parent's
/// stdout pump would never see EOF while any of them lived.
///
/// Call it before any native library is initialised or called. A failure to redirect fd 1 is
/// logged, not returned: stdout then still reaches the pipe, which is exactly how the helper
/// behaved before the guard existed.
pub fn claim_stdout() -> io::Result<File> {
    let private = duplicate_stdout()?;
    redirect_stdout_to_stderr();
    Ok(private)
}

#[cfg(unix)]
fn duplicate_stdout() -> io::Result<File> {
    use std::os::fd::AsFd;
    Ok(File::from(io::stdout().as_fd().try_clone_to_owned()?))
}

#[cfg(unix)]
fn redirect_stdout_to_stderr() {
    // SAFETY: a plain descriptor-table edit. dup2 closes the old fd 1, the pipe, but the
    // duplicate above already holds it open, so nothing the parent reads from is released.
    if unsafe { libc::dup2(2, 1) } < 0 {
        eprintln!(
            "prisc_ipc: could not point stdout at stderr ({}); a native library printing to stdout would corrupt the \
             protocol",
            io::Error::last_os_error()
        );
    }
}

#[cfg(windows)]
fn duplicate_stdout() -> io::Result<File> {
    use std::os::windows::io::AsHandle;
    Ok(File::from(
        io::stdout()
            .as_handle()
            .try_clone_to_owned()?,
    ))
}

/// The original pipe handle came in through STARTUPINFO and is inheritable, so first its
/// inherit flag is cleared: the UCRT `_dup2` below is the only thing that closes it, and that
/// step is skipped in a static-CRT process that never loads `ucrtbase.dll`. Left inheritable,
/// it would reach every grandchild spawned with handle inheritance (std's `Command` among them).
///
/// Then `SetStdHandle` moves what `GetStdHandle` returns (Rust's own `println!`, and anything
/// else that asks the OS), and the shared UCRT's `_dup2(2, 1)` moves the C runtime's fd 1, which
/// native `printf` writes to. The last step assumes every native library shares the dynamic
/// UCRT (`ucrtbase.dll`); a library with its own CRT keeps the old handle value in its own fd
/// table (§3.2).
#[cfg(windows)]
fn redirect_stdout_to_stderr() {
    use windows::core::{s, w};
    use windows::Win32::Foundation::{SetHandleInformation, HANDLE_FLAGS, HANDLE_FLAG_INHERIT};
    use windows::Win32::System::Console::{GetStdHandle, SetStdHandle, STD_ERROR_HANDLE, STD_OUTPUT_HANDLE};
    use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};

    // SAFETY: a flag change on this process's own standard output handle, which
    // duplicate_stdout has just proved valid.
    let uninherited =
        unsafe { GetStdHandle(STD_OUTPUT_HANDLE).and_then(|stdout| SetHandleInformation(stdout, HANDLE_FLAG_INHERIT.0, HANDLE_FLAGS(0))) };
    if let Err(e) = uninherited {
        eprintln!("prisc_ipc: could not make the original stdout handle non-inheritable ({e})");
    }

    // SAFETY: handle-table edits on this process's own standard handles.
    let redirected = unsafe { GetStdHandle(STD_ERROR_HANDLE).and_then(|stderr| SetStdHandle(STD_OUTPUT_HANDLE, stderr)) };
    if let Err(e) = redirected {
        eprintln!("prisc_ipc: could not point STD_OUTPUT_HANDLE at stderr ({e})");
    }

    // Only when something already loaded the UCRT: a process without it has no CRT fd 1 a
    // native library could print through. The binaries themselves link the CRT statically, and
    // no native code of ours prints through that copy.
    // SAFETY: looking up an export of an already-loaded module; the signature is `_dup2`'s.
    unsafe {
        let Ok(ucrt) = GetModuleHandleW(w!("ucrtbase.dll")) else {
            return;
        };
        let Some(dup2) = GetProcAddress(ucrt, s!("_dup2")) else {
            return;
        };
        let dup2: unsafe extern "C" fn(i32, i32) -> i32 = std::mem::transmute(dup2);
        if dup2(2, 1) != 0 {
            eprintln!("prisc_ipc: the UCRT's _dup2(2, 1) failed; native stdout output is not redirected");
        }
    }
}

#[cfg(test)]
mod tests {
    use std::io::{Read, Write};
    use std::process::{Command, Stdio};
    use std::time::{Duration, Instant};

    const ROLE_VAR: &str = "PRISC_IPC_GUARD_TEST_ROLE";
    const GRANDCHILD_LIFETIME: Duration = Duration::from_secs(4);

    /// Re-runs this test binary as `role`, executing only the test named `test`.
    fn self_command(test: &str, role: &str) -> Command {
        let mut command = Command::new(std::env::current_exe().unwrap());
        command
            .args([test, "--exact", "--nocapture", "--test-threads=1"])
            .env(ROLE_VAR, role);
        command
    }

    /// The child: claims stdout, writes one line through the private handle, starts a
    /// grandchild with default (inherited) stdio that outlives it, and exits at once.
    #[test]
    fn guard_child_role() {
        match std::env::var(ROLE_VAR).as_deref() {
            Ok("child") => {
                let mut private = super::claim_stdout().unwrap();
                private.write_all(b"PROTOCOL\n").unwrap();
                // Through fd 1, which now goes to stderr.
                println!("STRAY");
                self_command("guard::tests::guard_child_role", "grandchild")
                    .spawn()
                    .unwrap();
                std::process::exit(0);
            }
            Ok("grandchild") => std::thread::sleep(GRANDCHILD_LIFETIME),
            _ => {}
        }
    }

    /// The private handle must not be inherited: if the grandchild held it, the pipe would stay
    /// open until the grandchild exits, and the read below would take its whole lifetime.
    #[test]
    fn private_stdout_is_not_inherited_and_fd1_goes_to_stderr() {
        let mut child = self_command("guard::tests::guard_child_role", "child")
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        let started = Instant::now();
        let mut stdout = String::new();
        child
            .stdout
            .take()
            .unwrap()
            .read_to_string(&mut stdout)
            .unwrap();
        let elapsed = started.elapsed();
        child.wait().unwrap();
        assert!(stdout.contains("PROTOCOL\n"), "stdout: {stdout:?}");
        assert!(!stdout.contains("STRAY"), "fd 1 still reaches the pipe: {stdout:?}");
        assert!(
            elapsed < GRANDCHILD_LIFETIME / 2,
            "stdout stayed open for {elapsed:?}: the grandchild inherited the private handle"
        );
    }
}
