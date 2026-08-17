//! Global input hooks feeding `--input-capture` (DESIGN §2).
//!
//! A dedicated thread installs `WH_KEYBOARD_LL` + `WH_MOUSE_LL` hooks and runs
//! the message pump low-level hooks require. The hook procs maintain a
//! lock-free snapshot of the current input state (button bitmask + keys-down
//! bitset, sampled once per rendered frame by the input-capture tick callback)
//! and hand every edge — key down/up, button down/up — to the caller's sink
//! as a [`RawEvent`] stamped with `os_gettime_ns()`, the same monotonic clock
//! behind `obs_get_video_frame_time`, so events and frame rows share one
//! timebase. The sink runs on the hook thread and must only do a channel send.
//!
//! Auto-repeat key-downs are suppressed at the source: the bitset already
//! records the key as down, so only real edges reach the channel.
//!
//! macOS: compiling stub — `start` succeeds, the state snapshot is empty and
//! no events are ever sent (DESIGN §2: identical signatures, stubbed behavior).

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
pub fn xbutton_bit(hiword: u16) -> Option<u32> {
    match hiword {
        1 => Some(BTN_X1),
        2 => Some(BTN_X2),
        _ => None,
    }
}

pub use imp::InputHook;

/// Where the hook thread delivers edge events (a channel send in practice).
pub type EventSink = Box<dyn Fn(RawEvent) + Send + Sync>;

#[cfg(windows)]
mod imp {
    use std::sync::mpsc;
    use std::sync::OnceLock;

    use windows_sys::Win32::Foundation::{HINSTANCE, LPARAM, LRESULT, WPARAM};
    use windows_sys::Win32::System::Threading::GetCurrentThreadId;
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
        GetAsyncKeyState, GetKeyState, GetKeyboardLayout, ToUnicodeEx, VK_CAPITAL, VK_CONTROL,
        VK_MENU, VK_SHIFT,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::{
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
            if unsafe { GetAsyncKeyState(m as i32) } as u16 & 0x8000 != 0 {
                key_state[m as usize] = 0x80;
            }
        }
        // Toggle state (low bit) — capitalization.
        if unsafe { GetKeyState(VK_CAPITAL as i32) } & 1 != 0 {
            key_state[VK_CAPITAL as usize] = 1;
        }
        let layout = unsafe {
            let fg = GetForegroundWindow();
            let tid = GetWindowThreadProcessId(fg, std::ptr::null_mut());
            GetKeyboardLayout(tid)
        };
        let mut buf = [0u16; 8];
        let n = unsafe {
            ToUnicodeEx(
                vk,
                scan_code,
                key_state.as_ptr(),
                buf.as_mut_ptr(),
                buf.len() as i32,
                0x4,
                layout,
            )
        };
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
                let kb = &*(lparam as *const KBDLLHOOKSTRUCT);
                let vk = kb.vkCode;
                match wparam as u32 {
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
        CallNextHookEx(std::ptr::null_mut(), code, wparam, lparam)
    }

    unsafe extern "system" fn mouse_proc(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
        if code >= 0 {
            if let Some(ctx) = CTX.get() {
                let ms = &*(lparam as *const MSLLHOOKSTRUCT);
                // Screen coords; physical px (the process is per-monitor-v2
                // DPI aware) — the same space as frame-row x/y.
                let (x, y) = (ms.pt.x, ms.pt.y);
                let (btn, down) = match wparam as u32 {
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
        CallNextHookEx(std::ptr::null_mut(), code, wparam, lparam)
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
                        let kb = SetWindowsHookExW(
                            WH_KEYBOARD_LL,
                            Some(keyboard_proc),
                            std::ptr::null_mut::<core::ffi::c_void>() as HINSTANCE,
                            0,
                        );
                        let ms = SetWindowsHookExW(
                            WH_MOUSE_LL,
                            Some(mouse_proc),
                            std::ptr::null_mut::<core::ffi::c_void>() as HINSTANCE,
                            0,
                        );
                        if kb.is_null() || ms.is_null() {
                            for h in [kb, ms] {
                                if !h.is_null() {
                                    UnhookWindowsHookEx(h);
                                }
                            }
                            let _ = ready_tx
                                .send(Err("SetWindowsHookExW failed (LL hooks)".to_string()));
                            return;
                        }

                        // Force the message queue into existence so the
                        // shutdown PostThreadMessageW can never race its
                        // creation, then hand the pump's thread id back.
                        let mut msg: MSG = std::mem::zeroed();
                        PeekMessageW(&mut msg, std::ptr::null_mut(), 0, 0, PM_NOREMOVE);
                        let _ = ready_tx.send(Ok(GetCurrentThreadId()));

                        while GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) > 0 {
                            DispatchMessageW(&msg);
                        }

                        UnhookWindowsHookEx(kb);
                        UnhookWindowsHookEx(ms);
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
            unsafe { PostThreadMessageW(self.thread_id, WM_QUIT, 0, 0) };
            if let Some(thread) = self.thread.take() {
                let _ = thread.join();
            }
        }
    }
}

#[cfg(not(windows))]
mod imp {
    use super::EventSink;

    /// Compiling stub (DESIGN §2): no hooks, no events, empty snapshots.
    pub struct InputHook {
        _sink: EventSink,
    }

    impl InputHook {
        pub fn start(sink: EventSink) -> Result<InputHook, String> {
            Ok(InputHook { _sink: sink })
        }

        pub fn snapshot(&self) -> (u32, Vec<u32>) {
            (0, Vec::new())
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
    fn xbutton_hiword_maps_to_the_bitmask() {
        assert_eq!(xbutton_bit(1), Some(BTN_X1));
        assert_eq!(xbutton_bit(2), Some(BTN_X2));
        assert_eq!(xbutton_bit(0), None);
        assert_eq!(xbutton_bit(3), None);
    }
}
