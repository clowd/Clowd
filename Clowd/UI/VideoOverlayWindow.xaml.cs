using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Clowd.Capture;
using Clowd.Config;
using Clowd.Video;
using Clowd.UI.Helpers;
using Clowd.Util;
using ScreenVersusWpf;

namespace Clowd.UI
{
    [PropertyChanged.ImplementPropertyChanged]
    public partial class VideoOverlayWindow : OverlayWindow
    {
        public bool IsRecording { get; set; }
        public bool IsStarted { get; set; }
        public bool IsAudioSupported { get; set; }

        public bool IsMicrophoneEnabled { get; set; }
        public bool IsLoopbackEnabled { get; set; }

        private bool _isCancelled = false;
        private NAudioItem speaker;
        private NAudioItem mic;
        private System.Timers.Timer audioTimer;
        private Point? _moveMouseDown;
        private Point? _moveInitial;
        private IVideoCapturer _capturer;

        public VideoOverlayWindow(WpfRect captureArea)
        {
            SelectionRectangle = captureArea;
            InitializeComponent();

            _recording = new LiveScreenRecording(captureArea.ToScreenRect().ToSystem());
            _recording.LogReceived += Recording_LogRecieved;

            var settings = App.Current.Settings.VideoSettings;
            settings.VideoCodec.PropertyChanged += SavedPresets_PropertyChanged;
            settings.VideoCodec.SavedPresets.PropertyChanged += SavedPresets_PropertyChanged;

            this.Closed += (s, e) =>
            {
                App.Current.Settings.VideoSettings.VideoCodec.PropertyChanged -= SavedPresets_PropertyChanged;
                App.Current.Settings.VideoSettings.VideoCodec.SavedPresets.PropertyChanged -= SavedPresets_PropertyChanged;

                mic?.Dispose();
                speaker?.Dispose();
                mic = speaker = null;
                audioTimer.Enabled = false;
                audioTimer.Dispose();
            };

            this.Loaded += (s, e) =>
            {
                UpdateButtonPanelPosition(toolActionBarStackPanel);
            };

            UpdateAudioState();

            // start audio update timer
            audioTimer = new System.Timers.Timer(20);
            audioTimer.Elapsed += AudioTimer_Elapsed;
            audioTimer.AutoReset = true;
            audioTimer.Enabled = true;
            audioTimer.Start();

            recordingLabelButton.PreviewMouseDown += RecordingLabelButton_MouseDown;
            recordingLabelButton.PreviewMouseMove += RecordingLabelButton_MouseMove;
            recordingLabelButton.PreviewMouseUp += RecordingLabelButton_MouseUp;
        }

        private void RecordingLabelButton_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            recordingLabelButton.ReleaseMouseCapture();
            _moveMouseDown = null;
            _moveInitial = null;
        }

        private void RecordingLabelButton_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            e.Handled = true;
            if (!_moveMouseDown.HasValue)
                return;

            var mouse = e.GetPosition(this);

            var xdelta = mouse.X - _moveMouseDown.Value.X;
            var ydelta = mouse.Y - _moveMouseDown.Value.Y;

