//! The macOS Services provider (WP-MACAPP; README section 4.5).

use std::ffi::c_void;

use crate::error::Error;
use crate::ffi::types::PriscString;
use crate::panic;

/// Called with the file paths a Services request carries; `paths` are borrowed for the call
/// (mode 3). May call exports (README R20).
pub type PriscServicesFilesCallback = Option<unsafe extern "C" fn(user: *mut c_void, paths: *const PriscString, count: usize)>;

/// macOS: registers the Services provider under the UTF-8 `port_name` and refreshes the dynamic
/// services; `on_files` receives the paths of each request.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`, `PRISC_E_UNSUPPORTED` off macOS.
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
        let _ = (port_name, len, on_files, user);
        Err(Error::unsupported("prisc_services_register_provider_macos is not implemented yet"))
    })
}
