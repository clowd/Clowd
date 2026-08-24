use std::borrow::Cow;
use std::sync::Arc;
#[cfg(windows)]
use std::sync::Mutex;

/// A shader program for one pipeline. Windows ships precompiled DXBC
/// (`build.rs`: WGSL → naga → HLSL → FXC) as separate vertex/fragment
/// passthrough modules and NEVER compiles WGSL at runtime — a blob the
/// driver rejects is a loud panic, not a silent fallback, because a
/// fallback is a second, untested compile path that only ever runs on the
/// machines least able to handle it (see issue #74). Everywhere else the
/// WGSL is compiled at runtime into a single module exposing both entry
/// points.
pub enum ShaderPair {
    /// Separate vertex and fragment modules (Windows DXBC passthrough).
    #[cfg(windows)]
    Split { vs: wgpu::ShaderModule, fs: wgpu::ShaderModule },
    /// One module exposing both `vs_main` and `fs_main` (runtime-WGSL).
    #[cfg_attr(windows, allow(dead_code))]
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

// A failed passthrough create surfaces as an *uncaptured* wgpu error (no error
// scope is open), which our handler records here so `load` can turn it into a
// panic that names the shader. Without the recording, the failure would only
// show up later as cryptic invalid-object errors at pipeline creation.
#[cfg(windows)]
static LAST_UNCAPTURED_ERROR: Mutex<Option<String>> = Mutex::new(None);
// Serialises shader loads so a concurrent load can't interleave the
// clear/create/check of `LAST_UNCAPTURED_ERROR`. Kept separate from that mutex
// so the error handler (which runs synchronously *inside* the create call)
// never contends with a lock this holds.
#[cfg(windows)]
static LOAD_GUARD: Mutex<()> = Mutex::new(());

/// Install a non-fatal uncaptured-error handler on `device`. Must be called
/// once, right after the device is created and before any shader is loaded.
/// Without it, wgpu's default handler panics on the first uncaptured error
/// with a generic message; this one logs the real error text (and on Windows
/// records it so [`load`] can panic naming the shader that caused it).
pub fn install_error_handler(device: &wgpu::Device) {
    device.on_uncaptured_error(Arc::new(|err| {
        let msg = err.to_string();
        log::error!("wgpu uncaptured error (non-fatal): {msg}");
        #[cfg(windows)]
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

/// Create one passthrough module from a DXBC blob. `entry_points` must name
/// EXACTLY one entry point when `dxil` is set — wgpu 30 validates the count
/// (wgpu-core `create_shader_module_passthrough`), and leaving it defaulted
/// (empty) is precisely the bug that silently disabled every precompiled
/// shader from 4.1.1 to 4.1.34. The dx12 backend hands the blob to D3D12
/// verbatim (FXC DXBC and DXC DXIL share the container format), so the name
/// only has to satisfy the count check — it still matches the HLSL entry
/// FXC compiled, for honesty in tooling that reads it.
#[cfg(windows)]
unsafe fn passthrough(device: &wgpu::Device, label: &str, bytes: &'static [u8], entry_point: &'static str) -> wgpu::ShaderModule {
    unsafe {
        device.create_shader_module_passthrough(wgpu::ShaderModuleDescriptorPassthrough {
            label: Some(label),
            entry_points: Cow::Owned(vec![wgpu::PassthroughShaderEntryPoint {
                name: Cow::Borrowed(entry_point),
                workgroup_size: (0, 0, 0),
            }]),
            dxil: Some(Cow::Borrowed(bytes)),
            ..Default::default()
        })
    }
}

/// Compile the WGSL source at runtime. The only path on macOS; never used on
/// Windows.
#[cfg(not(windows))]
fn wgsl(device: &wgpu::Device, label: &str, wgsl_source: &'static str) -> ShaderPair {
    ShaderPair::Unified(device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some(label),
        source: wgpu::ShaderSource::Wgsl(Cow::Borrowed(wgsl_source)),
    }))
}

/// Load the precompiled passthrough shader pair. A creation error is a PANIC
/// that names the shader and carries the driver/validation message: the
/// render worker's fail path (`ReadyGuard` → `failed_count` → the show gate →
/// the shell's error dialog with the capture log) turns that into a loud,
/// attributable report instead of a broken overlay.
#[cfg(windows)]
fn load(device: &wgpu::Device, label: &str, vs_bytes: &'static [u8], fs_bytes: &'static [u8]) -> ShaderPair {
    let _guard = LOAD_GUARD.lock().unwrap();
    take_uncaptured_error(); // clear any stale error
    let vs = unsafe { passthrough(device, &format!("{label} VS"), vs_bytes, "vs_main") };
    let fs = unsafe { passthrough(device, &format!("{label} FS"), fs_bytes, "fs_main") };
    if let Some(err) = take_uncaptured_error() {
        panic!("precompiled shader '{label}' failed to load (no runtime fallback exists on Windows): {err}");
    }
    ShaderPair::Split {
        vs,
        fs,
    }
}

macro_rules! shader_fn {
    ($name:ident, $label:literal, $wgsl:literal, $vs:literal, $fs:literal) => {
        pub fn $name(device: &wgpu::Device) -> ShaderPair {
            #[cfg(windows)]
            {
                const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), $vs));
                const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), $fs));
                load(device, $label, VS, FS)
            }
            #[cfg(not(windows))]
            wgsl(device, $label, include_str!($wgsl))
        }
    };
}

shader_fn!(
    desktop,
    "desktop",
    "../../shaders/desktop.wgsl",
    "/desktop_vs.dxbc",
    "/desktop_ps.dxbc"
);
shader_fn!(peek, "peek", "../../shaders/peek.wgsl", "/peek_vs.dxbc", "/peek_ps.dxbc");
shader_fn!(
    ui_rect,
    "ui_rect",
    "../../shaders/ui_rect.wgsl",
    "/ui_rect_vs.dxbc",
    "/ui_rect_ps.dxbc"
);
shader_fn!(
    ui_icon,
    "ui_icon",
    "../../shaders/ui_icon.wgsl",
    "/ui_icon_vs.dxbc",
    "/ui_icon_ps.dxbc"
);
shader_fn!(
    ui_lift,
    "ui_lift",
    "../../shaders/ui_lift.wgsl",
    "/ui_lift_vs.dxbc",
    "/ui_lift_ps.dxbc"
);
