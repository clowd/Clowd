using Clowd.Controls;
using Clowd.Utilities;
using Microsoft.Win32;
using ScreenVersusWpf;
using System;
using System.Drawing.Imaging;
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
        public BitmapSource PopupWindowImage { get; private set; }
        public ScreenRect PopupWindowRectangle { get; set; }
        public Rect PopupWindowRectangleWpf { get { return PopupWindowRectangle.ToWpfRect(); } private set { PopupWindowRectangle = new WpfRect(value).ToScreenRect(); } }
        public bool ShowMagnifier { get; private set; } = App.Current.Settings.CaptureSettings.MagnifierEnabled;
        public bool ShowTips { get; private set; } = App.Current.Settings.CaptureSettings.TipsEnabled;

        private bool? capturing = null;
        private bool draggingArea = false;
        private ScreenPoint draggingOrigin = default(ScreenPoint);
        private WindowFinder2 windowFinder = new WindowFinder2();
        private ScreenRect? initialRegion = null;

        private CaptureWindow(ScreenRect? initial = null)
        {
            initialRegion = initial;
            InitializeComponent();
            this.SourceInitialized += CaptureWindow_SourceInitialized;
            this.Loaded += CaptureWindow_Loaded;
            this.Closed += CaptureWindow_Closed;
            Width = Height = 1; // the window becomes visible very briefly before it's redrawn with the captured screenshot; this makes it unnoticeable
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
        public static async Task<CaptureWindow> ShowNew(ScreenRect? captureRegion = null)
        {
            var c = new CaptureWindow(captureRegion);
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
        private ScreenRect TruncatedCroppingRect()
        {
            var rect = CroppingRectangle;
            //x
            if (rect.Left < 0)
                rect.Left = 0;
            //y
            if (rect.Top < 0)
                rect.Top = 0;
            //width
            if (rect.Width > ScreenImage.PixelWidth)
                rect.Width = ScreenImage.PixelWidth;
            //height
            if (rect.Height > ScreenImage.PixelHeight)
                rect.Height = ScreenImage.PixelHeight;
            return rect;
        }
        private BitmapSource CropBitmap()
        {
            var rect = TruncatedCroppingRect();

            if (PopupWindowImage != null)
            {
                rect = new ScreenRect(rect.Left - PopupWindowRectangle.Left, rect.Top - PopupWindowRectangle.Top, rect.Width, rect.Height);
                return new CroppedBitmap(PopupWindowImage, rect);
            }
            else
            {
                return new CroppedBitmap(ScreenImage, rect);
            }
        }

        private void CaptureWindow_SourceInitialized(object sender, EventArgs e)
        {
            this.Handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            CaptureBitmap();
            windowFinder.NewCapture();
        }
        private void CaptureWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (System.Diagnostics.Debugger.IsAttached)
                this.Topmost = false;
            var primary = ScreenTools.Screens.First().Bounds;
            var virt = ScreenTools.VirtualScreen.Bounds;
            // WPF makes some fairly inconvenient DPI conversions to Left and Top which have also changed between NET 4.5 and 4.8; just use WinAPI instead of de-converting them
            Interop.USER32.SetWindowPos(this.Handle, 0, -primary.Left, -primary.Top, virt.Width, virt.Height, Interop.SWP.SHOWWINDOW);
            Interop.USER32.SetForegroundWindow(this.Handle);
            if (initialRegion == null)
            {
                UpdateCanvasMode(true);
            }
            else
            {
                UpdateCanvasMode(false);
                UpdateCanvasSelection(initialRegion.Value);
                ManageSelectionResizeHandlers(true);
            }

            if (App.Current.Settings.CaptureSettings.SelectedWindowPromotion != SelectedWindowForegroundPromotion.None)
            {
                windowFinder.PopulateWindowBitmaps();
            }
        }
        private void CaptureWindow_Closed(object sender, EventArgs e)
        {
            windowFinder.Dispose();
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
                var resize = adornerLayer.GetAdorners(selectionBorder).FirstOrDefault(a => a is ResizingAdorner);
                if (resize != null)
                    adornerLayer.Remove(resize);

                selectionBorder.RemoveRoutedEventHandlers(UserControl.MouseDownEvent);
                selectionBorder.RemoveRoutedEventHandlers(UserControl.MouseMoveEvent);
                selectionBorder.RemoveRoutedEventHandlers(UserControl.MouseUpEvent);
                selectionBorder.Cursor = Cursors.Cross;
            }
        }

        private WpfPoint PositionWithinAScreen(FrameworkElement element, WpfPoint anchor, HorizontalAlignment horz, VerticalAlignment vert, double distance)
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
            return new WpfPoint(x, y);
        }
        private double DistancePointToRectangle(WpfPoint point, Rect rect)
        {
            //  Calculate a distance between a point and a rectangle.
            //  The area around/in the rectangle is defined in terms of
            //  several regions:
            //
            //  O--x
            //  |
            //  y
            //
            //
            //        I   |    II    |  III
            //      ======+==========+======   --yMin
            //       VIII |  IX (in) |  IV
            //      ======+==========+======   --yMax
            //       VII  |    VI    |   V
            //
            //
            //  Note that the +y direction is down because of Unity's GUI coordinates.

            if (point.X < rect.Left)
            { // Region I, VIII, or VII
                if (point.Y < rect.Top)
                { // I
                    WpfPoint diff = point - new WpfPoint(rect.Left, rect.Top);
                    return Math.Sqrt((diff.X * diff.X) + (diff.Y * diff.Y));
                }
                else if (point.Y > rect.Bottom)
                { // VII
                    WpfPoint diff = point - new WpfPoint(rect.Left, rect.Bottom);
                    return Math.Sqrt((diff.X * diff.X) + (diff.Y * diff.Y));
                }
                else
                { // VIII
                    return rect.Left - point.X;
                }
            }
            else if (point.X > rect.Right)
            { // Region III, IV, or V
                if (point.Y < rect.Top)
                { // III
                    WpfPoint diff = point - new WpfPoint(rect.Right, rect.Top);
                    return Math.Sqrt((diff.X * diff.X) + (diff.Y * diff.Y));
                }
                else if (point.Y > rect.Bottom)
                { // V
                    WpfPoint diff = point - new WpfPoint(rect.Right, rect.Bottom);
                    return Math.Sqrt((diff.X * diff.X) + (diff.Y * diff.Y));
                }
                else
                { // IV
                    return point.X - rect.Right;
                }
            }
            else
            { // Region II, IX, or VI
                if (point.Y < rect.Top)
                { // II
                    return rect.Top - point.Y;
                }
                else if (point.Y > rect.Bottom)
                { // VI
                    return point.Y - rect.Bottom;
                }
                else
                { // IX
                    return 0d;
                }
            }
        }
        private double DistancePointToPoint(double x1, double y1, double x2, double y2)
        {
            return Math.Sqrt(Math.Pow((x2 - x1), 2) + Math.Pow((y2 - y1), 2));
        }

        private void PhotoExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            var cropped = CropBitmap();
            Close();

            var w = TemplatedWindow.CreateWindow("Edit Capture", new ImageEditorPage(cropped));
            var rectPos = TruncatedCroppingRect().ToWpfRect();
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
            UpdateCanvasMode(true);
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
            new VideoOverlayWindow(CroppingRectangle).Show();
            this.Close();
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

        private void RootGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                ShowTips = false;
                crosshair.CaptureMouse();
                draggingArea = true;
                draggingOrigin = ScreenTools.GetMousePosition();
            }
        }
        private void RootGrid_MouseMove(object sender, MouseEventArgs e)
        {
            var currentPoint = ScreenTools.GetMousePosition();
            var currentPointWpf = currentPoint.ToWpfPoint();

            if (ShowMagnifier)
            {
                var result = PositionWithinAScreen(pixelMagnifier, currentPointWpf, HorizontalAlignment.Right, VerticalAlignment.Bottom, 20);

                if (result.X > currentPointWpf.X && result.Y > currentPointWpf.Y) // point is to the bottom right
                    pixelMagnifier.IndicatorPosition = PixelMagnifier.ArrowIndicatorPosition.TopLeft;
                else if (result.X > currentPointWpf.X) // point is to the top right
                    pixelMagnifier.IndicatorPosition = PixelMagnifier.ArrowIndicatorPosition.BottomLeft;
                else if (result.Y > currentPointWpf.Y) // point is to the bottom left
                    pixelMagnifier.IndicatorPosition = PixelMagnifier.ArrowIndicatorPosition.TopRight;
                else // point is to the top left
                    pixelMagnifier.IndicatorPosition = PixelMagnifier.ArrowIndicatorPosition.BottomRight;

                pixelMagnifier.DrawMagnifier(currentPoint);
            }

            if (ShowTips)
            {
                var tipsOriginPoint = TipsPanel.TransformToAncestor(rootGrid).Transform(new Point(0, 0));
                var tipsPadding = pixelMagnifier.Width + 5;
                var tipsRect = new Rect(tipsOriginPoint.X - tipsPadding, tipsOriginPoint.Y - tipsPadding,
                    TipsPanel.Width + (tipsPadding * 2), TipsPanel.ActualHeight + (tipsPadding * 2));
                var distance = DistancePointToRectangle(currentPointWpf, tipsRect);
                TipsPanel.Opacity = Math.Max(Math.Min(distance / 100, 0.8), 0);
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

                UpdateCanvasSelection(window.ImageBoundsRect == ScreenRect.Empty
                    ? new ScreenRect(0, 0, 0, 0)
                    : window.ImageBoundsRect);

                if (App.Current.Settings.CaptureSettings.SelectedWindowPromotion == SelectedWindowForegroundPromotion.WhenHovered)
                    BringSelectedWindowToFront(window);
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
            crosshair.ReleaseMouseCapture();
            draggingArea = false;

            UpdateCanvasMode(false);

            var currentMouse = ScreenTools.GetMousePosition();

            // if the mouse hasn't moved far, let's treat it like a click event and find out what window they clicked on
            if (DistancePointToPoint(currentMouse.X, currentMouse.Y, draggingOrigin.X, draggingOrigin.Y) < 10)
            {
                var window = windowFinder.GetWindowThatContainsPoint(ScreenTools.GetMousePosition());

                if (window.ImageBoundsRect == ScreenRect.Empty)
                {
                    UpdateCanvasSelection(new ScreenRect(0, 0, 0, 0));
                }
                else
                {
                    UpdateCanvasSelection(window.ImageBoundsRect);
                    if (App.Current.Settings.CaptureSettings.SelectedWindowPromotion == SelectedWindowForegroundPromotion.WhenClicked ||
                        App.Current.Settings.CaptureSettings.SelectedWindowPromotion == SelectedWindowForegroundPromotion.WhenHovered)
                        BringSelectedWindowToFront(window);
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
                crosshair.Visibility = Visibility.Visible;
            }
            else
            {
                if (capturing == true)
                {
                    ManageCanvasMouseHandlers(false);
                    ManageSelectionResizeHandlers(true);
                }
                capturing = false;
                areaSizeIndicator.Visibility = Visibility.Hidden;
                pixelMagnifier.Visibility = Visibility.Hidden;
                crosshair.Visibility = Visibility.Collapsed;
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
            PopupWindowImage = null;
            PopupWindowRectangle = ScreenRect.Empty;
            CroppingRectangle = selection;
            UpdateCanvasPlacement();
        }
        private void BringSelectedWindowToFront(WindowFinder2.CachedWindow window)
        {
            if (window.IsPartiallyCovered)
            {
                var parent = windowFinder.GetTopLevel(window);
                if (parent.WindowBitmapWpf != null)
                {
                    PopupWindowImage = parent.WindowBitmapWpf;
                    PopupWindowRectangle = parent.WindowRect;
                }
            }
        }
    }
}
