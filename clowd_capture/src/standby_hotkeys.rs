//! Global screenshot hotkeys, owned by this process rather than the shell.
//!
//! Two hard requirements shape this module. First, the trigger path must not
//! depend on the C# shell being resident — a paged-out shell would add its
//! page-in time to every capture. Second, the key must be SUPPRESSED, not
//! merely observed: Windows 11's "PrintScreen opens Snipping Tool" feature
//! consumes the key ahead of RegisterHotKey delivery, so an ordinary hotkey
//! registration reports success and then never fires. clowd_hotkeys provides
//! a low-level hook (WH_KEYBOARD_LL / CGEventTap) that swallows a registered
//! press before any OS handler sees it.
//!
//! Matching and suppression share one snapshot of the registrations inside
//! the hook callback, so a press is never swallowed without firing, or fired
//! without being swallowed. This module only maps the fired registration to
//! its capture mode. While the shell records a gesture in its settings UI it
//! pauses this hook over stdin (`{"type":"pause"}`), so the press it records
//! reaches the shell's own hook instead of starting a capture here.

use std::collections::HashMap;
use std::sync::{mpsc, Arc, Mutex};

use clowd_hotkeys::{Error, Event, Gesture, HotkeyId, Hotkeys};
use winit::event_loop::EventLoopProxy;

use crate::settings::{CaptureMode, CliArgs};

pub struct StandbyHotkeys {
    /// None when the hook could not start (see `start_error`).
    hotkeys: Option<Arc<Hotkeys>>,
    /// Why the hook could not start (missing macOS Accessibility, no Linux
    /// backend, hook failure). Reported per hotkey so the shell's settings UI
    /// shows the real, actionable reason.
    start_error: Option<String>,
    /// Whether that failure is specific to this process (see `should_bail`).
    bail: bool,
    /// What the hook currently holds.
    registered: Mutex<HashMap<Gesture, HotkeyId>>,
    /// Read by the forwarding thread to turn a fired registration into a mode.
    modes: Arc<Mutex<HashMap<HotkeyId, CaptureMode>>>,
    rx: mpsc::Receiver<CaptureMode>,
}

impl StandbyHotkeys {
    pub fn new(proxy: EventLoopProxy<()>) -> Self {
        let modes: Arc<Mutex<HashMap<HotkeyId, CaptureMode>>> = Arc::default();
        let (tx, rx) = mpsc::channel();

        let started = Hotkeys::start().and_then(|(hotkeys, events)| {
            let modes_reader = Arc::clone(&modes);
            std::thread::Builder::new()
                .name("standby-hotkeys".into())
                .spawn(move || {
                    while let Some(event) = events.recv() {
                        match event {
                            Event::Hotkey {
                                id,
                                repeat: false,
                            } => {
                                let mode = modes_reader
                                    .lock()
                                    .unwrap_or_else(|e| e.into_inner())
                                    .get(&id)
                                    .copied();
                                if let Some(mode) = mode {
                                    if tx.send(mode).is_err() {
                                        return;
                                    }
                                    let _ = proxy.send_event(());
                                }
                            }
                            // A standby with a dead hook is a healthy-looking process
                            // whose hotkeys never fire and whose statuses would keep
                            // claiming active — exit instead, so the shell's crash
                            // accounting restarts us or falls back to its own hotkeys.
                            Event::Stopped(reason) => {
                                eprintln!("standby hotkey hook died ({reason}); exiting so the shell can recover");
                                std::process::exit(1);
                            }
                            _ => {}
                        }
                    }
                    // The queue closed because Hotkeys was dropped on a normal
                    // shutdown (stdin EOF → main returns): not a failure.
                })
                .map(|_| Arc::new(hotkeys))
                .map_err(|err| Error::HookFailed(format!("failed to start the hotkey thread: {err}")))
        });

        let (hotkeys, start_error, bail) = match started {
            Ok(hotkeys) => (Some(hotkeys), None, false),
            Err(err) => {
                let bail = matches!(err, Error::HookFailed(_));
                (None, Some(err.to_string()), bail)
            }
        };
        Self {
            hotkeys,
            start_error,
            bail,
            registered: Mutex::default(),
            modes,
            rx,
        }
    }

    /// Why the hook could not start, if it couldn't.
    pub fn listener_error(&self) -> Option<&str> {
        self.start_error.as_deref()
    }

    /// True when the hook could not start for a reason another process would
    /// not share (`Error::HookFailed`) — standby then exits so the shell falls
    /// back to its own registrations. `Unsupported` and `PermissionDenied`
    /// would fail the same way in the shell, so they keep the process up.
    pub fn should_bail(&self) -> bool {
        self.bail
    }

    /// For the stdin reader thread: applies `{"type":"pause"}` immediately,
    /// even mid-capture. None without a hook, where there is nothing to pause.
    pub fn pause_handle(&self) -> Option<Arc<Hotkeys>> {
        self.hotkeys.clone()
    }

