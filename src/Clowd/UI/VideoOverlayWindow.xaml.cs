//using System;
//using System.Diagnostics;
//using System.IO;
//using System.Linq;
//using System.Threading.Tasks;
//using System.Windows;
//using System.Windows.Controls;
//using Clowd.Capture;
//using Clowd.Config;
//using Clowd.Video;
//using Clowd.UI.Helpers;
//using Clowd.Util;
//using ScreenVersusWpf;

//namespace Clowd.UI
//{
//    [PropertyChanged.ImplementPropertyChanged]
//    public partial class VideoOverlayWindow : OverlayWindow, IVideoCapturePage
//    {
//        public bool IsRecording { get; set; }
//        public bool IsStarted { get; set; }
//        public bool IsAudioSupported { get; set; }

//        public bool IsMicrophoneEnabled { get; set; }
//        public bool IsLoopbackEnabled { get; set; }

//        private bool _isCancelled = false;
//        private IAudioLevelListener speaker;
//        private IAudioLevelListener mic;
//        private System.Timers.Timer audioTimer;
//        private Point? _moveMouseDown;
//        private Point? _moveInitial;
//        private IVideoCapturer _capturer;
//        private readonly IPageManager _pages;
//        private VideoCapturerSettings _settings;
//        private string _fileName;

//        public VideoOverlayWindow(VideoCapturerSettings settings, IVideoCapturer capturer, IPageManager pages)
//        {
//            InitializeComponent();
//            _capturer = capturer;
//            _pages = pages;
//            _settings = settings;
//        }

//        public void Dispose()
//        {
//            this.Close();
//        }

//        public void Open(ScreenRect captureArea)
//        {
//            SelectionRectangle = captureArea.ToWpfRect();

//            if (_settings.CaptureMicrophoneDevice == null) _settings.CaptureMicrophoneDevice = AudioDeviceManager.GetDefaultMicrophone();
//            if (_settings.CaptureSpeakerDevice == null) _settings.CaptureSpeakerDevice = AudioDeviceManager.GetDefaultSpeaker();
//            if (!Directory.Exists(_settings.OutputDirectory)) _settings.OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
//            _settings.PropertyChanged += settings_PropertyChanged;

//            //_capturer = new ObsCapturer(App.DefaultLog);
//            _capturer.StatusReceived += capturer_StatusReceived;
//            _capturer.CriticalError += capturer_CriticalError;

//            this.Closed += (s, e) =>
//            {
//                _settings.PropertyChanged -= settings_PropertyChanged;
//                _capturer.StatusReceived -= capturer_StatusReceived;
//                _capturer.CriticalError -= capturer_CriticalError;

//                mic?.Dispose();
//                speaker?.Dispose();
//                mic = speaker = null;
//                audioTimer.Enabled = false;
//                audioTimer.Dispose();
//                //_capturer.Dispose();
//            };

//            this.Loaded += (s, e) =>
//            {
//                UpdateButtonPanelPosition(toolActionBarStackPanel);
//            };

//            UpdateAudioState();

//            // start audio update timer
//            audioTimer = new System.Timers.Timer(20);
//            audioTimer.Elapsed += AudioTimer_Elapsed;
//            audioTimer.AutoReset = true;
//            audioTimer.Enabled = true;
//            audioTimer.Start();

//            recordingLabelButton.PreviewMouseDown += RecordingLabelButton_MouseDown;
//            recordingLabelButton.PreviewMouseMove += RecordingLabelButton_MouseMove;
//            recordingLabelButton.PreviewMouseUp += RecordingLabelButton_MouseUp;
//            Show();
//        }

//        private async void capturer_CriticalError(object sender, VideoCriticalErrorEventArgs e)
//        {
//            this.Close();

//            var filename = "capture_error_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
//            filename = System.IO.Path.Combine(System.IO.Path.GetFullPath(_settings.OutputDirectory), filename);

//            _capturer.WriteLogToFile(filename);
//            File.AppendAllText(filename, Environment.NewLine + Environment.NewLine + e.Error);

//            if (await NiceDialog.ShowPromptAsync(this, NiceDialogIcon.Error, "An unexpected error was encountered while trying to start recording. A log file has been created in your video output directory.", "Open Error Log"))
//            {
//                Process.Start("notepad.exe", filename);
//            }
//        }

//        private void settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
//        {
//            UpdateAudioState();
//        }

//        private void capturer_StatusReceived(object sender, VideoStatusEventArgs e)
//        {
//            Dispatcher.Invoke(() =>
//            {
//                if (e.AvgFps != 0 && (e.TotalTime == default(TimeSpan) || DateTime.Now.Ticks / (4 * TimeSpan.TicksPerSecond) % 2 == 0))
//                {
//                    recordingLabelButton.Text = e.AvgFps + " FPS";
//                }
//                else if (e.TotalTime != default(TimeSpan))
//                {
//                    recordingLabelButton.Text = $"{((int)e.TotalTime.TotalMinutes):D2}:{((int)e.TotalTime.Seconds):D2}";
//                }
//            });
//        }

//        private void RecordingLabelButton_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
//        {
//            e.Handled = true;
//            recordingLabelButton.ReleaseMouseCapture();
//            _moveMouseDown = null;
//            _moveInitial = null;
//        }

//        private void RecordingLabelButton_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
//        {
//            e.Handled = true;
//            if (!_moveMouseDown.HasValue)
//                return;

//            var mouse = e.GetPosition(this);

//            var xdelta = mouse.X - _moveMouseDown.Value.X;
//            var ydelta = mouse.Y - _moveMouseDown.Value.Y;

