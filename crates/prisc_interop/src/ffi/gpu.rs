//! The GPU device, the D3D12 readback ring and the NV12 converter (WP-GPU; README section 4.7).
//! Raw OS pointers handed out are borrowed from their handle, except the D3D11 device that
//! `prisc_gpu_d3d11_device_acquire_windows` transfers to FFmpeg (README C2).

use std::ffi::c_void;

use crate::error::Error;
use crate::ffi::types::PriscString;
use crate::panic;

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

/// Creates the GPU device: D3D12 on the first hardware adapter with feature level 11.0 and a direct
/// queue (Windows; a software adapter only with `AllowSoftwareAdapter`), the system default Metal
/// device and a queue (macOS).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_CAPABILITY`, `PRISC_E_OS`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_device_create(
    flags: u32,
    out_device: *mut *mut PriscGpuDevice,
    out_info: *mut PriscGpuDeviceInfo,
) -> i32 {
    panic::guard("prisc_gpu_device_create", || {
        let _ = (flags, out_device, out_info);
        Err(Error::unsupported("prisc_gpu_device_create is not implemented yet"))
    })
}

/// Releases the device; a ring or converter created from it keeps what it needs alive. Null is
/// `PRISC_OK`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_device_destroy(device: *mut PriscGpuDevice) -> i32 {
    panic::guard("prisc_gpu_device_destroy", || {
        let _ = device;
        Err(Error::unsupported("prisc_gpu_device_destroy is not implemented yet"))
    })
}

/// Windows: the D3D11 device on the same adapter (created on first use) with a reference
/// transferred to the caller (FFmpeg releases it).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_d3d11_device_acquire_windows(device: *mut PriscGpuDevice, out_d3d11: *mut *mut c_void) -> i32 {
    panic::guard("prisc_gpu_d3d11_device_acquire_windows", || {
        let _ = (device, out_d3d11);
        Err(Error::unsupported("prisc_gpu_d3d11_device_acquire_windows is not implemented yet"))
    })
}

/// Windows: creates the readback ring and self-checks it (slot 0 cleared, submitted and read back);
/// a mismatch is `PRISC_E_CAPABILITY`, so the caller falls back to the CPU path.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_CAPABILITY`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
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
        let _ = (device, width, height, slots, out_ring, out_info);
        Err(Error::unsupported("prisc_gpu_readback_ring_create_windows is not implemented yet"))
    })
}

/// Windows: drains, unmaps and frees the ring; null is `PRISC_OK`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_destroy_windows(ring: *mut PriscGpuReadbackRing) -> i32 {
    panic::guard("prisc_gpu_readback_ring_destroy_windows", || {
        let _ = ring;
        Err(Error::unsupported("prisc_gpu_readback_ring_destroy_windows is not implemented yet"))
    })
}

/// Windows: moves `slot` from idle to composing and lends its `ID3D12Resource*`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_begin_windows(
    ring: *mut PriscGpuReadbackRing,
    slot: u32,
    out_texture: *mut *mut c_void,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_begin_windows", || {
        let _ = (ring, slot, out_texture);
        Err(Error::unsupported("prisc_gpu_readback_ring_begin_windows is not implemented yet"))
    })
}

/// Windows: returns a composing `slot` to idle.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_cancel_begin_windows(ring: *mut PriscGpuReadbackRing, slot: u32) -> i32 {
    panic::guard("prisc_gpu_readback_ring_cancel_begin_windows", || {
        let _ = (ring, slot);
        Err(Error::unsupported(
            "prisc_gpu_readback_ring_cancel_begin_windows is not implemented yet",
        ))
    })
}

/// Windows: submits the copy of `slot` to its readback buffer (the copy path).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_submit_windows(
    ring: *mut PriscGpuReadbackRing,
    slot: u32,
    out_fence_value: *mut u64,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_submit_windows", || {
        let _ = (ring, slot, out_fence_value);
        Err(Error::unsupported("prisc_gpu_readback_ring_submit_windows is not implemented yet"))
    })
}

/// Windows: signals `slot` for the D3D11 twin (the zero-copy path).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_submit_shared_windows(
    ring: *mut PriscGpuReadbackRing,
    slot: u32,
    out_fence_value: *mut u64,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_submit_shared_windows", || {
        let _ = (ring, slot, out_fence_value);
        Err(Error::unsupported(
            "prisc_gpu_readback_ring_submit_shared_windows is not implemented yet",
        ))
    })
}

/// Windows: waits for `slot`'s copy and lends the mapped pixels.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_S_TIMEOUT`, `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_wait_windows(
    ring: *mut PriscGpuReadbackRing,
    slot: u32,
    timeout_ms: u32,
    out_address: *mut *const u8,
    out_row_bytes: *mut u32,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_wait_windows", || {
        let _ = (ring, slot, timeout_ms, out_address, out_row_bytes);
        Err(Error::unsupported("prisc_gpu_readback_ring_wait_windows is not implemented yet"))
    })
}

