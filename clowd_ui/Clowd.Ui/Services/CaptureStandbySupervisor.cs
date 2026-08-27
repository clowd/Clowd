using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.Util;

namespace Clowd.UI
{
    /// <summary>
    /// Runs the capturer in its lightweight hotkey-waiting standby mode (CAPTURE_PROTOCOL.md).
    /// Exists only while warm capture is in effect: created and disposed by
    /// <c>App.ConfigureCaptureHotkeys</c>, which owns the choice between this and the classic
    /// SharpHook + one-shot path. When standby cannot work — missing permission or binary,
    /// permission-revoked exit, repeated crashes — the supervisor calls the fallback delegate
    /// exactly once and stops; the shell then reverts to one-shot capture for the rest of its
    /// process lifetime without touching the saved setting.
    /// </summary>
    internal sealed class CaptureStandbySupervisor : IDisposable
    {
        private const int MaxPipeChars = 256 * 1024;
        private const int MaxConsecutiveFailures = 3;
        private const string ReadyLine = "CLOWD_STANDBY_READY";
        private const string SessionPrefix = "CLOWD_SESSION ";
        private const string CaptureFinishedPrefix = "CLOWD_CAPTURE_FINISHED ";
        private const string SettingsStatusPrefix = "CLOWD_SETTINGS_STATUS ";
        private const string IpcErrorPrefix = "CLOWD_IPC_ERROR ";

        /// <summary>A process that has been running this long forgives earlier crashes:
        /// without this, three unrelated crashes weeks apart (say one per GPU driver
        /// update) would silently degrade a long-lived session to one-shot forever.
        /// Watchdog kills are exempt — see the failure accounting in SuperviseAsync.</summary>
        private static readonly TimeSpan StableUptime = TimeSpan.FromMinutes(10);

        /// <summary>How long the overlay may stay open before the process is presumed hung
        /// and killed (the crash path then restarts or falls back). Generous on purpose —
        /// people do sit in the overlay — because a false positive costs a capture, while a
        /// true hang would otherwise swallow tray clicks and block updates forever.</summary>
        private static readonly TimeSpan OverlayHangTimeout = TimeSpan.FromMinutes(30);

        private static CaptureStandbySupervisor _current;

        /// <summary>True while the standby capturer's overlay is on screen (or an accepted
        /// request is about to put it there) — merged into ScreenCapturePage.IsCaptureActive
        /// for the IdleMonitor update gate.</summary>
        public static bool IsOverlayOpen =>
            Volatile.Read(ref _current) is { } current && Volatile.Read(ref current._overlayOpen) != 0;

        private readonly CancellationTokenSource _stop = new();
        private readonly object _stdinGate = new();
        private Action _onFallback;
        private Process _process;
        private string _lastSettings;
        private int _ready;
        private int _overlayOpen;
        private long _overlayOpenedAtTicks;
        private int _watchdogKilled;

        public CaptureStandbySupervisor(Action onFallback)
        {
            _onFallback = onFallback;
            _current = this;
            SettingsRoot.Current.Capture.PropertyChanged += OnSettingsChanged;
            SettingsRoot.Current.Hotkeys.PropertyChanged += OnSettingsChanged;
            SettingsRoot.Current.General.PropertyChanged += OnSettingsChanged;
            _ = Task.Run(RunAsync);
        }

        /// <summary>
        /// Routes a shell-initiated capture (tray, menu) to the waiting standby process.
        /// True: handled — either sent, or dropped because an overlay is already up (matching
        /// how the capturer treats its own hotkeys). False: standby cannot take it right now
        /// (no supervisor, faulted, or between capture and READY) — the caller must run the
        /// classic one-shot capture instead. Nothing is ever queued: a request that cannot be
        /// satisfied promptly is either dropped as a duplicate or taken over by the fallback.
        /// </summary>
        public static bool TryCapture(CaptureMode mode)
        {
            var current = Volatile.Read(ref _current);
            return current != null && current.TrySendCapture(mode);
        }

