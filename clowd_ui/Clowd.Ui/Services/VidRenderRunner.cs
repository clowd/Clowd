using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Clowd.UI.Services
{
    /// <summary>How a vid-render run ended.</summary>
    internal enum VidRenderOutcome
    {
        Success,
        Canceled,
        Error,
    }

    /// <summary>The terminal state of a vid-render run: exactly one of the tool's <c>done</c>,
    /// <c>canceled</c> or <c>error</c> messages (or a synthesized error when the process died
    /// without sending one). <see cref="Message"/> is user-facing; <see cref="Diagnostics"/> is the
    /// captured stderr / unrecognized-stdout tail, for the crash report only.</summary>
    internal sealed record VidRenderResult(VidRenderOutcome Outcome, string OutputPath, long Bytes, string Message, string Diagnostics)
    {
        public static VidRenderResult Success(string outputPath, long bytes) =>
            new(VidRenderOutcome.Success, outputPath, bytes, null, null);

        public static VidRenderResult Canceled() =>
            new(VidRenderOutcome.Canceled, null, 0, null, null);

        public static VidRenderResult Error(string message) =>
            new(VidRenderOutcome.Error, null, 0, message, null);
    }

    /// <summary>
    /// Hosts one vid-render job and speaks its protocol, which is the vid2gif one verbatim:
    /// line-delimited plain text on stdout (<c>progress &lt;0-100&gt;</c> repeatedly, then exactly one
    /// of <c>done &lt;path&gt; &lt;bytes&gt;</c> / <c>canceled</c> / <c>error &lt;message&gt;</c>),
    /// free-form FFmpeg chatter on stderr, and <c>quit</c> on stdin to cancel. The whole job is
    /// described by a render-args JSON file (a serialized <see cref="Clowd.VideoSDK.Model.Project"/> plus
    /// sibling output/crf properties, written by <see cref="Clowd.VideoSDK.Editing.ProjectFileWriter"/>,
    /// which the tool dispatches on by version), which is the
    /// tool's single argument — there is no init handshake, the process starts rendering the moment
    /// it is spawned, so the whole lifecycle is one <see cref="RunAsync"/> call that resolves when
    /// the process has exited. <see cref="ProgressChanged"/> is raised on the UI thread. One
    /// instance per render.
    /// </summary>
    internal sealed class VidRenderRunner : IDisposable
    {
        public const string EnvVarName = "CLOWD_VID_RENDER_PATH";

        // vid-render honors a quit almost immediately (it polls the cancel token between frames),
        // so anything past this is a wedged child, not a slow one.
        private static readonly TimeSpan CancelGracePeriod = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(5);

        // FFmpeg can be chatty on stderr for a long render; only the tail is worth keeping (§4.2).
        private const int MaxLogLines = 200;
        private const int MaxLogChars = 32 * 1024;

        /// <summary>Raised on the UI thread for every <c>progress</c> line, clamped to 0-100.</summary>
        public event EventHandler<int> ProgressChanged;

        private readonly TaskCompletionSource<VidRenderResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object _logLock = new();
        private readonly Queue<string> _log = new();
        private int _logChars;

        private Process _proc;
        private Task _stdoutPump;
        private Task _stderrPump;
        private VidRenderResult _terminal;
        private volatile bool _cancelRequested;
        private volatile bool _disposed;
        private readonly object _disposeLock = new();
        private Task _shutdownTask;

        public static string BinaryFileName =>
            OperatingSystem.IsWindows() ? "Clowd.VideoRender.exe" : "Clowd.VideoRender";

        /// <summary>Locates the render tool: the <c>CLOWD_VID_RENDER_PATH</c> override first, then
        /// beside the app executable (Clowd.VideoRender publishes into the same directory as
        /// Clowd.Ui — see ci.yml), then the dev-build output next to this repo's csproj. Returns
        /// null when it cannot be found.</summary>
        public static string ResolveBinaryPath()
        {
            var env = Environment.GetEnvironmentVariable(EnvVarName);
            if (!String.IsNullOrWhiteSpace(env) && File.Exists(env))
                return Path.GetFullPath(env);

            var local = Path.Combine(AppContext.BaseDirectory, BinaryFileName);
            if (File.Exists(local))
                return Path.GetFullPath(local);

            // dev layout: walk up from the app's bin directory to the repo root and probe the
            // Clowd.VideoRender build output (mirrors ObsBinaryLocator's dev probe).
            var dir = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
            while (dir != null)
            {
                var project = Path.Combine(dir.FullName, "clowd_ui", "Clowd.VideoRender");
                if (Directory.Exists(project))
                {
                    foreach (var configuration in new[] { "Debug", "Release" })
                    {
                        var candidate = Path.Combine(project, "bin", configuration, "net10.0", BinaryFileName);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }

                dir = dir.Parent;
            }

            return null;
        }

        /// <summary>
        /// Spawns vid-render against an already-written render-args file and resolves once it has
        /// exited, with whichever terminal message it sent. May only be called once.
        /// Throws <see cref="FileNotFoundException"/> when the binary is missing and
        /// <see cref="InvalidOperationException"/> when the process cannot be started; every other
        /// failure comes back as a <see cref="VidRenderOutcome.Error"/> result.
        /// </summary>
        public async Task<VidRenderResult> RunAsync(string renderArgsPath)
        {
            if (_proc != null)
                throw new InvalidOperationException("RunAsync may only be called once per VidRenderRunner.");

            var exePath = ResolveBinaryPath();
            if (exePath == null)
                throw new FileNotFoundException(
                    $"Could not find {BinaryFileName}. It ships alongside the recorder; set {EnvVarName} to its full path to override.",
                    BinaryFileName);

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // the child writes the protocol as UTF-8 (Program.cs wraps stdout explicitly);
                // without this the runner decodes with the ANSI code page and a non-ASCII output
                // path corrupts the `done <path>` line.
                StandardOutputEncoding = new System.Text.UTF8Encoding(false),
                StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            };

            // The render tool self-locates FFmpeg (beside itself, then the dev walk-up), but in
            // the release layout the DLLs live in the obs-express/ subdirectory — hand it the
            // exact directory we already resolved obs-express from so the layouts can never
            // disagree. Harmless when obs-express is missing (the tool's own probing runs).
            var ffmpegDir = ObsBinaryLocator.ResolveFFmpegDirectory();
            if (!String.IsNullOrEmpty(ffmpegDir))
                psi.Environment["CLOWD_FFMPEG_PATH"] = ffmpegDir;

            // the whole job description is the file; nothing else is on the command line.
            psi.ArgumentList.Add(renderArgsPath);

            Debug.WriteLine("Starting video render: " + exePath + " " + String.Join(" ", psi.ArgumentList));
            _proc = Process.Start(psi);
            if (_proc == null)
                throw new InvalidOperationException("Failed to start video render process: " + exePath);

            // .NET's stdin writer does not auto-flush by default — without this a "quit" would sit
            // in the writer's buffer and the cancel would only ever land as a kill.
            _proc.StandardInput.AutoFlush = true;

            _stdoutPump = Task.Run(PumpStdoutAsync);
            _stderrPump = Task.Run(PumpStderrAsync);

            // a cancel that arrived while we were still spawning has no process to talk to yet.
            if (_cancelRequested)
                WriteCommand("quit");

            return await _completion.Task;
        }

        /// <summary>Asks the render to stop (stdin <c>quit</c>) and waits for the process to go
        /// away, killing it if it will not. The run itself still resolves through
        /// <see cref="RunAsync"/>, normally with <see cref="VidRenderOutcome.Canceled"/>.</summary>
        public async Task CancelAsync()
        {
            _cancelRequested = true;

            var proc = _proc;
            if (proc == null)
                return; // RunAsync sends the quit itself once the process exists

            WriteCommand("quit");

            try
            {
                await proc.WaitForExitAsync().WaitAsync(CancelGracePeriod);
            }
            catch (TimeoutException)
            {
                // a wedged FFmpeg would otherwise hold the row on the page forever; killing it
                // closes stdout, which drives OnProcessEndedAsync and resolves the run.
                Debug.WriteLine($"Video render did not stop within {CancelGracePeriod.TotalSeconds:0}s; killing it.");
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    // a concurrent disposal has already taken the process down and detached it.
                    Debug.WriteLine("Failed to kill wedged video render: " + ex.Message);
                    if (!_disposed)
                        SentryConfig.CaptureHandled(ex, "render.kill-wedged");
                }
            }
            catch (Exception ex)
            {
                // the process was disposed out from under us — it is gone, which is what we wanted.
                Debug.WriteLine("Waiting for the canceled video render to exit failed: " + ex.Message);
            }
        }

        /// <summary>The most recent stderr + unrecognized stdout output (bounded ring buffer).</summary>
        public string GetLog()
        {
            lock (_logLock)
                return String.Join(Environment.NewLine, _log);
        }

        /// <summary>Non-blocking best-effort shutdown; callers that can wait should
        /// <c>await</c> <see cref="DisposeAsync"/>.</summary>
        public void Dispose()
        {
            _ = DisposeAsync();
        }

        /// <summary>Kills the process if it is still running and releases it once the pumps have
        /// drained. Idempotent; every caller gets the same task.</summary>
        public Task DisposeAsync()
        {
            lock (_disposeLock)
            {
                if (_shutdownTask == null)
                {
                    _disposed = true;
                    var proc = _proc;
                    _shutdownTask = proc == null ? Task.CompletedTask : Task.Run(() => ShutdownCoreAsync(proc));
                }

                return _shutdownTask;
            }
        }

        private async Task ShutdownCoreAsync(Process proc)
        {
            try
            {
                // no clean-exit courtesy here: an abandoned render has nothing to flush, and
                // vid-render removes its own partial output when it is asked to quit rather than shot.
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error shutting down video render process: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "render.shutdown");
            }
            finally
            {
                await JoinPumpsAsync();
                try { proc.Dispose(); }
                catch { }
            }
        }

        /// <summary>Waits for both pumps before the <see cref="Process"/> is disposed: disposing it
        /// closes the stdio streams, which faults a parked <c>ReadLineAsync</c> instead of ending
        /// it at EOF.</summary>
        private async Task JoinPumpsAsync()
        {
            var pumps = new[] { _stdoutPump, _stderrPump };
            try
            {
                await Task.WhenAll(Array.FindAll(pumps, p => p != null)).WaitAsync(PumpDrainTimeout);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Video render pumps did not drain before disposal: " + ex.Message);
            }
        }

        private async Task PumpStdoutAsync()
        {
            try
            {
                string line;
                while ((line = await _proc.StandardOutput.ReadLineAsync()) != null)
                    HandleStdoutLine(line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Video render stdout pump failed: " + ex);
                SentryConfig.CaptureHandled(ex, "render.stdout-pump");
            }

            // stdout EOF: the process is gone (or going), and every buffered line — including the
            // terminal one — has already been handled.
            await OnProcessEndedAsync();
        }

        private async Task PumpStderrAsync()
        {
            try
            {
                string line;
                while ((line = await _proc.StandardError.ReadLineAsync()) != null)
                    AppendLog(line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Video render stderr pump failed: " + ex);
                SentryConfig.CaptureHandled(ex, "render.stderr-pump");
            }
        }

        private async Task OnProcessEndedAsync()
        {
            var exitCode = -1;
            try
            {
                await _proc.WaitForExitAsync();
                exitCode = _proc.ExitCode;
            }
            // a concurrent disposal may have released the Process already; stdout EOF means it is gone anyway.
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                Debug.WriteLine("WaitForExitAsync failed: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "render.wait-for-exit");
            }

            var result = _terminal;
            if (result == null)
            {
                // no terminal message: either we killed a wedged process (the user asked for that,
                // so honor the cancel) or it died on its own, which is a failure however it exited.
                result = _cancelRequested
                    ? VidRenderResult.Canceled()
                    : VidRenderResult.Error($"The video render process exited unexpectedly (exit code {exitCode}).");
            }

            // fill the diagnostics in here rather than at parse time: stderr is a separate pump, so
            // the FFmpeg lines explaining a failure often land after the error line itself.
            if (result.Outcome == VidRenderOutcome.Error)
                result = result with { Diagnostics = GetLog() };

            _completion.TrySetResult(result);
        }

        private void HandleStdoutLine(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                return;

            if (trimmed.StartsWith("progress ", StringComparison.Ordinal))
            {
                if (Int32.TryParse(trimmed.AsSpan("progress ".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                {
                    var clamped = Math.Clamp(percent, 0, 100);
                    Dispatcher.UIThread.Post(() => ProgressChanged?.Invoke(this, clamped));
                }
                else
                {
                    AppendLog(trimmed);
                }

                return;
            }

            if (trimmed.StartsWith("done ", StringComparison.Ordinal))
            {
                _terminal = ParseDone(trimmed);
                return;
            }

            if (trimmed.StartsWith("error ", StringComparison.Ordinal))
            {
                _terminal = VidRenderResult.Error(trimmed.Substring("error ".Length));
                return;
            }

            if (String.Equals(trimmed, "canceled", StringComparison.Ordinal))
            {
                _terminal = VidRenderResult.Canceled();
                return;
            }

            AppendLog(trimmed);
        }

        /// <summary>Splits <c>done &lt;path&gt; &lt;bytes&gt;</c>. The path is unquoted and may contain
        /// spaces, so only the trailing token is the size — without a parseable one the whole
        /// remainder is the path and the size is simply unknown.</summary>
        private static VidRenderResult ParseDone(string line)
        {
            var rest = line.Substring("done ".Length);
            var lastSpace = rest.LastIndexOf(' ');
            if (lastSpace > 0 && Int64.TryParse(rest.AsSpan(lastSpace + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
                return VidRenderResult.Success(rest.Substring(0, lastSpace), bytes);

            return VidRenderResult.Success(rest, 0);
        }

        private void WriteCommand(string command)
        {
            var proc = _proc;
            if (proc == null)
                return;

            try
            {
                proc.StandardInput.WriteLine(command);
            }
            catch (Exception ex)
            {
                // the process may already be dead; commands are best-effort. After disposal the
                // stdin writer is closed by design, so that failure is expected, not a defect.
                Debug.WriteLine($"Failed to write '{command}' to video render stdin: {ex.Message}");
                if (!_disposed)
                    SentryConfig.CaptureHandled(ex, "render.write-stdin");
            }
        }

        private void AppendLog(string line)
        {
            if (String.IsNullOrEmpty(line))
                return;

            lock (_logLock)
            {
                _log.Enqueue(line);
                _logChars += line.Length;
                while (_log.Count > MaxLogLines || _logChars > MaxLogChars)
                    _logChars -= _log.Dequeue().Length;
            }
        }
    }
}
