using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// The pinned time strip above the rows: major/minor ticks and labels picked by
    /// <see cref="TimelineViewMath.PickTickStepTicks"/> for the current zoom, plus the playhead's
    /// grab triangle (the surface draws the playhead <i>line</i>; the triangle lives here, exactly
    /// as the single-track control drew it). Pressing anywhere scrubs, with the same
    /// pointer-capture idiom as the surface and the same scrub events, which the parent
    /// <see cref="TimelineControl"/> folds into its <c>Position</c> property.
    /// </summary>
    internal sealed class TimelineRuler : Control
    {
        public const double RulerHeight = 24;

        private readonly TimelineViewport _viewport;
        private long _positionTicks;
        private bool _scrubbing;

        public event EventHandler ScrubStarted;

        public event EventHandler<long> Scrubbed;

        public event EventHandler<long> ScrubCompleted;

        public TimelineRuler(TimelineViewport viewport)
        {
            _viewport = viewport;
            _viewport.Changed += (_, _) => InvalidateVisual();
            ActualThemeVariantChanged += (_, _) => InvalidateVisual();
            ClipToBounds = true;
        }

        /// <summary>Playhead position in timeline ticks; the parent control pushes it on every
        /// <c>Position</c> change.</summary>
        public long PositionTicks
        {
            get => _positionTicks;
            set
            {
                if (_positionTicks == value)
                    return;

                _positionTicks = value;
                InvalidateVisual();
            }
        }

        protected override Size MeasureOverride(Size availableSize) => new Size(0, RulerHeight);

        // ------------------------------------------------------------------------- interaction

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _viewport.DurationTicks <= 0)
                return;

            _scrubbing = true;
            e.Pointer.Capture(this);
            ScrubStarted?.Invoke(this, EventArgs.Empty);
            Scrubbed?.Invoke(this, _viewport.XToTicksClamped(e.GetPosition(this).X));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (_scrubbing && Equals(e.Pointer.Captured, this))
                Scrubbed?.Invoke(this, _viewport.XToTicksClamped(e.GetPosition(this).X));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (!_scrubbing || !Equals(e.Pointer.Captured, this))
                return;

            var ticks = _viewport.XToTicksClamped(e.GetPosition(this).X);
            _scrubbing = false; // before Capture(null): it re-enters OnPointerCaptureLost
            e.Pointer.Capture(null);
            ScrubCompleted?.Invoke(this, ticks);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);

            if (!_scrubbing)
                return;

            _scrubbing = false;
            ScrubCompleted?.Invoke(this, Math.Clamp(_positionTicks, 0, Math.Max(0, _viewport.DurationTicks)));
        }

        // --------------------------------------------------------------------------- rendering

        public override void Render(DrawingContext context)
        {
            var palette = TimelinePalette.ForVariant(ActualThemeVariant);
            context.FillRectangle(palette.RulerBackground, new Rect(Bounds.Size));

            var duration = _viewport.DurationTicks;
            var tpp = _viewport.TicksPerPixel;
            if (duration <= 0 || !(tpp > 0) || Bounds.Width <= 0)
                return;

            var step = TimelineViewMath.PickTickStepTicks(tpp);
            if (step > 0)
            {
                // minor ticks at a fifth of the step, drawn only when they get breathing room —
                // the same 5px rule the single-track ruler used.
                var minorStep = step / 5;
                var drawMinor = minorStep > 0 && minorStep / tpp >= 5;
                var inc = drawMinor ? minorStep : step;

                var typeface = new Typeface(FontFamily.Default);
                var start = Math.Max(0, _viewport.ScrollTicks);
                start -= start % inc;
                var end = Math.Min(duration, _viewport.ScrollEndTicks);

                for (var t = start; t <= end; t += inc)
                {
                    var x = _viewport.TickToX(t);
                    var isMajor = t % step == 0;
                    var tickHeight = isMajor ? 6.0 : 3.0;
                    context.DrawLine(isMajor ? palette.TickPen : palette.MinorTickPen,
                        new Point(x, RulerHeight - tickHeight), new Point(x, RulerHeight));

                    if (!isMajor)
                        continue;

                    var text = new FormattedText(TimelineViewMath.FormatTick(t, step),
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, palette.LabelBrush);
                    // centered on the tick; edge labels scroll off naturally (clamping them like
                    // the fixed-width ruler did would make them slide against the ticks here).
                    context.DrawText(text, new Point(x - text.Width / 2, RulerHeight - tickHeight - text.Height - 1));
                }
            }

            RenderPlayheadTriangle(context, palette, duration);
        }

        private void RenderPlayheadTriangle(DrawingContext context, TimelinePalette palette, long duration)
        {
            var x = _viewport.TickToX(Math.Clamp(_positionTicks, 0, duration));
            if (x < -6 || x > Bounds.Width + 6)
                return;

            const double half = 5;
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                gc.BeginFigure(new Point(x - half, RulerHeight - 9), true);
                gc.LineTo(new Point(x + half, RulerHeight - 9));
                gc.LineTo(new Point(x, RulerHeight - 1));
                gc.EndFigure(true);
            }

            context.DrawGeometry(palette.PlayheadPen.Brush, palette.PlayheadOutlinePen, geo);
        }
    }
}
