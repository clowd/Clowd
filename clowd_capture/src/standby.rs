//! Cross-platform standby control plane. Newline-delimited JSON on stdin carries
//! complete CLI snapshots and explicit capture requests; EOF means the shell died.
//! The screenshot hotkeys themselves are owned here too — see standby_hotkeys.rs
//! for why they must live in this process and use a low-level hook.

use std::io::{BufRead, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc};
use std::time::{Instant, SystemTime};

use clap::Parser;
use serde::Deserialize;
use winit::application::ApplicationHandler;
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop, EventLoopProxy};
use winit::platform::run_on_demand::EventLoopExtRunOnDemand;

use crate::settings::{CaptureMode, CliArgs};
use crate::standby_hotkeys::{Poll, StandbyHotkeys};

/// Writes one protocol line to stdout. Never panics: when the shell has died the
/// pipe is closed and `println!` would abort the process from inside a protocol
/// print — parent-EOF handling is the one place that decides how we exit.
pub fn emit_line(line: std::fmt::Arguments<'_>) {
    let mut out = std::io::stdout().lock();
    let _ = out.write_fmt(line);
    let _ = out.write_all(b"\n");
    let _ = out.flush();
}

macro_rules! emit {
    ($($arg:tt)*) => { crate::standby::emit_line(format_args!($($arg)*)) };
}
pub(crate) use emit;

/// CLOWD_IPC_ERROR is a line protocol like everything else, but its payload is
/// arbitrary error text (clap help output spans many lines) — collapse it so a
/// diagnostic can never masquerade as, or interrupt, another protocol line.
fn emit_ipc_error(message: &str) {
    let flat = message
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ");
    emit!("CLOWD_IPC_ERROR {flat}");
}

/// `Pause` stops the hotkeys while the shell records a gesture. It is applied
/// on the reader thread, so it takes effect even mid-capture, and is never
/// forwarded.
#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
enum Message {
    Settings { args: Vec<String> },
    Capture { mode: String },
    Pause { paused: bool },
}

enum Input {
    Message(Message),
    Invalid(String),
}

pub struct Standby {
    input_rx: mpsc::Receiver<Input>,
    parent_gone: Arc<AtomicBool>,
    capture_active: Arc<AtomicBool>,
    hotkeys: StandbyHotkeys,
    last_statuses: serde_json::Map<String, serde_json::Value>,
}

impl Standby {
    pub fn new(proxy: EventLoopProxy<()>) -> anyhow::Result<Self> {
        let parent_gone = Arc::new(AtomicBool::new(false));
        let capture_active = Arc::new(AtomicBool::new(false));
        let hotkeys = StandbyHotkeys::new(proxy.clone());
        // Usually the process stays up for tray captures, reports the error on
        // every gesture and, on macOS, keeps retrying the hook. A failure only
        // this process has (Windows) exits so the shell's crash accounting
        // falls back to its own clowd_hotkeys registrations.
        if let Some(err) = hotkeys.listener_error() {
            if hotkeys.should_bail() {
                anyhow::bail!("standby hotkey hook unavailable: {err}");
            }
        }

        let (input_tx, input_rx) = mpsc::channel();
        let parent_gone_reader = Arc::clone(&parent_gone);
        let capture_active_reader = Arc::clone(&capture_active);
        let pause = hotkeys.pause_handle();
        std::thread::Builder::new()
            .name("standby-ipc".into())
            .spawn(move || {
                for line in std::io::stdin().lock().lines() {
                    match line {
                        Ok(line) if !line.trim().is_empty() => {
                            let input = match serde_json::from_str(&line) {
                                Ok(Message::Pause {
                                    paused,
                                }) => {
                                    pause.set_paused(paused);
                                    continue;
                                }
                                Ok(message) => Input::Message(message),
                                Err(err) => Input::Invalid(err.to_string()),
                            };
                            if input_tx.send(input).is_err() {
                                return;
                            }
                            let _ = proxy.send_event(());
                        }
                        Ok(_) => {}
                        Err(err) => {
                            let _ = input_tx.send(Input::Invalid(err.to_string()));
                            break;
                        }
                    }
                }
                // SeqCst pairs with wait()'s store(capture_active)-then-load(parent_gone):
                // the total order guarantees at least one side sees the other, so an EOF
                // racing a hotkey either exits here or is caught by wait()'s re-check —
                // never a fully orphaned capture with this kill switch already gone.
                parent_gone_reader.store(true, Ordering::SeqCst);
                let _ = proxy.send_event(());
                if capture_active_reader.load(Ordering::SeqCst) {
                    std::process::exit(0);
                }
            })?;
        Ok(Self {
            input_rx,
            parent_gone,
            capture_active,
            hotkeys,
            last_statuses: serde_json::Map::new(),
        })
    }

