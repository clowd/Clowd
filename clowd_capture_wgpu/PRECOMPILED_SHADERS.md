# Precompiled Shader Passthrough

## What we did (Windows/DX12)

Moved shader compilation from runtime to build time. Previously, wgpu compiled all 4 WGSL shaders at startup via naga (WGSL→HLSL) then FXC (HLSL→DXBC). Now build.rs runs the same pipeline at compile time, embeds the DXBC bytecode, and passes it through to D3D12 via `device.create_shader_module_passthrough()`.

## Why

Eliminates shader compilation latency at app startup. The naga parse + FXC compile was the slowest part of GPU initialization, running once per render thread (one per monitor).

## wgpu passthrough API

wgpu 29.0 added `device.create_shader_module_passthrough()` (unsafe) which accepts precompiled bytecode and passes it directly to the GPU backend without any compilation or validation. Relevant issues/PRs:

- **https://github.com/gfx-rs/wgpu/issues/9052** — tracking issue for precompiled shader support across all backends
- **https://github.com/gfx-rs/wgpu/pull/7831** — the PR that implemented HLSL/DXIL/MSL/metallib passthrough

The descriptor (`CreateShaderModuleDescriptorPassthrough`) has fields for each backend's format:
```rust
pub struct CreateShaderModuleDescriptorPassthrough<'a, L> {
    pub label: L,
    pub num_workgroups: (u32, u32, u32),  // only for compute shaders
    pub spirv: Option<Cow<'a, [u32]>>,
    pub dxil: Option<Cow<'a, [u8]>>,      // DX12 — accepts both DXBC and DXIL despite the name
    pub hlsl: Option<Cow<'a, str>>,
    pub metallib: Option<Cow<'a, [u8]>>,  // Metal — precompiled metallib binary
    pub msl: Option<Cow<'a, str>>,        // Metal — MSL source (compiled at runtime by Metal)
    pub glsl: Option<Cow<'a, str>>,
    pub wgsl: Option<Cow<'a, str>>,
}
```

**Required device feature**: `wgpu::Features::PASSTHROUGH_SHADERS` must be enabled when requesting the device.

**Key constraint**: Each passthrough shader module wraps ONE precompiled blob for ONE entry point. So each .wgsl file (which has both `vs_main` and `fs_main`) produces two separate shader modules at runtime.

## Files changed (Windows implementation)

### `src/shader_bindings.rs` (new)
Single source of truth for binding metadata. Defines each shader's bindings as plain data (no wgpu/naga types). `include!()`'d by build.rs to construct the naga HLSL binding map. The binding map must match wgpu-hal's root signature construction — see the register assignment algorithm in `build_hlsl_options()`.

### `Cargo.toml`
Added Windows-only build dependencies:
```toml
[target.'cfg(windows)'.build-dependencies]
naga = { version = "29.0", features = ["wgsl-in", "hlsl-out"] }
libloading = "0.8"
```
naga version must match what wgpu 29.0 uses internally (both 29.0.x).

### `build.rs`
On Windows, for each of the 4 shaders:
1. Parse WGSL with `naga::front::wgsl::parse_str()`
2. Validate with `naga::valid::Validator`
3. Build `naga::back::hlsl::Options` with a binding map that replicates wgpu-hal's register assignment (see `build_hlsl_options()`)
4. Generate HLSL per entry point with `naga::back::hlsl::Writer` (one call for VS, one for FS)
5. Compile HLSL→DXBC via FXC (`D3DCompile` from `d3dcompiler_47.dll`, loaded with `libloading`)
6. Write 8 .dxbc files to `OUT_DIR` (VS+FS for each of 4 shaders)

On non-Windows, `compile_shaders()` is a no-op.

### `src/gpu.rs`
- Added `compiled_shaders` module with `include_bytes!()` for the 4 desktop/peek DXBC files
- Added `create_passthrough_module()` helper (pub(crate)) used by all pipeline files
- Desktop and peek pipelines use passthrough on Windows, `include_wgsl!()` fallback on other platforms
- Added `wgpu::Features::PASSTHROUGH_SHADERS` to required device features (Windows only)

### `src/ui/gpu/rect.rs`
Rect pipeline uses passthrough on Windows with `#[cfg(windows)]` / `#[cfg(not(windows))]` branching.

