using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Clowd.Config;
using Clowd.Util;

namespace Clowd.UI
{
    /// <summary>
    /// The parent→child <c>show</c> command of the persistent capture protocol
    /// (clowd_capture/src/host/protocol.rs). Field names and casing are the capturer's serde
    /// schema; every field is written on every show, so the values are exactly the ones
    /// <see cref="CaptureArguments.Build"/> would have put on the one-shot command line. Note the
    /// polarity: <c>peek</c>/<c>cursor</c> are positive here where the CLI has
    /// <c>--no-peek</c>/<c>--no-cursor</c>.
    /// </summary>
    internal sealed class CaptureShowCommand
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "show";

        [JsonPropertyName("session_dir")]
        public string SessionDir { get; set; }

        /// <summary>Hex <c>#RRGGBB</c>, the same string <c>--accent-color</c> takes.</summary>
        [JsonPropertyName("accent_color")]
        public string AccentColor { get; set; }

        [JsonPropertyName("tips_mode")]
        public string TipsMode { get; set; }

        [JsonPropertyName("peek")]
        public bool Peek { get; set; }

        [JsonPropertyName("peek_threshold")]
        public double PeekThreshold { get; set; }

        [JsonPropertyName("cursor")]
        public bool Cursor { get; set; }

        [JsonPropertyName("capture_mode")]
        public string CaptureMode { get; set; }

