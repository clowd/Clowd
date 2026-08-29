//! Instance / device / queue / plain resources for the wgpu backend
//! (absorbed what used to be `capture/session.rs::create_wgpu_instance`
//! and `gpu/device.rs`).

use std::sync::{Arc, Mutex};
use std::time::Duration;

use anyhow::Result;

use crate::gxi::types::{BindingRes, CreateMark, FilterMode, ShaderId, TexFormat, TextureDesc};
use crate::shader_bindings::ResourceKind;

use super::pipeline::create_bind_group_layout;

// ── Instance ────────────────────────────────────────────────────────

/// The GPU API entry point. Created once on the main thread and cloned to
/// every render worker (cheap: it wraps an `Arc`).
#[derive(Clone)]
pub struct Instance {
    raw: Arc<wgpu::Instance>,
}

impl Instance {
    pub fn new() -> Self {
        #[allow(unused_mut)]
        let mut backend_options = wgpu::BackendOptions::default();
        #[cfg(windows)]
        {
            backend_options.dx12.shader_compiler = wgpu::Dx12Compiler::Fxc;
            backend_options.dx12.latency_waitable_object = wgpu::Dx12UseFrameLatencyWaitableObject::Wait;
        }

        let raw = wgpu::Instance::new(wgpu::InstanceDescriptor {
            backends: backends(),
            flags: wgpu::InstanceFlags::DISCARD_HAL_LABELS,
            backend_options,
            ..wgpu::InstanceDescriptor::new_without_display_handle()
        });
        Self {
            raw: Arc::new(raw),
        }
    }

    pub(super) fn raw(&self) -> &Arc<wgpu::Instance> {
        &self.raw
    }
}

impl Default for Instance {
    fn default() -> Self {
        Self::new()
    }
}

fn backends() -> wgpu::Backends {
    #[cfg(windows)]
    {
        wgpu::Backends::DX12
    }
    #[cfg(target_os = "macos")]
    {
        wgpu::Backends::METAL
    }
    #[cfg(not(any(windows, target_os = "macos")))]
    {
        wgpu::Backends::VULKAN
    }
}

// ── Device + Queue ──────────────────────────────────────────────────

/// The GPU device. `Clone + Send + Sync` — the deferred pipeline-build
/// thread gets its own clone while the worker keeps using the original.
#[derive(Clone)]
pub struct Device {
    device: wgpu::Device,
    adapter: wgpu::Adapter,
    adapter_name: Arc<str>,
    /// Lazily-built bind group layouts, one per shader, derived from the
    /// `shader_bindings.rs` tables. Shared across clones so a layout is
    /// built once per device regardless of which thread asks first.
    bgls: Arc<Mutex<[Option<wgpu::BindGroupLayout>; ShaderId::COUNT]>>,
}

/// The submission queue. `Clone + Send + Sync`; uploads only — command
/// submission and present happen inside `Frame::present`.
///
/// Threading contract: the API is `Sync`, so every backend must be safe
/// under concurrent calls — wgpu's queue is free-threaded, and the d3d11
/// backend wraps its immediate `ID3D11DeviceContext` (which is NOT
/// free-threaded) in a mutex to honor the same bound. That mutex is
/// uncontended in practice: every queue touch today happens on the owning
/// render worker's thread (the deferred build thread clones only
/// [`Device`], whose `ID3D11Device` half IS free-threaded).
#[derive(Clone)]
pub struct Queue {
    queue: wgpu::Queue,
}

