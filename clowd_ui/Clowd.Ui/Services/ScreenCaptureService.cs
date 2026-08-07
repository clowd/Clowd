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
    /// Locates the external Rust capture binary (<c>clowd_capture_wgpu</c>, see CAPTURE_PROTOCOL.md).
    /// Probe order: the <c>CLOWD_CAPTURE_PATH</c> environment variable, then alongside the Clowd.Ui
    /// executable (release layout), then walking up from the app base directory to a cargo workspace
    /// root and probing <c>target/debug</c> followed by <c>target/release</c> (debug-time layout).
    /// </summary>
    public static class CaptureBinaryLocator
    {
        public const string EnvVarName = "CLOWD_CAPTURE_PATH";

        public static string BinaryFileName =>
            OperatingSystem.IsWindows() ? "clowd_capture_wgpu.exe" : "clowd_capture_wgpu";

        public static string Resolve() =>
            Resolve(Environment.GetEnvironmentVariable(EnvVarName), AppContext.BaseDirectory);

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
    /// (action.txt only) opens the color viewer; a cancelled capture (neither file) deletes
    /// the pre-created session directory. COPY/SAVE are handled inside the capturer itself
    /// and never produce a session.
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
        /// <c>EXIT_NO_SCREEN_PERMISSION</c> in clowd_capture/src/system/mod.rs. Reported instead of
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

                // The warm host (if it is up) already holds an initialized GPU and hidden overlay
                // windows, so this is a few milliseconds instead of the 500-1000 ms a cold spawn
                // pays for wgpu init. Anything it cannot handle falls through to that cold spawn
                // below, with the same session directory.
                var host = CaptureProcessHost.Current;
                var warmHandled = false;
                if (host.IsReady)
                {
                    // Hand our foreground rights to the capturer, once per show: the grant is
                    // consumed by a single SetForegroundWindow, and it only works while we hold
                    // foreground permission ourselves — which is exactly what the hotkey / tray
                    // interaction that got us here gives us.
                    if (OperatingSystem.IsWindows())
                        AllowSetForegroundWindow(host.Pid);

                    try
                    {
                        warmHandled = await host.RunCaptureAsync(sessionDir, mode, video, SettingsRoot.Current.Capture);
                    }
                    catch (CaptureHostStoppedException)
                    {
                        // the host was taken down on purpose (app exit, an OS session ending, the
                        // feature switched off) with this capture in flight — cancel quietly, the
                        // same as an Escape out of the overlay. Not a crash to report.
                        Debug.WriteLine("Capture host was shut down while a capture was in flight; cancelling.");
                        CaptureSessionDispatcher.DeleteSessionDir(sessionDir);
                        return;
                    }
                    catch (CaptureHostCrashException crash)
                    {
                        // died with the overlay already on screen — the user has seen (and possibly
                        // used) a capture, so re-running it from cold would be worse than saying so.
                        await ReportWarmCaptureCrashAsync(sessionDir, crash);
                        return;
                    }
                }

                if (!warmHandled)
                {
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
                        // an FFI frame, or a kill. Its exit code and stderr are all that survives
                        // those, so report from this side. Keep the message free of the exit code so
                        // Sentry groups every capturer death into one issue; the specifics ride along
                        // in Data.
                        var crash = new InvalidOperationException("Capture process exited unexpectedly");
                        crash.Data["exit_code"] = process.ExitCode;
                        crash.Data["stderr"] = SummarizeTail(stderr);
                        if (captureLog != null)
                            crash.Data["capture_log"] = SummarizeTail(captureLog);
                        SentryConfig.CaptureHandled(crash, "capture.process-crash");

                        await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error,
                            $"The screen capture tool exited unexpectedly (code {process.ExitCode}).\n\n{SummarizeTail(stderr)}",
                            "Screen capture failed");
                        return;
                    }
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

        /// <summary>
        /// The warm capture host died mid-capture. Reported exactly like a non-zero one-shot exit
        /// (same Sentry issue, same dialog, same discarded session directory) — the difference is
        /// only where the diagnostics come from: the host's chatter buffer instead of a one-shot
        /// stderr, and its rolling capture-host.log instead of the per-session capture.log.
        /// </summary>
        private static async Task ReportWarmCaptureCrashAsync(string sessionDir, CaptureHostCrashException crash)
        {
            Debug.WriteLine($"Capture host died mid-capture (code {crash.ExitCode}):\n{crash.Log}");

            var chatter = SummarizeTail(crash.Log);
            var hostLog = CaptureProcessHost.TryReadHostLog();
            CaptureSessionDispatcher.DeleteSessionDir(sessionDir);

            var reported = new InvalidOperationException("Capture process exited unexpectedly");
            reported.Data["exit_code"] = crash.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            reported.Data["stderr"] = chatter;
            if (hostLog != null)
                reported.Data["capture_log"] = SummarizeTail(hostLog);
            SentryConfig.CaptureHandled(reported, "capture.process-crash");

            await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error,
                $"The screen capture tool exited unexpectedly.\n\n{chatter}",
                "Screen capture failed");
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

            if (settings.MemoryHints == CapturerMemoryHints.MaxPerformance)
            {
                args.Add("--memory-hints");
                args.Add("max-performance");
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
    }

    /// <summary>A finished, non-cancelled capture. <see cref="Session"/> is set for
    /// Edit/Upload; <see cref="Color"/> for SelectColor; <see cref="Region"/> and
    /// <see cref="SessionDir"/> for Video.</summary>
    public sealed class CaptureResult
    {
        public CaptureAction Action { get; init; }
        public SessionInfo Session { get; init; }
        public Color? Color { get; init; }
        public ScreenRect Region { get; init; }
        public string SessionDir { get; init; }
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
        /// directory is deleted. Otherwise the capture was cancelled: the pre-created
        /// directory is deleted and null is returned.
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

        /// <summary>Parses the "x,y,w,h" rect of a video action (invariant ints, x/y may be
        /// negative in virtual-desktop space). Returns null when unparseable.</summary>
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
