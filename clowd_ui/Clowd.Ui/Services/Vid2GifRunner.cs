using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Clowd.UI.Services
{
    /// <summary>How a vid2gif run ended.</summary>
    internal enum Vid2GifOutcome
    {
        Success,
        Canceled,
        Error,
    }

    /// <summary>The terminal state of a vid2gif run: exactly one of the tool's <c>done</c>,
    /// <c>canceled</c> or <c>error</c> messages (or a synthesized error when the process died
    /// without sending one). <see cref="Message"/> is user-facing; <see cref="Diagnostics"/> is the
    /// captured stderr / unrecognized-stdout tail, for the crash report only.</summary>
    internal sealed record Vid2GifResult(Vid2GifOutcome Outcome, string OutputPath, long Bytes, string Message, string Diagnostics)
    {
        public static Vid2GifResult Success(string outputPath, long bytes) =>
            new(Vid2GifOutcome.Success, outputPath, bytes, null, null);

        public static Vid2GifResult Canceled() =>
            new(Vid2GifOutcome.Canceled, null, 0, null, null);

        public static Vid2GifResult Error(string message) =>
            new(Vid2GifOutcome.Error, null, 0, message, null);
    }

    /// <summary>
    /// Hosts one vid2gif conversion and speaks its protocol: line-delimited plain text on stdout
    /// (<c>progress &lt;0-100&gt;</c> repeatedly, then exactly one of <c>done &lt;path&gt; &lt;bytes&gt;</c> /
    /// <c>canceled</c> / <c>error &lt;message&gt;</c>), free-form FFmpeg chatter on stderr, and
    /// <c>quit</c> on stdin to cancel. Unlike the recorder there is no init handshake — the process
    /// starts converting the moment it is spawned — so the whole lifecycle is one
    /// <see cref="RunAsync"/> call that resolves when the process has exited.
    /// <see cref="ProgressChanged"/> is raised on the UI thread. One instance per conversion.
    /// </summary>
    internal sealed class Vid2GifRunner : IDisposable
    {
        public const string EnvVarName = "CLOWD_VID2GIF_PATH";

        // vid2gif honors a quit almost immediately (it polls the cancel token between frames), so
        // anything past this is a wedged child, not a slow one.
        private static readonly TimeSpan CancelGracePeriod = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(5);

        // FFmpeg can be chatty on stderr for a long conversion; only the tail is worth keeping (§4.2).
        private const int MaxLogLines = 200;
        private const int MaxLogChars = 32 * 1024;

        /// <summary>Raised on the UI thread for every <c>progress</c> line, clamped to 0-100.</summary>
        public event EventHandler<int> ProgressChanged;

        private readonly TaskCompletionSource<Vid2GifResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object _logLock = new();
        private readonly Queue<string> _log = new();
        private int _logChars;

        private Process _proc;
        private Task _stdoutPump;
        private Task _stderrPump;
        private Vid2GifResult _terminal;
        private volatile bool _cancelRequested;
        private volatile bool _disposed;
        private readonly object _disposeLock = new();
        private Task _shutdownTask;

        public static string BinaryFileName =>
            OperatingSystem.IsWindows() ? "vid2gif.exe" : "vid2gif";

        /// <summary>Locates vid2gif: the <c>CLOWD_VID2GIF_PATH</c> override first, otherwise the
        /// directory obs-express was found in — the two binaries ship side by side in every layout
        /// (release bundle and cargo target dir alike). Returns null when it cannot be found.</summary>
        public static string ResolveBinaryPath()
        {
            var env = Environment.GetEnvironmentVariable(EnvVarName);
            if (!String.IsNullOrWhiteSpace(env) && File.Exists(env))
                return HelperBinary.EnsureExecutable(Path.GetFullPath(env));

            var obs = ObsBinaryLocator.Resolve();
            if (String.IsNullOrEmpty(obs))
                return null;

            var candidate = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(obs)), BinaryFileName);
            return File.Exists(candidate) ? HelperBinary.EnsureExecutable(candidate) : null;
        }

        /// <summary>
        /// Spawns vid2gif and resolves once it has exited, with whichever terminal message it sent.
        /// May only be called once. <paramref name="maxWidth"/>/<paramref name="maxHeight"/> of 0
        /// mean "no cap" and omit the flag entirely — vid2gif's parser rejects 0 as a value.
        /// Throws <see cref="FileNotFoundException"/> when the binary is missing and
        /// <see cref="InvalidOperationException"/> when the process cannot be started; every other
        /// failure comes back as an <see cref="Vid2GifOutcome.Error"/> result.
        /// </summary>
        public async Task<Vid2GifResult> RunAsync(string inputPath, string outputPath, string quality, int maxWidth, int maxHeight)
        {
            if (_proc != null)
                throw new InvalidOperationException("RunAsync may only be called once per Vid2GifRunner.");

            var exePath = ResolveBinaryPath();
            if (exePath == null)
                throw new FileNotFoundException(
                    $"Could not find {BinaryFileName}. It ships alongside the recorder; set {EnvVarName} to its full path to override.",
                    BinaryFileName);

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // vid2gif links the FFmpeg DLLs that sit beside it and loads them relative to the
                // working directory — it cannot start anywhere else.
                WorkingDirectory = Path.GetDirectoryName(exePath),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add(outputPath);
            psi.ArgumentList.Add("--quality");
            psi.ArgumentList.Add(quality);
            if (maxWidth > 0)
            {
                psi.ArgumentList.Add("--max-width");
                psi.ArgumentList.Add(maxWidth.ToString(CultureInfo.InvariantCulture));
            }

            if (maxHeight > 0)
            {
                psi.ArgumentList.Add("--max-height");
                psi.ArgumentList.Add(maxHeight.ToString(CultureInfo.InvariantCulture));
            }

            Debug.WriteLine("Starting gif conversion: " + exePath + " " + String.Join(" ", psi.ArgumentList));
            _proc = Process.Start(psi);
            if (_proc == null)
                throw new InvalidOperationException("Failed to start gif conversion process: " + exePath);

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

        /// <summary>Asks the conversion to stop (stdin <c>quit</c>) and waits for the process to
        /// go away, killing it if it will not. The run itself still resolves through
        /// <see cref="RunAsync"/>, normally with <see cref="Vid2GifOutcome.Canceled"/>.</summary>
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
                Debug.WriteLine($"Gif conversion did not stop within {CancelGracePeriod.TotalSeconds:0}s; killing it.");
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    // a concurrent disposal has already taken the process down and detached it.
                    Debug.WriteLine("Failed to kill wedged gif conversion: " + ex.Message);
                    if (!_disposed)
                        SentryConfig.CaptureHandled(ex, "gif.kill-wedged");
                }
            }
            catch (Exception ex)
            {
                // the process was disposed out from under us — it is gone, which is what we wanted.
                Debug.WriteLine("Waiting for the canceled gif conversion to exit failed: " + ex.Message);
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
                // no clean-exit courtesy here: an abandoned conversion has nothing to flush, and
                // vid2gif removes its own partial output when it is asked to quit rather than shot.
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error shutting down gif conversion process: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "gif.shutdown");
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
                Debug.WriteLine("Gif conversion pumps did not drain before disposal: " + ex.Message);
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
                Debug.WriteLine("Gif conversion stdout pump failed: " + ex);
                SentryConfig.CaptureHandled(ex, "gif.stdout-pump");
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
                Debug.WriteLine("Gif conversion stderr pump failed: " + ex);
                SentryConfig.CaptureHandled(ex, "gif.stderr-pump");
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
                SentryConfig.CaptureHandled(ex, "gif.wait-for-exit");
            }

            var result = _terminal;
            if (result == null)
            {
                // no terminal message: either we killed a wedged process (the user asked for that,
                // so honor the cancel) or it died on its own, which is a failure however it exited.
                result = _cancelRequested
                    ? Vid2GifResult.Canceled()
                    : Vid2GifResult.Error($"The GIF conversion process exited unexpectedly (exit code {exitCode}).");
            }

            // fill the diagnostics in here rather than at parse time: stderr is a separate pump, so
            // the FFmpeg lines explaining a failure often land after the error line itself.
            if (result.Outcome == Vid2GifOutcome.Error)
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
                _terminal = Vid2GifResult.Error(trimmed.Substring("error ".Length));
                return;
            }

            if (String.Equals(trimmed, "canceled", StringComparison.Ordinal))
            {
                _terminal = Vid2GifResult.Canceled();
                return;
            }

            AppendLog(trimmed);
        }

        /// <summary>Splits <c>done &lt;path&gt; &lt;bytes&gt;</c>. The path is unquoted and may contain
        /// spaces, so only the trailing token is the size — without a parseable one the whole
        /// remainder is the path and the size is simply unknown.</summary>
        private static Vid2GifResult ParseDone(string line)
        {
            var rest = line.Substring("done ".Length);
            var lastSpace = rest.LastIndexOf(' ');
            if (lastSpace > 0 && Int64.TryParse(rest.AsSpan(lastSpace + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
                return Vid2GifResult.Success(rest.Substring(0, lastSpace), bytes);

            return Vid2GifResult.Success(rest, 0);
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
                Debug.WriteLine($"Failed to write '{command}' to gif conversion stdin: {ex.Message}");
                if (!_disposed)
                    SentryConfig.CaptureHandled(ex, "gif.write-stdin");
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
