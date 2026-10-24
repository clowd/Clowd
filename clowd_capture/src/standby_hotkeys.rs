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
    /// per-gesture statuses for CLOWD_SETTINGS_STATUS. Hook-based hotkeys
    /// cannot conflict with other applications, so a gesture is active exactly
    /// when it parses, is not a duplicate, and the listener is running.
    pub fn apply(&self, args: &CliArgs) -> serde_json::Map<String, serde_json::Value> {
        let requested = [
            ("main", args.hk_main.as_slice(), CaptureMode::Region),
            ("window", args.hk_window.as_slice(), CaptureMode::Window),
            ("monitor", args.hk_monitor.as_slice(), CaptureMode::Screen),
        ];
        let plan = plan_hotkeys(&requested, self.listener_error.as_deref());

        // matches first, blocking second. The invariant this ordering buys: a
        // CONFIGURED hotkey never loses a capture — a press racing the writes
        // may go un-suppressed once (leaks to the OS) but still triggers. The
        // cost falls only on a REMOVED hotkey, whose final racing press can be
        // suppressed without triggering (one swallowed keystroke); the reversed
        // order would instead drop captures for just-added hotkeys. Both sets
        // are swapped whole, so this holds for every gesture of every mode.
        *self
            .matches
            .lock()
            .unwrap_or_else(|e| e.into_inner()) = plan.matches;
        *self
            .blocking
            .lock()
            .unwrap_or_else(|e| e.into_inner()) = plan.blocking;
        plan.statuses
    }

    pub fn try_recv(&self) -> Option<CaptureMode> {
        self.rx.try_recv().ok()
    }

    pub fn drain(&self) {
        while self.rx.try_recv().is_ok() {}
    }
}

/// The outcome of one settings snapshot, computed without touching the hook
/// so the registration rules can be tested without a live listener.
struct HotkeyPlan {
    blocking: HashSet<Hotkey>,
    matches: Vec<(Hotkey, CaptureMode)>,
    statuses: serde_json::Map<String, serde_json::Value>,
}

