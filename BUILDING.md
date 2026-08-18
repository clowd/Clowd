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

The screen capture overlay is a separate Rust binary (`clowd_capture_wgpu`); the tray app / editor is the Avalonia project `Clowd.Ui`. For video recording, Clowd looks for an [obs-express](https://github.com/clowd/obs-express-rs) distribution in an `obs-express/` directory alongside the Clowd.Ui binary — see `.github/workflows/ci.yml` for how release packages are assembled.

AI video/audio effects (background matting, speech denoising) run in another Rust binary, `clowd_tractnni`, spawned per job by `Clowd.VideoSDK` — it embeds its ONNX models (see `clowd_tractnni/assets/models/README.md`; the GPL-3.0 RobustVideoMatting weights make that one crate GPL in an otherwise MIT repo, and release packages ship its `LICENSE`/`NOTICE.txt` beside the exe) but loads ONNX Runtime dynamically. CI downloads the official `microsoft/onnxruntime` release dylib into the package beside it; for local development point `ORT_DYLIB_PATH` (or `--ort-dylib`) at any onnxruntime 1.2x dylib, or place `onnxruntime.dll`/`libonnxruntime.dylib` beside the built exe. Without a runtime the binary exits with a named code and the app falls back to raw passthrough.

On Windows there is also `clowd_shell_ext`, a small COM dll (`IExplorerCommand`) that adds the "Upload with Clowd" entry to the Windows 11 context menu. It is registered as a *sparse* MSIX package: CI substitutes version/arch into `clowd_shell_ext/msix/AppxManifest.template.xml`, packs it with `makeappx` into `ClowdShellExt.msix` (manifest + logos only — the dll stays outside the package as external content), and release.yml signs the msix with Azure Trusted Signing before Velopack bundles it. At install time the app copies the dll to the install root and registers the msix with `Add-AppxPackage -ExternalLocation` (see `SparsePackageManager`). The installed dll is named for the crate's own version (`ClowdShellExt_1.0.0.dll`, independent of the app version) — **bump the `clowd_shell_ext` crate version whenever the extension changes**, so the update lands under a fresh filename that Explorer's lock on the old copy cannot block; unchanged versions are never re-copied. The crate compiles to an empty library on non-Windows so workspace-wide builds still work on macOS.

## Tests

```cmd
cargo test -p clowd_capture_wgpu
cargo test -p clowd_shell_ext
cargo test -p clowd_tractnni
dotnet test clowd_ui/Clowd.Shared.Tests/Clowd.Shared.Tests.csproj
dotnet test clowd_ui/Clowd.Drawing.Tests/Clowd.Drawing.Tests.csproj
```

The upload relay server (`clowd_server/`) is a separate Cloudflare Worker project with its own build and test setup — see `clowd_server/TESTING.md`.
