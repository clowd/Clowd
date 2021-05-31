using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Clowd.Config;
using Clowd.UI.Helpers;
using ScreenVersusWpf;

namespace Clowd.UI
{
    internal sealed class VideoCaptureWindow : IVideoCapturePage
    {
        public event EventHandler Closed;

        //        public bool IsRecording { get; set; }
        //        public bool IsStarted { get; set; }
        //        public bool IsAudioSupported { get; set; }

        //        public bool IsMicrophoneEnabled { get; set; }
        //        public bool IsLoopbackEnabled { get; set; }

        private IVideoCapturer _capturer;
        private readonly IPageManager _pages;
        private VideoCapturerSettings _settings;
        private UIAudioMonitor _monitor;
        private string _fileName;
        private ClowdWin64.BorderWindow _border;
        private FloatingButtonWindow _floating;

        public VideoCaptureWindow(VideoCapturerSettings settings, IVideoCapturer capturer, IPageManager pages)
        {
            _border = new ClowdWin64.BorderWindow();
            _capturer = capturer;
            _pages = pages;
            _settings = settings;

            var _buttons = new List<FloatingButtonDetail>();

            _buttons.Add(new FloatingButtonDetail
            {
                Primary = true,
                Enabled = true,
                Label = "Start",
                IconResourceName = "IconPlay",
                PulseBackground = true,
                Executed = OnStart,
            });

            _buttons.Add(new FloatingButtonDetail
            {
                Primary = true,
                Enabled = true,
                Label = "Finish",
                IconResourceName = "IconPlay",
                //Executed = OnStop,
            });

            //_buttons.Add(new FloatingButtonDetail
            //{
            //    Enabled = true,
            //    Label = "Tune",
            //    IconResourceName = "IconSettings",
            //    Executed = OnSettings,
            //});

            //_buttons.Add(new FloatingButtonDetail
            //{
            //    Enabled = true,
            //    Label = "Tune",
            //    IconResourceName = "IconSettings",
            //    Executed = OnSettings,
            //});

            _buttons.Add(new FloatingButtonDetail
            {
                Enabled = true,
                Label = "Tune",
                IconResourceName = "IconSettings",
                Executed = OnSettings,
            });

            _buttons.Add(new FloatingButtonDetail
            {
                Enabled = true,
                Label = "Draw",
                IconResourceName = "IconDrawing",
                Executed = OnDraw,
            });

            _buttons.Add(new FloatingButtonDetail
            {
                Enabled = true,
                Label = "Cancel",
                IconResourceName = "IconClose",
                Executed = OnCancel,
            });
        }

        public async void Open(ScreenRect captureArea)
        {
            var sys = captureArea.ToSystem();
            var clr = System.Drawing.Color.FromArgb(App.Current.AccentColor.A, App.Current.AccentColor.R, App.Current.AccentColor.G, App.Current.AccentColor.B);
            _border.OverlayText = "Press Start";
            _border.Show(clr, sys);
            _floating.ShowPanel(sys, IntPtr.Zero);
            _monitor = new UIAudioMonitor(_settings, _floating.Dispatcher, 20);
        }

        private void OnStart(object sender, EventArgs e)
        {
        }

        private void OnSettings(object sender, EventArgs e)
        {
        }

        private void OnDraw(object sender, EventArgs e)
        {
        }

        private void OnCancel(object sender, EventArgs e)
        {
        }

        public void Close()
        {
            Dispose();
        }

        public void Dispose()
        {
            _monitor.Dispose();
            _floating.HidePanel();
            _border.Hide();
            _border.Dispose();
        }
    }
}
