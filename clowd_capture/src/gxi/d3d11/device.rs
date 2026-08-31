//! Instance / device / queue / plain resources for the D3D11 backend.
//!
//! Threading model (the contract Phase B pinned in the wgpu backend's
//! docs): [`Device`] is free-threaded — `ID3D11Device` methods are
//! thread-safe unless `D3D11_CREATE_DEVICE_SINGLETHREADED` is passed,
//! which this backend never does — while the immediate
//! `ID3D11DeviceContext` is NOT free-threaded and therefore lives inside
//! an `Arc<Mutex<..>>` shared by [`Queue`], [`Device::wait_idle`] and the
//! per-frame path. That mutex is uncontended in practice: every context
//! touch happens on the owning render worker's thread (the deferred
//! build thread clones only [`Device`]).

use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use anyhow::{Context as _, Result};
use windows::core::Interface;
use windows::Win32::Foundation::HMODULE;
use windows::Win32::Graphics::Direct3D::{
    D3D_DRIVER_TYPE, D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_UNKNOWN, D3D_DRIVER_TYPE_WARP, D3D_FEATURE_LEVEL, D3D_FEATURE_LEVEL_11_0,
};
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, ID3D11Buffer, ID3D11Device, ID3D11DeviceContext, ID3D11Query, ID3D11SamplerState, ID3D11ShaderResourceView,
    ID3D11Texture2D, D3D11_BIND_CONSTANT_BUFFER, D3D11_BIND_SHADER_RESOURCE, D3D11_BIND_VERTEX_BUFFER, D3D11_BOX, D3D11_BUFFER_DESC,
    D3D11_COMPARISON_NEVER, D3D11_CPU_ACCESS_WRITE, D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_CREATE_DEVICE_DEBUG, D3D11_CREATE_DEVICE_FLAG,
    D3D11_FILTER_MIN_MAG_MIP_POINT, D3D11_MAPPED_SUBRESOURCE, D3D11_MAP_WRITE_DISCARD, D3D11_QUERY_DESC, D3D11_QUERY_EVENT,
    D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION, D3D11_SAMPLER_DESC, D3D11_SDK_VERSION, D3D11_SUBRESOURCE_DATA, D3D11_TEXTURE2D_DESC,
    D3D11_TEXTURE_ADDRESS_CLAMP, D3D11_USAGE_DEFAULT, D3D11_USAGE_DYNAMIC, D3D11_USAGE_IMMUTABLE,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_R8_UNORM,
    DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, IDXGIAdapter, IDXGIAdapter1, IDXGIDevice, IDXGIFactory2, DXGI_ADAPTER_FLAG_SOFTWARE,
};

use crate::gxi::types::{BindingRes, CreateMark, ShaderId, TexFormat, TextureDesc};
use crate::shader_bindings::{BindingEntry, ResourceKind};

use super::pipeline::SharedStates;

// ── Instance ────────────────────────────────────────────────────────

/// The GPU API entry point. Created once on the main thread and cloned to
/// every render worker (cheap: it is an empty token).
///
/// Unlike the wgpu backend there is nothing to initialize up front: the
/// DXGI factory is created inside [`Device::create`], *on the worker
/// thread that will use it*, so no COM object ever has to cross threads
/// just to get a device built.
#[derive(Clone, Default)]
pub struct Instance;

impl Instance {
    pub fn new() -> Self {
        Self
    }
}

// ── Context cell ────────────────────────────────────────────────────

/// The immediate `ID3D11DeviceContext`, newtyped so the `Send` claim (and
/// its justification) is stated exactly once.
pub(super) struct ContextCell(pub(super) ID3D11DeviceContext);

// SAFETY: `ID3D11DeviceContext` has no thread affinity — Microsoft's
// "Multithreading and Direct3D 11" docs require only that *calls into an
// immediate context be externally synchronized* (it is "not thread safe",
// not "thread bound"): any thread may use it as long as no two do so
// concurrently. The `Mutex` this cell always lives inside provides
// exactly that serialization, and moving the guard-protected value
// between threads is therefore safe. (COM refcounting — AddRef/Release
// on clone/drop — is atomic and always thread-safe.)
unsafe impl Send for ContextCell {}

// ── Device + Queue ──────────────────────────────────────────────────

