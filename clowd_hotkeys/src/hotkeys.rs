use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::Duration;

#[cfg(target_os = "macos")]
use crate::macos as backend;
#[cfg(not(any(windows, target_os = "macos")))]
use crate::unsupported as backend;
#[cfg(windows)]
use crate::windows as backend;
use crate::{Error, Gesture, Key, Modifiers};

/// A registration, as returned by `Hotkeys::register`.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash)]
pub struct HotkeyId(pub(crate) u32);

impl HotkeyId {
    /// Ids start at 1 and are never reused within one `Hotkeys`.
    pub const fn get(self) -> u32 {
        self.0
    }
}

/// What the hook reports, in order, on `Events`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Event {
    /// A registered gesture was pressed (and swallowed). `repeat` = an auto-repeat of a press that fired.
    Hotkey { id: HotkeyId, repeat: bool },
    /// While recording session `session`: the side-agnostic modifiers held changed (every modifier edge emits one).
    RecordingModifiers { session: u32, modifiers: Modifiers },
    /// Recording session `session` finished: the first non-modifier key-down (swallowed, with its key-up) and the
    /// modifiers held. Escape and system combos are NOT special here — the consumer decides.
    Recorded { session: u32, gesture: Gesture },
    /// Observe stream (only while `set_observe(true)`): every key edge, nothing swallowed by observe itself.
    Key(KeyEvent),
    /// The hook died unexpectedly (macOS tap invalidated, Windows message loop error). Last event; the queue then closes.
    Stopped(String),
}

/// One key edge on the observe stream.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub struct KeyEvent {
    pub down: bool,
    pub repeat: bool,
    /// `Key::None` when the code has no `Key`.
    pub key: Key,
    /// Side-agnostic, as tracked by the hook at this event.
    pub modifiers: Modifiers,
    /// Windows VK / macOS CGKeyCode — passed through, never mapped.
    pub raw_code: u32,
    /// Key-down only: the character it types under the foreground layout, computed in the hook at
    /// the edge. `None` for up edges, modifiers, dead or non-typing keys and control characters.
    pub ch: Option<char>,
}

/// State shared by the API side and the hook thread.
pub(crate) struct Shared {
    pub paused: AtomicBool,
    /// The armed session token, 0 = not recording. The hook only ever swaps it to 0; it never
    /// writes a nonzero value, so a concurrent `stop_recording` can never be undone.
    pub recording: AtomicU32,
    /// Starts at 1 (0 is skipped on wrap).
    pub next_session: AtomicU32,
    pub observing: AtomicBool,
    /// Writers lock, build a new `Arc`, swap, unlock. The hook locks only to clone the `Arc`.
    pub table: Mutex<Arc<[(Gesture, HotkeyId)]>>,
    /// Starts at 1.
    pub next_id: AtomicU32,
    /// False once the hook thread ended.
    pub alive: AtomicBool,
}

impl Shared {
    pub fn new() -> Self {
        Self {
            paused: AtomicBool::new(false),
            recording: AtomicU32::new(0),
            next_session: AtomicU32::new(1),
            observing: AtomicBool::new(false),
            table: Mutex::new(Arc::from([])),
            next_id: AtomicU32::new(1),
            alive: AtomicBool::new(true),
        }
    }

    pub fn register(&self, gesture: Gesture) -> Result<HotkeyId, Error> {
        gesture.validate()?;
        if !self.alive.load(Ordering::Acquire) {
            return Err(Error::Stopped);
        }
        let mut table = lock(&self.table);
        if table
            .iter()
            .any(|(registered, _)| *registered == gesture)
        {
            return Err(Error::Duplicate);
        }
        let id = HotkeyId(self.next_id.fetch_add(1, Ordering::Relaxed));
        *table = table
            .iter()
            .copied()
            .chain(std::iter::once((gesture, id)))
            .collect();
        Ok(id)
    }

    pub fn unregister(&self, id: HotkeyId) -> bool {
        let mut table = lock(&self.table);
        if !table
            .iter()
            .any(|(_, registered)| *registered == id)
        {
            return false;
        }
        *table = table
            .iter()
            .copied()
            .filter(|(_, registered)| *registered != id)
            .collect();
        true
    }

    pub fn start_recording(&self) -> u32 {
        let mut session = self
            .next_session
            .fetch_add(1, Ordering::Relaxed);
        if session == 0 {
            session = self
                .next_session
                .fetch_add(1, Ordering::Relaxed);
        }
        self.recording
            .store(session, Ordering::Release);
        session
    }

    pub fn stop_recording(&self, session: u32) {
        if session != 0 {
            let _ = self
                .recording
                .compare_exchange(session, 0, Ordering::AcqRel, Ordering::Relaxed);
        }
    }
}

/// `lock()` that shrugs off poisoning: every critical section here leaves the data consistent.
pub(crate) fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
}

/// A running keyboard hook. Dropping it stops the hook thread and joins it.
pub struct Hotkeys {
    pub(crate) shared: Arc<Shared>,
    // Dropped (stopped and joined) with the Hotkeys; it holds the only Sender on its thread.
    _backend: backend::Backend,
}

/// The hook's event queue. When the hook ends it drains what is queued, then reports closed.
pub struct Events {
    rx: mpsc::Receiver<Event>,
}

