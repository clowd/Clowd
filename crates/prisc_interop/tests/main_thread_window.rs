//! The window area's AppKit cases on the process's real main thread (`harness = false`: libtest never
//! runs a test on the main thread). Each case runs in sequence from `main` on a real NSWindow and
//! checks the export's effect on it; a failure panics, which exits non-zero.
//!
//! The windows are fully transparent (alpha 0) and sit far off every screen, so the one call that
//! orders a window front (the raise) shows nothing and takes no focus: this runs in the plain cargo
//! test gate, beside GUI checks of other work in progress.

#[cfg(target_os = "macos")]
fn main() {
    mac::run();
}

#[cfg(not(target_os = "macos"))]
fn main() {
    println!("main_thread_window: the AppKit cases run on macOS only");
}

#[cfg(target_os = "macos")]
mod mac {
    use objc2::rc::Retained;
    use objc2::{MainThreadMarker, MainThreadOnly};
    use objc2_app_kit::{
        NSApplication, NSBackingStoreType, NSWindow, NSWindowButton, NSWindowCollectionBehavior, NSWindowSharingType, NSWindowStyleMask,
    };
    use objc2_foundation::{NSPoint, NSRect, NSSize};
    use prisc_interop::ffi::cursor::prisc_cursor_position;
    use prisc_interop::ffi::status::PriscInteropStatus as S;
    use prisc_interop::ffi::types::{PriscOverlayFlags, PriscPoint, PriscRect};
    use prisc_interop::ffi::window::*;

    const OK: i32 = S::Ok as i32;

