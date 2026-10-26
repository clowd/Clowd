//! The Windows backend: a WH_KEYBOARD_LL hook on its own message-loop thread. `ll_proc` is glue —
//! every decision lives in `WinState` and `HookCore` (and the re-hook decision in `Watchdog`).

use std::cell::{Cell, RefCell};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::Ordering;
use std::sync::mpsc::{self, Sender};
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::Instant;

use windows::core::w;
use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::Threading::{GetCurrentThread, GetCurrentThreadId, SetThreadPriority, THREAD_PRIORITY_HIGHEST};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    GetAsyncKeyState, GetKeyState, GetKeyboardLayout, SendInput, ToUnicodeEx, HKL, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT,
    KEYBD_EVENT_FLAGS, KEYEVENTF_KEYUP, VIRTUAL_KEY, VK_CAPITAL, VK_CONTROL, VK_MENU, VK_SHIFT,
};
use windows::Win32::UI::Input::{GetRegisteredRawInputDevices, RegisterRawInputDevices, RAWINPUTDEVICE, RIDEV_INPUTSINK, RIDEV_REMOVE};
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, CreateWindowExW, DestroyWindow, DispatchMessageW, GetForegroundWindow, GetGUIThreadInfo, GetMessageW,
    GetWindowThreadProcessId, PeekMessageW, PostThreadMessageW, SetWindowsHookExW, TranslateMessage, UnhookWindowsHookEx, GUITHREADINFO,
    HHOOK, HWND_MESSAGE, KBDLLHOOKSTRUCT, MSG, PM_NOREMOVE, WH_KEYBOARD_LL, WINDOW_EX_STYLE, WINDOW_STYLE, WM_INPUT, WM_QUIT,
};

use crate::core::{HookCore, Verdict};
use crate::hotkeys::Shared;
use crate::watchdog::Watchdog;
use crate::winstate::{WinEvent, WinState, MASK_MARKER, MASK_VK, SIDE_KEYS};
use crate::{Error, Event};

thread_local! {
    /// The hook thread's state. `ll_proc` runs on this thread (LL hooks are called through its
    /// message loop), so a thread-local is the whole synchronization story. It outlives a re-hook.
    static STATE: RefCell<Option<(HookCore, WinState)>> = const { RefCell::new(None) };
    /// `ll_proc` invocations, for the watchdog.
    static HOOK_CALLS: Cell<u64> = const { Cell::new(0) };
}

/// HID usage page and usage of a keyboard, for the watchdog's raw-input canary.
const HID_PAGE_GENERIC: u16 = 0x01;
const HID_USAGE_KEYBOARD: u16 = 0x06;

/// The running hook thread. Dropping it posts WM_QUIT and joins.
pub(crate) struct Backend {
    thread_id: u32,
    thread: Option<JoinHandle<()>>,
}

