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
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Clowd.Interop;
using ScreenVersusWpf;

namespace Clowd.WinLegacy
{
    public class CaptureColorEventArgs : EventArgs
    {
        public Color SelectedColor { get; }

        public CaptureColorEventArgs(Color selectedColor)
        {
            SelectedColor = selectedColor;
        }
    }

    public class CaptureSelectionEventArgs : EventArgs
    {
        public System.Drawing.Rectangle Selection { get; }

        public CaptureSelectionEventArgs(System.Drawing.Rectangle selection)
        {
            Selection = selection;
        }
    }

    public class CaptureBitmapEventArgs : CaptureSelectionEventArgs
    {
        public BitmapSource Image { get; }

        public CaptureBitmapEventArgs(BitmapSource image, System.Drawing.Rectangle sel) : base(sel)
        {
            Image = image;
        }
    }

    public partial class CaptureWindow2 : OverlayWindow, IScreenCapturePage
    {
        public EventHandler<CaptureBitmapEventArgs> PhotoCommand;
        public EventHandler<CaptureBitmapEventArgs> CopyCommand;
        public EventHandler<CaptureBitmapEventArgs> SaveAsCommand;
        public EventHandler<CaptureBitmapEventArgs> UploadCommand;
        public EventHandler<CaptureBitmapEventArgs> SearchCommand;
        public EventHandler<CaptureSelectionEventArgs> VideoCommand;
        public EventHandler<CaptureColorEventArgs> ColorCommand;

        public bool IsPromptCapture
        {
            get { return (bool)GetValue(IsPromptCaptureProperty); }
            set { SetValue(IsPromptCaptureProperty, value); }
        }

        public bool HasCapturedArea => !IsCapturing;

        public System.Drawing.Rectangle Selection => SelectionRectangle.ToScreenRect().ToSystem();

        public static readonly bool IsPromptCaptureDefaultValue = false;

        public static readonly DependencyProperty IsPromptCaptureProperty =
            DependencyProperty.Register(nameof(IsPromptCapture), typeof(bool), typeof(CaptureWindow2),
                new PropertyMetadata(IsPromptCaptureDefaultValue, (s, e) => (s as CaptureWindow2)?.OnIsPromptCaptureChanged(s, e)));

        public event DependencyPropertyChangedEventHandler IsPromptCaptureChanged;

        protected virtual void OnIsPromptCaptureChanged(object sender, DependencyPropertyChangedEventArgs e)
            => this.IsPromptCaptureChanged?.Invoke(sender, e);

        //public static CaptureWindow2 Current { get; private set; }

        private readonly IScopedLog _timer;
        private readonly IPageManager _manager;

        //private readonly Action<BitmapSource> _callback;

        public CaptureWindow2(IScopedLog timer, IPageManager manager)
        {
            InitializeComponent();
            this._timer = timer;
            this._manager = manager;
            //this._callback = callback;
            this.ContentRendered += CaptureWindow2_ContentRendered;
            this.SelectionRectangleChanged += (s, e) => UpdateButtonPanelPosition(toolActionBarStackPanel);
            this.IsCapturingChanged += (s, e) => UpdateButtonPanelPosition(toolActionBarStackPanel);
        }

        //public static void ShowNewCapture(WpfRect? selection = null, Action<BitmapSource> callback = null)
        //{
        //    if (Current != null)
        //    {
        //        if (Current.SourceCreated)
        //            Current.Activate();
        //        return;
        //    }

        //    var timer = App.DefaultLog.CreateProfiledScope("Capture");
        //    timer.Info("Start");
        //    Current = new CaptureWindow2(timer, callback);
        //    Current.Closed += (s, e) => Current = null;
        //    Current.StartCaptureInstance(selection);
        //}

        public void Open()
        {
            StartCaptureInstance(null);
        }

        public void Open(ScreenRect area)
        {
            StartCaptureInstance(area.ToWpfRect());
        }

        public void Open(IntPtr hWnd)
        {
            var area = ScreenRect.FromSystem(Interop.USER32EX.GetTrueWindowBounds(hWnd));
            Open(area);
        }

        public void Dispose()
        {
            Close();
        }

        private void StartCaptureInstance(WpfRect? selection)
        {
            // creating this first is significantly faster for some reason
            this.EnsureHandle();
            _timer.Info("Source created");

            // this will create the bitmap and do the initial render ahead of time

            var opt = new FastRendererOptions()
            {
                IsDesignMode = false,
                AccentColor = Colors.Red,
                CaptureCursor = true,
                CompatibilityMode = false,
                DetectWindows = true,
            };

            _timer.RunProfiled("FastCap", (l) => fastCapturer.StartFastCapture(l, opt));

            if (selection.HasValue)
            {
                //IsPromptCapture = true;
                SelectionRectangle = selection.Value;
                fastCapturer.StopCapture();
            }

            _timer.RunProfiled("WinShow", (l) =>
            {
                Show();
                //throw new InvalidOperationException("AHHH");
            });
        }

        private async void CaptureWindow2_ContentRendered(object sender, EventArgs e)
        {
            // once our first render has finished, we can fire up low priorty tasks to capture window bitmaps
            _timer.Info("Rendered");

            using (var scoped = _timer.CreateProfiledScope("FinishUp"))
            {
                scoped.Info("Waiting 200ms...");
                await Task.Delay(200); // add a delay so that these expensive background operations dont interfere with initial window interactions / calculations
                await fastCapturer.FinishUpFastCapture(scoped);
            }

            _timer.Info("End");

            _timer.Dispose();
        }

        private BitmapSource CropBitmap()
        {
            return fastCapturer.GetSelectedBitmap();
        }

        //private Stream GetCompressedImageStream()
        //{
        //    var cropped = CropBitmap();
        //    BitmapEncoder encoder = new PngBitmapEncoder();
        //    encoder.Frames.Add(BitmapFrame.Create(cropped));
        //    var ms = new MemoryStream();
        //    encoder.Save(ms);
        //    ms.Position = 0;
        //    return ms;
        //}

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
            Close();
            PhotoCommand?.Invoke(this, new CaptureBitmapEventArgs(CropBitmap(), Selection));
        }

        private void CopyExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();
            CopyCommand?.Invoke(this, new CaptureBitmapEventArgs(CropBitmap(), Selection));
        }

        private void SaveAsExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();
            SaveAsCommand?.Invoke(this, new CaptureBitmapEventArgs(CropBitmap(), Selection));
        }

        private void ResetExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            fastCapturer.Reset();
        }

        private void UploadExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();
            UploadCommand?.Invoke(this, new CaptureBitmapEventArgs(CropBitmap(), Selection));
        }

        private void VideoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();
            VideoCommand?.Invoke(this, new CaptureSelectionEventArgs(Selection));
        }

        private void SelectScreenExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            fastCapturer.SelectScreen();
        }

        private void CloseExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                fastCapturer.StopCapture();

            this.Close();
        }

        private void SelectColorExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                fastCapturer.StopCapture();

            this.Close();

            ColorCommand?.Invoke(this, new CaptureColorEventArgs(fastCapturer.GetHoveredColor()));
        }

        private void SelectAllExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            fastCapturer.SelectAll();
        }

        private async void SearchExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();
            SearchCommand?.Invoke(this, new CaptureBitmapEventArgs(CropBitmap(), Selection));
        }

        private void ProfilerExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                fastCapturer.StopCapture();

            this.Close();

            fastCapturer.ShowProfiler();
        }
    }
}
