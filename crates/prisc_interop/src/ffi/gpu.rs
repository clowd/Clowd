//! The GPU device, the D3D12 readback ring and the NV12 converter (WP-GPU; README section 4.7).
//! Raw OS pointers handed out are borrowed from their handle, except the D3D11 device that
//! `prisc_gpu_d3d11_device_acquire_windows` transfers to FFmpeg (README C2).
//!
//! Rendering (Skia) and codecs (FFmpeg) stay in C#. What crosses here is the device and queue
//! pointers Skia's backend contexts need, the ring's slot textures and mapped readback memory, and
//! the NV12 textures FFmpeg encodes from. Inside the crate the handles hold an `Arc` chain
//! (converter -> ring -> device, and every outstanding NV12 texture -> converter), so a `_destroy`
//! in the wrong order delays a release instead of freeing an object another handle still uses.

use std::ffi::c_void;
use std::sync::Arc;

#[cfg(target_os = "macos")]
use crate::platform::macos::gpu as metal;
#[cfg(windows)]
use crate::platform::windows::gpu::{device as d3d, nv12, ring};

use crate::convert;
use crate::error::{Error, Result};
use crate::ffi::status::PRISC_OK;
use crate::ffi::types::PriscString;
use crate::panic;
use crate::util;

/// The GPU device handle.
pub struct PriscGpuDevice {
    _private: [u8; 0],
}

/// The D3D12 readback ring handle (Windows).
pub struct PriscGpuReadbackRing {
    _private: [u8; 0],
}

/// The NV12 converter handle (Windows).
pub struct PriscGpuNv12Converter {
    _private: [u8; 0],
}

/// Which API a GPU device speaks.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscGpuBackend {
    None = 0,
    D3D12 = 1,
    Metal = 2,
}

crate::convert::ffi_enum!(PriscGpuBackend: i32 { None, D3D12, Metal });

/// Device creation options; bit flags (`[Flags]` in C#).
#[repr(u32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscGpuDeviceFlags {
    None = 0,
    /// Windows: a software (WARP) adapter may be chosen when no hardware adapter qualifies (tests).
    AllowSoftwareAdapter = 1,
}

/// What `prisc_gpu_device_create` reports (64 bytes). Pointers are borrowed from the device;
/// `adapter_name` is mode 3.
#[repr(C)]
#[derive(Debug)]
pub struct PriscGpuDeviceInfo {
    /// A `PriscGpuBackend`.
    pub backend: i32,
    pub _reserved: u32,
    pub adapter_name: PriscString,
    /// `IDXGIAdapter1*`; null off Windows.
    pub d3d12_adapter: *mut c_void,
    /// `ID3D12Device*`; null off Windows.
    pub d3d12_device: *mut c_void,
    /// `ID3D12CommandQueue*` (direct); null off Windows.
    pub d3d12_queue: *mut c_void,
    /// `id<MTLDevice>`; null off macOS.
    pub metal_device: *mut c_void,
    /// `id<MTLCommandQueue>`; null off macOS.
    pub metal_queue: *mut c_void,
}

/// What `prisc_gpu_readback_ring_create_windows` reports (24 bytes).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PriscGpuRingInfo {
    pub width: u32,
    pub height: u32,
    pub slots: u32,
    pub row_pitch: u32,
    /// 1 when the slots are shareable with the D3D11 twin (the zero-copy path).
    pub shareable: u8,
    pub _reserved: [u8; 7],
}

// The handles are a `Box<Arc<Inner>>` behind the opaque pointer: the box keeps the pointer stable,
// the `Arc` is what rings, converters and textures share. The C# SafeHandle and its lease make
// sure no call is in flight across the one `_destroy`.

/// Boxes an `Arc` as an opaque handle.
#[allow(dead_code)] // Linux has no GPU handles
fn into_handle<T, H>(inner: Arc<T>) -> *mut H {
    Box::into_raw(Box::new(inner)).cast::<H>()
}

/// The innards behind a live handle; `PRISC_E_INVALID_ARG` for null.
///
/// # Safety
/// `handle` is null or came from `into_handle::<T, H>` and has not been destroyed.
#[allow(dead_code)]
unsafe fn live<'a, T, H>(handle: *mut H, name: &str) -> Result<&'a Arc<T>> {
    handle
        .cast::<Arc<T>>()
        .as_ref()
        .ok_or_else(|| Error::null(name))
}

