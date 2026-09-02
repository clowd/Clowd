using System;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Draws a background wallpaper into a rectangle: the phase clock, the cover placement, and
    /// the one entry point Clowd.Ui's inspector tiles and flyouts share with the composer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Time.</b> A wallpaper's loop phase is a pure function of a PROJECT-timeline instant
    /// (100ns ticks, global, already un-warped from speed effects — the <c>timeTicks</c> that
    /// <c>FrameComposer.ComposeItem</c> receives). The export composes frame n at
    /// <c>warp.ToProject(FrameIndexToTicks(n))</c>, the preview at the player's
    /// <c>ClockToProjectTicks(clock)</c>, and both hand that <c>long</c> to
    /// <see cref="PhaseOf(BackgroundStyle, long, double)"/> unchanged; the phase is computed in
    /// integer ticks before any floating-point operation, so identical ticks give identical
    /// phases whichever consumer asks. What differs between the two is only which instants get
    /// sampled (the render's frame grid versus the preview's interpolating clock), which is
    /// inherent and applies equally to media frames and cursor spinners today: a scrub to any
    /// tick shows exactly the export's frame for that tick.
    /// </para>
    /// <para>
    /// The clock is global project time rather than time since the item's start, deliberately:
    /// splitting a background item or trimming its start must not jump the animation, and it is
    /// the policy <c>DrawCursorItem</c> already uses for spinners. A consequence shared with
    /// spinners is that a 2x speed effect plays a 60 s loop in 30 output seconds.
    /// </para>
    /// <para>
    /// <b>Placement.</b> Every file is <c>preserveAspectRatio="xMidYMid slice"</c>: the art
    /// COVERS the destination, centered, with the overflow on the longer axis clipped. No
    /// drawer in <c>FrameComposer</c> clips to its own box, so <see cref="DrawScene"/> does.
    /// </para>
    /// </remarks>
    public static class BackgroundRenderer
    {
        /// <summary>
        /// Where in [0, 1) the style's loop is at a project-timeline instant; 0 for a static
        /// style. <paramref name="animationSpeed"/> is the content's own dial (1 = as authored)
        /// and multiplies the ticks before the modulo, so it composes with speed effects rather
        /// than fighting them. A non-positive or NaN speed reads as 1.
        /// </summary>
        public static double PhaseOf(BackgroundStyle style, long timeTicks, double animationSpeed = 1.0)
        {
            long period = style?.PeriodTicks ?? 0;
            if (period <= 0)
                return 0.0;
            double speed = animationSpeed > 0 && !double.IsNaN(animationSpeed) && !double.IsInfinity(animationSpeed)
                ? animationSpeed
                : 1.0;
            // Integer ticks before the modulo: the double conversion happens once, on a value
            // already inside one period, so the preview's clock and the render's grid cannot
            // disagree by a last bit at a 60 s or 90 s period.
            long scaled = speed == 1.0 ? timeTicks : (long)Math.Round(timeTicks * speed);
            long phaseTicks = scaled % period;
            if (phaseTicks < 0)
                phaseTicks += period;
            return phaseTicks / (double)period;
        }

        /// <summary>As <see cref="PhaseOf(BackgroundStyle, long, double)"/>, resolving the style
        /// id first (an unknown id is the default style).</summary>
        public static double PhaseOf(string style, long timeTicks, double animationSpeed = 1.0)
            => PhaseOf(BackgroundCatalog.Find(BackgroundCatalog.ResolveStyle(style)), timeTicks, animationSpeed);

        /// <summary>
        /// The cached scene for a (style, theme) pair, with both ids resolved (an unknown style
        /// is the default style, an unknown theme the style's first). Null only when the
        /// embedded file is missing, which the tests rule out for every catalog row. Safe to
        /// call from any thread; the result is shared and immutable.
        /// </summary>
        public static BackgroundScene GetScene(string style, string theme)
            => BackgroundAssets.GetScene(style, theme);

        /// <summary>
        /// Cover-fits the scene into <paramref name="dest"/> (centered, overflow clipped to
        /// <paramref name="dest"/>) at <paramref name="phase"/> and <paramref name="opacity"/>.
        /// An opacity below 1, or a scene that <see cref="BackgroundScene.NeedsIsolation"/>,
        /// composites through one layer so overlapping shapes fade as a whole and the art's blend
        /// modes see the art rather than what is under it. The canvas is left as it was found.
        /// </summary>
        public static void DrawScene(SKCanvas canvas, SKRect dest, BackgroundScene scene, double phase, double opacity = 1.0)
        {
            if (canvas == null || scene == null)
                return;
            if (dest.Width <= 0 || dest.Height <= 0 || opacity <= 0)
                return;
            var viewBox = scene.ViewBox;
            if (viewBox.Width <= 0 || viewBox.Height <= 0)
                return;

            int save = canvas.Save();
            canvas.ClipRect(dest, SKClipOperation.Intersect, antialias: true);
            if (opacity < 1 || scene.NeedsIsolation)
            {
                using var layer = new SKPaint { Color = SKColors.White.WithAlpha(AlphaByte(opacity)) };
                canvas.SaveLayer(dest, layer);
            }
            canvas.Concat(CoverMatrix(dest, viewBox));
            scene.Draw(canvas, phase);
            canvas.RestoreToCount(save);
        }

        /// <summary>
        /// Convenience for inspector tiles and flyouts: resolves both ids (unknown ones draw
        /// the defaults, never nothing), turns seconds into ticks with
        /// <c>(long)Math.Round(timeSeconds * TimeSpan.TicksPerSecond)</c>, and goes through the
        /// same <see cref="PhaseOf(BackgroundStyle, long, double)"/> and <see cref="DrawScene"/> the
        /// composer uses — so a tile fed the playhead's project seconds shows the frame the
        /// preview shows. A plain canvas draw with no context assumptions: an Avalonia-leased
        /// canvas and a <c>WriteableBitmap</c>-backed surface are both fine.
        /// </summary>
        public static void Draw(SKCanvas canvas, SKRect dest, string style, string theme, double timeSeconds,
            double animationSpeed = 1.0)
        {
            var resolved = BackgroundCatalog.Find(BackgroundCatalog.ResolveStyle(style));
            var scene = GetScene(resolved.Id, theme);
            if (scene == null)
                return;
            long ticks = double.IsNaN(timeSeconds) ? 0 : (long)Math.Round(timeSeconds * TimeSpan.TicksPerSecond);
            DrawScene(canvas, dest, scene, PhaseOf(resolved, ticks, animationSpeed), 1.0);
        }

        /// <summary>The matrix that maps <paramref name="viewBox"/> onto <paramref name="dest"/>
        /// as <c>xMidYMid slice</c>: uniform scale by the larger of the two ratios, centered.</summary>
        internal static SKMatrix CoverMatrix(SKRect dest, SKRect viewBox)
        {
            float s = Math.Max(dest.Width / viewBox.Width, dest.Height / viewBox.Height);
            float tx = dest.MidX - viewBox.Width * s / 2f - viewBox.Left * s;
            float ty = dest.MidY - viewBox.Height * s / 2f - viewBox.Top * s;
            return SKMatrix.CreateScaleTranslation(s, s, tx, ty);
        }

        private static byte AlphaByte(double opacity)
            => (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255);
    }
}
