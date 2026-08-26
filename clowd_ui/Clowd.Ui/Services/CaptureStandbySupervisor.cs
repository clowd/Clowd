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
    /// <summary>Runs the capturer in its lightweight cross-platform hotkey-waiting mode.</summary>
    internal sealed class CaptureStandbySupervisor : IDisposable
    {
        private const int MaxStdoutChars = 256 * 1024;
        private const string SessionPrefix = "CLOWD_SESSION ";
        private const string CaptureFinishedPrefix = "CLOWD_CAPTURE_FINISHED ";
        private const string SettingsStatusPrefix = "CLOWD_SETTINGS_STATUS ";
        internal static bool IsReady => Volatile.Read(ref _ready) != 0;
        private static int _ready;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _permissionChanged = new(0, 1);
        private readonly Task _runner;
        private readonly object _stdinGate = new();
        private Process _process;
        private string _lastSettings;
        private string _pendingCapture;
        private long _settingsRevision;
        private long _lastStatusRevision;
        private int _captureInFlight;

        private static CaptureStandbySupervisor _current;

        public CaptureStandbySupervisor()
        {
            _current = this;
            SettingsRoot.Current.Capture.PropertyChanged += OnSettingsChanged;
            SettingsRoot.Current.Hotkeys.PropertyChanged += OnSettingsChanged;
            SettingsRoot.Current.General.PropertyChanged += OnSettingsChanged;
            MacPermissions.StateChanged += OnPermissionChanged;
            _runner = Task.Run(RunAsync);
        }

        public static bool TryCapture(CaptureMode mode)
        {
            var current = Volatile.Read(ref _current);
            if (current == null || !MacPermissions.HasScreenRecording)
                return false;
            return current.TrySendCapture(new
            {
                type = "capture",
                mode = mode.ToString().ToLowerInvariant(),
            });
        }

        private void OnSettingsChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) => SendSettings();

        private void OnPermissionChanged(object sender, EventArgs e)
        {
            if (!MacPermissions.HasScreenRecording)
                return;
            try { _permissionChanged.Release(); }
            catch (SemaphoreFullException) { }
        }

        private async Task RunAsync()
        {
            int failures = 0;
            while (!_stop.IsCancellationRequested && failures < 5)
            {
                if (!MacPermissions.HasScreenRecording)
                {
                    await _permissionChanged.WaitAsync(_stop.Token);
                    continue;
                }

                string sessionDir = null;
                var stdout = new StringBuilder();
                try
                {
                    var binary = CaptureBinaryLocator.Resolve();
                    if (binary == null)
                        return;

                    var psi = new ProcessStartInfo(binary)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        // Keeping this pipe open is the child's parent-lifetime lease. If Clowd.UI
                        // terminates for any reason, EOF tells the standby event loop to exit.
                        RedirectStandardInput = true,
                        WorkingDirectory = Path.GetDirectoryName(binary),
                    };
                    foreach (var arg in CaptureArguments.BuildStandby(PathConstants.SessionData,
                                 SettingsRoot.Current.Capture, SettingsRoot.Current.Hotkeys,
                                 SettingsRoot.Current.General.LastSavePath))
                        psi.ArgumentList.Add(arg);

                    var process = Process.Start(psi);
                    if (process == null)
                        throw new InvalidOperationException("Failed to start standby capturer");
                    _process = process;
                    var stderrBuffer = new StringBuilder();
                    var stderrTask = DrainStderrAsync(process.StandardError, stderrBuffer);
                    while (await process.StandardOutput.ReadLineAsync() is { } line)
                    {
                        AppendStdout(stdout, line);
                        Debug.WriteLine("[CaptureStandby] " + line);
                        if (line == "CLOWD_STANDBY_READY")
                        {
                            Volatile.Write(ref _ready, 1);
                            SendSettings(force: true);
                            lock (_stdinGate)
                            {
                                if (_pendingCapture != null && SendLocked(_pendingCapture))
                                    _pendingCapture = null;
                            }
                        }
                        else if (line.StartsWith(SessionPrefix, StringComparison.Ordinal))
                            sessionDir = line.Substring(SessionPrefix.Length);
                        else if (line.StartsWith("CLOWD_HOTKEY ", StringComparison.Ordinal))
                        {
                            Volatile.Write(ref _ready, 0);
                            Interlocked.Exchange(ref _captureInFlight, 1);
                            ScreenCapturePage.StandbyCaptureStarted();
                        }
                        else if (line.StartsWith(CaptureFinishedPrefix, StringComparison.Ordinal))
                        {
                            var finished = line.Substring(CaptureFinishedPrefix.Length);
                            sessionDir = null;
                            failures = 0;
                            Interlocked.Exchange(ref _captureInFlight, 0);
                            DispatchFinishedSession(finished);
                        }
                        else if (line.StartsWith(SettingsStatusPrefix, StringComparison.Ordinal))
                            ApplyHotkeyStatus(line.Substring(SettingsStatusPrefix.Length));
                    }
                    await process.WaitForExitAsync(_stop.Token);
                    await stderrTask;
                    var stderr = stderrBuffer.ToString();
                    _process = null;
                    Volatile.Write(ref _ready, 0);
                    Interlocked.Exchange(ref _captureInFlight, 0);
                    ScreenCapturePage.StandbyCaptureAborted();

                    if (process.ExitCode == 0 && sessionDir != null)
                    {
                        failures = 0;
                        var captured = sessionDir;
                        Dispatcher.UIThread.Post(async () => await ScreenCapturePage.ProcessStandbySessionAsync(captured));
                    }
                    else
                    {
                        failures++;
                        Debug.WriteLine($"Standby capturer exited with code {process.ExitCode}: {stderr}");
                        ReportFailure(process.ExitCode, failures, stdout.ToString(), stderr, TryReadCaptureLog(sessionDir),
                            failures >= 5 ? "Standby capturer stopped after consecutive crashes" :
                                            "Standby capturer exited unexpectedly");
                        if (sessionDir != null)
                            CaptureSessionDispatcher.DeleteSessionDir(sessionDir);
                    }
                    process.Dispose();
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Volatile.Write(ref _ready, 0);
                    Interlocked.Exchange(ref _captureInFlight, 0);
                    ScreenCapturePage.StandbyCaptureAborted();
                    failures++;
                    Debug.WriteLine("Standby capturer failed: " + ex);
                    ex.Data["consecutive_failures"] = failures;
                    SentryConfig.AttachProcessLog(ex, ("stdout", stdout.ToString()));
                    SentryConfig.CaptureHandled(ex, failures >= 5
                        ? "capture.standby-consecutive-crashes"
                        : "capture.standby-supervisor");
                }
            }
        }

        private static void AppendStdout(StringBuilder stdout, string line)
        {
            stdout.AppendLine(line);
            if (stdout.Length > MaxStdoutChars)
                stdout.Remove(0, stdout.Length - MaxStdoutChars);
        }

        private static async Task DrainStderrAsync(StreamReader reader, StringBuilder stderr)
        {
            while (await reader.ReadLineAsync() is { } line)
                AppendStdout(stderr, line);
        }

        private static void DispatchFinishedSession(string sessionDir)
        {
            Dispatcher.UIThread.Post(async () => await ScreenCapturePage.ProcessStandbySessionAsync(sessionDir));
        }

        private void ApplyHotkeyStatus(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var revision = root.GetProperty("revision").GetInt64();
                if (revision < Interlocked.Read(ref _lastStatusRevision))
                    return;
                Interlocked.Exchange(ref _lastStatusRevision, revision);

                var hotkeys = root.GetProperty("hotkeys");
                var statuses = new[]
                {
                    Read("main", HotkeyId.CaptureRegion),
                    Read("window", HotkeyId.CaptureActive),
                    Read("monitor", HotkeyId.CaptureFullscreen),
                };
                Dispatcher.UIThread.Post(() =>
                {
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
                var args = CaptureArguments.BuildStandby(PathConstants.SessionData,
                    SettingsRoot.Current.Capture, SettingsRoot.Current.Hotkeys,
                    SettingsRoot.Current.General.LastSavePath);
                var fingerprint = JsonSerializer.Serialize(args);
                lock (_stdinGate)
                {
                    if (!force && fingerprint == _lastSettings)
                        return;
                    _lastSettings = fingerprint;
                    var payload = JsonSerializer.Serialize(new
                    {
                        type = "settings",
                        revision = ++_settingsRevision,
                        args,
                    });
                    SendLocked(payload);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to send standby settings: " + ex);
            }
        }

        private void SendOrQueueCapture(object message)
        {
            var payload = JsonSerializer.Serialize(message);
            lock (_stdinGate)
            {
                if (!SendLocked(payload))
                    _pendingCapture = payload;
            }
        }

        private bool TrySendCapture(object message)
        {
            // Once accepted, repeated tray/button presses are intentionally
            // ignored until Rust reports that the overlay has finished.
            if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0)
                return true;
            SendOrQueueCapture(message);
            return true;
        }

        private bool SendLocked(string payload)
        {
            try
            {
                if (!IsReady || _process is not { HasExited: false } process)
                    return false;
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

        private static void ReportFailure(int exitCode, int failures, string stdout, string stderr,
                                          string captureLog, string message)
        {
            var crash = new InvalidOperationException(message);
            crash.Data["exit_code"] = exitCode;
            crash.Data["consecutive_failures"] = failures;
            SentryConfig.AttachProcessLog(crash, ("stdout", stdout), ("stderr", stderr), ("capture.log", captureLog));
            SentryConfig.CaptureHandled(crash, failures >= 5
                ? "capture.standby-consecutive-crashes"
                : "capture.standby-process-crash");
        }

        public void Dispose()
        {
            SettingsRoot.Current.Capture.PropertyChanged -= OnSettingsChanged;
            SettingsRoot.Current.Hotkeys.PropertyChanged -= OnSettingsChanged;
            SettingsRoot.Current.General.PropertyChanged -= OnSettingsChanged;
            MacPermissions.StateChanged -= OnPermissionChanged;
            Interlocked.CompareExchange(ref _current, null, this);
            Volatile.Write(ref _ready, 0);
            _stop.Cancel();
            try { if (_process is { HasExited: false }) _process.Kill(); } catch { }
            try { _runner.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _process?.Dispose();
            _permissionChanged.Dispose();
            _stop.Dispose();
        }
    }
}
