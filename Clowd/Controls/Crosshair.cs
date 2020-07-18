using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ScreenVersusWpf;

namespace Clowd.Controls
{
    public class Crosshair : FrameworkElement
    {
        public Color AccentColor
        {
            get { return (Color)GetValue(AccentColorProperty); }
            set { SetValue(AccentColorProperty, value); }
        }

        public static readonly DependencyProperty AccentColorProperty =
            DependencyProperty.Register("AccentColor", typeof(Color), typeof(Crosshair), new PropertyMetadata(default(Color)));

        public double DashLength
        {
            get { return (double)GetValue(DashLengthProperty); }
            set { SetValue(DashLengthProperty, value); }
        }

        public static readonly DependencyProperty DashLengthProperty =
            DependencyProperty.Register("DashLength", typeof(double), typeof(Crosshair), new PropertyMetadata(8d));


        protected override int VisualChildrenCount => 1;

        private DrawingVisual _visual;
        private double _zoom;

        public Crosshair()
        {
            _visual = new DrawingVisual();
            AddVisualChild(_visual);
            this.Cursor = Cursors.None;
            this.IsHitTestVisible = true;
            this.ClipToBounds = true;

            var sys = Mouse.GetPosition(this);
            Draw(sys);

            this.PreviewMouseMove += OnPreviewMouseMove;
        }

        public void ForceRender()
        {
            var currentPoint = ScreenTools.GetMousePosition();
            var currentPointWpf = currentPoint.ToWpfPoint();
            Draw(currentPointWpf, new WpfRect(0, 0, this.ActualWidth, this.ActualHeight));
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            ForceRender();
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            return new PointHitTestResult(this, hitTestParameters.HitPoint);
        }

        protected override GeometryHitTestResult HitTestCore(GeometryHitTestParameters hitTestParameters)
        {
            return new GeometryHitTestResult(this, IntersectionDetail.FullyContains);
        }

        private void Draw(Point sys)
        {
            Draw(new WpfPoint(sys.X, sys.Y), new WpfRect(0, 0, this.ActualWidth, this.ActualHeight));
        }

        private void Draw(WpfPoint cursor, WpfRect bounds)
        {
            const double crossRadius = 120;
            const double handleRadius = 3;

            var x = Math.Min(cursor.X, bounds.Right);
            var y = Math.Min(cursor.Y, bounds.Bottom);

            var whitePen = new Pen(new SolidColorBrush(Color.FromArgb(127, 255, 255, 255)), 1);
            whitePen.DashStyle = new DashStyle(new double[] { DashLength, DashLength }, 0);

            var blackPen = new Pen(new SolidColorBrush(Color.FromArgb(127, 0, 0, 0)), 1);
            blackPen.DashStyle = new DashStyle(new double[] { DashLength, DashLength }, DashLength);

            var accentBrush = new SolidColorBrush(Color.FromArgb(255, AccentColor.R, AccentColor.G, AccentColor.B));
            var accentPen = new Pen(accentBrush, 1.5);

            using (var context = _visual.RenderOpen())
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

        protected override Visual GetVisualChild(int index)
        {
            return _visual;
        }
    }
}