/// The GPU device. `Clone + Send + Sync` — the deferred pipeline-build
/// thread gets its own clone while the worker keeps using the original.
#[derive(Clone)]
pub struct Device {
    device: ID3D11Device,
    /// The factory the adapter was enumerated from; `Surface::configure`
    /// creates the swapchain on it.
    factory: IDXGIFactory2,
    /// Shared with [`Queue`]: `wait_idle` needs the immediate context
    /// (Flush + event-query poll are context operations in D3D11).
    ctx: Arc<Mutex<ContextCell>>,
    /// The blend/rasterizer state objects every pipeline shares.
    states: Arc<SharedStates>,
    adapter_name: Arc<str>,
}

// SAFETY: `ID3D11Device` is free-threaded — per MSDN ("Multithreading and
// Direct3D 11" / ID3D11Device docs) all device (as opposed to context)
// methods are thread-safe unless the device was created with
// `D3D11_CREATE_DEVICE_SINGLETHREADED`, which this backend never passes.
// `IDXGIFactory2` methods are likewise safe to call from any thread (DXGI
// serializes internally on its own lock — MSDN "Multithread
// Considerations" for DXGI); in this crate it is only ever *used* from
// the worker thread that created it anyway (`Surface::configure`).
// `SharedStates` holds immutable device-child state objects whose only
// cross-thread operations are binding calls (made through the
// mutex-guarded context) and atomic refcounting. The remaining fields are
// plain data or already `Send + Sync`.
unsafe impl Send for Device {}
unsafe impl Sync for Device {}

/// The submission queue. `Clone + Send + Sync`; uploads only — command
/// submission and present happen inside `Frame::present`.
///
/// D3D11 has no separate queue object: this wraps the immediate context
/// in the `Arc<Mutex<..>>` the type-level `Sync` bound requires (see the
/// wgpu backend's `Queue` docs, which pinned this exact design).
#[derive(Clone)]
pub struct Queue {
    ctx: Arc<Mutex<ContextCell>>,
}

impl Queue {
    /// Serialized access to the immediate context. Every context touch in
    /// the backend goes through here (or through `Device::ctx`, the same
    /// mutex).
    pub(super) fn lock(&self) -> MutexGuard<'_, ContextCell> {
        self.ctx
            .lock()
            .expect("d3d11 immediate context mutex poisoned")
    }
}

impl Device {
    /// Select an adapter and create the device + queue.
    ///
    /// `adapter_hint` is an existing DXGI `(vendor, device)` id pair (from
    /// the monitor enumeration) naming the adapter that owns the output;
    /// when it matches nothing, falls back to the first hardware adapter,
    /// and when device creation fails outright, retries on WARP. `mark`
    /// fires at the two telemetry-relevant milestones so the caller can
    /// stamp its startup marks ([`CreateMark`]).
    ///
    /// The feature-level array passed to `D3D11CreateDevice` is NULL on
    /// purpose (the runtime walks its default 11.0→9.1 ladder): passing an
    /// explicit array containing 11_1 fails with `E_INVALIDARG` on old
    /// runtimes that predate that level — exactly the machines this
    /// backend exists for. The precompiled SM 5.0 blobs then require the
    /// *achieved* level to be ≥ 11_0; hardware that comes up below that
    /// (FL-10 iGPUs, e.g. Sandy Bridge) takes the same WARP retry an
    /// outright creation failure does — see `create_d3d11_device` — and
    /// only if even that ends below 11_0 (not a real configuration on any
    /// supported Windows, where WARP is FL 11_1) does `create` return
    /// `Err` and fail the worker via its existing path (the achieved
    /// level is logged either way, so telemetry sees what the machine
    /// offered).
    pub fn create(instance: &Instance, adapter_hint: Option<(u32, u32)>, mut mark: impl FnMut(CreateMark)) -> Result<(Device, Queue)> {
        let _ = instance;
        let factory: IDXGIFactory2 = unsafe { CreateDXGIFactory1() }.context("CreateDXGIFactory1")?;

        let adapter = select_adapter(&factory, adapter_hint);
        mark(CreateMark::AdapterSelected);

        // Split point for wedge diagnosis (the metal backend logs the
        // same split before its device call): a log that ends here says
        // the hang is inside D3D11CreateDevice, in the driver.
        info!("creating d3d11 device");

        let (device, context, feature_level) = create_d3d11_device(adapter.as_ref())?;

        let (adapter_name, vendor, dev_id, umd_version) = describe_device_adapter(&device);
        info!(
            "d3d11 device created: \"{}\" (vendor=0x{:04X} device=0x{:04X} umd={}) feature level {}",
            adapter_name,
            vendor,
            dev_id,
            umd_version
                .map(format_umd_version)
                .unwrap_or_else(|| "unknown".into()),
            format_feature_level(feature_level),
        );

        // Safety net only: `create_d3d11_device` already retries on WARP
        // when hardware ends up below 11_0, so reaching this bail means
        // even WARP could not offer 11_0.
        if feature_level.0 < D3D_FEATURE_LEVEL_11_0.0 {
            anyhow::bail!(
                "d3d11 feature level {} is below 11_0; the precompiled SM 5.0 shaders cannot run",
                format_feature_level(feature_level)
            );
        }

        let states = SharedStates::create(&device).context("d3d11 shared render states")?;
        mark(CreateMark::DeviceReady);

        let ctx = Arc::new(Mutex::new(ContextCell(context)));
        Ok((
            Device {
                device,
                factory,
                ctx: ctx.clone(),
                states: Arc::new(states),
                adapter_name: adapter_name.into(),
            },
            Queue {
                ctx,
            },
        ))
    }

