//! macOS: the system default `MTLDevice` and one `MTLCommandQueue`, which is everything Skia's
//! `GRMtlBackendContext` needs (`prisc_gpu_device_create`).
//!
//! Deliberately no Metal readback ring: macOS keeps the synchronous `SyncReadbackRing` in C#, which
//! is what it used before this move. The ring and converter exports are `_windows` because they
//! are Direct3D concepts.

use std::ffi::c_void;
use std::sync::Arc;

use objc2::rc::{autoreleasepool, Retained};
use objc2::runtime::ProtocolObject;
use objc2_metal::{MTLCommandQueue, MTLCreateSystemDefaultDevice, MTLDevice};

use crate::error::{Error, Result};

/// The Metal device and its queue, owned for the handle's life. Both protocols are `Send + Sync`
/// in objc2-metal, so the handle is usable from any thread.
pub(crate) struct DeviceInner {
    device: Retained<ProtocolObject<dyn MTLDevice>>,
    queue: Retained<ProtocolObject<dyn MTLCommandQueue>>,
    name: String,
}

impl DeviceInner {
    /// `MTLDevice.name`, read once at creation, for diagnostics.
    pub(crate) fn name(&self) -> &str {
        &self.name
    }

    /// The raw `id<MTLDevice>` for `GRMtlBackendContext.DeviceHandle`. Borrowed, not retained:
    /// Skia retains it itself.
    pub(crate) fn device_raw(&self) -> *mut c_void {
        Retained::as_ptr(&self.device) as *mut c_void
    }

    /// The raw `id<MTLCommandQueue>` for `GRMtlBackendContext.QueueHandle`, borrowed likewise.
    pub(crate) fn queue_raw(&self) -> *mut c_void {
        Retained::as_ptr(&self.queue) as *mut c_void
    }
}

/// The system default device and one command queue. `PRISC_E_CAPABILITY` when this Mac has no
/// Metal device (the "composite on the CPU" answer); a device that refuses a queue is `PRISC_E_OS`,
/// a broken driver rather than a machine without a GPU.
pub(crate) fn create() -> Result<Arc<DeviceInner>> {
    // -[MTLDevice name] hands back an autoreleased NSString and the caller may be a thread with no
    // pool of its own; nothing borrows from the pool after it closes.
    autoreleasepool(|_| {
        let device = MTLCreateSystemDefaultDevice().ok_or_else(|| Error::capability("MTLCreateSystemDefaultDevice returned nil."))?;
        let queue = device
            .newCommandQueue()
            .ok_or_else(|| Error::os(0, "newCommandQueue returned nil."))?;
        let name = device.name().to_string();
        Ok(Arc::new(DeviceInner {
            device,
            queue,
            name,
        }))
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn metal_device_has_a_queue_and_a_name() {
        let device = match create() {
            Ok(device) => device,
            Err(e) => {
                eprintln!("skipping: no Metal device ({})", e.message);
                return;
            }
        };
        assert!(!device.device_raw().is_null());
        assert!(!device.queue_raw().is_null());
        assert_ne!(device.device_raw(), device.queue_raw());
        assert!(!device.name().is_empty());
        assert_eq!(device.device_raw(), device.device_raw(), "borrowed: the same pointer every time");
    }
}
