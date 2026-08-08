using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.PlatformUtil;
using Clowd.Util;

namespace Clowd.UI
{
    /// <summary>Progress from the driver, one per phase change of every scroll step.</summary>
    internal sealed record ScrollProgress(int Frames, int HeightPx, string State);

    /// <summary>
    /// How a driver run ended, assembled from the terminal event (if one arrived) and the
    /// process's exit code. <see cref="Result"/> is null when the driver died without saying
    /// anything — a crash, or a cancel, which by contract emits nothing.
    /// </summary>
    internal sealed record ScrollDriverOutcome(string Result, string Message, int Frames, int HeightPx, int? ExitCode);

    /// <summary>
    /// Hosts one run of the <c>clowd_scroll_driver</c> process and speaks its NDJSON protocol
    /// (CAPTURE_PROTOCOL.md): JSON commands in on stdin, JSON events out on stdout, log chatter on
    /// stderr. Only lines that start with '{' and end with '}' are protocol — the same rule the
    /// recording and warm-host protocols use; everything else goes to the bounded log buffer a
    /// failure report attaches.
    /// <para>Exactly one run per instance: <see cref="RunAsync"/> resolves when the process has
    /// exited AND both pumps have drained, so the fields it reads are already final. Events are
    /// raised on the UI thread.</para>
    /// </summary>
    internal sealed class ScrollDriver
    {
        // the driver logs a line or two per scroll step, and a capped run is 120 steps — this
        // only needs to survive long enough to explain a failure.
        private const int MaxLogLines = 500;
        private const int MaxLogChars = 128 * 1024;

        // how long a cancelled driver gets to notice and unwind before it is killed. It polls
        // the flag at the top of each step and once more after settling, so the worst case is
        // one settle cycle (800 ms) plus the write itself.
        private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2);

        // the pumps end at stdout EOF, which the process's death has already forced; this is
        // only a backstop against a child that leaked an inherited handle to its own stdout.
        private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Raised on the UI thread for every <c>status</c> event.</summary>
        public event EventHandler<ScrollProgress> StatusReceived;

        private readonly object _logLock = new();
        private readonly Queue<string> _log = new();
        private int _logChars;

        private readonly object _shutdownLock = new();
        private Task _shutdownTask;
        private volatile bool _shuttingDown;

        private Process _proc;
        private Task _stdoutPump;
        private Task _stderrPump;

        // written by the stdout pump, read by RunAsync only after the pumps have joined.
        private string _doneResult;
        private string _fatalMessage;
        private int _frames;
        private int _heightPx;

        /// <summary>
        /// Spawns the driver and resolves when it is gone. Never throws for an outcome the caller
        /// can act on — a driver that fails says so on the protocol channel and still exits 0, so
        /// the returned outcome, not an exception, is what distinguishes success from failure.
        /// Only the spawn itself throws.
        /// </summary>
        public async Task<ScrollDriverOutcome> RunAsync(string binary, string sessionDir, ScreenRect region,
                                                        ScreenPoint point, long targetHwnd)
        {
            if (_proc != null)
                throw new InvalidOperationException("A ScrollDriver drives exactly one capture.");

            var psi = new ProcessStartInfo(binary)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(binary),
                // stdin must be redirected even though we may never write to it: the driver
                // treats a *closed* stdin as "the shell died" and cancels, which is exactly the
                // behavior we want if this process crashes mid-run.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var args = BuildArguments(sessionDir, region, point, targetHwnd);
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            Debug.WriteLine("Starting scrolling capture driver: " + binary + " " + String.Join(" ", args));
            var proc = Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException("Failed to start the scrolling capture process: " + binary);

            _proc = proc;

            // .NET's stdin writer does not auto-flush: without this a FINISH or CANCEL would sit
            // in the writer's buffer while the driver kept scrolling the user's window.
            proc.StandardInput.AutoFlush = true;

            // The driver foregrounds the window it is about to scroll, and a freshly spawned
            // process is refused SetForegroundWindow by the foreground lock. Best-effort: the
            // grant only works while we hold foreground rights ourselves, and the driver carries
            // on without it (Win10+ routes the wheel by cursor position regardless).
            if (OperatingSystem.IsWindows())
                AllowSetForegroundWindow(proc.Id);

            _stdoutPump = Task.Run(PumpStdoutAsync);
            _stderrPump = Task.Run(PumpStderrAsync);

            try
            {
                await proc.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                // a concurrent ShutdownAsync has already taken the process object down; its exit
                // is the outcome we were waiting for anyway.
                Debug.WriteLine("Waiting for the scrolling capture process failed: " + ex.Message);
            }

            int? exitCode = null;
            try { exitCode = proc.ExitCode; }
            catch { /* same race: disposal may have taken the Process with it */ }

            // joins the pumps (and disposes the process) whether or not anyone asked for a
            // shutdown, so the fields below are stable by the time they are read.
            await ShutdownAsync();

            return new ScrollDriverOutcome(_doneResult, _fatalMessage, _frames, _heightPx, exitCode);
        }

