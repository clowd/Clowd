//! macOS: the NSServices provider (`prisc_services_register_provider_macos`).
//!
//! `PriscServicesProvider` keeps the runtime class name the hand-written `objc_allocateClassPair`
//! gave it, so crash logs read the same. It holds no state: the callback lives in [`SINK`] only.
//! Delivery and registration both run on the main thread, so no delivery can race a registration.
//!
//! `-[NSApplication setServicesProvider:]` is deliberately not used: it registers the listening
//! port under the executable name (`Prisc.Ui`), while delivery looks the port up by the advertised
//! `NSPortName` (`Prisc`).

use std::ffi::c_void;
use std::sync::atomic::{AtomicPtr, Ordering};
use std::sync::{Mutex, PoisonError};

use objc2::rc::Retained;
use objc2::runtime::{AnyObject, NSObject};
use objc2::{define_class, msg_send, ClassType, MainThreadMarker, MainThreadOnly};
use objc2_app_kit::{NSPasteboard, NSRegisterServicesProvider, NSUpdateDynamicServices};
use objc2_foundation::{NSArray, NSString, NSURL};

use crate::error::{Error, Result};
use crate::ffi::log::PriscInteropLogLevel;
use crate::ffi::status::PRISC_OK;
use crate::ffi::types::PriscString;
use crate::panic::{self, CallbackPanic};
use crate::platform::macos::main_thread;

/// The callback the provider delivers to.
pub(crate) type FilesCallback = unsafe extern "C" fn(user: *mut c_void, paths: *const PriscString, count: usize);

/// Where a delivery goes: the callback and the caller's `user` token (kept as a usize so the
/// static is `Sync`; handed back verbatim, never dereferenced). Set once, for the process.
static SINK: Mutex<Option<(FilesCallback, usize)>> = Mutex::new(None);

/// The provider instance, created on the first registration and never released: AppKit holds a
/// services provider unretained, so releasing it would leave the pasteboard server messaging a
/// freed object. A raw pointer because a `Retained` of a main-thread-only class is not `Sync`.
static PROVIDER: AtomicPtr<AnyObject> = AtomicPtr::new(std::ptr::null_mut());

/// The panic slot of the delivery trampoline. It has no handle and no later export to report
/// through, so a caught panic is reported to the host's log sink straight away.
static DELIVERY_PANIC: CallbackPanic = CallbackPanic::new();

define_class!(
    // SAFETY: NSObject has no subclassing requirements; no ivars and no Drop impl, and the one
    // instance is never deallocated.
    #[unsafe(super(NSObject))]
    // AppKit only invokes a services provider on the main thread.
    #[thread_kind = MainThreadOnly]
    #[name = "PriscServicesProvider"]
    struct PriscServicesProvider;

    impl PriscServicesProvider {
        /// The plist's `NSMessage` (`v@:@@^@`): the pasteboard, the user data and an optional out
        /// error string a provider may leave alone.
        #[unsafe(method(uploadFiles:userData:error:))]
        fn upload_files(&self, pasteboard: Option<&NSPasteboard>, _user_data: *mut NSString, _error: *mut *mut NSString) {
            panic::callback_guard(&DELIVERY_PANIC, (), || deliver(pasteboard));
            if let Some(text) = DELIVERY_PANIC.take() {
                crate::ffi::log::emit(PriscInteropLogLevel::Error, &format!("the Services delivery failed: {text}"));
            }
        }
    }
);

/// Reads the file paths off the pasteboard and hands them to the callback, when one is armed and
/// the request carried at least one file.
fn deliver(pasteboard: Option<&NSPasteboard>) {
    // Copied out under the lock and released before calling out, so a callback that re-enters
    // the library cannot deadlock against this delivery.
    let sink = *SINK
        .lock()
        .unwrap_or_else(PoisonError::into_inner);
    let (Some((callback, user)), Some(pasteboard)) = (sink, pasteboard) else {
        return;
    };
    let paths = file_paths(pasteboard);
    if paths.is_empty() {
        return;
    }
    // Borrowed views (mode 3): the Strings stay owned here until the callback has returned.
    let views: Vec<PriscString> = paths
        .iter()
        .map(|path| PriscString {
            ptr: path.as_ptr().cast_mut(),
            len: path.len(),
        })
        .collect();
    unsafe { callback(user as *mut c_void, views.as_ptr(), views.len()) };
}

/// `[pasteboard readObjectsForClasses:@[NSURL.class] options:nil]`, then each URL's path; anything
/// that is not a URL or has no path is skipped.
pub(crate) fn file_paths(pasteboard: &NSPasteboard) -> Vec<String> {
    let classes = NSArray::from_slice(&[NSURL::class()]);
    // SAFETY: the class array names NSURL, whose instances are downcast below; nil options are
    // documented as "no options".
    let Some(objects) = (unsafe { pasteboard.readObjectsForClasses_options(&classes, None) }) else {
        return Vec::new();
    };
    objects
        .iter()
        .filter_map(|object| object.downcast::<NSURL>().ok())
        .filter_map(|url| url.path())
        .map(|path| path.to_string())
        .filter(|path| !path.is_empty())
        .collect()
}

/// The process's provider, created on first use.
fn provider(mtm: MainThreadMarker) -> *mut AnyObject {
    let existing = PROVIDER.load(Ordering::Acquire);
    if !existing.is_null() {
        return existing;
    }
    // SAFETY: -init on a fresh NSObject subclass with no ivars.
    let instance: Retained<PriscServicesProvider> = unsafe { msg_send![PriscServicesProvider::alloc(mtm), init] };
    // Leaked on purpose (see PROVIDER).
    let raw = Retained::into_raw(instance).cast::<AnyObject>();
    PROVIDER.store(raw, Ordering::Release);
    raw
}

/// Arms the callback, registers the port and refreshes the services cache.
pub(crate) fn register(port_name: &str, on_files: FilesCallback, user: *mut c_void) -> Result {
    let mtm = main_thread("prisc_services_register_provider_macos")?;
    {
        // Armed before AppKit hears of the provider, and the lock is not held across an AppKit
        // call: a delivery dispatched while NSUpdateDynamicServices runs takes the same lock.
        let mut sink = SINK
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        if sink.is_some() {
            return Err(Error::state("a Services provider is already registered in this process"));
        }
        *sink = Some((on_files, user as usize));
    }
    let instance = provider(mtm);
    let name = NSString::from_str(port_name);
    // SAFETY: `instance` is a live PriscServicesProvider that is never deallocated, which is what
    // NSRegisterServicesProvider needs, since it does not retain the provider.
    unsafe { NSRegisterServicesProvider(Some(&*instance), &name) };
    NSUpdateDynamicServices();
    Ok(PRISC_OK)
}
