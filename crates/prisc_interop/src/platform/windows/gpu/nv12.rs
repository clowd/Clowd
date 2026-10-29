//! The Direct3D 11 half of the zero-copy encode path: the readback ring's slot textures and fences
//! opened on the Direct3D 11 twin device, a video processor configured to convert BGRA to BT.601
//! limited-range NV12, and the NV12 textures FFmpeg hands to NVENC or AMF.
//!
//! This is the native half `D3D11EncodeBridge.cs` ran in C# before this move, step for step:
//!
//! * each ring slot texture is opened through an NT handle (`ID3D12Device::CreateSharedHandle`
//!   then `ID3D11Device1::OpenSharedResource1`) and both ring fences through
//!   `ID3D11Device5::OpenSharedFence`, each NT handle closed the moment it has been opened;
//! * per frame, on the immediate context and with nothing waited for on the CPU:
//!   `Wait(renderFence, v)`, `VideoProcessorBlt`, `Signal(consumedFence, v)`, `Flush`. The consumed
//!   signal goes out **even when the blit was refused**, because the ring's slot waits for exactly
//!   that value; a refused `Wait` returns before both, so nothing is queued.
//!
//! The encoder textures live in a crate-global registry keyed by the raw `ID3D11Texture2D*` that
//! FFmpeg carries, and each entry owns an `Arc<ConverterInner>` (critique F9): FFmpeg's free path
//! (`prisc_gpu_nv12_texture_release_windows`) takes no converter, so a texture freed after the
//! converter handle was destroyed still finds its view and its device, and the last texture back
//! drops the converter's innards.
//!
//! Which failures are `PRISC_E_CAPABILITY` is a contract: exactly the conditions
//! `D3D11EncodeBridge.IsMissingCapability` matches on (see `capability_error`).

use std::collections::BTreeMap;
use std::ffi::c_void;
use std::mem::ManuallyDrop;
use std::ptr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError};

use windows::Win32::Foundation::{CloseHandle, GENERIC_ALL, RECT};
use windows::Win32::Graphics::Direct3D11::{
    ID3D11Device1, ID3D11Device5, ID3D11DeviceContext4, ID3D11Fence, ID3D11Texture2D, ID3D11VideoContext, ID3D11VideoDevice,
    ID3D11VideoProcessor, ID3D11VideoProcessorEnumerator, ID3D11VideoProcessorInputView, ID3D11VideoProcessorOutputView,
    D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_CPU_ACCESS_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_MAP_READ,
    D3D11_TEX2D_VPIV, D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT, D3D11_USAGE_STAGING, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
    D3D11_VIDEO_PROCESSOR_COLOR_SPACE, D3D11_VIDEO_PROCESSOR_CONTENT_DESC, D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT,
    D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT, D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC, D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC_0,
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC, D3D11_VIDEO_PROCESSOR_STREAM, D3D11_VIDEO_USAGE_OPTIMAL_QUALITY,
    D3D11_VPIV_DIMENSION_TEXTURE2D, D3D11_VPOV_DIMENSION_TEXTURE2D,
};
use windows::Win32::Graphics::Direct3D12::{ID3D12Device, ID3D12Fence, ID3D12Resource};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_NV12, DXGI_RATIONAL, DXGI_SAMPLE_DESC};
use windows_core::{Interface, PCWSTR};

use crate::error::{Error, Result};
use crate::ffi::status::{PRISC_OK, PRISC_S_NONE};
use crate::platform::windows::gpu::device::{D3d11, DeviceInner};
use crate::platform::windows::gpu::ring::RingInner;
use crate::platform::windows::gpu::{capability_error, missing_object, FreeThreaded};
use crate::platform::windows::os_error;

/// `D3D11_VIDEO_PROCESSOR_COLOR_SPACE` is a bitfield the windows crate models as one `u32`. Field
/// order, LSB first: `Usage:1`, `RGB_Range:1`, `YCbCr_Matrix:1` (0 is BT.601), `YCbCr_xvYCC:1`,
/// `Nominal_Range:2`, `Reserved:26`. The input stream: `Usage = 1` (video processing), full-range
/// RGB, BT.601. These are the values the C# `D3D11Backend.ColorSpace*` constants spelled; the
/// conversion self-check and `converter_blit_matches_bt601_limited` confirm them on hardware.
const COLOR_SPACE_STREAM: u32 = 0x1;

/// The output surface: `Usage = 1` and `Nominal_Range = 1` (16 to 235) in bits 4 and 5, the
/// limited range the readback path's swscale also produces.
const COLOR_SPACE_OUTPUT: u32 = 0x1 | (1 << 4);

/// What the converter's destroy allows its own drain: two seconds. The ordered C# path drains with
/// the full fence timeout first, so this only bounds a `SafeHandle` finalizer on a hung GPU.
const DESTROY_DRAIN_TIMEOUT_MS: u32 = 2_000;

/// The conversion self-check's colour. BT.601 limited gives Y 100.5, U 94.1, V 191.6; BT.709 would
/// give Y 91 and full range V 200, both well outside the tolerance.
const SELF_CHECK_RGB: [u8; 3] = [200, 60, 30];

