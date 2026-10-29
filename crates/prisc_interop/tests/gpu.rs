//! Export-level tests of the GPU area (README section 4.7): device creation through the public
//! surface on every OS, the off-OS answers of the `_windows` exports, and null arguments.

use std::ffi::c_void;
use std::ptr;

use prisc_interop::ffi::error::prisc_last_error_message;
use prisc_interop::ffi::gpu::*;
use prisc_interop::ffi::status::PriscInteropStatus as S;

fn last_message() -> String {
    let len = unsafe { prisc_last_error_message(ptr::null_mut(), 0) };
    let mut buf = vec![0u8; len];
    assert_eq!(unsafe { prisc_last_error_message(buf.as_mut_ptr(), buf.len()) }, len);
    String::from_utf8(buf).unwrap()
}

fn empty_info() -> PriscGpuDeviceInfo {
    PriscGpuDeviceInfo {
        backend: -1,
        _reserved: 7,
        adapter_name: prisc_interop::ffi::types::PriscString {
            ptr: ptr::null_mut(),
            len: 0,
        },
        d3d12_adapter: ptr::null_mut(),
        d3d12_device: ptr::null_mut(),
        d3d12_queue: ptr::null_mut(),
        metal_device: ptr::null_mut(),
        metal_queue: ptr::null_mut(),
    }
}

/// `PRISC_GPU_ALLOW_WARP=1` (CI) lets Windows pick WARP, as the C# tests do.
fn create_flags() -> u32 {
    if std::env::var("PRISC_GPU_ALLOW_WARP").as_deref() == Ok("1") {
        PriscGpuDeviceFlags::AllowSoftwareAdapter as u32
    } else {
        0
    }
}

/// A device and its info, or `None` with a printed reason when the machine has no GPU (the
/// documented `PRISC_E_CAPABILITY` answer). Any other failure fails the test.
fn device_or_skip(test: &str) -> Option<(*mut PriscGpuDevice, PriscGpuDeviceInfo)> {
    let mut device: *mut PriscGpuDevice = ptr::null_mut();
    let mut info = empty_info();
    let status = unsafe { prisc_gpu_device_create(create_flags(), &mut device, &mut info) };
    if status == S::Capability as i32 || status == S::Unsupported as i32 {
        eprintln!("skipping {test}: no GPU device ({})", last_message());
        return None;
    }
    assert_eq!(status, S::Ok as i32, "{test}: {}", last_message());
    assert!(!device.is_null());
    Some((device, info))
}

#[test]
fn device_create_reports_the_backend_borrowed_pointers_and_the_adapter_name() {
    let Some((device, info)) = device_or_skip("device_create_reports_the_backend_borrowed_pointers_and_the_adapter_name") else {
        return;
    };
    assert_eq!(info._reserved, 0);
    let name = unsafe { std::str::from_utf8(std::slice::from_raw_parts(info.adapter_name.ptr, info.adapter_name.len)) }.unwrap();
    assert!(!name.is_empty(), "every adapter and every Metal device has a name");
    if cfg!(target_os = "macos") {
        assert_eq!(info.backend, PriscGpuBackend::Metal as i32);
        assert!(!info.metal_device.is_null() && !info.metal_queue.is_null());
        assert_ne!(info.metal_device, info.metal_queue);
        assert!(info.d3d12_adapter.is_null() && info.d3d12_device.is_null() && info.d3d12_queue.is_null());
    } else {
        assert_eq!(info.backend, PriscGpuBackend::D3D12 as i32);
        assert!(!info.d3d12_adapter.is_null() && !info.d3d12_device.is_null() && !info.d3d12_queue.is_null());
        assert!(info.metal_device.is_null() && info.metal_queue.is_null());
    }

    // A second device is a separate handle over the same system device.
    let Some((second, second_info)) = device_or_skip("second device") else {
        panic!("a machine that made one device makes two");
    };
    assert_ne!(device, second);
    if cfg!(target_os = "macos") {
        assert_eq!(
            info.metal_device, second_info.metal_device,
            "MTLCreateSystemDefaultDevice is a singleton"
        );
    }
    unsafe {
        assert_eq!(prisc_gpu_device_destroy(second), S::Ok as i32);
        assert_eq!(prisc_gpu_device_destroy(device), S::Ok as i32);
    }
}

#[test]
fn device_create_refuses_null_out_pointers_and_unknown_flags() {
    let mut device: *mut PriscGpuDevice = ptr::null_mut();
    let mut info = empty_info();
    unsafe {
        assert_eq!(prisc_gpu_device_create(0, ptr::null_mut(), &mut info), S::InvalidArg as i32);
        assert!(last_message().contains("out_device"));
        assert_eq!(prisc_gpu_device_create(0, &mut device, ptr::null_mut()), S::InvalidArg as i32);
        assert!(last_message().contains("out_info"));
        assert_eq!(prisc_gpu_device_create(0x10, &mut device, &mut info), S::InvalidArg as i32);
        assert!(last_message().contains("unknown bits"), "{}", last_message());
    }
    assert!(device.is_null(), "nothing was created on a refused call");
    assert_eq!(info.backend, -1, "nothing was written on a refused call");
}

#[test]
fn device_destroy_of_null_is_ok() {
    assert_eq!(unsafe { prisc_gpu_device_destroy(ptr::null_mut()) }, S::Ok as i32);
}

