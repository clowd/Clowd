# prisc_interop

The one native library Prisc's C# hosts load in-process (Prisc.Ui, Prisc.VideoRender and their
tests): a thin C ABI over the shared Rust crates (prisc_platform, prisc_rust_core, prisc_hotkeys,
prisc_ipc) plus the OS code that has no other Rust consumer. This file is the ABI contract.

- `src/ffi/*.rs`: the exports, one file per area. Nothing else is part of the ABI.
- `src/platform/{windows,macos}`: the OS halves the exports call.
- `src/panic.rs`, `src/error.rs`, `src/util.rs`, `src/convert.rs`: the boundary machinery.
- `crates/prisc_codegen` generates the C# side from `src/ffi` (`cargo codegen`):
  `ui/Prisc.Interop/Generated/NativeMethods.g.cs` (DllImports, structs, handles, internal),
  `InteropTypes.g.cs` (the public enums) and `InteropAbi.g.cs` (the ABI fingerprint). Its test
  (`cargo test -p prisc_codegen`) fails when those are stale and enforces every crate rule below.

Building: `cargo build -p prisc_interop` puts `libprisc_interop.dylib` / `prisc_interop.dll` in
`target/debug`, where the C# loader's dev walk-up finds it. The shipped library is built in its
own cargo invocation so no feature of another package (prisc_capture's `telemetry`) is unified in.

## A. Export shape

A1. Every export is `#[no_mangle] pub unsafe extern "C" fn prisc_<area>_<verb>[_<noun>]` declared
in a file under `src/ffi/`, and returns `i32` status. The only exceptions:
`prisc_last_error_message -> usize` and `prisc_last_error_code -> i64`. The library exports
nothing else: no `#[export_name]` item and no objc2 `define_class!` (whose class statics a cdylib
would export) anywhere in the crate or its path dependencies (lint rules 12 and 14; an Objective-C
class is built at run time with `ClassBuilder`), and CI fails the macOS build on any exported
symbol other than `_prisc_*` (`nm -gU`).

A2. One logical C# operation is one export. A C# wrapper never chains exports to complete one
operation (lifecycle verbs on a handle, and per-frame ring verbs with Skia/FFmpeg work between
them, are separate operations by nature).

A3. Neutral names where semantics unify across OSes (the off-OS arm answers truthfully or
`PRISC_E_UNSUPPORTED`); `_windows` / `_macos` suffix only for a one-OS concept. Every export
exists on every target (Linux included).

A4. Status table (`src/ffi/status.rs`, `#[repr(i32)] pub enum PriscInteropStatus`, emitted as
public C# `InteropStatus`):

| Name | Value | Meaning |
|---|---|---|
| `PRISC_OK` / `Ok` | 0 | success |
| `PRISC_S_NONE` / `None` | 1 | success, nothing to report (no package identity, point on no screen) |
| `PRISC_S_TIMEOUT` / `Timeout` | 2 | a bounded wait expired |
| `PRISC_S_CLOSED` / `Closed` | 3 | a pull stream ended and is drained (hotkeys) |
| `PRISC_E_UNSUPPORTED` | -1 | not on this OS / this build |
| `PRISC_E_INVALID_ARG` | -2 | null pointer, out of range, bad UTF-8, unknown enum value, unparsable text |
| `PRISC_E_OS` | -3 | the OS call failed; code in `prisc_last_error_code` |
| `PRISC_E_PANIC` | -4 | a panic was caught at the boundary |
| `PRISC_E_STATE` | -5 | wrong thread, handle stopped/destroyed, called twice |
| `PRISC_E_PERMISSION` | -6 | a TCC/OS permission is missing |
| `PRISC_E_CAPABILITY` | -7 | hardware/driver lacks a needed feature |

Positive values are benign. New members only by appending.

A5. Last error: thread-local slot, cleared on entry by the panic guard, set on every non-OK
status. `prisc_last_error_message(buf, cap) -> usize` returns the full length and writes only if
it fits (never truncates, never allocates); `prisc_last_error_code() -> i64` is the raw OS code
(HRESULT / OSStatus / errno sign-extended from i32, 0 when none). No TLS path can unwind
(`try_with` + `try_borrow`), because exports run on finalizer and OS callback threads.

A6. Panic guard (`src/panic.rs`): every export body is `panic::guard("<its own name>", || ...)`.
Rust code never calls an export (lint). The host may re-enter from an OS-loop callback (R20): the
guard keeps a thread-local depth, and a nested entry saves the outer last-error slot and restores
it on exit when it held an error, so the outer call's error survives; an empty outer slot is left
alone, so the nested call's own failure stays readable by its caller. Workspace keeps
`panic = "unwind"`.

## B. Types

B1. `#[repr(C)]` structs and `#[repr(i32|u32|u8)]` enums live only in `src/ffi/` (shared ones in
`src/ffi/types.rs`: `PriscString { ptr: *mut u8, len: usize }`, `PriscRect { x, y, width, height: i32 }`,
`PriscPoint { x, y: i32 }`).

B2. No enum type appears in an export signature or a `#[repr(C)]` field (R6). Parameters and
fields are `i32`/`u32`; Rust converts with `from_raw` (`convert::ffi_enum!`, or `convert::flags`
for a bit-flag enum) and answers `PRISC_E_INVALID_ARG` on an unknown value. Every enum not named
`*Flags` has its `crate::convert::ffi_enum!` invocation right after its declaration, listing exactly
its variants (prisc_codegen's lint, rule 13). Enums are emitted to C# as public enums for the
wrappers.

B3. No `bool` across the boundary (R7): `u8`, 0 or 1; Rust treats any non-zero input as true.

B4. Structs whose size is not a multiple of 8 get explicit `_reserved` padding so the layout reads
the same in C#. Every struct has a Rust offset-pinning test (`offset_of!`) and a C#
`Marshal.OffsetOf`/`sizeof` test.

B5. Strings in: `(*const u8, len: usize)` UTF-8, never NUL-terminated; a null pointer with len 0
is the empty string.

B6. Strings out (R8): mode 1 standalone owned `PriscString`, freed by `prisc_string_free` (which
zeroes it; a second free is a no-op); mode 2 owned by a containing allocation (an array freed by
its own `_free`); mode 3 borrowed from a handle, valid until that handle's `_destroy`. Each export
doc names the mode. The caller-buffer convention exists only for `prisc_last_error_message`.

B7. Lists of unknown size are owned arrays with a matching `_free(ptr, count) -> i32`; small
bounded lists may use `(out, capacity, out_count)` where `capacity = 0` is a sizing probe and the
full count is always written.

B8. Structs are never passed by value (R18): inputs are `*const PriscX` (null is
`PRISC_E_INVALID_ARG`), outputs `*mut PriscX`. The lint rejects a by-value struct parameter.

## C. Handles, callbacks, OS objects

C1. Handles: opaque `pub struct PriscX { _private: [u8; 0] }` (no repr) in `src/ffi`, the real
type in `src/platform` or `src/*_host.rs`. Exactly one `_create`/`_open` and one `_destroy` per
handle type. `_destroy(null)` is OK. C# side (R19): the wrapper holds an `InteropHandle`
(SafeHandle) and every call goes through `using var lease = handle.Lease();` (`HandleLease`,
FOUNDATION infra), so `_destroy` runs from `ReleaseHandle` only after the last in-flight call has
returned. A wrapper whose handle can be inside a blocking call (`next_event`, audio input) calls
its `_stop`/`_request_stop` before `Dispose` so the blocked call returns promptly. Finalizable
handles: hotkeys, GPU device/ring/converter, audio output/input. Not finalizable (plain
`IDisposable`, no SafeHandle, destroy on the owning thread only): the Windows foreground watch
(`UnhookWinEvent` is thread-affine). The macOS Services registration has no handle at all: AppKit
holds the provider unretained, so it lasts for the process (there is no unregister export), and a
second registration answers `PRISC_E_STATE`.

C2. Raw OS pointers handed out (ID3D12Device*, id<MTLDevice>, ID3D12Resource*) are borrowed from
the owning handle. Exactly two exports transfer ownership: `prisc_gpu_d3d11_device_acquire_windows`
(an AddRef'd ID3D11Device* that FFmpeg releases) and `prisc_ipc_claim_stdout` (the private stdout
HANDLE/fd the caller closes). Raw OS pointers taken in (HWND, NSWindow*, process HANDLE) are
borrowed for the duration of the call; nothing retains them.

C3. Callbacks only where the OS API is callback-shaped (audio render/input, NSServices, the
foreground WinEvent, the log sink). A callback must not block for long and must not unwind. Re-entry
(R20): the WinEvent and NSServices callbacks are delivered by the OS loop outside any export and may
call exports synchronously (the foreground watch keeps today's synchronous `changed()`); the log
sink (which runs inside an export) and the audio callbacks (OS audio threads) must not call any
export. Rust releases every registry lock before invoking a host callback, so a `Dispose` inside
the callback cannot deadlock. WINEVENTPROC has no user parameter, so Rust keeps a
hook-to-(callback, user) registry. C# implements every callback as a static
`[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` thunk plus a `GCHandle` passed as
`user`; a process-global callback (the log sink, the NSServices delivery) is instead a static
delegate with a null `user` and no `GCHandle`. The thunk body is
`try { ... } catch (Exception e) { /* log once, no rethrow */ }`, because an exception escaping an
unmanaged-callers-only method is a fail-fast.

C4. COM (Windows): `platform/windows/com.rs` `ScopedApartment::multithreaded()`, tri-state
(S_OK/S_FALSE owns the balance, RPC_E_CHANGED_MODE proceeds without uninit). Exports that return
COM objects take no apartment.

C5. Every `extern "C"` (or `"C-unwind"`, `"system"`) fn under `src/platform/**` that the OS calls
(CoreAudio render proc, AudioQueue input callback, HAL property listener, WinEvent proc, the
NSServices method) is a trampoline whose body is
`panic::callback_guard(<slot>, <benign value>, || ...)`: it catches a panic, records it on `<slot>`
and returns the benign value (it logs nothing, so audio paths stay silent). `<slot>` is the
`panic::CallbackPanic` that the handle's state owns, reached through the trampoline's user pointer
(or the hook registry); each export on that handle drains it first (`slot.check()?`) and so reports
the panic as `PRISC_E_PANIC`, while other handles never see it. A trampoline that has no handle to
reach yet (a registry lookup before the handle is known) uses `CallbackPanic::process()`, which
the next export of any kind drains to the log sink. The NSServices method may run with no export
following it, so it owns a static slot and reports a caught panic to the log sink at once. Without the guard a panic would abort the process
(abort-on-unwind out of `extern "C"`) or unwind into Objective-C frames.

## D. Crate rules (enforced by prisc_codegen's lint, one gate: `cargo test -p prisc_codegen`)

D1. No `#[cfg(...)]` or `#[cfg_attr(...)]` on any item under `src/ffi/` (csbindgen never
evaluates cfg), with two exceptions: a `use` item may carry a target cfg
(`#[cfg(target_os = "macos")] use crate::platform::macos::x;`), and a bare `#[cfg(test)]` on an
inline `mod`, which may contain no `#[no_mangle]` fn, no `#[repr(C)]` struct, no `pub const`, no
enum. The platform switch is a cfg'd block inside the body; the `#[cfg(not(...))]` arm returns
`PRISC_E_UNSUPPORTED` (or the truthful off-OS answer).

D2. No panic crosses the boundary (A6).

D3. No telemetry in-process: no `sentry`, no `telemetry` feature of prisc_rust_core, no GPL crate,
and the crate installs no `log` logger (rust_core's `log::warn!` calls are then no-ops). The
crate's own diagnostics go only to the host sink set by `prisc_log_set_callback`.

D4. prisc_interop's own sources spawn no thread (`thread::spawn`, `thread::Builder`; a syn scan
of `src/**` in the codegen lint). Threads owned by a handle are allowed only inside a dependency
and only when started by the handle's `_create` and joined by its `_stop`/`_destroy`; they never
call a host callback and never log. Today exactly one handle owns threads: `PriscHotkeys`
(`prisc-hotkeys-hook` on both OSes, `prisc-hotkeys-watch` on macOS, `prisc-hotkeys-supervise`
for the R17 retry timer; main-queue media-key decode is Rust only). OS callback threads
(CoreAudio render/input, the HAL listener queue, the WinEvent callback on the caller's message
loop) are the only threads running our code that call into the host. No realtime scheduling
class anywhere (memory rule); `THREAD_PRIORITY_HIGHEST` on the Windows hook thread is a normal
class priority and stays.

D5. Exactly one `Thread:` line in every export's doc comment, one of:
- `Thread: any thread.`
- `Thread: any thread; the crate scopes its own COM apartment for the duration of the call.`
- `Thread: must be the macOS main thread.` (answers `PRISC_E_STATE` elsewhere)
- `Thread: must be the macOS main thread; any thread elsewhere.`
- `Thread: any thread, never concurrently with another call on the same handle.`
- `Thread: the thread that owns the window (inside its window procedure).`
- `Thread: a thread that pumps window messages; the callback runs on that thread.`
The lint enforces exactly one line with the `Thread:` prefix; the README lists this vocabulary.

D6. Every enum crossing to C# has an explicit `#[repr]`; every `pub const` in `src/ffi` is a
non-negative literal (csbindgen silently drops anything else) named `PRISC_*`, and no other const
is a `PRISC_*` literal. Those consts are exactly the ones generated into `NativeMethods` (limits C#
checks before it calls, such as `PRISC_KEYBOARD_MAX_CODES`), so a wrapper never copies a number.

D7. No em-dash (U+2014) in any source, doc or generated file of prisc_interop, prisc_platform,
prisc_codegen or ui/Prisc.Interop.

D8. No `#[no_mangle]` or `#[export_name]` item and no `define_class!` in any path dependency of
prisc_interop (lint rule 12: ld64 keeps one of two duplicate symbols silently), and none of the
latter two in prisc_interop itself (rule 14, see A1).

## E. ABI hash and loading

E1. `prisc_codegen/src/abi_fingerprint.rs` (std + syn only, so build.rs can `include!` it)
parses every `crates/prisc_interop/src/ffi/*.rs` (sorted) and `crates/prisc_hotkeys/src/key.rs`,
and hashes (64-bit FNV-1a) a canonical rendering of: every `#[no_mangle]` fn name and signature,
every `#[repr(C)]` struct with field names and types, every opaque handle struct, every `#[repr]`
enum with its discriminants, every type alias, every `pub const` (the `PRISC_*` bounds the
wrappers check against), the `Key` enum discriminants. Doc comments, bodies, formatting and key labels do not count.
prisc_interop's build.rs writes it to `OUT_DIR/abi_hash.rs` (`PRISC_INTEROP_ABI_HASH`);
prisc_codegen writes the same number to `ui/Prisc.Interop/Generated/InteropAbi.g.cs`. Nothing
hash-related is checked into the Rust tree, so the hash causes no merge churn.

E2. `prisc_interop_abi_hash(out_hash: *mut u64) -> i32`. The C# loader calls it through
`NativeLibrary.TryGetExport` + a `delegate* unmanaged[Cdecl]<ulong*, int>` before any
`[DllImport]` resolves, and rejects (frees) a library whose hash differs. Dev loop: edit
`src/ffi/x.rs`, `cargo build` (new hash in the library), run without `cargo codegen` (old hash in
C#) gives "stale bindings: run cargo codegen" at load instead of undefined behaviour.

## F. Adding an export (README "how to")

1. Declare it in the area's `src/ffi/<area>.rs` with docs, status list and its `Thread:` line
   (a new or changed enum keeps its `ffi_enum!` list in step, B2); implement the OS halves in
   `src/platform/...` or call the shared rlib.
2. `cargo codegen` (regenerates NativeMethods, InteropTypes and InteropAbi).
3. Write the C# wrapper in the area's `ui/Prisc.Interop/<Area>.cs` (lease every handle call, R19).
4. Rust tests (null args, unsupported off-OS, layout), a C# smoke test, `cargo test -p prisc_codegen`.

## Exports by area

89 exports. Each area's C# wrapper is `ui/Prisc.Interop/<Area>.cs`.

Every export has a production caller except two that exist for tests, kept on purpose:
`prisc_interop_selftest` (the boundary self-test the C# and probe suites drive) and
`prisc_gpu_nv12_converter_read_windows` (a slow staging readback of an NV12 texture, the only way
the real-GPU C# bridge tests can see what a conversion produced; production only encodes the
textures). Any other export without a production caller is dropped, with its coverage moved into
the crate's own tests.

| Area (`src/ffi/`) | Exports | |
|---|---|---|
| infra: `version`, `error`, `string`, `log`, `selftest` | 7 | version and ABI hash, last error, string free, log sink, the boundary self-test |
| `screens`, `pixels`, `idle`, `system`, `cursor` | 10 | the monitor list, screen grab and pixel read, idle time, animation preference, cursor position (screens and grab: prisc_rust_core::desktop) |
| `window` | 11 | overlay setup, click-through, capture exclusion, foreground, bounds, the foreground watch |
| `ipc`, `process` | 5 | the stdout guard, the kill-on-close job, the foreground grant, FFmpeg preparation, package identity |
| `app`, `services`, `permissions` | 4 | activation policy, the NSServices provider, permission state and request (prisc_platform) |
| `devices`, `audio_output`, `audio_input` | 10 | camera and audio device lists, CoreAudio output and microphone input |
| `gpu` | 19 | the D3D12 or Metal device, the readback ring, the NV12 converter and its textures |
| `hotkeys` | 9 | the supervised keyboard hook (prisc_hotkeys) |
| `gesture`, `keyboard` | 5 | gesture parse, format and check, physical key state, Secure Event Input |
| `color`, `share_region`, `encoding` | 9 | the accent palette, OKLCH conversion and painting, share-region geometry, H.264 quality (prisc_rust_core) |

## Threads the library owns

prisc_interop's own sources spawn no thread (D4). The one handle that owns threads is the
supervised hotkey hook (`prisc_hotkeys_create`): inside prisc_hotkeys it runs the hook thread
(`prisc-hotkeys-hook`, both OSes), the macOS watch (`prisc-hotkeys-watch`) and the supervisor's
retry timer (`prisc-hotkeys-supervise`). `prisc_hotkeys_create` starts them and
`prisc_hotkeys_stop` / `prisc_hotkeys_destroy` join them; they never call a host callback and never
log. The Windows hook thread runs at `THREAD_PRIORITY_HIGHEST`, a normal-class priority; nothing in
Prisc uses a realtime scheduling class.
