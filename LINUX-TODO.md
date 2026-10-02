# Linux bring-up — status and remaining work

Working notes for getting Clowd running and testable on Linux. Written 2026-10-03 on Windows;
nothing here has been run on a Linux machine yet.

## Where things stand

- **Rust capture side** (`clowd_capture`, `clowd_capture_wayland`) — committed in `47030694`
  and follow-ups. X11: the capture overlay runs as on Windows/macOS, including VIDEO (re-enabled
  Oct 3). Peek, scroll, share, copy, OCR search and reverse image search are forced off
  (`clowd_capture/src/settings.rs` `apply_platform_limits`, `panel/model.rs`). Wayland: the
  overlay is not used; `clowd_capture_wayland --session-dir <dir>` uses the Screenshot portal.
  Its host contract is in the module docs of `clowd_capture_wayland/src/main.rs`.
- **Recorder** — obs-express Linux support is draft PR clowd/obs-express-rs#11
  (`caesay/linux-support`). X11: xshm with `--region`/`--monitor` as today. Wayland:
  `--region`/`--monitor` are rejected and obs-express opens the ScreenCast portal picker itself.
  `--input-capture`, `--window-capture` and `--tracker` are rejected on Linux; hardware encoding
  falls back to x264; `clowd-share-region` is a stub.
