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

    /// <summary>One video track inside the recorded mp4: its stream index and the frame size it
    /// was encoded at.</summary>
    public sealed record ObsTrackInfo(int Index, int Width, int Height);

    /// <summary>One audio track inside the recorded mp4: its index among the audio streams, and
    /// what the recorder put on it — <c>"speaker"</c> (the system mix), <c>"microphone"</c>, or
    /// <c>"mixed"</c> when a single-track recording carried every device on one track. Only the
    /// recorder knows this; the file itself does not say, which is the whole point of keeping it.
    /// <see cref="Kind"/> is null when the report did not name one.</summary>
    public sealed record ObsAudioTrackInfo(int Index, string Kind);

    /// <summary>The tracks the recorder wrote, reported on <c>started_recording</c> and again on
    /// <c>stopped_recording</c> as an optional <c>tracks</c> object. The video editor needs it to
    /// place the webcam overlay (which stream, and what aspect ratio) and to label the audio rows.
    /// Null on a recorder too old to send it; <see cref="Webcam"/> is null whenever no camera was
    /// captured, and <see cref="Audio"/> is empty when the report named no audio tracks.</summary>
    public sealed record ObsTracks(ObsTrackInfo Screen, ObsTrackInfo Webcam, IReadOnlyList<ObsAudioTrackInfo> Audio);

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
        // the pumps unblock as soon as the process's stdio handles close, which the shutdown above
        // has already forced; this is only a backstop against blocking disposal forever.
        private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(5);

        // libobs is chatty on stderr for the life of the process; an hours-long recording
        // must not accumulate unboundedly (§4.2).
        private const int MaxLogLines = 1000;
        private const int MaxLogChars = 256 * 1024;

        /// <summary>Raised on the UI thread for every <c>status</c> message while recording.</summary>
        public event EventHandler<ObsStatus> StatusReceived;

        /// <summary>Raised on the UI thread for every <c>levels</c> message (10 Hz from
        /// <c>initialized</c> onward, only when audio sources exist).</summary>
        public event EventHandler<ObsLevels> LevelsReceived;

        /// <summary>The video tracks the recorder last reported (<c>started_recording</c>, then
        /// <c>stopped_recording</c>), or null when it did not report any — the field is optional,
        /// so an older recorder simply leaves this null and the video editor falls back to probing
        /// the file. Written from the stdout pump and read from the UI thread after a lifecycle
        /// ack has resolved, which is what orders the two.</summary>
        public ObsTracks LastTracks { get; private set; }

        /// <summary>Raised on the UI thread when the recording fails: a nonzero
        /// <c>stopped_recording</c> code (possibly spontaneous) or a process exit without a
        /// preceding <c>stopped_recording</c>.</summary>
        public event EventHandler<string> CriticalError;

        // a pause/resume only flips the output's pause flag; an unacked one means a wedged child.
        private static readonly TimeSpan PauseTimeout = TimeSpan.FromSeconds(10);

        private readonly TaskCompletionSource<bool> _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _startTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _stopTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // non-null only while a configure is in flight; the recorder acks exactly one event per
        // accepted command, so the next ack seen belongs to this one.
        private TaskCompletionSource<ObsConfigureResult> _configureTcs;
        // non-null only while a pause or resume is in flight (same single-ack contract:
        // recording_paused / recording_resumed).
        private TaskCompletionSource<bool> _pauseTcs;

        private readonly object _logLock = new();
        private readonly Queue<string> _log = new();
        private int _logChars;

        private Process _proc;
        private Task _stdoutPump;
        private Task _stderrPump;
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

            _stdoutPump = Task.Run(PumpStdoutAsync);
            _stderrPump = Task.Run(PumpStderrAsync);

            await WaitForAckAsync(_initTcs.Task, InitializeTimeout, "initialized");
        }

        /// <summary>Starts (or would resume) the recording; resolves on <c>started_recording</c>,
        /// i.e. when frames are actually flowing. Throws <see cref="TimeoutException"/> after 10 s.</summary>
        public async Task StartAsync()
        {
            WriteCommand("start");
            await WaitForAckAsync(_startTcs.Task, StartTimeout, "started_recording");
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
                return await WaitForAckAsync(_stopTcs.Task, StopTimeout, "stopped_recording");
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
                    // a concurrent disposal has already taken the process down and detached it,
                    // so only a live-object failure is worth reporting.
                    Debug.WriteLine("Failed to kill wedged recording process: " + ex.Message);
                    if (!_disposed)
                        SentryConfig.CaptureHandled(AttachProcessLog(ex), "obs.kill-wedged");
                }

                // bounded in case even Kill could not take the process down; a TimeoutException
                // here surfaces through the caller's catch → CriticalError path.
                return await WaitForAckAsync(_stopTcs.Task, TimeSpan.FromSeconds(10), "stopped_recording");
            }
        }

        /// <summary>Pauses the recording; resolves on <c>recording_paused</c>. The recorder keeps
        /// running (levels keep flowing) but no frames are written and <c>status</c> messages stop.
        /// Throws <see cref="TimeoutException"/> after 10 s.</summary>
        public Task PauseAsync() => PauseCoreAsync("pause");

        /// <summary>Resumes a paused recording; resolves on <c>recording_resumed</c>. (The wire
        /// command is <c>start</c>, which the recorder reuses for resume — <c>started_recording</c>
        /// only ever fires for the initial start.)</summary>
        public Task ResumeAsync() => PauseCoreAsync("start");

        private async Task PauseCoreAsync(string command)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _pauseTcs, tcs, null) != null)
                throw new InvalidOperationException("A pause/resume command is already in flight.");

            try
            {
                WriteCommand(command);
                await WaitForAckAsync(tcs.Task, PauseTimeout, command == "pause" ? "recording_paused" : "recording_resumed");
            }
            finally
            {
                Interlocked.CompareExchange(ref _pauseTcs, null, tcs);
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
                return await WaitForAckAsync(tcs.Task, ConfigureTimeout, "configure_applied");
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

        /// <summary>Stows the recent recorder output on <paramref name="ex"/> (under
        /// <see cref="SentryConfig.ProcessLogKey"/>) so whichever layer ultimately reports it —
        /// usually a <c>video.*</c> catch several frames up the page — attaches the log to the
        /// Sentry event. A bare "exited unexpectedly" or "timed out" is undiagnosable without
        /// libobs's stderr (CLOWD-Z, CLOWD-C).</summary>
        private Exception AttachProcessLog(Exception ex)
        {
            try
            {
                var log = GetLog();
                if (log.Length > 0 && !ex.Data.Contains(SentryConfig.ProcessLogKey))
                    ex.Data[SentryConfig.ProcessLogKey] = log;
            }
            catch
            {
                // Exception.Data may be read-only for exotic exception types
            }

            return ex;
        }

        /// <summary>Awaits a protocol ack, replacing the bare <c>WaitAsync</c> timeout with one
        /// that names the message that never came, and attaching the recorder's recent output to
        /// whatever failure surfaces.</summary>
        private async Task<T> WaitForAckAsync<T>(Task<T> task, TimeSpan timeout, string expected)
        {
            try
            {
                return await task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                throw AttachProcessLog(new TimeoutException(
                    $"The recording process did not send '{expected}' within {timeout.TotalSeconds:0} s."));
            }
            catch (Exception ex)
            {
                AttachProcessLog(ex);
                throw;
            }
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
                SentryConfig.CaptureHandled(AttachProcessLog(ex), "obs.shutdown");
            }
            finally
            {
                await JoinPumpsAsync();
                try { proc.Dispose(); }
                catch { }
            }
        }

        /// <summary>Waits for both pumps to drain before the <see cref="Process"/> is disposed:
        /// disposing it closes the stdio streams, which faults a parked <c>ReadLineAsync</c>
        /// instead of ending it at EOF. The pumps end on their own once the process is gone, so
        /// the timeout only matters if the child left an inherited handle to its stdout open.</summary>
        private async Task JoinPumpsAsync()
        {
            var pumps = new[] { _stdoutPump, _stderrPump };
            try
            {
                await Task.WhenAll(Array.FindAll(pumps, p => p != null)).WaitAsync(PumpDrainTimeout);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Recording pumps did not drain before disposal: " + ex.Message);
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
                SentryConfig.CaptureHandled(AttachProcessLog(ex), "obs.stdout-pump");
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
                SentryConfig.CaptureHandled(AttachProcessLog(ex), "obs.stderr-pump");
            }
        }

        private async Task OnProcessEndedAsync()
        {
            try { await _proc.WaitForExitAsync(); }
            // ShutdownCore may have disposed the Process already; stdout EOF means it is gone anyway.
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                Debug.WriteLine("WaitForExitAsync failed: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "obs.wait-for-exit");
            }

            // any exit without a preceding stopped_recording is a fatal error, regardless of
            // exit code (§1.4 — never key off the exit code alone; it is still worth reporting,
            // e.g. an NTSTATUS like 0xC0000005 names the crash where the log may not).
            string exitCode = null;
            try
            {
                var code = _proc.ExitCode;
                exitCode = code + " (0x" + unchecked((uint)code).ToString("X8") + ")";
            }
            catch
            {
                // disposal may have taken the Process object already
            }

            var died = AttachProcessLog(new InvalidOperationException(
                "The recording process has exited unexpectedly" + (exitCode == null ? "" : " with code " + exitCode) + "."));
            FailObserved(_initTcs, died);
            FailObserved(_startTcs, died);
            FailObserved(_configureTcs, died);
            FailObserved(_pauseTcs, died);
            if (_stopTcs.TrySetResult(false) && !_disposed)
                RaiseCriticalError("The recording process has exited unexpectedly.");
        }

        /// <summary>Faults a lifecycle TCS whose task may never be awaited — cancelling during WAIT
        /// leaves <c>_startTcs</c> untouched, and a caller whose <c>WaitAsync</c> already timed out
        /// has walked away from the original task. Reading <see cref="Task.Exception"/> marks it
        /// observed so it cannot resurface as an <c>UnobservedTaskException</c> at GC time.</summary>
        private static void FailObserved<T>(TaskCompletionSource<T> tcs, Exception ex)
        {
            if (tcs != null && tcs.TrySetException(ex))
                _ = tcs.Task.Exception;
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
                        // the tracks are read before the ack resolves, so a caller that awaits
                        // StartAsync can read LastTracks straight afterwards.
                        ReadTracks(root);
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
                        // ack for the pending PauseAsync/ResumeAsync; never sent unsolicited, so
                        // one nobody is waiting for (a timed-out command answered late) is only
                        // logged — same contract as configure.
                        var pauseTcs = _pauseTcs;
                        if (pauseTcs == null || !pauseTcs.TrySetResult(true))
                            AppendLog(trimmed);
                        break;

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
                // logged first so the offending line is part of the attached log
                AppendLog(trimmed);
                SentryConfig.CaptureHandled(AttachProcessLog(ex), "obs.protocol-parse");
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

        /// <summary>Reads the optional <c>tracks</c> object into <see cref="LastTracks"/>. An absent
        /// field leaves the previous value alone rather than clearing it: <c>stopped_recording</c> is
        /// the second report, and a recorder that sends tracks on start but not on stop must not
        /// lose them.</summary>
        private void ReadTracks(JsonElement root) => LastTracks = ParseTracks(root, LastTracks);

        /// <summary>
        /// The pure half of <see cref="ReadTracks"/>: the tracks a protocol message describes, or
        /// <paramref name="previous"/> when it describes none.
        ///
        /// <code>
        /// {"screen": {"index":0,"width":W,"height":H},
        ///  "webcam": {"index":1,"width":W,"height":H},          // absent, not null, without one
        ///  "audio":  [{"index":0,"kind":"speaker","device":"default","name":"Speaker 1"}, …]}
        /// </code>
        ///
        /// Every part of it is optional as far as this reader is concerned: an older recorder sends
        /// no <c>audio</c> array at all (single mixed track, nothing to say about it), and the
        /// editor's rows come from probing the file either way — the report only decorates them.
        /// So anything unparseable is dropped, never thrown on.
        /// </summary>
        internal static ObsTracks ParseTracks(JsonElement root, ObsTracks previous)
        {
            if (!root.TryGetProperty("tracks", out var tracksEl) || tracksEl.ValueKind != JsonValueKind.Object)
                return previous;

            var screen = ReadTrack(tracksEl, "screen");
            if (screen == null)
                return previous; // a tracks object without a screen track is not one we can use

            return new ObsTracks(screen, ReadTrack(tracksEl, "webcam"), ReadAudioTracks(tracksEl));
        }

        /// <summary>The <c>audio</c> array of a tracks report, in the order it was written. An entry
        /// without a numeric index says nothing about a stream and is skipped; a missing
        /// <c>kind</c> leaves the label to the editor's own fallback.</summary>
        private static IReadOnlyList<ObsAudioTrackInfo> ReadAudioTracks(JsonElement tracks)
        {
            if (!tracks.TryGetProperty("audio", out var el) || el.ValueKind != JsonValueKind.Array)
                return Array.Empty<ObsAudioTrackInfo>();

            var list = new List<ObsAudioTrackInfo>(el.GetArrayLength());
            foreach (var entry in el.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("index", out var indexEl)
                    || indexEl.ValueKind != JsonValueKind.Number
                    || !indexEl.TryGetInt32(out var index)
                    || index < 0)
                    continue;

                var kind = entry.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String
                    ? kindEl.GetString()
                    : null;

                list.Add(new ObsAudioTrackInfo(index, kind));
            }

            return list;
        }

        private static ObsTrackInfo ReadTrack(JsonElement tracks, string name)
        {
            // "webcam": null is the documented way of saying "no camera was captured".
            if (!tracks.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
                return null;

            return new ObsTrackInfo(
                ReadInt(el, "index"),
                ReadInt(el, "width"),
                ReadInt(el, "height"));
        }

        private static int ReadInt(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value)
                ? value
                : 0;

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
            // the final word on what was written; the page reads it once the stop has resolved.
            ReadTracks(root);

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
                // the process may already be dead; commands are best-effort. After disposal the
                // stdin writer is closed by design, so that failure is expected, not a defect.
                Debug.WriteLine($"Failed to write '{command}' to recording process stdin: {ex.Message}");
                if (!_disposed)
                    SentryConfig.CaptureHandled(AttachProcessLog(ex), "obs.write-stdin");
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
