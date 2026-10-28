using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace Prisc.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// A stroked line icon: a path drawn in a 24-unit box (the color dialog's icons are the mockup's
    /// own SVG paths) and scaled to whatever size the control is given. The stroke is in the same
    /// 24-unit space, so it thins with the icon exactly as the SVG's did. Ink comes from the
    /// inherited text foreground, so an icon inside a button takes the button's ink and its states.
    /// </summary>
    public class LineIcon : Control
    {
        public static readonly StyledProperty<Geometry> DataProperty =
            AvaloniaProperty.Register<LineIcon, Geometry>(nameof(Data));

        public Geometry Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        public static readonly StyledProperty<IBrush> ForegroundProperty =
            TextElement.ForegroundProperty.AddOwner<LineIcon>();

        public IBrush Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public static readonly StyledProperty<double> StrokeThicknessProperty =
            AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeThickness), 1.7);

        /// <summary>In the icon's 24-unit space, like the SVG stroke-width it stands in for.</summary>
        public double StrokeThickness
        {
            get => GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        private const double DesignSize = 24;

        static LineIcon()
        {
            AffectsRender<LineIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
            IsHitTestVisibleProperty.OverrideDefaultValue<LineIcon>(false);
        }

        protected override Size MeasureOverride(Size availableSize) => new(
            double.IsNaN(Width) ? 16 : Width,
            double.IsNaN(Height) ? 16 : Height);

        /// <summary>How long <see cref="ShowCopied"/> holds the check.</summary>
        public static readonly TimeSpan CopiedFeedbackDuration = TimeSpan.FromSeconds(1.2);

        private DispatcherTimer _copiedTimer;
        private Geometry _restingData;

        /// <summary>
        /// Swaps this icon for a check for a moment, then back: a copy button's feedback, since
        /// copying never closes the window or changes anything else on screen. A second copy while
        /// the check is up restarts it.
        /// </summary>
        public void ShowCopied()
        {
            if (_copiedTimer == null)
            {
                _copiedTimer = new DispatcherTimer { Interval = CopiedFeedbackDuration };
                _copiedTimer.Tick += (_, _) =>
                {
                    _copiedTimer.Stop();
                    Data = _restingData;
                };
            }

            if (!_copiedTimer.IsEnabled)
                _restingData = Data;

            _copiedTimer.Stop();
            if (this.TryFindResource("ColorDialogIconCheck", out var check) && check is Geometry geometry)
                Data = geometry;
            _copiedTimer.Start();
        }

        public override void Render(DrawingContext context)
        {
            var data = Data;
            var brush = Foreground;
            if (data == null || brush == null || Bounds.Width <= 0 || Bounds.Height <= 0)
                return;

            var scale = Math.Min(Bounds.Width, Bounds.Height) / DesignSize;
            var offset = new Point((Bounds.Width - DesignSize * scale) / 2, (Bounds.Height - DesignSize * scale) / 2);
            var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
                context.DrawGeometry(null, pen, data);
        }
    }
}
