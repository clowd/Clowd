//! How a helper ends (HELPER_PROTOCOL.md §3.3): one quit path for `quit`, stdin EOF, signals and
//! a dead stdout, the orphan hard-exit timer, and the process's single exit function.
//!
//! The state lives in a [`Lifetime`] so the tests can run the real logic against their own
//! instance; the public functions all act on the process-wide one.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, Once};
use std::time::Duration;

use crate::exit;

/// What fired the quit path.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum QuitReason {
    /// `{"type":"quit"}` on stdin.
    Command,
    /// stdin reached EOF or failed: the parent is gone or closed its end.
    StdinClosed,
    /// SIGINT/SIGTERM/SIGHUP, or a console control event on Windows.
    Signal,
    /// An emit failed: nobody is reading stdout any more.
    StdoutClosed,
}

type OnQuit = Arc<dyn Fn(QuitReason) + Send + Sync>;

#[derive(Default)]
struct QuitState {
    on_quit: Option<OnQuit>,
    grace: Option<Duration>,
    /// The first reason, once the quit path has fired.
    triggered: Option<QuitReason>,
    /// Whether `on_quit` has run (it may only run once `install` supplied it).
    handled: bool,
}

pub(crate) struct Lifetime {
    quit: Mutex<Option<QuitState>>,
    hooks: Mutex<Vec<fn(i32)>>,
    exit_fn: Mutex<Option<fn(i32) -> !>>,
    exiting: AtomicBool,
}

pub(crate) static GLOBAL: Lifetime = Lifetime::new();

impl Lifetime {
    pub(crate) const fn new() -> Self {
        Self {
            quit: Mutex::new(None),
            hooks: Mutex::new(Vec::new()),
            exit_fn: Mutex::new(None),
            exiting: AtomicBool::new(false),
        }
    }

    /// Supplies the quit callback and the grace. A quit that already fired (EOF before the
    /// helper got this far) runs it now.
    pub(crate) fn set_on_quit(&'static self, on_quit: OnQuit, grace: Duration) {
        let fire = {
            let mut guard = lock(&self.quit);
            let state = guard.get_or_insert_with(QuitState::default);
            assert!(state.on_quit.is_none(), "lifetime::install called twice");
            state.on_quit = Some(on_quit.clone());
            state.grace = Some(grace);
            match state.triggered {
                Some(reason) if !state.handled => {
                    state.handled = true;
                    Some(reason)
                }
                _ => None,
            }
        };
        if let Some(reason) = fire {
            self.fire(&on_quit, reason, grace);
        }
    }

    pub(crate) fn trigger(&'static self, reason: QuitReason) {
        let fire = {
            let mut guard = lock(&self.quit);
            let state = guard.get_or_insert_with(QuitState::default);
            if state.triggered.is_some() {
                return;
            }
            state.triggered = Some(reason);
            match (&state.on_quit, state.grace) {
                (Some(on_quit), Some(grace)) => {
                    state.handled = true;
                    Some((on_quit.clone(), grace))
                }
                _ => None,
            }
        };
        // Outside the lock: on_quit may run on any thread, and must never find this lock held.
        if let Some((on_quit, grace)) = fire {
            self.fire(&on_quit, reason, grace);
        }
    }

    fn fire(&'static self, on_quit: &OnQuit, reason: QuitReason, grace: Duration) {
        eprintln!("prisc_ipc: quitting ({reason:?})");
        self.arm_timer(grace);
        on_quit(reason);
    }

    fn arm_timer(&'static self, grace: Duration) {
        let spawned = std::thread::Builder::new()
            .name("ipc-quit-timer".into())
            .spawn(move || {
                std::thread::sleep(grace);
                eprintln!("prisc_ipc: still alive {grace:?} after quit; exiting");
                self.exit(exit::QUIT_TIMEOUT)
            });
        if let Err(e) = spawned {
            eprintln!("prisc_ipc: could not start the quit timer: {e}");
        }
    }

