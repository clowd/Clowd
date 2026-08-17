using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Model;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The pure pieces of cursor-overlay drawing: kind → glyph resolution, monitor/region math on
    /// the capture header, and the primitive draws (the recorded native cursor sprite, a themed
    /// vector glyph, the click animations). <c>FrameComposer</c> owns placement — it resolves the
    /// screen item's <see cref="PictureMapping"/>, applies its clips, and calls in here — so
    /// preview and render share every number by construction.
    /// </summary>
    internal static class CursorCompose
    {
        /// <summary>Base size of a themed glyph in source (physical) pixels at monitor scale 1 —
        /// the standardised 100% cursor size, deliberately larger than the 32px a Windows cursor
        /// is authored at (a recorded cursor reads better a touch oversized). Multiplied by
        /// <c>CursorContent.Size</c> and the header's monitor scale, so 100% lands at 40x40 on a
        /// screen row shown at its recorded logical size whatever the recording's DPI was.</summary>
        internal const double BaseCursorPx = 40.0;

        /// <summary>The <see cref="CursorAssets"/> kind key for a captured cursor kind — the same
        /// wire names the recorder writes. Null for <see cref="CursorKind.Custom"/> (an
        /// application's own cursor, which no pack can have artwork for) and for
        /// <see cref="CursorKind.Hidden"/>; both fall back to the style's arrow, and Hidden is
        /// dropped before that by <see cref="ResolveGlyph"/>.</summary>
        internal static string KindKey(CursorKind kind) => kind switch
        {
            CursorKind.Arrow => CursorAssets.KindArrow,
            CursorKind.IBeam => CursorAssets.KindIBeam,
            CursorKind.Wait => CursorAssets.KindWait,
            CursorKind.Cross => CursorAssets.KindCross,
            CursorKind.UpArrow => CursorAssets.KindUpArrow,
            CursorKind.SizeNWSE => CursorAssets.KindSizeNwse,
            CursorKind.SizeNESW => CursorAssets.KindSizeNesw,
            CursorKind.SizeWE => CursorAssets.KindSizeWe,
            CursorKind.SizeNS => CursorAssets.KindSizeNs,
            CursorKind.SizeAll => CursorAssets.KindSizeAll,
            CursorKind.No => CursorAssets.KindNo,
            CursorKind.Hand => CursorAssets.KindHand,
            CursorKind.AppStarting => CursorAssets.KindAppStarting,
            CursorKind.Help => CursorAssets.KindHelp,
            CursorKind.Pen => CursorAssets.KindPen,
            CursorKind.Person => CursorAssets.KindPerson,
            _ => null,
        };

        /// <summary>The glyph for a (style, kind) pair in the style's default colourway; see the
        /// three-argument overload.</summary>
        internal static CursorGlyph ResolveGlyph(string style, CursorKind kind)
            => ResolveGlyph(style, null, kind);

        /// <summary>
        /// The glyph to draw for a (style, colourway, kind) triple: the style's artwork for the
        /// kind, else the style's arrow (unmodelled/custom kinds and the documented per-style
        /// gaps), else the default style's equivalent for an unknown style name. An unrecognised
        /// colourway is not a miss — it resolves to the style's default. Null only for
        /// <see cref="CursorKind.Hidden"/> — nothing is drawn then.
        /// </summary>
        internal static CursorGlyph ResolveGlyph(string style, string variant, CursorKind kind)
        {
            if (kind == CursorKind.Hidden)
                return null;

            string key = KindKey(kind);
            var glyph = key != null ? CursorAssets.TryGet(style, variant, key) : null;
            glyph ??= CursorAssets.TryGet(style, variant, CursorAssets.KindArrow);
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

        // --------------------------------------------------------------------------- sprite draw

        /// <summary>
        /// Draws a recorded native cursor sprite with its hotspot on the mapped capture position:
        /// sprite pixels are the screen's physical pixels (the recorder rasterizes the live cursor
        /// at native size), so it scales by the same px→canvas factor as the screen frame itself,
        /// times the item's <paramref name="sizeMultiplier"/>. An inverting cursor's XOR plane
        /// (<see cref="CursorSprite.Mask"/>) draws over the bmp in
        /// <see cref="SKBlendMode.Difference"/> — one draw handles all three mask values exactly:
        /// a white pixel inverts the pixels beneath (|d − 1| = 1 − d), a black pixel is the
        /// preserved no-op (|d − 0| = d), a transparent pixel does not apply. Caller has already
        /// clipped to the screen item's rect.
        /// </summary>
        internal static void DrawNativeSprite(SKCanvas target, CursorSprite sprite, in PictureMapping map,
            double hotspotSourceX, double hotspotSourceY, double sizeMultiplier, double opacity)
        {
            var bmp = sprite?.GetBmpImage();
            if (bmp == null || sprite.Width <= 0 || sprite.Height <= 0 || sizeMultiplier <= 0)
                return;

            var pos = map.Map(hotspotSourceX, hotspotSourceY);
            float left = pos.X - (float)(sprite.HotX * map.ScaleX * sizeMultiplier);
            float top = pos.Y - (float)(sprite.HotY * map.ScaleY * sizeMultiplier);
            var dest = new SKRect(left, top,
                left + (float)(sprite.Width * map.ScaleX * sizeMultiplier),
                top + (float)(sprite.Height * map.ScaleY * sizeMultiplier));
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White.WithAlpha(FrameComposer.AlphaByte(opacity)),
            };
            target.DrawImage(bmp, new SKRect(0, 0, bmp.Width, bmp.Height), dest, sampling, paint);

            var mask = sprite.GetMaskImage();
            if (mask != null)
            {
                // at opacity < 1 the Difference draw is a partial inversion, which reads as the
                // sprite fading like any other — the exact-inversion contract holds at 1.
                using var maskPaint = new SKPaint
                {
                    IsAntialias = true,
                    BlendMode = SKBlendMode.Difference,
                    Color = SKColors.White.WithAlpha(FrameComposer.AlphaByte(opacity)),
                };
                target.DrawImage(mask, new SKRect(0, 0, mask.Width, mask.Height), dest, sampling, maskPaint);
            }
        }

        // ---------------------------------------------------------------------------- glyph draw

        /// <summary>
        /// Draws a themed glyph with its hotspot on <paramref name="pos"/> at
        /// <paramref name="sizePx"/> canvas pixels, wearing the item's
        /// <paramref name="surround"/> — the glyph's drawn box is its reference extent, so a bigger
        /// cursor casts a proportionally bigger shadow. A surround is a decoration-only
        /// filter (see <see cref="SurroundMath"/>), which is why the glyph is painted twice:
        /// once inside the filtered layer to produce the decoration, once plainly on top. Each pass
        /// goes through one layer so overlapping strokes/fills never double-blend.
        /// </summary>
        internal static void DrawGlyph(SKCanvas target, CursorGlyph glyph, SKPoint pos,
            float sizePx, Surround surround, double opacity)
        {
            if (glyph == null || sizePx <= 0 || opacity <= 0)
                return;

            using var decoration = SurroundMath.CreateDecoration(surround, sizePx);
            float scale = sizePx / glyph.ViewBox;

            // pass 0 paints the decoration (skipped when there is none), pass 1 the glyph itself
            for (int pass = decoration != null ? 0 : 1; pass <= 1; pass++)
            {
                int save = target.Save();
                try
                {
                    if (pass == 0 || opacity < 1)
                    {
                        using var layer = new SKPaint
                        {
                            Color = SKColors.White.WithAlpha(FrameComposer.AlphaByte(opacity)),
                            ImageFilter = pass == 0 ? decoration : null,
                        };
                        target.SaveLayer(layer);
                    }

                    target.Translate(pos.X - glyph.Hotspot.X * scale, pos.Y - glyph.Hotspot.Y * scale);
                    target.Scale(scale);
                    PaintGlyph(target, glyph);
                }
                finally
                {
                    target.RestoreToCount(save);
                }
            }
        }

        /// <summary>The glyph's own ink in the caller's (already translated and scaled) space:
        /// each layer's halo-stroke immediately followed by that layer's fill, in document order.
        /// The halo is a centred stroke, so a layer's own fill has to land on top of it (see
        /// <see cref="CursorGlyphPath.Stroke"/>); doing it per layer rather than in two global
        /// passes is what lets a badge sit on a base shape — the badge's halo separates the two,
        /// which is exactly what a single pass of every halo first would paint over.</summary>
        private static void PaintGlyph(SKCanvas target, CursorGlyph glyph)
        {
            // Round joins/caps, not Skia's default miter: the halo stands in for an *outside*
            // stroke in the source artwork, which offsets a corner into an arc. A miter join
            // instead spikes out by up to the miter limit, which on a pointed shape (Point's
            // triangular caps) visibly inflates the glyph.
            using var paint = new SKPaint
            {
                IsAntialias = true,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
            };

            foreach (var layer in glyph.Paths)
            {
                var path = GetPath(layer);
                if (layer.HasStroke)
                {
                    paint.Style = SKPaintStyle.Stroke;
                    paint.Color = layer.Stroke;
                    paint.StrokeWidth = layer.StrokeWidth;
                    target.DrawPath(path, paint);
                }

                paint.Style = SKPaintStyle.Fill;
                paint.Color = layer.Fill;
                target.DrawPath(path, paint);
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

        // --------------------------------------------------------------------- click highlight

        /// <summary>
        /// Draws the click highlight at <paramref name="sourceMs"/>: a solid dot pinned to the
        /// pointer for as long as a button is held (<see cref="InputFrame.Buttons"/> on
        /// <paramref name="row"/>, so it follows a drag), and one expanding animation per mouse-<b>up</b>
        /// event — the highlight explodes where the button was released. Both animate in
        /// <b>project</b> time (the source-time window scales by <paramref name="speed"/> so a
        /// sped-up clip does not compress the animation). Ripple = expanding fading circle (the
        /// tracker's constants); pulse = the same fade with the radius shrinking instead. The item's
        /// own <c>HoldSize</c>, <c>ClickSize</c> and <c>AnimationSpeed</c> scale the held dot, the
        /// sweep and the clock respectively. Drawn beneath the glyph/sprite — callers invoke this
        /// first.
        /// </summary>
        internal static void DrawClickAnimations(SKCanvas target, InputCapture capture,
            in PictureMapping map, in InputFrame row, double sourceMs, double speed,
            CursorContent cursor, double monitorScale, double opacity)
        {
            if (cursor == null || !ClickHighlight.TryParse(cursor.ClickAnimation, out bool pulse))
                return;

            if (speed <= 0)
                speed = 1.0;

            var header = capture.Header;
            var color = new SKColor(cursor.ClickColor);
            using var paint = new SKPaint { IsAntialias = true };

            if (row.Buttons != 0)
            {
                float heldRadius = (float)(ClickHighlight.HeldRadiusDip(cursor.HoldSize)
                    * monitorScale * map.ScaleX);
                double heldAlpha = ClickHighlight.MaxOpacity * (color.Alpha / 255.0) * opacity;
                paint.Color = color.WithAlpha(FrameComposer.AlphaByte(heldAlpha));
                target.DrawCircle(map.Map(row.X - header.RegionX, row.Y - header.RegionY),
                    heldRadius, paint);
            }

            double windowMs = ClickHighlight.DurationMsAt(cursor.AnimationSpeed) * speed;
            var events = capture.EventsBetween(sourceMs - windowMs, sourceMs + 0.001);
            if (events.Count == 0)
                return;

            foreach (var e in events)
            {
                if (e.Kind != InputEventKind.MouseUp)
                    continue;
                double progress = (sourceMs - e.TimeMs) / windowMs;
                if (progress < 0 || progress >= 1)
                    continue;

                float radius = (float)(ClickHighlight.RadiusDip(progress, pulse, cursor.ClickSize)
                    * monitorScale * map.ScaleX);
                double alpha = ClickHighlight.Opacity(progress) * (color.Alpha / 255.0) * opacity;

                paint.Color = color.WithAlpha(FrameComposer.AlphaByte(alpha));
                var pos = map.Map(e.X - header.RegionX, e.Y - header.RegionY);
                target.DrawCircle(pos, radius, paint);
            }
        }
    }
}
