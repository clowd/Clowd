//! The macOS backend: a CGEventTap on its own run-loop thread. `tap_cb` is glue — every decision
//! lives in `HookCore`.
//!
//! Needs the Accessibility permission (an active, suppressing tap); `spawn` checks it up front and
//! never prompts. The tap, its run-loop source and the boxed `Ctx` the callback reads are created,
//! run and torn down on the hook thread only; the dropping thread only asks that run loop to stop.
//!
//! The hook thread wakes every `TICK` to check the tap's health: a revoked permission (polled by a
//! watcher thread, which wakes the hook at once), an invalidated tap or one that keeps timing out
//! tears the tap down and ends in `Event::Stopped`.
//!
//! Media keys need AppKit to decode, which happens on the main thread and only when a media key
//! could matter (`aux_data1_on_main`, `HookCore::wants_media`).

use std::cell::{Cell, RefCell};
use std::ffi::{c_ulong, c_void};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr::{self, NonNull};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use block2::RcBlock;
use dispatch2::DispatchQueue;
use objc2::rc::autoreleasepool;
use objc2::MainThreadMarker;
use objc2_app_kit::{NSEvent, NSEventType};
use objc2_core_foundation::{kCFRunLoopCommonModes, kCFRunLoopDefaultMode, CFMachPort, CFRetained, CFRunLoop};
use objc2_core_graphics::{
    CGError, CGEvent, CGEventField, CGEventFlags, CGEventMask, CGEventTapInformation, CGEventTapLocation, CGEventTapOptions,
    CGEventTapPlacement, CGEventTapProxy, CGEventType, CGGetEventTapList, CGPreflightPostEventAccess,
};

use crate::core::{HookCore, Verdict};
use crate::hotkeys::Shared;
use crate::key::mac_positional;
use crate::{Error, Event, Key, Modifiers};

/// NX_SYSDEFINED (NSEventTypeSystemDefined): media keys arrive as subtype 8 of it. CoreGraphics
/// has no name for it.
const SYSTEM_DEFINED: CGEventType = CGEventType(14);

/// NX_SUBTYPE_AUX_CONTROL_BUTTONS: the media / volume keys.
const AUX_CONTROL_BUTTONS: i16 = 8;

/// The longest a media key waits for the main thread to decode it before it passes undecoded.
/// Well inside the tap's own timeout.
const MAIN_TIMEOUT: Duration = Duration::from_millis(50);

/// Set while a decode sits on the main queue: a main thread that has not run the last one will
/// not run the next in time either, so the next event passes without waiting or queueing more.
static MAIN_QUEUED: AtomicBool = AtomicBool::new(false);

const EVENT_MASK: CGEventMask =
    1 << CGEventType::KeyDown.0 | 1 << CGEventType::KeyUp.0 | 1 << CGEventType::FlagsChanged.0 | 1 << SYSTEM_DEFINED.0;

/// The bits a keyboard tap is useless without. CoreGraphics clears them from a tap that may not see
/// keys and still returns it, as FlagsChanged and the media keys survive.
const KEY_MASK: CGEventMask = 1 << CGEventType::KeyDown.0 | 1 << CGEventType::KeyUp.0;

/// The longest the hook thread sleeps between health checks.
const TICK: Duration = Duration::from_secs(1);

/// Timeout re-enables allowed within `REENABLE_WINDOW`; the next one gives up instead. A tap that
/// keeps timing out (reported after the permission goes) stalls input on every attempt.
const MAX_REENABLES: usize = 5;
const REENABLE_WINDOW: Duration = Duration::from_secs(10);

/// kVK_CapsLock: one FlagsChanged per press, reported as a down and an up.
const CAPS_LOCK: u16 = 0x39;

/// Suppression codes for NX media events start here, clear of every CGKeyCode (0..0x80); the
/// key events themselves use the CGKeyCode.
const NX_CODE_BASE: u16 = 0x100;

/// KBGetLayoutType's answer for ISO keyboards ('ISO '; ANSI is 'ANSI', JIS 'JIS ', unknown '????').
const KEYBOARD_ISO: u32 = u32::from_be_bytes(*b"ISO ");

#[link(name = "ApplicationServices", kind = "framework")]
extern "C" {
    /// Whether this process holds the Accessibility permission. Never prompts.
    fn AXIsProcessTrusted() -> bool;
}

#[link(name = "Carbon", kind = "framework")]
extern "C" {
    /// The physical layout (ANSI, ISO, JIS) of a keyboard type. Exported by HIToolbox but in no
    /// current SDK header. A static table lookup (about 10 ns), safe off the main thread.
    fn KBGetLayoutType(keyboard_type: i16) -> u32;
}

/// Whether this process may run the keyboard tap. Neither query prompts. AXIsProcessTrusted is
/// answered from a cache after its first call (about 10 ms, then microseconds), so alone it may
/// never see a revocation; CGPreflightPostEventAccess asks tccd every time (about 8 ms), which is
/// why the tick asks from a watcher thread, not from the thread every key waits on.
fn permitted() -> bool {
    // SAFETY: a plain query with no arguments.
    (unsafe { AXIsProcessTrusted() }) && CGPreflightPostEventAccess()
}

/// The tap `run` installs and how it checks on it. Always `KEYBOARD` outside tests, which use a
/// listen-only tablet tap: it needs no permission and never sees or delays input.
#[derive(Copy, Clone)]
struct TapConfig {
    location: CGEventTapLocation,
    placement: CGEventTapPlacement,
    options: CGEventTapOptions,
    mask: CGEventMask,
    /// The bits of `mask` the tap must actually be granted.
    required: CGEventMask,
    /// Asked on the hook thread before the tap is created, then every tick by the watcher. Must
    /// never prompt.
    permitted: fn() -> bool,
    tick: Duration,
}

impl TapConfig {
    const KEYBOARD: Self = Self {
        location: CGEventTapLocation::SessionEventTap,
        placement: CGEventTapPlacement::HeadInsertEventTap,
        options: CGEventTapOptions::Default,
        mask: EVENT_MASK,
        required: KEY_MASK,
        permitted,
        tick: TICK,
    };
}

/// The hook thread's run loop, held by the API side and the watcher only to stop it.
struct RunLoopRef(CFRetained<CFRunLoop>);

impl RunLoopRef {
    /// Makes the loop's current (or next) run return. A bare CFRunLoopStop sent before the loop is
    /// entered is lost; a block performed on the loop runs once it is. Harmless on a loop whose
    /// thread has ended: the block just sits there.
    fn stop(&self) {
        let block = RcBlock::new(|| {
            if let Some(run_loop) = CFRunLoop::current() {
                run_loop.stop();
            }
        });
        // SAFETY: CFRunLoopPerformBlock copies the block and is thread-safe; the common-modes
        // constant is a valid run-loop mode.
        unsafe {
            self.0
                .perform_block(kCFRunLoopCommonModes.map(|mode| &**mode), Some(&*block));
        }
        self.0.wake_up();
    }
}

