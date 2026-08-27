//! Global screenshot hotkeys, owned by this process rather than the shell.
//!
//! Two hard requirements shape this module. First, the trigger path must not
//! depend on the C# shell being resident — a paged-out shell would add its
//! page-in time to every capture. Second, the key must be SUPPRESSED, not
//! merely observed: Windows 11's "PrintScreen opens Snipping Tool" feature
//! consumes the key ahead of RegisterHotKey delivery, so an ordinary hotkey
//! registration reports success and then never fires. handy-keys provides
//! libuiohook-style low-level hooks (WH_KEYBOARD_LL / CGEventTap / evdev)
//! with synchronous blocking, which beats the OS handler the same way the
//! shell's SharpHook hook used to.
//!
//! Trigger matching deliberately reuses the exact rule the hook applies for
//! blocking (`Hotkey::modifiers.matches() && key ==`), so a press can never
//! be suppressed without also starting a capture, or vice versa.

use std::collections::HashSet;
use std::sync::{mpsc, Arc, Mutex};

use handy_keys::{BlockingHotkeys, Hotkey, KeyboardListener};
use winit::event_loop::EventLoopProxy;

use crate::settings::{CaptureMode, CliArgs};

pub struct StandbyHotkeys {
    /// Shared with the hook, which checks it synchronously per key event.
    blocking: BlockingHotkeys,
    /// Shared with the forwarding thread, which maps a matched press to a mode.
    matches: Arc<Mutex<Vec<(Hotkey, CaptureMode)>>>,
    rx: mpsc::Receiver<CaptureMode>,
    /// Why the listener could not start (missing macOS accessibility, missing
    /// /dev/uinput access on Linux, hook failure). Reported per hotkey so the
    /// shell's settings UI shows the real, actionable reason.
    listener_error: Option<String>,
}

impl StandbyHotkeys {
    pub fn new(proxy: EventLoopProxy<()>) -> Self {
        let blocking: BlockingHotkeys = Arc::new(Mutex::new(HashSet::new()));
        let matches: Arc<Mutex<Vec<(Hotkey, CaptureMode)>>> = Arc::new(Mutex::new(Vec::new()));
        let (tx, rx) = mpsc::channel();

        let listener_error = match KeyboardListener::new_with_blocking(Arc::clone(&blocking)) {
            Err(err) => Some(err.to_string()),
            Ok(listener) => {
                let matches_reader = Arc::clone(&matches);
                let spawned = std::thread::Builder::new()
                    .name("standby-hotkeys".into())
                    .spawn(move || {
                        while let Ok(event) = listener.recv() {
                            if !event.is_key_down {
                                continue;
                            }
                            let mode = matches_reader
                                .lock()
                                .unwrap_or_else(|e| e.into_inner())
                                .iter()
                                .find(|(hotkey, _)| hotkey.modifiers.matches(event.modifiers) && hotkey.key == event.key)
                                .map(|(_, mode)| *mode);
                            if let Some(mode) = mode {
                                if tx.send(mode).is_err() {
                                    return;
                                }
                                let _ = proxy.send_event(());
                            }
                        }
                        // recv() fails only when the hook side dropped its sender — on
                        // Windows that includes SetWindowsHookExW failing INSIDE the
                        // crate's hook thread (its spawn "succeeds" regardless) and a
                        // later hook-thread death. A standby with a dead hook is a
                        // healthy-looking process whose hotkeys never fire and whose
                        // statuses would keep claiming active — exit instead, so the
                        // shell's crash accounting restarts us or falls back to its
                        // own SharpHook hotkeys.
                        eprintln!("standby hotkey hook died; exiting so the shell can recover");
                        std::process::exit(1);
                    });
                match spawned {
                    Ok(_) => None,
                    Err(err) => Some(format!("failed to start hotkey thread: {err}")),
                }
            }
        };

        Self {
            blocking,
            matches,
            rx,
            listener_error,
        }
    }

    /// Why the hook could not start, if it couldn't. macOS keeps running in
    /// this state (SharpHook needs the same Accessibility permission, so a
    /// fallback would gain nothing and the per-hotkey error is actionable);
    /// everywhere else the caller should treat it as fatal so the shell falls
    /// back to hotkeys that can work.
    pub fn listener_error(&self) -> Option<&str> {
        self.listener_error.as_deref()
    }

