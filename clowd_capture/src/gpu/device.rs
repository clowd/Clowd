use std::sync::Arc;
use std::time::Instant;

use anyhow::Result;

use crate::telemetry::startup::WorkerTimings;

pub(crate) async fn request_adapter_device(
    instance: &Arc<wgpu::Instance>,
    adapter_hint: Option<(u32, u32)>,
    t_start: Instant,
    timings: &WorkerTimings,
) -> Result<(wgpu::Adapter, wgpu::Device, wgpu::Queue, String)> {
    #[cfg(windows)]
    let backends = wgpu::Backends::DX12;
    #[cfg(target_os = "macos")]
    let backends = wgpu::Backends::METAL;
    #[cfg(not(any(windows, target_os = "macos")))]
    let backends = wgpu::Backends::VULKAN;

    let adapter = match adapter_hint {
        Some((vendor, device)) => {
            info!("adapter hint: vendor=0x{:04X} device=0x{:04X}", vendor, device);
            // Enumeration is the expensive half of adapter selection: on DX12
            // wgpu-hal does a full `D3D12CreateDevice` per adapter just to read
            // its capabilities (wgpu-hal-30.0.0 `dx12/adapter.rs`,
            // `expose_adapter`) before anything gets filtered. `request_adapter`
            // runs that same `hal::enumerate_adapters` internally
            // (wgpu-core-30.0.0 `instance.rs`, both `Instance::enumerate_adapters`
            // and `Instance::request_adapter`), so a hint *miss* used to pay for
            // the whole thing twice. Enumerate once and satisfy both the hint and
            // the fallback out of the same Vec.
            let mut adapters = instance.enumerate_adapters(backends).await;
            match adapters
                .iter()
                .position(|a: &wgpu::Adapter| {
                    let info = a.get_info();
                    info.vendor == vendor && info.device == device
                }) {
                Some(idx) => {
                    info!("matched DXGI adapter hint to wgpu adapter");
                    Some(adapters.swap_remove(idx))
                }
                None => {
                    warn!("no wgpu adapter matched DXGI hint; picking best from the adapters already enumerated");
                    pick_high_performance(adapters)
                }
            }
        }
        None => {
            info!("no DXGI adapter hint; using request_adapter fallback");
            None
        }
    };
    let adapter = match adapter {
        Some(a) => a,
        None => {
            // Only reachable with no hint at all, or when enumeration came back
            // empty — in which case `request_adapter` will not find anything
            // either, but it produces the canonical "no adapters" error.
            instance
                .request_adapter(&wgpu::RequestAdapterOptions {
                    power_preference: wgpu::PowerPreference::HighPerformance,
                    compatible_surface: None,
                    force_fallback_adapter: false,
                    // limit bucketing only matters when exposing wgpu to untrusted
                    // content; we want the adapter's real limits.
                    apply_limit_buckets: false,
                })
                .await?
        }
    };
    timings
        .prep_adapter
        .set_once(t_start.elapsed());

    let adapter_info = adapter.get_info();
    info!(
        "selected adapter: \"{}\" (vendor=0x{:04X} device=0x{:04X} type={:?})",
        adapter_info.name, adapter_info.vendor, adapter_info.device, adapter_info.device_type
    );

    let adapter_limits = adapter.limits();
    let required_limits = wgpu::Limits {
        max_texture_dimension_2d: adapter_limits.max_texture_dimension_2d,
        // `Limits::default().max_non_sampler_bindings` is 1_000_000
        // (wgpu-types-30.0.0 `limits.rs:458`), and DX12 uses that number
        // verbatim as `NumDescriptors` for the one shader-visible
        // CBV/SRV/UAV descriptor heap it creates per device
        // (wgpu-hal-30.0.0 `dx12/device.rs:119`
        // `let capacity_views = limits.max_non_sampler_bindings as u64;`
        // handed straight to `GeneralHeap::new`). At the usual 32-byte
        // CBV/SRV/UAV descriptor stride that is a 32 MB heap allocated and
        // zeroed on every device creation, on the critical path to frame 0.
        //
        // Capping it is safe because the heap size is this limit's *only*
        // effect anywhere in the stack: `grep -rn max_non_sampler_bindings`
        // over wgpu-core-30.0.0/src/ returns zero hits, so nothing validates
        // a bind group, layout, or pipeline against it. We create ~5 bind
        // groups; 4096 descriptors is three orders of magnitude of headroom.
        //
        // If we ever did overflow it, the failure is loud and immediate, not
        // silent corruption: `GeneralHeap::allocate_slice` logs
        // "Unable to allocate descriptors" and returns
        // `DeviceError::OutOfMemory` (wgpu-hal-30.0.0
        // `dx12/descriptor.rs:100`).
        max_non_sampler_bindings: 4096,
        ..wgpu::Limits::default()
    };

    let adapter_features = adapter.features();
    let mut required_features = wgpu::Features::empty();
    if crate::ui::gpu::gpu_timing::GPU_TIMING_ENABLED() && adapter_features.contains(wgpu::Features::TIMESTAMP_QUERY) {
        required_features |= wgpu::Features::TIMESTAMP_QUERY;
    }

    // Split point for the wedge-diagnosis in issue #74: "selected adapter"
    // has printed by now, so a log that ends here says the hang is inside
    // `request_device` (D3D12 device/queue + allocator init in the driver),
    // not in shader or pipeline creation.
    info!("requesting wgpu device");

    let (device, queue) = adapter
        .request_device(&wgpu::DeviceDescriptor {
            label: Some("clowd_capture_wgpu device"),
            required_features,
            required_limits,
            // MemoryUsage, unconditionally: it keeps gpu-allocator's
            // retained blocks at 8/4 MB instead of Performance's 128/64 MB
            // per device — which matters from a 128 MB iGPU carve-out up —
            // and the measured startup difference is negligible (heap
            // creation is lazy and sub-ms; the large snapshot texture takes
            // the allocator's dedicated path either way). There used to be
            // a `--memory-hints` switch for A/B-ing this; the A/B was
            // settled and the switch removed.
            memory_hints: wgpu::MemoryHints::MemoryUsage,
            trace: wgpu::Trace::Off,
            experimental_features: wgpu::ExperimentalFeatures::disabled(),
        })
        .await?;

    // Make wgpu shader/pipeline errors non-fatal so a validation error is
    // reported instead of silently killing the render worker.
    crate::gpu::shaders::install_error_handler(&device);

    timings
        .prep_device
        .set_once(t_start.elapsed());

    Ok((adapter, device, queue, adapter_info.name))
}

