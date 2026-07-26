use std::borrow::Cow;
use std::sync::{Arc, Mutex};

/// A shader program for one pipeline. Windows ships precompiled DXBC as
/// separate vertex/fragment passthrough modules; everywhere else (and as the
/// Windows fallback) the WGSL is compiled at runtime into a single module
/// exposing both entry points.
pub enum ShaderPair {
    /// Separate vertex and fragment modules (Windows DXBC passthrough).
    #[cfg(windows)]
    Split { vs: wgpu::ShaderModule, fs: wgpu::ShaderModule },
    /// One module exposing both `vs_main` and `fs_main` (runtime-WGSL).
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
#[cfg(windows)]
static LOAD_GUARD: Mutex<()> = Mutex::new(());

/// Install the non-fatal uncaptured-error handler on `device`. Must be called
/// once, right after the device is created and before any shader is loaded.
/// Without it, wgpu's default handler panics on the first shader/pipeline
/// validation error — on Windows that would defeat [`load`]'s WGSL fallback,
/// and everywhere else it killed the render worker thread, leaving the overlay
/// invisible while the event loop spun forever.
pub fn install_error_handler(device: &wgpu::Device) {
    device.on_uncaptured_error(Arc::new(|err| {
        let msg = err.to_string();
        if cfg!(windows) {
            // expected when a precompiled DXBC shader can't load on this host;
            // `load` notices and falls back to runtime WGSL.
            log::warn!("wgpu uncaptured error (non-fatal): {msg}");
        } else {
            // no fallback path exists here, so this is a real bug — report it,
            // but keep the process alive and interactive.
            log::error!("wgpu uncaptured error (non-fatal): {msg}");
        }
        if let Ok(mut slot) = LAST_UNCAPTURED_ERROR.lock() {
            *slot = Some(msg);
        }
    }));
}

/// Drain any error recorded by the uncaptured-error handler since the last drain.
#[cfg(windows)]
fn take_uncaptured_error() -> Option<String> {
    LAST_UNCAPTURED_ERROR
        .lock()
        .ok()
        .and_then(|mut slot| slot.take())
}

#[cfg(windows)]
unsafe fn passthrough(device: &wgpu::Device, label: &str, bytes: &'static [u8]) -> wgpu::ShaderModule {
    unsafe {
        device.create_shader_module_passthrough(wgpu::ShaderModuleDescriptorPassthrough {
            label: Some(label),
            dxil: Some(Cow::Borrowed(bytes)),
            ..Default::default()
        })
    }
}

/// Compile the WGSL source at runtime. This is the "usual" wgpu path (bindings
/// are assigned by wgpu-hal from the `@group`/`@binding` attributes). It is the
/// only path on macOS, and the fallback on Windows when a precompiled DXBC
/// passthrough shader cannot be loaded on this host.
fn wgsl(device: &wgpu::Device, label: &str, wgsl_source: &'static str) -> ShaderPair {
    ShaderPair::Unified(device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some(label),
        source: wgpu::ShaderSource::Wgsl(Cow::Borrowed(wgsl_source)),
    }))
}

/// Load the precompiled passthrough shader; if creating it raises an uncaptured
/// error (recorded by `install_error_handler`), fall back to runtime WGSL.
#[cfg(windows)]
fn load(device: &wgpu::Device, label: &str, vs_bytes: &'static [u8], fs_bytes: &'static [u8], wgsl_source: &'static str) -> ShaderPair {
    let _guard = LOAD_GUARD.lock().unwrap();
    take_uncaptured_error(); // clear any stale error
    let vs = unsafe { passthrough(device, &format!("{label} VS"), vs_bytes) };
    let fs = unsafe { passthrough(device, &format!("{label} FS"), fs_bytes) };
    if take_uncaptured_error().is_some() {
        log::warn!("precompiled DX shader '{label}' failed to load; falling back to runtime WGSL compilation");
        return wgsl(device, label, wgsl_source);
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
    #[cfg(not(windows))]
    wgsl(device, "desktop", WGSL)
}

pub fn peek(device: &wgpu::Device) -> ShaderPair {
    const WGSL: &str = include_str!("../../shaders/peek.wgsl");
    #[cfg(windows)]
    {
        const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/peek_vs.dxbc"));
        const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/peek_ps.dxbc"));
        load(device, "peek", VS, FS, WGSL)
    }
    #[cfg(not(windows))]
    wgsl(device, "peek", WGSL)
}

pub fn ui_rect(device: &wgpu::Device) -> ShaderPair {
    const WGSL: &str = include_str!("../../shaders/ui_rect.wgsl");
    #[cfg(windows)]
    {
        const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_rect_vs.dxbc"));
        const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_rect_ps.dxbc"));
        load(device, "ui_rect", VS, FS, WGSL)
    }
    #[cfg(not(windows))]
    wgsl(device, "ui_rect", WGSL)
}

pub fn ui_icon(device: &wgpu::Device) -> ShaderPair {
    const WGSL: &str = include_str!("../../shaders/ui_icon.wgsl");
    #[cfg(windows)]
    {
        const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_icon_vs.dxbc"));
        const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_icon_ps.dxbc"));
        load(device, "ui_icon", VS, FS, WGSL)
    }
    #[cfg(not(windows))]
    wgsl(device, "ui_icon", WGSL)
}
