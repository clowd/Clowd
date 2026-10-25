//! The Windows per-event state machine, kept free of Win32 calls so it compiles and is tested on
//! every OS. `windows.rs::ll_proc` only extracts the `KBDLLHOOKSTRUCT` fields, supplies the two
//! OS queries as closures, and acts on the verdict.

use crate::core::{HookCore, Verdict};
use crate::{Key, Modifiers};

/// `dwExtraInfo` of the mask key we inject ourselves; such events pass untouched.
pub(crate) const MASK_MARKER: usize = 0x434C_484B;
/// The unassigned VK injected after a swallowed Win/Alt combo, so the key-up of Win does not
/// open Start and the key-up of Alt does not focus the menu bar.
pub(crate) const MASK_VK: u16 = 0xE8;

const LLKHF_EXTENDED: u32 = 0x01;
const LLKHF_UP: u32 = 0x80;
/// Set in the scan code of key events Windows synthesizes itself: the phantom AltGr LCtrl (0x21D) and
/// the fake shifts around NumLock-on numpad keys (0x22A / 0x236). Real Right Shift (0x36) is
/// `LLKHF_EXTENDED` (KBDEXT in every layout table), so that flag can never identify a fake.
const SCANCODE_SIMULATED: u32 = 0x200;

const VK_LSHIFT: u32 = 0xA0;
const VK_RSHIFT: u32 = 0xA1;

const LCTRL: u8 = 0x01;
const RCTRL: u8 = 0x02;
const LALT: u8 = 0x04;
const RALT: u8 = 0x08;
const LSHIFT: u8 = 0x10;
const RSHIFT: u8 = 0x20;
const LWIN: u8 = 0x40;
const RWIN: u8 = 0x80;

/// The eight side-specific modifier VKs and their bits, for the async-state reconcile.
pub(crate) const SIDE_KEYS: [(u16, u8); 8] = [
    (0xA2, LCTRL),
    (0xA3, RCTRL),
    (0xA4, LALT),
    (0xA5, RALT),
    (0xA0, LSHIFT),
    (0xA1, RSHIFT),
    (0x5B, LWIN),
    (0x5C, RWIN),
];

/// A down of the key still held from the last down is a repeat, however slow (FilterKeys/RepeatKeys
/// stretch the delay and interval to ~2 s each at their UI maximum). Past this cap since that down
/// it counts as fresh instead: its key-up was probably lost (secure desktop, lock, sleep).
const REPEAT_STALE_MS: u32 = 5000;

const VK_SNAPSHOT: u32 = 0x2C;
/// PrintScreen's scan codes: plain (0x37 extended) and as SysRq with Alt held (0x54, not extended).
const SC_PRINT_SCREEN: u16 = 0x137;
const SC_SYSRQ: u32 = 0x54;

/// The fields of one `KBDLLHOOKSTRUCT`.
#[derive(Copy, Clone, Debug)]
pub(crate) struct WinEvent {
    pub vk: u32,
    pub scan: u32,
    pub flags: u32,
    pub time: u32,
    pub extra: usize,
}

pub(crate) struct WinState {
    /// The eight side bits held, as tracked from the hook's own edges.
    mods: u8,
    /// Code and time of the last non-modifier down while that key is still held, for repeat
    /// detection. One slot is enough: Windows only auto-repeats the most recently pressed key.
    last_down: Option<(u16, u32)>,
    /// A mask key was injected for the current Win/Alt hold.
    mask_sent: bool,
    /// Shift side bits a fake shift-up released while the real key stays held. GetAsyncKeyState then
    /// reads them up, so the reconcile must not take them for lost key-ups.
    fake_up: u8,
}

impl WinState {
    pub fn new() -> Self {
        Self {
            mods: 0,
            last_down: None,
            mask_sent: false,
            fake_up: 0,
        }
    }

