//! Cross-platform standby control plane: the `capture` catalog of Prisc's helper
//! protocol (crates/prisc_capture/HELPER_PROTOCOL.md §4.5). JSON lines on stdin carry
//! complete CLI snapshots and explicit capture requests; the quit path (`quit`, EOF,
//! a signal, a dead stdout) means the shell is done with us. The screenshot hotkeys
//! themselves are owned here too: see standby_hotkeys.rs for why they must live in
//! this process and use a low-level hook.

use std::ops::ControlFlow;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc};
use std::time::{Instant, SystemTime};

use clap::Parser;
use prisc_ipc::catalog::capture::{self, CaptureResult, Event};
use prisc_ipc::catalog::ErrorCode;
use prisc_ipc::Inbound;
use winit::application::ApplicationHandler;
use winit::event_loop::{ActiveEventLoop, ControlFlow as LoopFlow, EventLoop, EventLoopProxy};
use winit::platform::run_on_demand::EventLoopExtRunOnDemand;

use crate::protocol::out;
use crate::settings::{CaptureMode, CliArgs};
use crate::standby_hotkeys::{Poll, StandbyHotkeys};

/// What the event loop is handed from stdin. `pause` never gets here: it stops
/// the hotkeys while the shell records a gesture, so it is applied on the
/// reader thread and takes effect even mid-capture.
enum Input {
    Settings(Vec<String>),
    Capture(CaptureMode),
    /// A malformed line, answered with `bad_command` in arrival order.
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
        // falls back to its own prisc_hotkeys registrations.
        if let Some(err) = hotkeys.listener_error() {
            if hotkeys.should_bail() {
                anyhow::bail!("standby hotkey hook unavailable: {err}");
            }
        }

        // The quit path. Between captures it wakes the loop, which returns
        // from `wait` and ends with `done{stopped}`. Mid-capture the overlay
        // owns the main thread and `wait` is not listening, so the process
        // ends from here: the overlay closes without writing the session.
        // SeqCst pairs with wait()'s store(capture_active)-then-load(parent_gone):
        // the total order guarantees at least one side sees the other, so a
        // quit racing a hotkey either exits here or is caught by wait()'s
        // re-check, never a fully orphaned capture.
        let parent_gone_quit = Arc::clone(&parent_gone);
        let capture_active_quit = Arc::clone(&capture_active);
        let proxy_quit = proxy.clone();
        prisc_ipc::lifetime::install(
            move |reason| {
                parent_gone_quit.store(true, Ordering::SeqCst);
                let _ = proxy_quit.send_event(());
                if capture_active_quit.load(Ordering::SeqCst) {
                    let spawned = std::thread::Builder::new()
                        .name("standby-quit".into())
                        .spawn(move || {
                            log::info!("quit ({reason:?}) during a capture; closing the overlay");
                            crate::finish(CaptureResult::Stopped, None)
                        });
                    if spawned.is_err() {
                        prisc_ipc::lifetime::exit(prisc_ipc::exit::OK);
                    }
                }
            },
            capture::QUIT_GRACE + prisc_ipc::SELF_EXIT_MARGIN,
        );

