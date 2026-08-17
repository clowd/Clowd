using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor.Inspector
{
    /// <summary>
    /// One surround tile's picture: a small rounded chip — the same size and place in every tile, so
    /// the tiles differ only by what is drawn <i>around</i> it — on a lighter plate that stands in for
    /// the canvas behind the item. The decoration comes from the live <see cref="Surround"/> the tile
    /// is handed, off <see cref="SurroundMath"/>'s own numbers, so dialling the rows below the grid
    /// moves the picked tile's picture with them.
    /// </summary>
    /// <remarks>
    /// <b>The plate is what makes this readable.</b> A shadow is black and a glow is white; against
    /// the sidebar's near-black chrome one of the two would always be invisible, so both are drawn
    /// over a mid grey with a dark chip on top — every kind then contrasts with something. It also
    /// bounds the picture: nothing may spread past the plate, so a big shadow cannot bleed into the
    /// neighbouring tile.
    ///
    /// <b>It exaggerates, deliberately.</b> The dials are fractions of the item's drawn extent, and
    /// an item is hundreds of pixels where this is tens: proportionally correct, a default shadow
    /// would land half a pixel from the chip. Everything is therefore scaled by
    /// <see cref="Amplify"/> and then clamped to the plate. The tiles are honest about each other —
    /// twice the softness looks twice as soft — not measurable against the render.
    ///
    /// The blur is drawn as a handful of nested translucent rounded rects rather than through a real
    /// blur: at this size the ramp is indistinguishable, and it keeps the plate out of the blur (an
    /// Avalonia <c>Effect</c> would apply to everything the control paints, plate included).
    /// </remarks>
    public sealed class SurroundPreview : Control
    {
        /// <summary>How much the drawn decoration is scaled up over its true proportion — see the
        /// class remarks.</summary>
        private const double Amplify = 3.0;

        /// <summary>The chip's side, as a fraction of the control's shorter side. The rest is the
        /// margin the decoration has to spread into.</summary>
        private const double ChipFraction = 0.5;

        private const double ChipRadiusFraction = 0.22;

        private const double PlateRadius = 4;

        /// <summary>Nested rects per blur — six is where another one stops being visible.</summary>
        private const int BlurSteps = 6;

        /// <summary>The notional canvas behind the item.</summary>
        private static readonly IBrush PlateBrush = new SolidColorBrush(Color.FromRgb(0x74, 0x74, 0x74));

        /// <summary>The item itself: dark, so a white outline and a white glow both read on the
        /// plate — and so does a black shadow.</summary>
        private static readonly IBrush ChipBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x31));

        /// <summary>What this tile draws around the chip, or null for the bare chip (the None
        /// tile).</summary>
        public static readonly StyledProperty<Surround> SurroundProperty =
            AvaloniaProperty.Register<SurroundPreview, Surround>(nameof(Surround));

        static SurroundPreview()
        {
            AffectsRender<SurroundPreview>(SurroundProperty);
        }

        public Surround Surround
        {
            get => GetValue(SurroundProperty);
            set => SetValue(SurroundProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            double width = Bounds.Width, height = Bounds.Height;
            if (width <= 0 || height <= 0)
                return;

            var plate = new Rect(0, 0, width, height);
            double extent = Math.Min(width, height);
            double side = extent * ChipFraction;
            var chip = new Rect((width - side) / 2, (height - side) / 2, side, side);
            double radius = side * ChipRadiusFraction;

            using (context.PushClip(new RoundedRect(plate, PlateRadius)))
            {
                context.DrawRectangle(PlateBrush, null, plate, PlateRadius, PlateRadius);
                DrawSurround(context, chip, radius, side, margin: (extent - side) / 2);
                context.DrawRectangle(ChipBrush, null, chip, radius, radius);
            }
        }

        /// <summary>The decoration behind the chip. <paramref name="side"/> is the chip's own extent
        /// — the same reference the compositor measures the fractions against — and
        /// <paramref name="margin"/> is how far anything may reach before it hits the plate's
        /// edge.</summary>
        private void DrawSurround(DrawingContext context, Rect chip, double radius,
            double side, double margin)
        {
            var surround = Surround;
            if (surround == null || surround.Kind == SurroundKind.None || margin <= 0)
                return;

            var color = Color.FromUInt32(surround.Color);
            if (color.A == 0)
                return;

            switch (surround.Kind)
            {
                case SurroundKind.Shadow:
                {
                    double offset = SurroundMath.OffsetPx(surround, side) * Amplify;
                    double blur = SurroundMath.BlurPx(surround, side) * Amplify;
                    double fit = Fit(offset + blur, margin);
                    DrawSoft(context, chip.Translate(new Vector(offset * fit, offset * fit)),
                        radius, blur * fit, color);
                    break;
                }

                case SurroundKind.Glow:
                {
                    double blur = SurroundMath.BlurPx(surround, side) * Amplify;
                    DrawSoft(context, chip, radius, blur * Fit(blur, margin), color);
                    break;
                }

                case SurroundKind.Outline:
                {
                    double thickness = SurroundMath.OutlinePx(surround, side) * Amplify;
                    thickness *= Fit(thickness, margin);
                    var brush = new SolidColorBrush(color);
                    context.DrawRectangle(brush, null, chip.Inflate(thickness),
                        radius + thickness, radius + thickness);
                    break;
                }
            }
        }

        /// <summary>A soft-edged rounded rect: nested inflations of the same shape, each at a
        /// fraction of the colour's alpha, which accumulate into a ramp. A zero blur collapses to the
        /// shape drawn <see cref="BlurSteps"/> times — a hard edge at full strength, which is exactly
        /// what a shadow with no softness is.</summary>
        private static void DrawSoft(DrawingContext context, Rect rect, double radius,
            double blur, Color color)
        {
            byte alpha = (byte)Math.Max(1, Math.Round(color.A / (double)BlurSteps));
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));

            for (int i = BlurSteps; i >= 1; i--)
            {
                double grow = blur * i / BlurSteps;
                context.DrawRectangle(brush, null, rect.Inflate(grow), radius + grow, radius + grow);
            }
        }

        /// <summary>The factor that keeps a reach of <paramref name="wanted"/> pixels inside
        /// <paramref name="margin"/> — 1 while it already fits.</summary>
        private static double Fit(double wanted, double margin) =>
            wanted > margin && wanted > 0 ? margin / wanted : 1;
    }
}
