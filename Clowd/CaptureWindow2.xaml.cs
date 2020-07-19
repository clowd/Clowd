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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Clowd.Controls;
using Clowd.Utilities;
using ScreenVersusWpf;

namespace Clowd
{
    /// <summary>
    /// Interaction logic for CaptureWindow2.xaml
    /// </summary>
    public partial class CaptureWindow2 : Window
    {
        public IntPtr Handle { get; private set; }

        Color _highlightColor = Colors.Red;

        ScreenPoint? _dragBegin = null;
        ScreenRect? _selection = null;
        bool _selecting = true;
        Brush _overlayBrush = new SolidColorBrush(Color.FromArgb(0x77, 0, 0, 0));

        WindowFinder2 _windowFinder;
        BitmapSource _image;
        BitmapSource _imageGray;

        ScreenRect? _boundsPromotedWindow;
        BitmapSource _imagePromotedWindow;

        VisualCollection _visuals;
        DrawingVisual _backgroundImage;
        DrawingVisual _crosshair;
        DrawingVisual _foregroundImage;

        public CaptureWindow2()
        {
            InitializeComponent();
            this.SourceInitialized += CaptureWindow2_SourceInitialized;
            this.Loaded += CaptureWindow2_Loaded;
            _visuals = new VisualCollection(this);

            _backgroundImage = new DrawingVisual();
            _visuals.Add(_backgroundImage);

            _foregroundImage = new DrawingVisual();
            _visuals.Add(_foregroundImage);

            _crosshair = new DrawingVisual();
            _visuals.Add(_crosshair);

            var bo = new Border();
            bo.Width = 100;
            bo.Height = 200;
            bo.BorderThickness = new Thickness(2);
            bo.BorderBrush = Brushes.Red;

            _visuals.Add(bo);
        }

        private static CaptureWindow2 _readyWindow;
        public static void ShowNewCapture()
        {
            if (_readyWindow == null)
            {
                _readyWindow = new CaptureWindow2();
                _readyWindow.Show();
            }

            _readyWindow.ShowCapture();

            _readyWindow.Closed += (s, e) =>
            {
                _readyWindow = new CaptureWindow2();
                _readyWindow.Show();
            };
        }


        private void CaptureWindow2_SourceInitialized(object sender, EventArgs e)
        {
            this.Handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            HwndSource source = HwndSource.FromHwnd(this.Handle);
            source.AddHook(new HwndSourceHook(WndProc));
        }

