//! The platform-independent hook decision logic. Each backend turns an OS event into
//! `(code, key, down, repeat, mods)`, calls `HookCore`, and swallows or passes on its verdict.
//! `HookCore` lives on the hook thread only and owns the only `Sender`.
//!
//! The callback rules apply here as in the backends: no allocation beyond the mpsc node, no I/O
//! or logging, no calls into user code. The table lock is held for an `Arc` clone only.

use std::sync::atomic::Ordering;
use std::sync::mpsc::Sender;
use std::sync::Arc;

use crate::hotkeys::{lock, Shared};
use crate::{Event, Gesture, HotkeyId, Key, KeyEvent, Modifiers};

/// Number of 64-bit words in the suppression bitset (1024 physical-key codes).
const WORDS: usize = 16;

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub(crate) enum Verdict {
    Pass,
    Swallow,
}

pub(crate) struct HookCore {
    shared: Arc<Shared>,
    tx: Sender<Event>,
    /// Keyed by the physical key code (see the backends), because one physical key's VK can differ
    /// between its down and its up.
    suppressed: [u64; WORDS],
    /// Code + id of the last press that fired, for its repeat events.
    fired: Option<(u16, HotkeyId)>,
}

impl HookCore {
    pub fn new(shared: Arc<Shared>, tx: Sender<Event>) -> Self {
        Self {
            shared,
            tx,
            suppressed: [0; WORDS],
            fired: None,
        }
    }

    /// Whether the observe stream is on (backends compute `ch` only when it is).
    pub fn observing(&self) -> bool {
        self.shared.observing.load(Ordering::Relaxed)
    }

    /// A modifier key edge (never swallowed). `mods` = side-agnostic modifiers AFTER this edge.
    pub fn modifier(&mut self, raw_code: u32, key: Key, down: bool, repeat: bool, mods: Modifiers) {
        if self.observing() {
            self.send(Event::Key(KeyEvent {
                down,
                repeat,
                key,
                modifiers: mods,
                raw_code,
                ch: None,
            }));
        }
        let session = self.shared.recording.load(Ordering::Acquire);
        if session != 0 && key.is_modifier() {
            self.send(Event::RecordingModifiers {
                session,
                modifiers: mods,
            });
        }
    }

    /// A non-modifier key edge. Returns the verdict; the backend swallows on `Swallow`.
    /// `raw_code`: `Some(VK / CGKeyCode)` to include the edge in the observe stream, `None` to keep
    /// it out (macOS NX media events, which have no key code). `ch`: the typed character (the
    /// backend computes it only when `observing() && down`; otherwise `None`).
    // The flat argument list is the backends' shared contract (spec §4.3); a struct would only
    // be built and torn apart again on every key event.
    #[allow(clippy::too_many_arguments)]
    pub fn key(
        &mut self,
        code: u16,
        raw_code: Option<u32>,
        ch: Option<char>,
        key: Key,
        down: bool,
        repeat: bool,
        mods: Modifiers,
    ) -> Verdict {
        if let (true, Some(raw_code)) = (self.observing(), raw_code) {
            self.send(Event::Key(KeyEvent {
                down,
                repeat,
                key,
                modifiers: mods,
                raw_code,
                ch,
            }));
        }
        let was = self.is_suppressed(code);

        // Never swallow an up whose down passed; never pass an up whose down was swallowed (even if
        // the gesture was unregistered, the hook paused, or a modifier released in between).
        if !down {
            self.set_suppressed(code, false);
            if matches!(self.fired, Some((fired, _)) if fired == code) {
                self.fired = None;
            }
            return if was { Verdict::Swallow } else { Verdict::Pass };
        }

        if repeat {
            if !was {
                return Verdict::Pass;
            }
            if let Some((fired, id)) = self.fired {
                if fired == code && !self.shared.paused.load(Ordering::Relaxed) {
                    self.send(Event::Hotkey {
                        id,
                        repeat: true,
                    });
                }
            }
            return Verdict::Swallow;
        }

        // A fresh down: a set bit here is stale, from a lost key-up.
        self.set_suppressed(code, false);
        if key != Key::None {
            let session = self
                .shared
                .recording
                .swap(0, Ordering::AcqRel);
            if session != 0 {
                self.send(Event::Recorded {
                    session,
                    gesture: Gesture::new(key, mods),
                });
                self.set_suppressed(code, true);
                return Verdict::Swallow;
            }
        }
        if key == Key::None || self.shared.paused.load(Ordering::Relaxed) {
            return Verdict::Pass;
        }
        let table = lock(&self.shared.table).clone();
        match table
            .iter()
            .find(|(gesture, _)| gesture.key == key && gesture.modifiers == mods)
        {
            Some(&(_, id)) => {
                self.set_suppressed(code, true);
                self.fired = Some((code, id));
                self.send(Event::Hotkey {
                    id,
                    repeat: false,
                });
                Verdict::Swallow
            }
            None => Verdict::Pass,
        }
    }

    /// Forget all suppressed bits and `fired` (macOS tap re-enabled after a timeout).
    #[cfg_attr(not(target_os = "macos"), allow(dead_code))]
    pub fn reset(&mut self) {
        self.suppressed = [0; WORDS];
        self.fired = None;
    }

