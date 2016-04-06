using Clowd.Controls;
using Clowd.Utilities;
using ScreenVersusWpf;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clowd
{
    [PropertyChanged.ImplementPropertyChanged]
    public partial class CaptureWindow : Window
    {
        //Disclaimer, I started writing this using MVVM and then ditched that idea, so code is kind of inconsistant.

        public Cursor CanvasCursor { get; private set; } = Cursors.Cross;
        public ScreenRect CroppingRectangle { get; private set; } = new ScreenRect(0, 0, 0, 0);
        public Rect CroppingRectangleWpf { get { return CroppingRectangle.ToWpfRect(); } private set { CroppingRectangle = new WpfRect(value).ToScreenRect(); } }
        public BitmapSource GrayScreenImage { get; private set; }
        public IntPtr Handle { get; private set; }
        public BitmapSource ScreenImage { get; private set; }
        public bool ShowMagnifier { get; private set; } = App.Current.Settings.CaptureSettings.MagnifierEnabled;
        public bool ShowTips { get; private set; } = App.Current.Settings.CaptureSettings.TipsEnabled;

        private bool? capturing = null;
        private bool draggingArea = false;
        private ScreenPoint draggingOrigin = default(ScreenPoint);
        private WindowFinder2 windowFinder = new WindowFinder2();

        private CaptureWindow()
        {
            InitializeComponent();
            this.SourceInitialized += CaptureWindow_SourceInitialized;
            this.Loaded += CaptureWindow_Loaded;
        }

        public static System.Drawing.Bitmap MakeGrayscale3(System.Drawing.Bitmap original)
        {
            //create a blank bitmap the same size as original
            var newBitmap = new System.Drawing.Bitmap(original.Width, original.Height);

            //get a graphics object from the new image
            var g = System.Drawing.Graphics.FromImage(newBitmap);

            //create the grayscale ColorMatrix
            var colorMatrix = new System.Drawing.Imaging.ColorMatrix(
               new float[][]
               {
                 new float[] {.3f, .3f, .3f, 0, 0},
                 new float[] {.59f, .59f, .59f, 0, 0},
                 new float[] {.11f, .11f, .11f, 0, 0},
                 new float[] {0, 0, 0, 1, 0},
                 new float[] {0, 0, 0, 0, 1}
               });

            //create some image attributes
            var attributes = new System.Drawing.Imaging.ImageAttributes();

            //set the color matrix attribute
            attributes.SetColorMatrix(colorMatrix);

            //draw the original image on the new image
            //using the grayscale color matrix
            g.DrawImage(original, new System.Drawing.Rectangle(0, 0, original.Width, original.Height),
               0, 0, original.Width, original.Height, System.Drawing.GraphicsUnit.Pixel, attributes);

            //dispose the Graphics object
            g.Dispose();
            return newBitmap;
        }
        public static async Task<CaptureWindow> ShowNew()
        {
            var c = new CaptureWindow();
            if (TaskWindow.Current?.IsVisible == true)
            {
                await TaskWindow.Current.Hide();
            }
            c.Show();
            return c;
        }
        private void CaptureBitmap()
        {
            using (var source = ScreenUtil.Capture(captureCursor: App.Current.Settings.CaptureSettings.ScreenshotWithCursor))
            {
                ScreenImage = source.ToBitmapSource();
                GrayScreenImage = new FormatConvertedBitmap(ScreenImage, PixelFormats.Gray8, BitmapPalettes.Gray256, 1);
            }
        }

        private void CaptureWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (System.Diagnostics.Debugger.IsAttached)
                this.Topmost = false;
            this.Left = SystemParameters.VirtualScreenLeft;
            this.Top = SystemParameters.VirtualScreenTop;
            this.Width = SystemParameters.VirtualScreenWidth;
            this.Height = SystemParameters.VirtualScreenHeight;
            Interop.USER32.SetForegroundWindow(this.Handle);
            UpdateCanvasMode(true);
        }
        private void CaptureWindow_SourceInitialized(object sender, EventArgs e)
        {
            this.Handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            CaptureBitmap();
            windowFinder.Capture();
        }

        private void ManageCanvasMouseHandlers(bool register)
        {
            if (register)
            {
                rootGrid.MouseDown += RootGrid_MouseDown;
                rootGrid.MouseMove += RootGrid_MouseMove;
                rootGrid.MouseUp += RootGrid_MouseUp;
            }
            else
            {
                rootGrid.MouseDown -= RootGrid_MouseDown;
                rootGrid.MouseMove -= RootGrid_MouseMove;
                rootGrid.MouseUp -= RootGrid_MouseUp;
            }
        }
        private void ManageSelectionResizeHandlers(bool register)
        {
            if (register)
            {
                const string template =
    "<ControlTemplate TargetType=\"Thumb\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
        "<Grid>" +
            "<Ellipse Fill = \"{TemplateBinding Background}\" />" +
            "<Ellipse Margin = \"1\" Fill = \"White\" />" +
            "<Ellipse Margin = \"2\" Fill = \"{TemplateBinding Background}\" />" +
        "</Grid>" +
    "</ControlTemplate>";
                Style style = new Style(typeof(Thumb));
                //style.Setters.Add(new Setter(Thumb.OpacityProperty, 0.7));
                //new SolidColorBrush(Color.FromRgb(59, 151, 210) clowd color
                style.Setters.Add(new Setter(Thumb.BackgroundProperty, App.Current.Resources["HighlightBrush"]));
                style.Setters.Add(new Setter(Thumb.TemplateProperty, (ControlTemplate)System.Windows.Markup.XamlReader.Parse(template)));

                AdornerLayer adornerLayer = AdornerLayer.GetAdornerLayer(rootGrid);
                ResizingAdorner myAdorner = new ResizingAdorner(selectionBorder, style);
                myAdorner.SetupCustomResizeHandling(UpdateCanvasSelection);
                adornerLayer.Add(myAdorner);

                ScreenPoint mouseDownPos = default(ScreenPoint);
                ScreenRect originRect = default(ScreenRect);
                bool mouseDown = false;
                MouseButtonEventHandler mouseDownHandler = (sender, e) =>
                {
                    selectionBorder.CaptureMouse();
                    originRect = CroppingRectangle;
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
                    UpdateCanvasSelection(result);
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
            }
            else
            {
                AdornerLayer adornerLayer = AdornerLayer.GetAdornerLayer(rootGrid);
                var resize = adornerLayer.GetAdorners(selectionBorder)?.Where(a => a is ResizingAdorner)?.FirstOrDefault();
                if (resize != null)
                    adornerLayer.Remove(resize);

                selectionBorder.RemoveRoutedEventHandlers(UserControl.MouseDownEvent);
                selectionBorder.RemoveRoutedEventHandlers(UserControl.MouseMoveEvent);
                selectionBorder.RemoveRoutedEventHandlers(UserControl.MouseUpEvent);
                selectionBorder.Cursor = Cursors.Cross;
            }
        }

        private static void PositionWithinAScreen(FrameworkElement element, WpfPoint anchor, HorizontalAlignment horz, VerticalAlignment vert, double distance)
        {
            var scr = ScreenTools.Screens.FirstOrDefault(s => s.Bounds.ToWpfRect().Contains(anchor));
            if (scr == null)
                scr = ScreenTools.Screens.OrderBy(s => Math.Min(
                     Math.Min(Math.Abs(s.Bounds.Left - anchor.X), Math.Abs(s.Bounds.Right - anchor.X)),
                     Math.Min(Math.Abs(s.Bounds.Top - anchor.Y), Math.Abs(s.Bounds.Bottom - anchor.Y)))
                ).First();
            var screen = scr.Bounds.ToWpfRect();

            var alignCoordinate = RT.Util.Ut.Lambda((int mode, double anchorXY, double elementSize, double screenMin, double screenMax) =>
            {
                for (int repeat = 0; repeat < 2; repeat++) // repeat twice to allow a right-align to flip left and vice versa
                {
                    if (mode > 0) // right/bottom alignment: left/top edge aligns with anchor
                    {
                        var xy = anchorXY + distance;
                        if (xy + elementSize < screenMax)
                            return xy; // it fits
                        else if (anchorXY + elementSize < screenMax)
                            return screenMax - elementSize; // it fits if we shrink the distance from anchor point
                        else // it doesn't fit either way; flip alignment
                            mode = -1;
                    }
                    if (mode < 0) // left/top alignment: right/bottom edge aligns with anchor
                    {
                        var xy = anchorXY - distance - elementSize;
                        if (xy >= screenMin)
                            return xy; // it fits
                        else if (anchorXY - elementSize >= screenMin)
                            return screenMin; // it fits if we shrink the distance from anchor point
                        else
                            mode = 1; // it doesn't fit either way
                    }
                }
                // We're here either because the element is center-aligned or is larger than the screen
                return anchorXY - elementSize / 2;
            });

            double x = alignCoordinate(horz == HorizontalAlignment.Left ? -1 : horz == HorizontalAlignment.Right ? 1 : 0, anchor.X, element.ActualWidth, screen.Left, screen.Right);
            double y = alignCoordinate(vert == VerticalAlignment.Top ? -1 : vert == VerticalAlignment.Bottom ? 1 : 0, anchor.Y, element.ActualHeight, screen.Top, screen.Bottom);

            x = Math.Max(screen.Left, Math.Min(screen.Right - element.ActualWidth, x));
            y = Math.Max(screen.Top, Math.Min(screen.Bottom - element.ActualHeight, y));

            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
        }

        private void PhotoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            var cropped = new CroppedBitmap(ScreenImage, CroppingRectangle);
            BitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            var path = System.IO.Path.GetTempFileName() + ".png";
            using (var fileStream = new System.IO.FileStream(path, System.IO.FileMode.Create))
            {
                encoder.Save(fileStream);
            }
            this.Close();
            TemplatedWindow.CreateWindow("Edit Capture", new ImageEditorPage(path)).Show();
        }
        private void ResetExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            UpdateCanvasMode(true);
            //UpdateCanvasSelection(new Rect(0, 0, 0, 0));
        }
        private void UploadExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            var cropped = new CroppedBitmap(ScreenImage, CroppingRectangle);
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
            //var rec = new Screeney.ScreeneyRecorder(new System.Drawing.Rectangle(0, 0, 100, 100));
            //rec.GetVideoSources();
            MessageBox.Show("This feature is temporarily disabled.");
        }
        private void SelectScreenExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            var screenContainingMouse = ScreenTools.GetScreenContaining(ScreenTools.GetMousePosition()).Bounds;
            UpdateCanvasMode(false);
            UpdateCanvasSelection(screenContainingMouse);
        }
        private void CloseExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();
        }

        private void RootGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var wfMouse = System.Windows.Forms.Cursor.Position;
                ShowTips = false;
                ((UIElement)sender).CaptureMouse();
                draggingArea = true;
                draggingOrigin = ScreenTools.GetMousePosition();
            }
        }
        private void RootGrid_MouseMove(object sender, MouseEventArgs e)
        {
            var currentPoint = ScreenTools.GetMousePosition();

            if (ShowMagnifier)
            {
                PositionWithinAScreen(pixelMagnifier, currentPoint.ToWpfPoint(), HorizontalAlignment.Right, VerticalAlignment.Bottom, 20);
                pixelMagnifier.DrawMagnifier(currentPoint);
            }

            if (draggingArea)
            {
                var rect = new ScreenRect();
                rect.Left = Math.Min(draggingOrigin.X, currentPoint.X);
                rect.Top = Math.Min(draggingOrigin.Y, currentPoint.Y);
                rect.Width = Math.Abs(draggingOrigin.X - currentPoint.X) + 1;
                rect.Height = Math.Abs(draggingOrigin.Y - currentPoint.Y) + 1;
                UpdateCanvasSelection(rect);
            }
            else if (App.Current.Settings.CaptureSettings.DetectWindows)
            {
                var window = windowFinder.GetWindowThatContainsPoint(currentPoint);

                if (window.WindowRect == ScreenRect.Empty)
                {
                    UpdateCanvasSelection(new ScreenRect(0, 0, 0, 0));
                }
                else
                {
                    UpdateCanvasSelection(window.WindowRect);
                }
            }
            else
            {
                UpdateCanvasSelection(new ScreenRect(0, 0, 0, 0));
            }
        }
        private void RootGrid_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!draggingArea)
                return;
            ((UIElement)sender).ReleaseMouseCapture();
            draggingArea = false;

            UpdateCanvasMode(false);

            if (CroppingRectangle.Width < 15 && CroppingRectangle.Height < 15)
            {
                var window = windowFinder.GetWindowThatContainsPoint(ScreenTools.GetMousePosition());
                if (window.WindowRect == ScreenRect.Empty)
                {
                    UpdateCanvasSelection(new ScreenRect(0, 0, 0, 0));
                }
                else
                {
                    UpdateCanvasSelection(window.WindowRect);
                }
            }
        }

        private void UpdateCanvasMode(bool capture)
        {
            if (capture)
            {
                if (capturing != true)
                    ManageCanvasMouseHandlers(true);
                if (capturing == false)
                    ManageSelectionResizeHandlers(false);
                capturing = true;
                areaSizeIndicator.Visibility = Visibility.Visible;
                ShowTips = true;
                CanvasCursor = Cursors.Cross;
                pixelMagnifier.Visibility = ShowMagnifier ? Visibility.Visible : Visibility.Hidden;
                toolActionBar.Visibility = Visibility.Hidden;
                //crosshairBottomLeft.Width = rootGrid.ActualWidth;
                //crosshairBottomLeft.Height = rootGrid.ActualHeight;
                //crosshairTopRight.Width = rootGrid.ActualWidth;
                //crosshairTopRight.Height = rootGrid.ActualHeight;
                //crosshairBottomLeft.Visibility = Visibility.Visible;
                //crosshairTopRight.Visibility = Visibility.Visible;
            }
            else
            {
                if (capturing == true)
                {
                    ManageCanvasMouseHandlers(false);
                    ManageSelectionResizeHandlers(true);
                    capturing = false;
                }
                areaSizeIndicator.Visibility = Visibility.Hidden;
                pixelMagnifier.Visibility = Visibility.Hidden;
                //crosshairBottomLeft.Visibility = Visibility.Hidden;
                //crosshairTopRight.Visibility = Visibility.Hidden;
                ShowTips = false;
                CanvasCursor = Cursors.Arrow;
                toolActionBar.Visibility = Visibility.Visible;
            }
            UpdateCanvasPlacement();

            var args = new MouseEventArgs(Mouse.PrimaryDevice, 0);
            args.RoutedEvent = MouseMoveEvent;
            rootGrid.RaiseEvent(args);
        }

        private void UpdateCanvasPlacement()
        {
            var selection = CroppingRectangle.ToWpfRect();
            if (capturing == true)
            {
                areaSizeIndicatorWidth.Text = CroppingRectangle.Width.ToString();
                areaSizeIndicatorHeight.Text = CroppingRectangle.Height.ToString();
                PositionWithinAScreen(areaSizeIndicator, new WpfPoint(selection.Left + selection.Width / 2, selection.Bottom), HorizontalAlignment.Center, VerticalAlignment.Bottom, 5);
            }
            else if (capturing == false)
            {
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
        }

        private void UpdateCanvasSelection(ScreenRect selection)
        {
            CroppingRectangle = selection;
            UpdateCanvasPlacement();
        }

        private void ToggleMagnifierExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            ShowMagnifier = !ShowMagnifier;
            if (capturing == true)
            {
                pixelMagnifier.Visibility = ShowMagnifier ? Visibility.Visible : Visibility.Collapsed;
                var args = new MouseEventArgs(Mouse.PrimaryDevice, 0);
                args.RoutedEvent = MouseMoveEvent;
                rootGrid.RaiseEvent(args);
            }
        }
    }
}