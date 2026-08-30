//! Instance / device / queue / plain resources for the Metal backend.
//!
//! Threading model (the contract Phase B pinned in the wgpu backend's
//! docs): `MTLDevice`, `MTLCommandQueue` and the resource objects are all
//! free-threaded - Apple's Metal docs ("About Threading and Multiprocessing
//! in Metal") make every object here safe to use from multiple threads,
//! with per-frame encoding (one thread at a time in this crate) the only
//! exception. The one piece of shared mutable state is the write fence:
//! resources use CPU-accessible memory the CPU writes directly, so
//! [`Queue`] keeps the last presented frame's command buffer and the first
//! `write_*` after a present waits for it - wgpu staged uploads internally
//! and D3D11 renamed via WRITE_DISCARD; naive writes here would race the
//! in-flight frame.

use std::ptr::NonNull;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use anyhow::{Context as _, Result};
use objc2::rc::Retained;
use objc2::runtime::ProtocolObject;
use objc2_foundation::NSString;
use objc2_metal::{
    MTLBuffer as MTLBufferProto, MTLCommandBuffer as MTLCommandBufferProto, MTLCommandBufferStatus,
    MTLCommandQueue as MTLCommandQueueProto, MTLCreateSystemDefaultDevice, MTLDevice as MTLDeviceProto, MTLOrigin, MTLPixelFormat,
    MTLRegion, MTLResource as _, MTLResourceOptions, MTLSamplerAddressMode, MTLSamplerDescriptor, MTLSamplerMinMagFilter,
    MTLSamplerMipFilter, MTLSamplerState, MTLSize, MTLStorageMode, MTLTexture as MTLTextureProto, MTLTextureDescriptor, MTLTextureUsage,
};

use crate::gxi::types::{BindingRes, CreateMark, ShaderId, TexFormat, TextureDesc};
use crate::shader_bindings::{BindingEntry, ResourceKind};

// ── Instance ────────────────────────────────────────────────────────

/// The GPU API entry point. Created once on the main thread and cloned to
/// every render worker (cheap: it is an empty token).
///
/// Like the d3d11 backend there is nothing to initialize up front: Metal
/// has no instance object at all, and the device is created inside
/// [`Device::create`], *on the worker thread that will use it*.
#[derive(Clone, Default)]
pub struct Instance;

impl Instance {
    pub fn new() -> Self {
        Self
    }
}

// ── Device + Queue ──────────────────────────────────────────────────

/// The GPU device. `Clone + Send + Sync` - the deferred pipeline-build
/// thread gets its own clone while the worker keeps using the original.
#[derive(Clone)]
pub struct Device {
    device: Retained<ProtocolObject<dyn MTLDeviceProto>>,
    /// A clone of [`Queue`]'s command queue: `wait_idle` needs to submit
    /// a marker command buffer, which is a queue operation in Metal.
    queue: Retained<ProtocolObject<dyn MTLCommandQueueProto>>,
    adapter_name: Arc<str>,
    /// Storage mode for every texture: `Shared` on unified-memory GPUs
    /// (Apple silicon), `Managed` on discrete/Intel GPUs, where Metal
    /// allows shared storage for buffers only and texture creation would
    /// fail. `replaceRegion` is valid on both and performs the CPU-to-GPU
    /// sync itself on Managed textures.
    texture_storage_mode: MTLStorageMode,
}

// SAFETY: `MTLDevice` and `MTLCommandQueue` are thread-safe - Apple's
// Metal documentation ("About Threading and Multiprocessing in Metal")
// lists both among the objects that may be used from multiple threads
// simultaneously; objc2-metal simply does not translate that guarantee
// into `Send`/`Sync` impls on its protocol objects. Objective-C
// refcounting (retain/release on clone/drop) is atomic. The remaining
// fields are plain data.
unsafe impl Send for Device {}
unsafe impl Sync for Device {}

