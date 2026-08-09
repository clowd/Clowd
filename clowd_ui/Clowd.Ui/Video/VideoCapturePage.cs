using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;
using Clowd.Video;
using Clowd.Video.Playback;

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

        /// <summary>True while the recording is rolling but paused (no frames written).</summary>
        public bool IsPaused { get; private set; }

        public event EventHandler Closed;

        // set once obs-express has emitted "initialized" — Start/Toggle are no-ops before that
        // (the WAIT state, §4.2/F6): an ungated "start" pre-initialized combined with a slow
        // first-run OBS init would time out inside an async void.
        private bool _initialized;
        private bool _initializing;
        private bool _starting;
        private bool _finishing;
        private bool _pausing;
        private bool _closing;
        private bool _closedRaised;

        // a configure is in flight (only one at a time), and a settings change that arrived while
        // it — or the initial spawn — was running, coalesced into one follow-up configure (§4.2).
        private bool _configuring;
        private bool _configurePending;

        // whether the recorder currently running was built WITH a webcam source. Compared against
        // what a configure asks for so a rejected configure is only blamed on (and only reverts)
        // the camera when the camera is the thing that changed.
        private bool _appliedWebcam;

        private ObsCapturer _obs;
        // shutdown of a capturer being replaced (failed configure) — awaited before the
        // replacement spawns (both write the same video.mp4).
        private Task _pendingShutdown;
        private BorderWindow _border;
        private FloatingToolbarWindow _toolbar;
        private SettingsRecording _settings;
        private ScreenRect _region;
        private string _binaryPath;
        private string _sessionDir;
        private string _outputMp4;
        private string _settingsPath;
        // where the finished video actually ended up (§4.5 / issue #50): the user's output folder
        // once MoveToOutputFolderAsync has run, and _outputMp4 while recording or if the move failed.
        private string _savedPath;
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
                _settingsPath = Path.Combine(sessionDir, ObsArguments.SettingsFileName);
                _savedPath = _outputMp4;

                // normalize the configured output folder up-front (creating it, or falling back to
                // Videos when it has gone away) and write it back, so the settings page the OPTIONS
                // button opens always names the folder this recording will actually be saved to.
                var outputDir = RecordingOutputPath.ResolveDirectory(_settings);
                if (outputDir != null)
                    _settings.OutputDirectory = outputDir;

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
                _toolbar.PauseToggleClicked += (s, e) => TogglePauseRecording();
                _toolbar.FinishClicked += (s, e) => FinishRecording();
                _toolbar.CancelClicked += (s, e) => Cancel();
                _toolbar.SettingsClicked += (s, e) => PageManager.Current.GetSettingsPage().Open(SettingsPageTab.SettingsRecording);
                // live mutes only — the toolbar itself persists the toggle settings.
                _toolbar.MicToggled += (s, enabled) => _obs?.SetMicrophoneMute(!enabled);
                _toolbar.SpeakerToggled += (s, enabled) => _obs?.SetSpeakerMute(!enabled);
                _toolbar.WebcamToggled += (s, enabled) => OnWebcamToggled(enabled);
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
        /// ready. Also the recovery path after a failed <c>configure</c>: the settings file is
        /// rewritten here, so the replacement process starts from exactly the values the running
        /// one refused to take.
        /// </summary>
        private async Task InitializeCapturerAsync()
        {
            _initializing = true;
            _initialized = false;
            SetPrimaryText("WAIT…");
            // a respawned capturer with audio removed never emits levels again — clear the
            // stale meters rather than freezing the last values through the WAIT phase.
            _toolbar?.SetAudioLevels(null, null);

            try
            {
                // the replaced process holds video.mp4 open until it exits; spawning its
                // successor first would race it for the file.
                await AwaitPendingShutdownAsync();

                // Cancel/quit during that wait has already deleted the session directory and
                // disposed nothing (there is no process yet) — do not spawn one into it.
                if (_closing)
                    return;

                // the recorder reads the file while parsing its command line, so it has to exist
                // before the spawn. Written here rather than in Open so a change made during a
                // respawn is picked up by the process it starts (hence the flag reset).
                _configurePending = false;
                ObsArguments.WriteSettingsFile(_settingsPath, _settings);
                // the file just written is what this process is about to be built from.
                _appliedWebcam = IsWebcamCaptured();

                _obs = new ObsCapturer();
                _obs.CriticalError += OnCriticalError;
                _obs.StatusReceived += OnStatusReceived;
                _obs.LevelsReceived += OnLevelsReceived;

                await _obs.InitializeAsync(ObsArguments.Build(_region, _outputMp4, _settingsPath), _binaryPath);
            }
            finally
            {
                _initializing = false;
            }

            if (_closing)
                return;

            _initialized = true;
            SetPrimaryText("START");

            // the settings file carries the devices, never the capture toggles — those are mutes.
            ApplyCaptureMutes();

            // a change that landed while the pipeline was being built missed the file write above.
            if (_configurePending)
                ApplySettingsChange();
        }

        /// <summary>Starts the recording. No-op unless initialized and not already recording
        /// (the WAIT gate, §4.2/F6).</summary>
        public async void StartRecording()
        {
            try
            {
                if (IsRecording || _starting || _initializing || _closing)
                    return;

                if (!_initialized)
                    return;
                _starting = true;

                // the settings file always lists the devices; the Capture* toggles are runtime
                // mutes, re-applied here in case a configure rebuilt the sources.
                ApplyCaptureMutes();

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

        /// <summary>Pauses or resumes a rolling recording. Paused time is excluded from the output
        /// (obs-express pauses the encoder, not the pipeline: levels keep flowing, statuses stop).
        /// No-op while a start/finish/another toggle is in flight.</summary>
        public async void TogglePauseRecording()
        {
            try
            {
                if (!IsRecording || _pausing || _finishing || _closing)
                    return;
                _pausing = true;

                if (IsPaused)
                {
                    await _obs.ResumeAsync();
                    IsPaused = false;
                }
                else
                {
                    await _obs.PauseAsync();
                    IsPaused = true;
                }

                _toolbar?.SetPausedState(IsPaused);
            }
            catch (Exception ex)
            {
                // an unacked pause means a wedged or dead child, exactly like a failed start.
                Debug.WriteLine("Failed to pause/resume recording: " + ex);
                SentryConfig.CaptureHandled(ex, "video.pause");
                if (!_closing)
                    OnCriticalError(this, ex.Message);
            }
            finally
            {
                _pausing = false;
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

                // the capturer process holds the mp4 open until it exits, so it has to be gone
                // before the file can be moved out of the session directory.
                await ShutdownCapturersAsync();

                var moveError = await MoveToOutputFolderAsync();

                var session = CreateSession();

                // A recording with a webcam is only half-finished: the camera was captured as a
                // separate track and is composited nowhere until the user places it, so the editor
                // *is* the "open when finished" action for those — it overrides the setting rather
                // than dropping the user on a Recents row whose thumbnail shows no webcam at all.
                // Recordings without one keep the existing behavior exactly.
                if (session != null && session.HasWebcamTrack && session.ShowEditVideo)
                {
                    VideoEditor.VideoEditorWindow.ShowSession(session);
                }
                else
                {
                    switch (SettingsRoot.Current.Recording.OpenWhenFinished)
                    {
                        case RecordingFinishAction.RecentsPage:
                            PageManager.Current.GetSettingsPage().Open(SettingsPageTab.RecentSessions);
                            break;
                        case RecordingFinishAction.OutputFolder:
                            // reveals the saved file in its folder — the same affordance as the
                            // "Show in folder" item in Recents (WPF: OpenFinishedInExplorer).
                            ShellHelper.RevealFileInFolder(_savedPath);
                            break;
                    }
                }

                // this is a tray app with no MainWindow, so with nothing open there is simply
                // nowhere to host the toast. That is deliberate now: opening a page the user did
                // not ask for just to carry a toast would override the setting — revealing the
                // folder is its own confirmation, and "None" means none.
                var host = Toast.GetActiveOrMainWindow();
                if (host != null)
                    Toast.Show(host, "Recording saved");

                // reported after the session exists and the toast has shown: the recording itself
                // is safe either way, this only says it is not in the folder the user picked.
                if (moveError != null)
                {
                    await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Warning,
                        $"It was saved to {_savedPath} instead.{Environment.NewLine}{Environment.NewLine}" +
                        $"Error:{Environment.NewLine}{moveError.Message}",
                        "Unable to save the recording to your chosen folder");
                }

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

        /// <summary>Hotkey entry point (Start/Stop Recording): finish if recording, start if the
        /// button would be actionable, ignore during WAIT (§4.2/F6 — WPF parity).</summary>
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
                {
                    await MoveToOutputFolderAsync();
                    CreateSession();
                }
                else
                {
                    DeleteDirectory(_sessionDir); // WAIT state: nothing was recorded
                }
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
        /// anything the recorder reads from its settings file is pushed to the waiting process
        /// with a <c>configure</c>.
        /// </summary>
        private void OnRecordingSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            // once frames are flowing the recorder ignores these — the change applies to the next
            // recording (the settings page says as much).
            if (_closing || IsRecording || _starting)
                return;

            if (e.PropertyName is nameof(SettingsRecording.CaptureMicrophone) or nameof(SettingsRecording.CaptureSpeaker))
            {
                // runtime mutes: the settings page and the toolbar buttons behave identically.
                // CaptureWebcam deliberately does NOT belong here — there is no such thing as a
                // muted camera; the recorder has to build or drop the source, so it falls through
                // to the configure path below like any other pipeline setting.
                ApplyCaptureMutes();
                return;
            }

            if (!ReachesRecorder(e.PropertyName))
                return;

            ApplySettingsChange();
        }

        /// <summary>
        /// True when changing <paramref name="propertyName"/> changes the settings file the
        /// recorder reads. Deliberately a deny-list of the settings that never reach it: a setting
        /// added to <see cref="ObsArguments.WriteSettingsFile"/> without touching this method costs
        /// at worst a needless configure, never a recording made with the value the user just
        /// changed away from — so a null or empty name ("everything changed") counts too.
        /// </summary>
        private static bool ReachesRecorder(string propertyName) => propertyName switch
        {
            // post-recording UI behavior; the capturer never sees it.
            nameof(SettingsRecording.OpenWhenFinished) => false,
            // the capturer always writes video.mp4 into the session dir; these only decide where
            // the finished file is moved to afterwards (issue #50), which happens at stop time.
            nameof(SettingsRecording.OutputDirectory) => false,
            nameof(SettingsRecording.FilenamePattern) => false,
            // read by the GIF conversion tool long after a recording has finished; the recorder has
            // never heard of them.
            nameof(SettingsRecording.GifQuality) => false,
            nameof(SettingsRecording.GifMaxWidth) => false,
            nameof(SettingsRecording.GifMaxHeight) => false,
            // spelled out rather than left to the default: both halves of the webcam decision feed
            // the settings file's "webcam_device" key, and the recorder rebuilds its pipeline (source,
            // track-1 encoder, obs_view mix) when it changes — so a change to either must configure.
            nameof(SettingsRecording.CaptureWebcam) => true,
            nameof(SettingsRecording.WebcamDeviceId) => true,
            _ => true,
        };

        /// <summary>
        /// Pushes the current settings onto the waiting (initialized but not recording) capturer:
        /// rewrite the settings file, <c>configure</c>, wait for the ack. Replaces the old
        /// tear-down-and-RESTART cycle — the user never sees the process being reconfigured.
        /// A rejected, failed or timed-out configure leaves the recorder in a state we can no
        /// longer reason about, so it is silently replaced with a fresh one instead.
        /// Changes arriving while a configure (or the spawn itself) is in flight are coalesced
        /// into a single follow-up pass.
        /// </summary>
        private async void ApplySettingsChange()
        {
            if (_closing || IsRecording || _starting)
                return;

            if (_configuring || _initializing || !_initialized)
            {
                _configurePending = true;
                return;
            }

            _configuring = true;
            try
            {
                while (!_closing && !IsRecording && !_starting && _initialized && _obs != null)
                {
                    _configurePending = false;
                    var obs = _obs;

                    // what this pass is asking the recorder to build, remembered across the await so
                    // a rejection can be blamed on the webcam only when the webcam is what changed.
                    var wantedWebcam = IsWebcamCaptured();

                    ObsConfigureResult result = null;
                    try
                    {
                        ObsArguments.WriteSettingsFile(_settingsPath, _settings);
                        result = await obs.ConfigureAsync(_settingsPath);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Failed to reconfigure the recording process: " + ex.Message);
                        SentryConfig.CaptureHandled(ex, "video.configure");
                    }

                    // the capturer may have been replaced or shut down while we waited; whoever
                    // did that owns the new one.
                    if (_closing || !ReferenceEquals(_obs, obs))
                        return;

                    // START is actionable throughout the (up to 10 s) wait for the ack, and the
                    // recorder handles commands in order — so "start" is queued behind this
                    // configure and by now frames may already be flowing. Respawning here would
                    // quit that process and truncate video.mp4 while the toolbar says FINISH: the
                    // recording wins, and the settings apply to the next one.
                    if (IsRecording || _starting)
                    {
                        if (result == null || !result.Applied)
                            Debug.WriteLine("Recorder did not apply the new settings, but a recording has since started; leaving it alone.");
                        return;
                    }

                    if (result == null || !result.Applied)
                    {
                        Debug.WriteLine("Recorder did not apply the new settings" +
                            (result == null ? "" : $" (fatal={result.Fatal}): {result.Message}") + "; respawning it.");

                        // A camera the recorder refuses (unplugged, held by another app, or a
                        // platform that has no webcam support at all) would otherwise be retried by
                        // the respawn and fail it too, leaving the session with no recorder. Untick
                        // the box first — that raises PropertyChanged, which the toolbar picks up to
                        // unlight CAM and this method turns into a follow-up pass the respawn below
                        // absorbs (InitializeCapturerAsync rewrites the settings file from scratch).
                        if (wantedWebcam && !_appliedWebcam)
                        {
                            _settings.CaptureWebcam = false;
                            NotifyWebcamRejected(result?.Message);
                        }

                        await RespawnCapturerAsync();
                        return;
                    }

                    _appliedWebcam = wantedWebcam;

                    // rebuilt audio sources come back unmuted.
                    ApplyCaptureMutes();

                    // the recorder only ignores keys once recording has started, which is not a
                    // state this method runs in — worth knowing about, not worth acting on.
                    if (result.IgnoredKeys.Length > 0)
                        Debug.WriteLine("Recorder ignored settings: " + String.Join(", ", result.IgnoredKeys));

                    if (!_configurePending)
                        return;
                }
            }
            catch (Exception ex)
            {
                // in practice the respawn above failing: there is no recorder left to record with,
                // so this is the same dead end as any other capturer failure.
                Debug.WriteLine("Failed to apply the new recording settings: " + ex);
                SentryConfig.CaptureHandled(ex, "video.apply-settings");
                if (!_closing)
                    OnCriticalError(this, ex.Message);
            }
            finally
            {
                _configuring = false;

                // a change that arrived while this pass (or the respawn it triggered) was running
                // and could not be serviced by the loop above; posted rather than called so it
                // cannot re-enter through the frame that set the flag.
                if (_configurePending && !_closing)
                    Dispatcher.UIThread.Post(ApplySettingsChange);
            }
        }

        /// <summary>
        /// Replaces the capturer in place after it failed to accept new settings. The old process
        /// is detached before disposal: its exit must not reach the page's critical-error path,
        /// and its statuses must never drive the replacement's UI.
        /// </summary>
        private async Task RespawnCapturerAsync()
        {
            _initialized = false;

            var obs = _obs;
            _obs = null;
            if (obs != null)
            {
                obs.CriticalError -= OnCriticalError;
                obs.StatusReceived -= OnStatusReceived;
                obs.LevelsReceived -= OnLevelsReceived;
                _toolbar?.SetAudioLevels(null, null);
                _pendingShutdown = obs.DisposeAsync();
            }

            await InitializeCapturerAsync();
        }

        /// <summary>True when the current settings will actually produce a webcam track: the box
        /// ticked AND a camera picked, which is exactly the condition
        /// <see cref="ObsArguments.WriteSettingsFile"/> uses to emit a non-empty
        /// <c>webcam_device</c>.</summary>
        private bool IsWebcamCaptured()
            => _settings != null && _settings.CaptureWebcam && !String.IsNullOrEmpty(_settings.WebcamDeviceId);

        /// <summary>
        /// The CAM button was pressed. The toolbar has already written
        /// <see cref="SettingsRecording.CaptureWebcam"/>, and <see cref="OnRecordingSettingChanged"/>
        /// has already turned that into a <c>configure</c> — a webcam is a pipeline element, not a
        /// mute, so there is nothing to apply live here. This only exists to say so in the log when
        /// the click lands in a phase the recorder can no longer act on (a hotkey starting the
        /// recording between the press and this callback).
        /// </summary>
        private void OnWebcamToggled(bool enabled)
        {
            if (IsRecording || _starting)
                Debug.WriteLine($"Webcam toggled ({enabled}) after the recording started; it applies to the next recording.");
        }

        /// <summary>
        /// Tells the user their camera did not make it into the pipeline, after the toggle has
        /// already been reverted. Prefers a toast, but during the WAIT phase the only visible
        /// windows are usually this session's own toolbar and border — a 50px transparent strip is
        /// no place for a notification — so those are skipped and a notice takes over.
        /// </summary>
        private void NotifyWebcamRejected(string recorderMessage)
        {
            var message = "The webcam could not be added to this recording, so it has been turned off." +
                (String.IsNullOrEmpty(recorderMessage) ? "" : Environment.NewLine + Environment.NewLine + recorderMessage);

            var host = Toast.GetActiveOrMainWindow();
            if (host != null && !ReferenceEquals(host, _toolbar) && !ReferenceEquals(host, _border))
            {
                Toast.Show(host, "Webcam capture unavailable — turned off");
                Debug.WriteLine(message);
                return;
            }

            _ = NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Warning, message, "Webcam capture unavailable");
        }

        /// <summary>Applies the CaptureSpeaker/CaptureMicrophone toggles as live mutes — the
        /// settings file lists the devices unconditionally, so these are the only thing deciding
        /// whether audio is actually recorded.</summary>
        private void ApplyCaptureMutes()
        {
            _obs?.SetSpeakerMute(!_settings.CaptureSpeaker);
            _obs?.SetMicrophoneMute(!_settings.CaptureMicrophone);
        }

        /// <summary>Shuts the current capturer down AND waits out a process an in-flight
        /// <see cref="RespawnCapturerAsync"/> is still killing — both hold the session's video.mp4
        /// open, so measuring or deleting that file before this completes races a live
        /// obs-express.</summary>
        private async Task ShutdownCapturersAsync()
        {
            if (_obs != null)
                await _obs.DisposeAsync();

            await AwaitPendingShutdownAsync();
        }

        /// <summary>Waits for the replaced process (if any) to exit. Read into a local first:
        /// <see cref="RespawnCapturerAsync"/> and <see cref="ShutdownCapturersAsync"/> can both be
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

        /// <summary>Mirrors the primary-button label onto the border overlay ("WAIT…" / "START").</summary>
        private void SetPrimaryText(string text)
        {
            _border?.SetOverlayText(text);
            _toolbar?.SetPrimaryText(text);
        }

        private void OnStatusReceived(object sender, ObsStatus status)
        {
            _lastStatusElapsed = status.Elapsed;

            // statuses arrive at 1 Hz — alternate elapsed time / FPS every 4 s (§4.2). Statuses
            // stop while paused, but one may already be in flight when the pause lands — the
            // toolbar (which owns the PAUSED label) drops it rather than letting it overwrite.
            var text = (_statusCount++ / 4) % 2 == 1
                ? $"{status.Fps:F0} FPS"
                : $"{(int)status.Elapsed.TotalMinutes:D2}:{status.Elapsed.Seconds:D2}";
            _toolbar?.SetStatusText(text);
        }

        private void OnLevelsReceived(object sender, ObsLevels levels)
        {
            // one device per source type (CLI index 0); an absent source sends an empty array.
            _toolbar?.SetAudioLevels(
                levels.Mic.Length > 0 ? levels.Mic[0] : null,
                levels.Speaker.Length > 0 ? levels.Speaker[0] : null);
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
                {
                    // a partial recording is still the user's recording — save it where they
                    // expect it. A failure here only leaves it in the session directory, which
                    // the message below already points at.
                    await MoveToOutputFolderAsync();
                    CreateSession();
                }
                else
                {
                    DeleteDirectory(_sessionDir);
                }

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
        /// region-confirm time (§4.1). Returns the session so the caller can act on it (the
        /// webcam auto-open in <see cref="FinishRecording"/>).</summary>
        private SessionInfo CreateSession()
        {
            var session = SessionManager.Current.CreateSessionInDirectory(_sessionDir);
            session.Name = "Recording";
            session.CreatedUtc = DateTime.UtcNow;
            session.ContentKind = "video"; // IsUploadOnly=true → no *image* editor affordance (correct); the video editor is offered through CanEditVideo
            // usually outside the session dir now (issue #50), so the recording survives the
            // session being deleted here or expiring out of Recents — the user's file is theirs.
            session.VideoPath = _savedPath;
            session.DurationMs = (long)_lastStatusElapsed.TotalMilliseconds;
            session.PreviewImgPath = Path.Combine(_sessionDir, "cropped.png");
            session.OriginalBounds = _region;
            session.WebcamTrack = ResolveWebcamTrack();
            return session;
        }

        /// <summary>
        /// The webcam track this recording ended up with, or null if it has none. Normally read
        /// straight from the recorder's own <c>tracks</c> report (started_recording, refreshed on
        /// stopped_recording); a recorder too old to send one falls back to probing the finished
        /// file, which is the only way an existing recording can be classified at all. The probe is
        /// deliberately best-effort: a missing FFmpeg only costs the auto-open, never the session.
        /// </summary>
        private SessionVideoTrack ResolveWebcamTrack()
        {
            try
            {
                var reported = _obs?.LastTracks;
                if (reported != null)
                {
                    return reported.Webcam == null
                        ? null
                        : new SessionVideoTrack
                        {
                            Index = reported.Webcam.Index,
                            Width = reported.Webcam.Width,
                            Height = reported.Webcam.Height,
                        };
                }

                if (!OperatingSystem.IsWindows() || !FFmpegLoader.TryInitialize(ResolveFFmpegDirectory))
                    return null;

                var info = MediaProbe.Probe(_savedPath);
                // stream 0 is the screen, stream 1 (when present) is the webcam — the order the
                // recorder writes the tracks in, and the order the editor reads them back in.
                if (info?.VideoStreams == null || info.VideoStreams.Count < 2)
                    return null;

                var webcam = info.VideoStreams[1];
                return new SessionVideoTrack
                {
                    Index = webcam.StreamIndex,
                    Width = webcam.Width,
                    Height = webcam.Height,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Could not determine the recording's webcam track: " + ex.Message);
                return null;
            }
        }

        /// <summary>Production layout: the FFmpeg DLLs sit next to obs-express; dev machines set
        /// CLOWD_FFMPEG_PATH, which FFmpegLoader checks before consulting this.</summary>
        private static string ResolveFFmpegDirectory()
        {
            var obs = ObsBinaryLocator.Resolve();
            return obs != null ? Path.GetDirectoryName(obs) : null;
        }

        /// <summary>
        /// Moves the finished mp4 out of the session directory into the folder the user chose,
        /// named with their filename pattern (issue #50 — WPF parity with
        /// VideoCaptureWindow.StopRecording). Updates <see cref="_savedPath"/> to wherever the
        /// video ended up and returns the failure, if any, so the caller can report it; a failed
        /// move is never fatal — the video simply stays in the session directory, which is exactly
        /// where the pre-#50 rewrite always left it. Callers must have shut the capturer down
        /// first: obs-express holds the file open until its process exits.
        /// </summary>
        private async Task<Exception> MoveToOutputFolderAsync()
        {
            try
            {
                if (!File.Exists(_outputMp4))
                    return null;

                var target = RecordingOutputPath.GetSavePath(_settings);
                if (String.IsNullOrEmpty(target))
                    return null; // no writable output folder at all; keep the session copy silently

                // File.Move degrades to a full copy across volumes, which for a long recording is
                // seconds of work — never on the UI thread.
                await Task.Run(() => File.Move(_outputMp4, target));

                _savedPath = target;
                Debug.WriteLine("Recording saved to " + target);
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to move the recording to the output folder: " + ex);
                SentryConfig.CaptureHandled(ex, "video.move-output");
                return ex;
            }
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