        private void CaptureWindow2_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateSelfPosition();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)Interop.WindowMessage.WM_DISPLAYCHANGE)
                UpdateSelfPosition();
            return IntPtr.Zero;
        }

        private void UpdateSelfPosition()
        {
            var primary = ScreenTools.Screens.First().Bounds;
            var virt = ScreenTools.VirtualScreen.Bounds;
            // WPF makes some fairly inconvenient DPI conversions to Left and Top which have also changed between NET 4.5 and 4.8; just use WinAPI instead of de-converting them
            Interop.USER32.SetWindowPos(this.Handle, 0, -primary.Left, -primary.Top, virt.Width, virt.Height, Interop.SWP.SHOWWINDOW);
            Interop.USER32.SetForegroundWindow(this.Handle);
        }

        private void ShowCapture()
        {
            _windowFinder = WindowFinder2.NewCapture();
            using (var source = ScreenUtil.Capture(captureCursor: App.Current.Settings.CaptureSettings.ScreenshotWithCursor))
            {
                _image = source.ToBitmapSource();
                _imageGray = new FormatConvertedBitmap(_image, PixelFormats.Gray8, BitmapPalettes.Gray256, 1);
            }
            //this.Topmost = true;

            this.MouseMove += CaptureWindow2_MouseMove;
            this.MouseDown += CaptureWindow2_MouseDown;
            this.MouseUp += CaptureWindow2_MouseUp;

            var currentPoint = ScreenTools.GetMousePosition();

            if (App.Current.Settings.CaptureSettings.DetectWindows)
            {
                var window = _windowFinder?.GetWindowThatContainsPoint(currentPoint);
                _selection = window.ImageBoundsRect;
            }

            DrawBackgroundImage();
            DrawForegroundImage();
            DrawCrosshair();
        }

        private void CaptureWindow2_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_selecting)
            {
                this.CaptureMouse();
                _imagePromotedWindow = null;
                _boundsPromotedWindow = null;
                _dragBegin = ScreenTools.GetMousePosition();
                _selection = null;
            }
        }

        private void CaptureWindow2_MouseMove(object sender, MouseEventArgs e)
        {
            var currentPoint = ScreenTools.GetMousePosition();
            var currentPointWpf = ScreenTools.GetMousePosition().ToWpfPoint();
            if (_selecting)
            {
                if (_dragBegin != null)
                {
                    var draggingOrigin = _dragBegin.Value;
                    var rect = new ScreenRect();
                    rect.Left = Math.Min(draggingOrigin.X, currentPoint.X);
                    rect.Top = Math.Min(draggingOrigin.Y, currentPoint.Y);
                    rect.Width = Math.Abs(draggingOrigin.X - currentPoint.X) + 1;
                    rect.Height = Math.Abs(draggingOrigin.Y - currentPoint.Y) + 1;
                    _selection = rect;
                }
                else if (App.Current.Settings.CaptureSettings.DetectWindows)
                {
                    var window = _windowFinder?.GetWindowThatContainsPoint(currentPoint);
                    _selection = window.ImageBoundsRect;
                }
                else
                {
                    _selection = null;
                }

                DrawForegroundImage();
                DrawCrosshair();
            }
            else
            {
                if (_selection.Value.Contains(currentPoint))
                    Cursor = Cursors.SizeAll;
                else
                    Cursor = Cursors.Arrow;
            }
        }

        private void CaptureWindow2_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_selecting)
            {
                this.ReleaseMouseCapture();

                if (!_dragBegin.HasValue)
                    return; // huh??

                var draggingOrigin = _dragBegin.Value;
                var currentMouse = ScreenTools.GetMousePosition();

                // if the mouse hasn't moved far, let's treat it like a click event and find out what window they clicked on
                if (DistancePointToPoint(currentMouse.X, currentMouse.Y, draggingOrigin.X, draggingOrigin.Y) < 10)
                {
                    var window = _windowFinder.GetWindowThatContainsPoint(currentMouse);

                    // show debug info if control key is being held while clicking
                    if (Keyboard.IsKeyDown(Key.LeftCtrl))
                        window.ShowDebug();

                    if (window.ImageBoundsRect == ScreenRect.Empty)
                    {
                        _selection = null;
                        _dragBegin = null;
                        return;
                    }

                    // bring bitmap of window to front if we can
                    if (_windowFinder.BitmapsReady && window.IsPartiallyCovered)
                    {
                        var parent = _windowFinder.GetTopLevelWindow(window);
                        if (parent.WindowBitmapWpf != null)
                        {
                            _imagePromotedWindow = parent.WindowBitmapWpf;
                            _boundsPromotedWindow = parent.WindowRect;
                        }
                    }

                    _selection = window.ImageBoundsRect;
                }

                _selecting = false;

                DrawForegroundImage();
                DrawCrosshair();
            }
        }

        protected override Visual GetVisualChild(int index) => _visuals[index];

        protected override int VisualChildrenCount => _visuals.Count;

        private void DrawForegroundImage()
        {
            var windowBounds = new WpfRect(0, 0, this.ActualWidth, this.ActualHeight);
            using (var context = _foregroundImage.RenderOpen())
            {
                if (_selection != null)
                {
                    var clip = _selection.Value.ToWpfRect();
                    var clipRect = new Rect(clip.Left, clip.Top, clip.Width, clip.Height);
                    context.PushClip(new RectangleGeometry(clipRect));

                    if (_imagePromotedWindow != null && _boundsPromotedWindow != null)
                    {
                        var proBounds = _boundsPromotedWindow.Value.ToWpfRect();
                        context.DrawImage(_imagePromotedWindow, new Rect(proBounds.Left, proBounds.Top, proBounds.Width, proBounds.Height));
                    }
                    else
                    {
                        context.DrawImage(_image, windowBounds);
                    }
                    context.Pop();
                    context.DrawRectangle(null, new Pen(Brushes.Red, 2), clipRect);
                }
            }
        }

        private void DrawBackgroundImage()
        {
            var windowBounds = new WpfRect(0, 0, this.ActualWidth, this.ActualHeight);
            using (var context = _backgroundImage.RenderOpen())
            {
                context.DrawImage(_imageGray, windowBounds);
                context.DrawRectangle(_overlayBrush, null, windowBounds);
            }
        }

        private void DrawCrosshair()
        {
            if (!_selecting)
            {
                using (var context = _crosshair.RenderOpen())
                {
                    return;
                }
            }

            var currentPoint = ScreenTools.GetMousePosition();
            var cursor = currentPoint.ToWpfPoint();

            var bounds = new WpfRect(0, 0, this.ActualWidth, this.ActualHeight);
            Color AccentColor = _highlightColor;
            double DashLength = 8;

            const double crossRadius = 120;
            const double handleRadius = 2;

            var x = Math.Min(cursor.X, bounds.Right);
            var y = Math.Min(cursor.Y, bounds.Bottom);

            var whitePen = new Pen(new SolidColorBrush(Color.FromArgb(127, 255, 255, 255)), 1);
            whitePen.DashStyle = new DashStyle(new double[] { DashLength, DashLength }, 0);

            var blackPen = new Pen(new SolidColorBrush(Color.FromArgb(127, 0, 0, 0)), 1);
            blackPen.DashStyle = new DashStyle(new double[] { DashLength, DashLength }, DashLength);

            var accentBrush = new SolidColorBrush(Color.FromArgb(255, AccentColor.R, AccentColor.G, AccentColor.B));
            var accentPen = new Pen(accentBrush, 1);

            using (var context = _crosshair.RenderOpen())
            {
                // draw soft crosshair size of bounds
                context.DrawLine(blackPen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
                context.DrawLine(whitePen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
                context.DrawLine(blackPen, new Point(bounds.Left, y), new Point(bounds.Right, y));
                context.DrawLine(whitePen, new Point(bounds.Left, y), new Point(bounds.Right, y));

                // draw accent crosshair size of (crossRadius*2)
                context.DrawLine(accentPen, new Point(x, y - crossRadius), new Point(x, y + crossRadius));
                context.DrawLine(accentPen, new Point(x - crossRadius, y), new Point(x + crossRadius, y));

                // draw crosshair handles 
                var horSize = new Size(crossRadius / 2, handleRadius * 2);
                var vertSize = new Size(handleRadius * 2, crossRadius / 2);
                context.DrawRectangle(accentBrush, null, new Rect(new Point(x - handleRadius, y - crossRadius), vertSize));
                context.DrawRectangle(accentBrush, null, new Rect(new Point(x - handleRadius, y + (crossRadius / 2)), vertSize));
                context.DrawRectangle(accentBrush, null, new Rect(new Point(x - crossRadius, y - handleRadius), horSize));
                context.DrawRectangle(accentBrush, null, new Rect(new Point(x + (crossRadius / 2), y - handleRadius), horSize));
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

        private void CloseExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();
        }
    }
}
