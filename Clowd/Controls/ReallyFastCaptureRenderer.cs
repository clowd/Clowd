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
            DependencyProperty.Register(nameof(IsCapturing), typeof(bool), typeof(ReallyFastCaptureRenderer), new PropertyMetadata(false));

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
            ths._boundsPromotedWindow = null;
            ths._imagePromotedWindow = null;
            ths.DrawForegroundImage();
        }

        ScreenPoint? _dragBegin = null;
        const int _clickDistance = 6;

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
        DrawingVisual _magnifier;

        double _sharpLineWidth;
        Pen _sharpBlackLineDashed;
        Pen _sharpWhiteLineDashed;
        Pen _sharpAccentLine;
        Pen _sharpAccentLineWide;

        private WpfSize _finderSize;
        private ScreenSize _singlePixelSize;
        private int _zoomedPixels;

        public ReallyFastCaptureRenderer()
        {
            this.Cursor = Cursors.None;

            _backgroundImage = new DrawingVisual();
            _foregroundImage = new DrawingVisual();
            _crosshair = new DrawingVisual();
            _magnifier = new DrawingVisual();

            _visuals = new VisualCollection(this);
            _visuals.Add(_backgroundImage);
            _visuals.Add(_foregroundImage);
            _visuals.Add(_crosshair);
            _visuals.Add(_magnifier);

            // Crosshair constants
            var accentBrush = new SolidColorBrush(App.Current.AccentColor);
            var dashLength = ScreenTools.WpfSnapToPixelsFloor(8);
            _sharpLineWidth = ScreenTools.WpfSnapToPixelsFloor(1);
            _sharpBlackLineDashed = new Pen(Brushes.Black, _sharpLineWidth);
            _sharpBlackLineDashed.DashStyle = new DashStyle(new double[] { dashLength, dashLength }, 0);
            _sharpWhiteLineDashed = new Pen(Brushes.White, _sharpLineWidth);
            _sharpWhiteLineDashed.DashStyle = new DashStyle(new double[] { dashLength, dashLength }, dashLength);
            _sharpAccentLine = new Pen(accentBrush, _sharpLineWidth);
            _sharpAccentLineWide = new Pen(accentBrush, _sharpLineWidth * 5);

            // Magnifier constants
            _singlePixelSize = new WpfSize(App.Current.Settings.MagnifierSettings.Zoom, App.Current.Settings.MagnifierSettings.Zoom).ToScreenSize();
            _zoomedPixels = App.Current.Settings.MagnifierSettings.AreaSize - App.Current.Settings.MagnifierSettings.AreaSize % 2 + 1;
            _finderSize = (_singlePixelSize * _zoomedPixels).ToWpfSize();
        }

        public void DoFastCapture()
        {
            if (_windowFinder != null)
                return;

            _windowFinder = WindowFinder2.NewCapture();
            using (var source = ScreenUtil.Capture(captureCursor: App.Current.Settings.CaptureSettings.ScreenshotWithCursor))
            {
                _image = source.ToBitmapSource();
                _imageGray = new FormatConvertedBitmap(_image, PixelFormats.Gray8, BitmapPalettes.Gray256, 1);
            }

            Reset();
        }

        public void Reset()
        {
            if (IsCapturing)
                return;

            _imagePromotedWindow = null;
            _boundsPromotedWindow = null;
            IsCapturing = true;
            this.Cursor = Cursors.None;

            this.MouseMove += CaptureWindow2_MouseMove;
            this.MouseDown += CaptureWindow2_MouseDown;
            this.MouseUp += CaptureWindow2_MouseUp;

            DrawBackgroundImage();

            var currentPoint = ScreenTools.GetMousePosition();
            if (App.Current.Settings.CaptureSettings.DetectWindows)
            {
                var window = _windowFinder?.GetWindowThatContainsPoint(currentPoint);
                SelectionRectangle = window.ImageBoundsRect.ToWpfRect();
            }

            DrawCrosshair();
        }

        private void CaptureWindow2_MouseDown(object sender, MouseButtonEventArgs e)
        {
            this.CaptureMouse();
            _dragBegin = ScreenTools.GetMousePosition();
        }

        private void CaptureWindow2_MouseMove(object sender, MouseEventArgs e)
        {
            var currentPoint = ScreenTools.GetMousePosition();

            var newSelectionWindow = SelectionRectangle;

            if (_dragBegin != null && DistancePointToPoint(currentPoint.X, currentPoint.Y, _dragBegin.Value.X, _dragBegin.Value.Y) > _clickDistance)
            {
                var draggingOrigin = _dragBegin.Value;
                var rect = new ScreenRect();
                rect.Left = Math.Min(draggingOrigin.X, currentPoint.X);
                rect.Top = Math.Min(draggingOrigin.Y, currentPoint.Y);
                rect.Width = Math.Abs(draggingOrigin.X - currentPoint.X) + 1;
                rect.Height = Math.Abs(draggingOrigin.Y - currentPoint.Y) + 1;
                newSelectionWindow = rect.ToWpfRect();
            }
            else if (App.Current.Settings.CaptureSettings.DetectWindows)
            {
                var window = _windowFinder?.GetWindowThatContainsPoint(currentPoint);
                newSelectionWindow = window.ImageBoundsRect.ToWpfRect();
            }
            else
            {
                newSelectionWindow = default(WpfRect);
            }

            if (newSelectionWindow != SelectionRectangle)
            {
                SelectionRectangle = newSelectionWindow;
            }

            DrawCrosshair();
        }

        private void CaptureWindow2_MouseUp(object sender, MouseButtonEventArgs e)
        {
            this.ReleaseMouseCapture();

            if (!_dragBegin.HasValue)
                return; // huh??

            var draggingOrigin = _dragBegin.Value;
            var currentMouse = ScreenTools.GetMousePosition();

            // if the mouse hasn't moved far, let's treat it like a click event and find out what window they clicked on
            if (DistancePointToPoint(currentMouse.X, currentMouse.Y, draggingOrigin.X, draggingOrigin.Y) < _clickDistance)
            {
                var window = _windowFinder.GetWindowThatContainsPoint(currentMouse);

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
                if (_windowFinder.BitmapsReady && window.IsPartiallyCovered)
                {
                    var parent = _windowFinder.GetTopLevelWindow(window);
                    if (parent.WindowBitmapWpf != null)
                    {
                        _imagePromotedWindow = parent.WindowBitmapWpf;
                        _boundsPromotedWindow = parent.WindowRect;
                    }
                }
            }

            IsCapturing = false;
            this.Cursor = Cursors.Arrow;

            DrawForegroundImage();
            DrawCrosshair();

            this.MouseMove -= CaptureWindow2_MouseMove;
            this.MouseDown -= CaptureWindow2_MouseDown;
            this.MouseUp -= CaptureWindow2_MouseUp;
        }

        protected override Visual GetVisualChild(int index) => _visuals[index];

        protected override int VisualChildrenCount => _visuals.Count;

        private void DrawForegroundImage()
        {
            var windowBounds = ScreenTools.VirtualScreen.Bounds.ToWpfRect();
            using (var context = _foregroundImage.RenderOpen())
            {
                if (SelectionRectangle != default(WpfRect))
                {
                    var clip = SelectionRectangle;
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
                }
            }
        }

        private void DrawBackgroundImage()
        {
            var windowBounds = ScreenTools.VirtualScreen.Bounds.ToWpfRect();
            using (var context = _backgroundImage.RenderOpen())
            {
                context.DrawImage(_imageGray, windowBounds);
                context.DrawRectangle(_overlayBrush, null, windowBounds);
            }
        }

        private void DrawCrosshair(ScreenPoint? currentCursorLocation = null)
        {
            var currentPoint = currentCursorLocation ?? ScreenTools.GetMousePosition();
            DrawMagnifier(currentPoint);

            using (var context = _crosshair.RenderOpen())
            {
                if (!IsCapturing)
                    return;

                const double crossRadius = 100;
                var bounds = ScreenTools.VirtualScreen.Bounds.ToWpfRect();
                var cursor = currentPoint.ToWpfPoint();
                var offsetHalfPixel = ScreenTools.ScreenToWpf(0.5);
                var x = Math.Min(cursor.X, bounds.Right) + offsetHalfPixel;
                var y = Math.Min(cursor.Y, bounds.Bottom) + offsetHalfPixel;

                context.DrawLine(_sharpWhiteLineDashed, new Point(x, bounds.Top), new Point(x, y - crossRadius));
                context.DrawLine(_sharpWhiteLineDashed, new Point(x, bounds.Bottom), new Point(x, y + crossRadius));
                context.DrawLine(_sharpWhiteLineDashed, new Point(bounds.Left, y), new Point(x - crossRadius, y));
                context.DrawLine(_sharpWhiteLineDashed, new Point(bounds.Right, y), new Point(x + crossRadius, y));
                context.DrawLine(_sharpBlackLineDashed, new Point(x, bounds.Top), new Point(x, y - crossRadius));
                context.DrawLine(_sharpBlackLineDashed, new Point(x, bounds.Bottom), new Point(x, y + crossRadius));
                context.DrawLine(_sharpBlackLineDashed, new Point(bounds.Left, y), new Point(x - crossRadius, y));
                context.DrawLine(_sharpBlackLineDashed, new Point(bounds.Right, y), new Point(x + crossRadius, y));
                context.DrawLine(_sharpAccentLineWide, new Point(x, y - crossRadius), new Point(x, y - crossRadius / 2));
                context.DrawLine(_sharpAccentLineWide, new Point(x, y + crossRadius / 2), new Point(x, y + crossRadius));
                context.DrawLine(_sharpAccentLineWide, new Point(x - crossRadius, y), new Point(x - crossRadius / 2, y));
                context.DrawLine(_sharpAccentLineWide, new Point(x + crossRadius / 2, y), new Point(x + crossRadius, y));
                context.DrawLine(_sharpAccentLine, new Point(x - crossRadius / 2, y), new Point(x + crossRadius / 2, y));
                context.DrawLine(_sharpAccentLine, new Point(x, y - crossRadius / 2), new Point(x, y + crossRadius / 2));
            }
        }

        private void DrawMagnifier(ScreenPoint location)
        {
            using (DrawingContext g = _magnifier.RenderOpen())
            {
                if (_image == null || !IsCapturing)
                    return;

                var currentPointWpf = location.ToWpfPoint();
                var positionTransform = PositionWithinAScreen(_finderSize, currentPointWpf, HorizontalAlignment.Right, VerticalAlignment.Bottom, 20);
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

                var cornerX = (int)location.X - _zoomedPixels / 2;
                var cornerY = (int)location.Y - _zoomedPixels / 2;
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

                var gridLinePixelWidth = Math.Max(ScreenTools.WpfToScreen(App.Current.Settings.MagnifierSettings.GridLineWidth), 1);
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
                var xhairBrush = new SolidColorBrush(App.Current.Settings.MagnifierSettings.CrosshairColor);
                var xhairGrow = gridLineWidth / 2; // make sure the crosshair rectangles cover the adjacent grid lines wholly on both sides
                g.DrawRectangle(xhairBrush, null, new WpfRect(0, (_finderSize.Height - zoomedPixel.Height) / 2, (_finderSize.Width - zoomedPixel.Width) / 2, zoomedPixel.Height).Grow(xhairGrow)); // Left
                g.DrawRectangle(xhairBrush, null, new WpfRect((_finderSize.Width + zoomedPixel.Width) / 2, (_finderSize.Height - zoomedPixel.Height) / 2, (_finderSize.Width - zoomedPixel.Width) / 2, zoomedPixel.Height).Grow(xhairGrow)); // Right
                g.DrawRectangle(xhairBrush, null, new WpfRect((_finderSize.Width - zoomedPixel.Width) / 2, 0, zoomedPixel.Width, (_finderSize.Height - zoomedPixel.Height) / 2).Grow(xhairGrow)); // Top
                g.DrawRectangle(xhairBrush, null, new WpfRect((_finderSize.Width - zoomedPixel.Width) / 2, (_finderSize.Height + zoomedPixel.Height) / 2, zoomedPixel.Width, (_finderSize.Height - zoomedPixel.Height) / 2).Grow(xhairGrow)); // Bottom

                // Draw a highlight around the pixel under cursor
                var innerRect = new WpfRect((_finderSize.Width - zoomedPixel.Width) / 2, (_finderSize.Height - zoomedPixel.Height) / 2, zoomedPixel.Width, zoomedPixel.Height);
                g.DrawRectangle(null, new Pen(Brushes.White, gridLineWidth), innerRect);
                g.DrawRectangle(null, new Pen(Brushes.Black, gridLineWidth), innerRect.Grow(gridLineWidth));
                g.Pop(); // grid line 0.5 px offset

                // Draw the magnifier border
                Pen pen = new Pen(new SolidColorBrush(App.Current.Settings.MagnifierSettings.BorderColor), App.Current.Settings.MagnifierSettings.BorderWidth);
                g.DrawEllipse(null, pen, new Point(_finderSize.Width / 2 + gridOffset, _finderSize.Height / 2 + gridOffset), _finderSize.Width / 2, _finderSize.Height / 2);

                g.Pop(); // the circular clip

                // draw indicator pointer
                const double indSize = 60d;
                const double indBorder = 10d;
                Rect indicatorSquare = Rect.Empty;
                switch (arrow)
                {
                    case ArrowIndicatorPosition.TopLeft:
                        indicatorSquare = new Rect(0, 0, indSize, indSize);
                        break;
                    case ArrowIndicatorPosition.TopRight:
                        indicatorSquare = new Rect(_finderSize.Width - indSize, 0, indSize, indSize);
                        break;
                    case ArrowIndicatorPosition.BottomLeft:
                        indicatorSquare = new Rect(0, _finderSize.Height - indSize, indSize, indSize);
                        break;
                    case ArrowIndicatorPosition.BottomRight:
                        indicatorSquare = new Rect(_finderSize.Width - indSize, _finderSize.Height - indSize, indSize, indSize);
                        break;
                }
                if (!indicatorSquare.IsEmpty)
                {
                    var indicatorGeo = Geometry.Combine(
                        new RectangleGeometry(indicatorSquare),
                        new EllipseGeometry(new Rect(-indBorder, -indBorder, _finderSize.Width + (indBorder * 2), _finderSize.Height + (indBorder * 2))),
                        GeometryCombineMode.Exclude, null);
                    g.DrawGeometry(new SolidColorBrush(Color.FromArgb(180, 135, 135, 135)), null, indicatorGeo);
                }

                this.Width = _finderSize.Width;
                this.Height = _finderSize.Height;
            }
        }

        private double DistancePointToPoint(double x1, double y1, double x2, double y2)
        {
            return Math.Sqrt(Math.Pow((x2 - x1), 2) + Math.Pow((y2 - y1), 2));
        }

        private WpfPoint PositionWithinAScreen(WpfSize objectRect, WpfPoint anchor, HorizontalAlignment horz, VerticalAlignment vert, double distance)
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

            double x = alignCoordinate(horz == HorizontalAlignment.Left ? -1 : horz == HorizontalAlignment.Right ? 1 : 0, anchor.X, objectRect.Width, screen.Left, screen.Right);
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
    }
}