    /// Returns `(verdict, inject_mask)`. `async_mods` = the 8 side bits from GetAsyncKeyState, the
    /// logical async state (injected edges count) of every earlier edge (called only before a fresh
    /// down); `ch` = the typed character (called only when `core.observing()` and this is a
    /// non-modifier down). Both are closures so tests can inject them.
    pub fn on_event(
        &mut self,
        core: &mut HookCore,
        ev: &WinEvent,
        async_mods: impl FnOnce() -> u8,
        ch: impl FnOnce() -> Option<char>,
    ) -> (Verdict, bool) {
        // Our own mask key and VK_PACKET / unassigned codes pass untouched: no tracking, no observe,
        // no matching.
        if ev.extra == MASK_MARKER || matches!(ev.vk, 0 | 0xE7 | 0xFF) {
            return (Verdict::Pass, false);
        }
        let down = ev.flags & LLKHF_UP == 0;
        // So do the fake shifts Windows wraps around NumLock-on numpad keys, except that a fake up of
        // a held Shift is remembered so the reconcile keeps it held.
        if matches!(ev.vk, VK_LSHIFT | VK_RSHIFT) && ev.scan & SCANCODE_SIMULATED != 0 {
            let bit = if ev.vk == VK_LSHIFT { LSHIFT } else { RSHIFT };
            self.fake_up = if down {
                self.fake_up & !bit
            } else {
                self.fake_up | (bit & self.mods)
            };
            return (Verdict::Pass, false);
        }
        let extended = ev.flags & LLKHF_EXTENDED != 0;
        let vk = (ev.vk & 0xFF) as u16;
        let key = Key::from_vk(vk).unwrap_or(Key::None);

        // The phantom AltGr LCtrl (scan 0x21D) is an ordinary LeftCtrl: AltGr = Ctrl+Alt.
        if let Some(bit) = side_bit(ev.vk) {
            // Windows auto-repeats held modifiers.
            let repeat = down && self.mods & bit != 0;
            if down && !repeat {
                // The clear-only reconcile, so the modifiers reported for this edge (and the
                // recording preview) drop a lost key-up now rather than at the next key.
                self.set_mods(self.mods & (async_mods() | self.fake_up));
            }
            self.set_mods(if down { self.mods | bit } else { self.mods & !bit });
            core.modifier(ev.vk, key, down, repeat, collapse(self.mods));
            return (Verdict::Pass, false);
        }

        // Physical key: scan code + extended bit (0..512), or 512 + VK for injected events with scan 0.
        // SysRq is PrintScreen's code: Alt can change between its down and its up.
        let code = if ev.vk == VK_SNAPSHOT && ev.scan == SC_SYSRQ {
            SC_PRINT_SCREEN
        } else if ev.scan != 0 {
            (ev.scan & 0xFF) as u16 | (extended as u16) << 8
        } else {
            512 + vk
        };
        let repeat = down && matches!(self.last_down, Some((last, time)) if last == code && ev.time.wrapping_sub(time) <= REPEAT_STALE_MS);
        if down {
            self.last_down = Some((code, ev.time));
        } else if matches!(self.last_down, Some((last, _)) if last == code) {
            self.last_down = None;
        }
        if down && !repeat {
            // Clear-only reconcile: recovers modifier key-ups lost to the secure desktop, lock or sleep.
            self.set_mods(self.mods & (async_mods() | self.fake_up));
        }
        let ch = if core.observing() && down { ch() } else { None };
        let verdict = core.key(code, Some(ev.vk), ch, key, down, repeat, collapse(self.mods));
        let inject = verdict == Verdict::Swallow && down && self.mods & (LWIN | RWIN | LALT | RALT) != 0 && !self.mask_sent;
        if inject {
            self.mask_sent = true;
        }
        (verdict, inject)
    }

    /// Every update path goes through here, so a Win key-up lost on the secure desktop (Win+L)
    /// cannot leave `mask_sent` stuck and let a later Win+X open Start.
    fn set_mods(&mut self, mods: u8) {
        self.mods = mods;
        self.fake_up &= mods;
        if mods & (LWIN | RWIN | LALT | RALT) == 0 {
            self.mask_sent = false;
        }
    }
}

/// The side bit of a modifier VK; the generic VK_SHIFT/VK_CONTROL/VK_MENU count as Left.
fn side_bit(vk: u32) -> Option<u8> {
    match vk {
        0xA2 | 0x11 => Some(LCTRL),
        0xA3 => Some(RCTRL),
        0xA4 | 0x12 => Some(LALT),
        0xA5 => Some(RALT),
        0xA0 | 0x10 => Some(LSHIFT),
        0xA1 => Some(RSHIFT),
        0x5B => Some(LWIN),
        0x5C => Some(RWIN),
        _ => None,
    }
}

