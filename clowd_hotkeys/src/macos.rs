//! The macOS backend: a CGEventTap on its own run-loop thread. `tap_cb` is glue — every decision
//! lives in `HookCore`.
//!
//! Needs the Accessibility permission (an active, suppressing tap); `spawn` checks it up front and
//! never prompts. The tap, its run-loop source and the boxed `Ctx` the callback reads are created,
//! run and torn down on the hook thread only; the dropping thread only asks that run loop to stop.

use std::cell::{Cell, RefCell};
use std::ffi::{c_ulong, c_void};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr::{self, NonNull};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Sender};
use std::sync::Arc;
use std::thread::{self, JoinHandle};

use block2::RcBlock;
use objc2::rc::autoreleasepool;
use objc2_app_kit::{NSEvent, NSEventType};
use objc2_core_foundation::{kCFRunLoopCommonModes, CFMachPort, CFRetained, CFRunLoop};
use objc2_core_graphics::{
    CGEvent, CGEventField, CGEventFlags, CGEventMask, CGEventTapLocation, CGEventTapOptions, CGEventTapPlacement, CGEventTapProxy,
    CGEventType,
};

use crate::core::{HookCore, Verdict};
use crate::hotkeys::Shared;
use crate::{Error, Event, Key, Modifiers};

/// NX_SYSDEFINED (NSEventTypeSystemDefined): media keys arrive as subtype 8 of it. CoreGraphics
/// has no name for it.
const SYSTEM_DEFINED: CGEventType = CGEventType(14);

/// NX_SUBTYPE_AUX_CONTROL_BUTTONS: the media / volume keys.
const AUX_CONTROL_BUTTONS: i16 = 8;

const EVENT_MASK: CGEventMask =
    1 << CGEventType::KeyDown.0 | 1 << CGEventType::KeyUp.0 | 1 << CGEventType::FlagsChanged.0 | 1 << SYSTEM_DEFINED.0;

/// kVK_CapsLock: one FlagsChanged per press, reported as a down and an up.
const CAPS_LOCK: u16 = 0x39;

/// Suppression codes for NX media events start here, clear of every CGKeyCode (0..0x80); the
/// key events themselves use the CGKeyCode.
const NX_CODE_BASE: u16 = 0x100;

#[link(name = "ApplicationServices", kind = "framework")]
extern "C" {
    /// Whether this process holds the Accessibility permission. Never prompts.
    fn AXIsProcessTrusted() -> bool;
}

/// The hook thread's run loop, held by the API side only to stop it.
struct RunLoopRef(CFRetained<CFRunLoop>);

// SAFETY: objc2-core-foundation marks CFRunLoop not thread-safe because most of its API must run on
// the loop's own thread. Off the hook thread this wrapper is only used for CFRunLoopPerformBlock
// and CFRunLoopWakeUp, which Apple documents as callable from any thread, and for retain/release,
// which are atomic.
unsafe impl Send for RunLoopRef {}
// SAFETY: as above; neither call needs exclusive access.
unsafe impl Sync for RunLoopRef {}