/// Frees a handle and gives back its `Arc`; `None` for null.
///
/// # Safety
/// As [`live`], and this is the handle's only destroy.
#[allow(dead_code)]
unsafe fn take_handle<T, H>(handle: *mut H) -> Option<Arc<T>> {
    (!handle.is_null()).then(|| *Box::from_raw(handle.cast::<Arc<T>>()))
}

/// A mode 3 string borrowed from text the handle owns.
#[allow(dead_code)]
fn borrowed(text: &str) -> PriscString {
    if text.is_empty() {
        return PriscString::empty();
    }
    PriscString {
        ptr: text.as_ptr().cast_mut(),
        len: text.len(),
    }
}

#[allow(dead_code)]
fn windows_only(export: &str) -> Error {
    Error::unsupported(format!("{export} is implemented on Windows only"))
}

/// Creates the GPU device: D3D12 on the first hardware adapter with feature level 11.0 and a direct
/// queue (Windows; a software adapter only with `AllowSoftwareAdapter`), the system default Metal
/// device and a queue (macOS). `out_info` is filled in full; its pointers and `adapter_name` are
/// borrowed from the device and valid until `prisc_gpu_device_destroy`.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` (null out pointer, unknown flag bit);
/// `PRISC_E_CAPABILITY` when no adapter qualifies or the Mac has no Metal device (composite on the
/// CPU); `PRISC_E_OS`; `PRISC_E_UNSUPPORTED` off Windows and macOS.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_device_create(
    flags: u32,
    out_device: *mut *mut PriscGpuDevice,
    out_info: *mut PriscGpuDeviceInfo,
) -> i32 {
    panic::guard("prisc_gpu_device_create", || {
        let flags = convert::flags(flags, PriscGpuDeviceFlags::AllowSoftwareAdapter as u32, "flags")?;
        let out_device = util::out(out_device, "out_device")?;
        let out_info = util::out(out_info, "out_info")?;
        #[cfg(windows)]
        {
            let inner = d3d::create(flags & PriscGpuDeviceFlags::AllowSoftwareAdapter as u32 != 0)?;
            *out_info = PriscGpuDeviceInfo {
                backend: PriscGpuBackend::D3D12 as i32,
                _reserved: 0,
                adapter_name: borrowed(inner.name()),
                d3d12_adapter: inner.adapter_raw(),
                d3d12_device: inner.device_raw(),
                d3d12_queue: inner.queue_raw(),
                metal_device: std::ptr::null_mut(),
                metal_queue: std::ptr::null_mut(),
            };
            *out_device = into_handle(inner);
            Ok(PRISC_OK)
        }
        #[cfg(target_os = "macos")]
        {
            let _ = flags; // no software adapters on macOS: a truthful no-op
            let inner = metal::create()?;
            *out_info = PriscGpuDeviceInfo {
                backend: PriscGpuBackend::Metal as i32,
                _reserved: 0,
                adapter_name: borrowed(inner.name()),
                d3d12_adapter: std::ptr::null_mut(),
                d3d12_device: std::ptr::null_mut(),
                d3d12_queue: std::ptr::null_mut(),
                metal_device: inner.device_raw(),
                metal_queue: inner.queue_raw(),
            };
            *out_device = into_handle(inner);
            Ok(PRISC_OK)
        }
        #[cfg(not(any(windows, target_os = "macos")))]
        {
            let _ = (flags, out_device, out_info);
            Err(Error::unsupported(
                "prisc_gpu_device_create is implemented on Windows and macOS only",
            ))
        }
    })
}

/// Releases the device handle; a ring or converter created from it keeps what it needs alive, and
/// the Direct3D 11 reference FFmpeg holds stays valid. Null is `PRISC_OK`.
///
/// Status: `PRISC_OK`.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_device_destroy(device: *mut PriscGpuDevice) -> i32 {
    panic::guard("prisc_gpu_device_destroy", || {
        #[cfg(windows)]
        drop(take_handle::<d3d::DeviceInner, _>(device));
        #[cfg(target_os = "macos")]
        drop(take_handle::<metal::DeviceInner, _>(device));
        #[cfg(not(any(windows, target_os = "macos")))]
        let _ = device;
        Ok(PRISC_OK)
    })
}

