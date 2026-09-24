# obs-express

A minimal, headless screen recorder backed by [libobs](https://github.com/obsproject/obs-studio) (the OBS Studio core). It records a screen region or a whole monitor straight to an MP4, driven entirely by command-line flags (plus an optional JSON settings file) and a small line-oriented stdin/stdout protocol — no GUI, no OBS install required.

This is a Rust rewrite of [clowd/obs-express](https://github.com/clowd/obs-express) (originally C++). libobs 32.1.2 is built from source from the pinned `obs-studio` submodule and bundled next to the binary, so a release is self-contained.

## Features

- **Region or monitor capture** — record an arbitrary `X,Y,W,H` rectangle (which may span multiple displays) or a whole monitor by id/index. Defaults to the primary monitor.
- **Hardware or software H.264** — x264 by default; `--hw-accel` prefers a GPU encoder (NVENC → AMF → QSV on Windows, VideoToolbox on macOS; none yet on Linux) and transparently falls back to x264.
- **Multi-device audio** — any number of speaker (output) and microphone (input) devices: up to 8 total mixed into one audio track, or up to 6 on separate tracks with `--multi-track`.
- **Multi-track recording** — `--multi-track` writes every stream to its own track in one MP4: video track 0 = clean screen, video track 1 = webcam, and one audio track per capture device. A screen recording with a webcam, a speaker and a microphone is a 4-track file, ready for picture-in-picture compositing and per-source audio mixing at edit time.
- **Webcam second track** — `--webcam <id>` (requires `--multi-track`) records a camera — DirectShow on Windows, AVFoundation on macOS, V4L2 on Linux — as video track 1; `--list-cameras` enumerates the available devices.
- **Programmatic control** — a parent process drives recording over stdin (`start` / `pause` / `quit`, per-device mute) and reads structured progress as one JSON object per line on stdout.
- **Live reconfiguration** — all tunables (fps, quality, encoder, resolution cap, cursor, tracker, audio devices) can be supplied as a JSON file via `--settings` and re-applied at runtime with the stdin `configure` command — in `--pause` mode the whole pipeline is rebuilt in place, no process restart needed.
- **Aspect-preserving downscale** — cap output resolution with `--max-width` / `--max-height` without distorting the picture (never upscales).
- **Click highlight** — `--tracker` draws an expanding, fading circle at the pointer on every mouse click, in the recording only.
- **Cursor toggle** and a **paused-start** mode for building the pipeline ahead of time and starting instantly on command.
- **Cross-platform** — Windows (x64 / ARM64), macOS (x64 / arm64) and Linux (x64; X11 and Wayland, see [Linux](#linux) for what differs).

## Installation

### Prebuilt releases

Each release publishes a zipped, self-contained bundle for every supported target on the [Releases](https://github.com/clowd/obs-express/releases) page:

- `obs-express-windows-x64`, `obs-express-windows-arm64`
- `obs-express-macos-x64`, `obs-express-macos-arm64`
- `obs-express-linux-x64`, `obs-express-linux-arm64` (a `.tar.gz`: `tar -xzf` keeps the execute bits)

Unzip and run `obs-express` in place — the bundled OBS runtime (plugins, data, and the FFmpeg/x264 libraries) lives alongside the executable and is fully relocatable.

The bundled FFmpeg/x264 libraries are also usable on their own (e.g. by a host that uses the FFmpeg C API in-process): on Windows they are ordinary DLLs next to the executable (load them from that directory, e.g. via `AddDllDirectory`/`LOAD_WITH_ALTERED_SEARCH_PATH`), and on macOS the dylibs in `Frameworks/` carry an `@loader_path` rpath so they can be `dlopen`ed directly by any program as long as they stay together. On macOS, unzip with a tool that restores symlinks and modes (`unzip`, `ditto -x -k`, `tar`): the versioned aliases (`libavcodec.61.dylib`) are symlinks and the executables rely on their execute bits.

On macOS the binaries are ad-hoc signed but not notarized, so the first launch may need:

```sh
xattr -dr com.apple.quarantine <unzipped-directory>
```

### Build from source

See [Building](#building) below.

## Linux

Linux (x64 and arm64) is supported on **X11** and **Wayland**. The session type is detected at startup (`XDG_SESSION_TYPE=wayland` or `WAYLAND_DISPLAY` means Wayland, otherwise `DISPLAY` / X11). An X11 session is never used from inside a Wayland session: XWayland only captures black.

- **X11** — works like the other platforms: `--monitor` and `--region` use the XRandR monitor layout (a monitor is matched by connector name such as `DP-1`, or by 0-based index), and capture runs through OBS's XSHM source.
- **Wayland** — the monitor or window is chosen in the desktop's own screen-share dialog (xdg-desktop-portal + PipeWire), so `--monitor` and `--region` are rejected (exit 2). The recorder builds the pipeline, waits for the pick (up to 120 s), sizes the canvas to what was picked, then prints `initialized`. Cancelling the dialog exits 1, and `quit` or a signal during the wait exits 0. The dialog appears on every run: portal restore tokens are not used.
- **Audio** — PulseAudio sources (`pulse_output_capture` / `pulse_input_capture`), which also work on PipeWire through `pipewire-pulse`. `default` is the default sink's monitor or the default source. `--speaker-volume-compensation` is a no-op.
- **Webcam** — V4L2 (`v4l2_input`). `--list-cameras` prints `/dev/videoN` paths as ids.
- **Encoding** — x264 only. `--hw-accel` falls back to x264 with the usual message (no NVENC/VAAPI selection yet). `--capture-method` is ignored, as on macOS.
- **Not supported** — `--input-capture`, `--window-capture` and `--tracker` fail at startup with a clear error (exit 2) rather than misbehaving.
- **No `clowd_share_region`** — the share-region binary is not built on Linux and is not in the Linux bundle. Cargo cannot leave a workspace member out for one target, so a plain `cargo build` on Linux fails on that crate. Build the rest of the workspace instead:

  ```sh
  cargo build --release --workspace --exclude clowd_share_region
  cargo test --release --workspace --exclude clowd_share_region
  ```

**Runtime requirements.** The bundle carries libobs, the OBS plugins and FFmpeg, with x264 and Mbed TLS linked statically into the plugins that use them. Everything else comes from the system, through libraries whose SONAMEs are stable across distributions:

- **glibc ≥ 2.34**: Ubuntu 22.04 or newer, Debian 12 or newer, Fedora, RHEL / AlmaLinux / Rocky 9 or newer. The release is built in a manylinux_2_34 (AlmaLinux 9) image, and CI runs it on stock Ubuntu 22.04 and 24.04.
- **Desktop libraries**: EGL/OpenGL (Mesa or a vendor driver), X11/xcb, xkbcommon, Wayland client, GLib ≥ 2.68, PipeWire, the PulseAudio client library, libv4l2, udev, libuuid, jansson, zlib, and libva/libpci/libdrm. OBS's FFmpeg plugin links the VAAPI libraries even though obs-express does not use them. On Debian/Ubuntu, `tools/linux-build/install-runtime-deps.sh` installs exactly this set; desktop installs already have it.
- **Audio**: a PulseAudio server, or PipeWire with `pipewire-pulse`.
- **Wayland capture**: a running `xdg-desktop-portal` with a backend for your desktop (GNOME, KDE, wlroots, ...) and PipeWire.

## Usage

```
obs-express --output <FILE.mp4|FILE.mkv> [capture target] [options]
```

`--output` is required and must end in `.mp4` or `.mkv` (its parent directory must already exist); the extension picks the container, and `.mkv` is only accepted without `--multi-track`. If neither `--region` nor `--monitor` is given, the **primary monitor** is recorded.

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
| `--output <PATH>` | *(required)* | Destination file. Must end in `.mp4` or `.mkv` (the extension picks the container; `.mkv` is rejected with `--multi-track`); parent directory must exist. |
| `--region <X,Y,W,H>` | — | Capture rectangle in the platform capture coordinate space. `X`/`Y` may be negative (virtual desktop); `W`/`H` must be ≥ 2. Mutually exclusive with `--monitor`. |
| `--monitor <ID>` | — | Record a whole monitor by device id, alternate id, or 0-based index. Mutually exclusive with `--region`. |
| `--fps <INT>` | `30` | Output frame rate (≥ 1). |
| `--crf <0-51>` | `24` | Quality, lower is better. An x264-style CRF that each encoder maps onto its own quality scale so a given value lands on comparable output whichever encoder is selected: x264 CRF = crf; NVENC constant-quality target (`CQVBR`) = crf+4; AMF constant QP = crf; QSV `ICQ` = crf (constant QP on iGPUs older than Haswell); VideoToolbox quality slider (inverted 0-100) on Apple Silicon, average bitrate on Intel Macs. |
| `--max-width <INT>` | `0` | Downscale cap for width; `0` = off. |
| `--max-height <INT>` | `0` | Downscale cap for height; `0` = off. |
| `--hw-accel` | off | Prefer a hardware H.264 encoder; falls back to x264 if none is available. |
| `--low-cpu` | off | Use the x264 `superfast` preset at crf+2 instead of `veryfast` (the +2 re-centers `superfast`'s CRF scale, which lacks mbtree, onto `veryfast`'s bytes). No effect with a hardware encoder. |
| `--no-cursor` | off | Do not capture the mouse cursor. |
| `--capture-method <METHOD>` | `auto` | Windows only (ignored on macOS and Linux): which OS API backs display capture — `auto`, `dxgi` (desktop duplication) or `wgc` (Windows Graphics Capture). `auto` takes WGC on Windows 11 and newer — the yellow border Windows draws around a WGC-captured display can be suppressed there (`GraphicsCaptureSession::IsBorderRequired`, Windows 11+), and WGC captures monitors on any graphics adapter. On Windows 10 it leaves the choice to win-capture, which takes DXGI unless the monitor is off the current graphics adapter or the machine is a multi-GPU laptop on mains. That heuristic optimises for capture, not for the border — unsuppressable on Windows 10 — so pin `dxgi` there if a borderless capture matters. Session-fixed, so it is not part of the settings file. |
| `--tracker` | off | Highlight mouse clicks with an expanding, fading circle (see below). |
| `--tracker-color <R,G,B>` | `255,0,0` | Color of the click highlight; each component 0-255. |
| `--pause` | off | Build the pipeline, emit `initialized`, and wait for a stdin `start` before recording. |
| `--speaker <DEVICE>` | — | Output-capture (system audio) device id, or `default`. Repeatable. On macOS 13+ system audio is captured via ScreenCaptureKit: the device id is ignored (the flag only toggles system-audio capture on) and repeating the flag is rejected. |
| `--microphone <DEVICE>` | — | Input-capture (microphone) device id, or `default`. Repeatable. |
| `--multi-track` | off | Give every stream its own track (OBS's hybrid MP4 output): video track 0 = screen, video track 1 = webcam, and one audio track per `--speaker` / `--microphone` device — speakers first, in the order given, at most 6 audio tracks. Without it the recording uses the single-track muxer: one video track and all audio mixed into one track, and `--webcam` is rejected. |
| `--webcam <ID>` | — | Record the given camera as a second video track (track 0 = screen, track 1 = webcam, ≤ 1280x720, x264 CRF). Requires `--multi-track`. `ID` is a device id exactly as printed by `--list-cameras`. The camera's built-in microphone is never recorded — use `--microphone` for that. |
| `--list-cameras` | — | Enumerate cameras (DirectShow on Windows, AVFoundation on macOS, V4L2 on Linux): prints exactly one JSON line `{"type":"cameras","cameras":[{"id":..,"name":..}]}` on stdout and exits 0 (`{"type":"error","message":..}` and exit 1 on failure). Mutually exclusive with all recording flags; `--output` is not required. |
| `--speaker-volume-compensation` | off | Windows: boost speaker capture to undo the system master volume when the audio device applies it in software. On such devices (no hardware volume control — common for USB DACs) the loopback stream Windows hands to recorders is already attenuated by the volume slider, so recordings sound quieter than the played content did. Devices with hardware volume are detected and left untouched; no-op on macOS and Linux. Volume changes made while recording are tracked within ~100 ms; the boost is capped at +30 dB. |
| `--settings <FILE.json>` | — | Read the tunables from a JSON file instead of individual flags (see below). Conflicts with every flag it replaces: `--fps`, `--crf`, `--max-width`, `--max-height`, `--hw-accel`, `--low-cpu`, `--no-cursor`, `--tracker`, `--tracker-color`, `--speaker`, `--microphone`, `--speaker-volume-compensation`. |

### Graphics adapter

On Windows the recorder runs libobs on the GPU that drives the display the capture region mostly covers (`obs_video_info.adapter`, resolved through `CreateDXGIFactory1` / `EnumAdapters1` — the same index space libobs uses). This is not cosmetic under `--capture-method dxgi`: desktop duplication only finds monitors attached to the *current graphics device's* adapter, so on a multi-GPU machine a display hanging off the second GPU would never start capturing and the recording would stay black. WGC has no such constraint, but running on the GPU that already owns the surface saves a cross-adapter copy per frame, so the adapter is selected either way.

A region spanning displays driven by two different GPUs can only pick one — the most-covered display wins, and under `dxgi` the displays on the other adapter will not capture. Use `wgc` for that case. The adapter is also fixed for the process: libobs builds the graphics device on the first `obs_reset_video` and ignores the field on later ones.

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
- **Linux (X11)** — X screen pixels (the XRandR layout). Under Wayland neither `--region` nor `--monitor` is accepted; see [Linux](#linux).

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

- **Container** — MP4 or Matroska, chosen by the `--output` extension. By default the single-track `ffmpeg_muxer` (one video track, one mixed audio track), which writes whichever container the extension names. `--multi-track` switches to OBS's hybrid MP4 output (`mp4_output`): it carries a track per stream (see below), is written fragment-by-fragment so a crash or kill mid-recording leaves a file FFmpeg can still read, and is soft-remuxed to a standard MP4 on stop. It is MP4 only, so `--multi-track` rejects an `.mkv` output.
- **Track layout** — with `--multi-track`, video track 0 is the clean screen, video track 1 the webcam, and each `--speaker` / `--microphone` device gets its own audio track (speakers first, in the order given; at most 6, libobs's mixer limit). Every audio source is routed to exactly one libobs mixer and encoded by that mixer's own AAC encoder, so the tracks stay fully separate — nothing is pre-mixed. Track names (`Screen`, `Webcam`, `Speaker 1`, `Microphone 1`, …) are written into the MP4. Without the flag, all audio devices are mixed into the single audio track, exactly as before.
- **Video** — H.264. Software x264 by default (`veryfast`, or `superfast` at crf+2 with `--low-cpu`); `--hw-accel` selects the first available hardware encoder (Windows priority NVENC → AMF → QSV; macOS VideoToolbox) and falls back to x264 otherwise. `--crf` is an x264-style CRF that each encoder maps onto its own quality scale: x264 uses it as the CRF; NVENC runs constant-quality VBR (`CQVBR`, preset `p6`, `hq` tune, quarter-res multipass, lookahead, adaptive quantization, 2 B-frames, no bitrate cap) at target quality crf+4, which matches the bytes of a CQP-at-crf recording at equal or better quality; AMF uses constant QP = crf; QSV uses Intel's constant-quality mode `ICQ` at crf when the plugin lists it (Haswell or newer, the same probe OBS's own recording presets make) and constant QP = crf otherwise; VideoToolbox maps it onto its inverted 0-100 quality slider on Apple Silicon, while Intel Macs (no quality mode) record at an average bitrate of 4 Mbps scaled by pixel rate relative to 3440x1440@30 and clamped to 1.5-6 Mbps. Every encoder runs 2 B-frames (VideoToolbox: frame reordering on). Every video encoder uses a 2 s keyframe interval: the hybrid MP4 output flushes a fragment at each keyframe, so this bounds the data lost to a hard crash/kill to a few seconds (an encoder-default ~8 s GOP would make any recording killed in its first ~9 seconds a zero-byte total loss) and keeps editor seeking fast.
- **Webcam track** — with `--webcam` (or the `webcam_device` settings key), video track 1 carries the camera at its native size, downscaled aspect-preserving to fit 1280x720, always encoded with x264 (CRF from `--crf`/settings, `veryfast`, high profile) at the recording fps. The camera renders into its own private `obs_view` mix, so the screen track never sees it. Windows uses the DirectShow source (`dshow_input`), macOS AVFoundation (`macos-avcapture`).
- **Audio** — AAC at 192 kbps (`CoreAudio_AAC` on macOS when available, otherwise `ffmpeg_aac`), 48 kHz, one encoder per audio track. 48 kHz is the shared-mode rate of nearly every Windows and macOS device, so capture is normally a straight copy; libobs resamples any device that runs at another rate (44.1 kHz USB audio, for example), so mixed-rate device sets still work.

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
- **Linux** (x86_64 and aarch64) — build in the reference image, `tools/linux-build/Dockerfile`. It is a manylinux_2_34 (AlmaLinux 9, glibc 2.34) base with clang/`libclang` (for `bindgen`), `ninja`, `nasm`, `patchelf`, Rust, and the development packages of the system libraries the bundle links. CI builds in the same image, and building there is what keeps the result portable to every glibc 2.34+ distribution:

  ```sh
  docker build -t obs-express-linux tools/linux-build
  tools/linux-build/run-in-image.sh cargo build --release --workspace --exclude clowd_share_region
  ```

  A native build on another distribution also works, given the same tools and `-dev` packages; the Dockerfile is the list. Its output then needs that distribution's glibc or newer. Do **not** install the system FFmpeg development packages (`libav*-dev`). The build downloads a pinned FFmpeg 7.1 shared build (BtbN FFmpeg-Builds, SHA-256 verified), builds pinned x264 and Mbed TLS from source, and fetches a pinned SIMDe, all into `obs-studio/.deps`. The first build therefore needs access to github.com. libobs, every plugin and `vid2gif` share that one FFmpeg.

### Steps

```sh
git clone --recursive https://github.com/clowd/obs-express.git
cd obs-express
./build.sh          # inits submodules, then `cargo build --release`
```

`build.sh` is a thin wrapper for macOS; you can also run the steps directly. On Linux, use the `--workspace --exclude clowd_share_region` commands from the requirements above instead.

```sh
git submodule update --init --recursive
cargo build --release
```

The build script stages the runtime next to the binary:

- **Windows** — `obs-express.exe` plus `obs.dll`, the graphics/plugin DLLs, the FFmpeg & x264 runtime DLLs, `obs-plugins/`, and `data/` are copied into `target/release/`.
- **Linux** — like Windows: `libobs.so.30`, `libobs-opengl.so.30`, the FFmpeg `.so` files, `obs-ffmpeg-mux`, `obs-plugins/*.so` and `data/` are copied into `target/release/`, each with an `$ORIGIN`-relative RUNPATH. The executables also carry absolute RUNPATHs into the build tree, so cargo's test binaries resolve. `tools/linux-build/stage-bundle.sh` removes those for the release bundle and checks it, and `tools/linux-build/verify-bundle.sh` runs it end to end under Xvfb; CI runs both. Running the smoke tests needs an X server, e.g. `xvfb-run -a -s "-screen 0 1280x720x24 +extension RANDR" cargo test --release -p obs-express --test smoke -- --ignored` (on a blank Xvfb screen the recordings compress below the tests' size threshold, so put something moving on it, such as `glxgears`).
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

Every push and pull request builds all five variants (Windows x64/ARM64, macOS x64/arm64, Linux x64) through a reusable GitHub Actions workflow; each build job archives its bundle itself (macOS with `ditto`, Linux as a `.tar.gz`, so symlinks and execute bits survive — the artifact store would strip both) and uploads the archive as its artifact. The Linux job builds inside the manylinux_2_34 image from `tools/linux-build`. It checks that no file needs a glibc newer than 2.34, then moves the bundle out of the checkout, proves every library and symbol resolves there (`ldd -r`), records under Xvfb and runs `vid2gif` on the result. A portability job repeats those checks on stock Ubuntu 22.04 and 24.04 runners that have only the runtime packages installed. The manually-dispatched release workflow bumps the version, rebuilds every variant, and attaches those archives unchanged as assets on a GitHub Release.

## License & credits

Licensed under the [GNU General Public License v2.0](LICENSE), matching [clowd/obs-express](https://github.com/clowd/obs-express) (the C++ original this is a rewrite of) and [OBS Studio](https://github.com/obsproject/obs-studio) / libobs, which this project links and is therefore bound by.
