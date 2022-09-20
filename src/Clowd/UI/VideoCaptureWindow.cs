using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Controls;
using Clowd.UI.Helpers;
using Clowd.UI.Unmanaged;
using Clowd.Util;
using Clowd.Video;
using Clowd.Video.FFmpeg;
using NLog;

namespace Clowd.UI
{
    internal sealed class VideoCaptureWindow : IVideoCapturePage
    {
        public event EventHandler Closed;
        public bool IsRecording { get; private set; }

        private CaptureToolButton _btnClowd;
        private CaptureToolButton _btnStart;
        private CaptureToolButton _btnStop;
        private CaptureToolButton _btnMicrophone;
        private CaptureToolButton _btnSpeaker;
        private CaptureToolButton _btnOutput;
        private CaptureToolButton _btnSettings;
        private CaptureToolButton _btnDraw;
        private CaptureToolButton _btnCancel;

        private bool _opened;
        private bool _disposed;
        private bool _hasStarted;
        private bool _isCancelled;

        private ScreenRect _selection;
        private IVideoCapturer _capturer;
        private SettingsVideo _settings = SettingsRoot.Current.Video;
        private string _fileName;
        private FloatingButtonWindow _floating;

        private IAudioLevelListener _speakerLevel;
        private IAudioLevelListener _microphoneLevel;

        private static readonly ILogger _log = LogManager.GetCurrentClassLogger();

        public VideoCaptureWindow()
        {
            _capturer = new ObsCapturer();
            _capturer.StatusReceived += SynchronizationContextEventHandler.CreateDelegate<VideoStatusEventArgs>(CapturerStatusReceived);
            _capturer.CriticalError += SynchronizationContextEventHandler.CreateDelegate<VideoCriticalErrorEventArgs>(CapturerCriticalError);

            if (!Directory.Exists(_settings.OutputDirectory))
                _settings.OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            _btnClowd = new CaptureToolButton
            {
                Primary = true,
                Text = "Drag Me",
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconToolNone),
                IsDragHandle = true,
            };

            _btnStart = new CaptureToolButton
            {
                Primary = true,
                Text = "Start",
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconPlay),
                PulseBackground = true,
                Executed = (s, e) => StartRecording(),
            };