        let (input_tx, input_rx) = mpsc::channel();
        let pause = hotkeys.pause_handle();
        prisc_ipc::read_commands::<capture::Command>(move |inbound| {
            let input = match inbound {
                Inbound::Command(capture::Command::Pause {
                    paused,
                }) => {
                    pause.set_paused(paused);
                    return ControlFlow::Continue(());
                }
                Inbound::Command(capture::Command::Settings {
                    args,
                }) => Input::Settings(args),
                Inbound::Command(capture::Command::Capture {
                    mode,
                }) => Input::Capture(match mode {
                    capture::CaptureMode::Region => CaptureMode::Region,
                    capture::CaptureMode::Screen => CaptureMode::Screen,
                    capture::CaptureMode::Window => CaptureMode::Window,
                }),
                Inbound::Malformed {
                    reason,
                } => Input::Invalid(reason),
            };
            if input_tx.send(input).is_err() {
                return ControlFlow::Break(());
            }
            let _ = proxy.send_event(());
            ControlFlow::Continue(())
        });
        Ok(Self {
            input_rx,
            parent_gone,
            capture_active,
            hotkeys,
            last_statuses: serde_json::Map::new(),
        })
    }

    /// Returns false when the quit path fired before a capture was requested.
    pub fn wait(&mut self, args: &mut CliArgs, event_loop: &mut EventLoop<()>) -> anyhow::Result<bool> {
        loop {
            self.capture_active
                .store(false, Ordering::SeqCst);

            // The hotkeys stay hooked while the overlay is active, and everything
            // queued in the channels meanwhile is stale: a raced capture request
            // was superseded by the overlay the user just saw, and the shell
            // re-sends its complete settings snapshot after every `ready`. Capture
            // requests and malformed lines are still answered, in arrival order.
            self.hotkeys.drain();
            while let Ok(input) = self.input_rx.try_recv() {
                match input {
                    Input::Capture(_) => out().error(ErrorCode::Rejected, "a capture was already open", false),
                    Input::Invalid(reason) => out().error(ErrorCode::BadCommand, reason, false),
                    Input::Settings(_) => {}
                }
            }

            out().emit(&Event::Ready {});
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
                    out().emit(&Event::Session {
                        dir: session.to_string_lossy().into_owned(),
                    });
                    return Ok(true);
                }
                Err(err) => {
                    // Environmental (root deleted, disk full, ACL): not worth a process
                    // exit that burns one of the shell's crash-fallback strikes. Report
                    // and go back to waiting; the shell clears its overlay gate on this.
                    log::error!("failed to create session dir in {}: {err}", root.display());
                    out().error(ErrorCode::IoFailed, format!("failed to create session dir: {err}"), false);
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
        let rejection = match CliArgs::try_parse_from(std::iter::once("prisc_capture".to_string()).chain(raw_args)) {
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
        out().error(ErrorCode::Rejected, format!("rejected settings snapshot: {rejection}"), false);
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
                Input::Settings(args) => self.apply_settings(args),
                Input::Capture(mode) => {
                    self.selected = Some(mode);
                    return true;
                }
                Input::Invalid(reason) => out().error(ErrorCode::BadCommand, reason, false),
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
fn control_flow(exit: bool, next_retry: Option<Instant>) -> LoopFlow {
    match next_retry {
        _ if exit => LoopFlow::Poll,
        Some(at) => LoopFlow::WaitUntil(at),
        None => LoopFlow::Wait,
    }
}

impl ApplicationHandler for StandbyApp<'_> {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        event_loop.set_control_flow(LoopFlow::Wait);
    }

    fn user_event(&mut self, event_loop: &ActiveEventLoop, _event: ()) {
        self.process_pending(event_loop);
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        self.process_pending(event_loop);
    }

    fn window_event(&mut self, _event_loop: &ActiveEventLoop, _window_id: winit::window::WindowId, _event: winit::event::WindowEvent) {}
}

/// `settings_status`. The statuses come from standby_hotkeys as JSON objects in
/// exactly the record's shape (`{gesture, active, error, error_code}` per
/// gesture, under `main`, `window` and `monitor`).
fn report_settings_status(hotkeys: serde_json::Map<String, serde_json::Value>) {
    match serde_json::from_value::<capture::Hotkeys>(serde_json::Value::Object(hotkeys)) {
        Ok(hotkeys) => {
            out().emit(&Event::SettingsStatus {
                hotkeys,
            });
        }
        Err(err) => log::error!("hotkey statuses do not match the protocol's shape: {err}"),
    }
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
        assert_eq!(control_flow(true, None), LoopFlow::Poll);
        assert_eq!(control_flow(true, Some(at)), LoopFlow::Poll);
        assert_eq!(control_flow(false, Some(at)), LoopFlow::WaitUntil(at));
        assert_eq!(control_flow(false, None), LoopFlow::Wait);
    }
}