impl Device {
    /// Select an adapter and create the device + queue.
    ///
    /// `adapter_hint` is an existing DXGI `(vendor, device)` id pair (from
    /// the monitor enumeration) naming the adapter that owns the output;
    /// when it matches nothing, falls back to the high-performance pick.
    /// `mark` fires at the two telemetry-relevant milestones so the caller
    /// can stamp its startup marks ([`CreateMark`]).
    pub fn create(instance: &Instance, adapter_hint: Option<(u32, u32)>, mut mark: impl FnMut(CreateMark)) -> Result<(Device, Queue)> {
        let (device, queue, adapter, adapter_name) = pollster::block_on(async {
            let adapter = select_adapter(instance.raw(), adapter_hint).await?;
            mark(CreateMark::AdapterSelected);

            let adapter_info = adapter.get_info();
            info!(
                "selected adapter: \"{}\" (vendor=0x{:04X} device=0x{:04X} type={:?})",
                adapter_info.name, adapter_info.vendor, adapter_info.device, adapter_info.device_type
            );

            let adapter_limits = adapter.limits();
            let required_limits = wgpu::Limits {
                max_texture_dimension_2d: adapter_limits.max_texture_dimension_2d,
                // `Limits::default().max_non_sampler_bindings` is 1_000_000
                // (wgpu-types-30.0.0 `limits.rs:458`), and DX12 uses that
                // number verbatim as `NumDescriptors` for the one
                // shader-visible CBV/SRV/UAV descriptor heap it creates per
                // device (wgpu-hal-30.0.0 `dx12/device.rs:119`). At the
                // usual 32-byte descriptor stride that is a 32 MB heap
                // allocated and zeroed on every device creation, on the
                // critical path to frame 0.
                //
                // Capping it is safe because the heap size is this limit's
                // *only* effect anywhere in the stack: nothing in wgpu-core
                // validates a bind group, layout, or pipeline against it.
                // We create ~5 bind groups; 4096 descriptors is three
                // orders of magnitude of headroom, and an overflow fails
                // loudly (`DeviceError::OutOfMemory`), not silently.
                max_non_sampler_bindings: 4096,
                ..wgpu::Limits::default()
            };

            let adapter_features = adapter.features();
            let mut required_features = wgpu::Features::empty();
            if crate::gxi::gpu_timing_enabled() && adapter_features.contains(wgpu::Features::TIMESTAMP_QUERY) {
                required_features |= wgpu::Features::TIMESTAMP_QUERY;
            }
            // Windows ships precompiled DXBC shaders consumed via
            // passthrough (see gxi/wgpu/shaders.rs); the dx12 backend
            // exposes this feature unconditionally (wgpu-hal 30
            // dx12/adapter.rs baseline feature set, independent of the
            // FXC/DXC compiler choice), so requiring it cannot fail device
            // creation on any dx12 adapter.
            #[cfg(windows)]
            {
                required_features |= wgpu::Features::PASSTHROUGH_SHADERS;
            }

            // Split point for the wedge-diagnosis in issue #74: "selected
            // adapter" has printed by now, so a log that ends here says the
            // hang is inside `request_device` (D3D12 device/queue +
            // allocator init in the driver), not in shader or pipeline
            // creation.
            info!("requesting wgpu device");

            let (device, queue) = adapter
                .request_device(&wgpu::DeviceDescriptor {
                    label: Some("clowd_capture gxi device"),
                    required_features,
                    required_limits,
                    // MemoryUsage, unconditionally: it keeps gpu-allocator's
                    // retained blocks at 8/4 MB instead of Performance's
                    // 128/64 MB per device — which matters from a 128 MB
                    // iGPU carve-out up — and the measured startup
                    // difference is negligible.
                    memory_hints: wgpu::MemoryHints::MemoryUsage,
                    trace: wgpu::Trace::Off,
                    experimental_features: wgpu::ExperimentalFeatures::disabled(),
                })
                .await?;

            // Make wgpu shader/pipeline errors non-fatal so a validation
            // error is reported instead of silently killing the render
            // worker.
            super::shaders::install_error_handler(&device);
            mark(CreateMark::DeviceReady);

            anyhow::Ok((device, queue, adapter, adapter_info.name))
        })?;

        Ok((
            Device {
                device,
                adapter,
                adapter_name: adapter_name.into(),
                bgls: Arc::new(Mutex::new(Default::default())),
            },
            Queue {
                queue,
            },
        ))
    }

    pub fn adapter_name(&self) -> &str {
        &self.adapter_name
    }

    pub fn max_texture_dimension_2d(&self) -> u32 {
        self.device.limits().max_texture_dimension_2d
    }

    /// Block until all submitted GPU work has completed (bounded by
    /// `timeout`). Frame 0 uses this so `first_render` means "the GPU is
    /// actually done", not "commands were queued".
    pub fn wait_idle(&self, timeout: Duration) {
        let _ = self.device.poll(wgpu::PollType::Wait {
            submission_index: None,
            timeout: Some(timeout),
        });
    }

    // ── Resources ───────────────────────────────────────────────────

    pub fn create_uniform_buffer(&self, label: &str, size: u64) -> Buffer {
        Buffer {
            raw: self
                .device
                .create_buffer(&wgpu::BufferDescriptor {
                    label: Some(label),
                    size,
                    usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                }),
        }
    }

    /// A per-instance vertex buffer. Growth (by recreation) is caller
    /// policy, as today.
    pub fn create_instance_buffer(&self, label: &str, size: u64) -> Buffer {
        Buffer {
            raw: self
                .device
                .create_buffer(&wgpu::BufferDescriptor {
                    label: Some(label),
                    size,
                    usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                }),
        }
    }