/// Resolves every gesture of every mode, in request order. Each name maps to
/// an array with one `{gesture, active, error}` entry per gesture received,
/// and "gesture" echoes the received string verbatim — the shell matches
/// statuses back to its own gestures by that string, so it must never be
/// normalized here.
fn plan_hotkeys(requested: &[(&str, &[String], CaptureMode)], listener_error: Option<&str>) -> HotkeyPlan {
    let mut blocking = HashSet::new();
    let mut matches = Vec::new();
    let mut statuses = serde_json::Map::new();
    for &(name, gestures, mode) in requested {
        let mut entries = Vec::with_capacity(gestures.len());
        for gesture in gestures {
            let (active, error) = match gesture.parse::<Hotkey>() {
                Err(err) => (false, Some(err.to_string())),
                // handy-keys accepts modifier-only hotkeys ("Control+Shift"); the
                // shell can never emit one, and hooking it would block bare
                // modifier presses system-wide — reject rather than register.
                Ok(hotkey) if hotkey.key.is_none() => (false, Some("gesture has no key".into())),
                // The shell already gives each gesture to at most one action, so
                // this is defense in depth. The forwarding thread takes the FIRST
                // matching entry, so a second registration of the same hotkey
                // could never fire — report it rather than claim it active.
                // Compared parsed, so a respelling ("Shift+Control+A") counts as
                // the gesture it duplicates.
                Ok(hotkey) if blocking.contains(&hotkey) => (false, Some("duplicate gesture".into())),
                Ok(hotkey) => match listener_error {
                    Some(err) => (false, Some(err.to_string())),
                    None => {
                        blocking.insert(hotkey);
                        matches.push((hotkey, mode));
                        (true, None)
                    }
                },
            };
            entries.push(serde_json::json!({ "gesture": gesture, "active": active, "error": error }));
        }
        statuses.insert(name.into(), serde_json::Value::Array(entries));
    }
    HotkeyPlan {
        blocking,
        matches,
        statuses,
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

    fn gestures(list: &[&str]) -> Vec<String> {
        list.iter().map(|g| g.to_string()).collect()
    }

    fn plan(main: &[&str], window: &[&str], monitor: &[&str], listener_error: Option<&str>) -> HotkeyPlan {
        let (main, window, monitor) = (gestures(main), gestures(window), gestures(monitor));
        plan_hotkeys(
            &[
                ("main", &main, CaptureMode::Region),
                ("window", &window, CaptureMode::Window),
                ("monitor", &monitor, CaptureMode::Screen),
            ],
            listener_error,
        )
    }

    fn hotkey(gesture: &str) -> Hotkey {
        gesture.parse().unwrap()
    }

    #[test]
    fn registers_every_gesture_of_a_mode() {
        let plan = plan(&["PrintScreen", "Control+Shift+1"], &["Alt+PrintScreen"], &[], None);
        assert_eq!(
            plan.matches,
            vec![
                (hotkey("PrintScreen"), CaptureMode::Region),
                (hotkey("Control+Shift+1"), CaptureMode::Region),
                (hotkey("Alt+PrintScreen"), CaptureMode::Window),
            ]
        );
        // blocking and matches must stay the same set, or a press could be
        // suppressed without triggering (or trigger without being suppressed)
        assert_eq!(plan.blocking.len(), 3);
        assert!(plan
            .matches
            .iter()
            .all(|(h, _)| plan.blocking.contains(h)));
    }

    #[test]
    fn empty_lists_register_nothing_but_still_report() {
        let plan = plan(&[], &[], &[], None);
        assert!(plan.matches.is_empty());
        assert!(plan.blocking.is_empty());
        assert_eq!(
            serde_json::Value::Object(plan.statuses),
            serde_json::json!({ "main": [], "window": [], "monitor": [] })
        );
    }

    #[test]
    fn duplicate_across_modes_keeps_the_first() {
        let plan = plan(
            &["PrintScreen"],
            &["Control+Shift+A"],
            &["Control+Shift+A", "PrintScreen", "Shift+Control+A"],
            None,
        );
        assert_eq!(
            plan.matches,
            vec![
                (hotkey("PrintScreen"), CaptureMode::Region),
                (hotkey("Control+Shift+A"), CaptureMode::Window),
            ]
        );
        assert_eq!(plan.statuses["window"][0]["active"], true);
        for (i, gesture) in ["Control+Shift+A", "PrintScreen", "Shift+Control+A"]
            .into_iter()
            .enumerate()
        {
            assert_eq!(
                plan.statuses["monitor"][i],
                serde_json::json!({ "gesture": gesture, "active": false, "error": "duplicate gesture" })
            );
        }
    }

    #[test]
    fn duplicate_within_a_mode_keeps_the_first() {
        let plan = plan(&["PrintScreen", "PrintScreen"], &[], &[], None);
        assert_eq!(plan.matches.len(), 1);
        assert_eq!(plan.statuses["main"][0]["active"], true);
        assert_eq!(plan.statuses["main"][1]["error"], "duplicate gesture");
    }

    /// The shell maps statuses back by the echoed string, in order, so the
    /// echo must be the exact input — rejected gestures included.
    #[test]
    fn status_shape_echoes_gestures_in_order() {
        let plan = plan(&["Control+Shift", "Snapshot", "PrintScreen"], &[], &["Alt+F12"], None);
        let main = plan.statuses["main"].as_array().unwrap();
        assert_eq!(main.len(), 3);
        assert_eq!(
            main[0],
            serde_json::json!({ "gesture": "Control+Shift", "active": false, "error": "gesture has no key" })
        );
        assert_eq!(main[1]["gesture"], "Snapshot");
        assert_eq!(main[1]["active"], false);
        assert!(main[1]["error"].is_string());
        assert_eq!(
            main[2],
            serde_json::json!({ "gesture": "PrintScreen", "active": true, "error": null })
        );
        assert_eq!(plan.statuses["window"], serde_json::json!([]));
        assert_eq!(
            plan.statuses["monitor"],
            serde_json::json!([{ "gesture": "Alt+F12", "active": true, "error": null }])
        );
        assert_eq!(plan.matches.len(), 2);
    }

    #[test]
    fn listener_error_reports_on_every_gesture() {
        let plan = plan(&["PrintScreen", "Control+1"], &[], &[], Some("no accessibility"));
        assert!(plan.matches.is_empty());
        assert!(plan.blocking.is_empty());
        for status in plan.statuses["main"].as_array().unwrap() {
            assert_eq!(status["active"], false);
            assert_eq!(status["error"], "no accessibility");
        }
    }

    /// Repeated flags keep their order, and the stdin snapshot goes through
    /// this same parse, so both paths share the shape.
    #[test]
    fn cli_flags_are_repeatable() {
        use clap::Parser;
        let args = CliArgs::try_parse_from([
            "clowd_capture",
            "--standby",
            "--session-root",
            "x",
            "--hk-main",
            "PrintScreen",
            "--hk-monitor",
            "Control+PrintScreen",
            "--hk-main",
            "Control+Shift+1",
        ])
        .unwrap();
        assert_eq!(args.hk_main, ["PrintScreen", "Control+Shift+1"]);
        assert!(args.hk_window.is_empty());
        assert_eq!(args.hk_monitor, ["Control+PrintScreen"]);
    }
}