/// The submission queue. `Clone + Send + Sync`; uploads only - command
/// submission and present happen inside `Frame::present`.
///
/// Wraps the `MTLCommandQueue` plus the write fence: the command buffer
/// of the last presented frame (stored by `Frame::present`), which the
/// first `write_*` after a present waits on before touching shared
/// memory the GPU may still be reading. With a maximum drawable count of
/// 2 the fence almost never actually blocks.
#[derive(Clone)]
pub struct Queue {
    raw: Retained<ProtocolObject<dyn MTLCommandQueueProto>>,
    /// See [`Queue`] docs. Shared across clones so the fence is one per
    /// device, not one per handle.
    last_submitted: Arc<Mutex<Option<SubmittedCmd>>>,
}

// SAFETY: `MTLCommandQueue` is thread-safe (see [`Device`]'s safety
// note); the fence slot's own safety story is on [`SubmittedCmd`].
unsafe impl Send for Queue {}
unsafe impl Sync for Queue {}

/// The write fence's slot: the last presented frame's command buffer.
struct SubmittedCmd(Retained<ProtocolObject<dyn MTLCommandBufferProto>>);

// SAFETY: `MTLCommandBuffer` is not documented as free-threaded, but
// every touch of the stored one - the store in `Frame::present`, the
// status/wait in the write fence - happens under the `last_submitted`
// mutex, which provides the required serialization. Refcounting is
// atomic.
unsafe impl Send for SubmittedCmd {}
unsafe impl Sync for SubmittedCmd {}

impl Device {
    /// Create the device + queue.
    ///
    /// `adapter_hint` is an existing DXGI `(vendor, device)` id pair (from
    /// the monitor enumeration on Windows) and is meaningless to Metal -
    /// it is logged and ignored; `MTLCreateSystemDefaultDevice` returns
    /// the GPU driving the main display (and on automatic-graphics-
    /// switching Macs, wakes the discrete GPU - the same device wgpu's
    /// Metal backend selected). `mark` fires at the two telemetry-relevant
    /// milestones so the caller can stamp its startup marks
    /// ([`CreateMark`]); there is no separate adapter enumeration on this
    /// backend, so the `AdapterSelected` → `DeviceReady` delta is ~0 (see
    /// the backend-aware note on [`CreateMark`]).
    pub fn create(instance: &Instance, adapter_hint: Option<(u32, u32)>, mut mark: impl FnMut(CreateMark)) -> Result<(Device, Queue)> {
        let _ = instance;
        if let Some((vendor, dev)) = adapter_hint {
            info!("ignoring DXGI-shaped adapter hint (vendor=0x{vendor:04X} device=0x{dev:04X}); Metal uses the system default device");
        }

        // Split point for wedge diagnosis (mirrors the d3d11 backend's
        // "creating d3d11 device" line): a log that ends here says the
        // hang is inside MTLCreateSystemDefaultDevice, in the driver.
        info!("creating metal device");

        let device = MTLCreateSystemDefaultDevice().context("MTLCreateSystemDefaultDevice returned nil (no Metal-capable GPU)")?;
        mark(CreateMark::AdapterSelected);

        let adapter_name = device.name().to_string();
        info!("metal device created: \"{adapter_name}\"");

        // See the field docs: shared-storage textures are a
        // unified-memory feature; Intel/AMD Macs need Managed.
        let texture_storage_mode = if device.hasUnifiedMemory() {
            MTLStorageMode::Shared
        } else {
            MTLStorageMode::Managed
        };

        let queue = device
            .newCommandQueue()
            .context("MTLDevice newCommandQueue returned nil")?;
        mark(CreateMark::DeviceReady);

        Ok((
            Device {
                device,
                queue: queue.clone(),
                adapter_name: adapter_name.into(),
                texture_storage_mode,
            },
            Queue {
                raw: queue,
                last_submitted: Arc::new(Mutex::new(None)),
            },
        ))
    }

    pub fn adapter_name(&self) -> &str {
        &self.adapter_name
    }

    pub fn max_texture_dimension_2d(&self) -> u32 {
        // 16384 on every Metal GPU in a Mac (MTLGPUFamily mac2 baseline,
        // which every macOS version this app supports requires).
        16384
    }

