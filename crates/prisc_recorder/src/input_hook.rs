//! Global input hooks feeding `--input-capture` (DESIGN §2).
//!
//! Keys come from `prisc_hotkeys`, Prisc's one keyboard hook (`WH_KEYBOARD_LL`
//! on Windows, a `CGEventTap` on macOS), through its observe stream: no gesture
//! is ever registered, so it passes every key, and a forwarder thread drains
//! the stream. Mouse buttons come from this module's own hook thread, because
//! `prisc_hotkeys` is keyboard only — `WH_MOUSE_LL` behind a `GetMessageW` pump
//! on Windows, a listen-only mouse `CGEventTap` on a `CFRunLoop` on macOS.
//!
//! Both halves maintain one lock-free snapshot of the current input state
//! (button bitmask + keys-down bitset, sampled once per rendered frame by the
//! input-capture tick callback) and hand every edge — key down/up, button
//! down/up — to the caller's sink as a [`RawEvent`] stamped with
//! `os_gettime_ns()`, the same monotonic clock behind
//! `obs_get_video_frame_time`, so events and frame rows share one timebase. A
//! key edge is stamped when the forwarder receives it, one queue hop after the
//! hook saw it (microseconds; well under a frame). The sink runs on the hook
//! and forwarder threads and must only do a channel send.
//!
//! Auto-repeat key-downs are suppressed here: the bitset already records the
//! key as down, so only real edges reach the channel.
//!
//! `RawEventKind::{KeyDown, KeyUp}` carry the platform's native key numbering —
//! Win32 virtual-key codes on Windows, `CGKeyCode` on macOS (`prisc_hotkeys`
//! passes its `raw_code` through unmapped). The two spaces do not agree, which
//! is what the header's `platform` field is for: consumers key their
//! interpretation of `vk` off it.
//!
//! macOS needs the Accessibility grant (System Settings › Privacy & Security),
//! the one `prisc_hotkeys` requires; without it [`InputHook::start`] fails with
//! an actionable message rather than silently recording nothing. While *secure
//! input* is active — any focused password field, and the whole time the
//! screen is locked — the OS withholds key events from every event tap in the
//! session. Mouse buttons and modifier flag changes still arrive, so a
//! recording made over a login prompt legitimately shows clicks and modifiers
//! but no keys.

// Linux has no hook (see its `imp` below), so the event model here — the
// button bits, `RawEventKind`, `InputState` — has no producer there. It stays
// compiled and unit-tested on Linux rather than being cfg'd out piecemeal,
// hence the Linux-only allow.
#![cfg_attr(target_os = "linux", allow(dead_code))]

use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::OnceLock;
use std::thread::JoinHandle;

use prisc_hotkeys::{Event, Events, Hotkeys, KeyEvent};

/// Mouse button bits of the frame-row `b` bitmask and the event-row `btn`
/// values (wire contract).
pub const BTN_LEFT: u32 = 1;
pub const BTN_RIGHT: u32 = 2;
pub const BTN_MIDDLE: u32 = 4;
pub const BTN_X1: u32 = 8;
pub const BTN_X2: u32 = 16;

/// One input edge, stamped with `os_gettime_ns()`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RawEvent {
    pub t_ns: u64,
    pub kind: RawEventKind,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RawEventKind {
    /// A real key press edge (auto-repeats never reach the channel). `ch` is
    /// the best-effort character it types under the foreground layout, absent
    /// for control/non-printable results.
    KeyDown {
        vk: u32,
        ch: Option<char>,
    },
    KeyUp {
        vk: u32,
    },
    /// `btn` is one of the `BTN_*` bits; `x`/`y` the cursor position at the
    /// click in the platform capture coordinate space.
    MouseDown {
        btn: u32,
        x: i32,
        y: i32,
    },
    MouseUp {
        btn: u32,
        x: i32,
        y: i32,
    },
}

/// Current input state, written by the hooks and snapshotted per frame.
/// Pure and lock-free: a button bitmask plus a 256-bit keys-down bitset.
#[derive(Default)]
pub struct InputState {
    buttons: AtomicU32,
    /// VK 0..255, one bit each, in four words (vk / 64, vk % 64).
    keys: [AtomicU64; 4],
}