    /// Returns false when the shell closed stdin before a capture was requested.
    pub fn wait(&mut self, args: &mut CliArgs, event_loop: &mut EventLoop<()>) -> anyhow::Result<bool> {
        loop {
            self.capture_active
                .store(false, Ordering::SeqCst);

            // The hotkeys stay hooked while the overlay is active, and everything
            // queued in the channels meanwhile is stale and dropped: a raced capture
            // request was superseded by the overlay the user just saw, and the shell
            // re-sends its complete settings snapshot after every READY.
            self.hotkeys.drain();
            while self.input_rx.try_recv().is_ok() {}

            emit!("CLOWD_STANDBY_READY");
            let mut app = StandbyApp {
                args,
                hotkeys: &mut self.hotkeys,
                last_statuses: &mut self.last_statuses,
                input_rx: &self.input_rx,
                parent_gone: &self.parent_gone,
                selected: None,
                hook_died: None,
            };
            event_loop.run_app_on_demand(&mut app)?;

            if let Some(reason) = app.hook_died {
                // A standby with a dead hook is a healthy-looking process whose
                // hotkeys never fire — exit, so the shell's crash accounting
                // restarts us or falls back to its own hotkeys.
                anyhow::bail!("standby hotkey hook died: {reason}");
            }
            let Some(selected) = app.selected else {
                return Ok(false);
            };
            self.capture_active
                .store(true, Ordering::SeqCst);
            if self.parent_gone.load(Ordering::SeqCst) {
                // EOF raced the hotkey and the reader thread may already be gone —
                // without it the mid-capture kill switch is disarmed, so don't start.
                return Ok(false);
            }
            let root = args
                .session_root
                .as_deref()
                .expect("clap requires session root");
            match create_session_dir(root) {
                Ok(session) => {
                    args.session_dir = Some(session.clone());
                    args.capture_mode = selected;
                    emit!("CLOWD_SESSION {}", session.display());
                    return Ok(true);
                }
                Err(err) => {
                    // Environmental (root deleted, disk full, ACL): not worth a process
                    // exit that burns one of the shell's crash-fallback strikes. Report
                    // and go back to waiting; the shell clears its overlay gate on this.
                    log::error!("failed to create session dir in {}: {err}", root.display());
                    emit_ipc_error(&format!("failed to create session dir: {err}"));
                }
            }
        }
    }
}

struct StandbyApp<'a> {
    args: &'a mut CliArgs,
    hotkeys: &'a mut StandbyHotkeys,
    last_statuses: &'a mut serde_json::Map<String, serde_json::Value>,
    input_rx: &'a mpsc::Receiver<Input>,
    parent_gone: &'a AtomicBool,
    selected: Option<CaptureMode>,
    /// Set when the hook died where that is fatal (see `StandbyHotkeys::poll`).
    hook_died: Option<String>,
}

