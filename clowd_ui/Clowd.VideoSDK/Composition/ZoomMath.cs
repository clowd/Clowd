using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Model;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Pure evaluation of <see cref="ZoomContent"/> effect items into canvas matrices — the piece
    /// of the zoom compositor that is testable without any pixels. A zoom item scales every video
    /// track composited beneath its row (lower <see cref="Track.Order"/>) about its focal point;
    /// stacked zoom rows multiply, with the topmost row applied outermost (it zooms the already-
    /// zoomed picture beneath it). Matrices are canvas-local, so a caller's letterbox pre-scale
    /// composes correctly and preview/render share the math by construction.
    /// </summary>
    public static class ZoomMath
    {
        /// <summary>
        /// The item's effective zoom factor at <paramref name="timeTicks"/>:
        /// <c>1 + (Zoom − 1) · rampProgress</c>, where the ramp progress eases 0→1 over the entry
        /// ramp, holds 1 in the middle, and eases 1→0 over the exit ramp (overlapping ramps clamp
        /// to the smaller progress). Returns 1 outside the item's active span or for non-zoom
        /// content.
        /// </summary>
        public static double FactorAt(Item item, long timeTicks)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.Content is not ZoomContent zoom)
                return 1;
            if (timeTicks < item.TimelineStartTicks || timeTicks >= item.TimelineEndTicks)
                return 1;

            double progress = Math.Min(
                TransitionMath.EntryProgress(item, timeTicks),
                TransitionMath.ExitProgress(item, timeTicks));
            return 1 + (zoom.Zoom - 1) * progress;
        }

        /// <summary>
        /// The canvas matrix for one zoom item: scale by <paramref name="zoom"/> about the focal
        /// point (<paramref name="focusX"/>·w, <paramref name="focusY"/>·h), with the translation
        /// clamped so the scaled canvas still covers the viewport (no blank edges). Identity when
        /// <paramref name="zoom"/> ≤ 1.
        /// </summary>
        public static SKMatrix ItemMatrix(double zoom, double focusX, double focusY,
            double canvasWidth, double canvasHeight)
        {
            if (zoom <= 1)
                return SKMatrix.CreateIdentity();

            // scale about (fx·w, fy·h): p' = z·p + f·(1−z). The focal point maps to itself; the
            // clamp then pins whichever canvas edge the focus would otherwise pull into view.
            double tx = Math.Clamp(focusX * canvasWidth * (1 - zoom), canvasWidth * (1 - zoom), 0);
            double ty = Math.Clamp(focusY * canvasHeight * (1 - zoom), canvasHeight * (1 - zoom), 0);
            return SKMatrix.CreateScaleTranslation((float)zoom, (float)zoom, (float)tx, (float)ty);
        }

        /// <summary>
        /// The accumulated zoom matrix for a track of order <paramref name="targetTrackOrder"/> at
        /// <paramref name="timeTicks"/>: the product of every active zoom item on a non-hidden
        /// <see cref="TrackKind.Effect"/> track with <c>Order &gt; targetTrackOrder</c>, topmost
        /// applied outermost. Identity when no zoom is in effect.
        /// </summary>
        public static SKMatrix EffectiveMatrix(Project project, long timeTicks,
            int targetTrackOrder, double canvasWidth, double canvasHeight)
        {
            ArgumentNullException.ThrowIfNull(project);

            if (project.Tracks == null || project.Items == null)
                return SKMatrix.CreateIdentity();

            List<Track> zoomTracks = null;
            foreach (var track in project.Tracks)
            {
                if (track.Kind == TrackKind.Effect && !track.Hidden && track.Order > targetTrackOrder)
                    (zoomTracks ??= new List<Track>()).Add(track);
            }

            if (zoomTracks == null)
                return SKMatrix.CreateIdentity();

            // topmost first (descending Order, ties broken by Id like the composer's paint order),
            // so the top row's matrix ends up leftmost — outermost when mapping points.
            zoomTracks.Sort((a, b) =>
            {
                int byOrder = b.Order.CompareTo(a.Order);
                return byOrder != 0 ? byOrder : b.Id.CompareTo(a.Id);
            });

            var result = SKMatrix.CreateIdentity();
            foreach (var track in zoomTracks)
            {
                foreach (var item in project.Items)
                {
                    if (item.TrackId != track.Id || item.Content is not ZoomContent zoom)
                        continue;

                    double z = FactorAt(item, timeTicks);
                    if (z <= 1)
                        continue;

                    result = result.PreConcat(ItemMatrix(z, zoom.FocusX, zoom.FocusY, canvasWidth, canvasHeight));
                    break; // per-track overlap rule: at most one item is active at any instant
                }
            }

            return result;
        }
    }
}