impl InputState {
    pub fn new() -> InputState {
        InputState::default()
    }

    /// Marks `vk` down; returns whether it was already down (the auto-repeat
    /// suppression signal — a repeat sets no new bit).
    pub fn key_down(&self, vk: u32) -> bool {
        let (word, bit) = (vk as usize / 64 % 4, vk % 64);
        let prev = self.keys[word].fetch_or(1 << bit, Ordering::Relaxed);
        prev & (1 << bit) != 0
    }

    pub fn key_up(&self, vk: u32) {
        let (word, bit) = (vk as usize / 64 % 4, vk % 64);
        self.keys[word].fetch_and(!(1 << bit), Ordering::Relaxed);
    }

    pub fn button_down(&self, btn: u32) {
        self.buttons.fetch_or(btn, Ordering::Relaxed);
    }

    pub fn button_up(&self, btn: u32) {
        self.buttons.fetch_and(!btn, Ordering::Relaxed);
    }

    /// `(button bitmask, VK codes currently down — sorted ascending)`, the
    /// frame-row `b` / `k` fields.
    pub fn snapshot(&self) -> (u32, Vec<u32>) {
        let buttons = self.buttons.load(Ordering::Relaxed);
        let mut keys = Vec::new();
        for (w, word) in self.keys.iter().enumerate() {
            let mut bits = word.load(Ordering::Relaxed);
            while bits != 0 {
                let bit = bits.trailing_zeros();
                keys.push(w as u32 * 64 + bit);
                bits &= bits - 1;
            }
        }
        (buttons, keys)
    }
}

/// X-button bit for the `HIWORD(mouseData)` value of a WM_XBUTTON message
/// (1 = X1, 2 = X2; anything else is unknown and dropped).
#[cfg(windows)]
pub fn xbutton_bit(hiword: u16) -> Option<u32> {
    match hiword {
        1 => Some(BTN_X1),
        2 => Some(BTN_X2),
        _ => None,
    }
}

/// `kCGMouseEventButtonNumber` → the `BTN_*` bit. macOS numbers buttons 0..31;
/// the wire contract only names the five Windows exposes, so higher buttons
/// are dropped rather than invented.
#[cfg(target_os = "macos")]
pub fn cg_button_bit(button: i64) -> Option<u32> {
    match button {
        0 => Some(BTN_LEFT),
        1 => Some(BTN_RIGHT),
        2 => Some(BTN_MIDDLE),
        3 => Some(BTN_X1),
        4 => Some(BTN_X2),
        _ => None,
    }
}

/// Where edge events are delivered (a channel send in practice).
pub type EventSink = Box<dyn Fn(RawEvent) + Send + Sync>;

/// Hook procs get no context parameter, so the state and event sink live in a
/// process-wide static, shared by the mouse hook and the key forwarder. One
/// hook per process: the recorder creates it once and it lives until
/// `exit_process`.
struct HookCtx {
    state: InputState,
    sink: EventSink,
}

static CTX: OnceLock<HookCtx> = OnceLock::new();

fn send(kind: RawEventKind) {
    if let Some(ctx) = CTX.get() {
        let t_ns = prisc_obs::time::now_ns();
        (ctx.sink)(RawEvent { t_ns, kind });
    }
}

/// Owns both hooks. Exit paths that skip Drop are fine: the hooks die with
/// the process.
pub struct InputHook {
    _keys: KeyHook,
    _mouse: imp::MouseHook,
}

impl InputHook {
    /// Installs the mouse hook and the keyboard hook. Edge events flow into
    /// `sink`; the state snapshot is read via [`InputHook::snapshot`]. One per
    /// process (the context static is set once).
    pub fn start(sink: EventSink) -> Result<InputHook, String> {
        if CTX
            .set(HookCtx {
                state: InputState::new(),
                sink,
            })
            .is_err()
        {
            return Err("input hooks are already installed in this process".to_string());
        }
        let mouse = imp::MouseHook::start()?;
        let keys = KeyHook::start()?;
        Ok(InputHook {
            _keys: keys,
            _mouse: mouse,
        })
    }

