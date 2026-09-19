using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Clowd.Config;
using Clowd.UI.Controls.Tray;

namespace Clowd.UI
{
    /// <summary>
    /// The floating recording strip: grip · primary (Start / timer) · microphone · system audio ·
    /// camera (Studio mode only) · options · end (Cancel before the recording starts, Finish after).
    /// A thin composition over the generic tray chassis: every window, placement, drag, rotate and
    /// tooltip behaviour is <see cref="FloatingTrayWindow"/>'s, the settings mirror and device audit
    /// are <see cref="RecordingSources"/>', the device menus are <see cref="RecordingDeviceMenu"/>'s,
    /// and the enable/lock/label rules are <see cref="RecordingSourceRules"/>'. What is left here is
    /// the wiring: which control raises which event, and the handful of single-writer update methods
    /// that turn the strip's three facts (waiting, recording, paused) plus the sources model into
    /// control state.
    /// <para>
    /// Deliberately decoupled from <see cref="VideoCapturePage"/> — it only raises events and persists
    /// the mic/speaker/camera toggles (and the device ids its own pickers write); the page wires the
    /// events and drives state via the Set* methods. The two physical transport tiles carry four
    /// commands, multiplexed here on <see cref="_recording"/>: the page never learns which tile was
    /// pressed, only what was asked for.
    /// </para>
    /// </summary>
    public sealed class RecordingFloatingButtons : FloatingTrayWindow
    {
        public event EventHandler StartClicked;
        public event EventHandler PauseToggleClicked;
        public event EventHandler FinishClicked;
        public event EventHandler CancelClicked;
        public event EventHandler SettingsClicked;

        /// <summary>Raised when the microphone toggle flips <see cref="SettingsRecording.CaptureMicrophone"/>
        /// (already written by the time this fires). This is the live mute path while recording — the
        /// page early-outs its settings subscription once frames flow — so it must fire on every
        /// user-driven turn-on/off, including a turn-on completed by a device pick.</summary>
        public event EventHandler<bool> MicToggled;

        /// <summary>As <see cref="MicToggled"/>, for <see cref="SettingsRecording.CaptureSpeaker"/>.</summary>
        public event EventHandler<bool> SpeakerToggled;

        /// <summary>Raised when the camera toggle flips <see cref="SettingsRecording.CaptureWebcam"/>
        /// (already written by the time this fires, like the mic/speaker toggles). Never raised
        /// while recording — the slot is disabled then — and never raised for a click that only
        /// opened the settings page because no camera has been picked yet.</summary>
        public event EventHandler<bool> WebcamToggled;

        private const string LockedSuffix = " · locked while recording";

        private readonly RecordingSources _sources;
        private readonly TrayPrimaryButton _primary;
        private readonly TraySplitToggle _mic;
        private readonly TraySplitToggle _spk;
        private readonly TraySplitToggle _cam;
        private readonly TrayButton _options;
        private readonly TrayButton _end;

        private bool _recording;
        private bool _paused;
        // the recorder is still being built (or rebuilt): the primary cannot act yet, so it waits
        // visibly until VideoCapturePage says otherwise.
        private bool _waiting = true;
        // the last elapsed time the page reported; null until the first status of a recording, so the
        // primary reads "00:00" from the moment the recording starts rather than a stale value.
        private TimeSpan? _elapsed;

