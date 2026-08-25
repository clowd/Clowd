//! Global input hooks feeding `--input-capture` (DESIGN §2).
//!
//! A dedicated thread installs the OS-wide listener and runs the event loop it
//! requires — `WH_KEYBOARD_LL` + `WH_MOUSE_LL` behind a `GetMessageW` pump on
//! Windows, a listen-only `CGEventTap` on a `CFRunLoop` on macOS. The callbacks
//! maintain a lock-free snapshot of the current input state (button bitmask +
//! keys-down bitset, sampled once per rendered frame by the input-capture tick
//! callback) and hand every edge — key down/up, button down/up — to the
//! caller's sink as a [`RawEvent`] stamped with `os_gettime_ns()`, the same
//! monotonic clock behind `obs_get_video_frame_time`, so events and frame rows
//! share one timebase. The sink runs on the hook thread and must only do a
//! channel send.
//!
//! Auto-repeat key-downs are suppressed at the source: the bitset already
//! records the key as down, so only real edges reach the channel.
//!
//! `RawEventKind::{KeyDown, KeyUp}` carry the platform's native key numbering —
//! Win32 virtual-key codes on Windows, `CGKeyCode` on macOS. The two spaces do
//! not agree, which is what the header's `platform` field is for: consumers key
//! their interpretation of `vk` off it.
//!
//! macOS needs the Input Monitoring grant (System Settings › Privacy &
//! Security). Without it `CGEventTapCreate` returns null and [`InputHook::start`]
//! fails with an actionable message rather than silently recording nothing.
//!
//! Two macOS behaviors are worth knowing about because they look like bugs:
//! while *secure input* is active — any focused password field, and the whole
//! time the screen is locked — the OS withholds key events from every event tap
//! in the session. Mouse buttons and modifier flag changes still arrive, so a
//! recording made over a login prompt legitimately shows clicks and modifiers
//! but no keys. And a tap the OS has disabled (`kCGEventTapDisabledBy*`) stays
//! dead until re-armed, which the tap callback does on the spot.

use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};

/// Mouse button bits of the frame-row `b` bitmask and the event-row `btn`
/// values (wire contract).
pub const BTN_LEFT: u32 = 1;
pub const BTN_RIGHT: u32 = 2;
pub const BTN_MIDDLE: u32 = 4;
pub const BTN_X1: u32 = 8;
pub const BTN_X2: u32 = 16;