    pub fn create_texture(&self, desc: &TextureDesc) -> Texture {
        let format = texture_format(desc.format);
        let raw = self
            .device
            .create_texture(&wgpu::TextureDescriptor {
                label: Some(desc.label),
                size: wgpu::Extent3d {
                    width: desc.width,
                    height: desc.height,
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format,
                usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                view_formats: &[],
            });
        let view = raw.create_view(&wgpu::TextureViewDescriptor::default());
        Texture {
            raw,
            view,
            bytes_per_pixel: desc.format.bytes_per_pixel(),
        }
    }

    /// Primary texture path: create and upload the full contents in one
    /// call (`data` is tightly packed, `bytes_per_pixel * width` per row).
    /// The d3d11 backend maps this to `USAGE_IMMUTABLE` + `pInitialData`.
    pub fn create_texture_with_data(&self, queue: &Queue, desc: &TextureDesc, data: &[u8]) -> Texture {
        let texture = self.create_texture(desc);
        queue.write_texture(&texture, (0, 0), (desc.width, desc.height), data);
        texture
    }

    /// Fallible variant of [`Device::create_texture_with_data`] for the
    /// mid-render-loop, size-driven uploads (blurred desktop, peek). On
    /// this backend creation errors (validation, OOM) are reported through
    /// the installed error handler rather than a return value, so this
    /// never returns `Err` — the fallible signature exists for the d3d11
    /// backend, where an HRESULT failure would otherwise panic the render
    /// worker.
    pub fn try_create_texture_with_data(&self, queue: &Queue, desc: &TextureDesc, data: &[u8]) -> Result<Texture> {
        Ok(self.create_texture_with_data(queue, desc, data))
    }

    pub fn create_sampler(&self, label: &str, filter: FilterMode) -> Sampler {
        let f = match filter {
            FilterMode::Nearest => wgpu::FilterMode::Nearest,
        };
        Sampler {
            raw: self
                .device
                .create_sampler(&wgpu::SamplerDescriptor {
                    label: Some(label),
                    address_mode_u: wgpu::AddressMode::ClampToEdge,
                    address_mode_v: wgpu::AddressMode::ClampToEdge,
                    address_mode_w: wgpu::AddressMode::ClampToEdge,
                    mag_filter: f,
                    min_filter: f,
                    mipmap_filter: wgpu::MipmapFilterMode::Nearest,
                    ..Default::default()
                }),
        }
    }

    /// Bind `resources` against `layout`'s binding table
    /// ([`ShaderId::bindings`]). Resources are given in table order; each
    /// kind is checked against the table.
    pub fn create_bind_group(&self, label: &str, layout: ShaderId, resources: &[BindingRes]) -> BindGroup {
        let table = layout.bindings();
        assert_eq!(
            table.len(),
            resources.len(),
            "bind group '{label}': {} resources for a {}-entry table",
            resources.len(),
            table.len()
        );
        let entries: Vec<wgpu::BindGroupEntry> = table
            .iter()
            .zip(resources)
            .map(|(entry, res)| {
                let resource = match (entry.kind, res) {
                    (ResourceKind::UniformBuffer, BindingRes::Uniform(b)) => b.raw.as_entire_binding(),
                    (ResourceKind::Texture2D, BindingRes::Texture(t)) => wgpu::BindingResource::TextureView(&t.view),
                    (ResourceKind::Sampler, BindingRes::Sampler(s)) => wgpu::BindingResource::Sampler(&s.raw),
                    (kind, _) => panic!(
                        "bind group '{label}': binding {} expects {kind:?}, got a different resource",
                        entry.binding
                    ),
                };
                wgpu::BindGroupEntry {
                    binding: entry.binding,
                    resource,
                }
            })
            .collect();
        BindGroup {
            raw: self
                .device
                .create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some(label),
                    layout: &self.bgl(layout),
                    entries: &entries,
                }),
        }
    }

    /// The (lazily created, cached) bind group layout for `id`.
    pub(crate) fn bgl(&self, id: ShaderId) -> wgpu::BindGroupLayout {
        let mut cache = self.bgls.lock().unwrap();
        let slot = &mut cache[id.index()];
        if let Some(bgl) = slot {
            return bgl.clone();
        }
        let bgl = create_bind_group_layout(&self.device, id);
        *slot = Some(bgl.clone());
        bgl
    }

    pub(super) fn raw(&self) -> &wgpu::Device {
        &self.device
    }

    pub(super) fn raw_adapter(&self) -> &wgpu::Adapter {
        &self.adapter
    }
}

impl Queue {
    pub fn write_buffer(&self, buffer: &Buffer, offset: u64, data: &[u8]) {
        self.queue
            .write_buffer(&buffer.raw, offset, data);
    }