impl Drop for Backend {
    fn drop(&mut self) {
        // SAFETY: posting to a thread id is always safe; the queue exists (created before `spawn`
        // returned), so WM_QUIT is delivered even if the loop is not waiting yet.
        let _ = unsafe { PostThreadMessageW(self.thread_id, WM_QUIT, WPARAM(0), LPARAM(0)) };
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

pub(crate) fn spawn(shared: Arc<Shared>, tx: Sender<Event>) -> Result<Backend, Error> {
    let (ready_tx, ready_rx) = mpsc::channel();
    let thread = thread::Builder::new()
        .name("prisc-hotkeys-hook".into())
        .spawn(move || run(shared, tx, ready_tx))
        .map_err(|e| Error::HookFailed(e.to_string()))?;
    match ready_rx.recv() {
        Ok(Ok(thread_id)) => Ok(Backend {
            thread_id,
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

fn run(shared: Arc<Shared>, tx: Sender<Event>, ready: Sender<Result<u32, Error>>) {
    let mut msg = MSG::default();
    // SAFETY: plain Win32 calls on this thread with valid out-pointers. PeekMessageW creates the
    // thread's message queue so PostThreadMessageW(WM_QUIT) can never be lost.
    unsafe {
        let _ = PeekMessageW(&mut msg, None, 0, 0, PM_NOREMOVE);
        let _ = SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    }
    STATE.with(|state| *state.borrow_mut() = Some((HookCore::new(shared.clone(), tx), WinState::new())));

    let mut hook = match install() {
        Ok(hook) => hook,
        Err(error) => {
            STATE.with(|state| state.borrow_mut().take());
            shared.alive.store(false, Ordering::Release);
            let _ = ready.send(Err(Error::HookFailed(error.message())));
            return;
        }
    };
    // SAFETY: trivially safe.
    let _ = ready.send(Ok(unsafe { GetCurrentThreadId() }));

    let canary = create_canary();
    let started = Instant::now();
    let mut watchdog = Watchdog::new();
    let mut failed = None;
    loop {
        // SAFETY: `msg` is a valid MSG for the duration of each call.
        match unsafe { GetMessageW(&mut msg, None, 0, 0) }.0 {
            0 => break,
            -1 => {
                failed = Some("message loop failed".to_string());
                break;
            }
            _ => {
                // Only the canary gets WM_INPUT on this thread. The re-hook happens here because a
                // hook can only be re-installed from the thread that runs it.
                if msg.message == WM_INPUT && watchdog.canary(HOOK_CALLS.get(), started.elapsed().as_millis() as u64) {
                    // SAFETY: `hook` came from SetWindowsHookExW on this thread. If Windows removed it,
                    // this fails with ERROR_INVALID_HOOK_HANDLE: the expected case.
                    let _ = unsafe { UnhookWindowsHookEx(hook) };
                    match install() {
                        Ok(new) => {
                            hook = new;
                            #[cfg(test)]
                            tests::REHOOKS.fetch_add(1, Ordering::Relaxed);
                        }
                        Err(error) => {
                            hook = HHOOK::default();
                            failed = Some(format!(
                                "the keyboard hook was removed and could not be re-installed: {}",
                                error.message()
                            ));
                            break;
                        }
                    }
                }
                // SAFETY: `msg` came from GetMessageW. The canary's class proc leaves WM_INPUT to
                // DefWindowProc, which does its cleanup.
                unsafe {
                    let _ = TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
            }
        }
    }

    if !hook.is_invalid() {
        // SAFETY: `hook` came from SetWindowsHookExW on this thread and is unhooked once.
        let _ = unsafe { UnhookWindowsHookEx(hook) };
    }
    if let Some(canary) = canary {
        destroy_canary(canary);
    }
    // Dropping the state drops the only Sender, which closes Events once it is drained.
    if let Some((mut core, _)) = STATE.with(|state| state.borrow_mut().take()) {
        if let Some(reason) = failed {
            core.stopped(reason);
        }
    }
    shared.alive.store(false, Ordering::Release);
}

/// Installs `ll_proc` for the calling thread, which must run a message loop.
fn install() -> windows::core::Result<HHOOK> {
    // SAFETY: ll_proc matches HOOKPROC; a null hmod is fine for a low-level hook, which is never
    // injected into other processes.
    unsafe { SetWindowsHookExW(WH_KEYBOARD_LL, Some(ll_proc), None, 0) }
}

/// The watchdog's canary: a message-only window that gets keyboard raw input (WM_INPUT) on this
/// thread, foreground or not. Keyboard raw input goes to one window per process, so this is `None`
/// when the process already takes it elsewhere (a winit event loop does, unless it hands it back
/// as `prisc_capture` does): the watchdog is then off.
fn create_canary() -> Option<HWND> {
    if keyboard_raw_input_target() != Ok(None) {
        return None;
    }
    // SAFETY: plain Win32 calls; the window belongs to this thread and `destroy_canary` destroys it
    // on this thread. STATIC is a system class whose proc leaves unknown messages to DefWindowProc.
    unsafe {
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            w!("STATIC"),
            None,
            WINDOW_STYLE(0),
            0,
            0,
            0,
            0,
            Some(HWND_MESSAGE),
            None,
            None,
            None,
        )
        .ok()?;
        let device = RAWINPUTDEVICE {
            usUsagePage: HID_PAGE_GENERIC,
            usUsage: HID_USAGE_KEYBOARD,
            dwFlags: RIDEV_INPUTSINK,
            hwndTarget: hwnd,
        };
        if RegisterRawInputDevices(&[device], size_of::<RAWINPUTDEVICE>() as u32).is_err() {
            let _ = DestroyWindow(hwnd);
            return None;
        }
        Some(hwnd)
    }
}

/// Drops the keyboard raw-input registration if it is still the canary's, then the window.
fn destroy_canary(hwnd: HWND) {
    // SAFETY: plain Win32 calls; `hwnd` is the canary, created on this thread.
    unsafe {
        if keyboard_raw_input_target() == Ok(Some(hwnd)) {
            let device = RAWINPUTDEVICE {
                usUsagePage: HID_PAGE_GENERIC,
                usUsage: HID_USAGE_KEYBOARD,
                dwFlags: RIDEV_REMOVE,
                hwndTarget: HWND::default(),
            };
            let _ = RegisterRawInputDevices(&[device], size_of::<RAWINPUTDEVICE>() as u32);
        }
        let _ = DestroyWindow(hwnd);
    }
}

/// The target window of this process's keyboard raw-input registration, `Ok(None)` if there is
/// none (a page-wide generic-desktop registration counts as one). `Err` if the query failed.
fn keyboard_raw_input_target() -> Result<Option<HWND>, ()> {
    let size = size_of::<RAWINPUTDEVICE>() as u32;
    let mut count = 0u32;
    // SAFETY: with no buffer the call only writes the count; the second call writes at most `count`
    // entries into `devices`, which holds that many.
    let devices = unsafe {
        let _ = GetRegisteredRawInputDevices(None, &mut count, size);
        let mut devices = vec![RAWINPUTDEVICE::default(); count as usize];
        if count > 0 && GetRegisteredRawInputDevices(Some(devices.as_mut_ptr()), &mut count, size) == u32::MAX {
            return Err(());
        }
        devices.truncate(count as usize);
        devices
    };
    Ok(devices
        .iter()
        .find(|d| d.usUsagePage == HID_PAGE_GENERIC && matches!(d.usUsage, 0 | HID_USAGE_KEYBOARD))
        .map(|d| d.hwndTarget))
}

/// The hook callback: decide and enqueue, nothing else. No allocation beyond the queue node, no
/// logging, no user code; a panic passes the key.
unsafe extern "system" fn ll_proc(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    HOOK_CALLS.with(|calls| calls.set(calls.get().wrapping_add(1)));
    if code < 0 {
        return CallNextHookEx(None, code, wparam, lparam);
    }
    // SAFETY: for HC_ACTION, lparam points at a KBDLLHOOKSTRUCT valid for this call.
    let info = &*(lparam.0 as *const KBDLLHOOKSTRUCT);
    let ev = WinEvent {
        vk: info.vkCode,
        scan: info.scanCode,
        flags: info.flags.0,
        time: info.time,
        extra: info.dwExtraInfo,
    };
    let (verdict, inject) = catch_unwind(AssertUnwindSafe(|| {
        STATE.with(|state| match state.try_borrow_mut().as_deref_mut() {
            Ok(Some((core, win))) => win.on_event(core, &ev, async_mods, || translate(ev.vk, ev.scan)),
            _ => (Verdict::Pass, false),
        })
    }))
    .unwrap_or((Verdict::Pass, false));
    // The borrow is released: SendInput may re-enter the hook.
    if inject {
        send_mask();
    }
    match verdict {
        Verdict::Swallow => LRESULT(1),
        Verdict::Pass => CallNextHookEx(None, code, wparam, lparam),
    }
}

fn key_down(vk: u16) -> bool {
    // SAFETY: trivially safe.
    unsafe { GetAsyncKeyState(vk as i32) as u16 & 0x8000 != 0 }
}

/// The eight side bits in the async key state: the logical state (injected edges count) of every
/// earlier edge, not this one.
fn async_mods() -> u8 {
    SIDE_KEYS
        .iter()
        .filter(|(vk, _)| key_down(*vk))
        .fold(0, |mods, (_, bit)| mods | bit)
}

/// The character this key-down types under the focused thread's layout. Flag 0x4 keeps
/// ToUnicodeEx from touching the keyboard state (the foreground app's dead-key state survives).
/// The async state includes the phantom AltGr LCtrl, so AltGr characters come out right.
fn translate(vk: u32, scan: u32) -> Option<char> {
    let mut state = [0u8; 256];
    for modifier in [VK_SHIFT, VK_CONTROL, VK_MENU] {
        if key_down(modifier.0) {
            state[modifier.0 as usize] = 0x80;
        }
    }
    // SAFETY: plain Win32 queries; `state` and `buffer` outlive the calls.
    let (n, buffer) = unsafe {
        if GetKeyState(VK_CAPITAL.0 as i32) & 1 != 0 {
            state[VK_CAPITAL.0 as usize] = 1;
        }
        let mut buffer = [0u16; 8];
        (ToUnicodeEx(vk, scan, &state, &mut buffer, 0x4, Some(focus_layout())), buffer)
    };
    if n < 1 {
        return None;
    }
    let len = (n as usize).min(buffer.len());
    char::decode_utf16(buffer[..len].iter().copied())
        .next()?
        .ok()
        .filter(|c| !c.is_control())
}

/// The keyboard layout of the thread that owns the keyboard focus. GetGUIThreadInfo(0) describes the
/// foreground thread: its focus window, else its active window, else the foreground window names
/// the thread (console hosts and Store apps focus a window of another thread than the foreground
/// window's). With no window at all, layout 0: this thread's own.
fn focus_layout() -> HKL {
    let mut info = GUITHREADINFO {
        cbSize: size_of::<GUITHREADINFO>() as u32,
        ..Default::default()
    };
    // SAFETY: plain Win32 queries; `info` outlives the call.
    unsafe {
        let window = match GetGUIThreadInfo(0, &mut info) {
            Ok(()) if !info.hwndFocus.is_invalid() => info.hwndFocus,
            Ok(()) if !info.hwndActive.is_invalid() => info.hwndActive,
            _ => GetForegroundWindow(),
        };
        let thread = if window.is_invalid() {
            0
        } else {
            GetWindowThreadProcessId(window, None)
        };
        GetKeyboardLayout(thread)
    }
}

/// Injects a down+up of the unassigned mask VK, marked so our own hook passes it.
fn send_mask() {
    let input = |flags| INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: VIRTUAL_KEY(MASK_VK),
                wScan: 0,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: MASK_MARKER,
            },
        },
    };
    // SAFETY: two fully initialized INPUTs and their exact size.
    let _ = unsafe { SendInput(&[input(KEYBD_EVENT_FLAGS(0)), input(KEYEVENTF_KEYUP)], size_of::<INPUT>() as i32) };
}

#[cfg(test)]
pub(crate) mod tests {
    use std::os::windows::io::AsRawHandle;
    use std::sync::atomic::AtomicU32;
    use std::time::Duration;

    use windows::Win32::Foundation::HANDLE;
    use windows::Win32::System::Threading::{ResumeThread, SuspendThread};

    use super::*;

    /// Successful re-hooks, process-wide.
    pub(crate) static REHOOKS: AtomicU32 = AtomicU32::new(0);

    /// A down+up of the unassigned mask VK, NOT marked: the hook observes it like any key.
    fn tap() {
        let input = |flags| INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 {
                ki: KEYBDINPUT {
                    wVk: VIRTUAL_KEY(MASK_VK),
                    wScan: 0,
                    dwFlags: flags,
                    time: 0,
                    dwExtraInfo: 0,
                },
            },
        };
        // SAFETY: two fully initialized INPUTs and their exact size.
        let _ = unsafe { SendInput(&[input(KEYBD_EVENT_FLAGS(0)), input(KEYEVENTF_KEYUP)], size_of::<INPUT>() as i32) };
    }

    fn taps_seen(rx: &mpsc::Receiver<Event>) -> usize {
        rx.try_iter()
            .filter(|e| matches!(e, Event::Key(k) if k.raw_code == MASK_VK as u32))
            .count()
    }

    /// End to end: stall the hook thread past LowLevelHooksTimeout so Windows skips the hook for a
    /// few events (raw input still sees them), then check the watchdog re-installs it on the hook
    /// thread and keys are observed again, once each. Three stalled taps do not get the hook removed
    /// for good (Windows 11 28000): that takes more timeouts than is polite to cause in a test, and
    /// differs only in the unhook failing, which is ignored. Ignored: it injects real key events
    /// (the unassigned VK 0xE8, which nothing acts on) into the session, and while the thread is
    /// suspended every keyboard event on the desktop waits up to the hook timeout.
    #[test]
    #[ignore]
    fn watchdog_reinstalls_a_silently_removed_hook() {
        let shared = Arc::new(Shared::new());
        shared
            .observing
            .store(true, Ordering::Relaxed);
        let (tx, rx) = mpsc::channel();
        let backend = spawn(shared.clone(), tx).unwrap();
        tap();
        thread::sleep(Duration::from_millis(300));
        assert_eq!(taps_seen(&rx), 2, "the hook observes injected keys");

        let thread = HANDLE(
            backend
                .thread
                .as_ref()
                .unwrap()
                .as_raw_handle(),
        );
        // SAFETY: the handle is the live hook thread's; it is resumed below.
        unsafe { SuspendThread(thread) };
        for _ in 0..3 {
            tap();
        }
        thread::sleep(Duration::from_millis(2500));
        // SAFETY: as above.
        unsafe { ResumeThread(thread) };
        thread::sleep(Duration::from_millis(300));
        let rehooks = REHOOKS.load(Ordering::Relaxed);
        let _ = taps_seen(&rx);

        tap();
        thread::sleep(Duration::from_millis(300));
        assert_eq!(taps_seen(&rx), 2, "the re-installed hook observes keys again");
        assert!(rehooks >= 1, "the watchdog re-hooked (the queued canaries alone are enough)");
        assert!(shared.alive.load(Ordering::Acquire));
    }
}
