//! Instance / device / queue / plain resources for the wgpu backend.
//!
//! Threading model (the contract Phase B pinned when this backend was
//! first written): every wgpu object here is free-threaded — `Device`,
//! `Queue`, buffers, textures and samplers are all `Send + Sync` and
//! internally synchronized — so, unlike the d3d11 backend, no context
//! mutex is needed, and nothing here takes a device-wide lock. wgpu's
//! error-scope stack is thread-local (the `std` feature), so two threads
//! bracketing work in [`Device::scoped`] at once (the deferred UI build
//! compiles pipelines on two threads) each see only their own errors.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

use anyhow::{Context as _, Result};
use wgpu::rwh::HasDisplayHandle;

use crate::gxi::types::{BindingRes, CreateMark, SamplerFilter, ShaderId, TexFormat, TextureDesc};
use crate::shader_bindings::ResourceKind;

use super::pipeline::create_bind_group_layout;

// ── Instance ────────────────────────────────────────────────────────

/// Environment override for the backend pick: `vulkan` or `gl`. Anything
/// else is logged and ignored, and the probe in [`create_instance`]
/// decides as usual.
const BACKEND_OVERRIDE_VAR: &str = "CLOWD_GPU_BACKEND";

/// The windowing system's display, handed to every `wgpu::Instance` built
/// in this process (see [`set_display_handle`]). Process-wide rather than
/// a field of [`Instance`] because the instance is created deep inside
/// `CaptureSession::new`, which never sees the event loop, while the
/// handle is only obtainable from the event loop on the main thread.
static DISPLAY_HANDLE: OnceLock<Arc<dyn DisplayHandleSource>> = OnceLock::new();

/// What `wgpu::InstanceDescriptor::display` wants (`WgpuHasDisplayHandle`,
/// which wgpu does not re-export), spelled locally so it can name a trait
/// object. Blanket-implemented; winit's `OwnedDisplayHandle` qualifies.
pub trait DisplayHandleSource: HasDisplayHandle + std::fmt::Debug + Send + Sync + 'static {}
impl<T: HasDisplayHandle + std::fmt::Debug + Send + Sync + 'static> DisplayHandleSource for T {}

/// Tell the backend which display its surfaces will be created on. Call
/// once, on the main thread, before the first [`Instance`] is used; a
/// second call is ignored.
///
/// Vulkan does not care (the window handle carries everything a surface
/// needs), but the GL fallback does: without a display wgpu-hal asks EGL
/// for the *surfaceless* platform (`EGL_MESA_platform_surfaceless`, which
/// every Mesa and current NVIDIA libEGL advertises) and can then never
/// create a window surface, so a GL device would come up fine and every
/// frame would then fail to present. With the X11 display it opens the real
/// `EGL_PLATFORM_X11_KHR` display instead. The headless smoke test skips
/// this and stays surfaceless, which is exactly right for it.
pub fn set_display_handle(display: impl DisplayHandleSource) {
    if DISPLAY_HANDLE
        .set(Arc::new(display))
        .is_err()
    {
        warn!("gpu display handle set twice; keeping the first");
    }
}

/// The GPU API entry point. Created once on the main thread and cloned to
/// every render worker (cheap: it wraps an `Arc`).
///
/// The `wgpu::Instance` inside is built lazily, by the first thread that
/// needs it (`Device::create` on a render worker or `Surface::create` on
/// the main thread, whichever comes first): building it loads the Vulkan
/// loader and probes for an adapter, and the main thread's startup path
/// should not pay for that when a worker can. Every clone shares the one
/// cell, so all surfaces and devices in the process come from the same
/// instance — a requirement, since a surface can only be configured
/// against an adapter of the instance that created it.
#[derive(Clone, Default)]
pub struct Instance {
    raw: Arc<OnceLock<wgpu::Instance>>,
}

impl Instance {
    pub fn new() -> Self {
        Self::default()
    }

    pub(super) fn raw(&self) -> &wgpu::Instance {
        self.raw.get_or_init(create_instance)
    }
}