    /// Current `(button bitmask, sorted key codes down)` — VKs on Windows,
    /// CGKeyCodes on macOS.
    pub fn snapshot(&self) -> (u32, Vec<u32>) {
        match CTX.get() {
            Some(ctx) => {
                #[cfg(target_os = "macos")]
                imp::release_stranded_keys(ctx);
                ctx.state.snapshot()
            }
            None => (0, Vec::new()),
        }
    }
}

/// The keyboard half: `prisc_hotkeys`' observe stream, drained by a forwarder
/// thread into the shared state and sink.
struct KeyHook {
    /// `Some` until Drop: stopping the hook closes its event queue, which is
    /// what ends the forwarder.
    hotkeys: Option<Hotkeys>,
    forwarder: Option<JoinHandle<()>>,
}

impl KeyHook {
    fn start() -> Result<KeyHook, String> {
        let (hotkeys, events) = Hotkeys::start().map_err(|e| match e {
            prisc_hotkeys::Error::PermissionDenied => {
                "the keyboard hook needs the Accessibility permission — grant it under System \
                 Settings › Privacy & Security, then retry"
                    .to_string()
            }
            e => e.to_string(),
        })?;
        hotkeys.set_observe(true);
        let forwarder = std::thread::Builder::new()
            .name("input-keys".to_string())
            .spawn(move || forward_keys(events))
            .map_err(|e| format!("failed to spawn the key forwarder thread: {e}"))?;
        Ok(KeyHook {
            hotkeys: Some(hotkeys),
            forwarder: Some(forwarder),
        })
    }
}

impl Drop for KeyHook {
    fn drop(&mut self) {
        drop(self.hotkeys.take());
        if let Some(forwarder) = self.forwarder.take() {
            let _ = forwarder.join();
        }
    }
}

/// Runs until the hook stops and its queue is drained.
fn forward_keys(events: Events) {
    while let Some(event) = events.recv() {
        match event {
            Event::Key(key) => on_key(key),
            Event::Stopped(reason) => {
                eprintln!("Warning: the keyboard hook stopped ({reason}); keys are no longer recorded");
            }
            // No gesture is registered and no recording armed: nothing else arrives.
            _ => {}
        }
    }
}

fn on_key(key: KeyEvent) {
    let Some(ctx) = CTX.get() else { return };
    let vk = key.raw_code;
    if key.down {
        // A repeat finds the bit already set — state only.
        if !ctx.state.key_down(vk) {
            send(RawEventKind::KeyDown { vk, ch: key.ch });
        }
    } else {
        ctx.state.key_up(vk);
        send(RawEventKind::KeyUp { vk });
    }
}

#[cfg(windows)]
mod imp {
    use std::sync::mpsc;

    use windows::Win32::Foundation::{LPARAM, LRESULT, WPARAM};
    use windows::Win32::System::Threading::GetCurrentThreadId;
    use windows::Win32::UI::WindowsAndMessaging::{
        CallNextHookEx, DispatchMessageW, GetMessageW, PeekMessageW, PostThreadMessageW,
        SetWindowsHookExW, UnhookWindowsHookEx, MSG, MSLLHOOKSTRUCT, PM_NOREMOVE, WH_MOUSE_LL,
        WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MBUTTONDOWN, WM_MBUTTONUP, WM_QUIT, WM_RBUTTONDOWN,
        WM_RBUTTONUP, WM_XBUTTONDOWN, WM_XBUTTONUP,
    };

    use super::{send, xbutton_bit, RawEventKind, BTN_LEFT, BTN_MIDDLE, BTN_RIGHT, CTX};

