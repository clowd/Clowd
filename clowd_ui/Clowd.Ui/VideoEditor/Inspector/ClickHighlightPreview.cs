using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Clowd.VideoSDK.Composition;

namespace Clowd.UI.VideoEditor.Inspector
{
    /// <summary>
    /// A looping preview of one click-highlight animation, drawn from <see cref="ClickHighlight"/> —
    /// the very numbers the compositor draws the recorded clicks with, so the tile plays what the
    /// render will play. The loop is the whole click: the held dot arrives, sits there as it would
    /// under a held button, bursts on the release, then a beat of nothing before it comes round
    /// again.
    /// </summary>
    /// <remarks>
    /// Sized in DIP against the widest frame the current settings produce, so the animation exactly
    /// fills the control at any size dial — the three tiles then share one scale and their
    /// differences are the animation's, not the layout's. Only the moving phases repaint; the held
    /// beat and the blank one cost a single frame each. The <c>none</c> tile never starts a clock
    /// and never draws.
    /// </remarks>
    public sealed class ClickHighlightPreview : Control
    {
        /// <summary>How long the held dot takes to arrive. The compositor has no such ramp — a
        /// held button is simply down — but a dot that materializes out of nothing reads as an
        /// accident, and this reads as a press.</summary>
        private const double PressInMs = 150.0;

        /// <summary>How long the dot then sits still, standing in for the button being held.</summary>
        private const double HoldMs = 600.0;

        /// <summary>The blank beat after the burst. Long enough that a cycle reads as one click
        /// rather than a pulsing blob.</summary>
        private const double GapMs = 700.0;

        private const double PressEndMs = PressInMs + HoldMs;

        /// <summary>The burst's length at the item's own animation speed, and the two marks that
        /// follow from it. Instance state, unlike the beats around it: the speed dial is the one
        /// part of the cycle the user can change.</summary>
        private double BurstMs => ClickHighlight.DurationMsAt(AnimationSpeed);

        private double BurstEndMs => PressEndMs + BurstMs;

        private double CycleMs => BurstEndMs + GapMs;

        /// <summary>~30fps while animating. The circle is a few dozen pixels wide; the cost is the
        /// invalidation, not the drawing, and this is the rate the preview player uses too.</summary>
        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(33);

        /// <summary>Where in the cycle a moment falls. Only <see cref="Press"/> and
        /// <see cref="Burst"/> move, which is what decides whether a tick repaints.</summary>
        private enum Phase
        {
            Press,
            Hold,
            Burst,
            Blank,
        }

        /// <summary>The <c>CursorContent.ClickAnimation</c> wire name this tile previews.</summary>
        public static readonly StyledProperty<string> AnimationProperty =
            AvaloniaProperty.Register<ClickHighlightPreview, string>(nameof(Animation));

        /// <summary>The highlight color, packed ARGB — the selected cursor row's own
        /// <c>ClickColor</c>, so the preview is the color that will be drawn.</summary>
        public static readonly StyledProperty<uint> ColorArgbProperty =
            AvaloniaProperty.Register<ClickHighlightPreview, uint>(nameof(ColorArgb),
                SelectedItemViewModel.DefaultCursorClickColor);

        /// <summary>The item's <c>HoldSize</c>: the held dot previews at the size it will be
        /// drawn.</summary>
        public static readonly StyledProperty<double> HoldSizeProperty =
            AvaloniaProperty.Register<ClickHighlightPreview, double>(nameof(HoldSize),
                SelectedItemViewModel.DefaultHighlightFactor);

        /// <summary>The item's <c>ClickSize</c>.</summary>
        public static readonly StyledProperty<double> ClickSizeProperty =
            AvaloniaProperty.Register<ClickHighlightPreview, double>(nameof(ClickSize),
                SelectedItemViewModel.DefaultHighlightFactor);

        /// <summary>The item's <c>AnimationSpeed</c>. It shortens the burst but not the beats
        /// around it — the cycle is a demonstration, not a recording, and a preview that also sped
        /// up its own pauses would just look erratic.</summary>
        public static readonly StyledProperty<double> AnimationSpeedProperty =
            AvaloniaProperty.Register<ClickHighlightPreview, double>(nameof(AnimationSpeed),
                SelectedItemViewModel.DefaultHighlightFactor);

