//! The Windows backend: a WH_KEYBOARD_LL hook on its own message-loop thread. `ll_proc` is glue —
//! every decision lives in `WinState` and `HookCore`.

use std::cell::RefCell;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::Ordering;
use std::sync::mpsc::{self, Sender};
use std::sync::Arc;
use std::thread::{self, JoinHandle};

use windows::Win32::Foundation::{LPARAM, LRESULT, WPARAM};
use windows::Win32::System::Threading::{GetCurrentThread, GetCurrentThreadId, SetThreadPriority, THREAD_PRIORITY_HIGHEST};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    GetAsyncKeyState, GetKeyState, GetKeyboardLayout, SendInput, ToUnicodeEx, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT,
    KEYBD_EVENT_FLAGS, KEYEVENTF_KEYUP, VIRTUAL_KEY, VK_CAPITAL, VK_CONTROL, VK_MENU, VK_SHIFT,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, DispatchMessageW, GetForegroundWindow, GetMessageW, GetWindowThreadProcessId, PeekMessageW, PostThreadMessageW,
    SetWindowsHookExW, TranslateMessage, UnhookWindowsHookEx, KBDLLHOOKSTRUCT, MSG, PM_NOREMOVE, WH_KEYBOARD_LL, WM_QUIT,
};

use crate::core::{HookCore, Verdict};
use crate::hotkeys::Shared;
use crate::winstate::{WinEvent, WinState, MASK_MARKER, MASK_VK, SIDE_KEYS};
use crate::{Error, Event};

thread_local! {
    /// The hook thread's state. `ll_proc` runs on this thread (LL hooks are called through its
    /// message loop), so a thread-local is the whole synchronization story.
    static STATE: RefCell<Option<(HookCore, WinState)>> = const { RefCell::new(None) };
}

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
        .name("clowd-hotkeys-hook".into())
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

    // SAFETY: ll_proc matches HOOKPROC; a null hmod is fine for a low-level hook, which is never
    // injected into other processes.
    let hook = match unsafe { SetWindowsHookExW(WH_KEYBOARD_LL, Some(ll_proc), None, 0) } {
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

    let mut failed = false;
    loop {
        // SAFETY: `msg` is a valid MSG for the duration of each call.
        match unsafe { GetMessageW(&mut msg, None, 0, 0) }.0 {
            0 => break,
            -1 => {
                failed = true;
                break;
            }
            _ => unsafe {
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            },
        }
    }

    // SAFETY: `hook` came from SetWindowsHookExW on this thread and is unhooked once.
    let _ = unsafe { UnhookWindowsHookEx(hook) };
    // Dropping the state drops the only Sender, which closes Events once it is drained.
    if let Some((mut core, _)) = STATE.with(|state| state.borrow_mut().take()) {
        if failed {
            core.stopped("message loop failed".into());
        }
    }
    shared.alive.store(false, Ordering::Release);
}

/// The hook callback: decide and enqueue, nothing else. No allocation beyond the queue node, no
/// logging, no user code; a panic passes the key.
unsafe extern "system" fn ll_proc(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
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
            Ok(Some((core, win))) => win.on_event(core, &ev, physical_mods, || translate(ev.vk, ev.scan)),
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

/// The eight side bits as the OS currently sees them (every earlier edge, not this one).
fn physical_mods() -> u8 {
    SIDE_KEYS
        .iter()
        .filter(|(vk, _)| key_down(*vk))
        .fold(0, |mods, (_, bit)| mods | bit)
}

/// The character this key-down types under the foreground window's layout. Flag 0x4 keeps
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
        let layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), None));
        let mut buffer = [0u16; 8];
        (ToUnicodeEx(vk, scan, &state, &mut buffer, 0x4, Some(layout)), buffer)
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
