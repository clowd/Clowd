using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using SkiaSharp;
using ModelTransform = Clowd.VideoSDK.Model.Transform;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// Where <c>FrameComposer</c> draws one item, in canvas coordinates, plus the two numbers a
    /// resize needs: the drawn picture's <see cref="Aspect"/> (height/width) and the pixel width
    /// that <see cref="ModelTransform.Scale"/> == 1 stands for
    /// (<see cref="ScaleDenominatorPx"/> — the canvas width for picture content, the text block's
    /// own natural width for a text card, which is exactly where the composer's two sizing rules
    /// differ). <see cref="ScaleDenominatorYPx"/> is the same number for the height, which only a
    /// free (aspect-unlocked) resize needs.
    /// </summary>
    internal readonly record struct PlacedItem(
        double X, double Y, double W, double H, double Aspect,
        double ScaleDenominatorPx, double ScaleDenominatorYPx)
    {
        public double Right => X + W;

        public double Bottom => Y + H;

        /// <summary>Half-open containment, so two items sharing an edge cannot both claim a point.</summary>
        public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
    }

    /// <summary>
    /// Where <c>FrameComposer</c> puts an item on a canvas — the placement half of its
    /// <c>DrawPicture</c>/<c>DrawSolid</c>/<c>DrawText</c> + <c>PlaceRect</c>, as pure geometry so
    /// the preview chrome can be positioned (and unit-tested against composed pixels) without a
    /// renderer.
    ///
    /// This exists because the transform gizmo has to land on the composed picture to the pixel:
    /// the composer places a picture at <c>Scale * canvasWidth</c> wide, derives the height from the
    /// content's own (cropped) aspect and bounds it to nothing — an overlay taller than the frame
    /// really does hang off both edges and is merely clipped. Text is the exception the gizmo has
    /// to know about: its <c>Size</c> is in output pixels, mapped onto the canvas by
    /// <c>canvasHeight / Output.HeightPx</c> (the same rule <c>FrameComposer.DrawText</c> applies),
    /// and <c>Scale</c> multiplies that measured block rather than mapping to a canvas-width
    /// fraction.
    ///
    /// Rotation is deliberately not modelled in the <b>placement</b>: the composer rotates about
    /// the item centre, so the unrotated rect is still the item's extent — the gizmo is arranged on
    /// it and then visually rotated as a whole. The click hit-test <i>is</i> rotation-aware: it
    /// unrotates the point about each candidate's centre, so a click lands on what is actually
    /// drawn there.
    /// </summary>
    internal static class ItemPlacement
    {
        /// <param name="transform">The item's transform — normalized centre and width fraction.</param>
        /// <param name="pictureAspect">The drawn picture's height/width (after any crop).</param>
        /// <param name="canvasWidth">Canvas width; for the preview, the letterboxed video rect.</param>
        /// <param name="canvasHeight">Canvas height.</param>
        /// <returns>The dest rect in canvas coordinates. Its edges may fall outside the canvas —
        /// exactly as the composed picture does, which is then simply clipped.</returns>
        public static (double X, double Y, double W, double H) Compose(
            ModelTransform transform, double pictureAspect, double canvasWidth, double canvasHeight)
        {
            ArgumentNullException.ThrowIfNull(transform);

            // FrameComposer.DrawPicture: Scale is a fraction of the canvas *width*, the height
            // follows the picture's aspect — or its own canvas fraction when the user unlocked it.
            double w = transform.Scale * canvasWidth;
            double h = transform.ScaleY is { } scaleY ? scaleY * canvasHeight : w * pictureAspect;

            return Place(transform, w, h, canvasWidth, canvasHeight);
        }

        /// <summary>The text sizing rule: <c>Scale</c> multiplies a natural block size measured in
        /// canvas pixels (<c>FrameComposer.MeasureText</c>) instead of mapping to a canvas-width
        /// fraction.</summary>
        public static (double X, double Y, double W, double H) ComposeNatural(
            ModelTransform transform, double naturalWidth, double naturalHeight,
            double canvasWidth, double canvasHeight)
        {
            ArgumentNullException.ThrowIfNull(transform);

            return Place(transform,
                naturalWidth * transform.Scale,
                naturalHeight * (transform.ScaleY ?? transform.Scale),
                canvasWidth, canvasHeight);
        }

        /// <summary>FrameComposer.PlaceRect: the dest size centred on the normalized transform.
        /// (The transition slide offset is not applied — the gizmo tracks where the item lives, not
        /// where a transition is momentarily throwing it.)</summary>
        private static (double X, double Y, double W, double H) Place(
            ModelTransform transform, double w, double h, double canvasWidth, double canvasHeight)
        {
            double cx = transform.X * canvasWidth;
            double cy = transform.Y * canvasHeight;
            return (cx - w / 2, cy - h / 2, w, h);
        }

        /// <summary>
        /// Resolves where <paramref name="item"/> is drawn on a canvas of the given size, or false
        /// when the item has no resolvable picture (an audio stream, a media stream whose size the
        /// project does not know, an image file that will not open, text that measures to nothing,
        /// a crop that removes everything). The gizmo and the preview click hit-test both key off
        /// this, so "no placement" is also "no gizmo".
        /// </summary>
        public static bool TryResolve(Project project, Item item, double canvasWidth, double canvasHeight,
            out PlacedItem placed)
        {
            placed = default;
            if (project == null || item == null || canvasWidth <= 0 || canvasHeight <= 0)
                return false;

            var transform = item.Transform ?? new ModelTransform();

            double x, y, w, h, aspect, denominator, denominatorY;
            if (item.Content is TextContent text)
            {
                var (naturalW, naturalH) = FrameComposer.MeasureText(text, canvasHeight,
                    project.Output?.HeightPx ?? 0);
                if (!(naturalW > 0) || !(naturalH > 0))
                    return false;

                aspect = naturalH / naturalW;
                denominator = naturalW;
                denominatorY = naturalH;
                (x, y, w, h) = ComposeNatural(transform, naturalW, naturalH, canvasWidth, canvasHeight);
            }
            else
            {
                var resolved = ContentAspect(project, item, canvasWidth, canvasHeight);
                if (resolved is not > 0)
                    return false;

                aspect = resolved.Value;
                denominator = canvasWidth;
                denominatorY = canvasHeight;
                (x, y, w, h) = Compose(transform, aspect, canvasWidth, canvasHeight);
            }

            if (!(w > 0) || !(h > 0) || !Double.IsFinite(x) || !Double.IsFinite(y))
                return false;

            placed = new PlacedItem(x, y, w, h, aspect, denominator, denominatorY);
            return true;
        }

        /// <summary>
        /// The drawn height/width of an item's content, or null when it is unknown or draws nothing.
        /// Media takes the probed stream dimensions (the decoded frame may be a proxy, but a proxy
        /// keeps the aspect), images the file header, a solid the canvas itself — and every one of
        /// them is <b>crop-aware</b> where the composer is: <c>DrawPicture</c> derives the height
        /// from the cropped region, so a half-width crop makes the drawn rect twice as tall for the
        /// same <c>Scale</c>.
        /// </summary>
        public static double? ContentAspect(Project project, Item item, double canvasWidth, double canvasHeight)
        {
            switch (item?.Content)
            {
                case MediaContent media:
                {
                    var source = project?.Sources?.FirstOrDefault(s => s.Id == media.SourceId);
                    var stream = source?.Streams?.FirstOrDefault(s => s.Index == media.StreamIndex);
                    if (stream is not { Kind: StreamKind.Video, Width: > 0, Height: > 0 })
                        return null;

                    return CroppedAspect(stream.Width, stream.Height, item.Transform?.Crop);
                }

                case ImageContent image:
                {
                    if (ImageSizeCache.Get(image.Path) is not { } size)
                        return null;

                    return CroppedAspect(size.Width, size.Height, item.Transform?.Crop);
                }

                case SolidContent:
                    // FrameComposer.DrawSolid: no intrinsic picture, so the natural size is the
                    // canvas (and crop does not apply to it at all).
                    return canvasWidth > 0 ? canvasHeight / canvasWidth : null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// The topmost item covering <paramref name="timeTicks"/> whose composed rect contains the
        /// point — the preview's click-to-select. Walks the visual stack the way
        /// <c>FrameComposer</c> paints it, from the top down (descending <c>Track.Order</c>, ties
        /// by id, matching the composer's total ordering reversed), skipping hidden and audio rows.
        /// Null when the click landed on bare canvas.
        /// </summary>
        public static Item HitTest(Project project, long timeTicks, double x, double y,
            double canvasWidth, double canvasHeight)
        {
            if (project?.Tracks == null || project.Items == null)
                return null;

            var tracks = new List<Track>();
            foreach (var track in project.Tracks)
            {
                if (track.Kind == TrackKind.Video && !track.Hidden)
                    tracks.Add(track);
            }

            tracks.Sort((a, b) =>
            {
                int byOrder = b.Order.CompareTo(a.Order);
                return byOrder != 0 ? byOrder : b.Id.CompareTo(a.Id);
            });

            foreach (var track in tracks)
            {
                foreach (var item in project.Items)
                {
                    if (item.TrackId != track.Id)
                        continue;
                    if (timeTicks < item.TimelineStartTicks || timeTicks >= item.TimelineEndTicks)
                        continue;
                    if (!TryResolve(project, item, canvasWidth, canvasHeight, out var placed))
                        continue;

                    // the composer rotates the picture about its centre, so test the point in the
                    // item's unrotated space — otherwise a rotated item claims its empty AABB
                    // corners and disowns the pixels it actually covers.
                    double hx = x, hy = y;
                    if (item.Transform is { Rotation: not 0 } t)
                        (hx, hy) = GizmoMath.RotateAbout(x, y,
                            placed.X + placed.W / 2, placed.Y + placed.H / 2, -t.Rotation);

                    if (placed.Contains(hx, hy))
                        return item;
                }
            }

            return null;
        }

        /// <summary>
        /// The content's own height/width with any crop ignored — what the inspector's aspect-ratio
        /// presets need: a "16:9 fill" crop is computed against the full source picture, not against
        /// whatever crop is being replaced. Null when the dimensions are unknown (same cases as
        /// <see cref="ContentAspect"/>); solids have no intrinsic picture and also answer null.
        /// </summary>
        public static double? UncroppedContentAspect(Project project, Item item)
        {
            switch (item?.Content)
            {
                case MediaContent media:
                {
                    var source = project?.Sources?.FirstOrDefault(s => s.Id == media.SourceId);
                    var stream = source?.Streams?.FirstOrDefault(s => s.Index == media.StreamIndex);
                    if (stream is not { Kind: StreamKind.Video, Width: > 0, Height: > 0 })
                        return null;

                    return (double)stream.Height / stream.Width;
                }

                case ImageContent image:
                {
                    if (ImageSizeCache.Get(image.Path) is not { } size)
                        return null;

                    return (double)size.Height / size.Width;
                }

                default:
                    return null;
            }
        }

        private static double? CroppedAspect(double width, double height, CropRect crop)
        {
            double left = 0, top = 0, right = 0, bottom = 0;
            if (crop != null)
            {
                left = Clamp01(crop.Left);
                top = Clamp01(crop.Top);
                right = Clamp01(crop.Right);
                bottom = Clamp01(crop.Bottom);
                if (left + right >= 1 || top + bottom >= 1)
                    return null; // cropped to nothing — the composer draws nothing either
            }

            double croppedW = width * (1 - left - right);
            double croppedH = height * (1 - top - bottom);
            return croppedW > 0 && croppedH > 0 ? croppedH / croppedW : null;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>
        /// Pixel sizes of <c>ImageContent</c> files, read from the codec header (never a full
        /// decode) and cached by path — the gizmo asks on every layout pass. Failures cache as null
        /// so a bad path costs one probe, exactly like the composer's own image cache.
        /// </summary>
        private static class ImageSizeCache
        {
            private static readonly object Sync = new object();

            private static readonly Dictionary<string, (int Width, int Height)?> Cache =
                new Dictionary<string, (int, int)?>(StringComparer.OrdinalIgnoreCase);

            public static (int Width, int Height)? Get(string path)
            {
                if (String.IsNullOrEmpty(path))
                    return null;

                lock (Sync)
                {
                    if (Cache.TryGetValue(path, out var cached))
                        return cached;

                    (int, int)? size = null;
                    try
                    {
                        using var codec = SKCodec.Create(path);
                        if (codec is { Info: { Width: > 0, Height: > 0 } info })
                            size = (info.Width, info.Height);
                    }
                    catch
                    {
                        size = null;
                    }

                    Cache[path] = size;
                    return size;
                }
            }
        }
    }

    /// <summary>
    /// The gizmo's pointer math, free of Avalonia types so it can be tested without a UI thread.
    /// All pixel arguments are in the preview control's own coordinate space (the space pointer
    /// positions are read in, so the gizmo moving under the pointer mid-drag cannot corrupt a
    /// delta); the canvas rectangle is the letterboxed video rect in that same space.
    /// </summary>
    internal static class GizmoMath
    {
        /// <summary>Body drag: the pointer delta since the press, as a normalized centre. Clamped
        /// to the canvas exactly as the inspector's own Position spinners are, so the two cannot
        /// disagree about how far off-frame an item may go.</summary>
        public static (double X, double Y) Move(double startX, double startY,
            double deltaXPx, double deltaYPx, double canvasWidth, double canvasHeight)
        {
            var x = canvasWidth > 0 ? startX + deltaXPx / canvasWidth : startX;
            var y = canvasHeight > 0 ? startY + deltaYPx / canvasHeight : startY;
            return (Clamp(x, 0, 1), Clamp(y, 0, 1));
        }

        /// <summary>
        /// Anchored uniform resize: the dragged corner follows the pointer, the opposite corner
        /// (<paramref name="anchorX"/>/<paramref name="anchorY"/>) stays put, and the aspect stays
        /// the content's own — the height is derived, never dragged. The candidate width is taken
        /// from whichever axis the user is pulling hardest, then clamped, then read back so the
        /// anchor really stays anchored at the clamp.
        /// </summary>
        /// <param name="scaleDenominatorPx">Pixel width that <c>Scale == 1</c> means: the canvas
        /// width for pictures, the natural block width for text (see <see cref="PlacedItem"/>).</param>
        public static (double Scale, double X, double Y) Resize(
            double pointerX, double pointerY, double anchorX, double anchorY,
            bool draggingRight, bool draggingDown, double aspect, double scaleDenominatorPx,
            double canvasX, double canvasY, double canvasWidth, double canvasHeight,
            double minScale, double maxScale)
        {
            if (!(aspect > 0))
                aspect = 9.0 / 16.0;

            var widthFromX = Math.Abs(pointerX - anchorX);
            var widthFromY = Math.Abs(pointerY - anchorY) / aspect;
            var widthPx = Math.Max(widthFromX, widthFromY);

            var scale = Clamp(scaleDenominatorPx > 0 ? widthPx / scaleDenominatorPx : minScale,
                minScale, maxScale);

            var effectiveW = scale * scaleDenominatorPx;
            var effectiveH = effectiveW * aspect;

            var centerX = anchorX + (draggingRight ? 1 : -1) * effectiveW / 2;
            var centerY = anchorY + (draggingDown ? 1 : -1) * effectiveH / 2;

            var x = canvasWidth > 0 ? (centerX - canvasX) / canvasWidth : 0.5;
            var y = canvasHeight > 0 ? (centerY - canvasY) / canvasHeight : 0.5;
            return (scale, Clamp(x, 0, 1), Clamp(y, 0, 1));
        }

        /// <summary>
        /// One axis of an anchored resize, for an item whose aspect ratio the user unlocked: the
        /// dragged edge follows the pointer, the opposite edge stays put, and the other axis is not
        /// touched at all. This is the whole of an edge-handle drag and half of a corner one.
        /// </summary>
        /// <param name="pointer">Pointer position on this axis, in preview-control pixels.</param>
        /// <param name="anchor">The opposite edge, which stays put.</param>
        /// <param name="draggingPositive">Whether the dragged edge is right of / below the anchor.</param>
        /// <param name="denominatorPx">Pixel extent that a scale of 1 means on this axis: the canvas
        /// width/height for pictures, the natural block width/height for text.</param>
        /// <returns>The scale for this axis and the item's new normalized centre on it.</returns>
        public static (double Scale, double Center) ResizeAxis(
            double pointer, double anchor, bool draggingPositive, double denominatorPx,
            double canvasOrigin, double canvasExtent, double minScale, double maxScale)
        {
            var scale = Clamp(denominatorPx > 0 ? Math.Abs(pointer - anchor) / denominatorPx : minScale,
                minScale, maxScale);

            var center = anchor + (draggingPositive ? 1 : -1) * scale * denominatorPx / 2;
            var normalized = canvasExtent > 0 ? (center - canvasOrigin) / canvasExtent : 0.5;
            return (scale, Clamp(normalized, 0, 1));
        }

        /// <summary>
        /// Anchored <b>free</b> corner resize: <see cref="ResizeAxis"/> on both axes at once, so the
        /// dragged corner really lands under the cursor instead of following whichever axis is being
        /// pulled hardest (which is what the aspect-locked <see cref="Resize"/> must do).
        /// </summary>
        public static (double ScaleX, double ScaleY, double X, double Y) ResizeFree(
            double pointerX, double pointerY, double anchorX, double anchorY,
            bool draggingRight, bool draggingDown,
            double scaleDenominatorPx, double scaleDenominatorYPx,
            double canvasX, double canvasY, double canvasWidth, double canvasHeight,
            double minScale, double maxScale)
        {
            var (scaleX, x) = ResizeAxis(pointerX, anchorX, draggingRight, scaleDenominatorPx,
                canvasX, canvasWidth, minScale, maxScale);
            var (scaleY, y) = ResizeAxis(pointerY, anchorY, draggingDown, scaleDenominatorYPx,
                canvasY, canvasHeight, minScale, maxScale);

            return (scaleX, scaleY, x, y);
        }

        /// <summary>Rotates a point about a centre by <paramref name="degrees"/> (clockwise, the
        /// composer's <c>Transform.Rotation</c> sense). Pass the negated angle to unrotate — a
        /// pointer position is mapped into a rotated item's own space this way before any of the
        /// axis-aligned resize math above sees it.</summary>
        public static (double X, double Y) RotateAbout(double x, double y,
            double centerX, double centerY, double degrees)
        {
            var rad = degrees * Math.PI / 180;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            double dx = x - centerX, dy = y - centerY;
            return (centerX + dx * cos - dy * sin, centerY + dy * cos + dx * sin);
        }

        /// <summary>
        /// The centre a rotated resize must land on so the anchor stays put <i>on screen</i>: the
        /// composer rotates about the item centre, and a resize moves that centre — so the anchored
        /// corner would orbit if the centre were derived in unrotated space alone. Solving
        /// <c>anchorVis == centre + Rot(toAnchor)</c> for the centre pins it exactly.
        /// </summary>
        /// <param name="anchorVisX">Where the anchor is drawn (rotated space, preview px).</param>
        /// <param name="toAnchorX">Centre-to-anchor vector in the item's own (unrotated) space —
        /// ±width/2, and 0 on the axis an edge handle does not touch.</param>
        /// <param name="degrees">The item's rotation.</param>
        /// <returns>The new normalized centre, clamped to the canvas like every other write.</returns>
        public static (double X, double Y) AnchoredCenter(
            double anchorVisX, double anchorVisY, double toAnchorX, double toAnchorY, double degrees,
            double canvasX, double canvasY, double canvasWidth, double canvasHeight)
        {
            var (ax, ay) = RotateAbout(toAnchorX, toAnchorY, 0, 0, degrees);
            double cx = anchorVisX - ax, cy = anchorVisY - ay;

            var x = canvasWidth > 0 ? (cx - canvasX) / canvasWidth : 0.5;
            var y = canvasHeight > 0 ? (cy - canvasY) / canvasHeight : 0.5;
            return (Clamp(x, 0, 1), Clamp(y, 0, 1));
        }

        /// <summary>Math.Clamp with NaN collapsing to the lower bound — a NaN pointer delta must not
        /// be able to poison the project (the same rule the inspector's setters use).</summary>
        private static double Clamp(double value, double min, double max) =>
            Double.IsNaN(value) ? min : Math.Clamp(value, min, max);
    }
}