        /// <summary>The item's <c>FillOpacity</c> — the ring tile's inner disc previews at the
        /// opacity it will be drawn.</summary>
        public static readonly StyledProperty<double> FillOpacityProperty =
            AvaloniaProperty.Register<ClickHighlightPreview, double>(nameof(FillOpacity),
                SelectedItemViewModel.DefaultCursorFillOpacity);

        static ClickHighlightPreview()
        {
            AffectsRender<ClickHighlightPreview>(ColorArgbProperty, HoldSizeProperty,
                ClickSizeProperty, AnimationSpeedProperty, FillOpacityProperty);
        }

        private readonly Stopwatch _clock = new Stopwatch();
        private DispatcherTimer _timer;
        private Phase _phase;
        private bool _attached;

        public string Animation
        {
            get => GetValue(AnimationProperty);
            set => SetValue(AnimationProperty, value);
        }

        public uint ColorArgb
        {
            get => GetValue(ColorArgbProperty);
            set => SetValue(ColorArgbProperty, value);
        }

        public double HoldSize
        {
            get => GetValue(HoldSizeProperty);
            set => SetValue(HoldSizeProperty, value);
        }

        public double ClickSize
        {
            get => GetValue(ClickSizeProperty);
            set => SetValue(ClickSizeProperty, value);
        }

        public double AnimationSpeed
        {
            get => GetValue(AnimationSpeedProperty);
            set => SetValue(AnimationSpeedProperty, value);
        }

        public double FillOpacity
        {
            get => GetValue(FillOpacityProperty);
            set => SetValue(FillOpacityProperty, value);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            UpdateClock();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _attached = false;
            Stop();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == AnimationProperty)
            {
                UpdateClock();
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            var mode = ClickHighlight.ModeOf(Animation);
            if (!_clock.IsRunning || mode == HighlightMode.None)
                return;

            double extent = Math.Min(Bounds.Width, Bounds.Height) / 2;
            if (extent <= 0)
                return;

            double at = _clock.Elapsed.TotalMilliseconds % CycleMs;
            var phase = PhaseAt(at);

            if (mode == HighlightMode.Ring)
            {
                RenderRing(context, extent, at, phase);
                return;
            }
            if (mode == HighlightMode.Press)
            {
                RenderPress(context, extent, at, phase);
                return;
            }

            bool pulse = mode == HighlightMode.Pulse;
            double heldDip = ClickHighlight.HeldRadiusDip(HoldSize);
            double radiusDip, opacity;
            switch (phase)
            {
                case Phase.Press:
                    // the dot swells into its held size; opacity is already the held one, so what
                    // the eye reads is a press landing, not a fade
                    double arriving = at / PressInMs;
                    radiusDip = heldDip * (1 - (1 - arriving) * (1 - arriving));
                    opacity = ClickHighlight.MaxOpacity;
                    break;

                case Phase.Hold:
                    // exactly what the compositor draws under a held button
                    radiusDip = heldDip;
                    opacity = ClickHighlight.MaxOpacity;
                    break;

                case Phase.Burst:
                    double progress = (at - PressEndMs) / BurstMs;
                    radiusDip = ClickHighlight.RadiusDip(progress, pulse, ClickSize);
                    opacity = ClickHighlight.Opacity(progress);
                    break;

                default:
                    return; // the blank beat between cycles
            }

            var color = Color.FromUInt32(ColorArgb);
            byte alpha = (byte)Math.Clamp(Math.Round(opacity * color.A), 0, 255);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));

