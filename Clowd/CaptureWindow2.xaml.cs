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
            var newv = (WpfRect)e.NewValue;

            Storyboard sb = ths.FindResource("BorderDashAnimation") as Storyboard;
            if (newv == default(WpfRect) && ths.selectionBorder.Visibility == Visibility.Visible)
            {
                sb.Stop();
                ths.selectionBorder.Visibility = Visibility.Collapsed;
            }
            else if (ths.selectionBorder.Visibility == Visibility.Collapsed)
            {
                ths.selectionBorder.Visibility = Visibility.Visible;
                sb.Begin();
            }

            if (!ths.IsCapturing)
                ths.UpdateButtonBarPosition();
        }

        private static void IsCapturingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ths = (CaptureWindow2)d;
            var currently = (bool)e.NewValue;
            ths.ManageSelectionResizeHandlers(!currently);
            if (!currently)
                ths.UpdateButtonBarPosition();

            ths.toolActionBar.Visibility = ths._initialized && !currently ? Visibility.Visible : Visibility.Hidden;

            var lineW = ScreenTools.WpfSnapToPixelsFloor(currently ? 1 : 2);
            var margin = new Thickness(-ScreenTools.WpfSnapToPixelsFloor(currently ? 0 : 2));
            ths.crectBottom.StrokeThickness = ths.crectTop.StrokeThickness = lineW;
            ths.crectBottom.Margin = ths.crectTop.Margin = margin;
        }

        private CaptureWindow2()
        {
            InitializeComponent();
        }

        private bool _adornerRegistered = false;
        private bool _initialized = false;
        private static CaptureWindow2 _readyWindow;

        public static async void ShowNewCapture()
        {
            if (_readyWindow != null)
                return;

            Stopwatch sw = new Stopwatch();
            sw.Start();

            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - START");

            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - Create Window/Handle Start");
            _readyWindow = new CaptureWindow2();
            var fstCap = _readyWindow.fastCapturer.StartFastCapture(sw);
            _readyWindow.Closed += (s, e) => { _readyWindow = null; };
            var hWnd = new WindowInteropHelper(_readyWindow).EnsureHandle();
            var primary = ScreenTools.Screens.First().Bounds;
            var virt = ScreenTools.VirtualScreen.Bounds;
            USER32.SetWindowPos(hWnd, SWP_HWND.HWND_TOP, -primary.Left, -primary.Top, virt.Width, virt.Height, SWP.NOACTIVATE | SWP.ASYNCWINDOWPOS);
            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - Create Window/Handle Complete");

            await fstCap;

            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - Preparations Complete");

            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - Showing Window");
            if (!Debugger.IsAttached) _readyWindow.Topmost = true;
            _readyWindow.ContentRendered += async (s, e) =>
            {
                Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - Render Complete");
                await Task.Delay(1);
                _readyWindow.fastCapturer.FinishFastCapture();
                _readyWindow.Activate();
                Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - END");
                sw.Stop();
                _readyWindow.WindowReady();
            };
            _readyWindow.ShowActivated = false;
            _readyWindow.Show();
            _readyWindow._initialized = true;
            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - Show Complete");
        }

        private void WindowReady()
        {
            var uploadCommand = Resources["Command.Upload"] as RoutedCommand;
            if (!App.CanUpload)
                CommandBindings.Remove(CommandBindings.Cast<CommandBinding>().Single(c => c.Command == uploadCommand));
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
            var container = toolActionBar;
            var panel = toolActionBarStackPanel;

            var numberOfButtons = panel.Children
                .Cast<FrameworkElement>()
                .Where(f => f is Button)
                .Cast<Button>()
                .Where(b => b.Command.CanExecute(null))
                .Count();

            var containerSize = numberOfButtons * 50;

            var selection = SelectionRectangle;
            var selectionScreen = ScreenTools.GetScreenContaining(SelectionRectangle.ToScreenRect()).Bounds.ToWpfRect();
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
                if (panel.Orientation == Orientation.Vertical)
                {
                    panel.Orientation = Orientation.Horizontal;
                    //this extension will cause wpf to render the pending changes, so that we can calculate the
                    //correct toolbar size below.
                    this.DoRender();
                }
                indLeft = intersecting.Left + intersecting.Width / 2 - containerSize / 2;
                indTop = bottomSpace >= 60 ? intersecting.Bottom + 5 : intersecting.Bottom;
            }
            else if (rightSpace >= 50)
            {
                if (panel.Orientation == Orientation.Horizontal)
                {
                    panel.Orientation = Orientation.Vertical;
                    this.DoRender();
                }
                indLeft = rightSpace >= 60 ? intersecting.Right + 5 : intersecting.Right;
                indTop = intersecting.Bottom - containerSize;
            }
            else if (leftSpace >= 50)
            {
                if (panel.Orientation == Orientation.Horizontal)
                {
                    panel.Orientation = Orientation.Vertical;
                    this.DoRender();
                }
                indLeft = leftSpace >= 60 ? intersecting.Left - 55 : intersecting.Left - 50;
                indTop = intersecting.Bottom - containerSize;
            }
            else
            {
                if (panel.Orientation == Orientation.Vertical)
                {
                    panel.Orientation = Orientation.Horizontal;
                    this.DoRender();
                }
                indLeft = intersecting.Left + intersecting.Width / 2 - containerSize / 2;
                indTop = intersecting.Bottom - 70;
            }
            if (indLeft < selectionScreen.Left)
                indLeft = selectionScreen.Left;
            else if (indLeft + containerSize > selectionScreen.Right)
                indLeft = selectionScreen.Right - containerSize;
            Canvas.SetLeft(container, indLeft);
            Canvas.SetTop(container, indTop);
        }

        private BitmapSource CropBitmap()
        {
            return fastCapturer.GetSelectedBitmap();
        }

        private void PhotoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
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
            var cropped = CropBitmap();
            if (ClipboardEx.SetImage(cropped))
                Close();
            else
                MessageBox.Show("Unable to set clipboard data; try again later.");
        }
        private void SaveAsExecuted(object sender, ExecutedRoutedEventArgs e)
        {
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
            fastCapturer.Reset();
        }
        private void UploadExecuted(object sender, ExecutedRoutedEventArgs e)
        {
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
            this.Close();
        }
        private void VideoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (!Directory.Exists(App.Current.Settings.VideoSettings.OutputDirectory))
            {
                MessageBox.Show("Please update your Video output directory in the settings before recording a video");
            }
            else
            {
                new VideoOverlayWindow(SelectionRectangle.ToScreenRect()).Show();
                this.Close();
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
        private void ToggleMagnifierExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            fastCapturer.ShowMagnifier = !fastCapturer.ShowMagnifier;
        }
    }
}