    pub fn adapter_name(&self) -> &str {
        &self.adapter_name
    }

    pub fn max_texture_dimension_2d(&self) -> u32 {
        // `create` guarantees FL ≥ 11_0, where the D3D11 spec fixes this
        // at 16384 for every adapter.
        D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION
    }

    /// Block until all submitted GPU work has completed (bounded by
    /// `timeout`). Frame 0 uses this so `first_render` means "the GPU is
    /// actually done", not "commands were queued". Implemented as Flush +
    /// event-query poll — the D3D11 equivalent of a fence wait.
    pub fn wait_idle(&self, timeout: Duration) {
        let mut query: Option<ID3D11Query> = None;
        let desc = D3D11_QUERY_DESC {
            Query: D3D11_QUERY_EVENT,
            MiscFlags: 0,
        };
        if let Err(e) = unsafe {
            self.device
                .CreateQuery(&desc, Some(&mut query))
        } {
            warn!("wait_idle: event query creation failed ({e}); falling back to Flush only");
            let ctx = self.lock_ctx();
            unsafe { ctx.0.Flush() };
            return;
        }
        let query = query.expect("CreateQuery succeeded without an object");

        {
            let ctx = self.lock_ctx();
            unsafe {
                ctx.0.End(&query);
                ctx.0.Flush();
            }
        }

        let deadline = Instant::now() + timeout;
        loop {
            // `GetData` writes TRUE into `done` once the GPU has drained
            // past the query; until then it returns S_FALSE and leaves the
            // output untouched — and windows-rs folds S_FALSE into `Ok`,
            // so the write, not the HRESULT, is the completion signal.
            let mut done: u32 = 0;
            let hr = {
                let ctx = self.lock_ctx();
                unsafe {
                    ctx.0
                        .GetData(&query, Some(&mut done as *mut u32 as *mut core::ffi::c_void), 4, 0)
                }
            };
            match hr {
                Err(e) => {
                    warn!("wait_idle: GetData failed: {e}");
                    return;
                }
                Ok(()) if done != 0 => return,
                Ok(()) => {}
            }
            if Instant::now() >= deadline {
                warn!("wait_idle: GPU still busy after {timeout:?}");
                return;
            }
            std::thread::sleep(Duration::from_millis(1));
        }
    }

    // ── Resources ───────────────────────────────────────────────────

    pub fn create_uniform_buffer(&self, label: &str, size: u64) -> Buffer {
        // D3D11 requires constant-buffer sizes in whole 16-byte registers.
        // Rounding up is invisible to callers: writes are bounded by the
        // requested size and shaders read only their declared fields.
        let byte_width = (size.max(1) as u32).next_multiple_of(16);
        self.create_dynamic_buffer(label, byte_width, D3D11_BIND_CONSTANT_BUFFER.0 as u32)
    }

    /// A per-instance vertex buffer. Growth (by recreation) is caller
    /// policy, as today.
    pub fn create_instance_buffer(&self, label: &str, size: u64) -> Buffer {
        self.create_dynamic_buffer(label, size.max(1) as u32, D3D11_BIND_VERTEX_BUFFER.0 as u32)
    }

