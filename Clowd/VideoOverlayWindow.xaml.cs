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
//using Screeney;
using ScreenVersusWpf;

namespace Clowd
{
    [PropertyChanged.ImplementPropertyChanged]
    public partial class VideoOverlayWindow : Window
    {
        public ScreenRect CroppingRectangle { get; private set; } = new ScreenRect(0, 0, 0, 0);
        public Rect CroppingRectangleWpf { get { return CroppingRectangle.ToWpfRect(); } private set { CroppingRectangle = new WpfRect(value).ToScreenRect(); } }
        public IntPtr Handle { get; private set; }

        public Color BorderColor { get; set; } = Colors.Green;

        private ScreenUtil.LiveScreenRecording recording;
        //private ScreeneyRecorder _recorder;
        //private Recording _capture;

        public VideoOverlayWindow(ScreenRect captureArea)
        {
            CroppingRectangle = captureArea;
            InitializeComponent();
            this.SourceInitialized += VideoOverlayWindow_SourceInitialized;
            this.Loaded += VideoOverlayWindow_Loaded;
            Width = Height = 1; // the window becomes visible very briefly before it's redrawn with the captured screenshot; this makes it unnoticeable

            recording = ScreenUtil.PrepareVideoRecording(captureArea);

            //_recorder = new ScreeneyRecorder(App.Current.Settings.VideoSettings);

            //var topLeft = new ScreenPoint(captureArea.Left, captureArea.Top);
            //var bottomRight = new ScreenPoint(captureArea.Left + captureArea.Width, captureArea.Top + captureArea.Height);
            //try
            //{
            //    ScreenTools.Screens.Single(s => s.Bounds.Contains(topLeft) && s.Bounds.Contains(bottomRight)).Bounds.ToWpfRect();
            //}
            //catch (Exception e)
            //{
            //    MessageBox.Show("Video capture must be entirely contained within a single screen");
            //}

            //if (!Directory.Exists(App.Current.Settings.VideoSettings.OutputDirectory))
            //{
            //    MessageBox.Show("Please set a video output directory in the application video settings.");
            //}
        }

        private void VideoOverlayWindow_SourceInitialized(object sender, EventArgs e)
        {
            this.Handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        }

        private void VideoOverlayWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (System.Diagnostics.Debugger.IsAttached)
                this.Topmost = false;
            var primary = ScreenTools.Screens.First().Bounds;
            var virt = ScreenTools.VirtualScreen.Bounds;
            // WPF makes some fairly inconvenient DPI conversions to Left and Top which have also changed between NET 4.5 and 4.8; just use WinAPI instead of de-converting them
            Interop.USER32.SetWindowPos(this.Handle, 0, -primary.Left, -primary.Top, virt.Width, virt.Height, Interop.SWP.SHOWWINDOW);
            Interop.USER32.SetForegroundWindow(this.Handle);
            this.DoRender();
            UpdateCanvasPlacement();
        }

        private void UpdateCanvasPlacement()
        {
            var selection = CroppingRectangle.ToWpfRect();
            var selectionScreen = ScreenTools.GetScreenContaining(CroppingRectangle).Bounds.ToWpfRect();
            var bottomSpace = Math.Max(selectionScreen.Bottom - selection.Bottom, 0);
            var rightSpace = Math.Max(selectionScreen.Right - selection.Right, 0);
            var leftSpace = Math.Max(selection.Left - selectionScreen.Left, 0);
            double indLeft = 0, indTop = 0;
            //we want to display (and clip) the controls on/to the primary screen -
            //where the primary screen is the screen that contains the center of the cropping rectangle
            var intersecting = selectionScreen.Intersect(selection);
            if (intersecting == WpfRect.Empty)
                return; // not supposed to happen since selectionScreen contains the center of selection rect
            if (bottomSpace >= 50)
            {
                if (toolActionBarStackPanel.Orientation == Orientation.Vertical)
                {
                    toolActionBarStackPanel.Orientation = Orientation.Horizontal;
                    //this extension will cause wpf to render the pending changes, so that we can calculate the
                    //correct toolbar size below.
                    this.DoRender();
                }
                indLeft = intersecting.Left + intersecting.Width / 2 - toolActionBar.ActualWidth / 2;
                indTop = bottomSpace >= 60 ? intersecting.Bottom + 5 : intersecting.Bottom;
            }
            else if (rightSpace >= 50)
            {
                if (toolActionBarStackPanel.Orientation == Orientation.Horizontal)
                {
                    toolActionBarStackPanel.Orientation = Orientation.Vertical;
                    this.DoRender();
                }
                indLeft = rightSpace >= 60 ? intersecting.Right + 5 : intersecting.Right;
                indTop = intersecting.Bottom - toolActionBar.ActualHeight;
            }
            else if (leftSpace >= 50)
            {
                if (toolActionBarStackPanel.Orientation == Orientation.Horizontal)
                {
                    toolActionBarStackPanel.Orientation = Orientation.Vertical;
                    this.DoRender();
                }
                indLeft = leftSpace >= 60 ? intersecting.Left - 55 : intersecting.Left - 50;
                indTop = intersecting.Bottom - toolActionBar.ActualHeight;
            }
            else
            {
                if (toolActionBarStackPanel.Orientation == Orientation.Vertical)
                {
                    toolActionBarStackPanel.Orientation = Orientation.Horizontal;
                    this.DoRender();
                }
                indLeft = intersecting.Left + intersecting.Width / 2 - toolActionBar.ActualWidth / 2;
                indTop = intersecting.Bottom - 70;
            }
            if (indLeft < selectionScreen.Left)
                indLeft = selectionScreen.Left;
            else if (indLeft + toolActionBar.ActualWidth > selectionScreen.Right)
                indLeft = selectionScreen.Right - toolActionBar.ActualWidth;
            Canvas.SetLeft(toolActionBar, indLeft);
            Canvas.SetTop(toolActionBar, indTop);
        }

        private async void buttonStart_Click(object sender, RoutedEventArgs e)
        {
            labelCountdown.Visibility = Visibility.Visible;
            buttonStart.Visibility = Visibility.Collapsed;
            buttonStop.Visibility = Visibility.Visible;

            for (int i = 4; i >= 1; i--)
            {
                labelCountdown.Text = i.ToString();
                await Task.Delay(1000);
            }

            labelCountdown.Visibility = Visibility.Collapsed;
            await recording.Start();
            //_capture = _recorder.OpenCapture(CroppingRectangle);
            //this.DoRender(); // give a chance for the countdown to dissapear 
            //_capture.Start();
        }

        private async void buttonStop_Click(object sender, RoutedEventArgs e)
        {
            await recording.Stop();
            //_capture.Finish();
            //this.Close();
            //await Task.Delay(1000);
            //Process.Start("explorer.exe", $"/select,\"{_capture.FileName}\"");
        }
    }
}