/// Windows: the D3D11 device on the same adapter (created on first use, then shared with the NV12
/// converter) with a reference transferred to the caller: FFmpeg's `AVD3D11VADeviceContext`
/// releases it exactly once. A refused `D3D11CreateDevice` and an adapter LUID mismatch are
/// `PRISC_E_OS`.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG`; `PRISC_E_OS`; `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_d3d11_device_acquire_windows(device: *mut PriscGpuDevice, out_d3d11: *mut *mut c_void) -> i32 {
    panic::guard("prisc_gpu_d3d11_device_acquire_windows", || {
        #[cfg(windows)]
        {
            let inner = live::<d3d::DeviceInner, _>(device, "device")?;
            let out = util::out(out_d3d11, "out_d3d11")?;
            *out = inner.acquire_d3d11()?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (device, out_d3d11);
            Err(windows_only("prisc_gpu_d3d11_device_acquire_windows"))
        }
    })
}

/// Windows: creates the readback ring (shareable with the D3D11 twin when the driver allows) and
/// self-checks it: slot 0 is cleared to a known colour, read back through the copy queue and
/// compared, and a mismatch is `PRISC_E_CAPABILITY`, so the caller falls back to the synchronous
/// ring. `out_info` is filled on success.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` (null pointer, zero size or slot count);
/// `PRISC_E_CAPABILITY` (unexpected copy footprint, self-check mismatch or timeout); `PRISC_E_OS`;
/// `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_create_windows(
    device: *mut PriscGpuDevice,
    width: u32,
    height: u32,
    slots: u32,
    out_ring: *mut *mut PriscGpuReadbackRing,
    out_info: *mut PriscGpuRingInfo,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_create_windows", || {
        #[cfg(windows)]
        {
            let inner = live::<d3d::DeviceInner, _>(device, "device")?;
            let out_ring = util::out(out_ring, "out_ring")?;
            let out_info = util::out(out_info, "out_info")?;
            let created = ring::create(inner, width, height, slots)?;
            *out_info = created.info();
            *out_ring = into_handle(created);
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (device, width, height, slots, out_ring, out_info);
            Err(windows_only("prisc_gpu_readback_ring_create_windows"))
        }
    })
}

/// Windows: drains the copy queue (bounded at 30 s), unmaps every readback buffer and closes the
/// wait event, then releases the handle; a converter built on the ring keeps its fences and
/// textures alive. Null is `PRISC_OK`.
///
/// Status: `PRISC_OK`.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_destroy_windows(ring: *mut PriscGpuReadbackRing) -> i32 {
    panic::guard("prisc_gpu_readback_ring_destroy_windows", || {
        #[cfg(windows)]
        if let Some(inner) = take_handle::<ring::RingInner, _>(ring) {
            inner.teardown();
        }
        #[cfg(not(windows))]
        let _ = ring;
        Ok(PRISC_OK)
    })
}

/// Windows: moves `slot` from idle to composing and lends its `ID3D12Resource*` (the same pointer
/// every frame, valid until the ring's destroy). Wrap it for Skia with `ResourceState = COMMON`.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG`; `PRISC_E_STATE` (the slot is not idle);
/// `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_begin_windows(
    ring: *mut PriscGpuReadbackRing,
    slot: u32,
    out_texture: *mut *mut c_void,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_begin_windows", || {
        #[cfg(windows)]
        {
            let inner = live::<ring::RingInner, _>(ring, "ring")?;
            let out = util::out(out_texture, "out_texture")?;
            *out = inner.begin(slot)?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (ring, slot, out_texture);
            Err(windows_only("prisc_gpu_readback_ring_begin_windows"))
        }
    })
}

/// Windows: returns a composing `slot` to idle (wrapping the texture for Skia failed).
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG`; `PRISC_E_STATE`; `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_cancel_begin_windows(ring: *mut PriscGpuReadbackRing, slot: u32) -> i32 {
    panic::guard("prisc_gpu_readback_ring_cancel_begin_windows", || {
        #[cfg(windows)]
        {
            live::<ring::RingInner, _>(ring, "ring")?.cancel_begin(slot)?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (ring, slot);
            Err(windows_only("prisc_gpu_readback_ring_cancel_begin_windows"))
        }
    })
}

