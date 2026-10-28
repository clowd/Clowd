# prisc_capture protocol

The contract between the Prisc.Ui shell and the Rust capture processes.

The capture overlay `prisc_capture` runs **one-shot**: the shell spawns one
process per capture with CLI flags; the process shows the overlay, writes its
result into `--session-dir`, emits `done`, and exits. Completion signal = the
terminal `done`, then process exit.

**Stdio is not specified here.** Every process in this document speaks Prisc's
helper protocol, [`HELPER_PROTOCOL.md`](HELPER_PROTOCOL.md): `hello`, JSON-lines
events and commands, `quit`, the error and exit codes (§4.5 for the capturer,
§4.4 for the scrolling driver, §4.6 for `prisc_ai`). This document covers
everything that is not stdio: CLI flags, the session-directory files,
`session.json`, the OCR request and response, and what the events mean.

A **separate binary**, `prisc_scroll_driver` (§2), carries out the second half
of a scrolling capture the overlay already set up. It is not a capture overlay
at all — no window, no event loop, no GPU — and shares the session format with
the capturer and nothing else. It is documented here because it speaks to the
same shell across the same boundary.

A second separate binary, `prisc_ai` (§3, its `ocr` subcommand), does text
recognition. It is the odd one out: the shell never spawns it for this and
never speaks to it — the *overlay* does, per OCR press. (The same binary's
`matte`/`denoise` subcommands serve the video editor's AI effects, over a
different protocol owned by `Prisc.VideoSDK`'s `AiClient.cs`.) It is
documented here because it is the third process in the same family and shares
`prisc_rust_core` with the other two.

Source of truth: `src/settings.rs` (CLI), `src/session_output.rs` (session
files), `crates/prisc_scroll_driver/src/drive.rs` (scrolling-capture driver),
`src/ocr/client.rs` + `crates/prisc_ai/src/ocr.rs` (recognizer), and
`prisc_rust_core` for what the binaries must agree on: the `session.json`
shape (`session.rs`), the recognition contract (`ocr.rs`) and the coordinate
space (`geometry.rs`). The protocol messages and exit codes are
`crates/prisc_ipc`. C# counterparts:
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
| `--accent-color` | `#RRGGBB` / `#RRGGBBAA` (leading `#` optional) | `#3B97D2` | The accent as the user picked it (the OS accent, or their own). The capturer derives everything it paints from it the same way the shell does (`src/accent.rs` mirrors `AccentColors`): the crosshair, selection borders and UI highlights wear it darkened until white labels on it read at 4.5:1, and the hint comet its own OKLCH-derived body and head. |
| `--no-accent-contrast` | flag | correction on | Use `--accent-color` exactly as given for the fills, skipping the contrast correction (the shell's "Maintain minimum contrast" setting, off). |
| `--tips-mode` | `hints` \| `tips` \| `off` | `hints` | Tips/hints overlay at startup (user cycles with `T`). |
| `--no-peek` | flag | peek on | Disable obstructed-window peek-through capture. |
| `--peek-threshold` | 0.0–1.0 | `0.80` | Max obstructed fraction before a window is dropped from hit-testing. |
| `--no-cursor` | flag | cursor on | Start with the captured cursor hidden (user toggles with `M`). |
| `--no-rounded-corners` | flag | rounded on | Keep window selections square. By default a selection made by picking a window (hover + click, `W`, `--capture-mode window`) takes on that window's OS corner radius: the dashed border is drawn rounded and the corner pixels are transparent in the copied / saved image and in `cropped.png`. Dragged, moved or resized selections are always square. The radius is asked of the OS where possible (DWM on Windows 11; the window server's own corner mask on macOS) with a per-version table as fallback — `src/system/corners.rs`. `desktop.png` is never rounded. |
| `--no-upload` | flag | UPLOAD shown | Hide the UPLOAD button — in the capture strip *and* the OCR strip — and drop its `U` accelerator. |
| `--no-share` | flag | SHARE shown | Hide the SHARE button and drop its `H` accelerator. Not the opposite of `--share`: that is a mode which never shows the strip, so the two are sent together whenever the user has trimmed the button away but started a share from the shell's tray item or hotkey. |
| `--no-scroll-capture` | flag | SCROLL shown | Hide the SCROLL button and drop its `L` accelerator. Windows-only button; the flag parses everywhere. |
| `--no-video` | flag | VIDEO shown | Hide the VIDEO button and drop its `V` accelerator. Sent when the shell has recording switched off entirely (there is no per-button switch for VIDEO). Like `--no-share`, it hides the button only: `--video` is a mode that never shows the strip and is unaffected. |
| `--no-ocr` | flag | OCR shown | Hide the OCR button and drop its `O` accelerator. The button is the only way into OCR mode, so this also removes the OCR strip. |
| `--no-image-search` | flag | SEARCH shown | Hide the SEARCH (reverse image search) button and drop its `A` accelerator. Unlike `--no-share` and `--no-video` this removes the action as well as the button: nothing else reaches it. |
| `--capture-mode` | `region` \| `screen` \| `window` | `region` | `region` = free crosshair; `screen`/`window` pre-select the active monitor / foreground window and show the action panel. |
| `--video` | flag | off | Video-region picker: first confirmed selection dispatches the VIDEO action immediately. Requires `--session-dir`. |
| `--share` | flag | off | Share-region picker: first confirmed selection dispatches the SHARE action immediately, skipping the panel. Requires `--session-dir`. The same auto-dispatch shape as `--video`, and mutually exclusive with it: passing both is rejected by clap as a malformed command line, like any other bad flag, because the first confirmed selection cannot be both a recording region and a mirror region. Without the flag the SHARE action is still reachable — from its button on the capture strip. |
| `--filename-pattern` | .NET date-format string | `yyyy-MM-dd HH-mm-ss` | Name the SAVE dialog opens with, rendered against the local clock at the moment SAVE is pressed and uniquified against `--save-dir` (`name`, `name (1)`, …). Mirrors the shell's "Filename pattern" setting, so a capture saved from the overlay is named exactly as one saved from the editor (`src/filename_pattern.rs`, matching `PathConstants.GetFreePatternFileName`). Month/day names render in English and the timezone specifiers (`z`, `K`) are passed through as literals — neither can appear in a pattern that names a file. A pattern that renders to nothing, or to something the OS cannot spell, falls back to `yyyyMMdd_HHmmss_fff`; an extension typed into it is stripped. |
| `--save-dir` | path | none | Folder the SAVE dialog opens in and uniquifies the suggested name against. The shell passes `General.LastSavePath` when it exists. Omit = the dialog opens wherever the OS last left it and the name is not uniquified. |
| `--shell-pid` | pid | none | The shell's process id, so the overlay can hand its foreground rights back as the cycle ends (§2.5). Process-level: the shell knows its own id, and the capturer never outlives it, so the two cannot disagree. Omit in standalone runs. |

### 1.2 Session-directory file protocol

The shell pre-creates the session directory and passes it via
`--session-dir`. Which files appear depends on how the capture ended:

Standby mode intentionally reverses creation ownership. With
`--standby`, the shell supplies `--session-root` plus any number of
`--hk-main`, `--hk-window`, and `--hk-monitor` occurrences — each flag is
repeatable, one occurrence per gesture in the action's list order (less any
combination the OS reserves for itself, which the shell never binds), and zero
occurrences means that action has no hotkey (hotkeys take effect when the
first settings snapshot arrives — see below). The capturer emits `ready` and
waits event-driven without initializing capture/GPU
resources (the main loop is fully blocked, and so is the Windows hook thread
between key events; on macOS the hook thread and a permission watcher wake
about once a second for tap-health and Accessibility checks, and a hook that
is down is retried every 2 s while a gesture needs it — see below), then
creates a unique child directory and emits `session` with its absolute path
when a hotkey fires, followed by one ordinary capture cycle. The log goes to
stderr and the session's `capture.log`. Stdin is a lifetime lease (the quit
path of HELPER_PROTOCOL.md §3.3): a quit while waiting ends with
`done{result:"stopped"}` without starting a capture, and a quit mid-capture
closes the overlay without writing the session and ends the same way.

