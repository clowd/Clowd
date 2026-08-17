using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The pure pieces of cursor-overlay drawing: kind → glyph resolution, monitor/region math on
    /// the capture header, and the primitive draws (the recorded 512-box, a themed vector glyph,
    /// the click animations). <c>FrameComposer</c> owns placement — it resolves the screen item's
    /// <see cref="PictureMapping"/>, applies its clips, and calls in here — so preview and render
    /// share every number by construction.
    /// </summary>
    internal static class CursorCompose
    {
        /// <summary>Base size of a themed glyph in source (physical) pixels at monitor scale 1 —
        /// the nominal size of a Windows cursor at 100% DPI. Multiplied by
        /// <c>CursorContent.Size</c> and the header's monitor scale.</summary>
        internal const double BaseCursorPx = 32.0;

        // Click animation constants — the obs tracker's, verbatim (tracker.rs): 400 ms, 85% peak
        // opacity fading linearly to 0, radius 10 → 40 DIP.
        internal const double ClickDurationMs = 400.0;
        internal const double ClickMaxOpacity = 0.85;
        internal const double ClickRadiusStartDip = 10.0;
        internal const double ClickRadiusGrowthDip = 30.0;

        /// <summary>The <see cref="CursorAssets"/> kind key for a captured cursor kind, or null
        /// for the kinds without dedicated artwork (they fall back to the style's arrow).</summary>
        internal static string KindKey(CursorKind kind) => kind switch
        {
            CursorKind.Arrow => CursorAssets.KindArrow,
            CursorKind.Hand => CursorAssets.KindHand,
            CursorKind.IBeam => CursorAssets.KindIBeam,
            _ => null,
        };

        /// <summary>
        /// The glyph to draw for a (style, kind) pair: the style's artwork for the kind, else the
        /// style's arrow (unmodelled/custom kinds and the documented per-style gaps), else the
        /// default style's equivalent for an unknown style name. Null only for
        /// <see cref="CursorKind.Hidden"/> — nothing is drawn then.
        /// </summary>
        internal static CursorGlyph ResolveGlyph(string style, CursorKind kind)
        {
            if (kind == CursorKind.Hidden)
                return null;

            string key = KindKey(kind);
            var glyph = key != null ? CursorAssets.TryGet(style, key) : null;
            glyph ??= CursorAssets.TryGet(style, CursorAssets.KindArrow);
            if (glyph == null)
            {
                // unknown style (or "native" reaching here by mistake): the default theme
                glyph = key != null ? CursorAssets.TryGet(CursorAssets.DefaultStyle, key) : null;
                glyph ??= CursorAssets.TryGet(CursorAssets.DefaultStyle, CursorAssets.KindArrow);
            }
            return glyph;
        }

        /// <summary>Whether a captured position (physical px, virtual-desktop coords) lies inside
        /// the header's recording region. False for a header-less capture — there is then no
        /// space to map against and nothing draws.</summary>
        internal static bool IsInsideRegion(InputCaptureHeader header, int x, int y)
        {
            if (header == null || header.RegionWidth <= 0 || header.RegionHeight <= 0)
                return false;
            return x >= header.RegionX && x < header.RegionX + header.RegionWidth
                && y >= header.RegionY && y < header.RegionY + header.RegionHeight;
        }

        /// <summary>The DPI scale of the monitor containing the point, else the first monitor's,
        /// else 1.0 — what sizes a themed glyph (and the click animation's DIP radii) in source
        /// pixels.</summary>
        internal static double MonitorScaleAt(InputCaptureHeader header, int x, int y)
        {
            double first = 0;
            if (header?.Monitors != null)
            {
                foreach (var mon in header.Monitors)
                {
                    if (mon.Scale > 0 && first <= 0)
                        first = mon.Scale;
                    if (mon.Scale > 0
                        && x >= mon.X && x < mon.X + mon.Width
                        && y >= mon.Y && y < mon.Y + mon.Height)
                    {
                        return mon.Scale;
                    }
                }
            }
            return first > 0 ? first : 1.0;
        }

        // ------------------------------------------------------------------------------ box draw

        /// <summary>
        /// Draws the recorded cursor-box frame centred on the mapped hotspot: the box's pixels are
        /// 1:1 with the screen's physical pixels (the recorder pins the sampled cursor position to
        /// the box centre), so it scales by the same px→canvas factor as the screen frame itself.
        /// Caller has already clipped to the screen item's rect.
        /// </summary>
        internal static void DrawBox(SKCanvas target, SKImage box, in PictureMapping map,
            double hotspotSourceX, double hotspotSourceY, double opacity)
        {
            if (box == null || box.Width <= 0 || box.Height <= 0)
                return;

            var pos = map.Map(hotspotSourceX, hotspotSourceY);
            float halfW = (float)(box.Width / 2.0 * map.ScaleX);
            float halfH = (float)(box.Height / 2.0 * map.ScaleY);
            var dest = new SKRect(pos.X - halfW, pos.Y - halfH, pos.X + halfW, pos.Y + halfH);

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White.WithAlpha(FrameComposer.AlphaByte(opacity)),
            };
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            target.DrawImage(box, new SKRect(0, 0, box.Width, box.Height), dest, sampling, paint);
        }

        // ---------------------------------------------------------------------------- glyph draw

        /// <summary>
        /// Draws a themed glyph with its hotspot on <paramref name="pos"/> at
        /// <paramref name="sizePx"/> canvas pixels. Layers paint halo-strokes first, then every
        /// fill in document order — the halo is a centred stroke, so fills must land on top of it
        /// (see <see cref="CursorGlyphPath.Stroke"/>). The drop shadow (and any translucency)
        /// wraps the whole glyph in one layer so overlapping strokes/fills never double-blend.
        /// </summary>
        internal static void DrawGlyph(SKCanvas target, CursorGlyph glyph, SKPoint pos,
            float sizePx, bool dropShadow, double opacity)
        {
            if (glyph == null || sizePx <= 0 || opacity <= 0)
                return;

            float scale = sizePx / glyph.ViewBox;
            int save = target.Save();
            try
            {
                if (dropShadow || opacity < 1)
                {
                    using var layer = new SKPaint
                    {
                        Color = SKColors.White.WithAlpha(FrameComposer.AlphaByte(opacity)),
                    };
                    if (dropShadow)
                    {
                        layer.ImageFilter = SKImageFilter.CreateDropShadow(
                            sizePx * 0.06f, sizePx * 0.09f, sizePx * 0.06f, sizePx * 0.06f,
                            SKColors.Black.WithAlpha(128));
                    }
                    target.SaveLayer(layer);
                }

                target.Translate(pos.X - glyph.Hotspot.X * scale, pos.Y - glyph.Hotspot.Y * scale);
                target.Scale(scale);

                using var paint = new SKPaint { IsAntialias = true };

                paint.Style = SKPaintStyle.Stroke;
                foreach (var layer in glyph.Paths)
                {
                    if (!layer.HasStroke)
                        continue;
                    paint.Color = layer.Stroke;
                    paint.StrokeWidth = layer.StrokeWidth;
                    target.DrawPath(GetPath(layer), paint);
                }

                paint.Style = SKPaintStyle.Fill;
                foreach (var layer in glyph.Paths)
                {
                    paint.Color = layer.Fill;
                    target.DrawPath(GetPath(layer), paint);
                }
            }
            finally
            {
                target.RestoreToCount(save);
            }
        }

        /// <summary>Process-wide parse cache for glyph layer paths, keyed by the (immutable,
        /// static) layer instance — <see cref="CursorAssets"/>' own contract that callers cache
        /// parsed paths. Read-only after insertion; Skia paths are safe for concurrent const
        /// draws.</summary>
        private static readonly object PathSync = new object();
        private static readonly Dictionary<CursorGlyphPath, SKPath> PathCache
            = new Dictionary<CursorGlyphPath, SKPath>();

        private static SKPath GetPath(CursorGlyphPath layer)
        {
            lock (PathSync)
            {
                if (!PathCache.TryGetValue(layer, out var path))
                {
                    path = SKPath.ParseSvgPathData(layer.PathData) ?? new SKPath();
                    PathCache[layer] = path;
                }
                return path;
            }
        }

        // ------------------------------------------------------------------------- click anims

        /// <summary>
        /// Draws every in-flight click animation at <paramref name="sourceMs"/>: one animation per
        /// mouse-down event, started at the event's time and animated in <b>project</b> time (the
        /// source-time window scales by <paramref name="speed"/> so a sped-up clip does not
        /// compress the animation). Ripple = expanding fading circle (the tracker's constants);
        /// pulse = the same fade with the radius shrinking instead. Drawn beneath the glyph —
        /// callers invoke this first.
        /// </summary>
        internal static void DrawClickAnimations(SKCanvas target, InputCapture capture,
            in PictureMapping map, double sourceMs, double speed, string animation,
            uint colorArgb, double monitorScale, double opacity)
        {
            bool pulse;
            if (string.Equals(animation, "ripple", StringComparison.OrdinalIgnoreCase))
                pulse = false;
            else if (string.Equals(animation, "pulse", StringComparison.OrdinalIgnoreCase))
                pulse = true;
            else
                return;

            if (speed <= 0)
                speed = 1.0;
            double windowMs = ClickDurationMs * speed;
            var events = capture.EventsBetween(sourceMs - windowMs, sourceMs + 0.001);
            if (events.Count == 0)
                return;

            var header = capture.Header;
            var color = new SKColor(colorArgb);
            using var paint = new SKPaint { IsAntialias = true };

            foreach (var e in events)
            {
                if (e.Kind != InputEventKind.MouseDown)
                    continue;
                double progress = (sourceMs - e.TimeMs) / windowMs;
                if (progress < 0 || progress >= 1)
                    continue;

                double radiusDip = pulse
                    ? ClickRadiusStartDip + (1 - progress) * ClickRadiusGrowthDip
                    : ClickRadiusStartDip + progress * ClickRadiusGrowthDip;
                float radius = (float)(radiusDip * monitorScale * map.ScaleX);
                double alpha = (1 - progress) * ClickMaxOpacity * (color.Alpha / 255.0) * opacity;

                paint.Color = color.WithAlpha(FrameComposer.AlphaByte(alpha));
                var pos = map.Map(e.X - header.RegionX, e.Y - header.RegionY);
                target.DrawCircle(pos, radius, paint);
            }
        }
    }
}
