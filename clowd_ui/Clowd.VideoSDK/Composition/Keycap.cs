using System;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The keycap half of the keystroke overlay: one 3D cap — a dark rounded face sitting on a
    /// black rounded seat that peeks out below it — its legend, and the vector icons the familiar
    /// wide keys carry. Every icon is an <see cref="SKPath"/> on purpose: the overlay draws with
    /// the platform default typeface, and the symbols these keys want (⇧ ↵ ⌫ ⇥) are exactly the
    /// glyphs a default typeface is likely to be missing, which renders as a tofu box.
    ///
    /// The cap's whole vertical footprint fits inside one row of
    /// <see cref="FrameComposer.KeyboardMetrics"/>, so a row of caps measures the same as a row of
    /// text and the editor's gizmo stays truthful.
    /// </summary>
    internal sealed class Keycap : IDisposable
    {
        private static readonly SKColor FaceColor = new SKColor(0x3D, 0x3D, 0x3D);
        private static readonly SKColor SeatColor = SKColors.Black;
        private static readonly SKColor LegendColor = SKColors.White;

        private readonly SKFont _centred;
        private readonly SKFont _legend;

        public Keycap(SKTypeface typeface, float fontPx, float rowHeight)
        {
            Inset = fontPx * 0.06f;
            BaseDrop = Math.Max(1f, fontPx * 0.10f);
            Height = Math.Max(1f, rowHeight - 2 * Inset - BaseDrop);
            Radius = Height * 0.22f;
            PadX = fontPx * 0.24f;
            PadY = fontPx * 0.16f;
            IconSize = Height * 0.40f;

            // legends are smaller than the typing they sit beside, as they are on a real keyboard
            _centred = new SKFont(typeface, fontPx * 0.78f) { Subpixel = true };
            _legend = new SKFont(typeface, fontPx * 0.60f) { Subpixel = true };
        }

        /// <summary>Height of the cap face — the seat adds <see cref="BaseDrop"/> below it.</summary>
        public float Height { get; }

        /// <summary>How far the black seat sticks out under the face: the whole 3D of the thing.</summary>
        public float BaseDrop { get; }

        /// <summary>The cap's full vertical footprint, face and seat together.</summary>
        public float TotalHeight => Height + BaseDrop;

        private float Inset { get; }

        private float Radius { get; }

        private float PadX { get; }

        private float PadY { get; }

        private float IconSize { get; }

        /// <summary>Wide keys wear their legend bottom-left with an icon top-right, the way the
        /// real keys are moulded; everything else centres its legend.</summary>
        private static bool IsWide(string label) => label is
            "Ctrl" or "Shift" or "Alt" or "Win" or "Enter" or "Tab" or "Space" or "Bksp" or "Caps";

        /// <summary>Arrow keys are the one cap drawn as an icon alone — the glyph <i>is</i> the
        /// legend, and spelling "Left" on a square cap reads worse than the arrow does.</summary>
        private static bool IsArrow(string label) => label is "Up" or "Down" or "Left" or "Right";

        public float Width(string label)
        {
            if (string.IsNullOrEmpty(label))
                return Height;
            if (IsArrow(label))
                return Height;
            if (IsWide(label))
                return Math.Max(Height * 1.7f, _legend.MeasureText(label) + IconSize + 3 * PadX);
            return Math.Max(Height, _centred.MeasureText(label) + 2 * PadX);
        }

        /// <summary>Draws the cap with its left edge at <paramref name="left"/>, its footprint
        /// centred on <paramref name="centreY"/>.</summary>
        public void Draw(SKCanvas canvas, string label, float left, float centreY, double alpha)
        {
            float width = Width(label);
            float top = centreY - TotalHeight / 2;
            var face = new SKRect(left, top, left + width, top + Height);
            var seat = new SKRect(left, top + BaseDrop, left + width, top + Height + BaseDrop);

            using var paint = new SKPaint { IsAntialias = true };

            paint.Style = SKPaintStyle.Fill;
            paint.Color = Fade(SeatColor, alpha);
            using (var rr = new SKRoundRect(seat, Radius, Radius))
                canvas.DrawRoundRect(rr, paint);

            paint.Color = Fade(FaceColor, alpha);
            using (var rr = new SKRoundRect(face, Radius, Radius))
                canvas.DrawRoundRect(rr, paint);

            var ink = Fade(LegendColor, alpha);
            if (IsArrow(label))
            {
                float size = Height * 0.55f;
                DrawIcon(canvas, label, new SKRect(
                    face.MidX - size / 2, face.MidY - size / 2,
                    face.MidX + size / 2, face.MidY + size / 2), ink);
                return;
            }

            if (IsWide(label))
            {
                paint.Color = ink;
                var metrics = _legend.Metrics;
                canvas.DrawText(label, face.Left + PadX, face.Bottom - PadY - metrics.Descent,
                    SKTextAlign.Left, _legend, paint);
                DrawIcon(canvas, label, new SKRect(
                    face.Right - PadX - IconSize, face.Top + PadY * 0.7f,
                    face.Right - PadX, face.Top + PadY * 0.7f + IconSize), ink);
                return;
            }

            paint.Color = ink;
            canvas.DrawText(label, face.MidX, Baseline(_centred, face.MidY),
                SKTextAlign.Center, _centred, paint);
        }

        /// <summary>The baseline that centres a line's ink on <paramref name="centreY"/>, from the
        /// font's real ascent/descent rather than a guess at the cap height — the fix for text
        /// riding high in its pill.</summary>
        public static float Baseline(SKFont font, float centreY)
        {
            var metrics = font.Metrics;
            return centreY - (metrics.Ascent + metrics.Descent) / 2;
        }

        public static SKColor Fade(SKColor color, double alpha)
        {
            double a = color.Alpha / 255.0 * alpha;
            return color.WithAlpha((byte)Math.Clamp(Math.Round(a * 255), 0, 255));
        }

        public void Dispose()
        {
            _centred.Dispose();
            _legend.Dispose();
        }

        // -------------------------------------------------------------------------------- icons

        /// <summary>Draws <paramref name="label"/>'s icon inside <paramref name="box"/>, or
        /// nothing when the key has none. Paths are built in the unit square and mapped onto the
        /// box, so one description serves every font size.</summary>
        private static void DrawIcon(SKCanvas canvas, string label, SKRect box, SKColor ink)
        {
            var fill = new SKPath();
            var stroke = new SKPath();
            float strokeWidth = 0.13f;

            switch (label)
            {
                case "Shift":
                    Arrow(fill, 0.06f, 0.94f, 0);
                    break;
                case "Caps":
                    Arrow(fill, 0.03f, 0.72f, 0);
                    fill.AddRect(new SKRect(0.28f, 0.82f, 0.72f, 0.97f));
                    break;
                case "Up":
                    Arrow(fill, 0.06f, 0.94f, 0);
                    break;
                case "Down":
                    Arrow(fill, 0.06f, 0.94f, 180);
                    break;
                case "Left":
                    Arrow(fill, 0.06f, 0.94f, 270);
                    break;
                case "Right":
                    Arrow(fill, 0.06f, 0.94f, 90);
                    break;
                case "Enter":
                    stroke.MoveTo(0.92f, 0.10f);
                    stroke.LineTo(0.92f, 0.62f);
                    stroke.LineTo(0.30f, 0.62f);
                    fill.MoveTo(0.34f, 0.38f);
                    fill.LineTo(0.34f, 0.86f);
                    fill.LineTo(0.04f, 0.62f);
                    fill.Close();
                    break;
                case "Bksp":
                    stroke.MoveTo(0.34f, 0.14f);
                    stroke.LineTo(0.97f, 0.14f);
                    stroke.LineTo(0.97f, 0.86f);
                    stroke.LineTo(0.34f, 0.86f);
                    stroke.LineTo(0.03f, 0.50f);
                    stroke.Close();
                    stroke.MoveTo(0.52f, 0.34f);
                    stroke.LineTo(0.80f, 0.66f);
                    stroke.MoveTo(0.80f, 0.34f);
                    stroke.LineTo(0.52f, 0.66f);
                    strokeWidth = 0.09f;
                    break;
                case "Tab":
                    fill.MoveTo(0.04f, 0.18f);
                    fill.LineTo(0.58f, 0.50f);
                    fill.LineTo(0.04f, 0.82f);
                    fill.Close();
                    fill.AddRect(new SKRect(0.72f, 0.12f, 0.92f, 0.88f));
                    break;
                case "Win":
                    fill.AddRect(new SKRect(0.06f, 0.06f, 0.46f, 0.46f));
                    fill.AddRect(new SKRect(0.54f, 0.06f, 0.94f, 0.46f));
                    fill.AddRect(new SKRect(0.06f, 0.54f, 0.46f, 0.94f));
                    fill.AddRect(new SKRect(0.54f, 0.54f, 0.94f, 0.94f));
                    break;
                case "Ctrl":
                    stroke.MoveTo(0.12f, 0.68f);
                    stroke.LineTo(0.50f, 0.28f);
                    stroke.LineTo(0.88f, 0.68f);
                    break;
                case "Alt":
                    stroke.MoveTo(0.04f, 0.24f);
                    stroke.LineTo(0.38f, 0.24f);
                    stroke.LineTo(0.88f, 0.80f);
                    stroke.MoveTo(0.58f, 0.24f);
                    stroke.LineTo(0.96f, 0.24f);
                    break;
                case "Space":
                    stroke.MoveTo(0.08f, 0.34f);
                    stroke.LineTo(0.08f, 0.70f);
                    stroke.LineTo(0.92f, 0.70f);
                    stroke.LineTo(0.92f, 0.34f);
                    break;
            }

            try
            {
                if (fill.IsEmpty && stroke.IsEmpty)
                    return;

                var map = SKMatrix.CreateScale(box.Width, box.Height)
                    .PostConcat(SKMatrix.CreateTranslation(box.Left, box.Top));

                using var paint = new SKPaint { IsAntialias = true, Color = ink };
                if (!fill.IsEmpty)
                {
                    fill.Transform(map);
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawPath(fill, paint);
                }

                if (!stroke.IsEmpty)
                {
                    stroke.Transform(map);
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = strokeWidth * Math.Min(box.Width, box.Height);
                    paint.StrokeCap = SKStrokeCap.Round;
                    paint.StrokeJoin = SKStrokeJoin.Round;
                    canvas.DrawPath(stroke, paint);
                }
            }
            finally
            {
                fill.Dispose();
                stroke.Dispose();
            }
        }

        /// <summary>An up-arrow (head plus stem) spanning <paramref name="top"/>..<paramref
        /// name="bottom"/> of the unit box, rotated clockwise by <paramref name="degrees"/>.</summary>
        private static void Arrow(SKPath path, float top, float bottom, float degrees)
        {
            float head = top + (bottom - top) * 0.52f;
            var arrow = new SKPath();
            arrow.MoveTo(0.50f, top);
            arrow.LineTo(0.94f, head);
            arrow.LineTo(0.68f, head);
            arrow.LineTo(0.68f, bottom);
            arrow.LineTo(0.32f, bottom);
            arrow.LineTo(0.32f, head);
            arrow.LineTo(0.06f, head);
            arrow.Close();

            if (degrees != 0)
                arrow.Transform(SKMatrix.CreateRotationDegrees(degrees, 0.5f, 0.5f));

            path.AddPath(arrow);
            arrow.Dispose();
        }
    }
}