/// Pick the backend and build the instance. Vulkan unless
/// [`BACKEND_OVERRIDE_VAR`] says otherwise; GL only when a Vulkan
/// instance comes up with no adapter at all (no loader, no ICD, or a
/// driver that failed to initialize) — a hint miss or an unusual adapter
/// type never triggers the fallback, so a machine with any working Vulkan
/// stays on it.
fn create_instance() -> wgpu::Instance {
    let forced = std::env::var(BACKEND_OVERRIDE_VAR)
        .ok()
        .and_then(|value| match value.to_ascii_lowercase().as_str() {
            "vulkan" | "vk" => Some(wgpu::Backends::VULKAN),
            "gl" | "gles" | "opengl" => Some(wgpu::Backends::GL),
            other => {
                warn!("{BACKEND_OVERRIDE_VAR}={other:?} is not a known backend (vulkan|gl); probing instead");
                None
            }
        });
    if let Some(backends) = forced {
        info!("gpu backend forced by {BACKEND_OVERRIDE_VAR}: {backends:?}");
        return new_instance(backends);
    }

    let vulkan = new_instance(wgpu::Backends::VULKAN);
    if pollster::block_on(vulkan.enumerate_adapters(wgpu::Backends::VULKAN)).is_empty() {
        warn!("no Vulkan adapter available; falling back to the GL backend");
        return new_instance(wgpu::Backends::GL);
    }
    vulkan
}

fn new_instance(backends: wgpu::Backends) -> wgpu::Instance {
    // The display is what lets the GL backend present at all (see
    // `set_display_handle`); Vulkan ignores it. `None` only in the headless
    // smoke test, where nothing is ever presented.
    let display = DISPLAY_HANDLE
        .get()
        .map(|d| Box::new(Arc::clone(d)) as _);
    if display.is_none() && backends.contains(wgpu::Backends::GL) {
        warn!("no display handle for the GL backend; surfaces cannot be presented on it");
    }
    wgpu::Instance::new(wgpu::InstanceDescriptor {
        backends,
        // Validation in debug builds (the d3d11 backend's debug layer
        // equivalent), nothing in release; HAL labels are noise either way.
        flags: wgpu::InstanceFlags::from_build_config() | wgpu::InstanceFlags::DISCARD_HAL_LABELS,
        display,
        ..wgpu::InstanceDescriptor::new_without_display_handle()
    })
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
    bgls: Arc<Mutex<HashMap<ShaderId, wgpu::BindGroupLayout>>>,
    /// Set by the device-lost callback installed in [`Device::create`];
    /// `Surface::acquire` reports it as `AcquireResult::DeviceLost`.
    lost: Arc<AtomicBool>,
}

/// The submission queue. `Clone + Send + Sync`; uploads only — command
/// submission and present happen inside `Frame::present`.
///
/// wgpu's queue is free-threaded and stages uploads internally, so the
/// write paths need neither the d3d11 backend's context mutex nor the
/// metal backend's write fence.
#[derive(Clone)]
pub struct Queue {
    queue: wgpu::Queue,
}