/// Windows: ends drawing on a composing `slot` (after Skia flushed and submitted it on the direct
/// queue) and queues its copy to the readback buffer: the render fence is signalled behind the
/// draws, the copy queue waits for it on the GPU, copies and signals the consumed fence. Writes the
/// fence value the copy reaches. Nothing waits on the CPU.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG`; `PRISC_E_STATE`; `PRISC_E_OS`;
/// `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_submit_windows(
    ring: *mut PriscGpuReadbackRing,
    slot: u32,
    out_fence_value: *mut u64,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_submit_windows", || {
        #[cfg(windows)]
        {
            let inner = live::<ring::RingInner, _>(ring, "ring")?;
            let out = util::out(out_fence_value, "out_fence_value")?;
            *out = inner.submit(slot)?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (ring, slot, out_fence_value);
            Err(windows_only("prisc_gpu_readback_ring_submit_windows"))
        }
    })
}

/// Windows: ends drawing on a composing `slot` for an external GPU consumer (the zero-copy path):
/// signals the render fence and writes its value, which the consumer waits for and then signals on
/// the consumed fence; no copy is queued.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG`; `PRISC_E_STATE`; `PRISC_E_OS`;
/// `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_submit_shared_windows(
    ring: *mut PriscGpuReadbackRing,
    slot: u32,
    out_fence_value: *mut u64,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_submit_shared_windows", || {
        #[cfg(windows)]
        {
            let inner = live::<ring::RingInner, _>(ring, "ring")?;
            let out = util::out(out_fence_value, "out_fence_value")?;
            *out = inner.submit_shared(slot)?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (ring, slot, out_fence_value);
            Err(windows_only("prisc_gpu_readback_ring_submit_shared_windows"))
        }
    })
}

/// Windows: waits (at most `timeout_ms`, `u32::MAX` is infinite) for `slot`'s copy, makes the slot
/// idle and lends its mapped readback memory and row pitch (valid until the slot's next begin).
/// On `PRISC_S_TIMEOUT` the slot stays in flight, the out parameters are untouched, and the caller
/// may wait again.
///
/// Status: `PRISC_OK`; `PRISC_S_TIMEOUT`; `PRISC_E_INVALID_ARG`; `PRISC_E_STATE` (not submitted,
/// or submitted for an external consumer); `PRISC_E_OS` (a removed device);
/// `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_wait_windows(
    ring: *mut PriscGpuReadbackRing,
    slot: u32,
    timeout_ms: u32,
    out_address: *mut *const u8,
    out_row_bytes: *mut u32,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_wait_windows", || {
        #[cfg(windows)]
        {
            let inner = live::<ring::RingInner, _>(ring, "ring")?;
            let out_address = util::out(out_address, "out_address")?;
            let out_row_bytes = util::out(out_row_bytes, "out_row_bytes")?;
            match inner.wait(slot, timeout_ms)? {
                Some((address, row_bytes)) => {
                    *out_address = address;
                    *out_row_bytes = row_bytes;
                    Ok(PRISC_OK)
                }
                None => Ok(crate::ffi::status::PRISC_S_TIMEOUT),
            }
        }
        #[cfg(not(windows))]
        {
            let _ = (ring, slot, timeout_ms, out_address, out_row_bytes);
            Err(windows_only("prisc_gpu_readback_ring_wait_windows"))
        }
    })
}

/// Windows: waits (at most `timeout_ms`) until the external consumer of a shared `slot` signalled
/// the consumed fence for it, then makes the slot idle. On `PRISC_S_TIMEOUT` it stays in flight.
///
/// Status: `PRISC_OK`; `PRISC_S_TIMEOUT`; `PRISC_E_INVALID_ARG`; `PRISC_E_STATE`; `PRISC_E_OS`;
/// `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_release_shared_windows(
    ring: *mut PriscGpuReadbackRing,
    slot: u32,
    timeout_ms: u32,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_release_shared_windows", || {
        #[cfg(windows)]
        {
            live::<ring::RingInner, _>(ring, "ring")?.release_shared(slot, timeout_ms)
        }
        #[cfg(not(windows))]
        {
            let _ = (ring, slot, timeout_ms);
            Err(windows_only("prisc_gpu_readback_ring_release_shared_windows"))
        }
    })
}