            Canvas.SetLeft(toolActionBarStackPanel, _moveInitial.Value.X + xdelta);
            Canvas.SetTop(toolActionBarStackPanel, _moveInitial.Value.Y + ydelta);
        }

        private void RecordingLabelButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            recordingLabelButton.CaptureMouse();
            _moveMouseDown = e.GetPosition(this);

            var top = Canvas.GetTop(toolActionBarStackPanel);
            var left = Canvas.GetLeft(toolActionBarStackPanel);
            _moveInitial = new Point(left, top);
        }

        private void AudioTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            // don't bother the UI thread if we're not capturing audio.
            if (mic == null && speaker == null)
                return;

            this.Dispatcher.Invoke(() =>
            {
                levelSpeaker.Value = ConvertLevelToDb(speaker);
                levelMic.Value = ConvertLevelToDb(mic);
            });
        }

        private double ConvertLevelToDb(NAudioItem item)
        {
            if (item == null)
                return 0;

            double level = item.PeakLevel;

            if (level > 0 && level <= 1)
                return (20 * Math.Log10(level)) / 60 * 100 + 100;

            return 0;
        }

        private void SavedPresets_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UpdateAudioState();
        }

        //private void Recording_LogRecieved(object sender, FFMpegLogEventArgs e)
        //{
        //    //frame=  219 fps= 31 q=10.0 size=       0kB time=00:00:05.80 bitrate=   0.1kbits/s dup=5 drop=0 speed=0.82x

        //    string getData(string label)
        //    {
        //        var msg = e.Data;
        //        var start = msg.IndexOf(label);
        //        if (start < 0)
        //            return null;
        //        msg = msg.Substring(start + label.Length).TrimStart();
        //        msg = msg.Substring(0, msg.IndexOf(" "));
        //        if (msg == "0.0" || msg == "00:00:00.00")
        //            return null;

        //        return msg;
        //    }

        //    var fps = getData("fps=");
        //    var time = getData("time=");
        //    TimeSpan ts = default(TimeSpan);

        //    if (time != null)
        //    {
        //        try
        //        {
        //            // sometimes ffmpeg gives us garbage timecodes, it depends on the input stream timestamp on the frames & the settings we use.
        //            ts = TimeSpan.Parse(time);
        //        }
        //        catch { }
        //    }

        //    Dispatcher.Invoke(() =>
        //    {
        //        if (fps != null && (ts == default(TimeSpan) || DateTime.Now.Ticks / (4 * TimeSpan.TicksPerSecond) % 2 == 0))
        //        {
        //            recordingLabelButton.Text = fps + " FPS";
        //        }
        //        else if (ts != default(TimeSpan))
        //        {
        //            recordingLabelButton.Text = $"{((int)ts.TotalMinutes):D2}:{((int)ts.Seconds):D2}";
        //        }
        //    });
        //}

        private async void StartRecording()
        {
            IsStarted = true;
            UpdateAudioState();
            labelCountdown.Visibility = Visibility.Visible;

            for (int i = 4; i >= 1; i--)
            {
                labelCountdown.Text = i.ToString();
                labelCountdown.FontSize = 120;
                recordingLabelButton.Text = "REC in " + i.ToString();
                await Task.Delay(1000);
                if (_isCancelled)
                    return;
            }

            labelCountdown.Visibility = Visibility.Collapsed;
            recordingLabelButton.Text = "Starting";

            IsRecording = true;

            try
            {
                await _recording.Start();
            }
            catch (Exception ex)
            {
                this.Close();

                var filename = "ffmpeg_error_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                filename = System.IO.Path.Combine(System.IO.Path.GetFullPath(_recording.OutputDirectory), filename);

                File.WriteAllText(filename, _recording.ConsoleLog);
                File.AppendAllText(filename, Environment.NewLine + Environment.NewLine + ex.ToString());

                if (await NiceDialog.ShowPromptAsync(this, NiceDialogIcon.Error, "An unexpected error was encountered while trying to start recording. A log file has been created in your video output directory.", "Open Error Log"))
                {
                    Process.Start("notepad.exe", filename);
                }
            }
        }

        public void UpdateAudioState()
        {
            // dispose of all sounds stuff and re-create it. this doesn't happen too often (when settings change) so it's okay
            mic?.Dispose();
            speaker?.Dispose();
            mic = speaker = null;
            levelSpeaker.Value = levelMic.Value = 0;

            if (App.Current.Settings.VideoSettings.VideoCodec.GetSelectedPreset() is FFmpegCodecPreset_AudioBase audio)
            {
                IsAudioSupported = !IsStarted;
                IsMicrophoneEnabled = audio.CaptureMicrophone;
                IsLoopbackEnabled = audio.CaptureLoopbackAudio;

                if (IsMicrophoneEnabled && (audio.SelectedMicrophone != null || audio.SelectedMicrophone.FriendlyName != mic?.Name))
                {
                    mic?.Dispose();
                    mic = NAudioItem.Microphones.FirstOrDefault(m => m.Name == audio.SelectedMicrophone.FriendlyName);
                    mic?.StartListeningForPeakLevel();
                }

                if (IsLoopbackEnabled && speaker == null)
                {
                    speaker = NAudioItem.DefaultSpeaker;
                    speaker?.StartListeningForPeakLevel();
                }
            }
            else
            {
                IsMicrophoneEnabled = IsLoopbackEnabled = IsAudioSupported = false;
            }
        }

        private void buttonStart_Click(object sender, RoutedEventArgs e)
        {
            StartRecording();
        }

        private async void buttonStop_Click(object sender, RoutedEventArgs e)
        {
            var wasRecording = IsRecording;
            _isCancelled = true;
            if (IsRecording)
            {
                IsRecording = false;
                await _recording.Stop();
            }
            this.Close();

            if (wasRecording)
            {
                await Task.Delay(1000);
                // this method of selecting a file will re-use an existing windows explorer window instead of opening a new one
                Interop.Shell32.WindowsExplorer.ShowFileOrFolder(_recording.FileName);
            }
        }

        private async void buttonCancel_Click(object sender, RoutedEventArgs e)
        {
            _isCancelled = true;
            if (IsRecording)
            {
                IsRecording = false;
                await _recording.Stop();
            }
            this.Close();

            await Task.Delay(10 * 1000);
            if (File.Exists(_recording.FileName))
                File.Delete(_recording.FileName);
        }

        private void toggleMicrophone_Click(object sender, RoutedEventArgs e)
        {
            if (App.Current.Settings.VideoSettings.VideoCodec.GetSelectedPreset() is FFmpegCodecPreset_AudioBase audio)
            {
                if (!audio.CaptureMicrophone && audio.SelectedMicrophone == null)
                {
                    NiceDialog.ShowSettingsPromptAsync(this, SettingsCategory.Video, "Please select a microphone to record audio from before enabling this feature.");
                    return;
                }

                audio.CaptureMicrophone = !audio.CaptureMicrophone;
                IsMicrophoneEnabled = audio.CaptureMicrophone;
            }
        }

        private void toggleSpeaker_Click(object sender, RoutedEventArgs e)
        {
            if (App.Current.Settings.VideoSettings.VideoCodec.GetSelectedPreset() is FFmpegCodecPreset_AudioBase audio)
            {
                if (!audio.CaptureLoopbackAudio && !audio.IsDirectShowInstalled)
                {
                    NiceDialog.ShowSettingsPromptAsync(this, SettingsCategory.Windows, "You must install the 'DirectShow Add-ons' from the settings page before enabling this feature.");
                    return;
                }

                audio.CaptureLoopbackAudio = !audio.CaptureLoopbackAudio;
                IsLoopbackEnabled = audio.CaptureLoopbackAudio;
            }
        }

        private void settings_Click(object sender, RoutedEventArgs e)
        {
            App.Current.ShowSettings(SettingsCategory.Video);
        }

        private void draw_Click(object sender, RoutedEventArgs e)
        {
            AntFu7.LiveDraw.LiveDrawWindow.ShowNewOrExisting();
        }
    }
}