    type Case = (&'static str, fn(MainThreadMarker));

    struct TestWindow(Retained<NSWindow>);

    impl TestWindow {
        fn new(mtm: MainThreadMarker, style: NSWindowStyleMask) -> Self {
            let frame = NSRect::new(NSPoint::new(-20_000.0, -20_000.0), NSSize::new(320.0, 200.0));
            let window = unsafe {
                NSWindow::initWithContentRect_styleMask_backing_defer(
                    NSWindow::alloc(mtm),
                    frame,
                    style,
                    NSBackingStoreType::Buffered,
                    false,
                )
            };
            // owned by the Retained, so closing must not release it a second time
            unsafe { window.setReleasedWhenClosed(false) };
            window.setAlphaValue(0.0);
            Self(window)
        }

        fn handle(&self) -> isize {
            Retained::as_ptr(&self.0) as isize
        }
    }

    impl Drop for TestWindow {
        fn drop(&mut self) {
            self.0.close();
        }
    }

    fn titled(mtm: MainThreadMarker) -> TestWindow {
        TestWindow::new(
            mtm,
            NSWindowStyleMask::Titled | NSWindowStyleMask::Closable | NSWindowStyleMask::Miniaturizable | NSWindowStyleMask::Resizable,
        )
    }

    pub fn run() {
        let mtm = MainThreadMarker::new().expect("a harness = false test runs on the main thread");
        let _app = NSApplication::sharedApplication(mtm);
        let cases: [Case; 9] = [
            ("cover_menu_bar_sets_the_overlay_level_and_behaviour", cover_menu_bar),
            ("click_through_is_set_by_the_flag_and_toggled_live", click_through),
            ("unknown_flags_are_refused_and_change_nothing", unknown_flags),
            ("raise_lifts_above_the_overlay_level", raise),
            ("capture_exclusion_toggles_the_sharing_type", capture_exclusion),
            ("is_foreground_reads_on_the_main_thread", is_foreground),
            ("traffic_lights_move_as_a_unit_and_idempotently", traffic_lights),
            ("a_borderless_window_has_no_traffic_lights", borderless_traffic_lights),
            ("deferred_and_windows_only_exports_are_unsupported", unsupported),
        ];
        for (name, case) in cases {
            case(mtm);
            println!("test {name} ... ok");
        }
        let mut point = PriscPoint::default();
        let status = unsafe { prisc_cursor_position(&mut point) };
        assert!(status == OK || status == S::None as i32, "cursor on the main thread: {status}");
        println!("test cursor_position_on_the_main_thread ... ok");
        println!("main_thread_window: {} cases passed", cases.len() + 1);
    }

    fn cover_menu_bar(mtm: MainThreadMarker) {
        let w = titled(mtm);
        let before = w.0.ignoresMouseEvents();
        assert_eq!(
            unsafe { prisc_window_prepare_overlay(w.handle(), PriscOverlayFlags::CoverMenuBar as u32) },
            OK
        );
        assert_eq!(w.0.level(), 25);
        let expected = NSWindowCollectionBehavior::Stationary
            | NSWindowCollectionBehavior::CanJoinAllSpaces
            | NSWindowCollectionBehavior::FullScreenAuxiliary
            | NSWindowCollectionBehavior::IgnoresCycle;
        assert_eq!(w.0.collectionBehavior(), expected);
        assert_eq!(w.0.ignoresMouseEvents(), before, "no ClickThrough bit leaves the mouse state alone");
        // OpaqueLayered is Windows only: a truthful no-op here
        assert_eq!(
            unsafe { prisc_window_prepare_overlay(w.handle(), PriscOverlayFlags::OpaqueLayered as u32) },
            OK
        );
        assert_eq!(w.0.level(), 25);
    }

    fn click_through(mtm: MainThreadMarker) {
        let w = titled(mtm);
        let all = PriscOverlayFlags::CoverMenuBar as u32 | PriscOverlayFlags::OpaqueLayered as u32 | PriscOverlayFlags::ClickThrough as u32;
        assert_eq!(unsafe { prisc_window_prepare_overlay(w.handle(), all) }, OK);
        assert!(w.0.ignoresMouseEvents());
        assert_eq!(unsafe { prisc_window_set_click_through(w.handle(), 0) }, OK);
        assert!(!w.0.ignoresMouseEvents());
        assert_eq!(unsafe { prisc_window_set_click_through(w.handle(), 2) }, OK, "any non-zero is true");
        assert!(w.0.ignoresMouseEvents());
    }

    fn unknown_flags(mtm: MainThreadMarker) {
        let w = titled(mtm);
        let level = w.0.level();
        assert_eq!(unsafe { prisc_window_prepare_overlay(w.handle(), 1 | 16) }, S::InvalidArg as i32);
        assert_eq!(w.0.level(), level);
    }

    fn raise(mtm: MainThreadMarker) {
        let w = titled(mtm);
        assert_eq!(
            unsafe { prisc_window_prepare_overlay(w.handle(), PriscOverlayFlags::CoverMenuBar as u32) },
            OK
        );
        assert_eq!(unsafe { prisc_window_raise_topmost_no_activate(w.handle()) }, OK);
        assert_eq!(w.0.level(), 26);
        assert!(w.0.isVisible(), "orderFrontRegardless ordered it in");
        assert!(!w.0.isKeyWindow(), "the raise must not make the window key");
        w.0.orderOut(None);
    }

    fn capture_exclusion(mtm: MainThreadMarker) {
        let w = titled(mtm);
        assert_eq!(w.0.sharingType(), NSWindowSharingType::ReadOnly);
        assert_eq!(unsafe { prisc_window_set_excluded_from_capture(w.handle(), 1) }, OK);
        assert_eq!(w.0.sharingType(), NSWindowSharingType::None);
        assert_eq!(unsafe { prisc_window_set_excluded_from_capture(w.handle(), 0) }, OK);
        assert_eq!(w.0.sharingType(), NSWindowSharingType::ReadOnly);
    }

    fn is_foreground(mtm: MainThreadMarker) {
        let w = titled(mtm);
        let mut flag = 7u8;
        assert_eq!(unsafe { prisc_window_is_foreground(w.handle(), &mut flag) }, OK);
        let expected = NSApplication::sharedApplication(mtm).isActive() && w.0.isKeyWindow();
        assert_eq!(flag, u8::from(expected));
        assert_eq!(flag, 0, "a window never ordered in is not key");
    }

    fn traffic_lights(mtm: MainThreadMarker) {
        let w = titled(mtm);
        let buttons: Vec<_> = [
            NSWindowButton::CloseButton,
            NSWindowButton::MiniaturizeButton,
            NSWindowButton::ZoomButton,
        ]
        .into_iter()
        .map(|kind| {
            w.0.standardWindowButton(kind)
                .expect("a titled window has its buttons")
        })
        .collect();
        let before: Vec<_> = buttons.iter().map(|b| b.frame()).collect();
        let bar_height = unsafe { buttons[0].superview() }
            .expect("the buttons sit in the title bar view")
            .frame()
            .size
            .height;

        assert_eq!(unsafe { prisc_window_set_traffic_light_position_macos(w.handle(), 14.0, 14.0) }, OK);
        let after: Vec<_> = buttons.iter().map(|b| b.frame()).collect();
        assert_eq!(after[0].origin.x, 14.0);
        let shift = 14.0 - before[0].origin.x;
        for (b, a) in before.iter().zip(&after) {
            assert_eq!(a.origin.x, b.origin.x + shift, "the cluster moves as a unit");
            assert_eq!(a.origin.y, bar_height - b.size.height - 14.0, "top inset from the bar's top");
            assert_eq!(a.size, b.size, "AppKit's size is kept");
        }

        // a second call computes a zero shift
        assert_eq!(unsafe { prisc_window_set_traffic_light_position_macos(w.handle(), 14.0, 14.0) }, OK);
        let again: Vec<_> = buttons.iter().map(|b| b.frame()).collect();
        assert_eq!(again, after);

        assert_eq!(
            unsafe { prisc_window_set_traffic_light_position_macos(w.handle(), f64::NAN, 14.0) },
            S::InvalidArg as i32
        );
    }

    fn borderless_traffic_lights(mtm: MainThreadMarker) {
        let w = TestWindow::new(mtm, NSWindowStyleMask::Borderless);
        assert_eq!(
            unsafe { prisc_window_set_traffic_light_position_macos(w.handle(), 14.0, 14.0) },
            S::None as i32
        );
    }

    fn unsupported(mtm: MainThreadMarker) {
        let w = titled(mtm);
        let mut in_front = 7u8;
        assert_eq!(
            unsafe { prisc_window_take_foreground(w.handle(), &mut in_front) },
            S::Unsupported as i32
        );
        assert_eq!(in_front, 0);
        let rect = PriscRect {
            x: 0,
            y: 0,
            width: 10,
            height: 10,
        };
        assert_eq!(unsafe { prisc_window_set_bounds(w.handle(), &rect) }, S::Unsupported as i32);
        let mut result = 0isize;
        assert_eq!(
            unsafe { prisc_window_default_proc_windows(w.handle(), 0, 0, 0, &mut result) },
            S::Unsupported as i32
        );
    }
}
