//! The supervised hook: the one owner of the retry interval, the stale-grant backoff, the recovery
//! policy per OS, re-registration across restarts with stable ids, and the observe and pause
//! switches that outlive a hook.
//!
//! A [`Hotkeys`] that cannot start (macOS before Accessibility is granted, a NULL tap, a stale
//! grant) or that dies (a grant revoked under a running tap, a tap that keeps timing out) is gone
//! for good. Hosts that want hotkeys to come back by themselves wrap it in one of two layers:
//!
//! - [`Supervisor`]: a sans-IO state machine an event loop drives. It starts hooks through a
//!   [`Starter`], answers [`next_retry`](Supervisor::next_retry) for the loop's timer and retries on
//!   [`poll`](Supervisor::poll); the loop forwards each started hook's events itself (through a
//!   [`Router`]) and reports its death with [`died`](Supervisor::died). prisc_capture's standby
//!   drives one from its winit loop.
//! - [`Supervised`]: a `Supervisor<Hotkeys>` with its own `prisc-hotkeys-supervise` thread and
//!   one merged event stream that carries [`Status`] changes, for hosts without such a loop (the
//!   `prisc_hotkeys_*` exports of prisc_interop, which Prisc.Ui uses).
//!
//! The policy (identical for both):
//!
//! - A hook that is down is tried again at most every [`RETRY_INTERVAL`] while [`recovery`] says
//!   [`Recovery::Retry`]: on macOS for a missing permission, a stale grant, a tap that could not
//!   be made and a death (all of them come down to the Accessibility grant); never on Windows,
//!   where a failed or lost hook is this process's own ([`Recovery::Exit`]); never where there is
//!   no backend ([`Recovery::Report`]).
//! - A stale grant (Accessibility reads as granted, yet the tap the crate made was stripped of key
//!   events) heals only when the user removes and re-adds the app, so it backs off: attempts 2, 4
//!   and 8 ticks apart, then every [`STALE_GRANT_MAX_TICKS`] ticks (32 s). The skipped ticks still
//!   ask the permission probe, and a read that says the permission is really missing ends the
//!   back-off at once.
//! - Registrations are the supervisor's: each gets a stable id (from 1, never reused) that
//!   survives restarts; one made while the hook is down is held and registered on the next hook,
//!   as is every live one after a death. Validation failures and duplicates are refused up front,
//!   as [`Hotkeys::register`] refuses them.
//! - Pause and observe are switches on the supervisor that every new hook starts with.
//! - Recording sessions get supervisor-wide tokens too, so a token from a dead hook never cancels
//!   or matches a session on its successor.
//!
//! Nothing here logs: a host reads the [`Change`]s and logs what it wants.

use std::collections::{BTreeMap, HashMap};
use std::fmt;
use std::hash::Hash;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError};
use std::sync::{Arc, Condvar, Mutex, PoisonError};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use crate::hotkeys::{lock, StartFailure};
use crate::{Error, Event, Events, Gesture, HotkeyId, Hotkeys, KeyEvent, Modifiers, REVOKED_REASON, STALE_GRANT_CODE};

/// How often a hook that is down for a retryable reason is tried again. A grant in System
/// Settings notifies nobody, so this is how a host finds out; each try is a thread start plus one
/// tccd query (about 10 ms), and the user who just flipped the switch expects the hotkeys to work
/// within seconds.
pub const RETRY_INTERVAL: Duration = Duration::from_secs(2);

/// The longest a stale grant waits between start attempts, in [`RETRY_INTERVAL`] ticks (32 s):
/// each attempt makes and tears down a keyboard tap.
pub const STALE_GRANT_MAX_TICKS: u32 = 16;

/// Why a supervised hook is down.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Down {
    /// The hook could not start, or stopped, with this error.
    Error(Error),
    /// macOS: Accessibility reads as granted, yet the tap was made without key events. Granting
    /// again does nothing; removing the app from the list and adding it again does.
    StaleGrant,
}

impl Down {
    /// The machine-readable code (one of [`crate::ERROR_CODES`]).
    pub fn code(&self) -> &'static str {
        match self {
            Down::Error(error) => error.code(),
            Down::StaleGrant => STALE_GRANT_CODE,
        }
    }
}

impl fmt::Display for Down {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Down::Error(error) => error.fmt(f),
            Down::StaleGrant => f.write_str("the Accessibility permission is listed as granted but no longer works for the keyboard hook"),
        }
    }
}

impl From<Error> for Down {
    fn from(error: Error) -> Self {
        Down::Error(error)
    }
}

impl From<StartFailure> for Down {
    fn from(failure: StartFailure) -> Self {
        match failure {
            StartFailure {
                error: Error::PermissionDenied,
                stale_grant: true,
            } => Down::StaleGrant,
            StartFailure {
                error,
                ..
            } => Down::Error(error),
        }
    }
}

/// What a host does while its hook is down.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum Recovery {
    /// Report the reason and try again at most every [`RETRY_INTERVAL`].
    Retry,
    /// Report the reason; trying again is pointless (no backend).
    Report,
    /// The hook failed in a way only this process has: a host that can hand the hotkeys to another
    /// process (the standby capturer) exits; one that cannot reports it for good.
    Exit,
}

/// The OS whose recovery policy applies (a parameter, so each OS's table runs on one machine).
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum Platform {
    MacOs,
    Windows,
    /// No hook backend (Linux).
    Other,
}

impl Platform {
    /// The OS this was built for.
    pub const CURRENT: Platform = if cfg!(target_os = "macos") {
        Platform::MacOs
    } else if cfg!(windows) {
        Platform::Windows
    } else {
        Platform::Other
    };
}

/// [`recovery_on`] for this OS.
pub fn recovery(down: &Down) -> Recovery {
    recovery_on(down, Platform::CURRENT)
}

/// What a host does while its hook is down for `down` on `platform`.
pub fn recovery_on(down: &Down, platform: Platform) -> Recovery {
    match (down, platform) {
        // macOS: a NULL tap, a stale grant and a revocation all come down to the Accessibility
        // grant, which every process of the app shares, so giving up would only burn crash
        // strikes. Granting it later must make the hotkeys work without a restart.
        (Down::StaleGrant, Platform::MacOs) => Recovery::Retry,
        (Down::Error(Error::PermissionDenied | Error::HookFailed(_) | Error::Stopped), Platform::MacOs) => Recovery::Retry,
        // Windows: the OS refused or lost this process's hook (a failed SetWindowsHookEx, a
        // message-loop error, or a silently removed hook that could not be re-installed), which a
        // fresh process need not share.
        (Down::Error(Error::HookFailed(_) | Error::Stopped), _) => Recovery::Exit,
        _ => Recovery::Report,
    }
}

/// One event of a hook, with the hook's own registration ids and recording tokens.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum HookEvent<I> {
    /// A registered gesture was pressed; `repeat` = an auto-repeat of a press that fired.
    Hotkey { id: I, repeat: bool },
    /// While recording `session`: the modifiers held changed.
    RecordingModifiers { session: u32, modifiers: Modifiers },
    /// Recording `session` finished with `gesture`.
    Recorded { session: u32, gesture: Gesture },
    /// The observe stream.
    Key(KeyEvent),
    /// The hook died with this reason; the last event.
    Stopped(String),
}

/// What a [`Supervisor`] needs of a running hook: [`Hotkeys`] outside tests, so the policy can be
/// tested without installing one.
pub trait Hook: Send + Sync + 'static {
    /// The hook's own registration id.
    type Id: Copy + Eq + Hash + Send + Sync + 'static;
    /// The hook's event queue, read with [`Hook::next_event`].
    type Events: Send + 'static;

    fn register(&self, gesture: Gesture) -> Result<Self::Id, Error>;
    fn unregister(&self, id: Self::Id);
    fn set_paused(&self, paused: bool);
    fn set_observe(&self, observe: bool);
    /// Arms a recording and returns the hook's token; None once the hook has stopped.
    fn start_recording(&self) -> Option<u32>;
    fn stop_recording(&self, session: u32);
    /// The next event of `events`, waiting at most `timeout`; `Disconnected` once the queue is
    /// drained after the hook ended.
    fn next_event(events: &Self::Events, timeout: Duration) -> Result<HookEvent<Self::Id>, RecvTimeoutError>;
}

impl Hook for Hotkeys {
    type Id = HotkeyId;
    type Events = Events;

