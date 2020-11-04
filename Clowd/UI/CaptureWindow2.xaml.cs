using System;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Clowd.Capture;
using Clowd.Config;
using Clowd.Interop;
using Clowd.UI.Helpers;
using Clowd.Util;
using RT.Util.ExtensionMethods;
using ScreenVersusWpf;

namespace Clowd.UI
{
    public partial class CaptureWindow2 : Window
    {
        public WpfRect SelectionRectangle
        {
            get { return (WpfRect)GetValue(SelectionRectangleProperty); }
            set { SetValue(SelectionRectangleProperty, value); }
        }
        public static readonly DependencyProperty SelectionRectangleProperty =
            DependencyProperty.Register(nameof(SelectionRectangle), typeof(WpfRect), typeof(CaptureWindow2), new PropertyMetadata(new WpfRect(), SelectionRectangleChanged));

        public bool IsCapturing
        {
            get { return (bool)GetValue(IsCapturingProperty); }
            set { SetValue(IsCapturingProperty, value); }
        }
        public static readonly DependencyProperty IsCapturingProperty =
            DependencyProperty.Register(nameof(IsCapturing), typeof(bool), typeof(CaptureWindow2), new PropertyMetadata(false, IsCapturingChanged));

        private static void SelectionRectangleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ths = (CaptureWindow2)d;
            ths.UpdateButtonBarPosition();
        }