/// Every `_windows` export answers `PRISC_E_UNSUPPORTED` off Windows, naming itself, before it
/// looks at its arguments, and writes nothing.
#[cfg(not(windows))]
#[test]
fn windows_exports_are_unsupported_off_windows() {
    let ring: *mut PriscGpuReadbackRing = ptr::null_mut();
    let converter: *mut PriscGpuNv12Converter = ptr::null_mut();
    let mut out: *mut c_void = ptr::null_mut();
    let mut value = 0u64;
    let mut address: *const u8 = ptr::null();
    let mut row_bytes = 0u32;
    let mut queued = 7u8;
    let mut info = PriscGpuRingInfo::default();
    let mut ring_out: *mut PriscGpuReadbackRing = ptr::null_mut();
    let mut converter_out: *mut PriscGpuNv12Converter = ptr::null_mut();
    let mut y = [0u8; 4];
    let mut uv = [0u8; 2];
    let device: *mut PriscGpuDevice = ptr::null_mut();
    let cases: Vec<(&str, i32)> = unsafe {
        vec![
            (
                "prisc_gpu_d3d11_device_acquire_windows",
                prisc_gpu_d3d11_device_acquire_windows(device, &mut out),
            ),
            (
                "prisc_gpu_readback_ring_create_windows",
                prisc_gpu_readback_ring_create_windows(device, 16, 16, 2, &mut ring_out, &mut info),
            ),
            (
                "prisc_gpu_readback_ring_begin_windows",
                prisc_gpu_readback_ring_begin_windows(ring, 0, &mut out),
            ),
            (
                "prisc_gpu_readback_ring_cancel_begin_windows",
                prisc_gpu_readback_ring_cancel_begin_windows(ring, 0),
            ),
            (
                "prisc_gpu_readback_ring_submit_windows",
                prisc_gpu_readback_ring_submit_windows(ring, 0, &mut value),
            ),
            (
                "prisc_gpu_readback_ring_submit_shared_windows",
                prisc_gpu_readback_ring_submit_shared_windows(ring, 0, &mut value),
            ),
            (
                "prisc_gpu_readback_ring_wait_windows",
                prisc_gpu_readback_ring_wait_windows(ring, 0, 0, &mut address, &mut row_bytes),
            ),
            (
                "prisc_gpu_readback_ring_release_shared_windows",
                prisc_gpu_readback_ring_release_shared_windows(ring, 0, 0),
            ),
            (
                "prisc_gpu_readback_ring_abandon_shared_windows",
                prisc_gpu_readback_ring_abandon_shared_windows(ring, 0),
            ),
            (
                "prisc_gpu_readback_ring_wait_consumed_windows",
                prisc_gpu_readback_ring_wait_consumed_windows(ring, 1, 0),
            ),
            (
                "prisc_gpu_nv12_converter_create_windows",
                prisc_gpu_nv12_converter_create_windows(device, ring, &mut converter_out),
            ),
            (
                "prisc_gpu_nv12_converter_self_check_windows",
                prisc_gpu_nv12_converter_self_check_windows(converter, 0),
            ),
            (
                "prisc_gpu_nv12_converter_convert_windows",
                prisc_gpu_nv12_converter_convert_windows(converter, 0, 1, out, &mut queued),
            ),
            (
                "prisc_gpu_nv12_converter_drain_windows",
                prisc_gpu_nv12_converter_drain_windows(converter, 0),
            ),
            (
                "prisc_gpu_nv12_converter_read_windows",
                prisc_gpu_nv12_converter_read_windows(converter, out, y.as_mut_ptr(), y.len(), uv.as_mut_ptr(), uv.len()),
            ),
            (
                "prisc_gpu_nv12_texture_acquire_windows",
                prisc_gpu_nv12_texture_acquire_windows(converter, &mut out),
            ),
            (
                "prisc_gpu_nv12_texture_release_windows",
                prisc_gpu_nv12_texture_release_windows(out),
            ),
        ]
    };
    for (name, status) in cases {
        assert_eq!(status, S::Unsupported as i32, "{name}");
    }
    // The message of the last one names it.
    assert!(
        last_message().contains("prisc_gpu_nv12_texture_release_windows"),
        "{}",
        last_message()
    );
    assert!(out.is_null() && ring_out.is_null() && converter_out.is_null() && address.is_null());
    assert_eq!((value, row_bytes, queued), (0, 0, 7));
    assert_eq!(info, PriscGpuRingInfo::default());

    // The destroys are null tolerant on every OS.
    unsafe {
        assert_eq!(prisc_gpu_readback_ring_destroy_windows(ptr::null_mut()), S::Ok as i32);
        assert_eq!(prisc_gpu_nv12_converter_destroy_windows(ptr::null_mut()), S::Ok as i32);
    }
}

/// On Windows the handle exports check their handle first.
#[cfg(windows)]
#[test]
fn windows_exports_refuse_null_handles() {
    let mut out: *mut c_void = ptr::null_mut();
    let mut value = 0u64;
    unsafe {
        assert_eq!(
            prisc_gpu_d3d11_device_acquire_windows(ptr::null_mut(), &mut out),
            S::InvalidArg as i32
        );
        assert_eq!(
            prisc_gpu_readback_ring_begin_windows(ptr::null_mut(), 0, &mut out),
            S::InvalidArg as i32
        );
        assert_eq!(
            prisc_gpu_readback_ring_submit_windows(ptr::null_mut(), 0, &mut value),
            S::InvalidArg as i32
        );
        assert_eq!(prisc_gpu_nv12_converter_drain_windows(ptr::null_mut(), 0), S::InvalidArg as i32);
        assert_eq!(prisc_gpu_nv12_texture_release_windows(ptr::null_mut()), S::None as i32);
        assert_eq!(prisc_gpu_readback_ring_destroy_windows(ptr::null_mut()), S::Ok as i32);
        assert_eq!(prisc_gpu_nv12_converter_destroy_windows(ptr::null_mut()), S::Ok as i32);
    }
}
