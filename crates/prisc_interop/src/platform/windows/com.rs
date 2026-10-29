//! The crate's COM policy, in one type (README C4).
//!
//! The crate never leaves an apartment initialised on a thread it did not create, and never
//! changes a host thread's apartment model. C# code reached the same COM APIs only because the CLR
//! had already initialised the calling thread's apartment; a Rust `CoCreateInstance` reached
//! through a P/Invoke gets no such favour and would answer `CO_E_NOTINITIALIZED` on a thread-pool
//! thread where the C# version succeeded.

use windows::Win32::Foundation::{RPC_E_CHANGED_MODE, S_FALSE};
use windows::Win32::System::Com::{CoInitializeEx, CoUninitialize, COINIT_MULTITHREADED};

use crate::error::Result;
use crate::platform::windows::hresult_error;

/// COM for the duration of one export call, balanced on drop, without ever changing a host
/// thread's apartment model. Exports that hand a COM object back across the boundary must not use
/// it (the object would outlive the apartment); D3D12, D3D11 and DXGI creation need no apartment.
pub(crate) struct ScopedApartment {
    owns_balance: bool,
}

impl ScopedApartment {
    /// Joins (or establishes) the multithreaded apartment for this call. Tri-state:
    /// - `S_OK` / `S_FALSE`: COM is initialised by (or for) this call, which owns one
    ///   `CoUninitialize`.
    /// - `RPC_E_CHANGED_MODE`: the thread already has an STA (Avalonia's UI thread, a CLR STA
    ///   thread); COM is usable, and uninitialising would unbalance the host, so the drop does
    ///   nothing.
    /// - anything else: `PRISC_E_OS` with the HRESULT.
    pub(crate) fn multithreaded() -> Result<ScopedApartment> {
        let hr = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
        if hr.is_ok() || hr == S_FALSE {
            return Ok(ScopedApartment {
                owns_balance: true,
            });
        }
        if hr == RPC_E_CHANGED_MODE {
            return Ok(ScopedApartment {
                owns_balance: false,
            });
        }
        Err(hresult_error(hr, "CoInitializeEx"))
    }
}

impl Drop for ScopedApartment {
    fn drop(&mut self) {
        if self.owns_balance {
            unsafe { CoUninitialize() };
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn nested_apartments_balance() {
        let outer = ScopedApartment::multithreaded().expect("MTA");
        let inner = ScopedApartment::multithreaded().expect("MTA again");
        assert!(outer.owns_balance);
        assert!(inner.owns_balance);
    }
}
