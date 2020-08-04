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

        ScreenPoint? _lastPoint = null;
        double _lastPointDeltaX = 0;
        double _lastPointDeltaY = 0;
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

        WpfSize _finderSize;
        ScreenSize _singlePixelSize;
        int _zoomedPixels;
        Color _accentColor;
        Brush _accentBrush;
        Brush _magCrosshairBrush;
        Pen _magBorderPen;
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

            // magnifier constants
            var magZoom = 10;
            var magArea = 10;
            _singlePixelSize = new WpfSize(magZoom, magZoom).ToScreenSize();
            _zoomedPixels = magArea - magArea % 2 + 1;
            _finderSize = (_singlePixelSize * _zoomedPixels).ToWpfSize();
            _magBorderPen = new Pen(_accentBrush, 2);
            _magCrosshairBrush = new SolidColorBrush(Color.FromArgb(125, 173, 216, 230));
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
            if (_lastPoint.HasValue && _globalZoom > 1)
            {
                var xDelta = (currentPoint.X - _lastPoint.Value.X) / _globalZoom * ScreenTools.DpiZoom;
                var yDelta = (currentPoint.Y - _lastPoint.Value.Y) / _globalZoom * ScreenTools.DpiZoom;

                _lastPointDeltaX += xDelta;
                _lastPointDeltaY += yDelta;

                var newX = _lastPoint.Value.X;
                var newY = _lastPoint.Value.Y;

                while (_lastPointDeltaX > 1)
                {
                    newX += 1;
                    _lastPointDeltaX -= 1;
                }

                while (_lastPointDeltaX < 1)
                {
                    newX -= 1;
                    _lastPointDeltaX += 1;
                }

                while (_lastPointDeltaY > 1)
                {
                    newY += 1;
                    _lastPointDeltaY -= 1;
                }

                while (_lastPointDeltaY < 1)
                {
                    newY -= 1;
                    _lastPointDeltaY += 1;
                }

                var slowPoint = new ScreenPoint(newX, newY);
                _lastPoint = slowPoint;
                Console.WriteLine($"(cursor) :: {currentPoint} -> {slowPoint} ... dx:{xDelta}/{_lastPointDeltaX}, dy:{yDelta}/{_lastPointDeltaY}");
                System.Windows.Forms.Cursor.Position = slowPoint.ToSystem();
                currentPoint = slowPoint;
            }
            else
            {
                _lastPoint = currentPoint;
                _lastPointDeltaX = 0;
                _lastPointDeltaY = 0;
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
            //DrawCrosshair();
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
            var mouse = (_lastPoint.HasValue && _globalZoom > 1) ? _lastPoint.Value : ScreenTools.GetMousePosition();
            DrawBackgroundImage(mouse);
            DrawForegroundImage(mouse);
            DrawCrosshair(mouse);
            //DrawMagnifier(mouse);
            DrawAreaIndicator(mouse);
        }

        private void DrawForegroundImage(ScreenPoint mousePoint)
        {
            var windowBounds = ScreenTools.VirtualScreen.Bounds.ToWpfRect();
            using (var context = _foregroundImage.RenderOpen())
            {
                var wpfPoint = mousePoint.ToWpfPoint();
                if (_globalZoom > 1)
                    context.PushTransform(new ScaleTransform(_globalZoom, _globalZoom, wpfPoint.X, wpfPoint.Y));

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

        private void DrawBackgroundImage(ScreenPoint mousePoint)
        {
            var windowBounds = ScreenTools.VirtualScreen.Bounds.ToWpfRect();
            using (var context = _backgroundImage.RenderOpen())
            {
                var wpfPoint = mousePoint.ToWpfPoint();
                if (_globalZoom > 1)
                    context.PushTransform(new ScaleTransform(_globalZoom, _globalZoom, wpfPoint.X, wpfPoint.Y));
                //if (_onFade < 1)
                //{
                //    context.DrawImage(_image, windowBounds);
                //    context.PushOpacity(_onFade);
                //}
                context.DrawImage(_imageGray, windowBounds);
                context.DrawRectangle(_overlayBrush, null, windowBounds);
            }
        }

        private void DrawAreaIndicator(ScreenPoint mousePoint)
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

        private void DrawCrosshair(ScreenPoint mousePoint)
        {
            using (var context = _crosshair.RenderOpen())
            {
                if (!IsCapturing)
                    return;

                const double crossRadius = 100;
                const double halfCrossRadius = crossRadius / 2;

                var bounds = ScreenTools.VirtualScreen.Bounds.ToWpfRect();
                var cursor = mousePoint.ToWpfPoint();
                var offsetHalfPixel = ScreenTools.ScreenToWpf(0.5);
                var x = ScreenTools.WpfSnapToPixelsFloor(Math.Min(cursor.X, bounds.Right)) + offsetHalfPixel;
                var y = ScreenTools.WpfSnapToPixelsFloor(Math.Min(cursor.Y, bounds.Bottom)) + offsetHalfPixel;

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
                        context.PushTransform(new ScaleTransform(_globalZoom, _globalZoom, cursor.X, cursor.Y));

                    var selRec = new WpfRect(
                        SelectionRectangle.Left + (offsetHalfPixel / _globalZoom),
                        SelectionRectangle.Top + (offsetHalfPixel / _globalZoom),
                        SelectionRectangle.Width,
                        SelectionRectangle.Height);

                    context.DrawRectangle(null, new Pen(_accentBrush, _sharpLineWidth / _globalZoom), selRec);
                }
            }
        }

        private void DrawMagnifier(ScreenPoint mousePoint)
        {
            const double indArrowSize = 60d;
            const double indBorderExclude = 10d;

            using (DrawingContext g = _magnifier.RenderOpen())
            {
                if (_image == null || !IsCapturing || !ShowMagnifier)
                    return;

                // calculate size of color box. this changes the finder size
                var zoomedColor = GetPixelColor(_image, mousePoint.X, mousePoint.Y);
                // convert to grayscale and then calculate hsl
                var grayScale = (0.3d * zoomedColor.R) + (0.59d * zoomedColor.G) + (0.11d * zoomedColor.G);
                // if lightness is > 60% then we want to use black
                var txtColor = grayScale > 127 ? Color.FromArgb(200, 0, 0, 0) : Color.FromArgb(200, 255, 255, 255);
                var txtBrush = new SolidColorBrush(txtColor);
                var txt = new FormattedText(
                    $"rgb({zoomedColor.R},{zoomedColor.G},{zoomedColor.B})\r\n{zoomedColor.ToHexRgb()}",
                    CultureInfo.CurrentUICulture,
                    this.FlowDirection,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    12,
                    txtBrush,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                double colorBoxWidth = txt.WidthIncludingTrailingWhitespace + (indBorderExclude * 2);
                double colorBoxHeight = txt.Height + indBorderExclude;

                var currentPointWpf = mousePoint.ToWpfPoint();
                var positionTransform = PositionWithinAScreen(_finderSize, currentPointWpf, HorizontalAlignment.Right, VerticalAlignment.Bottom, 20, colorBoxWidth);
                g.PushTransform(new TranslateTransform(positionTransform.X, positionTransform.Y));

                ArrowIndicatorPosition arrow;
                if (positionTransform.X > currentPointWpf.X && positionTransform.Y > currentPointWpf.Y) // point is to the bottom right
                    arrow = ArrowIndicatorPosition.TopLeft;
                else if (positionTransform.X > currentPointWpf.X) // point is to the top right
                    arrow = ArrowIndicatorPosition.BottomLeft;
                else if (positionTransform.Y > currentPointWpf.Y) // point is to the bottom left
                    arrow = ArrowIndicatorPosition.TopRight;
                else // point is to the top left
                    arrow = ArrowIndicatorPosition.BottomRight;

                var cornerX = (int)mousePoint.X - _zoomedPixels / 2;
                var cornerY = (int)mousePoint.Y - _zoomedPixels / 2;
                var px = ScreenTools.ScreenToWpf(1);

                var sourceRect = new ScreenRect(cornerX, cornerY, _zoomedPixels, _zoomedPixels);
                var targetRect = new WpfRect(0, 0, _finderSize.Width, _finderSize.Height);

                // Crop the source & target rectangles so that they don't go past the edges of the screen(s)
                var zoomedPixel = _singlePixelSize.ToWpfSize();
                if (sourceRect.Left < 0)
                {
                    sourceRect.Left -= cornerX;
                    sourceRect.Width += cornerX;
                    targetRect.Left -= cornerX * zoomedPixel.Width;
                    targetRect.Width += cornerX * zoomedPixel.Width;
                }
                if (sourceRect.Top < 0)
                {
                    sourceRect.Top -= cornerY;
                    sourceRect.Height += cornerY;
                    targetRect.Top -= cornerY * zoomedPixel.Height;
                    targetRect.Height += cornerY * zoomedPixel.Height;
                }
                if (sourceRect.Left + sourceRect.Width > _image.PixelWidth)
                {
                    int excess = sourceRect.Left + sourceRect.Width - _image.PixelWidth;
                    sourceRect.Width -= excess;
                    targetRect.Width -= excess * zoomedPixel.Width;
                }
                if (sourceRect.Top + sourceRect.Height > _image.PixelHeight)
                {
                    int excess = sourceRect.Top + sourceRect.Height - _image.PixelHeight;
                    sourceRect.Height -= excess;
                    targetRect.Height -= excess * zoomedPixel.Height;
                }

                var gridLinePixelWidth = Math.Max(ScreenTools.WpfToScreen(_sharpLineWidth), 1);
                var gridOffset = (gridLinePixelWidth % 2) * 0.5 * px; // offset the line by 0.5 pixels if the line width is odd, to avoid blurring
                // Clip to the exact same ellipse as the border (thus clipping off half of the drawn border)
                g.PushClip(new EllipseGeometry(new Point(_finderSize.Width / 2 + gridOffset, _finderSize.Height / 2 + gridOffset), _finderSize.Width / 2, _finderSize.Height / 2));

                // Draw the black background visible at the edge of the screen where no zoomed pixels are available
                g.DrawRectangle(Brushes.Black, null, new Rect(0, 0, _finderSize.Width, _finderSize.Height));

                // Draw the magnified image
                var group = new DrawingGroup();
                group.Children.Add(new ImageDrawing(new CroppedBitmap(_image, sourceRect), targetRect));
                g.DrawDrawing(group);

                // Draw the pixel grid lines
                var gridLineWidth = ScreenTools.ScreenToWpf(gridLinePixelWidth);
                var gridPen = new Pen(Brushes.DimGray, gridLineWidth);

                // Apply grid offset transform and draw grid
                g.PushTransform(new TranslateTransform(gridOffset, gridOffset));
                for (int x = sourceRect.Left - cornerX; x <= sourceRect.Left + sourceRect.Width - cornerX; x++)
                    g.DrawLine(gridPen, new Point(x * zoomedPixel.Width, targetRect.Top), new Point(x * zoomedPixel.Width, targetRect.Bottom));
                for (int y = sourceRect.Top - cornerY; y <= sourceRect.Top + sourceRect.Height - cornerY; y++)
                    g.DrawLine(gridPen, new Point(targetRect.Left, y * zoomedPixel.Height), new Point(targetRect.Right, y * zoomedPixel.Height));

                // Draw the crosshair
                var xhairGrow = gridLineWidth / 2; // make sure the crosshair rectangles cover the adjacent grid lines wholly on both sides
                g.DrawRectangle(_magCrosshairBrush, null, new WpfRect(0, (_finderSize.Height - zoomedPixel.Height) / 2, (_finderSize.Width - zoomedPixel.Width) / 2, zoomedPixel.Height).Grow(xhairGrow)); // Left
                g.DrawRectangle(_magCrosshairBrush, null, new WpfRect((_finderSize.Width + zoomedPixel.Width) / 2, (_finderSize.Height - zoomedPixel.Height) / 2, (_finderSize.Width - zoomedPixel.Width) / 2, zoomedPixel.Height).Grow(xhairGrow)); // Right
                g.DrawRectangle(_magCrosshairBrush, null, new WpfRect((_finderSize.Width - zoomedPixel.Width) / 2, 0, zoomedPixel.Width, (_finderSize.Height - zoomedPixel.Height) / 2).Grow(xhairGrow)); // Top
                g.DrawRectangle(_magCrosshairBrush, null, new WpfRect((_finderSize.Width - zoomedPixel.Width) / 2, (_finderSize.Height + zoomedPixel.Height) / 2, zoomedPixel.Width, (_finderSize.Height - zoomedPixel.Height) / 2).Grow(xhairGrow)); // Bottom

                // Draw a highlight around the pixel under cursor
                var innerRect = new WpfRect((_finderSize.Width - zoomedPixel.Width) / 2, (_finderSize.Height - zoomedPixel.Height) / 2, zoomedPixel.Width, zoomedPixel.Height);
                g.DrawRectangle(null, new Pen(Brushes.White, gridLineWidth), innerRect);
                g.DrawRectangle(null, new Pen(Brushes.Black, gridLineWidth), innerRect.Grow(gridLineWidth));
                g.Pop(); // grid line 0.5 px offset

                // Draw the magnifier border
                g.DrawEllipse(null, _magBorderPen, new Point(_finderSize.Width / 2 + gridOffset, _finderSize.Height / 2 + gridOffset), _finderSize.Width / 2, _finderSize.Height / 2);

                g.Pop(); // the circular clip

                Rect indicatorSquare = Rect.Empty;
                Rect colorSquare = Rect.Empty;
                double colorTxtOffsetX = (indBorderExclude / 2);

                switch (arrow)
                {
                    case ArrowIndicatorPosition.TopLeft:
                        indicatorSquare = new Rect(0, 0, indArrowSize, indArrowSize);
                        colorSquare = new Rect(_finderSize.Width, (_finderSize.Height - colorBoxHeight) / 2, colorBoxWidth, colorBoxHeight);
                        colorTxtOffsetX += indBorderExclude;
                        break;
                    case ArrowIndicatorPosition.BottomLeft:
                        indicatorSquare = new Rect(0, _finderSize.Height - indArrowSize, indArrowSize, indArrowSize);
                        colorSquare = new Rect(_finderSize.Width, (_finderSize.Height - colorBoxHeight) / 2, colorBoxWidth, colorBoxHeight);
                        colorTxtOffsetX += indBorderExclude;
                        break;
                    case ArrowIndicatorPosition.TopRight:
                        indicatorSquare = new Rect(_finderSize.Width - indArrowSize, 0, indArrowSize, indArrowSize);
                        colorSquare = new Rect(0 - colorBoxWidth, (_finderSize.Height - colorBoxHeight) / 2, colorBoxWidth, colorBoxHeight);
                        break;
                    case ArrowIndicatorPosition.BottomRight:
                        indicatorSquare = new Rect(_finderSize.Width - indArrowSize, _finderSize.Height - indArrowSize, indArrowSize, indArrowSize);
                        colorSquare = new Rect(0 - colorBoxWidth, (_finderSize.Height - colorBoxHeight) / 2, colorBoxWidth, colorBoxHeight);
                        break;
                }
                if (!indicatorSquare.IsEmpty)
                {
                    var indicatorGeo = Geometry.Combine(
                        new RectangleGeometry(indicatorSquare),
                        new EllipseGeometry(new Rect(-indBorderExclude, -indBorderExclude, _finderSize.Width + (indBorderExclude * 2), _finderSize.Height + (indBorderExclude * 2))),
                        GeometryCombineMode.Exclude, null);
                    g.DrawGeometry(new SolidColorBrush(Color.FromArgb(180, 135, 135, 135)), null, indicatorGeo);

                    var colorGeo = Geometry.Combine(
                      new RectangleGeometry(colorSquare, 4, 4),
                      new EllipseGeometry(new Rect(-indBorderExclude, -indBorderExclude, _finderSize.Width + (indBorderExclude * 2), _finderSize.Height + (indBorderExclude * 2))),
                      GeometryCombineMode.Exclude, null);

                    //var colorPen = new Pen(new SolidColorBrush(Color.FromRgb((byte)(255 - zoomedColor.R), (byte)(255 - zoomedColor.G), (byte)(255 - zoomedColor.B))), _sharpLineWidth);

                    g.DrawGeometry(new SolidColorBrush(zoomedColor), new Pen(txtBrush, _sharpLineWidth), colorGeo);
                    g.DrawText(txt, new Point(colorSquare.X + colorTxtOffsetX, colorSquare.Y + ((colorSquare.Height - txt.Height) / 2)));
                }

                this.Width = _finderSize.Width;
                this.Height = _finderSize.Height;
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
