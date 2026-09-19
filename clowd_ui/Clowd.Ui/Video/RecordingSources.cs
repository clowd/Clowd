using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.Config;

namespace Clowd.UI
{
    /// <summary>The three sources the recording strip toggles, each with a device behind it.</summary>
    public enum CaptureSource
    {
        Microphone,
        Speaker,
        Webcam,
    }

    /// <summary>
    /// The recording strip's model: the <see cref="SettingsRecording"/> mirror, the device audit and
    /// the debounced persistence, with no controls in it. Everything here used to live in the old
    /// shared toolbar window, where a share session could reach it and every method had to check which
    /// strip it was on; as a separate type the fence is the compiler's — a strip that does not own one
    /// of these cannot read a setting or enumerate a device.
    /// <para>
    /// It is the only place the strip reaches <see cref="AudioDeviceManager"/> or
    /// <see cref="CameraDeviceManager"/>, and the only place that writes settings. The pure decisions
    /// live in <see cref="RecordingSourceRules"/>; what is left here is the state that cannot be
    /// pure: the settings object, the camera list and the save timer.
    /// </para>
    /// <para>
    /// Nothing in here raises <see cref="Changed"/> for its own writes. A capture toggle written here
    /// comes back through <see cref="SettingsRecording"/>'s PropertyChanged like a toggle written by
    /// the settings page, so the strip has one refresh path instead of two.
    /// </para>
    /// </summary>
    public sealed class RecordingSources : IDisposable
    {
        private readonly SettingsRecording _settings;
        private readonly DispatcherTimer _saveDebounce;

        /// <summary>Tri-state on purpose: <c>null</c> until the first enumeration lands (which is not
        /// the same as "no cameras" — see <see cref="HasDevice"/> and <see cref="RealDeviceIds"/>),
        /// then the list, empty or not.</summary>
        private List<CameraDeviceInfo> _cameras;

        /// <summary>Set by <see cref="Dispose"/>. The camera enumeration is fire-and-forget, so its
        /// continuation can land after the strip has closed — where auditing devices would write
        /// settings on behalf of a strip nobody can see, and the save it queues would sit on a timer
        /// nobody flushes.</summary>
        private bool _disposed;

        /// <summary>
        /// The order below is load-bearing and is the old <c>InitializeRecordingControls</c> order:
        /// the settings are captured first, the audit runs before anything can await (the camera
        /// continuation calls it again and reads the same field), the camera enumeration is started
        /// fire-and-forget, and the settings subscription is taken last so the audit's own writes do
        /// not come back as changes the strip has to mirror before it exists.
        /// </summary>
        public RecordingSources(SettingsRecording settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            // nothing in the settings graph saves itself, and the Recording page's auto-save only
            // attaches when that page is opened — the strip persists its own toggles (debounced).
            // Created before the audit below, which can queue a save.
            _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _saveDebounce.Tick += (s, e) =>
            {
                _saveDebounce.Stop();
                SaveSettings();
            };

            DisableCaptureWithoutDevice();

            // re-enumerate rather than take the process-wide cache: the strip opens long after the
            // app did, and a camera plugged in since then is exactly the one being reached for.
            // Native enumeration makes this ~60 ms, so it is affordable on every strip.
            _ = RefreshCamerasAsync();

            // the options button opens the same settings this strip writes: without this, the
            // mirrored toggles go stale and the next mic/system click would flip the *old* value
            // back over the user's choice.
            _settings.PropertyChanged += OnSettingsChanged;
        }

        /// <summary>
        /// Raised when something the strip mirrors has changed: the property name for a settings
        /// change the strip cares about (<c>null</c> or <c>""</c> pass through as the settings bus's
        /// own "everything" signal), and <c>null</c> when the camera list lands — which is also an
        /// "everything" for the strip, since a camera arriving can change what the webcam toggle
        /// means.
        /// </summary>
        public event EventHandler<string> Changed;

        /// <summary>The recording mode, which gates the webcam entirely: an Instant recording is one
        /// flattened track with nowhere for a camera to go.</summary>
        public RecordingMode Mode => _settings.Mode;

        /// <summary>
        /// Whether <paramref name="source"/> will be recorded as things stand. Mic and system audio
        /// are the tick alone; a webcam is only captured when the box is ticked, a camera has been
        /// chosen and Studio mode is on — <c>ObsArguments.WriteSettingsFile</c> writes an empty device
        /// id (i.e. no webcam source at all) when any of those is missing, so the button says the
        /// same rather than lighting up for a camera that will not be recorded.
        /// </summary>
        public bool IsEnabled(CaptureSource source) => source switch
        {
            CaptureSource.Microphone => _settings.CaptureMicrophone,
            CaptureSource.Speaker => _settings.CaptureSpeaker,
            _ => ObsArguments.UsesWebcam(_settings),
        };

