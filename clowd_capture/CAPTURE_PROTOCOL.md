# clowd_capture protocol

The contract between the Clowd.Ui shell and the Rust capture processes. The
capture overlay `clowd_capture_wgpu` has two modes, which share all capture
behavior and the on-disk session format:

- **One-shot**: the shell spawns one process per capture with CLI flags; the
  process shows the overlay, writes its result into `--session-dir`, and exits.
  Completion signal = process exit.
- **Persistent** (`--persistent`): the shell keeps one warmed-up process
  resident and drives captures over an NDJSON stdin/stdout protocol.
  Completion signal = the `finished` event. The session directory format is
  identical — large payloads never ride the pipe.

A **separate binary**, `clowd_scroll_driver` (§3), carries out the second half
of a scrolling capture the overlay already set up. It is not a capture overlay
at all — no window, no event loop, no GPU — and shares the session format with
the capturer and nothing else. It is documented here because it speaks to the
same shell across the same boundary.

Source of truth: `src/settings.rs` (CLI), `src/session_output.rs` (session
files), `src/host/protocol.rs` + `src/host/stdin.rs` (wire protocol),
`clowd_scroll_driver/src/drive.rs` (scrolling-capture driver), and
`clowd_rust_core` for what the two binaries must agree on — the `session.json`
shape (`session.rs`), the coordinate space (`geometry.rs`) and the exit codes
(`exit.rs`). C# counterparts: `ScreenCaptureService.cs` (one-shot + session
dispatch), `CaptureProcessHost.cs` (persistent host), `Scroll/ScrollDriver.cs`
+ `Scroll/ScrollDriverProtocol.cs` (driver).

## 1. One-shot mode

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
| `--capture-mode` | `region` \| `screen` \| `window` | `region` | `region` = free crosshair; `screen`/`window` pre-select the active monitor / foreground window and show the action panel. |
| `--video` | flag | off | Video-region picker: first confirmed selection dispatches the VIDEO action immediately. Requires `--session-dir`. |
| `--memory-hints` | `lower-memory-usage` \| `max-performance` | `lower-memory-usage` | GPU allocator strategy, read once at device creation (process-level, applies in both modes). `lower-memory-usage` keeps the allocator's retained heap blocks small so an idle persistent host holds minimal memory; a running host must be relaunched for a change to take effect. |
| `--persistent` | flag | off | Persistent host mode (§2). The per-capture flags above (except `--memory-hints`) are ignored; settings arrive per `show`. |
| `--log-dir` | path | none | Persistent mode only: directory for `capture-host.log`. |

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
| COPY / SAVE | none — handled inside the capturer (clipboard / save dialog) | — |
| Cancelled (Escape / close) | none | — |

File contents:

| File | Contents |
|---|---|
| `desktop.png` | Full virtual-desktop bitmap, locked peek window composited, never the cursor (the editor toggles cursor visibility itself). |
| `cropped.png` | Preview of the selection, peek composited; cursor composited only if visible to the user. For VIDEO: no peek compositing (the recording shows real obstructions). |
| `cursor.png` | Desktop crop at the cursor rect with the cursor composited. Absent when no cursor was captured or the OS reported it hidden. |
| `session.json` | Session metadata (§1.3). |
| `action.txt` | Routing marker read by `CaptureSessionDispatcher` (missing = edit). |
| `capture.log` | Mirror of the capturer's log (one-shot mode only), read by the shell after a non-zero exit. |
| `scroll.log` | Same, for the `clowd_scroll_driver` pass (§3). A separate name because the driver runs in a directory the overlay already wrote `capture.log` into, and truncating that would erase the diagnostics for the half of the capture that came first. |

**Write ordering** (`session_output.rs`) — the last file to appear is the
completion signal, so readers must wait for process exit (one-shot) or the
`finished` event (persistent) and then key off these files:

1. EDIT/UPLOAD: the three PNGs are written first (in parallel), then
   `action.txt` is written (upload) or removed (edit), then **`session.json`
   strictly last**. `session.json` present = capture succeeded.
