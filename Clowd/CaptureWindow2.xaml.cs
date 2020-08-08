using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Clowd.Controls;
using Clowd.Interop;
using Clowd.Utilities;
using Microsoft.Win32;
using PropertyChanged;
using ScreenVersusWpf;

namespace Clowd
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
            ths.Dep_SelectionRectangleChanged(d, e);
        }

        private static void IsCapturingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ths = (CaptureWindow2)d;
            ths.Dep_IsCaptureChanged(d, e);
        }

        public static CaptureWindow2 Current { get; private set; }

        public IntPtr Handle { get; private set; }

        private CaptureWindow2()
        {
            InitializeComponent();
        }

        private bool _adornerRegistered = false;
        private bool _initialized = false;

        public static async void ShowNewCapture()
        {
            if (Current != null)
            {
                Current.Activate();
                return;
            }

            Stopwatch sw = new Stopwatch();
            sw.Start();

            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - START");

            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - Create Window/Handle Start");
            Current = new CaptureWindow2();
            Current.Closed += (s, e) => Current = null;
            var fstCap = Current.fastCapturer.StartFastCapture(sw);
            var hWnd = new WindowInteropHelper(Current).EnsureHandle();
            Current.Handle = hWnd;
            var primary = ScreenTools.Screens.First().Bounds;
            var virt = ScreenTools.VirtualScreen.Bounds;
            USER32.SetWindowPos(hWnd, SWP_HWND.HWND_TOP, -primary.Left, -primary.Top, virt.Width, virt.Height, SWP.NOACTIVATE | SWP.ASYNCWINDOWPOS);
            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - Create Window/Handle Complete");

            await fstCap;

            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - Preparations Complete");

            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - Showing Window");
            if (!Debugger.IsAttached) Current.Topmost = true;
            Current.ContentRendered += (s, e) =>
            {
                Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - Render Complete");
                Current.fastCapturer.FinishFastCapture();
                Current.Activate();
            };
            Current.ShowActivated = true;
            Current.Show();
            Current._initialized = true;
            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - Show Complete");
        }

        private void ManageSelectionResizeHandlers(bool register)
        {
            if (register && !_adornerRegistered)
            {
                _adornerRegistered = true;
                const string template =
    "<ControlTemplate TargetType=\"Thumb\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
        "<Grid>" +
            "<Ellipse Fill = \"{TemplateBinding Background}\" />" +
            "<Ellipse Margin = \"1\" Fill = \"White\" />" +
            "<Ellipse Margin = \"2\" Fill = \"{TemplateBinding Background}\" />" +
        "</Grid>" +
    "</ControlTemplate>";
                Style style = new Style(typeof(Thumb));
                style.Setters.Add(new Setter(Thumb.BackgroundProperty, App.Current.Resources["HighlightBrush"]));
                style.Setters.Add(new Setter(Thumb.TemplateProperty, (ControlTemplate)System.Windows.Markup.XamlReader.Parse(template)));

                AdornerLayer adornerLayer = AdornerLayer.GetAdornerLayer(rootGrid);
                ResizingAdorner myAdorner = new ResizingAdorner(selectionBorder, style);
                myAdorner.SetupCustomResizeHandling((r) => SelectionRectangle = r.ToWpfRect());
                adornerLayer.Add(myAdorner);

                ScreenPoint mouseDownPos = default(ScreenPoint);
                ScreenRect originRect = default(ScreenRect);
                bool mouseDown = false;
                MouseButtonEventHandler mouseDownHandler = (sender, e) =>
                {
                    selectionBorder.CaptureMouse();
                    originRect = SelectionRectangle.ToScreenRect();
                    mouseDownPos = ScreenTools.GetMousePosition();
                    mouseDown = true;
                };
                MouseEventHandler mouseMoveHandler = (sender, e) =>
                {
                    if (!mouseDown)
                        return;
                    var cur = ScreenTools.GetMousePosition();
                    var delta = mouseDownPos - cur;
                    var result = new ScreenRect(originRect.Left - delta.X, originRect.Top - delta.Y, originRect.Width, originRect.Height);
                    result = result.Intersect(ScreenTools.VirtualScreen.Bounds);
                    SelectionRectangle = result.ToWpfRect();
                };
                MouseButtonEventHandler mouseUpHandler = (sender, e) =>
                {
                    selectionBorder.ReleaseMouseCapture();
                    mouseDown = false;
                };

                selectionBorder.MouseDown += mouseDownHandler;
                selectionBorder.MouseMove += mouseMoveHandler;
                selectionBorder.MouseUp += mouseUpHandler;
                selectionBorder.Cursor = Cursors.SizeAll;
                selectionBorder.IsHitTestVisible = true;
            }

            if (!register && _adornerRegistered)
            {
                _adornerRegistered = false;
                AdornerLayer adornerLayer = AdornerLayer.GetAdornerLayer(rootGrid);
                Console.WriteLine(adornerLayer);
                Console.WriteLine(selectionBorder);
                if (adornerLayer != null)
                {
                    var resize = adornerLayer.GetAdorners(selectionBorder).FirstOrDefault(a => a is ResizingAdorner);
                    if (resize != null)
                        adornerLayer.Remove(resize);

                    selectionBorder.RemoveRoutedEventHandlers(UserControl.MouseDownEvent);
                    selectionBorder.RemoveRoutedEventHandlers(UserControl.MouseMoveEvent);
                    selectionBorder.RemoveRoutedEventHandlers(UserControl.MouseUpEvent);
                    selectionBorder.Cursor = Cursors.Cross;
                    selectionBorder.IsHitTestVisible = false;
                }
            }
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

        private void Dep_IsCaptureChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var currently = (bool)e.NewValue;
            ManageSelectionResizeHandlers(!currently);
            if (!currently)
                UpdateButtonBarPosition();

            Storyboard sb = FindResource("BorderDashAnimation") as Storyboard;
            if (_initialized && !currently)
            {
                toolActionBarStackPanel.Visibility = Visibility.Visible;
                selectionBorder.Visibility = Visibility.Visible;
                sb.Begin();
            }
            else
            {
                toolActionBarStackPanel.Visibility = Visibility.Collapsed;
                selectionBorder.Visibility = Visibility.Collapsed;
                sb.Stop();
            }

            var lineW = ScreenTools.WpfSnapToPixelsFloor(currently ? 1 : 2);
            var margin = new Thickness(-ScreenTools.WpfSnapToPixelsFloor(currently ? 0 : 2));
            crectBottom.StrokeThickness = crectTop.StrokeThickness = lineW;
            crectBottom.Margin = crectTop.Margin = margin;
        }

        private void Dep_SelectionRectangleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsCapturing)
            {
                var newv = (WpfRect)e.NewValue;

                Storyboard sb = FindResource("BorderDashAnimation") as Storyboard;

                if (newv == default(WpfRect) && selectionBorder.Visibility == Visibility.Visible)
                {
                    sb.Stop();
                    selectionBorder.Visibility = Visibility.Collapsed;
                }
                else if (selectionBorder.Visibility == Visibility.Collapsed)
                {
                    selectionBorder.Visibility = Visibility.Visible;
                    sb.Begin();
                }

                UpdateButtonBarPosition();
            }
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

            var w = TemplatedWindow.CreateWindow("Edit Capture", new ImageEditorPage(cropped));
            var rectPos = SelectionRectangle;
            var primaryScreen = ScreenTools.Screens.First().Bounds.ToWpfRect();
            w.Left = rectPos.Left - primaryScreen.Left - App.Current.Settings.EditorSettings.CapturePadding - 7;
            w.Top = rectPos.Top - primaryScreen.Top - App.Current.Settings.EditorSettings.CapturePadding - 60;
            w.Show();
        }
        private void CopyExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                return;

            var cropped = CropBitmap();
            if (ClipboardEx.SetImage(cropped))
                Close();
            else
                this.ShowNotice(MessageBoxIcon.Error, "Unable to set clipboard data; try again later.");
        }
        private void SaveAsExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                return;

            var cropped = CropBitmap();
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.DefaultExt = ".png";
            dlg.Filter = "PNG files (.png)|*.png|All files (*.*)|*.*"; // Filter files by extension
            if (dlg.ShowDialog() == true)
            {
                cropped.Save(dlg.FileName, ImageFormat.Png);
                Close();
            }
        }
        private void ResetExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                return;

            fastCapturer.Reset();
        }
        private void UploadExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                return;

            this.Close();
            var cropped = CropBitmap();
            BitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                ms.Position = 0;
                byte[] b;
                using (BinaryReader br = new BinaryReader(ms))
                {
                    b = br.ReadBytes(Convert.ToInt32(ms.Length));
                }
                var task = UploadManager.Upload(b, "clowd-default.png");
            }
        }
        private void VideoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCapturing)
                return;

            if (!Directory.Exists(App.Current.Settings.VideoSettings.OutputDirectory))
            {
                this.ShowSettingsPrompt(SettingsCategory.Video, "You must set a video save directory in the video capture settings before recording a video");
            }
            else
            {
                fastCapturer.SetSelectedWindowForeground();
                new VideoOverlayWindow(SelectionRectangle.ToScreenRect()).Show();
                this.Close();
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

            Clipboard.SetText(fastCapturer.GetHoveredColor().ToHexRgb());
            this.Close();
        }

        private void SelectAllExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (!IsCapturing)
                return;

            fastCapturer.SelectAll();
        }
    }
}