    fn register(&self, gesture: Gesture) -> Result<HotkeyId, Error> {
        Hotkeys::register(self, gesture)
    }

    fn unregister(&self, id: HotkeyId) {
        Hotkeys::unregister(self, id);
    }

    fn set_paused(&self, paused: bool) {
        Hotkeys::set_paused(self, paused);
    }

    fn set_observe(&self, observe: bool) {
        Hotkeys::set_observe(self, observe);
    }

    fn start_recording(&self) -> Option<u32> {
        self.shared
            .alive
            .load(Ordering::Acquire)
            .then(|| Hotkeys::start_recording(self))
    }

    fn stop_recording(&self, session: u32) {
        Hotkeys::stop_recording(self, session);
    }

    fn next_event(events: &Events, timeout: Duration) -> Result<HookEvent<HotkeyId>, RecvTimeoutError> {
        Ok(match events.recv_timeout(timeout)? {
            Event::Hotkey {
                id,
                repeat,
            } => HookEvent::Hotkey {
                id,
                repeat,
            },
            Event::RecordingModifiers {
                session,
                modifiers,
            } => HookEvent::RecordingModifiers {
                session,
                modifiers,
            },
            Event::Recorded {
                session,
                gesture,
            } => HookEvent::Recorded {
                session,
                gesture,
            },
            Event::Key(key) => HookEvent::Key(key),
            Event::Stopped(reason) => HookEvent::Stopped(reason),
        })
    }
}

/// Starts [`Hotkeys`] for a supervisor, telling a stale grant from a missing permission (the
/// crate-private start detail behind [`Down::StaleGrant`]).
pub fn start() -> Result<(Hotkeys, Events), Down> {
    Hotkeys::start_detailed().map_err(Down::from)
}

/// Starts one hook: [`start`] outside tests.
pub type Starter<H> = Box<dyn FnMut() -> Result<(H, <H as Hook>::Events), Down> + Send>;

/// Whether the Accessibility permission reads as granted right now (never prompts), asked on the
/// retry ticks a stale grant skips: `prisc_platform::permissions::accessibility` outside tests.
pub type Probe = Box<dyn FnMut() -> bool + Send>;

/// What happened to the supervisor's hook, in order: the host forwards events, updates its
/// statuses and logs from these (see [`Supervisor::take_changes`]).
pub enum Change<H: Hook> {
    /// A hook came up. Forward its `events`, and report its death with this `generation`.
    Started { generation: u64, events: H::Events },
    /// A start attempt failed (one per attempt, repeats included).
    StartFailed(Down),
    /// The running hook died with `reason`, and is down for `down` now.
    Died { reason: String, down: Down },
}

impl<H: Hook> fmt::Debug for Change<H> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Change::Started {
                generation,
                ..
            } => f
                .debug_struct("Started")
                .field("generation", generation)
                .finish_non_exhaustive(),
            Change::StartFailed(down) => f
                .debug_tuple("StartFailed")
                .field(down)
                .finish(),
            Change::Died {
                reason,
                down,
            } => f
                .debug_struct("Died")
                .field("reason", reason)
                .field("down", down)
                .finish(),
        }
    }
}

struct Slot<H> {
    paused: bool,
    observe: bool,
    hook: Option<Arc<H>>,
}

/// The pause and observe switches, for another thread (the standby capturer's stdin reader applies
/// `{"type":"pause"}` at once, even mid-capture). They reach whichever hook is running, including
/// one started later.
pub struct PauseHandle<H>(Arc<Mutex<Slot<H>>>);

impl<H> Clone for PauseHandle<H> {
    fn clone(&self) -> Self {
        Self(Arc::clone(&self.0))
    }
}

impl<H: Hook> PauseHandle<H> {
    /// Paused: registered gestures neither fire nor swallow. Recording and observe are unaffected.
    pub fn set_paused(&self, paused: bool) {
        let mut slot = lock(&self.0);
        slot.paused = paused;
        if let Some(hook) = &slot.hook {
            hook.set_paused(paused);
        }
    }

    /// Turns the observe stream on or off.
    pub fn set_observe(&self, observe: bool) {
        let mut slot = lock(&self.0);
        slot.observe = observe;
        if let Some(hook) = &slot.hook {
            hook.set_observe(observe);
        }
    }
}

/// No hook runs: matches no generation (they start at 1).
const NO_GENERATION: u64 = 0;

struct Routes<I> {
    generation: u64,
    /// The running hook's ids to stable ids.
    ids: HashMap<I, u32>,
    /// The running hook's recording token to the supervisor's.
    sessions: HashMap<u32, u32>,
}

/// An event of the running hook in the supervisor's terms: stable ids and tokens.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Routed {
    Hotkey { id: u32, repeat: bool },
    RecordingModifiers { session: u32, modifiers: Modifiers },
    Recorded { session: u32, gesture: Gesture },
    Key(KeyEvent),
}

/// Translates the running hook's events for the thread that forwards them
/// ([`Supervisor::router`]).
pub struct Router<I>(Arc<Mutex<Routes<I>>>);

impl<I> Clone for Router<I> {
    fn clone(&self) -> Self {
        Self(Arc::clone(&self.0))
    }
}

impl<I: Copy + Eq + Hash> Router<I> {
    /// `event` of the hook started as `generation`, in stable terms. None for a `Stopped` (the
    /// forwarder reports it with [`Supervisor::died`]), for an event of a hook that is not the
    /// running one any more, and for an id or token the supervisor no longer holds (a press racing
    /// an unregister, a cancelled or replaced recording).
    pub fn route(&self, generation: u64, event: HookEvent<I>) -> Option<Routed> {
        let mut routes = lock(&self.0);
        if routes.generation != generation {
            return None;
        }
        match event {
            HookEvent::Hotkey {
                id,
                repeat,
            } => routes
                .ids
                .get(&id)
                .map(|&id| Routed::Hotkey {
                    id,
                    repeat,
                }),
            HookEvent::RecordingModifiers {
                session,
                modifiers,
            } => routes
                .sessions
                .get(&session)
                .map(|&session| Routed::RecordingModifiers {
                    session,
                    modifiers,
                }),
            HookEvent::Recorded {
                session,
                gesture,
            } => routes
                .sessions
                .remove(&session)
                .map(|session| Routed::Recorded {
                    session,
                    gesture,
                }),
            HookEvent::Key(key) => Some(Routed::Key(key)),
            HookEvent::Stopped(_) => None,
        }
    }
}

struct Registration<I> {
    gesture: Gesture,
    /// Its id on the running hook; None while the hook is down.
    hook_id: Option<I>,
}

/// The sans-IO supervisor (see the module docs). The methods that act on time take it as an
/// argument, so the whole policy runs under a fake clock in tests.
pub struct Supervisor<H: Hook> {
    starter: Starter<H>,
    permitted: Probe,
    platform: Platform,
    /// Retry a hook that is down even with nothing registered (a host that always wants one).
    retry_when_idle: bool,
    slot: Arc<Mutex<Slot<H>>>,
    routes: Arc<Mutex<Routes<H::Id>>>,
    /// Of the running hook, or of the last one.
    generation: u64,
    /// Why the hook is down and the text that goes with it; None while it runs.
    down: Option<(Down, String)>,
    /// The earliest the next start (or stale-grant tick) may be tried.
    next_try: Instant,
    /// Ticks between attempts while the grant is stale (0: not backing off).
    stale_ticks: u32,
    /// Ticks left before the next of those attempts.
    stale_skip: u32,
    registrations: BTreeMap<u32, Registration<H::Id>>,
    next_id: u32,
    next_session: u32,
    shut: bool,
    changes: Vec<Change<H>>,
}

impl Supervisor<Hotkeys> {
    /// The real hook ([`start`]), the real permission probe and this OS's policy. Not started yet:
    /// call [`start`](Self::start).
    pub fn new() -> Self {
        Self::with_starter(
            Box::new(start),
            Box::new(prisc_platform::permissions::accessibility),
            Platform::CURRENT,
        )
    }
}

impl Default for Supervisor<Hotkeys> {
    fn default() -> Self {
        Self::new()
    }
}

