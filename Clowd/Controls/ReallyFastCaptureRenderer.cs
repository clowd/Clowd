using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
using System.Windows.Threading;
using Clowd.Controls;
using Clowd.Utilities;
using ScreenVersusWpf;

namespace Clowd
{
    [System.ComponentModel.DesignTimeVisible(false)]
    public class ReallyFastCaptureRenderer : FrameworkElement
    {
        public WpfRect SelectionRectangle
        {
            get { return (WpfRect)GetValue(SelectionRectangleProperty); }
            set { SetValue(SelectionRectangleProperty, value); }
        }
        public static readonly DependencyProperty SelectionRectangleProperty =
            DependencyProperty.Register(nameof(SelectionRectangle), typeof(WpfRect), typeof(ReallyFastCaptureRenderer), new PropertyMetadata(new WpfRect(), SelectionRectangleChanged));

        public bool IsCapturing
        {
            get { return (bool)GetValue(IsCapturingProperty); }
            set { SetValue(IsCapturingProperty, value); }
        }
        public static readonly DependencyProperty IsCapturingProperty =
            DependencyProperty.Register(nameof(IsCapturing), typeof(bool), typeof(ReallyFastCaptureRenderer), new PropertyMetadata(true));

        public Color AccentColor
        {
            get { return (Color)GetValue(AccentColorProperty); }
            set { SetValue(AccentColorProperty, value); }
        }
        public static readonly DependencyProperty AccentColorProperty =
            DependencyProperty.Register(nameof(AccentColor), typeof(Color), typeof(ReallyFastCaptureRenderer), new PropertyMetadata(default(Color)));

        public bool ShowMagnifier
        {
            get { return (bool)GetValue(ShowMagnifierProperty); }
            set { SetValue(ShowMagnifierProperty, value); }
        }
        public static readonly DependencyProperty ShowMagnifierProperty =
            DependencyProperty.Register(nameof(ShowMagnifier), typeof(bool), typeof(ReallyFastCaptureRenderer), new PropertyMetadata(true, ShowMagnifierChanged));

        private static void ShowMagnifierChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ths = (ReallyFastCaptureRenderer)d;
            ths.Draw();
        }

