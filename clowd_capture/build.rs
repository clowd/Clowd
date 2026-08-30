/// The application manifest is shared with the other Clowd binaries and
/// lives in `clowd_rust_core/` beside the Rust they share (see that crate's
/// manifest note). It cannot be embedded *by* that crate — `/MANIFEST:EMBED`
/// is a link argument, and only the crate producing the executable links one
/// — so each binary's build script points at the single copy instead of
/// keeping its own. What it buys us is per-monitor DPI awareness; a copy
/// that silently drifted would make one binary read virtualized coordinates
/// and photograph the wrong pixels.
const SHARED_MANIFEST: &str = "../clowd_rust_core/app.manifest";

// Windows precompiles the capture shaders (WGSL → naga → HLSL → FXC → DXBC)
// and the binary consumes the blobs directly — the d3d11 backend feeds
// the SM 5.0 set to Create{Vertex,Pixel}Shader (src/gxi/d3d11/shaders.rs)
// — so no naga or FXC runs on user machines. macOS precompiles the same
// WGSL to MSL *source* text instead
// (WGSL → naga → {name}.metal in OUT_DIR), which the metal backend
// compiles at runtime with newLibraryWithSource. Source rather than a
// metallib because a metallib is pinned to the Metal language version it
// was built against, which caused too many compatibility problems across
// supported macOS versions; MSL source has no such pin, so naga still
// never runs on user machines and WGSL stays the single source of truth.
#[cfg(any(windows, target_os = "macos"))]
include!("src/shader_bindings.rs");

