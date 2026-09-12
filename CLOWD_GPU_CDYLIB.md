# clowd_gpu: a Rust cdylib for the video engine's GPU interop

Status: plan, not started. Written 2026-09-11 after the render-pipeline performance work; the
specifics below are direction, not a spec. Revisit when Metal zero-copy or another native interop
feature comes up.

## Why

`Clowd.VideoSDK` composes frames with Skia (SkiaSharp) and encodes them with FFmpeg
(FFmpeg.AutoGen). Both are native, and the measured cost of a render lives in them and the
driver, not in C#. What C# does own is the glue between them, and that glue is the ugly part of
the codebase:

- `Composition/D3D12Backend.cs` creates the D3D12 device by calling COM methods through raw vtable
  slot indices (`delegate* unmanaged[Stdcall]`), with hand-declared struct layouts. The pipelined
  readback (own readback heap, fences, copy queue) and the zero-copy path (shared texture and fence
  opened in a D3D11 device for NVENC/AMF `AV_PIX_FMT_D3D11` frames) add a lot more of the same.
  A wrong slot index or struct layout fails at runtime, on the user's GPU, with an access violation.
- `Composition/MetalBackend.cs` is `objc_msgSend` by hand and has never run on a Mac. Metal
  zero-copy (IOSurface-backed `CVPixelBuffer` as the Skia render target, handed straight to
  VideoToolbox) will be more of it.
- Native resources are released by `Dispose` discipline; every early-return path is a leak risk.

Measurements that motivated the readback and zero-copy work (RTX 4070, i7-14700K, 3863-frame
60 fps job): the old synchronous `SKSurface.ReadPixels` cost 2 ms/frame at 1240x1166 and
8 ms/frame at 2480x2332 (~2.8 GB/s, an uncached readback-heap path), which capped the render at
243 / 68 fps regardless of encoder. Swapping in NVENC alone changed almost nothing. The fix is
GPU plumbing, and GPU plumbing is what this crate would own.

Rewriting the whole engine in Rust buys nothing: the hot loops are already native, and the point
of `Clowd.VideoSDK` is one composition engine shared by the Avalonia preview and the final render
(WYSIWYG). vid-render was Rust and was replaced by the C# engine precisely to get that parity.
The crate proposed here is the opposite move: keep the engine and the model in C#, push only the
platform GPU primitives into Rust.

## What goes in the crate

A `cdylib` named something like `clowd_gpu`, in the existing Cargo workspace (which already
depends on the `windows` crate and the `objc2` family, and already builds one cdylib,
`clowd_shell_ext`). The boundary is "GPU memory primitives", not rendering:

- **Device**: enumerate hardware adapters, create the D3D12 device and direct queue Skia needs, and
  a D3D11 device on the same adapter (LUID match) for the encoders. Returns raw `IUnknown*`
  handles because `GRD3DBackendContext` wants exactly those.
- **Render textures**: create a shareable BGRA texture (`D3D12_HEAP_FLAG_SHARED`,
  `ALLOW_SIMULTANEOUS_ACCESS`), hand back the D3D12 resource pointer for Skia's `GRBackendTexture`
  and the D3D11 twin (`OpenSharedResource1`) for FFmpeg's D3D11 frame. Same GPU memory, no copy.
- **Readback ring**: N slots, each with a readback-heap buffer and fence; `begin(slot)` records the
  texture-to-buffer copy after Skia's submit, `wait(slot)` blocks on the fence, `map(slot)` returns
  a pointer and pitch. Optionally the BGRA to NV12 conversion happens during map with SIMD, so the
  software encoder path never touches swscale.
- **Shared fence**: one D3D12 fence, opened as `ID3D11Fence`, so the D3D11 side can `Wait` before
  an encoder reads a texture and `Signal` when it is done, which is what makes texture reuse safe.
- **Metal twin**: IOSurface-backed `CVPixelBuffer` plus the `MTLTexture` view for Skia, exposed
  through the same ring API. `objc2-metal` / `objc2-core-video` make this typed. This is the part
  that should be written first, because it needs new native code either way.

