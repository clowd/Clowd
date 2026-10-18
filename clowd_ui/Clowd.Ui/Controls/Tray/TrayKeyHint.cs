using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// A tile's accelerator key, drawn small in its corner (<see cref="TrayButton.AccessKey"/>) and only
    /// while <see cref="IsShown"/>: it flies up into the tile from below its bottom edge, overshooting a
    /// little before it settles, and drops back off that edge when hidden (issue #110). It only ever
    /// moves, never fades or changes size: the template clips it to the tile, which is what makes it
    /// appear from and vanish past the edge. <see cref="Delay"/> holds the start back, so the tiles of a
    /// strip can ripple rather than land at once.
    /// <para>
    /// The motion is code rather than theme transitions because each tile's delay is its own, and a
    /// transition in a template cannot be bound. Whatever an animation leaves behind is written back as
    /// the local value when it finishes, so cancelling one half-way (the strip disengaged mid-flight) never
    /// snaps a hint to a state it was not heading for. Skipped, like the tray's other motion, with Windows'
    /// animation effects off.
    /// </para>
    /// </summary>
    public sealed class TrayKeyHint : TextBlock
    {
        public static readonly StyledProperty<bool> IsShownProperty =
            AvaloniaProperty.Register<TrayKeyHint, bool>(nameof(IsShown));

        public static readonly StyledProperty<TimeSpan> DelayProperty =
            AvaloniaProperty.Register<TrayKeyHint, TimeSpan>(nameof(Delay));

        /// <summary>How far below its resting place the hint is drawn: <see cref="TrayTokens.KeyHintTravel"/>
        /// when hidden (just past the tile's bottom edge, where the template's clip hides it), 0 when shown.
        /// A plain double the animations drive, mirrored into the translate: Avalonia does not animate a
        /// <c>RenderTransform</c> between transform operations here (the run faults and moves nothing).</summary>
        private static readonly StyledProperty<double> DropProperty =
            AvaloniaProperty.Register<TrayKeyHint, double>("Drop");

        private readonly TranslateTransform _shift = new();

        // the mockup's cubic-bezier(.2,.8,.3,1.5): the rise overshoots and settles. Leaving falls away
        // faster and faster, off the edge.
        private static readonly Easing FlyIn = new CubicBezierEasing(0.2, 0.8, 0.3, 1.5);
        private static readonly Easing FlyOut = new CubicEaseIn();

        private CancellationTokenSource _run;

        static TrayKeyHint()
        {
            IsHitTestVisibleProperty.OverrideDefaultValue<TrayKeyHint>(false);
            IsShownProperty.Changed.AddClassHandler<TrayKeyHint>((hint, _) => hint.Play());
            DropProperty.Changed.AddClassHandler<TrayKeyHint>((hint, _) => hint._shift.Y = hint.GetValue(DropProperty));
        }

        public TrayKeyHint()
        {
            FontSize = TrayTokens.KeyHintFontSize;
            FontWeight = FontWeight.SemiBold;
            LineHeight = TrayTokens.KeyHintFontSize;
            Foreground = TrayTokens.KeyHintBrush;
            Margin = TrayTokens.KeyHintMargin;
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            RenderTransform = _shift;
            // a tight dark halo keeps the letter apart from the glyph corner it sits over, and legible
            // on the accent fill of a lit tile
            Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 0, BlurRadius = 3, Color = Colors.Black, Opacity = 0.75 };
            Snap(false);
        }

        public bool IsShown
        {
            get => GetValue(IsShownProperty);
            set => SetValue(IsShownProperty, value);
        }

        public TimeSpan Delay
        {
            get => GetValue(DelayProperty);
            set => SetValue(DelayProperty, value);
        }

        /// <summary>A strip that opens engaged shows its hints arriving, not already there.</summary>
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (IsShown)
                Play();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _run?.Cancel();
            _run = null;
            Snap(IsShown);
        }

        private void Play()
        {
            _run?.Cancel();
            _run = null;

            var shown = IsShown;
            if (VisualRoot == null || !FloatingTray.AnimationsEnabled())
            {
                Snap(shown);
                return;
            }

            _run = new CancellationTokenSource();
            _ = RunAsync(shown, _run.Token);
        }

        private async Task RunAsync(bool shown, CancellationToken token)
        {
            // the fly-away ripples at half the step, so a disengaging strip clears quickly
            var delay = shown ? Delay : Delay / 2;
            var (from, to) = shown ? (TrayTokens.KeyHintTravel, 0d) : (0d, TrayTokens.KeyHintTravel);

            // FillMode.Both: the start position holds through the delay, so a hint waiting its turn is
            // already where it will start from; the end holds until the local value takes over (Snap)
            var fly = new Animation
            {
                Duration = shown ? TrayTokens.KeyHintIn : TrayTokens.KeyHintOut,
                Delay = delay,
                Easing = shown ? FlyIn : FlyOut,
                FillMode = FillMode.Both,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(DropProperty, from) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(DropProperty, to) } },
                },
            };

            try
            {
                await fly.RunAsync(this, token);
            }
            catch (Exception ex)
            {
                // never leave a hint stranded mid-way, or parked out of sight, because a run faulted
                System.Diagnostics.Debug.WriteLine("[TrayKeyHint] animation failed: " + ex);
            }

            if (!token.IsCancellationRequested)
                Snap(shown);
        }

        private void Snap(bool shown)
        {
            SetValue(DropProperty, shown ? 0d : TrayTokens.KeyHintTravel);
        }
    }
}