fn main() {
    println!("cargo:rerun-if-changed={SHARED_MANIFEST}");
    println!("cargo:rerun-if-changed=src/shader_bindings.rs");
    // baked into the Sentry release name via option_env! (clowd_rust_core's
    // telemetry::release), so a version bump has to invalidate the cached build
    println!("cargo:rerun-if-env-changed=CLOWD_VERSION");

    #[cfg(windows)]
    {
        let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
        let manifest_path = std::path::Path::new(&manifest_dir).join(SHARED_MANIFEST);
        // Fail loudly rather than link an unmanifested exe: without the
        // manifest the process is DPI-virtualized, which is a subtle
        // wrong-pixels bug rather than an obvious one.
        assert!(
            manifest_path.is_file(),
            "shared app manifest not found at {}",
            manifest_path.display()
        );
        println!("cargo:rustc-link-arg-bins=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg-bins=/MANIFESTINPUT:{}", manifest_path.display());

        compile_shaders(&manifest_dir);
    }

    #[cfg(target_os = "macos")]
    {
        let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
        compile_shaders_msl(&manifest_dir);
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Windows: WGSL → naga → HLSL → FXC → DXBC
//
// One blob set is produced per shader:
//   * {name}_d11_vs.dxbc / {name}_d11_ps.dxbc — SM 5.0, flat classic
//     registers for the D3D11 backend. Register contract: walk the
//     shader's BindingEntry table in order with three counters —
//     UniformBuffer → b0.., Texture2D → t0.., Sampler → s0.. — all
//     space0. The runtime backend recomputes identical slots from the
//     same tables (see the note in src/shader_bindings.rs).
// ═══════════════════════════════════════════════════════════════════════

#[cfg(windows)]
fn compile_shaders(manifest_dir: &str) {
    let out_dir = std::env::var("OUT_DIR").unwrap();
    let fxc = FxcCompiler::load();

    for shader in ALL_SHADERS {
        let wgsl_path = format!("{}/{}", manifest_dir, shader.wgsl_path);
        println!("cargo:rerun-if-changed={}", wgsl_path);

        let wgsl_source = std::fs::read_to_string(&wgsl_path).unwrap_or_else(|e| panic!("failed to read {}: {e}", wgsl_path));

        let module = naga::front::wgsl::parse_str(&wgsl_source).unwrap_or_else(|e| panic!("failed to parse {}: {e}", shader.name));

        let info = naga::valid::Validator::new(naga::valid::ValidationFlags::all(), naga::valid::Capabilities::empty())
            .validate(&module)
            .unwrap_or_else(|e| panic!("validation failed for {}: {e}", shader.name));

        compile_d3d11_set(&fxc, &out_dir, shader, &module, &info);
    }
}

/// SM 5.0 set for the D3D11 backend. naga 30's HLSL backend has no classic
/// sampler-register mode — it unconditionally emits a 2048-entry sampler
/// heap plus a StructuredBuffer of sampler indices (register-space syntax
/// FXC rejects below SM 5.1) — so the generated HLSL is deterministically
/// patched back to `SamplerState name : register(sN);` before FXC. The
/// patch is guarded by hard assertions; any failure dumps the HLSL to
/// OUT_DIR and panics with the path.
#[cfg(windows)]
fn compile_d3d11_set(fxc: &FxcCompiler, out_dir: &str, shader: &ShaderDef, module: &naga::Module, info: &naga::valid::ModuleInfo) {
    let hlsl_options = build_d3d11_hlsl_options(shader.bindings);
    let expected_samplers = shader
        .bindings
        .iter()
        .filter(|b| b.kind == ResourceKind::Sampler)
        .count();

    for (stage, entry_name, suffix, profile) in [
        (naga::ShaderStage::Vertex, "vs_main", "vs", "vs_5_0"),
        (naga::ShaderStage::Fragment, "fs_main", "ps", "ps_5_0"),
    ] {
        let hlsl = generate_hlsl(shader.name, module, info, &hlsl_options, stage, entry_name);
        let patched = patch_d3d11_hlsl(shader.name, suffix, &hlsl.source, expected_samplers, out_dir);

        let dxbc = fxc
            .try_compile(&patched, &hlsl.entry_point, profile)
            .unwrap_or_else(|err| {
                let dump = dump_hlsl(out_dir, shader.name, suffix, &patched);
                panic!(
                    "FXC compilation failed for {} ({profile} {}):\n{err}\npatched HLSL dumped to {dump}",
                    shader.name, hlsl.entry_point
                );
            });

        std::fs::write(format!("{out_dir}/{}_d11_{suffix}.dxbc", shader.name), &dxbc)
            .unwrap_or_else(|e| panic!("failed to write {}_d11_{suffix}.dxbc: {e}", shader.name));
    }
}

/// Rewrites naga's sampler-heap ABI into classic SM 5.0 sampler registers:
///   * drops the `SamplerState nagaSamplerHeap[2048]` /
///     `SamplerComparisonState nagaComparisonSamplerHeap[2048]` declarations,
///   * drops the `StructuredBuffer<uint> nagaGroup*SamplerIndexArray`
///     declaration (its scratch t# register is never referenced again),
///   * replaces each
///     `static const SamplerState NAME = nagaSamplerHeap[nagaGroup…SamplerIndexArray[R]];`
///     with `SamplerState NAME : register(sR);` — R is the literal we put in
///     the sampler's BindTarget, i.e. the flat s# slot.
#[cfg(windows)]
fn patch_d3d11_hlsl(shader_name: &str, stage_suffix: &str, source: &str, expected_samplers: usize, out_dir: &str) -> String {
    let mut replaced = 0usize;
    let mut out = String::with_capacity(source.len());

    for line in source.lines() {
        let trimmed = line.trim_start();

        if trimmed.starts_with("SamplerState nagaSamplerHeap[")
            || trimmed.starts_with("SamplerComparisonState nagaComparisonSamplerHeap[")
            || (trimmed.starts_with("StructuredBuffer<uint> nagaGroup") && trimmed.contains("SamplerIndexArray"))
        {
            continue;
        }

        if let Some(rest) = trimmed.strip_prefix("static const SamplerState ") {
            if let Some(eq_pos) = rest.find(" = nagaSamplerHeap[") {
                let name = &rest[..eq_pos];
                let tail = &rest[eq_pos..];
                let idx_open = tail
                    .find("SamplerIndexArray[")
                    .map(|p| p + "SamplerIndexArray[".len())
                    .unwrap_or_else(|| {
                        let dump = dump_hlsl(out_dir, shader_name, stage_suffix, source);
                        panic!("d11 patcher: unrecognized sampler initializer for {shader_name} {stage_suffix}: {line:?}\nHLSL dumped to {dump}");
                    });
                let idx_close = tail[idx_open..]
                    .find(']')
                    .unwrap_or_else(|| {
                        let dump = dump_hlsl(out_dir, shader_name, stage_suffix, source);
                        panic!("d11 patcher: unterminated sampler index for {shader_name} {stage_suffix}: {line:?}\nHLSL dumped to {dump}");
                    });
                let register: u32 = tail[idx_open..idx_open + idx_close].parse().unwrap_or_else(|e| {
                    let dump = dump_hlsl(out_dir, shader_name, stage_suffix, source);
                    panic!("d11 patcher: non-numeric sampler index for {shader_name} {stage_suffix} ({e}): {line:?}\nHLSL dumped to {dump}");
                });
                out.push_str(&format!("SamplerState {name} : register(s{register});\n"));
                replaced += 1;
                continue;
            }
        }

        out.push_str(line);
        out.push('\n');
    }

    // Hard assertions: the patch must have fully eliminated the heap ABI,
    // and touched exactly as many samplers as the binding table declares
    // (naga emits every module global into both stages' HLSL).
    let mut failure = None;
    if replaced != expected_samplers {
        failure = Some(format!(
            "replaced {replaced} sampler declarations, binding table has {expected_samplers}"
        ));
    } else if out.contains("nagaSamplerHeap") || out.contains("nagaComparisonSamplerHeap") {
        failure = Some("nagaSamplerHeap reference survived patching".to_string());
    } else if out.contains("SamplerIndexArray") {
        failure = Some("SamplerIndexArray reference survived patching".to_string());
    } else if register_annotation_has_space(&out) {
        failure = Some("register-space annotation survived patching (FXC SM 5.0 rejects it)".to_string());
    }
    if let Some(msg) = failure {
        let dump = dump_hlsl(out_dir, shader_name, stage_suffix, &out);
        panic!("d11 patcher assertion failed for {shader_name} {stage_suffix}: {msg}\npatched HLSL dumped to {dump}");
    }

    out
}

/// True if any `register(...)` annotation in the HLSL still names a register
/// space (naga writes them as `register(xN, spaceM)`). Anchored to the
/// annotations themselves so a WGSL identifier that happens to be called
/// `space` elsewhere in the shader can't trip a spurious build panic.
#[cfg(windows)]
fn register_annotation_has_space(hlsl: &str) -> bool {
    let mut rest = hlsl;
    while let Some(pos) = rest.find("register(") {
        let after = &rest[pos + "register(".len()..];
        let annotation = after.split(')').next().unwrap_or(after);
        if annotation.contains("space") {
            return true;
        }
        rest = after;
    }
    false
}

#[cfg(windows)]
fn dump_hlsl(out_dir: &str, shader_name: &str, stage_suffix: &str, source: &str) -> String {
    let path = format!("{out_dir}/{shader_name}_d11_{stage_suffix}_failed.hlsl");
    std::fs::write(&path, source).unwrap_or_else(|e| panic!("failed to dump HLSL to {path}: {e}"));
    path
}

// ── HLSL generation ─────────────────────────────────────────────────

#[cfg(windows)]
struct HlslOutput {
    source: String,
    entry_point: String,
}

#[cfg(windows)]
fn generate_hlsl(
    shader_name: &str,
    module: &naga::Module,
    info: &naga::valid::ModuleInfo,
    options: &naga::back::hlsl::Options,
    stage: naga::ShaderStage,
    entry_name: &str,
) -> HlslOutput {
    use naga::back::hlsl;

    let pipeline_options = hlsl::PipelineOptions {
        entry_point: Some((stage, entry_name.to_string())),
    };

    let mut source = String::new();
    let mut writer = hlsl::Writer::new(&mut source, options, &pipeline_options);

    let frag_ep = if stage == naga::ShaderStage::Vertex {
        hlsl::FragmentEntryPoint::new(module, "fs_main")
    } else {
        None
    };

    let mut reflection = writer
        .write(module, info, frag_ep.as_ref())
        .unwrap_or_else(|e| panic!("HLSL generation failed for {shader_name} {entry_name}: {e}"));

    assert_eq!(reflection.entry_point_names.len(), 1);
    let entry_point = reflection
        .entry_point_names
        .pop()
        .unwrap()
        .unwrap_or_else(|e| panic!("entry point error for {shader_name} {entry_name}: {e}"));

    HlslOutput {
        source,
        entry_point,
    }
}

// ── D3D11 HLSL binding map construction ─────────────────────────────
// Flat classic-register scheme derived from the BindingEntry table order:
// UniformBuffer → b0.., Texture2D → t0.., Sampler → s0.., all space0.
// This is the contract the Phase D d3d11 backend recomputes at runtime
// from the same tables — see the note in src/shader_bindings.rs.

#[cfg(windows)]
fn build_d3d11_hlsl_options(bindings: &[BindingEntry]) -> naga::back::hlsl::Options {
    use naga::back::hlsl;
    use std::collections::BTreeMap;

    let flat = |register: u32| hlsl::BindTarget {
        space: 0,
        register,
        binding_array_size: None,
        dynamic_storage_buffer_offsets_index: None,
        restrict_indexing: false,
    };

    let mut binding_map = hlsl::BindingMap::default();
    let mut sampler_buffer_binding_map = hlsl::SamplerIndexBufferBindingMap::default();

    let mut cbv_register: u32 = 0;
    let mut srv_register: u32 = 0;
    let mut sampler_register: u32 = 0;

    for entry in bindings {
        let rb = naga::ResourceBinding {
            group: 0,
            binding: entry.binding,
        };
        match entry.kind {
            ResourceKind::UniformBuffer => {
                binding_map.insert(rb, flat(cbv_register));
                cbv_register += 1;
            }
            ResourceKind::Texture2D => {
                binding_map.insert(rb, flat(srv_register));
                srv_register += 1;
            }
            ResourceKind::Sampler => {
                // naga writes this register as the literal subscript of the
                // sampler-index array — the patcher lifts it back out as the
                // s# slot, so it IS the flat sampler register.
                binding_map.insert(rb, flat(sampler_register));
                sampler_register += 1;
            }
        }
    }

    if sampler_register > 0 {
        // Scratch SRV register above our real textures for naga's
        // sampler-index StructuredBuffer; the patcher deletes its
        // declaration, so it never reaches FXC.
        sampler_buffer_binding_map.insert(
            hlsl::SamplerIndexBufferKey {
                group: 0,
            },
            flat(srv_register),
        );
    }

    hlsl::Options {
        shader_model: hlsl::ShaderModel::V5_0,
        binding_map,
        fake_missing_bindings: false,
        special_constants_binding: None,
        immediates_target: None,
        // Only feeds the heap declarations the patcher deletes; the values
        // never reach FXC.
        sampler_heap_target: hlsl::SamplerHeapBindTargets {
            standard_samplers: flat(0),
            comparison_samplers: flat(2048),
        },
        sampler_buffer_binding_map,
        dynamic_storage_buffer_offsets_targets: BTreeMap::new(),
        external_texture_binding_map: BTreeMap::new(),
        zero_initialize_workgroup_memory: true,
        restrict_indexing: true,
        force_loop_bounding: true,
        ray_query_initialization_tracking: true,
        task_dispatch_limits: None,
        mesh_shader_primitive_indices_clamp: true,
    }
}

// ── FXC compilation via d3dcompiler_47.dll ───────────────────────────

#[cfg(windows)]
type D3DCompileFn = unsafe extern "system" fn(
    psrcdata: *const std::ffi::c_void,
    srcdatasize: usize,
    psourcename: *const u8,
    pdefines: *const std::ffi::c_void,
    pinclude: *const std::ffi::c_void,
    pentrypoint: *const u8,
    ptarget: *const u8,
    flags1: u32,
    flags2: u32,
    ppcode: *mut *mut ID3DBlob,
    pperrormsgs: *mut *mut ID3DBlob,
) -> i32;

#[cfg(windows)]
#[repr(C)]
struct ID3DBlobVtbl {
    query_interface: *const std::ffi::c_void,
    add_ref: unsafe extern "system" fn(*mut ID3DBlob) -> u32,
    release: unsafe extern "system" fn(*mut ID3DBlob) -> u32,
    get_buffer_pointer: unsafe extern "system" fn(*mut ID3DBlob) -> *const u8,
    get_buffer_size: unsafe extern "system" fn(*mut ID3DBlob) -> usize,
}

#[cfg(windows)]
#[repr(C)]
struct ID3DBlob {
    vtbl: *const ID3DBlobVtbl,
}

#[cfg(windows)]
impl ID3DBlob {
    unsafe fn data(&self) -> &[u8] {
        let ptr = ((*self.vtbl).get_buffer_pointer)(self as *const _ as *mut _);
        let len = ((*self.vtbl).get_buffer_size)(self as *const _ as *mut _);
        std::slice::from_raw_parts(ptr, len)
    }

    unsafe fn release(&self) {
        ((*self.vtbl).release)(self as *const _ as *mut _);
    }
}

#[cfg(windows)]
const D3DCOMPILE_ENABLE_STRICTNESS: u32 = 1 << 11;

#[cfg(windows)]
struct FxcCompiler {
    _lib: libloading::Library,
    d3d_compile: D3DCompileFn,
}

#[cfg(windows)]
impl FxcCompiler {
    fn load() -> Self {
        let lib = unsafe { libloading::Library::new("d3dcompiler_47.dll") }
            .expect("failed to load d3dcompiler_47.dll — is the Windows SDK installed?");
        let d3d_compile: D3DCompileFn = unsafe {
            *lib.get::<D3DCompileFn>(b"D3DCompile\0")
                .expect("D3DCompile not found in d3dcompiler_47.dll")
        };
        Self {
            _lib: lib,
            d3d_compile,
        }
    }

    fn try_compile(&self, hlsl_source: &str, entry_point: &str, profile: &str) -> Result<Vec<u8>, String> {
        let entry_cstr = std::ffi::CString::new(entry_point).unwrap();
        let profile_cstr = std::ffi::CString::new(profile).unwrap();

        let mut code: *mut ID3DBlob = std::ptr::null_mut();
        let mut errors: *mut ID3DBlob = std::ptr::null_mut();

        let hr = unsafe {
            (self.d3d_compile)(
                hlsl_source.as_ptr() as *const _,
                hlsl_source.len(),
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
                entry_cstr.as_ptr() as *const u8,
                profile_cstr.as_ptr() as *const u8,
                D3DCOMPILE_ENABLE_STRICTNESS,
                0,
                &mut code,
                &mut errors,
            )
        };

        if hr < 0 {
            let err_msg = if !errors.is_null() {
                let msg = unsafe { String::from_utf8_lossy((*errors).data()).to_string() };
                unsafe { (*errors).release() };
                msg
            } else {
                format!("HRESULT 0x{:08x}", hr as u32)
            };
            return Err(err_msg);
        }

        let result = unsafe { (*code).data().to_vec() };
        unsafe { (*code).release() };
        if !errors.is_null() {
            unsafe { (*errors).release() };
        }
        Ok(result)
    }
}

// ═══════════════════════════════════════════════════════════════════════
// macOS: WGSL → naga → MSL source ({name}.metal)
//
// One MSL source file per shader, carrying both entry points; the metal
// backend include_str!s the text and compiles it at runtime with
// newLibraryWithSource (see the header comment for why source rather
// than a metallib). Slot contract: walk the shader's BindingEntry table
// in order with three counters, UniformBuffer → buffer(0..),
// Texture2D → texture(0..), Sampler → sampler(0..). The runtime backend
// recomputes identical slots from the same tables (see the note in
// src/shader_bindings.rs). Vertex/instance data arrives via [[stage_in]]
// plus a runtime MTLVertexDescriptor with its layout pinned at
// buffer(30), so it can never collide with the uniform-buffer slots.
// ═══════════════════════════════════════════════════════════════════════

#[cfg(target_os = "macos")]
fn compile_shaders_msl(manifest_dir: &str) {
    let out_dir = std::env::var("OUT_DIR").unwrap();

    for shader in ALL_SHADERS {
        let wgsl_path = format!("{}/{}", manifest_dir, shader.wgsl_path);
        println!("cargo:rerun-if-changed={}", wgsl_path);

        let wgsl_source = std::fs::read_to_string(&wgsl_path).unwrap_or_else(|e| panic!("failed to read {}: {e}", wgsl_path));

        let module = naga::front::wgsl::parse_str(&wgsl_source).unwrap_or_else(|e| panic!("failed to parse {}: {e}", shader.name));

        let info = naga::valid::Validator::new(naga::valid::ValidationFlags::all(), naga::valid::Capabilities::empty())
            .validate(&module)
            .unwrap_or_else(|e| panic!("validation failed for {}: {e}", shader.name));

        let options = build_msl_options(shader.bindings);
        let pipeline_options = naga::back::msl::PipelineOptions {
            // None writes every entry point, so one source file serves both
            // stages of the pipeline.
            entry_point: None,
            // Keep Metal's [[stage_in]] attribute assembly; the runtime
            // MTLVertexDescriptor does the unpacking, not injected code.
            vertex_pulling_transform: false,
            ..Default::default()
        };

        let (msl, translation) = naga::back::msl::write_string(&module, &info, &options, &pipeline_options)
            .unwrap_or_else(|e| panic!("MSL generation failed for {}: {e}", shader.name));

        // The metal backend looks the functions up by name with
        // newFunctionWithName, so the translated names must survive
        // untouched. Fail the build loudly if naga ever renames them
        // (same posture as the D3D11 HLSL patcher's assertions).
        let entry_names = translation
            .entry_point_names
            .into_iter()
            .map(|r| r.unwrap_or_else(|e| panic!("entry point error for {}: {e}", shader.name)))
            .collect::<Vec<String>>();
        assert_eq!(
            entry_names,
            ["vs_main", "fs_main"],
            "unexpected MSL entry point names for {}",
            shader.name
        );

        let metal_path = format!("{out_dir}/{}.metal", shader.name);
        std::fs::write(&metal_path, &msl).unwrap_or_else(|e| panic!("failed to write {metal_path}: {e}"));
    }
}

// ── MSL binding map construction ────────────────────────────────────
// Flat slot scheme derived from the BindingEntry table order, mirroring
// build_d3d11_hlsl_options' three-counter walk. naga resolves resources
// per entry point; both stages see the same module globals, so both
// keys carry the identical map.

#[cfg(target_os = "macos")]
fn build_msl_options(bindings: &[BindingEntry]) -> naga::back::msl::Options {
    use naga::back::msl;

    let mut resources = msl::BindingMap::default();

    let mut buffer_slot: msl::Slot = 0;
    let mut texture_slot: msl::Slot = 0;
    let mut sampler_slot: msl::Slot = 0;

    for entry in bindings {
        let rb = naga::ResourceBinding {
            group: 0,
            binding: entry.binding,
        };
        let target = match entry.kind {
            ResourceKind::UniformBuffer => {
                let t = msl::BindTarget {
                    buffer: Some(buffer_slot),
                    ..Default::default()
                };
                buffer_slot += 1;
                t
            }
            ResourceKind::Texture2D => {
                let t = msl::BindTarget {
                    texture: Some(texture_slot),
                    ..Default::default()
                };
                texture_slot += 1;
                t
            }
            ResourceKind::Sampler => {
                let t = msl::BindTarget {
                    sampler: Some(msl::BindSamplerTarget::Resource(sampler_slot)),
                    ..Default::default()
                };
                sampler_slot += 1;
                t
            }
        };
        resources.insert(rb, target);
    }

    let per_entry_point = msl::EntryPointResources {
        resources,
        immediates_buffer: None,
        sizes_buffer: None,
    };

    let mut per_entry_point_map = msl::EntryPointResourceMap::default();
    per_entry_point_map.insert("vs_main".to_string(), per_entry_point.clone());
    per_entry_point_map.insert("fs_main".to_string(), per_entry_point);

    msl::Options {
        // MSL 2.0 is available on every supported macOS version; nothing in
        // these shaders needs newer language features.
        lang_version: (2, 0),
        per_entry_point_map,
        // A binding missing from the map must fail the build, not emit
        // invalid MSL that only breaks at runtime.
        fake_missing_bindings: false,
        ..Default::default()
    }
}