The capturer owns the screenshot hotkeys, via a low-level keyboard hook
(the prisc_hotkeys crate: WH_KEYBOARD_LL on Windows, a CGEventTap on macOS —
which needs the Accessibility permission; there is no Linux backend yet, and
that error is reported per gesture), NOT via RegisterHotKey-style
registration. Both halves of that choice are load-bearing: the trigger path
must not depend on the shell being resident (a paged-out Prisc.UI would add
its page-in time to every capture), and the key must be suppressed before OS
handlers see it — Windows 11's "PrintScreen opens Snipping Tool" feature
consumes the key ahead of RegisterHotKey delivery, so a plain registration
reports success and then never fires. Gestures travel in the settings form:
`Control`/`Alt`/`Shift`/`Meta` joined with `+`, then a canonical key name
(`PrintScreen`, `Control+Shift+D1`, `Alt+Meta+Oem3`); aliases such as
`Snapshot` are accepted. The shell translates nothing — the key names are
prisc_hotkeys' key table, which the shell's `HotkeyKey` is generated from —
and a key with no code on this OS surfaces as an explicit per-hotkey error.
A hook that cannot start, or dies later, is fatal only on Windows, where the
failure (`HookFailed`, the hook's message loop failing, or a silently
removed hook that could not be re-installed) is specific to
this process: it ends with a fatal `error` (`init_failed` at startup,
`failed` later) and exit 1 (a death noticed under an open overlay waits
until that capture has finished), so the shell's crash accounting
restarts it or falls back. Everywhere else the process stays up for tray
captures and reports the error on every gesture. On macOS the ways the tap
fails — `PermissionDenied`, a NULL tap (`HookFailed`), a running tap ended by
a revoked grant, repeated timeouts or invalidation (`Stopped`) — come down to
the Accessibility grant, which the shell's own hook shares, or are transient,
so the capturer retries the hook every 2 s while the last snapshot holds a
gesture it would register — not before the first snapshot, nor with no
gestures or only refused ones (a settings snapshot also retries it, once 2 s
have passed since the last try) — logs a start failure once per change of
reason rather than per try and a death as an error only the first time per
run (a revoked grant never), and announces each change (the
hook coming up, going down, failing differently, or dying and coming straight
back with its gestures registered again) with an unsolicited
`settings_status`; granting
Accessibility later makes the hotkeys work without restarting anything. The
capturer never prompts for the permission — the shell does, keyed off
`error_code` (below). `Unsupported` (Linux) is reported and never retried.
Standby is not the only capture path: the
shell only runs it while its "keep capturer warm" setting is in effect, and
it falls back to its own prisc_hotkeys registrations plus classic one-shot
spawns for the rest of its process lifetime when standby proves unusable —
no permission or binary, exit code 3 (permission revoked), or repeated
crashes.