/// Windows: waits until the D3D11 twin is done with a shared `slot` and returns it to idle.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_S_TIMEOUT`, `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_release_shared_windows(
    ring: *mut PriscGpuReadbackRing,
    slot: u32,
    timeout_ms: u32,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_release_shared_windows", || {
        let _ = (ring, slot, timeout_ms);
        Err(Error::unsupported(
            "prisc_gpu_readback_ring_release_shared_windows is not implemented yet",
        ))
    })
}

/// Windows: gives up on a shared `slot` after a failure, leaving it reusable.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_abandon_shared_windows(ring: *mut PriscGpuReadbackRing, slot: u32) -> i32 {
    panic::guard("prisc_gpu_readback_ring_abandon_shared_windows", || {
        let _ = (ring, slot);
        Err(Error::unsupported(
            "prisc_gpu_readback_ring_abandon_shared_windows is not implemented yet",
        ))
    })
}

/// Windows: waits until the consumer fence reaches `value`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_S_TIMEOUT`, `PRISC_E_INVALID_ARG`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_readback_ring_wait_consumed_windows(
    ring: *mut PriscGpuReadbackRing,
    value: u64,
    timeout_ms: u32,
) -> i32 {
    panic::guard("prisc_gpu_readback_ring_wait_consumed_windows", || {
        let _ = (ring, value, timeout_ms);
        Err(Error::unsupported(
            "prisc_gpu_readback_ring_wait_consumed_windows is not implemented yet",
        ))
    })
}

/// Windows: opens the ring's shared slots on the D3D11 twin and creates the BT.601 limited-range
/// video processor and its texture pool.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_CAPABILITY`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_converter_create_windows(
    device: *mut PriscGpuDevice,
    ring: *mut PriscGpuReadbackRing,
    out_converter: *mut *mut PriscGpuNv12Converter,
) -> i32 {
    panic::guard("prisc_gpu_nv12_converter_create_windows", || {
        let _ = (device, ring, out_converter);
        Err(Error::unsupported("prisc_gpu_nv12_converter_create_windows is not implemented yet"))
    })
}

/// Windows: converts a cleared slot and compares three BT.601 sample points; on any failure leaves
/// slot 0 idle itself.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_CAPABILITY`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_converter_self_check_windows(converter: *mut PriscGpuNv12Converter, timeout_ms: u32) -> i32 {
    panic::guard("prisc_gpu_nv12_converter_self_check_windows", || {
        let _ = (converter, timeout_ms);
        Err(Error::unsupported(
            "prisc_gpu_nv12_converter_self_check_windows is not implemented yet",
        ))
    })
}

/// Windows: converts shared `slot` (ready at `fence_value`) into `dst_texture` (NV12).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_converter_convert_windows(
    converter: *mut PriscGpuNv12Converter,
    slot: u32,
    fence_value: u64,
    dst_texture: *mut c_void,
    out_signal_queued: *mut u8,
) -> i32 {
    panic::guard("prisc_gpu_nv12_converter_convert_windows", || {
        let _ = (converter, slot, fence_value, dst_texture, out_signal_queued);
        Err(Error::unsupported(
            "prisc_gpu_nv12_converter_convert_windows is not implemented yet",
        ))
    })
}

/// Windows: waits until every queued conversion has finished.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_S_TIMEOUT`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_converter_drain_windows(converter: *mut PriscGpuNv12Converter, timeout_ms: u32) -> i32 {
    panic::guard("prisc_gpu_nv12_converter_drain_windows", || {
        let _ = (converter, timeout_ms);
        Err(Error::unsupported("prisc_gpu_nv12_converter_drain_windows is not implemented yet"))
    })
}

/// Windows: reads an NV12 texture back through a staging copy (tests).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread, never concurrently with another call on the same handle.
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
        let _ = (converter, texture, y, y_len, uv, uv_len);
        Err(Error::unsupported("prisc_gpu_nv12_converter_read_windows is not implemented yet"))
    })
}

/// Windows: drains (bounded at 2 s) and releases the converter; textures still out keep its inner
/// state alive. Null is `PRISC_OK`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_converter_destroy_windows(converter: *mut PriscGpuNv12Converter) -> i32 {
    panic::guard("prisc_gpu_nv12_converter_destroy_windows", || {
        let _ = converter;
        Err(Error::unsupported(
            "prisc_gpu_nv12_converter_destroy_windows is not implemented yet",
        ))
    })
}

/// Windows: an NV12 texture from the converter's pool for FFmpeg's frame pool.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_texture_acquire_windows(
    converter: *mut PriscGpuNv12Converter,
    out_texture: *mut *mut c_void,
) -> i32 {
    panic::guard("prisc_gpu_nv12_texture_acquire_windows", || {
        let _ = (converter, out_texture);
        Err(Error::unsupported("prisc_gpu_nv12_texture_acquire_windows is not implemented yet"))
    })
}

/// Windows: returns a pool texture (FFmpeg's free path); safe after the converter was destroyed.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_S_NONE` (unknown pointer), `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gpu_nv12_texture_release_windows(texture: *mut c_void) -> i32 {
    panic::guard("prisc_gpu_nv12_texture_release_windows", || {
        let _ = texture;
        Err(Error::unsupported("prisc_gpu_nv12_texture_release_windows is not implemented yet"))
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
}