    /// Replaces the whole gesture set from a CLI snapshot and returns the
    /// per-gesture statuses for CLOWD_SETTINGS_STATUS. Hook-based hotkeys
    /// cannot conflict with other applications, so a gesture is active exactly
    /// when it parses, is usable on this OS, is not a duplicate, and the hook
    /// accepted it.
    pub fn apply(&self, args: &CliArgs) -> serde_json::Map<String, serde_json::Value> {
        let requested = [
            ("main", args.hk_main.as_slice(), CaptureMode::Region),
            ("window", args.hk_window.as_slice(), CaptureMode::Window),
            ("monitor", args.hk_monitor.as_slice(), CaptureMode::Screen),
        ];
        let mut plan = plan_hotkeys(&requested, self.start_error.as_deref());
        let Some(hotkeys) = &self.hotkeys else {
            return plan.statuses;
        };

        // Held for the whole apply, so the forwarding thread can never see a
        // registration without its mode. A gesture present in both snapshots
        // is never unregistered, so it keeps firing throughout.
        let mut modes = self
            .modes
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        let mut registered = self
            .registered
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        let mut kept = Vec::with_capacity(plan.wanted.len());
        for (&(gesture, mode), &(name, index)) in plan.wanted.iter().zip(&plan.slots) {
            if let Some(&id) = registered.get(&gesture) {
                modes.insert(id, mode);
                kept.push(gesture);
                continue;
            }
            match hotkeys.register(gesture) {
                Ok(id) => {
                    registered.insert(gesture, id);
                    modes.insert(id, mode);
                    kept.push(gesture);
                }
                Err(err) => {
                    if let Some(status) = plan
                        .statuses
                        .get_mut(name)
                        .and_then(|entries| entries.get_mut(index))
                    {
                        status["active"] = false.into();
                        status["error"] = err.to_string().into();
                    }
                }
            }
        }
        registered.retain(|gesture, id| {
            let keep = kept.contains(gesture);
            if !keep {
                hotkeys.unregister(*id);
                modes.remove(id);
            }
            keep
        });
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
struct HotkeyPlan<'a> {
    /// The gestures to hold, in request order, each with its mode.
    wanted: Vec<(Gesture, CaptureMode)>,
    /// Parallel to `wanted`: where its status sits in `statuses`, so a
    /// registration the hook refuses can still be reported in place.
    slots: Vec<(&'a str, usize)>,
    statuses: serde_json::Map<String, serde_json::Value>,
}

/// Resolves every gesture of every mode, in request order. Each name maps to
/// an array with one `{gesture, active, error}` entry per gesture received,
/// and "gesture" echoes the received string verbatim — the shell matches
/// statuses back to its own gestures by that string, so it must never be
/// normalized here.
fn plan_hotkeys<'a>(requested: &[(&'a str, &[String], CaptureMode)], start_error: Option<&str>) -> HotkeyPlan<'a> {
    let mut wanted: Vec<(Gesture, CaptureMode)> = Vec::new();
    let mut slots = Vec::new();
    let mut statuses = serde_json::Map::new();
    for &(name, gestures, mode) in requested {
        let mut entries = Vec::with_capacity(gestures.len());
        for gesture in gestures {
            let error = match gesture.parse::<Gesture>() {
                Err(err) => Some(err.to_string()),
                Ok(parsed) => match parsed.validate() {
                    Err(err) => Some(err.to_string()),
                    // The shell already gives each gesture to at most one action, so
                    // this is defense in depth: the hook refuses an equal gesture
                    // anyway. Compared parsed, so a respelling ("Shift+Control+A")
                    // counts as the gesture it duplicates.
                    Ok(()) if wanted.iter().any(|(g, _)| *g == parsed) => Some("duplicate gesture".into()),
                    Ok(()) => match start_error {
                        Some(err) => Some(err.to_string()),
                        None => {
                            wanted.push((parsed, mode));
                            slots.push((name, entries.len()));
                            None
                        }
                    },
                },
            };
            entries.push(serde_json::json!({ "gesture": gesture, "active": error.is_none(), "error": error }));
        }
        statuses.insert(name.into(), serde_json::Value::Array(entries));
    }
    HotkeyPlan {
        wanted,
        slots,
        statuses,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn gestures(list: &[&str]) -> Vec<String> {
        list.iter().map(|g| g.to_string()).collect()
    }

    fn plan(main: &[&str], window: &[&str], monitor: &[&str], start_error: Option<&str>) -> HotkeyPlan<'static> {
        let (main, window, monitor) = (gestures(main), gestures(window), gestures(monitor));
        plan_hotkeys(
            &[
                ("main", &main, CaptureMode::Region),
                ("window", &window, CaptureMode::Window),
                ("monitor", &monitor, CaptureMode::Screen),
            ],
            start_error,
        )
    }

    fn gesture(text: &str) -> Gesture {
        text.parse().unwrap()
    }

    // The tests below use keys that exist on every OS. PrintScreen and Cancel
    // have no macOS key code, so they are refused there (see
    // settings_form_gestures_parse), and CI runs this suite on macOS too.

    #[test]
    fn registers_every_gesture_of_a_mode() {
        let plan = plan(&["F12", "Control+Shift+D1"], &["Alt+F12"], &[], None);
        assert_eq!(
            plan.wanted,
            vec![
                (gesture("F12"), CaptureMode::Region),
                (gesture("Control+Shift+D1"), CaptureMode::Region),
                (gesture("Alt+F12"), CaptureMode::Window),
            ]
        );
        assert_eq!(plan.slots, vec![("main", 0), ("main", 1), ("window", 0)]);
    }

    #[test]
    fn empty_lists_register_nothing_but_still_report() {
        let plan = plan(&[], &[], &[], None);
        assert!(plan.wanted.is_empty());
        assert_eq!(
            serde_json::Value::Object(plan.statuses),
            serde_json::json!({ "main": [], "window": [], "monitor": [] })
        );
    }

    #[test]
    fn duplicate_across_modes_keeps_the_first() {
        let plan = plan(
            &["Return"],
            &["Control+Shift+A"],
            &["Control+Shift+A", "Return", "Shift+Control+A", "Enter"],
            None,
        );
        assert_eq!(
            plan.wanted,
            vec![
                (gesture("Return"), CaptureMode::Region),
                (gesture("Control+Shift+A"), CaptureMode::Window),
            ]
        );
        assert_eq!(plan.statuses["window"][0]["active"], true);
        // Compared parsed: a modifier respelling and a key alias both count.
        for (i, gesture) in ["Control+Shift+A", "Return", "Shift+Control+A", "Enter"]
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
        let plan = plan(&["F12", "F12"], &[], &[], None);
        assert_eq!(plan.wanted.len(), 1);
        assert_eq!(plan.statuses["main"][0]["active"], true);
        assert_eq!(plan.statuses["main"][1]["error"], "duplicate gesture");
    }

    /// The shell maps statuses back by the echoed string, in order, so the
    /// echo must be the exact input — rejected and respelled gestures included.
    #[test]
    fn status_shape_echoes_gestures_in_order() {
        let plan = plan(&["Control+Shift", "control+snapshot", "F12"], &[], &["Alt+F12"], None);
        let main = plan.statuses["main"].as_array().unwrap();
        assert_eq!(main.len(), 3);
        assert_eq!(
            main[0],
            serde_json::json!({ "gesture": "Control+Shift", "active": false, "error": "unknown key 'Shift'" })
        );
        assert_eq!(main[1]["gesture"], "control+snapshot");
        assert_eq!(main[1]["active"], cfg!(not(target_os = "macos")));
        assert_eq!(main[2], serde_json::json!({ "gesture": "F12", "active": true, "error": null }));
        assert_eq!(plan.statuses["window"], serde_json::json!([]));
        assert_eq!(
            plan.statuses["monitor"],
            serde_json::json!([{ "gesture": "Alt+F12", "active": true, "error": null }])
        );
    }

    #[test]
    fn listener_error_reports_on_every_gesture() {
        let plan = plan(&["F12", "Control+D1"], &[], &[], Some("no accessibility"));
        assert!(plan.wanted.is_empty());
        for status in plan.statuses["main"].as_array().unwrap() {
            assert_eq!(status["active"], false);
            assert_eq!(status["error"], "no accessibility");
        }
    }

    /// The gestures the shell sends today (HotkeyGesture.ToSerializedString)
    /// plan active wherever their key exists.
    #[test]
    fn settings_form_gestures_parse() {
        let all = [
            "PrintScreen",
            "Control+PrintScreen",
            "Alt+PrintScreen",
            "Shift+PrintScreen",
            "Control+Shift+D1",
            "Control+Cancel",
        ];
        let plan = plan(&all, &[], &[], None);
        let main = plan.statuses["main"].as_array().unwrap();
        for (status, text) in main.iter().zip(all) {
            #[cfg(not(target_os = "macos"))]
            assert_eq!(*status, serde_json::json!({ "gesture": text, "active": true, "error": null }));
            // No macOS key code: refused up front, never a silent dead hotkey.
            #[cfg(target_os = "macos")]
            if text == "Control+Shift+D1" {
                assert_eq!(status["active"], true, "{text}");
            } else {
                let key = gesture(text).key;
                assert_eq!(
                    status["error"],
                    format!("the key '{}' cannot be used as a global hotkey on this system", key.name()),
                    "{text}"
                );
            }
        }
    }

    #[test]
    fn none_and_modifier_keys_are_refused() {
        let plan = plan(&["None", "LeftShift", "Control+RightAlt"], &[], &[], None);
        assert!(plan.wanted.is_empty());
        let main = &plan.statuses["main"];
        assert_eq!(
            main[0],
            serde_json::json!({ "gesture": "None", "active": false, "error": "gesture has no key" })
        );
        for i in [1, 2] {
            assert_eq!(main[i]["active"], false);
            assert_eq!(main[i]["error"], "a modifier key on its own cannot be a global hotkey");
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
            "Control+Shift+D1",
        ])
        .unwrap();
        assert_eq!(args.hk_main, ["PrintScreen", "Control+Shift+D1"]);
        assert!(args.hk_window.is_empty());
        assert_eq!(args.hk_monitor, ["Control+PrintScreen"]);
    }
}
