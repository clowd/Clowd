//! Global screenshot hotkeys, owned by this process rather than the shell.
//!
//! Two hard requirements shape this module. First, the trigger path must not
//! depend on the C# shell being resident — a paged-out shell would add its
//! page-in time to every capture. Second, the key must be SUPPRESSED, not
//! merely observed: Windows 11's "PrintScreen opens Snipping Tool" feature
//! consumes the key ahead of RegisterHotKey delivery, so an ordinary hotkey
//! registration reports success and then never fires. prisc_hotkeys provides
//! a low-level hook (WH_KEYBOARD_LL / CGEventTap) that swallows a registered
//! press before any OS handler sees it.
//!
//! Matching and suppression share one snapshot of the registrations inside
//! the hook callback, so a press is never swallowed without firing, or fired
//! without being swallowed. This module only maps the fired registration to
//! its capture mode. While the shell records a gesture in its settings UI it
//! pauses this hook over stdin (`{"type":"pause"}`), so the press it records
//! reaches the shell's own hook instead of starting a capture here.
//!
//! On macOS the hook can be down for a while — Accessibility not granted yet,
//! or revoked under a running tap — and comes back without a restart: the
//! standby retries it (see `recovery`) and re-reports every gesture when it
//! comes up or goes down.

use std::collections::HashMap;
use std::sync::{mpsc, Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use prisc_hotkeys::supervise::{Change, Down, Hook, HookEvent, PauseHandle, Recovery, Routed, Router, Supervisor};
use prisc_hotkeys::{Error, Gesture, Hotkeys, INVALID_GESTURE_CODE};
use prisc_ipc::catalog::capture::{self, HotkeyStatus};
use winit::event_loop::EventLoopProxy;

use crate::settings::{CaptureMode, CliArgs};

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(|e| e.into_inner())
}

/// What `poll` found.
pub enum Poll {
    Unchanged,
    /// The hook came up, went down, failed differently, or died and was
    /// restarted at once: the gestures' new statuses, for an unsolicited
    /// `settings_status`.
    Statuses(capture::Hotkeys),
    /// The hook died where that is fatal (`Recovery::Exit`).
    Fatal(String),
}

