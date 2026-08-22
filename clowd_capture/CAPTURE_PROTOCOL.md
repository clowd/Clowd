# clowd_capture protocol

The contract between the Clowd.Ui shell and the Rust capture processes.

The capture overlay `clowd_capture_wgpu` runs **one-shot**: the shell spawns one
process per capture with CLI flags; the process shows the overlay, writes its
result into `--session-dir`, and exits. Completion signal = process exit.

A **separate binary**, `clowd_scroll_driver` (§2), carries out the second half
of a scrolling capture the overlay already set up. It is not a capture overlay
at all — no window, no event loop, no GPU — and shares the session format with
the capturer and nothing else. It is documented here because it speaks to the
same shell across the same boundary.

A second separate binary, `clowd_ai` (§3, its `ocr` subcommand), does text
recognition. It is the odd one out: the shell never spawns it for this and
never speaks to it — the *overlay* does, per OCR press. (The same binary's
`matte`/`denoise` subcommands serve the video editor's AI effects, over a
different protocol owned by `Clowd.VideoSDK`'s `AiClient.cs`.) It is
documented here because it is the third process in the same family and shares
`clowd_rust_core` with the other two.

Source of truth: `src/settings.rs` (CLI), `src/session_output.rs` (session
files), `clowd_scroll_driver/src/drive.rs` (scrolling-capture driver),
`src/ocr/client.rs` + `clowd_ai/src/ocr.rs` (recognizer), and
`clowd_rust_core` for what the binaries must agree on — the `session.json`
shape (`session.rs`), the recognition contract (`ocr.rs`), the coordinate space
(`geometry.rs`) and the exit codes (`exit.rs`). C# counterparts:
`ScreenCaptureService.cs` (spawn + session dispatch), `Scroll/ScrollDriver.cs`
+ `Scroll/ScrollDriverProtocol.cs` (driver). There is no C# counterpart for
§3 by design.

## 1. The capture overlay

### 1.1 CLI

Flag defaults mirror `CapturerSettings::default()`; the shell only passes
flags that differ (`CaptureArguments.Build`).

| Flag | Value | Default | Meaning |
|---|---|---|---|
| `--session-dir` | path | none | Directory for the session payload. Omit = standalone mode (no files written, actions handled in-process). |
| `--accent-color` | `#RRGGBB` / `#RRGGBBAA` (leading `#` optional) | `#2F7CAE` | Accent for crosshair, selection borders, UI highlights. |
| `--tips-mode` | `hints` \| `tips` \| `off` | `hints` | Tips/hints overlay at startup (user cycles with `T`). |
| `--no-peek` | flag | peek on | Disable obstructed-window peek-through capture. |
| `--peek-threshold` | 0.0–1.0 | `0.80` | Max obstructed fraction before a window is dropped from hit-testing. |
| `--no-cursor` | flag | cursor on | Start with the captured cursor hidden (user toggles with `M`). |
| `--no-upload` | flag | UPLOAD shown | Hide the UPLOAD button — in the capture strip *and* the OCR strip — and drop its `U` accelerator. |
| `--no-scroll-capture` | flag | SCROLL shown | Hide the SCROLL button and drop its `L` accelerator. Windows-only button; the flag parses everywhere. |
| `--no-ocr` | flag | OCR shown | Hide the OCR button and drop its `O` accelerator. The button is the only way into OCR mode, so this also removes the OCR strip. |
| `--capture-mode` | `region` \| `screen` \| `window` | `region` | `region` = free crosshair; `screen`/`window` pre-select the active monitor / foreground window and show the action panel. |
| `--video` | flag | off | Video-region picker: first confirmed selection dispatches the VIDEO action immediately. Requires `--session-dir`. |
| `--memory-hints` | `max-performance` \| `lower-memory-usage` | `max-performance` | GPU allocator strategy, read once at device creation. `max-performance` is wgpu's large-block allocator, trading memory for start-up latency — the right trade for a process that exits after one capture. `lower-memory-usage` keeps the retained heap blocks small. The shell never passes this; it exists for standalone runs and experiments. |
| `--shell-pid` | pid | none | The shell's process id, so the overlay can hand its foreground rights back as the cycle ends (§2.5). Process-level: the shell knows its own id, and the capturer never outlives it, so the two cannot disagree. Omit in standalone runs. |

### 1.2 Session-directory file protocol

The shell pre-creates the session directory and passes it via
`--session-dir`. Which files appear depends on how the capture ended:

| Outcome | Files written | `action.txt` content |
|---|---|---|
| EDIT | `desktop.png`, `cropped.png`, [`cursor.png`], `session.json` | none (any stale marker from a failed retry is deleted) |
| UPLOAD | same as EDIT + `action.txt` | `upload` |
| SELECT-COLOR | `action.txt` only | `select-color #RRGGBB` |
| VIDEO | `cropped.png` (poster frame), `action.txt` | `video X,Y,W,H` |
| SCROLL | `action.txt` only | `scroll X,Y,W,H PX,PY HWND` |
| OCR-UPLOAD | `ocr.txt`, `action.txt` | `ocr-upload` |
| COPY / SAVE | none — handled inside the capturer (clipboard / save dialog) | — |
| OCR-COPY / OCR-SEARCH | none — handled inside the capturer (clipboard / browser launch) | — |
| Cancelled (Escape / close) | none | — |

File contents:

| File | Contents |
|---|---|
| `desktop.png` | Full virtual-desktop bitmap, locked peek window composited, never the cursor (the editor toggles cursor visibility itself). |
| `cropped.png` | Preview of the selection, peek composited; cursor composited only if visible to the user. For VIDEO: no peek compositing (the recording shows real obstructions). |
| `cursor.png` | Desktop crop at the cursor rect with the cursor composited. Absent when no cursor was captured or the OS reported it hidden. |
| `session.json` | Session metadata (§1.3). |
| `ocr.txt` | The text recognized in the selection, UTF-8 without BOM, lines separated by `\n`. Present only with the `ocr-upload` marker; the shell reads it, uploads it as a text paste, and deletes the directory. |
| `action.txt` | Routing marker read by `CaptureSessionDispatcher` (missing = edit). |
| `capture.log` | Mirror of the capturer's log, read by the shell after a non-zero exit — and the only diagnostics that survive a native fault, which never reaches a Rust error path. |
| `scroll.log` | Same, for the `clowd_scroll_driver` pass (§2). A separate name because the driver runs in a directory the overlay already wrote `capture.log` into, and truncating that would erase the diagnostics for the half of the capture that came first. |

**Write ordering** (`session_output.rs`) — the last file to appear is the
completion signal, so readers must wait for process exit and then key off
these files:

1. EDIT/UPLOAD: the three PNGs are written first (in parallel), then
   `action.txt` is written (upload) or removed (edit), then **`session.json`
   strictly last**. `session.json` present = capture succeeded.
2. VIDEO: `cropped.png` first, then **`action.txt` last**. Its appearance is
   the completion signal; no `desktop.png`, no `session.json` (the session is
   created by Clowd.Ui when recording finishes).
3. OCR-UPLOAD: `ocr.txt` first, then **`action.txt` last**. Its appearance is
   the completion signal; no PNGs and no `session.json` — the recognized text
   is the entire payload, and the shell uploads it as a text paste.
4. SELECT-COLOR / SCROLL: `action.txt` only.
5. Neither `session.json` nor `action.txt` present = the capture was
   cancelled; the shell deletes the pre-created directory.

The VIDEO rect is emitted in the platform capture coordinate space: physical
pixels (virtual-desktop, possibly negative origin) on Windows, CG points on
macOS — passed verbatim to obs-express `--region`. W and H are always >= 2.

The SCROLL marker uses the same rect space and adds two fields: `PX,PY` is
the point the scrolling capture driver parks the cursor at and aims wheel
events from — always inside `X,Y,W,H` — and `HWND` is the decimal top-level
window handle under that point, or `0` when the walker could not resolve one
(the driver then falls back to `WindowFromPoint`). Unlike VIDEO there is no
poster frame: the driver produces every image plus the `session.json` for
the stitched result, so `action.txt` is the whole overlay payload. macOS
never emits this marker.

The OCR markers come from a second action panel the overlay shows once it has
recognized text inside the selection (Windows only — like SCROLL, the button
is compiled out elsewhere, so macOS never emits them either). Only UPLOAD
reaches the shell: COPY and SEARCH finish inside the capturer, BACK returns to
the ordinary panel without ending the cycle at all, and EXIT is an ordinary
cancel. `ocr-upload` carries no rect — by then the selection has been reduced
to the text in `ocr.txt`.

### 1.3 `session.json` schema

Fixed-schema JSON, shared with `Clowd.Ui`'s `SessionInfo`.
Rects serialize as `{ "X": …, "Y": …, "Width": …, "Height": … }`
(`Clowd.PlatformUtil.ScreenRect` casing).

| Key | Type | Notes |
|---|---|---|
| `CreatedUtc` | ISO 8601 UTC string | e.g. `2026-06-12T18:30:00Z` |
| `Name` | string | Always `"Screenshot"` |
| `DesktopImgPath` | string | Absolute path to `desktop.png` |
| `PreviewImgPath` | string | Absolute path to `cropped.png` |
| `CursorImgPath` | string, optional | Absolute path to `cursor.png`; present only with `CursorPosition` |
| `CursorPosition` | rect, optional | Cursor rect relative to the desktop bitmap origin |
| `CroppedRect` | rect | Selection relative to the desktop bitmap origin |
| `OriginalBounds` | rect | Selection in virtual-desktop coordinates |

### 1.4 Exit codes

Constants in `src/system/mod.rs`; keep in sync with
`ScreenCaptureService.cs`.

| Code | Meaning |
|---|---|
| 0 | Every normal outcome — edit, upload, color, video, copy, save, OCR copy/search/upload, **and cancel**. The shell distinguishes them by the session files, not the code. |
| 3 | `EXIT_NO_SCREEN_PERMISSION` — the OS has not granted screen capture (macOS Screen Recording). The shell shows the permission dialog instead of a crash report. |
| 4 | `EXIT_CAPTURE_FAILED` — the desktop screenshot itself failed. The reason is in stderr/`capture.log`, not a stack trace. |

Any other non-zero exit is a crash; the shell reports it with the stderr and
`capture.log` tails.

## 2. Scrolling-capture driver (`clowd_scroll_driver`)

The driver is the second half of a scrolling capture. The first half is an
ordinary capture cycle: the user selects a region, presses SCROLL, clicks the
point to scroll at, and the overlay exits leaving the `scroll X,Y,W,H PX,PY
HWND` marker of §1.2. `CaptureSessionDispatcher` turns that marker into
`CaptureAction.Scroll`, `ScrollCapturePage` puts its border window up around
the region, and spawns the driver to do the mechanical part.

It is a **separate binary**, not a mode of the capturer. Nothing it does needs
a window, an event loop, a GPU or the screen-recording permission dance, and
bringing any of that up would put pixels on screen in front of the content it
is about to photograph. It ships beside `clowd_capture_wgpu`, which is where
`CaptureBinaryLocator.ResolveScrollDriver` looks for it.

**Windows and macOS.** The loop, the caps, the stitcher, this protocol and the
session output are one implementation; the OS half — injecting a wheel,
parking the cursor, resolving and raising the target, photographing the region
— has a Win32 and a Core Graphics backend. macOS needs **two permissions** and
refuses without them (`fatal_error`): Screen Recording to photograph the
region, and Accessibility, because the window server silently discards
synthetic events from a process it does not trust. TCC records both against
`Clowd.app`, which is what makes the shell's own grants cover this executable
(`MacPermissions`); `ScrollCapturePage` asks for Accessibility before it
spawns anything, so the usual path to a missing grant is a dialog with a button
to the right Settings pane rather than a driver refusal.

### 2.1 Spawn

```
clowd_scroll_driver --session-dir <dir> --region X,Y,W,H --point PX,PY --hwnd N [--no-rewind]
```

| Flag | Required | Meaning |
|---|---|---|
| `--session-dir` | yes | The directory the overlay was given. It is empty by the time the driver starts (the shell consumed `action.txt`), and the driver owns it from here. |
| `--region X,Y,W,H` | yes | The rect to photograph, in the marker's platform capture space (§1.2) — physical virtual-desktop px on Windows, CG points on macOS — and the same numbers. Rejected if W or H is 0. **Not** the unit the captured frames are in: `kCGWindowImageBestResolution` returns a Retina region at 2×, so the composite is in pixels while the region is in points. |
| `--point PX,PY` | yes | Where the cursor is parked and the wheel is aimed, same space. Re-clamped into `--region`; a point outside it is a caller bug and is logged. |
| `--no-rewind` | no | Start capturing from wherever the document is sitting instead of winding it back to the top first. Negative because rewinding is the default — the shell passes this only when the user turns "Scroll to top first" off, which is the deliberate "capture from here" intent. |
| `--hwnd N` | no (default 0) | Decimal window handle from the marker: **the window the user's selection snapped to**, not whatever is topmost at the scroll point. An `HWND` on Windows, a `CGWindowID` on macOS — one flag name for both, because it is the same field of the same marker. Re-validated at drive time (still a window, still covering the point); `0` or a stale handle falls back to whatever is topmost at the point. Whichever wins is then raised over the scroll point and *verified* there before the first frame — see §2.5. |

The shell must redirect **all three** stdio streams. Stdin in particular: the
driver reads a closed stdin as "the shell is gone" and cancels, which is what
keeps a crashed Clowd.Ui from leaving something scrolling the user's window.
On Windows the shell also calls `AllowSetForegroundWindow(driver pid)` right
after spawn — without it `SetForegroundWindow` in the driver is refused and the
run relies on Win10+ scroll-inactive-windows routing alone. macOS has no
equivalent grant to pass on.

### 2.2 Events (driver → shell)

One JSON object per line on stdout, and **only** protocol lines: the terminal
logger is routed to stderr for exactly that reason, so the shell may treat any
line starting `{` and ending `}` as an event.

| Event | Fields | Semantics |
|---|---|---|
| `ready` | — | The target is resolved and focused; scrolling is about to start. |
| `status` | `frames` (u32), `height_px` (u32), `state`, `resume_in_s` (u64, optional) | Progress. Emitted at each phase change of every step, so up to three per step. `state` ∈ `rewinding` \| `paused` \| `resuming` \| `scrolling` \| `settling` \| `stitching`. `rewinding` appears only before the first frame and only when the rewind is enabled; its `frames` and `height_px` are both 0. `paused` means the user has the mouse (§2.5) — nothing advances until they put it down. `resuming` carries `resume_in_s`, the whole seconds left before the run takes the cursor back, and is the only state on which that field is present. |
| `done` | `result`, `frames` (u32), `height_px` (u32) | The run ended. Unless `result` is `failed`, `session.json` is already on disk. |
| `fatal_error` | `message` (string) | Setup or output failed and there is no session. The shell deletes the directory and reports it. |

```json
{"type":"ready"}
{"type":"status","frames":12,"height_px":4180,"state":"scrolling"}
{"type":"done","result":"complete","frames":31,"height_px":9800}
{"type":"fatal_error","message":"no window at scroll point ScreenPoint { x: 400, y: 300 }"}
```

`done.result`:

| Result | Meaning | Session written |
|---|---|---|
| `complete` | Two consecutive steps produced no new content: the document ended. | yes |
| `stopped` | Esc, a `stop` command, the target window closing/moving, the stitcher giving up, or a pause the user never came back from (§2.5). The partial capture is kept. | yes |
| `max_reached` | A hard cap: 120 frames, 20,000 px of composite, or 120 s wall clock — the clock excludes time spent paused. | yes |
| `no_movement` | Nothing the driver could inject ever moved the target — most often an elevated Windows target whose UIPI eats `SendInput`, or a surface that ignores a synthetic wheel. Reported whatever else ended the run, and a single-screen session is still written. | yes |
| `failed` | Defined for completeness; the driver has no failure it can recognise *after* there is content worth keeping, so failures go out as `fatal_error` instead. The shell must still handle it. | no |

### 2.3 Commands (shell → driver)

| Command | Effect |
|---|---|
| `{"type":"stop"}` | Finish now and keep everything captured so far — the HUD's FINISH button. Ends as `stopped`. |
| `{"type":"cancel"}` | Abandon the run. **Nothing is written**: no `done`, no session, an empty directory for the shell to delete. |

Both are polled between steps, never inside one, so a command that lands
mid-settle takes effect up to a settle cycle (~800 ms) later. Unparseable
lines are logged and ignored — garbage on the command channel must not take
down a capture that is going fine. Stdin EOF is treated as `cancel`; stdin
that is unusable rather than closed (no console, not redirected) is tolerated
and the run continues without a command channel.

### 2.4 Output and exit

On any result but `failed` the driver writes, in this order: `desktop.png`
(the stitched composite), `cropped.png` (a byte copy of it — `cropped.png` is
what `SessionInfo.UploadSourcePath` shares from Recents, so it must not be a
downscaled preview), then **`session.json` strictly last**, per §1.2's
ordering invariant. `CroppedRect` is `0,0,W,H` and `OriginalBounds` is the
empty rect: a 20,000 px composite has no meaningful place on the virtual
desktop, and empty bounds make the editor centre its window instead of trying
to open one taller than every monitor stacked. `Name` is `"Screenshot"` like
every other session; the shell renames it to "Scrolling Capture".

The process **exits 0 for every outcome the shell can act on**, `fatal_error`
included — the shell reads the event, not the code. A non-zero exit therefore
means a crash, and the shell reports it with the stderr tail attached.

### 2.5 The target window and the user's mouse

Both halves of the run depend on the same fact: **whatever window is at the
scroll point gets the wheel and gets photographed.** Wheel input is routed by
cursor position on both platforms, and each frame is a screenshot of that
region of the screen.

So before frame 0 the driver raises the target over the scroll point and then
checks that it worked — the window at the point must be the target or another
window of the same application (an owned popup on Windows, a sheet or panel on
macOS). Failing the check is a `fatal_error`, not a warning: a run against a
covered window produces a tall, entirely plausible picture of the wrong thing,
and there is no way for the user to tell.

How it raises differs, and macOS has less to work with:

| | Windows | macOS |
|---|---|---|
| Already at the point | proves itself in the verification below | short-circuits — the point came off a screenshot of the user's own desktop, so the target is usually already there, and activating anything would take their focus for nothing |
| Rung 1 | `SetForegroundWindow` — activates as well as restacks, so the target behaves as though the user clicked it | `NSRunningApplication.activate(.activateAllWindows)`, so a target that is not the app's key window comes up with the rest |
| Rung 2 | `SetWindowPos(HWND_TOP, SWP_NOACTIVATE)` — Z-order without focus, which is all the capture strictly needs | none. There is no `SetWindowPos` for another process's windows, and raising one specific `AXWindow` means guessing which of an app's windows we photographed |
| Refusals | the foreground lock (see the chain below) | macOS 14+ may refuse a cross-application activation outright |

On Windows the second rung carries the common case unaided: restacking a
window that is not the foreground one is not gated by the foreground lock, so
a target buried under an *inactive* window comes up even when
`SetForegroundWindow` was refused. What neither rung can do without foreground
rights is get past a window that holds the foreground itself. Those rights
arrive along a chain, and every link is required:

| Link | Where | Why |
|---|---|---|
| Shell → capturer | `ScreenCaptureService`, on each spawn | The overlay needs focus the instant it appears (Esc, shortcuts). |
| Capturer → shell | `hide_overlay_for_action`, which every action dispatch and `finish_cycle` go through **before** the overlay hides. Addressed with `--shell-pid` (§1.1) | The shell has no visible window at this moment, so once ours goes away it holds nothing to pass on. |
| Shell → driver | `ScrollDriver.RunAsync`, right after `Process.Start` | A freshly spawned process is refused `SetForegroundWindow` outright. |

Deliberately **not** used: `AttachThreadInput` to borrow the foreground
thread's input queue. It defeats the lock reliably, and it also couples the
driver to another application's input state — a driver left attached to a
hung foreground thread is a much worse outcome than a capture that declines
to run. macOS has no such chain: there is nothing to hand on and nothing to
borrow, which is why the verification is the whole of the guarantee there.

During the run, the cursor stays parked on the scroll point, and moving it
more than 10 units (px or points, per the region's space) **pauses** the run
rather than ending it — a wheel injected
while the user is pointing somewhere else scrolls whatever they are pointing
at, and a frame taken while they are moving picks up hover highlights and
selections it can never lose again. A pause emits `status.state = paused`,
holds until the cursor has been still for 3 s, then parks it back on the
scroll point and resumes; the frame captured across the disturbance is
discarded and retaken without an extra wheel burst. After 1 s of stillness
the state switches to `resuming` and ticks `resume_in_s` down each second, so
the cursor is never taken back without warning — any movement drops straight
back to `paused`. `stop`, `cancel`, Esc and
the target window going away are all honoured while paused. Paused time is
excluded from the wall-clock cap. One pause may last 60 s — reachable only by
a cursor that keeps *moving* that whole time — after which the run finalizes
as `stopped` with everything captured so far.

## 3. Text recognizer (`clowd_ai ocr`)

The only protocol here that the shell is not a party to. When the user presses
OCR, the overlay extracts the selected region's pixels — compositing a
click-locked peek if one is up, so what is recognized is what the user can
actually see — spawns `clowd_ai ocr`, and waits for it on the detached `ocr`
worker thread. **One request, one process, one answer.**

It is a **separate binary** because recognition runs on a large native engine
(PaddleOCR models on ONNX Runtime, via the `ort` crate). A Rust panic in it
would unwind harmlessly, but an `abort`, a segfault or a refused allocation on
a degenerate selection kills the process it is running in — which in-process
meant the overlay, mid-capture, with the user's selection already framed.
Out-of-process the same failure is an exit code the capturer turns into an
"OCR failed" pill. It is also the licence boundary: `clowd_ai` embeds GPL-3.0
matting weights and is GPL-3.0 itself, which the process boundary keeps out of
the MIT overlay.

Two things follow from that split, both improvements rather than costs:
cancelling (BACK) is killing a process rather than polling a flag between
inference batches, so a superseded request can no longer hold the engine while
the next one queues behind it; and the tens of MB of embedded models plus the
static runtime left the overlay, which is spawned fresh for every capture and
therefore pays for its own size in start-up latency.

It ships beside `clowd_capture_wgpu` on every platform that has an ONNX
Runtime build — Windows x64/arm64 and Apple Silicon, **not** Intel macOS,
where the spawn fails and the overlay shows OCR as unavailable — which is
where `src/ocr/client.rs` looks for it (sibling of `current_exe()`; the
`CLOWD_AI_BINARY` environment variable overrides that, for tests and local
development). It needs no window, no event loop, no GPU, and — because it is
handed pixels rather than taking them — no screen-recording permission.

### 3.1 Spawn

```
clowd_ai ocr --out <path> [--log-file <path>]
```

| Flag | Required | Meaning |
|---|---|---|
| `--out` | yes | Where to write the response (§3.3). The capturer puts this in the capture's session directory as `ocr.json` when there is one, and in the temp directory otherwise — OCR has no `--session-dir` requirement, since COPY and SEARCH need no shell round-trip. It reads the file only after the process exits 0, and deletes it either way. |
| `--log-file` | no | Mirrors the log (det/rec timings, the tier choice) to a file. The capturer passes `<session-dir>/ocr.log` when it has a session; that file is deliberately *not* cleaned up, because it is the artefact a "why was OCR slow" report is diagnosed from. |

Stdio, all three of which the capturer sets deliberately:

| Stream | Setting | Why |
|---|---|---|
| stdin | pipe | Carries the request (§3.2). |
| stdout | **null** | The `ocr` subcommand writes nothing to stdout — the answer is the `--out` file, which doubles as the session's `ocr.json` artefact, and a native runtime that printed to stdout (the original MNN engine did) could never corrupt a file. The overlay's own stdout is the NDJSON protocol of §1, so the child's is nulled rather than inherited; a pipe would work but one nobody drains can eventually block the child. |
| stderr | inherited | Log chatter, by the same convention §2.2 follows. It lands in the overlay's stderr, which the shell already pumps into its diagnostics. |

On Windows the child is spawned with `CREATE_NO_WINDOW` — the overlay is a
GUI-subsystem process with no console, so a console-wanting child would flash a
brand new window on top of a fullscreen capture.

### 3.2 Request (capturer → recognizer, stdin)

One `RequestHeader` as a single JSON line, then the raw BGRA pixels —
`width * height * 4` bytes, tightly packed — through to EOF. Closing stdin is
what starts recognition.

```json
{"width":3440,"height":1440,"origin":{"x":-500,"y":-300,"width":3440,"height":1440}}
```

| Field | Meaning |
|---|---|
| `width`, `height` | Dimensions of the pixel payload. The recognizer checks the payload length against them and fails loudly on a mismatch — both sides of a private protocol disagreeing is a bug, not a bad capture. |
| `origin` | The rect the crop **actually** covers, after `extract_selection_bgra` clamped it to the desktop bitmap. Result rects are offset by it; offsetting by the selection instead would misplace every bubble on a negative-origin multi-monitor layout. |

The pixels never touch the disk. They are a picture of the user's screen, and a
temp file holding one outlives a killed process. A 3440x1440 selection is
19.8 MB, uploaded in 1 MB chunks so the cancel flag is polled throughout.

### 3.3 Response (`--out` file)

The JSON `OcrResponse` — `Result<OcrOutcome, OcrError>`, so `{"Ok":…}` or
`{"Err":…}`. Rects use the same explicit `{x,y,width,height}` shape as
`session.json` (§1.3).

```json
{"Ok":{"lines":[{"text":"hello","rect":{"x":-12.5,"y":7.25,"width":112.5,"height":14.25}}],"full_text":"hello","text_angle":0.0}}
```

A recognition that ran and failed is part of the answer, not a failure of the
process: it is written as `{"Err":"Unavailable"}` or `{"Err":{"Failed":"…"}}`
and the process still exits 0. That is what lets the capturer tell "the engine
does not work on this machine" apart from "the child died", which is all a
non-zero exit can mean.

### 3.4 Exit codes and failure handling

| Code | Meaning to the capturer |
|---|---|
| 0 | The response file is there and can be trusted. |
| anything else | The child crashed or could not write. The file is not read. Reported as `OcrError::Failed`, and logged at `error!` — which is the path by which the crashes this split exists to isolate reach Sentry, since no panic hook runs for an `abort`. |

The capturer additionally enforces a 30 s ceiling and kills the child if it is
exceeded. That is not a latency target — a dense 3440x1440 desktop measures
about 1.1 s end to end — but the `Scanning` phase has no other way out, and
without it a child that hung instead of exiting would leave the user under the
sweep animation indefinitely.

The recognizer has **no Sentry client** of its own: it runs once per OCR
press rather than once per app run, so release-health sessions would measure
key presses rather than app runs — and the session envelope measured ~100 ms
on a process that lives for about a second. The capturer reports on its
behalf: an abnormal exit is logged at `error!` with the exit code, and a Rust
panic in the child leaves its message (file and line) in the response file
from a panic hook, which the capturer reads only for that message and appends
to the report instead of a bare exit code.
