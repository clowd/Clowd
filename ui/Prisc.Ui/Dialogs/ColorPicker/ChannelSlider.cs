using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Prisc.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// The color dialog's channel slider: a 10px fully rounded track painted with the channel's
    /// live gradient (over <see cref="ColorSlider.Background"/>, the alpha row's checkerboard), and
    /// an 18px ring thumb that shows the track through it rather than a fill of its own. The thumb
    /// centres on the value, so at either end it hangs half its width past the track (the control
    /// does not clip). Input is <see cref="ColorSlider"/>'s — click or drag anywhere on it — plus
    /// the keyboard: it is a tab stop, Left / Right (or Down / Up) step it, Shift steps ten times
    /// as far, Home and End go to the ends, and keyboard focus rings the thumb in the strong accent.
    /// </summary>
    public class ChannelSlider : ColorSlider, ICustomHitTest
    {
        public const double TrackHeight = 10;
        public const double ThumbDiameter = 18;

        private static readonly IPen ThumbRing = new Pen(Brushes.White, 2);

        // the ring's thin dark outline and its drop: the mockup's 0 0 0 1px rgba(0,0,0,.6),
        // 0 1px 3px rgba(0,0,0,.4)
        private static readonly BoxShadows ThumbShadow = BoxShadows.Parse("0 0 0 1 #99000000, 0 1 3 0 #66000000");

        private const double FocusRingGap = 2;

        static ChannelSlider()
        {
            HeightProperty.OverrideDefaultValue<ChannelSlider>(ThumbDiameter);
            FocusableProperty.OverrideDefaultValue<ChannelSlider>(true);
        }

        /// <summary>One key press: a whole unit on a 0-255 or 0-360 channel, 1% on a 0-1 one.</summary>
        private double SmallStep => ValueMax > 1 ? 1 : 0.01;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || ValueMax <= 0)
                return;

            var step = SmallStep * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1);
            double? next = e.Key switch
            {
                Key.Left or Key.Down => Value - step,
                Key.Right or Key.Up => Value + step,
                Key.Home => 0,
                Key.End => ValueMax,
                _ => null,
            };

            if (next is not { } value)
                return;

            SetValueFromUser(Math.Clamp(value, 0, ValueMax));
            e.Handled = true;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            // the ring follows :focus-visible, which is settled by the time this repaints
            if (change.Property == IsFocusedProperty)
                InvalidateVisual();
        }

        // the whole thumb-tall strip takes clicks, not just the drawn 10px track: a press a little
        // above or below the track (but within the thumb's reach) still lands
        public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

        public override void Render(DrawingContext context)
        {
            var width = Bounds.Width;
            if (width <= 0)
                return;

            var track = new RoundedRect(new Rect(0, (Bounds.Height - TrackHeight) / 2, width, TrackHeight), TrackHeight / 2);
            if (Background != null)
                context.DrawRectangle(Background, null, track);
            if (SliderBrush != null)
                context.DrawRectangle(SliderBrush, null, track);

            var max = ValueMax;
            var x = max > 0 ? width * Math.Clamp(Value / max, 0, 1) : 0;
            var center = new Point(x, Bounds.Height / 2);
            var radius = ThumbDiameter / 2;

            // a shadow-only rectangle: Avalonia, like CSS, draws an outer shadow outside the shape
            // only, so the track still shows through the ring
            context.DrawRectangle(null, null, new RoundedRect(new Rect(x - radius, center.Y - radius, ThumbDiameter, ThumbDiameter), radius), ThumbShadow);
            context.DrawEllipse(null, ThumbRing, center, radius - 1, radius - 1);

            // keyboard focus only, as every other control in the dialog shows it
            if (IsFocused && Classes.Contains(":focus-visible")
                && this.TryFindResource("ColorDialogAccentStrongBrush", ActualThemeVariant, out var accent) && accent is IBrush ring)
            {
                var ringRadius = radius + FocusRingGap + 1;
                context.DrawEllipse(null, new Pen(ring, 2), center, ringRadius, ringRadius);
            }
        }
    }
}