/// One input edge from the hook thread, stamped with `os_gettime_ns()`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RawEvent {
    pub t_ns: u64,
    pub kind: RawEventKind,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RawEventKind {
    /// A real key press edge (auto-repeats never reach the channel). `ch` is
    /// the best-effort translated character (`ToUnicodeEx`), absent for
    /// control/non-printable results.
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

/// Current input state, written by the hook procs and snapshotted per frame.
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

    /// Whether `vk` is currently recorded as down. Used by the macOS
    /// flags-changed path, which has to decide per key whether a shared
    /// modifier bit clearing is a release it still owes an event for.
    pub fn is_down(&self, vk: u32) -> bool {
        let (word, bit) = (vk as usize / 64 % 4, vk % 64);
        self.keys[word].load(Ordering::Relaxed) & (1 << bit) != 0
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

// ---------------------------------------------------------------------------
// macOS event decoding (pure; the tap callback in `imp` is the only caller)
// ---------------------------------------------------------------------------

/// `CGEventFlags` bits for the modifier keys we track, as the bare `u64`s the
/// `MODIFIER_KEYS` mask arithmetic runs on.
#[cfg(target_os = "macos")]
mod cg_flags {
    use objc2_core_graphics::CGEventFlags;

    pub const ALPHA_SHIFT: u64 = CGEventFlags::MaskAlphaShift.bits(); // caps lock
    pub const SHIFT: u64 = CGEventFlags::MaskShift.bits();
    pub const CONTROL: u64 = CGEventFlags::MaskControl.bits();
    pub const ALTERNATE: u64 = CGEventFlags::MaskAlternate.bits(); // option
    pub const COMMAND: u64 = CGEventFlags::MaskCommand.bits();
    pub const SECONDARY_FN: u64 = CGEventFlags::MaskSecondaryFn.bits();
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

/// The modifier `CGKeyCode`s, each paired with the `CGEventFlags` bit it
/// drives. Left/right twins deliberately share a bit — that is how the OS
/// reports them, and [`modifier_twins`] exists to undo the ambiguity.
#[cfg(target_os = "macos")]
pub const MODIFIER_KEYS: [(u32, u64); 10] = [
    (54, cg_flags::COMMAND),      // right command
    (55, cg_flags::COMMAND),      // left command
    (56, cg_flags::SHIFT),        // left shift
    (60, cg_flags::SHIFT),        // right shift
    (58, cg_flags::ALTERNATE),    // left option
    (61, cg_flags::ALTERNATE),    // right option
    (59, cg_flags::CONTROL),      // left control
    (62, cg_flags::CONTROL),      // right control
    (57, cg_flags::ALPHA_SHIFT),  // caps lock
    (63, cg_flags::SECONDARY_FN), // fn
];

/// The flag bit a modifier `CGKeyCode` drives, or `None` for a normal key.
#[cfg(target_os = "macos")]
pub fn modifier_mask(keycode: u32) -> Option<u64> {
    MODIFIER_KEYS
        .iter()
        .find(|(kc, _)| *kc == keycode)
        .map(|(_, mask)| *mask)
}

/// Every keycode sharing `mask`. `kCGEventFlagsChanged` reports the new flag
/// set, not an up/down, so a bit that has gone clear means *both* twins are
/// now up — releasing left shift while right shift is held reports the bit
/// still set, and only the final release clears it. Sweeping the pair on the
/// clearing edge is what keeps a twin from being stranded down forever.
#[cfg(target_os = "macos")]
pub fn modifier_twins(mask: u64) -> impl Iterator<Item = u32> {
    MODIFIER_KEYS
        .iter()
        .filter(move |(_, m)| *m == mask)
        .map(|(kc, _)| *kc)
}

pub use imp::InputHook;

/// Where the hook thread delivers edge events (a channel send in practice).
pub type EventSink = Box<dyn Fn(RawEvent) + Send + Sync>;

#[cfg(windows)]
mod imp {
    use std::sync::mpsc;
    use std::sync::OnceLock;

    use windows::Win32::Foundation::{LPARAM, LRESULT, WPARAM};
    use windows::Win32::System::Threading::GetCurrentThreadId;
    use windows::Win32::UI::Input::KeyboardAndMouse::{
        GetAsyncKeyState, GetKeyState, GetKeyboardLayout, ToUnicodeEx, VK_CAPITAL, VK_CONTROL,
        VK_MENU, VK_SHIFT,
    };
    use windows::Win32::UI::WindowsAndMessaging::{
        CallNextHookEx, DispatchMessageW, GetForegroundWindow, GetMessageW,
        GetWindowThreadProcessId, PeekMessageW, PostThreadMessageW, SetWindowsHookExW,
        UnhookWindowsHookEx, KBDLLHOOKSTRUCT, MSG, MSLLHOOKSTRUCT, PM_NOREMOVE, WH_KEYBOARD_LL,
        WH_MOUSE_LL, WM_KEYDOWN, WM_KEYUP, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MBUTTONDOWN,
        WM_MBUTTONUP, WM_QUIT, WM_RBUTTONDOWN, WM_RBUTTONUP, WM_SYSKEYDOWN, WM_SYSKEYUP,
        WM_XBUTTONDOWN, WM_XBUTTONUP,
    };

    use super::{
        xbutton_bit, EventSink, InputState, RawEvent, RawEventKind, BTN_LEFT, BTN_MIDDLE, BTN_RIGHT,
    };

    /// Hook procs get no context parameter, so the state and event sink live
    /// in a process-wide static. One hook per process: the recorder creates it
    /// once and it lives until `exit_process`.
    struct HookCtx {
        state: InputState,
        sink: EventSink,
    }

    static CTX: OnceLock<HookCtx> = OnceLock::new();

    fn send(kind: RawEventKind) {
        if let Some(ctx) = CTX.get() {
            let t_ns = unsafe { obs_sys::os_gettime_ns() };
            (ctx.sink)(RawEvent { t_ns, kind });
        }
    }

    /// Best-effort character for a key press (DESIGN §1: dead-key
    /// imperfection accepted). The keyboard state is rebuilt from the async
    /// modifier state — the hook thread's own input queue never sees the
    /// keystrokes, so `GetKeyboardState` would be stale. Flag 0x4 keeps
    /// `ToUnicodeEx` from clobbering the foreground app's dead-key state
    /// (Win10 1607+; older systems accept the bit and ignore it).
    fn translate_char(vk: u32, scan_code: u32) -> Option<char> {
        let mut key_state = [0u8; 256];
        for m in [VK_SHIFT, VK_CONTROL, VK_MENU] {
            if unsafe { GetAsyncKeyState(m.0 as i32) } as u16 & 0x8000 != 0 {
                key_state[m.0 as usize] = 0x80;
            }
        }
        // Toggle state (low bit) — capitalization.
        if unsafe { GetKeyState(VK_CAPITAL.0 as i32) } & 1 != 0 {
            key_state[VK_CAPITAL.0 as usize] = 1;
        }
        let layout = unsafe {
            let fg = GetForegroundWindow();
            let tid = GetWindowThreadProcessId(fg, None);
            GetKeyboardLayout(tid)
        };
        // The crate derives cchBuff from the slice length.
        let mut buf = [0u16; 8];
        let n = unsafe { ToUnicodeEx(vk, scan_code, &key_state, &mut buf, 0x4, Some(layout)) };
        if n < 1 {
            return None;
        }
        char::decode_utf16(buf[..n as usize].iter().copied())
            .next()
            .and_then(Result::ok)
            .filter(|c| !c.is_control())
    }

    unsafe extern "system" fn keyboard_proc(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
        if code >= 0 {
            if let Some(ctx) = CTX.get() {
                let kb = &*(lparam.0 as *const KBDLLHOOKSTRUCT);
                let vk = kb.vkCode;
                match wparam.0 as u32 {
                    WM_KEYDOWN | WM_SYSKEYDOWN => {
                        // A repeat finds the bit already set — state only.
                        if !ctx.state.key_down(vk) {
                            send(RawEventKind::KeyDown {
                                vk,
                                ch: translate_char(vk, kb.scanCode),
                            });
                        }
                    }
                    WM_KEYUP | WM_SYSKEYUP => {
                        ctx.state.key_up(vk);
                        send(RawEventKind::KeyUp { vk });
                    }
                    _ => {}
                }
            }
        }
        CallNextHookEx(None, code, wparam, lparam)
    }

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

    /// Owns the hook thread. Dropping posts `WM_QUIT` to its pump and joins —
    /// the thread unhooks on the way out. Exit paths that skip Drop are fine:
    /// the hooks die with the process.
    pub struct InputHook {
        thread_id: u32,
        thread: Option<std::thread::JoinHandle<()>>,
    }

    impl InputHook {
        /// Installs the hooks on a fresh pump thread. Edge events flow into
        /// `sink`; the state snapshot is read via [`InputHook::snapshot`].
        /// One per process (the context static is set once).
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

            let (ready_tx, ready_rx) = mpsc::channel::<Result<u32, String>>();
            let thread = std::thread::Builder::new()
                .name("input-hook".to_string())
                .spawn(move || {
                    unsafe {
                        // Low-level hooks deliver through the installing
                        // thread's message queue, so install + pump here.
                        let kb = SetWindowsHookExW(WH_KEYBOARD_LL, Some(keyboard_proc), None, 0);
                        let ms = SetWindowsHookExW(WH_MOUSE_LL, Some(mouse_proc), None, 0);
                        let (kb, ms) = match (kb, ms) {
                            (Ok(kb), Ok(ms)) => (kb, ms),
                            // Unhook whichever half made it in.
                            (kb, ms) => {
                                for h in [kb, ms].into_iter().flatten() {
                                    let _ = UnhookWindowsHookEx(h);
                                }
                                let _ = ready_tx
                                    .send(Err("SetWindowsHookExW failed (LL hooks)".to_string()));
                                return;
                            }
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

                        let _ = UnhookWindowsHookEx(kb);
                        let _ = UnhookWindowsHookEx(ms);
                    }
                })
                .map_err(|e| format!("failed to spawn the input-hook thread: {e}"))?;

            match ready_rx.recv() {
                Ok(Ok(thread_id)) => Ok(InputHook {
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

        /// Current `(button bitmask, sorted VK codes down)`.
        pub fn snapshot(&self) -> (u32, Vec<u32>) {
            match CTX.get() {
                Some(ctx) => ctx.state.snapshot(),
                None => (0, Vec::new()),
            }
        }
    }

    impl Drop for InputHook {
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
    use std::ffi::{c_ulong, c_void};
    use std::ptr::NonNull;
    use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
    use std::sync::mpsc;
    use std::sync::{Arc, OnceLock};

    use objc2_core_foundation::{
        kCFRunLoopCommonModes, kCFRunLoopDefaultMode, CFMachPort, CFRetained, CFRunLoop,
    };
    use objc2_core_graphics::{
        CGEvent, CGEventField, CGEventMask, CGEventSource, CGEventSourceStateID,
        CGEventTapLocation, CGEventTapOptions, CGEventTapPlacement, CGEventTapProxy, CGEventType,
    };

    use super::{
        cg_button_bit, modifier_mask, modifier_twins, EventSink, InputState, RawEvent, RawEventKind,
    };

    const fn ev(t: CGEventType) -> CGEventMask {
        1 << t.0
    }

    const EVENT_MASK: CGEventMask = ev(CGEventType::KeyDown)
        | ev(CGEventType::KeyUp)
        | ev(CGEventType::FlagsChanged)
        | ev(CGEventType::LeftMouseDown)
        | ev(CGEventType::LeftMouseUp)
        | ev(CGEventType::RightMouseDown)
        | ev(CGEventType::RightMouseUp)
        | ev(CGEventType::OtherMouseDown)
        | ev(CGEventType::OtherMouseUp);

    /// How long the tap thread blocks per run-loop turn. Only the shutdown
    /// latency floor — events wake the loop immediately.
    const RUN_LOOP_TURN_SECS: f64 = 0.1;

    /// `kCGEventSourceStateCombinedSessionState` — the same state id
    /// `platform::macos::get_mouse_info` samples button state from.
    const COMBINED_SESSION_STATE: CGEventSourceStateID = CGEventSourceStateID::CombinedSessionState;

    /// The tap callback gets a `user_info` pointer, but the sink must outlive
    /// any in-flight callback, so the context lives in a process-wide static
    /// exactly as on Windows. One hook per process.
    struct HookCtx {
        state: InputState,
        sink: EventSink,
        /// The tap's `CFMachPort`, published once `CGEventTapCreate` returns so
        /// the callback can re-arm itself. 0 until then.
        tap: AtomicUsize,
    }

    static CTX: OnceLock<HookCtx> = OnceLock::new();

    fn send(kind: RawEventKind) {
        if let Some(ctx) = CTX.get() {
            let t_ns = unsafe { obs_sys::os_gettime_ns() };
            (ctx.sink)(RawEvent { t_ns, kind });
        }
    }

    /// Best-effort character for a key press (DESIGN §1: dead-key imperfection
    /// accepted). `CGEventKeyboardGetUnicodeString` applies the active keyboard
    /// layout and the event's own modifier flags, so unlike the Windows path
    /// there is no keyboard state to rebuild by hand.
    fn translate_char(event: &CGEvent) -> Option<char> {
        let mut buf = [0u16; 8];
        let mut len: c_ulong = 0;
        unsafe {
            CGEvent::keyboard_get_unicode_string(
                Some(event),
                buf.len() as c_ulong,
                &mut len,
                buf.as_mut_ptr(),
            )
        };
        let len = (len as usize).min(buf.len());
        if len == 0 {
            return None;
        }
        char::decode_utf16(buf[..len].iter().copied())
            .next()
            .and_then(Result::ok)
            .filter(|c| !c.is_control())
    }

    /// Drops keys the OS no longer reports as held, emitting the up edge the
    /// tap never got.
    ///
    /// A key pressed just before secure input engages — the screen locking
    /// mid-keystroke is the easy way to reproduce it — never delivers its up
    /// event to any tap, so without this it reads as held for the rest of the
    /// recording and every later frame row carries a phantom key.
    /// `CGEventSourceKeyState` bypasses the tap entirely and can arbitrate.
    ///
    /// Keys only: secure input withholds key events specifically, while mouse
    /// events keep flowing, so buttons have no equivalent stranding path.
    /// Called once per rendered frame, and typically iterates nothing.
    fn release_stranded_keys(ctx: &HookCtx) {
        let (_, keys) = ctx.state.snapshot();
        for vk in keys {
            // Reads true key state without a tap — so it stays truthful even
            // while secure input is withholding events. No permission needed.
            if !CGEventSource::key_state(COMBINED_SESSION_STATE, vk as u16) {
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
        let Some(ctx) = CTX.get() else { return pass };
        let event = event.as_ref();

        // A slow callback — or a burst the OS decides to protect itself from —
        // disables the tap, and it stays dead until explicitly re-armed. Not
        // handling this is how event taps silently stop working mid-session.
        if etype == CGEventType::TapDisabledByTimeout
            || etype == CGEventType::TapDisabledByUserInput
        {
            let tap = ctx.tap.load(Ordering::Acquire) as *const CFMachPort;
            if !tap.is_null() {
                CGEvent::tap_enable(&*tap, true);
            }
            return pass;
        }

        match etype {
            CGEventType::KeyDown => {
                let vk =
                    CGEvent::integer_value_field(Some(event), CGEventField::KeyboardEventKeycode)
                        as u32;
                // A repeat finds the bit already set — state only.
                if !ctx.state.key_down(vk) {
                    send(RawEventKind::KeyDown {
                        vk,
                        ch: translate_char(event),
                    });
                }
            }
            CGEventType::KeyUp => {
                let vk =
                    CGEvent::integer_value_field(Some(event), CGEventField::KeyboardEventKeycode)
                        as u32;
                ctx.state.key_up(vk);
                send(RawEventKind::KeyUp { vk });
            }
            // Modifiers never produce key up/down; they report the new flag set.
            // Caps lock therefore reads as held for as long as the lock is on,
            // which is the state a consumer actually wants to render.
            CGEventType::FlagsChanged => {
                let vk =
                    CGEvent::integer_value_field(Some(event), CGEventField::KeyboardEventKeycode)
                        as u32;
                if let Some(mask) = modifier_mask(vk) {
                    if CGEvent::flags(Some(event)).bits() & mask != 0 {
                        if !ctx.state.key_down(vk) {
                            send(RawEventKind::KeyDown { vk, ch: None });
                        }
                    } else {
                        for kc in modifier_twins(mask) {
                            if ctx.state.is_down(kc) {
                                ctx.state.key_up(kc);
                                send(RawEventKind::KeyUp { vk: kc });
                            }
                        }
                    }
                }
            }
            _ => {
                let down = match etype {
                    CGEventType::LeftMouseDown
                    | CGEventType::RightMouseDown
                    | CGEventType::OtherMouseDown => true,
                    CGEventType::LeftMouseUp
                    | CGEventType::RightMouseUp
                    | CGEventType::OtherMouseUp => false,
                    _ => return pass,
                };
                let n =
                    CGEvent::integer_value_field(Some(event), CGEventField::MouseEventButtonNumber);
                if let Some(btn) = cg_button_bit(n) {
                    // Global display points, top-left origin — the same space
                    // as `CGDisplayBounds`, hence as frame-row x/y (§1.1).
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
            }
        }

        pass
    }

    /// Owns the tap thread. Dropping asks its run loop to stop and joins — the
    /// thread releases the tap on the way out. Exit paths that skip Drop are
    /// fine: the tap dies with the process.
    pub struct InputHook {
        /// The tap thread's `CFRunLoop`, retained (a raw +1 from
        /// `CFRetained::into_raw`) for the lifetime of this handle so the
        /// wake-up in Drop cannot race the thread's teardown.
        runloop: usize,
        stop: Arc<AtomicBool>,
        thread: Option<std::thread::JoinHandle<()>>,
    }

    impl InputHook {
        /// Installs a listen-only `CGEventTap` on a fresh run-loop thread. Edge
        /// events flow into `sink`; the state snapshot is read via
        /// [`InputHook::snapshot`]. One per process (the context static is set
        /// once).
        pub fn start(sink: EventSink) -> Result<InputHook, String> {
            if CTX
                .set(HookCtx {
                    state: InputState::new(),
                    sink,
                    tap: AtomicUsize::new(0),
                })
                .is_err()
            {
                return Err("input hooks are already installed in this process".to_string());
            }

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
                        let _ = ready_tx.send(Err(
                            "CGEventTapCreate failed — grant this binary Input Monitoring under \
                             System Settings › Privacy & Security, then retry"
                                .to_string(),
                        ));
                        return;
                    };
                    // Publish before the source goes live: the first event can
                    // arrive immediately, and the callback needs the port to
                    // re-arm itself.
                    if let Some(ctx) = CTX.get() {
                        ctx.tap
                            .store(CFRetained::as_ptr(&tap).as_ptr() as usize, Ordering::Release);
                    }

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
                Ok(Ok(runloop)) => Ok(InputHook {
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

        /// Current `(button bitmask, sorted CGKeyCodes down)`.
        pub fn snapshot(&self) -> (u32, Vec<u32>) {
            match CTX.get() {
                Some(ctx) => {
                    release_stranded_keys(ctx);
                    ctx.state.snapshot()
                }
                None => (0, Vec::new()),
            }
        }
    }

    impl Drop for InputHook {
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

    #[test]
    fn is_down_tracks_the_bitset() {
        let s = InputState::new();
        assert!(!s.is_down(56));
        s.key_down(56);
        assert!(s.is_down(56));
        // A key 64 apart must not read as down through word aliasing.
        assert!(!s.is_down(120));
        s.key_up(56);
        assert!(!s.is_down(56));
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

    #[cfg(target_os = "macos")]
    #[test]
    fn modifier_keycodes_map_to_their_flag_bits() {
        // Left/right twins share a bit — that sharing is the whole reason
        // `modifier_twins` exists, so pin it.
        assert_eq!(modifier_mask(56), modifier_mask(60)); // shift
        assert_eq!(modifier_mask(54), modifier_mask(55)); // command
        assert_eq!(modifier_mask(58), modifier_mask(61)); // option
        assert_eq!(modifier_mask(59), modifier_mask(62)); // control
                                                          // Distinct modifiers must not collide.
        assert_ne!(modifier_mask(56), modifier_mask(59));
        assert_ne!(modifier_mask(57), modifier_mask(63));
        // A normal key (kVK_ANSI_A) is not a modifier.
        assert_eq!(modifier_mask(0), None);
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn modifier_twins_covers_both_sides_of_a_shared_bit() {
        let shift = modifier_mask(56).unwrap();
        let mut twins: Vec<u32> = modifier_twins(shift).collect();
        twins.sort_unstable();
        assert_eq!(twins, vec![56, 60]);

        // Caps lock and fn are unpaired: sweeping them must not touch anything
        // else.
        let caps = modifier_mask(57).unwrap();
        assert_eq!(modifier_twins(caps).collect::<Vec<_>>(), vec![57]);
        let func = modifier_mask(63).unwrap();
        assert_eq!(modifier_twins(func).collect::<Vec<_>>(), vec![63]);
    }

    /// Releasing one shift while the other is held reports the shared bit as
    /// still set, so only the final release clears it — at which point both
    /// twins must come up. Without the sweep the first-released key stays down
    /// for the rest of the session.
    #[cfg(target_os = "macos")]
    #[test]
    fn shared_modifier_bit_clearing_releases_both_twins() {
        let s = InputState::new();
        let shift = modifier_mask(56).unwrap();

        s.key_down(56); // left shift down
        s.key_down(60); // right shift down
        assert_eq!(s.snapshot().1, vec![56, 60]);

        // Left released, bit still set (right held): the flags-changed arm
        // treats a set bit as a press, which the bitset suppresses as a repeat.
        assert!(s.key_down(56));
        assert_eq!(s.snapshot().1, vec![56, 60]);

        // Right released, bit now clear: sweep the pair.
        for kc in modifier_twins(shift) {
            if s.is_down(kc) {
                s.key_up(kc);
            }
        }
        assert!(s.snapshot().1.is_empty());
    }
}