        private bool TrySendCapture(CaptureMode mode)
        {
            // serialized before the CAS so no failure can strand _overlayOpen at 1
            var payload = JsonSerializer.Serialize(new
            {
                type = "capture",
                mode = mode.ToString().ToLowerInvariant(),
            });
            if (Interlocked.CompareExchange(ref _overlayOpen, 1, 0) != 0)
                return true;
            // ticks after winning the CAS only — a losing click must not refresh a hung
            // overlay's watchdog deadline. The ns-wide stale-stamp window this leaves is
            // covered by the watchdog requiring two consecutive expired samples.
            Volatile.Write(ref _overlayOpenedAtTicks, DateTime.UtcNow.Ticks);
            lock (_stdinGate)
            {
                if (SendLocked(payload, grantForeground: true))
                    return true;
            }
            Volatile.Write(ref _overlayOpen, 0);
            return false;
        }

        private void OnSettingsChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) => SendSettings();

        /// <summary>Backstop wrapper: nothing may escape the supervise loop silently — an
        /// unread stdout pipe would hang the child and lose captures with no one watching.</summary>
        private async Task RunAsync()
        {
            try
            {
                await SuperviseAsync();
            }
            catch (Exception ex)
            {
                try { SentryConfig.CaptureHandled(ex, "capture.standby-supervisor-crash"); }
                catch { }
                try { if (Volatile.Read(ref _process) is { HasExited: false } orphan) orphan.Kill(); }
                catch { }
                Fallback("supervisor crashed: " + ex.Message);
            }
        }