/// How far a self-check sample may sit from the BT.601-limited value: rounding and the video
/// processor's arithmetic account for a level or two.
const SELF_CHECK_TOLERANCE: f64 = 4.0;

/// The BT.601 limited-range NV12 samples `(Y, U, V)` of an opaque sRGB colour: what swscale's
/// default produces on the readback path, and what the video processor is configured to produce.
pub(crate) fn bt601_limited(rgb: [u8; 3]) -> (f64, f64, f64) {
    let (r, g, b) = (f64::from(rgb[0]), f64::from(rgb[1]), f64::from(rgb[2]));
    let ey = 0.299 * r + 0.587 * g + 0.114 * b;
    let eu = (b - ey) / 1.772; // 0.5 / (1 - Kb)
    let ev = (r - ey) / 1.402; // 0.5 / (1 - Kr)
    (16.0 + 219.0 * ey / 255.0, 128.0 + 224.0 * eu / 255.0, 128.0 + 224.0 * ev / 255.0)
}

/// One ring slot as the Direct3D 11 device sees it.
struct ConverterSlot {
    /// The opened twin of the ring's slot texture, held so the share stays open.
    _twin: FreeThreaded<ID3D11Texture2D>,
    input_view: FreeThreaded<ID3D11VideoProcessorInputView>,
}

/// One NV12 encoder texture, the output view the blit writes through, and the converter it
/// belongs to (which it keeps alive).
struct TextureEntry {
    texture: FreeThreaded<ID3D11Texture2D>,
    view: FreeThreaded<ID3D11VideoProcessorOutputView>,
    converter: Arc<ConverterInner>,
}

/// Every NV12 texture handed out and not yet released, keyed by the raw pointer FFmpeg carries.
static TEXTURES: Mutex<BTreeMap<usize, TextureEntry>> = Mutex::new(BTreeMap::new());

/// Everything the NV12 converter owns, behind the `Arc` its handle and its textures share.
pub(crate) struct ConverterInner {
    /// The `converter -> ring` edge; `drain` waits on the ring's consumed fence.
    ring: Arc<RingInner>,
    d3d11: Arc<D3d11>,
    context4: FreeThreaded<ID3D11DeviceContext4>,
    video_device: FreeThreaded<ID3D11VideoDevice>,
    video_context: FreeThreaded<ID3D11VideoContext>,
    enumerator: FreeThreaded<ID3D11VideoProcessorEnumerator>,
    processor: FreeThreaded<ID3D11VideoProcessor>,
    render_fence: FreeThreaded<ID3D11Fence>,
    consumed_fence: FreeThreaded<ID3D11Fence>,
    slots: Vec<ConverterSlot>,
    width: u32,
    height: u32,
    /// The highest consumed-fence value this converter's own `Signal` was accepted for.
    last_signaled: AtomicU64,
    /// Test-only fault injection for the self-check's recovery paths.
    #[cfg(test)]
    pub(crate) fault: std::sync::atomic::AtomicU32,
}

/// Self-check faults the tests inject (`ConverterInner::fault`).
#[cfg(test)]
pub(crate) mod fault {
    /// Fail right after the shared submit, before anything was converted.
    pub(crate) const AFTER_SUBMIT: u32 = 1;
    /// Refuse the conversion before its GPU wait, so nothing is queued.
    pub(crate) const CONVERT_REFUSED: u32 = 2;
}

impl ConverterInner {
    fn is(&self, other: &Arc<ConverterInner>) -> bool {
        ptr::eq(Arc::as_ptr(other), self)
    }