2. VIDEO: `cropped.png` first, then **`action.txt` last**. Its appearance is
   the completion signal; no `desktop.png`, no `session.json` (the session is
   created by Clowd.Ui when recording finishes).
3. SELECT-COLOR / SCROLL: `action.txt` only.
4. Neither `session.json` nor `action.txt` present = the capture was
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
`ScreenCaptureService.cs` / `CaptureProcessHost.cs`.

| Code | Meaning |
|---|---|
| 0 | Every normal outcome — edit, upload, color, video, copy, save, **and cancel**. The shell distinguishes them by the session files, not the code. |
| 3 | `EXIT_NO_SCREEN_PERMISSION` — the OS has not granted screen capture (macOS Screen Recording). The shell shows the permission dialog instead of a crash report. |
| 4 | `EXIT_CAPTURE_FAILED` — the desktop screenshot itself failed. The reason is in stderr/`capture.log`, not a stack trace. |

Any other non-zero exit is a crash; the shell reports it with the stderr and
`capture.log` tails.

## 2. Persistent mode

### 2.1 Spawn

```
clowd_capture_wgpu --persistent --log-dir <dir>
```

with stdin/stdout/stderr redirected (and `CreateNoWindow` on Windows). The
process warms up everything slow — adapter enumeration, per-monitor devices,
pipelines, hidden overlay windows, configured surfaces — then parks. Each
capture only costs the fast work (screenshot, texture upload, show), reported
via `shown.elapsed_ms`.

Logging: stderr plus `<log-dir>/capture-host.log` (truncated on start; the
previous run is kept as `capture-host.log.1`). No per-session `capture.log`
is written in this mode. `stdout` carries **nothing but protocol lines**.

### 2.2 Framing

One JSON object per line ("NDJSON"), UTF-8, `\n`-terminated, flushed per
line. Parent writes `HostCommand`s to the child's stdin; child writes
`HostEvent`s to its stdout.

- **Chatter rule** (parent side): a stdout line whose trimmed form starts
  with `{` and ends with `}` is an event; anything else is chatter and goes
  to a bounded log ring. Unknown `type` values are logged, not fatal.
- **Child side**: blank stdin lines are skipped, a leading UTF-8 BOM is
  stripped, and unparseable commands are warned about and ignored — garbage
  never kills a warm host.
- Payloads stay small: screenshots and session JSON travel on disk in the
  per-capture `session_dir` exactly as in one-shot mode (§1.2).

### 2.3 Commands (parent → child)

`#[serde(tag = "type", rename_all = "snake_case")]` — see
`HostCommand` in `src/host/protocol.rs`.

| Command | Fields | Semantics |
|---|---|---|
| `show` | see below | Start a capture cycle. Ignored (with a warning, no event) while a cycle is already active. |
| `cancel` | — | End the active cycle as if the user pressed Escape (`finished` with `action: "cancelled"`). No-op when idle. |
| `ping` | — | Liveness probe; answered with `pong`. |
| `shutdown` | — | Cancel any active cycle and exit cleanly (exit code 0). |

`show` fields mirror the CLI one-to-one and every default matches the CLI
default, so `{"type":"show"}` produces the same overlay a bare one-shot
launch would. Note the polarity: `peek`/`cursor` are positive here where the
CLI has `--no-peek`/`--no-cursor`.

| Field | Type | Default | CLI counterpart |
|---|---|---|---|
| `session_dir` | string (path) | none | `--session-dir` |
| `accent_color` | string, `#RRGGBB` / `#RRGGBBAA` | `#2F7CAE` | `--accent-color` |
| `tips_mode` | `hints` \| `tips` \| `off` | `hints` | `--tips-mode` |
| `peek` | bool | `true` | `--no-peek` (inverted) |
| `peek_threshold` | number 0.0–1.0 | `0.80` | `--peek-threshold` |
| `cursor` | bool | `true` | `--no-cursor` (inverted) |
| `capture_mode` | `region` \| `screen` \| `window` | `region` | `--capture-mode` |
| `video` | bool | `false` | `--video` |

Examples:

```json
{"type":"show","session_dir":"C:\\ProgramData\\Clowd\\Sessions\\42","accent_color":"#2F7CAE","tips_mode":"hints","peek":true,"peek_threshold":0.8,"cursor":true,"capture_mode":"region","video":false}
{"type":"cancel"}
{"type":"ping"}
{"type":"shutdown"}
```

### 2.4 Events (child → parent)

| Event | Fields | Semantics |
|---|---|---|
| `ready` | `warmup_ms` (u64), `monitors` (count) | Emitted once, when every render worker has parked (device, pipelines, surface ready). A `show` will now be fast. `warmup_ms` is measured from process start. |
| `shown` | `elapsed_ms` (u64) | The overlay windows are on screen. Exactly one per accepted `show`; `elapsed_ms` is measured from the `show` command. |
| `finished` | `action` | The capture cycle ended and any session payload is already on disk (§1.2). Exactly one per accepted `show`. `action` ∈ `edit` \| `upload` \| `select_color` \| `video` \| `scroll` \| `copy` \| `save` \| `cancelled`. |
| `pong` | — | Answer to `ping`. |
| `display_changed` | — | The monitor topology changed (or a GPU device was lost) under the warm state; the process exits right after with code 5 or 6. Informational — the parent keys its respawn policy off the exit code. |
| `fatal_error` | `message` (string) | Something unrecoverable happened to the active cycle (e.g. the desktop screenshot never arrived within its 30 s deadline). The cycle is cancelled; a `finished` (`action: "cancelled"`) always follows, so a waiting parent is never left hanging. |

Examples:

```json
{"type":"ready","warmup_ms":850,"monitors":2}
{"type":"shown","elapsed_ms":38}
{"type":"finished","action":"edit"}
{"type":"pong"}
{"type":"display_changed"}
{"type":"fatal_error","message":"timed out waiting for the desktop screenshot"}
```

### 2.5 Lifecycle

Child states, driven by commands:

```
spawn --warm-up--> ready ----show----> busy (overlay on screen)
        |            ^                   |
        |            +----finished-------+   (user action / cancel / fatal_error)
        |
        +--> exit 3  (no screen permission)
ready/busy --shutdown / stdin EOF--> exit 0
ready/busy --topology change-------> display_changed, exit 5
ready/busy --GPU device lost-------> display_changed, exit 6
```

- Per accepted `show`, the child emits exactly one `shown` and then exactly
  one `finished`. `show` while busy is ignored; `cancel` while idle is a
  no-op.
- On `show` the child re-verifies the monitor topology first; a mismatch
  emits `display_changed` and exits 5 **before** showing anything (the
  parent cold-spawns that capture and respawns the host).
- Between cycles the child sits in a `Wait` event loop at ~0% CPU.

**Stdin EOF = child exits.** The parent holds the stdin pipe open for its
whole life, so EOF means the parent is gone (cleanly or not): the child
cancels any active cycle, hides its windows, and exits 0. A crashed or
force-killed Clowd.Ui can therefore never orphan a capture host.

### 2.6 Exit codes and respawn expectations

| Code | Meaning | Parent behavior (`CaptureProcessHost`) |
|---|---|---|
| 0 | `shutdown`, stdin EOF, or event-loop unwind | Nothing when the parent asked for it (`StopAsync`); an exit 0 the parent did *not* request is an unexpected death and follows the backoff row below. |
| 3 | Screen-recording permission revoked | Give up — only an app restart can pick the permission up; captures use the cold path. |
| 5 | `EXIT_DISPLAY_CHANGED` — monitor topology changed under the warm state | Respawn immediately, no backoff, no failure penalty. |
| 6 | `EXIT_GPU_LOST` — a worker's wgpu device was lost (driver reset/update) | Same as 5; distinct only for logs. |
| 4 / other | Screenshot failed / crash | Respawn with backoff 1/2/5/10/30 s; after 5 consecutive deaths without a `ready` in between, give up and report once (Sentry `capture.host-crash`). Any `ready` resets the streak. |

Parent-side timing contract:

- `AllowSetForegroundWindow(child pid)` is called per `show`, from the
  hotkey context (the grant is consumed by a single use).
- `shown` is awaited with a **5 s ack timeout**; a miss kills the child and
  the capture falls back to a cold one-shot spawn with the same session dir.