### `src/ui/gpu/icon.rs`
Icon pipeline uses passthrough on Windows, same pattern.

## The binding map problem

The hardest part of precompilation is ensuring the precompiled shader's register/buffer assignments match what wgpu's pipeline layout (root signature / argument table) expects at runtime.

For DX12, wgpu-hal's `create_pipeline_layout()` constructs a root signature and simultaneously builds a `naga::back::hlsl::Options` binding map. The HLSL register assignments in the precompiled shader must exactly match the root signature. Our `build_hlsl_options()` in build.rs replicates this algorithm for our simple case (single bind group, no dynamic offsets, no immediates).

For Metal, there will be an analogous problem: naga's MSL backend assigns buffer/texture/sampler indices, and wgpu-hal's Metal backend expects specific indices when binding resources. You'll need to study `wgpu-hal/src/metal/device.rs` to understand how it assigns indices.

## Shaders

4 shaders, all in `shaders/`:

| Shader | Bindings | Notes |
|--------|----------|-------|
| `desktop.wgsl` | uniform(0), texture(1), sampler(2) | Fullscreen triangle, no vertex buffers |
| `peek.wgsl` | uniform(0), texture(1), texture(2), sampler(3) | 6-vertex quad |
| `ui_rect.wgsl` | uniform(0) | Instanced quads, vertex buffer with 4x float32x4 |
| `ui_icon.wgsl` | uniform(0), texture(1), sampler(2) | Instanced quads, vertex buffer with 2x float32x4 + float32 |

All use bind group 0 only. No compute shaders. Entry points are always `vs_main` and `fs_main`.

## Metal precompilation — research needed

### 1. Passthrough format
The passthrough descriptor has two Metal options: `msl` (MSL source string, compiled at runtime by Metal) and `metallib` (precompiled metallib binary, zero runtime compilation). You want `metallib` for full precompilation.

### 2. naga MSL backend
Study `naga::back::msl::Options` and `naga::back::msl::PipelineOptions`. The MSL backend is simpler than HLSL — Metal uses sequential buffer/texture/sampler indices rather than the complex register space model. Check what options wgpu-hal's Metal backend passes to the naga MSL writer in `wgpu-hal/src/metal/device.rs`.

### 3. metallib compilation
The pipeline would be: WGSL → naga → MSL source → `xcrun metal` → AIR → `xcrun metallib` → metallib binary. Both `metal` and `metallib` are command-line tools in Xcode. In build.rs you'd shell out to them. Check if there's a library alternative.

### 4. wgpu-hal Metal passthrough path
Read how `ShaderInput::MetalLib` is handled in `wgpu-hal/src/metal/device.rs`. In the DX12 backend, passthrough returns `CompiledShader::Precompiled(bytes)` which bypasses all compilation. The Metal backend likely has a similar path loading the metallib with `device.newLibrary(data:)`.

### 5. Binding index assignment
The critical sync problem: Metal uses buffer indices, texture indices, and sampler indices. Study how wgpu-hal's Metal `create_pipeline_layout()` assigns these, then replicate the algorithm in build.rs for naga's MSL options. This is the Metal equivalent of our `build_hlsl_options()`.

### 6. Build dependencies for macOS
You'll need naga with `msl-out` feature:
```toml
[target.'cfg(target_os = "macos")'.build-dependencies]
naga = { version = "29.0", features = ["wgsl-in", "msl-out"] }
```
No `libloading` needed — shell out to `xcrun metal` and `xcrun metallib` instead.

### 7. Relevant wgpu source files to read
All in the local cargo registry at `~/.cargo/registry/src/index.crates.io-*/`:
- `wgpu-hal-29.0.1/src/metal/device.rs` — `create_shader_module`, `create_pipeline_layout`, `load_shader`
- `wgpu-hal-29.0.1/src/metal/mod.rs` — `ShaderModule`, `ShaderModuleSource` types
- `naga-29.0.1/src/back/msl/mod.rs` — `Options`, `PipelineOptions`, `BindTarget`
- `wgpu-types-29.0.1/src/shader.rs` — `CreateShaderModuleDescriptorPassthrough`