    unsafe extern "system" fn mouse_proc(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
        if code >= 0 {
            if let Some(ctx) = CTX.get() {
                let ms = &*(lparam.0 as *const MSLLHOOKSTRUCT);
                // Screen coords; physical px (the process is per-monitor-v2
                // DPI aware) — the same space as frame-row x/y.
                let (x, y) = (ms.pt.x, ms.pt.y);
                let (btn, down) = match wparam.0 as u32 {
                    WM_LBUTTONDOWN => (Some(BTN_LEFT), true),
                    WM_LBUTTONUP => (Some(BTN_LEFT), false),
                    WM_RBUTTONDOWN => (Some(BTN_RIGHT), true),
                    WM_RBUTTONUP => (Some(BTN_RIGHT), false),
                    WM_MBUTTONDOWN => (Some(BTN_MIDDLE), true),
                    WM_MBUTTONUP => (Some(BTN_MIDDLE), false),
                    WM_XBUTTONDOWN => (xbutton_bit((ms.mouseData >> 16) as u16), true),
                    WM_XBUTTONUP => (xbutton_bit((ms.mouseData >> 16) as u16), false),
                    _ => (None, false),
                };
                if let Some(btn) = btn {
                    if down {
                        ctx.state.button_down(btn);
                        send(RawEventKind::MouseDown { btn, x, y });
                    } else {
                        ctx.state.button_up(btn);
                        send(RawEventKind::MouseUp { btn, x, y });
                    }
                }
            }
        }
        CallNextHookEx(None, code, wparam, lparam)
    }

    /// Owns the mouse hook thread. Dropping posts `WM_QUIT` to its pump and
    /// joins — the thread unhooks on the way out.
    pub struct MouseHook {
        thread_id: u32,
        thread: Option<std::thread::JoinHandle<()>>,
    }

    impl MouseHook {
        /// Installs the hook on a fresh pump thread.
        pub fn start() -> Result<MouseHook, String> {
            let (ready_tx, ready_rx) = mpsc::channel::<Result<u32, String>>();
            let thread = std::thread::Builder::new()
                .name("input-hook".to_string())
                .spawn(move || {
                    unsafe {
                        // Low-level hooks deliver through the installing
                        // thread's message queue, so install + pump here.
                        let Ok(hook) = SetWindowsHookExW(WH_MOUSE_LL, Some(mouse_proc), None, 0)
                        else {
                            let _ = ready_tx
                                .send(Err("SetWindowsHookExW failed (WH_MOUSE_LL)".to_string()));
                            return;
                        };

                        // Force the message queue into existence so the
                        // shutdown PostThreadMessageW can never race its
                        // creation, then hand the pump's thread id back.
                        let mut msg = MSG::default();
                        let _ = PeekMessageW(&mut msg, None, 0, 0, PM_NOREMOVE);
                        let _ = ready_tx.send(Ok(GetCurrentThreadId()));

                        // Tri-state BOOL: 0 is WM_QUIT, -1 an error — both
                        // end the pump.
                        while GetMessageW(&mut msg, None, 0, 0).0 > 0 {
                            DispatchMessageW(&msg);
                        }

                        let _ = UnhookWindowsHookEx(hook);
                    }
                })
                .map_err(|e| format!("failed to spawn the input-hook thread: {e}"))?;

            match ready_rx.recv() {
                Ok(Ok(thread_id)) => Ok(MouseHook {
                    thread_id,
                    thread: Some(thread),
                }),
                Ok(Err(e)) => {
                    let _ = thread.join();
                    Err(e)
                }
                Err(_) => Err("the input-hook thread died during startup".to_string()),
            }
        }
    }

    impl Drop for MouseHook {
        fn drop(&mut self) {
            let _ = unsafe { PostThreadMessageW(self.thread_id, WM_QUIT, WPARAM(0), LPARAM(0)) };
            if let Some(thread) = self.thread.take() {
                let _ = thread.join();
            }
        }
    }
}

#[cfg(target_os = "macos")]
mod imp {
    use std::ffi::c_void;
    use std::ptr::NonNull;
    use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
    use std::sync::mpsc;
    use std::sync::Arc;

    use objc2_core_foundation::{
        kCFRunLoopCommonModes, kCFRunLoopDefaultMode, CFMachPort, CFRetained, CFRunLoop,
    };
    use objc2_core_graphics::{
        CGEvent, CGEventField, CGEventMask, CGEventTapLocation, CGEventTapOptions,
        CGEventTapPlacement, CGEventTapProxy, CGEventType,
    };

