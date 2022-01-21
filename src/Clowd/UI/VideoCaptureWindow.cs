﻿using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Controls;
using Clowd.UI.Helpers;
using Clowd.UI.Unmanaged;
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
        private static SettingsVideo _settings => SettingsRoot.Current.Video;
        private UIAudioMonitor _monitor;
        private string _fileName;
        private FloatingButtonWindow _floating;
        private bool _isCancelled = false;

        public VideoCaptureWindow(IVideoCapturer capturer, IPageManager pages)
        {
            _pages = pages;

            _capturer = capturer;
            _capturer.StatusReceived += SynchronizationContextEventHandler.CreateDelegate<VideoStatusEventArgs>(CapturerStatusReceived);
            _capturer.CriticalError += SynchronizationContextEventHandler.CreateDelegate<VideoCriticalErrorEventArgs>(CapturerCriticalError);

            if (!Directory.Exists(_settings.OutputDirectory))
                _settings.OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            _btnClowd = new CaptureToolButton
            {
                Primary = true,
                Text = "CLOWD",
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconClowd),
                IsDragHandle = true,
            };

            _btnStart = new CaptureToolButton
            {
                Primary = true,
                Text = "Start",
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconPlay),
                PulseBackground = true,
                Executed = OnStart,
            };

            _btnStop = new CaptureToolButton
            {
                Primary = true,
                Text = "Finish",
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconStop),
                Executed = OnStop,
                Visibility = Visibility.Collapsed,
            };

            _btnMicrophone = new CaptureToolButton
            {
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconMicrophoneDisabled),
                IconPathAlternate = AppStyles.GetIconElement(ResourceIcon.IconMicrophoneEnabled),
                Executed = OnMicrophoneToggle,
                Text = "Mic",
            };

            _btnSpeaker = new CaptureToolButton
            {
                IconPath = AppStyles.GetIconElement(ResourceIcon.IconSpeakerDisabled),
                IconPathAlternate = AppStyles.GetIconElement(ResourceIcon.IconSpeakerEnabled),
                Executed = OnSpeakerToggle,
                Text = "Spk",
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

            var wpfclr = AppStyles.AccentColor;

            _monitor = new UIAudioMonitor(_capturer, _settings, 20);
            _btnMicrophone.Overlay = _monitor.GetMicrophoneVisual();
            _btnMicrophone.SetBinding(CaptureToolButton.ShowAlternateIconProperty, _monitor.GetMicrophoneEnabledBinding());
            _btnSpeaker.Overlay = _monitor.GetSpeakerVisual();
            _btnSpeaker.SetBinding(CaptureToolButton.ShowAlternateIconProperty, _monitor.GetSpeakerEnabledBinding());

            BorderWindow.Show(wpfclr, captureArea);
            BorderWindow.SetText("Press Start");

            _floating.ShowPanel(captureArea);
        }

        private async void OnStart(object sender, EventArgs e)
        {
            _btnStart.IsEnabled = false;

            for (int i = 4; i >= 1; i--)
            {
                BorderWindow.SetText(i.ToString());
                //labelCountdown.FontSize = 120;
                _btnClowd.Text = "REC in " + i.ToString();
                await Task.Delay(1000);
                if (_isCancelled)
                    return;
            }

            BorderWindow.SetText(null);
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
            BorderWindow.Hide();
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
            BorderWindow.Hide();
            _floating.Close();
            _monitor.Dispose();
            Closed?.Invoke(this, new EventArgs());
        }
    }
}
