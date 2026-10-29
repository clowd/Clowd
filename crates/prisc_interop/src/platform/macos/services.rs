//! macOS: the NSServices provider (`prisc_services_register_provider_macos`).
//!
//! `PriscServicesProvider` keeps the runtime class name the hand-written `objc_allocateClassPair`
//! gave it, so crash logs read the same. It holds no state: the callback lives in [`SINK`] only.
//! Delivery and registration both run on the main thread, so no delivery can race a registration.
//!
//! The class is built at run time with [`ClassBuilder`], not with objc2's `define_class!`: that
//! macro gives every class `#[export_name]` statics (`__CLASS_`, `__IVAR_OFFSET_`,
//! `__DROP_FLAG_OFFSET_`, `__REGISTER_CLASS_`), which a cdylib exports beside the `prisc_*`
//! surface (prisc_codegen's lint rule 14 keeps `define_class!` out of the crate).
//!
//! `-[NSApplication setServicesProvider:]` is deliberately not used: it registers the listening
//! port under the executable name (`Prisc.Ui`), while delivery looks the port up by the advertised
//! `NSPortName` (`Prisc`).

use std::ffi::{c_void, CStr};
use std::sync::atomic::{AtomicPtr, Ordering};
use std::sync::{Mutex, OnceLock, PoisonError};

use objc2::rc::Retained;
use objc2::runtime::{AnyClass, AnyObject, ClassBuilder, NSObject, Sel};
use objc2::{msg_send, sel, ClassType};
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

/// The provider's Objective-C class name.
const CLASS_NAME: &CStr = c"PriscServicesProvider";

/// Where a delivery goes: the callback and the caller's `user` token (kept as a usize so the
/// static is `Sync`; handed back verbatim, never dereferenced). Set once, for the process.
static SINK: Mutex<Option<(FilesCallback, usize)>> = Mutex::new(None);

/// The provider instance, created on the first registration and never released: AppKit holds a
/// services provider unretained, so releasing it would leave the pasteboard server messaging a
/// freed object. A raw pointer because the instance is only ever messaged on the main thread.
static PROVIDER: AtomicPtr<AnyObject> = AtomicPtr::new(std::ptr::null_mut());

/// The registered class, or why it could not be registered (another image in the process already
/// registered the name).
static CLASS: OnceLock<std::result::Result<&'static AnyClass, String>> = OnceLock::new();

/// The panic slot of the delivery trampoline. It has no handle and no later export to report
/// through, so a caught panic is reported to the host's log sink straight away.
static DELIVERY_PANIC: CallbackPanic = CallbackPanic::new();

/// `-uploadFiles:userData:error:`, the plist's `NSMessage` (`v@:@@^@`): the pasteboard, the user
/// data and an optional out error string a provider may leave alone. AppKit only invokes a
/// services provider on the main thread.
unsafe extern "C" fn upload_files(
    _this: *mut AnyObject,
    _cmd: Sel,
    pasteboard: *mut NSPasteboard,
    _user_data: *mut NSString,
    _error: *mut *mut NSString,
) {
    panic::callback_guard(&DELIVERY_PANIC, (), || {
        // SAFETY: AppKit passes the request's pasteboard (or nil), alive for this call.
        deliver(unsafe { pasteboard.as_ref() })
    });
    if let Some(text) = DELIVERY_PANIC.take() {
        crate::ffi::log::emit(PriscInteropLogLevel::Error, &format!("the Services delivery failed: {text}"));
    }
}

/// The provider class: an NSObject subclass with the one Services method, registered once.
fn provider_class() -> Result<&'static AnyClass> {
    CLASS
        .get_or_init(|| {
            let Some(mut builder) = ClassBuilder::new(CLASS_NAME, NSObject::class()) else {
                return Err(format!(
                    "another image in this process already registered the Objective-C class {}",
                    CLASS_NAME.to_string_lossy()
                ));
            };
            // SAFETY: the function's argument and return types are the selector's (`v@:@@^@`);
            // NSObject has no subclassing requirements, and the class adds no ivars.
            unsafe {
                builder.add_method(
                    sel!(uploadFiles:userData:error:),
                    upload_files as unsafe extern "C" fn(_, _, _, _, _),
                );
            }
            Ok(builder.register())
        })
        .clone()
        .map_err(Error::state)
}

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
fn provider() -> Result<*mut AnyObject> {
    let existing = PROVIDER.load(Ordering::Acquire);
    if !existing.is_null() {
        return Ok(existing);
    }
    let class = provider_class()?;
    // SAFETY: +new on an NSObject subclass with no ivars.
    let instance: Retained<AnyObject> = unsafe { msg_send![class, new] };
    // Leaked on purpose (see PROVIDER).
    let raw = Retained::into_raw(instance);
    PROVIDER.store(raw, Ordering::Release);
    Ok(raw)
}

/// Arms the callback, registers the port and refreshes the services cache.
pub(crate) fn register(port_name: &str, on_files: FilesCallback, user: *mut c_void) -> Result {
    main_thread("prisc_services_register_provider_macos")?;
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
    let instance = match provider() {
        Ok(instance) => instance,
        Err(error) => {
            // Disarmed again, so the failure is not mistaken for a registration.
            *SINK
                .lock()
                .unwrap_or_else(PoisonError::into_inner) = None;
            return Err(error);
        }
    };
    let name = NSString::from_str(port_name);
    // SAFETY: `instance` is a live PriscServicesProvider that is never deallocated, which is what
    // NSRegisterServicesProvider needs, since it does not retain the provider.
    unsafe { NSRegisterServicesProvider(Some(&*instance), &name) };
    NSUpdateDynamicServices();
    Ok(PRISC_OK)
}
