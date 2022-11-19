using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Controls;
using Clowd.UI.Converters;
using Clowd.UI.Helpers;
using Clowd.UI.Unmanaged;
using Clowd.Util;
using Clowd.Video;
using Clowd.Video.FFmpeg;
using NLog;

namespace Clowd.UI
{
    class ObsInitWrapper : SimpleNotifyObject, IDisposable
    {
        public ScreenRect Selection { get; }

        public string FileName { get; }

        public string PrimaryText
        {
            get
            {
                if (_initializing) return "WAIT...";
                return _initialized ? "START" : "RELOAD";
            }
        }

        public bool CanPrimary => MustReload || CanStart;

        public bool MustReload => !Started && !Initialized && !Initializing;

        private bool CanStart => !Started && Initialized;

        public bool Initialized
        {
            get => _initialized;
            private set => Set(ref _initialized, value, nameof(Initialized), nameof(MustReload), nameof(PrimaryText), nameof(CanPrimary));
        }

        public bool Initializing
        {
            get => _initializing;
            private set => Set(ref _initializing, value, nameof(Initializing), nameof(MustReload), nameof(PrimaryText), nameof(CanPrimary));
        }

        public bool Started
        {
            get => _started;
            private set => Set(ref _started, value, nameof(Started), nameof(CanPrimary));
        }

        public bool Recording
        {
            get => _recording;
            private set => Set(ref _recording, value, nameof(Recording));
        }

        public event EventHandler OutputChanged;
        public event EventHandler ListenerChanged;
        public event EventHandler<VideoCriticalErrorEventArgs> CriticalError;
        public event EventHandler<VideoStatusEventArgs> StatusReceived;

        private bool _disposed;
        private bool _initialized;
        private bool _initializing;
        private bool _started;
        private bool _recording;
        private SettingsVideo _settings = SettingsRoot.Current.Video;
        private ObsCapturer _capturer;
        private SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public ObsInitWrapper(string outputFile, ScreenRect selection)
        {
            Selection = selection;
            FileName = outputFile;
            _settings.PropertyChanged += SettingChanged;
        }

        public async Task Initialize()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (_disposed || Started || Initialized) return;

                Initializing = true;
                Initialized = false;

                if (_capturer != null)
                    await _capturer.DisposeAsync();

                _capturer = new ObsCapturer();
                _capturer.CriticalError += SynchronizationContextEventHandler.CreateDelegate<VideoCriticalErrorEventArgs>(CapturerCriticalError);
                _capturer.StatusReceived += SynchronizationContextEventHandler.CreateDelegate<VideoStatusEventArgs>(CapturerStatusReceived);
                await _capturer.Initialize(FileName, Selection, _settings);