    fn create_dynamic_buffer(&self, label: &str, byte_width: u32, bind_flags: u32) -> Buffer {
        let desc = D3D11_BUFFER_DESC {
            ByteWidth: byte_width,
            Usage: D3D11_USAGE_DYNAMIC,
            BindFlags: bind_flags,
            CPUAccessFlags: D3D11_CPU_ACCESS_WRITE.0 as u32,
            MiscFlags: 0,
            StructureByteStride: 0,
        };
        let mut raw: Option<ID3D11Buffer> = None;
        unsafe {
            self.device
                .CreateBuffer(&desc, None, Some(&mut raw))
        }
        .unwrap_or_else(|e| panic!("d3d11 buffer '{label}' ({byte_width} bytes): {e}"));
        Buffer {
            raw: raw.expect("CreateBuffer succeeded without an object"),
            size: byte_width as u64,
        }
    }

    pub fn create_texture(&self, desc: &TextureDesc) -> Texture {
        self.create_texture_impl(desc, None)
    }

    /// Primary texture path: create and upload the full contents in one
    /// call (`data` is tightly packed, `bytes_per_pixel * width` per row).
    /// `USAGE_IMMUTABLE` + `pInitialData` — the driver never has to worry
    /// about future writes, and `Queue::write_texture` is (correctly)
    /// impossible to use on these because nothing in the crate writes to a
    /// texture it created with data (empty-then-written textures — the
    /// atlases — go through [`Device::create_texture`], `USAGE_DEFAULT`).
    pub fn create_texture_with_data(&self, queue: &Queue, desc: &TextureDesc, data: &[u8]) -> Texture {
        self.try_create_texture_with_data(queue, desc, data)
            .unwrap_or_else(|e| panic!("d3d11 texture '{}' ({}x{}): {e:#}", desc.label, desc.width, desc.height))
    }

    /// Fallible variant of [`Device::create_texture_with_data`] for the
    /// mid-render-loop, size-driven uploads (blurred desktop, peek):
    /// those textures are optional cosmetics, and an `E_OUTOFMEMORY` on a
    /// multi-4K desktop should be a logged skip, not a dead render worker
    /// (this backend has no installed device error handler to catch it
    /// otherwise). Size-mismatch asserts still panic — that is a
    /// caller bug, not a runtime condition.
    pub fn try_create_texture_with_data(&self, queue: &Queue, desc: &TextureDesc, data: &[u8]) -> Result<Texture> {
        let _ = queue; // the upload rides device creation; no context needed
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
        let init = D3D11_SUBRESOURCE_DATA {
            pSysMem: data.as_ptr() as *const core::ffi::c_void,
            SysMemPitch: desc.format.bytes_per_pixel() * desc.width,
            SysMemSlicePitch: 0,
        };
        self.try_create_texture_impl(desc, Some(init))
    }

    fn create_texture_impl(&self, desc: &TextureDesc, init: Option<D3D11_SUBRESOURCE_DATA>) -> Texture {
        self.try_create_texture_impl(desc, init)
            .unwrap_or_else(|e| panic!("d3d11 texture '{}' ({}x{}): {e:#}", desc.label, desc.width, desc.height))
    }

    fn try_create_texture_impl(&self, desc: &TextureDesc, init: Option<D3D11_SUBRESOURCE_DATA>) -> Result<Texture> {
        let raw_desc = D3D11_TEXTURE2D_DESC {
            Width: desc.width,
            Height: desc.height,
            MipLevels: 1,
            ArraySize: 1,
            Format: texture_format(desc.format),
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: if init.is_some() {
                D3D11_USAGE_IMMUTABLE
            } else {
                D3D11_USAGE_DEFAULT
            },
            BindFlags: D3D11_BIND_SHADER_RESOURCE.0 as u32,
            CPUAccessFlags: 0,
            MiscFlags: 0,
        };
        let mut tex: Option<ID3D11Texture2D> = None;
        unsafe {
            self.device
                .CreateTexture2D(&raw_desc, init.as_ref().map(|i| i as *const _), Some(&mut tex))
        }
        .context("CreateTexture2D")?;
        let tex = tex.expect("CreateTexture2D succeeded without an object");
        let mut srv: Option<ID3D11ShaderResourceView> = None;
        unsafe {
            self.device
                .CreateShaderResourceView(&tex, None, Some(&mut srv))
        }
        .context("CreateShaderResourceView")?;
        Ok(Texture {
            tex,
            srv: srv.expect("CreateShaderResourceView succeeded without an object"),
            bytes_per_pixel: desc.format.bytes_per_pixel(),
        })
    }

