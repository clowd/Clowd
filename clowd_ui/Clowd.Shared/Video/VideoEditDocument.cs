using System;
using System.Collections.Generic;
using System.Linq;

namespace Clowd.VideoSDK
{
    /// <summary>The outline the webcam overlay is masked to. The mask itself is rasterized by the
    /// UI (it needs a renderer) and handed to the render tool as a PNG.</summary>
    public enum WebcamOverlayShape
    {
        Circle,
        RoundedRect,
    }

    /// <summary>
    /// A half-open <c>[StartMs, EndMs)</c> span of the source recording, in source timeline
    /// milliseconds. Used both for the regions the user cut out (<see cref="VideoEditDocument.GetCutRanges"/>)
    /// and for the ones that survive (<see cref="VideoEditDocument.GetKeepSegments"/>) — they are
    /// the same shape, and a keep list is just the complement of a cut list inside the trim range.
    /// Immutable so the merge logic can rebuild the list without aliasing surprises.
    /// </summary>
    public sealed record CutRegion(long StartMs, long EndMs)
    {
        public long DurationMs => EndMs - StartMs;
    }

    /// <summary>
    /// The webcam picture-in-picture overlay. Geometry is normalized 0-1 against the *track-0*
    /// (screen) frame, so the document survives a change of output resolution: only the width is
    /// stored because the height follows the webcam track's own aspect ratio, which the document
    /// does not know about. <see cref="CornerRadius"/> is a fraction of the overlay's height
    /// (0 = square corners, 0.5 = fully rounded ends) and applies to
    /// <see cref="WebcamOverlayShape.RoundedRect"/> only.
    /// </summary>
    public sealed class WebcamOverlay : SimpleNotifyObject
    {
        /// <summary>Smallest overlay width, as a fraction of the screen frame — below this the
        /// mask would be a couple of pixels wide and the handles unusable.</summary>
        public const double MinWidth = 0.02;

        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }

        public WebcamOverlayShape Shape
        {
            get => _shape;
            set => Set(ref _shape, value);
        }

        /// <summary>Corner radius as a fraction of the overlay height, clamped to 0-0.5.</summary>
        public double CornerRadius
        {
            get => _cornerRadius;
            set => Set(ref _cornerRadius, Clamp(value, 0, 0.5));
        }

        /// <summary>Overlay centre X, 0-1 of the screen frame width.</summary>
        public double CenterX
        {
            get => _centerX;
            set => Set(ref _centerX, Clamp(value, 0, 1));
        }

        /// <summary>Overlay centre Y, 0-1 of the screen frame height.</summary>
        public double CenterY
        {
            get => _centerY;
            set => Set(ref _centerY, Clamp(value, 0, 1));
        }

        /// <summary>Overlay width, 0-1 of the screen frame width. The height is derived from the
        /// webcam track's aspect ratio at render time.</summary>
        public double Width
        {
            get => _width;
            set => Set(ref _width, Clamp(value, MinWidth, 1));
        }

        /// <summary>Math.Clamp, but NaN (which Math.Clamp throws on for the ordering check on some
        /// paths and otherwise propagates) collapses to the lower bound — a NaN drag delta must not
        /// be able to poison the document.</summary>
        private static double Clamp(double value, double min, double max)
        {
            if (Double.IsNaN(value))
                return min;

            return Math.Clamp(value, min, max);
        }

