//! Windows GPU: the Direct3D 12 device and its Direct3D 11 twin, the readback ring and the NV12
//! converter (`prisc_gpu_*`).
//!
//! This file carries what the three children share: the `FreeThreaded` newtype that makes a COM
//! interface wrapper `Send + Sync` with the justification written down, the capability half of
//! the HRESULT to status mapping, and the one bounded CPU wait on a Direct3D 12 fence.
//!
//! # The reference graph
//!
//! Every handle this area hands out owns an `Arc` on its own innards, and every innards that needs
//! another one holds an `Arc` on it: `ConverterInner -> RingInner -> DeviceInner`, with a second
//! direct edge `ConverterInner -> DeviceInner` for the Direct3D 11 device. The NV12 encoder
//! textures are registered in a crate-global table (`nv12.rs`) whose entries each own an
//! `Arc<ConverterInner>`, so a texture FFmpeg frees after the converter handle was destroyed still
//! finds everything it needs, and the last texture back drops the converter's innards. A
//! `_destroy` performs its own side effects (the ring's drain, the converter's bounded drain) and
//! then drops its `Arc`; the COM interfaces are released in the innards' `Drop`, when the last
//! `Arc` goes. A `_destroy` out of order is therefore a delay, never a use after free, which
//! matters because the C# wrappers are `SafeHandle`s and a leaked factory's finalizers run in
//! whatever order the runtime picks.

pub(crate) mod device;
pub(crate) mod nv12;
pub(crate) mod ring;

use std::ops::Deref;

use windows::Win32::Foundation::{CloseHandle, GetLastError, HANDLE, WAIT_OBJECT_0, WAIT_TIMEOUT};
use windows::Win32::Graphics::Direct3D12::ID3D12Fence;
use windows::Win32::System::Threading::{CreateEventW, WaitForSingleObject};
use windows_core::{Interface, HRESULT, PCWSTR};

use crate::error::{Error, Result};
use crate::platform::windows::{hresult_error, os_error};

/// A COM interface wrapper that may be used from any thread.
///
/// The generated windows 0.62 interface wrappers (`ID3D12Device`, `ID3D11DeviceContext4`, ...) are
/// `#[repr(transparent)]` newtypes over a raw pointer and implement **neither `Send` nor `Sync`**.
/// Every handle in this area is reached from more than one thread (the composer thread begins and
/// submits, the convert thread converts, the encoder's own threads release pool textures, and a
/// `SafeHandle` finalizer can destroy from anywhere), so the wrappers carry the claim explicitly.
///
/// **Why the claim is true.** Direct3D 12 devices, command queues, fences and resources are
/// free-threaded by the Direct3D 12 specification. The Direct3D 11 immediate context is not
/// free-threaded by default and is made so by `ID3D11Multithread::SetMultithreadProtected(TRUE)`,
/// which `device.rs` enables the moment it creates the Direct3D 11 device. DXGI factories and
/// adapters are free-threaded as well. Command allocators and lists are not, and the ones this
/// area keeps (the ring's recorded copies and its clear list) are only ever touched under the slot
/// state machine or a mutex.
///
/// The bound is `Interface` rather than a blanket `T` so this cannot become a general "make
/// anything Send" tool.
pub(crate) struct FreeThreaded<T: Interface>(T);

// SAFETY: see the type's doc comment. Every interface this crate puts in a FreeThreaded is a
// Direct3D 12, DXGI or multithread-protected Direct3D 11 object, or a command list or allocator
// that its owner serialises.
unsafe impl<T: Interface> Send for FreeThreaded<T> {}
// SAFETY: same, for shared references.
unsafe impl<T: Interface> Sync for FreeThreaded<T> {}

impl<T: Interface> FreeThreaded<T> {
    pub(crate) fn new(interface: T) -> Self {
        FreeThreaded(interface)
    }

    /// The interface itself, for the call sites that pass it as a parameter.
    pub(crate) fn get(&self) -> &T {
        &self.0
    }

