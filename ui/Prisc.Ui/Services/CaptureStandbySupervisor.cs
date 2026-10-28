using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Prisc.Config;
using Prisc.Ipc;
using Prisc.Util;
using IpcCaptureMode = Prisc.Ipc.CaptureMode;

namespace Prisc.UI
{
    /// <summary>
    /// Runs the capturer in its lightweight hotkey-waiting standby mode over the <c>capture</c>
    /// catalog (HELPER_PROTOCOL.md §4.5).
    /// Exists only while warm capture is in effect: created and disposed by
    /// <c>App.ConfigureCaptureHotkeys</c>, which owns the choice between this and the classic
    /// in-process hotkeys + one-shot path. When standby cannot work — missing permission or binary,
    /// permission-revoked exit, repeated crashes — the supervisor calls the fallback delegate
    /// exactly once and stops; the shell then reverts to one-shot capture for the rest of its
    /// process lifetime without touching the saved setting.
    /// </summary>
    internal sealed class CaptureStandbySupervisor : IDisposable
    {
        private const int MaxConsecutiveFailures = 3;

        /// <summary>The status <c>error_code</c> of a gesture the capturer cannot hook because
        /// macOS has not granted Accessibility. The capturer never prompts; the shell does.</summary>
        internal const string PermissionErrorCode = "permission";

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

        // whether the standby capturer's hook should be paused: true while the shell records a
        // gesture (HotkeyManager.IsRecording), so the combination being recorded is not caught
        // there as well. Re-sent after every `ready`, since a restarted capturer starts unpaused.
        private static volatile bool s_hotkeysPaused;

        /// <summary>True while the standby capturer's overlay is on screen (or an accepted
        /// request is about to put it there) — merged into ScreenCapturePage.IsCaptureActive
        /// for the IdleMonitor update gate.</summary>
        public static bool IsOverlayOpen =>
            Volatile.Read(ref _current) is { } current && Volatile.Read(ref current._overlayOpen) != 0;

        private readonly CancellationTokenSource _stop = new();
        private readonly object _stdinGate = new();
        private Action _onFallback;
        private HelperClient<CaptureEvent, CaptureCommand> _client;
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
            // the recording, upload and share modes decide the strip's VIDEO, UPLOAD and SHARE
            // buttons (--no-video / --no-upload / --no-share); everything else on those pages is
            // deduped away by the fingerprint in SendSettings.
            SettingsRoot.Current.Recording.PropertyChanged += OnSettingsChanged;
            SettingsRoot.Current.Uploads.PropertyChanged += OnSettingsChanged;
            SettingsRoot.Current.ShareRegion.PropertyChanged += OnSettingsChanged;
            _ = Task.Run(RunAsync);
        }

        /// <summary>
        /// Routes a shell-initiated capture (tray, menu) to the waiting standby process.
        /// True: handled, either sent or dropped because an overlay is already up (matching
        /// how the capturer treats its own hotkeys). False: standby cannot take it right now
        /// (no supervisor, faulted, or between a capture and the next <c>ready</c>), so the
        /// caller must run the classic one-shot capture instead. Nothing is ever queued: a request that cannot be
        /// satisfied promptly is either dropped as a duplicate or taken over by the fallback.
        /// </summary>
        public static bool TryCapture(CaptureMode mode)
        {
            var current = Volatile.Read(ref _current);
            return current != null && current.TrySendCapture(mode);
        }

