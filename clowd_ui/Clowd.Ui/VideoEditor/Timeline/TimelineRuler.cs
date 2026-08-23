using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// The pinned time strip above the rows, and the one place the playhead is moved by hand:
    /// notches along the top, labels along the bottom, the playhead's head (a rectangle over the
    /// top half, with the surface's line dropping out of it) and — while the pointer is over the
    /// strip — a blue ghost of that head showing where a click would land it. Pressing scrubs, with
    /// the same pointer-capture idiom as the surface and the same scrub events, which the parent
    /// <see cref="TimelineControl"/> folds into its <c>Position</c> property.
    /// </summary>
    internal sealed class TimelineRuler : Control
    {
        public const double RulerHeight = 30;

        /// <summary>The playhead head: a rectangle over the top half of the strip, covering the
        /// notches (never the labels, which sit below it).</summary>
        public const double HeadWidth = 10;

        public const double HeadHeight = 15;

        /// <summary>Rounding on the head's bottom corners only — the top two stay square against
        /// the top of the strip.</summary>
        private const double HeadCornerRadius = 4;

        /// <summary>Clear space kept between two ruler labels; a label that cannot have it is
        /// dropped (its notch stays).</summary>
        private const double LabelGapPx = 8;

        /// <summary>Gap between the total and the end-of-project rule it is pinned against.</summary>
        private const double EndLabelPadPx = 4;

        private readonly TimelineViewport _viewport;
        private long _positionTicks;
        private bool _scrubbing;
        private long _lastScrubTicks;
        private long? _hoverTicks;

        public event EventHandler ScrubStarted;

        public event EventHandler<long> Scrubbed;

        public event EventHandler<long> ScrubCompleted;

        /// <summary>The tick the ghost playhead is sitting on, or null when the pointer left the
        /// strip (or a scrub took over). The parent forwards it to the surface, which runs the
        /// ghost's line on down through the rows.</summary>
        public event EventHandler<long?> HoverTicksChanged;

        public TimelineRuler(TimelineViewport viewport)
        {
            _viewport = viewport;
            _viewport.Changed += (_, _) => InvalidateVisual();
            ActualThemeVariantChanged += (_, _) => InvalidateVisual();
            ClipToBounds = true;
            // no pointer over the strip: the ghost playhead IS the cursor here — it says where the
            // click will land more precisely than an arrow tip ever could, and two markers under
            // the hand at once only fight each other.
            Cursor = new Cursor(StandardCursorType.None);
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
            SetHoverX(Double.NaN); // the real playhead is about to be where the ghost was
            e.Pointer.Capture(this);
            ScrubStarted?.Invoke(this, EventArgs.Empty);
            _lastScrubTicks = _viewport.XToTicksClamped(e.GetPosition(this).X);
            Scrubbed?.Invoke(this, _lastScrubTicks);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            var x = e.GetPosition(this).X;
            if (_scrubbing && Equals(e.Pointer.Captured, this))
            {
                // dedupe: high-polling-rate mice report sub-pixel jitter while "held still", and
                // every scrub event costs a full pipeline flush + container seek downstream.
                var ticks = _viewport.XToTicksClamped(x);
                if (ticks != _lastScrubTicks)
                {
                    _lastScrubTicks = ticks;
                    Scrubbed?.Invoke(this, ticks);
                }

                return;
            }

            SetHoverX(x);
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

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            SetHoverX(Double.NaN);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);

            if (!_scrubbing)
                return;

            _scrubbing = false;
            ScrubCompleted?.Invoke(this, Math.Clamp(_positionTicks, 0, Math.Max(0, _viewport.DurationTicks)));
        }

        /// <summary>Records the hovered instant as the <i>tick</i> a click would produce, not the
        /// raw x: that is what the ghost has to show, and it is also what the surface needs to draw
        /// the same line under its own (identical) viewport mapping.</summary>
        private void SetHoverX(double x)
        {
            long? ticks = Double.IsNaN(x) || _viewport.DurationTicks <= 0
                ? null
                : _viewport.XToTicksClamped(x);

            if (_hoverTicks == ticks)
                return;

            _hoverTicks = ticks;
            HoverTicksChanged?.Invoke(this, ticks);
            InvalidateVisual();
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

            RenderSpeedBands(context, palette, tpp, duration);

            // past the end of the project: the strip stops with the rows under it, and gets the
            // same gutter texture the surface paints there — one dead zone, ruler to bottom row.
            var endX = _viewport.TickToX(duration);
            var endVisible = endX < Bounds.Width;
            if (endVisible)
                palette.DrawBlockGap(context,
                    new Rect(Math.Max(0, endX), 0, Bounds.Width - Math.Max(0, endX), RulerHeight));

            var typeface = new Typeface(FontFamily.Default);

            // the notches step through OUTPUT time and land where the warp puts each instant on
            // the project axis (see TimelineViewMath.BuildRulerMarks); unwarped, this is the plain
            // even ladder it always was.
            var ruler = TimelineViewMath.BuildRulerMarks(_viewport.ScrollTicks, tpp, Bounds.Width,
                duration, _viewport.Warp);

            // the total, pinned against the end rule — the last thing the ruler says, and the one
            // label that never scrolls off while the end is on screen. Laid out first so the
            // regular labels can yield to it rather than overprint it.
            FormattedText endText = null;
            var endLabelLeft = 0.0;
            if (endVisible && endX > 0 && ruler.StepTicks > 0)
            {
                endText = Label(typeface, palette, EndTicks(duration), ruler.StepTicks);
                endLabelLeft = Math.Max(0, endX - endText.Width - EndLabelPadPx);
            }

            if (ruler.StepTicks > 0)
            {
                var lastLabelRight = Double.NegativeInfinity;

                foreach (var mark in ruler.Marks)
                {
                    // notches hang from the top edge; the labels sit on the bottom, under the
                    // band the playhead head occupies.
                    var tickHeight = mark.IsMajor ? 11.0 : 6.0;
                    context.DrawLine(mark.IsMajor ? palette.RulerTickPen : palette.RulerMinorTickPen,
                        new Point(mark.X, 0), new Point(mark.X, tickHeight));

                    if (!mark.IsMajor)
                        continue;

                    var text = Label(typeface, palette, mark.OutputTicks, ruler.StepTicks);
                    // centered on the tick, except at the left edge: the ruler opens on 0:00 and
                    // half a label hanging off the timeline is worse than one sitting a few pixels
                    // right of its own notch.
                    var left = Math.Max(0, mark.X - text.Width / 2);
                    // inside a slowed span the notches can close faster than the text they carry,
                    // and the total owns the right end: the notch still lands on its instant, the
                    // label that cannot fit is dropped.
                    if (left < lastLabelRight + LabelGapPx
                        || (endText != null && left + text.Width + LabelGapPx > endLabelLeft))
                        continue;

                    context.DrawText(text, new Point(left, RulerHeight - text.Height - 2));
                    lastLabelRight = left + text.Width;
                }
            }

            if (endText != null)
            {
                context.DrawLine(palette.ProjectEndPen, new Point(endX, 0), new Point(endX, RulerHeight));
                context.DrawText(endText, new Point(endLabelLeft, RulerHeight - endText.Height - 2));
            }

            // the ghost first, so the playhead stays legible when the two overlap
            if (_hoverTicks is long hover && !_scrubbing)
                RenderHead(context, _viewport.TickToX(hover), palette.HoverPlayheadBrush, palette.HoverPlayheadPen);

            var px = _viewport.TickToX(Math.Clamp(_positionTicks, 0, duration));
            RenderHead(context, px, palette.PlayheadPen.Brush, palette.PlayheadPen);
        }

        /// <summary>The end of the project on the ruler's own clock: the total the transport
        /// readout shows, so the two cannot disagree.</summary>
        private long EndTicks(long durationTicks) =>
            _viewport.Warp is { IsIdentity: false } warp ? warp.ToOutput(durationTicks) : durationTicks;

        private static FormattedText Label(Typeface typeface, TimelinePalette palette, long ticks, long stepTicks) =>
            new FormattedText(TimelineViewMath.FormatTick(ticks, stepTicks), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 12, palette.RulerLabelBrush);

        /// <summary>
        /// The wash over each warped stretch: warm where the video runs faster than the footage,
        /// cool where it runs slower, and nothing at all over unwarped time. A constant-factor span
        /// is one flat color between hard edges; a transition ramp is a gradient between the speeds
        /// at its two ends, so a speed change eased in over half a second looks eased and one cut
        /// straight in looks cut.
        /// </summary>
        private void RenderSpeedBands(DrawingContext context, TimelinePalette palette, double ticksPerPixel,
            long durationTicks)
        {
            var bands = TimelineViewMath.BuildSpeedBands(_viewport.ScrollTicks, ticksPerPixel,
                Bounds.Width, durationTicks, _viewport.Warp);

            foreach (var band in bands)
            {
                var rect = new Rect(band.X0, 0, Math.Max(0, band.X1 - band.X0), RulerHeight);
                var from = palette.SpeedTint(band.SpeedStart);
                var to = palette.SpeedTint(band.SpeedEnd);

                context.FillRectangle(from == to
                    ? new SolidColorBrush(from)
                    : new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(from, 0),
                            new GradientStop(to, 1),
                        },
                    }, rect);
            }
        }

        /// <summary>The playhead marker: a <see cref="HeadWidth"/> x <see cref="HeadHeight"/> block
        /// in the top half of the strip with a line dropping from it to the rows below (the surface
        /// carries that line on down). Every edge is snapped to whole device pixels — see
        /// <see cref="TimelineViewMath.SnapToPixel"/> for why a hairline that is not costs the
        /// playhead its color.</summary>
        private void RenderHead(DrawingContext context, double x, IBrush fill, IPen linePen)
        {
            if (x < -HeadWidth || x > Bounds.Width + HeadWidth)
                return;

            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            var left = TimelineViewMath.SnapToPixel(x - HeadWidth / 2, scaling);
            var right = TimelineViewMath.SnapToPixel(x + HeadWidth / 2, scaling);
            var bottom = TimelineViewMath.SnapToPixel(HeadHeight, scaling);

            context.DrawRectangle(fill, null,
                new RoundedRect(new Rect(left, 0, right - left, bottom), 0, 0,
                    HeadCornerRadius, HeadCornerRadius));

            var lineX = TimelineViewMath.SnapToPixel(x, scaling, linePen.Thickness);
            context.DrawLine(linePen, new Point(lineX, bottom),
                new Point(lineX, TimelineViewMath.SnapToPixel(RulerHeight, scaling)));
        }
    }
}