        public RecordingFloatingButtons()
            : base(new FloatingTrayOptions { Title = "Clowd Recording Toolbar" })
        {
            // the model first: its constructor audits the capture toggles against the attached devices
            // and writes the result back before the recorder is spawned, so the settings file the
            // recorder reads already agrees with what the strip shows.
            _sources = new RecordingSources(SettingsRoot.Current.Recording);

            _primary = new TrayPrimaryButton();
            _primary.Click += (s, e) =>
            {
                // the same physical button: Start before recording, Pause/Resume after.
                if (_recording)
                    PauseToggleClicked?.Invoke(this, EventArgs.Empty);
                else
                    StartClicked?.Invoke(this, EventArgs.Empty);
            };

            _mic = BuildSource(CaptureSource.Microphone, TrayGlyphs.Mic, TrayGlyphs.MicOff, "Microphone");
            _spk = BuildSource(CaptureSource.Speaker, TrayGlyphs.Spk, TrayGlyphs.SpkOff, "System audio");
            _cam = BuildSource(CaptureSource.Webcam, TrayGlyphs.Cam, TrayGlyphs.CamOff, "Camera");

            _options = new TrayButton { Glyph = TrayGlyphs.Sliders };
            ToolTip.SetTip(_options, "Options");
            AutomationProperties.SetName(_options, "Options");
            _options.Click += (s, e) => SettingsClicked?.Invoke(this, EventArgs.Empty);

            _end = new TrayButton();
            _end.Click += (s, e) =>
            {
                // the same physical button: Cancel (discard) before recording, Finish (save) after —
                // a rolling recording can be stopped but not discarded from the strip.
                if (_recording)
                    FinishClicked?.Invoke(this, EventArgs.Empty);
                else
                    CancelClicked?.Invoke(this, EventArgs.Empty);
            };

            Tray.Items.Add(_primary);
            Tray.Items.Add(_mic);
            Tray.Items.Add(_spk);
            Tray.Items.Add(_cam);
            Tray.Items.Add(_options);
            Tray.Items.Add(_end);

            MirrorSources();
            ApplyMode();
            UpdateLocks();
            // the strip opens in the Waiting state: the primary locked, unaccented and still.
            UpdatePrimary();
            UpdateEnd();

            // the Options button opens the same settings this strip writes: without this, the toggles
            // above go stale and the next click would flip the *old* value back over the user's choice.
            // Subscribed last, after every control exists — the handler refreshes them.
            _sources.Changed += OnSourcesChanged;
        }

        // -- page-driven state --

        /// <summary>Waiting ⇄ Idle on the primary: locked and unaccented while the recorder is being
        /// built (or rebuilt), "Start" once it reports ready. Applies whether or not recording.</summary>
        public void SetWaiting(bool waiting)
        {
            _waiting = waiting;
            UpdatePrimary();
        }

        /// <summary>Modes the strip for a rolling recording: the primary becomes the Active timer
        /// (zeroed until the first status), the trailing Cancel button becomes Finish, and the
        /// camera slot and every device chevron lock (<see cref="UpdateLocks"/>). False is the
        /// symmetric reverse.</summary>
        public void SetRecordingState(bool recording)
        {
            _recording = recording;
            _paused = false;

            // an elapsed status can arrive before the page hears that frames are flowing, and the old
            // strip kept the text it was handed across that transition — so a status already in hand
            // survives the way in, and only the way out clears the label back to 00:00.
            _elapsed = recording ? _elapsed : null;
            UpdateLocks();
            UpdatePrimary();
            UpdateEnd();
        }

        /// <summary>Flips the primary between the Active timer and the amber Paused state. Statuses
        /// stop while paused, so the label freezes on its last value by itself. Only meaningful
        /// while recording.</summary>
        public void SetPausedState(bool paused)
        {
            if (!_recording)
                return;

            _paused = paused;
            UpdatePrimary();
        }

        /// <summary>The recording's elapsed time from the page's 1 Hz status feed — the primary's
        /// label. Not driven by a UI timer: the recorder is the only clock that knows about pauses.</summary>
        public void SetElapsed(TimeSpan elapsed)
        {
            _elapsed = elapsed;
            UpdatePrimary();
        }

        /// <summary>The recorder's frame rate, shown in the grip's tooltip: the primary is a fixed-width
        /// timer and the grip has no label, so the tooltip is the one surface left for it.</summary>
        public void SetFps(double fps)
        {
            ToolTip.SetTip(Grip, $"Drag to move · {fps:F0} FPS");
        }

        /// <summary>Drives the microphone / system-audio level bars from obs-express's 100 ms levels
        /// feed (the page forwards <see cref="ObsLevels"/>). Peak dBFS; null means that source does not
        /// exist or the capturer was torn down — the bar empties rather than freezing.</summary>
        public void SetAudioLevels(double? micDb, double? spkDb)
        {
            _mic.Level = TrayLevel.FromPeakDbfs(micDb);
            _spk.Level = TrayLevel.FromPeakDbfs(spkDb);
        }