While waiting, stdin carries the catalog's commands. A `settings` command
replaces the complete standby CLI snapshot (including hotkeys) after parsing
its `args` through `CliArgs`; every snapshot is answered with
`settings_status` (statuses cannot arrive out of order: one pipe, one
sequential reader), and one may also arrive unasked when the hook comes up or
goes down. Its `hotkeys` object
maps each of `main`, `window`, and `monitor` to an array with one entry per
gesture received for that action, in the order received; `gesture` echoes
the received string verbatim, which is what the shell matches back to
`HotkeyGesture.ToSerializedString()` of its own gestures:

```json
{"type":"settings_status","hotkeys":{
  "main":[{"gesture":"PrintScreen","active":true},
          {"gesture":"Control+Shift+1","active":true}],
  "window":[{"gesture":"Shift+Control+1","active":false,
             "error":"duplicate gesture","error_code":"duplicate"}],
  "monitor":[{"gesture":"Control+PrintScreen","active":true}]}}
```

`error` is display text (prisc_hotkeys' error text, echoed verbatim) and may
change; `error_code` is its stable, machine-readable twin, and the one to
branch on. Both are absent exactly when `active` is true. Codes: `permission`
(macOS Accessibility not granted, or a stale grant: listed and trusted, but
the tap gets no key events — the shell tells the two apart with
`AXIsProcessTrusted`, prompting and showing its grant instructions in place
of `error` only for a missing grant, and its re-add instructions for a stale
one),
`unsupported` (no hook on this OS), `hook_failed` (the OS refused the hook),
`hook_stopped` (the hook died and is not back yet), `invalid_gesture` (does
not parse), `no_key`, `modifier_key` (a bare modifier), `unsupported_key` (no
code on this OS), `duplicate`. A code the shell does not know means nothing
special: it shows the text.

Hook-based hotkeys cannot conflict with other applications, so a gesture is
active exactly when it parses, names a key usable on this OS (not `None`, not
a bare modifier), and the hook is running; a rejected gesture or a missing
hook is non-fatal and does not prevent other hotkeys or settings from being
applied. The shell gives each gesture to at most one action; as
defense in depth, a gesture repeated anywhere in the snapshot (compared after
parsing) registers at its first occurrence and every later one reports
`"active":false,"error":"duplicate gesture"`. A snapshot that fails to parse
changes nothing and is answered with `error{code:"rejected"}`, then the
surviving hotkeys' real state. A `pause` command (`paused` true or false) pauses or resumes the hotkeys
(paused: registered gestures neither fire nor are suppressed) while the shell
records a gesture in its settings UI, so the recorded press reaches the
shell. It is applied immediately, even mid-capture, gets no reply, and the
shell re-sends it after every `ready`. A `capture` command (`mode` `region`,
`screen` or `window`; `monitor` is accepted for `screen`) starts the same
single capture cycle as a native hotkey. This is how shell and tray capture
buttons reuse the waiting process instead of spawning a second one; the shell
sends one only when no overlay is open or requested — nothing is ever queued
on either side. A non-fatal `error` is the NACK: `rejected` for a refused
snapshot or a capture request that arrived while one was open, `io_failed`
for a failed session-dir creation, `bad_command` for a malformed line; the
shell clears its overlay-pending state on it. Overlay exclusion is per-side:
during the brief windows where the shell falls back to a one-shot spawn, a
native hotkey can still race a second overlay from the standby process —
accepted as rare and harmless rather than adding cross-process locking.

Normal completion does not exit the standby process. After all overlay windows,
render workers and GPU objects have been dropped, the capturer closes that
session's `capture.log`, emits `capture_finished` with the directory (the
shell dispatches it immediately) and emits `ready` again. Process exit is
therefore reserved for the quit path (`done{stopped}`) or a fatal `error`,
after which the shell restarts the standby process (with
backoff) or falls back as above.

