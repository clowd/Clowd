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
3. **Every line-protocol child says `hello` first.** The parent checks `protocol`; a mismatch
   is a hard failure, not a negotiation (the helpers ship inside the app, so a mismatch is a
   broken install or a stale dev build).
4. **One terminal rule.** Each run ends with exactly one terminal event, `done` (or the
   catalog's named terminal event), or a fatal `error`. If the process exits without one and the
   parent did not ask it to stop, the parent treats it as a crash. This rule now holds for
   share_region too (it gains `done`).
5. **`quit` == stdin EOF == SIGINT/SIGTERM/console-ctrl == parent death.** All four go through
   one callback in the child. What "quit" means per helper is fixed in §4 (recorder: stop and
   flush; job and scroll: cancel; share: stop; standby: exit between captures).
6. **Cancel is spelled `cancelled`** (the majority spelling). VideoRender's `canceled` goes away.
7. **Single source of truth: Rust codegen** (§6). Message types are declared once in
   `prisc_ipc`, and the C# records are generated from them by a cargo test, the same pattern as
   `prisc_hotkeys/src/csharp.rs` → `HotkeyKey.g.cs`.
8. **Dependency direction: `prisc_ipc` is a leaf; `prisc_rust_core` depends on it** and
   re-exports `exit`. Neither crate depends on libobs, so `prisc_capture` stays libobs-free.
   `prisc_recorder`, `prisc_share_region` and `prisc_vid2gif` gain a `prisc_rust_core`
   dependency for telemetry.
9. **One env var** for finding helpers, `PRISC_HELPERS_DIR`. Every per-helper override
   (`PRISC_RECORDER_PATH`, `PRISC_SHARE_REGION_PATH`, `PRISC_VID2GIF_PATH`,
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
  `-100.0`, as today).
- Integers: `u32`/`u64`/`i32`/`i64` as JSON numbers. Paths are strings (lossy-UTF-8 on the Rust
  side, as today).

### 2.2 Common events (every line-protocol helper)

```json
{"type":"hello","protocol":1,"helper":"prisc_recorder","version":"4.11.0","pid":4242}
{"type":"error","code":"bad_command","message":"unknown command 'mvoe'","fatal":false}
```

| Event | Fields | Rules |
|---|---|---|
| `hello` | `protocol` u32, `helper` string, `version` string, `pid` u32 | First stdout line, emitted by `prisc_ipc::open` before any other work (before libobs/FFmpeg/ONNX init), so it arrives in milliseconds. `helper` is the binary stem (`prisc_recorder`, `prisc_share_region`, `prisc_vid2gif`, `Prisc.VideoRender`, `prisc_scroll_driver`, `prisc_capture`). `version` is the crate/assembly version, for logs only. |
| `error` | `code` string, `message` string, `fatal` bool | `message` is one line (`single_line`), display text. `code` is the stable twin to branch on. `fatal:true` is terminal: the helper exits with code 1 right after it. `fatal:false` never changes state; the helper keeps running. |

Error codes (closed set; a parent that sees an unknown code falls back to `fatal` and the text):

| `code` | Meaning |
|---|---|
| `bad_command` | stdin line not JSON, unknown `type`, missing/invalid field. Never fatal. |
| `rejected` | A well-formed command the helper refused in its current state (standby: rejected settings snapshot, a `capture` while one is open). |
| `init_failed` | Setup failed before the helper became useful (libobs, FFmpeg open, no window at scroll point). |
| `io_failed` | Reading/writing a file or directory failed (session dir, output file). |
| `permission` | An OS permission is missing (macOS Screen Recording, Accessibility). |
| `failed` | Any other failure. |

### 2.3 Common commands

```json
{"type":"quit"}
```

`quit` is handled by `prisc_ipc` itself and never reaches a helper's command enum: it fires the
same quit callback as EOF and signals (§3.3). It is idempotent.

### 2.4 Unknown-command policy

Malformed JSON, an unknown `type`, or bad fields produce `error{code:"bad_command",fatal:false}`
**written by the helper's own command-draining thread, in arrival order with the acks** (the
share_region rule, `commands.rs` `Command::Error`: a rejection written from the reader thread
can overtake the ack of the previous command). It is also logged to stderr. Empty lines are
skipped silently.

