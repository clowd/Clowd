//! The macapp area's AppKit cases on the process's real main thread (`harness = false`: libtest never
//! runs a test on the main thread). Each case runs in sequence from `main`; a failure panics, which
//! exits non-zero.
//!
//! Nothing here takes focus: the Regular policy is set without activation (the test process shows a
//! dock icon for a moment), and the Services delivery goes through a private, uniquely named
//! pasteboard, never the user's clipboard.

#[cfg(target_os = "macos")]
fn main() {
    mac::run();
}

#[cfg(not(target_os = "macos"))]
fn main() {
    println!("main_thread_macapp: the AppKit cases run on macOS only");
}

#[cfg(target_os = "macos")]
mod mac {
    use std::ffi::c_void;
    use std::ptr;
    use std::sync::Mutex;

    use objc2::rc::Retained;
    use objc2::runtime::{AnyClass, AnyObject, ProtocolObject};
    use objc2::{msg_send, MainThreadMarker};
    use objc2_app_kit::{NSApplication, NSApplicationActivationPolicy, NSPasteboard, NSPasteboardWriting};
    use objc2_foundation::{NSArray, NSString, NSURL};
    use prisc_interop::ffi::app::{prisc_app_set_activation_policy_macos, PriscActivationPolicy as P};
    use prisc_interop::ffi::services::prisc_services_register_provider_macos;
    use prisc_interop::ffi::status::PriscInteropStatus as S;
    use prisc_interop::ffi::types::PriscString;

    const OK: i32 = S::Ok as i32;

    type Case = (&'static str, fn(MainThreadMarker));

    pub fn run() {
        let mtm = MainThreadMarker::new().expect("a harness = false test runs on the main thread");
        let _app = NSApplication::sharedApplication(mtm);
        let cases: [Case; 4] = [
            ("every_activation_policy_is_set_on_the_app", activation_policies),
            ("an_unknown_policy_changes_nothing", unknown_policy),
            ("a_registered_provider_delivers_the_pasteboard_files", services),
            ("a_second_registration_is_refused", second_registration),
        ];
        for (name, case) in cases {
            case(mtm);
            println!("test {name} ... ok");
        }
        println!("main_thread_macapp: {} cases passed", cases.len());
    }

    fn set(policy: i32, activate: u8) -> i32 {
        unsafe { prisc_app_set_activation_policy_macos(policy, activate) }
    }

    fn activation_policies(mtm: MainThreadMarker) {
        let app = NSApplication::sharedApplication(mtm);
        let expected = [
            (P::Regular, NSApplicationActivationPolicy::Regular),
            (P::Prohibited, NSApplicationActivationPolicy::Prohibited),
            (P::Accessory, NSApplicationActivationPolicy::Accessory),
        ];
        for (policy, appkit) in expected {
            assert_eq!(set(policy as i32, 0), OK, "{policy:?} from {:?}", app.activationPolicy());
            assert_eq!(app.activationPolicy(), appkit, "{policy:?}");
        }
        // Activation applies to Regular only: an Accessory app asked to activate stays where it is.
        assert_eq!(set(P::Accessory as i32, 1), OK);
        assert_eq!(app.activationPolicy(), NSApplicationActivationPolicy::Accessory);
        assert!(!app.isActive(), "Accessory with activate_if_regular must not activate the app");
    }

    fn unknown_policy(mtm: MainThreadMarker) {
        let app = NSApplication::sharedApplication(mtm);
        let before = app.activationPolicy();
        assert_eq!(set(3, 1), S::InvalidArg as i32);
        assert_eq!(set(-1, 0), S::InvalidArg as i32);
        assert_eq!(app.activationPolicy(), before);
    }

    /// What the callback saw: the user token and the paths of each delivery.
    static DELIVERED: Mutex<Vec<(usize, Vec<String>)>> = Mutex::new(Vec::new());
    const USER: usize = 0x5EED;

    unsafe extern "C" fn on_files(user: *mut c_void, paths: *const PriscString, count: usize) {
        let paths = std::slice::from_raw_parts(paths, count)
            .iter()
            .map(|s| String::from_utf8(std::slice::from_raw_parts(s.ptr, s.len).to_vec()).expect("UTF-8 path"))
            .collect();
        DELIVERED
            .lock()
            .unwrap()
            .push((user as usize, paths));
    }

    unsafe extern "C" fn never_called(_user: *mut c_void, _paths: *const PriscString, _count: usize) {
        unreachable!("a refused registration must never be delivered to");
    }

    fn port_name() -> String {
        // Unique per run, so the test never takes over the shipped app's "Prisc" port.
        format!("PriscInteropTest.{}", std::process::id())
    }

    /// Invokes the plist's NSMessage on a provider instance with a private pasteboard holding `urls`,
    /// as the pasteboard server does for a Finder Services request.
    fn invoke(mtm: MainThreadMarker, urls: &[&str]) {
        let _ = mtm;
        let class = AnyClass::get(c"PriscServicesProvider").expect("the provider class is registered");
        let provider: Retained<AnyObject> = unsafe { msg_send![class, new] };
        let pasteboard = NSPasteboard::pasteboardWithUniqueName();
        pasteboard.clearContents();
        if !urls.is_empty() {
            let objects: Vec<Retained<ProtocolObject<dyn NSPasteboardWriting>>> = urls
                .iter()
                .map(|path| ProtocolObject::from_retained(NSURL::fileURLWithPath(&NSString::from_str(path))))
                .collect();
            assert!(pasteboard.writeObjects(&NSArray::from_retained_slice(&objects)));
        }
        let _: () = unsafe {
            msg_send![&*provider, uploadFiles: &*pasteboard, userData: ptr::null_mut::<NSString>(), error: ptr::null_mut::<*mut NSString>()]
        };
    }

    fn services(mtm: MainThreadMarker) {
        let name = port_name();
        let status = unsafe { prisc_services_register_provider_macos(name.as_ptr(), name.len(), Some(on_files), USER as *mut c_void) };
        assert_eq!(status, OK);

        invoke(mtm, &["/tmp/prisc services/a b.txt", "/tmp/Caméra.png"]);
        invoke(mtm, &[]);
        let delivered = DELIVERED.lock().unwrap().clone();
        // -[NSURL path] gives the file-system (decomposed) spelling, as it did for the C# provider.
        assert_eq!(
            delivered,
            vec![(
                USER,
                vec!["/tmp/prisc services/a b.txt".to_string(), "/tmp/Came\u{301}ra.png".to_string()]
            )],
            "one delivery for the request with files, none for the empty one"
        );
    }

    fn second_registration(mtm: MainThreadMarker) {
        let name = port_name();
        let status = unsafe { prisc_services_register_provider_macos(name.as_ptr(), name.len(), Some(never_called), ptr::null_mut()) };
        assert_eq!(status, S::State as i32);
        // The first registration still owns the deliveries.
        let count = DELIVERED.lock().unwrap().len();
        invoke(mtm, &["/tmp/again.txt"]);
        let delivered = DELIVERED.lock().unwrap();
        assert_eq!(delivered.len(), count + 1);
        assert_eq!(delivered[count], (USER, vec!["/tmp/again.txt".to_string()]));
    }
}