        private static void SelectionRectangleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ths = (ReallyFastCaptureRenderer)d;
            ths._selectedWindow = null;
            ths.Draw();
        }

        WpfPoint? _virtualPoint = null;
        ScreenPoint? _lastPoint = null;
        ScreenPoint? _dragBegin = null;
        const int _clickDistance = 2;

        Brush _overlayBrush = new SolidColorBrush(Color.FromArgb(127, 0, 0, 0));

        WindowFinder2.CachedWindow _selectedWindow;
        WindowFinder2 _windowFinder;
        BitmapSource _image;
        BitmapSource _imageGray;

        VisualCollection _visuals;
        DrawingVisual _backgroundImage;
        DrawingVisual _crosshair;
        DrawingVisual _foregroundImage;
        DrawingVisual _magnifier;
        DrawingVisual _sizeIndicator;

        Pen _sharpBlackLineDashed;
        Pen _sharpWhiteLineDashed;
        Pen _sharpAccentLine;
        Pen _sharpAccentLineWide;

        Color _accentColor;
        Brush _accentBrush;
        double _sharpLineWidth;

        double _globalZoom = 1;

        public ReallyFastCaptureRenderer()
        {
            this.Cursor = Cursors.None;

            _backgroundImage = new MyDrawingVisual();
            _foregroundImage = new MyDrawingVisual();
            _sizeIndicator = new DrawingVisual();
            _crosshair = new DrawingVisual();
            _magnifier = new MyDrawingVisual();

            _visuals = new VisualCollection(this);
            _visuals.Add(_backgroundImage);
            _visuals.Add(_foregroundImage);
            _visuals.Add(_sizeIndicator);
            _visuals.Add(_crosshair);
            _visuals.Add(_magnifier);

            // here to apease the WPF designer
            if (App.IsDesignMode)
                return;

            _accentColor = App.Current.AccentColor;
            _accentBrush = new SolidColorBrush(_accentColor);

            // crosshair constants
            var dashLength = ScreenTools.WpfSnapToPixelsFloor(8);
            _sharpLineWidth = ScreenTools.WpfSnapToPixelsFloor(1);
            _sharpBlackLineDashed = new Pen(Brushes.Black, _sharpLineWidth);
            _sharpBlackLineDashed.DashStyle = new DashStyle(new double[] { dashLength, dashLength }, 0);
            _sharpWhiteLineDashed = new Pen(Brushes.White, _sharpLineWidth);
            _sharpWhiteLineDashed.DashStyle = new DashStyle(new double[] { dashLength, dashLength }, dashLength);
            _sharpAccentLine = new Pen(_accentBrush, _sharpLineWidth);
            _sharpAccentLineWide = new Pen(_accentBrush, _sharpLineWidth * 5);
        }

        public async Task StartFastCapture(Stopwatch sw)
        {
            var tsk1 = Task.Run(() =>
            {
                Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - (#1) Wnd Enum Start");
                _windowFinder = WindowFinder2.NewCapture();
                _windowFinder.PropertyChanged += (s, e) =>
                {
                    this.Dispatcher.Invoke(Draw, System.Windows.Threading.DispatcherPriority.Render);
                };
                Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - (#1) Wnd Enum End");
            });

            var tsk2 = Task.Run(() =>
            {
                Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - (#2) GDI Capture Start");
                using (var source = ScreenUtil.Capture(captureCursor: App.Current.Settings.CaptureSettings.ScreenshotWithCursor))
                {
                    Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - (#2) GDI Image Captured");
                    _image = source.ConvertToBitmapSourceFast();
                }
                _image.Freeze();
                Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - (#2) GDI Image Converted");
            });

            await Task.WhenAll(tsk1, tsk2);

            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - (#3) Render Start");
            _imageGray = new FormatConvertedBitmap(_image, PixelFormats.Gray8, BitmapPalettes.Gray256, 1);
            Reset();
            Console.WriteLine($"+{sw.ElapsedMilliseconds}ms - (#3) Render End");
        }

        public async void FinishFastCapture(DateTime? animationStart = null)
        {
            // http://gizma.com/easing/#cub3
            //double cubicEaseInOut(double t, double b, double c, double d)
            //{
            //    t /= d / 2;
            //    if (t < 1) return c / 2 * t * t * t + b;
            //    t -= 2;
            //    return c / 2 * (t * t * t + 2) + b;
            //};

            //_onTimer.Interval = TimeSpan.FromMilliseconds(10); // 60 fps
            //_onTimer.Tick += (s, e) =>
            //{
            //    if (!animationStart.HasValue)
            //        animationStart = DateTime.Now;

            //    var curTime = DateTime.Now - animationStart.Value;
            //    _onFade = cubicEaseInOut(curTime.TotalMilliseconds, 0, 1, 300);
            //    Console.WriteLine($"time {curTime.TotalMilliseconds}, opacity {_onFade}");
            //    if (_onFade >= 1)
            //    {
            //        _onTimer.Stop();
            //        _onFade = 1;
            //    }
            //    DrawBackgroundImage();
            //};
            //_onTimer.Start();

            await Task.Delay(100);
            await _windowFinder.PopulateWindowBitmapsAsync();
        }

        public void Reset()
        {
            if (IsCapturing)
                return;

            _selectedWindow = null;
            _dragBegin = null;
            IsCapturing = true;
            this.Cursor = Cursors.None;

            this.MouseMove += CaptureWindow2_MouseMove;
            this.MouseDown += CaptureWindow2_MouseDown;
            this.MouseUp += CaptureWindow2_MouseUp;
            this.MouseWheel += CaptureWindow2_MouseWheel;

            var currentPoint = ScreenTools.GetMousePosition();
            if (App.Current.Settings.CaptureSettings.DetectWindows)
            {
                var window = _windowFinder?.GetWindowThatContainsPoint(currentPoint);
                if (window != null)
                {
                    SelectionRectangle = window.ImageBoundsRect.ToWpfRect();
                }
                else
                {
                    SelectionRectangle = default(WpfRect);
                }
            }
            else
            {
                SelectionRectangle = default(WpfRect);
            }

            Draw();
        }

        public void SelectScreen()
        {
            if (_dragBegin.HasValue || !IsCapturing)
                return;

            var screenContainingMouse = ScreenTools.GetScreenContaining(ScreenTools.GetMousePosition()).Bounds;
            SelectionRectangle = screenContainingMouse.ToWpfRect();
            StopCapture();
        }

        public void StopCapture()
        {
            this.ReleaseMouseCapture();

            IsCapturing = false;
            _globalZoom = 1;
            this.Cursor = Cursors.Arrow;

            Draw();

            this.MouseMove -= CaptureWindow2_MouseMove;
            this.MouseDown -= CaptureWindow2_MouseDown;
            this.MouseUp -= CaptureWindow2_MouseUp;
            this.MouseWheel -= CaptureWindow2_MouseWheel;
        }

        public BitmapSource GetSelectedBitmap()
        {
            var rect = SelectionRectangle.ToScreenRect();

            if (_selectedWindow != null && _selectedWindow.WindowBitmapWpf != null)
            {
                var pwinrect = _selectedWindow.WindowRect;
                rect = new ScreenRect(rect.Left - pwinrect.Left, rect.Top - pwinrect.Top, rect.Width, rect.Height);
                return new CroppedBitmap(_selectedWindow.WindowBitmapWpf, rect);
            }
            else
            {
                return new CroppedBitmap(_image, rect);
            }
        }

        public Color GetHoveredColor()
        {
            var location = ScreenTools.GetMousePosition();
            var zoomedColor = GetPixelColor(_image, location.X, location.Y);
            return zoomedColor;
        }

        public void SetSelectedWindowForeground()
        {
            if (_selectedWindow != null && _selectedWindow.WindowBitmapWpf != null)
                Interop.USER32.SetForegroundWindow(_selectedWindow.Handle);
        }

        private void CaptureWindow2_MouseDown(object sender, MouseButtonEventArgs e)
        {
            this.CaptureMouse();
            _dragBegin = ScreenTools.GetMousePosition();
        }

        private void CaptureWindow2_MouseMove(object sender, MouseEventArgs e)
        {
            var currentPoint = ScreenTools.GetMousePosition();

            if (_globalZoom > 1)
            {
                if (_virtualPoint.HasValue && _lastPoint.HasValue)
                {
                    var xDelta = (currentPoint.X - _lastPoint.Value.X) / _globalZoom * ScreenTools.DpiZoom;
                    var yDelta = (currentPoint.Y - _lastPoint.Value.Y) / _globalZoom * ScreenTools.DpiZoom;

                    _virtualPoint = new WpfPoint(_virtualPoint.Value.X + xDelta, _virtualPoint.Value.Y + yDelta);
                    currentPoint = _virtualPoint.Value.ToScreenPoint();
                    _lastPoint = currentPoint;
                    System.Windows.Forms.Cursor.Position = currentPoint.ToSystem();
                }
                else
                {
                    _lastPoint = currentPoint;
                    _virtualPoint = currentPoint.ToWpfPoint();
                }
            }
            else
            {
                _lastPoint = null;
                _virtualPoint = null;
            }

            var newSelectionWindow = SelectionRectangle;

            if (_dragBegin.HasValue && DistancePointToPoint(currentPoint.X, currentPoint.Y, _dragBegin.Value.X, _dragBegin.Value.Y) > _clickDistance)
            {
                var draggingOrigin = _dragBegin.Value;
                var rect = new ScreenRect();
                rect.Left = Math.Min(draggingOrigin.X, currentPoint.X);
                rect.Top = Math.Min(draggingOrigin.Y, currentPoint.Y);
                rect.Width = Math.Abs(draggingOrigin.X - currentPoint.X);
                rect.Height = Math.Abs(draggingOrigin.Y - currentPoint.Y);
                newSelectionWindow = rect.ToWpfRect();
            }
            else if (App.Current.Settings.CaptureSettings.DetectWindows)
            {
                var window = _windowFinder?.GetWindowThatContainsPoint(currentPoint);
                if (window != null)
                {
                    newSelectionWindow = window.ImageBoundsRect.ToWpfRect();
                }
                else
                {
                    newSelectionWindow = default(WpfRect);
                }
            }
            else
            {
                newSelectionWindow = default(WpfRect);
            }

            if (newSelectionWindow != SelectionRectangle)
            {
                SelectionRectangle = newSelectionWindow;
            }

            Draw();
        }

        private void CaptureWindow2_MouseUp(object sender, MouseButtonEventArgs e)
        {
            this.ReleaseMouseCapture();

            if (!_dragBegin.HasValue)
                return; // huh??

            var draggingOrigin = _dragBegin.Value;
            _dragBegin = null;
            var currentMouse = ScreenTools.GetMousePosition();

            // if the mouse hasn't moved far, let's treat it like a click event and find out what window they clicked on
            if (DistancePointToPoint(currentMouse.X, currentMouse.Y, draggingOrigin.X, draggingOrigin.Y) < _clickDistance)
            {
                var window = _windowFinder.GetWindowThatContainsPoint(currentMouse);
                if (window != null)
                {
                    // show debug info if control key is being held while clicking
                    if (Keyboard.IsKeyDown(Key.LeftCtrl))
                        window.ShowDebug();

                    if (window.ImageBoundsRect == ScreenRect.Empty)
                    {
                        SelectionRectangle = default(WpfRect);
                        _dragBegin = null;
                        return;
                    }

                    SelectionRectangle = window.ImageBoundsRect.ToWpfRect();

                    // bring bitmap of window to front if we can
                    if (window.IsPartiallyCovered)
                    {
                        _selectedWindow = _windowFinder.GetTopLevelWindow(window);
                    }
                }
            }

            if (SelectionRectangle != default(WpfRect))
                StopCapture();
        }

        private void CaptureWindow2_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta > 0)
            {
                _globalZoom = Math.Ceiling(Math.Pow(_globalZoom, 1.2d) + 1);
            }
            else
            {
                _globalZoom = Math.Floor(Math.Pow(_globalZoom, 1d / 1.2d) - 1);
            }

            _globalZoom = Math.Min(Math.Max(_globalZoom, 1), 100);
            Draw();
        }

        protected override Visual GetVisualChild(int index) => _visuals[index];

        protected override int VisualChildrenCount => _visuals.Count;

        private void Draw()
        {
            var mouse = (_virtualPoint.HasValue && _globalZoom > 1) ? _virtualPoint.Value : ScreenTools.GetMousePosition().ToWpfPoint();
            DrawBackgroundImage(mouse);
            DrawForegroundImage(mouse);
            DrawCrosshair(mouse);
            DrawAreaIndicator(mouse);
        }

        private void DrawForegroundImage(WpfPoint mousePoint)
        {
            var windowBounds = ScreenTools.VirtualScreen.Bounds.ToWpfRect();
            using (var context = _foregroundImage.RenderOpen())
            {
                if (_globalZoom > 1)
                    context.PushTransform(new ScaleTransform(_globalZoom, _globalZoom, mousePoint.X, mousePoint.Y));

                if (SelectionRectangle != default(WpfRect))
                {
                    var clip = SelectionRectangle;
                    var clipRect = new Rect(clip.Left, clip.Top, clip.Width, clip.Height);
                    context.PushClip(new RectangleGeometry(clipRect));

                    if (_selectedWindow != null && _selectedWindow.WindowBitmapWpf != null)
                    {
                        var proBounds = _selectedWindow.WindowRect.ToWpfRect();
                        context.DrawImage(_selectedWindow.WindowBitmapWpf, new Rect(proBounds.Left, proBounds.Top, proBounds.Width, proBounds.Height));
                    }
                    else
                    {
                        context.DrawImage(_image, windowBounds);
                    }
                }
            }
        }

        private void DrawBackgroundImage(WpfPoint mousePoint)
        {
            var windowBounds = ScreenTools.VirtualScreen.Bounds.ToWpfRect();
            using (var context = _backgroundImage.RenderOpen())
            {
                if (_globalZoom > 1)
                    context.PushTransform(new ScaleTransform(_globalZoom, _globalZoom, mousePoint.X, mousePoint.Y));
                //if (_onFade < 1)
                //{
                //    context.DrawImage(_image, windowBounds);
                //    context.PushOpacity(_onFade);
                //}
                context.DrawImage(_imageGray, windowBounds);
                context.DrawRectangle(_overlayBrush, null, windowBounds);
            }
        }

        private void DrawAreaIndicator(WpfPoint mousePoint)
        {
            using (DrawingContext g = _sizeIndicator.RenderOpen())
            {
                if (_image == null || !IsCapturing || SelectionRectangle == WpfRect.Empty)
                    return;

                var screen = SelectionRectangle.ToScreenRect();

                var txt = new FormattedText(
                    $"{screen.Width} × {screen.Height}",
                    CultureInfo.CurrentUICulture,
                    this.FlowDirection,
                    new Typeface(new FontFamily("Microsoft Sans Serif"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    12,
                    new SolidColorBrush(Color.FromRgb(51, 51, 51)),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                const int padding = 10;
                double indicatorWidth = txt.WidthIncludingTrailingWhitespace + (padding * 2);
                double indicatorHeight = txt.Height + padding;

                var pt = new WpfPoint(SelectionRectangle.Left + SelectionRectangle.Width / 2, SelectionRectangle.Bottom);

                if (_globalZoom > 1)
                {
                    var transform = new ScaleTransform(_globalZoom, _globalZoom, mousePoint.X, mousePoint.Y);
                    var newpt = transform.Transform(new Point(pt.X, pt.Y));
                    pt = new WpfPoint(newpt.X, newpt.Y);
                }

                var positionTransform = PositionWithinAScreen(new WpfSize(indicatorWidth, indicatorHeight), pt, HorizontalAlignment.Center, VerticalAlignment.Bottom, padding);
                g.PushTransform(new TranslateTransform(positionTransform.X, positionTransform.Y));
                g.PushOpacity(0.8d);

                var border = new Pen(_accentBrush, 2);
                g.DrawRoundedRectangle(Brushes.White, border, new Rect(0, 0, indicatorWidth, indicatorHeight), indicatorHeight / 2, indicatorHeight / 2);

                Point textLocation = new Point(
                    (indicatorWidth / 2) - (txt.WidthIncludingTrailingWhitespace / 2) - 1,
                    (indicatorHeight / 2) - (txt.Height / 2)
                );
                g.DrawText(txt, textLocation);
            }
        }

        private void DrawCrosshair(WpfPoint mousePoint)
        {
            using (var context = _crosshair.RenderOpen())
            {
                if (!IsCapturing)
                    return;

                const double crossRadius = 100;
                const double halfCrossRadius = crossRadius / 2;

                var bounds = ScreenTools.VirtualScreen.Bounds.ToWpfRect();
                var offsetHalfPixel = ScreenTools.ScreenToWpf(0.5);
                var x = ScreenTools.WpfSnapToPixelsFloor(Math.Min(mousePoint.X, bounds.Right)) + offsetHalfPixel;
                var y = ScreenTools.WpfSnapToPixelsFloor(Math.Min(mousePoint.Y, bounds.Bottom)) + offsetHalfPixel;

                context.DrawLine(_sharpWhiteLineDashed, new Point(x, bounds.Top), new Point(x, y - crossRadius));
                context.DrawLine(_sharpWhiteLineDashed, new Point(x, bounds.Bottom), new Point(x, y + crossRadius));
                context.DrawLine(_sharpWhiteLineDashed, new Point(bounds.Left, y), new Point(x - crossRadius, y));
                context.DrawLine(_sharpWhiteLineDashed, new Point(bounds.Right, y), new Point(x + crossRadius, y));
                context.DrawLine(_sharpBlackLineDashed, new Point(x, bounds.Top), new Point(x, y - crossRadius));
                context.DrawLine(_sharpBlackLineDashed, new Point(x, bounds.Bottom), new Point(x, y + crossRadius));
                context.DrawLine(_sharpBlackLineDashed, new Point(bounds.Left, y), new Point(x - crossRadius, y));
                context.DrawLine(_sharpBlackLineDashed, new Point(bounds.Right, y), new Point(x + crossRadius, y));

                context.DrawLine(_sharpAccentLineWide, new Point(x, y - crossRadius), new Point(x, y - halfCrossRadius));
                context.DrawLine(_sharpAccentLineWide, new Point(x, y + halfCrossRadius), new Point(x, y + crossRadius));
                context.DrawLine(_sharpAccentLineWide, new Point(x - crossRadius, y), new Point(x - halfCrossRadius, y));
                context.DrawLine(_sharpAccentLineWide, new Point(x + halfCrossRadius, y), new Point(x + crossRadius, y));

                context.DrawLine(_sharpAccentLine, new Point(x - halfCrossRadius, y), new Point(x + halfCrossRadius, y));
                context.DrawLine(_sharpAccentLine, new Point(x, y - halfCrossRadius), new Point(x, y + halfCrossRadius));

                if (SelectionRectangle != WpfRect.Empty)
                {
                    if (_globalZoom > 1)
                        context.PushTransform(new ScaleTransform(_globalZoom, _globalZoom, mousePoint.X, mousePoint.Y));

                    var selRec = new WpfRect(
                        SelectionRectangle.Left + (offsetHalfPixel / _globalZoom),
                        SelectionRectangle.Top + (offsetHalfPixel / _globalZoom),
                        SelectionRectangle.Width,
                        SelectionRectangle.Height);

                    context.DrawRectangle(null, new Pen(_accentBrush, _sharpLineWidth / _globalZoom), selRec);
                }
            }
        }

        private static Color GetPixelColor(BitmapSource bitmap, int x, int y)
        {
            Color color;
            var bytesPerPixel = (bitmap.Format.BitsPerPixel + 7) / 8;
            var bytes = new byte[bytesPerPixel];
            var rect = new Int32Rect(x, y, 1, 1);

            bitmap.CopyPixels(rect, bytes, bytesPerPixel, 0);

            if (bitmap.Format == PixelFormats.Bgra32)
            {
                color = Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
            }
            else if (bitmap.Format == PixelFormats.Bgr32)
            {
                color = Color.FromRgb(bytes[2], bytes[1], bytes[0]);
            }
            else if (bitmap.Format == PixelFormats.Bgr24)
            {
                color = Color.FromRgb(bytes[2], bytes[1], bytes[0]);
            }
            // handle other required formats
            else
            {
                color = Colors.Black;
            }

            return color;
        }

        private double DistancePointToPoint(double x1, double y1, double x2, double y2)
        {
            return Math.Sqrt(Math.Pow((x2 - x1), 2) + Math.Pow((y2 - y1), 2));
        }

        private WpfPoint PositionWithinAScreen(WpfSize objectRect, WpfPoint anchor, HorizontalAlignment horz, VerticalAlignment vert, double distance, double marginX = 0d)
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

            double x = alignCoordinate(horz == HorizontalAlignment.Left ? -1 : horz == HorizontalAlignment.Right ? 1 : 0, anchor.X, objectRect.Width, screen.Left + marginX, screen.Right - marginX);
            double y = alignCoordinate(vert == VerticalAlignment.Top ? -1 : vert == VerticalAlignment.Bottom ? 1 : 0, anchor.Y, objectRect.Height, screen.Top, screen.Bottom);

            x = Math.Max(screen.Left, Math.Min(screen.Right - objectRect.Width, x));
            y = Math.Max(screen.Top, Math.Min(screen.Bottom - objectRect.Height, y));

            return new WpfPoint(x, y);
        }

        private enum ArrowIndicatorPosition
        {
            None,
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }

        private class MyDrawingVisual : DrawingVisual
        {
            public MyDrawingVisual()
            {
                VisualBitmapScalingMode = BitmapScalingMode.NearestNeighbor;
            }
        }
    }
}
