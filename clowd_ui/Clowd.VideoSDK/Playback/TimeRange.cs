using System;
using System.Collections.Generic;

namespace Clowd.VideoSDK.Playback
{
    /// <summary>A half-open [Start, End) span of media time.</summary>
    public readonly struct TimeRange : IEquatable<TimeRange>
    {
        public TimeRange(TimeSpan start, TimeSpan end)
        {
            if (end < start)
                throw new ArgumentException("TimeRange end must be >= start.");
            Start = start;
            End = end;
        }

        public TimeSpan Start { get; }
        public TimeSpan End { get; }
        public TimeSpan Duration => End - Start;
        public bool IsEmpty => End <= Start;

        public bool Contains(TimeSpan t) => t >= Start && t < End;

        public bool Equals(TimeRange other) => Start == other.Start && End == other.End;
        public override bool Equals(object obj) => obj is TimeRange other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Start, End);
        public override string ToString() => $"[{Start} .. {End})";
    }

    /// <summary>
    /// An immutable, normalized (sorted, merged, empties removed) set of skip ranges with the
    /// lookup the playback loop needs: "am I inside a cut, and where do I resume?". Instances are
    /// swapped atomically on the player, so lookups are lock-free.
    /// </summary>
    public sealed class SkipRangeSchedule
    {
        public static readonly SkipRangeSchedule Empty = new SkipRangeSchedule(Array.Empty<TimeRange>());

        private readonly TimeRange[] _ranges;

        public SkipRangeSchedule(IReadOnlyList<TimeRange> ranges)
        {
            _ranges = Normalize(ranges);
        }

        public IReadOnlyList<TimeRange> Ranges => _ranges;

        /// <summary>Sorts by start, drops empty ranges, merges overlapping/touching ranges.</summary>
        private static TimeRange[] Normalize(IReadOnlyList<TimeRange> ranges)
        {
            if (ranges == null || ranges.Count == 0)
                return Array.Empty<TimeRange>();

            var sorted = new List<TimeRange>(ranges.Count);
            for (int i = 0; i < ranges.Count; i++)
            {
                if (!ranges[i].IsEmpty)
                    sorted.Add(ranges[i]);
            }

            sorted.Sort((a, b) => a.Start.CompareTo(b.Start));

            var merged = new List<TimeRange>(sorted.Count);
            foreach (var r in sorted)
            {
                if (merged.Count > 0 && r.Start <= merged[^1].End)
                {
                    if (r.End > merged[^1].End)
                        merged[^1] = new TimeRange(merged[^1].Start, r.End);
                }
                else
                {
                    merged.Add(r);
                }
            }

            return merged.ToArray();
        }

        /// <summary>
        /// When <paramref name="position"/> falls inside a skip range, returns true with the time
        /// playback should resume at (the end of that range, extended through any ranges that a
        /// merge normalization already collapsed). Binary search; no allocation.
        /// </summary>
        public bool TryGetSkipEnd(TimeSpan position, out TimeSpan resumeAt)
        {
            int lo = 0, hi = _ranges.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var r = _ranges[mid];
                if (position < r.Start)
                    hi = mid - 1;
                else if (position >= r.End)
                    lo = mid + 1;
                else
                {
                    resumeAt = r.End;
                    return true;
                }
            }

            resumeAt = default;
            return false;
        }

        /// <summary>The start of the next skip range at or after <paramref name="position"/>
        /// (TimeSpan.MaxValue when none). Lets a caller clamp scheduled work to the next cut.</summary>
        public TimeSpan NextSkipStart(TimeSpan position)
        {
            int lo = 0, hi = _ranges.Length - 1;
            TimeSpan best = TimeSpan.MaxValue;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_ranges[mid].Start >= position)
                {
                    best = _ranges[mid].Start;
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return best;
        }
    }
}