                Initialized = true;
                Initializing = false;
            }
            catch (Exception e)
            {
                CapturerCriticalError(_capturer, new VideoCriticalErrorEventArgs(e.ToString()));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private void CapturerStatusReceived(object sender, VideoStatusEventArgs e)
        {
            StatusReceived?.Invoke(sender, e);
        }

        private void CapturerCriticalError(object sender, VideoCriticalErrorEventArgs e)
        {
            CriticalError?.Invoke(sender, e);
        }

        public async Task Start()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (_disposed || Started || !Initialized) return;
                Started = true;
                await _capturer.StartAsync();
                Recording = true;
            }
            catch (Exception e)
            {
                CapturerCriticalError(_capturer, new VideoCriticalErrorEventArgs(e.ToString()));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task Stop()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (_disposed || !Recording) return;
                await _capturer.StopAsync();
                Recording = false;
            }
            catch (Exception e)
            {
                CapturerCriticalError(_capturer, new VideoCriticalErrorEventArgs(e.ToString()));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private void SettingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SettingsVideo.OpenFinishedInExplorer) or nameof(SettingsVideo.FilenamePattern) or nameof(SettingsVideo.OutputDirectory))
            {
                // do nothing
            }
            else if (e.PropertyName is nameof(SettingsVideo.OutputMode))
            {
                OutputChanged?.Invoke(this, new EventArgs());
            }
            else if (e.PropertyName is nameof(SettingsVideo.CaptureSpeakerDevice) or nameof(SettingsVideo.CaptureMicrophoneDevice))
            {
                ListenerChanged?.Invoke(this, new EventArgs());
            }
            else if (e.PropertyName is nameof(SettingsVideo.CaptureSpeaker) or nameof(SettingsVideo.CaptureMicrophone))
            {
                _capturer?.SetMicrophoneMute(!_settings.CaptureMicrophone);
                _capturer?.SetSpeakerMute(!_settings.CaptureSpeaker);
            }
            else if (Initialized || !Started)
            {
                Invalidate();
            }
        }

        private async void Invalidate()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (_disposed || Started || !Initialized) return;

                if (_capturer != null)
                {
                    await _capturer.DisposeAsync();
                    _capturer = null;
                }

                Initialized = false;
            }
            catch (Exception e)
            {
                CapturerCriticalError(_capturer, new VideoCriticalErrorEventArgs(e.ToString()));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _settings.PropertyChanged -= SettingChanged;
            _semaphore.WaitAsync(3000).ContinueWith((t) =>
            {
                _semaphore.Dispose();
                _capturer?.Dispose();
                _capturer = null;
            });
        }
    }


    internal sealed class VideoCaptureWindow : IVideoCapturePage
    {
        public event EventHandler Closed;

        public bool IsRecording => _obs?.Recording ?? false;

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

        private string _fileName;
        private ObsInitWrapper _obs;
        private SettingsVideo _settings = SettingsRoot.Current.Video;
        private FloatingButtonWindow _floating;

        private IAudioLevelListener _speakerLevel;
        private IAudioLevelListener _microphoneLevel;

        private static readonly ILogger _log = LogManager.GetCurrentClassLogger();

        private async void CapturerCriticalError(object sender, VideoCriticalErrorEventArgs e)
        {
            this.Close();

            if (sender is ObsCapturer capt)
            {
                try
                {
                    var filename = "capture_error_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                    if (_settings.OutputDirectory != null && Directory.Exists(_settings.OutputDirectory))
                    {
                        filename = Path.Combine(Path.GetFullPath(_settings.OutputDirectory), filename);
                    }
                    else
                    {
                        filename = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), filename);
                    }

                    capt.WriteLogToFile(filename);
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

                    return;
                }
                catch { }
            }

            // if we reached here, we were unable to write a log file
            _log.Error(e.Error);

            NiceDialog.ShowNoticeAsync(null,
                NiceDialogIcon.Error,
                e.Error,
                "An unexpected error was encountered while recording.");
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

        public async void Open(ScreenRect captureArea)
        {
            if (_opened || _disposed)
                throw new InvalidOperationException("Video capture can only be opened once");

            _opened = true;

            if (!Directory.Exists(_settings.OutputDirectory))
                _settings.OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            _settings.CaptureMicrophoneDevice = AudioDeviceManager.VerifyMicrophoneOrDefault(_settings.CaptureMicrophoneDevice);
            _settings.CaptureSpeakerDevice = AudioDeviceManager.VerifySpeakerOrDefault(_settings.CaptureSpeakerDevice);

            _fileName = Path.Combine(_settings.OutputDirectory, PathConstants.GetDatedFileName("recording", "mp4"));
            _obs = new ObsInitWrapper(_fileName, captureArea);
            _obs.CriticalError += CapturerCriticalError;
            _obs.StatusReceived += CapturerStatusReceived;
            _obs.ListenerChanged += (s, e) => RefreshListeners();
            _obs.OutputChanged += (s, e) => UpdateOutputIcon();

            _btnClowd = new CaptureToolButton
            {
                Primary = true,
                Text = "DRAG ME",
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconToolNone),
                IsDragHandle = true,
            };

            _btnStart = new CaptureToolButton
            {
                Primary = true,
                Text = "Start",
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconPlay),
                IconPathAlternate = AppStyles.GetIconElement(ResourceIcon.IconUndo),
                Executed = (s, e) => StartRecording(),
                PulseBackground = true,
            };
            _btnStart.SetBinding(CaptureToolButton.TextProperty, new Binding(nameof(ObsInitWrapper.PrimaryText)) { Source = _obs });
            _btnStart.SetBinding(CaptureToolButton.ShowAlternateIconProperty, new Binding(nameof(ObsInitWrapper.MustReload)) { Source = _obs });
            _btnStart.SetBinding(CaptureToolButton.VisibilityProperty, new Binding(nameof(ObsInitWrapper.Recording)) { Source = _obs, Converter = new BoolToInverseVisibilityConverter() });
            _btnStart.SetBinding(CaptureToolButton.IsEnabledProperty, new Binding(nameof(ObsInitWrapper.CanPrimary)) { Source = _obs });
            //_btnStart.SetBinding(CaptureToolButton.PulseBackgroundProperty, new Binding(nameof(ObsInitWrapper.CanPrimary)) { Source = _obs });

            _btnStop = new CaptureToolButton
            {
                Primary = true,
                Text = "Finish",
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconStop),
                Executed = (s, e) => StopRecording(),
            };
            _btnStop.SetBinding(CaptureToolButton.VisibilityProperty, new Binding(nameof(ObsInitWrapper.Recording)) { Source = _obs, Converter = new BoolToVisibilityConverter2() });

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

            RefreshListeners();
            BorderWindow.Show(AppStyles.AccentColor, captureArea);

            _floating.ShowPanel(captureArea);
            await _obs.Initialize();
        }

        public async Task StartRecording()
        {
            if (!_opened) return;
            if (_disposed) throw new ObjectDisposedException("This object is disposed.");

            if (_obs.MustReload) await _obs.Initialize();
            else if (_obs.CanPrimary) await _obs.Start();
        }

        public async Task StopRecording()
        {
            if (!_opened) return;
            if (_disposed) throw new ObjectDisposedException("This object is disposed.");

            var wasRecording = IsRecording;

            BorderWindow.Hide();
            _floating.Hide();

            if (IsRecording)
            {
                await _obs.Stop();
            }

            this.Close();

            if (wasRecording)
            {
                try
                {
                    // move file to user's desired location/name
                    var pattern = String.IsNullOrWhiteSpace(_settings.FilenamePattern) ? "yyyy-MM-dd HH-mm-ss" : Path.GetFileNameWithoutExtension(_settings.FilenamePattern);
                    var newPath = Path.Combine(_settings.OutputDirectory, PathConstants.GetFreePatternFileName(_settings.OutputDirectory, pattern)) + ".mp4";
                    File.Move(_fileName, newPath);
                    _fileName = newPath;
                }
                catch (Exception ex)
                {
                    await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error,
                        $"It was saved to {_fileName} instead.{Environment.NewLine}{Environment.NewLine}Error:{Environment.NewLine}{ex.Message}",
                        "Unable to save video to desired location.");
                }

                if (_settings.OutputMode == VideoOutputType.GIF)
                {
                    _fileName = await EncodeGif(_fileName);
                }

                if (_settings.OpenFinishedInExplorer)
                {
                    // this method of selecting a file will re-use an existing windows explorer window instead of opening a new one
                    Platform.Current.RevealFileOrFolder(File.Exists(_fileName) ? _fileName : _settings.OutputDirectory);
                }
            }
        }

        private string GetNewVideoName()
        {
            if (String.IsNullOrWhiteSpace(_settings.OutputDirectory))
                throw new Exception("OutputDirectory must not be null");

            if (!Directory.Exists(_settings.OutputDirectory))
                Directory.CreateDirectory(_settings.OutputDirectory);

            var pattern = String.IsNullOrWhiteSpace(_settings.FilenamePattern) ? "yyyy-MM-dd HH-mm-ss" : Path.GetFileNameWithoutExtension(_settings.FilenamePattern);
            return Path.Combine(_settings.OutputDirectory, PathConstants.GetFreePatternFileName(_settings.OutputDirectory, pattern)) + ".mp4";
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
                    break;
                default:
                    _settings.OutputMode = VideoOutputType.MP4;
                    break;
            }
            UpdateOutputIcon();
        }

        private void UpdateOutputIcon()
        {
            switch (_settings.OutputMode)
            {
                case VideoOutputType.GIF:
                    _btnOutput.IconPath = AppStyles.GetIconElement(ResourceIcon.IconVideoGIF);
                    break;
                default:
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
            BorderWindow.Hide();
            _floating.Hide();

            if (IsRecording)
            {
                await _obs.Stop();
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
            BorderWindow.Hide();
            _speakerLevel?.Dispose();
            _microphoneLevel?.Dispose();
            _floating.Close();
            _obs?.Dispose();
            Closed?.Invoke(this, new EventArgs());
        }
    }
}