    /// The per-frame GPU work, queued and not waited for. The flag is `out_signal_queued`: true
    /// only when the consumed-fence `Signal` was accepted, which the caller's release-or-abandon
    /// decision turns on and the status cannot say.
    pub(crate) fn convert(&self, index: u32, fence_value: u64, dst_texture: *mut c_void) -> (Result<()>, bool) {
        let Some(slot) = self.slots.get(index as usize) else {
            return (
                Err(Error::invalid_arg(format!(
                    "readback slot {index} is out of range for a ring with {} slots",
                    self.slots.len()
                ))),
                false,
            );
        };
        let view = {
            let textures = TEXTURES
                .lock()
                .unwrap_or_else(PoisonError::into_inner);
            match textures.get(&(dst_texture as usize)) {
                Some(entry) if self.is(&entry.converter) => entry.view.clone_interface(),
                _ => {
                    return (
                        Err(Error::invalid_arg("the destination texture was not acquired from this converter")),
                        false,
                    )
                }
            }
        };
        #[cfg(test)]
        if self.fault.load(Ordering::Acquire) == fault::CONVERT_REFUSED {
            return (Err(Error::os(0, "injected: the conversion was refused")), false);
        }

        unsafe {
            // A GPU-side wait: the convert thread never blocks here.
            if let Err(e) = self
                .context4
                .Wait(self.render_fence.get(), fence_value)
            {
                return (Err(os_error(&e, "ID3D11DeviceContext4::Wait")), false);
            }
            let mut streams = [D3D11_VIDEO_PROCESSOR_STREAM {
                Enable: true.into(),
                OutputIndex: 0,
                InputFrameOrField: 0,
                PastFrames: 0,
                FutureFrames: 0,
                ppPastSurfaces: ptr::null_mut(),
                pInputSurface: ManuallyDrop::new(Some(slot.input_view.clone_interface())),
                ppFutureSurfaces: ptr::null_mut(),
                ppPastSurfacesRight: ptr::null_mut(),
                pInputSurfaceRight: ManuallyDrop::new(None),
                ppFutureSurfacesRight: ptr::null_mut(),
            }];
            let blit = self
                .video_context
                .VideoProcessorBlt(self.processor.get(), &view, 0, &streams);
            ManuallyDrop::drop(&mut streams[0].pInputSurface);

            // The consumed signal goes out even when the blit was refused: the ring's slot waits
            // for exactly this value, and a refused blit must fail the render with a reason rather
            // than park it on the fence timeout.
            let signal = self
                .context4
                .Signal(self.consumed_fence.get(), fence_value);
            self.d3d11.context.Flush();

            let queued = signal.is_ok();
            if queued {
                self.last_signaled
                    .fetch_max(fence_value, Ordering::AcqRel);
            }
            if let Err(e) = blit {
                return (Err(os_error(&e, "ID3D11VideoContext::VideoProcessorBlt")), queued);
            }
            if let Err(e) = signal {
                return (Err(os_error(&e, "ID3D11DeviceContext4::Signal")), queued);
            }
            (Ok(()), queued)
        }
    }

    /// Waits out every conversion this converter queued: `PRISC_OK` or `PRISC_S_TIMEOUT`, and
    /// `PRISC_OK` at once before the first accepted signal.
    pub(crate) fn drain(&self, timeout_ms: u32) -> Result<i32> {
        let last = self.last_signaled.load(Ordering::Acquire);
        if last == 0 {
            return Ok(PRISC_OK);
        }
        self.ring.wait_consumed(last, timeout_ms)
    }

