//! The headless Direct3D 12 device: the DXGI adapter walk, the device and its direct command
//! queue, and the Direct3D 11 twin that is created lazily on the same adapter.
//!
//! The Direct3D 11 device lives here rather than on the encode bridge because the NV12 converter
//! and FFmpeg must share one device, and FFmpeg holds a reference of its own for as long as its
//! `AVD3D11VADeviceContext` lives. With one owner there is no window in which the bridge has
//! released its reference while FFmpeg still holds one, and `prisc_gpu_d3d11_device_acquire_windows`
//! hands out the transferred reference FFmpeg expects.
//!
//! Every driver failure here is `PRISC_E_OS`, never `PRISC_E_CAPABILITY`, except "no adapter
//! qualifies", which is the machine's answer rather than a failure. A refused `D3D11CreateDevice`
//! and a LUID mismatch in particular stay hard failures, so the C# suite keeps failing on them
//! instead of quietly skipping.

use std::ffi::c_void;
use std::sync::{Arc, Mutex, PoisonError};

use windows::Win32::Foundation::HMODULE;
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_UNKNOWN, D3D_FEATURE_LEVEL, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_11_1};
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, ID3D11Device, ID3D11DeviceContext, ID3D11Multithread, D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_CREATE_DEVICE_FLAG,
    D3D11_CREATE_DEVICE_VIDEO_SUPPORT, D3D11_SDK_VERSION,
};
use windows::Win32::Graphics::Direct3D12::{
    D3D12CreateDevice, ID3D12CommandQueue, ID3D12Device, D3D12_COMMAND_LIST_TYPE_DIRECT, D3D12_COMMAND_QUEUE_DESC,
    D3D12_COMMAND_QUEUE_FLAG_NONE, D3D12_COMMAND_QUEUE_PRIORITY_NORMAL,
};
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, IDXGIAdapter1, IDXGIDevice, IDXGIFactory1, DXGI_ADAPTER_FLAG_SOFTWARE, DXGI_ERROR_NOT_FOUND,
};
use windows_core::Interface;

use crate::error::{Error, Result};
use crate::platform::windows::gpu::{missing_object, FreeThreaded};
use crate::platform::windows::os_error;

/// The Direct3D 11 half of the device, created on first use and kept for the device's life.
pub(crate) struct D3d11 {
    pub(crate) device: FreeThreaded<ID3D11Device>,
    /// The immediate context `D3D11CreateDevice` returned, the one `SetMultithreadProtected(TRUE)`
    /// was set on. `nv12.rs` runs the per-frame wait, blit, signal and flush on it, and the staging
    /// read of the self-check.
    pub(crate) context: FreeThreaded<ID3D11DeviceContext>,
}

/// Everything the GPU handles reach through their `Arc`. The ring and the converter each hold one,
/// so a `prisc_gpu_device_destroy` that arrives before they are gone only drops a reference.
pub(crate) struct DeviceInner {
    /// Kept for its lifetime alone: DXGI documents adapters and devices as outliving the factory,
    /// and one reference is cheaper than reasoning about it.
    _factory: FreeThreaded<IDXGIFactory1>,
    adapter: FreeThreaded<IDXGIAdapter1>,
    device: FreeThreaded<ID3D12Device>,
    queue: FreeThreaded<ID3D12CommandQueue>,
    name: String,
    /// `None` until the first `acquire_d3d11` or converter creation. The mutex makes the lazy
    /// creation safe from the two threads that can ask for it (the encoder's setup through FFmpeg,
    /// the composer through the converter).
    d3d11: Mutex<Option<Arc<D3d11>>>,
}

impl DeviceInner {
    /// The adapter description (`DXGI_ADAPTER_DESC1.Description`), for diagnostics.
    pub(crate) fn name(&self) -> &str {
        &self.name
    }