    /// Block until all submitted GPU work has completed (bounded by
    /// `timeout`). Frame 0 uses this so `first_render` means "the GPU is
    /// actually done", not "commands were queued". Implemented by
    /// committing an empty marker command buffer and polling its status -
    /// command buffers on one queue complete in order, so the marker
    /// completing means everything before it has drained. Polled against
    /// the deadline rather than `waitUntilCompleted`, which has no
    /// timeout and would hang the worker forever on a wedged queue.
    pub fn wait_idle(&self, timeout: Duration) {
        let Some(marker) = self.queue.commandBuffer() else {
            warn!("wait_idle: commandBuffer returned nil; nothing to wait on");
            return;
        };
        marker.commit();

        let deadline = Instant::now() + timeout;
        loop {
            match marker.status() {
                MTLCommandBufferStatus::Completed => return,
                MTLCommandBufferStatus::Error => {
                    warn!("wait_idle: marker command buffer completed with an error");
                    return;
                }
                _ => {}
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
        // No 16-byte-register rounding here (contrast d3d11): Metal
        // buffer arguments have no minimum-size constraint beyond what
        // the shader actually reads.
        self.create_shared_buffer(label, size)
    }

    /// A per-instance vertex buffer. Growth (by recreation) is caller
    /// policy, as today.
    pub fn create_instance_buffer(&self, label: &str, size: u64) -> Buffer {
        self.create_shared_buffer(label, size)
    }

    fn create_shared_buffer(&self, label: &str, size: u64) -> Buffer {
        let len = size.max(1) as usize;
        let raw = self
            .device
            .newBufferWithLength_options(len, MTLResourceOptions::StorageModeShared)
            .unwrap_or_else(|| panic!("metal buffer '{label}' ({len} bytes): allocation failed"));
        raw.setLabel(Some(&NSString::from_str(label)));
        Buffer {
            raw,
            size: len as u64,
        }
    }

    pub fn create_texture(&self, desc: &TextureDesc) -> Texture {
        self.try_create_texture(desc)
            .unwrap_or_else(|e| panic!("metal texture '{}' ({}x{}): {e:#}", desc.label, desc.width, desc.height))
    }

    /// Primary texture path: create and upload the full contents in one
    /// call (`data` is tightly packed, `bytes_per_pixel * width` per row).
    /// Metal has no immutable usage to declare (contrast d3d11's
    /// `USAGE_IMMUTABLE`); the texture is created CPU-accessible and
    /// filled with one `replaceRegion` before anything references it,
    /// which needs no write fence for the same reason.
    pub fn create_texture_with_data(&self, queue: &Queue, desc: &TextureDesc, data: &[u8]) -> Texture {
        self.try_create_texture_with_data(queue, desc, data)
            .unwrap_or_else(|e| panic!("metal texture '{}' ({}x{}): {e:#}", desc.label, desc.width, desc.height))
    }

    /// Fallible variant of [`Device::create_texture_with_data`] for the
    /// mid-render-loop, size-driven uploads (blurred desktop, peek):
    /// those textures are optional cosmetics, and an allocation failure
    /// on a multi-4K desktop should be a logged skip, not a dead render
    /// worker. Size-mismatch asserts still panic - that is a caller bug,
    /// not a runtime condition.
    pub fn try_create_texture_with_data(&self, queue: &Queue, desc: &TextureDesc, data: &[u8]) -> Result<Texture> {
        // Brand-new texture: no in-flight command buffer can reference it
        // yet, so the upload skips the queue's write fence on purpose.
        let _ = queue;
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
        let texture = self.try_create_texture(desc)?;
        texture.upload((0, 0), (desc.width, desc.height), data);
        Ok(texture)
    }

    fn try_create_texture(&self, desc: &TextureDesc) -> Result<Texture> {
        // SAFETY (texture2DDescriptor...): width/height are bounds-checked
        // against nothing by the binding; out-of-range sizes fail at
        // newTextureWithDescriptor, which is handled below.
        let raw_desc = unsafe {
            MTLTextureDescriptor::texture2DDescriptorWithPixelFormat_width_height_mipmapped(
                texture_format(desc.format),
                desc.width as usize,
                desc.height as usize,
                false,
            )
        };
        raw_desc.setUsage(MTLTextureUsage::ShaderRead);
        // CPU-accessible storage so `replaceRegion` uploads need no
        // staging or blit encoder: Shared on unified-memory GPUs (the
        // native mode on Apple silicon), Managed on Intel/AMD Macs, which
        // reject shared-storage textures (see `Device::create`). All
        // textures here are written once or rarely (atlas grows), sampled
        // every frame.
        raw_desc.setStorageMode(self.texture_storage_mode);
        let raw = self
            .device
            .newTextureWithDescriptor(&raw_desc)
            .context("newTextureWithDescriptor returned nil")?;
        raw.setLabel(Some(&NSString::from_str(desc.label)));
        Ok(Texture {
            raw,
            bytes_per_pixel: desc.format.bytes_per_pixel(),
        })
    }

    /// Every sampler in the crate is nearest-filtered, clamp-to-edge; a
    /// filter parameter joins the signature the day a pipeline wants
    /// something else.
    pub fn create_sampler(&self, label: &str) -> Sampler {
        let desc = MTLSamplerDescriptor::new();
        desc.setLabel(Some(&NSString::from_str(label)));
        desc.setMinFilter(MTLSamplerMinMagFilter::Nearest);
        desc.setMagFilter(MTLSamplerMinMagFilter::Nearest);
        desc.setMipFilter(MTLSamplerMipFilter::Nearest);
        desc.setSAddressMode(MTLSamplerAddressMode::ClampToEdge);
        desc.setTAddressMode(MTLSamplerAddressMode::ClampToEdge);
        desc.setRAddressMode(MTLSamplerAddressMode::ClampToEdge);
        let raw = self
            .device
            .newSamplerStateWithDescriptor(&desc)
            .unwrap_or_else(|| panic!("metal sampler '{label}': creation failed"));
        Sampler {
            raw,
        }
    }

    /// Bind `resources` against `layout`'s binding table
    /// ([`ShaderId::bindings`]). Resources are given in table order; each
    /// kind is checked against the table.
    ///
    /// Slot resolution happens here, once, not per frame: the table is
    /// walked in order with three independent counters (buffer / texture /
    /// sampler) - the same walk `build_msl_options` in build.rs used to
    /// assign the MSL `[[buffer/texture/sampler(n)]]` slots (see the
    /// contract note in `src/shader_bindings.rs`) - and the result is
    /// stored as per-stage slot lists that `Frame::set_bind_group` replays
    /// with plain `set{Vertex,Fragment}*` encoder calls.
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
        let (mut b, mut t, mut s) = (0usize, 0usize, 0usize);
        for (entry, res) in table.iter().zip(resources) {
            match (entry.kind, res) {
                (ResourceKind::UniformBuffer, BindingRes::Uniform(buf)) => {
                    push_stage(entry, &mut bg.vs_buffers, &mut bg.fs_buffers, b, buf.raw.clone());
                    b += 1;
                }
                (ResourceKind::Texture2D, BindingRes::Texture(tex)) => {
                    push_stage(entry, &mut bg.vs_textures, &mut bg.fs_textures, t, tex.raw.clone());
                    t += 1;
                }
                (ResourceKind::Sampler, BindingRes::Sampler(sam)) => {
                    push_stage(entry, &mut bg.vs_samplers, &mut bg.fs_samplers, s, sam.raw.clone());
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

    pub(super) fn raw(&self) -> &ProtocolObject<dyn MTLDeviceProto> {
        &self.device
    }
}

fn push_stage<T: Clone>(entry: &BindingEntry, vs: &mut Vec<(usize, T)>, fs: &mut Vec<(usize, T)>, slot: usize, res: T) {
    if entry.vertex {
        vs.push((slot, res.clone()));
    }
    if entry.fragment {
        fs.push((slot, res));
    }
}

impl Queue {
    /// The write fence (see [`Queue`]'s docs): before mutating shared
    /// memory, wait for the last presented frame's command buffer - the
    /// GPU may still be reading the old contents. Taken (not peeked) so
    /// only the first write after a present pays the check, and the wait
    /// happens while the mutex is held: a concurrent `write_*` on another
    /// clone must block until the frame has retired, not observe the
    /// emptied slot and write while the first waiter is still waiting.
    fn fence_writes(&self) {
        let mut slot = self
            .last_submitted
            .lock()
            .expect("metal last-submitted mutex poisoned");
        if let Some(SubmittedCmd(cmd)) = slot.take() {
            if cmd.status() != MTLCommandBufferStatus::Completed {
                // Committed vsync'd frames complete within one refresh;
                // on device loss the buffer completes with Error, so this
                // cannot hang indefinitely.
                cmd.waitUntilCompleted();
            }
        }
    }

    /// Buffer upload: a plain memcpy into the shared allocation, behind
    /// the write fence. Unlike d3d11 (whole-buffer WRITE_DISCARD) partial
    /// writes at an offset would be fine here, but every call site in the
    /// crate writes whole buffers from offset 0 anyway.
    pub fn write_buffer(&self, buffer: &Buffer, offset: u64, data: &[u8]) {
        assert!(
            offset + data.len() as u64 <= buffer.size,
            "write_buffer: {} bytes at offset {offset} into a {}-byte buffer",
            data.len(),
            buffer.size
        );
        if data.is_empty() {
            return;
        }
        self.fence_writes();
        // SAFETY: `contents` points at the buffer's shared allocation,
        // `size` bytes long; the bounds are asserted above, and the fence
        // has retired any command buffer still reading it.
        unsafe {
            let dst = buffer
                .raw
                .contents()
                .as_ptr()
                .cast::<u8>()
                .add(offset as usize);
            std::ptr::copy_nonoverlapping(data.as_ptr(), dst, data.len());
        }
    }

    /// Upload `data` (tightly packed rows) into the `size` region of
    /// `texture` at `origin`. Full-texture uploads pass `(0, 0)` and the
    /// texture's own size; the atlases upload sub-rectangles.
    /// `replaceRegion` on CPU-accessible textures is a CPU-side copy, so
    /// it goes behind the same write fence as buffer writes.
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
        self.fence_writes();
        texture.upload(origin, size, data);
    }

    pub(super) fn raw(&self) -> &ProtocolObject<dyn MTLCommandQueueProto> {
        &self.raw
    }

    /// `Frame::present` stores the frame's committed command buffer here;
    /// the next `write_*` fences on it.
    pub(super) fn store_submitted(&self, cmd: Retained<ProtocolObject<dyn MTLCommandBufferProto>>) {
        *self
            .last_submitted
            .lock()
            .expect("metal last-submitted mutex poisoned") = Some(SubmittedCmd(cmd));
    }
}

/// The one `TexFormat` → native translation for this backend. `const` so
/// `super::SURFACE_FORMAT` can be derived from the shared policy const in
/// `gxi/types.rs` at compile time. `Rgba8UnormSrgb` is created as sRGB
/// and written with raw bytes - no view reinterpretation (matches wgpu's
/// behavior for the glyph color atlas).
pub(super) const fn texture_format(format: TexFormat) -> MTLPixelFormat {
    match format {
        TexFormat::Bgra8Unorm => MTLPixelFormat::BGRA8Unorm,
        TexFormat::Rgba8Unorm => MTLPixelFormat::RGBA8Unorm,
        TexFormat::Rgba8UnormSrgb => MTLPixelFormat::RGBA8Unorm_sRGB,
        TexFormat::R8Unorm => MTLPixelFormat::R8Unorm,
    }
}

// ── Plain resource wrappers ─────────────────────────────────────────
//
// SAFETY (applies to the four `unsafe impl` pairs below): each type holds
// only Metal resource objects (`MTLBuffer` / `MTLTexture` /
// `MTLSamplerState`) plus plain data. Apple's Metal docs ("About
// Threading and Multiprocessing in Metal") make these objects safe to use
// from multiple threads; objc2-metal simply does not mark its protocol
// objects `Send`/`Sync`. Refcounting (retain/release on clone/drop) is
// atomic per Objective-C rules. The crate's own discipline adds the
// missing piece for the shared-memory contents: CPU writes go through the
// [`Queue`] write fence, and encoding happens on one thread at a time.

/// A uniform or per-instance vertex buffer (shared storage; written by
/// the CPU through [`Queue::write_buffer`]).
pub struct Buffer {
    pub(super) raw: Retained<ProtocolObject<dyn MTLBufferProto>>,
    /// Requested size (min 1); write bounds are checked against it.
    size: u64,
}

unsafe impl Send for Buffer {}
unsafe impl Sync for Buffer {}

/// A 2D texture (shader-read usage, Shared or Managed storage - see
/// `Device::create`). Metal needs no separate view object - the texture
/// binds directly.
pub struct Texture {
    pub(super) raw: Retained<ProtocolObject<dyn MTLTextureProto>>,
    bytes_per_pixel: u32,
}

unsafe impl Send for Texture {}
unsafe impl Sync for Texture {}

impl Texture {
    /// The raw `replaceRegion` copy shared by [`Queue::write_texture`]
    /// (fenced) and [`Device::try_create_texture_with_data`] (unfenced -
    /// nothing references a brand-new texture). Callers have validated
    /// `data`'s length against the region.
    fn upload(&self, origin: (u32, u32), size: (u32, u32), data: &[u8]) {
        let region = MTLRegion {
            origin: MTLOrigin {
                x: origin.0 as usize,
                y: origin.1 as usize,
                z: 0,
            },
            size: MTLSize {
                width: size.0 as usize,
                height: size.1 as usize,
                depth: 1,
            },
        };
        // Tightly packed rows: Metal has no row-pitch alignment
        // requirement for replaceRegion (contrast wgpu's 256-byte
        // COPY_BYTES_PER_ROW_ALIGNMENT, which only constrains buffer to
        // texture blits).
        let bytes_per_row = self.bytes_per_pixel as usize * size.0 as usize;
        // SAFETY: callers assert `data` covers the region; the pointer is
        // valid for the duration of the (synchronous) copy.
        unsafe {
            self.raw
                .replaceRegion_mipmapLevel_withBytes_bytesPerRow(
                    region,
                    0,
                    NonNull::new(data.as_ptr() as *mut core::ffi::c_void).expect("slice pointer is non-null"),
                    bytes_per_row,
                );
        }
    }
}

/// A sampler. `Clone` is cheap (refcount) - the desktop snapshot keeps a
/// clone of the shared sampler so the peek bind group can reuse it per
/// frame.
#[derive(Clone)]
pub struct Sampler {
    pub(super) raw: Retained<ProtocolObject<dyn MTLSamplerState>>,
}

unsafe impl Send for Sampler {}
unsafe impl Sync for Sampler {}

/// Pre-resolved bind slot lists, one per stage and resource class - the
/// Metal spelling of a bind group. Built once by
/// [`Device::create_bind_group`]; `Frame::set_bind_group` replays it.
#[derive(Default)]
pub struct BindGroup {
    pub(super) vs_buffers: Vec<(usize, Retained<ProtocolObject<dyn MTLBufferProto>>)>,
    pub(super) fs_buffers: Vec<(usize, Retained<ProtocolObject<dyn MTLBufferProto>>)>,
    pub(super) vs_textures: Vec<(usize, Retained<ProtocolObject<dyn MTLTextureProto>>)>,
    pub(super) fs_textures: Vec<(usize, Retained<ProtocolObject<dyn MTLTextureProto>>)>,
    pub(super) vs_samplers: Vec<(usize, Retained<ProtocolObject<dyn MTLSamplerState>>)>,
    pub(super) fs_samplers: Vec<(usize, Retained<ProtocolObject<dyn MTLSamplerState>>)>,
}

unsafe impl Send for BindGroup {}
unsafe impl Sync for BindGroup {}