    /// Every sampler in the crate is nearest-filtered, clamp-to-edge; a
    /// filter parameter joins the signature the day a pipeline wants
    /// something else.
    pub fn create_sampler(&self, label: &str) -> Sampler {
        let desc = D3D11_SAMPLER_DESC {
            Filter: D3D11_FILTER_MIN_MAG_MIP_POINT,
            AddressU: D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV: D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW: D3D11_TEXTURE_ADDRESS_CLAMP,
            MipLODBias: 0.0,
            MaxAnisotropy: 1,
            ComparisonFunc: D3D11_COMPARISON_NEVER,
            BorderColor: [0.0; 4],
            MinLOD: 0.0,
            MaxLOD: f32::MAX,
        };
        let mut raw: Option<ID3D11SamplerState> = None;
        unsafe {
            self.device
                .CreateSamplerState(&desc, Some(&mut raw))
        }
        .unwrap_or_else(|e| panic!("d3d11 sampler '{label}': {e}"));
        Sampler {
            raw: raw.expect("CreateSamplerState succeeded without an object"),
        }
    }

    /// Bind `resources` against `layout`'s binding table
    /// ([`ShaderId::bindings`]). Resources are given in table order; each
    /// kind is checked against the table.
    ///
    /// Register resolution happens here, once, not per frame: the table is
    /// walked in order with three independent counters (`b`/`t`/`s`, all
    /// space0) — the same walk `build.rs` used to assign the SM 5.0 blob
    /// registers (see the contract note in `src/shader_bindings.rs`) — and
    /// the result is stored as per-stage slot lists that
    /// `Frame::set_bind_group` replays with plain `*SSet*` calls.
    pub fn create_bind_group(&self, label: &str, layout: ShaderId, resources: &[BindingRes]) -> BindGroup {
        let table = layout.bindings();
        assert_eq!(
            table.len(),
            resources.len(),
            "bind group '{label}': {} resources for a {}-entry table",
            resources.len(),
            table.len()
        );
        let mut bg = BindGroup::default();
        let (mut b, mut t, mut s) = (0u32, 0u32, 0u32);
        for (entry, res) in table.iter().zip(resources) {
            match (entry.kind, res) {
                (ResourceKind::UniformBuffer, BindingRes::Uniform(buf)) => {
                    push_stage(entry, &mut bg.vs_cbufs, &mut bg.ps_cbufs, b, buf.raw.clone());
                    b += 1;
                }
                (ResourceKind::Texture2D, BindingRes::Texture(tex)) => {
                    push_stage(entry, &mut bg.vs_srvs, &mut bg.ps_srvs, t, tex.srv.clone());
                    t += 1;
                }
                (ResourceKind::Sampler, BindingRes::Sampler(sam)) => {
                    push_stage(entry, &mut bg.vs_samplers, &mut bg.ps_samplers, s, sam.raw.clone());
                    s += 1;
                }
                (kind, _) => panic!(
                    "bind group '{label}': binding {} expects {kind:?}, got a different resource",
                    entry.binding
                ),
            }
        }
        bg
    }

    pub(super) fn lock_ctx(&self) -> MutexGuard<'_, ContextCell> {
        self.ctx
            .lock()
            .expect("d3d11 immediate context mutex poisoned")
    }

    pub(super) fn raw(&self) -> &ID3D11Device {
        &self.device
    }

    pub(super) fn factory(&self) -> &IDXGIFactory2 {
        &self.factory
    }

    pub(super) fn states(&self) -> &SharedStates {
        &self.states
    }
}

fn push_stage<T: Clone>(entry: &BindingEntry, vs: &mut Vec<(u32, T)>, ps: &mut Vec<(u32, T)>, slot: u32, res: T) {
    if entry.vertex {
        vs.push((slot, res.clone()));
    }
    if entry.fragment {
        ps.push((slot, res));
    }
}