The keyboard hook stays installed while an overlay is active, so the
gestures remain suppressed system-wide for the overlay's duration (the
dedicated hook thread keeps pumping regardless of what the capture cycle is
doing). Everything received during that interval, hotkey presses and stdin
commands alike, is stale and discarded before the next `ready` (a `capture`
request is still answered with `error{rejected}` and a malformed line with
`bad_command`, in arrival order; best effort: an event in flight between hook and
channel during the final microseconds of teardown can survive the drain and
start a capture, which is what the press asked for). The shell re-sends its
full settings snapshot in
response to every `ready`, so dropped settings cannot be lost, and that
snapshot is also the only thing that changes the hooked gesture set —
configuration has exactly one path.

| Outcome | Files written | `action.txt` content |
|---|---|---|
| EDIT | `desktop.png`, `cropped.png`, [`cursor.png`], `session.json` | none (any stale marker from a failed retry is deleted) |
| UPLOAD | same as EDIT + `action.txt` | `upload` |
| SELECT-COLOR | `action.txt` only | `select-color #RRGGBB` |
| VIDEO | `cropped.png` (poster frame), `action.txt` | `video X,Y,W,H [R]` |
| SCROLL | `action.txt` only | `scroll X,Y,W,H PX,PY HWND` |
| SHARE | `action.txt` only | `share X,Y,W,H` |
| SEARCH-IMAGE | `cropped.png`, `action.txt` | `search-image` |
| OCR-UPLOAD | `ocr.txt`, `action.txt` | `ocr-upload` |
| COPY / SAVE | none — handled inside the capturer (clipboard / save dialog, named per `--filename-pattern`) | — |
| OCR-COPY / OCR-SEARCH | none — handled inside the capturer (clipboard / browser launch) | — |
| Canceled (Escape / close) | none | — |

File contents:

| File | Contents |
|---|---|
| `desktop.png` | Full virtual-desktop bitmap, locked peek window composited, never the cursor (the editor toggles cursor visibility itself). |
| `cropped.png` | Preview of the selection, peek composited; cursor composited only if visible to the user; corners transparent when the selection is a picked window and rounded corners are on (`--no-rounded-corners`). For VIDEO: no peek compositing (the recording shows real obstructions) and never rounded. For SEARCH-IMAGE: exactly as for EDIT/UPLOAD — it is the image the user is looking up, so it must be the image they were shown. |
| `cursor.png` | Desktop crop at the cursor rect with the cursor composited. Absent when no cursor was captured or the OS reported it hidden. |
| `session.json` | Session metadata (§1.3). |
| `ocr.txt` | The text recognized in the selection, UTF-8 without BOM, lines separated by `\n`. Present only with the `ocr-upload` marker; the shell reads it, uploads it as a text paste, and deletes the directory. |
| `action.txt` | Routing marker read by `CaptureSessionDispatcher` (missing = edit). |
| `capture.log` | Mirror of the capturer's log, read by the shell after a non-zero exit — and the only diagnostics that survive a native fault, which never reaches a Rust error path. |
| `scroll.log` | Same, for the `prisc_scroll_driver` pass (§2). A separate name because the driver runs in a directory the overlay already wrote `capture.log` into, and truncating that would erase the diagnostics for the half of the capture that came first. |

**Write ordering** (`session_output.rs`) — the last file to appear is the
completion signal, so readers must wait for process exit and then key off
these files:

1. EDIT/UPLOAD: the three PNGs are written first (in parallel), then
   `action.txt` is written (upload) or removed (edit), then **`session.json`
   strictly last**. `session.json` present = capture succeeded.
2. VIDEO: `cropped.png` first, then **`action.txt` last**. Its appearance is
   the completion signal; no `desktop.png`, no `session.json` (the session is
   created by Prisc.Ui when recording finishes).
3. SEARCH-IMAGE: `cropped.png` first, then **`action.txt` last**. Its
   appearance is the completion signal; no `desktop.png` and no
   `session.json` — the shell hands that one file to the browser to search
   with and deletes the directory.
4. OCR-UPLOAD: `ocr.txt` first, then **`action.txt` last**. Its appearance is
   the completion signal; no PNGs and no `session.json` — the recognized text
   is the entire payload, and the shell uploads it as a text paste.
5. SELECT-COLOR / SCROLL / SHARE: `action.txt` only.
6. Neither `session.json` nor `action.txt` present = the capture was
   canceled; the shell deletes the pre-created directory.

The VIDEO rect is emitted in the platform capture coordinate space: physical
pixels (virtual-desktop, possibly negative origin) on Windows, CG points on
macOS — passed verbatim to prisc_recorder `--region`. W and H are always >= 2.

`R` is the optional corner radius of the recording region, in that same space
and separated from the rect by a space: the OS corner radius of the window the
user picked (see `--no-rounded-corners`), capped at half the shorter side.
Present only when non-zero; absent = square. Nothing on the video path is
actually rounded — the recorder captures the raw region and `cropped.png` is
its poster — so `R` is metadata only, the video counterpart of `session.json`'s
`CornerRadius`: the shell stores it on the recording's session and the video
editor seeds the screen track's rounded-rect mask from it.

The SCROLL marker uses the same rect space and adds two fields: `PX,PY` is
the point the scrolling capture driver parks the cursor at and aims wheel
events from — always inside `X,Y,W,H` — and `HWND` is the decimal top-level
window handle under that point, or `0` when the walker could not resolve one
(the driver then falls back to `WindowFromPoint`). Unlike VIDEO there is no
poster frame: the driver produces every image plus the `session.json` for
the stitched result, so `action.txt` is the whole overlay payload. macOS
never emits this marker.

The SHARE marker is the leading rect of the VIDEO marker and nothing else —
same space, same grammar, no radius and no poster. The shell hands it
straight to `prisc_share_region --region X,Y,W,H`, a long-lived helper that
mirrors the rectangle into a borderless window a meeting app can pick out of
its share picker; the pixels are read live off the screen for as long as the
share lasts, so there is no frame worth photographing at selection time and
no session to open when it ends. The helper normalises what it is handed
(each side forced to at least 64 px and made even) and reports the region it
actually applied, so unlike VIDEO — which prisc_recorder takes literally — the
rect here is a request. It is emitted two ways: by the SHARE button on the
capture strip (accelerator `H`), and by `--share`, which auto-dispatches the
same action the moment a selection is confirmed — the twin of the VIDEO
button and `--video`.

