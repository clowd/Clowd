using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clowd.Controls
{
    [PropertyChanged.ImplementPropertyChanged]
    public class ZoomBorderControl : Border
    {
        public bool Panning { get { return panning; } }
        public bool IsChildContained { get; private set; }

        public Point ContentOffset
        {
            get { return (Point)GetValue(ContentOffsetProperty); }
            set { SetValue(ContentOffsetProperty, value); }
        }
        public static readonly DependencyProperty ContentOffsetProperty =
            DependencyProperty.Register("ContentOffset", typeof(Point), typeof(ZoomBorderControl),
                new PropertyMetadata(new Point(0, 0), ContentOffsetChanged));

        public double ContentScale
        {
            get { return (double)GetValue(ContentScaleProperty); }
            set { SetValue(ContentScaleProperty, value); }
        }
        public static readonly DependencyProperty ContentScaleProperty =
            DependencyProperty.Register("ContentScale", typeof(double), typeof(ZoomBorderControl),
                new PropertyMetadata(1d, ContentScaleChanged));

        private static void ContentScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var me = (ZoomBorderControl)d;
            if (me.child != null)
            {
                var st = me.GetScaleTransform(me.child);
                var scale = (double)e.NewValue;
                st.ScaleX = scale;
                st.ScaleY = scale;

                UpdateIsContained(d);
            }
        }
        private static void ContentOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var me = (ZoomBorderControl)d;
            if (me.child != null)
            {
                var tt = me.GetTranslateTransform(me.child);
                var pt = (Point)e.NewValue;
                tt.X = pt.X;
                tt.Y = pt.Y;

                UpdateIsContained(d);
            }
        }
        private static void UpdateIsContained(DependencyObject d)
        {
            var me = (ZoomBorderControl)d;
            var crect = me.GetRenderedContentRect();
            bool contained = crect.X >= 0 && crect.Y >= 0 &&
                (crect.X + crect.Width) <= me.ActualWidth && (crect.Y + crect.Height) <= me.ActualHeight;
            if (contained != me.IsChildContained)
            {
                me.IsChildContained = contained;
            }
        }

        private UIElement child = null;
        private Point origin;
        private Point start;
        private bool panning = false;

        private TranslateTransform GetTranslateTransform(UIElement element)
        {
            return (TranslateTransform)((TransformGroup)element.RenderTransform)
              .Children.First(tr => tr is TranslateTransform);
        }

        private ScaleTransform GetScaleTransform(UIElement element)
        {
            return (ScaleTransform)((TransformGroup)element.RenderTransform)
              .Children.First(tr => tr is ScaleTransform);
        }

        public override UIElement Child
        {
            get { return base.Child; }
            set
            {
                if (value != null && value != this.Child)
                    this.Initialize(value);
                base.Child = value;
            }
        }

        public void Initialize(UIElement element)
        {
            this.child = element;
            if (child != null)
            {
                TransformGroup group = new TransformGroup();
                ScaleTransform st = new ScaleTransform();
                group.Children.Add(st);
                TranslateTransform tt = new TranslateTransform();
                group.Children.Add(tt);
                child.RenderTransform = group;
                child.RenderTransformOrigin = new Point(0.0, 0.0);
                this.MouseWheel += child_MouseWheel;
                this.MouseMove += ZoomBorderControl_MouseMove;
                UpdateIsContained(this);
            }
        }

        public Rect GetRenderedContentRect()
        {
            var me = this;
            var cwidth = (double)me.child.GetValue(FrameworkElement.ActualWidthProperty) * ContentScale;
            var cheight = (double)me.child.GetValue(FrameworkElement.ActualHeightProperty) * ContentScale;
            var cx = me.ContentOffset.X;
            var cy = me.ContentOffset.Y;
            return new Rect(cx, cy, cwidth, cheight);
        }

        public Rect GetActualContentRect()
        {
            var me = this;
            var cwidth = (double)me.child.GetValue(FrameworkElement.ActualWidthProperty);
            var cheight = (double)me.child.GetValue(FrameworkElement.ActualHeightProperty);
            var cx = 0;
            var cy = 0;
            return new Rect(cx, cy, cwidth, cheight);
        }

        private void ZoomBorderControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (child != null)
            {
                if (child.IsMouseCaptured)
                {
                    //var tt = GetTranslateTransform(child);
                    Vector v = start - e.GetPosition(this);
                    ContentOffset = new Point(origin.X - v.X, origin.Y - v.Y);
                    //tt.X = origin.X - v.X;
                    //tt.Y = origin.Y - v.Y;
                }
            }
        }

        public void StartPanning()
        {
            if (child != null)
            {
                panning = true;
                var tt = GetTranslateTransform(child);
                start = Mouse.GetPosition(this);
                origin = new Point(tt.X, tt.Y);
                this.Cursor = Cursors.SizeAll;
                child.CaptureMouse();
            }
        }
        public void StopPanning()
        {
            if (child != null)
            {
                panning = false;
                child.ReleaseMouseCapture();
                this.Cursor = Cursors.Arrow;
            }
        }
        public void Reset()
        {
            if (child != null)
            {
                // reset zoom
                var st = GetScaleTransform(child);
                st.ScaleX = 1.0;
                st.ScaleY = 1.0;

                // reset pan
                var tt = GetTranslateTransform(child);
                tt.X = 0.0;
                tt.Y = 0.0;
            }
        }
        private void child_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (child != null && !panning)
            {
                var st = GetScaleTransform(child);
                var tt = GetTranslateTransform(child);

                double zoom = e.Delta > 0 ? .2 : -.2;
                if (!(e.Delta > 0) && (st.ScaleX < .3 || st.ScaleY < .3))
                    return;
                if (e.Delta > 0 && (st.ScaleX > 2.9 || st.ScaleY > 2.9))
                    return;

                Point relative = e.GetPosition(child);
                double abosuluteX;
                double abosuluteY;

                abosuluteX = relative.X * st.ScaleX + tt.X;
                abosuluteY = relative.Y * st.ScaleY + tt.Y;

                ContentScale += zoom;
                ContentOffset = new Point(abosuluteX - relative.X * st.ScaleX, abosuluteY - relative.Y * st.ScaleY);

                //st.ScaleX += zoom;
                //st.ScaleY += zoom;

                //tt.X = abosuluteX - relative.X * st.ScaleX;
                //tt.Y = abosuluteY - relative.Y * st.ScaleY;
            }
        }
    }
}