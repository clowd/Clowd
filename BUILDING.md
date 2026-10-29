# Building Prisc

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A stable [Rust toolchain](https://rustup.rs)
- For video recording, the video editor, `Prisc.VideoRender` and the FFmpeg-gated tests: the native
  toolchain in [Recording stack (libobs)](#recording-stack-libobs) below (`cargo rec` stages FFmpeg
  8.1 beside `prisc_recorder`). Without it, set `PRISC_FFMPEG_PATH` to a directory holding the FFmpeg
  8.1 shared libraries, or `PRISC_HELPERS_DIR` to the directory an unpacked CI `recorder-<rid>`
  artifact sits in; see [The video editor's FFmpeg](#the-video-editors-ffmpeg).

## Build

```cmd
git clone https://github.com/priscapp/Prisc.git
cargo build -p prisc_capture
cargo build -p prisc_interop
dotnet build ui/Prisc.Ui/Prisc.Ui.csproj
```

`prisc_interop` is the native library Prisc.Ui and Prisc.VideoRender load in-process; neither starts
without it (see [Native interop (prisc_interop)](#native-interop-prisc_interop)).

A plain `cargo build` / `cargo test` covers the app crates (the workspace's `default-members`) and
stays fast. The recorder stack builds libobs with CMake and only builds when asked (`cargo rec`, see
below).

### Editors

rust-analyzer checks and runs build scripts with `--workspace` by default, which includes the
recorder stack: on a checkout without the `crates/obs-studio` submodule every check fails in the
`prisc_obs_sys` build script, and with it the editor starts the 20–40 minute libobs build in the
background and holds the `target/debug` lock while it runs. `.zed/settings.json` turns that off for
Zed. In VS Code, set the same two options in the workspace settings:

```json
{
  "rust-analyzer.check.workspace": false,
  "rust-analyzer.cargo.buildScripts.overrideCommand": [
    "cargo", "check", "--quiet", "--message-format=json", "--all-targets", "--keep-going"
  ]
}
```

Without `--workspace`, cargo at the workspace root covers only the app crates, so rust-analyzer has
no build-script output (the libobs bindings) for the recorder crates. When working on those, remove
both options (or add `"rust-analyzer.cargo.targetDir": true`, which gives the editor its own
target dir so it never blocks your own builds).

### Finding the helpers

Prisc.Ui spawns its helpers (`prisc_capture`, `prisc_scroll_driver`, `prisc_recorder`,
`prisc_share_region`, `prisc_vid2gif`, `prisc_ai`, `Prisc.VideoRender`) through one lookup,
`HelperLocator` (`ui/Prisc.Ipc`), with one override; the `prisc_interop` loader uses the same probe
(`NativeProbe`, `ui/Prisc.Interop`). For each file, the first hit wins:

1. each directory of `PRISC_HELPERS_DIR`, then its `recorder/` subdirectory. It is a path list,
   separated like `PATH` (`;` on Windows, `:` elsewhere),
2. the app directory, then its `recorder/` subdirectory (the release layout),
3. walking up to the workspace `Cargo.toml`, the newer of `target/debug` and `target/release` (the
   dev layout); `Prisc.VideoRender` is looked for in `ui/Prisc.VideoRender/bin/{Debug,Release}/net10.0`.

A Prisc.Ui built inside the checkout needs no variable. One built to an out-of-tree `OutDir` finds no
`Cargo.toml` on the way up and no single directory that holds both the Rust helpers and
`Prisc.VideoRender`, so set the list to both:

```sh
PRISC_HELPERS_DIR="<repo>/target/debug:<repo>/ui/Prisc.VideoRender/bin/Debug/net10.0"
```

(`;` instead of `:` on Windows.) The only other variable is `PRISC_FFMPEG_PATH`, which names the
FFmpeg library directory rather than a helper (below). The stdio contract every helper speaks is
`crates/prisc_capture/HELPER_PROTOCOL.md`.

### The video editor's FFmpeg

The editor, the playback engine and `Prisc.VideoRender` all load FFmpeg 8.1 natively (avcodec 62 /
avformat 62 / avutil 60 / swscale 9 / swresample 6) and refuse to start without it:
`FFmpegLoader.TryInitialize` asserts those majors. A release build takes them from the bundled
recorder payload; `FFmpegLoader.FindBesideRecorder` is the one resolver every caller goes through,
probing in order:

1. `PRISC_FFMPEG_PATH`, if set: always wins,
2. the recorder binary's own directory (the Windows layout, and every dev layout),
3. its `Frameworks/` subdirectory (the macOS bundle layout).

The recorder binary itself (`prisc_recorder`) is found like every other helper (see
[Finding the helpers](#finding-the-helpers)). Building the recorder with `cargo rec` (or `cargo recr`
for release) is therefore all a dev checkout needs on either platform: its build scripts stage the
FFmpeg libraries into the profile dir beside the binary, so the dev FFmpeg directory is
`target/<profile>`. Failing that, point `PRISC_HELPERS_DIR` at the directory an unpacked CI
`recorder-<rid>` artifact sits in (the one holding `recorder/`).

Roughly a hundred tests — every encode, render, composition-player, filmstrip, waveform and AI
sidecar test — skip rather than fail when FFmpeg is missing, so check for `Skipped: 0` before
believing a green run.

The screen capture overlay is a separate Rust binary (`prisc_capture`); the tray app / editor is the Avalonia project `Prisc.Ui`. For video recording, Prisc runs the recorder (`prisc_recorder`, built from `crates/prisc_recorder` in this repository) from a `recorder/` directory alongside the Prisc.Ui binary — see `.github/workflows/ci.yml` (which merges the outputs of `build-ui.yml`, `build-rust.yml` and `build-recorder.yml` in the same folder) for how release packages are assembled.

The video editor runs on Windows and macOS alike. Platform-specific pieces: the frame compositor
renders on Direct3D 12 or Metal (`GpuSurfaceFactory`, with a CPU fallback either way), video decode
uses D3D11VA or VideoToolbox and falls back to software, and the preview's audio master clock is
driven by WASAPI or a CoreAudio default-output AudioUnit (`AudioOutputFactory`) — the clock backs
off by whatever latency the device actually reports, which is ~100 ms on WASAPI and ~14 ms on
CoreAudio. The voice recorder's microphone capture is the same split: WASAPI shared mode or a
CoreAudio input AudioQueue behind one `MicCapture` abstraction, with every take normalized to a
48 kHz mono 16-bit WAV by `MicRecorder` whatever the device delivers.

On-device AI — text recognition for the capture overlay, plus the video/audio effects (background matting, speech denoising) — runs in another Rust binary, `prisc_ai`: the overlay spawns it per OCR press (`prisc_ai ocr`, see `crates/prisc_capture/CAPTURE_PROTOCOL.md` §3) and `Prisc.VideoSDK` spawns it per effect job. It embeds its ONNX models (see `crates/prisc_ai/assets/models/README.md`; the GPL-3.0 RobustVideoMatting weights make that one crate GPL-3.0, and release packages ship its `LICENSE`/`NOTICE.txt` beside the exe). ONNX Runtime is linked statically by the [ort](https://ort.pyke.io) crate, whose build downloads pyke's prebuilt runtime for the target (first build needs the network; verified against the hashes in `ort-sys`'s `dist.tsv`). Hardware execution providers are on by default per platform — DirectML on Windows (any DX12 GPU, no user-installed runtime; deliberately not CUDA, which would require the user's own CUDA toolkit), CoreML on Apple Silicon — and fall back to the CPU at runtime. One exception, spelled out at `CoreMl::Unsupported` in `crates/prisc_ai/src/main.rs`: the 48 kHz speech enhancer stays off CoreML, which miscompiles DPDFNet's recurrent state to the wrong rank and then fails every inference. That is a provider getting a graph wrong rather than a machine lacking one, so there is nothing to detect at registration and nothing to fall back from; DirectML compiles the same model correctly, so it costs Windows nothing. On Windows on Windows `DirectML.dll` must sit beside the exe, where cargo stages it and CI packages it from. Upstream ONNX Runtime no longer supports macOS x86_64, so the crate is not built on the osx-x64 leg; the app grays the AI effects out on Intel Macs and the overlay reports OCR as unavailable there.

On Windows there is also `prisc_shell_ext`, a small COM dll (`IExplorerCommand`) that adds the "Upload with Prisc" entry to the Windows 11 context menu. It is registered as a *sparse* MSIX package: CI substitutes version/arch into `crates/prisc_shell_ext/msix/AppxManifest.template.xml`, packs it with `makeappx` into `PriscShellExt.msix` (manifest + logos only — the dll stays outside the package as external content), and release.yml signs the msix with Azure Trusted Signing before Velopack bundles it. At install time the app copies the dll to the install root and registers the msix with `Add-AppxPackage -ExternalLocation` (see `SparsePackageManager`). The installed dll is named for the crate's own version (`PriscShellExt_<version>.dll`, e.g. `PriscShellExt_1.0.4.dll`, independent of the app version) — **bump the `prisc_shell_ext` crate version whenever the extension changes**, so the update lands under a fresh filename that Explorer's lock on the old copy cannot block; unchanged versions are never re-copied. The crate compiles to an empty library on non-Windows so workspace-wide builds still work on macOS.

### Global hotkeys (prisc_hotkeys)

Global hotkeys, gesture recording in the hotkey settings and the Draw on Screen keystroke overlay all
go through one keyboard hook in the `prisc_hotkeys` crate (`WH_KEYBOARD_LL` on Windows, a
`CGEventTap` on macOS, which needs the Accessibility permission; there is no Linux backend yet). It
is a plain Rust library: the capturer links it, and Prisc.Ui reaches it through `prisc_interop`'s
`prisc_hotkeys_*` and `prisc_gesture_*` exports. The C# key enum
(`ui/Prisc.Shared/Hotkeys/HotkeyKey.g.cs`), the status codes (`HotkeyErrorCodes.g.cs`) and the
keystroke overlay's keycap legends are generated from the crate's tables by `cargo codegen`.

### Native interop (prisc_interop)

C# reaches native code only through `crates/prisc_interop`, one library loaded in-process by
Prisc.Ui, Prisc.VideoRender and the test hosts (`prisc_interop.dll` / `libprisc_interop.dylib`,
shipped beside the Prisc.Ui executable). Its C ABI is a thin layer over the shared Rust crates
(`prisc_platform`, `prisc_rust_core`, `prisc_hotkeys`, `prisc_ipc`); the rules it keeps are in
`crates/prisc_interop/README.md`. The C# side, `ui/Prisc.Interop`, is generated plus a thin
hand-written wrapper per area.

Build it before running Prisc.Ui or any test project from a checkout:

```sh
cargo build -p prisc_interop
```

The loader finds it like a helper (above), with two additions: `PRISC_INTEROP_PATH` names the file
outright (set but not an existing file means unavailable, no fallback), and the dev walk-up also
looks in `target/{debug,release}/deps/`, where building a dependent crate leaves it. A Debug build of
`ui/Prisc.Interop` warns when no library is found. A library built from different `src/ffi` sources
than the C# bindings is refused at load (the ABI hash, README section E): run `cargo codegen`, then
build again.

Everything generated from Rust into C# comes from one command in `crates/prisc_codegen` (the
`DllImport`s, structs and enums of `ui/Prisc.Interop/Generated`, the ABI hash, the shared
environment variable names, the helper message catalogs and names, the hotkey tables, and the
contract constants shared with the Rust helpers):

```sh
cargo codegen            # rewrite every generated file
cargo codegen --check    # list what is stale, change nothing
cargo test -p prisc_codegen   # the gate: fails on a stale or orphaned file and on any crate rule
```

Edit the Rust source and regenerate; never edit a `.g.cs` file by hand. Hand-written native interop
anywhere else in `ui/` fails `tools/check-native-interop.sh`; the few places that keep it (the loader,
the callback thunks, FFmpeg's buffer callbacks) are listed in
`tools/native-interop-allowlist/permanent.txt`.

While Prisc.Ui, Prisc.VideoRender or a test host runs it holds the library open, so relinking it
fails on Windows (LNK1104). Close them (and the standby `prisc_capture`) before rebuilding.

The Windows half compiles on any machine with the target installed, which is how to check a change
to it from a Mac:

```sh
rustup target add x86_64-pc-windows-msvc
cargo clippy -p prisc_interop --all-targets --target x86_64-pc-windows-msvc -- -D warnings
```

The Avalonia previewer loads Prisc.Ui's views in its own process, which needs the library too for
any view that formats a gesture or reads an OS value, so build it before previewing those.

## Recording stack (libobs)

Screen recording runs in a separate process, `prisc_recorder`, a Rust front end over libobs. It and
its helpers are GPL-2.0 (see `crates/prisc_recorder/LICENSE`) and only ever run as separate
processes of the MIT app. libobs is compiled from the `crates/obs-studio` submodule (pinned to **32.2.2**),
so this part needs a full native toolchain:

- the submodule, recursively: `git submodule update --init --recursive` (or clone with
  `--recursive`). The disabled `obs-browser` / `obs-websocket` submodule dirs must exist, or their
  CMakeLists fail.
- `cmake` ≥ 3.28
- **Windows** — Visual Studio 2022 or 2026 with the C++ workload (the newest installed one picks the
  CMake generator; set `OBS_CMAKE_GENERATOR` to override), and LLVM/`libclang` for `bindgen` (set
  `LIBCLANG_PATH`, e.g. `C:\Program Files\LLVM\bin`, if it is not on `PATH`)
- **macOS** — full **Xcode 26.5+** (not just the Command Line Tools — the Metal renderer and Swift
  are required)
- **Linux** (x86_64 and aarch64) — build in the reference image, `tools/linux-build/Dockerfile`. It
  is a manylinux_2_34 (AlmaLinux 9, glibc 2.34) base with clang/`libclang` (for `bindgen`), `ninja`,
  `nasm`, `patchelf`, Rust, and the development packages of the system libraries the bundle links.
  CI builds in the same image, and building there is what keeps the result portable to every glibc
  2.34+ distribution:

  ```sh
  docker build -t prisc-recorder-linux tools/linux-build
  tools/linux-build/run-in-image.sh cargo build --release -p prisc_recorder -p prisc_vid2gif
  ```

  A native build on another distribution also works, given the same tools and `-dev` packages; the
  Dockerfile is the list. Its output then needs that distribution's glibc or newer. Do **not**
  install the system FFmpeg development packages (`libav*-dev`). The build downloads a pinned FFmpeg
  8.1 shared build (BtbN FFmpeg-Builds, SHA-256 verified), builds pinned x264 and Mbed TLS from
  source, and fetches a pinned SIMDe, all into `crates/obs-studio/.deps`, so the first build needs access
  to github.com. libobs, every plugin and `prisc_vid2gif` share that one FFmpeg.
  `prisc_share_region` is not built on Linux (there is no Linux UI), so Linux builds name
  `-p prisc_recorder -p prisc_vid2gif` rather than using `cargo rec`.

Then, from the repository root:

```sh
cargo rec                         # = cargo build -p prisc_recorder -p prisc_share_region -p prisc_vid2gif
cargo recr                        # the same, --release
```

The first build compiles libobs and takes 20–40 minutes; later builds reuse it. OBS derives its
version from `git describe --tags`, and a submodule checkout usually has no tags, so the build
passes the version itself (`OBS_VERSION_OVERRIDE`, default the pinned 32.2.2, sets it). On
Windows, OBS's child configures for the 32-bit helpers ignore that and still run `git describe`,
so before configuring, the build script gives HEAD a local lightweight tag of that version when it
has none. No manual tagging is needed, in CI or locally.

The build tree records a successful configure, together with the checkout and the submodule commit
it was for, so a failed configure, a moved clone or a submodule bump configures again on the next
build. If a libobs build still fails after you fixed the cause, delete `target/obs-<arch>` and
`crates/obs-studio/build_x86` (plus `build_x64` on Windows ARM64) and build again.

`OBS_BUILD_DIR` moves the libobs build tree (default `target/obs-<arch>` on Windows and Linux; on
macOS it lives in the `prisc_obs_sys` build script's `OUT_DIR`). Several worktrees can share one
tree only with two caveats. The tree keeps compiling the `crates/obs-studio` sources of the
worktree that configured it, so that worktree has to stay in place and on the same OBS commit. And
only that configure downloads the OBS deps, so every other worktree needs its
`crates/obs-studio/.deps` pointed at the first one's before its first build (a directory junction
on Windows, `mklink /J crates\obs-studio\.deps <first>\crates\obs-studio\.deps`, or a symlink
elsewhere). A shared tree that does not match the checkout is never wiped: the build stops and
names it instead.

The build scripts stage the runtime next to the binaries:

- **Windows** — `prisc_recorder.exe`, `prisc_share_region.exe` and `prisc_vid2gif.exe` plus
  `obs.dll`, the graphics/plugin DLLs, `obs-ffmpeg-mux.exe`, the FFmpeg & x264 runtime DLLs,
  `obs-plugins/` and `data/` end up in `target/<profile>/`.
- **Linux** — like Windows: `libobs.so.30`, `libobs-opengl.so.30`, the FFmpeg `.so` files,
  `obs-ffmpeg-mux`, `obs-plugins/*.so` and `data/` are copied into `target/<profile>/`, each with an
  `$ORIGIN`-relative RUNPATH. The executables also carry absolute RUNPATHs into the build tree, so
  cargo's test binaries resolve. `tools/linux-build/stage-bundle.sh` removes those for the bundle and
  checks it, and `tools/linux-build/verify-bundle.sh` runs it end to end under Xvfb; CI runs both.
  `build.sh` stages through `stage-bundle.sh` too, so a local Linux package build needs `patchelf`
  and binutils (`readelf`, `objdump`) and is held to the same rules, except that the glibc ceiling
  is the build machine's own. A native build that links `libstdc++` or a system FFmpeg, x264 or
  Mbed TLS fails there rather than producing a bundle.
- **macOS** — the binaries link `libobs.framework`, the graphics modules and the plugins straight
  out of the OBS build tree (absolute rpaths), and the FFmpeg/x264 dependency dylibs are copied into
  `target/<profile>/` (symlinked aliases preserved, each given an `@loader_path` rpath and ad-hoc
  re-signed), so that, as on Windows, the profile dir holds a loadable FFmpeg runtime. The
  self-contained, relocatable `recorder/` folder (framework + graphics modules + those dylibs +
  `.plugin` bundles, with `@executable_path/Frameworks` rpaths) is assembled by the stage step in
  `.github/workflows/build-recorder.yml`.

The bundled layout is discovered automatically, but libobs's paths can be overridden:
`OBS_PLUGIN_PATH`, `OBS_PLUGIN_DATA_PATH` and `OBS_DATA_PATH` point it at plugin binaries, plugin
data and core data respectively.

Tests (on Windows, put `target/release` on `PATH` first: the test executables need the staged
runtime DLLs):

```sh
cargo test --release -p prisc_recorder -p prisc_obs_platform -p prisc_share_region
cargo test --release -p prisc_vid2gif
cargo test --release -p prisc_ffmpeg_sys
cargo test --release -p prisc_recorder --test smoke -- --ignored   # end-to-end: records ~3 s and validates the MP4
```

The smoke test is `--ignored` by default because it needs a real display and the assembled OBS
runtime next to the binary. On Linux it needs an X server, e.g.
`xvfb-run -a -s "-screen 0 1280x720x24 +extension RANDR" cargo test --release -p prisc_recorder --test smoke -- --ignored`
(on a blank Xvfb screen the recordings compress below the tests' size threshold, so put something
moving on it, such as `glxgears`).

| Crate | Role |
| --- | --- |
| `crates/prisc_obs_sys` | Raw FFI bindings to libobs (via `bindgen`); its build script compiles OBS from the submodule with CMake (`links = "obs"`). |
| `crates/prisc_obs` | Safe, RAII Rust wrappers over the libobs FFI (context, sources, scenes, encoders, output, signals). |
| `crates/prisc_obs_platform` | Shared platform layer: monitor enumeration, OBS paths, display-capture settings and region math, used by the recorder and the share-region helper. |
| `crates/prisc_ffmpeg_sys` | Raw FFI bindings to the FFmpeg libraries the recorder ships (`links = "ffmpeg"`). |
| `crates/prisc_build_support` | Build-script helpers (the pinned Linux FFmpeg/x264/Mbed TLS runtime). |
| `crates/prisc_recorder` | The recorder binary: CLI, region planning, encoder configuration, the command run loop, and the platform capture back-ends. |
| `crates/prisc_share_region` | Mirrors a screen region into an ordinary window for meeting apps that can only share a whole screen or one window. Windows and macOS only. |
| `crates/prisc_vid2gif` | Converts a video into an optimized GIF by linking the bundled FFmpeg libraries. |

## Tests

```cmd
cargo test -p prisc_platform
cargo test -p prisc_interop
cargo test -p prisc_codegen
cargo test -p prisc_ipc
cargo test -p prisc_rust_core
cargo test -p prisc_hotkeys
cargo test -p prisc_capture
cargo test -p prisc_scroll_driver
cargo test -p prisc_shell_ext
cargo test -p prisc_ai
cargo build -p prisc_interop
dotnet test ui/Prisc.Interop.Tests/Prisc.Interop.Tests.csproj
dotnet test ui/Prisc.Ipc.Tests/Prisc.Ipc.Tests.csproj
dotnet test ui/Prisc.VideoSDK.Tests/Prisc.VideoSDK.Tests.csproj
dotnet test ui/Prisc.Shared.Tests/Prisc.Shared.Tests.csproj
dotnet test ui/Prisc.Drawing.Tests/Prisc.Drawing.Tests.csproj
```

Tests that need `prisc_interop` skip when it is missing; set `PRISC_INTEROP_REQUIRED=1` (CI does)
to make them fail instead. Prisc.Ui has a probe of every interop wrapper and kept facade,
`Prisc.Ui --interop-probe <out.json>`, which CI runs on both OSes.

The recorder crates' tests are listed under [Recording stack (libobs)](#recording-stack-libobs).

The upload relay server lives in its own repository, [priscapp/Prisc.Server](https://github.com/priscapp/Prisc.Server).