The SEARCH-IMAGE marker carries nothing at all — the payload is
`cropped.png` beside it. The shell writes the image into a one-shot local
HTML page and opens that in the default browser, which posts it to Google
Lens and is redirected to the results. The upload is the *browser's*
because Lens binds an upload to the session that made it: a POST from the
shell yields a link that reads "Expired upload" in the browser that opens
it. The capture is transient and no session is registered for it, which is
why the directory is deleted as soon as the file has been read. The action
is reachable only from the SEARCH button on the capture strip (accelerator
`A`), and `--no-image-search` removes both.

The OCR markers come from a second action panel the overlay shows once it has
recognized text inside the selection (Windows only — like SCROLL, the button
is compiled out elsewhere, so macOS never emits them either). Only UPLOAD
reaches the shell: COPY and SEARCH finish inside the capturer, BACK returns to
the ordinary panel without ending the cycle at all, and EXIT is an ordinary
cancel. `ocr-upload` carries no rect — by then the selection has been reduced
to the text in `ocr.txt`.

### 1.3 `session.json` schema

Fixed-schema JSON, shared with `Prisc.Ui`'s `SessionInfo`.
Rects serialize as `{ "X": …, "Y": …, "Width": …, "Height": … }`
(`Prisc.PlatformUtil.ScreenRect` casing).

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
| `CornerRadius` | number, optional | Corner radius in pixels of `CroppedRect` that the capture's corners are rounded with — the OS corner radius of the window the user picked (see `--no-rounded-corners`). Present only when non-zero; absent = square. `cropped.png` already has these corners transparent; `desktop.png` does not, so the editor applies the radius itself when it crops (its image graphic's `CornerRadius`, which the user can then edit). |

### 1.4 Exit codes

The codes, and the terminal line that precedes every exit the capturer
controls, are HELPER_PROTOCOL.md §4.5 and §5 (`prisc_ipc::exit`, mirrored in C#
as the generated `HelperExit`). In short: 0 after `done` for every normal
outcome, cancel included (the shell tells them apart by `done.result` and the
session files); 3 (macOS Screen Recording not granted) and 4 (the desktop
screenshot failed) after a best-effort fatal `error` (`permission` / `failed`);
1 after any other fatal `error`. Any other exit is a crash; the shell reports
it with the stderr and `capture.log` tails.

## 2. Scrolling-capture driver (`prisc_scroll_driver`)

The driver is the second half of a scrolling capture. The first half is an
ordinary capture cycle: the user selects a region, presses SCROLL, clicks the
point to scroll at, and the overlay exits leaving the `scroll X,Y,W,H PX,PY
HWND` marker of §1.2. `CaptureSessionDispatcher` turns that marker into
`CaptureAction.Scroll`, `ScrollCapturePage` puts its border window up around
the region, and spawns the driver to do the mechanical part.

It is a **separate binary**, not a mode of the capturer. Nothing it does needs
a window, an event loop, a GPU or the screen-recording permission dance, and
bringing any of that up would put pixels on screen in front of the content it
is about to photograph. It ships beside `prisc_capture`, which is where
`CaptureBinaryLocator.ResolveScrollDriver` looks for it.

**Windows and macOS.** The loop, the caps, the stitcher, this protocol and the
session output are one implementation; the OS half — injecting a wheel,
parking the cursor, resolving and raising the target, photographing the region
— has a Win32 and a Core Graphics backend. macOS needs **two permissions** and
refuses without them (a fatal `error{code:"permission"}`): Screen Recording to photograph the
region, and Accessibility, because the window server silently discards
synthetic events from a process it does not trust. TCC records both against
`Prisc.app`, which is what makes the shell's own grants cover this executable
(`MacPermissions`); `ScrollCapturePage` asks for Accessibility before it
spawns anything, so the usual path to a missing grant is a dialog with a button
to the right Settings pane rather than a driver refusal.

### 2.1 Spawn

```
prisc_scroll_driver --session-dir <dir> --region X,Y,W,H --point PX,PY --hwnd N [--no-rewind]
```