// SAFETY: objc2-core-foundation marks CFRunLoop not thread-safe because most of its API must run on
// the loop's own thread. Off the hook thread this wrapper is only used for CFRunLoopPerformBlock
// and CFRunLoopWakeUp, which Apple documents as callable from any thread, and for retain/release,
// which are atomic.
unsafe impl Send for RunLoopRef {}
// SAFETY: as above; neither call needs exclusive access.
unsafe impl Sync for RunLoopRef {}

/// The running tap thread and its permission watcher. Dropping it stops both and joins.
pub(crate) struct Backend {
    run_loop: RunLoopRef,
    stop_requested: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
    /// The permission watcher; dropping the Sender stops it.
    watcher: Option<(Sender<()>, JoinHandle<()>)>,
}

impl Drop for Backend {
    fn drop(&mut self) {
        self.stop_hook();
        if let Some((stop, watcher)) = self.watcher.take() {
            drop(stop);
            let _ = watcher.join();
        }
    }
}

impl Backend {
    fn stop_hook(&mut self) {
        let Some(thread) = self.thread.take() else {
            return;
        };
        self.stop_requested
            .store(true, Ordering::Release);
        // Skip the wake-up when the loop already ended on its own (a failed health check); the
        // block would only sit on a dead thread's run loop.
        if !thread.is_finished() {
            // The flag tells the thread not to run the loop again.
            self.run_loop.stop();
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
    budget: Cell<ReenableBudget>,
    /// Why the tap was left disabled; the next tick stops the hook.
    gave_up: Cell<Option<&'static str>>,
    /// Raised by the watcher once the permission is gone.
    revoked: Arc<AtomicBool>,
}

impl Ctx {
    fn new(core: HookCore, revoked: Arc<AtomicBool>) -> Self {
        Self {
            core: RefCell::new(core),
            tap: Cell::new(None),
            budget: Cell::new(ReenableBudget::default()),
            gave_up: Cell::new(None),
            revoked,
        }
    }
}

const REVOKED: &str = "the Accessibility permission was revoked";

/// The last `MAX_REENABLES` timeout re-enables, oldest at `next`.
#[derive(Copy, Clone, Default)]
struct ReenableBudget {
    recent: [Option<Instant>; MAX_REENABLES],
    next: usize,
}

impl ReenableBudget {
    /// Records a re-enable at `now`, or returns false when `MAX_REENABLES` already happened within
    /// `REENABLE_WINDOW` of it.
    fn allow(&mut self, now: Instant) -> bool {
        if self.recent[self.next].is_some_and(|oldest| now.saturating_duration_since(oldest) < REENABLE_WINDOW) {
            return false;
        }
        self.recent[self.next] = Some(now);
        self.next = (self.next + 1) % MAX_REENABLES;
        true
    }
}

pub(crate) fn spawn(shared: Arc<Shared>, tx: Sender<Event>) -> Result<Backend, Error> {
    spawn_with(shared, tx, TapConfig::KEYBOARD)
}

fn spawn_with(shared: Arc<Shared>, tx: Sender<Event>, config: TapConfig) -> Result<Backend, Error> {
    let stop_requested = Arc::new(AtomicBool::new(false));
    let revoked = Arc::new(AtomicBool::new(false));
    let (ready_tx, ready_rx) = mpsc::channel();
    let thread = thread::Builder::new()
        .name("clowd-hotkeys-hook".into())
        .spawn({
            let (shared, stop_requested, revoked) = (shared.clone(), stop_requested.clone(), revoked.clone());
            move || run(shared, tx, stop_requested, revoked, ready_tx, config)
        })
        .map_err(|e| Error::HookFailed(e.to_string()))?;
    match ready_rx.recv() {
        Ok(Ok(run_loop)) => {
            let mut backend = Backend {
                run_loop,
                stop_requested,
                thread: Some(thread),
                watcher: None,
            };
            let (stop_tx, stop_rx) = mpsc::channel();
            let hook = RunLoopRef(backend.run_loop.0.clone());
            // Dropping `backend` on failure stops the hook again.
            let watcher = thread::Builder::new()
                .name("clowd-hotkeys-watch".into())
                .spawn(move || watch(config.permitted, config.tick, revoked, hook, shared, stop_rx))
                .map_err(|e| Error::HookFailed(e.to_string()))?;
            backend.watcher = Some((stop_tx, watcher));
            Ok(backend)
        }
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

/// Asks `permitted` every `tick` until told to stop or the hook ends. Once it says no, raises
/// `revoked` and wakes the hook to tear the tap down now rather than on its own next tick: an active
/// tap that outlives its permission can stall input system-wide.
fn watch(permitted: fn() -> bool, tick: Duration, revoked: Arc<AtomicBool>, hook: RunLoopRef, shared: Arc<Shared>, stop: Receiver<()>) {
    while stop.recv_timeout(tick) == Err(RecvTimeoutError::Timeout) && shared.alive.load(Ordering::Acquire) {
        if !permitted() {
            revoked.store(true, Ordering::Release);
            // Not a stop request, so `pump` goes on to `check`, which reports the revocation.
            hook.stop();
            return;
        }
    }
}

fn run(
    shared: Arc<Shared>,
    tx: Sender<Event>,
    stop_requested: Arc<AtomicBool>,
    revoked: Arc<AtomicBool>,
    ready: Sender<Result<RunLoopRef, Error>>,
    config: TapConfig,
) {
    let fail = |error| {
        shared.alive.store(false, Ordering::Release);
        let _ = ready.send(Err(error));
    };

    if !(config.permitted)() {
        fail(Error::PermissionDenied);
        return;
    }

    let ctx = Box::into_raw(Box::new(Ctx::new(HookCore::new(shared.clone(), tx), revoked)));
    let before = tap_list().unwrap_or_default();
    // SAFETY: `ctx` stays valid until after the tap is invalidated below.
    let Some(tap) = (unsafe { create_tap(&config, ctx) }) else {
        // SAFETY: no tap was created, so nothing else holds `ctx`.
        drop(unsafe { Box::from_raw(ctx) });
        // Without the permission the tap may come back NULL rather than stripped.
        fail(match (config.permitted)() {
            true => Error::HookFailed("could not create the event tap".into()),
            false => Error::PermissionDenied,
        });
        return;
    };
    // A stale grant passes the check above and yields a tap that never sees a key.
    let granted = tap_list().and_then(|after| tap_granted(&before, &after, std::process::id() as i32, &config));
    if granted == Some(false) {
        CGEvent::tap_enable(&tap, false);
        tap.invalidate();
        drop(tap);
        // SAFETY: the tap is invalidated and was never on a run loop, so no callback can run.
        drop(unsafe { Box::from_raw(ctx) });
        fail(Error::PermissionDenied);
        return;
    }
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

    // SAFETY: `ctx` is freed only after `pump` returns; like the callbacks, `check` only takes a
    // shared reference, on this thread.
    let failure = pump(&stop_requested, config.tick, || check(unsafe { &*ctx }, &tap));

    // Teardown, in this order and on this thread: after it no callback can run, so the Ctx (and
    // with it the only Sender) can go. Disabling first releases input at once.
    CGEvent::tap_enable(&tap, false);
    tap.invalidate();
    run_loop.remove_source(Some(&source), modes);
    drop(source);
    drop(tap);
    // SAFETY: the tap is invalidated and its source removed; nothing else refers to `ctx`.
    let mut core = unsafe { Box::from_raw(ctx) }
        .core
        .into_inner();
    if let Some(reason) = failure {
        core.stopped(reason.into());
    }
    // Dropping the core drops the only Sender, which closes Events once it is drained.
    drop(core);
    shared.alive.store(false, Ordering::Release);
}

/// # Safety
///
/// `ctx` must stay valid until the tap is invalidated.
unsafe fn create_tap(config: &TapConfig, ctx: *mut Ctx) -> Option<CFRetained<CFMachPort>> {
    // SAFETY: tap_cb matches CGEventTapCallBack; the caller keeps `ctx` valid.
    unsafe {
        CGEvent::tap_create(
            config.location,
            config.placement,
            config.options,
            config.mask,
            Some(tap_cb),
            ctx.cast(),
        )
    }
}

/// Runs this thread's run loop until a stop is requested (`None`) or `check` reports a failure.
/// `check` runs at least every `tick` (and whenever the loop is stopped without a stop request):
/// nothing else would wake the loop when the tap dies, since an invalidation off this thread
/// removes the tap's source without waking it.
fn pump(stop_requested: &AtomicBool, tick: Duration, mut check: impl FnMut() -> Option<&'static str>) -> Option<&'static str> {
    // SAFETY: an immutable framework constant.
    let mode = unsafe { kCFRunLoopDefaultMode };
    loop {
        // Returns at once if the loop is empty (the tap invalidated on this thread), on a stop
        // (requested, or the watcher's), or when `tick` runs out.
        CFRunLoop::run_in_mode(mode, tick.as_secs_f64(), false);
        if stop_requested.load(Ordering::Acquire) {
            return None;
        }
        if let Some(reason) = check() {
            return Some(reason);
        }
    }
}

/// The tick's health check, on the hook thread between callbacks: why the hook must stop, if it must.
fn check(ctx: &Ctx, tap: &CFMachPort) -> Option<&'static str> {
    if let Some(reason) = ctx.gave_up.get() {
        return Some(reason);
    }
    if !tap.is_valid() {
        return Some("the event tap was invalidated");
    }
    if ctx.revoked.load(Ordering::Acquire) {
        return Some(REVOKED);
    }
    // Disabled without the callback hearing of it; counted like a timeout.
    if !CGEvent::tap_is_enabled(tap) {
        if let Ok(mut core) = ctx.core.try_borrow_mut() {
            reenable(ctx, &mut core, true);
        }
    }
    ctx.gave_up.get()
}

/// Turns a disabled tap back on (and forgets the ups that went by while it was off), unless the
/// permission is gone or `counted` and the re-enable budget is spent: then it stays off for the
/// next tick to stop.
fn reenable(ctx: &Ctx, core: &mut HookCore, counted: bool) {
    if ctx.revoked.load(Ordering::Acquire) {
        ctx.gave_up.set(Some(REVOKED));
        return;
    }
    let mut budget = ctx.budget.get();
    if counted && !budget.allow(Instant::now()) {
        ctx.gave_up
            .set(Some("the event tap kept timing out"));
        return;
    }
    ctx.budget.set(budget);
    if let Some(tap) = ctx.tap.get() {
        // SAFETY: `run` keeps the tap retained until after the last callback.
        CGEvent::tap_enable(unsafe { tap.as_ref() }, true);
    }
    core.reset();
}

/// Every event tap in the login session, or `None` if CoreGraphics will not list them.
fn tap_list() -> Option<Vec<CGEventTapInformation>> {
    let mut count = 0;
    // SAFETY: a null list asks for the count only.
    if unsafe { CGGetEventTapList(0, ptr::null_mut(), &mut count) } != CGError::Success {
        return None;
    }
    // Room for taps created between the two calls.
    let mut taps = Vec::with_capacity(count as usize + 8);
    // SAFETY: the buffer has room for the count passed, and CoreGraphics writes at most that many.
    if unsafe { CGGetEventTapList(taps.capacity() as u32, taps.as_mut_ptr(), &mut count) } != CGError::Success {
        return None;
    }
    // SAFETY: the first `count` entries (at most the capacity) were written.
    unsafe { taps.set_len((count as usize).min(taps.capacity())) };
    Some(taps)
}

/// Whether the tap just created was granted `config.required`, from the tap lists read before and
/// after creating it. The tap is this process's newest session-wide entry of `config`'s kind that
/// `before` lacks (a tap's id is not exposed). `None` when there is no such entry.
fn tap_granted(before: &[CGEventTapInformation], after: &[CGEventTapInformation], pid: i32, config: &TapConfig) -> Option<bool> {
    after
        .iter()
        .filter(|tap| {
            tap.tappingProcess == pid
                && tap.processBeingTapped == 0
                && tap.tapPoint == config.location
                && tap.options == config.options
                && !before
                    .iter()
                    .any(|old| old.eventTapID == tap.eventTapID)
        })
        .max_by_key(|tap| tap.eventTapID)
        .map(|tap| tap.eventsOfInterest & config.required == config.required)
}

/// The tap callback: decide and enqueue, nothing else. No allocation beyond the queue node (and a
/// wanted media key's hand-off to the main thread), no logging, no user code; a panic passes the
/// event.
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
        // A user-input disable follows something the user did, not a slow or unpermitted tap, and
        // cannot repeat at machine speed, so only timeouts count against the budget.
        CGEventType::TapDisabledByTimeout | CGEventType::TapDisabledByUserInput => {
            // Already back on: the tick saw this disable before its notice (the usual order after
            // a stall) and re-enabled and counted it. Counting it again would halve the budget,
            // and resetting again would forget presses swallowed since.
            let handled = ctx.tap.get().is_some_and(|tap| {
                // SAFETY: `run` keeps the tap retained until after the last callback.
                CGEvent::tap_is_enabled(unsafe { tap.as_ref() })
            });
            if !handled {
                reenable(ctx, &mut core, ty == CGEventType::TapDisabledByTimeout);
            }
            Verdict::Pass
        }
        CGEventType::KeyDown | CGEventType::KeyUp => {
            let down = ty == CGEventType::KeyDown;
            let os_code = keycode(event);
            // Matched and suppressed by position; observed as the OS reported it (C# feeds
            // raw_code back to CGEventSourceKeyState). A down and its up come from one keyboard,
            // so both get the same code.
            let code = mac_positional(os_code, is_iso(event));
            let repeat = CGEvent::integer_value_field(Some(event), CGEventField::KeyboardEventAutorepeat) != 0;
            let mods = collapse(CGEvent::flags(Some(event)));
            let ch = if down && core.observing() { typed_char(event) } else { None };
            core.key(
                code,
                Some(os_code as u32),
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
        SYSTEM_DEFINED => system_defined(&mut core, event, aux_data1_on_main),
        _ => Verdict::Pass,
    }
}

fn keycode(event: &CGEvent) -> u16 {
    CGEvent::integer_value_field(Some(event), CGEventField::KeyboardEventKeycode) as u16
}

/// Whether the keyboard that sent this key event is ISO. Asked per event, of the event's own
/// keyboard type rather than LMGetKbdType's last-used one: a hot-plugged or second keyboard can
/// differ, and the lookup is cheap.
fn is_iso(event: &CGEvent) -> bool {
    let keyboard_type = CGEvent::integer_value_field(Some(event), CGEventField::KeyboardEventKeyboardType);
    // SAFETY: a plain lookup; unknown types (injected events often carry 0) answer '????'.
    i16::try_from(keyboard_type).is_ok_and(|keyboard_type| unsafe { KBGetLayoutType(keyboard_type) } == KEYBOARD_ISO)
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

/// NSSystemDefined: media keys (subtype 8), read by `decode` (the `data1` of an aux-control event).
/// They stay out of the observe stream (no key code).
fn system_defined(core: &mut HookCore, event: &CGEvent, decode: impl FnOnce(&CGEvent) -> Option<isize>) -> Verdict {
    // Mouse buttons 3+, power, eject and the like arrive here too; leave AppKit out of all of it
    // unless a media key could match.
    if !core.wants_media(NX_CODE_BASE..) {
        return Verdict::Pass;
    }
    let Some((nx, down, repeat, key)) = decode(event)
        .and_then(decode_media)
        .and_then(|(nx, down, repeat)| Some((nx, down, repeat, Key::from_nx(nx)?)))
    else {
        // Not a media key we name, or its decode was lost. If only a swallowed press kept the
        // decoding on, its up was likely the one lost: forget it, or every system-defined event
        // would wait on the main thread until that key is pressed again.
        core.forget_unwanted_media(NX_CODE_BASE..);
        return Verdict::Pass;
    };
    core.key(
        NX_CODE_BASE + nx as u16,
        None,
        None,
        key,
        down,
        repeat,
        collapse(CGEvent::flags(Some(event))),
    )
}

/// `aux_data1` on the main thread (VLC saw these AppKit calls crash off it on M1 / Big Sur; libuiohook
/// marshals them too). Gives up after `MAIN_TIMEOUT`, so a host whose main thread does not service
/// its run loop or main queue never decodes a media key, and one busy for longer misses that event.
fn aux_data1_on_main(event: &CGEvent) -> Option<isize> {
    if MainThreadMarker::new().is_some() {
        return aux_data1(event);
    }
    decode_on(DispatchQueue::main(), &MAIN_QUEUED, event, aux_data1, MAIN_TIMEOUT)
}

/// Runs `decode` on `queue` and waits at most `timeout` for it; `None` on a timeout, or at once
/// while the last job of `queued` has not run. The job owns everything it touches (its end of a
/// channel, and a copy of the event: the original goes back to CoreGraphics when the callback
/// returns), so it may run late.
fn decode_on(
    queue: &DispatchQueue,
    queued: &'static AtomicBool,
    event: &CGEvent,
    decode: fn(&CGEvent) -> Option<isize>,
    timeout: Duration,
) -> Option<isize> {
    if queued.swap(true, Ordering::AcqRel) {
        return None;
    }
    let Some(copy) = CGEvent::new_copy(Some(event)).map(EventCopy) else {
        queued.store(false, Ordering::Release);
        return None;
    };
    let (tx, rx) = mpsc::sync_channel(1);
    queue.exec_async(move || {
        // libdispatch calls this through an extern "C" frame: a panic must not reach it.
        let data1 = catch_unwind(AssertUnwindSafe(|| decode(copy.event()))).unwrap_or(None);
        queued.store(false, Ordering::Release);
        // The hook may have stopped waiting; the slot is there either way, so this never blocks.
        let _ = tx.send(data1);
    });
    rx.recv_timeout(timeout).unwrap_or(None)
}

/// An event copy that only the queued job uses.
struct EventCopy(CFRetained<CGEvent>);

// SAFETY: made on the hook thread and then touched only by the job it moves into; CF retain counts
// are atomic, so it may be released on the queue's thread.
unsafe impl Send for EventCopy {}

impl EventCopy {
    fn event(&self) -> &CGEvent {
        &self.0
    }
}

/// The `data1` of an aux-control (media key) system-defined event, read through AppKit.
fn aux_data1(event: &CGEvent) -> Option<isize> {
    autoreleasepool(|_| {
        let ns_event = NSEvent::eventWithCGEvent(event)?;
        // Only ask for the subtype of a system-defined event; other types raise.
        (ns_event.r#type() == NSEventType::SystemDefined && ns_event.subtype().0 == AUX_CONTROL_BUTTONS).then(|| ns_event.data1())
    })
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
/// (per-side) flag bits; `None` for codes that are not a side modifier or Fn. An edge with no side
/// bit of its family set (injected events can carry only the device-independent mask) falls back
/// to that mask.
fn modifier_down(code: u16, flags: CGEventFlags) -> Option<bool> {
    const CONTROL: (u64, CGEventFlags) = (0x0001 | 0x2000, CGEventFlags::MaskControl);
    const SHIFT: (u64, CGEventFlags) = (0x0002 | 0x0004, CGEventFlags::MaskShift);
    const COMMAND: (u64, CGEventFlags) = (0x0008 | 0x0010, CGEventFlags::MaskCommand);
    const ALT: (u64, CGEventFlags) = (0x0020 | 0x0040, CGEventFlags::MaskAlternate);
    let (bit, (family, generic)) = match code {
        0x3B => (0x0001, CONTROL), // LeftCtrl
        0x38 => (0x0002, SHIFT),   // LeftShift
        0x3C => (0x0004, SHIFT),   // RightShift
        0x37 => (0x0008, COMMAND), // left Command (LWin)
        0x36 => (0x0010, COMMAND), // right Command (RWin)
        0x3A => (0x0020, ALT),     // LeftAlt
        0x3D => (0x0040, ALT),     // RightAlt
        0x3E => (0x2000, CONTROL), // RightCtrl
        0x3F => return Some(flags.contains(CGEventFlags::MaskSecondaryFn)),
        _ => return None,
    };
    Some(match flags.0 & family {
        0 => flags.contains(generic),
        _ => flags.0 & bit != 0,
    })
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
    use std::sync::atomic::AtomicU32;
    use std::sync::Mutex;

    use super::*;
    use crate::{core, Gesture};

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
    fn modifier_edges_without_side_bits_read_the_generic_flag() {
        // Injected events can carry only the device-independent masks.
        assert_eq!(modifier_down(0x38, CGEventFlags::MaskShift), Some(true));
        assert_eq!(modifier_down(0x3C, CGEventFlags::MaskShift), Some(true));
        assert_eq!(modifier_down(0x37, CGEventFlags::MaskCommand), Some(true));
        assert_eq!(modifier_down(0x36, CGEventFlags::MaskCommand), Some(true));
        assert_eq!(modifier_down(0x3B, CGEventFlags::MaskControl), Some(true));
        assert_eq!(modifier_down(0x3E, CGEventFlags::MaskControl), Some(true));
        assert_eq!(modifier_down(0x3A, CGEventFlags::MaskAlternate), Some(true));
        assert_eq!(modifier_down(0x3D, CGEventFlags::MaskAlternate), Some(true));
        assert_eq!(modifier_down(0x38, CGEventFlags::empty()), Some(false));
        // Another family's side bit does not count: Shift released while Command is held.
        assert_eq!(modifier_down(0x38, CGEventFlags(CGEventFlags::MaskCommand.0 | 0x0008)), Some(false));
        assert_eq!(modifier_down(0x38, CGEventFlags(CGEventFlags::MaskShift.0 | 0x0008)), Some(true));
    }

    /// A key event as a keyboard of `keyboard_type` (CGEventSourceKeyboardType) reports it. Never posted.
    fn key_event(code: u16, down: bool, keyboard_type: i64) -> CFRetained<CGEvent> {
        let event = CGEvent::new_keyboard_event(None, code, down).expect("a keyboard event");
        CGEvent::set_integer_value_field(Some(&event), CGEventField::KeyboardEventKeyboardType, keyboard_type);
        event
    }

    #[test]
    fn iso_keys_match_by_position_and_observe_the_os_code() {
        // Keyboard types 40 and 41: the ANSI and ISO variants of one Apple keyboard.
        const ANSI: i64 = 40;
        const ISO: i64 = 41;
        let (core, shared, rx) = core::tests::harness();
        let ctx = Ctx::new(core, Arc::default());
        let id = shared
            .register(Gesture::new(Key::Oem3, Modifiers::NONE))
            .unwrap();
        shared
            .observing
            .store(true, Ordering::Relaxed);
        let mut seen = vec![];
        // The key left of 1: 0x32 on ANSI, 0x0A on ISO. Both fire, and swallow their up.
        for (code, keyboard) in [(0x32, ANSI), (0x0A, ISO)] {
            assert_eq!(
                handle(&ctx, CGEventType::KeyDown, &key_event(code, true, keyboard)),
                Verdict::Swallow
            );
            assert_eq!(
                handle(&ctx, CGEventType::KeyUp, &key_event(code, false, keyboard)),
                Verdict::Swallow
            );
            seen.extend(core::tests::drain(&rx));
        }
        // The key beside left Shift on ISO is OemBackslash, so it passes.
        assert_eq!(handle(&ctx, CGEventType::KeyDown, &key_event(0x32, true, ISO)), Verdict::Pass);
        assert_eq!(handle(&ctx, CGEventType::KeyUp, &key_event(0x32, false, ISO)), Verdict::Pass);
        seen.extend(core::tests::drain(&rx));
        let edges: Vec<_> = seen
            .iter()
            .filter_map(|event| match event {
                Event::Key(key) => Some((key.key, key.raw_code, key.down)),
                _ => None,
            })
            .collect();
        // raw_code stays what the OS reported (C# passes it to CGEventSourceKeyState).
        assert_eq!(
            edges,
            [
                (Key::Oem3, 0x32, true),
                (Key::Oem3, 0x32, false),
                (Key::Oem3, 0x0A, true),
                (Key::Oem3, 0x0A, false),
                (Key::OemBackslash, 0x32, true),
                (Key::OemBackslash, 0x32, false)
            ]
        );
        let fired = seen
            .iter()
            .filter(|event| {
                **event
                    == Event::Hotkey {
                        id,
                        repeat: false,
                    }
            })
            .count();
        assert_eq!(fired, 2);
    }

    #[test]
    fn system_defined_skips_appkit_unless_a_media_key_could_match() {
        const PLAY_DOWN: isize = 16 << 16 | 0x0A << 8;
        const PLAY_UP: isize = 16 << 16 | 0x0B << 8;
        let (mut core, shared, rx) = core::tests::harness();
        shared
            .register(Gesture::new(Key::D1, Modifiers::CONTROL | Modifiers::SHIFT))
            .unwrap();
        // Never posted; the decoders below stand in for AppKit.
        let event = CGEvent::new(None).expect("an event");
        let decoded = Cell::new(false);
        let decode = |data1| {
            let decoded = &decoded;
            move |_: &CGEvent| {
                decoded.set(true);
                data1
            }
        };
        assert_eq!(system_defined(&mut core, &event, decode(Some(PLAY_DOWN))), Verdict::Pass);
        assert!(!decoded.get(), "decoded with no media key registered");

        let id = shared
            .register(Gesture::new(Key::MediaPlayPause, Modifiers::NONE))
            .unwrap();
        assert_eq!(system_defined(&mut core, &event, decode(Some(PLAY_DOWN))), Verdict::Swallow);
        assert!(decoded.take());
        assert_eq!(
            core::tests::drain(&rx),
            [Event::Hotkey {
                id,
                repeat: false
            }]
        );
        // Unregistered while held: the up is still decoded and swallowed, then decoding stops.
        assert!(shared.unregister(id));
        assert_eq!(system_defined(&mut core, &event, decode(Some(PLAY_UP))), Verdict::Swallow);
        assert!(decoded.take());
        assert_eq!(system_defined(&mut core, &event, decode(Some(PLAY_DOWN))), Verdict::Pass);
        assert!(!decoded.get());
        // A decode that gave up (the main thread was busy) passes.
        shared.start_recording();
        assert_eq!(system_defined(&mut core, &event, decode(None)), Verdict::Pass);
        assert!(decoded.take());
        assert!(core::tests::drain(&rx).is_empty());
    }

    #[test]
    fn a_media_press_whose_up_was_lost_stops_the_decoding_once_unregistered() {
        const PLAY_DOWN: isize = 16 << 16 | 0x0A << 8;
        let (mut core, shared, _rx) = core::tests::harness();
        let event = CGEvent::new(None).expect("an event");
        let decoded = Cell::new(0);
        let decode = |data1| {
            let decoded = &decoded;
            move |_: &CGEvent| {
                decoded.set(decoded.get() + 1);
                data1
            }
        };
        let id = shared
            .register(Gesture::new(Key::MediaPlayPause, Modifiers::NONE))
            .unwrap();
        assert_eq!(system_defined(&mut core, &event, decode(Some(PLAY_DOWN))), Verdict::Swallow);
        // Its up missed the main thread, so it passed undecoded and the press stays suppressed.
        assert_eq!(system_defined(&mut core, &event, decode(None)), Verdict::Pass);
        // Kept while registered: the up may still come.
        assert!(core.is_suppressed(NX_CODE_BASE + 16));
        assert!(shared.unregister(id));
        decoded.set(0);
        // Mouse buttons 4/5, brightness and the like, which decode to no media key.
        for _ in 0..3 {
            assert_eq!(system_defined(&mut core, &event, decode(None)), Verdict::Pass);
        }
        assert!(decoded.get() <= 1, "decoded {} times with nothing to match", decoded.get());
        assert!(!core.wants_media(NX_CODE_BASE..));
    }

    /// A media key event as AppKit builds it, never posted.
    fn media_event(data1: isize) -> CFRetained<CGEvent> {
        use objc2_app_kit::NSEventModifierFlags;
        use objc2_core_foundation::CGPoint;
        let event = NSEvent::otherEventWithType_location_modifierFlags_timestamp_windowNumber_context_subtype_data1_data2(
            NSEventType::SystemDefined,
            CGPoint::ZERO,
            NSEventModifierFlags::empty(),
            0.0,
            0,
            None,
            AUX_CONTROL_BUTTONS,
            data1,
            -1,
        )
        .expect("an NSEvent");
        let cg_event = event.CGEvent().expect("its CGEvent");
        // SAFETY: CGEvent is a CF type; this retains it once more for the CFRetained.
        unsafe { CFRetained::retain(NonNull::from(&*cg_event)) }
    }

    #[test]
    fn media_keys_decode_on_a_queue_from_a_private_copy() {
        static QUEUED: AtomicBool = AtomicBool::new(false);
        const PLAY_DOWN: isize = 16 << 16 | 0x0A << 8;
        let queue = DispatchQueue::new("clowd-hotkeys-test", None);
        let event = media_event(PLAY_DOWN);
        assert_eq!(
            decode_on(&queue, &QUEUED, &event, aux_data1, Duration::from_secs(2)),
            Some(PLAY_DOWN)
        );
        assert!(!QUEUED.load(Ordering::Acquire));
        // Not a media key event: decoded, but no data1.
        let other = CGEvent::new(None).expect("an event");
        assert_eq!(decode_on(&queue, &QUEUED, &other, aux_data1, Duration::from_secs(2)), None);
    }

    #[test]
    fn a_stalled_queue_times_out_and_the_late_job_is_harmless() {
        use dispatch2::DispatchObject;
        static QUEUED: AtomicBool = AtomicBool::new(false);
        const PLAY_DOWN: isize = 16 << 16 | 0x0A << 8;
        let queue = DispatchQueue::new("clowd-hotkeys-test", None);
        let event = media_event(PLAY_DOWN);
        queue.suspend();
        let start = Instant::now();
        assert_eq!(decode_on(&queue, &QUEUED, &event, aux_data1, MAIN_TIMEOUT), None);
        let waited = start.elapsed();
        assert!(waited >= MAIN_TIMEOUT && waited < Duration::from_secs(1), "{waited:?}");
        // While that job sits unrun, the next event does not wait at all.
        let start = Instant::now();
        assert_eq!(decode_on(&queue, &QUEUED, &event, aux_data1, Duration::from_secs(5)), None);
        assert!(start.elapsed() < Duration::from_secs(1));
        // The late job runs after its waiter (and the event) are gone.
        drop(event);
        queue.resume();
        let deadline = Instant::now() + Duration::from_secs(2);
        while QUEUED.load(Ordering::Acquire) && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(5));
        }
        assert!(!QUEUED.load(Ordering::Acquire), "the late job never ran");
        assert_eq!(
            decode_on(&queue, &QUEUED, &media_event(PLAY_DOWN), aux_data1, Duration::from_secs(2)),
            Some(PLAY_DOWN)
        );
    }

    #[test]
    fn media_keys_are_not_decoded_while_the_main_queue_is_not_serviced() {
        // libtest runs tests off the main thread and never services the main queue: the decode
        // must give up within its timeout, not hang the hook.
        assert!(MainThreadMarker::new().is_none());
        let event = media_event(16 << 16 | 0x0A << 8);
        let start = Instant::now();
        assert_eq!(aux_data1_on_main(&event), None);
        assert_eq!(aux_data1_on_main(&event), None);
        assert!(start.elapsed() < Duration::from_secs(1), "{:?}", start.elapsed());
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

    /// A listen-only tap on tablet proximity: needs no permission and never sees or delays input.
    const TABLET: TapConfig = TapConfig {
        location: CGEventTapLocation::SessionEventTap,
        placement: CGEventTapPlacement::TailAppendEventTap,
        options: CGEventTapOptions::ListenOnly,
        mask: 1 << CGEventType::TabletProximity.0,
        required: 1 << CGEventType::TabletProximity.0,
        permitted: || true,
        tick: TICK,
    };

    fn tap_info(id: u32, pid: i32, options: CGEventTapOptions, mask: CGEventMask) -> CGEventTapInformation {
        CGEventTapInformation {
            eventTapID: id,
            tapPoint: CGEventTapLocation::SessionEventTap,
            options,
            eventsOfInterest: mask,
            tappingProcess: pid,
            processBeingTapped: 0,
            enabled: true,
            minUsecLatency: 0.0,
            avgUsecLatency: 0.0,
            maxUsecLatency: 0.0,
        }
    }

    #[test]
    fn reenable_budget_gives_up_on_the_sixth_timeout_in_ten_seconds() {
        let mut budget = ReenableBudget::default();
        let start = Instant::now();
        let at = |ms| start + Duration::from_millis(ms);
        for i in 0..MAX_REENABLES as u64 {
            assert!(budget.allow(at(i * 100)), "re-enable {i}");
        }
        assert!(!budget.allow(at(500)));
        // The window slides: once the oldest is ten seconds back there is room again.
        assert!(budget.allow(at(10_000)));
        assert!(budget.allow(at(10_100)));
        assert!(!budget.allow(at(10_150)));
    }

    #[test]
    fn tap_granted_reads_the_key_bits_of_our_new_tap() {
        let pid = 42;
        let keyboard = TapConfig::KEYBOARD;
        let ours = |mask| tap_info(7, pid, CGEventTapOptions::Default, mask);
        assert_eq!(tap_granted(&[], &[ours(EVENT_MASK)], pid, &keyboard), Some(true));
        // Keys stripped, FlagsChanged and the media keys kept: the stale-grant tap.
        assert_eq!(tap_granted(&[], &[ours(EVENT_MASK & !KEY_MASK)], pid, &keyboard), Some(false));
        assert_eq!(tap_granted(&[], &[ours(1 << SYSTEM_DEFINED.0)], pid, &keyboard), Some(false));
        // Another process's tap, a listen-only one of ours and one that predates ours are not it.
        let others = [
            tap_info(8, pid + 1, CGEventTapOptions::Default, 0),
            tap_info(9, pid, CGEventTapOptions::ListenOnly, 0),
            tap_info(3, pid, CGEventTapOptions::Default, 0),
        ];
        assert_eq!(tap_granted(&others[2..], &others, pid, &keyboard), None);
        let mut after = others.to_vec();
        after.push(ours(EVENT_MASK));
        assert_eq!(tap_granted(&others[2..], &after, pid, &keyboard), Some(true));
        // Several new ones: the newest.
        after.push(tap_info(11, pid, CGEventTapOptions::Default, 0));
        assert_eq!(tap_granted(&others[2..], &after, pid, &keyboard), Some(false));
        let mut per_process = ours(EVENT_MASK);
        per_process.processBeingTapped = pid + 1;
        assert_eq!(tap_granted(&[], &[per_process], pid, &keyboard), None);
    }

    #[test]
    fn tap_list_shows_our_new_tap_and_its_mask() {
        let (core, _, _rx) = core::tests::harness();
        let ctx = Box::new(Ctx::new(core, Arc::default()));
        let before = tap_list().expect("the tap list");
        // SAFETY: `ctx` outlives the tap, which is invalidated below and never runs a callback.
        let tap = unsafe { create_tap(&TABLET, ptr::from_ref(&*ctx).cast_mut()) }.expect("a listen-only tap");
        let after = tap_list().expect("the tap list");
        tap.invalidate();
        assert_eq!(tap_granted(&before, &after, std::process::id() as i32, &TABLET), Some(true));
        // A bit the tap never asked for reads as not granted.
        let wider = TapConfig {
            required: TABLET.required | KEY_MASK,
            ..TABLET
        };
        assert_eq!(tap_granted(&before, &after, std::process::id() as i32, &wider), Some(false));
    }

    #[test]
    fn pump_notices_a_tap_invalidated_off_its_thread() {
        struct TapPtr(NonNull<CFMachPort>);
        // SAFETY: only CFMachPortInvalidate is called through it off the hook thread, and CF itself
        // invalidates ports from other threads; the hook thread keeps the tap retained.
        unsafe impl Send for TapPtr {}

        let stop = Arc::new(AtomicBool::new(false));
        let (started_tx, started_rx) = mpsc::channel();
        let (done_tx, done_rx) = mpsc::channel();
        let hook = thread::spawn({
            let stop = stop.clone();
            move || {
                let (core, _, _rx) = core::tests::harness();
                let ctx = Box::new(Ctx::new(core, Arc::default()));
                // SAFETY: `ctx` outlives the tap, which is invalidated before this thread ends.
                let tap = unsafe { create_tap(&TABLET, ptr::from_ref(&*ctx).cast_mut()) }.expect("a listen-only tap");
                ctx.tap.set(Some(NonNull::from(&*tap)));
                let source = CFMachPort::new_run_loop_source(None, Some(&tap), 0).unwrap();
                let run_loop = CFRunLoop::current().unwrap();
                // SAFETY: an immutable framework constant.
                run_loop.add_source(Some(&source), unsafe { kCFRunLoopCommonModes });
                started_tx
                    .send((RunLoopRef(run_loop.clone()), TapPtr(NonNull::from(&*tap))))
                    .unwrap();
                let reason = pump(&stop, TICK, || check(&ctx, &tap));
                tap.invalidate();
                let _ = done_tx.send(reason);
            }
        });
        let (run_loop, tap) = started_rx
            .recv_timeout(Duration::from_secs(2))
            .expect("the hook thread started");
        thread::sleep(Duration::from_millis(100));
        // SAFETY: the hook thread keeps the tap retained until pump returns.
        unsafe { tap.0.as_ref() }.invalidate();
        let reason = done_rx.recv_timeout(2 * TICK);
        if reason.is_err() {
            // Unstick the thread so a failure does not leave it running.
            stop.store(true, Ordering::Release);
            run_loop.stop();
        }
        hook.join().unwrap();
        assert_eq!(reason, Ok(Some("the event tap was invalidated")));
    }

    #[test]
    fn a_revoked_permission_stops_the_hook() {
        static CALLS: AtomicU32 = AtomicU32::new(0);
        // Granted when the tap is created, gone by the first tick.
        let config = TapConfig {
            permitted: || CALLS.fetch_add(1, Ordering::Relaxed) == 0,
            tick: Duration::from_millis(200),
            ..TABLET
        };
        let shared = Arc::new(Shared::new());
        let (tx, rx) = mpsc::channel();
        let backend = spawn_with(shared.clone(), tx, config).expect("the hook started");
        assert_eq!(
            rx.recv_timeout(Duration::from_secs(3)),
            Ok(Event::Stopped("the Accessibility permission was revoked".into()))
        );
        assert_eq!(rx.recv_timeout(Duration::from_secs(1)), Err(mpsc::RecvTimeoutError::Disconnected));
        assert!(!shared.alive.load(Ordering::Acquire));
        assert_eq!(shared.register(Gesture::new(Key::A, Modifiers::NONE)), Err(Error::Stopped));
        drop(backend);
    }

    #[test]
    fn a_revocation_stops_the_hook_as_soon_as_the_watcher_sees_it() {
        static CALLS: AtomicU32 = AtomicU32::new(0);
        static SAID_NO: Mutex<Option<Instant>> = Mutex::new(None);
        // Granted when the tap is created, refused from the watcher's first poll on.
        fn perm() -> bool {
            if CALLS.fetch_add(1, Ordering::Relaxed) == 0 {
                return true;
            }
            SAID_NO
                .lock()
                .unwrap()
                .get_or_insert_with(Instant::now);
            false
        }
        // The real tick, then shorter ones so the watcher's poll lands at other phases of the
        // hook's tick.
        for tick in [
            TICK,
            Duration::from_millis(300),
            Duration::from_millis(300),
            Duration::from_millis(300),
        ] {
            CALLS.store(0, Ordering::Relaxed);
            *SAID_NO.lock().unwrap() = None;
            let config = TapConfig {
                permitted: perm,
                tick,
                ..TABLET
            };
            let (tx, rx) = mpsc::channel();
            let backend = spawn_with(Arc::new(Shared::new()), tx, config).expect("the hook started");
            assert_eq!(
                rx.recv_timeout(4 * tick),
                Ok(Event::Stopped("the Accessibility permission was revoked".into()))
            );
            let late = SAID_NO
                .lock()
                .unwrap()
                .expect("the watcher asked")
                .elapsed();
            assert!(
                late < Duration::from_millis(250),
                "stopped {late:?} after the refusal (tick {tick:?})"
            );
            drop(backend);
        }
    }

    #[test]
    fn a_revoked_tap_is_not_reenabled() {
        let (core, _, _rx) = core::tests::harness();
        let ctx = Ctx::new(core, Arc::new(AtomicBool::new(true)));
        reenable(&ctx, &mut ctx.core.borrow_mut(), false);
        assert_eq!(ctx.gave_up.get(), Some(REVOKED));
    }

    #[test]
    fn a_disable_the_tick_already_handled_is_not_counted_twice() {
        const SPACE: u16 = 0x31; // kVK_Space
        let (core, shared, _rx) = core::tests::harness();
        shared
            .register(Gesture::new(Key::Space, Modifiers::NONE))
            .unwrap();
        let ctx = Box::new(Ctx::new(core, Arc::default()));
        // SAFETY: `ctx` outlives the tap, which is never on a run loop and is invalidated below.
        let tap = unsafe { create_tap(&TABLET, ptr::from_ref(&*ctx).cast_mut()) }.expect("a listen-only tap");
        ctx.tap.set(Some(NonNull::from(&*tap)));
        // Never posted: only its type matters to the disable notices.
        let notice = CGEvent::new(None).expect("an event");
        // WindowServer disables the tap, the tick re-enables it, then the queued notice arrives.
        for cycle in 0..MAX_REENABLES {
            CGEvent::tap_enable(&tap, false);
            assert_eq!(check(&ctx, &tap), None, "cycle {cycle}");
            assert!(CGEvent::tap_is_enabled(&tap));
            // A press swallowed between the two survives the stale notice.
            assert_eq!(handle(&ctx, CGEventType::KeyDown, &key_event(SPACE, true, 40)), Verdict::Swallow);
            assert_eq!(handle(&ctx, CGEventType::TapDisabledByTimeout, &notice), Verdict::Pass);
            assert_eq!(ctx.gave_up.get(), None, "cycle {cycle}");
            assert_eq!(
                handle(&ctx, CGEventType::KeyUp, &key_event(SPACE, false, 40)),
                Verdict::Swallow,
                "cycle {cycle}"
            );
        }
        // The budget still caps disables that are real: it is spent, so the next one gives up.
        CGEvent::tap_enable(&tap, false);
        assert_eq!(handle(&ctx, CGEventType::TapDisabledByTimeout, &notice), Verdict::Pass);
        assert_eq!(ctx.gave_up.get(), Some("the event tap kept timing out"));
        tap.invalidate();
    }

    #[test]
    fn dropping_the_hook_stops_it_without_a_stopped_event() {
        let shared = Arc::new(Shared::new());
        let (tx, rx) = mpsc::channel();
        let backend = spawn_with(shared.clone(), tx, TABLET).expect("the hook started");
        assert_eq!(rx.recv_timeout(Duration::from_millis(1500)), Err(mpsc::RecvTimeoutError::Timeout));
        assert!(shared.alive.load(Ordering::Acquire));
        drop(backend);
        assert_eq!(rx.recv_timeout(Duration::from_secs(1)), Err(mpsc::RecvTimeoutError::Disconnected));
        assert!(!shared.alive.load(Ordering::Acquire));
    }

    /// Documents that polling CGEventTapIsEnabled does not leak Mach ports (vouchers would show
    /// up as port names). Ignored: other tests create and drop ports in parallel; run it alone.
    #[test]
    #[ignore]
    fn tap_is_enabled_leaks_no_ports() {
        extern "C" {
            static mach_task_self_: u32;
            fn mach_port_names(task: u32, names: *mut *mut u32, names_count: *mut u32, types: *mut *mut u32, types_count: *mut u32) -> i32;
            fn vm_deallocate(task: u32, address: usize, size: usize) -> i32;
        }
        fn port_count() -> u32 {
            let (mut names, mut types, mut count, mut types_count) = (ptr::null_mut(), ptr::null_mut(), 0, 0);
            // SAFETY: the out-pointers are valid; the arrays it allocates are freed right after.
            unsafe {
                assert_eq!(
                    mach_port_names(mach_task_self_, &mut names, &mut count, &mut types, &mut types_count),
                    0
                );
                vm_deallocate(mach_task_self_, names as usize, count as usize * 4);
                vm_deallocate(mach_task_self_, types as usize, types_count as usize * 4);
            }
            count
        }
        let (core, _, _rx) = core::tests::harness();
        let ctx = Box::new(Ctx::new(core, Arc::default()));
        // SAFETY: `ctx` outlives the tap, which is invalidated below and never runs a callback.
        let tap = unsafe { create_tap(&TABLET, ptr::from_ref(&*ctx).cast_mut()) }.expect("a listen-only tap");
        assert!(CGEvent::tap_is_enabled(&tap));
        let before = port_count();
        for _ in 0..10_000 {
            assert!(CGEvent::tap_is_enabled(&tap));
        }
        let after = port_count();
        tap.invalidate();
        assert!(after <= before + 2, "ports {before} -> {after}");
    }
}
