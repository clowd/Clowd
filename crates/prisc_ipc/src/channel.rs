//! The stdout half (HELPER_PROTOCOL.md §2.1, §2.2): `hello`, then one JSON object per line.

use std::fmt::Display;
use std::io::Write;
use std::marker::PhantomData;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use serde::Serialize;

use crate::catalog::{self, ErrorCode, Event};
use crate::exit;
use crate::lifetime::{self, Lifetime, QuitReason};

/// Claims stdout ([`crate::claim_stdout`]), emits `hello`, and returns the process's one
/// protocol writer. Call it first thing in `main`, before any native library is initialised or
/// called, so `hello` arrives in milliseconds. Panics if called twice.
pub fn open<E: Event>(helper: &'static str) -> Channel<E> {
    static OPENED: AtomicBool = AtomicBool::new(false);
    assert!(!OPENED.swap(true, Ordering::SeqCst), "prisc_ipc::open called twice");
    let channel = match crate::claim_stdout() {
        Ok(stdout) => Channel::new(Box::new(stdout), &lifetime::GLOBAL),
        Err(e) => {
            // No usable stdout at all (a GUI-subsystem binary started without one). There is
            // nobody to talk to, which is not a reason to quit: emits are simply dropped.
            eprintln!("prisc_ipc: stdout is unusable ({e}); protocol lines are dropped");
            let channel = Channel::new(Box::new(std::io::sink()), &lifetime::GLOBAL);
            channel
                .inner
                .closed
                .store(true, Ordering::SeqCst);
            channel
        }
    };
    channel.write_line(&hello(E::SPEC, helper), false);
    channel
}

fn hello(spec: &catalog::CatalogSpec, helper: &str) -> catalog::Hello {
    catalog::Hello {
        protocol: crate::PROTOCOL,
        catalog: spec.name.to_owned(),
        catalog_hash: spec.catalog_hash(),
        helper: helper.to_owned(),
        // For logs only. The same fallback as prisc_rust_core's telemetry release name.
        version: option_env!("PRISC_VERSION")
            .unwrap_or(env!("CARGO_PKG_VERSION"))
            .to_owned(),
        pid: std::process::id(),
    }
}

/// The process's protocol writer. Cheap to clone; every clone writes to the same pipe under the
/// same lock, so lines from different threads never interleave.
pub struct Channel<E> {
    inner: Arc<Inner>,
    _event: PhantomData<fn(E)>,
}

struct Inner {
    out: Mutex<Box<dyn Write + Send>>,
    closed: AtomicBool,
    /// Set under the lock once a terminal line (§1.4) is out: nothing follows it.
    finished: AtomicBool,
    lifetime: &'static Lifetime,
}

impl<E> Clone for Channel<E> {
    fn clone(&self) -> Self {
        Self {
            inner: self.inner.clone(),
            _event: PhantomData,
        }
    }
}

impl<E: Event> Channel<E> {
    pub(crate) fn new(out: Box<dyn Write + Send>, lifetime: &'static Lifetime) -> Self {
        Self {
            inner: Arc::new(Inner {
                out: Mutex::new(out),
                closed: AtomicBool::new(false),
                finished: AtomicBool::new(false),
                lifetime,
            }),
            _event: PhantomData,
        }
    }

    /// Writes one event as one line and flushes it. Never panics. Returns false once the pipe is
    /// gone: the first failed write marks the channel closed (later emits are no-ops) and, after
    /// the lock is released, fires the quit path with [`QuitReason::StdoutClosed`].
    ///
    /// The first terminal event (or fatal `error`) to be written finishes the run: every emit
    /// after it, from any thread, is dropped and returns false, so the terminal line is always
    /// the last line on the wire even while other threads are still winding down.
    pub fn emit(&self, event: &E) -> bool {
        self.write_line(event, event.is_terminal())
    }

    /// Emits `error`, after collapsing `message` onto one line, and logs it to stderr. A fatal
    /// error ends the process: exit 2 for [`ErrorCode::Usage`], 1 for every other code.
    pub fn error(&self, code: ErrorCode, message: impl Display, fatal: bool) {
        let message = crate::single_line(&message.to_string());
        eprintln!("{} {}: {message}", if fatal { "Fatal" } else { "Error" }, code.as_str());
        self.emit(&E::error(code, message, fatal));
        if fatal {
            let status = if code == ErrorCode::Usage { exit::USAGE } else { exit::FAILED };
            self.inner.lifetime.exit(status);
        }
    }

    /// [`Self::error`] with `fatal: true`, for call sites that need the `!`.
    pub fn fatal(&self, code: ErrorCode, message: impl Display) -> ! {
        self.error(code, message, true);
        unreachable!("a fatal error exits")
    }

    pub fn is_closed(&self) -> bool {
        self.inner.closed.load(Ordering::SeqCst)
    }

    /// Whether a terminal line has gone out; later emits are dropped.
    pub fn is_finished(&self) -> bool {
        self.inner.finished.load(Ordering::SeqCst)
    }