- `finished` is awaited with **no timeout** (the user may sit in the overlay
  indefinitely); the wait is faulted only by child death. Death after
  `shown` is reported as a capture crash — never retried from cold.
- While the child is idle the parent pings every 60 s; two consecutive
  missed pongs mean a wedged child, which is killed (and respawned).

## 3. Scrolling-capture driver (`clowd_scroll_driver`)

The driver is the second half of a scrolling capture. The first half is an
ordinary one-shot cycle: the user selects a region, presses SCROLL, clicks the
point to scroll at, and the overlay exits leaving the `scroll X,Y,W,H PX,PY
HWND` marker of §1.2. `CaptureSessionDispatcher` turns that marker into
`CaptureAction.Scroll`, `ScrollCapturePage` puts its border window up around
the region, and spawns the driver to do the mechanical part.

It is a **separate binary**, not a mode of the capturer. Nothing it does needs
a window, an event loop, a GPU or the screen-recording permission dance, and
bringing any of that up would put pixels on screen in front of the content it
is about to photograph. It ships beside `clowd_capture_wgpu`, which is where
`CaptureBinaryLocator.ResolveScrollDriver` looks for it.

**Windows-only**; elsewhere it logs and exits 4 (the SCROLL button is compiled
out, so nothing should ever route there).

### 3.1 Spawn

```
clowd_scroll_driver --session-dir <dir> --region X,Y,W,H --point PX,PY --hwnd N
```

| Flag | Required | Meaning |
|---|---|---|
| `--session-dir` | yes | The directory the overlay was given. It is empty by the time the driver starts (the shell consumed `action.txt`), and the driver owns it from here. |
| `--region X,Y,W,H` | yes | The rect to photograph, physical virtual-desktop px — the same space and the same numbers as the marker. Rejected if W or H is 0. |
| `--point PX,PY` | yes | Where the cursor is parked and the wheel is aimed, same space. Re-clamped into `--region`; a point outside it is a caller bug and is logged. |
| `--hwnd N` | no (default 0) | Decimal top-level handle from the marker. Re-validated at drive time (`IsWindow` + `GetAncestor(GA_ROOT)` + rect contains the point) because the overlay's Z-order snapshot predates its own window; `0` or a stale handle falls back to `WindowFromPoint`. |

The shell must redirect **all three** stdio streams. Stdin in particular: the
driver reads a closed stdin as "the shell is gone" and cancels, which is what
keeps a crashed Clowd.Ui from leaving something scrolling the user's window.
The shell also calls `AllowSetForegroundWindow(driver pid)` right after spawn —
without it `SetForegroundWindow` in the driver is refused and the run relies on
Win10+ scroll-inactive-windows routing alone.

### 3.2 Events (driver → shell)

One JSON object per line on stdout, and **only** protocol lines: the terminal
logger is routed to stderr for exactly that reason, so the shell
may treat any line starting `{` and ending `}` as an event (the same rule as
§2.2).

| Event | Fields | Semantics |
|---|---|---|
| `ready` | — | The target is resolved and focused; scrolling is about to start. |
| `status` | `frames` (u32), `height_px` (u32), `state` | Progress. Emitted at each phase change of every step, so up to three per step. `state` ∈ `scrolling` \| `settling` \| `stitching`. |
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
| `stopped` | Esc, the cursor moving off the parked point, a `stop` command, the target window closing/moving, or the stitcher giving up. The partial capture is kept. | yes |
| `max_reached` | A hard cap: 120 frames, 20,000 px of composite, or 120 s wall clock. | yes |
| `no_movement` | Nothing the driver could inject ever moved the target — most often an elevated window silently eating `SendInput`. Reported whatever else ended the run, and a single-screen session is still written. | yes |
| `failed` | Defined for completeness; the driver has no failure it can recognise *after* there is content worth keeping, so failures go out as `fatal_error` instead. The shell must still handle it. | no |

### 3.3 Commands (shell → driver)

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

### 3.4 Output and exit

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
means a crash (or exit 4 on macOS, where the mode does not exist), and the
shell reports it with the stderr tail attached.
