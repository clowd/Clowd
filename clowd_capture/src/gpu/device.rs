use std::sync::Arc;
use std::time::Instant;

use anyhow::Result;

use crate::settings::MemoryHintsMode;
use crate::telemetry::startup::WorkerTimings;

pub(crate) async fn request_adapter_device(
    instance: &Arc<wgpu::Instance>,
    adapter_hint: Option<(u32, u32)>,
    memory_hints: MemoryHintsMode,
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
            let adapters = instance.enumerate_adapters(backends).await;
            let matched = adapters
                .into_iter()
                .find(|a: &wgpu::Adapter| {
                    let info = a.get_info();
                    info.vendor == vendor && info.device == device
                });
            if matched.is_some() {
                info!("matched DXGI adapter hint to wgpu adapter");
            } else {
                warn!("no wgpu adapter matched DXGI hint; falling back to request_adapter");
            }
            matched
        }
        None => {
            info!("no DXGI adapter hint; using request_adapter fallback");
            None
        }
    };
    let adapter = match adapter {
        Some(a) => a,
        None => {
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
        ..wgpu::Limits::default()
    };

    let adapter_features = adapter.features();
    let mut required_features = wgpu::Features::empty();
    if crate::ui::gpu::gpu_timing::GPU_TIMING_ENABLED && adapter_features.contains(wgpu::Features::TIMESTAMP_QUERY) {
        required_features |= wgpu::Features::TIMESTAMP_QUERY;
    }

    let (device, queue) = adapter
        .request_device(&wgpu::DeviceDescriptor {
            label: Some("clowd_capture_wgpu device"),
            required_features,
            required_limits,
            // Performance (wgpu's large-block default) trades memory for
            // start-up latency, which is the right trade for a process that
            // exits after one capture. The setting stays for anyone who wants
            // the smaller blocks.
            memory_hints: match memory_hints {
                MemoryHintsMode::LowerMemoryUsage => wgpu::MemoryHints::MemoryUsage,
                MemoryHintsMode::MaxPerformance => wgpu::MemoryHints::Performance,
            },
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