    fn write_line(&self, message: &impl Serialize, terminal: bool) -> bool {
        let mut line = match serde_json::to_vec(message) {
            Ok(line) => line,
            Err(e) => {
                eprintln!("prisc_ipc: could not serialize a protocol line: {e}");
                return false;
            }
        };
        line.push(b'\n');
        let failed = {
            let mut out = self
                .inner
                .out
                .lock()
                .unwrap_or_else(|e| e.into_inner());
            if self.is_closed() || self.is_finished() {
                return false;
            }
            if terminal {
                self.inner
                    .finished
                    .store(true, Ordering::SeqCst);
            }
            match out
                .write_all(&line)
                .and_then(|()| out.flush())
            {
                Ok(()) => return true,
                Err(e) => {
                    let first = !self
                        .inner
                        .closed
                        .swap(true, Ordering::SeqCst);
                    first.then_some(e)
                }
            }
        };
        // The lock is released here: the quit callback may run on this thread and must never
        // find it held.
        if let Some(e) = failed {
            eprintln!("prisc_ipc: stdout is gone ({e})");
            self.inner
                .lifetime
                .trigger(QuitReason::StdoutClosed);
        }
        false
    }

    #[cfg(test)]
    fn lock_is_free(&self) -> bool {
        self.inner.out.try_lock().is_ok()
    }
}

#[cfg(test)]
mod tests {
    use std::io::Read;
    use std::sync::OnceLock;
    use std::time::Duration;

    use super::*;
    use crate::catalog::job;

    fn progress(percent: u32) -> job::Event {
        job::Event::Progress {
            percent,
        }
    }

    #[test]
    fn hello_carries_the_catalog_and_its_hash() {
        let hello = hello(&job::SPEC, "prisc_vid2gif");
        let line = serde_json::to_string(&hello).unwrap();
        assert!(
            line.starts_with(r#"{"type":"hello","protocol":1,"catalog":"job","catalog_hash":""#),
            "{line}"
        );
        assert_eq!(hello.catalog_hash, job::SPEC.catalog_hash());
        assert_eq!(hello.pid, std::process::id());
    }

    #[test]
    fn emits_one_line_per_event() {
        let (mut reader, writer) = std::io::pipe().unwrap();
        let (lifetime, _) = crate::lifetime::tests::counting();
        let channel = Channel::<job::Event>::new(Box::new(writer), lifetime);
        assert!(channel.emit(&progress(1)));
        channel.error(ErrorCode::BadCommand, "unknown\ncommand  'x'", false);
        drop(channel);
        let mut text = String::new();
        reader.read_to_string(&mut text).unwrap();
        assert_eq!(
            text,
            "{\"type\":\"progress\",\"percent\":1}\n{\"type\":\"error\",\"code\":\"bad_command\",\"message\":\"unknown command \
             'x'\",\"fatal\":false}\n"
        );
    }

    #[test]
    fn nothing_follows_the_terminal_line() {
        let (mut reader, writer) = std::io::pipe().unwrap();
        let (lifetime, _) = crate::lifetime::tests::counting();
        let channel = Channel::<job::Event>::new(Box::new(writer), lifetime);
        assert!(channel.emit(&progress(1)));
        assert!(channel.emit(&job::Event::Done {
            result: job::JobResult::Cancelled,
            path: None,
            bytes: None,
        }));
        assert!(channel.is_finished());
        // A late progress line, a second terminal and a non-fatal error are all dropped.
        assert!(!channel.emit(&progress(2)));
        assert!(!channel.emit(&job::Event::Done {
            result: job::JobResult::Ok,
            path: None,
            bytes: None,
        }));
        channel.error(ErrorCode::Failed, "late", false);
        assert!(!channel.is_closed());
        drop(channel);
        let mut text = String::new();
        reader.read_to_string(&mut text).unwrap();
        assert_eq!(
            text,
            "{\"type\":\"progress\",\"percent\":1}\n{\"type\":\"done\",\"result\":\"cancelled\"}\n"
        );
    }

    #[test]
    fn epipe_closes_the_channel_and_quits_after_releasing_the_lock() {
        static CHANNEL: OnceLock<Channel<job::Event>> = OnceLock::new();
        static OBSERVED: OnceLock<(bool, bool)> = OnceLock::new();

        let lifetime: &'static Lifetime = Box::leak(Box::new(Lifetime::new()));
        lifetime.set_on_quit(
            Arc::new(|reason| {
                assert_eq!(reason, QuitReason::StdoutClosed);
                let channel = CHANNEL.get().unwrap();
                // Breaking the rule on purpose: an on_quit that emits must not deadlock.
                let lock_free = channel.lock_is_free();
                let emitted = channel.emit(&progress(3));
                OBSERVED.set((lock_free, emitted)).unwrap();
            }),
            Duration::from_secs(3600),
        );

        let (reader, writer) = std::io::pipe().unwrap();
        drop(reader);
        let channel = CHANNEL.get_or_init(|| Channel::new(Box::new(writer), lifetime));
        assert!(!channel.emit(&progress(1)));
        assert!(channel.is_closed());
        assert_eq!(OBSERVED.get(), Some(&(true, false)));
        assert_eq!(lifetime.triggered(), Some(QuitReason::StdoutClosed));
        // Later emits are no-ops and do not re-fire anything.
        assert!(!channel.emit(&progress(2)));
    }
}
