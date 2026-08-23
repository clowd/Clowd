# obs-express

A minimal, headless screen recorder backed by [libobs](https://github.com/obsproject/obs-studio) (the OBS Studio core). It records a screen region or a whole monitor straight to an MP4, driven entirely by command-line flags (plus an optional JSON settings file) and a small line-oriented stdin/stdout protocol — no GUI, no OBS install required.

This is a Rust rewrite of [clowd/obs-express](https://github.com/clowd/obs-express) (originally C++). libobs 32.1.2 is built from source from the pinned `obs-studio` submodule and bundled next to the binary, so a release is self-contained.

## Features

- **Region or monitor capture** — record an arbitrary `X,Y,W,H` rectangle (which may span multiple displays) or a whole monitor by id/index. Defaults to the primary monitor.
- **Hardware or software H.264** — x264 by default; `--hw-accel` prefers a GPU encoder (NVENC → AMF → QSV on Windows, VideoToolbox on macOS) and transparently falls back to x264.
- **Multi-device audio** — any number of speaker (output) and microphone (input) devices: up to 8 total mixed into one audio track, or up to 6 on separate tracks with `--multi-track`.
- **Multi-track recording** — `--multi-track` writes every stream to its own track in one MP4: video track 0 = clean screen, video track 1 = webcam, and one audio track per capture device. A screen recording with a webcam, a speaker and a microphone is a 4-track file, ready for picture-in-picture compositing and per-source audio mixing at edit time.
- **Webcam second track** — `--webcam <id>` (requires `--multi-track`) records a camera — DirectShow on Windows, AVFoundation on macOS — as video track 1; `--list-cameras` enumerates the available devices.
- **Programmatic control** — a parent process drives recording over stdin (`start` / `pause` / `quit`, per-device mute) and reads structured progress as one JSON object per line on stdout.
- **Live reconfiguration** — all tunables (fps, quality, encoder, resolution cap, cursor, tracker, audio devices) can be supplied as a JSON file via `--settings` and re-applied at runtime with the stdin `configure` command — in `--pause` mode the whole pipeline is rebuilt in place, no process restart needed.
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

The bundled FFmpeg/x264 libraries are also usable on their own (e.g. by a host that uses the FFmpeg C API in-process): on Windows they are ordinary DLLs next to the executable (load them from that directory, e.g. via `AddDllDirectory`/`LOAD_WITH_ALTERED_SEARCH_PATH`), and on macOS the dylibs in `Frameworks/` carry an `@loader_path` rpath so they can be `dlopen`ed directly by any program as long as they stay together. On macOS, unzip with a tool that restores symlinks and modes (`unzip`, `ditto -x -k`, `tar`): the versioned aliases (`libavcodec.61.dylib`) are symlinks and the executables rely on their execute bits.

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

# Four separate tracks: screen, webcam, speaker, microphone
obs-express --output clip.mp4 --multi-track --webcam "$(obs-express --list-cameras | jq -r .cameras[0].id)" \
            --speaker default --microphone default
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
| `--multi-track` | off | Give every stream its own track (OBS's hybrid MP4 output): video track 0 = screen, video track 1 = webcam, and one audio track per `--speaker` / `--microphone` device — speakers first, in the order given, at most 6 audio tracks. Without it the recording uses the single-track muxer: one video track and all audio mixed into one track, and `--webcam` is rejected. |
| `--webcam <ID>` | — | Record the given camera as a second video track (track 0 = screen, track 1 = webcam, ≤ 1280x720, x264 CRF). Requires `--multi-track`. `ID` is a device id exactly as printed by `--list-cameras`. The camera's built-in microphone is never recorded — use `--microphone` for that. |
| `--list-cameras` | — | Enumerate cameras (DirectShow on Windows, AVFoundation on macOS): prints exactly one JSON line `{"type":"cameras","cameras":[{"id":..,"name":..}]}` on stdout and exits 0 (`{"type":"error","message":..}` and exit 1 on failure). Mutually exclusive with all recording flags; `--output` is not required. |
| `--speaker-volume-compensation` | off | Windows: boost speaker capture to undo the system master volume when the audio device applies it in software. On such devices (no hardware volume control — common for USB DACs) the loopback stream Windows hands to recorders is already attenuated by the volume slider, so recordings sound quieter than the played content did. Devices with hardware volume are detected and left untouched; no-op on macOS. Volume changes made while recording are tracked within ~100 ms; the boost is capped at +30 dB. |
| `--settings <FILE.json>` | — | Read the tunables from a JSON file instead of individual flags (see below). Conflicts with every flag it replaces: `--fps`, `--crf`, `--max-width`, `--max-height`, `--hw-accel`, `--low-cpu`, `--no-cursor`, `--tracker`, `--tracker-color`, `--speaker`, `--microphone`, `--speaker-volume-compensation`. |

Downscaling preserves aspect ratio: the tightest of the two caps is applied once to both dimensions, and the output is never upscaled.

### Settings file

`--settings` points at a JSON object holding the tunable options — everything except the capture target, `--output`, and `--pause`, which stay CLI-only. The same file format is re-read by the runtime `configure` command (see below), which is the point of it: a parent process can rewrite the file and re-apply it without restarting the recorder.

```json
{
  "fps": 30,
  "crf": 24,
  "max_width": 0,
  "max_height": 0,
  "hw_accel": false,
  "low_cpu": false,
  "cursor": true,
  "tracker": false,
  "tracker_color": "255,0,0",
  "speakers": ["default"],
  "microphones": [],
  "speaker_volume_compensation": false,
  "webcam_device": ""
}
```

`webcam_device` is the settings-file equivalent of `--webcam` (a device id exactly as printed by `--list-cameras`; empty = no webcam). If the `--webcam` flag is also given it wins and pins the device for the process lifetime. Either way it requires `--multi-track` — a single-track recording carries one video track, so a webcam requested without that flag is rejected with a clear error (exit 2 at startup, `configure_error` at runtime).

Every field is optional and defaults to the corresponding flag's default; note `cursor` has positive polarity ("capture the cursor", default `true`) where the flag is `--no-cursor`. A missing field always means the *default* — never "keep the current value" — so a file resolves to the same effective config whether it is read at startup or by a later `configure`. Unknown fields are ignored. Values are validated like the flags they replace (bad values fail startup with exit 2, or ack `configure_error` at runtime).

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
| `configure <PATH>` | Re-read a settings file (same format as `--settings`) and apply it. The path is the rest of the line, unquoted — spaces allowed. Always answered with exactly one `configure_applied` or `configure_error` on stdout. |

### `configure`

What a `configure` can change depends on whether recording has started:

- **Before `start`** (the `--pause` wait) — everything applies: fps and the resolution caps rebuild the video pipeline in place, the encoder is recreated for `crf` / `hw_accel` / `low_cpu` changes, audio device lists are rebuilt (the `levels` arrays and mute indices follow the new lists; rebuilt devices come back unmuted, and with `--multi-track` the audio *tracks* are re-laid-out to match), the webcam chain is added/removed/rebuilt when `webcam_device` changes (unless `--webcam` pinned it), and cursor/tracker/color update directly. Repeatable — any number of `configure`s may precede `start`.
- **After `start`** — only the live-safe keys apply: `cursor`, `tracker`, `tracker_color`, and `speaker_volume_compensation`. Every other key that differs from the active config (including `webcam_device`) is left untouched and reported in the ack's `ignored_keys`; the recording is never disturbed.

On failure the ack is `configure_error` with a `message` and a `fatal` flag. `fatal:false` means the pipeline still matches the config from before the command (bad file, invalid values, a device that failed to open — all validated before anything is committed); `fatal:true` means a mid-rebuild failure may have left the pipeline unusable and the parent should restart the process. Mute state for *unchanged* devices survives a reconfigure; per-device mutes always address the current lists.

**EOF on stdin is treated as `quit`** — if the parent process dies, the pipe closes and the recording stops and flushes cleanly. `Ctrl+C` / `Ctrl+Break` / console-close on Windows and `SIGINT` / `SIGTERM` on POSIX behave the same way.

## Output protocol (stdout)

**stdout carries exactly one JSON object per line; all human-readable/log output goes to stderr.** Consumers should parse only lines that begin with `{`.

| Message | When |
| --- | --- |
| `{"type":"initialized"}` | Pipeline built and ready (emitted once at startup). |
| `{"type":"started_recording","tracks":{..}}` | The output actually started rolling. |
| `{"type":"recording_paused"}` / `{"type":"recording_resumed"}` | In response to `pause` / `start`. |
| `{"type":"status","timeMs":..,"fps":..,"dropped":..,"droppedPerc":..}` | Once per second while recording and not paused. |
| `{"type":"levels","speaker":[..],"mic":[..]}` | Every 100 ms from `initialized` on (including the pre-start `--pause` wait), when at least one audio device is configured. Peak dBFS per device (in `--speaker` / `--microphone` order), floored at `-100.0`. |
| `{"type":"configure_applied","ignored_keys":[..]}` | A `configure` succeeded. `ignored_keys` lists the non-live keys that differed but were skipped because recording had already started (empty before `start`). |
| `{"type":"configure_error","message":..,"fatal":..}` | A `configure` failed; nothing applied unless `fatal` is `true`, in which case the pipeline may be broken and the process should be restarted. |
| `{"type":"stopped_recording","code":..,"message":..,"error":..,"tracks":{..}}` | Final line before exit. |

`tracks` describes the streams of the mp4:

```json
{
  "screen": {"index": 0, "width": 1920, "height": 1080},
  "webcam": {"index": 1, "width": 1280, "height": 720},
  "audio":  [{"index": 0, "kind": "speaker",    "device": "default", "name": "Speaker 1"},
             {"index": 1, "kind": "microphone", "device": "mic-id",  "name": "Microphone 1"}]
}
```

`index` is the stream index *within its media type* (video / audio), matching the container's per-type numbering. For the video entries, `width`/`height` are the encoded dimensions (the screen canvas after any `max_width`/`max_height` downscale; the webcam's ≤ 1280x720 mix canvas), and the `webcam` entry is **absent** (not `null`) when no webcam is configured. `audio` always holds at least one entry: with `--multi-track` one per device (`kind` is `speaker` or `microphone`, in `--speaker`-then-`--microphone` order), otherwise a single `{"kind":"mixed","device":null}` track carrying all devices mixed together (silence when none is configured). `name` is the track name written into the mp4, which is what a player shows in its track menu.

`tracks` is present on both `started_recording` and `stopped_recording` (but absent from a `stopped_recording` emitted before recording ever started, e.g. cancellation during `--pause` or a start failure).

`status` fields: `timeMs` is elapsed recording time in milliseconds (excluding paused spans), `fps` is the measured frame rate over the trailing 5 seconds of that clock (a lifetime average would read permanently low, since the frame counter trails the clock by the encoder's startup and in-flight frames), and `dropped` / `droppedPerc` report dropped frames. The final `stopped_recording.code` mirrors the OBS output stop code (`0` = success; negative values indicate invalid path, unsupported format, out of disk space, encoder error, etc.), with a human-readable `message`.

Example session (`--pause` mode), stdin on the left, stdout on the right:

```
                                {"type":"initialized"}
configure /path/to/s.json ->
                                {"type":"configure_applied","ignored_keys":[]}
start                     ->
                                {"type":"started_recording","tracks":{"screen":{"index":0,"width":1920,"height":1080}}}
                                {"type":"status","timeMs":1000,"fps":24.0,"dropped":0,"droppedPerc":0.0}
                                {"type":"status","timeMs":2000,"fps":24.0,"dropped":0,"droppedPerc":0.0}
configure /path/to/s.json ->
                                {"type":"configure_applied","ignored_keys":["fps"]}
quit                      ->
                                {"type":"stopped_recording","code":0,"message":"Successfully stopped","error":null,"tracks":{..}}
```

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Recording stopped successfully (or was cancelled before it started). |
| `1` | Recording or initialization failure. |
| `2` | Invalid command-line arguments. |

## Encoding

- **Container** — MP4. By default the single-track `ffmpeg_muxer` (one video track, one mixed audio track). `--multi-track` switches to OBS's hybrid MP4 output (`mp4_output`): it carries a track per stream (see below), is written fragment-by-fragment so a crash or kill mid-recording leaves a file FFmpeg can still read, and is soft-remuxed to a standard MP4 on stop.
- **Track layout** — with `--multi-track`, video track 0 is the clean screen, video track 1 the webcam, and each `--speaker` / `--microphone` device gets its own audio track (speakers first, in the order given; at most 6, libobs's mixer limit). Every audio source is routed to exactly one libobs mixer and encoded by that mixer's own AAC encoder, so the tracks stay fully separate — nothing is pre-mixed. Track names (`Screen`, `Webcam`, `Speaker 1`, `Microphone 1`, …) are written into the MP4. Without the flag, all audio devices are mixed into the single audio track, exactly as before.
- **Video** — H.264. Software x264 by default (`veryfast`, or `ultrafast` with `--low-cpu`); `--hw-accel` selects the first available hardware encoder (Windows priority NVENC → AMF → QSV; macOS VideoToolbox) and falls back to x264 otherwise. `--crf` is passed through as the CRF (x264) or CQP (hardware) value. Every video encoder uses a 2 s keyframe interval: the hybrid MP4 output flushes a fragment at each keyframe, so this bounds the data lost to a hard crash/kill to a few seconds (an encoder-default ~8 s GOP would make any recording killed in its first ~9 seconds a zero-byte total loss) and keeps editor seeking fast.
- **Webcam track** — with `--webcam` (or the `webcam_device` settings key), video track 1 carries the camera at its native size, downscaled aspect-preserving to fit 1280x720, always encoded with x264 (CRF from `--crf`/settings, `veryfast`, high profile) at the recording fps. The camera renders into its own private `obs_view` mix, so the screen track never sees it. Windows uses the DirectShow source (`dshow_input`), macOS AVFoundation (`macos-avcapture`).
- **Audio** — AAC at 128 kbps (`CoreAudio_AAC` on macOS when available, otherwise `ffmpeg_aac`), 44.1 kHz, one encoder per audio track.

## vid2gif

A companion CLI (`vid2gif`, built from `crates/vid2gif`) that converts a recording (or any video FFmpeg can decode) into an optimized palette-based GIF. It links the FFmpeg libraries already bundled next to `obs-express` (`avformat`/`avcodec`/`avfilter`/`avutil`, via the bindgen-based `crates/ffmpeg-sys`) and runs the classic two-pass palette pipeline in process — no subprocesses, no separate ffmpeg binary, on **every** supported platform. Bindings are generated from the obs-deps headers at build time, so an FFmpeg version bump that changes the ABI fails the build instead of the runtime. The test suite is self-contained (committed fixtures, generated raw-video inputs, and a GIF byte-stream validator) and the release staging fails hard if `vid2gif` or its libraries are missing from the bundle.

```sh
vid2gif input.mkv                          # writes input.gif
vid2gif input.mp4 out.gif --quality best   # quality: best | good | fair (default good)
vid2gif input.mkv --max-width 480 --fps 12 # aspect-preserving clamps; never upscales
```

- `--quality` sets frame rate and dithering: `best` (20 fps, sierra2_4a), `good` (15 fps, bayer), `fair` (10 fps, coarse bayer). `--fps` overrides the preset.
- `--max-width` / `--max-height` cap the output size like obs-express's recording clamps: aspect preserved, the more restrictive wins, never upscales.
- Stdout is a line protocol for a parent process: `progress <0-100>` lines (monotonic), then `done <path> <bytes>`, or `error <message>` with exit code 1.
- Writing `quit\n` to stdin cancels the conversion: the in-flight ffmpeg is killed, temp files and any partial output are removed, and vid2gif prints `cancelled` and exits 0.

The conversion is two in-process passes (fps/scale + `palettegen`, then `paletteuse` into the GIF encoder) with the palette kept in memory — no temp files. Progress derives from input frame timestamps, so it streams smoothly through both passes.

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

The build script stages the runtime next to the binary:

- **Windows** — `obs-express.exe` plus `obs.dll`, the graphics/plugin DLLs, the FFmpeg & x264 runtime DLLs, `obs-plugins/`, and `data/` are copied into `target/release/`.
- **macOS** — the binary links `libobs.framework`, the graphics modules, and the plugins straight out of the OBS build tree (absolute rpaths), and the FFmpeg/x264 dependency dylibs are copied into `target/release/` (symlinked aliases preserved, each given an `@loader_path` rpath and ad-hoc re-signed) so that, as on Windows, the profile dir holds a loadable FFmpeg runtime. The self-contained, relocatable bundle (framework + graphics modules + those dylibs + `.plugin` bundles, with `@executable_path/Frameworks` rpaths) is assembled by the CI Stage step in `.github/workflows/build.yml`.

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

Every push and pull request builds all four variants (Windows x64/ARM64, macOS x64/arm64) through a reusable GitHub Actions workflow; each build job zips its bundle itself (macOS with `ditto`, so symlinks and execute bits survive — the artifact store would strip both) and uploads the zip as its artifact. The manually-dispatched release workflow bumps the version, rebuilds every variant, and attaches those zips unchanged as assets on a GitHub Release.

## License & credits

Licensed under the [GNU General Public License v2.0](LICENSE), matching [clowd/obs-express](https://github.com/clowd/obs-express) (the C++ original this is a rewrite of) and [OBS Studio](https://github.com/obsproject/obs-studio) / libobs, which this project links and is therefore bound by.