type Requested = Vec<(&'static str, Vec<String>, CaptureMode)>;

/// The standby's gestures on a supervised hook. The retry interval, the
/// stale-grant back-off, the recovery policy per OS, re-registration after a
/// restart and the pause switch are prisc_hotkeys' `Supervisor`, the same one
/// Prisc.Ui's in-process hook runs under; this module plans the gestures of
/// each settings snapshot, maps a fired registration to its capture mode,
/// drives the supervisor from the event loop and logs what it reports.
pub struct StandbyHotkeys<H: Hook = Hotkeys> {
    supervisor: Supervisor<H>,
    wake: Arc<dyn Fn() + Send + Sync>,
    /// True once a retried death was logged at error: a tap that keeps dying
    /// is restarted every time, and each error! is a Sentry event.
    reported_death: bool,
    /// The code of the down state last logged, so a retry that keeps failing
    /// the same way is logged once per change of reason. None while it runs.
    logged_down: Option<&'static str>,
    /// The gesture lists of the last settings snapshot, applied again when
    /// the hook comes or goes. None until the first snapshot, which is when
    /// hotkeys take effect.
    requested: Option<Requested>,
    /// What the supervisor holds for us: each gesture's stable id.
    registered: HashMap<Gesture, u32>,
    /// Read by the forwarding thread to turn a fired registration into a mode.
    modes: Arc<Mutex<HashMap<u32, CaptureMode>>>,
    fired_tx: mpsc::Sender<CaptureMode>,
    fired_rx: mpsc::Receiver<CaptureMode>,
    /// A hook's death (its generation and reason), kept apart from
    /// `fired_rx` so `drain` can never drop one.
    lost_tx: mpsc::Sender<(u64, String)>,
    lost_rx: mpsc::Receiver<(u64, String)>,
}

impl StandbyHotkeys {
    pub fn new(proxy: EventLoopProxy<()>) -> Self {
        // Send but not Sync on Windows (it holds an HWND), and the wake is shared.
        let proxy = Mutex::new(proxy);
        Self::with_supervisor(
            Supervisor::new(),
            Arc::new(move || {
                let _ = lock(&proxy).send_event(());
            }),
        )
    }
}

impl<H: Hook> StandbyHotkeys<H> {
    /// Starts the hook once through `supervisor`; `wake` rouses the event
    /// loop after a press or a hook death.
    fn with_supervisor(supervisor: Supervisor<H>, wake: Arc<dyn Fn() + Send + Sync>) -> Self {
        let (fired_tx, fired_rx) = mpsc::channel();
        let (lost_tx, lost_rx) = mpsc::channel();
        let mut hotkeys = Self {
            supervisor,
            wake,
            reported_death: false,
            logged_down: None,
            requested: None,
            registered: HashMap::new(),
            modes: Arc::default(),
            fired_tx,
            fired_rx,
            lost_tx,
            lost_rx,
        };
        hotkeys.supervisor.start(Instant::now());
        hotkeys.absorb();
        hotkeys
    }

    /// Why the hook is down, if it is.
    pub fn listener_error(&self) -> Option<&Down> {
        self.supervisor.down()
    }

    /// True when the hook is down for a reason another process would not
    /// share (`Recovery::Exit`): standby then exits so the shell falls back to
    /// its own registrations.
    pub fn should_bail(&self) -> bool {
        self.supervisor.recovery() == Some(Recovery::Exit)
    }

    /// For the stdin reader thread: applies `{"type":"pause"}` immediately,
    /// even mid-capture, to whichever hook is running then or later.
    pub fn pause_handle(&self) -> PauseHandle<H> {
        self.supervisor.pause_handle()
    }

    /// When the event loop must wake to retry the hook; None while it runs,
    /// while retrying is pointless, or while no gesture would be registered
    /// on it (a snapshot that adds one retries), so the loop can block
    /// indefinitely.
    pub fn next_retry(&self) -> Option<Instant> {
        self.supervisor.next_retry()
    }

    /// Replaces the whole gesture set from a CLI snapshot and returns the
    /// per-gesture statuses for `settings_status`. Hook-based hotkeys
    /// cannot conflict with other applications, so a gesture is active exactly
    /// when it parses, is usable on this OS, is not a duplicate, and the hook
    /// holds it. A hook that is down is tried again first if its retry is due
    /// (see `next_retry`).
    pub fn apply(&mut self, args: &CliArgs) -> capture::Hotkeys {
        self.apply_at(args, Instant::now())
    }

    fn apply_at(&mut self, args: &CliArgs, now: Instant) -> capture::Hotkeys {
        self.requested = Some(vec![
            ("main", args.hk_main.clone(), CaptureMode::Region),
            ("window", args.hk_window.clone(), CaptureMode::Window),
            ("monitor", args.hk_monitor.clone(), CaptureMode::Screen),
        ]);
        // The supervisor retries only a hook something would be registered
        // on, so the gestures go to it first.
        self.sync_registrations();
        self.supervisor.poll(now);
        self.absorb();
        self.sync()
    }

    /// One look at the hook's health from the event loop, which calls this
    /// on every pass: notices a death the forwarding thread reported and
    /// retries a hook that is down once `next_retry` has passed. A death is
    /// reported even when the restart in the same pass succeeds.
    pub fn poll(&mut self, now: Instant) -> Poll {
        let before = self.supervisor.down().cloned();
        let mut lost = None;
        while let Ok((generation, reason)) = self.lost_rx.try_recv() {
            self.supervisor
                .died(generation, reason.clone());
            lost = Some(reason);
        }
        let mut died = self.absorb();
        if let Some(reason) = lost.clone().or(died.clone()) {
            if self.should_bail() {
                return Poll::Fatal(reason);
            }
        }
        self.supervisor.poll(now);
        died = died.or(self.absorb());
        if self.requested.is_none() || (died.is_none() && lost.is_none() && self.supervisor.down() == before.as_ref()) {
            return Poll::Unchanged;
        }
        Poll::Statuses(self.sync())
    }

    pub fn try_recv(&self) -> Option<CaptureMode> {
        self.fired_rx.try_recv().ok()
    }

    pub fn drain(&self) {
        while self.fired_rx.try_recv().is_ok() {}
    }

    /// Acts on what the supervisor did: forwards each new hook's events from
    /// a thread of its own, and logs failures and deaths. Returns the reason
    /// of a death, if one happened.
    fn absorb(&mut self) -> Option<String> {
        let mut died = None;
        // Until quiet: a hook whose forwarding thread cannot start dies here,
        // which is another change.
        loop {
            let changes = self.supervisor.take_changes();
            if changes.is_empty() {
                return died;
            }
            for change in changes {
                if let Some(reason) = self.absorb_one(change) {
                    died = Some(reason);
                }
            }
        }
    }

    fn absorb_one(&mut self, change: Change<H>) -> Option<String> {
        match change {
            Change::Started {
                generation,
                events,
            } => {
                self.logged_down = None;
                let router = self.supervisor.router();
                let (modes, fired, lost, wake) = (
                    Arc::clone(&self.modes),
                    self.fired_tx.clone(),
                    self.lost_tx.clone(),
                    Arc::clone(&self.wake),
                );
                let spawned = std::thread::Builder::new()
                    .name("standby-hotkeys".into())
                    .spawn(move || forward::<H>(generation, &events, &router, &modes, &fired, &lost, &*wake));
                if let Err(err) = spawned {
                    // Nothing would read the hook: count it as dead.
                    self.supervisor
                        .died(generation, format!("failed to start the hotkey thread: {err}"));
                }
            }
            Change::StartFailed(down) => {
                // Logged once per change of reason: the retry repeats this every
                // RETRY_INTERVAL, and in release builds each error! is a Sentry event.
                let news = self.logged_down != Some(down.code());
                self.logged_down = Some(down.code());
                match news {
                    // A missing grant is the user's choice, not a defect.
                    true if matches!(down, Down::Error(Error::PermissionDenied)) => {
                        log::warn!("standby hotkey hook unavailable: {down}")
                    }
                    true => log::error!("standby hotkey hook unavailable: {down}"),
                    false => log::debug!("standby hotkey hook still unavailable: {down}"),
                }
            }
            Change::Died {
                reason,
                down,
            } => {
                self.logged_down = Some(down.code());
                if prisc_hotkeys::supervise::recovery(&down) == Recovery::Exit {
                    log::error!("standby hotkey hook died: {reason}");
                // Retried, so reported once per run like the shell's own hook:
                // a revoked grant is the user's choice, not a defect.
                } else if reason == prisc_hotkeys::REVOKED_REASON || self.reported_death {
                    log::warn!("standby hotkey hook died: {reason}");
                } else {
                    self.reported_death = true;
                    log::error!("standby hotkey hook died: {reason}");
                }
                return Some(reason);
            }
        }
        None
    }

    /// `requested` planned against the hook's state; None before the first
    /// snapshot.
    fn plan(&self) -> Option<HotkeyPlan<'static>> {
        let requested: Vec<(&'static str, &[String], CaptureMode)> = self
            .requested
            .as_ref()?
            .iter()
            .map(|(name, gestures, mode)| (*name, gestures.as_slice(), *mode))
            .collect();
        Some(plan_hotkeys(&requested, self.supervisor.down()))
    }

    /// Brings the supervisor's registrations in line with `requested`, held
    /// whether or not the hook runs. A gesture present in both snapshots is
    /// never unregistered, so it keeps firing throughout. Returns the statuses
    /// of the gestures the supervisor refused (by slot), which the plan has
    /// already refused in practice.
    fn sync_registrations(&mut self) -> Vec<((&'static str, usize), HotkeyStatus)> {
        let Some(mut plan) = self.plan() else {
            return Vec::new();
        };
        // Held for the whole sync, so the forwarding thread can never see a
        // registration without its mode.
        let modes_arc = Arc::clone(&self.modes);
        let mut modes = lock(&modes_arc);
        let mut kept = Vec::with_capacity(plan.wanted.len());
        let mut refused = Vec::new();
        for (&(gesture, mode), &slot) in plan.wanted.iter().zip(&plan.slots) {
            if let Some(&id) = self.registered.get(&gesture) {
                modes.insert(id, mode);
                kept.push(gesture);
                continue;
            }
            match self.supervisor.register(gesture) {
                Ok(id) => {
                    self.registered.insert(gesture, id);
                    modes.insert(id, mode);
                    kept.push(gesture);
                }
                Err(err) => {
                    let echo = entries_mut(&mut plan.statuses, slot.0)[slot.1]
                        .gesture
                        .clone();
                    refused.push((slot, status_entry(echo, Some((err.to_string(), err.code())))));
                }
            }
        }
        let supervisor = &mut self.supervisor;
        self.registered.retain(|gesture, id| {
            let keep = kept.contains(gesture);
            if !keep {
                supervisor.unregister(*id);
                modes.remove(id);
            }
            keep
        });
        refused
    }

    /// Brings the hook in line with `requested` and reports every gesture.
    /// Only called once a snapshot set `requested`.
    fn sync(&mut self) -> capture::Hotkeys {
        let refused = self.sync_registrations();
        // A register that found the hook dead counts as its death, which the
        // statuses then report.
        self.absorb();
        let mut plan = self
            .plan()
            .expect("sync runs after the first settings snapshot");
        for ((name, index), status) in refused {
            entries_mut(&mut plan.statuses, name)[index] = status;
        }
        plan.statuses
    }
}

/// The forwarding thread of the hook started as `generation`: turns its
/// presses into modes on `fired`, and hands its death to the event loop
/// rather than acting on it here, where a capture in progress cannot be seen.
fn forward<H: Hook>(
    generation: u64,
    events: &H::Events,
    router: &Router<H::Id>,
    modes: &Mutex<HashMap<u32, CaptureMode>>,
    fired: &mpsc::Sender<CaptureMode>,
    lost: &mpsc::Sender<(u64, String)>,
    wake: &(dyn Fn() + Send + Sync),
) {
    loop {
        match H::next_event(events, Duration::from_secs(3600)) {
            Ok(HookEvent::Stopped(reason)) => {
                let _ = lost.send((generation, reason));
                wake();
                return;
            }
            Ok(
                event @ HookEvent::Hotkey {
                    repeat: false,
                    ..
                },
            ) => {
                let Some(Routed::Hotkey {
                    id,
                    ..
                }) = router.route(generation, event)
                else {
                    continue;
                };
                let mode = lock(modes).get(&id).copied();
                if let Some(mode) = mode {
                    if fired.send(mode).is_err() {
                        return;
                    }
                    wake();
                }
            }
            Ok(_) | Err(mpsc::RecvTimeoutError::Timeout) => {}
            // The queue closed because the hook was dropped (a normal shutdown,
            // or a death already reported): not a failure.
            Err(mpsc::RecvTimeoutError::Disconnected) => return,
        }
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
    statuses: capture::Hotkeys,
}

/// One gesture's status; `error` is the text and `error_code` its stable code
/// (prisc_hotkeys' code table), both absent when active.
fn status_entry(gesture: String, error: Option<(String, &str)>) -> HotkeyStatus {
    let (error, code) = error.unzip();
    HotkeyStatus {
        gesture,
        active: error.is_none(),
        error,
        error_code: code.map(str::to_owned),
    }
}

/// The status list `settings_status` carries for the mode named `name`.
fn entries_mut<'h>(hotkeys: &'h mut capture::Hotkeys, name: &str) -> &'h mut Vec<HotkeyStatus> {
    match name {
        "main" => &mut hotkeys.main,
        "window" => &mut hotkeys.window,
        "monitor" => &mut hotkeys.monitor,
        other => unreachable!("no hotkey list named {other}"),
    }
}

/// Resolves every gesture of every mode, in request order. Each name maps to
/// an array with one status entry per gesture received, and "gesture" echoes
/// the received string verbatim — the shell matches statuses back to its own
/// gestures by that string, so it must never be normalized here. A gesture the
/// hook would hold is `wanted` whatever the hook's state; while the hook is
/// down (`hook_error`) its status reports why.
fn plan_hotkeys<'a>(requested: &[(&'a str, &[String], CaptureMode)], hook_error: Option<&Down>) -> HotkeyPlan<'a> {
    let mut wanted: Vec<(Gesture, CaptureMode)> = Vec::new();
    let mut slots = Vec::new();
    let mut statuses = capture::Hotkeys {
        main: Vec::new(),
        window: Vec::new(),
        monitor: Vec::new(),
    };
    for &(name, gestures, mode) in requested {
        let mut entries = Vec::with_capacity(gestures.len());
        for gesture in gestures {
            let error = match gesture.parse::<Gesture>() {
                Err(err) => Some((err.to_string(), INVALID_GESTURE_CODE)),
                Ok(parsed) => match parsed.validate() {
                    Err(err) => Some((err.to_string(), err.code())),
                    // The shell already gives each gesture to at most one action, so
                    // this is defense in depth: the hook refuses an equal gesture
                    // anyway. Compared parsed, so a respelling ("Shift+Control+A")
                    // counts as the gesture it duplicates.
                    Ok(()) if wanted.iter().any(|(g, _)| *g == parsed) => Some((Error::Duplicate.to_string(), Error::Duplicate.code())),
                    Ok(()) => {
                        wanted.push((parsed, mode));
                        slots.push((name, entries.len()));
                        hook_error.map(|down| (down.to_string(), down.code()))
                    }
                },
            };
            entries.push(status_entry(gesture.clone(), error));
        }
        *entries_mut(&mut statuses, name) = entries;
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

    /// The wire form of a `settings_status` payload, which the assertions read.
    fn json(statuses: &capture::Hotkeys) -> serde_json::Value {
        serde_json::to_value(statuses).unwrap()
    }

    fn gestures(list: &[&str]) -> Vec<String> {
        list.iter().map(|g| g.to_string()).collect()
    }

    fn plan(main: &[&str], window: &[&str], monitor: &[&str], hook_error: Option<&Down>) -> HotkeyPlan<'static> {
        let (main, window, monitor) = (gestures(main), gestures(window), gestures(monitor));
        plan_hotkeys(
            &[
                ("main", &main, CaptureMode::Region),
                ("window", &window, CaptureMode::Window),
                ("monitor", &monitor, CaptureMode::Screen),
            ],
            hook_error,
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
        assert_eq!(json(&plan.statuses), serde_json::json!({ "main": [], "window": [], "monitor": [] }));
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
        assert_eq!(json(&plan.statuses)["window"][0]["active"], true);
        // Compared parsed: a modifier respelling and a key alias both count.
        for (i, gesture) in ["Control+Shift+A", "Return", "Shift+Control+A", "Enter"]
            .into_iter()
            .enumerate()
        {
            assert_eq!(
                json(&plan.statuses)["monitor"][i],
                serde_json::json!({ "gesture": gesture, "active": false, "error": "duplicate gesture", "error_code": "duplicate" })
            );
        }
    }

    #[test]
    fn duplicate_within_a_mode_keeps_the_first() {
        let plan = plan(&["F12", "F12"], &[], &[], None);
        assert_eq!(plan.wanted.len(), 1);
        assert_eq!(json(&plan.statuses)["main"][0]["active"], true);
        assert_eq!(json(&plan.statuses)["main"][1]["error"], "duplicate gesture");
    }

    /// The shell maps statuses back by the echoed string, in order, so the
    /// echo must be the exact input — rejected and respelled gestures included.
    #[test]
    fn status_shape_echoes_gestures_in_order() {
        let plan = plan(&["Control+Shift", "control+snapshot", "F12"], &[], &["Alt+F12"], None);
        let statuses = json(&plan.statuses);
        let main = statuses["main"].as_array().unwrap();
        assert_eq!(main.len(), 3);
        assert_eq!(
            main[0],
            serde_json::json!({
                "gesture": "Control+Shift",
                "active": false,
                "error": "unknown key 'Shift'",
                "error_code": "invalid_gesture"
            })
        );
        assert_eq!(main[1]["gesture"], "control+snapshot");
        assert_eq!(main[1]["active"], cfg!(not(target_os = "macos")));
        assert_eq!(main[2], serde_json::json!({ "gesture": "F12", "active": true }));
        assert_eq!(json(&plan.statuses)["window"], serde_json::json!([]));
        assert_eq!(
            json(&plan.statuses)["monitor"],
            serde_json::json!([{ "gesture": "Alt+F12", "active": true }])
        );
    }

    #[test]
    fn listener_error_reports_on_every_gesture() {
        let plan = plan(&["F12", "Control+D1"], &[], &[], Some(&Down::Error(Error::PermissionDenied)));
        // still wanted: the supervisor holds them for the hook's return
        assert_eq!(plan.wanted.len(), 2);
        for status in json(&plan.statuses)["main"]
            .as_array()
            .unwrap()
        {
            assert_eq!(status["active"], false);
            assert_eq!(status["error"], Error::PermissionDenied.to_string());
        }
    }

    /// The shell keys behaviour off `error_code` (the Accessibility prompt
    /// on "permission"), never off the display text.
    #[test]
    fn every_status_carries_a_machine_readable_code() {
        let mixed = plan(&["F12", "Control+Shift", "None", "LeftShift", "F12"], &[], &[], None);
        let codes: Vec<_> = json(&mixed.statuses)["main"]
            .as_array()
            .unwrap()
            .iter()
            .map(|status| status["error_code"].clone())
            .collect();
        assert_eq!(
            codes,
            [
                serde_json::Value::Null,
                "invalid_gesture".into(),
                "no_key".into(),
                "modifier_key".into(),
                "duplicate".into()
            ]
        );

        let denied = plan(&["F12"], &[], &[], Some(&Down::Error(Error::PermissionDenied)));
        assert_eq!(json(&denied.statuses)["main"][0]["error_code"], "permission");
        let unsupported = plan(&["F12"], &[], &[], Some(&Down::Error(Error::Unsupported)));
        assert_eq!(json(&unsupported.statuses)["main"][0]["error_code"], "unsupported");
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
            // the macOS defaults
            "Shift+Meta+D2",
            "F13",
            "Shift+Meta+D1",
        ];
        let plan = plan(&all, &[], &[], None);
        let statuses = json(&plan.statuses);
        let main = statuses["main"].as_array().unwrap();
        for (status, text) in main.iter().zip(all) {
            #[cfg(not(target_os = "macos"))]
            assert_eq!(*status, serde_json::json!({ "gesture": text, "active": true }));
            // PrintScreen and Cancel have no macOS key code: refused up front, never a
            // silent dead hotkey.
            #[cfg(target_os = "macos")]
            if !matches!(gesture(text).key, prisc_hotkeys::Key::PrintScreen | prisc_hotkeys::Key::Cancel) {
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
        let main = &json(&plan.statuses)["main"];
        assert_eq!(
            main[0],
            serde_json::json!({ "gesture": "None", "active": false, "error": "gesture has no key", "error_code": "no_key" })
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
            "prisc_capture",
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

    // The hook's lifecycle, against a fake hook: nothing here installs a real one.

    use prisc_hotkeys::supervise::{Platform, Probe, Starter, RETRY_INTERVAL};

    #[derive(Default)]
    struct FakeState {
        registered: Mutex<Vec<Gesture>>,
        paused: std::sync::atomic::AtomicBool,
        /// Refuses registrations like a real hook whose thread has exited.
        dead: std::sync::atomic::AtomicBool,
    }

    /// The fake as the supervisor owns it; the test keeps the state to look inside.
    struct FakeHook(Arc<FakeState>);

    impl Hook for FakeHook {
        type Id = usize;
        type Events = mpsc::Receiver<HookEvent<usize>>;

        fn register(&self, gesture: Gesture) -> Result<usize, Error> {
            if self
                .0
                .dead
                .load(std::sync::atomic::Ordering::SeqCst)
            {
                return Err(Error::Stopped);
            }
            let mut registered = lock(&self.0.registered);
            registered.push(gesture);
            Ok(registered.len())
        }

        fn unregister(&self, _id: usize) {}

        fn set_paused(&self, paused: bool) {
            self.0
                .paused
                .store(paused, std::sync::atomic::Ordering::SeqCst);
        }

        fn set_observe(&self, _observe: bool) {}

        fn start_recording(&self) -> Option<u32> {
            None
        }

        fn stop_recording(&self, _session: u32) {}

        fn next_event(events: &Self::Events, timeout: Duration) -> Result<HookEvent<usize>, mpsc::RecvTimeoutError> {
            events.recv_timeout(timeout)
        }
    }

    struct Rig {
        hotkeys: StandbyHotkeys<FakeHook>,
        /// One entry per successful start: the hook, and where to send its events.
        hooks: mpsc::Receiver<(Arc<FakeState>, mpsc::Sender<HookEvent<usize>>)>,
        woke: mpsc::Receiver<()>,
    }

    /// Hook starts follow `script`, one entry per start; running past its end
    /// is a test failure.
    fn rig(script: Vec<Result<(), Error>>) -> Rig {
        let mut script = std::collections::VecDeque::from(script);
        let (hooks_tx, hooks) = mpsc::channel();
        let (wake_tx, woke) = mpsc::channel();
        let wake_tx = Mutex::new(wake_tx);
        let hooks_tx = Mutex::new(hooks_tx);
        let starter: Starter<FakeHook> = Box::new(move || {
            script
                .pop_front()
                .expect("unexpected hook start")?;
            let state = Arc::new(FakeState::default());
            let (events_tx, events) = mpsc::channel();
            lock(&hooks_tx)
                .send((Arc::clone(&state), events_tx))
                .unwrap();
            Ok((FakeHook(state), events))
        });
        let probe: Probe = Box::new(|| true);
        let wake = Arc::new(move || {
            let _ = lock(&wake_tx).send(());
        });
        Rig {
            hotkeys: StandbyHotkeys::with_supervisor(Supervisor::with_starter(starter, probe, Platform::CURRENT), wake),
            hooks,
            woke,
        }
    }

    fn snapshot(main: &[&str]) -> CliArgs {
        use clap::Parser;
        let mut argv = vec!["prisc_capture", "--standby", "--session-root", "x"];
        for gesture in main {
            argv.extend(["--hk-main", gesture]);
        }
        CliArgs::try_parse_from(argv).unwrap()
    }

    /// Granting Accessibility to a running Prisc makes the hotkeys work at
    /// the next retry, without a restart, and a pause sent while the hook was
    /// down holds on the hook that comes up.
    #[cfg(target_os = "macos")]
    #[test]
    fn a_permission_granted_later_starts_the_hook() {
        let mut rig = rig(vec![Err(Error::PermissionDenied), Ok(())]);
        assert!(!rig.hotkeys.should_bail());
        rig.hotkeys.pause_handle().set_paused(true);
        let statuses = rig.hotkeys.apply(&snapshot(&["F12"]));
        assert_eq!(
            json(&statuses)["main"][0],
            serde_json::json!({
                "gesture": "F12",
                "active": false,
                "error": Error::PermissionDenied.to_string(),
                "error_code": "permission"
            })
        );

        let at = rig
            .hotkeys
            .next_retry()
            .expect("a denied hook is retried");
        assert!(matches!(
            rig.hotkeys
                .poll(at - Duration::from_millis(1)),
            Poll::Unchanged
        ));
        let Poll::Statuses(statuses) = rig.hotkeys.poll(at) else {
            panic!("the hook that came up is reported unasked");
        };
        assert_eq!(json(&statuses)["main"][0], serde_json::json!({ "gesture": "F12", "active": true }));
        let (hook, _events) = rig
            .hooks
            .try_recv()
            .expect("the hook started");
        assert_eq!(*lock(&hook.registered), [gesture("F12")]);
        assert!(hook
            .paused
            .load(std::sync::atomic::Ordering::SeqCst));
        assert_eq!(rig.hotkeys.next_retry(), None);
        assert!(matches!(rig.hotkeys.poll(at + RETRY_INTERVAL), Poll::Unchanged));
    }

    /// A snapshot retries a hook that is down only once `RETRY_INTERVAL`
    /// has passed, so a burst of snapshots cannot hammer tccd.
    #[cfg(target_os = "macos")]
    #[test]
    fn a_snapshot_retries_only_past_the_rate_limit() {
        // A second start would run the script dry and panic.
        let mut rig = rig(vec![Err(Error::PermissionDenied)]);
        rig.hotkeys.apply(&snapshot(&["F12"]));
        let at = rig
            .hotkeys
            .next_retry()
            .expect("a denied hook is retried");
        for _ in 0..3 {
            assert_eq!(json(&rig.hotkeys.apply(&snapshot(&["F12"])))["main"][0]["error_code"], "permission");
        }
        assert_eq!(rig.hotkeys.next_retry(), Some(at));
    }

    /// A tap that cannot be created is the Accessibility grant in disguise on
    /// macOS, so standby stays up and retries; elsewhere it is this
    /// process's own failure and standby exits for the shell to recover.
    #[test]
    fn a_hook_that_cannot_start_bails_only_off_macos() {
        let mut rig = rig(vec![Err(Error::HookFailed("could not create the event tap".into()))]);
        assert_eq!(rig.hotkeys.should_bail(), cfg!(not(target_os = "macos")));
        rig.hotkeys.apply(&snapshot(&["F12"]));
        assert_eq!(rig.hotkeys.next_retry().is_some(), cfg!(target_os = "macos"));
    }

    #[test]
    fn unsupported_is_reported_and_never_retried() {
        let mut rig = rig(vec![Err(Error::Unsupported)]);
        assert!(!rig.hotkeys.should_bail());
        assert_eq!(rig.hotkeys.next_retry(), None);
        assert_eq!(
            json(&rig.hotkeys.apply(&snapshot(&["F12"])))["main"][0]["error_code"],
            "unsupported"
        );
        assert!(matches!(
            rig.hotkeys
                .poll(Instant::now() + RETRY_INTERVAL * 10),
            Poll::Unchanged
        ));
    }

    /// A hook death reaches the event loop instead of exiting from the
    /// forwarding thread. On macOS it means "hook down, retrying" (a revoked
    /// grant), reported per gesture; elsewhere it is fatal.
    #[test]
    fn a_hook_that_dies_is_retried_on_macos_and_fatal_elsewhere() {
        let mut rig = rig(vec![Ok(()), Err(Error::PermissionDenied), Ok(())]);
        rig.hotkeys.apply(&snapshot(&["F12"]));
        let (first, events) = rig.hooks.try_recv().unwrap();
        assert_eq!(*lock(&first.registered), [gesture("F12")]);
        events
            .send(HookEvent::Hotkey {
                id: 1,
                repeat: false,
            })
            .unwrap();
        events
            .send(HookEvent::Stopped("the Accessibility permission was revoked".into()))
            .unwrap();
        for _ in 0..2 {
            rig.woke
                .recv_timeout(Duration::from_secs(5))
                .expect("the forwarding thread wakes the loop");
        }
        assert_eq!(
            rig.hotkeys.try_recv(),
            Some(CaptureMode::Region),
            "a press ahead of the death still fires"
        );

        // Past the rate limit, so the death is retried at once.
        let now = Instant::now() + RETRY_INTERVAL;
        match rig.hotkeys.poll(now) {
            #[cfg(target_os = "macos")]
            Poll::Statuses(statuses) => {
                assert_eq!(json(&statuses)["main"][0]["active"], false);
                assert_eq!(json(&statuses)["main"][0]["error_code"], "permission");
            }
            #[cfg(not(target_os = "macos"))]
            Poll::Fatal(reason) => assert_eq!(reason, "the Accessibility permission was revoked"),
            _ => panic!("unexpected outcome of a hook death"),
        }
        #[cfg(target_os = "macos")]
        {
            assert!(!rig.hotkeys.should_bail());
            assert!(rig.hooks.try_recv().is_err(), "no hook while denied");
            let Poll::Statuses(statuses) = rig.hotkeys.poll(now + RETRY_INTERVAL) else {
                panic!("the hook that came back is reported unasked");
            };
            assert_eq!(json(&statuses)["main"][0]["active"], true);
            let (second, _events) = rig.hooks.try_recv().unwrap();
            assert_eq!(*lock(&second.registered), [gesture("F12")], "registered again on the new hook");
        }
    }
    /// A tap that keeps timing out or is invalidated dies with the grant
    /// intact, so the restart in the same poll succeeds: the new hook must
    /// still get the gestures, and the shell hear that they are active.
    #[cfg(target_os = "macos")]
    #[test]
    fn a_hook_that_dies_and_restarts_at_once_is_registered_again() {
        let mut rig = rig(vec![Ok(()), Ok(())]);
        rig.hotkeys.apply(&snapshot(&["F12"]));
        let (_first, events) = rig.hooks.try_recv().unwrap();
        events
            .send(HookEvent::Stopped("the event tap kept timing out".into()))
            .unwrap();
        rig.woke
            .recv_timeout(Duration::from_secs(5))
            .expect("the forwarding thread wakes the loop");

        let now = Instant::now() + RETRY_INTERVAL;
        let Poll::Statuses(statuses) = rig.hotkeys.poll(now) else {
            panic!("a death and restart is reported unasked");
        };
        assert_eq!(json(&statuses)["main"][0], serde_json::json!({ "gesture": "F12", "active": true }));
        let (second, _events) = rig.hooks.try_recv().unwrap();
        assert_eq!(*lock(&second.registered), [gesture("F12")], "registered again on the new hook");
        assert!(matches!(rig.hotkeys.poll(now + RETRY_INTERVAL), Poll::Unchanged));
    }

    /// A snapshot applied to a hook that died but whose death is not yet
    /// polled reports `hook_stopped`; the restart that follows corrects it.
    #[cfg(target_os = "macos")]
    #[test]
    fn a_snapshot_on_a_dead_hook_is_corrected_by_the_restart() {
        let mut rig = rig(vec![Ok(()), Ok(())]);
        rig.hotkeys.apply(&snapshot(&["F12"]));
        let (first, events) = rig.hooks.try_recv().unwrap();
        first
            .dead
            .store(true, std::sync::atomic::Ordering::SeqCst);
        events
            .send(HookEvent::Stopped("the event tap was invalidated".into()))
            .unwrap();
        rig.woke
            .recv_timeout(Duration::from_secs(5))
            .expect("the forwarding thread wakes the loop");
        let statuses = rig.hotkeys.apply(&snapshot(&["F12", "F11"]));
        assert_eq!(json(&statuses)["main"][1]["error_code"], "hook_stopped");

        let now = Instant::now() + RETRY_INTERVAL;
        let Poll::Statuses(statuses) = rig.hotkeys.poll(now) else {
            panic!("a death and restart is reported unasked");
        };
        assert_eq!(json(&statuses)["main"][0]["active"], true);
        assert_eq!(json(&statuses)["main"][1]["active"], true);
        let (second, _events) = rig.hooks.try_recv().unwrap();
        assert_eq!(*lock(&second.registered), [gesture("F12"), gesture("F11")]);
    }

    /// Records every log record with the thread that logged it, so a test
    /// sees only its own records among tests running in parallel.
    struct Recorder(Mutex<Vec<(std::thread::ThreadId, log::Level, String)>>);

    impl log::Log for Recorder {
        fn enabled(&self, _: &log::Metadata<'_>) -> bool {
            true
        }

        fn log(&self, record: &log::Record<'_>) {
            lock(&self.0).push((std::thread::current().id(), record.level(), record.args().to_string()));
        }

        fn flush(&self) {}
    }

    /// This thread's records containing `needle`, from the first call on.
    fn records(needle: &str) -> Vec<(log::Level, String)> {
        static RECORDER: Recorder = Recorder(Mutex::new(Vec::new()));
        static INSTALL: std::sync::Once = std::sync::Once::new();
        INSTALL.call_once(|| {
            log::set_logger(&RECORDER).expect("no other logger in this test binary");
            log::set_max_level(log::LevelFilter::Trace);
        });
        let me = std::thread::current().id();
        lock(&RECORDER.0)
            .iter()
            .filter(|(thread, _, text)| *thread == me && text.contains(needle))
            .map(|(_, level, text)| (*level, text.clone()))
            .collect()
    }

    /// In release builds every error! is a Sentry event, so a hook retried
    /// every 2 s for the life of the process must report a failure once per
    /// change of reason, not once per try. A missing grant is a user state,
    /// not a defect, so it is a warning.
    #[cfg(target_os = "macos")]
    #[test]
    fn a_failing_retry_is_reported_once_per_reason() {
        records("");
        let denied = || Err(Error::PermissionDenied);
        let mut rig = rig(vec![
            denied(),
            denied(),
            denied(),
            denied(),
            Err(Error::HookFailed("could not create the event tap".into())),
            Err(Error::HookFailed("could not create the event tap (again)".into())),
            denied(),
            Ok(()),
            denied(),
        ]);
        rig.hotkeys.apply(&snapshot(&["F12"]));
        let start = Instant::now();
        for k in 1..=7 {
            rig.hotkeys.poll(start + RETRY_INTERVAL * k);
        }
        let (_hook, events) = rig
            .hooks
            .try_recv()
            .expect("the hook came up");
        events
            .send(HookEvent::Stopped("the grant was revoked".into()))
            .unwrap();
        rig.woke
            .recv_timeout(Duration::from_secs(5))
            .expect("the forwarding thread wakes the loop");
        rig.hotkeys.poll(start + RETRY_INTERVAL * 8);

        let reported: Vec<_> = records("hook unavailable")
            .into_iter()
            .filter(|(level, _)| *level <= log::Level::Warn)
            .collect();
        let levels: Vec<_> = reported
            .iter()
            .map(|(level, _)| *level)
            .collect();
        assert_eq!(
            levels,
            [log::Level::Warn, log::Level::Error, log::Level::Warn, log::Level::Warn],
            "{reported:#?}"
        );
        assert_eq!(records("hook still unavailable").len(), 4, "repeats stay in the debug log");
    }

    /// A death on macOS is retried, so a tap that keeps dying would file a
    /// Sentry event per death: the first fault is an error, later ones are
    /// warnings, and a revoked grant (the user's choice, which the shell does
    /// not report either) is never an error.
    #[cfg(target_os = "macos")]
    #[test]
    fn a_retried_death_is_an_error_once_and_a_revocation_never() {
        records("");
        let mut rig = rig(vec![Ok(()), Ok(()), Ok(()), Ok(())]);
        rig.hotkeys.apply(&snapshot(&["F12"]));
        let start = Instant::now();
        for (k, reason) in [
            "the event tap kept timing out",
            "the event tap was invalidated",
            prisc_hotkeys::REVOKED_REASON,
        ]
        .into_iter()
        .enumerate()
        {
            let (_hook, events) = rig.hooks.try_recv().expect("the hook is up");
            events
                .send(HookEvent::Stopped(reason.into()))
                .unwrap();
            rig.woke
                .recv_timeout(Duration::from_secs(5))
                .expect("the forwarding thread wakes the loop");
            assert!(matches!(
                rig.hotkeys
                    .poll(start + RETRY_INTERVAL * (k as u32 + 1)),
                Poll::Statuses(_)
            ));
        }
        let levels: Vec<_> = records("hook died")
            .into_iter()
            .map(|(level, _)| level)
            .collect();
        assert_eq!(levels, [log::Level::Error, log::Level::Warn, log::Level::Warn]);
    }

    /// A hook nothing would be registered on is not worth a thread start and
    /// a tccd query every 2 s: before the first snapshot, with no gestures,
    /// or with only gestures refused up front. A snapshot that adds one
    /// brings the retries back.
    #[cfg(target_os = "macos")]
    #[test]
    fn a_hook_with_nothing_to_register_is_not_retried() {
        // A start past the first two would run the script dry and panic.
        let mut rig = rig(vec![Err(Error::PermissionDenied), Ok(())]);
        let later = Instant::now() + RETRY_INTERVAL * 10;
        assert_eq!(rig.hotkeys.next_retry(), None, "before the first snapshot");
        assert!(matches!(rig.hotkeys.poll(later), Poll::Unchanged));

        for refused in [&[][..], &["None", "LeftShift", "PrintScreen"][..]] {
            let statuses = rig.hotkeys.apply(&snapshot(refused));
            assert!(json(&statuses)["main"]
                .as_array()
                .unwrap()
                .iter()
                .all(|status| status["error_code"] != "permission"));
            assert_eq!(rig.hotkeys.next_retry(), None, "{refused:?}");
            assert!(matches!(rig.hotkeys.poll(later), Poll::Unchanged));
        }
        assert!(rig.hooks.try_recv().is_err(), "never started");

        // Past the rate limit already, so the snapshot itself retries.
        let statuses = rig
            .hotkeys
            .apply_at(&snapshot(&["F12"]), later);
        assert_eq!(json(&statuses)["main"][0]["active"], true);
        assert!(rig.hooks.try_recv().is_ok(), "started for the new gesture");
    }
}