        [JsonPropertyName("video")]
        public bool Video { get; set; }
    }

    /// <summary>
    /// The warm capture host died after its overlay was already on screen. The capture cannot be
    /// retried from cold — the user has seen (and interacted with) an overlay — so this surfaces
    /// as a capturer crash exactly like a non-zero exit of a one-shot capture.
    /// </summary>
    internal sealed class CaptureHostCrashException : Exception
    {
        public CaptureHostCrashException(string message, int? exitCode, string log) : base(message)
        {
            ExitCode = exitCode;
            Log = log;
        }

        /// <summary>The child's exit code, or null when it could not be read.</summary>
        public int? ExitCode { get; }

        /// <summary>The child's chatter (stderr + non-protocol stdout) as of its death.</summary>
        public string Log { get; }
    }

    /// <summary>
    /// The warm capture host was taken down on purpose (app exit, an OS session ending, or the user
    /// turning <see cref="SettingsCapture.KeepCaptureReady"/> off) while a capture was in flight.
    /// Not a crash: the capture is cancelled quietly, exactly as if the user had pressed Escape.
    /// </summary>
    internal sealed class CaptureHostStoppedException : OperationCanceledException
    {
        public CaptureHostStoppedException() : base("The capture host was shut down by request.")
        {
        }
    }

    /// <summary>
    /// Keeps a fully warmed-up <c>clowd_capture_wgpu --persistent</c> child alive so pressing the
    /// capture hotkey only costs the fast per-capture work (screenshot, upload, show) instead of
    /// wgpu adapter enumeration and device init — 500-1000 ms on some machines. Speaks the NDJSON
    /// stdio protocol in clowd_capture/src/host/protocol.rs: <c>show</c>/<c>cancel</c>/<c>ping</c>/
    /// <c>shutdown</c> out, <c>ready</c>/<c>shown</c>/<c>finished</c>/<c>pong</c>/
    /// <c>display_changed</c>/<c>fatal_error</c> in. Everything else — the session payload, the
    /// action marker — still travels on disk in the per-capture session directory, so
    /// <see cref="CaptureSessionDispatcher.ProcessFinishedSession"/> is unchanged.
    ///
    /// Modelled on <see cref="ObsCapturer"/> (auto-flushed stdin, stdout/stderr pumps, only lines
    /// shaped <c>{...}</c> are protocol, bounded chatter ring, stdout EOF means the child is gone),
    /// with the lifetime differences a resident child brings: it outlives any single capture, so a
    /// death while idle is respawned with backoff rather than reported, and the caller falls back
    /// to the cold one-shot spawn whenever the host is not ready.
    ///
    /// The child exits on stdin EOF, so a crashed or force-killed Clowd.Ui cannot orphan it.
    /// </summary>
    internal sealed class CaptureProcessHost
    {
        /// <summary>How long the child may take to acknowledge a <c>show</c> with <c>shown</c>
        /// before it is considered wedged. The work behind it is a BitBlt plus a texture upload;
        /// anything near this is already a failure, and the caller still has the cold path.</summary>
        private static readonly TimeSpan ShowAckTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Grace period for a <c>shutdown</c> command before the tree is killed.</summary>
        private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Idle liveness probe interval. Only runs while the child is parked — a child
        /// that is showing an overlay is answering the user, not us.</summary>
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(60);

        private const int MaxMissedPongs = 2;

        /// <summary>Backoff between respawns after an unexpected death; the last entry repeats.</summary>
        private static readonly TimeSpan[] RespawnBackoff =
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
        };

        /// <summary>After this many deaths in a row without a single <c>ready</c> in between, the
        /// warm path is clearly broken on this machine: stop respawning (captures keep working via
        /// the cold path) and report once.</summary>
        private const int MaxConsecutiveFailures = 5;

        /// <summary>Floor on the delay before honouring a "restart me" exit. The child wants to come
        /// straight back, but a condition that re-fires on every new child must not turn into an
        /// unthrottled loop of wgpu-initializing processes.</summary>
        private static readonly TimeSpan RestartRequestDelay = TimeSpan.FromSeconds(1);

        /// <summary>A child that stayed alive this long before asking for a restart was doing its
        /// job and reacted to a real topology/GPU change; anything shorter died during (or right
        /// after) warm-up, which is the shape of a restart loop.</summary>
        private static readonly TimeSpan RestartRequestHealthyLifetime = TimeSpan.FromSeconds(30);

        /// <summary>How many short-lived restart requests in a row before the warm path is treated
        /// as broken. Deliberately larger than <see cref="MaxConsecutiveFailures"/>: docking, KVM
        /// switching and RDP legitimately produce bursts of these.</summary>
        private const int MaxConsecutiveRestartRequests = 10;

        /// <summary>Exit codes that mean "clean, respawn me" rather than a crash: the monitor
        /// topology changed under us (5) or the GPU device was lost (6). See
        /// clowd_rust_core/src/exit.rs (5) and clowd_capture/src/system/mod.rs (6, capture-only).</summary>
        private const int ExitCodeDisplayChanged = 5;
        private const int ExitCodeGpuLost = 6;

        /// <summary>macOS revoked Screen Recording between our launch check and the child's own —
        /// respawning would spin forever against a permission only a restart can pick up.</summary>
        private const int ExitCodeNoScreenPermission = 3;

        /// <summary>Name of the child's log file inside <see cref="PathConstants.LogData"/>; the
        /// child truncates it on start and keeps the previous run as <c>.1</c>.</summary>
        private const string HostLogFileName = "capture-host.log";

        // the child is quiet once warm, but a driver having a bad day can be very loud; bound it.
        private const int MaxLogLines = 300;
        private const int MaxLogChars = 64 * 1024;

        public static CaptureProcessHost Current { get; } = new CaptureProcessHost();

        private readonly object _lock = new object();
        private readonly object _logLock = new object();
        private readonly Queue<string> _log = new Queue<string>();
        private int _logChars;

        private Process _proc;
        private int _pid;
        private Task _stdoutPump;
        private Task _stderrPump;
        // every spawn bumps this; a pump or a death handler belonging to an older child compares
        // it and returns instead of trampling the current one.
        private int _generation;
        private CaptureHostState _state = CaptureHostState.Stopped;

        // non-null only while a capture is in flight; the child acks exactly one of each per show.
        private TaskCompletionSource<bool> _shownTcs;
        private TaskCompletionSource<bool> _finishedTcs;

        private IDisposable _pingTimer;
        private bool _pingOutstanding;
        private int _missedPongs;

        private CancellationTokenSource _respawnCts;
        private int _consecutiveFailures;
        private int _restartRequests;
        private bool _gaveUp;

        private bool _settingsHooked;
        private bool _stopRequested;
        private Stopwatch _spawnWatch;

        private CaptureProcessHost()
        {
        }

        /// <summary>True when a <c>show</c> would be answered immediately — the only state in
        /// which <see cref="RunCaptureAsync"/> is worth attempting.</summary>
        public bool IsReady
        {
            get
            {
                lock (_lock)
                    return _state == CaptureHostState.Ready;
            }
        }

        /// <summary>The live child's process id (0 when there is none), for
        /// <c>AllowSetForegroundWindow</c>.</summary>
        public int Pid
        {
            get
            {
                lock (_lock)
                    return _proc == null ? 0 : _pid;
            }
        }

        /// <summary>The child's most recent chatter (stderr + non-protocol stdout), bounded.</summary>
        public string GetLog()
        {
            lock (_logLock)
                return String.Join(Environment.NewLine, _log);
        }

        // ---- lifetime ----

        /// <summary>
        /// Starts (or restarts) the warm capture host. Called from App.Startup once settings are
        /// loaded, and again whenever the user flips <see cref="SettingsCapture.KeepCaptureReady"/>.
        /// A no-op — never an error — when the feature is off, the capture binary cannot be found,
        /// or macOS has not granted Screen Recording for this launch: in all three cases captures
        /// still work through the cold one-shot path.
        /// </summary>
        public void Start()
        {
            lock (_lock)
            {
                if (!_settingsHooked && SettingsRoot.Current?.Capture is { } capture)
                {
                    capture.PropertyChanged += OnSettingsChanged;
                    _settingsHooked = true;
                }

                _stopRequested = false;

                // an explicit start is the user (or the app) asking again from scratch — forget
                // any earlier crash streak so a fixed machine is not stuck on the cold path.
                _consecutiveFailures = 0;
                _restartRequests = 0;
                _gaveUp = false;

                TryStartLocked();
            }
        }

        /// <summary>Shuts the child down (<c>shutdown</c> → 2 s grace → kill the tree) and stops
        /// respawning until the next <see cref="Start"/>. Idempotent and safe to call with no child
        /// running. Everything blocking runs on the thread pool — done inline, the grace period
        /// would freeze the UI thread (and hold the OS shutdown thread) before the caller ever got
        /// a task to time out on, exactly the trap <see cref="ObsCapturer.DisposeAsync"/> avoids.</summary>
        public Task StopAsync()
        {
            Process proc;
            Task[] pumps;
            TaskCompletionSource<bool> shown, finished;

            lock (_lock)
            {
                _stopRequested = true;
                StopPingTimerLocked();
                CancelRespawnLocked();

                proc = _proc;
                pumps = new[] { _stdoutPump, _stderrPump };
                shown = _shownTcs;
                finished = _finishedTcs;
                _shownTcs = null;
                _finishedTcs = null;
                _proc = null;
                _pid = 0;
                _state = CaptureHostState.Stopped;
            }

            // a capture in flight ends here too, but on purpose: the caller cancels it quietly
            // instead of reporting the child's imminent (requested) death as a crash.
            var stopped = new CaptureHostStoppedException();
            FailObserved(shown, stopped);
            FailObserved(finished, stopped);

            return proc == null ? Task.CompletedTask : Task.Run(() => StopCoreAsync(proc, pumps));
        }

        private async Task StopCoreAsync(Process proc, Task[] pumps)
        {
            try
            {
                if (!proc.HasExited)
                {
                    // the child cancels any active cycle and unwinds its event loop on this; stdin
                    // EOF would do the same, but only once we exit.
                    WriteCommand(proc, "{\"type\":\"shutdown\"}");
                    if (!proc.WaitForExit((int)ShutdownGrace.TotalMilliseconds))
                        proc.Kill(entireProcessTree: true);
                }
            }
            // the child died on its own while we were asking it to (its death handler disposes the
            // process object) — the outcome we wanted either way.
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                Debug.WriteLine("[CaptureHost] error shutting down the capture host: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "capture.host-shutdown");
            }
            finally
            {
                await JoinPumpsAsync(pumps);
                try { proc.Dispose(); }
                catch { }
            }
        }

        private void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsCapture.KeepCaptureReady))
            {
                // Off the UI thread in both directions: Start probes for the binary and spawns the
                // child, and even StopAsync's synchronous prologue contends on _lock, which
                // TryStartLocked holds across Process.Start — either would hitch the settings
                // window the checkbox was toggled from.
                if (SettingsRoot.Current?.Capture?.KeepCaptureReady == true)
                    _ = Task.Run(Start);
                else
                    _ = Task.Run(StopAsync);
            }
            else if (e.PropertyName == nameof(SettingsCapture.MemoryHints))
            {
                // --memory-hints is read once at spawn, so a running warm host must be relaunched
                // to apply it. Both calls are no-ops when the feature is off.
                _ = Task.Run(async () =>
                {
                    await StopAsync();
                    Start();
                });
            }
        }

        /// <summary>Spawns the child if it should be running and is not already. Caller holds
        /// <see cref="_lock"/>.</summary>
        private void TryStartLocked()
        {
            if (_proc != null || _stopRequested || _gaveUp)
                return;

            if (SettingsRoot.Current?.Capture?.KeepCaptureReady != true)
                return;

            // TCC answers are handed to a process at launch, so a permission granted since we
            // started cannot be used anyway; the cold path prompts for it when a capture is asked
            // for. Keeping a child that can only see a blank desktop resident would be worse.
            if (!MacPermissions.HasScreenRecording)
                return;

            var binary = CaptureBinaryLocator.Resolve();
            if (binary == null)
            {
                Debug.WriteLine("[CaptureHost] capture binary not found; the warm host stays off.");
                return;
            }

            CancelRespawnLocked();

            var psi = new ProcessStartInfo(binary)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(binary),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--persistent");
            psi.ArgumentList.Add("--log-dir");
            psi.ArgumentList.Add(TryGetLogDirectory());
            if (SettingsRoot.Current?.Capture?.MemoryHints == CapturerMemoryHints.MaxPerformance)
            {
                psi.ArgumentList.Add("--memory-hints");
                psi.ArgumentList.Add("max-performance");
            }

            Process proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                // a missing/blocked binary must not take the app down — captures still work cold.
                Debug.WriteLine("[CaptureHost] failed to start the capture host: " + ex);
                SentryConfig.CaptureHandled(ex, "capture.host-spawn");
                return;
            }

            if (proc == null)
                return;

            // .NET's stdin writer does not auto-flush by default — without this a "show" would sit
            // in the writer's buffer and the overlay would never appear.
            proc.StandardInput.AutoFlush = true;

            ClearLog();
            _proc = proc;
            _pid = proc.Id;
            _state = CaptureHostState.Starting;
            _spawnWatch = Stopwatch.StartNew();
            var generation = ++_generation;

            _stdoutPump = Task.Run(() => PumpStdoutAsync(proc, generation));
            _stderrPump = Task.Run(() => PumpStderrAsync(proc, generation));

            Debug.WriteLine($"[CaptureHost] started {binary} (pid {_pid})");
        }

        /// <summary>The <c>--log-dir</c> the child writes <c>capture-host.log</c> into. Falls back
        /// to the temp directory if the Clowd log folder cannot be created — the child refuses to
        /// start without a usable one.</summary>
        private static string TryGetLogDirectory()
        {
            try
            {
                return PathConstants.LogData;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CaptureHost] could not resolve the log directory: " + ex.Message);
                return Path.GetTempPath();
            }
        }

        // ---- capture ----

        /// <summary>
        /// Runs one capture on the warm child. Returns true when the capture ran to completion (the
        /// session directory is now final and <see cref="CaptureSessionDispatcher.ProcessFinishedSession"/>
        /// should read it), false when the host could not take it — not ready, or it never
        /// acknowledged the show within <see cref="ShowAckTimeout"/> — in which case the caller
        /// falls back to a cold one-shot spawn with the same session directory. Throws
        /// <see cref="CaptureHostCrashException"/> if the child dies after the overlay appeared,
        /// which is a crash to report rather than something to retry.
        /// </summary>
        public async Task<bool> RunCaptureAsync(string sessionDir, CaptureMode mode, bool video, SettingsCapture settings)
        {
            TaskCompletionSource<bool> shown, finished;
            Process proc;

            lock (_lock)
            {
                if (_state != CaptureHostState.Ready || _proc == null)
                    return false;

                proc = _proc;
                _state = CaptureHostState.Busy;
                StopPingTimerLocked();
                shown = _shownTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                finished = _finishedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            try
            {
                WriteCommand(proc, BuildShowCommand(sessionDir, settings, mode, video));

                try
                {
                    await shown.Task.WaitAsync(ShowAckTimeout);
                }
                // we asked for this death; nothing to kill, nothing to retry from cold — the
                // capture is being cancelled on purpose and the caller treats it as such.
                catch (CaptureHostStoppedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // wedged (or dead) before anything reached the screen: take it down and let the
                    // caller spawn a cold capturer for this one. Killing it closes stdout, which
                    // drives the death handler and the respawn.
                    Debug.WriteLine("[CaptureHost] the capture host did not show the overlay: " + ex.Message);
                    KillChild(proc, "show was not acknowledged");
                    // ...but the kill is best-effort (access-denied throws), and a child that
                    // survives it never closes stdout, so the death handler that would take us out
                    // of Busy may never run. Cut it loose here so the warm path recovers either way.
                    AbandonChild(proc);
                    return false;
                }

                // no timeout here: the user may sit in the overlay for as long as they like. The
                // only thing that ends this wait short of a finished event is the child dying.
                await finished.Task;
                return true;
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_shownTcs, shown))
                        _shownTcs = null;
                    if (ReferenceEquals(_finishedTcs, finished))
                        _finishedTcs = null;
                }
            }
        }

        /// <summary>The <c>show</c> line for one capture. Mirrors <see cref="CaptureArguments.Build"/>
        /// value for value — the two paths must produce the same overlay.</summary>
        private static string BuildShowCommand(string sessionDir, SettingsCapture settings, CaptureMode mode, bool video)
        {
            // the overlay accent follows the OS (or the user's pick) and is contrast-corrected for
            // the white text drawn on it — see SettingsCapture.GetEffectiveAccentColor, issue #48.
            var accent = settings.GetEffectiveAccentColor();

            var command = new CaptureShowCommand
            {
                SessionDir = sessionDir,
                AccentColor = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}",
                TipsMode = settings.TipsMode.ToString().ToLowerInvariant(),
                Peek = settings.ObscuredWindowPeek,
                PeekThreshold = Math.Clamp(settings.ObscuredWindowDetectionThreshold, 0.0, 1.0),
                Cursor = settings.ScreenshotWithCursor,
                CaptureMode = mode.ToString().ToLowerInvariant(),
                Video = video,
            };

            return JsonSerializer.Serialize(command, ClowdUiJsonContext.Default.CaptureShowCommand);
        }

        // ---- protocol ----

        private async Task PumpStdoutAsync(Process proc, int generation)
        {
            try
            {
                string line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                    HandleStdoutLine(line, generation);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CaptureHost] stdout pump failed: " + ex);
                SentryConfig.CaptureHandled(ex, "capture.host-stdout-pump");
            }

            // stdout EOF: the child is gone (or going). EOF is only reached after every buffered
            // line has been delivered, so there is no race with a final finished event.
            await OnProcessEndedAsync(proc, generation);
        }

        private async Task PumpStderrAsync(Process proc, int generation)
        {
            try
            {
                // in persistent mode the child logs to stderr and its log file only — stdout
                // carries nothing but protocol lines.
                string line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                    AppendLog(line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CaptureHost] stderr pump failed: " + ex);
                SentryConfig.CaptureHandled(ex, "capture.host-stderr-pump");
            }
        }

        private void HandleStdoutLine(string line, int generation)
        {
            // protocol rule: only lines that start with '{' and end with '}' are events;
            // everything else is chatter and goes to the log buffer.
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
                    case "ready":
                        HandleReady(root, generation);
                        break;

                    case "shown":
                        // the number this whole feature exists for: compare against the cold
                        // startup timings in clowd_capture/src/telemetry/startup.rs.
                        Debug.WriteLine($"[CaptureHost] overlay shown in {ReadUInt64(root, "elapsed_ms")} ms");
                        CompleteShown(generation);
                        break;

                    case "finished":
                        Debug.WriteLine("[CaptureHost] capture finished: " + ReadString(root, "action"));
                        CompleteFinished(generation);
                        break;

                    case "pong":
                        lock (_lock)
                        {
                            if (generation == _generation)
                            {
                                _pingOutstanding = false;
                                _missedPongs = 0;
                            }
                        }
                        break;

                    case "display_changed":
                        // informational: the child exits with ExitCodeDisplayChanged right after,
                        // and the death handler respawns it without a backoff penalty.
                        Debug.WriteLine("[CaptureHost] the display topology changed; the host will restart.");
                        AppendLog(trimmed);
                        break;

                    case "fatal_error":
                        // the cycle is cancelled by the child and a finished event always follows,
                        // so the caller is never left hanging — record the reason for the crash log.
                        Debug.WriteLine("[CaptureHost] fatal error: " + ReadString(root, "message"));
                        AppendLog(trimmed);
                        break;

                    default:
                        AppendLog(trimmed);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CaptureHost] unparseable protocol line: " + trimmed + " (" + ex.Message + ")");
                SentryConfig.CaptureHandled(ex, "capture.host-protocol-parse");
                AppendLog(trimmed);
            }
        }

        private void HandleReady(JsonElement root, int generation)
        {
            var warmup = ReadUInt64(root, "warmup_ms");
            var monitors = ReadUInt64(root, "monitors");

            lock (_lock)
            {
                if (generation != _generation || _proc == null)
                    return;

                // a child that reached ready is a working child: the crash streak is over.
                _consecutiveFailures = 0;
                _state = CaptureHostState.Ready;
                StartPingTimerLocked();

                var spawnMs = _spawnWatch?.ElapsedMilliseconds ?? 0;
                Debug.WriteLine($"[CaptureHost] ready after {warmup} ms warmup ({monitors} monitors, " +
                                $"{spawnMs} ms since spawn)");
            }
        }

        private void CompleteShown(int generation)
        {
            TaskCompletionSource<bool> tcs;
            lock (_lock)
            {
                if (generation != _generation)
                    return;
                tcs = _shownTcs;
            }

            // an ack nobody is waiting for (a timed-out show answered late) is only logged.
            if (tcs == null || !tcs.TrySetResult(true))
                AppendLog("shown event with no capture in flight");
        }

        private void CompleteFinished(int generation)
        {
            TaskCompletionSource<bool> tcs;
            lock (_lock)
            {
                if (generation != _generation)
                    return;

                tcs = _finishedTcs;
                if (_state == CaptureHostState.Busy)
                {
                    _state = CaptureHostState.Ready;
                    StartPingTimerLocked();
                }
            }

            if (tcs == null || !tcs.TrySetResult(true))
                AppendLog("finished event with no capture in flight");
        }

        private void WriteCommand(Process proc, string json)
        {
            if (proc == null)
                return;

            try
            {
                proc.StandardInput.WriteLine(json);
            }
            catch (Exception ex)
            {
                // the child may already be dead; commands are best-effort, and the stdout pump
                // reports the death on its own.
                Debug.WriteLine("[CaptureHost] failed to write to the capture host's stdin: " + ex.Message);
            }
        }

        // ---- death and respawn ----

        private async Task OnProcessEndedAsync(Process proc, int generation)
        {
            try { await proc.WaitForExitAsync(); }
            // StopAsync may have disposed the process already; stdout EOF means it is gone anyway.
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                Debug.WriteLine("[CaptureHost] WaitForExitAsync failed: " + ex.Message);
            }

            int? exitCode = null;
            try { exitCode = proc.ExitCode; }
            catch { }

            TaskCompletionSource<bool> shown, finished;
            Task stderrPump;
            bool stale, stopRequested;

            lock (_lock)
            {
                stale = generation != _generation;
                stopRequested = _stopRequested;

                shown = _shownTcs;
                finished = _finishedTcs;
                stderrPump = _stderrPump;

                if (!stale)
                {
                    _shownTcs = null;
                    _finishedTcs = null;
                    _proc = null;
                    _pid = 0;
                    _state = CaptureHostState.Stopped;
                    StopPingTimerLocked();
                }
            }

            if (stale)
                return;

            Debug.WriteLine($"[CaptureHost] the capture host exited (code {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}).");

            // A capture in flight is over either way: before shown the caller cold-falls-back, after
            // shown it reports a crash — unless we asked for this death, which is a quiet cancel.
            // Both awaits see this exception, and they see it before the pump join below: that join
            // is bounded only by PumpDrainTimeout, and a child dying before it showed anything is
            // exactly the case the cold fallback has to be fast for.
            Exception died = stopRequested
                ? new CaptureHostStoppedException()
                : new CaptureHostCrashException("Capture process exited unexpectedly", exitCode, GetLog());
            FailObserved(shown, died);
            FailObserved(finished, died);

            // we *are* the stdout pump, so only the stderr one is left holding the process's
            // streams; it ends at EOF exactly as we just did. Draining it before disposing keeps
            // disposal from faulting a parked ReadLineAsync (see ObsCapturer.JoinPumpsAsync).
            await JoinPumpsAsync(new[] { stderrPump });
            try { proc.Dispose(); }
            catch { }

            if (stopRequested)
                return;

            ScheduleRespawn(exitCode);
        }

        /// <summary>Applies the respawn policy for a child that died on its own: a clean
        /// "restart me" exit comes back after a short floor delay, anything else backs off. Both
        /// paths are bounded — each has its own consecutive-failure cap — so no death can turn
        /// into an endless spawn loop.</summary>
        private void ScheduleRespawn(int? exitCode)
        {
            TimeSpan delay;
            string report = null;

            lock (_lock)
            {
                if (_stopRequested || _gaveUp)
                    return;

                if (exitCode == ExitCodeNoScreenPermission)
                {
                    // macOS took Screen Recording away; only a restart can pick it up again, and
                    // the cold path is the one that can explain that to the user.
                    Debug.WriteLine("[CaptureHost] screen recording permission was revoked; the warm host stays off.");
                    _gaveUp = true;
                    return;
                }

                if (exitCode == ExitCodeDisplayChanged || exitCode == ExitCodeGpuLost)
                {
                    // asked for by the child, not a failure: no backoff streak, no crash penalty.
                    // It still needs a bound of its own — a condition that re-arms on every fresh
                    // child (a mixed-DPI warm-up tripping the display-change debounce, a GPU whose
                    // device-lost callback fires again immediately) would otherwise spawn a new
                    // wgpu-initializing process every warm-up period forever, silently. A child
                    // that lived long enough to have been useful clears the streak.
                    if ((_spawnWatch?.Elapsed ?? TimeSpan.Zero) >= RestartRequestHealthyLifetime)
                        _restartRequests = 0;

                    if (++_restartRequests > MaxConsecutiveRestartRequests)
                    {
                        _gaveUp = true;
                        report = "Capture host keeps restarting itself";
                    }

                    delay = RestartRequestDelay;
                }
                else if (++_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    // reported below, outside the lock: it reads a log file off disk.
                    _gaveUp = true;
                    report = "Capture host process keeps crashing";
                    delay = TimeSpan.Zero;
                }
                else
                {
                    delay = RespawnBackoff[Math.Min(_consecutiveFailures - 1, RespawnBackoff.Length - 1)];
                }

                if (report == null)
                {
                    _state = CaptureHostState.Respawning;
                    CancelRespawnLocked();
                    var cts = new CancellationTokenSource();
                    _respawnCts = cts;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (delay > TimeSpan.Zero)
                                await Task.Delay(delay, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        lock (_lock)
                        {
                            if (!cts.IsCancellationRequested)
                                TryStartLocked();
                        }
                    });
                }
            }

            if (report != null)
                ReportGaveUp(exitCode, report);
        }

        /// <summary>One report, on the transition into "stop trying" — the crash itself repeats
        /// every backoff, so reporting each one would only bury the first.</summary>
        private void ReportGaveUp(int? exitCode, string message)
        {
            Debug.WriteLine($"[CaptureHost] giving up ({message}); captures will use the cold path.");

            // Keep the message free of the exit code so Sentry groups every host crash into one
            // issue; the specifics ride along in Data.
            var crash = new InvalidOperationException(message);
            crash.Data["exit_code"] = exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            crash.Data["chatter"] = Tail(GetLog());
            var hostLog = TryReadHostLog();
            if (hostLog != null)
                crash.Data["host_log"] = Tail(hostLog);
            SentryConfig.CaptureHandled(crash, "capture.host-crash");
        }

        /// <summary>Kills the child (and anything it spawned). The stdout pump hits EOF, which runs
        /// the normal death path — including the respawn.</summary>
        private void KillChild(Process proc, string reason)
        {
            try
            {
                if (!proc.HasExited)
                {
                    Debug.WriteLine($"[CaptureHost] killing the capture host: {reason}.");
                    proc.Kill(entireProcessTree: true);
                }
            }
            // the death handler may have disposed (and detached) the process already — it died on
            // its own, which is what we were asking for.
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                Debug.WriteLine("[CaptureHost] failed to kill the capture host: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "capture.host-kill");
            }
        }

        /// <summary>Forgets a child whose death we cannot count on (a kill that may have failed):
        /// the state machine leaves <see cref="CaptureHostState.Busy"/> deterministically and a
        /// fresh child is scheduled, instead of waiting on a stdout EOF that may never come.
        /// Bumping the generation makes the condemned child's pumps and death handler stale, so a
        /// later exit of it cannot respawn a second time. Closing its stdin is the only lever left
        /// on a process that survived <c>Kill</c> — the child exits on stdin EOF.
        /// A no-op if the death path got there first.</summary>
        private void AbandonChild(Process proc)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_proc, proc))
                    return;

                _shownTcs = null;
                _finishedTcs = null;
                _proc = null;
                _pid = 0;
                _state = CaptureHostState.Stopped;
                _generation++;
                StopPingTimerLocked();
            }

            try { proc.StandardInput.Close(); }
            catch { }

            // counted as a failure, so a machine that wedges every child gives up like any other
            // crash streak rather than respawning forever.
            ScheduleRespawn(null);
        }

        /// <summary>Waits for both pumps to drain before the <see cref="Process"/> is disposed:
        /// disposing it closes the stdio streams, which faults a parked <c>ReadLineAsync</c> instead
        /// of ending it at EOF.</summary>
        private static async Task JoinPumpsAsync(Task[] pumps)
        {
            try
            {
                await Task.WhenAll(Array.FindAll(pumps, p => p != null)).WaitAsync(PumpDrainTimeout);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CaptureHost] pumps did not drain before disposal: " + ex.Message);
            }
        }

        /// <summary>Faults a lifecycle TCS whose task may never be awaited (an idle death has no
        /// capture in flight, and a caller whose WaitAsync already timed out has walked away).
        /// Reading <see cref="Task.Exception"/> marks it observed so it cannot resurface as an
        /// <c>UnobservedTaskException</c> at GC time.</summary>
        private static void FailObserved<T>(TaskCompletionSource<T> tcs, Exception ex)
        {
            if (tcs != null && tcs.TrySetException(ex))
                _ = tcs.Task.Exception;
        }

        // ---- idle health check ----

        private void StartPingTimerLocked()
        {
            StopPingTimerLocked();
            _pingOutstanding = false;
            _missedPongs = 0;
            // not synchronized: this must not wait behind whatever the UI thread is doing.
            _pingTimer = DisposableTimer.Start(PingInterval, OnPingTick, synchronized: false);
        }

        private void StopPingTimerLocked()
        {
            _pingTimer?.Dispose();
            _pingTimer = null;
        }

        /// <summary>A parked child that has stopped answering is wedged — it would swallow the next
        /// capture into the show-ack timeout. Kill it now, while nobody is waiting.</summary>
        private void OnPingTick()
        {
            Process proc = null;
            bool wedged = false;

            lock (_lock)
            {
                // only while parked: a child painting an overlay is busy with the user.
                if (_state != CaptureHostState.Ready || _proc == null)
                    return;

                if (_pingOutstanding)
                    _missedPongs++;
                else
                    _missedPongs = 0;

                if (_missedPongs >= MaxMissedPongs)
                {
                    wedged = true;
                    proc = _proc;
                }
                else
                {
                    _pingOutstanding = true;
                    proc = _proc;
                }
            }

            if (wedged)
                KillChild(proc, $"{MaxMissedPongs} consecutive pings went unanswered");
            else
                WriteCommand(proc, "{\"type\":\"ping\"}");
        }

        // ---- logs ----

        /// <summary>Reads the child's log file (<c>capture-host.log</c>), which it keeps open while
        /// running. Null when absent or unreadable.</summary>
        public static string TryReadHostLog()
        {
            try
            {
                var path = Path.Combine(PathConstants.LogData, HostLogFileName);
                if (!File.Exists(path))
                    return null;

                // the child holds the file open for writing for its whole life.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CaptureHost] failed to read the capture host log: " + ex.Message);
                return null;
            }
        }

        /// <summary>The last few lines of a log — the failure is always at the end, after the
        /// routine startup lines.</summary>
        private static string Tail(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return "No diagnostic output was captured.";

            var lines = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length > 0)
                    lines.Add(line);
            }

            const int maxLines = 15;
            var start = Math.Max(0, lines.Count - maxLines);
            return String.Join("\n", lines.GetRange(start, lines.Count - start));
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

        private void ClearLog()
        {
            lock (_logLock)
            {
                _log.Clear();
                _logChars = 0;
            }
        }

        /// <summary>Drops a pending respawn. Never disposed: the scheduled task still reads the
        /// token it was handed, and a cancelled source with no registrations costs nothing.</summary>
        private void CancelRespawnLocked()
        {
            _respawnCts?.Cancel();
            _respawnCts = null;
        }

        private static string ReadString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

        private static ulong ReadUInt64(JsonElement root, string name) =>
            root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetUInt64(out var v)
                ? v
                : 0;

        /// <summary>Where the warm child is in its life. There is no state for "the feature is
        /// off" — that is <see cref="CaptureHostState.Stopped"/> with nothing scheduled.</summary>
        private enum CaptureHostState
        {
            Stopped,

            /// <summary>Spawned, warming up wgpu; not yet able to answer a show.</summary>
            Starting,

            /// <summary>Parked with everything initialized — a show is a few milliseconds away.</summary>
            Ready,

            /// <summary>An overlay is on screen.</summary>
            Busy,

            /// <summary>Dead, with a respawn scheduled after a backoff.</summary>
            Respawning,
        }
    }
}