        /// <summary>
        /// Whether <paramref name="source"/> has a device that can actually be recorded right now.
        /// A stored id for a device that has since been unplugged counts as none: the recorder would
        /// open nothing and the user would find that out after the recording, not before.
        /// </summary>
        public bool HasDevice(CaptureSource source) => source switch
        {
            CaptureSource.Microphone => RecordingSourceRules.HasAudioDevice(
                _settings.MicrophoneDeviceId, AudioDeviceIds(CaptureSource.Microphone), AudioDeviceManager.DefaultDeviceId),

            // there is no output device to pick on macOS — ScreenCaptureKit hands over the whole
            // system mix — so speaker capture is never blocked on one there.
            CaptureSource.Speaker => OperatingSystem.IsMacOS()
                || RecordingSourceRules.HasAudioDevice(
                    _settings.SpeakerDeviceId, AudioDeviceIds(CaptureSource.Speaker), AudioDeviceManager.DefaultDeviceId),

            // cameras are never enumerated here — a click has to answer immediately — so this
            // reads the list the strip already has. Until it lands a stored id is taken at face
            // value; after it does, an id no longer among them is no device (DisableCaptureWithout-
            // Device runs again at that point, which is what makes the deferred check count).
            _ => RecordingSourceRules.HasCameraDevice(_settings.WebcamDeviceId, CameraIds()),
        };

        /// <summary>
        /// The live audio enumeration behind <paramref name="source"/> — names as well as ids, which is
        /// what a device menu lists — with the "default" pseudo-device still in it, since only the rules
        /// decide what that one is worth. A local WASAPI/CoreAudio call, so it runs on every ask and
        /// picks up a device attached since the strip opened.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A webcam is not an audio source; the camera
        /// list is <see cref="RefreshCamerasAsync"/>'s, and asking for it here is a caller bug rather
        /// than a list to fall back on.</exception>
        public IReadOnlyList<AudioDeviceInfo> AudioDevices(CaptureSource source) => source switch
        {
            CaptureSource.Speaker => AudioDeviceManager.GetSpeakers(),
            CaptureSource.Microphone => AudioDeviceManager.GetMicrophones(),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "A webcam is not an audio source."),
        };

        /// <summary>
        /// The devices <paramref name="source"/> could actually be switched between, with the
        /// "default" pseudo-device dropped (<see cref="RecordingSourceRules.RealDeviceIds"/>). Audio
        /// is enumerated here and now — a local WASAPI/CoreAudio call — so this also catches a device
        /// that has appeared since the strip opened, every time it runs.
        /// </summary>
        public IReadOnlyList<string> RealDeviceIds(CaptureSource source)
        {
            switch (source)
            {
                case CaptureSource.Microphone:
                case CaptureSource.Speaker:
                    return RecordingSourceRules.RealDeviceIds(AudioDeviceIds(source), AudioDeviceManager.DefaultDeviceId);

                default:
                    // null (not enumerated yet) is not "no cameras" — it is "we cannot say", which
                    // an empty list expresses well enough for both callers: no chevron, and no
                    // shortcut, so a click opens the menu that does the waiting.
                    return CameraIds() ?? Array.Empty<string>();
            }
        }

        /// <summary>The only device <paramref name="source"/> could record from, or null when there
        /// is a choice to make (or nothing to choose).</summary>
        public string SingleDevice(CaptureSource source)
        {
            var ids = RealDeviceIds(source);
            return ids.Count == 1 ? ids[0] : null;
        }

        /// <summary>The device id stored for <paramref name="source"/> — what a picker checks, which
        /// is not necessarily something still attached (see <see cref="HasDevice"/>).</summary>
        public string DeviceId(CaptureSource source) => source switch
        {
            CaptureSource.Microphone => _settings.MicrophoneDeviceId,
            CaptureSource.Speaker => _settings.SpeakerDeviceId,
            _ => _settings.WebcamDeviceId,
        };

        /// <summary>Writes a capture toggle and queues the save. The strip raises its own event
        /// afterwards — the settings write has to happen first, so a handler that reads the settings
        /// back (the page's live mute path does) sees the new value.</summary>
        public void SetEnabled(CaptureSource source, bool enabled)
        {
            switch (source)
            {
                case CaptureSource.Microphone:
                    _settings.CaptureMicrophone = enabled;
                    break;

                case CaptureSource.Speaker:
                    _settings.CaptureSpeaker = enabled;
                    break;

                default:
                    _settings.CaptureWebcam = enabled;
                    break;
            }

            QueueSettingsSave();
        }

        /// <summary>Applies a device chosen from one of the pickers. Writing the settings property is
        /// the whole of it — VideoCapturePage turns the change into a <c>configure</c> on the waiting
        /// recorder, exactly as it does for the settings page. The deferred turn-on that opened the
        /// menu is the caller's half and runs after this: the settings file writes an empty
        /// webcam_device for either half missing, so the device has to land first.</summary>
        public void PickDevice(CaptureSource source, string deviceId)
        {
            switch (source)
            {
                case CaptureSource.Microphone:
                    _settings.MicrophoneDeviceId = deviceId;
                    break;

                case CaptureSource.Speaker:
                    _settings.SpeakerDeviceId = deviceId;
                    break;

                default:
                    _settings.WebcamDeviceId = deviceId;
                    break;
            }

            QueueSettingsSave();
        }