impl Queue {
    /// Whole-buffer upload. D3D11 dynamic buffers are written with
    /// `Map(WRITE_DISCARD)`, which invalidates the ENTIRE buffer — a
    /// partial write at a nonzero offset would leave the rest undefined —
    /// so `offset` must be 0. Every call site in the crate writes whole
    /// buffers from offset 0 (verified when this backend was written; a
    /// new offset-writing call site trips the assert immediately in any
    /// build). Bytes past `data.len()` are undefined after the write;
    /// that is fine because callers never draw more instances/fields than
    /// they just wrote.
    pub fn write_buffer(&self, buffer: &Buffer, offset: u64, data: &[u8]) {
        assert_eq!(offset, 0, "d3d11 write_buffer is whole-buffer (WRITE_DISCARD); offset must be 0");
        assert!(
            data.len() as u64 <= buffer.size,
            "write_buffer: {} bytes into a {}-byte buffer",
            data.len(),
            buffer.size
        );
        if data.is_empty() {
            return;
        }
        let ctx = self.lock();
        unsafe {
            let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
            if let Err(e) = ctx
                .0
                .Map(&buffer.raw, 0, D3D11_MAP_WRITE_DISCARD, 0, Some(&mut mapped))
            {
                error!("d3d11 buffer map failed (write dropped): {e}");
                return;
            }
            std::ptr::copy_nonoverlapping(data.as_ptr(), mapped.pData as *mut u8, data.len());
            ctx.0.Unmap(&buffer.raw, 0);
        }
    }

    /// Upload `data` (tightly packed rows) into the `size` region of
    /// `texture` at `origin`. Full-texture uploads pass `(0, 0)` and the
    /// texture's own size; the atlases upload sub-rectangles.
    /// `UpdateSubresource` on the `USAGE_DEFAULT` textures
    /// [`Device::create_texture`] makes (never the immutable ones).
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
        let dst_box = D3D11_BOX {
            left: origin.0,
            top: origin.1,
            front: 0,
            right: origin.0 + width,
            bottom: origin.1 + height,
            back: 1,
        };
        let ctx = self.lock();
        unsafe {
            ctx.0.UpdateSubresource(
                &texture.tex,
                0,
                Some(&dst_box),
                data.as_ptr() as *const core::ffi::c_void,
                texture.bytes_per_pixel * width,
                0,
            );
        }
    }
}

// ── Adapter selection ───────────────────────────────────────────────

/// Match the DXGI `(vendor, device)` hint against the factory's adapters;
/// on a miss (or no hint) fall back to the first hardware (non-software)
/// adapter. `None` means "let `D3D11CreateDevice` decide" — which, given
/// `create_d3d11_device`'s ladder, ends in a WARP retry.
fn select_adapter(factory: &IDXGIFactory2, adapter_hint: Option<(u32, u32)>) -> Option<IDXGIAdapter1> {
    let mut adapters: Vec<(IDXGIAdapter1, u32, u32, bool)> = Vec::new();
    let mut i = 0u32;
    while let Ok(adapter) = unsafe { factory.EnumAdapters1(i) } {
        i += 1;
        let Ok(desc) = (unsafe { adapter.GetDesc1() }) else {
            continue;
        };
        let software = desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32 != 0;
        adapters.push((adapter, desc.VendorId, desc.DeviceId, software));
    }

    if let Some((vendor, device)) = adapter_hint {
        info!("adapter hint: vendor=0x{vendor:04X} device=0x{device:04X}");
        if let Some(idx) = adapters
            .iter()
            .position(|(_, v, d, _)| *v == vendor && *d == device)
        {
            info!("matched DXGI adapter hint");
            return Some(adapters.swap_remove(idx).0);
        }
        warn!("no DXGI adapter matched hint; picking first hardware adapter");
    } else {
        info!("no DXGI adapter hint; picking first hardware adapter");
    }

    let hw = adapters
        .iter()
        .position(|(_, _, _, software)| !software);
    match hw {
        Some(idx) => Some(adapters.swap_remove(idx).0),
        None => {
            warn!("no hardware DXGI adapter found");
            None
        }
    }
}