/// Windows: gives up on a shared `slot` whose consumer failed before it could signal: the consumed
/// fence is moved to the slot's value from the CPU when it is behind (never backwards) and the
/// slot is idle again. Nothing is waited for; the caller vouches no GPU read is pending.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG`; `PRISC_E_STATE`; `PRISC_E_OS`;
/// `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_abandon_shared_windows(ring: *mut PriscGpuReadbackRing, slot: u32) -> i32 {
    panic::guard("prisc_gpu_readback_ring_abandon_shared_windows", || {
        #[cfg(windows)]
        {
            live::<ring::RingInner, _>(ring, "ring")?.abandon_shared(slot)?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (ring, slot);
            Err(windows_only("prisc_gpu_readback_ring_abandon_shared_windows"))
        }
    })
}

/// Windows: waits (at most `timeout_ms`) until the consumed fence reaches `value`.
///
/// Status: `PRISC_OK`; `PRISC_S_TIMEOUT`; `PRISC_E_INVALID_ARG`; `PRISC_E_STATE` (the ring was
/// destroyed under a converter); `PRISC_E_OS`; `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_wait_consumed_windows(
    ring: *mut PriscGpuReadbackRing,
    value: u64,
    timeout_ms: u32,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_wait_consumed_windows", || {
        #[cfg(windows)]
        {
            live::<ring::RingInner, _>(ring, "ring")?.wait_consumed(value, timeout_ms)
        }
        #[cfg(not(windows))]
        {
            let _ = (ring, value, timeout_ms);
            Err(windows_only("prisc_gpu_readback_ring_wait_consumed_windows"))
        }
    })
}

/// Windows: opens the ring's shared slots and fences on the D3D11 twin and creates the BT.601
/// limited-range video processor. `PRISC_E_CAPABILITY` for exactly the conditions the C# skip
/// predicate names (a ring that is not shareable, no ID3D11Device5 / ID3D11DeviceContext4 /
/// ID3D11VideoDevice / ID3D11VideoContext, a refused processor enumerator or processor); the
/// message keeps the `"{step} failed (0x{hr})"` shape. A ring from another device is
/// `PRISC_E_INVALID_ARG`.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG`; `PRISC_E_CAPABILITY`; `PRISC_E_OS`;
/// `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_converter_create_windows(
    device: *mut PriscGpuDevice,
    ring: *mut PriscGpuReadbackRing,
    out_converter: *mut *mut PriscGpuNv12Converter,
) -> i32 {
    panic::guard("prisc_gpu_nv12_converter_create_windows", || {
        #[cfg(windows)]
        {
            let device = live::<d3d::DeviceInner, _>(device, "device")?;
            let ring = live::<ring::RingInner, _>(ring, "ring")?;
            let out = util::out(out_converter, "out_converter")?;
            *out = into_handle(nv12::create(device, ring)?);
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (device, ring, out_converter);
            Err(windows_only("prisc_gpu_nv12_converter_create_windows"))
        }
    })
}

/// Windows: one known colour through the whole zero-copy path: slot 0 cleared on the direct queue,
/// submitted for the shared protocol, converted into a fresh texture, read back and compared at
/// three points with BT.601 limited range (tolerance 4). Slot 0 must be idle and is idle again on
/// every outcome (released when the consumed signal was queued, abandoned otherwise), so a caller
/// that falls back carries on with readback over the same ring. Each fence wait is bounded by
/// `timeout_ms`.
///
/// Status: `PRISC_OK`; `PRISC_E_CAPABILITY` (the samples are off: a driver that ignores the colour
/// space); `PRISC_E_STATE` (slot 0 not idle); `PRISC_E_OS`; `PRISC_E_INVALID_ARG`;
/// `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_converter_self_check_windows(converter: *mut PriscGpuNv12Converter, timeout_ms: u32) -> i32 {
    panic::guard("prisc_gpu_nv12_converter_self_check_windows", || {
        #[cfg(windows)]
        {
            live::<nv12::ConverterInner, _>(converter, "converter")?.self_check(timeout_ms)?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (converter, timeout_ms);
            Err(windows_only("prisc_gpu_nv12_converter_self_check_windows"))
        }
    })
}