            // normalized against the widest thing this cycle will draw rather than a fixed 40 DIP:
            // the tile then holds the whole animation at any size setting, and the held dot keeps
            // its true proportion to the burst instead of both being clipped
            double widestDip = Math.Max(ClickHighlight.RadiusEndDip * ClickHighlight.Factor(ClickSize), heldDip);
            double radius = radiusDip / widestDip * extent;
            context.DrawEllipse(brush, null,
                new Point(Bounds.Width / 2, Bounds.Height / 2), radius, radius);
        }

        /// <summary>The press/ring clocks the cycle's phase stands in for, in the convention of
        /// <see cref="ClickHighlight.RingScale"/>: the press phase is a fresh hold, the held beat
        /// a long one, the burst a release of a full press, and the blank beat plain idleness.</summary>
        private (double? SinceDown, double? SinceUp, double? PressDuration) ClocksAt(double at, Phase phase)
            => phase switch
            {
                Phase.Press => (at, null, null),
                Phase.Hold => (null, null, null),
                Phase.Burst => (null, at - PressEndMs, null),
                _ => (null, double.MaxValue, null),
            };

        /// <summary>The ring tile: the resting ring sits there between cycles (unlike the burst
        /// animations, the ring is always on screen), closes over the press, holds closed, and
        /// springs back out — the compositor's own numbers throughout.</summary>
        private void RenderRing(DrawingContext context, double extent, double at, Phase phase)
        {
            var (down, up, duration) = ClocksAt(at, phase);
            double scale = ClickHighlight.RingScale(down, up, duration, AnimationSpeed);

            // normalized against the overshoot's peak plus the stroke, so the breathe-out
            // never clips against the tile's edge
            double sizeDial = ClickHighlight.Factor(ClickSize);
            double widestDip = ClickHighlight.RingRadiusDip * sizeDial * 1.06 + ClickHighlight.RingStrokeDip;
            double radius = ClickHighlight.RingRadiusDip * sizeDial * scale / widestDip * extent;
            double stroke = Math.Max(1.0, ClickHighlight.RingStrokeDip / widestDip * extent);

            var color = Color.FromUInt32(ColorArgb);
            byte fillAlpha = (byte)Math.Clamp(
                Math.Round(ClickHighlight.Clamp01(FillOpacity) * color.A), 0, 255);
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            context.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(fillAlpha, color.R, color.G, color.B)),
                new Pen(new SolidColorBrush(color), stroke),
                center, radius, radius);
        }

        /// <summary>The press tile: the warp cannot be previewed literally in a 30px vector tile,
        /// so it is drawn schematically — concentric rings pulled toward the center by the same
        /// press envelope the compositor warps with, exaggerated to read at tile size. Color is
        /// deliberately neutral: the press has no color of its own.</summary>
        private void RenderPress(DrawingContext context, double extent, double at, Phase phase)
        {
            var (down, up, duration) = ClocksAt(at, phase);
            double envelope = ClickHighlight.PressAmount(down, up, duration, AnimationSpeed);
            double amount = 2 * ClickHighlight.PressMaxAmount * envelope;

            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(220, 144, 144, 144)), 1.5);
            foreach (double fraction in RingFractions)
            {
                // where content originally at this radius lands once the warp samples outward:
                // the display-space inverse of the shader's 1 + amount·(1−t)² stretch
                double pull = 1 - fraction;
                double shown = fraction * extent / (1 + amount * pull * pull);
                context.DrawEllipse(null, pen, center, shown, shown);
            }
        }

        private static readonly double[] RingFractions = { 0.35, 0.60, 0.85 };

        private Phase PhaseAt(double at)
        {
            if (at < PressInMs)
                return Phase.Press;
            if (at < PressEndMs)
                return Phase.Hold;
            return at < BurstEndMs ? Phase.Burst : Phase.Blank;
        }

        /// <summary>Runs the clock exactly while there is an animation to run and the tile is on
        /// screen; a <c>none</c> tile (or a detached one) costs nothing.</summary>
        private void UpdateClock()
        {
            bool wanted = _attached && ClickHighlight.TryParse(Animation, out _);
            if (wanted == _clock.IsRunning)
                return;

            if (!wanted)
            {
                Stop();
                return;
            }

            _clock.Restart();
            _phase = Phase.Press;
            _timer ??= new DispatcherTimer(Tick, DispatcherPriority.Background, (_, _) => OnTick());
            _timer.Start();
        }

        private void Stop()
        {
            _timer?.Stop();
            _clock.Reset();
            _phase = Phase.Blank;
        }

        /// <summary>Repaints while the dot is moving, plus the one frame that enters each still
        /// phase — the held beat and the blank one do not change, so they cost one invalidation
        /// each rather than thirty.</summary>
        private void OnTick()
        {
            var phase = PhaseAt(_clock.Elapsed.TotalMilliseconds % CycleMs);
            bool moving = phase is Phase.Press or Phase.Burst;
            if (!moving && phase == _phase)
                return;

            _phase = phase;
            InvalidateVisual();
        }
    }
}