        protected override void OnClosed(EventArgs e)
        {
            // unsubscribes from the settings and flushes a pending debounced save, so a quick
            // toggle-then-finish is not lost.
            _sources.Dispose();
            base.OnClosed(e);
        }

        // -- single writers --

        /// <summary>The single writer of the primary's state, label, tooltip and accessible name.</summary>
        private void UpdatePrimary()
        {
            var state = RecordingSourceRules.PrimaryState(_waiting, _recording, _paused);
            _primary.State = state;
            _primary.Label = _recording
                ? RecordingSourceRules.FormatElapsed(_elapsed)
                : state == TrayPrimaryState.Waiting ? "Wait…" : "Start";

            // at rest the glyph is the status and on hover it is the verb; the tooltip names the verb.
            var tip = state switch
            {
                TrayPrimaryState.Active => "Pause",
                TrayPrimaryState.Paused => "Resume",
                TrayPrimaryState.Waiting => "Starting the recorder…",
                _ => "Start recording",
            };
            ToolTip.SetTip(_primary, tip);
            AutomationProperties.SetName(_primary, tip);
        }

        /// <summary>The single writer of the end button's glyph, look, tooltip and accessible name:
        /// a quiet X that discards before the recording starts, a red stop that saves once it has.
        /// Same slot, same width — the strip never resizes across states.</summary>
        private void UpdateEnd()
        {
            _end.Glyph = _recording ? TrayGlyphs.Stop : TrayGlyphs.X;
            _end.Look = _recording ? TrayButtonLook.Danger : TrayButtonLook.Quiet;

            var tip = _recording ? "Finish and save" : "Cancel";
            ToolTip.SetTip(_end, tip);
            AutomationProperties.SetName(_end, tip);
        }

        /// <summary>
        /// The single writer of every IsEnabled, IsChevronEnabled and HasChevron on the three source
        /// slots (and, through <see cref="UpdateSourceToolTips"/>, of their tooltips). Audio is
        /// enumerated here and now (a local call), so this also catches a device that has appeared
        /// since the strip opened, every time it runs.
        /// </summary>
        private void UpdateLocks()
        {
            // Mic/system stay live while recording — they are mutes — but only where the recorder
            // actually built a source to mute. With no device there is nothing to unmute, and the
            // picker that would fix that is frozen too, so the toggle locks with it rather than
            // lighting up over silence.
            _mic.IsEnabled = RecordingSourceRules.SourceEnabled(_recording, _sources.HasDevice(CaptureSource.Microphone));
            _spk.IsEnabled = RecordingSourceRules.SourceEnabled(_recording, _sources.HasDevice(CaptureSource.Speaker));

            // the webcam is a pipeline element rather than a mute (there is no live equivalent of
            // the audio mutes), and the device ids are read when the pipeline is built, so neither
            // can change mid-recording: the camera slot and every chevron are disabled — not removed,
            // so the strip keeps its width — once frames flow.
            _cam.IsEnabled = !_recording;
            _mic.IsChevronEnabled = !_recording;
            _spk.IsChevronEnabled = !_recording;
            _cam.IsChevronEnabled = !_recording;

            // A chevron only exists when there is a choice to make with it — more than one real
            // device behind the source. Re-evaluated only while not recording: the device ids are
            // fixed once frames flow, and a chevron coming or going would change the strip's width
            // mid-recording. A change re-centres the strip on the region (a no-op once the user has
            // dragged it away).
            if (!_recording)
            {
                var changed = false;
                changed |= SetChevron(_mic, RecordingSourceRules.ShowsChevron(_sources.RealDeviceIds(CaptureSource.Microphone).Count));
                // never on macOS: ScreenCaptureKit hands over the whole system mix, so there is no
                // output device to choose (the same reason SpeakerDeviceId is [HiddenOnMacOS]).
                changed |= SetChevron(_spk, !OperatingSystem.IsMacOS()
                    && RecordingSourceRules.ShowsChevron(_sources.RealDeviceIds(CaptureSource.Speaker).Count));
                // …and the camera chevron only exists while the camera slot does (ApplyMode). The
                // camera list is null until the first enumeration lands, which counts as no devices.
                changed |= SetChevron(_cam, _cam.IsVisible
                    && RecordingSourceRules.ShowsChevron(_sources.RealDeviceIds(CaptureSource.Webcam).Count));

                if (changed)
                    QueueReposition();
            }

            UpdateSourceToolTips();
        }