    /// Replaces the whole gesture set from a CLI snapshot and returns the
    /// per-hotkey statuses for CLOWD_SETTINGS_STATUS. Hook-based hotkeys
    /// cannot conflict with other applications, so a gesture is active exactly
    /// when it parses and the listener is running.
    pub fn apply(&self, args: &CliArgs) -> serde_json::Map<String, serde_json::Value> {
        let requested = [
            ("main", args.hk_main.as_deref(), CaptureMode::Region),
            ("window", args.hk_window.as_deref(), CaptureMode::Window),
            ("monitor", args.hk_monitor.as_deref(), CaptureMode::Screen),
        ];

        let mut next_blocking = HashSet::new();
        let mut next_matches = Vec::new();
        let mut statuses = serde_json::Map::new();
        for (name, gesture, mode) in requested {
            let (active, error) = match gesture {
                None => (false, None),
                Some(gesture) => match gesture.parse::<Hotkey>() {
                    Err(err) => (false, Some(err.to_string())),
                    // handy-keys accepts modifier-only hotkeys ("Control+Shift"); the
                    // shell can never emit one, and hooking it would block bare
                    // modifier presses system-wide — reject rather than register.
                    Ok(hotkey) if hotkey.key.is_none() => (false, Some("gesture has no key".into())),
                    Ok(hotkey) => match &self.listener_error {
                        Some(err) => (false, Some(err.clone())),
                        None => {
                            next_blocking.insert(hotkey);
                            next_matches.push((hotkey, mode));
                            (true, None)
                        }
                    },
                },
            };
            statuses.insert(name.into(), serde_json::json!({ "active": active, "error": error }));
        }

        // matches first, blocking second. The invariant this ordering buys: a
        // CONFIGURED hotkey never loses a capture — a press racing the writes
        // may go un-suppressed once (leaks to the OS) but still triggers. The
        // cost falls only on a REMOVED hotkey, whose final racing press can be
        // suppressed without triggering (one swallowed keystroke); the reversed
        // order would instead drop captures for just-added hotkeys.
        *self
            .matches
            .lock()
            .unwrap_or_else(|e| e.into_inner()) = next_matches;
        *self
            .blocking
            .lock()
            .unwrap_or_else(|e| e.into_inner()) = next_blocking;
        statuses
    }

    pub fn try_recv(&self) -> Option<CaptureMode> {
        self.rx.try_recv().ok()
    }

    pub fn drain(&self) {
        while self.rx.try_recv().is_ok() {}
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The exact strings SimpleKeyGesture.ToCapturerString (CapturerKeyMap in
    /// Clowd.Shared) produces must parse in handy-keys' grammar — this map is
    /// the wire contract between the shell and the capturer.
    #[test]
    fn parses_capturer_gestures() {
        for gesture in [
            "PrintScreen",
            "Control+Shift+PrintScreen",
            "Alt+F12",
            "Control+A",
            "Control+1",
            "Super+Minus",
            "Control+Alt+Grave",
            "Shift+KeypadPlus",
            "Left",
            "Control+Space",
            "Control+PageDown",
            "Shift+Backspace",
            "Control+ForwardDelete",
            "Control+Semicolon",
            "Control+LeftBracket",
            "Control+Backslash",
            "Control+Quote",
            "Control+Comma",
            "Control+Period",
            "Control+Slash",
            "Control+Equal",
            "Control+Keypad7",
            "Control+KeypadMultiply",
            "Control+ContextMenu",
            "PlayPause",
            "Control+F24",
        ] {
            assert!(gesture.parse::<Hotkey>().is_ok(), "failed to parse {gesture}");
        }

        // raw Avalonia names must FAIL so the shell-side map stays the one
        // source of truth (a pass here would mean two grammars half-work)
        assert!("Snapshot".parse::<Hotkey>().is_err());
        assert!("OemMinus".parse::<Hotkey>().is_err());
        assert!("Control+D1".parse::<Hotkey>().is_err());
    }

    /// Blocking and triggering must share one matching rule; this pins the
    /// half of it we implement (the forwarding thread's lookup).
    #[test]
    fn match_rule_is_exact_on_modifiers() {
        let hotkey: Hotkey = "Control+Shift+PrintScreen".parse().unwrap();
        let pressed: Hotkey = "Control+Shift+PrintScreen".parse().unwrap();
        assert!(hotkey.modifiers.matches(pressed.modifiers));
        let wrong: Hotkey = "Control+PrintScreen".parse().unwrap();
        assert!(!hotkey.modifiers.matches(wrong.modifiers));
    }
}