/// `D3D11CreateDevice` with the compat-critical parameters (see
/// [`Device::create`]'s docs), plus the fallback ladder: debug layer (debug
/// builds, only if the SDK layers are installed) → plain → WARP. A
/// hardware device that comes up below feature level 11_0 counts as a
/// failure for the ladder's purposes: the precompiled SM 5.0 blobs cannot
/// run on it, so WARP (FL 11_1 software rasterizer) is the device that
/// actually serves that machine.
fn create_d3d11_device(adapter: Option<&IDXGIAdapter1>) -> Result<(ID3D11Device, ID3D11DeviceContext, D3D_FEATURE_LEVEL)> {
    // `Param<IDXGIAdapter>` accepts `Option<&IDXGIAdapter>` only for the
    // exact interface type, so upcast the enumerated `IDXGIAdapter1` once.
    let adapter: Option<IDXGIAdapter> = adapter.map(|a| {
        a.cast::<IDXGIAdapter>()
            .expect("IDXGIAdapter1 upcast cannot fail")
    });
    let base_flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;

    // Debug layer first in debug builds; it is absent on machines without
    // the SDK layers installed, in which case creation fails and we fall
    // through to the plain flags.
    // HARDWARE is the no-adapter driver type (runtime picks the default
    // hardware adapter); `try_create` swaps it for UNKNOWN when an
    // explicit adapter is passed, as the API contract requires. Passing
    // UNKNOWN with a NULL adapter is a documented E_INVALIDARG.
    if cfg!(debug_assertions) {
        match try_create(adapter.as_ref(), D3D_DRIVER_TYPE_HARDWARE, base_flags | D3D11_CREATE_DEVICE_DEBUG) {
            Ok(ok) if ok.2 .0 >= D3D_FEATURE_LEVEL_11_0.0 => return Ok(ok),
            Ok((_, _, fl)) => info!(
                "d3d11 debug-layer device reached only feature level {}; falling through",
                format_feature_level(fl)
            ),
            Err(e) => info!("d3d11 debug layer unavailable ({e}); creating without it"),
        }
    }

    // Keep going on WARP rather than fail the capture outright — both when
    // hardware creation fails and when it succeeds below 11_0 (the NULL
    // feature-level ladder happily returns an FL-10 device on e.g. Sandy
    // Bridge iGPUs, which the SM 5.0 blobs cannot use): a software-rendered
    // overlay beats an error dialog, and the log + adapter-name telemetry
    // tell us it happened.
    match try_create(adapter.as_ref(), D3D_DRIVER_TYPE_HARDWARE, base_flags) {
        Ok(ok) if ok.2 .0 >= D3D_FEATURE_LEVEL_11_0.0 => Ok(ok),
        Ok((_, _, fl)) => {
            warn!(
                "d3d11 hardware device reached only feature level {}; retrying on WARP",
                format_feature_level(fl)
            );
            try_create(None, D3D_DRIVER_TYPE_WARP, base_flags).context("D3D11CreateDevice(WARP)")
        }
        Err(e) => {
            warn!("d3d11 hardware device creation failed ({e}); retrying on WARP");
            try_create(None, D3D_DRIVER_TYPE_WARP, base_flags).context("D3D11CreateDevice(WARP)")
        }
    }
}

fn try_create(
    adapter: Option<&IDXGIAdapter>,
    driver_type: D3D_DRIVER_TYPE,
    flags: D3D11_CREATE_DEVICE_FLAG,
) -> windows::core::Result<(ID3D11Device, ID3D11DeviceContext, D3D_FEATURE_LEVEL)> {
    // An explicit adapter requires DRIVER_TYPE_UNKNOWN per the API contract.
    let driver_type = if adapter.is_some() { D3D_DRIVER_TYPE_UNKNOWN } else { driver_type };
    let mut device: Option<ID3D11Device> = None;
    let mut context: Option<ID3D11DeviceContext> = None;
    let mut feature_level = D3D_FEATURE_LEVEL::default();
    unsafe {
        D3D11CreateDevice(
            adapter,
            driver_type,
            HMODULE::default(),
            flags,
            // CRITICAL: NULL feature levels — the runtime's default
            // 11.0→9.1 ladder. An explicit array containing 11_1 is an
            // E_INVALIDARG on runtimes that predate it.
            None,
            D3D11_SDK_VERSION,
            Some(&mut device),
            Some(&mut feature_level),
            Some(&mut context),
        )?;
    }
    Ok((
        device.expect("D3D11CreateDevice succeeded without a device"),
        context.expect("D3D11CreateDevice succeeded without a context"),
        feature_level,
    ))
}