/// Windows: converts shared `slot` (ready at `fence_value`, from the ring's shared submit) into
/// `dst_texture`, a texture from `prisc_gpu_nv12_texture_acquire_windows` on this converter.
/// Queues a GPU wait on the render fence, the blit, a signal of the consumed fence with the same
/// value, and a flush; nothing waits on the CPU. `out_signal_queued` is written on every path:
/// 1 when the consumed signal reached the GPU timeline (release the slot), 0 when nothing was
/// queued (abandon it). The signal goes out even when the blit was refused.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` (slot out of range, a texture not from this
/// converter); `PRISC_E_OS`; `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_converter_convert_windows(
    converter: *mut PriscGpuNv12Converter,
    slot: u32,
    fence_value: u64,
    dst_texture: *mut c_void,
    out_signal_queued: *mut u8,
) -> i32 {
    panic::guard("prisc_gpu_nv12_converter_convert_windows", || {
        #[cfg(windows)]
        {
            let inner = live::<nv12::ConverterInner, _>(converter, "converter")?;
            let out = util::out(out_signal_queued, "out_signal_queued")?;
            *out = 0;
            let (converted, queued) = inner.convert(slot, fence_value, dst_texture);
            *out = util::to_flag(queued);
            converted?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (converter, slot, fence_value, dst_texture, out_signal_queued);
            Err(windows_only("prisc_gpu_nv12_converter_convert_windows"))
        }
    })
}

/// Windows: waits (at most `timeout_ms`) until every conversion this converter queued has
/// completed; `PRISC_OK` at once before the first.
///
/// Status: `PRISC_OK`; `PRISC_S_TIMEOUT`; `PRISC_E_INVALID_ARG`; `PRISC_E_STATE` (the ring was
/// destroyed first); `PRISC_E_OS`; `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_converter_drain_windows(converter: *mut PriscGpuNv12Converter, timeout_ms: u32) -> i32 {
    panic::guard("prisc_gpu_nv12_converter_drain_windows", || {
        #[cfg(windows)]
        {
            live::<nv12::ConverterInner, _>(converter, "converter")?.drain(timeout_ms)
        }
        #[cfg(not(windows))]
        {
            let _ = (converter, timeout_ms);
            Err(windows_only("prisc_gpu_nv12_converter_drain_windows"))
        }
    })
}

/// Windows: reads one of this converter's NV12 textures back through a staging copy: the tightly
/// packed luma plane (`width * height` bytes) into `y` and the interleaved chroma plane
/// (`width * height / 2`) into `uv`. Blocks until every queued conversion completed (tests).
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` (null or short buffers, a foreign texture);
/// `PRISC_E_OS`; `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_converter_read_windows(
    converter: *mut PriscGpuNv12Converter,
    texture: *mut c_void,
    y: *mut u8,
    y_len: usize,
    uv: *mut u8,
    uv_len: usize,
) -> i32 {
    panic::guard("prisc_gpu_nv12_converter_read_windows", || {
        #[cfg(windows)]
        {
            let inner = live::<nv12::ConverterInner, _>(converter, "converter")?;
            let y = util::slice_mut(y, y_len, "y")?;
            let uv = util::slice_mut(uv, uv_len, "uv")?;
            inner.read(texture, y, uv)?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (converter, texture, y, y_len, uv, uv_len);
            Err(windows_only("prisc_gpu_nv12_converter_read_windows"))
        }
    })
}

/// Windows: drains (bounded at 2 s, outcome swallowed: the ordered path drains first with
/// `prisc_gpu_nv12_converter_drain_windows`) and releases the handle; textures still out keep the
/// converter's inner state alive until they are released. Null is `PRISC_OK`.
///
/// Status: `PRISC_OK`.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_converter_destroy_windows(converter: *mut PriscGpuNv12Converter) -> i32 {
    panic::guard("prisc_gpu_nv12_converter_destroy_windows", || {
        #[cfg(windows)]
        if let Some(inner) = take_handle::<nv12::ConverterInner, _>(converter) {
            nv12::teardown(&inner);
        }
        #[cfg(not(windows))]
        let _ = converter;
        Ok(PRISC_OK)
    })
}