    /// The raw pointers that fill Skia's `GRD3DBackendContext`. Borrowed, not AddRef'd: Skia takes
    /// its own references when the backend context is handed to `GRContext.CreateDirect3D`.
    pub(crate) fn adapter_raw(&self) -> *mut c_void {
        self.adapter.as_raw()
    }

    pub(crate) fn device_raw(&self) -> *mut c_void {
        self.device.as_raw()
    }

    pub(crate) fn queue_raw(&self) -> *mut c_void {
        self.queue.as_raw()
    }

    /// The Direct3D 12 device, for the ring.
    pub(crate) fn d3d12(&self) -> &ID3D12Device {
        self.device.get()
    }

    /// The direct queue Skia renders on, for the ring's render-fence signals and its clear.
    pub(crate) fn direct_queue(&self) -> &ID3D12CommandQueue {
        self.queue.get()
    }

    /// Creates the Direct3D 11 device on the first ask and hands back the shared half. Serialised
    /// on the mutex, so two threads racing to be first still produce one device.
    pub(crate) fn ensure_d3d11(&self) -> Result<Arc<D3d11>> {
        let mut slot = self
            .d3d11
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        if let Some(existing) = slot.as_ref() {
            return Ok(existing.clone());
        }
        let created = Arc::new(self.create_d3d11()?);
        *slot = Some(created.clone());
        Ok(created)
    }

    /// The one export that transfers a reference: `ID3D11Device*` with a +1 the caller owns.
    /// FFmpeg writes it into `AVD3D11VADeviceContext.device` and releases it exactly once when the
    /// device context is freed, on every path including a failed `av_hwdevice_ctx_init`.
    pub(crate) fn acquire_d3d11(&self) -> Result<*mut c_void> {
        let d3d11 = self.ensure_d3d11()?;
        // Clone is AddRef and into_raw gives the wrapper up without the matching Release.
        Ok(d3d11.device.clone_interface().into_raw())
    }