    /// Copies one of this converter's textures to a staging texture and writes the tightly packed
    /// Y and UV planes into the caller's buffers. Slow by design: the self-check and the tests.
    /// The map blocks until every queued conversion has completed.
    pub(crate) fn read(&self, texture: *mut c_void, y: &mut [u8], uv: &mut [u8]) -> Result<()> {
        let width = self.width as usize;
        let height = self.height as usize;
        if y.len() < width * height || uv.len() < width * height / 2 {
            return Err(Error::invalid_arg(format!(
                "an NV12 read of {width}x{height} needs {} luma and {} chroma bytes, not {} and {}",
                width * height,
                width * height / 2,
                y.len(),
                uv.len()
            )));
        }
        let source = {
            let textures = TEXTURES
                .lock()
                .unwrap_or_else(PoisonError::into_inner);
            match textures.get(&(texture as usize)) {
                Some(entry) if self.is(&entry.converter) => entry.texture.clone_interface(),
                _ => return Err(Error::invalid_arg("the texture to read was not acquired from this converter")),
            }
        };

        let desc = D3D11_TEXTURE2D_DESC {
            Width: self.width,
            Height: self.height,
            MipLevels: 1,
            ArraySize: 1,
            Format: DXGI_FORMAT_NV12,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_STAGING,
            BindFlags: 0,
            CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
            MiscFlags: 0,
        };
        let mut staging: Option<ID3D11Texture2D> = None;
        unsafe {
            self.d3d11
                .device
                .CreateTexture2D(&desc, None, Some(&mut staging))
        }
        .map_err(|e| os_error(&e, "ID3D11Device::CreateTexture2D(staging NV12)"))?;
        let staging = staging.ok_or_else(|| missing_object("CreateTexture2D(staging NV12)"))?;

        unsafe {
            let context = self.d3d11.context.get();
            context.CopyResource(&staging, &source);
            let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
            context
                .Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))
                .map_err(|e| os_error(&e, "ID3D11DeviceContext::Map(staging)"))?;
            let base = mapped.pData as *const u8;
            let pitch = mapped.RowPitch as usize;
            for row in 0..height {
                ptr::copy_nonoverlapping(base.add(row * pitch), y.as_mut_ptr().add(row * width), width);
            }
            // The chroma plane follows the luma plane at RowPitch * Height, RowPitch strided.
            let chroma = base.add(pitch * height);
            for row in 0..height / 2 {
                ptr::copy_nonoverlapping(chroma.add(row * pitch), uv.as_mut_ptr().add(row * width), width);
            }
            context.Unmap(&staging, 0);
        }
        Ok(())
    }

    /// One NV12 encoder texture plus its output view, registered under the raw texture pointer
    /// that becomes the frame's identity. The entry keeps this converter alive.
    pub(crate) fn acquire(self: &Arc<Self>) -> Result<*mut c_void> {
        let desc = D3D11_TEXTURE2D_DESC {
            Width: self.width,
            Height: self.height,
            MipLevels: 1,
            ArraySize: 1,
            Format: DXGI_FORMAT_NV12,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            // The output view needs the render-target bind; these two flags are also what
            // HardwareFrames records on AVD3D11VAFramesContext.BindFlags.
            BindFlags: (D3D11_BIND_RENDER_TARGET.0 | D3D11_BIND_SHADER_RESOURCE.0) as u32,
            CPUAccessFlags: 0,
            MiscFlags: 0,
        };
        let mut texture: Option<ID3D11Texture2D> = None;
        unsafe {
            self.d3d11
                .device
                .CreateTexture2D(&desc, None, Some(&mut texture))
        }
        .map_err(|e| os_error(&e, &format!("ID3D11Device::CreateTexture2D({}x{} NV12)", self.width, self.height)))?;
        let texture = texture.ok_or_else(|| missing_object("CreateTexture2D(NV12)"))?;

        // Default zeroes the whole twelve-byte union (Texture2DArray is its widest variant).
        let view_desc = D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC {
            ViewDimension: D3D11_VPOV_DIMENSION_TEXTURE2D,
            ..Default::default()
        };
        let mut view: Option<ID3D11VideoProcessorOutputView> = None;
        unsafe {
            self.video_device
                .CreateVideoProcessorOutputView(&texture, self.enumerator.get(), &view_desc, Some(&mut view))
        }
        .map_err(|e| os_error(&e, "ID3D11VideoDevice::CreateVideoProcessorOutputView"))?;
        let view = view.ok_or_else(|| missing_object("CreateVideoProcessorOutputView"))?;

        let raw = texture.as_raw();
        TEXTURES
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .insert(
                raw as usize,
                TextureEntry {
                    texture: FreeThreaded::new(texture),
                    view: FreeThreaded::new(view),
                    converter: self.clone(),
                },
            );
        Ok(raw)
    }

    /// The conversion self-check: slot 0 cleared to a known colour, submitted for the shared
    /// protocol, converted into a fresh texture, read back and compared at three points with the
    /// BT.601-limited expectation. On any failure slot 0 is left idle (released when the consumed
    /// signal was queued, abandoned otherwise), so the caller can carry on with readback over the
    /// same ring. A mismatch is `PRISC_E_CAPABILITY`.
    pub(crate) fn self_check(self: &Arc<Self>, timeout_ms: u32) -> Result<()> {
        let ring = &self.ring;
        ring.begin(0)?;
        if let Err(e) = ring.clear_slot0(SELF_CHECK_RGB) {
            let _ = ring.cancel_begin(0);
            return Err(e);
        }
        let value = match ring.submit_shared(0) {
            Ok(value) => value,
            Err(e) => {
                // The slot is in flight and nothing will signal it.
                let _ = ring.abandon_shared(0);
                return Err(e);
            }
        };
        #[cfg(test)]
        if self.fault.load(Ordering::Acquire) == fault::AFTER_SUBMIT {
            let _ = ring.abandon_shared(0);
            return Err(Error::os(0, "injected: the self-check failed after its submit"));
        }

        let texture = match self.acquire() {
            Ok(texture) => texture,
            Err(e) => {
                let _ = ring.abandon_shared(0);
                return Err(e);
            }
        };
        let result = self.convert_and_compare(value, texture, timeout_ms);
        release_texture(texture);
        result
    }

    /// The part of the self-check after the texture exists; leaves slot 0 idle on every path.
    fn convert_and_compare(&self, value: u64, texture: *mut c_void, timeout_ms: u32) -> Result<()> {
        let ring = &self.ring;
        let (converted, queued) = self.convert(0, value, texture);
        if let Err(e) = converted {
            // A queued signal is waited for like any frame's (the blit, if it ran, may still be
            // reading the slot); otherwise nothing will ever signal it and the slot is given up.
            if !queued || !matches!(ring.release_shared(0, timeout_ms), Ok(PRISC_OK)) {
                let _ = ring.abandon_shared(0);
            }
            return Err(e);
        }
        match ring.release_shared(0, timeout_ms) {
            Ok(PRISC_OK) => {}
            Ok(_) => {
                let _ = ring.abandon_shared(0);
                return Err(Error::os(0, "the conversion self-check's consumed signal did not land in time"));
            }
            Err(e) => {
                let _ = ring.abandon_shared(0);
                return Err(e);
            }
        }

        let (w, h) = (self.width as usize, self.height as usize);
        let mut y = vec![0u8; w * h];
        let mut uv = vec![0u8; w * h / 2];
        self.read(texture, &mut y, &mut uv)?;
        let (ey, eu, ev) = bt601_limited(SELF_CHECK_RGB);
        for (px, py) in [(0, 0), (w - 1, h - 1), (w / 2, h / 2)] {
            let ys = y[py * w + px];
            let us = uv[(py / 2) * w + (px / 2) * 2];
            let vs = uv[(py / 2) * w + (px / 2) * 2 + 1];
            if (f64::from(ys) - ey).abs() > SELF_CHECK_TOLERANCE
                || (f64::from(us) - eu).abs() > SELF_CHECK_TOLERANCE
                || (f64::from(vs) - ev).abs() > SELF_CHECK_TOLERANCE
            {
                let [r, g, b] = SELF_CHECK_RGB;
                return Err(Error::capability(format!(
                    "the video processor produced YUV ({ys},{us},{vs}) at ({px},{py}) for RGB ({r},{g},{b}), \
                     expected BT.601 limited ({ey:.0},{eu:.0},{ev:.0})"
                )));
            }
        }
        Ok(())
    }
}