impl Hotkeys {
    /// Installs the hook on a new thread and blocks until it is installed (or failed).
    /// Linux/other: `Err(Error::Unsupported)`. macOS without Accessibility: `Err(PermissionDenied)`.
    pub fn start() -> Result<(Hotkeys, Events), Error> {
        let shared = Arc::new(Shared::new());
        let (tx, rx) = mpsc::channel();
        let backend = backend::spawn(shared.clone(), tx)?;
        Ok((
            Hotkeys {
                shared,
                _backend: backend,
            },
            Events {
                rx,
            },
        ))
    }

    /// Validates (`Gesture::validate`), returns `Err(Stopped)` if the hook died, and refuses an
    /// equal gesture already registered (`Duplicate`). Effective for the very next key event.
    pub fn register(&self, gesture: Gesture) -> Result<HotkeyId, Error> {
        self.shared.register(gesture)
    }

    /// Returns false for an unknown id. A press racing the unregister may still be reported once.
    pub fn unregister(&self, id: HotkeyId) -> bool {
        self.shared.unregister(id)
    }

    /// Paused: registered gestures neither fire nor swallow. Recording and observe are unaffected.
    /// (Used by the standby capturer; not exported over the C ABI.)
    pub fn set_paused(&self, paused: bool) {
        self.shared
            .paused
            .store(paused, Ordering::Relaxed);
    }

    /// Turns the `Event::Key` observe stream on or off.
    pub fn set_observe(&self, observe: bool) {
        self.shared
            .observing
            .store(observe, Ordering::Relaxed);
    }

    /// Arms a new recording session and returns its token (nonzero, never reused within a Hotkeys). The
    /// next non-modifier key-down is swallowed and reported as `Event::Recorded { session }`. While armed,
    /// registered gestures do not fire (recording wins over matching). Arming again replaces the session.
    /// Modifiers already held when arming are NOT reported until their next edge (the recorded gesture
    /// itself still includes them).
    pub fn start_recording(&self) -> u32 {
        self.shared.start_recording()
    }

    /// Cancels `session` if it is still the armed one; otherwise a no-op. A `Recorded` already
    /// queued is still delivered — the token lets the consumer drop it.
    pub fn stop_recording(&self, session: u32) {
        self.shared.stop_recording(session)
    }
}

impl Events {
    /// The next event, blocking. `None` once the hook is gone and the queue is drained.
    pub fn recv(&self) -> Option<Event> {
        self.rx.recv().ok()
    }

    /// The next event, waiting at most `timeout`.
    pub fn recv_timeout(&self, timeout: Duration) -> Result<Event, RecvTimeoutError> {
        self.rx.recv_timeout(timeout)
    }

    #[cfg(test)]
    pub(crate) fn from_receiver(rx: mpsc::Receiver<Event>) -> Self {
        Self {
            rx,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn assert_send_sync<T: Send + Sync>() {}
    fn assert_send<T: Send>() {}

    #[test]
    fn thread_safety_contract() {
        assert_send_sync::<Hotkeys>();
        assert_send::<Events>();
    }

    #[test]
    fn register_validates_refuses_duplicates_and_stopped() {
        let shared = Shared::new();
        let a = Gesture::new(Key::A, Modifiers::CONTROL);
        let first = shared.register(a).unwrap();
        assert_eq!(first.get(), 1);
        assert_eq!(shared.register(a), Err(Error::Duplicate));
        assert_eq!(shared.register(Gesture::new(Key::None, Modifiers::NONE)), Err(Error::NoKey));
        let second = shared
            .register(Gesture::new(Key::A, Modifiers::ALT))
            .unwrap();
        assert_eq!(second.get(), 2);
        assert!(shared.unregister(first));
        assert!(!shared.unregister(first));
        // Ids are never reused.
        assert_eq!(shared.register(a).unwrap().get(), 3);
        shared.alive.store(false, Ordering::Release);
        assert_eq!(shared.register(Gesture::new(Key::B, Modifiers::NONE)), Err(Error::Stopped));
        assert_eq!(
            shared.register(Gesture::new(Key::LeftCtrl, Modifiers::NONE)),
            Err(Error::ModifierKey)
        );
    }

    #[test]
    fn recording_tokens_are_nonzero_and_stop_only_cancels_its_own() {
        let shared = Shared::new();
        let first = shared.start_recording();
        let second = shared.start_recording();
        assert!(first != 0 && second != 0 && first != second);
        shared.stop_recording(first);
        assert_eq!(shared.recording.load(Ordering::Acquire), second);
        shared.stop_recording(second);
        assert_eq!(shared.recording.load(Ordering::Acquire), 0);
        shared
            .next_session
            .store(u32::MAX, Ordering::Relaxed);
        assert_eq!(shared.start_recording(), u32::MAX);
        assert_eq!(shared.start_recording(), 1);
    }

    #[test]
    fn events_report_closed_after_the_sender_goes() {
        let (tx, rx) = mpsc::channel();
        let events = Events::from_receiver(rx);
        tx.send(Event::Stopped("gone".into()))
            .unwrap();
        drop(tx);
        assert_eq!(events.recv(), Some(Event::Stopped("gone".into())));
        assert_eq!(events.recv(), None);
        assert_eq!(events.recv_timeout(Duration::from_millis(1)), Err(RecvTimeoutError::Disconnected));
    }
}