impl<H: Hook> Supervisor<H> {
    /// A supervisor over `starter`, asking `permitted` on the ticks a stale grant skips, with
    /// `platform`'s recovery policy. Not started yet: call [`start`](Self::start).
    pub fn with_starter(starter: Starter<H>, permitted: Probe, platform: Platform) -> Self {
        Self {
            starter,
            permitted,
            platform,
            retry_when_idle: false,
            slot: Arc::new(Mutex::new(Slot {
                paused: false,
                observe: false,
                hook: None,
            })),
            routes: Arc::new(Mutex::new(Routes {
                generation: NO_GENERATION,
                ids: HashMap::new(),
                sessions: HashMap::new(),
            })),
            generation: NO_GENERATION,
            down: Some((Down::Error(Error::Stopped), String::new())),
            next_try: Instant::now(),
            stale_ticks: 0,
            stale_skip: 0,
            registrations: BTreeMap::new(),
            next_id: 1,
            next_session: 1,
            shut: false,
            changes: Vec::new(),
        }
    }

    /// Whether a hook that is down is retried with nothing registered on it. Off by default: a
    /// hook nothing would be registered on is not worth a thread start and a tccd query every
    /// 2 s. [`Supervised`] turns it on, for a host that also observes and records.
    pub fn set_retry_when_idle(&mut self, retry: bool) {
        self.retry_when_idle = retry;
    }

    /// The first start attempt.
    pub fn start(&mut self, now: Instant) {
        self.attempt(now);
    }

    /// Why the hook is down; None while it runs.
    pub fn down(&self) -> Option<&Down> {
        self.down.as_ref().map(|(down, _)| down)
    }

    /// The text that goes with [`down`](Self::down): the start failure's message, or the reason
    /// the hook gave when it died. Empty while it runs.
    pub fn detail(&self) -> &str {
        self.down
            .as_ref()
            .map_or("", |(_, detail)| detail.as_str())
    }

    /// What the host should do about [`down`](Self::down); None while the hook runs.
    pub fn recovery(&self) -> Option<Recovery> {
        self.down()
            .map(|down| recovery_on(down, self.platform))
    }

    /// For the thread that forwards the running hook's events.
    pub fn router(&self) -> Router<H::Id> {
        Router(Arc::clone(&self.routes))
    }

    /// For a thread that pauses or observes at once (see [`PauseHandle`]).
    pub fn pause_handle(&self) -> PauseHandle<H> {
        PauseHandle(Arc::clone(&self.slot))
    }

    /// [`PauseHandle::set_paused`].
    pub fn set_paused(&self, paused: bool) {
        self.pause_handle().set_paused(paused);
    }

    /// [`PauseHandle::set_observe`].
    pub fn set_observe(&self, observe: bool) {
        self.pause_handle().set_observe(observe);
    }

    /// Validates `gesture` ([`Gesture::validate`]), refuses one equal to a registration already
    /// held (`Duplicate`), and holds it under a stable id: on the running hook at once, or on the
    /// next one while the hook is down. A running hook found dead here counts as its death
    /// ([`Change::Died`]), and the gesture waits for the successor too.
    pub fn register(&mut self, gesture: Gesture) -> Result<u32, Error> {
        gesture.validate()?;
        if self
            .registrations
            .values()
            .any(|registration| registration.gesture == gesture)
        {
            return Err(Error::Duplicate);
        }
        let id = self.next_id;
        self.next_id += 1;
        self.registrations.insert(
            id,
            Registration {
                gesture,
                hook_id: None,
            },
        );
        if self.down.is_none() && self.register_on_hook(id) == Err(Error::Stopped) {
            self.lose(self.generation, "the keyboard hook has stopped".into());
        }
        Ok(id)
    }

    /// Drops registration `id`; false when it is unknown.
    pub fn unregister(&mut self, id: u32) -> bool {
        let Some(registration) = self.registrations.remove(&id) else {
            return false;
        };
        if let Some(hook_id) = registration.hook_id {
            lock(&self.routes).ids.remove(&hook_id);
            if let Some(hook) = self.hook() {
                hook.unregister(hook_id);
            }
        }
        true
    }

    /// Whether registration `id` is on the running hook.
    pub fn is_registered(&self, id: u32) -> bool {
        self.registrations
            .get(&id)
            .is_some_and(|registration| registration.hook_id.is_some())
    }

    /// Arms a recording on the running hook and returns its token (nonzero, never reused by this
    /// supervisor); None while the hook is down.
    pub fn start_recording(&mut self) -> Option<u32> {
        let hook = self.hook()?;
        // Held across the arm, so the session's first event always finds its token.
        let mut routes = lock(&self.routes);
        let hook_session = hook.start_recording()?;
        let session = self.next_session;
        self.next_session = self.next_session.checked_add(1).unwrap_or(1);
        // Arming again replaces the hook's session, so only the newest token can still report.
        routes.sessions.clear();
        routes.sessions.insert(hook_session, session);
        Some(session)
    }

    /// Cancels recording `session` if it is still the armed one; a no-op otherwise.
    pub fn stop_recording(&mut self, session: u32) {
        let mut routes = lock(&self.routes);
        let Some(hook_session) = routes
            .sessions
            .iter()
            .find_map(|(&hook_session, &ours)| (ours == session).then_some(hook_session))
        else {
            return;
        };
        routes.sessions.remove(&hook_session);
        drop(routes);
        if let Some(hook) = self.hook() {
            hook.stop_recording(hook_session);
        }
    }

    /// The hook started as `generation` died with `reason` (its `Stopped` event, or its queue
    /// closing). A no-op when that hook is not the running one any more (a death already noticed).
    pub fn died(&mut self, generation: u64, reason: String) {
        if self.down.is_none() && generation == self.generation {
            self.lose(generation, reason);
        }
    }

    /// When the host must call [`poll`](Self::poll) next: None while the hook runs, while
    /// retrying is pointless ([`Recovery`] is not `Retry`), and while nothing would be registered
    /// on it (unless it retries when idle), so a loop can block indefinitely.
    pub fn next_retry(&self) -> Option<Instant> {
        let wanted = self.retry_when_idle || !self.registrations.is_empty();
        match self.recovery() {
            Some(Recovery::Retry) if wanted && !self.shut => Some(self.next_try),
            _ => None,
        }
    }

    /// Retries a hook that is down once [`next_retry`](Self::next_retry) has passed. The ticks a
    /// stale grant skips only ask the permission probe.
    pub fn poll(&mut self, now: Instant) {
        let Some(at) = self.next_retry() else {
            return;
        };
        if now < at {
            return;
        }
        if self.stale_skip > 0 {
            self.next_try = now + RETRY_INTERVAL;
            if (self.permitted)() {
                self.stale_skip -= 1;
                return;
            }
            // Really missing now (the user removed the app from the list): the next grant is the
            // fix, so stop backing off and find out at once.
            self.stale_ticks = 0;
            self.stale_skip = 0;
        }
        self.attempt(now);
    }

    /// Everything that happened since the last call, in order.
    pub fn take_changes(&mut self) -> Vec<Change<H>> {
        std::mem::take(&mut self.changes)
    }

    /// Stops supervising: no more starts, and the running hook (if any) is handed back so the
    /// caller drops it outside its own locks (dropping a hook joins its threads).
    pub fn shutdown(&mut self) -> Option<Arc<H>> {
        self.shut = true;
        lock(&self.routes).generation = NO_GENERATION;
        lock(&self.slot).hook.take()
    }

    fn hook(&self) -> Option<Arc<H>> {
        lock(&self.slot).hook.clone()
    }

    fn register_on_hook(&mut self, id: u32) -> Result<(), Error> {
        let Some(hook) = self.hook() else {
            return Ok(());
        };
        let Some(registration) = self.registrations.get_mut(&id) else {
            return Ok(());
        };
        // Held across the register, so a press right after it always finds its stable id.
        let mut routes = lock(&self.routes);
        let hook_id = hook.register(registration.gesture)?;
        registration.hook_id = Some(hook_id);
        routes.ids.insert(hook_id, id);
        Ok(())
    }

