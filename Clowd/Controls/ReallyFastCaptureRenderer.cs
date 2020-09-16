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

        private static void SelectionRectangleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ths = (ReallyFastCaptureRenderer)d;
            ths._selectedWindow = null;
            ths.Draw();
        }

        WpfPoint? _virtualDragBegin = null;
        WpfPoint? _virtualPoint = null;
        ScreenPoint? _lastScreenPoint = null;
        const int _clickDistance = 6;
        bool _dragging = false;
        double _minTipsWidth = 200;

        Brush _overlayBrush = new SolidColorBrush(Color.FromArgb(127, 0, 0, 0));

        WindowFinder3.CachedWindow _selectedWindow;
        WindowFinder3 _windowFinder;
        BitmapSource _image;
        BitmapSource _imageGray;

        VisualCollection _visuals;
        DrawingVisual _backgroundImage;
        DrawingVisual _crosshair;
        DrawingVisual _foregroundImage;
        DrawingVisual _sizeIndicator;
        DrawingVisual _tipsPanel;

        Pen _sharpBlackLineDashed;
        Pen _sharpWhiteLineDashed;
        Pen _sharpAccentLine;
        Pen _sharpAccentLineWide;

        Color _accentColor;
        Brush _accentBrush;
        double _sharpLineWidth;
        double _globalZoom = 1;

        private static ScreenUtil _screen = new ScreenUtil();

        public ReallyFastCaptureRenderer()
        {
            this.Cursor = Cursors.None;

            _backgroundImage = new MyDrawingVisual();
            _foregroundImage = new MyDrawingVisual();
            _sizeIndicator = new DrawingVisual();
            _crosshair = new DrawingVisual();
            _tipsPanel = new DrawingVisual();

            _visuals = new VisualCollection(this);
            _visuals.Add(_backgroundImage);
            _visuals.Add(_foregroundImage);
            _visuals.Add(_sizeIndicator);
            _visuals.Add(_crosshair);
            _visuals.Add(_tipsPanel);

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

        public void StartFastCapture(TimedConsoleLogger timer)
        {
            timer.Log("FastCapStage1", "Start");

            _windowFinder = new WindowFinder3();
            _windowFinder.CapturePart1(timer);
            _image = _screen.CaptureScreenWpf(null, App.Current.Settings.CaptureSettings.ScreenshotWithCursor, timer);

            timer.Log("FastCapStage1", "Creating grayscale image");
            _imageGray = new FormatConvertedBitmap(_image, PixelFormats.Gray8, BitmapPalettes.Gray256, 1);

            timer.Log("FastCapStage1", "Rendering visuals");
            Reset();

            timer.Log("FastCapStage1", "Complete");
        }

        public Task FinishUpFastCapture(TimedConsoleLogger timer)
        {
            return Task.Run(() =>
            {
                timer.Log("FastCapStage2", "Start");
                _windowFinder.CapturePart2(timer);
                _windowFinder.CapturePart3(timer);
                timer.Log("FastCapStage2", "Complete");
            });
        }

        public void Reset()
        {
            if (IsCapturing)
                return;

            _selectedWindow = null;
            _virtualPoint = null;
            _virtualDragBegin = null;
            _lastScreenPoint = null;
            IsCapturing = true;
            this.Cursor = Cursors.None;

            this.MouseMove += CaptureWindow2_MouseMove;
            this.MouseDown += CaptureWindow2_MouseDown;
            this.MouseUp += CaptureWindow2_MouseUp;
            this.MouseWheel += CaptureWindow2_MouseWheel;

            CaptureWindow2_MouseMove(null, null);
        }

        public void SelectScreen()
        {
            if (_virtualDragBegin.HasValue || !IsCapturing)
                return;

            var screenContainingMouse = ScreenTools.GetScreenContaining(ScreenTools.GetMousePosition()).Bounds;
            SelectionRectangle = screenContainingMouse.ToWpfRect();
            StopCapture();
        }

        public void SelectAll()
        {
            if (_virtualDragBegin.HasValue || !IsCapturing)
                return;
            SelectionRectangle = new WpfRect(0, 0, this.Width, this.Height);
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

            if (_selectedWindow != null)
            {
                var bmp = _selectedWindow.GetBitmap();
                if (bmp != null)
                {
                    var pwinrect = _selectedWindow.WindowRect;
                    rect = new ScreenRect(rect.Left - pwinrect.Left, rect.Top - pwinrect.Top, rect.Width, rect.Height);
                    return new CroppedBitmap(bmp, rect);
                }
            }

            return new CroppedBitmap(_image, rect);
        }

        public Color GetHoveredColor()
        {
            var zoomedColor = GetPixelColor(
                _image,
                ScreenTools.WpfToScreen(ScreenTools.WpfSnapToPixelsFloor(_virtualPoint.Value.X)),
                ScreenTools.WpfToScreen(ScreenTools.WpfSnapToPixelsFloor(_virtualPoint.Value.Y)));
            return zoomedColor;
        }

        public void SetSelectedWindowForeground()
        {
            if (_selectedWindow != null)
                Interop.USER32.SetForegroundWindow(_selectedWindow.Handle);
        }

        private void CaptureWindow2_MouseDown(object sender, MouseButtonEventArgs e)
        {
            this.CaptureMouse();
            _virtualDragBegin = _virtualPoint;
        }

        private void CaptureWindow2_MouseMove(object sender, MouseEventArgs e)
        {
            if (_globalZoom > 1 && _virtualPoint.HasValue && _lastScreenPoint.HasValue)
            {
                var currentPoint = ScreenTools.GetMousePosition();
                var xDelta = ScreenTools.ScreenToWpf((currentPoint.X - _lastScreenPoint.Value.X) / _globalZoom);
                var yDelta = ScreenTools.ScreenToWpf((currentPoint.Y - _lastScreenPoint.Value.Y) / _globalZoom);

                _virtualPoint = new WpfPoint(_virtualPoint.Value.X + xDelta, _virtualPoint.Value.Y + yDelta);

                currentPoint = _virtualPoint.Value.ToScreenPoint();
                _lastScreenPoint = currentPoint;
                System.Windows.Forms.Cursor.Position = currentPoint.ToSystem();
            }
            else
            {
                var currentPoint = ScreenTools.GetMousePosition();
                _lastScreenPoint = currentPoint;
                _virtualPoint = currentPoint.ToWpfPoint();
            }

            if (_virtualPoint.HasValue && _virtualDragBegin.HasValue
                && (_dragging || DistancePointToPoint(_virtualPoint.Value.X, _virtualPoint.Value.Y, _virtualDragBegin.Value.X, _virtualDragBegin.Value.Y) > _clickDistance / _globalZoom))
            {
                _dragging = true;

                double left, right;
                if (_virtualDragBegin.Value.X < _virtualPoint.Value.X)
                {
                    left = ScreenTools.WpfSnapToPixelsFloor(_virtualDragBegin.Value.X);
                    right = ScreenTools.WpfSnapToPixelsCeil(_virtualPoint.Value.X);
                }
                else
                {
                    left = ScreenTools.WpfSnapToPixelsFloor(_virtualPoint.Value.X);
                    right = ScreenTools.WpfSnapToPixelsCeil(_virtualDragBegin.Value.X);
                }

                double top, bottom;
                if (_virtualDragBegin.Value.Y < _virtualPoint.Value.Y)
                {
                    top = ScreenTools.WpfSnapToPixelsFloor(_virtualDragBegin.Value.Y);
                    bottom = ScreenTools.WpfSnapToPixelsCeil(_virtualPoint.Value.Y);
                }
                else
                {
                    top = ScreenTools.WpfSnapToPixelsFloor(_virtualPoint.Value.Y);
                    bottom = ScreenTools.WpfSnapToPixelsCeil(_virtualDragBegin.Value.Y);
                }

                SelectionRectangle = new WpfRect(left, top, right - left, bottom - top).ToScreenRect().ToWpfRect();
            }
            else if (App.Current.Settings.CaptureSettings.DetectWindows)
            {
                var window = _windowFinder?.HitTest(_virtualPoint.Value.ToScreenPoint());
                if (window != null)
                {
                    SelectionRectangle = window.WindowRect.ToWpfRect();
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

        private void CaptureWindow2_MouseUp(object sender, MouseButtonEventArgs e)
        {
            this.ReleaseMouseCapture();

            if (!_virtualDragBegin.HasValue)
                return; // huh??

            var origin = _virtualDragBegin.Value;
            var current = _virtualPoint.Value;
            var dragging = _dragging;

            _dragging = false;
            _virtualDragBegin = null;

            // if the mouse hasn't moved far, let's treat it like a click event and find out what window they clicked on
            if (!dragging && DistancePointToPoint(origin.X, origin.Y, current.X, current.Y) < _clickDistance)
            {
                var window = _windowFinder?.HitTest(current.ToScreenPoint());
                if (window != null)
                {
                    // show debug info if control key is being held while clicking
                    //if (Keyboard.IsKeyDown(Key.LeftCtrl))
                    //    window.ShowDebug();

                    if (window.WindowRect == ScreenRect.Empty)
                    {
                        SelectionRectangle = default(WpfRect);
                        return;
                    }

                    SelectionRectangle = window.WindowRect.ToWpfRect();

                    // bring bitmap of window to front if we can
                    if (window.IsPartiallyCovered)
                    {
                        _selectedWindow = window;
                    }
                }
            }

            if (SelectionRectangle != default(WpfRect))
                StopCapture();
        }

        private void CaptureWindow2_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Delta > 0)
                    _globalZoom *= 1.05;
                else
                    _globalZoom /= 1.05;
            }
            else
            {
                if (e.Delta > 0)
                    _globalZoom = Math.Ceiling(Math.Pow(_globalZoom, 1.2d) + 1);
                else
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
            DrawTips(mouse);
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

                    var selBmp = _selectedWindow?.GetBitmap();
                    if (selBmp != null)
                    {
                        var proBounds = _selectedWindow.WindowRect.ToWpfRect();
                        context.DrawImage(selBmp, new Rect(proBounds.Left, proBounds.Top, proBounds.Width, proBounds.Height));
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

        private void DrawTips(WpfPoint mousePoint)
        {
            List<(FormattedText shortcut, FormattedText text)> lines = new List<(FormattedText shortcut, FormattedText text)>();

            FormattedText getText(string text, int fontSize = 12, FontWeight? weight = null, bool recurse = true)
            {
                FormattedText txt = new FormattedText(
                    recurse ? text : (text + "..."),
                    CultureInfo.CurrentUICulture,
                    this.FlowDirection,
                    new Typeface(new FontFamily("Microsoft Sans Serif"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
                    fontSize,
                    new SolidColorBrush(Color.FromRgb(51, 51, 51)),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                if (recurse && txt.WidthIncludingTrailingWhitespace > 300)
                {
                    text = text.Substring(0, text.Length - 3);
                    while (txt.WidthIncludingTrailingWhitespace > 300)
                    {
                        text = text.Substring(0, text.Length - 1);
                        txt = getText(text, fontSize, weight, false);
                    }
                }
                return txt;
            }

            void addLine(string shortcut, string text)
            {
                lines.Add((getText(shortcut, weight: FontWeights.ExtraBold), getText(text)));
            }

            using (DrawingContext g = _tipsPanel.RenderOpen())
            {
                if (!IsCapturing)
                    return;

                var screenMouse = mousePoint.ToScreenPoint();

                var hoveredWindow = _windowFinder.HitTest(screenMouse)?.GetTopLevel();
                addLine("W", hoveredWindow?.Caption ?? " - ");

                var zoomedColor = this.GetHoveredColor();
                addLine("H", zoomedColor.ToHexRgb() + $"\nrgb({zoomedColor.R},{zoomedColor.G},{zoomedColor.B})");

                addLine("-", "Scroll to zoom!");
                addLine("F", "Select current screen");
                addLine("A", "Select all screens");

                const int shortcutWidth = 30;
                const int colorWidth = 30;
                const int margin = 10;
                const int iconWidth = 50;
                var title = getText("Shortcuts", 14, weight: FontWeights.ExtraBold);

                double height = ScreenTools.WpfSnapToPixels(lines.Sum(l => Math.Max(l.text.Height, l.shortcut.Height)) + ((lines.Count + 2) * margin) + title.Height);
                double width = ScreenTools.WpfSnapToPixels(lines.Max(l => l.text.WidthIncludingTrailingWhitespace) + iconWidth + shortcutWidth + (margin * 2));

                _minTipsWidth = Math.Max(_minTipsWidth, width);
                width = _minTipsWidth;

                double textYPadding = margin * 2 + title.Height;

                if (DistancePointToPoint(screenMouse.X, screenMouse.Y, _image.PixelWidth - 100 - (width / 2), _image.PixelHeight - 100 - (height / 2)) < width)
                {
                    g.PushTransform(new TranslateTransform(100, _image.PixelHeight - 100 - height));
                }
                else
                {
                    g.PushTransform(new TranslateTransform(_image.PixelWidth - 100 - width, _image.PixelHeight - 100 - height));
                }
                g.PushOpacity(0.8d);
                //var rounded = new RectangleGeometry(new Rect(0, 0, width, height), 5, 5);
                //g.PushClip(rounded);

                // draw background
                g.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                g.DrawRectangle(_accentBrush, null, new Rect(0, 0, iconWidth, height));
                g.DrawRectangle(null, new Pen(Brushes.Black, _sharpLineWidth), new Rect(_sharpLineWidth / 2, _sharpLineWidth / 2, width - _sharpLineWidth, height - _sharpLineWidth));

                // draw info icon
                var cp = new Pen(Brushes.White, 3);
                var cr = (iconWidth - (margin * 2)) / 2;
                var iHeight = iconWidth - (margin * 3) - 3;
                var iMin = (iconWidth / 2) - (iHeight / 2);
                var iMax = iMin + iHeight;
                g.DrawEllipse(null, cp, new Point(iconWidth / 2, iconWidth / 2), cr, cr);
                g.DrawLine(cp, new Point(iconWidth / 2, iMin + (iHeight / 3)), new Point(iconWidth / 2, iMax));
                g.DrawEllipse(Brushes.White, null, new Point(iconWidth / 2, iMin), 2d, 2d);

                // draw text
                g.DrawText(title, new Point(iconWidth + margin, margin));
                for (int i = 0; i < lines.Count; i++)
                {
                    var l = lines[i];
                    var textHeight = Math.Max(l.shortcut.Height, l.text.Height);

                    if (i == 1) // color display.. this detection is very bad! fix this, maybe?? 
                    {
                        var colorRect = new Rect(iconWidth + shortcutWidth, textYPadding, colorWidth, textHeight);
                        var screenRect = new WpfRect(colorRect).ToScreenRect();

                        var grayScale = (0.3d * zoomedColor.R) + (0.59d * zoomedColor.G) + (0.11d * zoomedColor.G);

                        if (grayScale > 127)
                        {
                            g.DrawRectangle(Brushes.Black, null, screenRect.ToWpfRect());
                            screenRect = new ScreenRect(screenRect.Left + 1, screenRect.Top + 1, screenRect.Width - 2, screenRect.Height - 2);
                        }

                        g.DrawRectangle(new SolidColorBrush(zoomedColor), null, screenRect.ToWpfRect());
                        g.DrawText(l.text, new Point(iconWidth + shortcutWidth + colorWidth + margin, textYPadding));
                    }
                    else
                    {
                        g.DrawText(l.text, new Point(iconWidth + shortcutWidth, textYPadding));
                    }

                    g.DrawText(l.shortcut, new Point((shortcutWidth / 2) - (l.shortcut.WidthIncludingTrailingWhitespace / 2) + iconWidth, textYPadding));

                    textYPadding += textHeight + margin;
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

        private class MyDrawingVisual : DrawingVisual
        {
            public MyDrawingVisual()
            {
                VisualBitmapScalingMode = BitmapScalingMode.NearestNeighbor;
            }
        }
    }
}
