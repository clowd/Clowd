#[cfg(windows)]
include!("src/shader_bindings.rs");

fn main() {
    println!("cargo:rerun-if-changed=app.manifest");
    println!("cargo:rerun-if-changed=src/shader_bindings.rs");

    let is_windows = std::env::var("CARGO_CFG_WINDOWS").is_ok();

    if is_windows {
        let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
        let manifest_path = format!("{}/app.manifest", manifest_dir);
        println!("cargo:rustc-link-arg-bins=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg-bins=/MANIFESTINPUT:{}", manifest_path);

        compile_shaders(&manifest_dir);
    }
}

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

        let hlsl_options = build_hlsl_options(shader.bindings);

        let vs_hlsl = generate_hlsl(shader.name, &module, &info, &hlsl_options, naga::ShaderStage::Vertex, "vs_main");
        let ps_hlsl = generate_hlsl(shader.name, &module, &info, &hlsl_options, naga::ShaderStage::Fragment, "fs_main");

        let vs_dxbc = fxc.compile(shader.name, &vs_hlsl.source, &vs_hlsl.entry_point, "vs_5_1");
        let ps_dxbc = fxc.compile(shader.name, &ps_hlsl.source, &ps_hlsl.entry_point, "ps_5_1");

        std::fs::write(format!("{out_dir}/{}_vs.dxbc", shader.name), &vs_dxbc)
            .unwrap_or_else(|e| panic!("failed to write {}_vs.dxbc: {e}", shader.name));
        std::fs::write(format!("{out_dir}/{}_ps.dxbc", shader.name), &ps_dxbc)
            .unwrap_or_else(|e| panic!("failed to write {}_ps.dxbc: {e}", shader.name));
    }
}

#[cfg(not(windows))]
fn compile_shaders(_manifest_dir: &str) {}

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

// ── Binding map construction ────────────────────────────────────────
// Replicates wgpu-hal's create_pipeline_layout register assignment
// for our simple case: single bind group, no dynamic offsets, no immediates.

#[cfg(windows)]
fn build_hlsl_options(bindings: &[BindingEntry]) -> naga::back::hlsl::Options {
    use naga::back::hlsl;
    use std::collections::BTreeMap;

    let mut binding_map = hlsl::BindingMap::default();
    let mut sampler_buffer_binding_map = hlsl::SamplerIndexBufferBindingMap::default();

    let mut cbv_register: u32 = 0;
    let mut srv_register: u32 = 0;
    let mut sampler_index: u32 = 0;
    let mut has_samplers = false;

    for entry in bindings {
        let rb = naga::ResourceBinding {
            group: 0,
            binding: entry.binding,
        };
        match entry.kind {
            ResourceKind::UniformBuffer => {
                binding_map.insert(
                    rb,
                    hlsl::BindTarget {
                        space: 0,
                        register: cbv_register,
                        binding_array_size: None,
                        dynamic_storage_buffer_offsets_index: None,
                        restrict_indexing: false,
                    },
                );
                cbv_register += 1;
            }
            ResourceKind::Texture2D => {
                binding_map.insert(
                    rb,
                    hlsl::BindTarget {
                        space: 0,
                        register: srv_register,
                        binding_array_size: None,
                        dynamic_storage_buffer_offsets_index: None,
                        restrict_indexing: false,
                    },
                );
                srv_register += 1;
            }
            ResourceKind::Sampler => {
                binding_map.insert(
                    rb,
                    hlsl::BindTarget {
                        space: 255,
                        register: sampler_index,
                        binding_array_size: None,
                        dynamic_storage_buffer_offsets_index: None,
                        restrict_indexing: false,
                    },
                );
                sampler_index += 1;
                has_samplers = true;
            }
        }
    }

    if has_samplers {
        sampler_buffer_binding_map.insert(
            hlsl::SamplerIndexBufferKey {
                group: 0,
            },
            hlsl::BindTarget {
                space: 0,
                register: srv_register,
                binding_array_size: None,
                dynamic_storage_buffer_offsets_index: None,
                restrict_indexing: false,
            },
        );
    }

    hlsl::Options {
        shader_model: hlsl::ShaderModel::V5_1,
        binding_map,
        fake_missing_bindings: false,
        special_constants_binding: None,
        immediates_target: None,
        sampler_heap_target: hlsl::SamplerHeapBindTargets {
            standard_samplers: hlsl::BindTarget {
                space: 0,
                register: 0,
                binding_array_size: None,
                dynamic_storage_buffer_offsets_index: None,
                restrict_indexing: false,
            },
            comparison_samplers: hlsl::BindTarget {
                space: 0,
                register: 2048,
                binding_array_size: None,
                dynamic_storage_buffer_offsets_index: None,
                restrict_indexing: false,
            },
        },
        sampler_buffer_binding_map,
        dynamic_storage_buffer_offsets_targets: BTreeMap::new(),
        external_texture_binding_map: BTreeMap::new(),
        zero_initialize_workgroup_memory: true,
        restrict_indexing: true,
        force_loop_bounding: true,
        ray_query_initialization_tracking: true,
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

    fn compile(&self, shader_name: &str, hlsl_source: &str, entry_point: &str, profile: &str) -> Vec<u8> {
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
            panic!("FXC compilation failed for {shader_name} ({profile} {entry_point}):\n{err_msg}");
        }

        let result = unsafe { (*code).data().to_vec() };
        unsafe { (*code).release() };
        if !errors.is_null() {
            unsafe { (*errors).release() };
        }
        result
    }
}