/// Unregisters one texture and releases it with its output view; the converter it held goes when
/// its last holder does. An unknown pointer is `PRISC_S_NONE`, never an error: this runs inside
/// FFmpeg's free path and, in bulk, inside a teardown loop that must not stop half way.
pub(crate) fn release_texture(texture: *mut c_void) -> i32 {
    let removed = TEXTURES
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
        .remove(&(texture as usize));
    // Dropped outside the lock: the last entry of a converter releases its whole Direct3D 11 side.
    match removed {
        Some(entry) => {
            drop(entry);
            PRISC_OK
        }
        None => PRISC_S_NONE,
    }
}

/// Builds the converter over a ring: opens the shares, builds the video processor, configures the
/// colour spaces and rects, and creates one input view per slot. All or nothing.
pub(crate) fn create(device: &Arc<DeviceInner>, ring: &Arc<RingInner>) -> Result<Arc<ConverterInner>> {
    if !Arc::ptr_eq(device, ring.device()) {
        return Err(Error::invalid_arg(
            "the readback ring was built on a different GPU device than the one given",
        ));
    }
    let info = ring.info();
    if info.shareable == 0 {
        return Err(Error::capability(
            "the readback ring's textures and fences are not shareable on this driver",
        ));
    }

    // A refused D3D11CreateDevice and a LUID mismatch come back as PRISC_E_OS from here.
    let d3d11 = device.ensure_d3d11()?;

    // ID3D11Device1 is not a capability tier: a runtime without it predates Windows 8.
    let device1 = d3d11
        .device
        .cast::<ID3D11Device1>()
        .map_err(|e| os_error(&e, "QueryInterface(ID3D11Device1)"))?;
    let device5 = d3d11
        .device
        .cast::<ID3D11Device5>()
        .map_err(|e| capability_error(e.code(), "QueryInterface(ID3D11Device5, which needs the Direct3D 11.4 runtime)"))?;
    let context4 = d3d11
        .context
        .cast::<ID3D11DeviceContext4>()
        .map_err(|e| capability_error(e.code(), "QueryInterface(ID3D11DeviceContext4)"))?;
    // The tier the GitHub Windows runner's virtual GPU refuses with E_NOINTERFACE.
    let video_device = d3d11
        .device
        .cast::<ID3D11VideoDevice>()
        .map_err(|e| capability_error(e.code(), "QueryInterface(ID3D11VideoDevice)"))?;
    let video_context = d3d11
        .context
        .cast::<ID3D11VideoContext>()
        .map_err(|e| capability_error(e.code(), "QueryInterface(ID3D11VideoContext)"))?;

    let d3d12 = device.d3d12();
    let mut twins = Vec::with_capacity(info.slots as usize);
    for index in 0..info.slots {
        twins.push(open_texture(d3d12, &device1, ring.slot_texture(index)?, index)?);
    }
    let render_fence = open_fence(d3d12, &device5, ring.render_fence(), "render fence")?;
    let consumed_fence = open_fence(d3d12, &device5, ring.consumed_fence(), "consumed fence")?;

    let content = D3D11_VIDEO_PROCESSOR_CONTENT_DESC {
        InputFrameFormat: D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
        // No rate conversion is asked of the processor; the rates only describe the content.
        InputFrameRate: DXGI_RATIONAL {
            Numerator: 60,
            Denominator: 1,
        },
        InputWidth: info.width,
        InputHeight: info.height,
        OutputFrameRate: DXGI_RATIONAL {
            Numerator: 60,
            Denominator: 1,
        },
        OutputWidth: info.width,
        OutputHeight: info.height,
        Usage: D3D11_VIDEO_USAGE_OPTIMAL_QUALITY,
    };
    let enumerator = unsafe { video_device.CreateVideoProcessorEnumerator(&content) }
        .map_err(|e| capability_error(e.code(), "ID3D11VideoDevice::CreateVideoProcessorEnumerator"))?;

    // An absent support bit is PRISC_E_OS, not a capability: a capable GPU whose processor refuses
    // these formats must keep failing the suite.
    check_format(
        &enumerator,
        DXGI_FORMAT_B8G8R8A8_UNORM,
        D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT.0 as u32,
        "the video processor does not take B8G8R8A8_UNORM input",
    )?;
    check_format(
        &enumerator,
        DXGI_FORMAT_NV12,
        D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT.0 as u32,
        "the video processor does not produce NV12 output",
    )?;

    let processor = unsafe { video_device.CreateVideoProcessor(&enumerator, 0) }
        .map_err(|e| capability_error(e.code(), "ID3D11VideoDevice::CreateVideoProcessor"))?;
    configure(&video_context, &processor, info.width, info.height);

    let mut slots = Vec::with_capacity(twins.len());
    for (index, twin) in twins.into_iter().enumerate() {
        let view_desc = D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC {
            // Zero takes the format from the texture.
            FourCC: 0,
            ViewDimension: D3D11_VPIV_DIMENSION_TEXTURE2D,
            Anonymous: D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC_0 {
                Texture2D: D3D11_TEX2D_VPIV {
                    MipSlice: 0,
                    ArraySlice: 0,
                },
            },
        };
        let mut view: Option<ID3D11VideoProcessorInputView> = None;
        unsafe { video_device.CreateVideoProcessorInputView(&twin, &enumerator, &view_desc, Some(&mut view)) }
            .map_err(|e| os_error(&e, &format!("ID3D11VideoDevice::CreateVideoProcessorInputView(slot {index})")))?;
        let view = view.ok_or_else(|| missing_object(&format!("CreateVideoProcessorInputView(slot {index})")))?;
        slots.push(ConverterSlot {
            _twin: FreeThreaded::new(twin),
            input_view: FreeThreaded::new(view),
        });
    }

    Ok(Arc::new(ConverterInner {
        ring: ring.clone(),
        d3d11,
        context4: FreeThreaded::new(context4),
        video_device: FreeThreaded::new(video_device),
        video_context: FreeThreaded::new(video_context),
        enumerator: FreeThreaded::new(enumerator),
        processor: FreeThreaded::new(processor),
        render_fence: FreeThreaded::new(render_fence),
        consumed_fence: FreeThreaded::new(consumed_fence),
        slots,
        width: info.width,
        height: info.height,
        last_signaled: AtomicU64::new(0),
        #[cfg(test)]
        fault: std::sync::atomic::AtomicU32::new(0),
    }))
}