        private bool TrySendCapture(CaptureMode mode)
        {
            // built before the CAS so no failure can strand _overlayOpen at 1
            var command = new CaptureCommand.Capture(mode switch
            {
                CaptureMode.Screen => IpcCaptureMode.Screen,
                CaptureMode.Window => IpcCaptureMode.Window,
                _ => IpcCaptureMode.Region,
            });
            if (Interlocked.CompareExchange(ref _overlayOpen, 1, 0) != 0)
                return true;
            // ticks after winning the CAS only — a losing click must not refresh a hung
            // overlay's watchdog deadline. The ns-wide stale-stamp window this leaves is
            // covered by the watchdog requiring two consecutive expired samples.
            Volatile.Write(ref _overlayOpenedAtTicks, DateTime.UtcNow.Ticks);
            lock (_stdinGate)
            {
                if (SendLocked(command, grantForeground: true))
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
                try { Volatile.Read(ref _client)?.Kill(); }
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
                HelperClient<CaptureEvent, CaptureCommand> client = null;
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

                    var binary = HelperLocator.Find("prisc_capture");
                    if (binary == null)
                    {
                        // one-shot shows its "binary missing" dialog on the next attempt
                        Fallback("capture binary not found");
                        return;
                    }

                    // The open stdin is the child's parent-lifetime lease: if Prisc.Ui terminates
                    // for any reason, EOF tells the standby event loop to exit.
                    // Not a tree kill: on Windows the browser an OCR search opens is this process's
                    // child when no browser was already running, and quitting Prisc, turning warm
                    // capture off or the hang watchdog must not close the user's browser (§7.4).
                    client = await HelperClient<CaptureEvent, CaptureCommand>.StartAsync(HelperSpecs.Capture, binary,
                        CaptureArguments.BuildStandby(PathConstants.SessionData, SettingsRoot.Current,
                            SettingsRoot.Current.General.LastSavePath),
                        new HelperStartOptions { KillEntireTree = false });
                    var startedUtc = DateTime.UtcNow;
                    lock (_stdinGate)
                        _client = client;
                    // Dekker pair with Dispose (Cancel then Kill-via-_client): a Dispose that ran
                    // before the publish above saw null and killed nothing, so re-check here;
                    // otherwise this child leaks alive, holding the native hotkey registrations.
                    if (_stop.IsCancellationRequested)
                    {
                        client.Kill();
                        return;
                    }

                    _ = WatchOverlayHangAsync(client, processExited.Token);
                    await foreach (var ev in client.Events.ReadAllAsync())
                    {
                        switch (ev)
                        {
                            case CaptureEvent.Ready:
                                // ready means no overlay is in progress, and events are ordered:
                                // this self-heals any stuck-open gate at the next cycle. Cleared
                                // before _ready so a racing tray click can't slip a duplicate in.
                                Interlocked.Exchange(ref _overlayOpen, 0);
                                Volatile.Write(ref _ready, 1);
                                // off the reader: a write that blocks on a full stdin pipe must
                                // never stall the loop that drains the child's stdout
                                _ = Task.Run(() =>
                                {
                                    SendSettings(force: true);
                                    SendPause();
                                });
                                break;

                            case CaptureEvent.Session session:
                                sessionDir = session.Dir;
                                // overlay flag before clearing _ready: a TryCapture landing
                                // between the two must read as "duplicate, drop" rather than
                                // "not ready" and fall through to a one-shot spawn on top of
                                // the overlay that is opening right now
                                Volatile.Write(ref _overlayOpenedAtTicks, DateTime.UtcNow.Ticks);
                                Interlocked.Exchange(ref _overlayOpen, 1);
                                Volatile.Write(ref _ready, 0);
                                IdleMonitor.NotifyCaptureActivity();
                                break;

                            case CaptureEvent.CaptureFinished finished:
                                sessionDir = null;
                                failures = 0;
                                Interlocked.Exchange(ref _overlayOpen, 0);
                                IdleMonitor.NotifyCaptureActivity();
                                var dir = finished.Dir;
                                Dispatcher.UIThread.Post(async () => await ScreenCapturePage.ProcessStandbySessionAsync(dir));
                                break;

                            case CaptureEvent.SettingsStatus status:
                                ApplyHotkeyStatus(status.Hotkeys);
                                break;

                            case CaptureEvent.Error { Fatal: false } error:
                                // A refusal (a capture while one is open, a rejected snapshot, a
                                // session-dir create failure): whatever we optimistically marked
                                // open is not coming, and a stuck-at-1 gate would swallow every
                                // later tray capture. Scoped to no-session so a refusal can never
                                // open the gate mid-overlay.
                                Debug.WriteLine($"Standby capturer refused a request: {error.Code}: {error.Message}");
                                if (sessionDir == null)
                                    Interlocked.Exchange(ref _overlayOpen, 0);
                                break;
                        }
                    }

                    var outcome = await client.Completion;
                    if (_stop.IsCancellationRequested)
                        return;

                    // A permission that went away, or a hook that could not come up at all, is
                    // not a crash and will not get better by restarting: one-shot mode owns that
                    // conversation from here.
                    if (outcome.Failure == HelperFailure.NoScreenPermission
                        || outcome.Error?.Code is IpcErrorCodes.Permission or IpcErrorCodes.InitFailed)
                    {
                        Fallback("standby capturer cannot run: " + outcome.Describe());
                        return;
                    }

                    // The standby process never exits on its own while the shell lives, so any
                    // exit here is a failure of some kind. Uptime does not forgive a watchdog
                    // kill: a capturer that hangs on every capture always runs "stably" past the
                    // threshold first, and forgiving it would trade permanent 30-minute hangs
                    // against ever reaching the fallback.
                    if (Interlocked.Exchange(ref _watchdogKilled, 0) == 0 && DateTime.UtcNow - startedUtc > StableUptime)
                        failures = 0;
                    failures++;

                    Debug.WriteLine($"Standby capturer ended: {outcome.Describe()}");
                    ReportCrash(outcome, failures, TryReadCaptureLog(sessionDir));
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
                    SentryConfig.CaptureHandled(ex, "capture.standby-supervisor");
                }
                finally
                {
                    processExited.Cancel();
                    Volatile.Write(ref _ready, 0);
                    Interlocked.Exchange(ref _overlayOpen, 0);
                    lock (_stdinGate)
                        _client = null;
                    if (client != null)
                        await client.DisposeAsync();
                    // any session not announced as finished is a partial the shell owns
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
        private async Task WatchOverlayHangAsync(HelperClient<CaptureEvent, CaptureCommand> client, CancellationToken token)
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
                    try { client.Kill(); }
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

        /// <summary>A capturer <c>settings_status</c>: each capture action's gesture statuses, and
        /// whether any of them is down for want of Accessibility.</summary>
        internal readonly record struct StandbyHotkeyStatus(
            (HotkeyId Id, ExternalGestureStatus[] Gestures)[] Actions, bool NeedsAccessibility);

        /// <summary>
        /// Reads a <c>settings_status</c>: each action has one status per <c>--hk-*</c> occurrence it
        /// received, in order, the gesture echoed exactly as sent. The shell acts on
        /// <c>error_code</c>, never on the display text, which prisc_hotkeys is free to reword.
        /// </summary>
        internal static StandbyHotkeyStatus ReadHotkeyStatus(CaptureHotkeys hotkeys)
        {
            var actions = new[]
            {
                Read(hotkeys?.Main, HotkeyId.CaptureRegion),
                Read(hotkeys?.Window, HotkeyId.CaptureActive),
                Read(hotkeys?.Monitor, HotkeyId.CaptureFullscreen),
            };
            return new StandbyHotkeyStatus(actions,
                actions.Any(a => a.Gestures.Any(g => g.ErrorCode == PermissionErrorCode)));

            static (HotkeyId Id, ExternalGestureStatus[] Gestures) Read(IReadOnlyList<CaptureHotkeyStatus> statuses, HotkeyId id) =>
                (id, (statuses ?? Array.Empty<CaptureHotkeyStatus>())
                    .Select(status => new ExternalGestureStatus(status.Gesture, status.Active, status.Error ?? "", status.ErrorCode))
                    .ToArray());
        }

        /// <summary>Asks for Accessibility when the capturer reports a gesture down for want of it —
        /// in warm-capture mode the shell may own no gesture at all, so its own hook never gets to
        /// ask. The capturer runs inside Prisc.app, so the grant covers it; it retries its hook by
        /// itself and reports again once the grant lands. A process macOS already trusts got a stale
        /// grant: the prompt would show nothing yet spend this run's one, which the grant missing
        /// for real (once the user removes Prisc from the list) still needs.</summary>
        /// <param name="isTrusted">AXIsProcessTrusted; never prompts.</param>
        internal static void PromptIfNeeded(StandbyHotkeyStatus status, AccessibilityPrompt prompt, Func<bool> isTrusted)
        {
            if (status.NeedsAccessibility && !isTrusted())
                prompt.RequestOnce();
        }

        /// <summary>
        /// Applies a <c>settings_status</c> (see <see cref="ReadHotkeyStatus"/>), which may also
        /// arrive unasked when the capturer's hook comes up or goes down. The manager matches
        /// statuses back to its own gestures by the echoed string, so a report for a snapshot that
        /// has since been superseded simply finds nothing to update for the gestures it did not
        /// know about.
        /// </summary>
        private void ApplyHotkeyStatus(CaptureHotkeys hotkeys)
        {
            try
            {
                var status = ReadHotkeyStatus(hotkeys);
                Dispatcher.UIThread.Post(() =>
                {
                    // a status drained from a dead process's pipe after fallback (or from a
                    // superseded supervisor after a warm toggle) must not stomp the statuses
                    // of whoever owns the hotkeys now
                    if (!ReferenceEquals(Volatile.Read(ref _current), this))
                        return;
                    // in warm mode the shell registers none of the screenshot gestures itself, so
                    // this report is its only sign that it should ask for Accessibility
                    PromptIfNeeded(status, AccessibilityPrompt.Shared, () => MacPermissions.HasAccessibility);
                    var manager = HotkeyManager.Current;
                    if (manager == null)
                        return;
                    foreach (var (id, gestures) in status.Actions)
                        manager.ApplyExternalStatus(id, gestures);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to apply standby hotkey status: " + ex);
            }
        }

        /// <summary>Pauses or resumes the standby capturer's hotkeys (best-effort: a capturer that
        /// is busy or restarting gets the current value with its next <c>ready</c>). Any thread.</summary>
        public static void SetHotkeysPaused(bool paused)
        {
            s_hotkeysPaused = paused;
            Volatile.Read(ref _current)?.SendPause();
        }

        private void SendPause()
        {
            try
            {
                lock (_stdinGate)
                    SendLocked(new CaptureCommand.Pause(s_hotkeysPaused));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to send standby pause: " + ex);
            }
        }

        private void SendSettings(bool force = false)
        {
            try
            {
                // built INSIDE the gate so lock order equals snapshot order: the forced send
                // after a `ready` racing a PropertyChanged send must not put older settings on the
                // wire last (the pipe write already sits under this lock anyway)
                lock (_stdinGate)
                {
                    var args = CaptureArguments.BuildStandby(PathConstants.SessionData, SettingsRoot.Current,
                        SettingsRoot.Current.General.LastSavePath);
                    var fingerprint = JsonSerializer.Serialize(args);
                    if (!force && fingerprint == _lastSettings)
                        return;
                    _lastSettings = fingerprint;
                    // A send that misses (capture in progress, process restarting) needs no
                    // queue: every ready is answered with a forced full snapshot.
                    SendLocked(new CaptureCommand.Settings(args));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to send standby settings: " + ex);
            }
        }

        private bool SendLocked(CaptureCommand command, bool grantForeground = false)
        {
            try
            {
                if (Volatile.Read(ref _ready) == 0 || _client is not { } client)
                    return false;
                // The overlay needs keyboard focus immediately (Esc / shortcuts), and a
                // process poked from the background is denied SetForegroundWindow by the
                // foreground lock. Best-effort, exactly like the one-shot spawn: it works
                // because the user just clicked our tray/menu, so we hold the rights.
                if (grantForeground)
                    HelperProcess.GrantForeground(client.ProcessId);
                if (client.Send(command))
                    return true;
                Volatile.Write(ref _ready, 0);
                return false;
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

        private static void ReportCrash(HelperOutcome<CaptureEvent> outcome, int failures, string captureLog)
        {
            var crash = new InvalidOperationException(failures >= MaxConsecutiveFailures
                ? "Standby capturer disabled after consecutive crashes"
                : "Standby capturer exited unexpectedly");
            crash.Data["exit_code"] = outcome.ExitCode;
            crash.Data["failure"] = outcome.Failure.ToString();
            crash.Data["error_code"] = outcome.Error?.Code;
            crash.Data["consecutive_failures"] = failures;
            SentryConfig.AttachProcessLog(crash, ("error", outcome.Error?.Message), ("stderr", outcome.StderrTail),
                ("capture.log", captureLog));
            SentryConfig.CaptureHandled(crash, failures >= MaxConsecutiveFailures
                ? "capture.standby-consecutive-crashes"
                : "capture.standby-process-crash");
        }

        public void Dispose()
        {
            SettingsRoot.Current.Capture.PropertyChanged -= OnSettingsChanged;
            SettingsRoot.Current.Hotkeys.PropertyChanged -= OnSettingsChanged;
            SettingsRoot.Current.General.PropertyChanged -= OnSettingsChanged;
            SettingsRoot.Current.Recording.PropertyChanged -= OnSettingsChanged;
            SettingsRoot.Current.Uploads.PropertyChanged -= OnSettingsChanged;
            SettingsRoot.Current.ShareRegion.PropertyChanged -= OnSettingsChanged;
            Interlocked.CompareExchange(ref _current, null, this);
            _onFallback = null;
            _stop.Cancel();
            // Kill WITHOUT _stdinGate: a writer stuck in SendLocked on a full pipe would
            // hold the lock, and this kill is the only thing that can unstick it; taking
            // the lock here would deadlock the UI thread. The runner observes the
            // kill/cancel and winds itself down (waiting for it here would stall the UI
            // thread during shutdown or a warm-setting toggle) and disposes the client in its
            // finally.
            try { Volatile.Read(ref _client)?.Kill(); }
            catch { }
        }
    }
}