            _btnStop = new CaptureToolButton
            {
                Primary = true,
                Text = "Finish",
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconStop),
                Executed = (s, e) => StopRecording(),
                Visibility = Visibility.Collapsed,
            };

            _btnMicrophone = new CaptureToolButton
            {
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconMicrophoneDisabled),
                IconPathAlternate = AppStyles.GetIconElement(ResourceIcon.IconMicrophoneEnabled),
                Executed = OnMicrophoneToggle,
                Text = "Mic",
            };
            _btnMicrophone.SetBinding(
                CaptureToolButton.ShowAlternateIconProperty,
                new Binding(nameof(SettingsVideo.CaptureMicrophone)) { Source = _settings, Mode = BindingMode.OneWay });

            _btnSpeaker = new CaptureToolButton
            {
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconSpeakerDisabled),
                IconPathAlternate = AppStyles.GetIconElement(ResourceIcon.IconSpeakerEnabled),
                Executed = OnSpeakerToggle,
                Text = "Spk",
            };
            _btnSpeaker.SetBinding(
                CaptureToolButton.ShowAlternateIconProperty,
                new Binding(nameof(SettingsVideo.CaptureSpeaker)) { Source = _settings, Mode = BindingMode.OneWay });

            _btnOutput = new CaptureToolButton
            {
                Text = "Output",
                Executed = OnChangeOutput,
                IconPath = _settings.OutputMode switch
                {
                    // VideoOutputType.MKV => AppStyles.GetIconElement(ResourceIcon.IconVideoMKV),
                    VideoOutputType.MP4 => AppStyles.GetIconElement(ResourceIcon.IconVideoMP4),
                    VideoOutputType.GIF => AppStyles.GetIconElement(ResourceIcon.IconVideoGIF),
                    _ => throw new ArgumentOutOfRangeException()
                },
            };

            _btnSettings = new CaptureToolButton
            {
                Text = "Settings",
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconSettings),
                Executed = OnSettings,
            };

            _btnDraw = new CaptureToolButton
            {
                Text = "Draw",
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconDrawing),
                Executed = OnDraw,
            };

            _btnCancel = new CaptureToolButton
            {
                Text = "Cancel",
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconClose),
                Executed = OnCancel,
            };

            _floating = FloatingButtonWindow.Create(
                new[] { _btnClowd, _btnStart, _btnStop, _btnMicrophone, _btnSpeaker, _btnOutput, _btnSettings, _btnDraw, _btnCancel });

            _settings.PropertyChanged += SettingChanged;
        }

        private async void CapturerCriticalError(object sender, VideoCriticalErrorEventArgs e)
        {
            this.Close();

            var filename = "capture_error_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
            filename = Path.Combine(Path.GetFullPath(_settings.OutputDirectory), filename);

            _capturer.WriteLogToFile(filename);
            File.AppendAllText(filename, Environment.NewLine + "--" + Environment.NewLine + e.Error);

            _log.ForErrorEvent()
                .Message("The capturer has crashed.")
                .Property<string>("obs.log", File.ReadAllText(filename))
                .Log();

            if (await NiceDialog.ShowPromptAsync(null,
                    NiceDialogIcon.Error,
                    e.Error + Environment.NewLine + "A log file has been created in your video output directory for more information.",
                    "An unexpected error was encountered while recording.",
                    "Open Error Log"))
            {
                Process.Start("notepad.exe", filename);
            }
        }

        private void CapturerStatusReceived(object sender, VideoStatusEventArgs e)
        {
            if (e.AvgFps != 0 && (e.TotalTime == default(TimeSpan) || DateTime.Now.Ticks / (4 * TimeSpan.TicksPerSecond) % 2 == 0))
            {
                _btnClowd.Text = e.AvgFps + " FPS";
            }
            else if (e.TotalTime != default(TimeSpan))
            {
                _btnClowd.Text = $"{((int)e.TotalTime.TotalMinutes):D2}:{((int)e.TotalTime.Seconds):D2}";
            }
        }

        public void Open(ScreenRect captureArea)
        {
            if (_opened || _disposed)
                throw new InvalidOperationException("Video capture can only be opened once");

            _opened = true;
            _selection = captureArea;

            RefreshListeners();
            BorderWindow.Show(AppStyles.AccentColor, captureArea);
            BorderWindow.SetText("Press Start");

            _floating.ShowPanel(captureArea);
        }

        public async Task StartRecording()
        {
            if (_disposed)
                throw new ObjectDisposedException("This object is disposed.");

            if (_hasStarted)
                throw new InvalidOperationException("StartRecording can only be called once");

            var initTask = _capturer.Initialize(_selection, _settings).ContinueWith(
                t =>
                {
                    if (t.Exception != null)
                        CapturerCriticalError(this, new VideoCriticalErrorEventArgs(t.Exception.ToString()));
                    _btnClowd.Text = "READY";
                },
                TaskScheduler.FromCurrentSynchronizationContext());

            _hasStarted = true;
            _btnStart.IsEnabled = false;
            _btnOutput.IsEnabled = false;
            _btnMicrophone.IsEnabled = false;
            _btnSpeaker.IsEnabled = false;

            for (int i = 3; i >= 1; i--)
            {
                BorderWindow.SetText(i.ToString());

                if (initTask.IsCompleted)
                    _btnClowd.Text = "REC in " + i.ToString();

                await Task.Delay(1000);
                if (_isCancelled)
                    return;
            }

            BorderWindow.SetText(null);
            _btnClowd.Text = "Starting";

            try
            {
                _fileName = await _capturer.StartAsync();
                IsRecording = true;
            }
            catch (Exception ex)
            {
                CapturerCriticalError(this, new VideoCriticalErrorEventArgs(ex.ToString()));
            }

            _btnClowd.Text = "Started";
            _btnClowd.IconPath = AppStyles.GetIconElement(ResourceIcon.IconClowd);
            _btnStart.Visibility = Visibility.Collapsed;
            _btnStop.Visibility = Visibility.Visible;
        }

        public async Task StopRecording()
        {
            if (_disposed)
                throw new ObjectDisposedException("This object is disposed.");

            var wasRecording = IsRecording;
            _isCancelled = true;

            BorderWindow.Hide();
            _floating.Hide();

            if (IsRecording)
            {
                IsRecording = false;
                await _capturer.StopAsync();
            }

            this.Close();

            if (wasRecording)
            {
                if (_settings.OutputMode == VideoOutputType.GIF)
                {
                    _fileName = await EncodeGif(_fileName);
                }

                if (SettingsRoot.Current.Video.OpenFinishedInExplorer)
                {
                    // this method of selecting a file will re-use an existing windows explorer window instead of opening a new one
                    if (File.Exists(_fileName))
                        Platform.Current.RevealFileOrFolder(_fileName);
                    else
                        Platform.Current.RevealFileOrFolder(_settings.OutputDirectory);
                }
            }
        }

        private void SettingChanged(object sender, PropertyChangedEventArgs e)
        {
            RefreshListeners();
        }

        private void RefreshListeners()
        {
            if (_disposed) return;

            if (_settings.CaptureMicrophoneDevice == null || !_settings.CaptureMicrophone)
            {
                _microphoneLevel?.Dispose();
                _microphoneLevel = null;
            }
            else if (_settings.CaptureMicrophoneDevice != _microphoneLevel?.Device)
            {
                _microphoneLevel?.Dispose();
                _microphoneLevel = AudioDeviceManager.GetAudioListener(_settings.CaptureMicrophoneDevice);
                _btnMicrophone.Overlay = GetLevelVisual(_microphoneLevel, nameof(_settings.CaptureMicrophone));
            }

            if (_settings.CaptureSpeakerDevice == null || !_settings.CaptureSpeaker)
            {
                _speakerLevel?.Dispose();
                _speakerLevel = null;
            }
            else if (_settings.CaptureSpeakerDevice != _speakerLevel?.Device)
            {
                _speakerLevel?.Dispose();
                _speakerLevel = AudioDeviceManager.GetAudioListener(_settings.CaptureSpeakerDevice);
                _btnSpeaker.Overlay = GetLevelVisual(_speakerLevel, nameof(_settings.CaptureSpeaker));
            }
        }

        private ProgressBar GetLevelVisual(IAudioLevelListener listener, string enabledPath)
        {
            var prog = new ProgressBar { Style = AppStyles.AudioLevelProgressBarStyle };

            var valueBinding = new Binding(nameof(IAudioLevelListener.PeakLevel));
            valueBinding.Source = listener;
            valueBinding.Mode = BindingMode.OneWay;
            prog.SetBinding(ProgressBar.ValueProperty, valueBinding);

            var visibilityBinding = new Binding(enabledPath);
            visibilityBinding.Source = _settings;
            visibilityBinding.Mode = BindingMode.OneWay;
            visibilityBinding.Converter = new Converters.BoolToVisibilityConverter2();
            prog.SetBinding(ProgressBar.VisibilityProperty, visibilityBinding);

            return prog;
        }

        private Task<string> EncodeGif(string filePath)
        {
            return Task.Run(() =>
            {
                var task = PageManager.Current.Tasks.CreateTask($"Encode GIF ({Path.GetFileName(filePath)})");
                task.SetStatus("Preparing...");

                var ffmpeg = new FFMpegConverter();
                ffmpeg.ConvertProgress += (s, e) =>
                {
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        task.SetProgress((int)e.Processed.TotalSeconds, (int)e.TotalDuration.TotalSeconds, false);
                    });
                };

                task.Show();

                // ffmpeg -ss 30 -t 3 -i input.mp4 -vf "fps=10,scale=320:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse" -loop 0 output.gif
                var gifPath = Path.Combine(Path.GetDirectoryName(filePath), Path.GetFileNameWithoutExtension(filePath) + ".gif");
                ffmpeg.Invoke($"-i \"{filePath}\" -vf \"fps=15\" \"{gifPath}\"");

                task.Hide();
                return gifPath;
            });
        }

        private void OnChangeOutput(object sender, EventArgs e)
        {
            switch (_settings.OutputMode)
            {
                case VideoOutputType.MP4:
                    _settings.OutputMode = VideoOutputType.GIF;
                    _btnOutput.IconPath = AppStyles.GetIconElement(ResourceIcon.IconVideoGIF);
                    break;
                // case VideoOutputType.MKV:
                //     _settings.OutputMode = VideoOutputType.GIF;
                //     _btnOutput.IconPath = AppStyles.GetIconElement(ResourceIcon.IconVideoGIF);
                //     break;
                default:
                    _settings.OutputMode = VideoOutputType.MP4;
                    _btnOutput.IconPath = AppStyles.GetIconElement(ResourceIcon.IconVideoMP4);
                    break;
            }
        }

        private void OnSpeakerToggle(object sender, EventArgs e)
        {
            _settings.CaptureSpeaker = !_settings.CaptureSpeaker;
        }

        private void OnMicrophoneToggle(object sender, EventArgs e)
        {
            _settings.CaptureMicrophone = !_settings.CaptureMicrophone;
        }

        private void OnSettings(object sender, EventArgs e)
        {
            PageManager.Current.GetSettingsPage().Open(SettingsPageTab.SettingsVideo);
        }

        private void OnDraw(object sender, EventArgs e)
        {
            PageManager.Current.GetLiveDrawPage().Open();
        }

        private async void OnCancel(object sender, EventArgs e)
        {
            _isCancelled = true;
            BorderWindow.Hide();
            _floating.Hide();
            if (IsRecording)
            {
                IsRecording = false;
                await _capturer.StopAsync();
            }

            this.Close();

            await Task.Delay(4 * 1000);
            if (File.Exists(_fileName))
                File.Delete(_fileName);
        }

        public void Close()
        {
            if (_disposed)
                return;

            _disposed = true;
            _settings.PropertyChanged -= SettingChanged;
            BorderWindow.Hide();
            _speakerLevel?.Dispose();
            _microphoneLevel?.Dispose();
            _capturer?.Dispose();
            _floating.Close();
            Closed?.Invoke(this, new EventArgs());
        }
    }
}