/// Adapter identity for logging, read back off the created device (so it
/// is right even on the WARP path, where no enumerated adapter was used).
fn describe_device_adapter(device: &ID3D11Device) -> (String, u32, u32, Option<i64>) {
    let fallback = ("unknown adapter".to_string(), 0, 0, None);
    let Ok(dxgi_device) = device.cast::<IDXGIDevice>() else {
        return fallback;
    };
    let Ok(adapter) = (unsafe { dxgi_device.GetAdapter() }) else {
        return fallback;
    };
    let Ok(desc) = (unsafe { adapter.GetDesc() }) else {
        return fallback;
    };
    let len = desc
        .Description
        .iter()
        .position(|&c| c == 0)
        .unwrap_or(desc.Description.len());
    let name = String::from_utf16_lossy(&desc.Description[..len]);
    // The documented way to read the user-mode driver version off an
    // adapter (works for D3D10+ interfaces only, hence IDXGIDevice's IID).
    let umd = unsafe { adapter.CheckInterfaceSupport(&IDXGIDevice::IID) }.ok();
    (name, desc.VendorId, desc.DeviceId, umd)
}

fn format_umd_version(v: i64) -> String {
    format!(
        "{}.{}.{}.{}",
        (v >> 48) & 0xFFFF,
        (v >> 32) & 0xFFFF,
        (v >> 16) & 0xFFFF,
        v & 0xFFFF
    )
}

fn format_feature_level(fl: D3D_FEATURE_LEVEL) -> String {
    format!("{}.{}", (fl.0 >> 12) & 0xF, (fl.0 >> 8) & 0xF)
}

/// The one `TexFormat` → native translation for this backend. `const` so
/// `super::SURFACE_FORMAT` can be derived from the shared policy const in
/// `gxi/types.rs` at compile time.
pub(super) const fn texture_format(format: TexFormat) -> DXGI_FORMAT {
    match format {
        TexFormat::Bgra8Unorm => DXGI_FORMAT_B8G8R8A8_UNORM,
        TexFormat::Rgba8Unorm => DXGI_FORMAT_R8G8B8A8_UNORM,
        TexFormat::Rgba8UnormSrgb => DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,
        TexFormat::R8Unorm => DXGI_FORMAT_R8_UNORM,
    }
}

// ── Plain resource wrappers ─────────────────────────────────────────
//
// SAFETY (applies to the four `unsafe impl` pairs below): each type holds
// only device-child COM objects (`ID3D11Buffer` / `ID3D11Texture2D` /
// `ID3D11ShaderResourceView` / `ID3D11SamplerState`) plus plain data.
// D3D11 device children have no thread affinity: every operation
// performed *through* them in this backend is either a device method
// (free-threaded, see [`Device`]'s safety note) or a context method made
// under the context mutex, and the objects' own refcounting
// (AddRef/Release on clone/drop) is atomic per COM rules. Immutable
// state is all that remains, so sharing references across threads is safe.

/// A uniform or per-instance vertex buffer.
pub struct Buffer {
    pub(super) raw: ID3D11Buffer,
    /// Requested size rounded to D3D11's constraints; write bounds are
    /// checked against it.
    size: u64,
}

unsafe impl Send for Buffer {}
unsafe impl Sync for Buffer {}

/// A 2D texture plus its default shader-resource view.
pub struct Texture {
    pub(super) tex: ID3D11Texture2D,
    pub(super) srv: ID3D11ShaderResourceView,
    bytes_per_pixel: u32,
}

unsafe impl Send for Texture {}
unsafe impl Sync for Texture {}

/// A sampler. `Clone` is cheap (COM refcount) — the desktop snapshot
/// keeps a clone of the shared sampler so the peek bind group can reuse
/// it per frame.
#[derive(Clone)]
pub struct Sampler {
    pub(super) raw: ID3D11SamplerState,
}

unsafe impl Send for Sampler {}
unsafe impl Sync for Sampler {}

/// Pre-resolved register slot lists, one per stage and resource class —
/// the D3D11 spelling of a bind group. Built once by
/// [`Device::create_bind_group`]; `Frame::set_bind_group` replays it.
#[derive(Default)]
pub struct BindGroup {
    pub(super) vs_cbufs: Vec<(u32, ID3D11Buffer)>,
    pub(super) ps_cbufs: Vec<(u32, ID3D11Buffer)>,
    pub(super) vs_srvs: Vec<(u32, ID3D11ShaderResourceView)>,
    pub(super) ps_srvs: Vec<(u32, ID3D11ShaderResourceView)>,
    pub(super) vs_samplers: Vec<(u32, ID3D11SamplerState)>,
    pub(super) ps_samplers: Vec<(u32, ID3D11SamplerState)>,
}

unsafe impl Send for BindGroup {}
unsafe impl Sync for BindGroup {}