    use super::{cg_button_bit, send, HookCtx, RawEventKind};

    const fn ev(t: CGEventType) -> CGEventMask {
        1 << t.0
    }

    const EVENT_MASK: CGEventMask = ev(CGEventType::LeftMouseDown)
        | ev(CGEventType::LeftMouseUp)
        | ev(CGEventType::RightMouseDown)
        | ev(CGEventType::RightMouseUp)
        | ev(CGEventType::OtherMouseDown)
        | ev(CGEventType::OtherMouseUp);

    /// How long the tap thread blocks per run-loop turn. Only the shutdown
    /// latency floor — events wake the loop immediately.
    const RUN_LOOP_TURN_SECS: f64 = 0.1;

    /// The tap's `CFMachPort`, published once `CGEventTapCreate` returns so the
    /// callback can re-arm itself. 0 until then.
    static TAP: AtomicUsize = AtomicUsize::new(0);

    /// Drops keys the OS no longer reports as held, emitting the up edge the
    /// keyboard tap never got.
    ///
    /// A key pressed just before secure input engages — the screen locking
    /// mid-keystroke is the easy way to reproduce it — never delivers its up
    /// event to any tap, so without this it reads as held for the rest of the
    /// recording and every later frame row carries a phantom key.
    /// `prisc_platform::input::is_key_down` (`CGEventSourceKeyState`, combined session state)
    /// bypasses the tap entirely and can arbitrate.
    ///
    /// Keys only: secure input withholds key events specifically, while mouse
    /// events keep flowing, so buttons have no equivalent stranding path.
    /// Called once per rendered frame, and typically iterates nothing.
    pub(super) fn release_stranded_keys(ctx: &HookCtx) {
        let (_, keys) = ctx.state.snapshot();
        for vk in keys {
            // Reads true key state without a tap — so it stays truthful even
            // while secure input is withholding events. No permission needed.
            if prisc_platform::input::is_key_down(vk) == Some(false) {
                ctx.state.key_up(vk);
                send(RawEventKind::KeyUp { vk });
            }
        }
    }

    unsafe extern "C-unwind" fn tap_callback(
        _proxy: CGEventTapProxy,
        etype: CGEventType,
        event: NonNull<CGEvent>,
        _user_info: *mut c_void,
    ) -> *mut CGEvent {
        // Listen-only: the event is passed through untouched.
        let pass = event.as_ptr();
        let Some(ctx) = super::CTX.get() else { return pass };
        let event = event.as_ref();

        // A slow callback — or a burst the OS decides to protect itself from —
        // disables the tap, and it stays dead until explicitly re-armed. Not
        // handling this is how event taps silently stop working mid-session.
        if etype == CGEventType::TapDisabledByTimeout
            || etype == CGEventType::TapDisabledByUserInput
        {
            let tap = TAP.load(Ordering::Acquire) as *const CFMachPort;
            if !tap.is_null() {
                CGEvent::tap_enable(&*tap, true);
            }
            return pass;
        }

        let down = match etype {
            CGEventType::LeftMouseDown
            | CGEventType::RightMouseDown
            | CGEventType::OtherMouseDown => true,
            CGEventType::LeftMouseUp | CGEventType::RightMouseUp | CGEventType::OtherMouseUp => {
                false
            }
            _ => return pass,
        };
        let n = CGEvent::integer_value_field(Some(event), CGEventField::MouseEventButtonNumber);
        if let Some(btn) = cg_button_bit(n) {
            // Global display points, top-left origin — the same space as
            // `CGDisplayBounds`, hence as frame-row x/y (§1.1).
            let p = CGEvent::location(Some(event));
            let (x, y) = (p.x as i32, p.y as i32);
            if down {
                ctx.state.button_down(btn);
                send(RawEventKind::MouseDown { btn, x, y });
            } else {
                ctx.state.button_up(btn);
                send(RawEventKind::MouseUp { btn, x, y });
            }
        }

        pass
    }