    pub(crate) fn triggered(&self) -> Option<QuitReason> {
        lock(&self.quit)
            .as_ref()
            .and_then(|state| state.triggered)
    }

    fn set_exit_fn(&self, f: fn(i32) -> !) {
        *lock(&self.exit_fn) = Some(f);
    }

    fn on_exit(&self, f: fn(i32)) {
        lock(&self.hooks).push(f);
    }

    pub(crate) fn exit(&self, code: i32) -> ! {
        // Only the first caller runs the hooks. A second one (the hard-exit timer firing while a
        // hook is stuck) goes straight to the exit fn, so a hook can never keep an orphan alive.
        if !self.exiting.swap(true, Ordering::SeqCst) {
            let hooks = lock(&self.hooks).clone();
            for hook in hooks {
                hook(code);
            }
        }
        let exit_fn = *lock(&self.exit_fn);
        match exit_fn {
            Some(f) => f(code),
            None => std::process::exit(code),
        }
    }
}

/// A poisoned lock here would only mean a panicking hook; the state is still usable.
fn lock<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(|e| e.into_inner())
}

/// Installs, once per process: the quit callback and the signal / console-ctrl handler.
/// `grace` arms the hard-exit timer when the quit path first fires; helpers pass their catalog's
/// `QUIT_GRACE` plus a 5 s margin, so a living parent's kill always comes first.
///
/// `on_quit` must only enqueue or set a flag: it must never block, never emit, and never take a
/// lock the emitting threads hold (trigger can run on any thread, including one that just failed
/// an emit). The one exception is a helper with no exit hooks and [`exit_now`] as its exit fn
/// (prisc_ai), which may call [`exit`] from it directly.
pub fn install(on_quit: impl Fn(QuitReason) + Send + Sync + 'static, grace: Duration) {
    GLOBAL.set_on_quit(Arc::new(on_quit), grace);
    static SIGNALS: Once = Once::new();
    SIGNALS.call_once(signals::install);
}

/// Fires the quit path. The first call runs `on_quit` and arms the timer; later calls no-op.
pub fn trigger(reason: QuitReason) {
    GLOBAL.trigger(reason);
}

/// The reason the quit path fired, if it has.
pub fn triggered() -> Option<QuitReason> {
    GLOBAL.triggered()
}

/// Overrides the final exit primitive (recorder/share_region: `platform::exit_process`, which
/// skips libobs's broken static destructors). Default `std::process::exit`.
pub fn set_exit_fn(f: fn(i32) -> !) {
    GLOBAL.set_exit_fn(f);
}

/// An exit fn for [`set_exit_fn`] that ends the process on the spot: no atexit handlers, no C++
/// static destructors, no DLL teardown. For helpers whose native libraries cannot survive being
/// torn down while another thread is still inside them (prisc_ai mid-inference). Anything that
/// must reach a file or pipe has to be flushed before it is called.
pub fn exit_now(code: i32) -> ! {
    #[cfg(unix)]
    unsafe {
        libc::_exit(code)
    }
    #[cfg(windows)]
    {
        use windows::Win32::System::Threading::{GetCurrentProcess, TerminateProcess};
        // TerminateProcess on our own process does not return once it succeeds.
        let _ = unsafe { TerminateProcess(GetCurrentProcess(), code as u32) };
        std::process::abort()
    }
}

/// Registers a hook that [`exit`] runs before the exit fn. Each hook is expected to bound itself
/// (telemetry flushes with a timeout).
pub fn on_exit(f: fn(i32)) {
    GLOBAL.on_exit(f);
}

/// The process's single exit path: runs every [`on_exit`] hook in registration order, then the
/// exit fn.
pub fn exit(code: i32) -> ! {
    GLOBAL.exit(code)
}

#[cfg(unix)]
mod signals {
    use super::QuitReason;

