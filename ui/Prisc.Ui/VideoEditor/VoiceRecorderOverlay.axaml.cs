using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Clowd.UI.Controls.Tray;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The voice-over recorder's face: a dark pill parked at the bottom of the video preview with a
    /// record/stop button, a live microphone level, the length of the take so far, which microphone
    /// it is listening to, and the two ways out (the recorder settings, and closing the recorder).
    /// The countdown before a take is drawn by this control too, centred over the preview, which is
    /// why the control fills the preview area rather than being only as big as the pill.
    /// <para>
    /// It owns no recording state of its own: the window opens the microphone, runs the countdown and
    /// the take, and pushes what it knows in through <see cref="IsRecording"/>,
    /// <see cref="LevelFraction"/>, <see cref="Elapsed"/>, <see cref="DeviceName"/> and
    /// <see cref="Countdown"/>. The four buttons are raised back out as plain events. Keyboard is the
    /// window's business as well (Esc and Space stop a take), so nothing here is focusable by key.
    /// </para>
    /// </summary>
    public partial class VoiceRecorderOverlay : UserControl
    {
        /// <summary>The level track's width, and so the fill width at a level of 1.</summary>
        private const double LevelTrackWidth = 64;

        /// <summary>
        /// Below this much room the device name is dropped from the pill. The preview shrinks with the
        /// window and the sidebar, and a pill wider than the preview would simply be clipped by it.
        /// </summary>
        private const double CompactWidth = 430;

        /// <summary>No microphone: the meter is a dim stub rather than a coloured bar, so "silent" and
        /// "not there" cannot be confused (the distinction <see cref="TrayLevel"/> exists to keep).</summary>
        private static readonly IBrush NoDeviceBrush = new ImmutableSolidColorBrush(Colors.White, 0.25);

        /// <summary>Whether a take is running. Swaps the red dot for a red square and the button's
        /// click for <see cref="StopClicked"/>.</summary>
        public static readonly StyledProperty<bool> IsRecordingProperty =
            AvaloniaProperty.Register<VoiceRecorderOverlay, bool>(nameof(IsRecording));

        /// <summary>Microphone level as 0..1 (see <see cref="TrayLevel.FromPeakDbfs"/>), or null when
        /// there is no device to listen to.</summary>
        public static readonly StyledProperty<double?> LevelFractionProperty =
            AvaloniaProperty.Register<VoiceRecorderOverlay, double?>(nameof(LevelFraction));

        /// <summary>How long the current take has been running; zero when idle.</summary>
        public static readonly StyledProperty<TimeSpan> ElapsedProperty =
            AvaloniaProperty.Register<VoiceRecorderOverlay, TimeSpan>(nameof(Elapsed));

        /// <summary>Friendly name of the open microphone.</summary>
        public static readonly StyledProperty<string> DeviceNameProperty =
            AvaloniaProperty.Register<VoiceRecorderOverlay, string>(nameof(DeviceName));

        /// <summary>The countdown number to show over the preview (3, 2, 1), or null for none.</summary>
        public static readonly StyledProperty<int?> CountdownProperty =
            AvaloniaProperty.Register<VoiceRecorderOverlay, int?>(nameof(Countdown));

        /// <summary>Whether a take can be started right now. False greys the record button out.</summary>
        public static readonly StyledProperty<bool> IsRecordEnabledProperty =
            AvaloniaProperty.Register<VoiceRecorderOverlay, bool>(nameof(IsRecordEnabled), true);

        public bool IsRecording
        {
            get => GetValue(IsRecordingProperty);
            set => SetValue(IsRecordingProperty, value);
        }

        public double? LevelFraction
        {
            get => GetValue(LevelFractionProperty);
            set => SetValue(LevelFractionProperty, value);
        }

        public TimeSpan Elapsed
        {
            get => GetValue(ElapsedProperty);
            set => SetValue(ElapsedProperty, value);
        }

        public string DeviceName
        {
            get => GetValue(DeviceNameProperty);
            set => SetValue(DeviceNameProperty, value);
        }

        public int? Countdown
        {
            get => GetValue(CountdownProperty);
            set => SetValue(CountdownProperty, value);
        }

        public bool IsRecordEnabled
        {
            get => GetValue(IsRecordEnabledProperty);
            set => SetValue(IsRecordEnabledProperty, value);
        }

        /// <summary>The record button was pressed while idle.</summary>
        public event EventHandler RecordClicked;

        /// <summary>The record button was pressed while a take was running.</summary>
        public event EventHandler StopClicked;

        /// <summary>The gear was pressed: show the recorder's settings in the properties panel.</summary>
        public event EventHandler SettingsClicked;

        /// <summary>The X was pressed: hide the recorder and un-toggle the tool button.</summary>
        public event EventHandler CloseClicked;

        public VoiceRecorderOverlay()
        {
            InitializeComponent();

            btnRecord.Click += (s, e) =>
            {
                if (IsRecording)
                    StopClicked?.Invoke(this, EventArgs.Empty);
                else
                    RecordClicked?.Invoke(this, EventArgs.Empty);
            };
            btnSettings.Click += (s, e) => SettingsClicked?.Invoke(this, EventArgs.Empty);
            btnClose.Click += (s, e) => CloseClicked?.Invoke(this, EventArgs.Empty);

            Avalonia.Automation.AutomationProperties.SetName(btnSettings, "Recorder settings");
            ToolTip.SetTip(btnSettings, "Recorder settings");
            Avalonia.Automation.AutomationProperties.SetName(btnClose, "Close");
            ToolTip.SetTip(btnClose, "Close");

            ApplyRecordState();
            ApplyLevel();
            ApplyElapsed();
            ApplyDeviceName();
            ApplyCountdown();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsRecordingProperty)
            {
                ApplyRecordState();
                ApplyLevel(); // the fill changes colour with the state
            }
            else if (change.Property == LevelFractionProperty)
            {
                ApplyLevel();
            }
            else if (change.Property == ElapsedProperty)
            {
                ApplyElapsed();
            }
            else if (change.Property == DeviceNameProperty)
            {
                ApplyDeviceName();
            }
            else if (change.Property == CountdownProperty)
            {
                ApplyCountdown();
            }
            else if (change.Property == IsRecordEnabledProperty)
            {
                ApplyRecordState();
            }
            else if (change.Property == BoundsProperty)
            {
                ApplyCompact();
            }
        }

        /// <summary>
        /// The take's length as the pill shows it: <c>0:07</c>, growing to <c>1:02:30</c> only once a
        /// take runs past the hour, so the common case stays as short as the design's example.
        /// </summary>
        public static string FormatElapsed(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
                value = TimeSpan.Zero;

            return value.TotalHours >= 1
                ? String.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}",
                    (int)value.TotalHours, value.Minutes, value.Seconds)
                : String.Format(CultureInfo.InvariantCulture, "{0}:{1:00}",
                    (int)value.TotalMinutes, value.Seconds);
        }

        private void ApplyRecordState()
        {
            var recording = IsRecording;
            recordDot.IsVisible = !recording;
            recordSquare.IsVisible = recording;

            // a take always stops, whatever the enabled flag says about starting one
            btnRecord.IsEnabled = recording || IsRecordEnabled;

            var label = recording ? "Stop" : "Record";
            ToolTip.SetTip(btnRecord, label);
            Avalonia.Automation.AutomationProperties.SetName(btnRecord, label);
        }

        private void ApplyLevel()
        {
            var level = LevelFraction;

            // No device reads as the 8px stub in a neutral grey. Otherwise the fill is the tray's
            // green while the mic is only being monitored and its red while a take is recording, so
            // the bar says what the button says without being read twice.
            levelFill.Background = level == null
                ? NoDeviceBrush
                : IsRecording ? TrayTokens.RecBrush : TrayTokens.OkBrush;

            levelFill.Width = level == null
                ? TrayTokens.LevelMinWidth
                : Math.Max(TrayTokens.LevelMinWidth, Math.Clamp(level.Value, 0, 1) * LevelTrackWidth);
        }

        private void ApplyElapsed()
        {
            txtElapsed.Text = FormatElapsed(Elapsed);
        }

        private void ApplyDeviceName()
        {
            var name = DeviceName;
            txtDevice.Text = name;
            // the label is trimmed at 160px, so the full name has to be available somewhere
            ToolTip.SetTip(txtDevice, String.IsNullOrEmpty(name) ? null : name);
            ApplyCompact();
        }

        private void ApplyCountdown()
        {
            var value = Countdown;
            txtCountdown.Text = value?.ToString(CultureInfo.CurrentCulture);
            countdownDisc.IsVisible = value != null;
        }

        private void ApplyCompact()
        {
            var room = Bounds.Width;
            var show = !String.IsNullOrEmpty(DeviceName) && (room <= 0 || room >= CompactWidth);
            deviceGroup.IsVisible = show;
            dividerDevice.IsVisible = show;
        }
    }
}
