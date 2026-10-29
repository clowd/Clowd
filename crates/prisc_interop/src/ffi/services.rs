//! The macOS Services provider behind Finder's "Upload with Prisc" (README section 4.5).
//!
//! macOS delivers a service invocation to the process that declared it, through an Objective-C
//! object registered under the port name the bundle's `Info.plist` advertises (`NSPortName`),
//! whose method name matches the plist's `NSMessage` (`uploadFiles:userData:error:`). That class
//! is defined once in Rust (`define_class!`); C# supplies only the callback.

use std::ffi::c_void;

#[cfg(target_os = "macos")]
use crate::platform::macos::services;

use crate::error::{Error, Result};
use crate::ffi::types::PriscString;
use crate::{panic, util};

/// Called with the file paths a Services request carries; `paths` are borrowed for the call
/// (mode 3). May call exports (README R20).
pub type PriscServicesFilesCallback = Option<unsafe extern "C" fn(user: *mut c_void, paths: *const PriscString, count: usize)>;

/// macOS: registers the Services provider under the UTF-8 `port_name` (`len` bytes, not
/// NUL-terminated; it must equal the bundle's `NSServices` `NSPortName`) and refreshes the
/// pasteboard server's services cache (`NSUpdateDynamicServices`), so an updated bundle's menu
/// entry shows without a re-login.
///
/// `on_files` runs on the main thread, inside AppKit's dispatch, once per invocation that carried
/// at least one file: `paths` points at `count` borrowed UTF-8 paths (mode 3, valid only during
/// the call). It must not let an exception escape. `user` is handed back unchanged and never
/// dereferenced. The registration lasts for the process (no caller unregisters; AppKit holds the
/// provider unretained), so `user` must stay valid for the process too.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for an empty or non-UTF-8 `port_name` or a null
/// `on_files` (checked first, on every OS and thread); `PRISC_E_STATE` off the main thread or when
/// a provider is already registered; `PRISC_E_UNSUPPORTED` off macOS.
///
/// Thread: must be the macOS main thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_services_register_provider_macos(
    port_name: *const u8,
    len: usize,
    on_files: PriscServicesFilesCallback,
    user: *mut c_void,
) -> i32 {
    panic::guard("prisc_services_register_provider_macos", || {
        register_provider(port_name, len, on_files, user)
    })
}

unsafe fn register_provider(port_name: *const u8, len: usize, on_files: PriscServicesFilesCallback, user: *mut c_void) -> Result {
    let port_name = util::utf8(port_name, len, "port_name")?;
    if port_name.is_empty() {
        return Err(Error::invalid_arg("port_name is empty"));
    }
    let on_files = on_files.ok_or_else(|| Error::null("on_files"))?;
    #[cfg(target_os = "macos")]
    {
        services::register(port_name, on_files, user)
    }
    #[cfg(not(target_os = "macos"))]
    {
        let _ = (on_files, user);
        Err(Error::unsupported("the Services provider exists only on macOS"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus as S;

    fn register(name: &[u8]) -> i32 {
        unsafe { prisc_services_register_provider_macos(name.as_ptr(), name.len(), None, std::ptr::null_mut()) }
    }

    /// Checked before the thread, so they hold on every OS and thread. The port name is checked
    /// before the callback; the off-main-thread refusal of a valid call is in tests/macapp.rs.
    #[test]
    fn bad_arguments_are_invalid_arg_naming_the_parameter() {
        assert_eq!(register(b""), S::InvalidArg as i32);
        assert_eq!(crate::error::snapshot().message, "port_name is empty");
        assert_eq!(register(&[0xFF, 0xFE]), S::InvalidArg as i32);
        assert!(crate::error::snapshot()
            .message
            .starts_with("port_name is not UTF-8"));
        assert_eq!(
            unsafe { prisc_services_register_provider_macos(std::ptr::null(), 5, None, std::ptr::null_mut()) },
            S::InvalidArg as i32
        );
        assert_eq!(register(b"Prisc"), S::InvalidArg as i32);
        assert_eq!(crate::error::snapshot().message, "on_files was null");
    }
}