    /// One more reference on the underlying object, as an owned wrapper (`Clone` is `AddRef`).
    pub(crate) fn clone_interface(&self) -> T {
        self.0.clone()
    }
}

impl<T: Interface> Deref for FreeThreaded<T> {
    type Target = T;

    fn deref(&self) -> &T {
        &self.0
    }
}

/// `PRISC_E_CAPABILITY` for a step the hardware or driver cannot do, with the HRESULT as the
/// last-error code.
///
/// The set of callers is closed: the conditions `D3D11EncodeBridge.IsMissingCapability` matches on
/// (the four QueryInterface tiers, `CreateVideoProcessorEnumerator`, `CreateVideoProcessor`; the
/// non-shareable ring sets the status directly). Everything else, including a refused
/// `D3D11CreateDevice`, a LUID mismatch and an absent `CheckVideoProcessorFormat` support bit, is
/// `PRISC_E_OS` through `hresult_error`. The message keeps the `"{step} failed (0x{hr:08X})"`
/// shape and the step names the C# predicate matches.
pub(crate) fn capability_error(hr: HRESULT, what: &str) -> Error {
    Error {
        code: i64::from(hr.0),
        ..Error::capability(format!("{what} failed (0x{:08X})", hr.0 as u32))
    }
}

/// `PRISC_E_OS` for a call that reported success without handing back its object.
pub(crate) fn missing_object(what: &str) -> Error {
    Error::os(0, format!("{what} succeeded without returning an object"))
}

/// A Win32 event handle closed on drop. Unnamed, auto-reset, initially unset.
pub(crate) struct Event(HANDLE);

// SAFETY: a Win32 event handle is a process-wide kernel object reference, not thread-affine state:
// WaitForSingleObject, SetEventOnCompletion and CloseHandle all accept it from any thread. windows
// 0.62 models HANDLE as a raw pointer newtype, which is the only reason it is not Send already.
unsafe impl Send for Event {}

impl Event {
    pub(crate) fn new() -> Result<Event> {
        let handle = unsafe { CreateEventW(None, false, false, PCWSTR::null()) }.map_err(|e| os_error(&e, "CreateEventW"))?;
        Ok(Event(handle))
    }

    pub(crate) fn handle(&self) -> HANDLE {
        self.0
    }
}

impl Drop for Event {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            let _ = unsafe { CloseHandle(self.0) };
        }
    }
}

/// How a bounded fence wait ended.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum FenceWait {
    Reached,
    TimedOut,
}

/// Blocks until `fence` reaches `value` or `timeout_ms` passes (`u32::MAX` is INFINITE), on
/// `event`. Re-reads the fence after every wake, so a stale signal from an earlier
/// `SetEventOnCompletion` on the same event is harmless. A fence at `u64::MAX` is a removed device
/// and comes back as the error `device_removed` builds.
pub(crate) fn wait_fence(
    fence: &ID3D12Fence,
    value: u64,
    timeout_ms: u32,
    event: &Event,
    device_removed: impl Fn() -> Error,
) -> Result<FenceWait> {
    loop {
        let completed = unsafe { fence.GetCompletedValue() };
        if completed == u64::MAX {
            return Err(device_removed());
        }
        if completed >= value {
            return Ok(FenceWait::Reached);
        }
        unsafe { fence.SetEventOnCompletion(value, event.handle()) }.map_err(|e| os_error(&e, "ID3D12Fence::SetEventOnCompletion"))?;
        let woken = unsafe { WaitForSingleObject(event.handle(), timeout_ms) };
        if woken == WAIT_OBJECT_0 {
            continue;
        }
        if woken != WAIT_TIMEOUT {
            let code = unsafe { GetLastError() };
            return Err(hresult_error(code.to_hresult(), "WaitForSingleObject"));
        }
        // A timeout the fence has since overtaken is not a timeout: one more turn reads it again.
        if unsafe { fence.GetCompletedValue() } < value {
            return Ok(FenceWait::TimedOut);
        }
    }
}