fn collapse(mods: u8) -> Modifiers {
    let mut out = Modifiers::NONE;
    if mods & (LCTRL | RCTRL) != 0 {
        out |= Modifiers::CONTROL;
    }
    if mods & (LALT | RALT) != 0 {
        out |= Modifiers::ALT;
    }
    if mods & (LSHIFT | RSHIFT) != 0 {
        out |= Modifiers::SHIFT;
    }
    if mods & (LWIN | RWIN) != 0 {
        out |= Modifiers::META;
    }
    out
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::Ordering;
    use std::sync::Arc;

    use super::*;
    use crate::core::tests::{drain, harness};
    use crate::hotkeys::Shared;
    use crate::{Event, Gesture, KeyEvent};

    const LLKHF_INJECTED: u32 = 0x10;

    struct Rig {
        state: WinState,
        core: HookCore,
        shared: Arc<Shared>,
        rx: std::sync::mpsc::Receiver<Event>,
        time: u32,
    }

    impl Rig {
        fn new() -> Self {
            let (core, shared, rx) = harness();
            Self {
                state: WinState::new(),
                core,
                shared,
                rx,
                time: 10_000,
            }
        }

        fn register(&self, key: Key, modifiers: Modifiers) {
            self.shared
                .register(Gesture::new(key, modifiers))
                .unwrap();
        }

        /// One event `ms` after the previous; `async_mods` answers the reconcile query (the async
        /// state of every earlier edge, never this one).
        fn send(&mut self, vk: u32, scan: u32, flags: u32, ms: u32, async_mods: u8) -> (Verdict, bool) {
            self.time += ms;
            let ev = WinEvent {
                vk,
                scan,
                flags,
                time: self.time,
                extra: 0,
            };
            self.state
                .on_event(&mut self.core, &ev, || async_mods, || None)
        }

        fn down(&mut self, vk: u32, scan: u32, async_mods: u8) -> (Verdict, bool) {
            self.send(vk, scan, 0, 2000, async_mods)
        }

        fn up(&mut self, vk: u32, scan: u32) -> (Verdict, bool) {
            self.send(vk, scan, LLKHF_UP, 50, 0)
        }
    }

    const SWALLOW_INJECT: (Verdict, bool) = (Verdict::Swallow, true);
    const SWALLOW: (Verdict, bool) = (Verdict::Swallow, false);
    const PASS: (Verdict, bool) = (Verdict::Pass, false);

    #[test]
    fn win_x_swallows_and_masks_once_per_hold() {
        let mut rig = Rig::new();
        rig.register(Key::X, Modifiers::META);
        assert_eq!(rig.down(0x5B, 0x5B, 0), PASS);
        assert_eq!(rig.down(0x58, 0x2D, LWIN), SWALLOW_INJECT);
        assert_eq!(rig.up(0x58, 0x2D), SWALLOW);
        assert_eq!(rig.down(0x58, 0x2D, LWIN), SWALLOW);
        assert_eq!(rig.up(0x58, 0x2D), SWALLOW);
        assert_eq!(rig.up(0x5B, 0x5B), PASS);
        // A new hold masks again.
        assert_eq!(rig.down(0x5B, 0x5B, 0), PASS);
        assert_eq!(rig.down(0x58, 0x2D, LWIN), SWALLOW_INJECT);
    }

    #[test]
    fn lost_win_up_is_reconciled_and_masking_rearms() {
        let mut rig = Rig::new();
        rig.register(Key::X, Modifiers::META);
        rig.register(Key::X, Modifiers::META | Modifiers::SHIFT);
        assert_eq!(rig.down(0x5B, 0x5B, 0), PASS);
        assert_eq!(rig.down(0x58, 0x2D, LWIN), SWALLOW_INJECT);
        assert_eq!(rig.up(0x58, 0x2D), SWALLOW);
        // Win+L: the Win key-up never reaches the hook. The next fresh down finds it up in the async state.
        assert_eq!(rig.down(0x41, 0x1E, 0), PASS);
        assert_eq!(rig.up(0x41, 0x1E), PASS);
        assert_eq!(rig.down(0x5B, 0x5B, 0), PASS);
        assert_eq!(rig.down(0xA0, 0x2A, LWIN), PASS);
        assert_eq!(rig.down(0x58, 0x2D, LWIN | LSHIFT), SWALLOW_INJECT);
    }

    #[test]
    fn numlock_shift_numpad_up_matches_its_down_by_scan_code() {
        let mut rig = Rig::new();
        rig.register(Key::Home, Modifiers::SHIFT);
        rig.shared
            .observing
            .store(true, Ordering::Relaxed);
        assert_eq!(rig.down(0xA0, 0x2A, 0), PASS);
        drain(&rig.rx);
        // Windows wraps the numpad key in fake shift edges (simulated scan 0x22A, not extended). The
        // fake up has already released Shift in the async key state when the key goes down.
        assert_eq!(rig.send(0xA0, 0x22A, LLKHF_UP, 1, 0), PASS);
        assert_eq!(rig.down(0x24, 0x47, 0), SWALLOW);
        assert_eq!(rig.send(0xA0, 0x22A, 0, 1, 0), PASS);
        // The real Shift goes up first, then the key comes up as VK_NUMPAD7 with the same scan code.
        assert_eq!(rig.up(0xA0, 0x2A), PASS);
        assert_eq!(rig.up(0x67, 0x47), SWALLOW);
        let events = drain(&rig.rx);
        let shown: Vec<(u32, bool, Modifiers)> = events
            .iter()
            .filter_map(|e| match e {
                Event::Key(k) => Some((k.raw_code, k.down, k.modifiers)),
                _ => None,
            })
            .collect();
        // No fake-shift edges in observe, and they never dropped Shift from the tracked modifiers.
        assert_eq!(
            shown,
            [
                (0x24, true, Modifiers::SHIFT),
                (0xA0, false, Modifiers::NONE),
                (0x67, false, Modifiers::NONE)
            ]
        );
    }

    #[test]
    fn numlock_shift_repress_with_the_real_scan_code_is_a_shift_repeat() {
        // Real hardware sends the Shift re-press after the numpad key with the normal scan 0x2A
        // (not the simulated 0x22A) if no other keys were pushed. Both orders: the re-press before
        // the real Shift-up, and after the numpad key-up.
        for repress_after_key_up in [false, true] {
            let mut rig = Rig::new();
            rig.register(Key::Home, Modifiers::SHIFT);
            rig.register(Key::A, Modifiers::SHIFT);
            rig.shared
                .observing
                .store(true, Ordering::Relaxed);
            assert_eq!(rig.down(0xA0, 0x2A, 0), PASS);
            assert_eq!(rig.send(0xA0, 0x22A, LLKHF_UP, 1, 0), PASS);
            assert_eq!(rig.down(0x24, 0x47, 0), SWALLOW);
            drain(&rig.rx);
            if repress_after_key_up {
                assert_eq!(rig.up(0x24, 0x47), SWALLOW);
                assert_eq!(rig.send(0xA0, 0x2A, 0, 1, 0), PASS);
                // Shift is still tracked held: Shift+A fires.
                assert_eq!(rig.down(0x41, 0x1E, LSHIFT), SWALLOW);
                assert_eq!(rig.up(0x41, 0x1E), SWALLOW);
                assert_eq!(rig.up(0xA0, 0x2A), PASS);
            } else {
                assert_eq!(rig.send(0xA0, 0x2A, 0, 1, 0), PASS);
                assert_eq!(rig.up(0xA0, 0x2A), PASS);
                assert_eq!(rig.up(0x67, 0x47), SWALLOW);
            }
            let shift: Vec<(bool, bool, Modifiers)> = drain(&rig.rx)
                .iter()
                .filter_map(|e| match e {
                    Event::Key(k) if k.raw_code == 0xA0 => Some((k.down, k.repeat, k.modifiers)),
                    Event::Hotkey {
                        ..
                    } if !repress_after_key_up => panic!("spurious hotkey"),
                    _ => None,
                })
                .collect();
            assert_eq!(shift, [(true, true, Modifiers::SHIFT), (false, false, Modifiers::NONE)]);
        }
    }

    #[test]
    fn alt_print_screen_is_one_key_whichever_scan_code_each_edge_carries() {
        // Plain PrintScreen is scan 0x37 extended; with Alt held the keyboard sends SysRq instead:
        // the same VK, scan 0x54, not extended. One press can go down as one and up as the other.
        let mut rig = Rig::new();
        rig.register(Key::PrintScreen, Modifiers::ALT);
        assert_eq!(rig.down(0xA4, 0x38, 0), PASS);
        assert_eq!(rig.down(0x2C, 0x54, LALT), SWALLOW_INJECT);
        assert_eq!(rig.up(0xA4, 0x38), PASS);
        assert_eq!(rig.send(0x2C, 0x37, LLKHF_EXTENDED | LLKHF_UP, 50, 0), SWALLOW);
        assert!(!rig.core.is_suppressed(0x054) && !rig.core.is_suppressed(0x137));

        // The reverse: a plain PrintScreen press with Alt pressed before its release.
        rig.register(Key::PrintScreen, Modifiers::NONE);
        assert_eq!(rig.send(0x2C, 0x37, LLKHF_EXTENDED, 2000, 0), SWALLOW);
        assert_eq!(rig.down(0xA4, 0x38, 0), PASS);
        assert_eq!(rig.up(0x2C, 0x54), SWALLOW);
        assert_eq!(rig.up(0xA4, 0x38), PASS);
        assert!(!rig.core.is_suppressed(0x054) && !rig.core.is_suppressed(0x137));

        // Plain PrintScreen alone.
        assert_eq!(rig.send(0x2C, 0x37, LLKHF_EXTENDED, 2000, 0), SWALLOW);
        assert_eq!(rig.send(0x2C, 0x37, LLKHF_EXTENDED | LLKHF_UP, 50, 0), SWALLOW);
        assert!(!rig.core.is_suppressed(0x137));
    }

    #[test]
    fn control_pause_arrives_as_cancel() {
        // Documented behaviour, not a bug: with Ctrl held, Pause reaches the hook as VK_CANCEL (0x03,
        // scan 0x46 extended), so Ctrl+Pause records as `Control+Cancel` and a registered
        // `Control+Pause` never fires.
        assert_eq!(Key::from_vk(0x03), Some(Key::Cancel));
        let mut rig = Rig::new();
        rig.register(Key::Pause, Modifiers::CONTROL);
        assert_eq!(rig.down(0xA2, 0x1D, 0), PASS);
        assert_eq!(rig.send(0x03, 0x46, LLKHF_EXTENDED, 1, LCTRL), PASS);
        assert_eq!(rig.send(0x03, 0x46, LLKHF_EXTENDED | LLKHF_UP, 1, 0), PASS);
        rig.register(Key::Cancel, Modifiers::CONTROL);
        assert_eq!(rig.send(0x03, 0x46, LLKHF_EXTENDED, 2000, LCTRL), SWALLOW);
    }

    #[test]
    fn lost_modifier_up_is_reconciled_on_the_next_modifier_down() {
        let mut rig = Rig::new();
        rig.shared
            .observing
            .store(true, Ordering::Relaxed);
        assert_eq!(rig.down(0x5B, 0x5B, 0), PASS);
        // Win+L: the Win key-up never reaches the hook. While recording, the live preview must not
        // show a phantom Meta once Ctrl goes down (the async state has Win up; Ctrl's own edge is
        // not in it yet).
        let session = rig.shared.start_recording();
        drain(&rig.rx);
        assert_eq!(rig.down(0xA2, 0x1D, 0), PASS);
        assert_eq!(
            drain(&rig.rx),
            [
                Event::Key(KeyEvent {
                    down: true,
                    repeat: false,
                    key: Key::LeftCtrl,
                    modifiers: Modifiers::CONTROL,
                    raw_code: 0xA2,
                    ch: None
                }),
                Event::RecordingModifiers {
                    session,
                    modifiers: Modifiers::CONTROL
                }
            ]
        );
    }

    #[test]
    fn fake_shift_up_ends_with_the_real_shift() {
        let mut rig = Rig::new();
        rig.register(Key::Home, Modifiers::SHIFT);
        rig.register(Key::A, Modifiers::SHIFT);
        assert_eq!(rig.down(0xA0, 0x2A, 0), PASS);
        assert_eq!(rig.send(0xA0, 0x22A, LLKHF_UP, 1, 0), PASS);
        assert_eq!(rig.down(0x24, 0x47, 0), SWALLOW);
        // Shift is released while the numpad key is held: no fake re-press follows.
        assert_eq!(rig.up(0xA0, 0x2A), PASS);
        assert_eq!(rig.up(0x67, 0x47), SWALLOW);
        // A later Shift hold whose key-up is lost (secure desktop) is reconciled away as usual; the
        // old fake up does not keep it held.
        assert_eq!(rig.down(0xA0, 0x2A, 0), PASS);
        assert_eq!(rig.down(0x41, 0x1E, 0), PASS);
    }

    #[test]
    fn real_right_shift_is_extended_and_tracked() {
        let mut rig = Rig::new();
        rig.register(Key::F1, Modifiers::SHIFT);
        rig.shared
            .observing
            .store(true, Ordering::Relaxed);
        // A real Right Shift is VK_RSHIFT, scan 0x36, LLKHF_EXTENDED.
        assert_eq!(rig.send(0xA1, 0x36, LLKHF_EXTENDED, 1, 0), PASS);
        assert_eq!(rig.down(0x70, 0x3B, RSHIFT), SWALLOW);
        assert_eq!(rig.up(0x70, 0x3B), SWALLOW);
        let session = rig.shared.start_recording();
        assert_eq!(rig.down(0x74, 0x3F, RSHIFT), SWALLOW);
        assert_eq!(rig.send(0xA1, 0x36, LLKHF_EXTENDED | LLKHF_UP, 1, 0), PASS);
        let session2 = rig.shared.start_recording();
        assert_eq!(rig.send(0xA1, 0x36, LLKHF_EXTENDED, 1, 0), PASS);
        let events: Vec<Event> = drain(&rig.rx)
            .into_iter()
            .filter(|e| !matches!(e, Event::Key(k) if !k.key.is_modifier()))
            .collect();
        let edge = |down, modifiers| {
            Event::Key(KeyEvent {
                down,
                repeat: false,
                key: Key::RightShift,
                modifiers,
                raw_code: 0xA1,
                ch: None,
            })
        };
        assert_eq!(
            events,
            [
                edge(true, Modifiers::SHIFT),
                Event::Hotkey {
                    id: crate::HotkeyId(1),
                    repeat: false
                },
                Event::Recorded {
                    session,
                    gesture: Gesture::new(Key::F5, Modifiers::SHIFT)
                },
                edge(false, Modifiers::NONE),
                edge(true, Modifiers::SHIFT),
                Event::RecordingModifiers {
                    session: session2,
                    modifiers: Modifiers::SHIFT
                },
            ]
        );
    }

    #[test]
    fn phantom_altgr_ctrl_is_a_real_ctrl() {
        let mut rig = Rig::new();
        rig.register(Key::E, Modifiers::ALT);
        assert_eq!(rig.down(0xA2, 0x21D, 0), PASS);
        // The async state already has the phantom LCtrl when RAlt goes down.
        assert_eq!(rig.send(0xA5, 0x38, LLKHF_EXTENDED, 1, LCTRL), PASS);
        // Alt+E must not eat AltGr+E.
        assert_eq!(rig.down(0x45, 0x12, LCTRL | RALT), PASS);
        assert_eq!(rig.up(0x45, 0x12), PASS);

        rig.register(Key::E, Modifiers::CONTROL | Modifiers::ALT);
        assert_eq!(rig.down(0x45, 0x12, LCTRL | RALT), SWALLOW_INJECT);
    }

    #[test]
    fn generic_injected_ctrl_tracks_as_left_ctrl() {
        let mut rig = Rig::new();
        rig.register(Key::A, Modifiers::CONTROL);
        rig.shared
            .observing
            .store(true, Ordering::Relaxed);
        assert_eq!(rig.send(0x11, 0, LLKHF_INJECTED, 1, 0), PASS);
        assert_eq!(rig.send(0x41, 0, LLKHF_INJECTED, 1, LCTRL), SWALLOW);
        assert!(rig.core.is_suppressed(512 + 0x41));
        assert_eq!(
            drain(&rig.rx)[0],
            Event::Key(KeyEvent {
                down: true,
                repeat: false,
                key: Key::LeftCtrl,
                modifiers: Modifiers::CONTROL,
                raw_code: 0x11,
                ch: None
            })
        );
        assert_eq!(rig.send(0x41, 0, LLKHF_INJECTED | LLKHF_UP, 1, 0), SWALLOW);
        assert!(!rig.core.is_suppressed(512 + 0x41));
    }

    #[test]
    fn our_mask_and_packet_events_pass_untouched() {
        let mut rig = Rig::new();
        rig.register(Key::A, Modifiers::NONE);
        rig.shared
            .observing
            .store(true, Ordering::Relaxed);
        let mut send = |vk, extra| {
            let ev = WinEvent {
                vk,
                scan: 0x1E,
                flags: 0,
                time: 1,
                extra,
            };
            rig.state
                .on_event(&mut rig.core, &ev, || panic!("async_mods"), || panic!("ch"))
        };
        assert_eq!(send(0x41, MASK_MARKER), PASS);
        assert_eq!(send(MASK_VK as u32, MASK_MARKER), PASS);
        assert_eq!(send(0xE7, 0), PASS);
        assert_eq!(send(0, 0), PASS);
        assert!(drain(&rig.rx).is_empty());
    }

    #[test]
    fn repeat_detection() {
        let mut rig = Rig::new();
        rig.shared
            .observing
            .store(true, Ordering::Relaxed);
        let repeats = |rig: &Rig| -> Vec<bool> {
            drain(&rig.rx)
                .iter()
                .filter_map(|e| match e {
                    Event::Key(k) => Some(k.repeat),
                    _ => None,
                })
                .collect()
        };
        rig.send(0x41, 0x1E, 0, 1, 0);
        rig.send(0x41, 0x1E, 0, 500, 0);
        rig.send(0x41, 0x1E, 0, 33, 0);
        rig.send(0x41, 0x1E, LLKHF_UP, 10, 0);
        rig.send(0x41, 0x1E, 0, 10, 0);
        // Held with no key-up: a repeat however slow (FilterKeys), until the staleness cap.
        rig.send(0x41, 0x1E, 0, 1100, 0);
        rig.send(0x41, 0x1E, 0, 1800, 0);
        rig.send(0x41, 0x1E, 0, 5001, 0);
        // Another key pressed in between: Windows now repeats that one, so A is a fresh press.
        rig.send(0x42, 0x30, 0, 30, 0);
        rig.send(0x41, 0x1E, 0, 30, 0);
        assert_eq!(repeats(&rig), [false, true, true, false, false, true, true, false, false, false]);
        rig.send(0xA0, 0x2A, 0, 1, 0);
        rig.send(0xA0, 0x2A, 0, 500, 0);
        rig.send(0xA0, 0x2A, LLKHF_UP, 10, 0);
        assert_eq!(repeats(&rig), [false, true, false]);
    }

    #[test]
    fn filter_keys_slow_repeats_of_a_hotkey_do_not_refire_it() {
        // FilterKeys/RepeatKeys can stretch the repeat delay and interval to ~2 s each.
        let mut rig = Rig::new();
        rig.register(Key::A, Modifiers::CONTROL);
        assert_eq!(rig.down(0xA2, 0x1D, 0), PASS);
        assert_eq!(rig.send(0x41, 0x1E, 0, 1, LCTRL), SWALLOW);
        assert_eq!(rig.send(0x41, 0x1E, 0, 1800, LCTRL), SWALLOW);
        assert_eq!(rig.send(0x41, 0x1E, 0, 1800, LCTRL), SWALLOW);
        assert_eq!(rig.up(0x41, 0x1E), SWALLOW);
        let id = crate::HotkeyId(1);
        assert_eq!(
            drain(&rig.rx),
            [
                Event::Hotkey {
                    id,
                    repeat: false
                },
                Event::Hotkey {
                    id,
                    repeat: true
                },
                Event::Hotkey {
                    id,
                    repeat: true
                }
            ]
        );
    }

    #[test]
    fn ch_is_only_computed_for_observed_downs() {
        fn send(rig: &mut Rig, flags: u32, ch: impl FnOnce() -> Option<char>) {
            let ev = WinEvent {
                vk: 0x41,
                scan: 0x1E,
                flags,
                time: 1,
                extra: 0,
            };
            rig.state
                .on_event(&mut rig.core, &ev, || 0, ch);
        }
        let mut rig = Rig::new();
        send(&mut rig, 0, || panic!("not observing"));
        send(&mut rig, LLKHF_UP, || panic!("not observing"));
        rig.shared
            .observing
            .store(true, Ordering::Relaxed);
        drain(&rig.rx);
        send(&mut rig, 0, || Some('a'));
        send(&mut rig, LLKHF_UP, || panic!("up edge"));
        let chars: Vec<Option<char>> = drain(&rig.rx)
            .iter()
            .filter_map(|e| match e {
                Event::Key(k) => Some(k.ch),
                _ => None,
            })
            .collect();
        assert_eq!(chars, [Some('a'), None]);
    }
}
