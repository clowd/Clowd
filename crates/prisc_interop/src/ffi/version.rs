//! What this library is: the ABI it speaks and the version it was built from (README E).

use crate::error::Result;
use crate::ffi::status::PRISC_OK;
use crate::ffi::types::PriscString;
use crate::{panic, util};

/// This library's version: `PRISC_VERSION` when the build stamped one (CI and build.sh do, the
/// same value prisc_rust_core's telemetry release uses), else `CARGO_PKG_VERSION`.
pub(crate) fn version() -> &'static str {
    match option_env!("PRISC_VERSION") {
        Some(stamped) if !stamped.is_empty() => stamped,
        _ => env!("CARGO_PKG_VERSION"),
    }
}

/// Writes the ABI fingerprint of this build to `out_hash`: a 64-bit FNV-1a over every export
/// signature, `#[repr(C)]` struct and `#[repr]` enum in `src/ffi` and the hotkey key table
/// (README E1). The C# loader calls this through `NativeLibrary.TryGetExport` before any
/// DllImport resolves, and refuses a library whose value differs from the generated
/// `InteropAbi.Hash` ("stale bindings: run cargo codegen").
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `out_hash`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_interop_abi_hash(out_hash: *mut u64) -> i32 {
    panic::guard("prisc_interop_abi_hash", || abi_hash(out_hash))
}

unsafe fn abi_hash(out_hash: *mut u64) -> Result {
    util::write(out_hash, "out_hash", crate::ABI_HASH)?;
    Ok(PRISC_OK)
}

/// Writes this library's version string to `out_version` as a standalone owned string (mode 1:
/// free it with `prisc_string_free`). `PRISC_VERSION` stamped at build time, `1.0.0` in a plain
/// `cargo build`.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `out_version`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_interop_version(out_version: *mut PriscString) -> i32 {
    panic::guard("prisc_interop_version", || write_version(out_version))
}

unsafe fn write_version(out_version: *mut PriscString) -> Result {
    util::write(out_version, "out_version", PriscString::from_string(version()))?;
    Ok(PRISC_OK)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus;

    #[test]
    fn abi_hash_is_the_built_fingerprint() {
        let mut hash = 0u64;
        assert_eq!(unsafe { prisc_interop_abi_hash(&mut hash) }, 0);
        assert_eq!(hash, crate::ABI_HASH);
        assert_ne!(hash, 0);
        assert_eq!(
            unsafe { prisc_interop_abi_hash(std::ptr::null_mut()) },
            PriscInteropStatus::InvalidArg as i32
        );
    }

    #[test]
    fn version_is_an_owned_string() {
        let mut s = PriscString::empty();
        assert_eq!(unsafe { prisc_interop_version(&mut s) }, 0);
        assert_eq!(unsafe { s.as_str() }, version());
        unsafe { s.release() };
    }
}
