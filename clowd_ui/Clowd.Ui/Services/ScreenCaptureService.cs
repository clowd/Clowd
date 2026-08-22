using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>
    /// Locates the external Rust capture binaries (see CAPTURE_PROTOCOL.md): the capture overlay
    /// <c>clowd_capture_wgpu</c> and, beside it, the scrolling-capture driver
    /// <c>clowd_scroll_driver</c>.
    /// Probe order: the <c>CLOWD_CAPTURE_PATH</c> environment variable, then alongside the Clowd.Ui
    /// executable (release layout), then walking up from the app base directory to a cargo workspace
    /// root and probing <c>target/debug</c> followed by <c>target/release</c> (debug-time layout).
    /// </summary>
    public static class CaptureBinaryLocator
    {
        public const string EnvVarName = "CLOWD_CAPTURE_PATH";

        public static string BinaryFileName => Executable("clowd_capture_wgpu");

        /// <summary>The scrolling-capture driver (CAPTURE_PROTOCOL.md §2) — a separate process
        /// from the overlay, spawned once the user has picked the region and the scroll point.
        /// Windows-only: the overlay's SCROLL button is compiled out elsewhere.</summary>
        public static string ScrollDriverFileName => Executable("clowd_scroll_driver");

        private static string Executable(string stem) =>
            OperatingSystem.IsWindows() ? stem + ".exe" : stem;

        public static string Resolve() =>
            Resolve(Environment.GetEnvironmentVariable(EnvVarName), AppContext.BaseDirectory);

        /// <summary>
        /// Locates the scrolling-capture driver, which ships in the same directory as the overlay
        /// in every layout — next to Clowd.Ui when installed, and in the same <c>target/</c>
        /// profile directory when built locally. Deriving it from the overlay's resolved path
        /// rather than probing again means the <c>CLOWD_CAPTURE_PATH</c> override keeps pointing
        /// both binaries at the same build. Returns null when either is missing.
        /// </summary>
        public static string ResolveScrollDriver() => ResolveScrollDriver(Resolve());

        /// <summary>Testable overload. <paramref name="capturePath"/> is the overlay binary's
        /// resolved path (null when it could not be found).</summary>
        public static string ResolveScrollDriver(string capturePath)
        {
            var dir = String.IsNullOrEmpty(capturePath) ? null : Path.GetDirectoryName(capturePath);
            if (String.IsNullOrEmpty(dir))
                return null;

            var driver = Path.Combine(dir, ScrollDriverFileName);
            return File.Exists(driver) ? Path.GetFullPath(driver) : null;
        }

        /// <summary>Testable overload. Returns null when the binary cannot be found.</summary>
        public static string Resolve(string envVarValue, string baseDirectory)
        {
            // (a) explicit override via environment variable
            if (!String.IsNullOrWhiteSpace(envVarValue) && File.Exists(envVarValue))
                return Path.GetFullPath(envVarValue);

            // (b) next to the Clowd.Ui executable (release layout copies the binary alongside)
            var local = Path.Combine(baseDirectory, BinaryFileName);
            if (File.Exists(local))
                return Path.GetFullPath(local);

            // (c) walk up to a directory containing Cargo.toml (the cargo workspace root) and
            // probe target/debug, then target/release. Keep walking if a Cargo.toml has no
            // built binary (a crate manifest may sit below the workspace root that owns target/).
            var dir = new DirectoryInfo(Path.GetFullPath(baseDirectory));
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Cargo.toml")))
                {
                    var debug = Path.Combine(dir.FullName, "target", "debug", BinaryFileName);
                    if (File.Exists(debug))
                        return debug;

                    var release = Path.Combine(dir.FullName, "target", "release", BinaryFileName);
                    if (File.Exists(release))
                        return release;
                }

                dir = dir.Parent;
            }

            return null;
        }
    }

    /// <summary>
    /// <see cref="IScreenCapturePage"/> backed by the external Rust capture process
    /// (CAPTURE_PROTOCOL.md). Mirrors the WPF CaptureWindow callback dispatch: a completed
    /// capture (session.json present) loads the session, names it "Screenshot" and opens the
    /// editor or the upload flow depending on the action.txt marker; a SELECT-COLOR capture
    /// (action.txt only) opens the color viewer; an OCR-UPLOAD capture (an "ocr-upload" marker
    /// plus an ocr.txt sidecar, no image) uploads the recognized text as a paste; a cancelled
    /// capture (neither file) deletes the pre-created session directory. COPY/SAVE are handled
    /// inside the capturer itself and never produce a session.
    /// </summary>
    internal sealed class ScreenCapturePage : IScreenCapturePage
    {
        public event EventHandler Closed;

        // one capture process at a time, across all page instances.
        private static int _captureActive;

        /// <summary>True while the capture overlay is on screen — a hard block on applying a
        /// background update (IdleMonitor).</summary>
        public static bool IsCaptureActive => Volatile.Read(ref _captureActive) != 0;

        /// <summary>The capturer's exit code for "macOS has not granted Screen Recording" — see
        /// <c>NO_SCREEN_PERMISSION</c> in clowd_rust_core/src/exit.rs. Reported instead of
        /// crashing, so a revoked permission gets the permission dialog rather than a stack trace.
        /// </summary>
        private const int ExitCodeNoScreenPermission = 3;

        public async void Open(CaptureMode mode, bool video = false)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => Open(mode, video));
                return;
            }

            if (Interlocked.CompareExchange(ref _captureActive, 1, 0) != 0)
            {
                Debug.WriteLine("Screen capture is already in progress; ignoring re-entrant request.");
                return;
            }

            IdleMonitor.NotifyCaptureActivity();

            try
            {
                // The capturer must never be launched into a desktop it cannot see: without Screen
                // Recording it would put an overlay over a blank screenshot. Ask here, where there is
                // a real UI to explain it, instead of letting the capture process own the
                // conversation (issue #49).
                if (!await EnsureScreenRecordingPermissionAsync())
                    return;

                var binary = CaptureBinaryLocator.Resolve();
                if (binary == null)
                {
                    await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error,
                        $"The screen capture binary ({CaptureBinaryLocator.BinaryFileName}) could not be found. " +
                        $"Run 'cargo build' in the clowd-rust repository, or set the {CaptureBinaryLocator.EnvVarName} " +
                        "environment variable to its location.",
                        "Screen capture unavailable");
                    return;
                }

                Debug.WriteLine("Resolved capture binary: " + binary);

                var sessionDir = SessionManager.Current.GetNextSessionDirectory();
                Directory.CreateDirectory(sessionDir);

                var psi = new ProcessStartInfo(binary)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // capture the capturer's log output (simplelog writes errors and any
                    // Rust panic to stderr) so a crash can be reported to the user.
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(binary),
                };
                foreach (var arg in CaptureArguments.Build(sessionDir, SettingsRoot.Current.Capture, mode, video))
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process == null)
                    throw new InvalidOperationException("Failed to start capture process: " + binary);

                // Hand our foreground rights to the capturer: a freshly spawned process is
                // denied SetForegroundWindow by the foreground lock, and the overlay needs
                // keyboard focus immediately (Esc / shortcuts). Best-effort — it only works
                // when we hold foreground permission ourselves (hotkey / tray interaction).
                if (OperatingSystem.IsWindows())
                    AllowSetForegroundWindow(process.Id);

                // drain stderr concurrently so a full pipe buffer can never block the
                // process from exiting (classic redirect deadlock).
                var stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var stderr = await stderrTask;

                // Every normal outcome (edit / upload / colour / video / cancel) exits 0.
                // A non-zero exit means the capturer crashed or aborted — e.g. a GPU shader
                // that failed to load — so surface it instead of silently treating the empty
                // session directory as a user cancellation (which produced no error at all).
                if (process.ExitCode != 0)
                {
                    Debug.WriteLine($"Capture process exited with code {process.ExitCode}:\n{stderr}");

                    // the capturer mirrors its log into the session dir (stdout is lost when
                    // launched from an .app bundle); grab it before the dir is deleted.
                    var captureLog = TryReadCaptureLog(sessionDir);
                    CaptureSessionDispatcher.DeleteSessionDir(sessionDir);

                    // permission revoked between our preflight and the capturer's own check, or the
                    // capturer's TCC verdict differs from ours — either way it is not a crash.
                    if (process.ExitCode == ExitCodeNoScreenPermission)
                    {
                        await PromptForScreenRecordingPermissionAsync();
                        return;
                    }

                    // The capturer reports its own panics, but it cannot report the ways it dies
                    // without running Rust code — a native fault in a GPU driver, an abort out of
                    // an FFI frame, or a kill. Its exit code and log are all that survives
                    // those, so report from this side. Keep the message free of the exit code so
                    // Sentry groups every capturer death into one issue; the specifics ride along
                    // in Data.
                    var crash = new InvalidOperationException("Capture process exited unexpectedly");
                    crash.Data["exit_code"] = process.ExitCode;
                    // Both logs in full, as the process-log.txt attachment. A death of that kind
                    // writes nothing to stderr, and the session log is the only place the adapter
                    // it selected is named — truncating either to fit inline in Data is what
                    // leaves these reports unactionable.
                    SentryConfig.AttachProcessLog(crash, ("stderr", stderr), ("capture.log", captureLog));
                    SentryConfig.CaptureHandled(crash, "capture.process-crash");

                    await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error,
                        $"The screen capture tool exited unexpectedly (code {process.ExitCode})." +
                        $"\n\n{SummarizeDiagnostics(stderr, captureLog)}",
                        "Screen capture failed");
                    return;
                }

                var result = CaptureSessionDispatcher.ProcessFinishedSession(sessionDir);
                switch (result?.Action)
                {
                    case CaptureAction.Edit:
                        EditorWindow.ShowSession(result.Session);
                        break;
                    case CaptureAction.Upload:
                        await UploadManager.UploadSession(result.Session);
                        break;
                    case CaptureAction.SelectColor:
                        NiceDialog.ShowColorViewer(result.Color);
                        break;
                    case CaptureAction.Video:
                        // the capture-active interlock is released in `finally` before the video
                        // page runs its own lifetime — correct: the screenshot overlay is done and
                        // VideoCapturePage has its own single-instance guard.
                        PageManager.Current.GetVideoCapturePage().Open(result.Region, result.SessionDir);
                        break;
                    case CaptureAction.Scroll:
                        // same hand-off as Video: the overlay is gone, and the scroll page runs its
                        // own (much longer) lifetime around the session dir it now owns.
                        PageManager.Current.GetScrollCapturePage()
                            .Open(result.Region, result.ScrollPoint, result.TargetHwnd, result.SessionDir);
                        break;
                    case CaptureAction.OcrUpload:
                        // the recognized text travels in the result, not in a session — the
                        // capturer's session dir is already gone by now. UploadText creates its
                        // own session, exactly as the clipboard-upload hotkey does.
                        await UploadManager.UploadText(result.Text, "Captured Text");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Screen capture failed: " + ex);
                SentryConfig.CaptureHandled(ex, "capture.screen");
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, ex.Message,
                    "An error occurred while capturing the screen");
            }
            finally
            {
                Interlocked.Exchange(ref _captureActive, 0);

                // re-stamp on the way out so the quiet period is measured from when the overlay
                // disappeared, not from when it was opened.
                IdleMonitor.NotifyCaptureActivity();
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Close()
        {
            // the capture process owns its own lifetime (Escape / X closes it); nothing to do here.
            Closed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Returns whether the capture may go ahead. The first capture on a fresh install is where
        /// macOS still has its own one-tap prompt to offer, so try that before anything else; every
        /// later attempt falls through to the dialog, since the only route left by then is System
        /// Settings plus a restart. True on every platform without such a permission.
        /// </summary>
        private static async Task<bool> EnsureScreenRecordingPermissionAsync()
        {
            if (MacPermissions.HasScreenRecording || MacPermissions.Request(MacPermission.ScreenRecording))
                return true;

            await PromptForScreenRecordingPermissionAsync();
            return false;
        }

        /// <summary>The "you need to grant this in System Settings" dialog, offering to open the
        /// pane. Shared by the pre-launch gate and the capturer's own permission exit code.</summary>
        private static async Task PromptForScreenRecordingPermissionAsync()
        {
            var openSettings = await NiceDialog.ShowDialogAsync(null, NiceDialogIcon.Warning,
                "Clowd needs Screen Recording permission to capture your screen.\n\n"
                + "Enable Clowd under Privacy & Security → Screen & System Audio Recording, then "
                + "restart Clowd.",
                "Screen Recording permission required", "Open System Settings", "Cancel");

            if (openSettings)
                MacPermissions.OpenSettings(MacPermission.ScreenRecording);
        }

        /// <summary>Reads the capturer's session-dir log file, if it wrote one. Null when absent
        /// or unreadable — older capturers and pre-logger crashes leave no file.</summary>
        private static string TryReadCaptureLog(string sessionDir)
        {
            try
            {
                var logPath = Path.Combine(sessionDir, "capture.log");
                return File.Exists(logPath) ? File.ReadAllText(logPath) : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to read capture log: " + ex);
                return null;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        /// <summary>Picks what to put in front of the user: the capturer's stderr when it managed to
        /// say something, and otherwise the log file it left behind. A process that dies without
        /// running Rust code — a native fault in a GPU driver, a kill — writes nothing to stderr, and
        /// "No diagnostic output was captured." is a dead end when the log file was there all
        /// along.</summary>
        private static string SummarizeDiagnostics(string stderr, string log)
            => String.IsNullOrWhiteSpace(stderr) ? SummarizeTail(log) : SummarizeTail(stderr);

        /// <summary>Condenses the capturer's captured stderr (or log file) into the tail few
        /// lines for an error dialog — the failure (a shader error, a panic) is always at the
        /// end, after the routine startup log lines.</summary>
        private static string SummarizeTail(string stderr)
        {
            if (String.IsNullOrWhiteSpace(stderr))
                return "No diagnostic output was captured.";

            var lines = new List<string>();
            foreach (var raw in stderr.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length > 0)
                    lines.Add(line);
            }

            const int maxLines = 15;
            var start = Math.Max(0, lines.Count - maxLines);
            return String.Join("\n", lines.GetRange(start, lines.Count - start));
        }
    }

    /// <summary>
    /// Maps Clowd.Ui state onto the capturer's clap CLI (CAPTURE_PROTOCOL.md / `clowd_capture_wgpu
    /// --help`). Flags are only emitted when they differ from the capturer's own defaults, so the
    /// command line stays minimal. Factored out of the page so it is testable without a process.
    /// </summary>
    public static class CaptureArguments
    {
        public static IReadOnlyList<string> Build(string sessionDir, SettingsCapture settings, CaptureMode mode,
                                                  bool video = false)
        {
            // the overlay accent follows the OS (or the user's pick) and is contrast-corrected for
            // the white text drawn on it — see SettingsCapture.GetEffectiveAccentColor, issue #48.
            var accent = settings.GetEffectiveAccentColor();

            var args = new List<string>
            {
                "--session-dir", sessionDir,
                "--accent-color", $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}",
                // So the overlay can hand its foreground rights back to us as the cycle ends — we
                // need them to raise whatever the capture opens next, and cannot grant what we no
                // longer hold (CAPTURE_PROTOCOL.md §2.5). Always emitted: unlike the flags below
                // there is no capturer-side default that could stand in for it.
                "--shell-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            };

            // Region is the capturer's default (free selection) and is left implicit;
            // Screen / Window pre-select the active monitor / foreground window.
            if (mode != CaptureMode.Region)
            {
                args.Add("--capture-mode");
                args.Add(mode.ToString().ToLowerInvariant());
            }

            if (settings.TipsMode != CapturerTipsMode.Hints)
            {
                args.Add("--tips-mode");
                args.Add(settings.TipsMode.ToString().ToLowerInvariant());
            }

            if (!settings.ObscuredWindowPeek)
                args.Add("--no-peek");

            var threshold = Math.Clamp(settings.ObscuredWindowDetectionThreshold, 0.0, 1.0);
            if (Math.Abs(threshold - 0.80) > 0.0001)
            {
                args.Add("--peek-threshold");
                args.Add(threshold.ToString("0.###", CultureInfo.InvariantCulture));
            }

            if (!settings.ScreenshotWithCursor)
                args.Add("--no-cursor");

            // The overlay's optional buttons (SettingsCapture "Optional features"). All on by
            // default, so these only ever appear when the user has switched something off.
            if (!settings.UploadButtonEnabled)
                args.Add("--no-upload");

            if (!settings.ScrollingCaptureEnabled)
                args.Add("--no-scroll-capture");

            if (!settings.OcrEnabled)
                args.Add("--no-ocr");

            if (settings.MemoryHints == CapturerMemoryHints.LowerMemoryUsage)
            {
                args.Add("--memory-hints");
                args.Add("lower-memory-usage");
            }

            // the overlay was launched specifically to pick a recording region: a confirmed
            // selection immediately dispatches the video action (DESIGN §3.1).
            if (video)
                args.Add("--video");

            return args;
        }
    }

    /// <summary>What the shell should do with a finished capture (the capturer's counterpart
    /// of the legacy CaptureType callback argument).</summary>
    public enum CaptureAction
    {
        Edit,
        Upload,
        SelectColor,
        Video,
        Scroll,
        OcrUpload,
    }

    /// <summary>A finished, non-cancelled capture. <see cref="Session"/> is set for
    /// Edit/Upload; <see cref="Color"/> for SelectColor; <see cref="Region"/> and
    /// <see cref="SessionDir"/> for Video and Scroll, which additionally carry
    /// <see cref="ScrollPoint"/> and <see cref="TargetHwnd"/>; <see cref="Text"/> for
    /// OcrUpload, which carries no session at all.</summary>
    public sealed class CaptureResult
    {
        public CaptureAction Action { get; init; }
        public SessionInfo Session { get; init; }
        public Color? Color { get; init; }
        public ScreenRect Region { get; init; }
        public string SessionDir { get; init; }

        /// <summary>The recognized text read out of the session dir's ocr.txt sidecar before that
        /// directory was deleted (OcrUpload only) — the whole payload of an OCR upload.</summary>
        public string Text { get; init; }

        /// <summary>Where the wheel events will be aimed, in the same physical virtual-desktop
        /// space as <see cref="Region"/> (Scroll only).</summary>
        public ScreenPoint ScrollPoint { get; init; }

        /// <summary>Top-level window under <see cref="ScrollPoint"/>, or 0 when the overlay could
        /// not resolve one — the driver falls back to WindowFromPoint (Scroll only).</summary>
        public long TargetHwnd { get; init; }
    }

    /// <summary>
    /// Post-exit handling for a capture session directory, factored out of the page so it is
    /// testable without spawning the capture process.
    /// </summary>
    public static class CaptureSessionDispatcher
    {
        /// <summary>
        /// Inspects <paramref name="sessionDir"/> after the capture process has exited.
        /// If session.json exists the capture succeeded: the session is loaded (and registered
        /// with <see cref="SessionManager"/>), renamed to "Screenshot" and returned with the
        /// action from the action.txt marker (missing marker = Edit). An action.txt of
        /// "select-color #RRGGBB" without a session carries just the picked color; the
        /// directory is deleted. An "ocr-upload" marker carries its text in an ocr.txt sidecar,
        /// which is read out before the directory is deleted. Otherwise the capture was
        /// cancelled: the pre-created directory is deleted and null is returned.
        /// </summary>
        public static CaptureResult ProcessFinishedSession(string sessionDir)
        {
            string action = null;
            try
            {
                var actionPath = Path.Combine(sessionDir, "action.txt");
                if (File.Exists(actionPath))
                    action = File.ReadAllText(actionPath).Trim();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to read capture action file: " + ex);
                SentryConfig.CaptureHandled(ex, "capture.read-action-file");
            }

            var jsonPath = Path.Combine(sessionDir, "session.json");
            if (File.Exists(jsonPath))
            {
                var session = SessionManager.Current.GetSessionFromPath(jsonPath);
                if (session != null)
                {
                    // the legacy C# shell named sessions after the capture callback fired.
                    session.Name = "Screenshot";
                    var kind = String.Equals(action, "upload", StringComparison.OrdinalIgnoreCase)
                        ? CaptureAction.Upload
                        : CaptureAction.Edit;
                    return new CaptureResult { Action = kind, Session = session };
                }

                Debug.WriteLine("session.json exists but the session could not be loaded: " + jsonPath);
                return null;
            }

            // a "video x,y,w,h" marker means the overlay confirmed a recording region: the dir
            // holds cropped.png (the poster frame, DESIGN §4.1) and must NOT be deleted — only
            // the consumed action.txt marker is removed (§4.3).
            const string videoPrefix = "video";
            if (action != null && action.StartsWith(videoPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var region = ParseRegion(action.Substring(videoPrefix.Length).Trim());
                if (region != null)
                {
                    try
                    {
                        File.Delete(Path.Combine(sessionDir, "action.txt"));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Failed to delete video action file: " + ex);
                        SentryConfig.CaptureHandled(ex, "capture.delete-action-file");
                    }

                    return new CaptureResult { Action = CaptureAction.Video, Region = region, SessionDir = sessionDir };
                }

                Debug.WriteLine("Unparseable video action: " + action);
                DeleteSessionDir(sessionDir);
                return null;
            }

            // a "scroll x,y,w,h px,py hwnd" marker means the overlay confirmed a scrolling capture
            // and exited. The directory is empty at this point — the scroll driver, not the
            // overlay, writes the session into it — so it must NOT be deleted; only the consumed
            // action.txt marker is removed. From here on ScrollCapturePage owns the directory.
            const string scrollPrefix = "scroll";
            if (action != null && action.StartsWith(scrollPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var scroll = ParseScrollAction(action.Substring(scrollPrefix.Length).Trim(), sessionDir);
                if (scroll != null)
                {
                    try
                    {
                        File.Delete(Path.Combine(sessionDir, "action.txt"));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Failed to delete scroll action file: " + ex);
                        SentryConfig.CaptureHandled(ex, "capture.delete-action-file");
                    }

                    return scroll;
                }

                Debug.WriteLine("Unparseable scroll action: " + action);
                DeleteSessionDir(sessionDir);
                return null;
            }

            // an "ocr-upload" marker means the overlay recognized text and wants it pasted. The
            // payload is not in the marker (it is multi-line, and markers are single-line
            // prefix-matched) but in an ocr.txt sidecar the capturer writes BEFORE action.txt, so
            // the marker's presence is proof the text is complete on disk. This branch must stay
            // above the fall-through DeleteSessionDir below — the sidecar has to be read before
            // the directory goes away.
            const string ocrPrefix = "ocr-upload";
            if (action != null && action.StartsWith(ocrPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string text = null;
                try
                {
                    text = File.ReadAllText(Path.Combine(sessionDir, "ocr.txt"));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to read OCR text: " + ex);
                    SentryConfig.CaptureHandled(ex, "capture.read-ocr-text");
                }

                // unlike Video/Scroll the directory is dead weight from here: there is no image and
                // UploadManager.UploadText mints its own session for the paste.
                DeleteSessionDir(sessionDir);

                if (!String.IsNullOrWhiteSpace(text))
                    return new CaptureResult { Action = CaptureAction.OcrUpload, Text = text };

                Debug.WriteLine("ocr-upload marker with no readable text");
                return null;
            }

            // no session payload — either a color pick or a cancelled capture; in both cases
            // the pre-created directory has nothing worth keeping.
            DeleteSessionDir(sessionDir);

            const string colorPrefix = "select-color";
            if (action != null && action.StartsWith(colorPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var hex = action.Substring(colorPrefix.Length).Trim();
                if (Color.TryParse(hex, out var color))
                    return new CaptureResult { Action = CaptureAction.SelectColor, Color = color };

                Debug.WriteLine("Unparseable select-color action: " + action);
            }

            return null;
        }

        /// <summary>Parses the "x,y,w,h" rect of a video or scroll action (invariant ints, x/y may
        /// be negative in virtual-desktop space). Returns null when unparseable.</summary>
        private static ScreenRect ParseRegion(string rect)
        {
            var parts = rect.Split(',');
            if (parts.Length != 4)
                return null;

            var nums = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (!Int32.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out nums[i]))
                    return null;
            }

            // obs-express requires W,H >= 2 (--region validation, exit 2 — DESIGN §1.1): reject
            // degenerate rects here so an out-of-contract selection is discarded like any other
            // unparseable action instead of surfacing as a process-exit error dialog. The overlay
            // also clamps at the source; this is defense-in-depth.
            if (nums[2] < 2 || nums[3] < 2)
                return null;

            return new ScreenRect(nums[0], nums[1], nums[2], nums[3]);
        }

        /// <summary>Parses the "x,y,w,h px,py hwnd" payload of a scroll action into a finished
        /// result (CAPTURE_PROTOCOL.md). Every field is required: a marker we cannot fully
        /// understand is a contract violation, not a capture worth driving. An unresolvable
        /// window is spelled as hwnd 0 by the overlay, not by omission. Returns null when
        /// unparseable.</summary>
        private static CaptureResult ParseScrollAction(string payload, string sessionDir)
        {
            var parts = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                return null;

            var region = ParseRegion(parts[0]);
            if (region == null)
                return null;

            var point = ParsePoint(parts[1]);
            if (point == null)
                return null;

            // isize on the Rust side, so signed and 64-bit wide — never parse an HWND as int.
            if (!Int64.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hwnd))
                return null;

            return new CaptureResult
            {
                Action = CaptureAction.Scroll,
                Region = region,
                ScrollPoint = point,
                TargetHwnd = hwnd,
                SessionDir = sessionDir,
            };
        }

        /// <summary>Parses an "x,y" point in the same virtual-desktop space as the region
        /// (invariant ints, either coordinate may be negative). Returns null when unparseable.</summary>
        private static ScreenPoint ParsePoint(string point)
        {
            var parts = point.Split(',');
            if (parts.Length != 2)
                return null;

            var nums = new int[2];
            for (int i = 0; i < 2; i++)
            {
                if (!Int32.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out nums[i]))
                    return null;
            }

            return new ScreenPoint(nums[0], nums[1]);
        }

        public static void DeleteSessionDir(string sessionDir)
        {
            try
            {
                if (Directory.Exists(sessionDir))
                    Directory.Delete(sessionDir, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to delete session directory: " + ex);
                SentryConfig.CaptureHandled(ex, "capture.delete-session-dir");
            }
        }
    }
}
