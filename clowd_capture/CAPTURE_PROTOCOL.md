# clowd_capture protocol

The contract between the Clowd.Ui shell and the `clowd_capture_wgpu` capture
process. Two modes share all capture behavior and the on-disk session format:

- **One-shot**: the shell spawns one process per capture with CLI flags; the
  process shows the overlay, writes its result into `--session-dir`, and exits.
  Completion signal = process exit.
- **Persistent** (`--persistent`): the shell keeps one warmed-up process
  resident and drives captures over an NDJSON stdin/stdout protocol.
  Completion signal = the `finished` event. The session directory format is
  identical — large payloads never ride the pipe.

Source of truth: `src/settings.rs` (CLI), `src/session_output.rs` (session
files), `src/host/protocol.rs` + `src/host/stdin.rs` (wire protocol),
`src/system/mod.rs` (exit codes). C# counterparts: `ScreenCaptureService.cs`
(one-shot + session dispatch), `CaptureProcessHost.cs` (persistent host).

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

**Write ordering** (`session_output.rs`) — the last file to appear is the
completion signal, so readers must wait for process exit (one-shot) or the
`finished` event (persistent) and then key off these files:

1. EDIT/UPLOAD: the three PNGs are written first (in parallel), then
   `action.txt` is written (upload) or removed (edit), then **`session.json`
   strictly last**. `session.json` present = capture succeeded.
2. VIDEO: `cropped.png` first, then **`action.txt` last**. Its appearance is
   the completion signal; no `desktop.png`, no `session.json` (the session is
   created by Clowd.Ui when recording finishes).
3. SELECT-COLOR: `action.txt` only.
4. Neither `session.json` nor `action.txt` present = the capture was
   cancelled; the shell deletes the pre-created directory.

The VIDEO rect is emitted in the platform capture coordinate space: physical
pixels (virtual-desktop, possibly negative origin) on Windows, CG points on
macOS — passed verbatim to obs-express `--region`. W and H are always >= 2.

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
| `finished` | `action` | The capture cycle ended and any session payload is already on disk (§1.2). Exactly one per accepted `show`. `action` ∈ `edit` \| `upload` \| `select_color` \| `video` \| `copy` \| `save` \| `cancelled`. |
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