        private static bool SetChevron(TraySplitToggle slot, bool hasChevron)
        {
            if (slot.HasChevron == hasChevron)
                return false;

            slot.HasChevron = hasChevron;
            return true;
        }

        /// <summary>The camera slot exists only in Studio mode: an Instant recording is one flattened
        /// track with nowhere for a camera to go, and the settings page hides the webcam rows for
        /// the same reason. Collapsed rather than disabled — it is not "unavailable right now", it
        /// is not part of this mode at all. The strip's width changes with it, so a change re-centres
        /// the strip on the region once the new layout has settled.</summary>
        private void ApplyMode()
        {
            var visible = _sources.Mode == RecordingMode.Studio;
            if (_cam.IsVisible == visible)
                return;

            _cam.IsVisible = visible;
            QueueReposition();
        }

        /// <summary>The single writer of the three slots' on/off state, straight from the settings
        /// model: the microphone and system-audio ticks, and for the camera whether a webcam will
        /// actually be recorded (tick, device and Studio mode — picking a device on the settings page
        /// is what lights the slot up after a click that could only send the user there).</summary>
        private void MirrorSources()
        {
            _mic.IsOn = _sources.IsEnabled(CaptureSource.Microphone);
            _spk.IsOn = _sources.IsEnabled(CaptureSource.Speaker);
            _cam.IsOn = _sources.IsEnabled(CaptureSource.Webcam);
            UpdateSourceToolTips();
        }

        /// <summary>The single writer of the source tooltips: the toggle half names its state, the
        /// chevron half the choice it opens, and both say so when a recording has locked them.</summary>
        private void UpdateSourceToolTips()
        {
            SetSourceToolTips(_mic, "Mic", "Choose microphone device");
            SetSourceToolTips(_spk, "System", "Choose system audio device");
            SetSourceToolTips(_cam, "Camera", "Choose camera device");
        }

        private static void SetSourceToolTips(TraySplitToggle slot, string name, string chevronTip)
        {
            slot.ToggleToolTip = name + (slot.IsOn ? " on" : " off") + (slot.IsEnabled ? "" : LockedSuffix);
            slot.ChevronToolTip = chevronTip + (slot.IsChevronEnabled ? "" : LockedSuffix);
            // The accessible name is the bare label (spec §10) and never grows the lock suffix: a screen
            // reader announcing "Choose microphone device · locked while recording" as the control's NAME
            // reads the reason every time it lands there, and the tooltip above already says it once.
            slot.ChevronName = chevronTip;
        }

        // -- sources --

        private TraySplitToggle BuildSource(CaptureSource source, TrayGlyph on, TrayGlyph off, string name)
        {
            var slot = new TraySplitToggle { OnGlyph = on, OffGlyph = off };
            AutomationProperties.SetName(slot, name);
            slot.ToggleClicked += (s, e) => OnSourceClicked(source);
            slot.ChevronClicked += (s, e) => OpenDeviceMenu(source, enableOnPick: false);
            return slot;
        }

        private TraySplitToggle Slot(CaptureSource source) => source switch
        {
            CaptureSource.Microphone => _mic,
            CaptureSource.Speaker => _spk,
            _ => _cam,
        };