impl Device {
    /// Select an adapter and create the device + queue.
    ///
    /// `adapter_hint` is a `(vendor, device)` id pair naming the adapter
    /// that owns the output, matched against the enumerated adapters when
    /// present; the Linux monitor enumeration has no such id today, so in
    /// practice the pick is `request_adapter`'s. `mark` fires at the two
    /// telemetry-relevant milestones so the caller can stamp its startup
    /// marks ([`CreateMark`]).
    pub fn create(instance: &Instance, adapter_hint: Option<(u32, u32)>, mut mark: impl FnMut(CreateMark)) -> Result<(Device, Queue)> {
        let instance = instance.raw();
        let adapter = pollster::block_on(select_adapter(instance, adapter_hint))?;
        mark(CreateMark::AdapterSelected);

        let info = adapter.get_info();
        info!(
            "selected adapter: \"{}\" (vendor=0x{:04X} device=0x{:04X} type={:?} backend={:?} driver=\"{}\" {})",
            info.name, info.vendor, info.device, info.device_type, info.backend, info.driver, info.driver_info
        );

        let mut required_features = wgpu::Features::empty();
        if adapter
            .features()
            .contains(wgpu::Features::TIMESTAMP_QUERY)
        {
            required_features |= wgpu::Features::TIMESTAMP_QUERY;
        }

        // Split point for wedge diagnosis (the other backends log the same
        // split before their device call): a log that ends here says the
        // hang is inside `request_device`, in the driver.
        info!("requesting wgpu device");

        let (device, queue) = pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor {
            label: Some("clowd_capture gxi device"),
            required_features,
            // The adapter's own limits, verbatim: the overlay wants the real
            // texture-size ceiling (`max_texture_dimension_2d` sizes the
            // desktop snapshot), and asking for exactly what the adapter
            // reports can never fail — where `Limits::default()` would on
            // a downlevel GL adapter.
            required_limits: adapter.limits(),
            // MemoryUsage keeps the allocator's retained blocks small; the
            // overlay's handful of resources gains nothing from
            // Performance's larger pools.
            memory_hints: wgpu::MemoryHints::MemoryUsage,
            trace: wgpu::Trace::Off,
            experimental_features: wgpu::ExperimentalFeatures::disabled(),
        }))
        .context("wgpu request_device")?;

        // Uncaptured errors (anything outside a `scoped` bracket) are
        // logged instead of taking wgpu's default route, a panic with a
        // generic message: the render worker keeps going, and the real
        // error text reaches the log and Sentry.
        device.on_uncaptured_error(Arc::new(|err| {
            error!("wgpu uncaptured error (non-fatal): {err}");
        }));
        let lost = Arc::new(AtomicBool::new(false));
        device.set_device_lost_callback({
            let lost = Arc::clone(&lost);
            move |reason, message| {
                // `Destroyed` also fires on an orderly device drop, when
                // nobody is left to read the flag.
                if reason != wgpu::DeviceLostReason::Destroyed {
                    error!("wgpu device lost ({reason:?}): {message}");
                }
                lost.store(true, Ordering::Release);
            }
        });
        mark(CreateMark::DeviceReady);

        Ok((
            Device {
                device,
                adapter,
                adapter_name: info.name.into(),
                bgls: Arc::new(Mutex::new(HashMap::new())),
                lost,
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
        if let Err(e) = self.device.poll(wgpu::PollType::Wait {
            submission_index: None,
            timeout: Some(timeout),
        }) {
            warn!("wait_idle: {e}");
        }
    }

    /// Whether the device-lost callback has fired. Read by
    /// `Surface::acquire` before every frame.
    pub(super) fn is_lost(&self) -> bool {
        self.lost.load(Ordering::Acquire)
    }

    // ── Resources ───────────────────────────────────────────────────

    pub fn create_uniform_buffer(&self, label: &str, size: u64) -> Buffer {
        self.create_buffer(label, size, wgpu::BufferUsages::UNIFORM)
    }

    /// A per-instance vertex buffer. Growth (by recreation) is caller
    /// policy, as today.
    pub fn create_instance_buffer(&self, label: &str, size: u64) -> Buffer {
        self.create_buffer(label, size, wgpu::BufferUsages::VERTEX)
    }

    /// A 32-bit index buffer; growth by recreation is caller policy, like
    /// the instance buffers.
    pub fn create_index_buffer(&self, label: &str, size: u64) -> Buffer {
        self.create_buffer(label, size, wgpu::BufferUsages::INDEX)
    }

    fn create_buffer(&self, label: &str, size: u64, usage: wgpu::BufferUsages) -> Buffer {
        // wgpu writes in whole `COPY_BUFFER_ALIGNMENT` (4-byte) units, so
        // round the allocation up the way the d3d11 backend rounds
        // constant buffers to 16: invisible to callers, whose writes are
        // bounded by what they asked for.
        let size = size
            .max(1)
            .next_multiple_of(wgpu::COPY_BUFFER_ALIGNMENT);
        Buffer {
            raw: self
                .device
                .create_buffer(&wgpu::BufferDescriptor {
                    label: Some(label),
                    size,
                    usage: usage | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                }),
        }
    }

    pub fn create_texture(&self, desc: &TextureDesc) -> Texture {
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
                format: texture_format(desc.format),
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
    /// wgpu has no immutable-with-initial-data texture, so this is a
    /// create followed by `Queue::write_texture`, and a failure panics
    /// like the other backends' creation failures do.
    pub fn create_texture_with_data(&self, queue: &Queue, desc: &TextureDesc, data: &[u8]) -> Texture {
        self.try_create_texture_with_data(queue, desc, data)
            .unwrap_or_else(|e| panic!("wgpu texture '{}' ({}x{}): {e:#}", desc.label, desc.width, desc.height))
    }

    /// Fallible variant of [`Device::create_texture_with_data`] for the
    /// mid-render-loop, size-driven uploads (blurred desktop, peek):
    /// those textures are optional cosmetics, and an out-of-memory on a
    /// multi-4K desktop should be a logged skip, not a dead render worker.
    /// wgpu reports creation errors through the device's error sink
    /// rather than a return value, so the create + upload runs inside an
    /// error scope ([`Device::scoped`]) that turns them back into an
    /// `Err`. Size-mismatch asserts still panic — that is a caller bug,
    /// not a runtime condition.
    pub fn try_create_texture_with_data(&self, queue: &Queue, desc: &TextureDesc, data: &[u8]) -> Result<Texture> {
        let expected = desc.format.bytes_per_pixel() as usize * desc.width as usize * desc.height as usize;
        assert!(
            data.len() >= expected,
            "texture '{}': {} bytes for a {}x{} {:?} texture (need {expected})",
            desc.label,
            data.len(),
            desc.width,
            desc.height,
            desc.format
        );
        self.scoped(&[wgpu::ErrorFilter::OutOfMemory, wgpu::ErrorFilter::Validation], || {
            let texture = self.create_texture(desc);
            queue.write_texture(&texture, (0, 0), (desc.width, desc.height), data);
            texture
        })
        .map_err(|e| anyhow!("{e}"))
    }

    /// Every sampler in the crate is clamp-to-edge with nearest mip
    /// selection; `filter` picks the min/mag filter.
    pub fn create_sampler(&self, label: &str, filter: SamplerFilter) -> Sampler {
        let filter = match filter {
            SamplerFilter::Nearest => wgpu::FilterMode::Nearest,
            SamplerFilter::Linear => wgpu::FilterMode::Linear,
        };
        Sampler {
            raw: self
                .device
                .create_sampler(&wgpu::SamplerDescriptor {
                    label: Some(label),
                    address_mode_u: wgpu::AddressMode::ClampToEdge,
                    address_mode_v: wgpu::AddressMode::ClampToEdge,
                    address_mode_w: wgpu::AddressMode::ClampToEdge,
                    mag_filter: filter,
                    min_filter: filter,
                    mipmap_filter: wgpu::MipmapFilterMode::Nearest,
                    ..Default::default()
                }),
        }
    }

    /// Bind `resources` against `layout`'s binding table
    /// ([`ShaderId::bindings`]). Resources are given in table order; each
    /// kind is checked against the table.
    ///
    /// No register resolution here, unlike the d3d11 and metal backends:
    /// wgpu binds by the WGSL `@binding(n)` index directly, which is what
    /// the table's `binding` field is.
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
    pub(super) fn bgl(&self, id: ShaderId) -> wgpu::BindGroupLayout {
        let mut cache = self
            .bgls
            .lock()
            .expect("wgpu bind group layout cache mutex poisoned");
        cache
            .entry(id)
            .or_insert_with(|| create_bind_group_layout(&self.device, id))
            .clone()
    }

    /// Run `f` inside wgpu error scopes for `filters`, returning the first
    /// error any of them caught (innermost first). No lock: wgpu's scope
    /// stack is thread-local, so a bracket only ever captures the calling
    /// thread's own operations, and two threads (the deferred pipeline
    /// builders) can be inside `scoped` at once without seeing each
    /// other's scopes or errors. A panic inside `f` unwinds through the
    /// guards' `Drop`, which pops this thread's scopes again.
    pub(super) fn scoped<T>(&self, filters: &[wgpu::ErrorFilter], f: impl FnOnce() -> T) -> std::result::Result<T, wgpu::Error> {
        let scopes: Vec<wgpu::ErrorScopeGuard> = filters
            .iter()
            .map(|&filter| self.device.push_error_scope(filter))
            .collect();
        let value = f();
        let mut first = None;
        for scope in scopes.into_iter().rev() {
            if let Some(err) = pollster::block_on(scope.pop()) {
                first.get_or_insert(err);
            }
        }
        match first {
            Some(err) => Err(err),
            None => Ok(value),
        }
    }

    pub(super) fn raw(&self) -> &wgpu::Device {
        &self.device
    }

    pub(super) fn raw_adapter(&self) -> &wgpu::Adapter {
        &self.adapter
    }
}

impl Queue {
    /// Whole- or partial-buffer upload; wgpu stages the bytes internally,
    /// so `offset` is unrestricted beyond the 4-byte alignment wgpu asks
    /// of every copy (every call site writes whole buffers from offset 0
    /// anyway — the d3d11 backend asserts as much).
    pub fn write_buffer(&self, buffer: &Buffer, offset: u64, data: &[u8]) {
        if data.is_empty() {
            return;
        }
        self.queue
            .write_buffer(&buffer.raw, offset, data);
    }

    /// Upload `data` (tightly packed rows) into the `size` region of
    /// `texture` at `origin`. Full-texture uploads pass `(0, 0)` and the
    /// texture's own size; the atlases upload sub-rectangles.
    pub fn write_texture(&self, texture: &Texture, origin: (u32, u32), size: (u32, u32), data: &[u8]) {
        let (width, height) = size;
        if width == 0 || height == 0 {
            return;
        }
        let expected = texture.bytes_per_pixel as usize * width as usize * height as usize;
        assert!(
            data.len() >= expected,
            "write_texture: {} bytes for a {width}x{height} region (need {expected})",
            data.len()
        );
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
            &data[..expected],
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

// ── Adapter selection ───────────────────────────────────────────────

/// Match the `(vendor, device)` hint against the enumerated adapters; on
/// a miss (or no hint) let `request_adapter` pick.
async fn select_adapter(instance: &wgpu::Instance, adapter_hint: Option<(u32, u32)>) -> Result<wgpu::Adapter> {
    if let Some((vendor, device)) = adapter_hint {
        info!("adapter hint: vendor=0x{vendor:04X} device=0x{device:04X}");
        let mut adapters = instance
            .enumerate_adapters(wgpu::Backends::all())
            .await;
        match adapters.iter().position(|a| {
            let info = a.get_info();
            info.vendor == vendor && info.device == device
        }) {
            Some(idx) => {
                info!("matched adapter hint");
                return Ok(adapters.swap_remove(idx));
            }
            None => warn!("no adapter matched hint; falling back to request_adapter"),
        }
    } else {
        info!("no adapter hint; using request_adapter");
    }
    // LowPower, deliberately: on a hybrid laptop it is the integrated GPU
    // that drives the panel, and waking a powered-down discrete GPU
    // (Optimus/PRIME) costs seconds on the path to frame 0 — for a 2D
    // overlay that composites straight to the display, the wrong trade.
    // A single-GPU machine is unaffected, and a CPU adapter (lavapipe)
    // still sorts last.
    instance
        .request_adapter(&wgpu::RequestAdapterOptions {
            power_preference: wgpu::PowerPreference::LowPower,
            compatible_surface: None,
            force_fallback_adapter: false,
            // Limit bucketing only matters when exposing wgpu to untrusted
            // content; we want the adapter's real limits.
            apply_limit_buckets: false,
        })
        .await
        .context("wgpu request_adapter")
}

/// The one `TexFormat` → native translation for this backend. `const` so
/// `super::SURFACE_FORMAT` can be derived from the shared policy const in
/// `gxi/types.rs` at compile time.
pub(super) const fn texture_format(format: TexFormat) -> wgpu::TextureFormat {
    match format {
        TexFormat::Bgra8Unorm => wgpu::TextureFormat::Bgra8Unorm,
        TexFormat::Rgba8Unorm => wgpu::TextureFormat::Rgba8Unorm,
    }
}

// ── Plain resource wrappers ─────────────────────────────────────────
//
// All four are `Send + Sync` by construction: wgpu's handles are.

/// A uniform, per-instance vertex, or index buffer.
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

/// A bound resource set for one shader's layout; `Frame::set_bind_group`
/// binds it in one call.
pub struct BindGroup {
    pub(super) raw: wgpu::BindGroup,
}
