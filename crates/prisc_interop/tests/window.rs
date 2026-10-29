//! Export-level tests of the window area through the library's public surface (README section
//! 4.3): argument checks, the off-OS answers of the `_windows` / `_macos` exports, the cursor, and
//! on Windows each chrome call's effect on a real window. The macOS chrome calls need the main
//! thread, which libtest never uses; they are in `main_thread_window.rs`.

use std::ffi::c_void;

use prisc_interop::ffi::cursor::prisc_cursor_position;
use prisc_interop::ffi::error::prisc_last_error_message;
use prisc_interop::ffi::status::PriscInteropStatus as S;
use prisc_interop::ffi::types::{PriscOverlayFlags, PriscPoint, PriscRect};
use prisc_interop::ffi::window::*;

fn last_message() -> String {
    let len = unsafe { prisc_last_error_message(std::ptr::null_mut(), 0) };
    let mut buf = vec![0u8; len];
    assert_eq!(unsafe { prisc_last_error_message(buf.as_mut_ptr(), buf.len()) }, len);
    String::from_utf8(buf).unwrap()
}

unsafe extern "C" fn ignore(_user: *mut c_void) {}

#[test]
fn zero_handles_are_invalid_arg_and_say_so() {
    assert_eq!(
        unsafe { prisc_window_prepare_overlay(0, PriscOverlayFlags::CoverMenuBar as u32) },
        S::InvalidArg as i32
    );
    assert!(last_message().contains("zero"), "{}", last_message());
    let mut flag = 0u8;
    assert_eq!(unsafe { prisc_window_is_foreground(0, &mut flag) }, S::InvalidArg as i32);
    assert_eq!(unsafe { prisc_window_set_click_through(0, 0) }, S::InvalidArg as i32);
}

#[test]
fn the_one_os_exports_answer_unsupported_elsewhere() {
    let mut result = 0isize;
    let mut watch: *mut PriscForegroundWatch = std::ptr::dangling_mut();
    let traffic = unsafe { prisc_window_set_traffic_light_position_macos(1, 14.0, 14.0) };
    if cfg!(windows) {
        assert_eq!(traffic, S::Unsupported as i32);
        assert!(last_message().contains("macOS only"), "{}", last_message());
        return;
    }
    assert_eq!(
        unsafe { prisc_window_default_proc_windows(1, 0, 0, 0, &mut result) },
        S::Unsupported as i32
    );
    assert!(last_message().contains("Windows only"), "{}", last_message());
    assert_eq!(
        unsafe { prisc_window_foreground_watch_create_windows(Some(ignore), std::ptr::null_mut(), &mut watch) },
        S::Unsupported as i32
    );
    assert!(watch.is_null(), "a failed create must leave a null watch");
    assert_eq!(
        unsafe { prisc_window_foreground_watch_destroy_windows(std::ptr::dangling_mut()) },
        S::Unsupported as i32
    );
    assert_eq!(unsafe { prisc_window_foreground_watch_destroy_windows(std::ptr::null_mut()) }, 0);

    // the neutral exports whose macOS arm is deferred (Q10) answer unsupported, never touching
    // the bogus handle, and write a definite "not in front"
    let mut in_front = 7u8;
    assert_eq!(unsafe { prisc_window_take_foreground(1, &mut in_front) }, S::Unsupported as i32);
    assert_eq!(in_front, 0);
    let rect = PriscRect {
        x: 0,
        y: 0,
        width: 10,
        height: 10,
    };
    assert_eq!(unsafe { prisc_window_set_bounds(1, &rect) }, S::Unsupported as i32);
}

#[test]
fn the_cursor_position_is_plausible() {
    let mut point = PriscPoint {
        x: i32::MIN,
        y: i32::MIN,
    };
    let status = unsafe { prisc_cursor_position(&mut point) };
    if status == S::None as i32 {
        eprintln!("skipping: the OS gave no cursor position in this session");
        return;
    }
    assert_eq!(status, S::Ok as i32);
    // a million pixels either way is no desktop
    assert!(point.x.abs() < 1_000_000 && point.y.abs() < 1_000_000, "{point:?}");
}