    /// Owns the tap thread. Dropping asks its run loop to stop and joins — the
    /// thread releases the tap on the way out.
    pub struct MouseHook {
        /// The tap thread's `CFRunLoop`, retained (a raw +1 from
        /// `CFRetained::into_raw`) for the lifetime of this handle so the
        /// wake-up in Drop cannot race the thread's teardown.
        runloop: usize,
        stop: Arc<AtomicBool>,
        thread: Option<std::thread::JoinHandle<()>>,
    }

    impl MouseHook {
        /// Installs a listen-only mouse `CGEventTap` on a fresh run-loop
        /// thread.
        pub fn start() -> Result<MouseHook, String> {
            let stop = Arc::new(AtomicBool::new(false));
            let thread_stop = stop.clone();
            let (ready_tx, ready_rx) = mpsc::channel::<Result<usize, String>>();
            let thread = std::thread::Builder::new()
                .name("input-hook".to_string())
                .spawn(move || unsafe {
                    // Taps deliver into the run loop of the thread that adds
                    // the source, so create + pump here. No main-thread
                    // requirement — only a run loop.
                    let Some(tap) = CGEvent::tap_create(
                        CGEventTapLocation::HIDEventTap,
                        CGEventTapPlacement::HeadInsertEventTap,
                        // Observe only — the tap must never be able to modify
                        // or swallow input.
                        CGEventTapOptions::ListenOnly,
                        EVENT_MASK,
                        Some(tap_callback),
                        std::ptr::null_mut(),
                    ) else {
                        let _ = ready_tx
                            .send(Err("CGEventTapCreate failed for the mouse tap".to_string()));
                        return;
                    };
                    // Publish before the source goes live: the first event can
                    // arrive immediately, and the callback needs the port to
                    // re-arm itself.
                    TAP.store(CFRetained::as_ptr(&tap).as_ptr() as usize, Ordering::Release);

                    // `tap` is CFRetained: the early-return drop here (and the
                    // fall-off-the-end drop below) replaces the manual
                    // CFRelease calls of the hand-rolled version.
                    let Some(source) = CFMachPort::new_run_loop_source(None, Some(&tap), 0) else {
                        let _ = ready_tx.send(Err(
                            "CFMachPortCreateRunLoopSource failed for the event tap".to_string(),
                        ));
                        return;
                    };

                    let Some(runloop) = CFRunLoop::current() else {
                        let _ = ready_tx.send(Err(
                            "CFRunLoopGetCurrent returned no run loop for the tap thread"
                                .to_string(),
                        ));
                        return;
                    };
                    runloop.add_source(Some(&source), kCFRunLoopCommonModes);
                    CGEvent::tap_enable(&tap, true);

                    // Ownership handed to the handle: Drop wakes this run loop
                    // from another thread (CFRunLoop is one of the few
                    // thread-safe CF types) and releases it after the join.
                    // `into_raw` keeps the +1 alive across the channel —
                    // CFRetained is not Send, so it crosses as a raw pointer.
                    let _ = ready_tx.send(Ok(CFRetained::into_raw(runloop).as_ptr() as usize));

                    // Bounded turns rather than CFRunLoopRun + CFRunLoopStop:
                    // a Drop that lands before the loop starts would make a
                    // bare stop a no-op and hang the join forever.
                    while !thread_stop.load(Ordering::Acquire) {
                        CFRunLoop::run_in_mode(kCFRunLoopDefaultMode, RUN_LOOP_TURN_SECS, false);
                    }

                    CGEvent::tap_enable(&tap, false);
                    // `source` and `tap` drop (release) here.
                })
                .map_err(|e| format!("failed to spawn the input-hook thread: {e}"))?;

            match ready_rx.recv() {
                Ok(Ok(runloop)) => Ok(MouseHook {
                    runloop,
                    stop,
                    thread: Some(thread),
                }),
                Ok(Err(e)) => {
                    let _ = thread.join();
                    Err(e)
                }
                Err(_) => Err("the input-hook thread died during startup".to_string()),
            }
        }
    }

