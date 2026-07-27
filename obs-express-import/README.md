# obs-express

A minimal, headless screen recorder backed by [libobs](https://github.com/obsproject/obs-studio) (the OBS Studio core). It records a screen region or a whole monitor straight to an MP4, driven entirely by command-line flags and a small line-oriented stdin/stdout protocol — no GUI, no config files, no OBS install required.

This is a Rust rewrite of [clowd/obs-express](https://github.com/clowd/obs-express) (originally C++). libobs 32.1.2 is built from source from the pinned `obs-studio` submodule and bundled next to the binary, so a release is self-contained.

## Features

- **Region or monitor capture** — record an arbitrary `X,Y,W,H` rectangle (which may span multiple displays) or a whole monitor by id/index. Defaults to the primary monitor.
- **Hardware or software H.264** — x264 by default; `--hw-accel` prefers a GPU encoder (NVENC → AMF → QSV on Windows, VideoToolbox on macOS) and transparently falls back to x264.
- **Multi-device audio** — any number of speaker (output) and microphone (input) devices, up to 8 total, mixed into the recording.
- **Programmatic control** — a parent process drives recording over stdin (`start` / `pause` / `quit`, per-device mute) and reads structured progress as one JSON object per line on stdout.
- **Aspect-preserving downscale** — cap output resolution with `--max-width` / `--max-height` without distorting the picture (never upscales).
- **Click highlight** — `--tracker` draws an expanding, fading circle at the pointer on every mouse click, in the recording only.
- **Cursor toggle** and a **paused-start** mode for building the pipeline ahead of time and starting instantly on command.
- **Cross-platform** — Windows (x64 / ARM64) and macOS (x64 / arm64).

## Installation

### Prebuilt releases

Each release publishes a zipped, self-contained bundle for every supported target on the [Releases](https://github.com/clowd/obs-express/releases) page:

- `obs-express-windows-x64`, `obs-express-windows-arm64`
- `obs-express-macos-x64`, `obs-express-macos-arm64`

Unzip and run `obs-express` in place — the bundled OBS runtime (plugins, data, and the FFmpeg/x264 libraries) lives alongside the executable and is fully relocatable.

On macOS the binaries are ad-hoc signed but not notarized, so the first launch may need:

```sh
xattr -dr com.apple.quarantine <unzipped-directory>
```

### Build from source

See [Building](#building) below.

## Usage

```
obs-express --output <FILE.mp4> [capture target] [options]
```

`--output` is required and must end in `.mp4` (its parent directory must already exist). If neither `--region` nor `--monitor` is given, the **primary monitor** is recorded.

```sh
# Record the primary monitor to recording.mp4
obs-express --output recording.mp4

# Record a 1280x720 region at the top-left, 60 fps, higher quality
obs-express --output clip.mp4 --region 0,0,1280,720 --fps 60 --crf 20

# Record monitor 0 with system audio and the default mic, hardware-encoded
obs-express --output clip.mp4 --monitor 0 --hw-accel --speaker default --microphone default
```

### Options

| Flag | Default | Description |
| --- | --- | --- |
| `--output <PATH>` | *(required)* | Destination file. Must end in `.mp4`; parent directory must exist. |
| `--region <X,Y,W,H>` | — | Capture rectangle in the platform capture coordinate space. `X`/`Y` may be negative (virtual desktop); `W`/`H` must be ≥ 2. Mutually exclusive with `--monitor`. |
| `--monitor <ID>` | — | Record a whole monitor by device id, alternate id, or 0-based index. Mutually exclusive with `--region`. |
| `--fps <INT>` | `30` | Output frame rate (≥ 1). |
| `--crf <0-51>` | `24` | Quality: x264 CRF / hardware CQP. Lower is higher quality. |
| `--max-width <INT>` | `0` | Downscale cap for width; `0` = off. |
| `--max-height <INT>` | `0` | Downscale cap for height; `0` = off. |
| `--hw-accel` | off | Prefer a hardware H.264 encoder; falls back to x264 if none is available. |
| `--low-cpu` | off | Use the x264 `ultrafast` preset instead of `veryfast`. |
| `--no-cursor` | off | Do not capture the mouse cursor. |
| `--tracker` | off | Highlight mouse clicks with an expanding, fading circle (see below). |
| `--tracker-color <R,G,B>` | `255,0,0` | Color of the click highlight; each component 0-255. |
| `--pause` | off | Build the pipeline, emit `initialized`, and wait for a stdin `start` before recording. |
| `--speaker <DEVICE>` | — | Output-capture (system audio) device id, or `default`. Repeatable. On macOS 13+ system audio is captured via ScreenCaptureKit: the device id is ignored (the flag only toggles system-audio capture on) and repeating the flag is rejected. |
| `--microphone <DEVICE>` | — | Input-capture (microphone) device id, or `default`. Repeatable. |

Downscaling preserves aspect ratio: the tightest of the two caps is applied once to both dimensions, and the output is never upscaled.

### Capture targets

A `--region` is composited from every monitor it intersects, so a rectangle can span two displays. Coordinates are in the platform capture space:

- **Windows** — physical pixels on the virtual desktop (`X`/`Y` can be negative for displays left of / above the primary).
- **macOS** — CoreGraphics points.

A `--monitor` value is matched, in order, against the monitor's stable device id, its alternate id (Windows GDI name / macOS `CGDirectDisplayID`), and finally as a 0-based index.

### Click highlight

`--tracker` adds a circle that flashes wherever a mouse button goes down and animates for 400 ms: it starts at a 20-unit diameter and 85% opacity, then expands to 80 units as it fades out. Holding a button pins the circle to the pointer and the fade starts on release. The highlight exists only in the recording — nothing is drawn on the real screen — and it is composited by libobs as an extra scene item on top of the captured displays, so it costs one texture draw per frame.

Its size adapts to the display the click happened on: on Windows it scales with that monitor's DPI, and on macOS it is sized in points (already density-independent) and mapped onto a Retina canvas along with everything else.

```sh
obs-express --output demo.mp4 --tracker --tracker-color 0,128,255
```

## Controlling a running recording

`obs-express` reads newline-delimited commands on **stdin**. The first whitespace-separated token is matched case-insensitively; unknown lines are logged to stderr and ignored.

| Command | Effect |
| --- | --- |
| `start` | Start recording (in `--pause` mode), or resume after `pause`. |
| `pause` | Pause recording. |
| `quit` / `q` | Stop the recording, flush the file, and exit. |
| `mute-speaker <N>` / `unmute-speaker <N>` | Mute/unmute speaker device `N` (0-based, in `--speaker` order). |
| `mute-mic <N>` / `unmute-mic <N>` | Mute/unmute microphone device `N` (0-based, in `--microphone` order). |

**EOF on stdin is treated as `quit`** — if the parent process dies, the pipe closes and the recording stops and flushes cleanly. `Ctrl+C` / `Ctrl+Break` / console-close on Windows and `SIGINT` / `SIGTERM` on POSIX behave the same way.

## Output protocol (stdout)

**stdout carries exactly one JSON object per line; all human-readable/log output goes to stderr.** Consumers should parse only lines that begin with `{`.

| Message | When |
| --- | --- |
| `{"type":"initialized"}` | Pipeline built and ready (emitted once at startup). |
| `{"type":"started_recording"}` | The output actually started rolling. |
| `{"type":"recording_paused"}` / `{"type":"recording_resumed"}` | In response to `pause` / `start`. |
| `{"type":"status","timeMs":..,"fps":..,"dropped":..,"droppedPerc":..}` | Once per second while recording and not paused. |
| `{"type":"levels","speaker":[..],"mic":[..]}` | Every 100 ms from `initialized` on (including the pre-start `--pause` wait), when at least one audio device is configured. Peak dBFS per device (in `--speaker` / `--microphone` order), floored at `-100.0`. |
| `{"type":"stopped_recording","code":..,"message":..,"error":..}` | Final line before exit. |

`status` fields: `timeMs` is elapsed recording time in milliseconds (excluding paused spans), `fps` is the measured frame rate over that clock, and `dropped` / `droppedPerc` report dropped frames. The final `stopped_recording.code` mirrors the OBS output stop code (`0` = success; negative values indicate invalid path, unsupported format, out of disk space, encoder error, etc.), with a human-readable `message`.

Example session (`--pause` mode), stdin on the left, stdout on the right:

```
                                {"type":"initialized"}
start                     ->
                                {"type":"started_recording"}
                                {"type":"status","timeMs":1000,"fps":30.0,"dropped":0,"droppedPerc":0.0}
                                {"type":"status","timeMs":2000,"fps":30.0,"dropped":0,"droppedPerc":0.0}
quit                      ->
                                {"type":"stopped_recording","code":0,"message":"Successfully stopped","error":null}
```

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Recording stopped successfully (or was cancelled before it started). |
| `1` | Recording or initialization failure. |
| `2` | Invalid command-line arguments. |

## Encoding

- **Video** — H.264 into an MP4 (`ffmpeg_muxer`). Software x264 by default (`veryfast`, or `ultrafast` with `--low-cpu`); `--hw-accel` selects the first available hardware encoder (Windows priority NVENC → AMF → QSV; macOS VideoToolbox) and falls back to x264 otherwise. `--crf` is passed through as the CRF (x264) or CQP (hardware) value.
- **Audio** — AAC at 128 kbps (`CoreAudio_AAC` on macOS when available, otherwise `ffmpeg_aac`), 44.1 kHz.

## Building

### Requirements

libobs is compiled from the `obs-studio` submodule (pinned to **32.1.2**), so a full native toolchain is needed:

- `git`, `cmake` (≥ 3.28), and a recent **Rust** toolchain (`cargo`)
- **Windows** — Visual Studio 2022 (the "Visual Studio 17 2022" generator) and LLVM/`libclang` (for `bindgen`; point `LIBCLANG_PATH` at it if not on `PATH`)
- **macOS** — full **Xcode** (not just the Command Line Tools — the Metal renderer and Swift are required)

### Steps

```sh
git clone --recursive https://github.com/clowd/obs-express.git
cd obs-express
./build.sh          # inits submodules, then `cargo build --release`
```

`build.sh` is a thin wrapper; you can also run the steps directly:

```sh
git submodule update --init --recursive
cargo build --release
```

The build script assembles a complete, runnable tree next to the binary:

- **Windows** — `obs-express.exe` plus `obs.dll`, the graphics/plugin DLLs, the FFmpeg & x264 runtime DLLs, `obs-plugins/`, and `data/` are copied into `target/release/`.
- **macOS** — `libobs.framework`, the Metal/OpenGL graphics modules, dependency dylibs, and the `.plugin` bundles are staged with `@executable_path`-relative rpaths into a relocatable bundle.

The resulting binary is `target/release/obs-express` (`.exe` on Windows).

### Tests

```sh
cargo test -p obs-express            # unit tests (region math, CLI, encoder selection, ...)
cargo test -p obs-express --test smoke -- --ignored   # end-to-end: records ~3s and validates the MP4
```

The smoke test is `--ignored` by default because it needs a real display and the assembled OBS runtime next to the binary.

### Environment overrides

The bundled layout is discovered automatically, but paths can be overridden: `OBS_PLUGIN_PATH`, `OBS_PLUGIN_DATA_PATH`, and `OBS_DATA_PATH` point libobs at plugin binaries, plugin data, and core data respectively; `OBS_VERSION_OVERRIDE` changes the version stamped into the OBS build.

## Project layout

The workspace is three crates:

| Crate | Role |
| --- | --- |
| `crates/obs-sys` | Raw FFI bindings to libobs (via `bindgen`); its build script compiles OBS from the submodule with CMake. |
| `crates/obs` | Safe, RAII Rust wrappers over the libobs FFI (context, sources, scenes, encoders, output, signals). |
| `crates/obs-express` | The recorder binary: CLI, region planning, encoder configuration, the command run loop, and platform (Windows/macOS) capture back-ends. |

## Releases & CI

Every push and pull request builds all four variants (Windows x64/ARM64, macOS x64/arm64) through a reusable GitHub Actions workflow. The manually-dispatched release workflow bumps the version, rebuilds every variant, and publishes them as zipped assets on a GitHub Release.

## License & credits

Licensed under the [GNU General Public License v2.0](LICENSE), matching [clowd/obs-express](https://github.com/clowd/obs-express) (the C++ original this is a rewrite of) and [OBS Studio](https://github.com/obsproject/obs-studio) / libobs, which this project links and is therefore bound by.
