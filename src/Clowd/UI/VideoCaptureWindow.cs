using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Controls;
using Clowd.UI.Helpers;
using Clowd.Util;

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
        private CaptureToolButton _btnSettings;
        private CaptureToolButton _btnDraw;
        private CaptureToolButton _btnCancel;

        private bool _disposed;
        private ScreenRect _selection;
        private IVideoCapturer _capturer;
        private readonly IPageManager _pages;
        private VideoCapturerSettings _settings;
        private UIAudioMonitor _monitor;
        private string _fileName;
        private ClowdWin64.BorderWindow _border;
        private FloatingButtonWindow _floating;
        private bool _isCancelled = false;

        public VideoCaptureWindow(VideoCapturerSettings settings, IVideoCapturer capturer, IPageManager pages)
        {
            _pages = pages;
            _settings = settings;

            _capturer = capturer;
            _capturer.StatusReceived += SynchronizationContextEventHandler.CreateDelegate<VideoStatusEventArgs>(CapturerStatusReceived);
            _capturer.CriticalError += SynchronizationContextEventHandler.CreateDelegate<VideoCriticalErrorEventArgs>(CapturerCriticalError);

            _btnClowd = new CaptureToolButton
            {
                Primary = true,
                Text = "CLOWD",
                IconPath = ResourceIcons.GetIconElement(ResourceIcon.IconClowd),
                IsDragHandle = true,
            };

            _btnStart = new CaptureToolButton
            {
                Primary = true,
                Text = "Start",
                IconPath = ResourceIcons.GetIconElement(ResourceIcon.IconPlay),
                PulseBackground = true,
                Executed = OnStart,
            };

            _btnStop = new CaptureToolButton
            {
                Primary = true,
                Text = "Finish",
                IconPath = ResourceIcons.GetIconElement(ResourceIcon.IconStop),
                Executed = OnStop,
                Visibility = Visibility.Collapsed,
            };

            _btnMicrophone = new CaptureToolButton
            {
                IconPath = ResourceIcons.GetIconElement(ResourceIcon.IconMicrophoneDisabled),
                IconPathAlternate = ResourceIcons.GetIconElement(ResourceIcon.IconMicrophoneEnabled),
                Executed = OnMicrophoneToggle,
                Text = "Mic",
            };

            _btnSpeaker = new CaptureToolButton
            {
                IconPath = ResourceIcons.GetIconElement(ResourceIcon.IconSpeakerDisabled),
                IconPathAlternate = ResourceIcons.GetIconElement(ResourceIcon.IconSpeakerEnabled),
                Executed = OnSpeakerToggle,
                Text = "Spk",
            };

            _btnSettings = new CaptureToolButton
            {
                Text = "Settings",
                IconPath = ResourceIcons.GetIconElement(ResourceIcon.IconSettings),
                Executed = OnSettings,
            };

            _btnDraw = new CaptureToolButton
            {
                Text = "Draw",
                IconPath = ResourceIcons.GetIconElement(ResourceIcon.IconDrawing),
                Executed = OnDraw,
            };

            _btnCancel = new CaptureToolButton
            {
                Text = "Cancel",
                IconPath = ResourceIcons.GetIconElement(ResourceIcon.IconClose),
                Executed = OnCancel,
            };

            _floating = FloatingButtonWindow.Create(
                new[] { _btnClowd, _btnStart, _btnStop, _btnMicrophone, _btnSpeaker, _btnSettings, _btnDraw, _btnCancel });
        }

        private async void CapturerCriticalError(object sender, VideoCriticalErrorEventArgs e)
        {
            this.Dispose();

            var filename = "capture_error_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
            filename = Path.Combine(Path.GetFullPath(_settings.OutputDirectory), filename);

            _capturer.WriteLogToFile(filename);
            File.AppendAllText(filename, Environment.NewLine + Environment.NewLine + e.Error);

            if (await NiceDialog.ShowPromptAsync(null,
                NiceDialogIcon.Error,
                "An unexpected error was encountered while trying to start recording. A log file has been created in your video output directory.",
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
            _selection = captureArea;
            var clr = System.Drawing.Color.FromArgb(App.Current.AccentColor.A, App.Current.AccentColor.R, App.Current.AccentColor.G, App.Current.AccentColor.B);

            _monitor = new UIAudioMonitor(_settings, 20);
            _btnMicrophone.Overlay = _monitor.GetMicrophoneVisual();
            _btnMicrophone.SetBinding(CaptureToolButton.ShowAlternateIconProperty, _monitor.GetMicrophoneEnabledBinding());
            _btnSpeaker.Overlay = _monitor.GetSpeakerVisual();
            _btnSpeaker.SetBinding(CaptureToolButton.ShowAlternateIconProperty, _monitor.GetSpeakerEnabledBinding());

            _border = new ClowdWin64.BorderWindow(clr, (System.Drawing.Rectangle)captureArea);
            _border.OverlayText = "Press Start";
            _floating.ShowPanel(captureArea);
        }

        private async void OnStart(object sender, EventArgs e)
        {
            _btnStart.IsEnabled = false;

            for (int i = 4; i >= 1; i--)
            {
                _border.OverlayText = i.ToString();
                //labelCountdown.FontSize = 120;
                _btnClowd.Text = "REC in " + i.ToString();
                await Task.Delay(1000);
                if (_isCancelled)
                    return;
            }

            _border.OverlayText = null;
            _btnClowd.Text = "Starting";

            try
            {
                _fileName = await _capturer.StartAsync(_selection, _settings);
                IsRecording = true;
            }
            catch (Exception ex)
            {
                CapturerCriticalError(this, new VideoCriticalErrorEventArgs(ex.Message));
            }

            _btnStart.Visibility = Visibility.Collapsed;
            _btnStop.Visibility = Visibility.Visible;
        }

        private async void OnStop(object sender, EventArgs e)
        {
            var wasRecording = IsRecording;
            _isCancelled = true;
            if (IsRecording)
            {
                IsRecording = false;
                await _capturer.StopAsync();
            }
            this.Dispose();

            if (wasRecording)
            {
                await Task.Delay(1000);
                // this method of selecting a file will re-use an existing windows explorer window instead of opening a new one
                if (File.Exists(_fileName))
                    Platform.Current.RevealFileOrFolder(_fileName);
                else
                    Platform.Current.RevealFileOrFolder(_settings.OutputDirectory);
            }
        }

        private void OnSpeakerToggle(object sender, EventArgs e)
        {
            _monitor.SpeakerEnabled = !_monitor.SpeakerEnabled;
        }

        private void OnMicrophoneToggle(object sender, EventArgs e)
        {
            _monitor.MicrophoneEnabled = !_monitor.MicrophoneEnabled;
        }

        private void OnSettings(object sender, EventArgs e)
        {
            //_pages.CreateSettingsPage().Open(SettingsCategory.Video);
        }

        private void OnDraw(object sender, EventArgs e)
        {
            _pages.CreateLiveDrawPage().Open();
        }

        private async void OnCancel(object sender, EventArgs e)
        {
            _isCancelled = true;
            _border.Dispose();
            _floating.Hide();
            if (IsRecording)
            {
                IsRecording = false;
                await _capturer.StopAsync();
            }
            this.Dispose();

            await Task.Delay(10 * 1000);
            if (File.Exists(_fileName))
                File.Delete(_fileName);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _border.Dispose();
            _floating.Close();
            _monitor.Dispose();
            Closed?.Invoke(this, new EventArgs());
        }
    }
}
