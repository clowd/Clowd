using System;
using System.Collections.Generic;
using System.IO;
using Clowd.VideoSDK.Model;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The ONLY place that knows what the picture looks like. <see cref="Compose"/> is a pure
    /// function of time: it draws the state of <paramref name="project"/> at one instant into an
    /// <see cref="SKCanvas"/>. Both the preview (Avalonia's leased canvas) and the render (an
    /// offscreen <see cref="ISurfaceFactory"/> surface) run this exact code, which is what makes
    /// the preview WYSIWYG by construction.
    ///
    /// The canvas is the project's output canvas: <paramref name="canvasWidth"/>/<paramref
    /// name="canvasHeight"/> define the coordinate space the normalized <see cref="Transform"/>
    /// geometry maps into. Render passes the exact <c>Output.WidthPx/HeightPx</c>; a preview
    /// composing at a scaled size passes the scaled dimensions (the transforms are normalized, so
    /// the picture is simply smaller — letterboxing and window placement are the caller's own
    /// canvas transform).
    /// </summary>
    public static class FrameComposer
    {
        /// <summary>
        /// Composes the project at <paramref name="timeTicks"/> (output-timeline 100ns ticks)
        /// into <paramref name="target"/>. Visual tracks composite in ascending
        /// <see cref="Track.Order"/> (higher order on top); hidden tracks and audio tracks are
        /// skipped. Media frames are pulled from <paramref name="frames"/> (items whose frames
        /// are unavailable are skipped; a null source skips all media items).
        /// </summary>
        public static void Compose(Project project, long timeTicks, IFrameSource frames,
            SKCanvas target, int canvasWidth, int canvasHeight)
        {
            ArgumentNullException.ThrowIfNull(project);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(canvasWidth, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(canvasHeight, 0);

            target.Clear(SKColors.Black);

            if (project.Tracks == null || project.Items == null)
                return;

            // bottom-up: ascending Order, ties broken by Id so the stacking is total (the same
            // tie-break Project.Normalize uses).
            var tracks = new List<Track>();
            foreach (var track in project.Tracks)
            {
                if (track.Kind == TrackKind.Video && !track.Hidden)
                    tracks.Add(track);
            }

            tracks.Sort((a, b) =>
            {
                int byOrder = a.Order.CompareTo(b.Order);
                return byOrder != 0 ? byOrder : a.Id.CompareTo(b.Id);
            });

            // TextContent.Size is in *output* pixels; composing on a differently-sized canvas (the
            // preview's letterboxed rect) must scale the font like every other content rule scales
            // its geometry, or text would be the one item drawn at a window-dependent size. The
            // factor is exactly 1.0 in the render (canvas == output).
            double textScale = TextScaleOf(project, canvasHeight);

            foreach (var track in tracks)
            {
                foreach (var item in project.Items)
                {
                    if (item.TrackId != track.Id)
                        continue;
                    if (timeTicks < item.TimelineStartTicks || timeTicks >= item.TimelineEndTicks)
                        continue;
                    ComposeItem(item, timeTicks, frames, target, canvasWidth, canvasHeight, textScale);
                }
            }
        }

        /// <summary>Canvas-height / output-height: what one output pixel of font size measures on
        /// this canvas (1.0 when composing at output resolution, or when the output is unknown).</summary>
        private static double TextScaleOf(Project project, double canvasHeight) =>
            project?.Output is { HeightPx: > 0 } output ? canvasHeight / output.HeightPx : 1.0;

        private static void ComposeItem(Item item, long timeTicks, IFrameSource frames,
            SKCanvas target, int canvasWidth, int canvasHeight, double textScale)
        {
            var transform = item.Transform ?? new Transform();
            var fx = TransitionMath.Evaluate(item, timeTicks);

            double opacity = Clamp01(transform.Opacity) * fx.Opacity;
            if (opacity <= 0)
                return;
            if (fx.HasWipe && fx.WipeFromFrac >= fx.WipeToFrac)
                return;

            switch (item.Content)
            {
                case MediaContent media:
                {
                    if (frames == null)
                        return;
                    long sourceTicks = media.SourceInTicks + (timeTicks - item.TimelineStartTicks);
                    if (!frames.TryGetFrame(media.SourceId, media.StreamIndex, sourceTicks, out var frame)
                        || frame.Image == null)
                        return;
                    DrawPicture(target, frame.Image, transform, fx, opacity, canvasWidth, canvasHeight);
                    break;
                }

                case ImageContent img:
                {
                    var image = ImageCache.Get(img.Path);
                    if (image == null)
                        return;
                    DrawPicture(target, image, transform, fx, opacity, canvasWidth, canvasHeight);
                    break;
                }

                case SolidContent solid:
                    DrawSolid(target, solid, transform, fx, opacity, canvasWidth, canvasHeight);
                    break;

                case TextContent text:
                    DrawText(target, text, transform, fx, opacity, canvasWidth, canvasHeight, textScale);
                    break;
            }
        }

        // ------------------------------------------------------------------------------- media

        private static void DrawPicture(SKCanvas target, SKImage image, Transform transform,
            ItemEffects fx, double opacity, int canvasWidth, int canvasHeight)
        {
            double imgW = image.Width, imgH = image.Height;
            if (imgW <= 0 || imgH <= 0)
                return;

            // Crop insets are fractions of the source picture, applied before Scale.
            double cl = 0, ct = 0, cr = 0, cb = 0;
            if (transform.Crop is { } crop)
            {
                cl = Clamp01(crop.Left);
                ct = Clamp01(crop.Top);
                cr = Clamp01(crop.Right);
                cb = Clamp01(crop.Bottom);
                if (cl + cr >= 1 || ct + cb >= 1)
                    return; // cropped to nothing
            }

            var src = new SKRect(
                (float)(cl * imgW), (float)(ct * imgH),
                (float)((1 - cr) * imgW), (float)((1 - cb) * imgH));

            // Scale = width fraction of the canvas; height follows the (cropped) content aspect,
            // unless the user unlocked the aspect ratio and gave the height its own fraction.
            double croppedW = imgW * (1 - cl - cr);
            double croppedH = imgH * (1 - ct - cb);
            double destW = transform.Scale * canvasWidth;
            double destH = transform.ScaleY is { } scaleY
                ? scaleY * canvasHeight
                : destW * croppedH / croppedW;

            var rect = PlaceRect(transform, fx, destW, destH, canvasWidth, canvasHeight);
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            int save = target.Save();
            try
            {
                ApplyClips(target, transform, fx, rect);
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = SKColors.White.WithAlpha(AlphaByte(opacity)),
                };
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
                target.DrawImage(image, src, rect, sampling, paint);
            }
            finally
            {
                target.RestoreToCount(save);
            }
        }

        // ------------------------------------------------------------------------------- solid

        private static void DrawSolid(SKCanvas target, SolidContent solid, Transform transform,
            ItemEffects fx, double opacity, int canvasWidth, int canvasHeight)
        {
            // A solid has no intrinsic picture; its natural size is the canvas itself, so the
            // default transform (centred, Scale 1) fills the whole frame.
            double destW = transform.Scale * canvasWidth;
            double destH = (transform.ScaleY ?? transform.Scale) * canvasHeight;

            var rect = PlaceRect(transform, fx, destW, destH, canvasWidth, canvasHeight);
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            var color = ParseColor(solid.Color, SKColors.Black);

            int save = target.Save();
            try
            {
                ApplyClips(target, transform, fx, rect);
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = color.WithAlpha(AlphaByte(opacity * color.Alpha / 255.0)),
                };
                target.DrawRect(rect, paint);
            }
            finally
            {
                target.RestoreToCount(save);
            }
        }

        // -------------------------------------------------------------------------------- text

        /// <summary>
        /// The natural (unscaled-by-<see cref="Transform.Scale"/>) size of a text card's block on a
        /// canvas of the given height, in canvas pixels — the very measurement
        /// <see cref="DrawText"/> sizes its dest rect from, exposed so the editor's transform gizmo
        /// can put a rectangle on drawn text without re-deriving it from another text stack
        /// (Avalonia's own measurement drifts from Skia's by whole pixels).
        ///
        /// <see cref="TextContent.Size"/> is in <b>output</b> pixels, so the block scales by
        /// <c>canvasHeight / outputHeightPx</c> exactly as <see cref="DrawText"/> scales the font;
        /// a non-positive <paramref name="outputHeightPx"/> measures at output resolution.
        /// <see cref="Transform.Scale"/> multiplies the block, so the caller applies the scale
        /// itself. Returns (0, 0) for text that draws nothing.
        /// </summary>
        public static (double Width, double Height) MeasureText(TextContent text,
            double canvasHeight, double outputHeightPx) =>
            MeasureTextScaled(text, outputHeightPx > 0 ? canvasHeight / outputHeightPx : 1.0);

        /// <summary>Measures at output resolution (canvas height == output height).</summary>
        public static (double Width, double Height) MeasureText(TextContent text) =>
            MeasureTextScaled(text, 1.0);

        private static (double Width, double Height) MeasureTextScaled(TextContent text, double textScale)
        {
            if (text == null || string.IsNullOrEmpty(text.Text))
                return (0, 0);

            using var typeface = CreateTypeface(text);
            using var font = new SKFont(typeface, (float)(FontSizeOf(text) * textScale)) { Subpixel = true };
            var block = LayoutText(text.Text, font);
            return (block.Width, block.Height);
        }

        private static void DrawText(SKCanvas target, TextContent text, Transform transform,
            ItemEffects fx, double opacity, int canvasWidth, int canvasHeight, double textScale)
        {
            if (string.IsNullOrEmpty(text.Text))
                return;

            using var typeface = CreateTypeface(text);
            using var font = new SKFont(typeface, (float)(FontSizeOf(text) * textScale)) { Subpixel = true };

            // the same layout MeasureText hands the editor — the gizmo's rect cannot drift from the
            // drawn one because there is only one measurement.
            var block = LayoutText(text.Text, font);
            string[] lines = block.Lines;
            float blockW = block.Width;
            float lineHeight = block.LineHeight;
            float blockH = block.Height;
            if (blockW <= 0 || blockH <= 0)
                return;

            // Text sizes in output pixels (TextContent.Size, mapped onto this canvas by textScale
            // above), so unlike picture content Scale here multiplies the natural block size
            // rather than mapping to a canvas-width fraction — Scale 1 draws the text at its
            // font size. ScaleY does the same to the height when the aspect ratio is unlocked.
            double scaleX = transform.Scale;
            double scaleY = transform.ScaleY ?? transform.Scale;
            double destW = blockW * scaleX;
            double destH = blockH * scaleY;

            var rect = PlaceRect(transform, fx, destW, destH, canvasWidth, canvasHeight);
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            var color = ParseColor(text.Color, SKColors.White);

            int save = target.Save();
            try
            {
                ApplyClips(target, transform, fx, rect);
                target.Translate(rect.Left, rect.Top);
                target.Scale((float)scaleX, (float)scaleY);

                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = color.WithAlpha(AlphaByte(opacity * color.Alpha / 255.0)),
                };

                var align = text.Align switch
                {
                    TextAlign.Center => SKTextAlign.Center,
                    TextAlign.Right => SKTextAlign.Right,
                    _ => SKTextAlign.Left,
                };
                float x = text.Align switch
                {
                    TextAlign.Center => blockW / 2,
                    TextAlign.Right => blockW,
                    _ => 0,
                };

                float baseline = -font.Metrics.Ascent;
                for (int i = 0; i < lines.Length; i++)
                    target.DrawText(lines[i], x, baseline + i * lineHeight, align, font, paint);
            }
            finally
            {
                target.RestoreToCount(save);
            }
        }

        private static float FontSizeOf(TextContent text) => text.Size > 0 ? (float)text.Size : 32f;

        private static SKTypeface CreateTypeface(TextContent text) =>
            text.Font != null ? SKTypeface.FromFamilyName(text.Font) : SKTypeface.CreateDefault();

        /// <summary>One measured text block: the lines, the widest line's advance width, and the
        /// line pitch (<see cref="SKFont.Spacing"/>, which includes the font's leading — the block
        /// is deliberately taller than the ink).</summary>
        private readonly struct TextBlock
        {
            public TextBlock(string[] lines, float width, float lineHeight)
            {
                Lines = lines;
                Width = width;
                LineHeight = lineHeight;
            }

            public string[] Lines { get; }

            public float Width { get; }

            public float LineHeight { get; }

            public float Height => Lines.Length * LineHeight;
        }

        private static TextBlock LayoutText(string text, SKFont font)
        {
            string[] lines = text.Split('\n');
            float width = 0;
            foreach (var line in lines)
                width = Math.Max(width, font.MeasureText(line));
            return new TextBlock(lines, width, font.Spacing);
        }

        // ---------------------------------------------------------------------------- geometry

        /// <summary>Places a dest rect of the given size: centred at the normalized
        /// <see cref="Transform.X"/>/<see cref="Transform.Y"/>, shifted by the transition's
        /// slide offset (fractions of the item's own extent).</summary>
        private static SKRect PlaceRect(Transform transform, ItemEffects fx,
            double destW, double destH, int canvasWidth, int canvasHeight)
        {
            double cx = transform.X * canvasWidth + fx.OffsetXFrac * destW;
            double cy = transform.Y * canvasHeight + fx.OffsetYFrac * destH;
            return new SKRect(
                (float)(cx - destW / 2), (float)(cy - destH / 2),
                (float)(cx + destW / 2), (float)(cy + destH / 2));
        }

        /// <summary>Rotation about the item centre, then the mask clip, then the wipe clip —
        /// all in the rotated space, so mask and wipe travel with the picture. Caller must
        /// Save/Restore around this.</summary>
        private static void ApplyClips(SKCanvas target, Transform transform, ItemEffects fx, SKRect rect)
        {
            if (transform.Rotation != 0)
                target.RotateDegrees((float)transform.Rotation, rect.MidX, rect.MidY);

            if (transform.Mask is { } mask)
            {
                if (mask.Shape == MaskShape.Circle)
                {
                    // The ellipse inscribed in the item rect — NOT a min(w,h)/2 circle. v1's mask
                    // PNGs and the editor's preview both inscribe an ellipse in the webcam rect
                    // (a circle only when the rect is square), and vid-render applied those PNGs
                    // verbatim; the parity gate (design step 9) measured a 23 dB divergence on a
                    // wide rect when this drew a true circle.
                    using var path = new SKPath();
                    path.AddOval(rect);
                    target.ClipPath(path, SKClipOperation.Intersect, antialias: true);
                }
                else if (mask.Shape == MaskShape.Squircle)
                {
                    using var path = SquirclePath(rect);
                    target.ClipPath(path, SKClipOperation.Intersect, antialias: true);
                }
                else
                {
                    float radius = (float)(Clamp01(mask.CornerRadius) * rect.Height);
                    using var rr = new SKRoundRect(rect, radius, radius);
                    target.ClipRoundRect(rr, SKClipOperation.Intersect, antialias: true);
                }
            }

            if (fx.HasWipe)
            {
                var band = new SKRect(
                    rect.Left + (float)(fx.WipeFromFrac * rect.Width), rect.Top,
                    rect.Left + (float)(fx.WipeToFrac * rect.Width), rect.Bottom);
                target.ClipRect(band, SKClipOperation.Intersect, antialias: true);
            }
        }

        /// <summary>The superellipse inscribed in the item rect, as a closed path — inscribed for
        /// the same reason <see cref="MaskShape.Circle"/> is, so a wide item gets a wide squircle.</summary>
        private static SKPath SquirclePath(SKRect rect)
        {
            Span<double> xy = stackalloc double[MaskGeometry.SquircleSegments * 2];
            MaskGeometry.BuildSquircle(rect.MidX, rect.MidY, rect.Width / 2, rect.Height / 2, xy);

            var path = new SKPath();
            path.MoveTo((float)xy[0], (float)xy[1]);
            for (int i = 1; i < MaskGeometry.SquircleSegments; i++)
                path.LineTo((float)xy[i * 2], (float)xy[i * 2 + 1]);
            path.Close();
            return path;
        }

        // ----------------------------------------------------------------------------- helpers

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        private static byte AlphaByte(double opacity)
            => (byte)Math.Clamp((int)Math.Round(Clamp01(opacity) * 255), 0, 255);

        /// <summary>Parses a <c>#AARRGGBB</c> (or <c>#RRGGBB</c>) model color string.</summary>
        internal static SKColor ParseColor(string color, SKColor fallback)
        {
            if (!string.IsNullOrWhiteSpace(color) && SKColor.TryParse(color, out var parsed))
                return parsed;
            return fallback;
        }

        /// <summary>
        /// Process-wide decode cache for <see cref="ImageContent"/> stills, keyed by path.
        /// Decoded images are raster (context-free), so they are safe to draw on any backend.
        /// Missing/undecodable files are cached as null so a bad path costs one probe, not one
        /// per composed frame. Deliberately unbounded: it holds at most the distinct image paths
        /// of open projects.
        /// </summary>
        private static class ImageCache
        {
            private static readonly object Sync = new object();
            private static readonly Dictionary<string, SKImage> Cache
                = new Dictionary<string, SKImage>(StringComparer.OrdinalIgnoreCase);

            public static SKImage Get(string path)
            {
                if (string.IsNullOrEmpty(path))
                    return null;

                lock (Sync)
                {
                    if (Cache.TryGetValue(path, out var cached))
                        return cached;

                    SKImage image = null;
                    try
                    {
                        if (File.Exists(path))
                            image = SKImage.FromEncodedData(path);
                    }
                    catch
                    {
                        image = null;
                    }

                    Cache[path] = image;
                    return image;
                }
            }
        }
    }
}