    /// Upload `data` (tightly packed rows) into the `size` region of
    /// `texture` at `origin`. Full-texture uploads pass `(0, 0)` and the
    /// texture's own size; the atlases upload sub-rectangles.
    pub fn write_texture(&self, texture: &Texture, origin: (u32, u32), size: (u32, u32), data: &[u8]) {
        let (width, height) = size;
        self.queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &texture.raw,
                mip_level: 0,
                origin: wgpu::Origin3d {
                    x: origin.0,
                    y: origin.1,
                    z: 0,
                },
                aspect: wgpu::TextureAspect::All,
            },
            data,
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(texture.bytes_per_pixel * width),
                rows_per_image: Some(height),
            },
            wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
        );
    }

    pub(super) fn raw(&self) -> &wgpu::Queue {
        &self.queue
    }
}

// ── Adapter selection (verbatim from gpu/device.rs) ─────────────────

async fn select_adapter(instance: &Arc<wgpu::Instance>, adapter_hint: Option<(u32, u32)>) -> Result<wgpu::Adapter> {
    let adapter = match adapter_hint {
        Some((vendor, device)) => {
            info!("adapter hint: vendor=0x{:04X} device=0x{:04X}", vendor, device);
            // Enumeration is the expensive half of adapter selection: on
            // DX12 wgpu-hal does a full `D3D12CreateDevice` per adapter
            // just to read its capabilities (wgpu-hal-30.0.0
            // `dx12/adapter.rs`, `expose_adapter`) before anything gets
            // filtered. `request_adapter` runs that same
            // `hal::enumerate_adapters` internally (wgpu-core-30.0.0
            // `instance.rs`), so a hint *miss* used to pay for the whole
            // thing twice. Enumerate once and satisfy both the hint and
            // the fallback out of the same Vec.
            let mut adapters = instance.enumerate_adapters(backends()).await;
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
    match adapter {
        Some(a) => Ok(a),
        None => {
            // Only reachable with no hint at all, or when enumeration came
            // back empty — in which case `request_adapter` will not find
            // anything either, but it produces the canonical "no adapters"
            // error.
            Ok(instance
                .request_adapter(&wgpu::RequestAdapterOptions {
                    power_preference: wgpu::PowerPreference::HighPerformance,
                    compatible_surface: None,
                    force_fallback_adapter: false,
                    // limit bucketing only matters when exposing wgpu to
                    // untrusted content; we want the adapter's real limits.
                    apply_limit_buckets: false,
                })
                .await?)
        }
    }
}

/// Pick the adapter `request_adapter(PowerPreference::HighPerformance)`
/// would have picked, out of a Vec we already enumerated.
///
/// This replicates `get_order` from wgpu-core-30.0.0 `instance.rs` (the
/// `prefer_integrated_gpu == false` arm, which is what `HighPerformance`
/// selects): DiscreteGpu=1, IntegratedGpu=2, Other=3, VirtualGpu=4, Cpu=5,
/// lower wins. wgpu sorts with `sort_by_key` and takes the first element;
/// `min_by_key` matches that exactly, because both are stable on ties and
/// so both fall back to enumeration order.
///
/// The one thing we do *not* replicate is `compatible_surface` filtering —
/// the call above passes `compatible_surface: None`, so there is nothing
/// to filter by. If a surface is ever threaded in here, this must grow a
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

/// The one `TexFormat` → native translation for this backend. `const` so
/// `super::SURFACE_FORMAT` can be derived from the shared policy const in
/// `gxi/types.rs` at compile time.
pub(super) const fn texture_format(format: TexFormat) -> wgpu::TextureFormat {
    match format {
        TexFormat::Bgra8Unorm => wgpu::TextureFormat::Bgra8Unorm,
        TexFormat::Rgba8Unorm => wgpu::TextureFormat::Rgba8Unorm,
        TexFormat::Rgba8UnormSrgb => wgpu::TextureFormat::Rgba8UnormSrgb,
        TexFormat::R8Unorm => wgpu::TextureFormat::R8Unorm,
    }
}

// ── Plain resource wrappers ─────────────────────────────────────────

/// A uniform or per-instance vertex buffer.
pub struct Buffer {
    pub(super) raw: wgpu::Buffer,
}

/// A 2D texture plus its default view.
pub struct Texture {
    pub(super) raw: wgpu::Texture,
    pub(super) view: wgpu::TextureView,
    bytes_per_pixel: u32,
}

/// A sampler. `Clone` is cheap (the handle is refcounted) — the desktop
/// snapshot keeps a clone of the shared sampler so the peek bind group
/// can reuse it per frame.
#[derive(Clone)]
pub struct Sampler {
    pub(super) raw: wgpu::Sampler,
}

pub struct BindGroup {
    pub(super) raw: wgpu::BindGroup,
}