        /// <summary>
        /// The exact command line the driver is spawned with. Factored out so the argument
        /// contract is readable in one place — the driver rejects a missing or malformed
        /// <c>--region</c>/<c>--point</c> outright (as a <c>fatal_error</c> event, not a usage
        /// error).
        /// <para>Every numeric option is emitted as a single <c>--name=value</c> token. A monitor
        /// left of or above the primary has a negative virtual-desktop origin, and clap reads a
        /// separate value token that begins with '-' as an unknown flag — the run would die with
        /// a usage error before emitting a single protocol line. <c>--session-dir</c> stays a
        /// two-token pair: a path never starts with '-'.</para>
        /// </summary>
        internal static IReadOnlyList<string> BuildArguments(string sessionDir, ScreenRect region, ScreenPoint point,
                                                             long targetHwnd)
        {
            static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

            return new[]
            {
                "--session-dir", sessionDir,
                // physical virtual-desktop px, the same space the action.txt marker used.
                "--region=" + String.Join(",", Num(region.X), Num(region.Y), Num(region.Width), Num(region.Height)),
                "--point=" + String.Join(",", Num(point.X), Num(point.Y)),
                // 0 means "the overlay could not resolve one"; the driver falls back to
                // WindowFromPoint. It re-validates a non-zero handle either way.
                "--hwnd=" + Num(targetHwnd),
            };
        }

        /// <summary>Sends a command to the running driver. Best-effort by design: after a
        /// shutdown the stdin writer is closed on purpose, and a dead driver is exactly the
        /// state the command was trying to reach.</summary>
        public void Send(ScrollDriverCommand command)
        {
            var proc = _proc;
            if (proc == null)
                return;

            try
            {
                proc.StandardInput.WriteLine(JsonSerializer.Serialize(command, ClowdUiJsonContext.Default.ScrollDriverCommand));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write '{command.Type}' to the scrolling capture process: {ex.Message}");
                if (!_shuttingDown)
                    SentryConfig.CaptureHandled(AttachProcessLog(ex), "scroll.write-stdin");
            }
        }

        /// <summary>
        /// Ends the run and disposes the process: cancel, a short grace period, then kill the
        /// tree. Cancel rather than stop — a shutdown means nobody is left to route a finished
        /// session anywhere, so the run must write nothing. Idempotent; every caller awaits the
        /// same task, including <see cref="RunAsync"/> on its way out.
        /// </summary>
        public Task ShutdownAsync()
        {
            lock (_shutdownLock)
            {
                if (_shutdownTask == null)
                {
                    _shuttingDown = true;
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
                if (!proc.HasExited)
                {
                    Send(ScrollDriverCommand.Cancel);
                    if (!proc.WaitForExit((int)CancelGrace.TotalMilliseconds))
                        proc.Kill(entireProcessTree: true);
                }
            }
            // the driver died on its own while we were asking it to — the outcome we wanted.
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                Debug.WriteLine("Error shutting down the scrolling capture process: " + ex.Message);
                SentryConfig.CaptureHandled(AttachProcessLog(ex), "scroll.shutdown-process");
            }
            finally
            {
                await JoinPumpsAsync();
                try { proc.Dispose(); }
                catch { }
            }
        }