/// Pick the adapter `request_adapter(PowerPreference::HighPerformance)` would
/// have picked, out of a Vec we already enumerated.
///
/// This replicates `get_order` from wgpu-core-30.0.0 `instance.rs` (the
/// `prefer_integrated_gpu == false` arm, which is what `HighPerformance`
/// selects): DiscreteGpu=1, IntegratedGpu=2, Other=3, VirtualGpu=4, Cpu=5,
/// lower wins. wgpu sorts with `sort_by_key` and takes the first element;
/// `min_by_key` matches that exactly, because both are stable on ties and so
/// both fall back to enumeration order.
///
/// The one thing we do *not* replicate is `compatible_surface` filtering —
/// the call above passes `compatible_surface: None`, so there is nothing to
/// filter by. If a surface is ever threaded in here, this must grow a
/// `Surface::get_capabilities` retain first or it will silently prefer an
/// adapter that cannot present.
fn pick_high_performance(adapters: Vec<wgpu::Adapter>) -> Option<wgpu::Adapter> {
    fn order(device_type: wgpu::DeviceType) -> u8 {
        match device_type {
            wgpu::DeviceType::DiscreteGpu => 1,
            wgpu::DeviceType::IntegratedGpu => 2,
            wgpu::DeviceType::Other => 3,
            wgpu::DeviceType::VirtualGpu => 4,
            wgpu::DeviceType::Cpu => 5,
        }
    }
    adapters
        .into_iter()
        .min_by_key(|a| order(a.get_info().device_type))
}