    /// Push `Event::Stopped(reason)` and mark the hook dead.
    pub fn stopped(&mut self, reason: String) {
        self.shared
            .alive
            .store(false, Ordering::Release);
        self.send(Event::Stopped(reason));
    }

    /// The consumer may have dropped `Events`; that is fine.
    fn send(&self, event: Event) {
        let _ = self.tx.send(event);
    }

    pub(crate) fn is_suppressed(&self, code: u16) -> bool {
        let (word, bit) = Self::slot(code);
        self.suppressed[word] & bit != 0
    }

    fn set_suppressed(&mut self, code: u16, on: bool) {
        let (word, bit) = Self::slot(code);
        if on {
            self.suppressed[word] |= bit;
        } else {
            self.suppressed[word] &= !bit;
        }
    }

    fn slot(code: u16) -> (usize, u64) {
        let code = code as usize % (WORDS * 64);
        (code / 64, 1 << (code % 64))
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use std::sync::mpsc::{self, Receiver};

    use super::*;

    /// A core with a fresh `Shared` and the receiving end of its queue.
    pub(crate) fn harness() -> (HookCore, Arc<Shared>, Receiver<Event>) {
        let shared = Arc::new(Shared::new());
        let (tx, rx) = mpsc::channel();
        (HookCore::new(shared.clone(), tx), shared, rx)
    }

    pub(crate) fn drain(rx: &Receiver<Event>) -> Vec<Event> {
        rx.try_iter().collect()
    }

    const A: u16 = 0x1E;
    const B: u16 = 0x30;
    const CTRL: Modifiers = Modifiers::CONTROL;
    const NONE: Modifiers = Modifiers::NONE;

    fn down(core: &mut HookCore, code: u16, key: Key, mods: Modifiers) -> Verdict {
        core.key(code, Some(code as u32), None, key, true, false, mods)
    }

    fn repeat(core: &mut HookCore, code: u16, key: Key, mods: Modifiers) -> Verdict {
        core.key(code, Some(code as u32), None, key, true, true, mods)
    }

    fn up(core: &mut HookCore, code: u16, key: Key, mods: Modifiers) -> Verdict {
        core.key(code, Some(code as u32), None, key, false, false, mods)
    }

    fn hotkey(id: HotkeyId, repeat: bool) -> Event {
        Event::Hotkey {
            id,
            repeat,
        }
    }

    #[test]
    fn swallowed_down_means_swallowed_up() {
        let (mut core, shared, rx) = harness();
        let id = shared
            .register(Gesture::new(Key::A, CTRL))
            .unwrap();
        assert_eq!(down(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        assert_eq!(up(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        assert_eq!(drain(&rx), [hotkey(id, false)]);
        // Without the modifier nothing matches, and the up of a passed down passes.
        assert_eq!(down(&mut core, A, Key::A, NONE), Verdict::Pass);
        assert_eq!(up(&mut core, A, Key::A, NONE), Verdict::Pass);
        assert!(drain(&rx).is_empty());
    }

    #[test]
    fn modifier_released_before_the_key_still_swallows_the_up() {
        let (mut core, shared, rx) = harness();
        let id = shared
            .register(Gesture::new(Key::A, CTRL))
            .unwrap();
        assert_eq!(down(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        core.modifier(0xA2, Key::LeftCtrl, false, false, NONE);
        assert_eq!(up(&mut core, A, Key::A, NONE), Verdict::Swallow);
        assert_eq!(drain(&rx), [hotkey(id, false)]);
    }

    #[test]
    fn modifier_pressed_after_a_passed_down_passes_the_up() {
        let (mut core, shared, _rx) = harness();
        shared
            .register(Gesture::new(Key::A, CTRL))
            .unwrap();
        assert_eq!(down(&mut core, A, Key::A, NONE), Verdict::Pass);
        core.modifier(0xA2, Key::LeftCtrl, true, false, CTRL);
        assert_eq!(up(&mut core, A, Key::A, CTRL), Verdict::Pass);
    }

    #[test]
    fn repeats_follow_their_press() {
        let (mut core, shared, rx) = harness();
        let id = shared
            .register(Gesture::new(Key::A, CTRL))
            .unwrap();
        assert_eq!(down(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        assert_eq!(repeat(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        assert_eq!(up(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        assert_eq!(drain(&rx), [hotkey(id, false), hotkey(id, true)]);
        assert_eq!(down(&mut core, B, Key::B, CTRL), Verdict::Pass);
        assert_eq!(repeat(&mut core, B, Key::B, CTRL), Verdict::Pass);
        assert!(drain(&rx).is_empty());
    }

    #[test]
    fn pause_or_unregister_while_held_still_swallows_the_rest() {
        let (mut core, shared, rx) = harness();
        let id = shared
            .register(Gesture::new(Key::A, CTRL))
            .unwrap();
        assert_eq!(down(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        shared.paused.store(true, Ordering::Relaxed);
        assert_eq!(repeat(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        assert_eq!(up(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        assert_eq!(drain(&rx), [hotkey(id, false)]);
        // Paused: a fresh press neither fires nor swallows.
        assert_eq!(down(&mut core, A, Key::A, CTRL), Verdict::Pass);
        assert_eq!(up(&mut core, A, Key::A, CTRL), Verdict::Pass);
        shared.paused.store(false, Ordering::Relaxed);

        assert_eq!(down(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        assert!(shared.unregister(id));
        assert_eq!(up(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        assert_eq!(drain(&rx), [hotkey(id, false)]);
        assert_eq!(down(&mut core, A, Key::A, CTRL), Verdict::Pass);
    }

    #[test]
    fn recording_consumes_one_press_then_matching_resumes() {
        let (mut core, shared, rx) = harness();
        let id = shared
            .register(Gesture::new(Key::A, CTRL))
            .unwrap();
        let session = shared.start_recording();
        assert_eq!(down(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        assert_eq!(up(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        assert_eq!(
            drain(&rx),
            [Event::Recorded {
                session,
                gesture: Gesture::new(Key::A, CTRL)
            }]
        );
        assert_eq!(down(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        assert_eq!(drain(&rx), [hotkey(id, false)]);
    }

    #[test]
    fn recording_skips_unmapped_keys_and_honors_stop() {
        let (mut core, shared, rx) = harness();
        let session = shared.start_recording();
        assert_eq!(down(&mut core, 0x7F, Key::None, NONE), Verdict::Pass);
        assert!(drain(&rx).is_empty());
        shared.stop_recording(session);
        assert_eq!(down(&mut core, A, Key::A, NONE), Verdict::Pass);
        assert!(drain(&rx).is_empty());

        // stop_recording(old) does not cancel a newer session.
        let old = shared.start_recording();
        let new = shared.start_recording();
        shared.stop_recording(old);
        assert_eq!(down(&mut core, B, Key::B, NONE), Verdict::Swallow);
        assert_eq!(
            drain(&rx),
            [Event::Recorded {
                session: new,
                gesture: Gesture::new(Key::B, NONE)
            }]
        );
    }

    #[test]
    fn recording_modifiers_carry_the_session_for_modifier_keys_only() {
        let (mut core, shared, rx) = harness();
        core.modifier(0xA2, Key::LeftCtrl, true, false, CTRL);
        assert!(drain(&rx).is_empty());
        let session = shared.start_recording();
        core.modifier(0xA2, Key::LeftCtrl, true, false, CTRL);
        core.modifier(0xA2, Key::LeftCtrl, true, true, CTRL);
        core.modifier(0x14, Key::CapsLock, true, false, CTRL);
        core.modifier(0xA2, Key::LeftCtrl, false, false, NONE);
        assert_eq!(
            drain(&rx),
            [
                Event::RecordingModifiers {
                    session,
                    modifiers: CTRL
                },
                Event::RecordingModifiers {
                    session,
                    modifiers: CTRL
                },
                Event::RecordingModifiers {
                    session,
                    modifiers: NONE
                }
            ]
        );
    }

    #[test]
    fn observe_carries_ch_and_skips_codeless_edges() {
        let (mut core, shared, rx) = harness();
        assert_eq!(core.key(A, Some(0x41), Some('a'), Key::A, true, false, NONE), Verdict::Pass);
        assert!(drain(&rx).is_empty());
        shared
            .observing
            .store(true, Ordering::Relaxed);
        assert!(core.observing());
        core.key(A, Some(0x41), Some('a'), Key::A, true, false, NONE);
        core.key(A, Some(0x41), None, Key::A, false, false, NONE);
        core.key(0x110, None, None, Key::MediaPlayPause, true, false, NONE);
        core.modifier(0xA0, Key::LeftShift, true, false, Modifiers::SHIFT);
        let key = |down, key, modifiers, raw_code, ch| {
            Event::Key(KeyEvent {
                down,
                repeat: false,
                key,
                modifiers,
                raw_code,
                ch,
            })
        };
        assert_eq!(
            drain(&rx),
            [
                key(true, Key::A, NONE, 0x41, Some('a')),
                key(false, Key::A, NONE, 0x41, None),
                key(true, Key::LeftShift, Modifiers::SHIFT, 0xA0, None)
            ]
        );
    }

    #[test]
    fn reset_forgets_suppression() {
        let (mut core, shared, rx) = harness();
        let id = shared
            .register(Gesture::new(Key::A, CTRL))
            .unwrap();
        assert_eq!(down(&mut core, A, Key::A, CTRL), Verdict::Swallow);
        core.reset();
        assert!(!core.is_suppressed(A));
        assert_eq!(repeat(&mut core, A, Key::A, CTRL), Verdict::Pass);
        assert_eq!(up(&mut core, A, Key::A, CTRL), Verdict::Pass);
        assert_eq!(drain(&rx), [hotkey(id, false)]);
    }

    #[test]
    fn stopped_marks_dead_and_is_reported() {
        let (mut core, shared, rx) = harness();
        core.stopped("tap invalidated".into());
        assert!(!shared.alive.load(Ordering::Acquire));
        assert_eq!(drain(&rx), [Event::Stopped("tap invalidated".into())]);
    }
}