### 2.5 Versioning

`PROTOCOL = 1`. Bump it for any change a parent of the previous version would misread: a
removed/renamed event, command or field, a changed type, a changed meaning. Do **not** bump for
additions (new optional field, new event, new command, new error code): readers ignore unknown
fields and events, and helpers answer unknown commands with `bad_command`. The parent fails a
start whose `hello.protocol != IpcProtocol.Version` with `HelperFailure.ProtocolMismatch`.

## 3. Rust crate `prisc_ipc`

`crates/prisc_ipc`, workspace member and default-member. Dependencies: `serde`, `serde_json`,
`ctrlc` (posix signals), `libc` (unix), `windows` (Console, Threading, Foundation, JobObjects not
needed). No `log`: its own diagnostics are `eprintln!` (the recorder and share_region do not use
`log`).

```rust
pub const PROTOCOL: u32 = 1;

pub mod exit;        // §5, moved from prisc_rust_core::exit
pub mod catalog;     // §4, macro-generated message types, one module per catalog
mod csharp;          // §6 generator + drift test

// --- stdout --------------------------------------------------------------------------
/// Claims stdout (guard, §3.2), emits `hello`, returns the process's one protocol writer.
/// Panics if called twice. Call first thing in main, before any native library loads.
pub fn open<E: catalog::Event>(helper: &'static str) -> Channel<E>;

#[derive(Clone)] pub struct Channel<E> { /* Arc<Mutex<File>>, closed flag */ }
impl<E: catalog::Event> Channel<E> {
    /// Serialize, then one write_all(json + "\n") + flush under the lock. Never panics.
    /// Returns false once the pipe is gone. The first EPIPE/broken-pipe error marks the
    /// channel closed (later emits are no-ops) and fires lifetime::trigger(ParentExited).
    pub fn emit(&self, event: &E) -> bool;
    /// Emits `error`. `message` is passed through single_line. If `fatal`, also flushes
    /// telemetry (when the helper initialised it) and exits with exit::FAILED.
    pub fn error(&self, code: catalog::ErrorCode, message: impl Display, fatal: bool);
    pub fn is_closed(&self) -> bool;
}

/// The guard alone: duplicate the real stdout into a private handle and point fd 1 /
/// STD_OUTPUT_HANDLE at stderr. Used by `open`, and directly by prisc_ai (payload channel).
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
    pub enum QuitReason { Command, StdinClosed, Signal, ParentExited }
    /// Installs, once per process: the quit callback, the signal / console-ctrl handler,
    /// and the parent-death watchdog. `grace` arms the hard-exit timer (below).
    pub fn install(on_quit: impl Fn(QuitReason) + Send + Sync + 'static, grace: Duration);
    /// Fire the quit path. First call runs on_quit and arms the timer; later calls no-op.
    pub fn trigger(reason: QuitReason);
    /// Overrides how the process exits (recorder/share_region: platform::exit_process,
    /// which skips libobs's broken static destructors). Default std::process::exit.
    pub fn set_exit_fn(f: fn(i32) -> !);
    /// Runs before any exit prisc_ipc performs (fatal error, hard exit). Helpers register
    /// prisc_rust_core::telemetry::flush here.
    pub fn on_fatal(f: fn());
}

/// Collapse all whitespace runs (including newlines) to one space and trim.
pub fn single_line(s: &str) -> String;

pub mod locate {
    /// Rust-side helper lookup (prisc_capture → prisc_ai): $PRISC_HELPERS_DIR/<exe>, then
    /// the directory of current_exe(). Returns None when neither exists.
    pub fn sibling(stem: &str) -> Option<PathBuf>;
    /// Sets PRISC_PARENT_PID=<our pid> on a Command we are about to spawn.
    pub fn mark_child(cmd: &mut std::process::Command);
}
```

### 3.1 Typical helper `main`

```rust
let out = prisc_ipc::open::<catalog::job::Event>("prisc_vid2gif");
let _sentry = prisc_rust_core::telemetry::init_job("prisc_vid2gif");
let cancel = CancelToken::new();
let c = cancel.clone();
prisc_ipc::lifetime::install(move |_| c.cancel(), catalog::job::QUIT_GRACE + SELF_EXIT_MARGIN);
prisc_ipc::read_commands::<catalog::job::Command>(|cmd| { /* job has no commands */ ControlFlow::Continue(()) });
```