    impl Drop for MouseHook {
        fn drop(&mut self) {
            self.stop.store(true, Ordering::Release);
            // Cut the current turn short; if the loop is not running yet the
            // stop flag still ends it one turn later.
            unsafe { &*(self.runloop as *const CFRunLoop) }.stop();
            if let Some(thread) = self.thread.take() {
                let _ = thread.join();
            }
            // Rebuild the CFRetained to drop it — the release balancing the
            // +1 `into_raw` handed over at startup.
            drop(unsafe {
                CFRetained::from_raw(NonNull::new_unchecked(self.runloop as *mut CFRunLoop))
            });
        }
    }
}

/// Linux: no global input hook. Wayland gives a non-focused client no access
/// to other clients' input by design (and `prisc_hotkeys` has no Linux backend
/// either), and the input-capture sidecar that consumes this hook is rejected
/// by `Cli::validate` on Linux, so `start` always fails with an explanation
/// rather than recording nothing.
///
/// The hook type is uninhabited: since `start` can never produce one, no
/// [`InputHook`] can exist on Linux either.
#[cfg(target_os = "linux")]
mod imp {
    pub enum MouseHook {}

    impl MouseHook {
        pub fn start() -> Result<MouseHook, String> {
            Err("global input capture is not supported on Linux".to_string())
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn button_bits_are_the_wire_contract_values() {
        assert_eq!(
            [BTN_LEFT, BTN_RIGHT, BTN_MIDDLE, BTN_X1, BTN_X2],
            [1, 2, 4, 8, 16]
        );
    }

    #[test]
    fn button_mask_accumulates_and_clears() {
        let s = InputState::new();
        s.button_down(BTN_LEFT);
        s.button_down(BTN_X2);
        assert_eq!(s.snapshot().0, BTN_LEFT | BTN_X2);
        s.button_up(BTN_LEFT);
        assert_eq!(s.snapshot().0, BTN_X2);
        s.button_up(BTN_X2);
        assert_eq!(s.snapshot().0, 0);
        // Releasing an unpressed button is a no-op, not a corruption.
        s.button_up(BTN_MIDDLE);
        assert_eq!(s.snapshot().0, 0);
    }

    #[test]
    fn key_down_reports_repeats() {
        let s = InputState::new();
        assert!(!s.key_down(75)); // first edge
        assert!(s.key_down(75)); // auto-repeat — suppressed by the caller
        s.key_up(75);
        assert!(!s.key_down(75)); // a fresh press is an edge again
    }

    #[test]
    fn key_snapshot_is_sorted_across_words() {
        let s = InputState::new();
        // One key per bitset word, inserted out of order.
        for vk in [200, 17, 91, 160] {
            s.key_down(vk);
        }
        assert_eq!(s.snapshot().1, vec![17, 91, 160, 200]);
        s.key_up(91);
        assert_eq!(s.snapshot().1, vec![17, 160, 200]);
    }

    #[test]
    fn key_bits_do_not_alias() {
        // VKs 64 apart land in different words, same bit index.
        let s = InputState::new();
        s.key_down(1);
        s.key_down(65);
        s.key_up(1);
        assert_eq!(s.snapshot().1, vec![65]);
    }

    #[cfg(windows)]
    #[test]
    fn xbutton_hiword_maps_to_the_bitmask() {
        assert_eq!(xbutton_bit(1), Some(BTN_X1));
        assert_eq!(xbutton_bit(2), Some(BTN_X2));
        assert_eq!(xbutton_bit(0), None);
        assert_eq!(xbutton_bit(3), None);
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn cg_button_numbers_map_to_the_bitmask() {
        assert_eq!(cg_button_bit(0), Some(BTN_LEFT));
        assert_eq!(cg_button_bit(1), Some(BTN_RIGHT));
        assert_eq!(cg_button_bit(2), Some(BTN_MIDDLE));
        assert_eq!(cg_button_bit(3), Some(BTN_X1));
        assert_eq!(cg_button_bit(4), Some(BTN_X2));
        // macOS numbers up to 31; the wire contract names only five.
        assert_eq!(cg_button_bit(5), None);
        assert_eq!(cg_button_bit(31), None);
        assert_eq!(cg_button_bit(-1), None);
    }
}
