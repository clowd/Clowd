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

        private LiveScreenRecording _recording;
        private bool _isCancelled = false;
        private bool _isRecording = false;

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
                else
                {
                    recordingFpsLabel.Text = "NO DATA";
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
            toolActionBarStackPanel.SetPanelCanvasPositionRelativeToSelection(CroppingRectangle.ToWpfRect(), 2, 10, 50, 153);
            StartRecording();
        }

        private async void StartRecording()
        {
            labelCountdown.Visibility = Visibility.Visible;

            for (int i = 4; i >= 1; i--)
            {
                labelCountdown.Text = i.ToString();
                recordingFpsLabel.Text = "REC in " + i.ToString();
                await Task.Delay(1000);
                if (_isCancelled)
                    return;
            }

            labelCountdown.Visibility = Visibility.Collapsed;
            recordingFpsLabel.Text = "Starting";

            _isRecording = true;

            try
            {
                await _recording.Start();
            }
            catch (Exception ex)
            {
                this.Close();

                var filename = "ffmpeg_error_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                File.WriteAllText(filename, _recording.ConsoleLog);

                using (var dialog = new TaskDialog())
                {
                    dialog.MainIcon = TaskDialogIcon.Error;
                    dialog.MainInstruction = "Recording Error";
                    dialog.Content = "An unexpected error was encountered while trying to start recording. A log file has been created in your video output directory.";

                    var open = new TaskDialogButton("Open Error Log");
                    var close = new TaskDialogButton(ButtonType.Close);
                    dialog.Buttons.Add(open);
                    dialog.Buttons.Add(close);
                    if (open == dialog.Show())
                    {
                        Process.Start("notepad.exe", filename);
                    }
                }
            }
        }

        private async void buttonStop_Click(object sender, RoutedEventArgs e)
        {
            var wasRecording = _isRecording;
            buttonCancel_Click(sender, e);
            if (wasRecording)
            {
                await Task.Delay(1000);
                Process.Start("explorer.exe", $"/select,\"{_recording.FileName}\"");
            }
        }

        private async void buttonCancel_Click(object sender, RoutedEventArgs e)
        {
            _isCancelled = true;
            if (_isRecording)
            {
                _isRecording = false;
                await _recording.Stop();
            }
            this.Close();
        }
    }
}
