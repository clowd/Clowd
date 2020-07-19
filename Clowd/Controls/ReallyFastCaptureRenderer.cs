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

        private static void SelectionRectangleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ths = (ReallyFastCaptureRenderer)d;
            ths.DrawForegroundImage();
        }

        Color _highlightColor = Colors.Red;

        ScreenPoint? _dragBegin = null;

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

        public ReallyFastCaptureRenderer()
        {
            _visuals = new VisualCollection(this);

            _backgroundImage = new DrawingVisual();
            _visuals.Add(_backgroundImage);

            _foregroundImage = new DrawingVisual();
            _visuals.Add(_foregroundImage);

            _crosshair = new DrawingVisual();
            _visuals.Add(_crosshair);
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

            this.MouseMove += CaptureWindow2_MouseMove;
            this.MouseDown += CaptureWindow2_MouseDown;
            this.MouseUp += CaptureWindow2_MouseUp;

            var currentPoint = ScreenTools.GetMousePosition();
            if (App.Current.Settings.CaptureSettings.DetectWindows)
            {
                var window = _windowFinder?.GetWindowThatContainsPoint(currentPoint);
                SelectionRectangle = window.ImageBoundsRect.ToWpfRect();
            }

            DrawBackgroundImage();
            DrawForegroundImage();
            DrawCrosshair();
        }

        private void CaptureWindow2_MouseDown(object sender, MouseButtonEventArgs e)
        {
            this.CaptureMouse();
            _dragBegin = ScreenTools.GetMousePosition();
            SelectionRectangle = default(WpfRect);
        }

        private void CaptureWindow2_MouseMove(object sender, MouseEventArgs e)
        {
            var currentPoint = ScreenTools.GetMousePosition();
            if (_dragBegin != null)
            {
                var draggingOrigin = _dragBegin.Value;
                var rect = new ScreenRect();
                rect.Left = Math.Min(draggingOrigin.X, currentPoint.X);
                rect.Top = Math.Min(draggingOrigin.Y, currentPoint.Y);
                rect.Width = Math.Abs(draggingOrigin.X - currentPoint.X) + 1;
                rect.Height = Math.Abs(draggingOrigin.Y - currentPoint.Y) + 1;
                SelectionRectangle = rect.ToWpfRect();
            }
            else if (App.Current.Settings.CaptureSettings.DetectWindows)
            {
                var window = _windowFinder?.GetWindowThatContainsPoint(currentPoint);
                SelectionRectangle = window.ImageBoundsRect.ToWpfRect();
            }
            else
            {
                SelectionRectangle = default(WpfRect);
            }

            DrawForegroundImage();
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
            if (DistancePointToPoint(currentMouse.X, currentMouse.Y, draggingOrigin.X, draggingOrigin.Y) < 10)
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

                SelectionRectangle = window.ImageBoundsRect.ToWpfRect();
            }

            IsCapturing = false;

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
                    context.Pop();
                    context.DrawRectangle(null, new Pen(Brushes.Red, 2), clipRect);
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

        private void DrawCrosshair()
        {
            if (!IsCapturing)
            {
                using (var context = _crosshair.RenderOpen())
                {
                    return;
                }
            }

            var currentPoint = ScreenTools.GetMousePosition();
            var cursor = currentPoint.ToWpfPoint();

            var bounds = ScreenTools.VirtualScreen.Bounds.ToWpfRect();
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
                context.DrawLine(blackPen, new Point(x, bounds.Top), new Point(x, y - crossRadius));
                context.DrawLine(blackPen, new Point(x, bounds.Bottom), new Point(x, y + crossRadius));

                context.DrawLine(whitePen, new Point(x, bounds.Top), new Point(x, y - crossRadius));
                context.DrawLine(whitePen, new Point(x, bounds.Bottom), new Point(x, y + crossRadius));

                context.DrawLine(blackPen, new Point(bounds.Left, y), new Point(x - crossRadius, y));
                context.DrawLine(blackPen, new Point(bounds.Right, y), new Point(x + crossRadius, y));

                context.DrawLine(whitePen, new Point(bounds.Left, y), new Point(x - crossRadius, y));
                context.DrawLine(whitePen, new Point(bounds.Right, y), new Point(x + crossRadius, y));

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

        private double DistancePointToPoint(double x1, double y1, double x2, double y2)
        {
            return Math.Sqrt(Math.Pow((x2 - x1), 2) + Math.Pow((y2 - y1), 2));
        }
    }
}
