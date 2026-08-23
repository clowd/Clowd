# Building Clowd

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A stable [Rust toolchain](https://rustup.rs)

## Build

```cmd
git clone https://github.com/clowd/Clowd.git
cargo build -p clowd_capture_wgpu
dotnet build clowd_ui/Clowd.Ui/Clowd.Ui.csproj
```

### The video editor's FFmpeg

The editor, the playback engine and `Clowd.VideoRender` all load FFmpeg 7.1 natively (avcodec 61 /
avformat 61 / avutil 59 / swscale 8 / swresample 5) and refuse to start without it —
`FFmpegLoader.TryInitialize` asserts those majors. A release build takes them from the bundled
obs-express payload; `ObsBinaryLocator.ResolveFFmpegDirectory` is the one resolver every caller
goes through, probing in order:

1. `CLOWD_FFMPEG_PATH`, if set — always wins,
2. the obs-express binary's own directory (the Windows layout),
3. its `Frameworks/` subdirectory (the macOS bundle layout).

The obs-express binary itself is found via `CLOWD_OBS_EXPRESS_PATH`, an `obs-express/` directory
beside the app (the release layout), or a sibling `obs-express-rs` checkout's
`target/{debug,release}` (the dev layout). Building that sibling is therefore all a dev checkout
needs on either platform: its `build.rs` stages the FFmpeg libraries into the profile dir beside
the binary, on macOS as of obs-express-rs v0.7.0. Failing that, point `CLOWD_OBS_EXPRESS_PATH` at
an unpacked release payload.

Roughly a hundred tests — every encode, render, composition-player, filmstrip, waveform and AI
sidecar test — skip rather than fail when FFmpeg is missing, so check for `Skipped: 0` before
believing a green run.

The screen capture overlay is a separate Rust binary (`clowd_capture_wgpu`); the tray app / editor is the Avalonia project `Clowd.Ui`. For video recording, Clowd looks for an [obs-express](https://github.com/clowd/obs-express-rs) distribution in an `obs-express/` directory alongside the Clowd.Ui binary — see `.github/workflows/ci.yml` for how release packages are assembled.

The video editor runs on Windows and macOS alike. Platform-specific pieces: the frame compositor
renders on Direct3D 12 or Metal (`GpuSurfaceFactory`, with a CPU fallback either way), video decode
uses D3D11VA or VideoToolbox and falls back to software, and the preview's audio master clock is
driven by WASAPI or a CoreAudio default-output AudioUnit (`AudioOutputFactory`) — the clock backs
off by whatever latency the device actually reports, which is ~100 ms on WASAPI and ~14 ms on
CoreAudio.

On-device AI — text recognition for the capture overlay, plus the video/audio effects (background matting, speech denoising) — runs in another Rust binary, `clowd_ai`: the overlay spawns it per OCR press (`clowd_ai ocr`, see `clowd_capture/CAPTURE_PROTOCOL.md` §3) and `Clowd.VideoSDK` spawns it per effect job. It embeds its ONNX models (see `clowd_ai/assets/models/README.md`; the GPL-3.0 RobustVideoMatting weights make that one crate GPL in an otherwise MIT repo, and release packages ship its `LICENSE`/`NOTICE.txt` beside the exe). ONNX Runtime is linked statically by the [ort](https://ort.pyke.io) crate, whose build downloads pyke's prebuilt runtime for the target (first build needs the network; verified against the hashes in `ort-sys`'s `dist.tsv`). Hardware execution providers are on by default per platform — DirectML on Windows (any DX12 GPU, no user-installed runtime; deliberately not CUDA, which would require the user's own CUDA toolkit), CoreML on Apple Silicon — and fall back to the CPU at runtime. One exception, spelled out at `CoreMl::Unsupported` in `clowd_ai/src/main.rs`: the 48 kHz speech enhancer stays off CoreML, which miscompiles DPDFNet's recurrent state to the wrong rank and then fails every inference. That is a provider getting a graph wrong rather than a machine lacking one, so there is nothing to detect at registration and nothing to fall back from; DirectML compiles the same model correctly, so it costs Windows nothing. On Windows on Windows `DirectML.dll` must sit beside the exe, where cargo stages it and CI packages it from. Upstream ONNX Runtime no longer supports macOS x86_64, so the crate is not built on the osx-x64 leg; the app grays the AI effects out on Intel Macs and the overlay reports OCR as unavailable there.

On Windows there is also `clowd_shell_ext`, a small COM dll (`IExplorerCommand`) that adds the "Upload with Clowd" entry to the Windows 11 context menu. It is registered as a *sparse* MSIX package: CI substitutes version/arch into `clowd_shell_ext/msix/AppxManifest.template.xml`, packs it with `makeappx` into `ClowdShellExt.msix` (manifest + logos only — the dll stays outside the package as external content), and release.yml signs the msix with Azure Trusted Signing before Velopack bundles it. At install time the app copies the dll to the install root and registers the msix with `Add-AppxPackage -ExternalLocation` (see `SparsePackageManager`). The installed dll is named for the crate's own version (`ClowdShellExt_1.0.0.dll`, independent of the app version) — **bump the `clowd_shell_ext` crate version whenever the extension changes**, so the update lands under a fresh filename that Explorer's lock on the old copy cannot block; unchanged versions are never re-copied. The crate compiles to an empty library on non-Windows so workspace-wide builds still work on macOS.

## Tests

```cmd
cargo test -p clowd_capture_wgpu
cargo test -p clowd_shell_ext
cargo test -p clowd_ai
dotnet test clowd_ui/Clowd.VideoSDK.Tests/Clowd.VideoSDK.Tests.csproj
dotnet test clowd_ui/Clowd.Shared.Tests/Clowd.Shared.Tests.csproj
dotnet test clowd_ui/Clowd.Drawing.Tests/Clowd.Drawing.Tests.csproj
```

The upload relay server (`clowd_server/`) is a separate Cloudflare Worker project with its own build and test setup — see `clowd_server/TESTING.md`.
