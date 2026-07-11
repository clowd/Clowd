using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Clowd.UI.Controls
{
    public class ToolButton : ToggleButton
    {
        public static readonly StyledProperty<Geometry> IconPathProperty =
            AvaloniaProperty.Register<ToolButton, Geometry>(nameof(IconPath));

        public Geometry IconPath
        {
            get => GetValue(IconPathProperty);
            set => SetValue(IconPathProperty, value);
        }

        public static readonly StyledProperty<bool> CanToggleProperty =
            AvaloniaProperty.Register<ToolButton, bool>(nameof(CanToggle), false);

        public bool CanToggle
        {
            get => GetValue(CanToggleProperty);
            set => SetValue(CanToggleProperty, value);
        }

        // 0-100. When ShowProgress is set, the icon is replaced by a circular ring filled to this value.
        public static readonly StyledProperty<double> ProgressProperty =
            AvaloniaProperty.Register<ToolButton, double>(nameof(Progress), 0d);

        public double Progress
        {
            get => GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        public static readonly StyledProperty<bool> ShowProgressProperty =
            AvaloniaProperty.Register<ToolButton, bool>(nameof(ShowProgress), false);

        public bool ShowProgress
        {
            get => GetValue(ShowProgressProperty);
            set => SetValue(ShowProgressProperty, value);
        }

        static ToolButton()
        {
            ControlThemes.EnsureRegistered();
        }

        protected override void Toggle()
        {
            if (CanToggle)
                base.Toggle();
        }
    }

    /// <summary>Builds the progress-arc geometry for the <see cref="ToolButton"/> ring (Avalonia 11.3
    /// has no Arc shape). Maps a 0-100 progress value to a stroked arc that sweeps clockwise from 12
    /// o'clock within a 16x16 box, concentric with the ring track ellipse (center 8,8 radius 7).</summary>
    public sealed class ProgressToArcGeometryConverter : IValueConverter
    {
        private const double Radius = 7d;
        private const double Center = 8d;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var progress = Math.Clamp(value is double d ? d : 0d, 0d, 100d);
            // a full 360° arc is degenerate (start == end), so stop just short to keep it visible.
            var sweep = Math.Min(progress * 3.6, 359.999d);

            var start = new Point(Center, Center - Radius);
            var rad = sweep * Math.PI / 180d;
            var end = new Point(Center + Radius * Math.Sin(rad), Center - Radius * Math.Cos(rad));

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(start, false);
                ctx.ArcTo(end, new Size(Radius, Radius), 0, sweep > 180d, SweepDirection.Clockwise);
                ctx.EndFigure(false);
            }

            return geometry;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
