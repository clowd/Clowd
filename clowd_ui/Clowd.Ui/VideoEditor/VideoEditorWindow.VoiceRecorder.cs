using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI.Controls.Tray;
using Clowd.UI.Helpers;
using Clowd.UI.Services;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Path = System.IO.Path;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The voice-over recorder half of the editor window: the mic tool button toggles the
    /// <see cref="VoiceRecorderOverlay"/> over the preview, the overlay's buttons drive the
    /// microphone (<see cref="MicRecorder"/>) and the take (<see cref="EditorSession.BeginVoiceTake"/>
    /// and friends), and the window's transport and keyboard defer to a take in progress.
    ///
    /// <para>A take runs on the wall clock, not the player's: the preview plays along from the
    /// playhead so the user hears what they are narrating over, but the take keeps growing after
    /// the player reaches the end of the project (the preview holds its last frame) and the
    /// finished clip simply extends the project. The session converts the take's elapsed real time
    /// into project ticks through the current speed warp, and the clip it makes is exempt from
    /// that warp, so a narration recorded over a sped-up stretch stays in real time.</para>
    /// </summary>
    public partial class VideoEditorWindow
    {
        /// <summary>The countdown before every take, in whole seconds shown one at a time.</summary>
        private const int VoiceCountdownSeconds = 3;

        /// <summary>How often the pill's elapsed time and the timeline's ghost take are refreshed
        /// from the take's clock while recording.</summary>
        private static readonly TimeSpan VoiceTickInterval = TimeSpan.FromMilliseconds(33);

        private const string VoiceErrorTitle = "Can't record voice-over";

        private MicRecorder _mic; // open while the overlay is shown, null otherwise
        private DispatcherTimer _voiceCountdown; // non-null while 3-2-1 is on screen
        private int _voiceCountdownValue;
        private DispatcherTimer _voiceTick; // non-null while recording
        private readonly Stopwatch _voiceClock = new Stopwatch();
        private Guid? _voiceTakeId; // the session's take, from the countdown's start to the finish
        private string _voiceWavPath;
        private bool _voiceFinishing; // CommitVoiceTake is on the stack (its callbacks must not re-enter)

        // a take whose recording has stopped but whose clip is not in the project yet: the
        // session refuses to finish under an open drag (the clip would ride on the drag's preview
        // and go with its cancel), so the complete file waits for the drag to end, polled by
        // _voiceFinishPoll (see StopVoiceTake and CommitVoiceTake)
        private (string Path, TimeSpan Recorded)? _voicePendingFinish;
        private DispatcherTimer _voiceFinishPoll;

        // a device picked in the inspector while a take is busy is applied once it is over
        private bool _voiceDeviceChangePending;
        private string _voicePendingDeviceId;

        // where the finished take leaves the playhead: consumed by Editor_ProjectChanged's re-seek
        // on the structural change the finish raises, so the playhead lands on the clip's end
        // rather than wherever the player stopped (the old project end, when the take ran past it).
        private long? _seekAfterTakeTicks;

        private bool IsVoiceCountingDown => _voiceCountdown != null;

        /// <summary>True while the microphone is writing a take (after the countdown, before the
        /// stop).</summary>
        private bool IsVoiceRecording => _voiceTakeId != null && _voiceCountdown == null && _voicePendingFinish == null;

        /// <summary>True between a stop that landed inside a drag and the commit that follows
        /// the drag's end: the file is complete, the clip is not in the project yet.</summary>
        private bool IsVoiceFinishPending => _voicePendingFinish != null;

        /// <summary>True from the record click to the finish or cancel.</summary>
        private bool IsVoiceTakeBusy => _voiceTakeId != null || _voiceFinishing;

        /// <summary>Wires the overlay, the tool button and the inspector's device picker. Called
        /// once from the constructor, after InitializeComponent.</summary>
        private void InitVoiceRecorder()
        {
            voiceRecorder.RecordClicked += (_, _) => StartVoiceCountdown();
            voiceRecorder.StopClicked += (_, _) => StopVoiceTake();
            voiceRecorder.SettingsClicked += (_, _) => ShowVoiceRecorderSettings();
            voiceRecorder.CloseClicked += (_, _) => btnVoice.IsChecked = false;

            // the button is the one switch: the overlay's X and the close path both go through
            // IsChecked, so there is exactly one show/hide transition to get right.
            btnVoice.IsCheckedChanged += (_, _) => SetVoiceRecorderVisible(btnVoice.IsChecked == true);

            Inspector.VoiceRecorderDeviceChanged += VoiceDeviceChanged;

            RefreshVoiceRecorderButton();
            RefreshVoiceRecordEnabled();
        }

        /// <summary>Enables the mic tool button where a take can be recorded at all, and puts the
        /// reason in the tip's footer where it cannot: no capture support on this platform, known
        /// at once, or no microphone to open, which is a COM walk over every capture endpoint and
        /// so is answered off the UI thread (a hung audio service or a long list of endpoints
        /// must not hold up the window's construction). Evaluated once, like the tip copy
        /// itself; the button is live until the walk says otherwise.</summary>
        private void RefreshVoiceRecorderButton()
        {
            if (!MicRecorder.IsSupported)
            {
                btnVoice.IsEnabled = false;
                tipVoice.DisabledReason = "Voice recording is not available on this platform yet.";
                return;
            }

            btnVoice.IsEnabled = true;
            tipVoice.DisabledReason = null;
            _ = Task.Run(() =>
            {
                var found = HasMicrophone();
                Dispatcher.UIThread.Post(() =>
                {
                    if (_closing || found)
                        return;

                    btnVoice.IsEnabled = false;
                    tipVoice.DisabledReason = "No microphone was found. Connect one and reopen the editor.";
                });
            });
        }

        /// <summary>Whether any capture endpoint exists. The enumeration always lists a "default"
        /// row, even with nothing behind it, so that row does not count.</summary>
        private static bool HasMicrophone()
        {
            try
            {
                return MicRecorder.Devices().Any(d => d.DeviceId != AudioDeviceManager.DefaultDeviceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Microphone enumeration failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>The overlay's record button is live while there is a microphone open, a
        /// project to record into and no take already on the way. (The button stays clickable
        /// while recording whatever this says: that click is the stop.) The transport's speed
        /// picker is the other control a take owns: pinned to 1x for its length (see
        /// <see cref="StartVoiceRecording"/>), so the picker is off until the take is over.</summary>
        private void RefreshVoiceRecordEnabled()
        {
            voiceRecorder.IsRecordEnabled = _mic is { IsDeviceOpen: true } && _editor != null
                                            && !IsVoiceTakeBusy && !_closing;
            btnSpeed.IsEnabled = !IsVoiceTakeBusy;
        }

        /// <summary>
        /// Shows the recorder (opening the microphone so the meter runs before any take, which is
        /// how the user checks the mic works) or hides it. Hiding mid-take ends the take first,
        /// keeping the audio: closing the recorder is never a way to lose a recording.
        /// </summary>
        private void SetVoiceRecorderVisible(bool show)
        {
            if (show == voiceRecorder.IsVisible)
                return;

            if (show)
            {
                voiceRecorder.IsVisible = true;
                OpenMic(SettingsRoot.Current?.Recording.MicrophoneDeviceId);
                return;
            }

            if (IsVoiceCountingDown)
                CancelVoiceCountdown();
            if (IsVoiceRecording)
                StopVoiceTake();
            if (IsVoiceFinishPending)
                CommitVoiceTake(); // a no-op while the drag it waits for is still open

            CloseMic();
            voiceRecorder.IsVisible = false;
            Inspector.HideVoiceRecorderSettings();
            RefreshVoiceRecordEnabled();
        }

        /// <summary>Opens (or switches to) <paramref name="deviceId"/> and starts metering. Null or
        /// empty is the default microphone. Never throws: a device that cannot be opened leaves
        /// the pill reading "No microphone" with the record button greyed out.</summary>
        private void OpenMic(string deviceId)
        {
            if (_mic == null)
            {
                _mic = new MicRecorder();
                _mic.PeakDbfsChanged += Mic_PeakDbfsChanged;
            }

            _mic.Open(deviceId);
            voiceRecorder.DeviceName = _mic.DeviceName;
            voiceRecorder.LevelFraction = _mic.IsDeviceOpen ? 0 : null;
            RefreshVoiceRecordEnabled();
        }

        /// <summary>Releases the microphone. A disposed recorder refuses to reopen, so the next
        /// show makes a new one.</summary>
        private void CloseMic()
        {
            if (_mic == null)
                return;

            _mic.PeakDbfsChanged -= Mic_PeakDbfsChanged;
            _mic.Dispose();
            _mic = null;
            _voiceDeviceChangePending = false;
            _voicePendingDeviceId = null;
            voiceRecorder.LevelFraction = null;
            voiceRecorder.DeviceName = MicRecorder.NoDeviceName;
        }

        /// <summary>The recorder's 100 ms level feed, already on the UI thread. A null level with
        /// the device gone is the microphone being unplugged (or failing) underneath the recorder:
        /// a countdown is abandoned, and a take in progress is finished with whatever reached the
        /// file rather than thrown away.</summary>
        private void Mic_PeakDbfsChanged(double? peakDbfs)
        {
            if (_closing || _mic == null)
                return;

            voiceRecorder.LevelFraction = TrayLevel.FromPeakDbfs(peakDbfs);
            voiceRecorder.DeviceName = _mic.DeviceName;

            if (peakDbfs == null && !_mic.IsDeviceOpen)
            {
                if (IsVoiceCountingDown)
                {
                    CancelVoiceCountdown();
                    Toast.Show(this, "The microphone stopped responding, so the take was not started.",
                        NotificationType.Error);
                }
                else if (IsVoiceRecording && !_voiceFinishing)
                {
                    StopVoiceTake();
                }
            }

            RefreshVoiceRecordEnabled();
        }

        /// <summary>The overlay's gear: the recorder's settings take over the properties panel
        /// (device picker, mute-others), so the panel has to be open to see them.</summary>
        private void ShowVoiceRecorderSettings()
        {
            Inspector.ShowVoiceRecorderSettings();
            SidebarVisible = true;
        }

        /// <summary>The inspector's device pick. Applied at once while idle (the meter switches
        /// over so the user can check the new mic), deferred to the end of a take that is busy: a
        /// take records on the device it started on.</summary>
        private void VoiceDeviceChanged(string deviceId)
        {
            if (_mic == null || !voiceRecorder.IsVisible)
                return; // the next show opens the stored device anyway

            if (IsVoiceTakeBusy)
            {
                _voiceDeviceChangePending = true;
                _voicePendingDeviceId = deviceId;
                return;
            }

            OpenMic(deviceId);
        }

        private void ApplyPendingVoiceDevice()
        {
            if (!_voiceDeviceChangePending || _mic == null || _closing)
                return;

            _voiceDeviceChangePending = false;
            var id = _voicePendingDeviceId;
            _voicePendingDeviceId = null;
            OpenMic(id);
        }

        // ====================================================================
        // The take
        // ====================================================================

        /// <summary>
        /// The record click: halts the player where it stands (the take starts at the playhead and
        /// the countdown must not move it), asks the session for a take there, which decides the
        /// row now and shows the ghost on it, then counts 3-2-1 over the preview before
        /// <see cref="StartVoiceRecording"/>. Escape during the countdown backs all of it out.
        /// </summary>
        private void StartVoiceCountdown()
        {
            if (!CanAddToProject || _mic is not { IsDeviceOpen: true } || IsVoiceTakeBusy)
                return;

            if (_player?.State == PlayerState.Playing)
                _player.Pause();

            try
            {
                _voiceTakeId = _editor.BeginVoiceTake(PlayheadTicks, this);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Voice take could not begin: " + ex);
                SentryConfig.CaptureHandled(ex, "voiceover.begin");
                _ = NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning, ex.Message, VoiceErrorTitle);
                return;
            }

            _voiceCountdownValue = VoiceCountdownSeconds;
            voiceRecorder.Countdown = _voiceCountdownValue;
            _voiceCountdown = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, VoiceCountdown_Tick);
            _voiceCountdown.Start();
            RefreshVoiceRecordEnabled();
        }

        private void VoiceCountdown_Tick(object sender, EventArgs e)
        {
            if (_voiceCountdown == null)
                return;

            _voiceCountdownValue--;
            if (_voiceCountdownValue > 0)
            {
                voiceRecorder.Countdown = _voiceCountdownValue;
                return;
            }

            _voiceCountdown.Stop();
            _voiceCountdown = null;
            voiceRecorder.Countdown = null;
            StartVoiceRecording();
        }

        /// <summary>Abandons a countdown: the ghost (and any row the session made for it) goes
        /// away again, and nothing was written.</summary>
        private void CancelVoiceCountdown()
        {
            if (_voiceCountdown == null)
                return;

            _voiceCountdown.Stop();
            _voiceCountdown = null;
            voiceRecorder.Countdown = null;

            if (_voiceTakeId is Guid id)
            {
                _voiceTakeId = null;
                try
                {
                    _editor?.CancelVoiceTake(id);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Voice take cancel failed: " + ex);
                }
            }

            ApplyPendingVoiceDevice();
            RefreshVoiceRecordEnabled();
        }

        /// <summary>The countdown ran out: the microphone starts writing, the take's clock starts,
        /// the preview plays from the playhead (muted if the user asked for that) and the tick
        /// timer grows the ghost. A player that has already reached the end has nothing to play:
        /// it holds its last frame and the take extends the project.</summary>
        private void StartVoiceRecording()
        {
            if (_voiceTakeId is not Guid takeId || _closing)
                return;

            var path = NewVoiceTakePath();
            try
            {
                _mic.StartRecording(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Voice recording could not start: " + ex);
                SentryConfig.CaptureHandled(ex, "voiceover.start");
                _voiceTakeId = null;
                try { _editor.CancelVoiceTake(takeId); }
                catch (Exception cancelEx) { Debug.WriteLine("Voice take cancel failed: " + cancelEx); }
                TryDeleteFile(path);
                ApplyPendingVoiceDevice(); // a mic picked during the countdown is not stranded
                RefreshVoiceRecordEnabled();
                _ = NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning,
                    "The microphone could not start recording: " + ex.Message, VoiceErrorTitle);
                return;
            }

            _voiceWavPath = path;
            _voiceClock.Restart();
            voiceRecorder.Elapsed = TimeSpan.Zero;
            voiceRecorder.IsRecording = true;

            if (_player != null)
            {
                // the take is measured on the wall clock and laid onto the project through the
                // speed warp alone, so the preview must run at 1x for its length: any other rate
                // would put the narration over the wrong stretch of picture. The picker is off
                // meanwhile (RefreshVoiceRecordEnabled) and the rate comes back at the stop.
                _player.PlaybackRate = 1.0;
                if (_player.State == PlayerState.Paused)
                    _player.Play();
            }

            _voiceTick = new DispatcherTimer(VoiceTickInterval, DispatcherPriority.Normal, VoiceTick_Tick);
            _voiceTick.Start();
            RefreshVoiceRecordEnabled();
        }

        /// <summary>Elapsed from the take's own clock, never the player's: the player stops at the
        /// project end while the take carries on.</summary>
        private void VoiceTick_Tick(object sender, EventArgs e)
        {
            if (_voiceTakeId is not Guid takeId || _voiceFinishing)
                return;

            var elapsed = _voiceClock.Elapsed;
            voiceRecorder.Elapsed = elapsed;
            try
            {
                _editor.UpdateVoiceTake(takeId, elapsed.Ticks);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Voice take update failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Ends the take's recording: the player pauses (its speed comes back), the microphone
        /// finalizes the file and reports how much audio reached it (the clip's length, so it
        /// never claims more than was recorded), and <see cref="CommitVoiceTake"/> turns the
        /// take into a clip. Inside an open drag the commit waits instead: the session refuses
        /// to add a clip under a gesture (it would ride on the drag's preview, unrecorded, and
        /// go with a cancel), so the complete file is held and a poll commits it the moment the
        /// drag ends. Safe to call from the stop button, Escape, Space, the transport, the mic
        /// dropping out, the overlay closing and the window closing; a no-op when no take is
        /// recording.
        /// </summary>
        private void StopVoiceTake()
        {
            if (!IsVoiceRecording || _voiceFinishing || _voiceTakeId is not Guid takeId)
                return;

            _voiceTick?.Stop();
            _voiceTick = null;
            _voiceClock.Stop();

            if (_player != null)
            {
                if (_player.State == PlayerState.Playing)
                    _player.Pause();
                _player.PlaybackRate = _playbackRate; // the take held it at 1x
            }

            TimeSpan recorded;
            try
            {
                recorded = _mic?.StopRecording() ?? _voiceClock.Elapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Voice recording stop failed: " + ex);
                recorded = _voiceClock.Elapsed;
            }

            voiceRecorder.IsRecording = false;
            voiceRecorder.Elapsed = TimeSpan.Zero;
            _voicePendingFinish = (_voiceWavPath, recorded);
            _voiceWavPath = null;

            if (_editor.IsGestureActive && !_closing)
            {
                // the ghost holds the take's final length while the drag runs its course
                try
                {
                    _editor.UpdateVoiceTake(takeId, recorded.Ticks);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Voice take update failed: " + ex.Message);
                }

                _voiceFinishPoll = new DispatcherTimer(VoiceTickInterval, DispatcherPriority.Normal, VoiceFinishPoll_Tick);
                _voiceFinishPoll.Start();
                RefreshVoiceRecordEnabled();
                return;
            }

            CommitVoiceTake();
        }

        private void VoiceFinishPoll_Tick(object sender, EventArgs e)
        {
            if (_editor == null || _editor.IsGestureActive)
                return;

            CommitVoiceTake();
        }

        /// <summary>
        /// The second half of the stop: the session turns the recorded file into a selected clip
        /// on the ghost's row, leaving the playhead at the clip's end. Anything that fails before
        /// the clip is in the project cancels the take, removes the partial file and says so the
        /// way an import failure is reported; a failure after it (a listener of the change) keeps
        /// the clip and its file. Waits (a no-op) while the drag it was deferred behind is still
        /// open, except on the way out of the window, where the take can only be abandoned: the
        /// drag's owner cancels the drag after the window is gone and would take the clip with
        /// it, so the file is kept on disk and nothing is written into the project.
        /// </summary>
        private void CommitVoiceTake()
        {
            if (_voiceFinishing || _voiceTakeId is not Guid takeId || _voicePendingFinish is not { } pending)
                return;
            if (_editor.IsGestureActive && !_closing)
                return;

            var (path, recorded) = pending;
            _voiceFinishPoll?.Stop();
            _voiceFinishPoll = null;
            _voiceFinishing = true;
            _voiceTakeId = null;
            _voicePendingFinish = null;

            Item item = null;
            string error = null;
            var keepFile = true;
            try
            {
                if (_editor.IsGestureActive)
                {
                    _editor.CancelVoiceTake(takeId);
                    Debug.WriteLine("Voice take '" + path + "' abandoned: the window closed during a drag.");
                }
                else
                {
                    // the ghost's final length is what the file holds, so the clip end the
                    // re-seek lands on (see Editor_ProjectChanged) is exactly the clip's end
                    _editor.UpdateVoiceTake(takeId, recorded.Ticks);
                    _seekAfterTakeTicks = _editor.VoiceTakeGhost?.EndTicks;

                    // a local wav's header, read synchronously: the whole finish stays one step,
                    // so nothing (a close, another click) can slip in between the file and the clip
                    var probe = MediaProbe.ProbeDetailed(path);
                    item = _editor.FinishVoiceTake(takeId, path, probe, recorded.Ticks, this);
                    if (item == null)
                    {
                        error = "The recording could not be added to the timeline.";
                        keepFile = false;
                    }
                }
            }
            catch (Exception ex)
            {
                // the finish raises its change to the timeline and inspector before it returns:
                // a listener failing after the commit is not a failed take
                item = CommittedVoiceItem(path);
                if (item != null)
                {
                    Debug.WriteLine("Voice take committed, but a listener failed: " + ex);
                    SentryConfig.CaptureHandled(ex, "voiceover.finish-listener");
                }
                else
                {
                    Debug.WriteLine("Voice take finish failed: " + ex);
                    SentryConfig.CaptureHandled(ex, "voiceover.finish");
                    error = "The recording could not be added to the timeline: " + ex.Message;
                    keepFile = false;
                }
            }

            if (error != null)
            {
                _seekAfterTakeTicks = null;
                if (_editor.IsVoiceTakeActive)
                {
                    try { _editor.CancelVoiceTake(takeId); }
                    catch (Exception ex) { Debug.WriteLine("Voice take cancel failed: " + ex); }
                }
            }

            if (!keepFile)
                TryDeleteFile(path);

            _voiceFinishing = false;
            ApplyPendingVoiceDevice();
            RefreshVoiceRecordEnabled();

            if (_closing)
                return;

            if (item != null)
                RevealNewItem(item);
            else if (error != null)
                _ = NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning, error, VoiceErrorTitle);
        }

        /// <summary>The clip a finished take put in the project for <paramref name="path"/>,
        /// or null when nothing was committed: how the commit tells a failure of its own from
        /// a listener's, so a take that is in the project never has its file deleted.</summary>
        private Item CommittedVoiceItem(string path)
        {
            var project = _editor?.Project;
            var source = project?.Sources.FirstOrDefault(s => s.Path == path);
            if (source == null)
                return null;

            return project.Items.FirstOrDefault(i => i.Content is MediaContent media && media.SourceId == source.Id);
        }

        /// <summary>Ends whatever the recorder is doing on the way out of the window: a countdown
        /// is dropped, a take is finished (the audio is kept and the clip saved with the edit),
        /// and the microphone is released. Runs before the edit is flushed to disk.</summary>
        private void ShutdownVoiceRecorder()
        {
            if (IsVoiceCountingDown)
                CancelVoiceCountdown();
            if (IsVoiceRecording)
                StopVoiceTake();
            if (IsVoiceFinishPending)
                CommitVoiceTake();

            CloseMic();
        }

        /// <summary>
        /// Where a take is written: beside <c>videoedit.json</c>, with the session's other files
        /// (the waveform cache, the AI sidecars), or next to the file being edited in the dev
        /// harness, which has no session directory. Named by the wall clock, with a counter for a
        /// second take inside the same second.
        /// </summary>
        private string NewVoiceTakePath()
        {
            var dir = _editDocPath != null
                ? Path.GetDirectoryName(_editDocPath)
                : Path.GetDirectoryName(Path.GetFullPath(_videoPath));

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(dir, "voice-" + stamp + ".wav");
            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(dir, "voice-" + stamp + "-" + n.ToString(CultureInfo.InvariantCulture) + ".wav");

            return path;
        }

        private static void TryDeleteFile(string path)
        {
            if (String.IsNullOrEmpty(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Could not delete partial take '" + path + "': " + ex.Message);
            }
        }
    }
}