    fn create_d3d11(&self) -> Result<D3d11> {
        // The video processor needs the video-support flag on some runtimes; a driver that refuses
        // it (pre-WDDM 1.2) gets a plain BGRA device and the converter's enumerator decides later
        // whether a processor exists at all.
        let levels = [D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0];
        let mut device: Option<ID3D11Device> = None;
        let mut context: Option<ID3D11DeviceContext> = None;
        let mut level = D3D_FEATURE_LEVEL::default();
        let mut result = unsafe {
            create_d3d11_on(
                self.adapter.get(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                &levels,
                &mut device,
                &mut level,
                &mut context,
            )
        };
        if result.is_err() {
            result = unsafe {
                create_d3d11_on(
                    self.adapter.get(),
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    &levels,
                    &mut device,
                    &mut level,
                    &mut context,
                )
            };
        }
        result.map_err(|e| os_error(&e, "D3D11CreateDevice"))?;
        let (Some(device), Some(context)) = (device, context) else {
            return Err(missing_object("D3D11CreateDevice"));
        };

        // Same GPU or nothing: a shared handle opened on another adapter is undefined, and the
        // render fence would never be seen by the other device's timeline.
        let luid11 = unsafe { adapter_luid(&device) }?;
        let luid12 = unsafe { self.device.GetAdapterLuid() };
        let luid12 = pack_luid(luid12.LowPart, luid12.HighPart);
        if luid11 != luid12 {
            return Err(Error::os(
                0,
                format!(
                    "the Direct3D 11 device landed on adapter LUID 0x{:X} but the composer's is 0x{:X}",
                    luid11 as u64, luid12 as u64
                ),
            ));
        }

        // The immediate context is used from the convert thread while the encoder's own D3D11
        // calls arrive from NVENC's or AMF's threads: the runtime's own lock serialises them. The
        // context is asked first and the device second; a runtime that offers neither is left
        // unprotected, as the C# bridge did.
        unsafe {
            let multithread = context
                .cast::<ID3D11Multithread>()
                .or_else(|_| device.cast::<ID3D11Multithread>());
            if let Ok(multithread) = multithread {
                // The return value is the PREVIOUS setting, not a success flag.
                let _ = multithread.SetMultithreadProtected(true);
            }
        }

        Ok(D3d11 {
            device: FreeThreaded::new(device),
            context: FreeThreaded::new(context),
        })
    }
}

/// `D3D11CreateDevice` on one adapter, with the driver type an explicit adapter demands.
unsafe fn create_d3d11_on(
    adapter: &IDXGIAdapter1,
    flags: D3D11_CREATE_DEVICE_FLAG,
    levels: &[D3D_FEATURE_LEVEL],
    device: &mut Option<ID3D11Device>,
    level: &mut D3D_FEATURE_LEVEL,
    context: &mut Option<ID3D11DeviceContext>,
) -> windows_core::Result<()> {
    unsafe {
        D3D11CreateDevice(
            adapter,
            // UNKNOWN is mandatory when an adapter is supplied; anything else is E_INVALIDARG.
            D3D_DRIVER_TYPE_UNKNOWN,
            HMODULE(std::ptr::null_mut()),
            flags,
            Some(levels),
            D3D11_SDK_VERSION,
            Some(device),
            Some(level),
            Some(context),
        )
    }
}

/// The LUID of the adapter a Direct3D 11 device landed on, as one 64-bit value with the low part
/// in the low dword.
unsafe fn adapter_luid(device: &ID3D11Device) -> Result<i64> {
    unsafe {
        let dxgi = device
            .cast::<IDXGIDevice>()
            .map_err(|e| os_error(&e, "QueryInterface(IDXGIDevice)"))?;
        let adapter = dxgi
            .GetAdapter()
            .map_err(|e| os_error(&e, "IDXGIDevice::GetAdapter"))?;
        let desc = adapter
            .GetDesc()
            .map_err(|e| os_error(&e, "IDXGIAdapter::GetDesc"))?;
        Ok(pack_luid(desc.AdapterLuid.LowPart, desc.AdapterLuid.HighPart))
    }
}

fn pack_luid(low: u32, high: i32) -> i64 {
    (i64::from(high) << 32) | i64::from(low)
}

/// Walks the adapters and builds the device and its direct queue on the first one that takes a
/// Direct3D 12 device at feature level 11.0. Software adapters (WARP, the Basic Render Driver) are
/// skipped unless `allow_software`: the CPU compositor is faster than WARP, and only tests on a
/// GPU-less runner ask for it.
///
/// `PRISC_E_CAPABILITY` when no adapter qualifies, which is the benign "fall back to the CPU
/// compositor" answer.
pub(crate) fn create(allow_software: bool) -> Result<Arc<DeviceInner>> {
    unsafe {
        let factory: IDXGIFactory1 = CreateDXGIFactory1().map_err(|e| os_error(&e, "CreateDXGIFactory1"))?;
        let mut index = 0u32;
        loop {
            let adapter = match factory.EnumAdapters1(index) {
                Ok(adapter) => adapter,
                Err(error) if error.code() == DXGI_ERROR_NOT_FOUND => {
                    return Err(Error::capability(if allow_software {
                        "No D3D12 adapter found."
                    } else {
                        "No hardware D3D12 adapter found."
                    }));
                }
                Err(error) => return Err(os_error(&error, "EnumAdapters1")),
            };
            index += 1;

            // A description we cannot read is an adapter we cannot judge, and is skipped.
            let Ok(desc) = adapter.GetDesc1() else {
                continue;
            };
            if desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32 != 0 && !allow_software {
                continue;
            }

            let mut device: Option<ID3D12Device> = None;
            if D3D12CreateDevice(&adapter, D3D_FEATURE_LEVEL_11_0, &mut device).is_err() {
                continue;
            }
            let Some(device) = device else {
                continue;
            };

            let queue_desc = D3D12_COMMAND_QUEUE_DESC {
                Type: D3D12_COMMAND_LIST_TYPE_DIRECT,
                Priority: D3D12_COMMAND_QUEUE_PRIORITY_NORMAL.0,
                Flags: D3D12_COMMAND_QUEUE_FLAG_NONE,
                NodeMask: 0,
            };
            let queue: ID3D12CommandQueue = device
                .CreateCommandQueue(&queue_desc)
                .map_err(|e| os_error(&e, "CreateCommandQueue"))?;

            return Ok(Arc::new(DeviceInner {
                _factory: FreeThreaded::new(factory),
                adapter: FreeThreaded::new(adapter),
                device: FreeThreaded::new(device),
                queue: FreeThreaded::new(queue),
                name: description_to_string(&desc.Description),
                d3d11: Mutex::new(None),
            }));
        }
    }
}

/// `DXGI_ADAPTER_DESC1.Description` is a NUL-padded UTF-16 array; a lone surrogate becomes the
/// replacement character rather than failing the call, because this string is diagnostics.
fn description_to_string(description: &[u16; 128]) -> String {
    let end = description
        .iter()
        .position(|c| *c == 0)
        .unwrap_or(description.len());
    String::from_utf16_lossy(&description[..end])
        .trim()
        .to_string()
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    /// A device, or `None` with a printed reason. Every GPU test in this crate skips rather than
    /// fails on a machine with no adapter at all (`PRISC_GPU_ALLOW_WARP=1` lets CI use WARP).
    pub(crate) fn device_or_skip(test: &str) -> Option<Arc<DeviceInner>> {
        let allow_software = std::env::var(prisc_ipc::env::GPU_ALLOW_WARP).as_deref() == Ok("1");
        match create(allow_software) {
            Ok(device) => Some(device),
            Err(e) => {
                eprintln!("skipping {test}: no Direct3D 12 device ({})", e.message);
                None
            }
        }
    }

    #[test]
    fn description_is_trimmed_at_the_first_nul() {
        let mut raw = [0u16; 128];
        for (i, c) in " Basic Render ".encode_utf16().enumerate() {
            raw[i] = c;
        }
        assert_eq!(description_to_string(&raw), "Basic Render");
    }

    #[test]
    fn luid_packs_low_into_the_low_dword() {
        assert_eq!(pack_luid(0x1234, 0x5), 0x5_0000_1234);
        assert_eq!(pack_luid(0xFFFF_FFFF, -1), -1);
    }

    #[test]
    fn device_exposes_borrowed_d3d12_pointers() {
        let Some(device) = device_or_skip("device_exposes_borrowed_d3d12_pointers") else {
            return;
        };
        assert!(!device.adapter_raw().is_null());
        assert!(!device.device_raw().is_null());
        assert!(!device.queue_raw().is_null());
        assert_eq!(device.device_raw(), device.device_raw());
        assert!(!device.name().is_empty());
    }

    #[test]
    fn d3d11_acquire_shares_one_device_on_the_same_adapter() {
        let Some(device) = device_or_skip("d3d11_acquire_shares_one_device_on_the_same_adapter") else {
            return;
        };
        let first = device
            .acquire_d3d11()
            .unwrap_or_else(|e| panic!("acquire: {}", e.message));
        let second = device
            .acquire_d3d11()
            .expect("the second acquire reuses the device");
        assert_eq!(first, second, "the D3D11 device is created once and shared");

        let d3d11 = device
            .ensure_d3d11()
            .expect("the device is up");
        let luid11 = unsafe { adapter_luid(d3d11.device.get()) }.expect("the D3D11 adapter LUID is readable");
        let luid12 = unsafe { device.device.GetAdapterLuid() };
        assert_eq!(luid11, pack_luid(luid12.LowPart, luid12.HighPart));

        // Both acquired references are the caller's to release; FFmpeg does this itself.
        unsafe {
            drop(ID3D11Device::from_raw(first));
            drop(ID3D11Device::from_raw(second));
        }
    }
}
