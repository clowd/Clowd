using System;
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
        private bool _starting;
        private bool _finishing;
        private bool _closing;
        private bool _closedRaised;

        private ObsCapturer _obs;
        private BorderWindow _border;
        private FloatingToolbarWindow _toolbar;
        private ScreenRect _region;
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

                _obs = new ObsCapturer();
                _obs.CriticalError += OnCriticalError;
                _obs.StatusReceived += OnStatusReceived;

                await _obs.InitializeAsync(ObsArguments.Build(region, _outputMp4, SettingsRoot.Current.Recording), binary);

                if (_closing)
                    return;

                _initialized = true;
                _border.SetOverlayText("START");
                _toolbar.SetPrimaryText("START");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to open recording session: " + ex);
                if (!_closing)
                    OnCriticalError(this, ex.Message);
            }
        }

        /// <summary>Starts the recording. No-op unless initialized and not already recording
        /// (the WAIT gate, §4.2/F6).</summary>
        public async void StartRecording()
        {
            try
            {
                if (!_initialized || IsRecording || _starting || _closing)
                    return;
                _starting = true;

                // the CLI always passes the device args; the Capture* toggles are runtime mutes.
                var settings = SettingsRoot.Current.Recording;
                _obs.SetSpeakerMute(!settings.CaptureSpeaker);
                _obs.SetMicrophoneMute(!settings.CaptureMicrophone);

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

                await _obs.DisposeAsync();
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to finish recording: " + ex);
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
                    }
                }

                // pre-start: writes "quit" → cancel-before-start, exit 0 (§1.2). Awaited so the
                // directory delete below cannot race the process's open file handles, without
                // ever blocking the UI thread (the old sync Dispose froze the app for up to 5 s
                // when cancelling during WAIT).
                if (_obs != null)
                    await _obs.DisposeAsync();

                DeleteDirectory(_sessionDir);
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error cancelling recording session: " + ex);
                Close();
            }
        }

        /// <summary>Hotkey entry point (Start/Stop Recording): finish if recording, start if
        /// initialized, ignore during WAIT (§4.2/F6 — WPF parity).</summary>
        public void Toggle()
        {
            try
            {
                if (IsRecording)
                    FinishRecording();
                else if (_initialized)
                    StartRecording();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error toggling recording: " + ex);
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
                    }
                }

                if (_obs != null)
                    await _obs.DisposeAsync();

                if (IsRecording && (stoppedCleanly || HasPartialVideo()))
                    CreateSession();
                else
                    DeleteDirectory(_sessionDir); // WAIT state: nothing was recorded
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error shutting down recording session: " + ex);
            }

            Close();
        }

        public void Close()
        {
            _closing = true;

            if (ReferenceEquals(ActiveInstance, this))
                ActiveInstance = null;

            try { _border?.Close(); }
            catch { }
            try { _toolbar?.Close(); }
            catch { }
            _border = null;
            _toolbar = null;

            RaiseClosed();
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
                if (_obs != null)
                    await _obs.DisposeAsync();

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
            }
        }
    }
}
