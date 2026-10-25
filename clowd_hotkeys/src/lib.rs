//! Clowd's global keyboard hook: registered hotkeys, one-shot gesture recording and the keystroke
//! observe stream, all from one hook per process. Linked into `clowd_capture` (Rust API) and built
//! as `clowd_hotkeys.dll` / `libclowd_hotkeys.dylib` for Clowd.Ui (C ABI, `ffi.rs`).
//!
//! - [`Key`] is the only key type for global hotkeys anywhere in Clowd; the C# `HotkeyKey` is
//!   generated from the same table. The only other key maps live inside this crate: `Key` to
//!   Windows VK and `Key` to macOS CGKeyCode / NX media keytype.
//! - A [`Gesture`]'s text form (`Control+Alt+Shift+Meta+<Key>`) is the one wire format: settings,
//!   the capturer's `--hk-*` flags and the status echo.
//! - The hook callback only decides suppression and enqueues; consumers pull [`Event`]s from
//!   [`Events`]. It never calls user code, so neither the capturer nor C# can stall it. Matching
//!   and suppression share one snapshot inside the callback, so a press is never swallowed
//!   without firing, or fired without being swallowed (bar a press racing an `unregister`).
//!
//! Limitations:
//!
//! - Windows: elevated foreground windows don't reach a medium-IL hook. Win+L and Ctrl+Alt+Del
//!   can't be suppressed (Win+L can still be recorded; registering it fires but Windows still
//!   locks). AltGr is Ctrl+Alt (as with SharpHook, Avalonia and RegisterHotKey), so a registered
//!   `Control+Alt+X` also swallows AltGr+X — and `Alt+E` never eats AltGr+E.
//! - Windows: a hook Windows removed silently (callback timeouts, e.g. a debugger pause) is
//!   re-installed after a few keystrokes it missed, which moves it to the front of the hook chain.
//!   The watchdog needs the process's keyboard raw input, so it is off where someone else already
//!   took that (winit's event loop in `clowd_capture`).
//! - macOS: needs the Accessibility permission (`start` fails with `PermissionDenied`, never
//!   prompts; losing it later ends the hook with `Event::Stopped` within about a second, as does a
//!   tap that times out a 6th time within 10 s). Secure Input (password fields, in any app) hides
//!   all keys from the tap. A MacBook's top row sends its printed functions (brightness, Mission
//!   Control, media) unless Fn is held or "Use F1, F2, etc. keys as standard function keys" is on,
//!   so an F-key gesture fires only then. Media keys reach the tap only from a keyboard: AirPods,
//!   the Touch Bar and Control Center go through MediaRemote and are never seen; media-key
//!   swallowing must be verified live on current macOS. Media keys are decoded on the main thread
//!   (AppKit), so a host using media-key gestures or recording must service its main run loop or
//!   main dispatch queue (Avalonia and winit do); a media key the main thread does not get to
//!   within 50 ms passes undecoded, its up included if its down was swallowed (once no media
//!   gesture or recording remains, such a press is forgotten at the next undecoded event). Keys are
//!   named by position: on an ISO keyboard too, the key left of 1 is `Oem3` and the one beside left
//!   Shift `OemBackslash`. As in Avalonia.Native, two keys share a name: on JIS, Yen and the key
//!   left of Return are both `OemPipe`; keypad `=` is `OemPlus`, like the main `=`.
//! - Linux: no backend yet; `start` fails with `Unsupported`.

// Used by the backends only.
#[cfg_attr(not(any(windows, target_os = "macos")), allow(dead_code))]
mod core;
mod error;
mod ffi;
mod gesture;
mod hotkeys;
mod key;
mod modifiers;
// Used only by windows.rs outside tests; compiled and tested on every OS.
#[cfg_attr(not(windows), allow(dead_code))]
mod watchdog;
#[cfg_attr(not(windows), allow(dead_code))]
mod winstate;

#[cfg(target_os = "macos")]
mod macos;
#[cfg(not(any(windows, target_os = "macos")))]
mod unsupported;
#[cfg(windows)]
mod windows;

#[cfg(test)]
mod csharp;

pub use std::sync::mpsc::RecvTimeoutError;

pub use error::Error;
pub use gesture::{Gesture, ParseGestureError};
pub use hotkeys::{Event, Events, HotkeyId, Hotkeys, KeyEvent};
pub use key::Key;
pub use modifiers::Modifiers;
