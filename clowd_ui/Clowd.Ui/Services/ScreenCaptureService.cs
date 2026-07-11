using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
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

        public async void Open(ScreenRect captureArea)
        {
            // the protocol has no region/fullscreen hint — the capturer always starts with
            // free region selection, so captureArea is intentionally ignored.
            if (!Dispatcher.UIThread.CheckAccess())
            {
                var area = captureArea;
                Dispatcher.UIThread.Post(() => Open(area));
                return;
            }

            if (Interlocked.CompareExchange(ref _captureActive, 1, 0) != 0)
            {
                Debug.WriteLine("Screen capture is already in progress; ignoring re-entrant request.");
                return;
            }

            try
            {
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
                    WorkingDirectory = Path.GetDirectoryName(binary),
                };
                foreach (var arg in CaptureArguments.Build(sessionDir, AppStyles.AccentColor, SettingsRoot.Current.Capture))
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process == null)
                    throw new InvalidOperationException("Failed to start capture process: " + binary);

                await process.WaitForExitAsync();

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
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Screen capture failed: " + ex);
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, ex.Message,
                    "An error occurred while capturing the screen");
            }
            finally
            {
                Interlocked.Exchange(ref _captureActive, 0);
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Close()
        {
            // the capture process owns its own lifetime (Escape / X closes it); nothing to do here.
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Maps Clowd.Ui state onto the capturer's clap CLI (CAPTURE_PROTOCOL.md / `clowd_capture_wgpu
    /// --help`). Flags are only emitted when they differ from the capturer's own defaults, so the
    /// command line stays minimal. Factored out of the page so it is testable without a process.
    /// </summary>
    public static class CaptureArguments
    {
        public static IReadOnlyList<string> Build(string sessionDir, Color accent, SettingsCapture settings)
        {
            var args = new List<string>
            {
                "--session-dir", sessionDir,
                "--accent-color", $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}",
            };

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
    }

    /// <summary>A finished, non-cancelled capture. <see cref="Session"/> is set for
    /// Edit/Upload; <see cref="Color"/> for SelectColor.</summary>
    public sealed class CaptureResult
    {
        public CaptureAction Action { get; init; }
        public SessionInfo Session { get; init; }
        public Color? Color { get; init; }
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

        private static void DeleteSessionDir(string sessionDir)
        {
            try
            {
                if (Directory.Exists(sessionDir))
                    Directory.Delete(sessionDir, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to delete session directory: " + ex);
            }
        }
    }
}