/// The converter destroy's side effect: a drain bounded at two seconds, swallowed (a timeout or a
/// removed device leaves nothing to wait for, and the ordered path already reported it).
pub(crate) fn teardown(converter: &ConverterInner) {
    let _ = converter.drain(DESTROY_DRAIN_TIMEOUT_MS);
}

/// One slot texture through an NT handle, closed the moment the resource is open.
fn open_texture(d3d12: &ID3D12Device, device1: &ID3D11Device1, texture: &ID3D12Resource, index: u32) -> Result<ID3D11Texture2D> {
    let handle = unsafe { d3d12.CreateSharedHandle(texture, None, GENERIC_ALL.0, PCWSTR::null()) }
        .map_err(|e| os_error(&e, &format!("ID3D12Device::CreateSharedHandle(slot {index})")))?;
    let opened = unsafe { device1.OpenSharedResource1::<ID3D11Texture2D>(handle) };
    let _ = unsafe { CloseHandle(handle) };
    opened.map_err(|e| os_error(&e, &format!("ID3D11Device1::OpenSharedResource1(slot {index})")))
}

/// The same for a fence, through `ID3D11Device5`'s own opener.
fn open_fence(d3d12: &ID3D12Device, device5: &ID3D11Device5, fence: &ID3D12Fence, what: &str) -> Result<ID3D11Fence> {
    let handle = unsafe { d3d12.CreateSharedHandle(fence, None, GENERIC_ALL.0, PCWSTR::null()) }
        .map_err(|e| os_error(&e, &format!("ID3D12Device::CreateSharedHandle({what})")))?;
    let mut opened: Option<ID3D11Fence> = None;
    let result = unsafe { device5.OpenSharedFence(handle, &mut opened) };
    let _ = unsafe { CloseHandle(handle) };
    result.map_err(|e| os_error(&e, &format!("ID3D11Device5::OpenSharedFence({what})")))?;
    opened.ok_or_else(|| missing_object(&format!("OpenSharedFence({what})")))
}

/// One `CheckVideoProcessorFormat` gate: the step alone when the call succeeded without the bit,
/// the step plus the HRESULT when it failed.
fn check_format(enumerator: &ID3D11VideoProcessorEnumerator, format: DXGI_FORMAT, bit: u32, step: &str) -> Result<()> {
    match unsafe { enumerator.CheckVideoProcessorFormat(format) } {
        Ok(support) if support & bit != 0 => Ok(()),
        Ok(_) => Err(Error::os(0, step)),
        Err(e) => Err(os_error(&e, step)),
    }
}

