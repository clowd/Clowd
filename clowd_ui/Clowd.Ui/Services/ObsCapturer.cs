using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Clowd.UI
{
    /// <summary>A 1 Hz progress report from obs-express while recording (DESIGN §1.3).
    /// <see cref="Fps"/> is a float on the wire and must be parsed as a double.</summary>
    public sealed record ObsStatus(double Fps, long Dropped, TimeSpan Elapsed);

    /// <summary>Peak audio levels from obs-express, emitted every 100 ms from <c>initialized</c>
    /// onward (including the pre-start WAIT phase) whenever audio sources exist: dBFS per source
    /// in CLI order, always finite, floored at -100.</summary>
    public sealed record ObsLevels(double[] Speaker, double[] Mic);

/// <summary>The recorder's answer to a <c>configure</c> command (DESIGN §1.3): either
/// <c>configure_applied</c>, whose <see cref="IgnoredKeys"/> names the settings the recorder
/// refused to change in its current phase (empty before recording starts), or
/// <c>configure_error</c>, which carries a message and a <see cref="Fatal"/> flag meaning the
/// pipeline may match neither the old nor the new settings.</summary>
public sealed record ObsConfigureResult(bool Applied, string[] IgnoredKeys, string Message, bool Fatal);

    /// <summary>
    /// Hosts the obs-express recording process and speaks its protocol (DESIGN §1): plain-text
    /// commands on stdin, line-delimited JSON on stdout, free-form libobs chatter on stderr.
    /// Lifecycle: <see cref="InitializeAsync"/> (spawns with <c>--pause</c>, resolves on
    /// <c>initialized</c>) → <see cref="StartAsync"/> (stdin <c>start</c>, resolves on
    /// <c>started_recording</c>) → <see cref="StopAsync"/> (stdin <c>quit</c>, resolves on
    /// <c>stopped_recording</c> or process exit). A <c>stopped_recording</c> message may arrive
    /// spontaneously at any time after recording starts (disk full / encoder error, §1.3) and any
    /// process exit without a preceding <c>stopped_recording</c> is fatal regardless of exit code
    /// (§1.4) — both surface through <see cref="CriticalError"/>. Events are raised on the UI
    /// thread. obs-express treats stdin EOF as <c>quit</c>, so a crashed Clowd.Ui cannot orphan
    /// a recording; no watchdog is needed.
    /// </summary>
    internal sealed class ObsCapturer : IDisposable
    {
        private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
        // a configure only re-reads a small file and touches the (not yet running) pipeline; if it
        // has not been acked by now the child is wedged and the page respawns it.
        private static readonly TimeSpan ConfigureTimeout = TimeSpan.FromSeconds(10);
        // comfortably above obs-express's own in-process 30 s stop deadline (§1.4), so the
        // synthetic code:-99 stopped_recording normally arrives first and this only fires
        // when the child is wedged hard (blocked kernel I/O, libobs deadlock).
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(45);

        // libobs is chatty on stderr for the life of the process; an hours-long recording
        // must not accumulate unboundedly (§4.2).
        private const int MaxLogLines = 1000;
        private const int MaxLogChars = 256 * 1024;

        /// <summary>Raised on the UI thread for every <c>status</c> message while recording.</summary>
        public event EventHandler<ObsStatus> StatusReceived;

        /// <summary>Raised on the UI thread for every <c>levels</c> message (10 Hz from
        /// <c>initialized</c> onward, only when audio sources exist).</summary>
        public event EventHandler<ObsLevels> LevelsReceived;

        /// <summary>Raised on the UI thread when the recording fails: a nonzero
        /// <c>stopped_recording</c> code (possibly spontaneous) or a process exit without a
        /// preceding <c>stopped_recording</c>.</summary>
        public event EventHandler<string> CriticalError;

        private readonly TaskCompletionSource<bool> _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _startTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _stopTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // non-null only while a configure is in flight; the recorder acks exactly one event per
        // accepted command, so the next ack seen belongs to this one.
        private TaskCompletionSource<ObsConfigureResult> _configureTcs;

        private readonly object _logLock = new();
        private readonly Queue<string> _log = new();
        private int _logChars;

        private Process _proc;
        private volatile bool _disposed;
        private readonly object _disposeLock = new();
        private Task _shutdownTask;

        /// <summary>Starts the obs-express process and resolves when the pipeline is fully built
        /// (<c>{"type":"initialized"}</c>). Throws <see cref="TimeoutException"/> after 30 s
        /// (first-run OBS init can be slow, but not that slow).</summary>
        public async Task InitializeAsync(IReadOnlyList<string> args, string exePath)
        {
            if (_proc != null)
                throw new InvalidOperationException("InitializeAsync may only be called once per ObsCapturer.");

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            Debug.WriteLine("Starting recording process: " + exePath + " " + String.Join(" ", args));
            _proc = Process.Start(psi);
            if (_proc == null)
                throw new InvalidOperationException("Failed to start recording process: " + exePath);

            // .NET's stdin writer does not auto-flush by default — without this, "start"/"quit"
            // would sit in the writer's buffer forever (hang → kill → unfinalized mp4).
            _proc.StandardInput.AutoFlush = true;

            _ = Task.Run(PumpStdoutAsync);
            _ = Task.Run(PumpStderrAsync);

            await _initTcs.Task.WaitAsync(InitializeTimeout);
        }

        /// <summary>Starts (or would resume) the recording; resolves on <c>started_recording</c>,
        /// i.e. when frames are actually flowing. Throws <see cref="TimeoutException"/> after 10 s.</summary>
        public async Task StartAsync()
        {
            WriteCommand("start");
            await _startTcs.Task.WaitAsync(StartTimeout);
        }

        /// <summary>Stops the recording and flushes the mp4. Returns true on a clean stop
        /// (<c>stopped_recording</c> code 0, including cancel-before-start); false when the
        /// recording failed — in which case <see cref="CriticalError"/> has already been raised.</summary>
        public async Task<bool> StopAsync()
        {
            // if the output already stopped (spontaneously or otherwise) the process is exiting
            // on its own; the extra "quit" would be harmless but pointless.
            if (!_stopTcs.Task.IsCompleted)
                WriteCommand("quit");

            try
            {
                return await _stopTcs.Task.WaitAsync(StopTimeout);
            }
            catch (TimeoutException)
            {
                // a wedged child would otherwise leave the app in a hidden, un-restartable
                // recording state forever. Kill it: closing stdout drives OnProcessEndedAsync,
                // which completes _stopTcs(false) and raises CriticalError, so the page's error
                // path (log file, keep-partial-mp4) runs as designed.
                Debug.WriteLine($"Recording process did not stop within {StopTimeout.TotalSeconds:0}s; killing it.");
                try
                {
                    _proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to kill wedged recording process: " + ex.Message);
                    SentryConfig.CaptureHandled(ex, "obs.kill-wedged");
                }

                // bounded in case even Kill could not take the process down; a TimeoutException
                // here surfaces through the caller's catch → CriticalError path.
                return await _stopTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>
        /// Points the recorder at a rewritten settings file and waits for it to apply the change,
        /// which replaces the old tear-down-and-respawn cycle for settings edited during WAIT.
        /// Throws <see cref="TimeoutException"/> after 10 s and
        /// <see cref="InvalidOperationException"/> if the process has already exited; callers
        /// treat any failure — thrown or a non-applied result — as "respawn the recorder".
        /// </summary>
        public async Task<ObsConfigureResult> ConfigureAsync(string settingsPath)
        {
            var tcs = new TaskCompletionSource<ObsConfigureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _configureTcs, tcs, null) != null)
                throw new InvalidOperationException("A configure command is already in flight.");

            try
            {
                // the path is everything after the command word, verbatim: the recorder trims it
                // and does no unquoting, so it may contain spaces but must never be quoted.
                WriteCommand("configure " + settingsPath);
                return await tcs.Task.WaitAsync(ConfigureTimeout);
            }
            finally
            {
                Interlocked.CompareExchange(ref _configureTcs, null, tcs);
            }
        }

        /// <summary>Mutes/unmutes the first speaker source (index 0 — one device each, WPF
        /// parity). Safe to call while recording; no-op if no speaker source was configured.</summary>
        public void SetSpeakerMute(bool mute) => WriteCommand(mute ? "mute-speaker 0" : "unmute-speaker 0");

        /// <summary>Mutes/unmutes the first microphone source (index 0).</summary>
        public void SetMicrophoneMute(bool mute) => WriteCommand(mute ? "mute-mic 0" : "unmute-mic 0");

        /// <summary>The most recent non-JSON stdout + stderr output (bounded ring buffer),
        /// for the error-log file written on <see cref="CriticalError"/>.</summary>
        public string GetLog()
        {
            lock (_logLock)
                return String.Join(Environment.NewLine, _log);
        }

        /// <summary>Non-blocking best-effort shutdown for safety-net callers; UI-thread code
        /// should <c>await</c> <see cref="DisposeAsync"/> instead.</summary>
        public void Dispose()
        {
            _ = DisposeAsync();
        }

        /// <summary>Shuts the process down (best-effort quit → 5 s wait → kill) on the thread
        /// pool. The quit sits unread while OBS is still initializing, so the wait can consume
        /// the full 5 s — synchronous disposal froze the UI thread for that long when cancelling
        /// during WAIT. Idempotent; every caller gets the same task.</summary>
        public Task DisposeAsync()
        {
            lock (_disposeLock)
            {
                if (_shutdownTask == null)
                {
                    _disposed = true;
                    var proc = _proc;
                    _shutdownTask = proc == null ? Task.CompletedTask : Task.Run(() => ShutdownCore(proc));
                }

                return _shutdownTask;
            }
        }

        private void ShutdownCore(Process proc)
        {
            try
            {
                if (!proc.HasExited)
                {
                    // best effort: a clean quit flushes the mp4 if we are still recording.
                    WriteCommand("quit");
                    proc.WaitForExit(5000);
                }

                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error shutting down recording process: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "obs.shutdown");
            }
            finally
            {
                try { proc.Dispose(); }
                catch { }
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
                Debug.WriteLine("Recording stdout pump failed: " + ex);
                SentryConfig.CaptureHandled(ex, "obs.stdout-pump");
            }

            // stdout EOF: the process is gone (or going). EOF is only reached after every
            // buffered line has been delivered, so there is no race with stopped_recording.
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
                Debug.WriteLine("Recording stderr pump failed: " + ex);
                SentryConfig.CaptureHandled(ex, "obs.stderr-pump");
            }
        }

        private async Task OnProcessEndedAsync()
        {
            try { await _proc.WaitForExitAsync(); }
            catch (Exception ex)
            {
                Debug.WriteLine("WaitForExitAsync failed: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "obs.wait-for-exit");
            }

            // any exit without a preceding stopped_recording is a fatal error, regardless of
            // exit code (§1.4 — never key off the exit code alone).
            var died = new InvalidOperationException("The recording process has exited unexpectedly.");
            _initTcs.TrySetException(died);
            _startTcs.TrySetException(died);
            _configureTcs?.TrySetException(died);
            if (_stopTcs.TrySetResult(false) && !_disposed)
                RaiseCriticalError("The recording process has exited unexpectedly.");
        }

        private void HandleStdoutLine(string line)
        {
            // protocol rule (§1.3): consumers parse only lines that start with '{' and end
            // with '}'; everything else is chatter and goes to the log buffer.
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                AppendLog(trimmed);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "initialized":
                        _initTcs.TrySetResult(true);
                        break;

                    case "started_recording":
                        _startTcs.TrySetResult(true);
                        break;

                    case "status":
                        // fps is a float on the wire (§1.3) — GetDouble, never GetInt32.
                        var status = new ObsStatus(
                            root.GetProperty("fps").GetDouble(),
                            root.GetProperty("dropped").GetInt64(),
                            TimeSpan.FromMilliseconds(root.GetProperty("timeMs").GetDouble()));
                        Dispatcher.UIThread.Post(() => StatusReceived?.Invoke(this, status));
                        break;

                    case "levels":
                        var levels = new ObsLevels(ReadLevelArray(root, "speaker"), ReadLevelArray(root, "mic"));
                        Dispatcher.UIThread.Post(() => LevelsReceived?.Invoke(this, levels));
                        break;

                    case "configure_applied":
                        CompleteConfigure(new ObsConfigureResult(true, ReadStringArray(root, "ignored_keys"), null, false), trimmed);
                        break;

                    case "configure_error":
                        var configureError = root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                            ? msgEl.GetString()
                            : "The recorder rejected the new settings.";
                        var fatal = root.TryGetProperty("fatal", out var fatalEl) && fatalEl.ValueKind == JsonValueKind.True;
                        CompleteConfigure(new ObsConfigureResult(false, Array.Empty<string>(), configureError, fatal), trimmed);
                        break;

                    case "recording_paused":
                    case "recording_resumed":
                        break; // informational; not surfaced in the UI

                    case "stopped_recording":
                        HandleStoppedRecording(root);
                        break;

                    default:
                        AppendLog(trimmed);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Unparseable recording protocol line: " + trimmed + " (" + ex.Message + ")");
                SentryConfig.CaptureHandled(ex, "obs.protocol-parse");
                AppendLog(trimmed);
            }
        }

        private static double[] ReadLevelArray(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
                return Array.Empty<double>();

            var values = new double[el.GetArrayLength()];
            for (var i = 0; i < values.Length; i++)
                values[i] = el[i].GetDouble();
            return values;
        }

        private static string[] ReadStringArray(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var values = new string[el.GetArrayLength()];
            for (var i = 0; i < values.Length; i++)
                values[i] = el[i].GetString();
            return values;
        }

        /// <summary>Hands an ack to the pending <see cref="ConfigureAsync"/>. The recorder never
        /// sends one unsolicited, so an ack nobody is waiting for (a timed-out configure being
        /// answered late) is only logged.</summary>
        private void CompleteConfigure(ObsConfigureResult result, string line)
        {
            var tcs = _configureTcs;
            if (tcs == null || !tcs.TrySetResult(result))
                AppendLog(line);
        }

        private void HandleStoppedRecording(JsonElement root)
        {
            var code = root.GetProperty("code").GetInt64();
            var message = root.GetProperty("message").GetString();
            string error = null;
            if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
                error = errorEl.GetString();

            Debug.WriteLine($"Recording stopped: code={code} message={message} error={error}");

            // may arrive spontaneously mid-recording (disk full / encoder error, §1.3) — the
            // handling is identical whether or not a StopAsync is pending. Idempotent: only the
            // first completion reports.
            var success = code == 0;
            if (!_stopTcs.TrySetResult(success))
                return;

            if (!success)
                RaiseCriticalError(String.IsNullOrEmpty(error) ? message : message + "\n" + error);
        }

        private void RaiseCriticalError(string message)
        {
            Dispatcher.UIThread.Post(() => CriticalError?.Invoke(this, message));
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
                // the process may already be dead; commands are best-effort.
                Debug.WriteLine($"Failed to write '{command}' to recording process stdin: {ex.Message}");
                SentryConfig.CaptureHandled(ex, "obs.write-stdin");
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
