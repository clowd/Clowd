use std::borrow::Cow;
use std::sync::{Arc, Mutex};

/// A shader program for one pipeline. Precompiled passthrough shaders come in
/// two shapes: DX12 ships separate vertex/fragment DXBC modules, while Metal
/// ships a single metallib exposing both entry points. The runtime-WGSL
/// fallback (see [`load`]) always produces a single unified module regardless
/// of platform, so both shapes are represented here.
pub enum ShaderPair {
    /// Separate vertex and fragment modules (Windows DXBC passthrough).
    #[cfg(windows)]
    Split { vs: wgpu::ShaderModule, fs: wgpu::ShaderModule },
    /// One module exposing both `vs_main` and `fs_main` (macOS metallib
    /// passthrough, and the runtime-WGSL fallback on either platform).
    Unified(wgpu::ShaderModule),
}

impl ShaderPair {
    pub fn vs(&self) -> &wgpu::ShaderModule {
        match self {
            #[cfg(windows)]
            ShaderPair::Split {
                vs,
                ..
            } => vs,
            ShaderPair::Unified(module) => module,
        }
    }

    pub fn fs(&self) -> &wgpu::ShaderModule {
        match self {
            #[cfg(windows)]
            ShaderPair::Split {
                fs,
                ..
            } => fs,
            ShaderPair::Unified(module) => module,
        }
    }
}

// A failed passthrough shader surfaces as an *uncaptured* wgpu error (thread-local
// error scopes do not intercept it), which by default aborts the whole process via
// wgpu's fatal handler. We install our own non-fatal handler (see
// `install_error_handler`) that records the message here instead, so `load` can
// notice the failure and fall back to compiling the WGSL at runtime.
static LAST_UNCAPTURED_ERROR: Mutex<Option<String>> = Mutex::new(None);
// Serialises shader loads so a concurrent load can't interleave the
// clear/create/check of `LAST_UNCAPTURED_ERROR`. Kept separate from that mutex
// so the error handler (which runs synchronously *inside* the create call) never
// contends with a lock this holds.
static LOAD_GUARD: Mutex<()> = Mutex::new(());

/// Install the non-fatal uncaptured-error handler on `device`. Must be called
/// once, right after the device is created and before any shader is loaded.
/// Without it, wgpu aborts the process on the first shader/pipeline validation
/// error rather than letting [`load`] fall back.
pub fn install_error_handler(device: &wgpu::Device) {
    device.on_uncaptured_error(Arc::new(|err| {
        let msg = err.to_string();
        log::warn!("wgpu uncaptured error (non-fatal): {msg}");
        if let Ok(mut slot) = LAST_UNCAPTURED_ERROR.lock() {
            *slot = Some(msg);
        }
    }));
}

/// Drain any error recorded by the uncaptured-error handler since the last drain.
fn take_uncaptured_error() -> Option<String> {
    LAST_UNCAPTURED_ERROR
        .lock()
        .ok()
        .and_then(|mut slot| slot.take())
}

unsafe fn passthrough(device: &wgpu::Device, label: &str, bytes: &'static [u8]) -> wgpu::ShaderModule {
    unsafe {
        device.create_shader_module_passthrough(wgpu::ShaderModuleDescriptorPassthrough {
            label: Some(label),
            #[cfg(windows)]
            dxil: Some(Cow::Borrowed(bytes)),
            #[cfg(target_os = "macos")]
            metallib: Some(Cow::Borrowed(bytes)),
            ..Default::default()
        })
    }
}

/// Compile the WGSL source at runtime. This is the "usual" wgpu path (bindings
/// are assigned by wgpu-hal from the `@group`/`@binding` attributes), used as a
/// fallback when a precompiled passthrough shader cannot be loaded on this host
/// — e.g. a metallib whose deployment target is newer than the running macOS.
fn wgsl_fallback(device: &wgpu::Device, label: &str, wgsl_source: &'static str) -> ShaderPair {
    ShaderPair::Unified(device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some(label),
        source: wgpu::ShaderSource::Wgsl(Cow::Borrowed(wgsl_source)),
    }))
}

/// Load the precompiled passthrough shader; if creating it raises an uncaptured
/// error (recorded by `install_error_handler`), fall back to runtime WGSL.
#[cfg(target_os = "macos")]
fn load(device: &wgpu::Device, label: &str, metallib_bytes: &'static [u8], wgsl_source: &'static str) -> ShaderPair {
    let _guard = LOAD_GUARD.lock().unwrap();
    take_uncaptured_error(); // clear any stale error
    let module = unsafe { passthrough(device, label, metallib_bytes) };
    if take_uncaptured_error().is_some() {
        log::warn!("precompiled Metal shader '{label}' failed to load; falling back to runtime WGSL compilation");
        return wgsl_fallback(device, label, wgsl_source);
    }
    ShaderPair::Unified(module)
}

#[cfg(windows)]
fn load(device: &wgpu::Device, label: &str, vs_bytes: &'static [u8], fs_bytes: &'static [u8], wgsl_source: &'static str) -> ShaderPair {
    let _guard = LOAD_GUARD.lock().unwrap();
    take_uncaptured_error(); // clear any stale error
    let vs = unsafe { passthrough(device, &format!("{label} VS"), vs_bytes) };
    let fs = unsafe { passthrough(device, &format!("{label} FS"), fs_bytes) };
    if take_uncaptured_error().is_some() {
        log::warn!("precompiled DX shader '{label}' failed to load; falling back to runtime WGSL compilation");
        return wgsl_fallback(device, label, wgsl_source);
    }
    ShaderPair::Split {
        vs,
        fs,
    }
}

pub fn desktop(device: &wgpu::Device) -> ShaderPair {
    const WGSL: &str = include_str!("../../shaders/desktop.wgsl");
    #[cfg(windows)]
    {
        const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/desktop_vs.dxbc"));
        const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/desktop_ps.dxbc"));
        load(device, "desktop", VS, FS, WGSL)
    }
    #[cfg(target_os = "macos")]
    {
        const METALLIB: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/desktop.metallib"));
        load(device, "desktop", METALLIB, WGSL)
    }
}

pub fn peek(device: &wgpu::Device) -> ShaderPair {
    const WGSL: &str = include_str!("../../shaders/peek.wgsl");
    #[cfg(windows)]
    {
        const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/peek_vs.dxbc"));
        const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/peek_ps.dxbc"));
        load(device, "peek", VS, FS, WGSL)
    }
    #[cfg(target_os = "macos")]
    {
        const METALLIB: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/peek.metallib"));
        load(device, "peek", METALLIB, WGSL)
    }
}

pub fn ui_rect(device: &wgpu::Device) -> ShaderPair {
    const WGSL: &str = include_str!("../../shaders/ui_rect.wgsl");
    #[cfg(windows)]
    {
        const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_rect_vs.dxbc"));
        const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_rect_ps.dxbc"));
        load(device, "ui_rect", VS, FS, WGSL)
    }
    #[cfg(target_os = "macos")]
    {
        const METALLIB: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_rect.metallib"));
        load(device, "ui_rect", METALLIB, WGSL)
    }
}

pub fn ui_icon(device: &wgpu::Device) -> ShaderPair {
    const WGSL: &str = include_str!("../../shaders/ui_icon.wgsl");
    #[cfg(windows)]
    {
        const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_icon_vs.dxbc"));
        const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_icon_ps.dxbc"));
        load(device, "ui_icon", VS, FS, WGSL)
    }
    #[cfg(target_os = "macos")]
    {
        const METALLIB: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_icon.metallib"));
        load(device, "ui_icon", METALLIB, WGSL)
    }
}