    fn attempt(&mut self, now: Instant) {
        if self.shut {
            return;
        }
        self.next_try = now + RETRY_INTERVAL;
        match (self.starter)() {
            Ok((hook, events)) => {
                self.generation += 1;
                let generation = self.generation;
                let hook = Arc::new(hook);
                {
                    let mut slot = lock(&self.slot);
                    hook.set_paused(slot.paused);
                    hook.set_observe(slot.observe);
                    slot.hook = Some(Arc::clone(&hook));
                }
                {
                    let mut routes = lock(&self.routes);
                    routes.generation = generation;
                    routes.ids.clear();
                    routes.sessions.clear();
                }
                self.down = None;
                self.stale_ticks = 0;
                self.stale_skip = 0;
                self.changes.push(Change::Started {
                    generation,
                    events,
                });
                let ids: Vec<u32> = self.registrations.keys().copied().collect();
                for id in ids {
                    if self.register_on_hook(id) == Err(Error::Stopped) {
                        // Died again already; the rest wait for the next hook.
                        self.lose(generation, "the keyboard hook has stopped".into());
                        break;
                    }
                }
            }
            Err(down) => {
                if down == Down::StaleGrant {
                    self.stale_ticks = (self.stale_ticks * 2).clamp(2, STALE_GRANT_MAX_TICKS);
                    self.stale_skip = self.stale_ticks - 1;
                } else {
                    self.stale_ticks = 0;
                    self.stale_skip = 0;
                }
                self.down = Some((down.clone(), down.to_string()));
                self.changes.push(Change::StartFailed(down));
            }
        }
    }

    /// The running hook of `generation` is gone: everything it held waits for the next one.
    fn lose(&mut self, generation: u64, reason: String) {
        debug_assert_eq!(generation, self.generation);
        // A revoked grant ends the tap (macOS): the hook is down for want of the permission (as a
        // start would now say), not for a fault.
        let down = if reason == REVOKED_REASON && self.platform == Platform::MacOs {
            Down::Error(Error::PermissionDenied)
        } else {
            Down::Error(Error::Stopped)
        };
        let dead = lock(&self.slot).hook.take();
        {
            let mut routes = lock(&self.routes);
            routes.generation = NO_GENERATION;
            routes.ids.clear();
            routes.sessions.clear();
        }
        for registration in self.registrations.values_mut() {
            registration.hook_id = None;
        }
        self.down = Some((down.clone(), reason.clone()));
        self.changes.push(Change::Died {
            reason,
            down,
        });
        // Joins the dead hook's threads, which have ended or are ending.
        drop(dead);
    }
}

/// A supervised hook's state, as [`SupervisedEvent::Status`] reports it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Status {
    /// Why the hook is down; None while it runs.
    pub down: Option<Down>,
    /// Whether the supervisor tries again by itself ([`Recovery::Retry`]); false while it runs.
    pub recoverable: bool,
    /// The start failure's message or the reason the hook died; empty while it runs.
    pub detail: String,
}

/// One event of a [`Supervised`] hook.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SupervisedEvent {
    /// Registration `id` (stable) was pressed.
    Hotkey {
        id: u32,
        repeat: bool,
    },
    RecordingModifiers {
        session: u32,
        modifiers: Modifiers,
    },
    Recorded {
        session: u32,
        gesture: Gesture,
    },
    Key(KeyEvent),
    /// The hook came up, went down, or is down for another reason now. The first event of every
    /// `Supervised` is one, with the state its first start left.
    Status(Status),
}

impl From<Routed> for SupervisedEvent {
    fn from(routed: Routed) -> Self {
        match routed {
            Routed::Hotkey {
                id,
                repeat,
            } => SupervisedEvent::Hotkey {
                id,
                repeat,
            },
            Routed::RecordingModifiers {
                session,
                modifiers,
            } => SupervisedEvent::RecordingModifiers {
                session,
                modifiers,
            },
            Routed::Recorded {
                session,
                gesture,
            } => SupervisedEvent::Recorded {
                session,
                gesture,
            },
            Routed::Key(key) => SupervisedEvent::Key(key),
        }
    }
}

/// How long the supervise thread waits on a running hook's queue (or for a wake-up while nothing
/// is due) before it looks again. Stopping wakes it at once, so this only bounds a missed wake-up.
const IDLE_WAIT: Duration = Duration::from_secs(5);

struct Inner<H: Hook> {
    supervisor: Mutex<Supervisor<H>>,
    events: Mutex<mpsc::Receiver<SupervisedEvent>>,
    stopping: AtomicBool,
    /// Wakes the supervise thread while it waits for a retry: set by stop and by every call that
    /// may change what is due.
    wake: (Mutex<bool>, Condvar),
}

/// A [`Supervisor`] over [`Hotkeys`] with its own `prisc-hotkeys-supervise` thread: it retries,
/// re-registers and forwards by itself, and merges everything into one event stream that also
/// carries [`Status`] changes. Every method may be called from any thread. Dropping it stops it.
pub struct Supervised<H: Hook = Hotkeys> {
    inner: Arc<Inner<H>>,
    thread: Mutex<Option<JoinHandle<()>>>,
}

impl Supervised<Hotkeys> {
    /// Starts the real hook under supervision. The first start runs here, so the first event (a
    /// [`Status`]) is queued when this returns. Fails only when the thread cannot be spawned.
    pub fn start() -> std::io::Result<Self> {
        Self::with_supervisor(Supervisor::new())
    }
}

impl<H: Hook> Supervised<H> {
    /// Runs `supervisor` (not started yet) on its own thread, retrying even with nothing
    /// registered.
    pub fn with_supervisor(mut supervisor: Supervisor<H>) -> std::io::Result<Self> {
        supervisor.set_retry_when_idle(true);
        supervisor.start(Instant::now());
        let (tx, rx) = mpsc::channel();
        let mut emitter = Emitter {
            tx,
            last: None,
        };
        let current = emitter.apply(&mut supervisor, None);
        let inner = Arc::new(Inner {
            supervisor: Mutex::new(supervisor),
            events: Mutex::new(rx),
            stopping: AtomicBool::new(false),
            wake: (Mutex::new(false), Condvar::new()),
        });
        let spawned = thread::Builder::new()
            .name("prisc-hotkeys-supervise".into())
            .spawn({
                let inner = Arc::clone(&inner);
                move || supervise(&inner, emitter, current)
            });
        match spawned {
            Ok(thread) => Ok(Self {
                inner,
                thread: Mutex::new(Some(thread)),
            }),
            Err(error) => {
                drop(lock(&inner.supervisor).shutdown());
                Err(error)
            }
        }
    }

    /// [`Supervisor::register`]: refusals come back at once; otherwise a stable id, registered now
    /// or as soon as the hook is back.
    pub fn register(&self, gesture: Gesture) -> Result<u32, Error> {
        let result = lock(&self.inner.supervisor).register(gesture);
        self.wake();
        result
    }

    /// [`Supervisor::unregister`].
    pub fn unregister(&self, id: u32) -> bool {
        let removed = lock(&self.inner.supervisor).unregister(id);
        self.wake();
        removed
    }

    /// Turns the observe stream on or off, for this hook and every later one.
    pub fn set_observe(&self, observe: bool) {
        lock(&self.inner.supervisor).set_observe(observe);
    }

    /// Pauses the registered gestures, on this hook and every later one.
    pub fn set_paused(&self, paused: bool) {
        lock(&self.inner.supervisor).set_paused(paused);
    }

    /// [`Supervisor::start_recording`]: None while the hook is down.
    pub fn start_recording(&self) -> Option<u32> {
        lock(&self.inner.supervisor).start_recording()
    }

    /// [`Supervisor::stop_recording`].
    pub fn stop_recording(&self, session: u32) {
        lock(&self.inner.supervisor).stop_recording(session);
    }

    /// The next event, waiting at most `timeout`; `Disconnected` once stopped and drained. Calls
    /// are serialized.
    pub fn next_event(&self, timeout: Duration) -> Result<SupervisedEvent, RecvTimeoutError> {
        lock(&self.inner.events).recv_timeout(timeout)
    }

    /// Stops the hook and the supervisor and joins their threads. Idempotent, and safe while
    /// another thread waits in [`next_event`](Self::next_event), which then drains what is queued
    /// and reports `Disconnected`.
    pub fn stop(&self) {
        self.inner
            .stopping
            .store(true, Ordering::Release);
        // Dropped outside the lock: it joins the hook's threads, which closes the queue the
        // supervise thread may be waiting on.
        let hook = lock(&self.inner.supervisor).shutdown();
        drop(hook);
        self.wake();
        let thread = lock(&self.thread).take();
        if let Some(thread) = thread {
            if thread.thread().id() != thread::current().id() {
                let _ = thread.join();
            }
        }
    }