/// The running tap thread. Dropping it stops the run loop and joins.
pub(crate) struct Backend {
    run_loop: RunLoopRef,
    stop_requested: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl Drop for Backend {
    fn drop(&mut self) {
        let Some(thread) = self.thread.take() else {
            return;
        };
        self.stop_requested
            .store(true, Ordering::Release);
        // Skip the wake-up when the loop already ended on its own (tap invalidated); the block
        // would only sit on a dead thread's run loop.
        if !thread.is_finished() {
            // A bare CFRunLoopStop sent before the loop is entered is lost; a block performed on
            // the loop runs once it is (and the flag tells the thread not to run it again).
            let block = RcBlock::new(|| {
                if let Some(run_loop) = CFRunLoop::current() {
                    run_loop.stop();
                }
            });
            // SAFETY: CFRunLoopPerformBlock copies the block and is thread-safe; the common-modes
            // constant is a valid run-loop mode.
            unsafe {
                self.run_loop
                    .0
                    .perform_block(kCFRunLoopCommonModes.map(|mode| &**mode), Some(&*block));
            }
            self.run_loop.0.wake_up();
        }
        let _ = thread.join();
    }
}

/// What the callback reads, through the tap's `user_info`. Lives on the hook thread only and is
/// freed only after the tap is invalidated and its source removed, so no callback can outlive it.
struct Ctx {
    core: RefCell<HookCore>,
    /// The tap, for re-enabling it after a timeout. Set before the source joins the run loop, so
    /// every callback sees it; `run` keeps it retained until after the last callback.
    tap: Cell<Option<NonNull<CFMachPort>>>,
}

pub(crate) fn spawn(shared: Arc<Shared>, tx: Sender<Event>) -> Result<Backend, Error> {
    let stop_requested = Arc::new(AtomicBool::new(false));
    let (ready_tx, ready_rx) = mpsc::channel();
    let thread = thread::Builder::new()
        .name("clowd-hotkeys-hook".into())
        .spawn({
            let stop_requested = stop_requested.clone();
            move || run(shared, tx, stop_requested, ready_tx)
        })
        .map_err(|e| Error::HookFailed(e.to_string()))?;
    match ready_rx.recv() {
        Ok(Ok(run_loop)) => Ok(Backend {
            run_loop,
            stop_requested,
            thread: Some(thread),
        }),
        Ok(Err(error)) => {
            let _ = thread.join();
            Err(error)
        }
        Err(_) => {
            let _ = thread.join();
            Err(Error::HookFailed("the hook thread exited during startup".into()))
        }
    }
}

fn run(shared: Arc<Shared>, tx: Sender<Event>, stop_requested: Arc<AtomicBool>, ready: Sender<Result<RunLoopRef, Error>>) {
    let fail = |error| {
        shared.alive.store(false, Ordering::Release);
        let _ = ready.send(Err(error));
    };

    // SAFETY: a plain query with no arguments.
    if !unsafe { AXIsProcessTrusted() } {
        fail(Error::PermissionDenied);
        return;
    }

    let ctx = Box::into_raw(Box::new(Ctx {
        core: RefCell::new(HookCore::new(shared.clone(), tx)),
        tap: Cell::new(None),
    }));
    // SAFETY: tap_cb matches CGEventTapCallBack, and `ctx` stays valid until after the tap is
    // invalidated below.
    let tap = unsafe {
        CGEvent::tap_create(
            CGEventTapLocation::SessionEventTap,
            CGEventTapPlacement::HeadInsertEventTap,
            CGEventTapOptions::Default,
            EVENT_MASK,
            Some(tap_cb),
            ctx.cast(),
        )
    };
    let Some(tap) = tap else {
        // SAFETY: no tap was created, so nothing else holds `ctx`.
        drop(unsafe { Box::from_raw(ctx) });
        fail(Error::HookFailed("could not create the event tap".into()));
        return;
    };
    // SAFETY: `ctx` is live and only this thread touches it.
    unsafe { (*ctx).tap.set(Some(NonNull::from(&*tap))) };

    // SAFETY: an immutable framework constant.
    let modes = unsafe { kCFRunLoopCommonModes };
    let (Some(source), Some(run_loop)) = (CFMachPort::new_run_loop_source(None, Some(&tap), 0), CFRunLoop::current()) else {
        tap.invalidate();
        drop(tap);
        // SAFETY: the tap is invalidated and was never on a run loop, so no callback can run.
        drop(unsafe { Box::from_raw(ctx) });
        fail(Error::HookFailed("could not attach the event tap to a run loop".into()));
        return;
    };
    run_loop.add_source(Some(&source), modes);
    CGEvent::tap_enable(&tap, true);
    let _ = ready.send(Ok(RunLoopRef(run_loop.clone())));

    // CFRunLoopRun returns when stopped, or at once when the tap is invalidated (its source goes,
    // leaving the loop empty). Never poll CGEventTapIsEnabled: it leaks Mach vouchers.
    let mut invalidated = false;
    loop {
        CFRunLoop::run();
        if stop_requested.load(Ordering::Acquire) {
            break;
        }
        if !tap.is_valid() {
            invalidated = true;
            break;
        }
    }

    // Teardown, in this order and on this thread: after it no callback can run, so the Ctx (and
    // with it the only Sender) can go.
    CGEvent::tap_enable(&tap, false);
    tap.invalidate();
    run_loop.remove_source(Some(&source), modes);
    drop(source);
    drop(tap);
    // SAFETY: the tap is invalidated and its source removed; nothing else refers to `ctx`.
    let mut core = unsafe { Box::from_raw(ctx) }
        .core
        .into_inner();
    if invalidated {
        core.stopped("the event tap was invalidated".into());
    }
    // Dropping the core drops the only Sender, which closes Events once it is drained.
    drop(core);
    shared.alive.store(false, Ordering::Release);
}

/// The tap callback: decide and enqueue, nothing else. No allocation beyond the queue node, no
/// logging, no user code; a panic passes the event.
unsafe extern "C-unwind" fn tap_cb(
    _proxy: CGEventTapProxy,
    ty: CGEventType,
    event: NonNull<CGEvent>,
    user_info: *mut c_void,
) -> *mut CGEvent {
    // SAFETY: `user_info` is the `Ctx` from `run`, which outlives every callback; callbacks run on
    // the hook thread only. `event` is valid for this call.
    let (ctx, cg_event) = unsafe { (&*user_info.cast::<Ctx>(), event.as_ref()) };
    let verdict = catch_unwind(AssertUnwindSafe(|| handle(ctx, ty, cg_event))).unwrap_or(Verdict::Pass);
    match verdict {
        Verdict::Swallow => ptr::null_mut(),
        Verdict::Pass => event.as_ptr(),
    }
}

fn handle(ctx: &Ctx, ty: CGEventType, event: &CGEvent) -> Verdict {
    let Ok(mut core) = ctx.core.try_borrow_mut() else {
        return Verdict::Pass;
    };
    match ty {
        CGEventType::TapDisabledByTimeout | CGEventType::TapDisabledByUserInput => {
            if let Some(tap) = ctx.tap.get() {
                // SAFETY: `run` keeps the tap retained until after the last callback.
                CGEvent::tap_enable(unsafe { tap.as_ref() }, true);
            }
            // Ups that went by while the tap was off were never seen.
            core.reset();
            Verdict::Pass
        }
        CGEventType::KeyDown | CGEventType::KeyUp => {
            let down = ty == CGEventType::KeyDown;
            let code = keycode(event);
            let repeat = CGEvent::integer_value_field(Some(event), CGEventField::KeyboardEventAutorepeat) != 0;
            let mods = collapse(CGEvent::flags(Some(event)));
            let ch = if down && core.observing() { typed_char(event) } else { None };
            core.key(
                code,
                Some(code as u32),
                ch,
                Key::from_mac(code).unwrap_or(Key::None),
                down,
                repeat,
                mods,
            )
        }
        CGEventType::FlagsChanged => {
            flags_changed(&mut core, event);
            Verdict::Pass
        }
        SYSTEM_DEFINED => system_defined(&mut core, event),
        _ => Verdict::Pass,
    }
}

fn keycode(event: &CGEvent) -> u16 {
    CGEvent::integer_value_field(Some(event), CGEventField::KeyboardEventKeycode) as u16
}

/// A modifier edge: observe and the recording preview only, never swallowed.
fn flags_changed(core: &mut HookCore, event: &CGEvent) {
    let code = keycode(event);
    let flags = CGEvent::flags(Some(event));
    let mods = collapse(flags);
    let key = Key::from_mac(code).unwrap_or(Key::None);
    if code == CAPS_LOCK {
        core.modifier(code as u32, key, true, false, mods);
        core.modifier(code as u32, key, false, false, mods);
    } else if let Some(down) = modifier_down(code, flags) {
        core.modifier(code as u32, key, down, false, mods);
    }
}

/// NSSystemDefined: media keys (subtype 8). They stay out of the observe stream (no key code).
fn system_defined(core: &mut HookCore, event: &CGEvent) -> Verdict {
    let data1 = autoreleasepool(|_| {
        let ns_event = NSEvent::eventWithCGEvent(event)?;
        // Only ask for the subtype of a system-defined event; other types raise.
        (ns_event.r#type() == NSEventType::SystemDefined && ns_event.subtype().0 == AUX_CONTROL_BUTTONS).then(|| ns_event.data1())
    });
    let Some((nx, down, repeat)) = data1.and_then(decode_media) else {
        return Verdict::Pass;
    };
    match Key::from_nx(nx) {
        Some(key) => core.key(
            NX_CODE_BASE + nx as u16,
            None,
            None,
            key,
            down,
            repeat,
            collapse(CGEvent::flags(Some(event))),
        ),
        None => Verdict::Pass,
    }
}

/// The character this key-down types, read from the real event (exact, unlike re-translating
/// the key code). `None` for dead keys, non-typing keys and control characters.
fn typed_char(event: &CGEvent) -> Option<char> {
    let mut buffer = [0u16; 8];
    let mut len: c_ulong = 0;
    // SAFETY: both pointers are valid, and the length passed is the buffer's.
    unsafe { CGEvent::keyboard_get_unicode_string(Some(event), buffer.len() as c_ulong, &mut len, buffer.as_mut_ptr()) };
    first_char(&buffer[..(len as usize).min(buffer.len())])
}

fn first_char(utf16: &[u16]) -> Option<char> {
    char::decode_utf16(utf16.iter().copied())
        .next()?
        .ok()
        .filter(|c| !c.is_control())
}

/// Side-agnostic modifiers from the device-independent flag bits. AlphaShift, SecondaryFn and
/// NumericPad are ignored.
fn collapse(flags: CGEventFlags) -> Modifiers {
    [
        (CGEventFlags::MaskShift, Modifiers::SHIFT),
        (CGEventFlags::MaskControl, Modifiers::CONTROL),
        (CGEventFlags::MaskAlternate, Modifiers::ALT),
        (CGEventFlags::MaskCommand, Modifiers::META),
    ]
    .into_iter()
    .filter(|(flag, _)| flags.contains(*flag))
    .fold(Modifiers::NONE, |mods, (_, modifier)| mods | modifier)
}

/// Whether the modifier key `code` is down after its FlagsChanged edge, from the device-dependent
/// (per-side) flag bits; `None` for codes that are not a side modifier or Fn.
fn modifier_down(code: u16, flags: CGEventFlags) -> Option<bool> {
    let bit = match code {
        0x3B => 0x0001, // LeftCtrl
        0x38 => 0x0002, // LeftShift
        0x3C => 0x0004, // RightShift
        0x37 => 0x0008, // left Command (LWin)
        0x36 => 0x0010, // right Command (RWin)
        0x3A => 0x0020, // LeftAlt
        0x3D => 0x0040, // RightAlt
        0x3E => 0x2000, // RightCtrl
        0x3F => CGEventFlags::MaskSecondaryFn.0,
        _ => return None,
    };
    Some(flags.0 & bit != 0)
}

/// `(nx keytype, down, repeat)` from an aux-control `data1`: keytype in bits 16..32, state 0x0A
/// (down) or 0x0B (up) in bits 8..16, repeat in bit 0. `None` for any other state.
fn decode_media(data1: isize) -> Option<(u32, bool, bool)> {
    let data1 = data1 as u64;
    let down = match (data1 >> 8) & 0xFF {
        0x0A => true,
        0x0B => false,
        _ => return None,
    };
    Some((((data1 >> 16) & 0xFFFF) as u32, down, data1 & 1 != 0))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn collapse_ignores_caps_fn_and_numpad() {
        let flags = CGEventFlags::MaskShift
            | CGEventFlags::MaskCommand
            | CGEventFlags::MaskAlphaShift
            | CGEventFlags::MaskSecondaryFn
            | CGEventFlags::MaskNumericPad;
        assert_eq!(collapse(flags), Modifiers::SHIFT | Modifiers::META);
        assert_eq!(
            collapse(CGEventFlags::MaskControl | CGEventFlags::MaskAlternate),
            Modifiers::CONTROL | Modifiers::ALT
        );
        assert_eq!(collapse(CGEventFlags::empty()), Modifiers::NONE);
    }

    #[test]
    fn modifier_edges_read_their_own_side_bit() {
        // Right Shift released while Left Shift is still held: the device-independent Shift flag
        // stays set, the RShift bit does not.
        let left_shift_held = CGEventFlags(CGEventFlags::MaskShift.0 | 0x0002);
        assert_eq!(modifier_down(0x3C, left_shift_held), Some(false));
        assert_eq!(modifier_down(0x38, left_shift_held), Some(true));
        assert_eq!(modifier_down(0x3E, CGEventFlags(0x2000)), Some(true));
        assert_eq!(modifier_down(0x3F, CGEventFlags::MaskSecondaryFn), Some(true));
        assert_eq!(modifier_down(0x00, left_shift_held), None);
        // Every side modifier in the key table has a bit.
        for key in [
            Key::LWin,
            Key::RWin,
            Key::LeftShift,
            Key::RightShift,
            Key::LeftCtrl,
            Key::RightCtrl,
            Key::LeftAlt,
            Key::RightAlt,
        ] {
            assert!(modifier_down(key.to_mac().unwrap(), CGEventFlags::empty()).is_some(), "{key}");
        }
    }

    #[test]
    fn media_data1_decodes() {
        // NX_KEYTYPE_PLAY down, then up with the repeat bit clear.
        assert_eq!(decode_media(16 << 16 | 0x0A << 8), Some((16, true, false)));
        assert_eq!(decode_media(16 << 16 | 0x0B << 8), Some((16, false, false)));
        // NX_KEYTYPE_SOUND_UP (0) auto-repeat.
        assert_eq!(decode_media(0x0A << 8 | 1), Some((0, true, true)));
        assert_eq!(decode_media(17 << 16 | 0x0C << 8), None);
        assert_eq!(Key::from_nx(16), Some(Key::MediaPlayPause));
    }

    #[test]
    fn typed_char_keeps_the_first_printable_scalar() {
        assert_eq!(first_char(&[]), None);
        assert_eq!(first_char(&['a' as u16]), Some('a'));
        assert_eq!(first_char(&[0x0001]), None);
        assert_eq!(first_char(&[0xD83D, 0xDE00, 'x' as u16]), Some('\u{1F600}'));
        assert_eq!(first_char(&[0xDE00]), None);
    }
}
