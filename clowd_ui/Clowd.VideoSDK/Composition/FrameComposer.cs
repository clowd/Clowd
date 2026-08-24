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
                // zoom rows above this track scale its whole picture about their focal points;
                // the matrix is canvas-local, so the caller's own letterbox transform composes.
                var zoom = ZoomMath.EffectiveMatrix(project, timeTicks, track.Order, canvasWidth, canvasHeight);

                foreach (var item in project.Items)
                {
                    if (item.TrackId != track.Id)
                        continue;
                    if (timeTicks < item.TimelineStartTicks || timeTicks >= item.TimelineEndTicks)
                        continue;

                    // a cursor item borrows all of its geometry from the linked screen item
                    // (DrawCursorItem maps through the screen's PictureMapping), so it must
                    // borrow the screen row's zoom too — a zoom row reordered to sit between
                    // the screen row and the cursor row would otherwise zoom the pixels and
                    // leave the cursor pointing at the wrong content.
                    var itemZoom = item.Content is CursorContent cursorContent
                        ? CursorItemZoom(project, item, cursorContent, timeTicks, zoom, canvasWidth, canvasHeight)
                        : zoom;

                    int save = target.Save();
                    try
                    {
                        // keyboard overlays caption the viewport, not the picture — they alone
                        // ride outside the zoom.
                        if (!itemZoom.IsIdentity && item.Content is not KeyboardContent)
                            target.Concat(in itemZoom);
                        ComposeItem(project, item, timeTicks, frames, target, canvasWidth, canvasHeight, textScale);
                    }
                    finally
                    {
                        target.RestoreToCount(save);
                    }
                }
            }
        }

        /// <summary>
        /// The zoom matrix a cursor item composes under: evaluated at its linked <b>screen</b>
        /// track's <see cref="Track.Order"/>, not the cursor row's own — the cursor's placement is
        /// only correct when both receive the identical matrix, and an effect row whose order
        /// falls between the two rows would otherwise split them. Falls back to the cursor row's
        /// own matrix when the screen item is gone (the item draws nothing then anyway).
        /// </summary>
        private static SKMatrix CursorItemZoom(Project project, Item item, CursorContent cursor,
            long timeTicks, SKMatrix fallback, int canvasWidth, int canvasHeight)
        {
            var source = FindSource(project, cursor.SourceId);
            var screen = source == null ? null
                : FindScreenMediaItem(project, source, item.LinkGroupId, timeTicks);
            if (screen == null)
                return fallback;

            foreach (var track in project.Tracks)
            {
                if (track.Id == screen.TrackId)
                    return ZoomMath.EffectiveMatrix(project, timeTicks, track.Order, canvasWidth, canvasHeight);
            }
            return fallback;
        }

        /// <summary>Canvas-height / output-height: what one output pixel of font size measures on
        /// this canvas (1.0 when composing at output resolution, or when the output is unknown).</summary>
        private static double TextScaleOf(Project project, double canvasHeight) =>
            project?.Output is { HeightPx: > 0 } output ? canvasHeight / output.HeightPx : 1.0;

        private static void ComposeItem(Project project, Item item, long timeTicks, IFrameSource frames,
            SKCanvas target, int canvasWidth, int canvasHeight, double textScale)
        {
            var transform = item.Transform ?? new Transform();
            // a keystroke overlay spends its Entry/Exit on its rows, one clock each, inside
            // DrawKeyboard — applying them to the block as well would animate everything twice.
            var fx = item.Content is KeyboardContent
                ? ItemEffects.Identity
                : TransitionMath.Evaluate(item, timeTicks);

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
                    // elapsed timeline time consumes source time at the item's playback speed
                    long sourceTicks = SourceTimeTicks(media, item, timeTicks);
                    if (!frames.TryGetFrame(media.SourceId, media.StreamIndex, sourceTicks, out var frame)
                        || frame.Image == null)
                        return;
                    DrawPicture(target, frame.Image, frame.Mask, transform, item.Surround,
                        item.Effect, fx, opacity, canvasWidth, canvasHeight);
                    DrawDefaultCursorOverlay(project, media, sourceTicks,
                        transform, fx, opacity, target, canvasWidth, canvasHeight);
                    break;
                }

                case ImageContent img:
                {
                    var image = ImageCache.Get(img.Path);
                    if (image == null)
                        return;
                    DrawPicture(target, image, null, transform, item.Surround,
                        item.Effect, fx, opacity, canvasWidth, canvasHeight);
                    break;
                }

                case SolidContent solid:
                    DrawSolid(target, solid, transform, fx, opacity, canvasWidth, canvasHeight);
                    break;

                case TextContent text:
                    DrawText(target, text, transform, fx, opacity, canvasWidth, canvasHeight, textScale);
                    break;

                case CursorContent cursor:
                    DrawCursorItem(project, item, cursor, timeTicks, frames, target, opacity,
                        canvasWidth, canvasHeight);
                    break;

                case KeyboardContent keyboard:
                    DrawKeyboard(project, item, keyboard, timeTicks, target, transform, opacity,
                        canvasWidth, canvasHeight, textScale);
                    break;
            }
        }

        /// <summary>Source time for a media item at output time <paramref name="timeTicks"/> —
        /// exact for realtime so speed-1 projects keep integer-perfect math.</summary>
        private static long SourceTimeTicks(MediaContent media, Item item, long timeTicks)
        {
            long elapsed = timeTicks - item.TimelineStartTicks;
            double speed = TimelineOps.SpeedOf(media);
            return media.SourceInTicks + (speed == 1.0 ? elapsed : (long)Math.Round(elapsed * speed));
        }

        // ------------------------------------------------------------------------------- media

        /// <summary>
        /// A video frame or a still, placed by its mapping, wearing the item's
        /// <paramref name="effect"/> on its own pixels and its <paramref name="surround"/> outside
        /// them. It is drawn from the <b>masked</b> silhouette but outside the
        /// mask itself: its layer opens before <see cref="ApplyClips"/>, so the clip shapes what
        /// casts the shadow while the shadow is free to fall beyond it. A surround is a
        /// decoration-only filter (see <see cref="SurroundMath"/>), so the picture is drawn
        /// twice — once to cast, once to be seen — and the effect runs in both passes, so a
        /// removed background casts no shadow.
        /// </summary>
        private static void DrawPicture(SKCanvas target, SKImage image, SKImage mask,
            Transform transform, Surround surround, VideoEffect effect, ItemEffects fx,
            double opacity, int canvasWidth, int canvasHeight)
        {
            // crop/aspect insets, dest rect and the px→canvas factors all live in the mapping —
            // shared with the cursor overlay, which maps captured positions through the same math.
            if (!PictureMapping.TryMap(transform, fx, image.Width, image.Height,
                    canvasWidth, canvasHeight, out var map))
                return;

            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            using var decoration = SurroundMath.CreateDecoration(
                surround, SurroundMath.ReferenceExtent(map.Dest));

            // pass 0 paints the decoration (skipped when there is none), pass 1 the picture itself
            for (int pass = decoration != null ? 0 : 1; pass <= 1; pass++)
            {
                int save = target.Save();
                try
                {
                    if (pass == 0)
                    {
                        // no bounds hint on the layer: the rotation lives inside it (ApplyClips),
                        // so an axis-aligned rect measured out here would clip a rotated item's
                        // surround. A surround therefore costs a canvas-sized layer per frame —
                        // which is why the filter is built once, above, and skipped when it draws
                        // nothing.
                        using var layer = new SKPaint
                        {
                            ImageFilter = decoration,
                            Color = SKColors.White.WithAlpha(AlphaByte(opacity)),
                        };
                        target.SaveLayer(layer);
                    }

                    ApplyClips(target, transform, fx, map.Dest);
                    // the decoration pass carries the item's opacity on its layer instead, so
                    // a translucent item's shadow fades once rather than twice
                    DrawEffected(target, image, mask, effect, map, sampling, pass == 0 ? 1 : opacity);
                }
                finally
                {
                    target.RestoreToCount(save);
                }
            }
        }

        /// <summary>
        /// Blur strength mapping: <see cref="VideoEffect.Amount"/> 1.0 blurs at a sigma of 1/16 of
        /// the item's reference extent (<see cref="SurroundMath.ReferenceExtent"/> — the dest
        /// rect's shorter side), the same canvas-relative rule every surround dial uses, so the
        /// blur reads identically at every compose size and preview==render by construction.
        /// 1/16 at Amount 1.0 is a heavy blur (a 1080-tall item at sigma ~67 — silhouettes only);
        /// the default 0.5 is a solid privacy blur that keeps the scene's shapes.
        /// </summary>
        private const double MaxBlurSigmaFraction = 1.0 / 16;

        /// <summary>The blur filter for an effect over a dest rect, or null when the dial rounds
        /// to no blur. Clamp tiling keeps edge pixels opaque instead of bleeding transparency in
        /// from the picture's border (and confines the output to the picture's bounds).</summary>
        private static SKImageFilter BlurFilter(VideoEffect effect, SKRect dest)
        {
            double amount = effect.Amount;
            double sigma = (Double.IsNaN(amount) ? 0 : Math.Clamp(amount, 0, 1))
                * MaxBlurSigmaFraction * SurroundMath.ReferenceExtent(dest);
            return sigma > 0
                ? SKImageFilter.CreateBlur((float)sigma, (float)sigma, SKShaderTileMode.Clamp)
                : null;
        }

        /// <summary>
        /// The item's own pixels wearing its <see cref="VideoEffect"/>: plain, blurred, or split
        /// into a blurred background under the matte-sharp subject. A segmented kind with no mask
        /// degrades to the plain draw — a missing or stale sidecar must never block playback or
        /// render, and the plain picture is the least surprising stand-in. Runs once per
        /// <see cref="DrawPicture"/> pass, inside its clips.
        /// </summary>
        private static void DrawEffected(SKCanvas target, SKImage image, SKImage mask,
            VideoEffect effect, in PictureMapping map, in SKSamplingOptions sampling, double opacity)
        {
            var kind = effect?.Kind ?? VideoEffectKind.None;
            if (VideoEffect.NeedsMatte(kind) && mask == null)
                kind = VideoEffectKind.None;

            switch (kind)
            {
                case VideoEffectKind.Blur:
                {
                    using var blur = BlurFilter(effect, map.Dest);
                    using var paint = new SKPaint
                    {
                        IsAntialias = true,
                        Color = SKColors.White.WithAlpha(AlphaByte(opacity)),
                        ImageFilter = blur,
                    };
                    target.DrawImage(image, map.Source, map.Dest, sampling, paint);
                    break;
                }

                case VideoEffectKind.BgBlur:
                {
                    // background blurred, subject sharp on top. Both draws run at full alpha
                    // inside one opacity layer, so a translucent item fades as one picture
                    // rather than the subject blending against its own blurred background —
                    // and the layer is skipped entirely at full opacity, the common case.
                    int save = target.Save();
                    try
                    {
                        if (opacity < 1)
                        {
                            using var group = new SKPaint
                            {
                                Color = SKColors.White.WithAlpha(AlphaByte(opacity)),
                            };
                            target.SaveLayer(map.Dest, group);
                        }

                        using (var blur = BlurFilter(effect, map.Dest))
                        using (var paint = new SKPaint { IsAntialias = true, ImageFilter = blur })
                            target.DrawImage(image, map.Source, map.Dest, sampling, paint);
                        DrawMasked(target, image, mask, map, sampling, 1);
                    }
                    finally
                    {
                        target.RestoreToCount(save);
                    }
                    break;
                }

                case VideoEffectKind.BgRemove:
                    DrawMasked(target, image, mask, map, sampling, opacity);
                    break;

                default:
                {
                    using var paint = new SKPaint
                    {
                        IsAntialias = true,
                        Color = SKColors.White.WithAlpha(AlphaByte(opacity)),
                    };
                    target.DrawImage(image, map.Source, map.Dest, sampling, paint);
                    break;
                }
            }
        }

        /// <summary>
        /// The frame multiplied by its person matte: the sharp picture inside a dest-bounded
        /// layer, then the matte drawn over it with luma-as-alpha
        /// (<see cref="SKColorFilter.CreateLumaColor"/>) in <see cref="SKBlendMode.DstIn"/> —
        /// a plain color filter + blend, so GPU and raster backends produce the same pixels. The
        /// matte decodes at analysis resolution, so its source rect is the frame's crop
        /// re-expressed as the same fractions of the matte's own pixels — the mapping is shared,
        /// the resolutions are not.
        /// </summary>
        private static void DrawMasked(SKCanvas target, SKImage image, SKImage mask,
            in PictureMapping map, in SKSamplingOptions sampling, double opacity)
        {
            int save = target.Save();
            try
            {
                // bounded to the dest rect (mapped through the CTM, so rotation is fine): DstIn
                // must only multiply the item's own pixels, never what is already composed under
                // them.
                using (var layer = new SKPaint { Color = SKColors.White.WithAlpha(AlphaByte(opacity)) })
                    target.SaveLayer(map.Dest, layer);

                using (var paint = new SKPaint { IsAntialias = true })
                    target.DrawImage(image, map.Source, map.Dest, sampling, paint);

                var maskSource = new SKRect(
                    (float)(map.Source.Left / image.Width * mask.Width),
                    (float)(map.Source.Top / image.Height * mask.Height),
                    (float)(map.Source.Right / image.Width * mask.Width),
                    (float)(map.Source.Bottom / image.Height * mask.Height));
                using var luma = SKColorFilter.CreateLumaColor();
                using var maskPaint = new SKPaint
                {
                    IsAntialias = true,
                    BlendMode = SKBlendMode.DstIn,
                    ColorFilter = luma,
                };
                target.DrawImage(mask, maskSource, map.Dest, sampling, maskPaint);
            }
            finally
            {
                target.RestoreToCount(save);
            }
        }

        // ---------------------------------------------------------------------- cursor overlay

        /// <summary>
        /// The default native cursor: a recording that carries an input-capture sidecar
        /// (<see cref="Source.InputCapturePath"/>) composites the recorded cursor sprite over its
        /// screen item even without a cursor track — the screen stream is captured cursor-less, so
        /// the cursor would otherwise simply be lost. Drawn through the screen item's own
        /// <see cref="PictureMapping"/> and clips — the sprite lands exactly where the recorder
        /// sampled it, crop/aspect included. Suppressed the moment the project holds any
        /// <see cref="CursorContent"/> item for the source — active at this instant or not: once a
        /// cursor track exists, it owns the cursor outright, so a gap the user cut into it means
        /// "no cursor here", not "back to the default" (deleting the track's last item restores
        /// the default). Also skipped for hidden cursors (always debounced — see
        /// <see cref="InputCapture.IsInactiveAt"/>), positions outside the capture region and
        /// frames carrying no sprite (a v1 file or a degraded capture).
        /// </summary>
        private static void DrawDefaultCursorOverlay(Project project, MediaContent media,
            long sourceTicks, Transform transform, ItemEffects fx, double opacity, SKCanvas target, int canvasWidth, int canvasHeight)
        {
            var source = FindSource(project, media.SourceId);
            if (source == null || string.IsNullOrEmpty(source.InputCapturePath))
                return;
            if (!IsScreenStream(source, media.StreamIndex))
                return; // the overlay belongs to the screen stream, not webcam items
            if (HasCursorItem(project, media.SourceId))
                return;

            var capture = InputCapture.Get(source.InputCapturePath);
            int rowIndex = capture.LatestAtOrBefore(sourceTicks / (double)TimeSpan.TicksPerMillisecond);
            if (rowIndex < 0)
                return;
            var row = capture.Frames[rowIndex];
            // debounced, not the raw Hidden bit: the default overlay has no setting to turn this
            // off — the typing flicker should never survive without a cursor track to opt out on.
            if (row.Cursor == CursorKind.Hidden
                || capture.IsInactiveAt(rowIndex)
                || !CursorCompose.IsInsideRegion(capture.Header, row.X, row.Y))
                return;

            if (row.SpriteId < 0 || !capture.TryGetSprite(row.SpriteId, out var sprite))
                return;
            // the stream's own dimensions, never the presented frame's: a preview presents
            // frames under IVideoPlayer.MaxPresentHeight, and a mapping resolved against a
            // downscaled image would place captured (physical-pixel) positions off by that
            // downscale factor. DrawCursorItem resolves the same way.
            var (imgW, imgH) = ScreenDims(source, media.StreamIndex, capture.Header);
            if (!PictureMapping.TryMap(transform, fx, imgW, imgH,
                    canvasWidth, canvasHeight, out var map))
                return;

            int save = target.Save();
            try
            {
                ApplyClips(target, transform, fx, map.Dest);
                target.ClipRect(map.Dest, SKClipOperation.Intersect, antialias: true);
                CursorCompose.DrawNativeSprite(target, sprite, map,
                    row.X - capture.Header.RegionX, row.Y - capture.Header.RegionY, 1.0, opacity);
            }
            finally
            {
                target.RestoreToCount(save);
            }
        }

        /// <summary>
        /// A cursor-track item: position and shape are data-driven (the capture file), placement
        /// rides the linked screen item's mapping — the cursor lands on the same canvas point the
        /// recorded pixel would, crop/aspect/mask included. <c>none</c> draws no cursor at all
        /// (the click highlight, its own setting, still does); <c>native</c> draws the frame's
        /// recorded sprite (<see cref="InputFrame.SpriteId"/>) at its captured pixel size times
        /// <c>Size</c>; every other style draws a themed glyph (an animated one — the wait and
        /// appstarting spinners — showing the frame project time selects) sized by
        /// <c>Size · 40 px · monitor scale</c> in source pixels, then through the same px→canvas
        /// factor as the screen frame. The native style ignores the item's shadow surround: the OS
        /// pointer shadow is DWM-composited, never part of the cursor shape the recorder
        /// rasterizes (routing the sprite through <see cref="CursorCompose.DrawGlyph"/>'s
        /// decoration layer is the easy follow-up if one is wanted). The click highlight draws
        /// beneath. A hidden cursor (debounced through the capture's inactivity latch unless the
        /// item's <c>Debounce</c> is off), a position outside the region, or a missing screen item
        /// draws nothing.
        /// </summary>
        private static void DrawCursorItem(Project project, Item item, CursorContent cursor,
            long timeTicks, IFrameSource frames, SKCanvas target, double opacity,
            int canvasWidth, int canvasHeight)
        {
            var source = FindSource(project, cursor.SourceId);
            if (source == null || string.IsNullOrEmpty(source.InputCapturePath))
                return;
            var capture = InputCapture.Get(source.InputCapturePath);
            if (capture.Frames.Count == 0)
                return;

            // the linked screen item defines both the time mapping and the placement math —
            // hard sync means the cursor has no clock or geometry of its own.
            var screen = FindScreenMediaItem(project, source, item.LinkGroupId, timeTicks);
            if (screen?.Content is not MediaContent media)
                return;
            long sourceTicks = SourceTimeTicks(media, screen, timeTicks);
            double sourceMs = sourceTicks / (double)TimeSpan.TicksPerMillisecond;

            var (imgW, imgH) = ScreenDims(source, media.StreamIndex, capture.Header);
            var screenTransform = screen.Transform ?? new Transform();
            var screenFx = TransitionMath.Evaluate(screen, timeTicks);
            if (!PictureMapping.TryMap(screenTransform, screenFx, imgW, imgH,
                    canvasWidth, canvasHeight, out var map))
                return;

            int rowIndex = capture.LatestAtOrBefore(sourceMs);
            if (rowIndex < 0)
                return;
            var row = capture.Frames[rowIndex];
            if (row.Cursor == CursorKind.Hidden
                || (cursor.Debounce && capture.IsInactiveAt(rowIndex))
                || !CursorCompose.IsInsideRegion(capture.Header, row.X, row.Y))
                return;

            var header = capture.Header;
            double monitorScale = CursorCompose.MonitorScaleAt(header, row.X, row.Y);

            int save = target.Save();
            try
            {
                ApplyClips(target, screenTransform, screenFx, map.Dest);
                target.ClipRect(map.Dest, SKClipOperation.Intersect, antialias: true);

                // the press warp redraws (a stretched copy of) the screen frame itself, so it
                // needs the frame the screen item just composed; items between the two rows are
                // not re-warped — the cursor row normally sits directly above its screen row.
                if (ClickHighlight.ModeOf(cursor.ClickAnimation) == HighlightMode.Press
                    && frames != null
                    && frames.TryGetFrame(media.SourceId, media.StreamIndex, sourceTicks, out var screenFrame)
                    && screenFrame.Image != null)
                {
                    CursorCompose.DrawPressWarp(target, capture, map, imgW, imgH, row, sourceMs,
                        TimelineOps.SpeedOf(media), cursor, monitorScale, opacity, screenFrame.Image);
                }

                CursorCompose.DrawClickAnimations(target, capture, map, row, sourceMs,
                    TimelineOps.SpeedOf(media), cursor, monitorScale, opacity);

                if (string.Equals(cursor.Style, CursorAssets.NoneStyle, StringComparison.OrdinalIgnoreCase))
                {
                    // deliberately nothing — the style that hides the cursor outright. The click
                    // highlight above still draws: it is its own subject with its own "none".
                }
                else if (string.Equals(cursor.Style, CursorAssets.NativeStyle, StringComparison.OrdinalIgnoreCase))
                {
                    if (row.SpriteId >= 0 && capture.TryGetSprite(row.SpriteId, out var sprite))
                    {
                        CursorCompose.DrawNativeSprite(target, sprite, map,
                            row.X - header.RegionX, row.Y - header.RegionY,
                            cursor.Size > 0 ? cursor.Size : 1.0, opacity);
                    }
                }
                else
                {
                    // an animated glyph (wait/appstarting spinners) picks its frame by *project*
                    // time, the click animations' clock: constant spin through speed-warped clips,
                    // and scrub/preview/render agree because the frame is a pure function of t.
                    var glyph = CursorCompose.ResolveGlyph(cursor.Style, cursor.Variant, row.Cursor)
                        ?.FrameAt(timeTicks / (double)TimeSpan.TicksPerMillisecond);
                    if (glyph != null)
                    {
                        double sizeSourcePx = (cursor.Size > 0 ? cursor.Size : 1.0)
                            * CursorCompose.BaseCursorPx * monitorScale;
                        var pos = map.Map(row.X - header.RegionX, row.Y - header.RegionY);
                        CursorCompose.DrawGlyph(target, glyph, pos,
                            (float)(sizeSourcePx * map.ScaleX), item.Surround, opacity);
                    }
                }
            }
            finally
            {
                target.RestoreToCount(save);
            }
        }

        /// <summary>Whether the project holds any <see cref="CursorContent"/> item for the source,
        /// anywhere on the timeline — the cursor track owns the cursor then and the default
        /// overlay stands down everywhere: in a gap the user trimmed into the track, under a
        /// hidden track, all of it deliberate. Only deleting the track's last cursor item hands
        /// the cursor back to the default overlay.</summary>
        internal static bool HasCursorItem(Project project, Guid sourceId)
        {
            if (project?.Items == null)
                return false;
            foreach (var item in project.Items)
            {
                if (item.Content is CursorContent cursor && cursor.SourceId == sourceId)
                    return true;
            }
            return false;
        }

        private static Source FindSource(Project project, Guid sourceId)
        {
            if (project?.Sources == null)
                return null;
            foreach (var source in project.Sources)
            {
                if (source.Id == sourceId)
                    return source;
            }
            return null;
        }

        /// <summary>Whether the stream is the source's screen recording: its lowest-index probed
        /// video stream (the webcam always probes after the screen). With no probed video streams,
        /// stream 0 — the container convention.</summary>
        public static bool IsScreenStream(Source source, int streamIndex)
        {
            int best = -1;
            if (source.Streams != null)
            {
                foreach (var stream in source.Streams)
                {
                    if (stream.Kind != StreamKind.Video)
                        continue;
                    if (best < 0 || stream.Index < best)
                        best = stream.Index;
                }
            }
            return best < 0 ? streamIndex == 0 : streamIndex == best;
        }

        /// <summary>
        /// The screen media item an overlay item follows: an active-at-<paramref name="timeTicks"/>
        /// screen-stream item of the source on a video track, preferring the overlay's own link
        /// group (the hard-sync partner) when several qualify. Null when the screen row is gone —
        /// overlays then draw nothing (cursor) or fall back to item-relative time (keyboard).
        /// </summary>
        internal static Item FindScreenMediaItem(Project project, Source source,
            Guid? linkGroupId, long timeTicks)
        {
            if (project?.Items == null || project.Tracks == null || source == null)
                return null;

            var videoTracks = new HashSet<Guid>();
            foreach (var track in project.Tracks)
            {
                if (track.Kind == TrackKind.Video)
                    videoTracks.Add(track.Id);
            }

            Item best = null;
            foreach (var item in project.Items)
            {
                if (item.Content is not MediaContent media || media.SourceId != source.Id)
                    continue;
                if (timeTicks < item.TimelineStartTicks || timeTicks >= item.TimelineEndTicks)
                    continue;
                if (!videoTracks.Contains(item.TrackId) || !IsScreenStream(source, media.StreamIndex))
                    continue;
                if (linkGroupId != null && item.LinkGroupId == linkGroupId)
                    return item;
                best ??= item;
            }
            return best;
        }

        /// <summary>The screen stream's pixel dimensions: the probe's numbers (what
        /// <see cref="DrawPicture"/>'s frames decode to), else the capture region — enough to map
        /// even when the probe is missing.</summary>
        private static (double Width, double Height) ScreenDims(Source source, int streamIndex,
            InputCaptureHeader header)
        {
            if (source.Streams != null)
            {
                foreach (var stream in source.Streams)
                {
                    if (stream.Index == streamIndex && stream.Width > 0 && stream.Height > 0)
                        return (stream.Width, stream.Height);
                }
            }
            return (header.RegionWidth, header.RegionHeight);
        }

        // ---------------------------------------------------------------------------- keyboard

        /// <summary>
        /// The pill geometry of one keyboard row, derived from the font size in canvas pixels:
        /// the padding round the text, the gap between stacked rows, the row's own height and the
        /// pill's corner radius. It lives in one place because two callers measure with it — the
        /// drawing below, and the editor's transform gizmo, which boxes the block through
        /// <see cref="MeasureKeyboardHeight"/> and would otherwise drift from the drawn pills the
        /// first time a proportion here was touched.
        /// </summary>
        internal readonly struct KeyboardMetrics
        {
            public KeyboardMetrics(float fontPx, float lineSpacing)
            {
                PadH = fontPx * 0.55f;
                PadV = fontPx * 0.30f;
                Gap = fontPx * 0.25f;
                RowHeight = lineSpacing + 2 * PadV;
            }

            /// <summary>Horizontal padding inside a pill, each side.</summary>
            public float PadH { get; }

            /// <summary>Vertical padding inside a pill, above and below the line.</summary>
            public float PadV { get; }

            /// <summary>Vertical space between two stacked pills.</summary>
            public float Gap { get; }

            /// <summary>One pill's height: the line pitch plus its vertical padding.</summary>
            public float RowHeight { get; }

            public float CornerRadius => RowHeight * 0.3f;

            /// <summary>The block height of <paramref name="rows"/> stacked pills, gaps
            /// included.</summary>
            public float BlockHeight(int rows) =>
                rows <= 0 ? 0 : rows * RowHeight + (rows - 1) * Gap;
        }

        /// <summary><see cref="KeyboardContent.FontSize"/> — output pixels, mapped onto this canvas
        /// by <paramref name="textScale"/> exactly as <see cref="TextContent"/> is — with the
        /// model's default standing in for a non-positive size.</summary>
        private static float KeyboardFontPx(KeyboardContent keyboard, double textScale) =>
            (float)((keyboard.FontSize > 0 ? keyboard.FontSize : KeyboardContent.DefaultFontSize) * textScale);

        /// <summary>
        /// The drawn height of a keyboard overlay of <paramref name="rows"/> pill rows on a canvas
        /// of the given height, in canvas pixels — measured from the very metrics
        /// <see cref="DrawKeyboard"/> lays the pills out on, and exposed so the editor's transform
        /// gizmo can box the block without re-deriving it. The <b>width</b> is not measured: it is
        /// the wrap box the transform sets (<c>Scale</c> · canvas width), which is why the block is
        /// the one content whose gizmo sizes horizontally alone.
        ///
        /// A non-positive <paramref name="outputHeightPx"/> measures at output resolution.
        /// Returns 0 for a block that draws nothing.
        /// </summary>
        public static double MeasureKeyboardHeight(KeyboardContent keyboard, int rows,
            double canvasHeight, double outputHeightPx)
        {
            if (keyboard == null || rows <= 0)
                return 0;

            float fontPx = KeyboardFontPx(keyboard,
                outputHeightPx > 0 ? canvasHeight / outputHeightPx : 1.0);
            if (fontPx <= 0)
                return 0;

            using var typeface = SKTypeface.CreateDefault();
            using var font = new SKFont(typeface, fontPx) { Subpixel = true };
            return new KeyboardMetrics(fontPx, font.Spacing).BlockHeight(rows);
        }

        /// <summary>Row animation length in ms for one of the item's transitions, or 0 when the
        /// row is to appear/vanish instantly. A kind that animates but carries no duration (hand
        /// written json, or an older project) gets a sane one rather than snapping.</summary>
        private const int DefaultKeyboardRowMs = 250;

        private static int KeyboardRowMs(Transition transition)
        {
            if (transition == null || !TransitionMath.IsAnimated(transition.Kind))
                return 0;
            return transition.DurationTicks > 0
                ? (int)(transition.DurationTicks / TimeSpan.TicksPerMillisecond)
                : DefaultKeyboardRowMs;
        }

        /// <summary>
        /// Widths for the wrap and the drawing alike: typed words measure in the row font, keycaps
        /// in their own geometry, and neighbors are spaced by what sits between them — a typed
        /// space inside a pill, a wider gap wherever a keycap breaks the row into separate
        /// segments.
        ///
        /// The typing pill's horizontal padding is carried <b>here</b>, not added by the drawing:
        /// a row is one pill per contiguous run of typed words, so the padding falls at the run's
        /// two ends — in <see cref="Edge"/> when the run starts or ends the line, and folded into
        /// <see cref="Gap"/> at every word↔keycap boundary. Measuring and drawing therefore walk
        /// the identical arithmetic, which is what keeps a wrapped line inside its box.
        /// </summary>
        private sealed class KeyboardAtomMetrics : IKeyAtomMetrics
        {
            private readonly SKFont _font;
            private readonly Keycap _caps;
            private readonly float _space;
            private readonly float _capGap;
            private readonly float _plusGap;
            private readonly float _padH;

            public KeyboardAtomMetrics(SKFont font, Keycap caps, float fontPx, KeyboardMetrics metrics)
            {
                _font = font;
                _caps = caps;
                _space = font.MeasureText(" ");
                _capGap = fontPx * 0.16f;
                _plusGap = fontPx * 0.05f;
                _padH = metrics.PadH;
            }

            public float Width(KeyAtom atom) => atom.Kind == KeyAtomKind.Cap
                ? _caps.Width(atom.Text)
                : _font.MeasureText(atom.Text);

            public float Gap(KeyAtom left, KeyAtom right)
            {
                if (left.Kind == KeyAtomKind.Word && right.Kind == KeyAtomKind.Word)
                    return _space;

                // a pill ends and/or begins here, so its padding is part of the gap
                float pad = Edge(left) + Edge(right);
                bool plus = left.Kind == KeyAtomKind.Plus || right.Kind == KeyAtomKind.Plus;
                return pad + (plus ? _plusGap : _capGap);
            }

            public float Edge(KeyAtom atom) => atom.Kind == KeyAtomKind.Word ? _padH : 0;
        }

        /// <summary>
        /// A keyboard-track item: the capture's keystroke runs stacked as rows, the active run at
        /// the anchored bottom (<see cref="Transform.X"/>/<see cref="Transform.Y"/> = the block
        /// bottom center), finished runs pushed up. Typing draws as text in a pill colored by
        /// <see cref="KeyboardContent.BackgroundColor"/>/<see cref="KeyboardContent.TextColor"/>;
        /// every special key and every chord member draws as a <see cref="Keycap"/> instead,
        /// <b>outside</b> any pill — a row is one pill per run of typed words with the caps free
        /// between them (see <see cref="DrawKeyboardLine"/>).
        ///
        /// Rows animate individually with the item's <see cref="Item.Entry"/>/<see cref="Item.Exit"/>
        /// — a new row plays the entry as it arrives, a finished one plays the exit once its
        /// <see cref="KeyboardContent.LingerMs"/> is up — which is why <see cref="ComposeItem"/>
        /// does not apply them to the block. A vertically sliding entry also takes only its eased
        /// share of the stack, so the rows above glide up as the new one lands rather than jumping.
        ///
        /// Text wraps at <see cref="Transform.Scale"/> · canvas width;
        /// <see cref="KeyboardContent.FontSize"/> is output pixels, mapped by
        /// <paramref name="textScale"/> exactly like <see cref="TextContent"/>. The composing track
        /// skips the zoom matrix (see <see cref="Compose"/>) — the overlay captions the viewport.
        /// </summary>
        private static void DrawKeyboard(Project project, Item item, KeyboardContent keyboard,
            long timeTicks, SKCanvas target, Transform transform, double opacity,
            int canvasWidth, int canvasHeight, double textScale)
        {
            var source = FindSource(project, keyboard.SourceId);
            if (source == null || string.IsNullOrEmpty(source.InputCapturePath))
                return;
            var runs = KeyboardLayout.GetRuns(source.InputCapturePath, Math.Max(0, keyboard.PauseBreakMs),
                keyboard.Filter);
            if (runs.Count == 0)
                return;

            // time rides the linked screen item like the cursor's; with the screen row gone the
            // item's own span stands in (SourceIn 0, realtime) so the overlay degrades, not dies.
            double speed = 1.0;
            long sourceTicks = timeTicks - item.TimelineStartTicks;
            var screen = FindScreenMediaItem(project, source, item.LinkGroupId, timeTicks);
            if (screen?.Content is MediaContent media)
            {
                speed = TimelineOps.SpeedOf(media);
                sourceTicks = SourceTimeTicks(media, screen, timeTicks);
            }
            double sourceMs = sourceTicks / (double)TimeSpan.TicksPerMillisecond;

            var rows = KeyboardLayout.VisibleRowsAt(runs, sourceMs, speed,
                Math.Max(0, keyboard.LingerMs), KeyboardRowMs(item.Entry), KeyboardRowMs(item.Exit));
            if (rows.Count == 0)
                return;

            float fontPx = KeyboardFontPx(keyboard, textScale);
            if (fontPx <= 0)
                return;

            using var typeface = SKTypeface.CreateDefault();
            using var font = new SKFont(typeface, fontPx) { Subpixel = true };
            var metrics = new KeyboardMetrics(fontPx, font.Spacing);
            using var caps = new Keycap(typeface, fontPx, metrics.RowHeight);
            var atomMetrics = new KeyboardAtomMetrics(font, caps, fontPx, metrics);
            // the wrap box is the transform's box outright: the pills' padding is measured inside
            // the line now, so subtracting it here would reserve it twice
            float wrapWidth = Math.Max(fontPx, (float)(transform.Scale * canvasWidth));

            // flatten runs into drawn lines, oldest first: a wrapped run animates as one
            var lines = new List<(IReadOnlyList<KeyAtom> Atoms, ItemEffects Fx, float Advance)>();
            foreach (var row in rows)
            {
                var (fx, advance) = KeyboardRowEffects(item, row);
                if (fx.Opacity <= 0)
                    continue;
                foreach (var line in KeyboardLayout.WrapAtoms(row.Atoms, atomMetrics, wrapWidth))
                    lines.Add((line, fx, advance));
            }
            if (lines.Count == 0)
                return;

            float centerX = (float)(transform.X * canvasWidth);
            float bottom = (float)(transform.Y * canvasHeight);

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var (atoms, fx, advance) = lines[i];
                double alpha = opacity * fx.Opacity;
                float width = KeyboardLayout.LineWidth(atoms, atomMetrics);
                float dx = (float)(fx.OffsetXFrac * width);
                float dy = (float)(fx.OffsetYFrac * metrics.RowHeight);
                var rect = new SKRect(
                    centerX - width / 2 + dx, bottom - metrics.RowHeight + dy,
                    centerX + width / 2 + dx, bottom + dy);

                if (alpha > 0)
                    DrawKeyboardLine(target, atoms, atomMetrics, font, caps, metrics, rect, keyboard, alpha);

                bottom -= advance * (metrics.RowHeight + metrics.Gap);
            }
        }

        /// <summary>One row's animation state: what to multiply/offset it by, and the share of its
        /// slot the stack above it should already have moved into.</summary>
        private static (ItemEffects Fx, float Advance) KeyboardRowEffects(Item item, KeyboardRow row)
        {
            var entryKind = item.Entry?.Kind ?? TransitionKind.None;
            var exitKind = item.Exit?.Kind ?? TransitionKind.None;
            double entryShown = TransitionMath.IsAnimated(entryKind)
                ? Easing.Apply(item.Entry.Easing, row.EntryRaw) : 1;
            double exitShown = TransitionMath.IsAnimated(exitKind)
                ? Easing.Apply(item.Exit.Easing, row.ExitRaw) : 1;

            // only a vertical slide compresses the stack; the others settle their slot at once
            float advance = entryKind is TransitionKind.SlideUp or TransitionKind.SlideDown
                ? (float)Math.Clamp(entryShown, 0, 1)
                : 1f;

            return (TransitionMath.EvaluateShown(entryKind, entryShown, exitKind, exitShown), advance);
        }

        /// <summary>
        /// Draws one laid-out line inside <paramref name="rect"/>. A keycap never sits inside a
        /// pill: the line is split into segments, one pill per contiguous run of typed words with
        /// its own padding at both ends, and every keycap (and every chord "+") standing free
        /// between them. <c>somt⌫e</c> is therefore pill·cap·pill, and a line that starts or ends
        /// on a keycap grows no empty pill beside it.
        ///
        /// Positions come from the very arithmetic <see cref="KeyboardLayout.LineWidth"/> measured
        /// the line with, so the segments cannot drift out of the rect the wrap sized. Everything
        /// centers on the row's midline by the font's real metrics.
        /// </summary>
        private static void DrawKeyboardLine(SKCanvas target, IReadOnlyList<KeyAtom> atoms,
            IKeyAtomMetrics atomMetrics, SKFont font, Keycap caps, KeyboardMetrics metrics,
            SKRect rect, KeyboardContent keyboard, double alpha)
        {
            var pen = new float[atoms.Count];
            float x = rect.Left + atomMetrics.Edge(atoms[0]);
            for (int i = 0; i < atoms.Count; i++)
            {
                if (i > 0)
                    x += atomMetrics.Width(atoms[i - 1]) + atomMetrics.Gap(atoms[i - 1], atoms[i]);
                pen[i] = x;
            }

            using var paint = new SKPaint { IsAntialias = true };

            // pills first, behind their words
            paint.Color = Keycap.Fade(new SKColor(keyboard.BackgroundColor), alpha);
            for (int i = 0; i < atoms.Count;)
            {
                if (atoms[i].Kind != KeyAtomKind.Word)
                {
                    i++;
                    continue;
                }

                int end = i;
                while (end < atoms.Count && atoms[end].Kind == KeyAtomKind.Word)
                    end++;

                var pill = new SKRect(
                    pen[i] - metrics.PadH, rect.Top,
                    pen[end - 1] + atomMetrics.Width(atoms[end - 1]) + metrics.PadH, rect.Bottom);
                using (var rr = new SKRoundRect(pill, metrics.CornerRadius, metrics.CornerRadius))
                    target.DrawRoundRect(rr, paint);

                i = end;
            }

            var ink = Keycap.Fade(new SKColor(keyboard.TextColor), alpha);
            float baseline = Keycap.Baseline(font, rect.MidY);

            for (int i = 0; i < atoms.Count; i++)
            {
                var atom = atoms[i];
                if (atom.Kind == KeyAtomKind.Cap)
                {
                    caps.Draw(target, atom.Text, pen[i], rect.MidY, alpha);
                    continue;
                }

                // a chord's "+" belongs to the keycaps' fixed livery, not to the typing colors
                paint.Color = atom.Kind == KeyAtomKind.Plus ? Keycap.Fade(SKColors.White, alpha) : ink;
                target.DrawText(atom.Text, pen[i], baseline, SKTextAlign.Left, font, paint);
            }
        }

        // ------------------------------------------------------------------------------- solid

        private static void DrawSolid(SKCanvas target, SolidContent solid, Transform transform,
            ItemEffects fx, double opacity, int canvasWidth, int canvasHeight)
        {
            // A solid has no intrinsic picture; its natural size is the canvas itself, so the
            // default transform (centered, Scale 1) fills the whole frame.
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

        /// <summary>Places a dest rect of the given size: centered at the normalized
        /// <see cref="Transform.X"/>/<see cref="Transform.Y"/>, shifted by the transition's
        /// slide offset (fractions of the item's own extent).</summary>
        internal static SKRect PlaceRect(Transform transform, ItemEffects fx,
            double destW, double destH, int canvasWidth, int canvasHeight)
        {
            double cx = transform.X * canvasWidth + fx.OffsetXFrac * destW;
            double cy = transform.Y * canvasHeight + fx.OffsetYFrac * destH;
            return new SKRect(
                (float)(cx - destW / 2), (float)(cy - destH / 2),
                (float)(cx + destW / 2), (float)(cy + destH / 2));
        }

        /// <summary>Rotation about the item center, then the mask clip, then the wipe clip —
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

        internal static byte AlphaByte(double opacity)
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
