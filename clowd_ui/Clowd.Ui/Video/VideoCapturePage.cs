using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>
    /// Orchestrates a screen-recording session (DESIGN §4.2): hosts the <see cref="ObsCapturer"/>
    /// process, shows the click-through <see cref="BorderWindow"/> and the
    /// <see cref="FloatingToolbarWindow"/>, and creates the recents session when the recording
    /// finishes (§4.5). Window-less <see cref="IPage"/> like <see cref="ScreenCapturePage"/>;
    /// single-instance via <see cref="ActiveInstance"/> (UI thread only). Every async void entry
    /// point wraps its awaits in try/catch routing to the CriticalError path — an unhandled
    /// exception in async void kills the process.
    /// </summary>
    internal sealed class VideoCapturePage : IVideoCapturePage
    {
        /// <summary>The currently open recording session, if any. UI thread only.</summary>
        internal static VideoCapturePage ActiveInstance { get; private set; }

        public bool IsRecording { get; private set; }

        public event EventHandler Closed;

        // set once obs-express has emitted "initialized" — Start/Toggle are no-ops before that
        // (the WAIT state, §4.2/F6): an ungated "start" pre-initialized combined with a slow
        // first-run OBS init would time out inside an async void.
        private bool _initialized;
        private bool _initializing;
        private bool _starting;
        private bool _finishing;
        private bool _closing;
        private bool _closedRaised;

        // a CLI-mapped setting changed before recording began, so the pending process was torn
        // down: the primary button is now RESTART and re-spawns the capturer (§4.2).
        private bool _restartRequired;
        // the same change arriving mid-init, applied once InitializeCapturerAsync returns —
        // killing a process that is still building its pipeline races its own initialization.
        private bool _settingsChangedDuringInit;

        private ObsCapturer _obs;
        // shutdown of the process a settings change invalidated — awaited before the replacement
        // spawns (both write the same video.mp4).
        private Task _pendingShutdown;
        private BorderWindow _border;
        private FloatingToolbarWindow _toolbar;
        private SettingsRecording _settings;
        private ScreenRect _region;
        private string _binaryPath;
        private string _sessionDir;
        private string _outputMp4;
        private TimeSpan _lastStatusElapsed;
        private int _statusCount;

        public async void Open(ScreenRect region, string sessionDir)
        {
            try
            {
                Dispatcher.UIThread.VerifyAccess();

                if (ActiveInstance != null)
                {
                    // a recording session is already active — ignore this request (§4.2). The
                    // freshly-captured dir would otherwise leak: nothing will ever use it.
                    Debug.WriteLine("A recording session is already active; ignoring new video capture.");
                    DeleteDirectory(sessionDir);
                    RaiseClosed();
                    return;
                }

                ActiveInstance = this;
                _region = region;
                _settings = SettingsRoot.Current.Recording;
                _sessionDir = sessionDir;
                _outputMp4 = Path.Combine(sessionDir, "video.mp4");

                var binary = ObsBinaryLocator.Resolve();
                if (binary == null)
                {
                    await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error,
                        $"The recording binary ({ObsBinaryLocator.BinaryFileName}) could not be found. " +
                        $"Run 'cargo build' in the obs-express-rs repository, or set the {ObsBinaryLocator.EnvVarName} " +
                        "environment variable to its location.",
                        "Screen recording unavailable");
                    DeleteDirectory(_sessionDir);
                    Close();
                    return;
                }

                Debug.WriteLine("Resolved recording binary: " + binary);
                _binaryPath = binary;

                _border = new BorderWindow(region);
                _border.SetOverlayText("WAIT…");
                _border.Show();

                _toolbar = new FloatingToolbarWindow();
                _toolbar.StartClicked += (s, e) => StartRecording();
                _toolbar.FinishClicked += (s, e) => FinishRecording();
                _toolbar.CancelClicked += (s, e) => Cancel();
                _toolbar.SettingsClicked += (s, e) => PageManager.Current.GetSettingsPage().Open(SettingsPageTab.SettingsRecording);
                // live mutes only — the toolbar itself persists the toggle settings.
                _toolbar.MicToggled += (s, enabled) => _obs?.SetMicrophoneMute(!enabled);
                _toolbar.SpeakerToggled += (s, enabled) => _obs?.SetSpeakerMute(!enabled);
                _toolbar.SetPrimaryText("WAIT…");
                _toolbar.ShowNear(region);

                // subscribed before the first spawn so a change made during WAIT is not lost.
                _settings.PropertyChanged += OnRecordingSettingChanged;

                await InitializeCapturerAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to open recording session: " + ex);
                SentryConfig.CaptureHandled(ex, "video.open");
                if (!_closing)
                    OnCriticalError(this, ex.Message);
            }
        }

        /// <summary>
        /// Spawns obs-express with the current recording settings and waits for it to report
        /// ready. Also the RESTART path: <see cref="ObsArguments"/> bakes every CLI-mapped setting
        /// in at spawn time, so a settings change before recording starts tears the process down
        /// (<see cref="InvalidateCapturer"/>) and this rebuilds it with the new values.
        /// </summary>
        private async Task InitializeCapturerAsync()
        {
            _initializing = true;
            _initialized = false;
            _restartRequired = false;
            _settingsChangedDuringInit = false;
            SetPrimaryText("WAIT…");

            try
            {
                // the invalidated process holds video.mp4 open until it exits; spawning its
                // replacement first would race it for the file.
                await AwaitPendingShutdownAsync();

                // Cancel/quit during that wait has already deleted the session directory and
                // disposed nothing (there is no process yet) — do not spawn one into it.
                if (_closing)
                    return;

                _obs = new ObsCapturer();
                _obs.CriticalError += OnCriticalError;
                _obs.StatusReceived += OnStatusReceived;

                await _obs.InitializeAsync(ObsArguments.Build(_region, _outputMp4, _settings), _binaryPath);
            }
            finally
            {
                _initializing = false;
            }

            if (_closing)
                return;

            _initialized = true;
            SetPrimaryText("START");

            // a change that landed while the pipeline was being built cannot be applied to it —
            // tear the process back down rather than record with settings the user moved off.
            if (_settingsChangedDuringInit)
                InvalidateCapturer();
        }

        /// <summary>Starts the recording. No-op unless initialized and not already recording
        /// (the WAIT gate, §4.2/F6). In the RESTART state it re-spawns the capturer instead —
        /// reload and start stay separate clicks (WPF parity), so a mis-click cannot begin
        /// recording while the user is still adjusting settings.</summary>
        public async void StartRecording()
        {
            try
            {
                if (IsRecording || _starting || _initializing || _closing)
                    return;

                if (_restartRequired)
                {
                    await InitializeCapturerAsync();
                    return;
                }

                if (!_initialized)
                    return;
                _starting = true;

                // the CLI always passes the device args; the Capture* toggles are runtime mutes.
                _obs.SetSpeakerMute(!_settings.CaptureSpeaker);
                _obs.SetMicrophoneMute(!_settings.CaptureMicrophone);

                // clear the overlay BEFORE writing "start": started_recording means frames are
                // already flowing — text cleared after would be captured in the first frames.
                _border.SetOverlayText(null);

                await _obs.StartAsync();

                if (_closing)
                    return;

                IsRecording = true;
                _toolbar.SetRecordingState(true);
                // the drag handle keeps its "DRAG ME" label until the first status arrives (WPF
                // parity) — OnStatusReceived then drives the FPS/timer alternation.
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to start recording: " + ex);
                SentryConfig.CaptureHandled(ex, "video.start");
                if (!_closing)
                    OnCriticalError(this, ex.Message);
            }
            finally
            {
                _starting = false;
            }
        }

        /// <summary>Stops the recording, creates the recents session (§4.5) and closes.</summary>
        public async void FinishRecording()
        {
            try
            {
                if (!IsRecording || _finishing || _closing)
                    return;
                _finishing = true;

                HideWindows();

                if (!await _obs.StopAsync())
                    return; // CriticalError has already been raised; its handler owns cleanup

                if (_closing)
                    return;
                _closing = true;

                CreateSession();

                if (SettingsRoot.Current.Recording.OpenRecentsWhenFinished)
                    PageManager.Current.GetSettingsPage().Open(SettingsPageTab.RecentSessions);

                // this is a tray app with no MainWindow: when recents is disabled and nothing
                // else is open, there is no window to host the toast and the user would get
                // zero save feedback — open recents as the guaranteed feedback surface.
                var host = Toast.GetActiveOrMainWindow();
                if (host == null)
                {
                    PageManager.Current.GetSettingsPage().Open(SettingsPageTab.RecentSessions);
                    host = Toast.GetActiveOrMainWindow();
                }

                Toast.Show(host, "Recording saved");

                await ShutdownCapturersAsync();
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to finish recording: " + ex);
                SentryConfig.CaptureHandled(ex, "video.finish");
                if (!_closing)
                    OnCriticalError(this, ex.Message);
            }
        }

        /// <summary>Aborts the session: stops any active recording, deletes the session
        /// directory and closes.</summary>
        public async void Cancel()
        {
            try
            {
                if (_closing)
                    return;
                _closing = true; // also suppresses CriticalError from the stop below

                HideWindows();

                if (IsRecording)
                {
                    try
                    {
                        await _obs.StopAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Error stopping recording during cancel: " + ex.Message);
                        SentryConfig.CaptureHandled(ex, "video.cancel-stop");
                    }
                }

                // pre-start: writes "quit" → cancel-before-start, exit 0 (§1.2). Awaited so the
                // directory delete below cannot race the process's open file handles, without
                // ever blocking the UI thread (the old sync Dispose froze the app for up to 5 s
                // when cancelling during WAIT).
                await ShutdownCapturersAsync();

                DeleteDirectory(_sessionDir);
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error cancelling recording session: " + ex);
                SentryConfig.CaptureHandled(ex, "video.cancel");
                Close();
            }
        }

        /// <summary>Hotkey entry point (Start/Stop Recording): finish if recording, start (or
        /// re-spawn a capturer invalidated by a settings change) if the button would be
        /// actionable, ignore during WAIT (§4.2/F6 — WPF parity).</summary>
        public void Toggle()
        {
            try
            {
                if (IsRecording)
                    FinishRecording();
                else if (_initialized || _restartRequired)
                    StartRecording();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error toggling recording: " + ex);
                SentryConfig.CaptureHandled(ex, "video.toggle");
            }
        }

        /// <summary>
        /// App-exit path: without this, exiting Clowd during an active recording lets obs-express
        /// flush a valid video.mp4 (stdin EOF == quit, §1.2) but no session.json is ever written,
        /// so the recording is invisible in Recents and the dir leaks forever. Stops the recording
        /// (bounded by the capturer's stop timeout), registers the session — keeping a partial
        /// video if the stop failed — and never shows UI (the app is exiting).
        /// </summary>
        internal async Task ShutdownAsync()
        {
            if (_closing)
                return;
            _closing = true; // suppresses OnCriticalError dialogs; cleanup is handled inline

            HideWindows();

            try
            {
                bool stoppedCleanly = false;
                if (IsRecording)
                {
                    try
                    {
                        stoppedCleanly = await _obs.StopAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Error stopping recording during app exit: " + ex.Message);
                        SentryConfig.CaptureHandled(ex, "video.exit-stop");
                    }
                }

                await ShutdownCapturersAsync();

                if (IsRecording && (stoppedCleanly || HasPartialVideo()))
                    CreateSession();
                else
                    DeleteDirectory(_sessionDir); // WAIT state: nothing was recorded
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error shutting down recording session: " + ex);
                SentryConfig.CaptureHandled(ex, "video.shutdown");
            }

            Close();
        }

        public void Close()
        {
            _closing = true;

            if (ReferenceEquals(ActiveInstance, this))
                ActiveInstance = null;

            if (_settings != null)
                _settings.PropertyChanged -= OnRecordingSettingChanged;

            try { _border?.Close(); }
            catch { }
            try { _toolbar?.Close(); }
            catch { }
            _border = null;
            _toolbar = null;

            RaiseClosed();
        }

        /// <summary>
        /// A recording setting changed while this session is open (from the settings page the
        /// OPTIONS button opens, or from the toolbar's own toggles). Mutes are applied live;
        /// anything that reaches the obs-express command line invalidates the pending process.
        /// </summary>
        private void OnRecordingSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            // once frames are flowing the CLI is fixed for the life of the file — the change
            // applies to the next recording (the settings page says as much).
            if (_closing || IsRecording || _starting)
                return;

            if (e.PropertyName is nameof(SettingsRecording.CaptureMicrophone) or nameof(SettingsRecording.CaptureSpeaker))
            {
                // runtime mutes: the settings page and the toolbar buttons behave identically.
                _obs?.SetMicrophoneMute(!_settings.CaptureMicrophone);
                _obs?.SetSpeakerMute(!_settings.CaptureSpeaker);
                return;
            }

            if (!ObsArguments.RequiresRestart(e.PropertyName))
                return;

            InvalidateCapturer();
        }

        /// <summary>
        /// Tears the pending (initialized but not recording) capturer down after a CLI-mapped
        /// setting changed, and turns START into RESTART — obs-express reads fps, quality, scaling,
        /// hw-accel, cursor and the audio devices once at spawn time, so the live process would
        /// otherwise record with the values the user just replaced (WPF parity: the old
        /// ObsViewWrapper.Invalidate/MustReload pair). Nothing has been recorded yet, so the only
        /// cost is the re-init the user triggers with the next click.
        /// </summary>
        private async void InvalidateCapturer()
        {
            try
            {
                if (_closing || IsRecording || _starting)
                    return;

                if (_initializing)
                {
                    // the pipeline is still being built; killing it here races its own
                    // initialization. InitializeCapturerAsync re-enters this on completion.
                    _settingsChangedDuringInit = true;
                    return;
                }

                if (!_initialized)
                    return; // already torn down — a RESTART is pending

                _initialized = false;
                _restartRequired = true;
                SetPrimaryText("RESTART", restart: true);

                // detached before disposal: this process's exit must not reach the page's
                // critical-error path, and its statuses must never drive the replacement's UI.
                var obs = _obs;
                _obs = null;
                if (obs != null)
                {
                    obs.CriticalError -= OnCriticalError;
                    obs.StatusReceived -= OnStatusReceived;
                    _pendingShutdown = obs.DisposeAsync();
                    await AwaitPendingShutdownAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to invalidate the pending recording process: " + ex);
                SentryConfig.CaptureHandled(ex, "video.invalidate");
            }
        }

        /// <summary>Shuts the current capturer down AND waits out a process an in-flight
        /// <see cref="InvalidateCapturer"/> is still killing — both hold the session's video.mp4
        /// open, so measuring or deleting that file before this completes races a live
        /// obs-express.</summary>
        private async Task ShutdownCapturersAsync()
        {
            if (_obs != null)
                await _obs.DisposeAsync();

            await AwaitPendingShutdownAsync();
        }

        /// <summary>Waits for the invalidated process (if any) to exit. Read into a local first:
        /// <see cref="InvalidateCapturer"/> and <see cref="ShutdownCapturersAsync"/> can both be
        /// in flight, and the loser must not await a field the winner has already cleared.</summary>
        private async Task AwaitPendingShutdownAsync()
        {
            var shutdown = _pendingShutdown;
            if (shutdown == null)
                return;

            await shutdown;

            if (ReferenceEquals(_pendingShutdown, shutdown))
                _pendingShutdown = null;
        }

        /// <summary>Mirrors the primary-button label onto the border overlay ("WAIT…" / "START" /
        /// "RESTART"); <paramref name="restart"/> also swaps the button's glyph.</summary>
        private void SetPrimaryText(string text, bool restart = false)
        {
            _border?.SetOverlayText(text);
            _toolbar?.SetPrimaryText(text, restart);
        }

        private void OnStatusReceived(object sender, ObsStatus status)
        {
            _lastStatusElapsed = status.Elapsed;

            // statuses arrive at 1 Hz — alternate elapsed time / FPS every 4 s (§4.2).
            var text = (_statusCount++ / 4) % 2 == 1
                ? $"{status.Fps:F0} FPS"
                : $"{(int)status.Elapsed.TotalMinutes:D2}:{status.Elapsed.Seconds:D2}";
            _toolbar?.SetStatusText(text);
        }

        /// <summary>
        /// The recording failed (nonzero stop code, unexpected process exit, or a timeout in one
        /// of the entry points): write the process log to a stable location OUTSIDE the session
        /// dir (§4.2 — the session dir is usually deleted below and the log must survive it),
        /// offer to open it, and keep the session when a partial video exists (a crash after
        /// minutes of recording isn't lost).
        /// </summary>
        private async void OnCriticalError(object sender, string message)
        {
            try
            {
                if (_closing)
                    return;
                _closing = true;

                Debug.WriteLine("Recording critical error: " + message);
                HideWindows();

                var logPath = WriteErrorLog(message);

                // shut the process down before touching its output file: the mp4 length check
                // and the directory delete below must not race a still-live obs-express.
                await ShutdownCapturersAsync();

                bool keepSession = HasPartialVideo();
                if (keepSession)
                    CreateSession();
                else
                    DeleteDirectory(_sessionDir);

                var content = "The recording has failed:\n" + message;
                if (keepSession)
                    content += "\n\nThe partial recording was kept and is available in Recent Sessions.";

                if (logPath != null)
                {
                    if (await NiceDialog.ShowPromptAsync(null, NiceDialogIcon.Error, content, "Recording failed", "Open Error Log"))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("Failed to open error log: " + ex.Message);
                            SentryConfig.CaptureHandled(ex, "video.open-error-log");
                        }
                    }
                }
                else
                {
                    await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, content, "Recording failed");
                }

                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error handling recording failure: " + ex);
                SentryConfig.CaptureHandled(ex, "video.handle-failure");
                Close();
            }
        }

        /// <summary>Creates the recents session for the finished (or partially recorded)
        /// video (§4.5). The poster frame (cropped.png) was written by the capture overlay at
        /// region-confirm time (§4.1).</summary>
        private void CreateSession()
        {
            var session = SessionManager.Current.CreateSessionInDirectory(_sessionDir);
            session.Name = "Recording";
            session.CreatedUtc = DateTime.UtcNow;
            session.ContentKind = "video"; // IsUploadOnly=true → no editor affordance (correct)
            session.VideoPath = _outputMp4;
            session.DurationMs = (long)_lastStatusElapsed.TotalMilliseconds;
            session.PreviewImgPath = Path.Combine(_sessionDir, "cropped.png");
            session.OriginalBounds = _region;
        }

        /// <summary>True when the output mp4 exists with any content — a crash or forced stop
        /// after minutes of recording should keep the partial file rather than delete it.</summary>
        private bool HasPartialVideo()
        {
            try
            {
                var mp4 = new FileInfo(_outputMp4);
                return mp4.Exists && mp4.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Writes the capturer's ring-buffer log next to the settings file
        /// (%APPDATA%\Clowd), falling back to the Desktop (WPF parity). Returns null on
        /// failure.</summary>
        private string WriteErrorLog(string message)
        {
            try
            {
                string dir = null;
                try
                {
                    dir = Path.GetDirectoryName(SettingsService.FilePath);
                }
                catch { }

                if (String.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                var logPath = Path.Combine(dir, $"capture_error_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(logPath, message + Environment.NewLine + Environment.NewLine + (_obs?.GetLog() ?? ""));
                return logPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to write recording error log: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "video.write-error-log");
                return null;
            }
        }

        private void HideWindows()
        {
            try { _border?.Hide(); }
            catch { }
            try { _toolbar?.Hide(); }
            catch { }
        }

        private void RaiseClosed()
        {
            if (_closedRaised)
                return;
            _closedRaised = true;
            Closed?.Invoke(this, EventArgs.Empty);
        }

        private static void DeleteDirectory(string dir)
        {
            try
            {
                if (!String.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to delete session directory: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "video.delete-session-dir");
            }
        }
    }
}