        private static void IsCapturingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ths = (CaptureWindow2)d;
            ths.UpdateButtonBarPosition();
        }

        public static CaptureWindow2 Current { get; private set; }

        public IntPtr Handle { get; private set; }

        private CaptureWindow2()
        {
            InitializeComponent();
        }

        private bool _initialized = false;
        private Action<BitmapSource> _completeStitch;

        public static void ShowNewCapture()
        {
            StartCapture(null, null);
        }

        public static void NewStitchCapture(Rect? captureBounds, Action<BitmapSource> completeStitch)
        {
            StartCapture((w) =>
            {
                w._completeStitch = completeStitch;
            }, (w) =>
            {
                if (captureBounds.HasValue)
                {
                    w.fastCapturer.StopCapture();
                    w.SelectionRectangle = new WpfRect(captureBounds.Value);
                }
            });
        }

        private static void StartCapture(Action<CaptureWindow2> initialized, Action<CaptureWindow2> rendered)
        {
            if (Current != null)
            {
                // if Handle == IntPtr.Zero, the window is still opening, so will be activated when that is finished
                if (Current.Handle != IntPtr.Zero && Current._initialized)
                    Current.Activate();
                return;
            }

            var timer = new TimedConsoleLogger("Capture", DateTime.Now);
            timer.Log("Total", "Start");
            timer.Log("Window", "Start");

            Current = new CaptureWindow2();
            timer.Log("Window", "Init");
            Current.Closed += (s, e) => Current = null;
            Current.StartCapture(timer, initialized, rendered);
        }

        private void StartCapture(TimedConsoleLogger timer, Action<CaptureWindow2> initialized, Action<CaptureWindow2> rendered)
        {
            var interop = new WindowInteropHelper(this);

            // position the window a soon as the source handle has been created (either Show() or EnsureHandle())
            SourceInitialized += (s, e) =>
            {
                timer.Log("WinSource", "Source Init Begin");
                Handle = interop.Handle;
                var primary = ScreenTools.Screens.First().Bounds;
                var virt = ScreenTools.VirtualScreen.Bounds;
                var swp = Debugger.IsAttached ? SWP_HWND.HWND_TOP : SWP_HWND.HWND_TOPMOST;
                USER32.SetWindowPos(Handle, swp, -primary.Left, -primary.Top, virt.Width, virt.Height, SWP.NOACTIVATE);
                timer.Log("WinSource", "Source Init Complete");
                initialized?.Invoke(this);
            };

            // close capture window if we lose focus, but skip it if we are already closing
            EventHandler deactivated = (s, e) => this.Close();
            Activated += (s, e) => { Deactivated += deactivated; };
            Closing += (s, e) =>
            {
                Deactivated -= deactivated;
                if (fastCapturer.IsCapturing)
                    fastCapturer.StopCapture();
            };

            // once our first render has finished, we can fire up low priorty tasks to capture window bitmaps
            ContentRendered += async (s, e) =>
            {
                timer.Log("WinShow", "Rendered");
                Activate();
                _initialized = true;
                timer.Log("Window", "Activated");

                await Task.Delay(200); // add a delay so that these expensive background operations dont interfere with initial window interactions / calculations
                await fastCapturer.FinishUpFastCapture(timer);

                timer.PrintSummary();
                rendered?.Invoke(this);
            };

            // if we create the handle before the window is shown, this drastically speeds up the total time it takes to show the window. 
            // SetWindowPos (in SourceInitialized event) is 1-10ms now, but after Show can take > 100ms
            interop.EnsureHandle();

            // this will create the bitmap and do the initial render ahead of time
            fastCapturer.StartFastCapture(timer);

            timer.Log("WinShow", "Showing Window");
            Show();
            timer.Log("WinShow", "Showing Complete");
        }

        private void UpdateButtonBarPosition()
        {
            var numberOfActiveButtons = toolActionBarStackPanel.Children
                .Cast<FrameworkElement>()
                .Where(f => f is Button)
                .Cast<Button>()
                .Where(b => b.Command.CanExecute(null))
                .Count();

            toolActionBarStackPanel.SetPanelCanvasPositionRelativeToSelection(SelectionRectangle, 2, 10, 50, numberOfActiveButtons * 50);
        }

        private BitmapSource CropBitmap()
        {
            return fastCapturer.GetSelectedBitmap();
        }

        private Stream GetCompressedImageStream()
        {
            var cropped = CropBitmap();
            BitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            return ms;
        }

        private void UploadCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = App.CanUpload;
            e.Handled = true;
        }
        private void PhotoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                return;

            var cropped = CropBitmap();
            Close();

            if (_completeStitch != null)
            {
                _completeStitch(cropped);
            }
            else
            {
                ImageEditorPage.ShowNewEditor(cropped, SelectionRectangle);
            }
        }
        private void CopyExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                return;

            var cropped = CropBitmap();
            if (ClipboardEx.SetImage(cropped))
                Close();
            else
                NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error, "Unable to set clipboard data; try again later.");
        }
        private async void SaveAsExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                return;

            var filename = await NiceDialog.ShowSelectSaveFileDialog(this, "Save Screenshot", App.Current.Settings.LastSavePath, "screenshot", "png");

            if (String.IsNullOrWhiteSpace(filename))
            {
                return;
            }
            else
            {
                this.Close();
                var cropped = CropBitmap();
                cropped.Save(filename, ImageFormat.Png);
                Interop.Shell32.WindowsExplorer.ShowFileOrFolder(filename);
                App.Current.Settings.LastSavePath = System.IO.Path.GetDirectoryName(filename);
            }
        }
        private void ResetExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                return;

            fastCapturer.Reset();
        }
        private async void UploadExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                return;

            this.Close();

            await UploadManager.UploadImage(GetCompressedImageStream(), "png", viewName: "Screenshot");
        }
        private void VideoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                return;

            var rawRect = SelectionRectangle.ToScreenRect();

            const int minWidth = 160;
            const int minHeight = 160;

            this.Close();

            if (rawRect.Width < minWidth || rawRect.Height < minHeight)
            {
                NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Warning, $"The minimum frame size for video is {minWidth}x{minHeight}. Increase the capture area and try again.");
            }
            else if (!Directory.Exists(App.Current.Settings.VideoSettings.OutputDirectory))
            {
                NiceDialog.ShowSettingsPromptAsync(this, SettingsCategory.Video, "You must set a video save directory in the video capture settings before recording a video");
            }
            else
            {
                fastCapturer.SetSelectedWindowForeground();
                new VideoOverlayWindow(rawRect).Show();
            }
        }
        private void SelectScreenExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (!IsCapturing)
                return;

            fastCapturer.SelectScreen();
        }
        private void CloseExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();
        }
        private void SelectColorExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (!IsCapturing)
                return;

            this.Close();
            NiceDialog.ShowColorDialogAsync(null, fastCapturer.GetHoveredColor());
        }

        private void SelectAllExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (!IsCapturing)
                return;

            fastCapturer.SelectAll();
        }

        private async void SearchExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                return;

            this.Close();

            throw new NotImplementedException();

            //var task = await UploadManager.Upload(GetCompressedImageStream(), "png", "Search", null);
            //if (task == null)
            //    return;

            //Process.Start("https://images.google.com/searchbyimage?image_url=" + task.UrlEscape());
        }

        private void ProfilerExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            fastCapturer.ShowProfiler();
        }
    }
}