### 3.2 Stdout guard

Generalised from `prisc_ai::claim_payload_stdout`. Unix: `dup(1)` into an owned `File`, then
`dup2(2, 1)`. Windows: `DuplicateHandle(GetStdHandle(STD_OUTPUT_HANDLE))` into an owned `File`,
`SetStdHandle(STD_OUTPUT_HANDLE, stderr)`, and the CRT `_dup2(2, 1)` so native `printf` follows.
After it, `println!`, libobs, FFmpeg, CoreML and ONNX Runtime all land on stderr, and only
`Channel::emit` (or prisc_ai's payload writer) reaches the real pipe. When stdout is not a pipe
(a developer running the binary in a terminal) the guard still applies; the protocol stays
readable because both streams reach the same terminal.

### 3.3 Signals, parent death, hard exit

- **Signals.** Windows: a directly registered `SetConsoleCtrlHandler` routine that calls
  `trigger(Signal)` and then **blocks forever** (the recorder's version, `recorder.rs`
  `console_ctrl`: the CTRL_CLOSE grace window lasts only while the routine runs). POSIX:
  `ctrlc` with `termination` (SIGINT, SIGTERM, SIGHUP) calling `trigger(Signal)` then parking.
  Rust's default SIGPIPE=ignore stays, so a dead stdout is an `EPIPE`, not a kill.
- **Parent death.** The parent sets `PRISC_PARENT_PID` (§7.4). The watchdog thread:
  Windows `OpenProcess(SYNCHRONIZE)` + `WaitForSingleObject(INFINITE)`; macOS `kqueue`
  `EVFILT_PROC`/`NOTE_EXIT`; Linux `prctl(PR_SET_PDEATHSIG, SIGTERM)` (lands in the signal path)
  followed by a `getppid()` check to close the race. If the pid is already gone at install,
  trigger immediately. Without the variable: Unix uses `getppid()` at startup (skipped when 1);
  Windows has no watchdog and relies on stdin EOF and the parent's job object.
- **Hard exit.** The first `trigger` arms a timer of `grace`. If the process is still alive when
  it fires, the exit fn runs with `exit::QUIT_TIMEOUT`. Helpers pass their catalog
  `QUIT_GRACE` plus a 5 s `SELF_EXIT_MARGIN`, so a living parent's kill (§7.5) always wins and
  the timer only matters for orphans.

### 3.4 Telemetry

`prisc_recorder` and `prisc_share_region` call `prisc_rust_core::telemetry::init(<stem>)`
(long-lived, one session per recording/share). `prisc_vid2gif` calls a new
`telemetry::init_job(<stem>)`: the same client with `auto_session_tracking: false`, so a
per-export process reports panics without skewing release health. `prisc_ai` stays without a
client (rationale in `CAPTURE_PROTOCOL.md` §3.4). `Channel::error(.., fatal: true)` calls
`telemetry::flush()` before exiting; because `prisc_ipc` cannot depend on `prisc_rust_core`, the
flush is registered with `lifetime::on_fatal(fn())` by the helper.

## 4. Message catalog

Notation: `field: type`, `?` = optional (omitted when absent). `Rect` = `{x: i32, y: i32,
w: u32, h: u32}`. Every catalog also contains the common `hello`, `error` and `quit` (§2.2,
§2.3). Terminal events are marked **T**. `QUIT_GRACE` is the parent's wait between `quit` and
kill (§7.5).

### 4.1 `recorder` (`prisc_recorder`), `QUIT_GRACE` = 45 s

Commands:

| Command | Fields | Ack |
|---|---|---|
| `start` | | `started_recording` (from `--pause`) or `recording_resumed` |
| `pause` | | `recording_paused` |
| `mute_speaker` / `unmute_speaker` / `mute_mic` / `unmute_mic` | `index: u32` | none |
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
`Fatal:` and exit 1 become `error{code:"init_failed",fatal:true}`; bad CLI stays clap's exit 2
with no protocol line beyond `hello`.

### 4.2 `share_region` (`prisc_share_region`), `QUIT_GRACE` = 5 s

Commands: `move {region: Rect}` (ack `region_changed`), `obscure {mode: "none" | "blur" |
"pixelate" | "hide", strength?: u32}` (1..=100, default 50 for blur/pixelate; ack `obscure`;
`unobscure` is `obscure{mode:"none"}`). Invalid values: `bad_command`.
`quit`: stop mirroring, emit `done`, exit.

| Event | Fields |
|---|---|
| `initialized` | |
| `sharing_started` | `region: Rect` |
| `region_changed` | `region: Rect` (as applied, after clamping) |
| `obscure` | `mode: string`, `strength: u32` (0 for none/hide) |
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
`quit` = the old `cancel`: write nothing, no `done`, exit 0. (A `quit`-initiated exit without a
terminal event is not a crash because the parent asked for it, §7.5.) The old `cancel` command
is removed.

| Event | Fields |
|---|---|
| `ready` | |
| `status` | `frames: u32`, `height_px: u32`, `state: "rewinding" \| "paused" \| "resuming" \| "scrolling" \| "settling" \| "stitching"`, `resume_in_s?: u64` |
| `done` **T** | `result: "complete" \| "stopped" \| "max_reached" \| "no_movement"`, `frames: u32`, `height_px: u32` |

`fatal_error` becomes `error{fatal:true}` and now **exits 1** (was 0). The `failed` result is
dropped. `session.json` is still written before `done`, unchanged.

### 4.5 `capture` (`prisc_capture`), `QUIT_GRACE` = 5 s

Commands (standby only; one-shot ignores stdin except EOF/`quit`), shapes unchanged:
`settings {args: [string]}` (answered by `settings_status`, or `error{code:"rejected"}` then
the surviving `settings_status`), `capture {mode: "region" | "screen" | "window"}`,
`pause {paused: bool}` (no reply).

`quit`: between captures, exit 0. Mid-capture, exit 0 immediately without writing the session
(today's EOF behaviour).

| Event | Old line | Fields |
|---|---|---|
| `ready` | `PRISC_STANDBY_READY` | |
| `session` | `PRISC_SESSION <dir>` | `dir: string` |
| `capture_finished` | `PRISC_CAPTURE_FINISHED <dir>` | `dir: string` |
| `settings_status` | `PRISC_SETTINGS_STATUS <json>` | `hotkeys: {main: [HotkeyStatus], window: [HotkeyStatus], monitor: [HotkeyStatus]}` |
| `error` | `PRISC_IPC_ERROR <text>` | `code: "rejected"` (bad snapshot, capture while busy) or `"io_failed"` (session dir); `fatal:false` |

`HotkeyStatus` = `{gesture: string, active: bool, error?: string, error_code?: string}` (codes
as in `CAPTURE_PROTOCOL.md` §1.2).

**One-shot** keeps `action.txt` + `session.json` exactly as today. What changes: all three
stdio streams are redirected (it gets `hello` on stdout and the stdin lease, so a dead parent
ends the overlay instead of orphaning it), it emits `capture_finished{dir}` just before a normal
exit, and the parent's job object (§7.4) covers it. It has no terminal event; the parent waits
for exit, and exit codes 3/4 keep their meaning.

### 4.6 `prisc_ai` (no catalog)

Payload protocols unchanged (OCR header + BGRA through `--out`; matte/denoise frames on
stdout). It adopts: `claim_stdout()` (replacing `claim_payload_stdout`, matte/denoise only, as
today), `lifetime::install` with the parent-pid watchdog (stdin EOF is end-of-input here, not
quit, so `read_commands` is not used), `exit::*` constants, stderr logging. No `hello`: stdout
is payload. prisc_capture spawns it via `locate::sibling("prisc_ai")` + `locate::mark_child`.

## 5. Exit codes (`prisc_ipc::exit`)

| Code | Const | Meaning |
|---|---|---|
| 0 | `OK` | Every outcome reported on the protocol, cancel included. |
| 1 | `FAILED` | Fatal failure; a fatal `error` was emitted (best effort). |
| 2 | `USAGE` | Bad command line (clap's own code). |
| 3 | `NO_SCREEN_PERMISSION` | macOS Screen Recording not granted. |
| 4 | `CAPTURE_FAILED` | Desktop screenshot failed; scroll driver on an unsupported platform. |
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
finite-float serializer) and (b) a `const SPEC: CatalogSpec` describing the same thing as data.
The macro is the only place either is written, so the two cannot drift.

```rust
ipc_catalog! {
    catalog job;
    helpers ["prisc_vid2gif", "Prisc.VideoRender"];
    quit_grace_ms 5000;
    strings JobResult { ok, cancelled }
    events {
        progress { percent: u32 }
            sample r#"{"type":"progress","percent":42}"#;
        terminal done { result: JobResult, path: Option<String>, bytes: Option<u64> }
            sample r#"{"type":"done","result":"ok","path":"/tmp/a.gif","bytes":1024}"#;
    }
    commands {}
}
```

Field types the macro accepts: `bool u32 u64 i32 i64 f64 String Vec<T> Option<T>`, a
`strings Name { .. }` string enum, and a `record Name { .. }` struct declared in the same
invocation. Nothing else (no `serde_json::Value`, no maps). `hello`, `error` and `quit` are
injected into every catalog by the macro.

**Generator**: `crates/prisc_ipc/src/csharp.rs`, modelled on `prisc_hotkeys/src/csharp.rs`.
It renders every `SPEC` into **`ui/Prisc.Ipc/Generated/HelperMessages.g.cs`**, and test
`csharp_mirror` fails when the checked-in file differs. Regenerate with
`PRISC_IPC_REGEN=1 cargo test -p prisc_ipc csharp_mirror`. Because the generator is a plain
cargo test in a default-member crate, the drift check runs in the existing Rust CI job with no
libobs build.

**Generated C# shape** (namespace `Prisc.Ipc`, `#nullable disable`):

```csharp
public static class IpcProtocol { public const int Version = 1; }
public static class HelperExit { public const int Ok = 0, Failed = 1, Usage = 2,
    NoScreenPermission = 3, CaptureFailed = 4, QuitTimeout = 6, InferenceUnavailable = 7, RustPanic = 101; }
public static class IpcErrorCodes { public const string BadCommand = "bad_command"; /* ... */ }

public sealed record IpcHello(int Protocol, string Helper, string Version, int Pid);

public abstract record JobEvent
{
    public sealed record Progress(uint Percent) : JobEvent;
    public sealed record Done(JobResult Result, string Path, ulong? Bytes) : JobEvent;
    public sealed record Error(string Code, string Message, bool Fatal) : JobEvent, IIpcError;
}
public enum JobResult { Ok, Cancelled }
public abstract record JobCommand { }   // recorder: RecorderCommand.Configure(string Path), ...

public static partial class HelperSpecs
{
    public static readonly HelperSpec<JobEvent, JobCommand> Job = new(
        catalog: "job", quitGrace: TimeSpan.FromMilliseconds(5000),
        decode: JobCodec.Decode, encode: JobCodec.Encode, isTerminal: e => e is JobEvent.Done || e is IIpcError { Fatal: true });
}
```

The codec is generated straight-line `Utf8JsonReader`/`Utf8JsonWriter` code (no reflection, no
`JsonSerializerContext`), so it is trim-safe and independent of `PriscUiJsonContext`. Decode
returns null for an unknown `type` (the client logs and skips it). Enum values decode from the
snake_case string; an unknown string decodes to an extra generated `Unknown` member rather than
failing the line.

**Conformance**: `crates/prisc_ipc/examples/ipc_conformance.rs` emits `hello` and one instance
of every event in every catalog, then echoes each command it receives as
`error{code:"bad_command"}` containing the parsed command's debug form. `Prisc.Ipc.Tests` runs
it when it is built (located with `HelperLocator`'s dev walk-up under `target/*/examples`) and
round-trips every generated command through it; it skips otherwise. Every event and command in
the macro carries a required `sample` line. `cargo test` deserializes each sample into the Rust
type and re-serializes it byte for byte; the generator copies the samples into the generated
file as `HelperSamples`, and `Prisc.Ipc.Tests` decodes and re-encodes each one with the
generated codec. So the samples are the shared golden fixtures, and they live in the one source
file.

## 7. C# side (`ui/Prisc.Ipc`)

`Prisc.Ipc.csproj`: net10.0, `RootNamespace Prisc.Ipc`, `Nullable disable`, no package
references, `AllowUnsafeBlocks` off, `InternalsVisibleTo Prisc.Ipc.Tests`. Referenced by
Prisc.VideoSDK, Prisc.Ui and Prisc.VideoRender. New `ui/Prisc.Ipc.Tests` (xunit, like
Prisc.Shared.Tests), added to `Prisc.slnx` and to the `ci.yml` test step that runs
Prisc.Shared.Tests.

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
    internal static string Find(string fileName, string envDir, string baseDirectory); // testable
}
```

Probe order for file name `f` = `Platform.ExeName(stem)` (or `LibName`), first existing wins:

1. `$PRISC_HELPERS_DIR/f`, then `$PRISC_HELPERS_DIR/recorder/f`.
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
    public ChannelReader<TEvent> Events { get; }          // unbounded, single reader, in order
    public Task<HelperOutcome<TEvent>> Completion { get; } // never faults
    public string StderrTail { get; }                       // HelperLog snapshot

    public bool Send(TCommand command);                     // false once stdin is closed
    public Task<TEvent> SendAndWaitAsync(TCommand command, Func<TEvent, bool> isReply, TimeSpan timeout);
    public Task<HelperOutcome<TEvent>> StopAsync();         // §7.5, idempotent
    public void Kill();                                     // tree kill now
    public ValueTask DisposeAsync();                        // StopAsync + join pumps
}

public sealed record HelperStartOptions
{
    public bool KillWithParent { get; init; } = true;       // job object (§7.4)
    public bool GrantForeground { get; init; }              // AllowSetForegroundWindow
    public IReadOnlyDictionary<string, string> Environment { get; init; }
    public string WorkingDirectory { get; init; }           // default: exe's directory
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int StderrLines { get; init; } = 1000;
    public int StderrChars { get; init; } = 256 * 1024;
}

public sealed record HelperOutcome<TEvent>(
    TEvent Terminal,            // done/terminal event or fatal error; null when none
    int? ExitCode,
    HelperFailure Failure,      // None when Terminal is a non-error terminal or we stopped it
    string StderrTail);

public enum HelperFailure { None, StartFailed, HelloTimeout, ProtocolMismatch,
    FatalError, NoScreenPermission, CaptureFailed, InferenceUnavailable, Crashed, Timeout }
```

Behaviour:

- **Start**: `HelperProcess.Start` (UTF-8 no BOM, all three streams redirected, `AutoFlush`,
  `ArgumentList`), env `PRISC_PARENT_PID=Environment.ProcessId` plus `options.Environment`,
  job assignment, optional foreground grant, then the stdout and stderr pumps. `StartAsync`
  completes when `hello` arrives; it fails (after `Kill`) on `HelloTimeout`, on exit before
  hello (`StartFailed`, or the exit-code mapping below), or on `ProtocolMismatch`.
- **stdout pump**: each line is decoded with `spec.Decode`. `hello` is consumed by the client.
  Unknown types are logged to the `HelperLog` as `[stdout] <line>` and skipped; non-JSON lines
  likewise (the guard makes them a bug). Events are written to `Events` in order; the first
  terminal event is recorded for `Completion`. The channel completes at stdout EOF.
- **stderr pump**: every line into `HelperLog` and `Debug.WriteLine`.
- **Completion**, on process exit after both pumps drain: terminal non-error event → `None`;
  fatal `error` → `FatalError`; no terminal and `StopAsync` was called → `None`; otherwise map
  the exit code: 3 → `NoScreenPermission`, 4 → `CaptureFailed`, 7 → `InferenceUnavailable`,
  anything else (including 0) → `Crashed`.
- **Send**: serializes with `spec.Encode` and writes one line under a lock; an `IOException`
  (child gone) returns false and marks stdin closed.

### 7.3 Grace policy (one policy, per-catalog duration)

`StopAsync`: send `{"type":"quit"}`, close stdin, wait `spec.QuitGrace` for exit, then
`Kill(entireProcessTree: true)`, wait 2 s more, then give up and report `Timeout`. Durations
come from the catalog: recorder 45 s (mp4 flush and encoder drain), everything else 5 s. This
replaces the 2/5/10/45 s constants in the runners. Helper-specific readiness timeouts
(recorder `initialized` 30 s, share `initialized` 30 s, configure/pause acks 10 s) stay in the
runners as `SendAndWaitAsync` timeouts.

### 7.4 Kill-with-parent

Windows: one process-wide job created lazily with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`; every
child started with `KillWithParent` is assigned right after `Process.Start`. If Prisc.Ui dies,
the OS kills them. **The recorder opts out** (`KillWithParent = false`): its parent-death path
is stdin EOF → stop and flush the mp4, then the §3.3 timer, which keeps the recording of a
crashed Prisc.Ui instead of truncating it. macOS/Linux: no job; the child's watchdog
(`PRISC_PARENT_PID`) and stdin EOF do the job.

### 7.5 Expected exits

A process that exits after `StopAsync` without a terminal event is not a crash (scroll `quit`,
standby EOF, a cancelled one-shot overlay). Any other exit without a terminal event is.

### 7.6 Child side for Prisc.VideoRender (`IpcChild`)

```csharp
public static class IpcChild
{
    public static IpcWriter<TEvent> Open<TEvent>(string helper, Func<TEvent, string> encode); // claims stdout, writes hello
    public static void ReadCommands<TCommand>(Func<string, TCommand> decode,
        Action<TCommand> onCommand, Action<string> onBadCommand); // background thread; quit/EOF -> OnQuit
    public static event Action OnQuit;                               // raised once
    public static void WatchParent(TimeSpan grace);                  // PRISC_PARENT_PID; hard exit 6 after grace
}
```

`Open` keeps `Console.OpenStandardOutput()` privately for protocol lines and calls
`Console.SetOut(Console.Error)` so stray `Console.WriteLine` cannot corrupt the stream (managed
code only; VideoRender loads no native code that prints to fd 1 except FFmpeg, whose logging it
already routes through `DiagnosticLog`). `IpcWriter.Emit` writes one line under a lock and
flushes; it never throws (an `IOException` marks the writer closed and raises `OnQuit`). The
generated `JobCodec.Encode`/`Decode` for events are used on this side too.

### 7.7 Runner map

| Today | After | Spec | Options |
|---|---|---|---|
| `ObsCapturer` | `RecorderDriver` | `HelperSpecs.Recorder` | `KillWithParent = false` |
| `CameraDeviceManager` (`--list-cameras` spawn) | same class, `HelperClient` awaiting `cameras` | `Recorder` | |
| `ShareRegionDriver` + `ShareRegionProtocol` | `ShareRegionDriver` | `ShareRegion` | `GrantForeground` |
| `Vid2GifRunner`, `VidRenderRunner` | one `JobRunner` with `ForVid2Gif(...)` / `ForVideoRender(...)` | `Job` | render passes `PRISC_FFMPEG_PATH` in `Environment` |
| `ScrollDriver` + `ScrollDriverProtocol` | `ScrollDriver` | `Scroll` | `GrantForeground` |
| `CaptureStandbySupervisor` | same class (keeps backoff, stable-uptime, fallback logic) | `Capture` | `GrantForeground` |
| `ScreenCaptureService` one-shot spawn | same class, `HelperClient` awaiting exit | `Capture` | `GrantForeground` |
| `AiClient` (VideoSDK) | same class on `HelperProcess.Start` + `HelperLog` + `JobObject`, binary streams; no `HelperClient` | none | `KillWithParent` |
| `AiLoader` resolver seam | `HelperLocator.Find("prisc_ai")`; `Configure` removed | | |
| `HotkeysLibraryLocator` | `HelperLocator.FindLibrary("prisc_hotkeys")` | | |
| VideoRender `Program.cs` protocol + `FindAiBinary` + `FindFFmpegDirectory` | `IpcChild` + `HelperLocator` + `FFmpegLoader.FindBesideRecorder` | `Job` (child side) | |
| `TestFFmpeg` (VideoSDK.Tests) | `FFmpegLoader.FindBesideRecorder` | | |

## 8. Migration

**Phase 0 (serial, Rust, small; merged before the parallel halves branch):** create
`crates/prisc_ipc` with `exit`, the `ipc_catalog!` macro, all five catalogs exactly as §4, the
generator, and the checked-in `ui/Prisc.Ipc/Generated/HelperMessages.g.cs` plus a minimal
`ui/Prisc.Ipc/Prisc.Ipc.csproj` that compiles it. No helper changes. Both halves branch from
this commit; neither edits the catalog afterwards without regenerating and telling the other.

The wire change is breaking in both directions, so the two halves **merge together** (one
integration branch, then the smoke tests: record, share, GIF export, render, scroll capture,
standby capture, one-shot capture, OCR, matte).

### 8.1 Rust half (worktree A)

1. `prisc_ipc`: `open`/`Channel`, `claim_stdout`, `read_commands`, `lifetime` (signals from
   `recorder.rs` ~1760-1850, watchdog, hard exit), `single_line`, `locate`, the conformance
   example. Unit tests: BOM line, invalid UTF-8, oversize line, EPIPE on emit, quit/EOF
   idempotence.
2. `prisc_rust_core`: depend on `prisc_ipc`; `exit.rs` becomes the re-export; add
   `telemetry::init_job` and keep `flush`.
3. `prisc_recorder`: replace `status::emit_json`/`emit_simple`/`emit_stopped_recording`/
   `emit_configure_*` with `Channel<recorder::Event>`; `commands.rs` text parser replaced by
   `recorder::Command` → internal `Command` mapping (internal variants `OutputStarted`,
   `SetSpeakerVolume`, ... stay); `spawn_stdin_thread`, `install_signal_handler`, `console_ctrl`
   replaced by `read_commands` + `lifetime::install` (`set_exit_fn(platform::exit_process)`);
   field renames; `done` result mapping; `cameras.rs` emits `cameras`/fatal `error`;
   `telemetry::init`; `exit::*` constants. Update README protocol sections to point here.
4. `prisc_share_region`: same treatment (`status.rs`, `commands.rs` grammar to JSON; keep the
   in-order rejection queue, now emitting `error{bad_command}`); `ctrlc` handler replaced by
   `lifetime`; `done{stopped|cancelled}`; `telemetry::init`.
5. `prisc_vid2gif`: `Channel<job::Event>`; `spawn_stdin_watcher` replaced by
   `read_commands` + `lifetime` tripping `CancelToken`; `done`/fatal `error`;
   `telemetry::init_job`. Fixes the unflushed `println!` lines and the missing EOF handling.
6. `prisc_scroll_driver`: `DriveEvent`/`DriveCommand`/`emit` replaced by the `scroll` catalog
   types; `cancel` → `quit`; fatal exits 1; drop `DriveResult::Failed`.
7. `prisc_capture`: `standby::emit_line`/`emit!`/`emit_ipc_error`/`report_settings_status` and
   the hand-rolled stdin thread replaced by `Channel<capture::Event>` + `read_commands`;
   `main.rs` one-shot path: `open`, `lifetime` (EOF mid-capture exits 0), `capture_finished`;
   `ocr/client.rs`: `locate::sibling("prisc_ai")` + `mark_child`, `PRISC_AI_BINARY` removed.
8. `prisc_ai`: `claim_payload_stdout` → `prisc_ipc::claim_stdout`; `lifetime::install` with
   watchdog; exit constants.
9. Docs: `CAPTURE_PROTOCOL.md` §1.2 standby stdio paragraphs, §1.4, §2.2-2.4 and §3.1 env var
   reduced to pointers here; vid2gif/share_region headers and READMEs likewise.

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
   task) plus the conformance test.
2. Rename and port runners per §7.7, and apply the rename map below.
3. `ScreenCaptureService` one-shot: redirect all three streams through `HelperClient`;
   `CaptureStandbySupervisor`: `Kill()` → tree kill via the client.
4. `Prisc.VideoRender/Program.cs`: `IpcChild`, `job` catalog, `cancelled` spelling.
5. Env vars: `ci.yml` sets `PRISC_HELPERS_DIR=$(pwd)` (the directory holding `recorder/`) in
   place of `PRISC_RECORDER_PATH`; `BUILDING.md` documents `PRISC_HELPERS_DIR` and
   `PRISC_FFMPEG_PATH` only.
6. `VideoCapturePage`: drop the `ObsCapabilities` probes (window capture and mkv are always
   supported by the shipped recorder).

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