impl StandbyApp<'_> {
    fn apply_settings(&mut self, raw_args: Vec<String>) {
        let rejection = match CliArgs::try_parse_from(std::iter::once("clowd_capture".to_string()).chain(raw_args)) {
            Ok(next) if next.standby => {
                let statuses = self.hotkeys.apply(&next);
                *self.args = next;
                *self.last_statuses = statuses.clone();
                report_settings_status(statuses);
                return;
            }
            Ok(_) => "settings snapshot must include --standby".to_string(),
            Err(err) => err.to_string(),
        };
        // A rejected snapshot changes nothing: the previous hotkeys stay hooked,
        // so replay their last reported state (exact, error text included)
        // rather than pretending they broke.
        emit_ipc_error(&format!("rejected settings snapshot: {rejection}"));
        if !self.last_statuses.is_empty() {
            report_settings_status(self.last_statuses.clone());
        }
    }

    /// One pass over everything that can need attention. Shared by `about_to_wait`
    /// and `user_event`: on Windows, `exit()` is only observed when it is set inside
    /// message dispatch — a wake-up whose work was deferred to the next
    /// `about_to_wait` would park the loop in an INFINITE wait with the exit
    /// decision already made. An exit decided in `about_to_wait` itself (a hook
    /// death whose wake a capture cycle swallowed) is why every exit also sets
    /// the control flow: see `control_flow`.
    fn process_pending(&mut self, event_loop: &ActiveEventLoop) {
        // The loop dispatches one final AboutToWait while unwinding after exit() —
        // a press landing in that instant must not overwrite the selection or apply
        // settings mid-teardown; it is drained as stale at the next wait().
        if self.selected.is_some() || event_loop.exiting() {
            return;
        }
        let exit = self.pass();
        event_loop.set_control_flow(control_flow(exit, self.hotkeys.next_retry()));
        if exit {
            event_loop.exit();
        }
    }

    /// The work of `process_pending`; true when the loop must exit.
    fn pass(&mut self) -> bool {
        if self.parent_gone.load(Ordering::SeqCst) {
            return true;
        }
        // Only here, between captures, so a hook that dies under an open
        // overlay never costs the capture: the death waits for the next wait().
        match self.hotkeys.poll(Instant::now()) {
            Poll::Unchanged => {}
            Poll::Statuses(statuses) => {
                *self.last_statuses = statuses.clone();
                report_settings_status(statuses);
            }
            Poll::Fatal(reason) => {
                self.hook_died = Some(reason);
                return true;
            }
        }
        while let Ok(input) = self.input_rx.try_recv() {
            match input {
                Input::Message(Message::Settings {
                    args,
                }) => self.apply_settings(args),
                Input::Message(Message::Capture {
                    mode,
                }) => {
                    self.selected = match mode.as_str() {
                        "region" => Some(CaptureMode::Region),
                        "window" => Some(CaptureMode::Window),
                        "screen" | "monitor" => Some(CaptureMode::Screen),
                        _ => {
                            emit_ipc_error(&format!("unknown capture mode '{mode}'"));
                            None
                        }
                    };
                    if self.selected.is_some() {
                        return true;
                    }
                }
                // Applied on the reader thread; never forwarded here.
                Input::Message(Message::Pause {
                    ..
                }) => {}
                Input::Invalid(err) => {
                    emit_ipc_error(&err);
                }
            }
        }
        if let Some(mode) = self.hotkeys.try_recv() {
            self.selected = Some(mode);
            return true;
        }
        false
    }
}

/// The control flow after a pass. Exiting must not leave `Wait`: Windows
/// checks the exit code only after the wait returns, so an exit decided in
/// `about_to_wait` with nothing else queued would block INFINITE. `Poll`
/// makes that wait return at once (the capture cycle sets its own flow).
/// Otherwise blocked indefinitely unless a hook that is down is due a retry.
fn control_flow(exit: bool, next_retry: Option<Instant>) -> ControlFlow {
    match next_retry {
        _ if exit => ControlFlow::Poll,
        Some(at) => ControlFlow::WaitUntil(at),
        None => ControlFlow::Wait,
    }
}

impl ApplicationHandler for StandbyApp<'_> {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        event_loop.set_control_flow(ControlFlow::Wait);
    }

    fn user_event(&mut self, event_loop: &ActiveEventLoop, _event: ()) {
        self.process_pending(event_loop);
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        self.process_pending(event_loop);
    }

    fn window_event(&mut self, _event_loop: &ActiveEventLoop, _window_id: winit::window::WindowId, _event: winit::event::WindowEvent) {}
}

fn report_settings_status(hotkeys: serde_json::Map<String, serde_json::Value>) {
    emit!("CLOWD_SETTINGS_STATUS {}", serde_json::json!({ "hotkeys": hotkeys }));
}

fn create_session_dir(root: &Path) -> anyhow::Result<PathBuf> {
    std::fs::create_dir_all(root)?;
    let stamp = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)?
        .as_nanos();
    let path = root.join(format!("capture-{stamp}-{}", std::process::id()));
    std::fs::create_dir(&path)?;
    Ok(path)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// An exit decided in `about_to_wait` (a hook death first seen there, the
    /// shell gone, a selection) must not leave the loop to block in `Wait`,
    /// where Windows never gets to check the exit code.
    #[test]
    fn an_exit_never_waits() {
        let at = Instant::now() + std::time::Duration::from_secs(2);
        assert_eq!(control_flow(true, None), ControlFlow::Poll);
        assert_eq!(control_flow(true, Some(at)), ControlFlow::Poll);
        assert_eq!(control_flow(false, Some(at)), ControlFlow::WaitUntil(at));
        assert_eq!(control_flow(false, None), ControlFlow::Wait);
    }
}