What stays in C#: Skia via SkiaSharp, FFmpeg via FFmpeg.AutoGen, `RenderJob` orchestration, the
composer thread and its affinity rules, the model and the editor. `ISurfaceFactory`'s readback
contract (added by the 2026-09 pipeline work) is the interface the crate implements behind; the
migration is a swap behind that interface, not a redesign.

## Bindings

Keep the exported surface C-flat so the generated C# is boring:

- `extern "C"` functions, opaque handles (`*mut c_void` or `u64` ids), `i32` HRESULT-style
  returns, `out` parameters for results, a last-error string getter for diagnostics.
- No callbacks across the boundary except an optional log function.
- Generate the C# `DllImport` layer at build time with [csbindgen](https://github.com/Cysharp/csbindgen)
  (a `build.rs` step that emits a `.g.cs` into `Clowd.VideoSDK`). `interoptopus` is the
  alternative if richer types are wanted. Either way nobody hand-writes P/Invoke signatures.

Ownership rule, stated once and never bent: Rust owns everything it creates; C# receives borrowed
pointers valid until the matching `destroy` call. C# never calls `Release` on a pointer it got from
the crate, and the crate never frees something C# allocated. Skia's `GRContext` still gets disposed
before the device it was created over, exactly as today.

Thread affinity does not change: the composer thread owns the Skia context and calls the crate
from that thread; the crate does not spin threads of its own.

## Build and packaging

- Per-RID artifacts: `clowd_gpu.dll` (win-x64) and `libclowd_gpu.dylib` (osx-arm64), staged beside
  the executables the way `clowd_capture` and `clowd_ai` already are (see BUILDING.md and
  `.github/workflows/ci.yml`).
- Unlike those two, this is in-process. A crash in the crate takes the host down: acceptable for
  `Clowd.VideoRender.exe`, and no worse than SkiaSharp for the in-app preview, but it is a reason to
  keep the crate small and to test it with the D3D12 debug layer from Rust, independently of the app.
- The C# side keeps its CPU fallback (`CpuSurfaceFactory`) and must degrade to it when the dll is
  missing or fails to initialise, the same way the GPU backend already degrades today.

## Risks

- Two toolchains for one feature; reviewers of the render pipeline need both.
- Debugging a fence hang across a language boundary is harder than in one process image. Mitigate
  with a verbose diagnostic mode in the crate and a small standalone Rust harness that renders a
  synthetic ring without Skia.
- Skia's tracked resource state for a texture we also touch from our own command lists has to stay
  consistent. Whatever protocol the C# implementation settles on (simultaneous-access promotion and
  decay on a copy queue, or explicit barriers on the direct queue) carries over unchanged.

## Migration path

1. Land the C# implementation of pipelined readback, encoder selection and D3D12/D3D11 zero-copy
   (in progress as of this document). Its `ISurfaceFactory` readback contract is the target API.
2. When Metal zero-copy is scheduled, start the crate with the Metal ring, since that code has to be
   written from scratch anyway. Validate on a Mac with the software and VideoToolbox encoders.
3. Port the D3D12 device, texture, readback and D3D11 share code into the crate behind the same
   exported API; switch `GpuSurfaceFactory` to call it; delete `D3D12Backend.cs` and the vtable
   helpers.
4. Only then consider moving the BGRA to NV12 conversion into the crate, if profiling shows swscale
   on the software path is worth it.

## Related

- `BUILDING.md` for how the Rust crates are built and staged.
- `clowd_capture/src/gxi` for the existing typed D3D11 and Metal abstraction in the capture
  overlay; it is a reference for the Rust side, not a dependency (the video engine needs D3D12 for
  Skia, and the crate should stay independent of the capture binary).
- The scratchpad brief and measurements from the 2026-09-11 session live in the session, not the
  repo; the numbers that matter are repeated above.
