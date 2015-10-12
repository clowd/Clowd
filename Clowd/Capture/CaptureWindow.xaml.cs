using Clowd.Controls;
using Clowd.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Clowd.Capture
{
    [PropertyChanged.ImplementPropertyChanged]
    public partial class CaptureWindow : Window
    {
        //Disclaimer, I started writing this using MVVM and then ditched that idea, so code is kind of inconsistant.

        public BitmapSource ScreenImage { get; private set; }
        public Rect CroppingRectangle { get; private set; } = new Rect(0, 0, 0, 0);
        public Cursor CanvasCursor { get; private set; } = Cursors.Cross;
        public bool ShowTips { get; private set; } = true;
        public bool ShowMagnifier { get; private set; } = true;
        public double PixelSizeX { get { return DpiScale.DownScaleX(1); } }
        public double PixelSizeY { get { return DpiScale.DownScaleY(1); } }
        public Thickness TopRightThickness { get { return new Thickness(PixelSizeX, 0, 0, PixelSizeY); } }
        public Thickness BottomLeftThickness { get { return new Thickness(0, PixelSizeY, PixelSizeX, 0); } }
        public Thickness NormalThickness { get { return new Thickness(PixelSizeX, PixelSizeY, PixelSizeX, PixelSizeY); } }
        public IntPtr Handle { get; private set; }

        private bool draggingArea = false;
        private Point draggingOrigin = default(Point);
        private WindowFinder2 windowFinder = new WindowFinder2();
        private bool? capturing = null;
        public CaptureWindow()
        {
            InitializeComponent();
            this.SourceInitialized += CaptureWindow_SourceInitialized;
            this.Loaded += CaptureWindow_Loaded;
        }

        private void CaptureWindow_SourceInitialized(object sender, EventArgs e)
        {
            this.Handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            CaptureBitmap();
            windowFinder.Capture();
        }

        private void CaptureWindow_Loaded(object sender, RoutedEventArgs e)
        {
            this.Left = 0;
            this.Top = 0;
            this.Width = SystemParameters.VirtualScreenWidth;
            this.Height = SystemParameters.VirtualScreenHeight;
            Interop.USER32.SetForegroundWindow(this.Handle);
            UpdateCanvasMode(true);
        }

        private void CaptureBitmap()
        {
            var source = ScreenUtil.Capture(new System.Drawing.Rectangle(0, 0, System.Windows.Forms.SystemInformation.VirtualScreen.Width, System.Windows.Forms.SystemInformation.VirtualScreen.Height));
            IntPtr ip = source.GetHbitmap();
            BitmapSource bs = null;
            try
            {
                bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(ip,
                   IntPtr.Zero, Int32Rect.Empty,
                   System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                Interop.Gdi32.GDI32.DeleteObject(ip);
            }

            ScreenImage = bs;
        }

        private void UpdateCanvasSelection(Rect selection)
        {
            CroppingRectangle = selection;
            UpdateCanvasPlacement();
        }
        private void UpdateCanvasPlacement()
        {
            var selection = CroppingRectangle;
            var primaryScreen = DpiScale.TranslateDownScaleRect(windowFinder.GetBoundsOfScreenContainingRect(selection, false));
            var bottomSpace = Math.Max(primaryScreen.Bottom - selection.Bottom, 0);
            if (capturing == true)
            {
                areaSizeIndicatorWidth.Text = Math.Round(DpiScale.UpScaleX(selection.Width)).ToString();
                areaSizeIndicatorHeight.Text = Math.Round(DpiScale.UpScaleY(selection.Height)).ToString();
                double indLeft, indTop;
                indLeft = selection.Left + (selection.Width / 2) - areaSizeIndicator.ActualWidth / 2;
                if (bottomSpace >= 30)
                {
                    if (bottomSpace >= 40)
                        indTop = selection.Bottom + 5;
                    else
                        indTop = selection.Bottom + 1;
                }
                else
                    indTop = selection.Bottom - 35;
                Canvas.SetLeft(areaSizeIndicator, indLeft);
                Canvas.SetTop(areaSizeIndicator, indTop);
            }
            else if (capturing == false)
            {
                var rightSpace = Math.Max(primaryScreen.Right - selection.Right, 0);
                var leftSpace = Math.Max(selection.Left - primaryScreen.Left, 0);
                double indLeft = 0, indTop = 0;
                //we want to display (and clip) the controls on/to the primary screen - 
                //where the primary screen is the screen that contains the center of the cropping rectangle
                var intersecting = primaryScreen;
                intersecting.Intersect(CroppingRectangle);
                if (bottomSpace >= 50)
                {
                    toolActionBarStackPanel.Orientation = Orientation.Horizontal;
                    indLeft = intersecting.Left + intersecting.Width / 2 - toolActionBar.ActualWidth / 2;
                    indTop = bottomSpace >= 60 ? intersecting.Bottom + 5 : intersecting.Bottom;
                }
                else if (rightSpace >= 50)
                {
                    toolActionBarStackPanel.Orientation = Orientation.Vertical;
                    indLeft = rightSpace >= 60 ? intersecting.Right + 5 : intersecting.Right;
                    indTop = intersecting.Bottom - toolActionBar.ActualHeight;
                }
                else if (leftSpace >= 50)
                {
                    toolActionBarStackPanel.Orientation = Orientation.Vertical;
                    indLeft = leftSpace >= 60 ? intersecting.Left - 55 : intersecting.Left - 50;
                    indTop = intersecting.Bottom - toolActionBar.ActualHeight;
                }
                else
                {
                    toolActionBarStackPanel.Orientation = Orientation.Horizontal;
                    indLeft = intersecting.Left + intersecting.Width / 2 - toolActionBar.ActualWidth / 2;
                    indTop = intersecting.Bottom - 70;
                }
                if (indLeft < primaryScreen.Left)
                    indLeft = primaryScreen.Left;
                else if (indLeft + toolActionBar.ActualWidth > primaryScreen.Right)
                    indLeft = primaryScreen.Right - toolActionBar.ActualWidth;
                Canvas.SetLeft(toolActionBar, indLeft);
                Canvas.SetTop(toolActionBar, indTop);
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
                CanvasCursor = Cursors.None;
                _magnifier.Visibility = ShowMagnifier ? Visibility.Visible : Visibility.Hidden;
                toolActionBar.Visibility = Visibility.Hidden;
                crosshairBottomLeft.Width = rootGrid.ActualWidth;
                crosshairBottomLeft.Height = rootGrid.ActualHeight;
                crosshairTopRight.Width = rootGrid.ActualWidth;
                crosshairTopRight.Height = rootGrid.ActualHeight;
                //Canvas.SetLeft(crosshairBottomLeft, -10);
                //Canvas.SetBottom(crosshairBottomLeft, rootGrid.Height + 10);
                //Canvas.SetTop(crosshairTopRight, -10);
                //Canvas.SetRight(crosshairTopRight, rootGrid.Width + 10);
                crosshairBottomLeft.Visibility = Visibility.Visible;
                crosshairTopRight.Visibility = Visibility.Visible;
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
                _magnifier.Visibility = Visibility.Hidden;
                crosshairBottomLeft.Visibility = Visibility.Hidden;
                crosshairTopRight.Visibility = Visibility.Hidden;
                ShowTips = false;
                CanvasCursor = Cursors.Arrow;
                toolActionBar.Visibility = Visibility.Visible;

            }
            UpdateCanvasPlacement();
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
                style.Setters.Add(new Setter(Thumb.BackgroundProperty, App.Singleton.Resources["HighlightBrush"]));
                style.Setters.Add(new Setter(Thumb.TemplateProperty, (ControlTemplate)System.Windows.Markup.XamlReader.Parse(template)));

                AdornerLayer adornerLayer = AdornerLayer.GetAdornerLayer(rootGrid);
                ResizingAdorner myAdorner = new ResizingAdorner(selectionBorder, style);
                myAdorner.SetupCustomResizeHandling(UpdateCanvasSelection);
                adornerLayer.Add(myAdorner);

                Point mouseDownPos = default(Point);
                Rect originRect = default(Rect);
                bool mouseDown = false;
                MouseButtonEventHandler mouseDownHandler = (sender, e) =>
                {
                    selectionBorder.CaptureMouse();
                    originRect = CroppingRectangle;
                    mouseDownPos = e.GetPosition(rootGrid);
                    mouseDown = true;
                };
                MouseEventHandler mouseMoveHandler = (sender, e) =>
                {
                    if (!mouseDown)
                        return;
                    var cur = e.GetPosition(rootGrid);
                    var x = mouseDownPos.X - cur.X;
                    var y = mouseDownPos.Y - cur.Y;
                    var result = new Rect(originRect.X - x, originRect.Y - y, originRect.Width, originRect.Height);
                    result.Intersect(new Rect(0, 0, rootGrid.ActualWidth, rootGrid.ActualHeight));
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
                selectionBorder.Cursor = Cursors.None;
            }
        }

        private void RootGrid_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!draggingArea)
                return;
            ((UIElement)sender).ReleaseMouseCapture();
            draggingArea = false;

            UpdateCanvasMode(false);

            if (CroppingRectangle.Width < 20 || CroppingRectangle.Height < 20)
            {
                var wfMouse = new Point(System.Windows.Forms.Cursor.Position.X, System.Windows.Forms.Cursor.Position.Y);
                var window = windowFinder.GetWindowThatContainsPoint(wfMouse);
                UpdateCanvasSelection(DpiScale.TranslateDownScaleRect(window.WindowRect));
            }
        }

        private void RootGrid_MouseMove(object sender, MouseEventArgs e)
        {
            var currentPoint = e.GetPosition(rootGrid);

            Canvas.SetLeft(crosshairTopRight, currentPoint.X - 1);
            Canvas.SetBottom(crosshairTopRight, (rootGrid.ActualHeight - currentPoint.Y));
            Canvas.SetTop(crosshairBottomLeft, currentPoint.Y - 1);
            Canvas.SetRight(crosshairBottomLeft, (rootGrid.ActualWidth - currentPoint.X));

            if (draggingArea)
            {
                double x, y, width, height;
                if (currentPoint.X > draggingOrigin.X)
                {

                    x = draggingOrigin.X;
                    width = currentPoint.X - draggingOrigin.X;
                }
                else
                {
                    x = currentPoint.X;
                    width = draggingOrigin.X - currentPoint.X;
                }
                if (currentPoint.Y > draggingOrigin.Y)
                {
                    y = draggingOrigin.Y;
                    height = currentPoint.Y - draggingOrigin.Y;
                }
                else
                {
                    y = currentPoint.Y;
                    height = draggingOrigin.Y - currentPoint.Y;
                }
                UpdateCanvasSelection(new Rect(x, y, width, height));
            }
            else
            {
                var wfMouse = new Point(System.Windows.Forms.Cursor.Position.X, System.Windows.Forms.Cursor.Position.Y);
                var window = windowFinder.GetWindowThatContainsPoint(wfMouse);
                UpdateCanvasSelection(DpiScale.TranslateDownScaleRect(window.WindowRect));
            }
        }

        private void RootGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var wfMouse = System.Windows.Forms.Cursor.Position;
                ShowTips = false;
                ((UIElement)sender).CaptureMouse();
                draggingArea = true;
                draggingOrigin = e.GetPosition(rootGrid);
            }
        }

        private void Photo_Clicked(object sender, RoutedEventArgs e)
        {

        }

        private void Video_Clicked(object sender, RoutedEventArgs e)
        {

        }

        private void Reset_Clicked(object sender, RoutedEventArgs e)
        {
            UpdateCanvasSelection(new Rect(0, 0, 0, 0));
            UpdateCanvasMode(true);
        }

        private void Close_Clicked(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