/// Windows: a new NV12 texture (render-target and shader-resource bindable, with the converter's
/// output view) for FFmpeg's frame pool; the raw `ID3D11Texture2D*` is the frame's identity and is
/// owned by the crate until `prisc_gpu_nv12_texture_release_windows`.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG`; `PRISC_E_OS`; `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_texture_acquire_windows(
    converter: *mut PriscGpuNv12Converter,
    out_texture: *mut *mut c_void,
) -> i32 {
    panic::guard("prisc_gpu_nv12_texture_acquire_windows", || {
        #[cfg(windows)]
        {
            let inner = live::<nv12::ConverterInner, _>(converter, "converter")?;
            let out = util::out(out_texture, "out_texture")?;
            *out = inner.acquire()?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (converter, out_texture);
            Err(windows_only("prisc_gpu_nv12_texture_acquire_windows"))
        }
    })
}

/// Windows: releases a texture from `prisc_gpu_nv12_texture_acquire_windows` and its output view.
/// Takes no converter: the texture is looked up in a crate-wide registry whose entry keeps its
/// converter alive, so a release after the converter (or the encoder wrapper) was destroyed is
/// safe, and the last texture back frees the converter. A pointer the crate does not know (null,
/// never handed out, released already) is `PRISC_S_NONE`, never an error.
///
/// Status: `PRISC_OK`; `PRISC_S_NONE`; `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_texture_release_windows(texture: *mut c_void) -> i32 {
    panic::guard("prisc_gpu_nv12_texture_release_windows", || {
        #[cfg(windows)]
        {
            Ok(nv12::release_texture(texture))
        }
        #[cfg(not(windows))]
        {
            let _ = texture;
            Err(windows_only("prisc_gpu_nv12_texture_release_windows"))
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    #[test]
    fn layouts_are_pinned() {
        assert_eq!(size_of::<PriscGpuDeviceInfo>(), 64);
        assert_eq!(offset_of!(PriscGpuDeviceInfo, backend), 0);
        assert_eq!(offset_of!(PriscGpuDeviceInfo, _reserved), 4);
        assert_eq!(offset_of!(PriscGpuDeviceInfo, adapter_name), 8);
        assert_eq!(offset_of!(PriscGpuDeviceInfo, d3d12_adapter), 24);
        assert_eq!(offset_of!(PriscGpuDeviceInfo, d3d12_device), 32);
        assert_eq!(offset_of!(PriscGpuDeviceInfo, d3d12_queue), 40);
        assert_eq!(offset_of!(PriscGpuDeviceInfo, metal_device), 48);
        assert_eq!(offset_of!(PriscGpuDeviceInfo, metal_queue), 56);
        assert_eq!(size_of::<PriscGpuRingInfo>(), 24);
        assert_eq!(offset_of!(PriscGpuRingInfo, width), 0);
        assert_eq!(offset_of!(PriscGpuRingInfo, height), 4);
        assert_eq!(offset_of!(PriscGpuRingInfo, slots), 8);
        assert_eq!(offset_of!(PriscGpuRingInfo, row_pitch), 12);
        assert_eq!(offset_of!(PriscGpuRingInfo, shareable), 16);
        assert_eq!(offset_of!(PriscGpuRingInfo, _reserved), 17);
    }

    #[test]
    fn borrowed_strings_point_into_the_owner() {
        let owner = String::from("Apple M1");
        let s = borrowed(&owner);
        assert_eq!((s.ptr.cast_const(), s.len), (owner.as_ptr(), owner.len()));
        let empty = borrowed("");
        assert!(empty.ptr.is_null() && empty.len == 0);
    }

    #[test]
    fn handles_round_trip_through_the_box() {
        let handle: *mut PriscGpuDevice = into_handle(Arc::new(41u32));
        let inner = unsafe { live::<u32, _>(handle, "device") }.unwrap();
        assert_eq!(**inner, 41);
        let weak = Arc::downgrade(inner);
        drop(unsafe { take_handle::<u32, _>(handle) });
        assert!(weak.upgrade().is_none());
        assert!(unsafe { take_handle::<u32, PriscGpuDevice>(std::ptr::null_mut()) }.is_none());
        assert!(unsafe { live::<u32, PriscGpuDevice>(std::ptr::null_mut(), "device") }.is_err());
    }
}