/// The seven settings, in the order the C# bridge applied them: full-range RGB in, BT.601 limited
/// YCbCr out, one progressive stream covering the whole frame, no driver enhancements. None of the
/// setters reports a status; a value the driver dislikes surfaces at the blit, which is what the
/// self-check is for.
fn configure(video_context: &ID3D11VideoContext, processor: &ID3D11VideoProcessor, width: u32, height: u32) {
    let full = RECT {
        left: 0,
        top: 0,
        right: width as i32,
        bottom: height as i32,
    };
    let stream_space = D3D11_VIDEO_PROCESSOR_COLOR_SPACE {
        _bitfield: COLOR_SPACE_STREAM,
    };
    let output_space = D3D11_VIDEO_PROCESSOR_COLOR_SPACE {
        _bitfield: COLOR_SPACE_OUTPUT,
    };
    unsafe {
        video_context.VideoProcessorSetStreamFrameFormat(processor, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        video_context.VideoProcessorSetStreamColorSpace(processor, 0, &stream_space);
        video_context.VideoProcessorSetOutputColorSpace(processor, &output_space);
        video_context.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
        video_context.VideoProcessorSetStreamSourceRect(processor, 0, true, Some(&full));
        video_context.VideoProcessorSetStreamDestRect(processor, 0, true, Some(&full));
        video_context.VideoProcessorSetOutputTargetRect(processor, true, Some(&full));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus;
    use crate::platform::windows::gpu::device::tests::device_or_skip;
    use crate::platform::windows::gpu::ring;

    /// A device, a ring and a converter, or `None` with a printed reason. A machine with no device,
    /// a driver that will not share, and the capability conditions are skips; anything else fails.
    fn converter_or_skip(test: &str) -> Option<(Arc<DeviceInner>, Arc<RingInner>, Arc<ConverterInner>)> {
        let device = device_or_skip(test)?;
        let ring = ring::create(&device, 64, 48, 2).unwrap_or_else(|e| panic!("{test}: a device must build a ring ({})", e.message));
        if ring.info().shareable == 0 {
            eprintln!("skipping {test}: this driver does not share the ring's textures and fences");
            return None;
        }
        match create(&device, &ring) {
            Ok(converter) => Some((device, ring, converter)),
            Err(e) if e.status == PriscInteropStatus::Capability => {
                eprintln!("skipping {test}: this GPU cannot run the NV12 converter ({})", e.message);
                None
            }
            Err(e) => panic!("{test}: the converter must come up over a shareable ring ({})", e.message),
        }
    }

    #[test]
    fn colour_space_words_are_the_documented_bits() {
        assert_eq!(COLOR_SPACE_STREAM, 0b00_0001);
        assert_eq!(COLOR_SPACE_OUTPUT, 0b01_0001);
    }

    #[test]
    fn bt601_limited_matches_the_documented_samples() {
        let (y, u, v) = bt601_limited([200, 60, 30]);
        assert!(
            (y - 100.5).abs() < 0.1 && (u - 94.1).abs() < 0.1 && (v - 191.6).abs() < 0.1,
            "{y} {u} {v}"
        );
        let (y, u, v) = bt601_limited([255, 255, 255]);
        assert!((y - 235.0).abs() < 1e-9 && (u - 128.0).abs() < 1e-9 && (v - 128.0).abs() < 1e-9);
        let (y, _, _) = bt601_limited([0, 0, 0]);
        assert!((y - 16.0).abs() < 1e-9);
    }

    #[test]
    fn converter_create_over_a_non_shareable_ring_returns_capability() {
        let Some(device) = device_or_skip("converter_create_over_a_non_shareable_ring_returns_capability") else {
            return;
        };
        let ring = ring::create_inner(&device, 32, 32, 1, false).expect("a non-shared ring builds on any device");
        let Err(e) = create(&device, &ring) else {
            panic!("a non-shareable ring must be refused");
        };
        assert_eq!(e.status, PriscInteropStatus::Capability);
        assert!(e.message.contains("not shareable"), "{}", e.message);
    }

    #[test]
    fn converter_create_refuses_a_ring_from_another_device() {
        let Some(first) = device_or_skip("converter_create_refuses_a_ring_from_another_device") else {
            return;
        };
        let second = device_or_skip("converter_create_refuses_a_ring_from_another_device").expect("a second device handle");
        let ring = ring::create(&first, 32, 32, 1).expect("a ring on the first device");
        assert_eq!(
            create(&second, &ring)
                .err()
                .map(|e| e.status),
            Some(PriscInteropStatus::InvalidArg)
        );
    }

    #[test]
    fn converter_self_check_passes_and_leaves_slot_zero_idle() {
        let Some((_device, ring, converter)) = converter_or_skip("converter_self_check_passes_and_leaves_slot_zero_idle") else {
            return;
        };
        converter
            .self_check(30_000)
            .unwrap_or_else(|e| panic!("self-check: {}", e.message));
        ring.begin(0)
            .expect("slot 0 is idle after the self-check");
        ring.cancel_begin(0).expect("cancel");
        assert!(
            TEXTURES
                .lock()
                .unwrap()
                .values()
                .all(|e| !converter.is(&e.converter)),
            "the probe texture was released"
        );
    }

    /// Every failure path of the self-check hands slot 0 back idle, so readback over the same ring
    /// carries on (what `D3D11EncodeBridge.TryCreate`'s caller does next).
    #[test]
    fn a_failed_self_check_leaves_slot_zero_idle() {
        let Some((_device, ring, converter)) = converter_or_skip("a_failed_self_check_leaves_slot_zero_idle") else {
            return;
        };
        for injected in [fault::AFTER_SUBMIT, fault::CONVERT_REFUSED] {
            converter
                .fault
                .store(injected, Ordering::Release);
            let Err(e) = converter.self_check(30_000) else {
                panic!("the injected fault fails the check");
            };
            assert!(e.message.starts_with("injected"), "{}", e.message);
            ring.begin(0)
                .expect("slot 0 is idle after a failed self-check");
            ring.submit(0)
                .expect("and the readback protocol runs on it");
            ring.wait(0, 30_000)
                .expect("wait")
                .expect("the copy completes");
        }
        converter.fault.store(0, Ordering::Release);
        converter
            .self_check(30_000)
            .unwrap_or_else(|e| panic!("a healthy self-check after failed ones: {}", e.message));
    }

    #[test]
    fn converter_blit_matches_bt601_limited() {
        let Some((_device, ring, converter)) = converter_or_skip("converter_blit_matches_bt601_limited") else {
            return;
        };
        let texture = converter.acquire().expect("an NV12 texture");
        ring.begin(0).expect("slot 0 is idle");
        ring.clear_slot0([30, 140, 220])
            .expect("clear");
        let value = ring
            .submit_shared(0)
            .expect("the shared submit is accepted");
        let (converted, queued) = converter.convert(0, value, texture);
        converted.unwrap_or_else(|e| panic!("convert: {}", e.message));
        assert!(queued);
        assert_eq!(
            ring.release_shared(0, 30_000)
                .expect("release"),
            PRISC_OK
        );

        let (w, h) = (64usize, 48usize);
        let mut y = vec![0u8; w * h];
        let mut uv = vec![0u8; w * h / 2];
        converter
            .read(texture, &mut y, &mut uv)
            .expect("read");
        let (ey, eu, ev) = bt601_limited([30, 140, 220]);
        for (x, row) in [(0usize, 0usize), (w - 2, h - 2), (w / 2, h / 2)] {
            let (sy, su, sv) = (
                f64::from(y[row * w + x]),
                f64::from(uv[(row / 2) * w + (x / 2) * 2]),
                f64::from(uv[(row / 2) * w + (x / 2) * 2 + 1]),
            );
            assert!(
                (sy - ey).abs() <= 4.0 && (su - eu).abs() <= 4.0 && (sv - ev).abs() <= 4.0,
                "at ({x},{row}) got ({sy},{su},{sv}), expected ({ey:.0},{eu:.0},{ev:.0})"
            );
        }
        assert_eq!(release_texture(texture), PRISC_OK);
    }

    #[test]
    fn convert_rejects_a_foreign_texture_and_queues_nothing() {
        let Some((_device, ring, converter)) = converter_or_skip("convert_rejects_a_foreign_texture_and_queues_nothing") else {
            return;
        };
        ring.begin(0).expect("slot 0 is idle");
        let value = ring
            .submit_shared(0)
            .expect("the shared submit is accepted");
        let (converted, queued) = converter.convert(0, value, 0x1234 as *mut c_void);
        assert_eq!(converted.unwrap_err().status, PriscInteropStatus::InvalidArg);
        assert!(!queued);
        ring.abandon_shared(0).expect("abandon");
    }

    #[test]
    fn release_of_an_unknown_texture_is_benign_and_a_second_release_too() {
        assert_eq!(release_texture(0xDEAD as *mut c_void), PRISC_S_NONE);
        let Some((_device, _ring, converter)) = converter_or_skip("release_of_an_unknown_texture_is_benign_and_a_second_release_too")
        else {
            return;
        };
        let texture = converter.acquire().expect("an NV12 texture");
        assert_eq!(release_texture(texture), PRISC_OK);
        assert_eq!(release_texture(texture), PRISC_S_NONE);
    }

    /// Critique F9: a texture freed after the converter handle went still finds its converter, and
    /// the last one back drops the converter's innards.
    #[test]
    fn texture_release_after_the_converter_is_gone_is_safe() {
        let Some((_device, ring, converter)) = converter_or_skip("texture_release_after_the_converter_is_gone_is_safe") else {
            return;
        };
        let texture = converter.acquire().expect("an NV12 texture");
        let weak = Arc::downgrade(&converter);
        teardown(&converter);
        drop(converter);
        assert!(weak.upgrade().is_some(), "the outstanding texture keeps the converter alive");
        drop(ring);
        assert_eq!(release_texture(texture), PRISC_OK);
        assert!(weak.upgrade().is_none(), "the last texture back drops the converter");
    }

    #[test]
    fn drain_is_a_noop_before_any_convert() {
        let Some((_device, _ring, converter)) = converter_or_skip("drain_is_a_noop_before_any_convert") else {
            return;
        };
        assert_eq!(converter.drain(0).expect("drain"), PRISC_OK);
    }
}