#[cfg(windows)]
mod windows_chrome {
    use super::*;
    use windows::core::w;
    use windows::Win32::Foundation::{HWND, RECT};
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DestroyWindow, GetLayeredWindowAttributes, GetWindowDisplayAffinity, GetWindowLongPtrW, GetWindowRect,
        GWL_EXSTYLE, LAYERED_WINDOW_ATTRIBUTES_FLAGS, WDA_EXCLUDEFROMCAPTURE, WDA_NONE, WINDOW_EX_STYLE, WM_NULL, WS_EX_LAYERED,
        WS_EX_TOOLWINDOW, WS_EX_TRANSPARENT, WS_POPUP,
    };

    /// A real top-level popup of the predefined STATIC class (no class to register), never shown.
    struct TestWindow(HWND);

    impl TestWindow {
        fn new(ex: WINDOW_EX_STYLE) -> Self {
            let hwnd = unsafe {
                CreateWindowExW(
                    ex | WS_EX_TOOLWINDOW,
                    w!("STATIC"),
                    w!("prisc_interop window test"),
                    WS_POPUP,
                    100,
                    100,
                    200,
                    150,
                    None,
                    None,
                    None,
                    None,
                )
            }
            .expect("CreateWindowExW(STATIC)");
            Self(hwnd)
        }

        fn handle(&self) -> isize {
            self.0 .0 as isize
        }

        fn ex_style(&self) -> isize {
            unsafe { GetWindowLongPtrW(self.0, GWL_EXSTYLE) }
        }
    }

    impl Drop for TestWindow {
        fn drop(&mut self) {
            let _ = unsafe { DestroyWindow(self.0) };
        }
    }

    #[test]
    fn prepare_overlay_makes_a_layered_window_opaque_and_click_through() {
        let window = TestWindow::new(WS_EX_LAYERED);
        let flags =
            PriscOverlayFlags::CoverMenuBar as u32 | PriscOverlayFlags::OpaqueLayered as u32 | PriscOverlayFlags::ClickThrough as u32;
        assert_eq!(
            unsafe { prisc_window_prepare_overlay(window.handle(), flags) },
            0,
            "{}",
            last_message()
        );
        let mut alpha = 0u8;
        let mut how = LAYERED_WINDOW_ATTRIBUTES_FLAGS(0);
        unsafe { GetLayeredWindowAttributes(window.0, None, Some(&mut alpha), Some(&mut how)) }.expect("GetLayeredWindowAttributes");
        assert_eq!(alpha, 255);
        assert_ne!(window.ex_style() & WS_EX_TRANSPARENT.0 as isize, 0);
    }

    #[test]
    fn click_through_toggles_ws_ex_transparent_both_ways() {
        let window = TestWindow::new(WINDOW_EX_STYLE(0));
        let transparent = WS_EX_TRANSPARENT.0 as isize;
        assert_eq!(window.ex_style() & transparent, 0);
        assert_eq!(
            unsafe { prisc_window_set_click_through(window.handle(), 1) },
            0,
            "{}",
            last_message()
        );
        assert_ne!(window.ex_style() & transparent, 0);
        // again: a no-op, still OK
        assert_eq!(unsafe { prisc_window_set_click_through(window.handle(), 1) }, 0);
        assert_eq!(
            unsafe { prisc_window_set_click_through(window.handle(), 0) },
            0,
            "{}",
            last_message()
        );
        assert_eq!(window.ex_style() & transparent, 0);
        assert_ne!(window.ex_style() & WS_EX_TOOLWINDOW.0 as isize, 0, "other bits are kept");
    }

    #[test]
    fn capture_exclusion_sets_and_clears_the_affinity() {
        let window = TestWindow::new(WINDOW_EX_STYLE(0));
        assert_eq!(
            unsafe { prisc_window_set_excluded_from_capture(window.handle(), 1) },
            0,
            "{}",
            last_message()
        );
        let mut affinity = 0u32;
        unsafe { GetWindowDisplayAffinity(window.0, &mut affinity) }.expect("GetWindowDisplayAffinity");
        assert_eq!(affinity, WDA_EXCLUDEFROMCAPTURE.0);
        assert_eq!(unsafe { prisc_window_set_excluded_from_capture(window.handle(), 0) }, 0);
        unsafe { GetWindowDisplayAffinity(window.0, &mut affinity) }.expect("GetWindowDisplayAffinity");
        assert_eq!(affinity, WDA_NONE.0);
    }

    #[test]
    fn set_bounds_moves_and_sizes_in_one_call() {
        let window = TestWindow::new(WINDOW_EX_STYLE(0));
        let rect = PriscRect {
            x: 37,
            y: 41,
            width: 321,
            height: 123,
        };
        assert_eq!(unsafe { prisc_window_set_bounds(window.handle(), &rect) }, 0, "{}", last_message());
        let mut actual = RECT::default();
        unsafe { GetWindowRect(window.0, &mut actual) }.expect("GetWindowRect");
        assert_eq!(
            (actual.left, actual.top, actual.right - actual.left, actual.bottom - actual.top),
            (37, 41, 321, 123)
        );
    }

    #[test]
    fn raise_and_the_foreground_reads_answer_ok_on_a_hidden_window() {
        let window = TestWindow::new(WINDOW_EX_STYLE(0));
        assert_eq!(
            unsafe { prisc_window_raise_topmost_no_activate(window.handle()) },
            0,
            "{}",
            last_message()
        );
        let mut flag = 7u8;
        assert_eq!(unsafe { prisc_window_is_foreground(window.handle(), &mut flag) }, 0);
        assert_eq!(flag, 0, "a hidden window is never the foreground window");
        let mut result = 7isize;
        assert_eq!(
            unsafe { prisc_window_default_proc_windows(window.handle(), WM_NULL, 0, 0, &mut result) },
            0
        );
        assert_eq!(result, 0, "DefWindowProc answers 0 for WM_NULL");
    }

    #[test]
    fn a_foreground_watch_lives_and_dies_on_its_thread() {
        let mut watch: *mut PriscForegroundWatch = std::ptr::null_mut();
        assert_eq!(
            unsafe { prisc_window_foreground_watch_create_windows(None, std::ptr::null_mut(), &mut watch) },
            S::InvalidArg as i32
        );
        assert_eq!(
            unsafe { prisc_window_foreground_watch_create_windows(Some(ignore), std::ptr::null_mut(), &mut watch) },
            0,
            "{}",
            last_message()
        );
        assert!(!watch.is_null());

        // another thread may not unhook it, and the watch survives the refusal
        let address = watch as usize;
        let status = std::thread::spawn(move || unsafe { prisc_window_foreground_watch_destroy_windows(address as *mut _) })
            .join()
            .unwrap();
        assert_eq!(status, S::State as i32);
        assert_eq!(
            unsafe { prisc_window_foreground_watch_destroy_windows(watch) },
            0,
            "{}",
            last_message()
        );
    }
}