        private bool _enabled;
        private WebcamOverlayShape _shape = WebcamOverlayShape.Circle;
        private double _cornerRadius = 0.25;
        private double _centerX = 0.82;
        private double _centerY = 0.78;
        private double _width = 0.2;
    }

    /// <summary>
    /// The non-destructive edit applied to one recording: a trim range, a set of cut-out regions
    /// inside it, and the webcam overlay. Nothing here touches pixels — the document is turned
    /// into a <see cref="RenderArgs"/> (a keep-segment list plus an overlay rect) and handed to the
    /// external render tool.
    ///
    /// Cut regions are kept normalized at all times: clamped to non-negative and correctly
    /// ordered, sorted, merged where they overlap or touch, and with anything shorter than
    /// <see cref="MinSegmentMs"/> discarded. The same minimum applies to the keep segments
    /// <see cref="GetKeepSegments"/> produces, so a render never contains a slice too short to
    /// hold a frame.
    /// </summary>
    public sealed class VideoEditDocument : SimpleNotifyObject
    {
        /// <summary>Shortest cut or keep segment that survives normalization, in milliseconds.
        /// Anything below this is an accidental click, not an edit.</summary>
        public const long MinSegmentMs = 100;

        /// <summary>Start of the kept range, in source milliseconds. Clamped to the media when the
        /// segments are computed, not here (the document does not know the duration).</summary>
        public long TrimStartMs
        {
            get => _trimStartMs;
            set => Set(ref _trimStartMs, Math.Max(0, value));
        }

        /// <summary>Exclusive end of the kept range, in source milliseconds. <c>0</c> — the default —
        /// means "to the end of the media", which is what a freshly opened, untrimmed document is.</summary>
        public long TrimEndMs
        {
            get => _trimEndMs;
            set => Set(ref _trimEndMs, Math.Max(0, value));
        }

        public WebcamOverlay Webcam { get; } = new WebcamOverlay();

        /// <summary>The normalized cut list, in ascending order. Replaced wholesale by the mutators
        /// below, which is what makes a single <c>Cuts</c> change notification enough for the UI.</summary>
        public IReadOnlyList<CutRegion> Cuts => _cuts;

        /// <summary>The cut-out regions, ascending and merged — the same list as
        /// <see cref="Cuts"/>, as a snapshot the caller may hold on to.</summary>
        public IReadOnlyList<CutRegion> GetCutRanges() => _cuts.ToArray();

        /// <summary>Cuts <c>[startMs, endMs)</c> out of the recording. The arguments may arrive in
        /// either order; the region is merged into any it overlaps or touches, and ignored when the
        /// result would be shorter than <see cref="MinSegmentMs"/>.</summary>
        public void AddCut(long startMs, long endMs)
        {
            var next = new List<CutRegion>(_cuts) { new CutRegion(startMs, endMs) };
            SetCutsCore(next);
        }

        /// <summary>Removes one region from the cut list. Returns false when it was not in it —
        /// the list is normalized, so a region the caller built by hand may have been merged into
        /// a bigger one and no longer exist as such.</summary>
        public bool RemoveCut(CutRegion cut)
        {
            if (cut == null)
                return false;

            var next = new List<CutRegion>(_cuts);
            if (!next.Remove(cut))
                return false;

            SetCutsCore(next);
            return true;
        }

        public void ClearCuts()
        {
            if (_cuts.Count == 0)
                return;

            SetCutsCore(new List<CutRegion>());
        }

        /// <summary>Replaces the whole cut list; the input is normalized the same way
        /// <see cref="AddCut"/> normalizes.</summary>
        public void SetCuts(IEnumerable<CutRegion> cuts)
        {
            SetCutsCore(cuts == null ? new List<CutRegion>() : new List<CutRegion>(cuts));
        }

        /// <summary>
        /// The parts of the recording that survive, in ascending order: the trim range minus every
        /// cut region, with segments shorter than <see cref="MinSegmentMs"/> dropped. Returns an
        /// empty list when the edit keeps nothing (everything trimmed or cut away), which callers
        /// must treat as "nothing to render" rather than "render everything".
        /// </summary>
        public IReadOnlyList<CutRegion> GetKeepSegments(long totalDurationMs)
        {
            var segments = new List<CutRegion>();
            if (totalDurationMs <= 0)
                return segments;

            var start = Math.Clamp(_trimStartMs, 0, totalDurationMs);
            // 0 is the "untrimmed" sentinel; anything else is clamped into the media.
            var end = _trimEndMs <= 0 ? totalDurationMs : Math.Clamp(_trimEndMs, 0, totalDurationMs);
            if (end - start < MinSegmentMs)
                return segments;

            var cursor = start;
            foreach (var cut in _cuts)
            {
                var cutStart = Math.Clamp(cut.StartMs, start, end);
                var cutEnd = Math.Clamp(cut.EndMs, start, end);
                if (cutEnd <= cutStart)
                    continue;

                if (cutStart > cursor)
                    AddSegment(segments, cursor, cutStart);

                cursor = Math.Max(cursor, cutEnd);
            }

            if (cursor < end)
                AddSegment(segments, cursor, end);

            return segments;
        }

        private static void AddSegment(List<CutRegion> segments, long startMs, long endMs)
        {
            // a keep segment below the minimum is dropped rather than merged into a neighbour:
            // the cuts on either side of it simply become one cut, which is what the user meant.
            if (endMs - startMs >= MinSegmentMs)
                segments.Add(new CutRegion(startMs, endMs));
        }

        private void SetCutsCore(List<CutRegion> cuts)
        {
            var normalized = Normalize(cuts);
            if (normalized.Count == _cuts.Count && normalized.SequenceEqual(_cuts))
                return;

            _cuts = normalized;
            OnPropertyChanged(nameof(Cuts));
        }

        /// <summary>Clamp (non-negative, ordered), sort, merge overlapping *and* touching regions,
        /// then drop whatever is still shorter than <see cref="MinSegmentMs"/>. Merging happens
        /// before the length filter so two adjacent short cuts add up to one real one instead of
        /// both vanishing.</summary>
        private static List<CutRegion> Normalize(IEnumerable<CutRegion> cuts)
        {
            var ordered = cuts
                          .Where(c => c != null)
                          .Select(c => new CutRegion(
                              Math.Max(0, Math.Min(c.StartMs, c.EndMs)),
                              Math.Max(0, Math.Max(c.StartMs, c.EndMs))))
                          .Where(c => c.DurationMs > 0)
                          .OrderBy(c => c.StartMs)
                          .ThenBy(c => c.EndMs)
                          .ToList();

            var merged = new List<CutRegion>();
            foreach (var region in ordered)
            {
                if (merged.Count > 0 && region.StartMs <= merged[merged.Count - 1].EndMs)
                {
                    var last = merged[merged.Count - 1];
                    if (region.EndMs > last.EndMs)
                        merged[merged.Count - 1] = last with { EndMs = region.EndMs };

                    continue;
                }

                merged.Add(region);
            }

            merged.RemoveAll(c => c.DurationMs < MinSegmentMs);
            return merged;
        }

        private long _trimStartMs;
        private long _trimEndMs;
        private List<CutRegion> _cuts = new List<CutRegion>();
    }
}
