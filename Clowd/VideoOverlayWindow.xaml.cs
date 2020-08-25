using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Clowd.Capture;
using Clowd.Utilities;
using NReco.VideoConverter;
using Ookii.Dialogs.Wpf;
using ScreenVersusWpf;

namespace Clowd
{
    [PropertyChanged.ImplementPropertyChanged]
    public partial class VideoOverlayWindow : Window
    {
        public ScreenRect CroppingRectangle { get; private set; } = new ScreenRect(0, 0, 0, 0);
        public Rect CroppingRectangleWpf { get { return CroppingRectangle.ToWpfRect(); } private set { CroppingRectangle = new WpfRect(value).ToScreenRect(); } }
        public IntPtr Handle { get; private set; }

        public bool IsRecording { get; set; }
        public bool IsStarted { get; set; }
        public bool IsNotStarted => !IsStarted;
        public bool IsAudioSupported { get; set; }

        public bool IsMicrophoneEnabled { get; set; }
        public bool IsLoopbackEnabled { get; set; }

        private LiveScreenRecording _recording;
        private bool _isCancelled = false;
        private NAudioItem speaker;
        private NAudioItem mic;
        private System.Timers.Timer audioTimer;

        public VideoOverlayWindow(ScreenRect captureArea)
        {
            CroppingRectangle = captureArea;
            InitializeComponent();
            this.SourceInitialized += VideoOverlayWindow_SourceInitialized;
            this.Loaded += VideoOverlayWindow_Loaded;

            selectionBorder.StrokeThickness = ScreenTools.WpfSnapToPixelsFloor(2);
            selectionBorder.Margin = new Thickness(-ScreenTools.WpfSnapToPixelsFloor(2));

            _recording = new LiveScreenRecording(captureArea.ToSystem());
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

            UpdateAudioState();

            // start audio update timer
            audioTimer = new System.Timers.Timer(20);
            audioTimer.Elapsed += AudioTimer_Elapsed; ;
            audioTimer.AutoReset = true;
            audioTimer.Enabled = true;
            audioTimer.Start();
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

        private void Recording_LogRecieved(object sender, FFMpegLogEventArgs e)
        {
            //frame=  219 fps= 31 q=10.0 size=       0kB time=00:00:05.80 bitrate=   0.1kbits/s dup=5 drop=0 speed=0.82x

            string getData(string label)
            {
                var msg = e.Data;
                var start = msg.IndexOf(label);
                if (start < 0)
                    return null;
                msg = msg.Substring(start + label.Length).TrimStart();
                msg = msg.Substring(0, msg.IndexOf(" "));
                if (msg == "0.0" || msg == "00:00:00.00")
                    return null;

                return msg;
            }

            var fps = getData("fps=");
            var time = getData("time=");
            TimeSpan ts = default(TimeSpan);

            if (time != null)
            {
                try
                {
                    // sometimes ffmpeg gives us garbage timecodes, it depends on the input stream timestamp on the frames & the settings we use.
                    ts = TimeSpan.Parse(time);
                }
                catch { }
            }

            Dispatcher.Invoke(() =>
            {
                if (fps != null && (ts == default(TimeSpan) || DateTime.Now.Ticks / (4 * TimeSpan.TicksPerSecond) % 2 == 0))
                {
                    recordingFpsLabel.Text = fps + " FPS";
                }
                else if (ts != default(TimeSpan))
                {
                    recordingFpsLabel.Text = $"{((int)ts.TotalMinutes):D2}:{((int)ts.Seconds):D2}";
                }
            });
        }

        private void VideoOverlayWindow_SourceInitialized(object sender, EventArgs e)
        {
            this.Handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        }

        private void VideoOverlayWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var primary = ScreenTools.Screens.First().Bounds;
            var virt = ScreenTools.VirtualScreen.Bounds;
            // WPF makes some fairly inconvenient DPI conversions to Left and Top which have also changed between NET 4.5 and 4.8; just use WinAPI instead of de-converting them
            Interop.USER32.SetWindowPos(this.Handle, 0, -primary.Left, -primary.Top, virt.Width, virt.Height, Interop.SWP.SHOWWINDOW);
            Interop.USER32.SetForegroundWindow(this.Handle);
            toolActionBarStackPanel.SetPanelCanvasPositionRelativeToSelection(CroppingRectangle.ToWpfRect(), 2, 10, 50, 303);
            //StartRecording();
        }

        private async void StartRecording()
        {
            IsStarted = true;
            UpdateAudioState();
            labelCountdown.Visibility = Visibility.Visible;

            for (int i = 4; i >= 1; i--)
            {
                labelCountdown.Text = i.ToString();
                labelCountdown.FontSize = 120;
                recordingFpsLabel.Text = "REC in " + i.ToString();
                await Task.Delay(1000);
                if (_isCancelled)
                    return;
            }

            labelCountdown.Visibility = Visibility.Collapsed;
            recordingFpsLabel.Text = "Starting";

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
                IsAudioSupported = true;
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

            if (IsStarted)
                IsAudioSupported = false;
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
                try
                {
                    // this method of selecting a file will re-use an existing windows explorer window instead of opening a new one
                    Interop.Shell32.WindowsExplorer.ShowFileOrFolder(_recording.FileName);
                }
                catch
                {
                    Process.Start("explorer.exe", $"/select,\"{_recording.FileName}\"");
                }
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

            await Task.Delay(5000);
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
    }
}
