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
    public partial class CaptureWindow2 : OverlayWindow
    {
        public static CaptureWindow2 Current { get; private set; }

        private readonly TimedConsoleLogger _timer;
        private readonly Action<BitmapSource> _callback;

        private CaptureWindow2(TimedConsoleLogger timer, Action<BitmapSource> callback)
        {
            InitializeComponent();
            this._timer = timer;
            this._callback = callback;
            this.ContentRendered += CaptureWindow2_ContentRendered;
            this.SelectionRectangleChanged += (s, e) => UpdateButtonBarPosition();
            this.IsCapturingChanged += (s, e) => UpdateButtonBarPosition();
        }

        public static void ShowNewCapture(WpfRect? selection = null, Action<BitmapSource> callback = null)
        {
            if (Current != null)
            {
                if (Current.SourceCreated)
                    Current.Activate();
                return;
            }

            var timer = new TimedConsoleLogger("Capture", DateTime.Now);

            timer.Log("Total", "Start");
            timer.Log("Window", "Start");
            Current = new CaptureWindow2(timer, callback);

            if (selection.HasValue)
                Current.SelectionRectangle = selection.Value;

            Current.Closed += (s, e) => Current = null;

            timer.Log("Window", "Source created");
            Current.StartCaptureInstance();
        }

        private void StartCaptureInstance()
        {
            // this will create the bitmap and do the initial render ahead of time
            fastCapturer.StartFastCapture(_timer);
            _timer.Log("WinShow", "Showing Window");
            Show();
            _timer.Log("WinShow", "Showing Complete");
        }

        private async void CaptureWindow2_ContentRendered(object sender, EventArgs e)
        {
            // once our first render has finished, we can fire up low priorty tasks to capture window bitmaps
            _timer.Log("Window", "Rendered");

            await Task.Delay(200); // add a delay so that these expensive background operations dont interfere with initial window interactions / calculations
            await fastCapturer.FinishUpFastCapture(_timer);

            _timer.Log("Total", "End");

            _timer.PrintSummary();
        }

        private void UpdateButtonBarPosition()
        {
            var numberOfActiveButtons = toolActionBarStackPanel.Children
                .Cast<FrameworkElement>()
                .Where(f => f is Button)
                .Cast<Button>()
                .Count();

            toolActionBarStackPanel.SetPanelCanvasPositionRelativeToSelection(SelectionRectangle, 2, 10, 50, numberOfActiveButtons * 50 + 3);
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

        private void Command_IsCapturing(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = IsCapturing;
        }

        private void Command_IsNotCapturing(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = !IsCapturing;
        }

        private void PhotoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            var cropped = CropBitmap();
            Close();

            if (_callback != null)
            {
                _callback(cropped);
            }
            else
            {
                ImageEditorPage.ShowNewEditor(cropped, SelectionRectangle);
            }
        }

        private void CopyExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            var cropped = CropBitmap();
            if (ClipboardEx.SetImage(cropped))
                Close();
            else
                NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error, "Unable to set clipboard data; try again later.");
        }

        private async void SaveAsExecuted(object sender, ExecutedRoutedEventArgs e)
        {
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
            fastCapturer.Reset();
        }

        private async void UploadExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();

            await UploadManager.UploadImage(GetCompressedImageStream(), "png", viewName: "Screenshot");
        }

        private void VideoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
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
            fastCapturer.SelectScreen();
        }

        private void CloseExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();
        }

        private void SelectColorExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();
            NiceDialog.ShowColorDialogAsync(null, fastCapturer.GetHoveredColor());
        }

        private void SelectAllExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            fastCapturer.SelectAll();
        }

        private async void SearchExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();

            var task = await UploadManager.UploadImage(GetCompressedImageStream(), "png", viewName: "Image Search");
            if (task == null)
                return;

            var upload = await task.UploadResult;
            if (upload == null)
                return;

            Process.Start("https://images.google.com/searchbyimage?image_url=" + upload.PublicUrl.UrlEscape());
            task.TaskView.SetExecuted();
        }

        private void ProfilerExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            fastCapturer.ShowProfiler();
        }
    }
}