//            Canvas.SetLeft(toolActionBarStackPanel, _moveInitial.Value.X + xdelta);
//            Canvas.SetTop(toolActionBarStackPanel, _moveInitial.Value.Y + ydelta);
//        }

//        private void RecordingLabelButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
//        {
//            e.Handled = true;
//            recordingLabelButton.CaptureMouse();
//            _moveMouseDown = e.GetPosition(this);

//            var top = Canvas.GetTop(toolActionBarStackPanel);
//            var left = Canvas.GetLeft(toolActionBarStackPanel);
//            _moveInitial = new Point(left, top);
//        }

//        private void AudioTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
//        {
//            // don't bother the UI thread if we're not capturing audio.
//            if (mic == null && speaker == null)
//                return;

//            this.Dispatcher.Invoke(() =>
//            {
//                levelSpeaker.Value = ConvertLevelToDb(speaker);
//                levelMic.Value = ConvertLevelToDb(mic);
//            });
//        }

//        private double ConvertLevelToDb(IAudioLevelListener item)
//        {
//            if (item == null)
//                return 0;

//            double level = item.GetPeakLevel();

//            if (level > 0 && level <= 1)
//                return (20 * Math.Log10(level)) / 60 * 100 + 100;

//            return 0;
//        }

//        private async void StartRecording()
//        {
//            IsStarted = true;
//            UpdateAudioState();
//            labelCountdown.Visibility = Visibility.Visible;

//            for (int i = 4; i >= 1; i--)
//            {
//                labelCountdown.Text = i.ToString();
//                labelCountdown.FontSize = 120;
//                recordingLabelButton.Text = "REC in " + i.ToString();
//                await Task.Delay(1000);
//                if (_isCancelled)
//                    return;
//            }

//            labelCountdown.Visibility = Visibility.Collapsed;
//            recordingLabelButton.Text = "Starting";

//            IsRecording = true;

//            try
//            {
//                _fileName = await _capturer.StartAsync(SelectionRectangle.ToScreenRect(), _settings);
//            }
//            catch (Exception ex)
//            {
//                capturer_CriticalError(this, new VideoCriticalErrorEventArgs(ex.Message));
//            }
//        }

//        public void UpdateAudioState()
//        {
//            // dispose of all sounds stuff and re-create it. this doesn't happen too often (when settings change) so it's okay

//            IAudioLevelListener tmp;
//            tmp = mic;
//            mic = null;
//            tmp?.Dispose();

//            tmp = speaker;
//            speaker = null;
//            tmp?.Dispose();

//            levelSpeaker.Value = levelMic.Value = 0;

//            IsAudioSupported = !IsStarted;
//            IsMicrophoneEnabled = _settings.CaptureMicrophone && _settings.CaptureMicrophoneDevice != null;
//            IsLoopbackEnabled = _settings.CaptureSpeaker && _settings.CaptureSpeakerDevice != null;

//            if (IsMicrophoneEnabled)
//                mic = _settings.CaptureMicrophoneDevice.GetLevelListener();

//            if (IsLoopbackEnabled)
//                speaker = _settings.CaptureSpeakerDevice.GetLevelListener();
//        }

//        private void buttonStart_Click(object sender, RoutedEventArgs e)
//        {
//            StartRecording();
//        }

//        private async void buttonStop_Click(object sender, RoutedEventArgs e)
//        {
//            var wasRecording = IsRecording;
//            _isCancelled = true;
//            if (IsRecording)
//            {
//                IsRecording = false;
//                await _capturer.StopAsync();
//            }
//            this.Close();

//            if (wasRecording)
//            {
//                await Task.Delay(1000);
//                // this method of selecting a file will re-use an existing windows explorer window instead of opening a new one
//                if (File.Exists(_fileName))
//                    Interop.Shell32.WindowsExplorer.ShowFileOrFolder(_fileName);
//                else
//                    Interop.Shell32.WindowsExplorer.ShowFileOrFolder(_settings.OutputDirectory);
//            }
//        }

//        private async void buttonCancel_Click(object sender, RoutedEventArgs e)
//        {
//            _isCancelled = true;
//            if (IsRecording)
//            {
//                IsRecording = false;
//                await _capturer.StopAsync();
//            }
//            this.Close();

//            await Task.Delay(10 * 1000);
//            if (File.Exists(_fileName))
//                File.Delete(_fileName);
//        }

//        private void toggleMicrophone_Click(object sender, RoutedEventArgs e)
//        {
//            if (!_settings.CaptureMicrophone && _settings.CaptureMicrophoneDevice == null)
//            {
//                NiceDialog.ShowSettingsPromptAsync(this, SettingsCategory.Video, "Please select a recording device in the settings.");
//                return;
//            }

//            _settings.CaptureMicrophone = !_settings.CaptureMicrophone;
//            IsMicrophoneEnabled = _settings.CaptureMicrophone;
//        }

//        private void toggleSpeaker_Click(object sender, RoutedEventArgs e)
//        {
//            if (!_settings.CaptureSpeaker && _settings.CaptureSpeakerDevice == null)
//            {
//                NiceDialog.ShowSettingsPromptAsync(this, SettingsCategory.Video, "Please select a recording device in the settings.");
//                return;
//            }

//            _settings.CaptureSpeaker = !_settings.CaptureSpeaker;
//            IsLoopbackEnabled = _settings.CaptureSpeaker;
//        }

//        private void settings_Click(object sender, RoutedEventArgs e)
//        {
//            _pages.CreateSettingsPage().Open(SettingsCategory.Video);
//        }

//        private void draw_Click(object sender, RoutedEventArgs e)
//        {
//            _pages.CreateLiveDrawPage().Open();
//        }
//    }
//}