        /// <summary>Waits for both pumps before the <see cref="Process"/> is disposed: disposal
        /// closes the stdio streams, which faults a parked <c>ReadLineAsync</c> instead of ending
        /// it at EOF — and a faulted pump may not have recorded the terminal event yet.</summary>
        private async Task JoinPumpsAsync()
        {
            var pumps = new[] { _stdoutPump, _stderrPump };
            try
            {
                await Task.WhenAll(Array.FindAll(pumps, p => p != null)).WaitAsync(PumpDrainTimeout);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Scrolling capture pumps did not drain before disposal: " + ex.Message);
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
                Debug.WriteLine("Scrolling capture stdout pump failed: " + ex);
                SentryConfig.CaptureHandled(AttachProcessLog(ex), "scroll.stdout-pump");
            }
        }

        private async Task PumpStderrAsync()
        {
            try
            {
                // the driver routes its terminal logger to stderr precisely so
                // stdout can carry nothing but protocol lines.
                string line;
                while ((line = await _proc.StandardError.ReadLineAsync()) != null)
                    AppendLog(line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Scrolling capture stderr pump failed: " + ex);
                SentryConfig.CaptureHandled(AttachProcessLog(ex), "scroll.stderr-pump");
            }
        }

        private void HandleStdoutLine(string line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                AppendLog(trimmed);
                return;
            }

            try
            {
                var evt = JsonSerializer.Deserialize(trimmed, ClowdUiJsonContext.Default.ScrollDriverEvent);
                switch (evt?.Type)
                {
                    case ScrollDriverEventType.Ready:
                        // nothing to do: the border is already up, and the driver waits out its
                        // own start delay before the first frame. Logged so a run that never got
                        // this far is distinguishable from one that did.
                        Debug.WriteLine("Scrolling capture driver is ready.");
                        break;

                    case ScrollDriverEventType.Status:
                        _frames = evt.Frames;
                        _heightPx = evt.HeightPx;
                        var progress = new ScrollProgress(evt.Frames, evt.HeightPx, evt.State);
                        Dispatcher.UIThread.Post(() => StatusReceived?.Invoke(this, progress));
                        break;

                    case ScrollDriverEventType.Done:
                        _doneResult = evt.Result;
                        _frames = evt.Frames;
                        _heightPx = evt.HeightPx;
                        break;

                    case ScrollDriverEventType.FatalError:
                        _fatalMessage = evt.Message;
                        AppendLog(trimmed);
                        break;

                    default:
                        AppendLog(trimmed);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Unparseable scrolling capture protocol line: " + trimmed + " (" + ex.Message + ")");
                // logged first so the offending line is part of the attached log
                AppendLog(trimmed);
                SentryConfig.CaptureHandled(AttachProcessLog(ex), "scroll.protocol-parse");
            }
        }

        /// <summary>The tail of the driver's output, for an error dialog: whatever went wrong is
        /// always at the end, after the routine startup lines.</summary>
        public string GetLogTail(int maxLines = 15)
        {
            lock (_logLock)
            {
                var skip = Math.Max(0, _log.Count - maxLines);
                var tail = new List<string>(Math.Min(_log.Count, maxLines));
                foreach (var entry in _log)
                {
                    if (skip-- > 0)
                        continue;
                    tail.Add(entry);
                }

                return tail.Count == 0 ? null : String.Join(Environment.NewLine, tail);
            }
        }

        /// <summary>Stows the driver's recent output on <paramref name="ex"/> so whichever layer
        /// reports it attaches the log — "exited unexpectedly" is undiagnosable without it.</summary>
        private Exception AttachProcessLog(Exception ex)
        {
            try
            {
                var log = GetLogTail(MaxLogLines);
                if (log != null && !ex.Data.Contains(SentryConfig.ProcessLogKey))
                    ex.Data[SentryConfig.ProcessLogKey] = log;
            }
            catch
            {
                // Exception.Data may be read-only for exotic exception types
            }

            return ex;
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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
    }
}
