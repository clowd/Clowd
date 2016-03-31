using Clowd.Controls;
using Clowd.Utilities;
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
using CS.Wpf;
using ScreenVersusWpf;

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
        public bool ShowMagnifier { get; private set; } = true;
        public bool ShowTips { get; private set; } = true;

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
            using (var source = ScreenUtil.Capture(System.Windows.Forms.SystemInformation.VirtualScreen,
                App.Current.Settings.CaptureSettings.ScreenshotWithCursor))
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
                    result = result.Intersect(ScreenTools.GetVirtualScreen());
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

        private WpfSize CalculateOptimalOffset(WpfSize objectSize, WpfPoint targetPoint, WpfSize desiredOffsetFromCenter)
        {
            var screenBounds = ScreenTools.GetBoundsOfScreenContaining(targetPoint.ToScreenPoint()).ToWpfRect();

            bool fitsX = screenBounds.Left + screenBounds.Width >= targetPoint.X + (objectSize.Width / 2) + desiredOffsetFromCenter.Width;
            bool fitsY = screenBounds.Top + screenBounds.Height >= targetPoint.Y + (objectSize.Height / 2) + desiredOffsetFromCenter.Height;

            return new WpfSize(
                fitsX ? desiredOffsetFromCenter.Width : -desiredOffsetFromCenter.Width,
                fitsY ? desiredOffsetFromCenter.Height : -desiredOffsetFromCenter.Height);
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
            var screenContainingMouse = ScreenTools.GetBoundsOfScreenContaining(ScreenTools.GetMousePosition());
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
                var offset = CalculateOptimalOffset(
                    pixelMagnifier.FinderSize,
                    currentPoint.ToWpfPoint(),
                    pixelMagnifier.FinderSize / 2 + new WpfSize(20, 20));
                var pos = currentPoint.ToWpfPoint() - pixelMagnifier.FinderSize / 2 + offset;
                Canvas.SetLeft(pixelMagnifier, pos.X);
                Canvas.SetTop(pixelMagnifier, pos.Y);
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
            else
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
#warning TODO: some of these checks are in screen pixels
            var selection = CroppingRectangle;
            var selectionScreen = ScreenTools.GetBoundsOfScreenContaining(selection);
            var bottomSpace = ScreenTools.ScreenToWpf(Math.Max(selectionScreen.Bottom - selection.Bottom, 0));
            if (capturing == true)
            {
                areaSizeIndicatorWidth.Text = selection.Width.ToString();
                areaSizeIndicatorHeight.Text = selection.Height.ToString();
                var indicatorPos = selection.ToWpfRect();
                indicatorPos.Left += (indicatorPos.Width / 2) - areaSizeIndicator.ActualWidth / 2;
                if (bottomSpace < 30)
                    indicatorPos.Top = indicatorPos.Bottom - 35;
                else if (bottomSpace >= 40)
                    indicatorPos.Top = indicatorPos.Bottom + 5;
                else
                    indicatorPos.Top = indicatorPos.Bottom + 1;
                Canvas.SetLeft(areaSizeIndicator, indicatorPos.Left);
                Canvas.SetTop(areaSizeIndicator, indicatorPos.Top);
            }
            else if (capturing == false)
            {
                var rightSpace = ScreenTools.ScreenToWpf(Math.Max(selectionScreen.Right - selection.Right, 0));
                var leftSpace = ScreenTools.ScreenToWpf(Math.Max(selection.Left - selectionScreen.Left, 0));
                double indLeft = 0, indTop = 0;
                //we want to display (and clip) the controls on/to the primary screen -
                //where the primary screen is the screen that contains the center of the cropping rectangle
                var intersecting = selectionScreen.Intersect(CroppingRectangle);
                if (intersecting == ScreenRect.Empty)
                    return;
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