    fn wake(&self) {
        let (flag, condvar) = &self.inner.wake;
        *lock(flag) = true;
        condvar.notify_all();
    }
}

impl<H: Hook> Drop for Supervised<H> {
    fn drop(&mut self) {
        self.stop();
    }
}

/// Turns the supervisor's changes into events on the merged stream, reporting each state once.
struct Emitter {
    tx: mpsc::Sender<SupervisedEvent>,
    /// The last status sent, as (down code, detail), None for running; None before the first.
    last: Option<Option<(&'static str, String)>>,
}

impl Emitter {
    /// Sends the statuses `supervisor`'s pending changes amount to, and returns the hook to
    /// forward now: a newly started one, else `current` while it is still the running one.
    fn apply<H: Hook>(&mut self, supervisor: &mut Supervisor<H>, current: Option<(u64, H::Events)>) -> Option<(u64, H::Events)> {
        let mut current = current;
        for change in supervisor.take_changes() {
            match change {
                Change::Started {
                    generation,
                    events,
                } => {
                    current = Some((generation, events));
                    self.status(None, false, String::new());
                }
                Change::StartFailed(down) => {
                    let recoverable = recovery_on(&down, supervisor.platform) == Recovery::Retry;
                    let detail = down.to_string();
                    self.status(Some(down), recoverable, detail);
                }
                Change::Died {
                    reason,
                    down,
                } => {
                    let recoverable = recovery_on(&down, supervisor.platform) == Recovery::Retry;
                    self.status(Some(down), recoverable, reason);
                }
            }
        }
        current.filter(|(generation, _)| supervisor.down.is_none() && *generation == supervisor.generation)
    }

    fn status(&mut self, down: Option<Down>, recoverable: bool, detail: String) {
        let key = down
            .as_ref()
            .map(|down| (down.code(), detail.clone()));
        if self.last.as_ref() == Some(&key) {
            return;
        }
        self.last = Some(key);
        self.send(SupervisedEvent::Status(Status {
            down,
            recoverable,
            detail,
        }));
    }