| Flag | Required | Meaning |
|---|---|---|
| `--session-dir` | yes | The directory the overlay was given. It is empty by the time the driver starts (the shell consumed `action.txt`), and the driver owns it from here. |
| `--region X,Y,W,H` | yes | The rect to photograph, in the marker's platform capture space (§1.2) — physical virtual-desktop px on Windows, CG points on macOS — and the same numbers. Rejected if W or H is 0. **Not** the unit the captured frames are in: `kCGWindowImageBestResolution` returns a Retina region at 2×, so the composite is in pixels while the region is in points. |
| `--point PX,PY` | yes | Where the cursor is parked and the wheel is aimed, same space. Re-clamped into `--region`; a point outside it is a caller bug and is logged. |
| `--no-rewind` | no | Start capturing from wherever the document is sitting instead of winding it back to the top first. Negative because rewinding is the default — the shell passes this only when the user turns "Scroll to top first" off, which is the deliberate "capture from here" intent. |
| `--hwnd N` | no (default 0) | Decimal window handle from the marker: **the window the user's selection snapped to**, not whatever is topmost at the scroll point. An `HWND` on Windows, a `CGWindowID` on macOS — one flag name for both, because it is the same field of the same marker. Re-validated at drive time (still a window, still covering the point); `0` or a stale handle falls back to whatever is topmost at the point. Whichever wins is then raised over the scroll point and *verified* there before the first frame — see §2.5. |

The shell must redirect **all three** stdio streams. Stdin in particular: the
driver reads a closed stdin as "the shell is gone" and quits (cancels), which
is what keeps a crashed Prisc.Ui from leaving something scrolling the user's
window.
On Windows the shell also calls `AllowSetForegroundWindow(driver pid)` right
after spawn — without it `SetForegroundWindow` in the driver is refused and the
run relies on Win10+ scroll-inactive-windows routing alone. macOS has no
equivalent grant to pass on.

### 2.2 Events (driver → shell)

The messages are the `scroll` catalog of HELPER_PROTOCOL.md §4.4. What they
mean:

- `ready`: the target is resolved and focused; scrolling is about to start.
- `status`: progress, at each phase change of every step, so up to three per
  step. `rewinding` appears only before the first frame and only when the
  rewind is enabled; its `frames` and `height_px` are both 0. `paused` means
  the user has the mouse (§2.5) and nothing advances until they put it down.
  `resuming` carries `resume_in_s`, the whole seconds left before the run
  takes the cursor back, and is the only state on which that field is present.
- `done`: the run ended. `session.json` is already on disk for every `result`
  but `cancelled`.

| `done.result` | Meaning | Session written |
|---|---|---|
| `complete` | Two consecutive steps produced no new content: the document ended. | yes |
| `stopped` | Esc, a `stop` command, the target window closing/moving, the stitcher giving up, or a pause the user never came back from (§2.5). The partial capture is kept. | yes |
| `max_reached` | A hard cap: 120 frames, 20,000 px of composite, or 120 s wall clock (the clock excludes time spent paused). | yes |
| `no_movement` | Nothing the driver could inject ever moved the target, most often an elevated Windows target whose UIPI eats `SendInput`, or a surface that ignores a synthetic wheel. Reported whatever else ended the run, and a single-screen session is still written. | yes |
| `cancelled` | The run was abandoned (`quit`, stdin EOF, a signal). `frames` and `height_px` are what was captured and discarded; the directory is empty for the shell to delete. | no |

A failure (missing permission, no window at the scroll point, the target
cannot be raised, the session cannot be written) is a fatal `error` and exit
1; arguments that parsed but cannot be used are `error{code:"usage"}` and
exit 2. There is no session.

### 2.3 Commands (shell → driver)

