using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clowd.Controls
{
    public class DPadControl : Border
    {
        public Brush Foreground
        {
            get { return (Brush)GetValue(ForegroundProperty); }
            set { SetValue(ForegroundProperty, value); }
        }

        public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(DPadControl), new PropertyMetadata(Brushes.White));

        public Brush HoverBrush
        {
            get { return (Brush)GetValue(HoverBrushProperty); }
            set { SetValue(HoverBrushProperty, value); }
        }

        public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.Register(nameof(HoverBrush), typeof(Brush), typeof(DPadControl), new PropertyMetadata(Brushes.White));

        public DPadControl()
        {
            this.Cursor = Cursors.Hand;
        }

        protected override void OnRender(DrawingContext ctx)
        {
            ctx.DrawRectangle(BorderBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

            var contentRect = new Rect(
                BorderThickness.Left,
                BorderThickness.Top,
                ActualWidth - BorderThickness.Left - BorderThickness.Right,
                ActualHeight - BorderThickness.Top - BorderThickness.Bottom);

            ctx.DrawRectangle(Background, null, contentRect);

            var buttonRect = new Rect(
                contentRect.Left + BorderThickness.Left,
                contentRect.Top + BorderThickness.Top,
                contentRect.Width - BorderThickness.Left - BorderThickness.Right,
                contentRect.Height - BorderThickness.Top - BorderThickness.Bottom);

            var centerX = buttonRect.X + buttonRect.Width / 2;
            var centerY = buttonRect.Y + buttonRect.Height / 2;

            DrawTriangle(ctx, BorderBrush, buttonRect.X, buttonRect.Y + 1, buttonRect.X, buttonRect.Bottom - 1, centerX - 1, centerY, DPadButton.Left); // D-LEFT
            DrawTriangle(ctx, BorderBrush, buttonRect.X + 1, buttonRect.Y, buttonRect.Right - 1, buttonRect.Y, centerX, centerY - 1, DPadButton.Top); // D-TOP
            DrawTriangle(ctx, BorderBrush, buttonRect.Right, buttonRect.Y + 1, buttonRect.Right, buttonRect.Bottom - 1, centerX + 1, centerY, DPadButton.Right); // D-RIGHT
            DrawTriangle(ctx, BorderBrush, buttonRect.X + 1, buttonRect.Bottom, buttonRect.Right - 1, buttonRect.Bottom, centerX, centerY + 1, DPadButton.Bottom); // D-BOTTOM

            const double sizecst = 1.5d;

            DrawTriangle(ctx, Foreground, buttonRect.X + 1, centerY, centerX / sizecst - 1, centerY / sizecst + 1, centerX / sizecst - 1, centerY / sizecst * 2 - 1, null);
            DrawTriangle(ctx, Foreground, buttonRect.X + 1, centerY, centerX / sizecst - 1, centerY / sizecst + 1, centerX / sizecst - 1, centerY / sizecst * 2 - 1, null, new RotateTransform(90, centerX, centerY));
            DrawTriangle(ctx, Foreground, buttonRect.X + 1, centerY, centerX / sizecst - 1, centerY / sizecst + 1, centerX / sizecst - 1, centerY / sizecst * 2 - 1, null, new RotateTransform(180, centerX, centerY));
            DrawTriangle(ctx, Foreground, buttonRect.X + 1, centerY, centerX / sizecst - 1, centerY / sizecst + 1, centerX / sizecst - 1, centerY / sizecst * 2 - 1, null, new RotateTransform(270, centerX, centerY));

            Console.WriteLine();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            this.InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            this.InvalidateVisual();
        }

        private void DrawTriangle(DrawingContext ctx, Brush brush, double p1x, double p1y, double p2x, double p2y, double p3x, double p3y, DPadButton? btn, RotateTransform transform = null)
        {
            if (transform != null)
                ctx.PushTransform(transform);

            var geometry = new PathGeometry(new[] { new PathFigure(new Point(p1x, p1y), new[] { new LineSegment(new Point(p2x, p2y), true), new LineSegment(new Point(p3x, p3y), true) }, true) });
            var mouseOver = btn.HasValue && geometry.FillContains(Mouse.GetPosition(this));

            ctx.DrawGeometry(brush, null, geometry);

            if (mouseOver)
                ctx.DrawGeometry(HoverBrush, null, geometry);

            if (transform != null)
                ctx.Pop();
        }
    }

    public enum DPadButton
    {
        Left = 1,
        Top,
        Right,
        Bottom,
    }
}