        /// <summary>
        /// Enumerates the cameras (off the UI thread) and takes the list as the strip's own: the
        /// open-time refresh and every camera-picker open go through here, so the list is current
        /// without the user having to ask for a refresh. Re-runs the audit the constructor could only
        /// half-run — the webcam's stored id had nothing to be checked against until now — and reports
        /// the result as a general change.
        /// </summary>
        public async Task<IReadOnlyList<CameraDeviceInfo>> RefreshCamerasAsync()
        {
            var pending = CameraDeviceManager.RefreshAsync();

            try
            {
                _cameras = await pending;
            }
            catch (Exception ex)
            {
                // CameraDeviceManager never throws, so this is a task-scheduling failure only.
                Debug.WriteLine("Failed to list cameras for the toolbar: " + ex);
                _cameras = new List<CameraDeviceInfo>();
            }

            // the strip can close while the enumeration is in flight (the constructor starts it
            // fire-and-forget, and a picker's refresh outlives a dismissed menu). Past Dispose there
            // is nothing left to audit for and no listener to tell, and writing settings here would
            // undo a tick on behalf of a strip that is gone.
            if (_disposed)
                return _cameras;

            // the camera half of the open-time audit could not run in the constructor: there was no
            // list to check the stored id against yet. This is that moment.
            DisableCaptureWithoutDevice();

            // no property name: a camera landing can change what the webcam toggle means, whether a
            // picker is worth offering, and whether the tick survived the audit at all.
            Changed?.Invoke(this, null);

            return _cameras;
        }

        public void Dispose()
        {
            _disposed = true;
            _settings.PropertyChanged -= OnSettingsChanged;

            // flush a pending debounced save so a quick toggle-then-finish isn't lost
            if (_saveDebounce.IsEnabled)
            {
                _saveDebounce.Stop();
                SaveSettings();
            }
        }

        /// <summary>Mirrors settings edited elsewhere (the recording settings page, or the page
        /// reverting a webcam the recorder refused) back out to the strip: the capture toggles drive
        /// the mic/system/camera glyphs and the level bars.</summary>
        private void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (null
                or "" or nameof(SettingsRecording.CaptureMicrophone) or nameof(SettingsRecording.CaptureSpeaker)
                or nameof(SettingsRecording.CaptureWebcam) or nameof(SettingsRecording.WebcamDeviceId)
                // gates the webcam entirely: leaving Studio mode unlights the camera slot.
                or nameof(SettingsRecording.Mode)))
                return;

            Changed?.Invoke(this, e.PropertyName);
        }

        /// <summary>
        /// A capture toggle that survived from a previous session pointing at a device that is no
        /// longer there is a promise the recording cannot keep — it would come back with a silent
        /// track, or no track at all. Runs while the strip is being built (before the recorder is
        /// spawned, so the settings file it reads already agrees) — which is also why it writes the
        /// settings rather than only the buttons — and again when the camera list lands, since the
        /// webcam's id has nothing to be checked against until then.
        /// </summary>
        private void DisableCaptureWithoutDevice()
        {
            var changed = false;

            if (_settings.CaptureMicrophone && !HasDevice(CaptureSource.Microphone))
            {
                _settings.CaptureMicrophone = false;
                changed = true;
            }

            if (_settings.CaptureSpeaker && !HasDevice(CaptureSource.Speaker))
            {
                _settings.CaptureSpeaker = false;
                changed = true;
            }

            // Instant mode is not a missing device — the camera rows are merely hidden, and the
            // user's tick is still what they will get back when they switch to Studio again.
            if (_settings.CaptureWebcam && _settings.Mode == RecordingMode.Studio && !HasDevice(CaptureSource.Webcam))
            {
                _settings.CaptureWebcam = false;
                changed = true;
            }

            if (changed)
                QueueSettingsSave();
        }

        /// <summary>The ids the audio enumerator currently offers for <paramref name="source"/>,
        /// "default" included — the rules decide what that one is worth. A projection of
        /// <see cref="AudioDevices"/> so there is one enumeration site.</summary>
        private IReadOnlyList<string> AudioDeviceIds(CaptureSource source)
            => AudioDevices(source).Select(d => d.DeviceId).ToList();

        /// <summary>The ids of the cameras the strip has been told about, <c>null</c> while the list
        /// itself is null — the tri-state of <see cref="_cameras"/> is load-bearing in
        /// <see cref="HasDevice"/>, so this passes it through rather than flattening it.</summary>
        private IReadOnlyList<string> CameraIds() => _cameras?.Select(c => c.DeviceId).ToList();

        private void QueueSettingsSave()
        {
            // after Dispose the debounce would never tick anywhere anyone flushes it, and Dispose has
            // already written whatever was pending.
            if (_disposed)
                return;

            _saveDebounce.Stop();
            _saveDebounce.Start();
        }

        private void SaveSettings()
        {
            try
            {
                SettingsService.Save(SettingsRoot.Current);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to save recording toggle settings: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "video.save-toggle-settings");
            }
        }
    }
}
