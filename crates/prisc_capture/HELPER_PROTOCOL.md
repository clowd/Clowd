# Prisc helper protocol (v1)

The one contract between a parent (Prisc.Ui, or prisc_capture spawning prisc_ai) and every child
process Prisc ships. It replaces the four stdout dialects in use today (recorder JSONL, share
JSONL, vid2gif/VideoRender text, standby `PRISC_*` lines) and the three stdin dialects (recorder
text, share text, scroll/standby NDJSON).

Status: **spec, not implemented.** Until the migration in §8 lands, the per-helper docs
(`CAPTURE_PROTOCOL.md` §1.2/§2.2/§2.3, `crates/prisc_recorder/README.md`, the header of
`crates/prisc_share_region/src/main.rs` and `crates/prisc_vid2gif/src/main.rs`) describe what
ships. `CAPTURE_PROTOCOL.md` stays authoritative for everything that is not stdio: CLI flags,
the session-directory files, `session.json`, the OCR request header.

Scope:

| Helper | Catalog (§4) | stdin | stdout | Notes |
|---|---|---|---|---|
| `prisc_recorder` | `recorder` | JSONL commands | JSONL events | also `--list-cameras` (one-shot query) |
| `prisc_share_region` | `share_region` | JSONL commands | JSONL events | |
| `prisc_vid2gif` | `job` | JSONL commands | JSONL events | |
| `Prisc.VideoRender` (C#) | `job` | JSONL commands | JSONL events | the only C# child |
| `prisc_scroll_driver` | `scroll` | JSONL commands | JSONL events | result also in `session.json` |
| `prisc_capture` (standby and one-shot) | `capture` | JSONL commands | JSONL events | one-shot keeps its file outputs |
| `prisc_ai` | none | binary payload | binary payload | adopts §3 lifetime, §5 exit codes, the stdout guard |

## 1. Decisions

1. **Commands become JSON too.** Every line on stdin is one JSON object with a `type`. The
   recorder's `configure <path>` grammar (rest-of-line path, no quoting) becomes
   `{"type":"configure","path":"..."}`, which removes the only reason the text grammar needed
   special cases. The churn is three parsers (recorder, share_region, vid2gif/render) that are
   rewritten anyway; one grammar for both directions is worth it.
2. **One envelope**: a flat JSON object, `"type"` first, snake_case type names and snake_case
   field names everywhere. The recorder's `timeMs` / `droppedPerc` become `time_ms` /
   `dropped_perc`.
3. **Every line-protocol child says `hello` first**, carrying the envelope `protocol` and a
   generated fingerprint of its catalog (§2.5). The parent compares both with its generated
   constants; any mismatch is a hard failure, not a negotiation (the helpers ship inside the
   app, so a mismatch is a broken install or a stale dev build).
4. **One terminal rule, no exceptions.** Every run ends with exactly one terminal event (`done`,
   or the catalog's named terminal event) or a fatal `error`, on **every** exit path the helper
   controls: normal completion, cancel, `quit`, user dismissal. A process that exits without
   one is a crash, whether or not the parent asked it to stop (§7.5). This now holds for
   share_region, scroll `quit`, standby and one-shot capture too (they gain terminal events).
5. **`quit` == stdin EOF == SIGINT/SIGTERM == stdout EPIPE.** All of them go through one
   callback in the child. Parent death is detected through stdin EOF and EPIPE (both pipes
   close when the parent dies, on every platform), plus the Windows job (§7.4); there is no
   parent-PID watchdog. What "quit" means per helper is fixed in §4 (recorder: stop and flush;
   job and scroll: cancel; share: stop; standby: exit between captures).
6. **Cancel is spelled `cancelled`** (the majority spelling). VideoRender's `canceled` goes away.
   Known bug fixed by the migration: today Rust vid2gif prints `cancelled` while
   `Vid2GifRunner` matches `canceled` (`Vid2GifRunner.cs` ~369), so GIF cancel has never been
   recognised as a cancel. Do not assume the old cancel path was ever exercised.
7. **Single source of truth: Rust codegen** (§6). Message types are declared once in
   `prisc_ipc`, and the C# records are generated from them by a cargo test, the same pattern as
   `prisc_hotkeys/src/csharp.rs` → `HotkeyKey.g.cs`.
8. **Dependency direction: `prisc_ipc` is a leaf; `prisc_rust_core` depends on it** and
   re-exports `exit`. Neither crate depends on libobs, so `prisc_capture` stays libobs-free.
   `prisc_recorder`, `prisc_share_region` and `prisc_vid2gif` gain a `prisc_rust_core`
   dependency for telemetry.
9. **One env var** for finding helpers, `PRISC_HELPERS_DIR`, a path list (§7.1). Every
   per-helper override (`PRISC_RECORDER_PATH`, `PRISC_SHARE_REGION_PATH`, `PRISC_VID2GIF_PATH`,
   `PRISC_VID_RENDER_PATH`, `PRISC_CAPTURE_PATH`, `PRISC_AI_PATH`, `PRISC_AI_BINARY`,
   `PRISC_HOTKEYS_PATH`) is removed. `PRISC_FFMPEG_PATH` survives: it names a library
   directory, not a helper.
10. **One C# project, `ui/Prisc.Ipc`**, with no package references, so Prisc.Ui, Prisc.VideoSDK
    and Prisc.VideoRender can all reference it. (Prisc.Shared is out: it pulls in Avalonia, which
    VideoSDK and VideoRender must not.)

## 2. Wire format

### 2.1 Framing

- UTF-8, no BOM, one JSON object per line, `\n` terminated (a trailing `\r` is stripped on
  read). Max line length 1 MiB in both directions; a longer line is discarded and reported as
  `bad_command` (stdin) or logged and skipped (stdout).
- stdout carries protocol lines and **nothing else**. Logs, panics and native-library noise go
  to stderr (enforced by the stdout guard, §3.2).
- Every object's first key is `"type"`. Readers must not depend on key order beyond that, and
  must ignore unknown fields.
- `Option` fields are **omitted** when absent (never `null`). Readers treat a missing key and
  `null` the same.
- Floats are always finite. The Rust serializer for catalog `f64` fields maps NaN/inf to `0.0`
  (with a `debug_assert!`); helpers clamp meaningfully before emitting (levels floor at
  `-100.0`, as today). Rust writes whole-number floats as `-100.0`; the C# writer writes
  `-100`. Both are valid and readers accept either (§6, conformance).
- Integers: `u32`/`u64`/`i32`/`i64` as JSON numbers. Paths are strings (lossy-UTF-8 on the Rust
  side, as today).

### 2.2 Common events (every line-protocol helper)

```json
{"type":"hello","protocol":1,"catalog":"recorder","catalog_hash":"9f3c0a7e12b4d5e6","helper":"prisc_recorder","version":"4.11.0","pid":4242}
{"type":"error","code":"bad_command","message":"unknown command 'mvoe'","fatal":false}
```

| Event | Fields | Rules |
|---|---|---|
| `hello` | `protocol` u32, `catalog` string, `catalog_hash` string, `helper` string, `version` string, `pid` u32 | First stdout line, emitted by `prisc_ipc::open` before any other work (before libobs/FFmpeg/ONNX are initialised or called), so it arrives in milliseconds. `catalog_hash` is §2.5. `helper` is the binary stem (`prisc_recorder`, `prisc_share_region`, `prisc_vid2gif`, `Prisc.VideoRender`, `prisc_scroll_driver`, `prisc_capture`). `version` is the build's release version, for logs only: Rust uses `option_env!("PRISC_VERSION")` with the same fallback as `telemetry::release`; C# uses the assembly's informational version. |
| `error` | `code` string, `message` string, `fatal` bool | `message` is one line (`single_line`), display text. `code` is the stable twin to branch on. `fatal:true` is terminal: the helper exits right after it, with exit code 2 for `usage` and 1 for every other code. `fatal:false` never changes state; the helper keeps running. |

Error codes (closed set; a parent that sees an unknown code falls back to `fatal` and the text):

| `code` | Meaning |
|---|---|
| `bad_command` | stdin line not JSON, unknown `type`, missing/invalid field. Never fatal. |
| `rejected` | A well-formed command the helper refused in its current state (standby: rejected settings snapshot, a `capture` while one is open; recorder: `pause` before the output started). Never fatal. |
| `usage` | Arguments that parsed but cannot be used, found after `hello` (recorder: monitor not found, webcam without `--multi-track`, bad audio arguments; share_region `mirror.rs` `fail_args`). Always fatal, exit 2. |
| `init_failed` | Setup failed before the helper became useful (libobs, FFmpeg open, no window at scroll point, standby hotkey hook unavailable at startup). |
| `io_failed` | Reading/writing a file or directory failed (session dir, output file). |
| `permission` | An OS permission is missing (macOS Screen Recording, Accessibility). |
| `failed` | Any other failure, including a well-formed command that failed to apply (share_region `move` whose `set_region` errors, recorder pause/unpause that libobs refuses). |

### 2.3 Common commands

```json
{"type":"quit"}
```

`quit` is handled by `prisc_ipc` itself and never reaches a helper's command enum: it fires the
same quit callback as EOF and signals (§3.3). It is idempotent. Ordering with earlier commands:
`read_commands` calls `lifetime::trigger(Command)` on the stdin thread after `sink` has
returned for every earlier line, so a helper whose commands drain on another thread (recorder,
share_region, standby) keeps arrival order by having `on_quit` post a quit message into **the
same queue** `sink` feeds. A `move` followed by `quit` is applied before the stop.

### 2.4 Unknown-command policy

Malformed JSON, an unknown `type`, or bad fields produce `error{code:"bad_command",fatal:false}`
**written by the helper's own command-draining thread, in arrival order with the acks** (the
share_region rule, `commands.rs` `Command::Error`: a rejection written from the reader thread
can overtake the ack of the previous command). It is also logged to stderr. Empty lines are
skipped silently.

### 2.5 Versioning

Two values in `hello`, both checked by the parent; either mismatch fails the start with
`HelperFailure.ProtocolMismatch`:

- `protocol` (`PROTOCOL = 1`) versions the envelope itself: framing, `hello`, `error`, `quit`.
  It is bumped by hand and should almost never change.
- `catalog_hash` is a **generated fingerprint** of the helper's catalog: 64-bit FNV-1a, lower-case
  hex, over the canonical text rendering of the catalog's `SPEC` (§6): every event, command,
  field name, field type, string-enum value and terminal flag, in declaration order. The Rust
  side computes it from `SPEC` at runtime; the generator writes the same value into the C#
  `HelperSpec` as a constant. Any change to a catalog changes the hash, so a stale helper or
  stale parent is caught automatically with no manual bump to forget.

Because helpers ship in lockstep with the parent, there are no additive-compatibility rules to
follow. Readers still ignore unknown fields and decode an unknown enum string to `Unknown`, but
that is defensive decoding, not a versioning mechanism.

## 3. Rust crate `prisc_ipc`

`crates/prisc_ipc`, workspace member and default-member. Dependencies: `serde`, `serde_json`,
`ctrlc` (posix signals), `libc` (unix), `windows` (Console, Foundation). No `log`: its own
diagnostics are `eprintln!` (the recorder and share_region do not use `log`).

```rust
pub const PROTOCOL: u32 = 1;

pub mod exit;        // §5, moved from prisc_rust_core::exit
pub mod catalog;     // §4, macro-generated message types, one module per catalog
mod csharp;          // §6 generator + drift test

// --- stdout --------------------------------------------------------------------------
/// Claims stdout (guard, §3.2), emits `hello`, returns the process's one protocol writer.
/// Panics if called twice. Call first thing in main, before any native library is
/// initialised or called.
pub fn open<E: catalog::Event>(helper: &'static str) -> Channel<E>;

#[derive(Clone)] pub struct Channel<E> { /* Arc<Mutex<File>>, closed flag */ }
impl<E: catalog::Event> Channel<E> {
    /// Serialize, then one write_all(json + "\n") + flush under the lock. Never panics.
    /// Returns false once the pipe is gone. The first EPIPE/broken-pipe error marks the
    /// channel closed (later emits are no-ops) and, **after the lock is released**, fires
    /// lifetime::trigger(StdoutClosed).
    pub fn emit(&self, event: &E) -> bool;
    /// Emits `error`. `message` is passed through single_line. If `fatal`, then calls
    /// lifetime::exit(2) for ErrorCode::Usage and lifetime::exit(exit::FAILED) otherwise.
    pub fn error(&self, code: catalog::ErrorCode, message: impl Display, fatal: bool);
    pub fn is_closed(&self) -> bool;
}

/// The guard alone: duplicate the real stdout into a private, non-inheritable handle and
/// point fd 1 / STD_OUTPUT_HANDLE at stderr. Used by `open`, and directly by prisc_ai
/// (payload channel).
pub fn claim_stdout() -> std::io::Result<std::fs::File>;

// --- stdin ---------------------------------------------------------------------------
pub enum Inbound<C> { Command(C), Malformed { reason: String } }

/// Spawns the one stdin thread ("ipc-stdin"; a second call panics). Reads raw lines
/// (read_until b'\n'), strips "\r", strips a leading UTF-8 BOM on any line, decodes
/// lossily, parses. `{"type":"quit"}` and EOF/read error call lifetime::trigger
/// (Command / StdinClosed) and are not passed to `sink`. Returns when `sink` returns
/// ControlFlow::Break or at EOF.
pub fn read_commands<C: DeserializeOwned + Send + 'static>(
    sink: impl FnMut(Inbound<C>) -> std::ops::ControlFlow<()> + Send + 'static);

// --- lifetime ------------------------------------------------------------------------
pub mod lifetime {
    pub enum QuitReason { Command, StdinClosed, Signal, StdoutClosed }
    /// Installs, once per process: the quit callback and the signal / console-ctrl handler.
    /// `grace` arms the hard-exit timer (below). `on_quit` must only enqueue or set a flag:
    /// it must never block, never emit, and never take a lock the emitting threads hold
    /// (trigger can run on any thread, including one that just failed an emit).
    pub fn install(on_quit: impl Fn(QuitReason) + Send + Sync + 'static, grace: Duration);
    /// Fire the quit path. First call runs on_quit and arms the timer; later calls no-op.
    pub fn trigger(reason: QuitReason);
    /// Overrides the final exit primitive (recorder/share_region: platform::exit_process,
    /// which skips libobs's broken static destructors). Default std::process::exit.
    pub fn set_exit_fn(f: fn(i32) -> !);
    /// Registers a hook that `exit` runs before the exit fn. Helpers register
    /// prisc_rust_core::telemetry::shutdown here (§3.4).
    pub fn on_exit(f: fn(i32));
    /// The process's single exit path: runs every on_exit hook (in registration order,
    /// each expected to bound itself), then the exit fn. Used by Channel::error, the
    /// hard-exit timer, and every exit site in the helpers themselves.
    pub fn exit(code: i32) -> !;
}

/// Collapse all whitespace runs (including newlines) to one space and trim.
pub fn single_line(s: &str) -> String;

pub mod locate {
    /// Rust-side helper lookup (prisc_capture → prisc_ai): each directory of
    /// $PRISC_HELPERS_DIR (std::env::split_paths) + <exe>, then the directory of
    /// current_exe(). Returns None when none exists.
    pub fn sibling(stem: &str) -> Option<PathBuf>;
}
```

### 3.1 Typical helper `main`

```rust
let out = prisc_ipc::open::<catalog::job::Event>("prisc_vid2gif");
let _sentry = prisc_rust_core::telemetry::init_job("prisc_vid2gif");
prisc_ipc::lifetime::on_exit(prisc_rust_core::telemetry::shutdown_hook);
let cancel = CancelToken::new();
let c = cancel.clone();
prisc_ipc::lifetime::install(move |_| c.cancel(), catalog::job::QUIT_GRACE + SELF_EXIT_MARGIN);
prisc_ipc::read_commands::<catalog::job::Command>(|cmd| { /* job has no commands */ ControlFlow::Continue(()) });
```

### 3.2 Stdout guard

Generalised from `prisc_ai::claim_payload_stdout`, and like it built on std's
`try_clone_to_owned`, so the private handle is **never inheritable**: Unix gets
`F_DUPFD_CLOEXEC` (never a plain `dup(1)`), Windows gets `DuplicateHandle` with
`bInheritHandle = FALSE`. Otherwise a grandchild (obs-ffmpeg-mux, prisc_ai, a browser) would
inherit the protocol pipe and the parent's stdout pump would never see EOF.

Then: Unix `dup2(2, 1)`. Windows `SetStdHandle(STD_OUTPUT_HANDLE, stderr)` and the CRT
`_dup2(2, 1)` so native `printf` follows. After it, `println!`, libobs, FFmpeg, CoreML and ONNX
Runtime all land on stderr, and only `Channel::emit` (or prisc_ai's payload writer) reaches the
real pipe. When stdout is not a pipe (a developer running the binary in a terminal) the guard
still applies; the protocol stays readable because both streams reach the same terminal.

Windows caveats, stated so nobody relies on more than this:

- `_dup2(2, 1)` closes the OS handle UCRT fd 1 owned. A native library with its own CRT (static
  CRT, or a mingw-built FFmpeg on `msvcrt.dll`) keeps the old handle value in its own fd table
  and would write to a closed or reused handle. The guard therefore **assumes every native
  library shares the dynamic UCRT**. Verify that for the shipped libobs and FFmpeg DLLs (check
  their imports for `api-ms-win-crt-*` / `ucrtbase.dll` and no `msvcrt.dll`) as part of the
  recorder port, and record the result here.
- Load-time-imported DLLs run their initialisers before `main`, so "before any native library"
  means before any native library is **initialised by us or called**, not before it is mapped.
  A DLL that prints from `DllMain` is not covered (none of ours is known to).

### 3.3 Signals, parent death, hard exit

- **Signals.** POSIX: `ctrlc` with `termination` (SIGINT, SIGTERM, SIGHUP) calling
  `trigger(Signal)` then parking. Rust's default SIGPIPE=ignore stays, so a dead stdout is an
  `EPIPE`, not a kill. Windows: a directly registered `SetConsoleCtrlHandler` routine that
  calls `trigger(Signal)` and then **blocks forever** (the recorder's version, `recorder.rs`
  `console_ctrl`: the CTRL_CLOSE grace window lasts only while the routine runs). This is
  **recorder-only, best effort**, covering CTRL_C / CTRL_BREAK / CTRL_CLOSE when a developer
  runs it in a console. prisc_capture, prisc_scroll_driver, prisc_ai and prisc_share_region are
  `windows_subsystem = "windows"` in release and have no console, and even the recorder does not
  receive CTRL_LOGOFF / CTRL_SHUTDOWN once user32/gdi32 are loaded (libobs loads them). On
  Windows, **stdin EOF and the job object are the real mechanisms**; no test may expect a
  logoff or shutdown to flush through this path.
- **Parent death.** No watchdog and no `PRISC_PARENT_PID`. The parent holds the only write end
  of the child's stdin and the only read end of its stdout (both CLOEXEC / non-inheritable on
  both sides), so its death means stdin EOF (`trigger(StdinClosed)`) and, on the next emit,
  EPIPE (`trigger(StdoutClosed)`). Windows adds the job (§7.4). prisc_capture keeps its existing
  `--shell-pid` for the separate shell-liveness purpose it serves today.
- **Hard exit.** The first `trigger` arms a timer of `grace`. If the process is still alive when
  it fires, `lifetime::exit(exit::QUIT_TIMEOUT)` runs. Helpers pass their catalog `QUIT_GRACE`
  plus a 5 s `SELF_EXIT_MARGIN`, so a living parent's kill (§7.3) always fires first and the
  timer only matters for orphans. Each catalog's `QUIT_GRACE` must cover the helper's own
  longest legitimate stop path (§4.1 for the recorder).

### 3.4 Telemetry

`prisc_recorder` and `prisc_share_region` call `prisc_rust_core::telemetry::init(<stem>)`
(long-lived, one session per recording/share). `prisc_vid2gif` calls a new
`telemetry::init_job(<stem>)`: the same client with `auto_session_tracking: false`, so a
per-export process reports panics without skewing release health. `prisc_ai` stays without a
client (rationale in `CAPTURE_PROTOCOL.md` §3.4).

Every exit in the recorder and share_region today goes through `platform::exit_process`
(`_exit` / `ExitProcess`), so the Sentry guard is never dropped: sessions never end and queued
events are lost. The fix is the single exit path:

- `prisc_rust_core` adds `telemetry::shutdown(timeout)` (end the session, `exited` for code 0
  and `abnormal` otherwise, then flush) and `shutdown_hook(code)`, which calls it with a 2 s
  timeout and is a no-op when no client was initialised.
- Every helper registers `lifetime::on_exit(telemetry::shutdown_hook)` and
  `lifetime::set_exit_fn(platform::exit_process)` where it needs the raw exit.
- **Every direct `platform::exit_process` / `std::process::exit` call in the recorder and
  share_region is replaced by `prisc_ipc::lifetime::exit`**: done/stopped, done/failed, cancel,
  the share prompt closing, and the `ui/appkit.rs` and `win32.rs` paths. `platform::exit_process`
  is then called only as the registered exit fn. A grep for `exit_process(` outside that
  registration is part of the review checklist.

Limitation, stated so nobody expects more: the recorder and share_region log with `eprintln!`,
not `log`, so the Sentry log integration sees nothing from them. Sentry receives panics and
session health only. Converting them to `log` is out of scope.

## 4. Message catalog

Notation: `field: type`, `?` = optional (omitted when absent). `Rect` = `{x: i32, y: i32,
w: u32, h: u32}`. Every catalog also contains the common `hello`, `error` and `quit` (§2.2,
§2.3). Terminal events are marked **T**. `QUIT_GRACE` is the parent's wait between `quit` and
kill (§7.3).

### 4.1 `recorder` (`prisc_recorder`), `QUIT_GRACE` = 65 s

The grace is derived from the recorder's own stop path: up to `STOP_DEADLINE` (30 s) for the
output's stop signal, then up to `FLUSH_DEADLINE` (30 s) for deactivate (the mp4 flush), plus
5 s margin. `prisc_recorder` carries a `const _: () = assert!(...)` that
`catalog::recorder::QUIT_GRACE >= STOP_DEADLINE + FLUSH_DEADLINE + 5 s`, so shortening the
catalog grace or lengthening either deadline fails the build. The orphan hard-exit timer fires
at 70 s.

Commands and replies. Every `start` and `pause` gets exactly one reply; use `SendAndWaitAsync`
only for those and `configure`:

| Command | Fields | Reply |
|---|---|---|
| `start` | | `started_recording` (first start, from `--pause`); `recording_resumed` (while paused, or re-emitted when already recording); while a first start is still pending, no separate reply: the pending `started_recording` answers it; `error{failed}` if libobs refuses to unpause |
| `pause` | | `recording_paused` (also re-emitted when already paused); `error{rejected}` before the output started; `error{failed}` if libobs refuses to pause |
| `mute_speaker` / `unmute_speaker` / `mute_mic` / `unmute_mic` | `index: u32` | **never acks**: fire with `Send` |
| `configure` | `path: string` | exactly one `configure_applied` or `configure_error` |

`quit`: stop the output, flush the file, emit `done`, exit.

Events:

| Event | Fields |
|---|---|
| `initialized` | |
| `started_recording` | `tracks: Tracks`, `input_capture?: string`, `window_capture?: string` |
| `recording_paused` / `recording_resumed` | |
| `status` | `time_ms: u64`, `fps: f64`, `dropped: i64`, `dropped_perc: f64` |
| `levels` | `speaker: [f64]`, `mic: [f64]` |
| `configure_applied` | `ignored_keys: [string]` |
| `configure_error` | `message: string`, `restart_required: bool` (was `fatal`; renamed because the process does not exit) |
| `done` **T** | `result: "stopped" \| "cancelled" \| "failed"`, `code: i64` (OBS stop code), `message: string` (the fixed `stop_code_message`), `detail?: string` (was `error`), `tracks?: Tracks`, `input_capture?: string`, `window_capture?: string` |
| `cameras` **T** | `cameras: [Camera]` (only with `--list-cameras`) |

`Tracks` = `{screen: VideoTrack, webcam?: VideoTrack, audio: [AudioTrack]}`;
`VideoTrack` = `{index: u32, width: u32, height: u32}`;
`AudioTrack` = `{index: u32, kind: "speaker" | "microphone" | "mixed", device?: string, name: string}`;
`Camera` = `{id: string, name: string}`.

`done.result`: `cancelled` when `quit` arrived before the output started; `stopped` for code 0
after start; `failed` for any non-zero code (exit 1). Init failures that used to print
`Fatal:` and exit 1 become `error{code:"init_failed",fatal:true}` (exit 1). The roughly ten
post-`hello` `fail_args` sites inside `Recorder::new` become `error{code:"usage",fatal:true}`
(exit 2). Arguments clap itself rejects stay clap's exit 2 with no protocol line beyond
`hello`.

### 4.2 `share_region` (`prisc_share_region`), `QUIT_GRACE` = 5 s

`strings ObscureMode { none, blur, pixelate, hide }`, used by both the command and the event.

Commands: `move {region: Rect}` (ack `region_changed`), `obscure {mode: ObscureMode,
strength?: u32}` (1..=100, default 50 for blur/pixelate; ack `obscure`; `unobscure` is
`obscure{mode:"none"}`). Replies other than the ack: unparseable or out-of-range values are
`bad_command`; a well-formed `move` that fails to apply (`Mirror::set_region` returns `Err`) is
`error{code:"failed",fatal:false}`. Both are emitted in order with acks (§2.4).
`quit`: stop mirroring, emit `done`, exit. Post-start `mirror.rs` `fail_args` becomes
`error{code:"usage",fatal:true}` (exit 2).

| Event | Fields |
|---|---|
| `initialized` | |
| `sharing_started` | `region: Rect` |
| `region_changed` | `region: Rect` (as applied, after clamping) |
| `obscure` | `mode: ObscureMode`, `strength: u32` (0 for none/hide) |
| `status` | `fps: f64` |
| `done` **T** | `result: "stopped" \| "cancelled"` (`cancelled` = the prompt was dismissed before sharing; replaces the bare `cancelled` event and the silent exit) |

### 4.3 `job` (`prisc_vid2gif`, `Prisc.VideoRender`), `QUIT_GRACE` = 5 s

No commands besides `quit`, which cancels: stop within one packet/frame, delete partial
output, emit `done{result:"cancelled"}`, exit 0.

| Event | Fields |
|---|---|
| `progress` | `percent: u32` (0..=100, monotonic, deduplicated) |
| `done` **T** | `result: "ok" \| "cancelled"`, `path?: string`, `bytes?: u64` (both present iff `ok`) |

Failures: `error{code:"failed"|"io_failed"|"init_failed",fatal:true}` (partial output deleted
first), exit 1.

### 4.4 `scroll` (`prisc_scroll_driver`), `QUIT_GRACE` = 5 s

Commands: `stop` (finish now, keep what was captured: ends `done{result:"stopped"}`).
`quit` = the old `cancel`: write nothing, emit `done{result:"cancelled"}`, exit 0. The old
`cancel` command is removed.

| Event | Fields |
|---|---|
| `ready` | |
| `status` | `frames: u32`, `height_px: u32`, `state: "rewinding" \| "paused" \| "resuming" \| "scrolling" \| "settling" \| "stitching"`, `resume_in_s?: u64` |
| `done` **T** | `result: "complete" \| "stopped" \| "max_reached" \| "no_movement" \| "cancelled"`, `frames: u32`, `height_px: u32` (for `cancelled`, what was captured and discarded) |

`fatal_error` becomes `error{fatal:true}` and now **exits 1** (was 0). The `failed` result is
dropped. `session.json` is still written before `done` (not for `cancelled`), unchanged.

### 4.5 `capture` (`prisc_capture`), `QUIT_GRACE` = 5 s

`strings CaptureMode { region, screen | monitor, window }`: `monitor` is a decode-only alias
for `screen` (as `standby.rs` ~284 accepts today), so an unknown mode is a `bad_command`
from the parser, never a `rejected`.

Commands (standby only; one-shot ignores stdin except EOF/`quit`), shapes otherwise unchanged:
`settings {args: [string]}` (answered by `settings_status`, or `error{code:"rejected"}` then
the surviving `settings_status`), `capture {mode: CaptureMode}` (`error{code:"rejected"}`
while a capture is open), `pause {paused: bool}` (no reply).

| Event | Old line | Fields |
|---|---|---|
| `ready` | `PRISC_STANDBY_READY` | |
| `session` | `PRISC_SESSION <dir>` | `dir: string` |
| `capture_finished` | `PRISC_CAPTURE_FINISHED <dir>` | `dir: string` (standby, per capture; not terminal) |
| `settings_status` | `PRISC_SETTINGS_STATUS <json>` | `hotkeys: {main: [HotkeyStatus], window: [HotkeyStatus], monitor: [HotkeyStatus]}` |
| `error` | `PRISC_IPC_ERROR <text>` | non-fatal: `code: "rejected"` (bad snapshot, capture while busy) or `"io_failed"` (session dir) |
| `done` **T** | (none) | `result: "finished" \| "cancelled" \| "stopped"`, `dir?: string` |

`HotkeyStatus` = `{gesture: string, active: bool, error?: string, error_code?: string}` (codes
as in `CAPTURE_PROTOCOL.md` §1.2).

**Standby** exits:

- `quit` between captures: `done{result:"stopped"}`, exit 0. `quit` mid-capture: close the
  overlay without writing the session, `done{result:"stopped"}`, exit 0 (today's EOF
  behaviour plus the terminal line).
- Hotkey hook unavailable at startup (`standby.rs` ~83 `bail`):
  `error{code:"init_failed",fatal:true}`, exit 1.
- Hook died later (`standby.rs` ~166 `bail`) or a `run_cycle` error:
  `error{code:"failed",fatal:true}`, exit 1. Today these exit 1 through anyhow with no protocol
  line; the supervisor's crash accounting and fallback branch on the code instead.

**One-shot** keeps `action.txt` + `session.json` exactly as today. What changes: all three
stdio streams are redirected (it gets `hello` on stdout and the stdin lease, so a dead parent
ends the overlay instead of orphaning it), and it always ends with one terminal:

- Any normal outcome (edit, upload, color, video): `done{result:"finished",dir}`, exit 0.
- Escape / user cancel: `done{result:"cancelled"}`, exit 0.
- `quit`/EOF mid-capture: `done{result:"cancelled"}`, exit 0, session not written.
- Exit codes 3 and 4 keep their meaning, each preceded by a best-effort fatal `error`
  (`permission` / `failed`).

### 4.6 `prisc_ai` (no catalog)

Payload protocols unchanged (OCR header + BGRA through `--out`; matte/denoise frames on
stdout). It adopts: `claim_stdout()` (replacing `claim_payload_stdout`, matte/denoise only, as
today), `lifetime::install` for signals and the hard-exit timer (stdin EOF is end-of-input
here, not quit, so `read_commands` is not used; a dead parent shows up as EPIPE on its payload
writes), `exit::*` constants, stderr logging. No `hello`: stdout is payload. prisc_capture
spawns it via `locate::sibling("prisc_ai")`.

## 5. Exit codes (`prisc_ipc::exit`)

| Code | Const | Meaning |
|---|---|---|
| 0 | `OK` | Every outcome reported on the protocol, cancel included. |
| 1 | `FAILED` | Fatal failure; a fatal `error` was emitted (best effort). |
| 2 | `USAGE` | Bad command line: clap's own rejection (no protocol line), or a post-`hello` `error{code:"usage",fatal:true}`. |
| 3 | `NO_SCREEN_PERMISSION` | macOS Screen Recording not granted. |
| 4 | `CAPTURE_FAILED` | Desktop screenshot failed. |
| 6 | `QUIT_TIMEOUT` | The hard-exit timer fired (§3.3). |
| 7 | `INFERENCE_UNAVAILABLE` | Reserved for prisc_ai (older binaries). |
| 101 | (Rust) | Panic. |

`prisc_rust_core::exit` becomes `pub use prisc_ipc::exit::*;` so existing paths keep
compiling; the recorder and share_region literals `0/1/2` are replaced by the constants. The C#
mirror is generated as `HelperExit` (§6); `ScreenCaptureService.ExitCodeNoScreenPermission` and
`AiClient.ExitInferenceUnavailable` are deleted in favour of it.

## 6. Single source of truth: codegen

**Source**: `crates/prisc_ipc/src/catalog.rs`, one `ipc_catalog!` invocation per catalog. A
`macro_rules!` macro expands each into (a) Rust serde enums
(`#[serde(tag = "type", rename_all = "snake_case")]`, `skip_serializing_if = "Option::is_none"`,
finite-float serializer) and (b) a `const SPEC: CatalogSpec` describing the same thing as data,
from which `catalog_hash` (§2.5) is computed. The macro is the only place either is written, so
the two cannot drift.

```rust
ipc_catalog! {
    catalog job;
    helpers ["prisc_vid2gif", "Prisc.VideoRender"];
    quit_grace_ms 5000;
    strings JobResult { Ok, Cancelled }
    events {
        event Progress { percent: u32 }
            sample r#"{"type":"progress","percent":42}"#;
        terminal Done { result: JobResult, path: Option<String>, bytes: Option<u64> }
            sample r#"{"type":"done","result":"ok","path":"/tmp/a.gif","bytes":1024}"#;
    }
    commands {}
}
```

Message and string-enum value names are written in PascalCase (they are the Rust variants and
the C# record and enum member names) and go on the wire in snake_case (`MaxReached` is
`max_reached`); field names are written as they go on the wire. Each event starts with `event`
or `terminal`. Field types the macro accepts: `bool u32 u64 i32 i64 f64 String`, a
`strings Name { A, B | Alias, .. }` string enum (an `| Alias` is accepted on decode only), a
`record Name { .. }` struct declared in the same invocation, and `Vec<T>` / `Option<T>` of one
of those (not nested). Nothing else (no `serde_json::Value`, no maps). `hello`, `error` and
`quit` are common to every catalog: `error` is a variant of every `Event` enum, `hello` and
`quit` are the `catalog::Hello` and `catalog::Quit` structs, and all three are part of every
catalog's hash. C# type names are the catalog's PascalCase name plus the Rust name, unless the
Rust name already starts with it (`VideoTrack` is `RecorderVideoTrack`, `JobResult` stays); a
field whose C# property would share its record's name is renamed `Items` (a list) or `Value`
(`RecorderEvent.Cameras(Items)`).

(Considered and declined: hand-written serde types plus a const `SPEC` table checked by the
samples. The samples only exercise the fields they contain, so a field added to the serde type
but not the table would slip through. The macro makes the equality structural.)

**Generator**: `crates/prisc_ipc/src/csharp.rs`, modelled on `prisc_hotkeys/src/csharp.rs`.
It renders every `SPEC` into **`ui/Prisc.Ipc/Generated/HelperMessages.g.cs`**, including each
catalog's `catalog_hash`, and test `csharp_mirror` fails when the checked-in file differs.
Regenerate with `PRISC_IPC_REGEN=1 cargo test -p prisc_ipc csharp_mirror`. The Rust CI gate
runs an explicit per-crate list, so Phase 0 adds to it (`ci.yml`, "Rust gate"):

```
cargo clippy -p prisc_ipc --all-targets -- -D warnings
cargo test -p prisc_ipc
```

and to the "UI gate: dotnet tests" step:

```
dotnet test ui/Prisc.Ipc.Tests/Prisc.Ipc.Tests.csproj -c Release --nologo
```

None of these needs a libobs build.

**Generated C# shape** (namespace `Prisc.Ipc`, `#nullable disable`):

```csharp
public static class IpcProtocol { public const int Version = 1; }
public static class HelperExit { public const int Ok = 0, Failed = 1, Usage = 2,
    NoScreenPermission = 3, CaptureFailed = 4, QuitTimeout = 6, InferenceUnavailable = 7, RustPanic = 101; }
public static class IpcErrorCodes { public const string BadCommand = "bad_command"; /* ... */ }

public sealed record IpcHello(uint Protocol, string Catalog, string CatalogHash, string Helper, string Version, uint Pid);

public abstract record JobEvent
{
    public sealed record Progress(uint Percent) : JobEvent;
    public sealed record Done(JobResult Result, string Path, ulong? Bytes) : JobEvent;
    public sealed record Error(string Code, string Message, bool Fatal) : JobEvent, IIpcError;
}
public enum JobResult { Unknown, Ok, Cancelled }
public abstract record JobCommand { }   // recorder: RecorderCommand.Configure(string Path), ...

public static partial class HelperSpecs
{
    public static readonly HelperSpec<JobEvent, JobCommand> Job = new(
        catalog: "job", catalogHash: "<generated>", quitGrace: TimeSpan.FromMilliseconds(5000),
        decode: JobCodec.DecodeEvent, encode: JobCodec.EncodeCommand,
        encodeEvent: JobCodec.EncodeEvent, decodeCommand: JobCodec.DecodeCommand,
        isTerminal: e => e is JobEvent.Done || e is IIpcError { Fatal: true });
}
```

The codec is generated straight-line `Utf8JsonReader`/`Utf8JsonWriter` code (no reflection, no
`JsonSerializerContext`, over the hand-written `IpcJson` primitives), so it is trim-safe and
independent of `PriscUiJsonContext`. `IpcCodec` holds the common `DecodeHello` / `EncodeHello`,
`QuitLine` and `ReadType`. The
writer is created with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, so non-ASCII and
`+ < > & '` go out literally, as serde_json writes them. Decode returns null for an unknown
`type` (the client logs and skips it) and throws `JsonException` for a line that is not JSON or
has a field of the wrong JSON type. Enum values decode from the snake_case string; an
unknown string decodes to the generated `Unknown` member rather than failing the line.

**Golden samples**: every event and command in the macro carries a required `sample` line;
there is no separate conformance executable. The comparison rule differs per side, on purpose:

- **Rust compares bytes.** `cargo test` deserializes each sample into the Rust type and
  re-serializes it byte for byte. Samples are therefore written in serde_json's form
  (whole-number floats as `30.0`).
- **C# compares JSON values.** The generator copies the samples into the generated file as
  `HelperSamples`; `Prisc.Ipc.Tests` decodes each with the generated codec, re-encodes it,
  parses both the sample and the re-encoding with `JsonNode.Parse`, and compares them
  semantically (`JsonNode.DeepEquals`, numbers compared by value). `Utf8JsonWriter` writes
  `30.0` as `30`, so a byte comparison would fail on valid output.

So the samples are the shared golden fixtures, they live in the one source file, and both
halves agree on what "valid" means.

## 7. C# side (`ui/Prisc.Ipc`)

`Prisc.Ipc.csproj`: net10.0, `RootNamespace Prisc.Ipc`, `Nullable disable`, no package
references, `AllowUnsafeBlocks` off (the P/Invokes in `JobObject` and `IpcChild` use plain
`DllImport`), `InternalsVisibleTo Prisc.Ipc.Tests`. Referenced by Prisc.VideoSDK, Prisc.Ui and
Prisc.VideoRender. New `ui/Prisc.Ipc.Tests` (xunit, like Prisc.Shared.Tests), added to
`Prisc.slnx` and to the `ci.yml` UI gate (§6).

Files:

| File | Content |
|---|---|
| `Generated/HelperMessages.g.cs` | §6, never hand-edited |
| `Platform.cs` | `ExeName(stem)` (`.exe` on Windows), `LibName(stem)` (`stem.dll`, `libstem.dylib`, `libstem.so`) |
| `HelperLocator.cs` | §7.1 |
| `HelperProcess.cs` | moved from Prisc.Ui (namespace `Prisc.Ipc`), gains `HelperStartOptions` |
| `HelperLog.cs` | `HelperProcessLog` moved and renamed |
| `JobObject.cs` | Windows kill-on-close job (§7.4) |
| `HelperClient.cs` | §7.2 |
| `HelperFailure.cs` | failure kinds + exception |
| `IpcChild.cs` | child side for Prisc.VideoRender (§7.6) |

### 7.1 `HelperLocator`

```csharp
public static class HelperLocator
{
    public const string EnvVarName = "PRISC_HELPERS_DIR";
    public static string Find(string stem);         // executable; null when not found; EnsureExecutable applied
    public static string FindLibrary(string stem);  // native library (prisc_hotkeys)
    internal static string Find(string fileName, string envDirs, string baseDirectory); // testable
    internal static Func<string, string> Override;  // test seam (DenoiseGeneratorTests); null in production
}
```

`PRISC_HELPERS_DIR` is a **path list** separated by `Path.PathSeparator` (`;` on Windows, `:`
elsewhere), probed in order. That covers an out-of-tree Prisc.Ui `OutDir`, where the dev
walk-up finds no `Cargo.toml` and one directory cannot hold both the Rust helpers and
VideoRender. Documented dev value:
`<repo>/target/debug;<repo>/ui/Prisc.VideoRender/bin/Debug/net10.0` (`:` on Unix).

Probe order for file name `f` = `Platform.ExeName(stem)` (or `LibName`), first existing wins:

1. For each directory `d` in `$PRISC_HELPERS_DIR`: `d/f`, then `d/recorder/f`.
2. `AppContext.BaseDirectory/f`, then `AppContext.BaseDirectory/recorder/f` (release layout:
   capture, scroll driver, ai, VideoRender and the hotkeys library beside Prisc.Ui; recorder,
   share_region and vid2gif in `recorder/`).
3. Dev walk-up from `AppContext.BaseDirectory`: at each ancestor holding `Cargo.toml`, probe
   `target/debug/f` and `target/release/f` and take the newer by last-write time (debug wins a
   tie; `CaptureBinaryLocator`'s rule). Stem `Prisc.VideoRender` instead probes
   `ui/Prisc.VideoRender/bin/{Debug,Release}/net10.0/f` at each ancestor, newer wins. Keep
   walking past a `Cargo.toml` with nothing built.

`EnsureExecutable` (from `HelperBinary`) is applied to every hit on Unix. The FFmpeg directory
is not a helper: `FFmpegLoader` (VideoSDK) gains `FindBesideRecorder()` = directory of
`HelperLocator.Find("prisc_recorder")`, or its `Frameworks/` subdirectory, verified by the
avcodec-major probe that `ObsBinaryLocator.ResolveFFmpegDirectory` has today.
`PRISC_FFMPEG_PATH` keeps precedence inside the loader.

### 7.2 `HelperClient<TEvent, TCommand>`

```csharp
public sealed class HelperClient<TEvent, TCommand> : IAsyncDisposable
    where TEvent : class where TCommand : class
{
    public static Task<HelperClient<TEvent, TCommand>> StartAsync(
        HelperSpec<TEvent, TCommand> spec, string exePath, IReadOnlyList<string> args,
        HelperStartOptions options = null, CancellationToken ct = default);  // awaits hello

    public IpcHello Hello { get; }
    public int ProcessId { get; }
    public ChannelReader<TEvent> Events { get; }          // unbounded, single reader, in order, every event
    public Task<HelperOutcome<TEvent>> Completion { get; } // never faults
    public string StderrTail { get; }                       // HelperLog snapshot

    public bool Send(TCommand command);                     // false once stdin is closed
    public Task<TEvent> SendAndWaitAsync(TCommand command, Func<TEvent, bool> isReply, TimeSpan timeout);
    public Task<HelperOutcome<TEvent>> StopAsync();         // §7.3, idempotent
    public void Kill();                                     // tree kill now
    public ValueTask DisposeAsync();                        // StopAsync + join pumps (bounded)
}

public sealed record HelperStartOptions
{
    public bool KillWithParent { get; init; } = true;       // job object (§7.4)
    public bool GrantForeground { get; init; }              // AllowSetForegroundWindow
    public IReadOnlyDictionary<string, string> Environment { get; init; }
    public string WorkingDirectory { get; init; }           // default: exe's directory
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int StderrLines { get; init; } = 1000;
    public int StderrChars { get; init; } = 256 * 1024;
}

public sealed record HelperOutcome<TEvent>(
    TEvent Terminal,            // terminal event or fatal error; null when none
    int? ExitCode,
    HelperFailure Failure,      // None only when Terminal is a non-error terminal event
    string StderrTail);

public enum HelperFailure { None, StartFailed, HelloTimeout, ProtocolMismatch,
    FatalError, NoScreenPermission, CaptureFailed, InferenceUnavailable, Crashed, Timeout }

public sealed class HelperCommandException : Exception { public IIpcError Error { get; } }
```

Behaviour:

- **Start**: `HelperProcess.Start` (UTF-8 no BOM, all three streams redirected, `AutoFlush`,
  `ArgumentList`), env `options.Environment`, job assignment (§7.4), optional foreground grant,
  then the stdout and stderr pumps. `StartAsync` completes when `hello` arrives; it fails
  (after `Kill`) on `HelloTimeout`, on exit before hello (`StartFailed`, or the exit-code
  mapping below), or on `ProtocolMismatch` (`protocol`, `catalog` or `catalog_hash` differs
  from the spec). `HelloTimeout` defaults to 30 s, matching today's readiness timeouts: a
  freshly updated helper exe can sit in Windows Defender's block-at-first-sight check (10 s by
  default) and must load its load-time-linked libobs/FFmpeg DLLs before `main` runs.
- **stdout pump**: each line is decoded with `spec.Decode`. `hello` is consumed by the client.
  Unknown types are logged to the `HelperLog` as `[stdout] <line>` and skipped; non-JSON lines
  likewise (the guard makes them a bug). For each decoded event, in this order: (1) if a
  `SendAndWaitAsync` is pending, test its `isReply`; a match completes the wait; a non-fatal
  `error` with code `bad_command`, `rejected` or `failed` fails the wait with
  `HelperCommandException` (commands are answered in order, §2.4, so the first such error
  after the send belongs to it); (2) write the event to `Events` regardless. So the runner's
  reader loop sees every event, replies included, and the wait never steals one. The first
  terminal event is recorded for `Completion`. The channel completes at stdout EOF or when
  `Completion` resolves, whichever is first.
- **SendAndWaitAsync**: at most one outstanding per client (a second concurrent call throws
  `InvalidOperationException`). It also fails when the process exits or a terminal event
  arrives first, and with `TimeoutException` after `timeout`. Use it only for commands the
  catalog lists with a guaranteed reply (§4.1 marks the recorder's never-ack commands).
- **stderr pump**: every line into `HelperLog` and `Debug.WriteLine`.
- **Completion**: resolves on process exit plus a **bounded** pump drain: after exit, wait up to
  `PumpDrainTimeout` (5 s, today's `ObsCapturer`/`ScrollDriver` value) for both pumps to reach
  EOF, then abandon them. A grandchild that inherited stderr (obs-ffmpeg-mux, a browser) can
  hold a pipe open indefinitely; that must not hang `Completion`. A line arriving after the
  drain window is logged and dropped. Mapping, first match wins:
  1. `StopAsync` had to kill the process → `Timeout`, whatever arrives afterwards.
  2. Terminal non-error event → `None`.
  3. Fatal `error` → `FatalError`.
  4. No terminal: exit code 3 → `NoScreenPermission`, 4 → `CaptureFailed`,
     7 → `InferenceUnavailable`, anything else (0 included, `StopAsync` or not) → `Crashed`.
- **Send**: serializes with `spec.Encode` and writes one line under a lock; an `IOException`
  (child gone) returns false and marks stdin closed.

### 7.3 Grace policy (one policy, per-catalog duration)

`StopAsync`: send `{"type":"quit"}`, close stdin, wait `spec.QuitGrace` for exit, then
`Kill(entireProcessTree: true)` and record that a kill was needed (Completion reports `Timeout`
from then on, §7.2), wait 2 s more, then give up. Durations come from the catalog: recorder
65 s (§4.1: stop signal plus mp4 flush), everything else 5 s. This replaces the 2/5/10/45 s
constants in the runners. Helper-specific readiness timeouts (recorder `initialized` 30 s,
share `initialized` 30 s, configure/pause acks 10 s) stay in the runners as `SendAndWaitAsync`
timeouts.

### 7.4 Kill-with-parent

Windows: one process-wide job created lazily with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE |
JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK`; every child started with `KillWithParent` is assigned
right after `Process.Start`.

- **Only direct children are in the job.** `SILENT_BREAKAWAY_OK` keeps every process a helper
  spawns out of it. Without it, the user's browser that prisc_capture opens with
  `ShellExecuteW` (`system/win_browser.rs`, OCR search) would be created inside the job when no
  browser is already running, and killed whenever Prisc.Ui exits, updates or crashes. Helper
  grandchildren (prisc_ai under prisc_capture) end through their own stdin lease when their
  parent dies, and `Kill()` is a tree kill.
- **Assignment is best-effort.** If `CreateJobObject` or `AssignProcessToJobObject` fails
  (Prisc.Ui itself running inside a job that forbids nesting or breakaway, as some launchers
  and CI runners do), log it once to the `HelperLog` and continue; never fail the start. The
  child then relies on stdin EOF.
- **The recorder opts out** (`KillWithParent = false`): its parent-death path is stdin EOF →
  stop and flush the mp4, then the §3.3 timer, which keeps the recording of a crashed
  Prisc.Ui instead of truncating it.

macOS/Linux: no job; stdin EOF and EPIPE do the job (§3.3).

### 7.5 Expected exits

There are none without a terminal event. Every catalog emits a terminal on every exit path it
controls (§1.4, §4), including scroll `quit`, standby `quit` and a cancelled one-shot overlay.
`StopAsync` therefore no longer excuses a silent exit: an exit without a terminal is `Crashed`
(or a specific exit-code failure), and an exit only reached by killing is `Timeout`. In
particular a recorder that `StopAsync` had to kill is never reported as a clean stop, matching
today's `ObsCapturer`, which treats any exit without `stopped_recording` as fatal.

### 7.6 Child side for Prisc.VideoRender (`IpcChild`)

```csharp
public static class IpcChild
{
    public static IpcWriter<TEvent> Open<TEvent>(string helper, HelperSpec<TEvent, object> spec); // claims stdout, writes hello
    public static void ReadCommands<TCommand>(Func<string, TCommand> decode,
        Action<TCommand> onCommand, Action<string> onBadCommand); // background thread; quit/EOF -> OnQuit
    public static event Action OnQuit;                               // raised once
    public static void ArmHardExit(TimeSpan grace);                  // after OnQuit: exit 6 when grace elapses
}
```

`Open` applies the **same fd-level guard as §3.2**, because VideoRender runs GPU backends and
hardware encoders (NVENC, AMF, VideoToolbox through FFmpeg, Skia GPU) whose drivers can write
to fd 1, the same class of problem as the prisc_ai CoreML incident:

- Unix: `fcntl(1, F_DUPFD_CLOEXEC, 3)` into a private `SafeFileHandle` wrapped in a
  `FileStream` for protocol lines, then `dup2(2, 1)` (libc via `DllImport`).
- Windows: `DuplicateHandle` of `GetStdHandle(STD_OUTPUT_HANDLE)` with `bInheritHandle =
  FALSE` into the private stream, `SetStdHandle(STD_OUTPUT_HANDLE, stderr)`, and `_dup2(2, 1)`
  in `ucrtbase.dll`, with the shared-UCRT assumption of §3.2.
- Then `Console.SetOut(Console.Error)` so managed `Console.WriteLine` follows.

`IpcWriter.Emit` writes one line under a lock and flushes; it never throws (an `IOException`
marks the writer closed and raises `OnQuit` after the lock is released). The generated
`JobCodec.Encode`/`Decode` are used on this side too.

### 7.7 Runner map

| Today | After | Spec | Options |
|---|---|---|---|
| `ObsCapturer` | `RecorderDriver` | `HelperSpecs.Recorder` | `KillWithParent = false` |
| `CameraDeviceManager` (`--list-cameras` spawn) | same class, `HelperClient` awaiting `cameras` | `Recorder` | |
| `ShareRegionDriver` + `ShareRegionProtocol` | `ShareRegionDriver` | `ShareRegion` | `GrantForeground` |
| `Vid2GifRunner`, `VidRenderRunner` | one `JobRunner` with `ForVid2Gif(...)` / `ForVideoRender(...)` | `Job` | render passes `PRISC_FFMPEG_PATH` in `Environment` |
| `ScrollDriver` + `ScrollDriverProtocol` | `ScrollDriver` | `Scroll` | `GrantForeground` |
| `CaptureStandbySupervisor` | same class (keeps backoff, stable-uptime, fallback logic) | `Capture` | `GrantForeground` |
| `ScreenCaptureService` one-shot spawn | same class, `HelperClient` awaiting `done` | `Capture` | `GrantForeground` |
| `AiClient` (VideoSDK) | same class on `HelperProcess.Start` + `HelperLog` + `JobObject`, binary streams; no `HelperClient` | none | `KillWithParent` |
| `AiLoader` resolver seam | `HelperLocator.Find("prisc_ai")`; `Configure` removed, test seam is `HelperLocator.Override` | | |
| `HotkeysLibraryLocator` | `HelperLocator.FindLibrary("prisc_hotkeys")` | | |
| VideoRender `Program.cs` protocol + `FindAiBinary` + `FindFFmpegDirectory` | `IpcChild` + `HelperLocator` + `FFmpegLoader.FindBesideRecorder` | `Job` (child side) | |
| `TestFFmpeg` (VideoSDK.Tests) | `FFmpegLoader.FindBesideRecorder` | | |

## 8. Migration

**Phase 0 (serial, Rust, small; merged before the parallel halves branch):** create
`crates/prisc_ipc` with `exit`, the `ipc_catalog!` macro, all five catalogs exactly as §4
(samples included), `catalog_hash`, the generator, and the checked-in
`ui/Prisc.Ipc/Generated/HelperMessages.g.cs` plus a minimal `ui/Prisc.Ipc/Prisc.Ipc.csproj`
that compiles it and a minimal `ui/Prisc.Ipc.Tests` running the sample round-trip. Add the
`prisc_ipc` clippy/test lines and the `Prisc.Ipc.Tests` line to `ci.yml` (§6). No helper
changes. Both halves branch from this commit; neither edits the catalog afterwards without
regenerating and telling the other (the hash makes a forgotten regeneration fail loudly).

The wire change is breaking in both directions, so the two halves **merge together** (one
integration branch, then the smoke tests: record, share, GIF export and GIF cancel, render,
scroll capture, standby capture, one-shot capture including Escape, OCR, matte).

### 8.1 Rust half (worktree A)

1. `prisc_ipc`: `open`/`Channel`, `claim_stdout` (CLOEXEC / non-inheritable), `read_commands`,
   `lifetime` (signals from `recorder.rs` ~1760-1850, hard exit, `on_exit`/`exit`),
   `single_line`, `locate`. Unit tests: BOM line, invalid UTF-8, oversize line, EPIPE on emit
   (trigger runs after the lock is released and an `on_quit` that emits does not deadlock),
   quit/EOF idempotence, the private stdout handle not inherited by a spawned child.
2. `prisc_rust_core`: depend on `prisc_ipc`; `exit.rs` becomes the re-export; add
   `telemetry::init_job`, `telemetry::shutdown` and `shutdown_hook`.
3. `prisc_recorder`: replace `status::emit_json`/`emit_simple`/`emit_stopped_recording`/
   `emit_configure_*` with `Channel<recorder::Event>`; `commands.rs` text parser replaced by
   `recorder::Command` → internal `Command` mapping (internal variants `OutputStarted`,
   `SetSpeakerVolume`, ... stay); `spawn_stdin_thread`, `install_signal_handler`, `console_ctrl`
   replaced by `read_commands` + `lifetime::install` (`set_exit_fn(platform::exit_process)`,
   `on_quit` posts into the command queue); every `exit_process` call site → `lifetime::exit`;
   field renames; the §4.1 reply rules for `start`/`pause` (re-emit on duplicates, `rejected` /
   `failed` instead of silence); post-`hello` `fail_args` → `error{usage}`; `done` result
   mapping; the `QUIT_GRACE` const assert; `cameras.rs` emits `cameras`/fatal `error`;
   `telemetry::init`; `exit::*` constants; verify the shared-UCRT assumption (§3.2) for the
   shipped DLLs. Update README protocol sections to point here.
4. `prisc_share_region`: same treatment (`status.rs`, `commands.rs` grammar to JSON; keep the
   in-order rejection queue, now emitting `error{bad_command}`, and `error{failed}` for a
   failed `set_region`); `ctrlc` handler replaced by `lifetime` with `on_quit` posting into the
   command queue; every `exit_process` call site (including `ui/appkit.rs`, `win32.rs`) →
   `lifetime::exit`; `mirror.rs` `fail_args` → `error{usage}`; `done{stopped|cancelled}`;
   `telemetry::init`.
5. `prisc_vid2gif`: `Channel<job::Event>`; `spawn_stdin_watcher` replaced by
   `read_commands` + `lifetime` tripping `CancelToken`; `done`/fatal `error`;
   `telemetry::init_job`. Fixes the missing EOF handling.
6. `prisc_scroll_driver`: `DriveEvent`/`DriveCommand`/`emit` replaced by the `scroll` catalog
   types; `cancel` → `quit` emitting `done{cancelled}`; fatal exits 1; drop
   `DriveResult::Failed`.
7. `prisc_capture`: `standby::emit_line`/`emit!`/`emit_ipc_error`/`report_settings_status` and
   the hand-rolled stdin thread replaced by `Channel<capture::Event>` + `read_commands`;
   standby `done{stopped}` on quit and fatal `error` on the hook `bail`s and `run_cycle`
   errors (§4.5); `CaptureMode` with the `monitor` alias; `main.rs` one-shot path: `open`,
   `lifetime`, `done{finished|cancelled}`; `ocr/client.rs`: `locate::sibling("prisc_ai")`,
   `PRISC_AI_BINARY` removed.
8. `prisc_ai`: `claim_payload_stdout` → `prisc_ipc::claim_stdout`; `lifetime::install`; exit
   constants.
9. Docs: `CAPTURE_PROTOCOL.md` §1.2 standby stdio paragraphs, §1.4, §2.2-2.4 and §3.1 env var
   reduced to pointers here; vid2gif/share_region headers and READMEs likewise. Drop the stale
   "scroll driver exits 4 on unsupported platforms" note from `exit.rs` (`main.rs` has a
   `compile_error!` for those).

Deleted Rust code: `prisc_recorder/src/commands.rs` `parse_command` and its tests,
`recorder.rs` `spawn_stdin_thread` / `install_signal_handler` / `mod console_ctrl`,
`status.rs` `emit_json` / `emit_simple` / `emit_stopped_recording` / `emit_configure_*`,
`prisc_share_region/src/status.rs` `emit_json` and emitters, `commands.rs` text grammar,
`prisc_vid2gif/src/main.rs` `spawn_stdin_watcher` and its `single_line`,
`prisc_scroll_driver/src/drive.rs` wire-protocol section, `prisc_capture/src/standby.rs`
`emit_line` / `emit!` / `emit_ipc_error`, `prisc_ai/src/main.rs` `claim_payload_stdout`.

### 8.2 C# half (worktree B)

1. `ui/Prisc.Ipc`: `Platform`, `HelperLocator`, `HelperProcess` + `HelperLog` (git mv from
   Prisc.Ui, namespace `Prisc.Ipc`), `JobObject`, `HelperClient`, `HelperFailure`, `IpcChild`.
   Project references from Prisc.VideoSDK, Prisc.Ui, Prisc.VideoRender. `ui/Prisc.Ipc.Tests`
   with an in-memory `HelperClient` test seam (internal constructor over streams and an exit
   task): reply matching without stealing from `Events`, error-fails-wait, bounded drain with
   a held-open pipe, the §7.2 Completion mapping including kill → `Timeout`, path-list
   `PRISC_HELPERS_DIR`.
2. Rename and port runners per §7.7, and apply the rename map below.
3. `ScreenCaptureService` one-shot: redirect all three streams through `HelperClient`, await
   `done`; `CaptureStandbySupervisor`: `Kill()` → tree kill via the client, branch crash
   accounting on the fatal `error` code.
4. `Prisc.VideoRender/Program.cs`: `IpcChild` (fd-level guard), `job` catalog, `cancelled`
   spelling.
5. Env vars: `ci.yml` sets `PRISC_HELPERS_DIR=$(pwd)` (the directory holding `recorder/`) in
   place of `PRISC_RECORDER_PATH`; `BUILDING.md` documents `PRISC_HELPERS_DIR` (path list,
   with the dev value of §7.1) and `PRISC_FFMPEG_PATH` only.
6. `VideoCapturePage`: drop the `ObsCapabilities` probes (window capture and mkv are always
   supported by the shipped recorder).
7. Call sites outside the runners that break and must be ported:
   - `App.axaml.cs` ~100 `AiLoader.Configure(AiBinaryLocator.Resolve)`: delete.
   - `ui/Prisc.VideoSDK.Tests/DenoiseGeneratorTests.cs` (~62, ~132, ~145) uses
     `AiLoader.Configure` as its seam: switch to `HelperLocator.Override` (internal, visible
     to the test assembly).
   - `VideoEditorWindow.axaml.cs` and `VideoSpikeWindow.axaml.cs` call
     `ObsBinaryLocator.ResolveFFmpegDirectory`: switch to `FFmpegLoader.FindBesideRecorder`.
   - `VideoEditorWindow.axaml.cs` dev mode news up a `VidRenderRunner`: switch to
     `JobRunner.ForVideoRender`.
   - `ui/Prisc.VideoSDK.Tests/RecorderPlumbingTests.cs` uses `ObsArguments`: rename to
     `RecorderArguments`.
   - Also grep `FFmpegGate.cs`, `VideoRenderManager.cs`, `VideoEditPersistence.cs`,
     `RecordingSources.cs`, `ProjectLoadTests.cs` and `MatteGeneratorTests.cs`, which reference
     the renamed or deleted types.

Rename map (types, files and test files follow the type name):

| Old | New |
|---|---|
| `ObsCapturer` | `RecorderDriver` |
| `ObsArguments` | `RecorderArguments` |
| `ObsSettingsJson` | `RecorderSettingsJson` |
| `ObsConfigureResult` | `RecorderConfigureResult` |
| `ObsStatus` | generated `RecorderEvent.Status` |
| `ObsLevels` | generated `RecorderEvent.Levels` |
| `ObsTracks` | generated `RecorderTracks` |
| `ObsTrackInfo` | generated `RecorderVideoTrack` |
| `ObsAudioTrackInfo` | generated `RecorderAudioTrack` |
| `ObsBinaryLocator` | `HelperLocator` (+ `FFmpegLoader.FindBesideRecorder`) |
| `ObsBinaryLocatorTests` | `HelperLocatorTests` (Prisc.Ipc.Tests) |
| `HelperProcessLog` | `HelperLog` |

`Obscure*` types are unrelated and keep their names. Remove the `Obs*` entries from
`PriscUiJsonContext`.

Files deleted (C#): `ui/Prisc.Ui/Services/ObsCapabilities.cs`, `ObsBinaryLocator.cs`,
`AiBinaryLocator.cs`, `HelperBinary.cs`, `Vid2GifRunner.cs`, `VidRenderRunner.cs`,
`ShareRegionProtocol.cs`, `Hotkeys/HotkeysLibraryLocator.cs`,
`ui/Prisc.Ui/Scroll/ScrollDriverProtocol.cs`, the `CaptureBinaryLocator` class in
`ScreenCaptureService.cs`, `ui/Prisc.VideoSDK.Tests/HelperBinaryTests.cs` (folded into
`HelperLocatorTests`), and `ShareRegionProtocolTests.cs` / `CaptureStandbyStatusTests.cs`
decode tests superseded by the generated-sample tests (behavioural assertions move to the
runner tests). Moved, not deleted: `HelperProcess.cs`, `HelperProcessLog.cs` → `ui/Prisc.Ipc/`.