        private async Task SuperviseAsync()
        {
            int failures = 0;
            while (!_stop.IsCancellationRequested)
            {
                string sessionDir = null;
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                Process process = null;
                using var processExited = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                try
                {
                    if (!MacPermissions.HasScreenRecording)
                    {
                        // The one-shot path owns the whole permission conversation (prompt,
                        // dialog, retry per attempt); standby must not spawn a capturer that
                        // can only see a black desktop.
                        Fallback("screen recording permission not granted");
                        return;
                    }

                    var binary = CaptureBinaryLocator.Resolve();
                    if (binary == null)
                    {
                        // one-shot shows its "binary missing" dialog on the next attempt
                        Fallback("capture binary not found");
                        return;
                    }

                    var psi = new ProcessStartInfo(binary)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        // Keeping this pipe open is the child's parent-lifetime lease. If Clowd.UI
                        // terminates for any reason, EOF tells the standby event loop to exit.
                        RedirectStandardInput = true,
                        // The protocol round-trips filesystem paths through these pipes; .NET
                        // otherwise defaults them to the OEM codepage, which corrupts any
                        // non-ASCII profile path and silently loses the capture. BOM-less on
                        // stdin: a StreamWriter preamble would corrupt the first JSON line.
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        StandardInputEncoding = new UTF8Encoding(false),
                        WorkingDirectory = Path.GetDirectoryName(binary),
                    };
                    foreach (var arg in CaptureArguments.BuildStandby(PathConstants.SessionData,
                                 SettingsRoot.Current.Capture, SettingsRoot.Current.Hotkeys,
                                 SettingsRoot.Current.General.LastSavePath))
                        psi.ArgumentList.Add(arg);

                    process = Process.Start(psi)
                        ?? throw new InvalidOperationException("Failed to start standby capturer");
                    var startedUtc = DateTime.UtcNow;
                    lock (_stdinGate)
                        _process = process;
                    // Dekker pair with Dispose (Cancel then Kill-via-_process): a Dispose
                    // that ran before the publish above saw null and killed nothing, so
                    // re-check here — otherwise this child leaks alive, holding the native
                    // hotkey registrations, with the runner parked in ReadLineAsync forever.
                    if (_stop.IsCancellationRequested)
                    {
                        try { process.Kill(); }
                        catch { }
                        return;
                    }

                    var stderrTask = DrainAsync(process.StandardError, stderr);
                    _ = WatchOverlayHangAsync(process, processExited.Token);
                    while (await process.StandardOutput.ReadLineAsync() is { } line)
                    {
                        Append(stdout, line);
                        Debug.WriteLine("[CaptureStandby] " + line);
                        if (line == ReadyLine)
                        {
                            // READY means no overlay is in progress, and stdout is ordered —
                            // this self-heals any stuck-open gate at the next cycle. Cleared
                            // before _ready so a racing tray click can't slip a duplicate in.
                            Interlocked.Exchange(ref _overlayOpen, 0);
                            Volatile.Write(ref _ready, 1);
                            // off the reader thread: writing stdin from the stdout loop is the
                            // classic bidirectional-pipe deadlock shape
                            _ = Task.Run(() => SendSettings(force: true));
                        }
                        else if (line.StartsWith(SessionPrefix, StringComparison.Ordinal))
                        {
                            sessionDir = line.Substring(SessionPrefix.Length);
                            // overlay flag before clearing _ready: a TryCapture landing
                            // between the two must read as "duplicate, drop" rather than
                            // "not ready" and fall through to a one-shot spawn on top of
                            // the overlay that is opening right now
                            Volatile.Write(ref _overlayOpenedAtTicks, DateTime.UtcNow.Ticks);
                            Interlocked.Exchange(ref _overlayOpen, 1);
                            Volatile.Write(ref _ready, 0);
                            IdleMonitor.NotifyCaptureActivity();
                        }
                        else if (line.StartsWith(CaptureFinishedPrefix, StringComparison.Ordinal))
                        {
                            var finished = line.Substring(CaptureFinishedPrefix.Length);
                            sessionDir = null;
                            failures = 0;
                            Interlocked.Exchange(ref _overlayOpen, 0);
                            IdleMonitor.NotifyCaptureActivity();
                            Dispatcher.UIThread.Post(async () => await ScreenCapturePage.ProcessStandbySessionAsync(finished));
                        }
                        else if (line.StartsWith(SettingsStatusPrefix, StringComparison.Ordinal))
                            ApplyHotkeyStatus(line.Substring(SettingsStatusPrefix.Length));
                        else if (line.StartsWith(IpcErrorPrefix, StringComparison.Ordinal))
                        {
                            // A NACK (bad message, unknown mode, session-dir create failure):
                            // whatever we optimistically marked open is not coming, and a
                            // stuck-at-1 gate would swallow every later tray capture. Scoped
                            // to no-session so a NACK can never open the gate mid-overlay.
                            if (sessionDir == null)
                                Interlocked.Exchange(ref _overlayOpen, 0);
                        }
                    }
                    await process.WaitForExitAsync(_stop.Token);
                    await stderrTask;
                    if (_stop.IsCancellationRequested)
                        return;

                    // The standby process never exits on its own while the shell lives — any
                    // exit here is a failure of some kind. Uptime does not forgive a
                    // watchdog kill: a capturer that hangs on every capture always runs
                    // "stably" past the threshold first, and forgiving it would trade
                    // permanent 30-minute hangs against ever reaching the fallback.
                    if (Interlocked.Exchange(ref _watchdogKilled, 0) == 0 && DateTime.UtcNow - startedUtc > StableUptime)
                        failures = 0;
                    failures++;
                    var captureLog = TryReadCaptureLog(sessionDir);

                    if (process.ExitCode == ScreenCapturePage.ExitCodeNoScreenPermission)
                    {
                        // Not a crash: TCC revoked the permission under us. One-shot mode
                        // re-runs the permission conversation on the next capture attempt.
                        Fallback("screen recording permission revoked");
                        return;
                    }

                    Debug.WriteLine($"Standby capturer exited with code {process.ExitCode}");
                    ReportCrash(process.ExitCode, failures, stdout.ToString(), stderr.ToString(), captureLog);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failures++;
                    Debug.WriteLine("Standby capturer failed: " + ex);
                    ex.Data["consecutive_failures"] = failures;
                    SentryConfig.AttachProcessLog(ex, ("stdout", stdout.ToString()), ("stderr", stderr.ToString()));
                    SentryConfig.CaptureHandled(ex, "capture.standby-supervisor");
                }
                finally
                {
                    processExited.Cancel();
                    Volatile.Write(ref _ready, 0);
                    Interlocked.Exchange(ref _overlayOpen, 0);
                    lock (_stdinGate)
                        _process = null;
                    process?.Dispose();
                    // any session not announced as FINISHED is a partial the shell owns
                    // deleting, on every exit path (crash, cancellation, escape)
                    if (sessionDir != null)
                        CaptureSessionDispatcher.DeleteSessionDir(sessionDir);
                }

                if (failures >= MaxConsecutiveFailures)
                {
                    Fallback($"{failures} consecutive standby failures");
                    return;
                }
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * failures), _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>The only self-heal for a hung-but-alive capturer: if the overlay has
        /// supposedly been open far longer than any real capture, kill the process — the
        /// normal crash handling then cleans up, restarts, or falls back. Without this, a
        /// wedged child swallows every tray click and blocks updates until app restart.
        /// Two consecutive expired samples are required so the instant between winning the
        /// overlay CAS and stamping its timestamp can never kill a healthy process.</summary>
        private async Task WatchOverlayHangAsync(Process process, CancellationToken token)
        {
            try
            {
                int expiredSamples = 0;
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), token);
                    var openedAt = new DateTime(Volatile.Read(ref _overlayOpenedAtTicks), DateTimeKind.Utc);
                    if (Volatile.Read(ref _overlayOpen) != 1 || DateTime.UtcNow - openedAt < OverlayHangTimeout)
                    {
                        expiredSamples = 0;
                        continue;
                    }
                    if (++expiredSamples < 2)
                        continue;
                    Debug.WriteLine("Standby capturer overlay unresponsive; killing process");
                    // kill first: a hang must be broken even where telemetry itself throws
                    Interlocked.Exchange(ref _watchdogKilled, 1);
                    try { process.Kill(); }
                    catch { }
                    try
                    {
                        SentryConfig.CaptureHandled(
                            new TimeoutException($"Standby capturer overlay open for over {OverlayHangTimeout.TotalMinutes:0} minutes; killed"),
                            "capture.standby-overlay-hang");
                    }
                    catch { }
                    return;
                }
            }
            catch
            {
                // best-effort watchdog: cancellation (normal) or a disposed token source
                // racing process exit — either way the supervise loop owns cleanup
            }
        }

        /// <summary>Hands capture duty back to the shell's one-shot path, at most once.</summary>
        private void Fallback(string reason)
        {
            Debug.WriteLine("Standby capturer falling back to one-shot capture: " + reason);
            var callback = Interlocked.Exchange(ref _onFallback, null);
            try
            {
                callback?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Standby fallback callback failed: " + ex);
            }
        }

        private static void Append(StringBuilder buffer, string line)
        {
            buffer.AppendLine(line);
            if (buffer.Length > MaxPipeChars)
                buffer.Remove(0, buffer.Length - MaxPipeChars);
        }

        private static async Task DrainAsync(StreamReader reader, StringBuilder buffer)
        {
            while (await reader.ReadLineAsync() is { } line)
                Append(buffer, line);
        }

        private void ApplyHotkeyStatus(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var hotkeys = document.RootElement.GetProperty("hotkeys");
                var statuses = new[]
                {
                    Read("main", HotkeyId.CaptureRegion),
                    Read("window", HotkeyId.CaptureActive),
                    Read("monitor", HotkeyId.CaptureFullscreen),
                };
                Dispatcher.UIThread.Post(() =>
                {
                    // a status drained from a dead process's pipe after fallback (or from a
                    // superseded supervisor after a warm toggle) must not stomp the statuses
                    // of whoever owns the hotkeys now
                    if (!ReferenceEquals(Volatile.Read(ref _current), this))
                        return;
                    var manager = HotkeyManager.Current;
                    if (manager == null)
                        return;
                    foreach (var (id, active, error) in statuses)
                        manager.GetEntry(id).SetStatus(active, error);
                });

                (HotkeyId Id, bool Active, string Error) Read(string name, HotkeyId id)
                {
                    var status = hotkeys.GetProperty(name);
                    var errorElement = status.GetProperty("error");
                    return (id, status.GetProperty("active").GetBoolean(),
                        errorElement.ValueKind == JsonValueKind.String ? errorElement.GetString() : "");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to parse standby hotkey status: " + ex);
            }
        }

        private void SendSettings(bool force = false)
        {
            try
            {
                // built INSIDE the gate so lock order equals snapshot order: a forced READY
                // send racing a PropertyChanged send must not put older settings on the
                // wire last (the pipe write already sits under this lock anyway)
                lock (_stdinGate)
                {
                    var args = CaptureArguments.BuildStandby(PathConstants.SessionData,
                        SettingsRoot.Current.Capture, SettingsRoot.Current.Hotkeys,
                        SettingsRoot.Current.General.LastSavePath);
                    var fingerprint = JsonSerializer.Serialize(args);
                    if (!force && fingerprint == _lastSettings)
                        return;
                    _lastSettings = fingerprint;
                    var payload = JsonSerializer.Serialize(new
                    {
                        type = "settings",
                        args,
                    });
                    // A send that misses (capture in progress, process restarting) needs no
                    // queue: every READY is answered with a forced full snapshot.
                    SendLocked(payload);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to send standby settings: " + ex);
            }
        }

        private bool SendLocked(string payload, bool grantForeground = false)
        {
            try
            {
                if (Volatile.Read(ref _ready) == 0 || _process is not { HasExited: false } process)
                    return false;
                // The overlay needs keyboard focus immediately (Esc / shortcuts), and a
                // process poked from the background is denied SetForegroundWindow by the
                // foreground lock. Best-effort, exactly like the one-shot spawn: it works
                // because the user just clicked our tray/menu, so we hold the rights.
                if (grantForeground && OperatingSystem.IsWindows())
                    AllowSetForegroundWindow(process.Id);
                process.StandardInput.WriteLine(payload);
                process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to write standby IPC: " + ex);
                Volatile.Write(ref _ready, 0);
                return false;
            }
        }

        private static string TryReadCaptureLog(string sessionDir)
        {
            try
            {
                if (String.IsNullOrEmpty(sessionDir))
                    return null;
                var path = Path.Combine(sessionDir, "capture.log");
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch { return null; }
        }

        private static void ReportCrash(int exitCode, int failures, string stdout, string stderr, string captureLog)
        {
            var crash = new InvalidOperationException(failures >= MaxConsecutiveFailures
                ? "Standby capturer disabled after consecutive crashes"
                : "Standby capturer exited unexpectedly");
            crash.Data["exit_code"] = exitCode;
            crash.Data["consecutive_failures"] = failures;
            SentryConfig.AttachProcessLog(crash, ("stdout", stdout), ("stderr", stderr), ("capture.log", captureLog));
            SentryConfig.CaptureHandled(crash, failures >= MaxConsecutiveFailures
                ? "capture.standby-consecutive-crashes"
                : "capture.standby-process-crash");
        }

        public void Dispose()
        {
            SettingsRoot.Current.Capture.PropertyChanged -= OnSettingsChanged;
            SettingsRoot.Current.Hotkeys.PropertyChanged -= OnSettingsChanged;
            SettingsRoot.Current.General.PropertyChanged -= OnSettingsChanged;
            Interlocked.CompareExchange(ref _current, null, this);
            _onFallback = null;
            _stop.Cancel();
            // Kill WITHOUT _stdinGate: a writer stuck in SendLocked on a full pipe would
            // hold the lock, and this Kill is the only thing that can unstick it — taking
            // the lock here would deadlock the UI thread. The runner observes the
            // kill/cancel and winds itself down (waiting for it here would stall the UI
            // thread during shutdown or a warm-setting toggle); the Process object itself
            // is disposed by the runner's finally.
            try
            {
                if (Volatile.Read(ref _process) is { HasExited: false } process)
                    process.Kill();
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
    }
}