    /// SIGINT, SIGTERM and SIGHUP (ctrlc's `termination` feature). Rust's SIGPIPE=ignore stays,
    /// so a dead stdout is an EPIPE on the next emit rather than a kill.
    pub(super) fn install() {
        let installed = ctrlc::set_handler(|| {
            super::trigger(QuitReason::Signal);
            // ctrlc delivers on its own thread; there is nothing more for it to do, and a second
            // signal must not re-enter the quit path.
            loop {
                std::thread::park();
            }
        });
        if let Err(e) = installed {
            eprintln!("prisc_ipc: could not install the signal handler: {e}");
        }
    }
}

#[cfg(windows)]
mod signals {
    use std::time::Duration;

    use windows::core::BOOL;
    use windows::Win32::System::Console::SetConsoleCtrlHandler;

    use super::QuitReason;

    /// Runs on an OS-injected thread and never returns: for CTRL_CLOSE the grace window lasts
    /// only while this routine runs, so it blocks while the helper stops and exits underneath
    /// it. Best effort, and in practice recorder-only: the other helpers are GUI-subsystem
    /// binaries in release and have no console, and no process receives CTRL_LOGOFF or
    /// CTRL_SHUTDOWN once user32 is loaded. stdin EOF and the parent's job are the real
    /// mechanisms on Windows.
    unsafe extern "system" fn handler(_ctrl_type: u32) -> BOOL {
        super::trigger(QuitReason::Signal);
        loop {
            std::thread::sleep(Duration::from_secs(60));
        }
    }

    pub(super) fn install() {
        if unsafe { SetConsoleCtrlHandler(Some(handler), true) }.is_err() {
            eprintln!("prisc_ipc: could not install the console control handler");
        }
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use std::sync::atomic::AtomicUsize;

    use super::*;

    /// A fresh lifetime whose quit callback counts and records its calls. Its timer is armed
    /// for an hour, so it never fires inside a test run.
    pub(crate) fn counting() -> (&'static Lifetime, Arc<Mutex<Vec<QuitReason>>>) {
        let lifetime: &'static Lifetime = Box::leak(Box::new(Lifetime::new()));
        let calls = Arc::new(Mutex::new(Vec::new()));
        let seen = calls.clone();
        lifetime.set_on_quit(Arc::new(move |reason| seen.lock().unwrap().push(reason)), Duration::from_secs(3600));
        (lifetime, calls)
    }

    #[test]
    fn only_the_first_trigger_runs_on_quit() {
        let (lifetime, calls) = counting();
        lifetime.trigger(QuitReason::Command);
        lifetime.trigger(QuitReason::StdinClosed);
        lifetime.trigger(QuitReason::Signal);
        assert_eq!(*calls.lock().unwrap(), [QuitReason::Command]);
        assert_eq!(lifetime.triggered(), Some(QuitReason::Command));
    }

    #[test]
    fn a_trigger_before_install_fires_on_install() {
        let lifetime: &'static Lifetime = Box::leak(Box::new(Lifetime::new()));
        lifetime.trigger(QuitReason::StdinClosed);
        let count = Arc::new(AtomicUsize::new(0));
        let seen = count.clone();
        lifetime.set_on_quit(
            Arc::new(move |reason| {
                assert_eq!(reason, QuitReason::StdinClosed);
                seen.fetch_add(1, Ordering::SeqCst);
            }),
            Duration::from_secs(3600),
        );
        lifetime.trigger(QuitReason::Command);
        assert_eq!(count.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn concurrent_triggers_run_on_quit_once() {
        let (lifetime, calls) = counting();
        let threads: Vec<_> = (0..8)
            .map(|_| std::thread::spawn(move || lifetime.trigger(QuitReason::Signal)))
            .collect();
        for thread in threads {
            thread.join().unwrap();
        }
        assert_eq!(calls.lock().unwrap().len(), 1);
    }
}
