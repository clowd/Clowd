use std::borrow::Cow;
use std::sync::Arc;
#[cfg(windows)]
use std::sync::Mutex;

/// A shader program for one pipeline. Windows ships precompiled DXBC
/// (`build.rs`: WGSL → naga → HLSL → FXC) as separate vertex/fragment
/// passthrough modules. A blob the driver rejects PANICS in debug builds
/// — a binding-contract break must be caught at the developer's desk —
/// but in release it falls back to compiling the WGSL at runtime, with an
/// `error!` that becomes a Sentry event: for a user, a working overlay on
/// the slow path beats an error dialog, and the event tells us it
/// happened (see issue #74). Everywhere else the WGSL is compiled at
/// runtime into a single module exposing both entry points.
pub struct ShaderPair {
    modules: ShaderModules,
    /// Carried so [`build_pipeline`] can rebuild this shader from source
    /// when the precompiled blobs fail at pipeline creation.
    #[cfg(windows)]
    label: &'static str,
    #[cfg(windows)]
    wgsl_source: &'static str,
}

enum ShaderModules {
    /// Separate vertex and fragment modules (Windows DXBC passthrough).
    #[cfg(windows)]
    Split { vs: wgpu::ShaderModule, fs: wgpu::ShaderModule },
    /// One module exposing both `vs_main` and `fs_main` (runtime-WGSL).
    Unified(wgpu::ShaderModule),
}

impl ShaderPair {
    pub fn vs(&self) -> &wgpu::ShaderModule {
        match &self.modules {
            #[cfg(windows)]
            ShaderModules::Split {
                vs,
                ..
            } => vs,
            ShaderModules::Unified(module) => module,
        }
    }

    pub fn fs(&self) -> &wgpu::ShaderModule {
        match &self.modules {
            #[cfg(windows)]
            ShaderModules::Split {
                fs,
                ..
            } => fs,
            ShaderModules::Unified(module) => module,
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

// Debug-overlay truth for `precomp_shaders`: true only if at least one
// passthrough pair was BUILT and no shader fell back to runtime WGSL —
// "were the precompiled shaders actually used", not "were they present".
// Always false on non-Windows (runtime WGSL is the only path there).
static ANY_SPLIT_USED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
static ANY_FALLBACK_USED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

/// Whether rendering is running on the precompiled (passthrough) shaders.
pub fn precompiled_in_use() -> bool {
    use std::sync::atomic::Ordering;
    ANY_SPLIT_USED.load(Ordering::Relaxed) && !ANY_FALLBACK_USED.load(Ordering::Relaxed)
}

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

/// Compile the WGSL source at runtime. The only path on macOS; on Windows
/// the release-build fallback when a precompiled blob fails to load.
fn wgsl(device: &wgpu::Device, label: &'static str, wgsl_source: &'static str) -> ShaderPair {
    let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some(label),
        source: wgpu::ShaderSource::Wgsl(Cow::Borrowed(wgsl_source)),
    });
    ShaderPair {
        modules: ShaderModules::Unified(module),
        #[cfg(windows)]
        label,
        #[cfg(windows)]
        wgsl_source,
    }
}

/// Load the precompiled passthrough shader pair.
///
/// NOTE: wgpu 30 does NOT validate blob CONTENTS here — module creation
/// only checks the entry-point count, and a bad/garbage blob surfaces
/// later, inside `create_render_pipeline`'s driver translation (verified
/// empirically: forced garbage blobs sail through this function and fail
/// with `Internal error … 0x80004005` at pipeline creation). The error
/// check below therefore only catches module-level failures (e.g. a
/// missing feature); the real blob-failure handling — debug panic,
/// release fallback + Sentry `error!` — lives in [`build_pipeline`].
#[cfg(windows)]
fn load(
    device: &wgpu::Device,
    label: &'static str,
    vs_bytes: &'static [u8],
    fs_bytes: &'static [u8],
    wgsl_source: &'static str,
) -> ShaderPair {
    use std::sync::atomic::Ordering;
    let _guard = LOAD_GUARD.lock().unwrap();
    take_uncaptured_error(); // clear any stale error
    let vs = unsafe { passthrough(device, &format!("{label} VS"), vs_bytes, "vs_main") };
    let fs = unsafe { passthrough(device, &format!("{label} FS"), fs_bytes, "fs_main") };
    if let Some(err) = take_uncaptured_error() {
        if cfg!(debug_assertions) {
            panic!("precompiled shader '{label}' failed to load: {err}");
        }
        log::error!("precompiled shader '{label}' failed to load; falling back to runtime WGSL compilation: {err}");
        ANY_FALLBACK_USED.store(true, Ordering::Relaxed);
        return wgsl(device, label, wgsl_source);
    }
    ShaderPair {
        modules: ShaderModules::Split {
            vs,
            fs,
        },
        label,
        wgsl_source,
    }
}

/// Build a render pipeline from `shader`, handling precompiled-blob
/// failure at the point where it actually surfaces (see [`load`]'s note).
/// On a creation error from the passthrough modules: debug builds PANIC
/// naming the pipeline (a broken blob or drifted register contract must
/// never survive development — the panic rides the worker's fail path:
/// `ReadyGuard` → `failed_count` → show gate → the shell's error dialog);
/// release builds recompile the shader's WGSL at runtime and rebuild the
/// pipeline, with an `error!` that becomes a Sentry event via
/// `clowd_rust_core::telemetry`'s log bridge, so the fallback is never
/// silent. The guard serialises concurrent pipeline builds (the deferred
/// UI stack compiles on several threads) so an uncaptured error is
/// attributed to the build that caused it.
pub fn build_pipeline(
    device: &wgpu::Device,
    pipeline_label: &str,
    shader: &ShaderPair,
    build: impl Fn(&ShaderPair) -> wgpu::RenderPipeline,
) -> wgpu::RenderPipeline {
    #[cfg(windows)]
    {
        use std::sync::atomic::Ordering;
        // A runtime-WGSL pair has no blobs to fail; build it straight.
        if matches!(shader.modules, ShaderModules::Unified(_)) {
            return build(shader);
        }
        let _guard = LOAD_GUARD.lock().unwrap();
        take_uncaptured_error(); // clear any stale error
        let pipeline = build(shader);
        if let Some(err) = take_uncaptured_error() {
            if cfg!(debug_assertions) {
                panic!(
                    "pipeline '{pipeline_label}' failed to build from precompiled shader '{}': {err}",
                    shader.label
                );
            }
            log::error!(
                "pipeline '{pipeline_label}' failed to build from precompiled shader '{}'; falling back to runtime WGSL compilation: {err}",
                shader.label
            );
            ANY_FALLBACK_USED.store(true, Ordering::Relaxed);
            let unified = wgsl(device, shader.label, shader.wgsl_source);
            return build(&unified);
        }
        ANY_SPLIT_USED.store(true, Ordering::Relaxed);
        pipeline
    }
    #[cfg(not(windows))]
    {
        let _ = (device, pipeline_label);
        build(shader)
    }
}

macro_rules! shader_fn {
    ($name:ident, $label:literal, $wgsl:literal, $vs:literal, $fs:literal) => {
        pub fn $name(device: &wgpu::Device) -> ShaderPair {
            #[cfg(windows)]
            {
                const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), $vs));
                const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), $fs));
                load(device, $label, VS, FS, include_str!($wgsl))
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
shader_fn!(
    ui_text,
    "ui_text",
    "../../shaders/ui_text.wgsl",
    "/ui_text_vs.dxbc",
    "/ui_text_ps.dxbc"
);