        /// <summary>
        /// A source toggle was clicked. Mic/system are mutes and stay live while recording; the camera
        /// is not a mute — the recorder builds (or drops) a whole webcam source and a second encoder
        /// for it, which it will only do while it is still waiting — hence the lock once frames flow.
        /// Turning a source on with no device chosen opens its picker. Outside Studio mode the camera
        /// slot is hidden altogether (<see cref="ApplyMode"/>); the settings-page branch below is the
        /// guard for a click that somehow lands anyway.
        /// </summary>
        private void OnSourceClicked(CaptureSource source)
        {
            if (source == CaptureSource.Webcam && _recording)
                return;

            // a click is the moment a device that appeared since the strip opened (AirPods
            // connecting, a camera plugged in) has to be noticed, and the audit is a cheap
            // enumeration.
            UpdateLocks();

            var on = _sources.IsEnabled(source);

            if (!on && source == CaptureSource.Webcam && _sources.Mode != RecordingMode.Studio)
            {
                // the page's own handler owns the navigation (the strip never touches PageManager).
                SettingsClicked?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!on && !_sources.HasDevice(source))
            {
                TurnOnWithDevice(source);
                return;
            }

            SetSourceEnabled(source, !on);
        }

        /// <summary>
        /// Turning a source on that has nothing to record from. With exactly one device there is no
        /// choice to put to the user — adopt it and let the click through. Otherwise ask, and let
        /// the pick complete the turn-on; that also covers having no device at all, where the menu
        /// is what says so (and, for cameras, what offers to look again).
        /// </summary>
        private void TurnOnWithDevice(CaptureSource source)
        {
            var only = _sources.SingleDevice(source);
            if (only != null)
            {
                // the device first, so the toggle that follows is already backed by it.
                _sources.PickDevice(source, only);
                CompleteTurnOn(source);
            }
            else
            {
                OpenDeviceMenu(source, enableOnPick: true);
            }
        }

        /// <summary>The deferred half of a turn-on that first needed a device. The pick has already
        /// been written; for the camera that write alone may have lit the slot (the tick was on and
        /// only the device was missing), in which case there is nothing left to flip and nothing to
        /// announce.</summary>
        private void CompleteTurnOn(CaptureSource source)
        {
            if (!_sources.IsEnabled(source))
                SetSourceEnabled(source, true);
        }

        /// <summary>
        /// Drops the device picker for <paramref name="source"/> off its slot on the strip's free side.
        /// <paramref name="enableOnPick"/> is set when the menu was opened by a turn-on that had
        /// nowhere to record from: choosing a device then completes the click the user made.
        /// Opening the same menu from the chevron is a device change only and leaves the toggle alone.
        /// </summary>
        private void OpenDeviceMenu(CaptureSource source, bool enableOnPick)
        {
            var onPicked = enableOnPick ? () => CompleteTurnOn(source) : (Action)null;
            ShowMenu(RecordingDeviceMenu.Build(_sources, source, onPicked), Slot(source));
        }

        /// <summary>Writes the toggle to the settings (which the settings bus mirrors back into the
        /// slot and turns into a <c>configure</c> on the waiting recorder), THEN raises the toggle
        /// event — the page's handler is the live mute while recording, and it must find the setting
        /// already written.</summary>
        private void SetSourceEnabled(CaptureSource source, bool on)
        {
            _sources.SetEnabled(source, on);

            switch (source)
            {
                case CaptureSource.Microphone:
                    MicToggled?.Invoke(this, on);
                    break;
                case CaptureSource.Speaker:
                    SpeakerToggled?.Invoke(this, on);
                    break;
                default:
                    WebcamToggled?.Invoke(this, on);
                    break;
            }
        }

        /// <summary>Mirrors settings edited elsewhere (the recording settings page, the page reverting
        /// a webcam the recorder refused, or the camera list landing) back into the strip. A mode
        /// change (or a whole-object change, which is what the camera list reports) can add or remove
        /// the camera slot and any chevron, so those re-run the layout-affecting writers too.</summary>
        private void OnSourcesChanged(object sender, string propertyName)
        {
            MirrorSources();

            if (propertyName is null or "" or nameof(SettingsRecording.Mode))
            {
                ApplyMode();
                UpdateLocks();
            }
        }
    }
}