    fn send(&self, event: SupervisedEvent) {
        let _ = self.tx.send(event);
    }
}

/// The `prisc-hotkeys-supervise` thread: forwards the running hook's events, notices its death
/// and retries while it is down. A panic here stops the hook (nobody would drain it any more) and
/// ends the stream with a status that says so.
fn supervise<H: Hook>(inner: &Inner<H>, mut emitter: Emitter, current: Option<(u64, H::Events)>) {
    let router = lock(&inner.supervisor).router();
    let outcome = catch_unwind(AssertUnwindSafe(|| run(inner, &router, &mut emitter, current)));
    if let Err(payload) = outcome {
        let text = payload
            .downcast_ref::<&str>()
            .map(|text| text.to_string())
            .or_else(|| payload.downcast_ref::<String>().cloned())
            .unwrap_or_default();
        let detail = format!("the hotkey supervisor panicked: {text}");
        drop(lock(&inner.supervisor).shutdown());
        emitter.send(SupervisedEvent::Status(Status {
            down: Some(Down::Error(Error::HookFailed(detail.clone()))),
            recoverable: false,
            detail,
        }));
    }
    // Dropping the emitter closes the stream once it is drained.
}

fn run<H: Hook>(inner: &Inner<H>, router: &Router<H::Id>, emitter: &mut Emitter, mut current: Option<(u64, H::Events)>) {
    loop {
        if inner.stopping.load(Ordering::Acquire) {
            return;
        }
        if let Some((generation, events)) = current.take() {
            match H::next_event(&events, IDLE_WAIT) {
                Ok(HookEvent::Stopped(reason)) => {
                    let mut supervisor = lock(&inner.supervisor);
                    supervisor.died(generation, reason);
                    supervisor.poll(Instant::now());
                    current = emitter.apply(&mut supervisor, None);
                }
                Ok(event) => {
                    if let Some(routed) = router.route(generation, event) {
                        emitter.send(routed.into());
                    }
                    current = Some((generation, events));
                }
                Err(RecvTimeoutError::Timeout) => {
                    // A death noticed elsewhere (a register that found the hook dead) shows here.
                    let mut supervisor = lock(&inner.supervisor);
                    supervisor.poll(Instant::now());
                    current = emitter.apply(&mut supervisor, Some((generation, events)));
                }
                Err(RecvTimeoutError::Disconnected) => {
                    if inner.stopping.load(Ordering::Acquire) {
                        return;
                    }
                    let mut supervisor = lock(&inner.supervisor);
                    supervisor.died(generation, "the keyboard hook's event queue closed".into());
                    supervisor.poll(Instant::now());
                    current = emitter.apply(&mut supervisor, None);
                }
            }
            continue;
        }

        let due = {
            let mut supervisor = lock(&inner.supervisor);
            supervisor.poll(Instant::now());
            current = emitter.apply(&mut supervisor, None);
            supervisor.next_retry()
        };
        if current.is_some() {
            continue;
        }
        let (flag, condvar) = &inner.wake;
        let mut woken = lock(flag);
        while !*woken && !inner.stopping.load(Ordering::Acquire) {
            let wait = due.map_or(IDLE_WAIT, |at| at.saturating_duration_since(Instant::now()));
            if wait.is_zero() {
                break;
            }
            let (guard, timeout) = condvar
                .wait_timeout(woken, wait)
                .unwrap_or_else(PoisonError::into_inner);
            woken = guard;
            if timeout.timed_out() {
                break;
            }
        }
        *woken = false;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Key;
    use std::collections::VecDeque;
    use std::sync::atomic::AtomicU32;

    // ---- a fake hook ----

    #[derive(Default)]
    struct FakeHook {
        registered: Mutex<Vec<(u32, Gesture)>>,
        next_id: AtomicU32,
        paused: AtomicBool,
        observe: AtomicBool,
        sessions: AtomicU32,
        stopped_sessions: Mutex<Vec<u32>>,
        /// Refuses like a hook whose thread has ended.
        dead: AtomicBool,
        /// The hook's side of its queue; dropped with the hook, which closes the queue.
        tx: Mutex<Option<mpsc::Sender<HookEvent<u32>>>>,
    }

    impl FakeHook {
        fn gestures(&self) -> Vec<Gesture> {
            lock(&self.registered)
                .iter()
                .map(|(_, gesture)| *gesture)
                .collect()
        }

        fn id_of(&self, gesture: Gesture) -> u32 {
            lock(&self.registered)
                .iter()
                .find(|(_, registered)| *registered == gesture)
                .expect("registered")
                .0
        }

        fn send(&self, event: HookEvent<u32>) {
            lock(&self.tx)
                .as_ref()
                .expect("queue open")
                .send(event)
                .unwrap();
        }

        /// Dies like a hook: queues Stopped and closes its queue.
        fn die(&self, reason: &str) {
            self.dead.store(true, Ordering::SeqCst);
            if let Some(tx) = lock(&self.tx).take() {
                let _ = tx.send(HookEvent::Stopped(reason.into()));
            }
        }
    }

    /// The fake as the supervisor owns it; the test keeps the `Arc` to look inside.
    struct Fake(Arc<FakeHook>);

    impl Drop for Fake {
        fn drop(&mut self) {
            // Dropping a real hook closes its queue.
            lock(&self.0.tx).take();
        }
    }

    impl Hook for Fake {
        type Id = u32;
        type Events = mpsc::Receiver<HookEvent<u32>>;

        fn register(&self, gesture: Gesture) -> Result<u32, Error> {
            if self.0.dead.load(Ordering::SeqCst) {
                return Err(Error::Stopped);
            }
            let id = self.0.next_id.fetch_add(1, Ordering::SeqCst) + 1;
            lock(&self.0.registered).push((id, gesture));
            Ok(id)
        }

        fn unregister(&self, id: u32) {
            lock(&self.0.registered).retain(|(registered, _)| *registered != id);
        }

        fn set_paused(&self, paused: bool) {
            self.0.paused.store(paused, Ordering::SeqCst);
        }

        fn set_observe(&self, observe: bool) {
            self.0
                .observe
                .store(observe, Ordering::SeqCst);
        }

        fn start_recording(&self) -> Option<u32> {
            (!self.0.dead.load(Ordering::SeqCst)).then(|| {
                self.0
                    .sessions
                    .fetch_add(1, Ordering::SeqCst)
                    + 1
            })
        }

        fn stop_recording(&self, session: u32) {
            lock(&self.0.stopped_sessions).push(session);
        }

        fn next_event(events: &Self::Events, timeout: Duration) -> Result<HookEvent<u32>, RecvTimeoutError> {
            match events.recv_timeout(timeout) {
                Ok(HookEvent::Hotkey {
                    id: PANIC_ID,
                    ..
                }) => panic!("the fake hook's queue broke"),
                other => other,
            }
        }
    }

    /// A hotkey id no registration hands out: the fake's `next_event` panics on it, inside the
    /// supervise thread.
    const PANIC_ID: u32 = u32::MAX;

    type Starts = Arc<Mutex<VecDeque<Result<(), Down>>>>;

    /// Hook starts follow `starts`, one entry per start; running past its end fails the test.
    /// Every hook started is handed to the test on `started`.
    struct Script {
        starts: Starts,
        started: mpsc::Receiver<Arc<FakeHook>>,
        attempts: Arc<AtomicU32>,
        permitted: Arc<AtomicBool>,
        probes: Arc<AtomicU32>,
    }

    impl Script {
        fn push(&self, start: Result<(), Down>) {
            lock(&self.starts).push_back(start);
        }

        fn hook(&self) -> Arc<FakeHook> {
            self.started
                .try_recv()
                .expect("a hook started")
        }

        fn attempts(&self) -> u32 {
            self.attempts.load(Ordering::SeqCst)
        }
    }

    fn rig(script: Vec<Result<(), Down>>, platform: Platform) -> (Supervisor<Fake>, Script) {
        let starts: Starts = Arc::new(Mutex::new(VecDeque::from(script)));
        let (started_tx, started) = mpsc::channel();
        let attempts = Arc::new(AtomicU32::new(0));
        let permitted = Arc::new(AtomicBool::new(true));
        let probes = Arc::new(AtomicU32::new(0));
        let starter: Starter<Fake> = Box::new({
            let (starts, attempts) = (Arc::clone(&starts), Arc::clone(&attempts));
            let started_tx = Mutex::new(started_tx);
            move || {
                attempts.fetch_add(1, Ordering::SeqCst);
                lock(&starts)
                    .pop_front()
                    .expect("unexpected hook start")?;
                let (tx, rx) = mpsc::channel();
                let hook = Arc::new(FakeHook::default());
                *lock(&hook.tx) = Some(tx);
                let _ = lock(&started_tx).send(Arc::clone(&hook));
                Ok((Fake(hook), rx))
            }
        });
        let probe: Probe = Box::new({
            let (permitted, probes) = (Arc::clone(&permitted), Arc::clone(&probes));
            move || {
                probes.fetch_add(1, Ordering::SeqCst);
                permitted.load(Ordering::SeqCst)
            }
        });
        (
            Supervisor::with_starter(starter, probe, platform),
            Script {
                starts,
                started,
                attempts,
                permitted,
                probes,
            },
        )
    }

    /// Polls once per retry tick from tick `from` through `to` (tick k is `t0 + k * RETRY_INTERVAL`),
    /// as a host's timer does.
    fn tick(sup: &mut Supervisor<Fake>, t0: Instant, from: u32, to: u32) {
        for k in from..=to {
            sup.poll(t0 + RETRY_INTERVAL * k);
        }
    }

    fn denied() -> Result<(), Down> {
        Err(Down::Error(Error::PermissionDenied))
    }

    fn g(text: &str) -> Gesture {
        text.parse().unwrap()
    }

    fn codes(changes: Vec<Change<Fake>>) -> Vec<String> {
        changes
            .into_iter()
            .map(|change| match change {
                Change::Started {
                    ..
                } => "started".to_string(),
                Change::StartFailed(down) => format!("failed:{}", down.code()),
                Change::Died {
                    down,
                    ..
                } => format!("died:{}", down.code()),
            })
            .collect()
    }

    // ---- the recovery table ----

    #[test]
    fn recovery_table_per_os() {
        use Recovery::*;
        let cases = [
            (Down::Error(Error::PermissionDenied), Retry, Report),
            (Down::StaleGrant, Retry, Report),
            (Down::Error(Error::HookFailed("x".into())), Retry, Exit),
            (Down::Error(Error::Stopped), Retry, Exit),
            (Down::Error(Error::Unsupported), Report, Report),
        ];
        for (down, mac, windows) in cases {
            assert_eq!(recovery_on(&down, Platform::MacOs), mac, "{down:?} on macOS");
            assert_eq!(recovery_on(&down, Platform::Windows), windows, "{down:?} on Windows");
        }
        assert_eq!(recovery_on(&Down::Error(Error::Unsupported), Platform::Other), Report);
        assert_eq!(Down::StaleGrant.code(), STALE_GRANT_CODE);
        assert_eq!(Down::Error(Error::PermissionDenied).code(), "permission");
    }

    #[test]
    fn only_a_stripped_tap_is_a_stale_grant() {
        let stale = StartFailure {
            error: Error::PermissionDenied,
            stale_grant: true,
        };
        assert_eq!(Down::from(stale), Down::StaleGrant);
        assert_eq!(
            Down::from(StartFailure::from(Error::PermissionDenied)),
            Down::Error(Error::PermissionDenied)
        );
        assert_eq!(
            Down::from(StartFailure::from(Error::HookFailed("x".into()))),
            Down::Error(Error::HookFailed("x".into()))
        );
    }

    // ---- retries, under a fake clock ----

    #[test]
    fn a_missing_permission_is_retried_every_interval_until_granted() {
        let (mut sup, script) = rig(vec![denied(), denied(), Ok(())], Platform::MacOs);
        let t0 = Instant::now();
        sup.start(t0);
        assert_eq!(codes(sup.take_changes()), ["failed:permission"]);
        // nothing would be registered on it: not worth retrying
        assert_eq!(sup.next_retry(), None);
        let id = sup.register(g("F12")).unwrap();
        assert_eq!(id, 1);
        assert!(!sup.is_registered(id));
        assert_eq!(sup.next_retry(), Some(t0 + RETRY_INTERVAL));

        sup.poll(t0 + RETRY_INTERVAL - Duration::from_millis(1));
        assert_eq!(script.attempts(), 1, "not before the interval");
        sup.poll(t0 + RETRY_INTERVAL);
        assert_eq!(codes(sup.take_changes()), ["failed:permission"]);
        assert_eq!(sup.next_retry(), Some(t0 + RETRY_INTERVAL * 2));
        sup.poll(t0 + RETRY_INTERVAL * 2);
        assert_eq!(codes(sup.take_changes()), ["started"]);
        assert_eq!(sup.down(), None);
        assert_eq!(sup.next_retry(), None);
        assert!(sup.is_registered(id));
        assert_eq!(script.hook().gestures(), [g("F12")]);
        // a missing grant is never probed: the start itself asks
        assert_eq!(script.probes.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn a_stale_grant_backs_off_doubling_to_sixteen_ticks() {
        let (mut sup, script) = rig(vec![Err(Down::StaleGrant)], Platform::MacOs);
        sup.set_retry_when_idle(true);
        let t0 = Instant::now();
        sup.start(t0);
        let mut attempts_at = vec![0u32];
        for k in 1..=80u32 {
            script.push(Err(Down::StaleGrant));
            let before = script.attempts();
            tick(&mut sup, t0, k, k);
            if script.attempts() > before {
                attempts_at.push(k);
            }
        }
        // 2, 4 and 8 ticks apart, then every 16 (32 s)
        assert_eq!(attempts_at, [0, 2, 6, 14, 30, 46, 62, 78]);
        assert_eq!(sup.down(), Some(&Down::StaleGrant));
        // the skipped ticks only asked the probe
        assert_eq!(script.probes.load(Ordering::SeqCst), 80 - 7);
    }

    #[test]
    fn a_missing_read_ends_the_back_off_and_retries_at_once() {
        let (mut sup, script) = rig(vec![Err(Down::StaleGrant), Err(Down::StaleGrant)], Platform::MacOs);
        sup.set_retry_when_idle(true);
        let t0 = Instant::now();
        sup.start(t0);
        tick(&mut sup, t0, 1, 2);
        assert_eq!(script.attempts(), 2);
        // 4 ticks apart now: tick 3 only asks the probe
        tick(&mut sup, t0, 3, 3);
        assert_eq!(script.attempts(), 2);
        // the user removed the app from the list: the probe reads missing, so try at once...
        script
            .permitted
            .store(false, Ordering::SeqCst);
        script.push(denied());
        tick(&mut sup, t0, 4, 4);
        assert_eq!(script.attempts(), 3);
        assert_eq!(sup.down(), Some(&Down::Error(Error::PermissionDenied)));
        // ...and on every tick after that, as for any missing grant
        script.push(Ok(()));
        tick(&mut sup, t0, 5, 5);
        assert_eq!(script.attempts(), 4);
        assert_eq!(sup.down(), None);
    }

    #[test]
    fn a_stale_grant_that_heals_starts_the_back_off_over() {
        let (mut sup, script) = rig(vec![Err(Down::StaleGrant), Err(Down::StaleGrant), Ok(())], Platform::MacOs);
        sup.set_retry_when_idle(true);
        let t0 = Instant::now();
        sup.start(t0);
        tick(&mut sup, t0, 1, 6);
        assert_eq!((script.attempts(), sup.down()), (3, None));
        // it dies and the grant is stale again: 2 ticks to the next attempt, not 16
        script
            .hook()
            .die("the event tap was invalidated");
        sup.died(1, "the event tap was invalidated".into());
        script.push(Err(Down::StaleGrant));
        script.push(Err(Down::StaleGrant));
        tick(&mut sup, t0, 7, 7);
        assert_eq!(script.attempts(), 4);
        tick(&mut sup, t0, 8, 8);
        assert_eq!(script.attempts(), 4);
        tick(&mut sup, t0, 9, 9);
        assert_eq!(script.attempts(), 5);
    }

    #[test]
    fn windows_hook_failures_are_not_recoverable() {
        let t0 = Instant::now();
        let (mut sup, script) = rig(
            vec![Err(Down::Error(Error::HookFailed("SetWindowsHookEx failed".into())))],
            Platform::Windows,
        );
        sup.set_retry_when_idle(true);
        sup.start(t0);
        sup.register(g("F12")).unwrap();
        assert_eq!(sup.recovery(), Some(Recovery::Exit));
        assert_eq!(sup.next_retry(), None);
        tick(&mut sup, t0, 1, 10);
        assert_eq!(script.attempts(), 1);

        // and a Windows hook that dies stays down
        let (mut sup, script) = rig(vec![Ok(())], Platform::Windows);
        sup.start(t0);
        sup.register(g("F12")).unwrap();
        script.hook().die("the message loop failed");
        sup.died(1, "the message loop failed".into());
        assert_eq!(sup.recovery(), Some(Recovery::Exit));
        assert_eq!(sup.detail(), "the message loop failed");
        assert_eq!(sup.next_retry(), None);
    }

    #[test]
    fn unsupported_is_reported_and_never_retried() {
        let t0 = Instant::now();
        let (mut sup, script) = rig(vec![Err(Down::Error(Error::Unsupported))], Platform::Other);
        sup.set_retry_when_idle(true);
        sup.start(t0);
        assert_eq!(sup.recovery(), Some(Recovery::Report));
        assert_eq!(sup.next_retry(), None);
        tick(&mut sup, t0, 1, 10);
        assert_eq!(script.attempts(), 1);
    }

    // ---- registrations, observe and pause across restarts ----

    #[test]
    fn registrations_observe_and_pause_survive_a_restart_with_stable_ids() {
        let (mut sup, script) = rig(vec![Ok(()), Ok(())], Platform::MacOs);
        let t0 = Instant::now();
        sup.start(t0);
        let first = script.hook();
        let router = sup.router();
        let a = sup.register(g("Control+A")).unwrap();
        let b = sup.register(g("Control+B")).unwrap();
        let c = sup.register(g("Control+C")).unwrap();
        assert!(sup.unregister(b));
        assert!(!sup.unregister(b));
        sup.set_observe(true);
        sup.pause_handle().set_paused(true);
        assert!(first.observe.load(Ordering::SeqCst) && first.paused.load(Ordering::SeqCst));
        assert_eq!(first.gestures(), [g("Control+A"), g("Control+C")]);
        let hotkey = |id| HookEvent::Hotkey {
            id,
            repeat: false,
        };
        assert_eq!(
            router.route(1, hotkey(first.id_of(g("Control+C")))),
            Some(Routed::Hotkey {
                id: c,
                repeat: false
            })
        );

        first.die("the event tap kept timing out");
        sup.died(1, "the event tap kept timing out".into());
        assert_eq!(codes(sup.take_changes()), ["started", "died:hook_stopped"]);
        assert!(!sup.is_registered(a));
        // one registered while down waits too, and the dead hook's late events are dropped
        let d = sup.register(g("Control+D")).unwrap();
        assert_eq!(router.route(1, hotkey(1)), None);
        // the death came an interval after the start, so the retry is due at once
        sup.poll(t0 + RETRY_INTERVAL);
        assert_eq!(codes(sup.take_changes()), ["started"]);
        let second = script.hook();
        assert_eq!(second.gestures(), [g("Control+A"), g("Control+C"), g("Control+D")]);
        assert!(second.observe.load(Ordering::SeqCst) && second.paused.load(Ordering::SeqCst));
        assert!(sup.is_registered(a) && sup.is_registered(d));
        // the same stable ids on the new hook
        for (gesture, id) in [(g("Control+A"), a), (g("Control+C"), c), (g("Control+D"), d)] {
            assert_eq!(
                router.route(2, hotkey(second.id_of(gesture))),
                Some(Routed::Hotkey {
                    id,
                    repeat: false
                })
            );
        }
    }

    #[test]
    fn refusals_come_back_at_once_in_every_state() {
        let (mut sup, _script) = rig(vec![denied()], Platform::MacOs);
        sup.start(Instant::now());
        assert_eq!(sup.register(Gesture::new(Key::None, Modifiers::NONE)), Err(Error::NoKey));
        assert_eq!(sup.register(g("LeftShift")), Err(Error::ModifierKey));
        sup.register(g("Control+Shift+A")).unwrap();
        // compared parsed, as the hook compares
        assert_eq!(sup.register(g("Shift+Control+A")), Err(Error::Duplicate));
        if cfg!(target_os = "macos") {
            assert_eq!(sup.register(g("PrintScreen")), Err(Error::UnsupportedKey(Key::PrintScreen)));
        }
    }

    #[test]
    fn a_dead_hook_found_by_a_register_is_a_death_and_the_gesture_waits() {
        let (mut sup, script) = rig(vec![Ok(()), Ok(())], Platform::MacOs);
        let t0 = Instant::now();
        sup.start(t0);
        sup.register(g("F12")).unwrap();
        script
            .hook()
            .die("the event tap was invalidated");
        // before the forwarder reports the death
        let f11 = sup.register(g("F11")).unwrap();
        assert_eq!(sup.down(), Some(&Down::Error(Error::Stopped)));
        assert!(!sup.is_registered(f11));
        // its report afterwards is not a second death
        sup.died(1, "the event tap was invalidated".into());
        assert_eq!(codes(sup.take_changes()), ["started", "died:hook_stopped"]);
        sup.poll(t0 + RETRY_INTERVAL);
        assert_eq!(script.hook().gestures(), [g("F12"), g("F11")]);
    }

    #[test]
    fn a_revocation_is_a_missing_permission() {
        let (mut sup, script) = rig(vec![Ok(())], Platform::MacOs);
        sup.start(Instant::now());
        script.hook().die(REVOKED_REASON);
        sup.died(1, REVOKED_REASON.into());
        assert_eq!(sup.down(), Some(&Down::Error(Error::PermissionDenied)));
        assert_eq!(sup.detail(), REVOKED_REASON);
        assert_eq!(sup.recovery(), Some(Recovery::Retry));
    }

    #[test]
    fn recording_tokens_are_the_supervisors_and_die_with_their_hook() {
        let (mut sup, script) = rig(vec![Ok(()), Ok(())], Platform::MacOs);
        sup.set_retry_when_idle(true);
        let t0 = Instant::now();
        sup.start(t0);
        let router = sup.router();
        let first = script.hook();
        let one = sup.start_recording().unwrap();
        // arming again replaces the session: the first token can no longer report
        let two = sup.start_recording().unwrap();
        assert_ne!(one, two);
        let modifiers = |session| HookEvent::RecordingModifiers {
            session,
            modifiers: Modifiers::SHIFT,
        };
        assert_eq!(router.route(1, modifiers(1)), None);
        assert_eq!(
            router.route(1, modifiers(2)),
            Some(Routed::RecordingModifiers {
                session: two,
                modifiers: Modifiers::SHIFT
            })
        );
        sup.stop_recording(one);
        assert!(lock(&first.stopped_sessions).is_empty(), "a replaced token cancels nothing");

        first.die("the event tap was invalidated");
        sup.died(1, "the event tap was invalidated".into());
        assert_eq!(sup.start_recording(), None, "no recording while down");
        sup.poll(t0 + RETRY_INTERVAL);
        let second = script.hook();
        let three = sup.start_recording().unwrap();
        assert!(three != one && three != two);
        // the dead hook's token cancels nothing on the new one, whose own token is 1 again
        sup.stop_recording(two);
        assert!(lock(&second.stopped_sessions).is_empty());
        assert_eq!(
            router.route(
                2,
                HookEvent::Recorded {
                    session: 1,
                    gesture: g("F5")
                }
            ),
            Some(Routed::Recorded {
                session: three,
                gesture: g("F5")
            })
        );
        sup.stop_recording(three);
        assert!(lock(&second.stopped_sessions).is_empty(), "recorded already: nothing to cancel");
    }

    #[test]
    fn shutdown_hands_the_hook_back_and_ends_the_retries() {
        let (mut sup, _script) = rig(vec![denied()], Platform::MacOs);
        sup.set_retry_when_idle(true);
        sup.start(Instant::now());
        assert!(sup.next_retry().is_some());
        assert!(sup.shutdown().is_none());
        assert_eq!(sup.next_retry(), None);
    }

    // ---- Supervised: the threaded wrapper ----

    fn supervised(script: Vec<Result<(), Down>>) -> (Supervised<Fake>, Script) {
        let (sup, script) = rig(script, Platform::MacOs);
        (Supervised::with_supervisor(sup).unwrap(), script)
    }

    fn next(s: &Supervised<Fake>) -> SupervisedEvent {
        s.next_event(Duration::from_secs(5))
            .expect("an event")
    }

    fn running() -> SupervisedEvent {
        SupervisedEvent::Status(Status {
            down: None,
            recoverable: false,
            detail: String::new(),
        })
    }

    #[test]
    fn the_first_event_is_the_initial_status() {
        let (s, _script) = supervised(vec![denied()]);
        assert_eq!(
            next(&s),
            SupervisedEvent::Status(Status {
                down: Some(Down::Error(Error::PermissionDenied)),
                recoverable: true,
                detail: Error::PermissionDenied.to_string(),
            })
        );
        let (s, _script) = supervised(vec![Ok(())]);
        assert_eq!(next(&s), running());
    }

    #[test]
    fn events_are_forwarded_in_stable_terms() {
        let (s, script) = supervised(vec![Ok(())]);
        assert_eq!(next(&s), running());
        let hook = script.hook();
        let id = s.register(g("Control+A")).unwrap();
        s.set_observe(true);
        assert!(hook.observe.load(Ordering::SeqCst));
        hook.send(HookEvent::Hotkey {
            id: hook.id_of(g("Control+A")),
            repeat: false,
        });
        let key = KeyEvent {
            down: true,
            repeat: false,
            key: Key::A,
            modifiers: Modifiers::CONTROL,
            raw_code: 0,
            ch: Some('a'),
        };
        hook.send(HookEvent::Key(key));
        assert_eq!(
            next(&s),
            SupervisedEvent::Hotkey {
                id,
                repeat: false
            }
        );
        assert_eq!(next(&s), SupervisedEvent::Key(key));
    }

    #[test]
    fn a_death_is_reported_and_the_hook_comes_back_by_itself() {
        let (s, script) = supervised(vec![Ok(()), Ok(())]);
        assert_eq!(next(&s), running());
        let id = s.register(g("F12")).unwrap();
        let first = script.hook();
        first.die("the event tap kept timing out");
        assert_eq!(
            next(&s),
            SupervisedEvent::Status(Status {
                down: Some(Down::Error(Error::Stopped)),
                recoverable: true,
                detail: "the event tap kept timing out".into(),
            })
        );
        // retried within the interval, the registration on the new hook under its old id
        assert_eq!(next(&s), running());
        let second = script.hook();
        assert_eq!(second.gestures(), [g("F12")]);
        second.send(HookEvent::Hotkey {
            id: second.id_of(g("F12")),
            repeat: false,
        });
        assert_eq!(
            next(&s),
            SupervisedEvent::Hotkey {
                id,
                repeat: false
            }
        );
    }

    #[test]
    fn a_failed_retry_reports_only_a_change_of_reason() {
        let (s, script) = supervised(vec![
            denied(),
            denied(),
            Err(Down::Error(Error::HookFailed("no tap".into()))),
            Ok(()),
        ]);
        assert!(matches!(
            next(&s),
            SupervisedEvent::Status(Status {
                down: Some(Down::Error(Error::PermissionDenied)),
                ..
            })
        ));
        // the repeated denial is not reported again
        assert_eq!(
            next(&s),
            SupervisedEvent::Status(Status {
                down: Some(Down::Error(Error::HookFailed("no tap".into()))),
                recoverable: true,
                detail: "could not install the keyboard hook: no tap".into(),
            })
        );
        assert_eq!(next(&s), running());
        assert_eq!(script.attempts(), 4);
    }

    #[test]
    fn stop_drains_then_closes_and_is_safe_beside_next_event() {
        let (s, script) = supervised(vec![Ok(())]);
        let s = Arc::new(s);
        assert_eq!(next(&s), running());
        let hook = script.hook();
        let id = s.register(g("F12")).unwrap();
        hook.send(HookEvent::Hotkey {
            id: hook.id_of(g("F12")),
            repeat: true,
        });
        let waiter = thread::spawn({
            let s = Arc::clone(&s);
            move || {
                let started = Instant::now();
                let mut received = Vec::new();
                loop {
                    match s.next_event(Duration::from_secs(10)) {
                        Ok(event) => received.push(event),
                        Err(error) => return (received, error, started.elapsed()),
                    }
                }
            }
        });
        thread::sleep(Duration::from_millis(100));
        s.stop();
        s.stop();
        let (received, error, waited) = waiter.join().unwrap();
        assert_eq!(
            received,
            [SupervisedEvent::Hotkey {
                id,
                repeat: true
            }]
        );
        assert_eq!(error, RecvTimeoutError::Disconnected);
        assert!(waited < Duration::from_secs(5), "{waited:?}");
        assert!(s.register(g("F1")).is_ok(), "registering after stop is harmless");
        assert_eq!(s.start_recording(), None);
        assert_eq!(s.next_event(Duration::ZERO), Err(RecvTimeoutError::Disconnected));
    }

    #[test]
    fn a_supervisor_panic_stops_the_hook_for_good_and_ends_the_stream() {
        // the broken queue must not leave the hook swallowing its gestures with nobody draining it
        let (s, script) = supervised(vec![Ok(())]);
        assert_eq!(next(&s), running());
        let hook = script.hook();
        s.register(g("Control+A")).unwrap();
        hook.send(HookEvent::Hotkey {
            id: PANIC_ID,
            repeat: false,
        });

        let SupervisedEvent::Status(status) = next(&s) else {
            panic!("expected the final status");
        };
        let Some(Down::Error(Error::HookFailed(detail))) = &status.down else {
            panic!("expected HookFailed, got {status:?}");
        };
        assert!(
            detail.contains("the hotkey supervisor panicked: the fake hook's queue broke"),
            "{detail}"
        );
        assert_eq!(&status.detail, detail);
        assert!(!status.recoverable, "a panicked supervisor is never retried");
        assert_eq!(s.next_event(Duration::from_secs(5)), Err(RecvTimeoutError::Disconnected));

        // shut down: dropping the fake closes its queue, as dropping a real hook stops it
        assert!(lock(&hook.tx).is_none(), "the hook was not shut down");
        assert_eq!(script.attempts(), 1, "no hook start after the panic");
    }

    #[test]
    fn a_hook_that_is_down_when_stopped_stops_at_once() {
        let (s, _script) = supervised(vec![denied()]);
        let started = Instant::now();
        drop(s);
        assert!(started.elapsed() < Duration::from_secs(2));
    }
}