`{"type":"stop"}` finishes now and keeps everything captured so far (the
HUD's FINISH button), ending as `stopped`. `{"type":"quit"}` (or stdin EOF)
abandons the run and writes nothing, ending as `cancelled`. Both are polled
between steps, never inside one, so a command that lands mid-settle takes
effect up to a settle cycle (~800 ms) later. A malformed line is answered with
a non-fatal `bad_command` and otherwise ignored: garbage on the command
channel must not take down a capture that is going fine.

### 2.4 Output and exit

On any result but `cancelled` the driver writes, in this order: `desktop.png`
(the stitched composite), `cropped.png` (a byte copy of it — `cropped.png` is
what `SessionInfo.UploadSourcePath` shares from Recents, so it must not be a
downscaled preview), then **`session.json` strictly last**, per §1.2's
ordering invariant. `CroppedRect` is `0,0,W,H` and `OriginalBounds` is the
empty rect: a 20,000 px composite has no meaningful place on the virtual
desktop, and empty bounds make the editor center its window instead of trying
to open one taller than every monitor stacked. `Name` is `"Screenshot"` like
every other session; the shell renames it to "Scrolling Capture".

The process exits 0 after every `done` and 1 (2 for usage) after a fatal
`error`. Any other exit, or one without a terminal line, is a crash, and the
shell reports it with the stderr tail attached.

### 2.5 The target window and the user's mouse

Both halves of the run depend on the same fact: **whatever window is at the
scroll point gets the wheel and gets photographed.** Wheel input is routed by
cursor position on both platforms, and each frame is a screenshot of that
region of the screen.

So before frame 0 the driver raises the target over the scroll point and then
checks that it worked — the window at the point must be the target or another
window of the same application (an owned popup on Windows, a sheet or panel on
macOS). Failing the check is a fatal `error`, not a warning: a run against a
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
back to `paused`. `stop`, `quit`, Esc and
the target window going away are all honored while paused. Paused time is
excluded from the wall-clock cap. One pause may last 60 s — reachable only by
a cursor that keeps *moving* that whole time — after which the run finalizes
as `stopped` with everything captured so far.

## 3. Text recognizer (`prisc_ai ocr`)

The only protocol here that the shell is not a party to. When the user presses
OCR, the overlay extracts the selected region's pixels — compositing a
click-locked peek if one is up, so what is recognized is what the user can
actually see — spawns `prisc_ai ocr`, and waits for it on the detached `ocr`
worker thread. **One request, one process, one answer.**

It is a **separate binary** because recognition runs on a large native engine
(PaddleOCR models on ONNX Runtime, via the `ort` crate). A Rust panic in it
would unwind harmlessly, but an `abort`, a segfault or a refused allocation on
a degenerate selection kills the process it is running in — which in-process
meant the overlay, mid-capture, with the user's selection already framed.
Out-of-process the same failure is an exit code the capturer turns into an
"OCR failed" pill. It is also the license boundary: `prisc_ai` embeds GPL-3.0
matting weights and is GPL-3.0 itself, which the process boundary keeps out of
the MIT overlay.

Two things follow from that split, both improvements rather than costs:
canceling (BACK) is killing a process rather than polling a flag between
inference batches, so a superseded request can no longer hold the engine while
the next one queues behind it; and the tens of MB of embedded models plus the
static runtime left the overlay, which is spawned fresh for every capture and
therefore pays for its own size in start-up latency.

It ships beside `prisc_capture` on every platform that has an ONNX
Runtime build — Windows x64/arm64 and Apple Silicon, **not** Intel macOS,
where the spawn fails and the overlay shows OCR as unavailable — which is
where `src/ocr/client.rs` looks for it, through `prisc_ipc::locate::sibling`
(each directory of `PRISC_HELPERS_DIR` first, for tests and local development,
then beside `current_exe()`). It needs no window, no event loop, no GPU, and — because it is
handed pixels rather than taking them — no screen-recording permission.

### 3.1 Spawn

```
prisc_ai ocr --out <path> [--log-file <path>]
```

| Flag | Required | Meaning |
|---|---|---|
| `--out` | yes | Where to write the response (§3.3). The capturer puts this in the capture's session directory as `ocr.json` when there is one, and in the temp directory otherwise — OCR has no `--session-dir` requirement, since COPY and SEARCH need no shell round-trip. It reads the file only after the process exits 0, and deletes it either way. |
| `--log-file` | no | Mirrors the log (det/rec timings, the tier choice) to a file. The capturer passes `<session-dir>/ocr.log` when it has a session; that file is deliberately *not* cleaned up, because it is the artifact a "why was OCR slow" report is diagnosed from. |

Stdio, all three of which the capturer sets deliberately:

| Stream | Setting | Why |
|---|---|---|
| stdin | pipe | Carries the request (§3.2). |
| stdout | **null** | The `ocr` subcommand writes nothing to stdout — the answer is the `--out` file, which doubles as the session's `ocr.json` artifact, and a native runtime that printed to stdout (the original MNN engine did) could never corrupt a file. The overlay's own stdout is its helper protocol channel, so the child's is nulled rather than inherited; a pipe would work but one nobody drains can eventually block the child. |
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