- **C# host side** (`clowd_ui`) — implemented on master but **uncommitted** (19 modified files
  plus `Clowd.Shared/ClowdPlatform.cs`, `Clowd.Shared.Tests/ClowdPlatformTests.cs`,
  `Clowd.VideoSDK.Tests/WaylandCaptureTests.cs`). Builds clean on Windows; Clowd.Shared.Tests and
  the related Clowd.VideoSDK.Tests pass. Summary:
  - `ClowdPlatform` holds every Linux/X11/Wayland decision (`IsWayland` from `WAYLAND_DISPLAY` /
    `XDG_SESSION_TYPE`, read once). Windows/macOS answers are unchanged.
  - Wayland screenshot: `ScreenCapturePage.Open` spawns `clowd_capture_wayland` (no timeout).
    Exit 0 + `session.json` opens the editor (with `OriginalBounds` cleared so it centres);
    exit 0 without it is a silent cancel; exit 4 shows an error dialog (no Sentry).
  - Region-less recording: `IVideoCapturePage.OpenWithoutRegion()`. No `BorderWindow`, toolbar
    placed on the primary monitor, 180 s init timeout while the picker is up. A user cancel in
    the picker closes quietly (matched from obs-express stderr: "screen-share dialog was
    cancelled or failed" + "denied or cancelled by user"); a PipeWire error shows the normal
    error.
  - Linux-safe obs-express args via `ObsRecorderFeatures` (no sidecars, capture method or
    tracker; Studio recordings bake the cursor in).
  - No global hotkey host on Wayland; no warm-capturer supervisor on Linux (`--standby` exits 4
    there); share region off on Linux; no shortcut text in the tray when hotkeys are off.
  - Wayland home page shows **Start screenshot** + **Start recording** instead of **Start
    Capture**; the Hotkeys nav item is removed on Wayland; Shared Region nav removed on Linux.
  - `[HiddenOnLinux]` / `[HiddenOnWayland]` settings attributes; Windows-only rows hidden.

## Before the first test run

1. **Enable overlay video on X11 in C#.** `clowd_ui/Clowd.Shared/ClowdPlatform.cs`:
   `OverlayPicksRecordingRegion => !IsLinux` must become `!IsWayland` (update its doc comment and
   `ClowdPlatformTests`). Everything keys off it: overlay VIDEO action routing
   (`ScreenCaptureService.cs` ~234/720), and the home-page Start recording button
   (`MainWindow.axaml.cs` ~192) hides itself again on X11.
2. **BorderWindow click-through on X11.** `Video/BorderWindow.axaml.cs` covers the whole recorded
   region, and `WindowNativeExtensions.AddExStyles` / `AddHitTestTransparentHook` /
   `SetIgnoresMouseEvents` are no-ops on Linux — on X11 the recorded area cannot be clicked.
   Add a Linux branch in `Helpers/WindowNativeExtensions.cs`: empty input region via
   `XShapeCombineRectangles(display, xid, ShapeInput, 0, 0, null, 0, ShapeSet, Unsorted)`
   (libXext), XID from `TryGetPlatformHandle().Handle`; opening a separate `XOpenDisplay`
   connection is fine since shapes are server-side. Same file, while there:
   - `AddExStyles(TOOLWINDOW|NOACTIVATE)`: set `_NET_WM_WINDOW_TYPE_UTILITY`,
     `_NET_WM_STATE_SKIP_TASKBAR|SKIP_PAGER`, `WM_HINTS.input = False` (floating tray,
     share/resize windows, recording toolbar otherwise steal focus and appear in alt-tab).
   - `RaiseTopmostNoActivate`: `_NET_WM_STATE_ABOVE` client message + `XRaiseWindow`.
   - `SetPhysicalBounds`: `XMoveResizeWindow` for an atomic move+resize (fallback jitters).
   - `ExcludeFromScreenCapture` has no X11 equivalent; the border relies on its outward
     inflation alone. Verify no border pixels land in X11 recordings.
3. **Linux workspace build.** `clowd_scroll_driver/src/main.rs:43` is a `compile_error!` on
   Linux, and `build.sh` runs `cargo build --release` over the whole workspace and then copies
   `clowd_capture`, `clowd_scroll_driver`, `clowd_ai` unconditionally. Exclude/stub the scroll
   driver on Linux and add `clowd_capture_wayland` to the copy list.
4. **Packaging / CI.** No Linux leg in `.github/workflows/ci.yml` or `release.yml`.
   - `dotnet publish` Clowd.Ui for `linux-x64` (and `linux-arm64` — obs-express builds it now).
     Multi-file publish (`vpk pack` needs it; `build.sh` currently uses `PublishSingleFile`).
   - Beside `Clowd.Ui`: `clowd_capture`, `clowd_capture_wayland`, `clowd_ai`, and the
     obs-express Linux tarball unpacked to `obs-express/` (layout `ObsBinaryLocator` already
     probes, incl. `libavcodec.so.<major>`). Preserve file modes (zip loses them;
     `HelperBinary.EnsureExecutable` repairs some but not all spawns).
   - `vpk pack` to AppImage: `--mainExe Clowd.Ui`, PNG icon, a `.desktop` file with
     `StartupWMClass`; add a `linux-x64` update channel for `UpdateService`.
   - `.cargo/config.toml` has target-cpu entries for Windows/macOS only.
5. **FFmpeg inside Clowd.Ui.** `Clowd.VideoSDK/FFmpegLoader.cs` has Windows (`SetDllDirectory`)
   and macOS (dylib preloader) branches but nothing for Linux. FFmpeg.AutoGen opens
   `libavcodec.so.61` etc. by absolute path, but their dependencies resolve via RUNPATH. Check
   `readelf -d obs-express/libavformat.so.61 | grep -i path` shows `$ORIGIN`; if not, add a Linux
   preloader (`NativeLibrary.Load` in dependency order). Affects the editor, video posters and
   thumbnails.
6. **Skia for Clowd.VideoRender.** Neither `Clowd.VideoSDK.csproj` nor `Clowd.VideoRender.csproj`
   references `SkiaSharp.NativeAssets.Linux`; VideoRender only works if published into the same
   folder as Clowd.Ui (which gets `libSkiaSharp.so` via Avalonia). Add the reference at
   `$(SkiaSharpVersion)`.
7. **Test machine runtime deps:** glibc 2.38+ (Ubuntu 24.04+, required by the obs bundle),
   `libicu`, `fontconfig`, `libxtst6` / `libxkbcommon-x11-0` (SharpHook), PipeWire or PulseAudio,
   `xdg-desktop-portal` plus the desktop's backend (`-gnome`, `-kde`, `-wlr`), `xdg-utils`.

## Likely to bite during early testing

- **No tray on stock GNOME.** Avalonia's tray is StatusNotifierItem; GNOME needs the
  AppIndicator extension. The app uses `ShutdownMode.OnExplicitShutdown` and the tray is the only
  Quit — add a Quit to the main window or exit on close when no SNI watcher exists.
- **Tray icon colour.** `AppStyles` uses the white macOS menu-bar glyph on every non-Windows
  platform (~line 65); invisible on light panels.
- **PrintScreen on X11.** libuiohook cannot suppress events on Linux (`GlobalHotkeyHost.cs`
  `SuppressEvent`), so PrtSc also opens the desktop's screenshot tool. Pick another default
  hotkey on Linux.
- **Clipboard while tray-only.** The non-Windows clipboard path needs a live TopLevel
  (`App.GetPrimaryClipboard()`), so "copy link after upload" does nothing with no window open.
- **Single-instance pipe.** `PathConstants.ClowdNamedPipe` becomes a socket in `$TMPDIR`, which
  is the shared `/tmp` on Linux — multiple users collide. Add the uid or use `$XDG_RUNTIME_DIR`.
- **Audio.** `AudioDeviceManager` lists only "Default device" (obs-express takes PulseAudio
  source names and sink `.monitor` names but cannot list them — use `pactl`/libpulse);
  `MicCapture` unsupported (voice-over off); editor preview silent (`AudioOutputFactory` →
  `SilentAudioOutput`).
- **Fonts.** "Segoe UI" (`SettingsEditor.cs:29`, `GraphicText.cs:58`, `GraphicMeasure.cs:21`,
  `FontDialog.axaml.cs:21`, `InspectorPanel.axaml.cs:327`), "Tahoma" (`DrawingCanvas.cs:58`), bare
  `Consolas` in `ColorDialog.axaml` / `MiniColorDialog.axaml`. Use bundled Inter / Cascadia Code.
- **`AppLaunchPath`** points inside the AppImage mount; use `$APPIMAGE` once autostart or
  file-manager actions exist.
- **`ReverseImageSearch`** writes HTML to `/tmp`, invisible to snap browsers (overlay has image
  search off on Linux, but the editor path may still reach it).
- **Tests.** Some `RecorderPlumbingTests` read `ObsRecorderFeatures.Current`; on a Linux test
  job they need `ObsRecorderFeatures.All` passed explicitly.

## Open decisions (current defaults)

- Picker cancel is detected from obs-express stderr text. A dedicated exit code or a stdout
  `picker_cancelled` event would be more robust.
- Wayland keeps the `--pause` + PRESS START flow; auto-start after the pick is a one-line change
  in `VideoCapturePage.InitializeCapturerAsync`.
- Recorder respawns during WAIT (mode/container change, rejected webcam) reopen the portal picker.
- Studio recordings on Linux bake the cursor in (no input-capture sidecar).
- Wayland screenshots clear `OriginalBounds` and rewrite `session.json`; alternatively the Rust
  side could omit it for portal captures.
- `CAPTURE_PROTOCOL.md`'s Linux section is stale (describes evdev hotkeys).

## Verification checklist

X11 session:
- [ ] Start Capture → overlay → screenshot → editor opens; upload, save.
- [ ] Overlay VIDEO → region recording; border visible, region clickable; stop → editor.
- [ ] Border not in the recording; toolbar doesn't steal focus or show in alt-tab.
- [ ] Global hotkeys fire (non-PrtSc gesture); tray menu works (KDE, GNOME + AppIndicator).
- [ ] Editor plays video (FFmpeg loads), thumbnails/posters render, render/export works.
- [ ] Webcam listed (`obs-express --list-cameras` → `/dev/videoN`) and recorded; mic/speaker audio.

Wayland session (GNOME and KDE):
- [ ] Home page shows Start screenshot / Start recording; no Hotkeys nav.
- [ ] Start screenshot → portal UI → editor; cancel → nothing; no portal backend → error dialog.
- [ ] Start recording → portal picker → PRESS START → record → stop → editor; picker cancel →
      session closes quietly.
- [ ] No overlay, standby capturer or hotkey hook process is ever started.
