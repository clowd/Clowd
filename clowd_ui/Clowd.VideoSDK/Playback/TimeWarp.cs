using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Playback
{
    /// <summary>One span of a <see cref="TimeWarp"/>: a half-open project-time interval mapped
    /// onto a half-open output-time interval, either at a constant speed or through an eased
    /// ramp. Segments tile the warped region contiguously in both domains, so clock and audio
    /// consumers can walk them for boundary crossings.</summary>
    public readonly struct TimeWarpSegment
    {
        internal TimeWarpSegment(long projectStartTicks, long projectEndTicks,
            long outputStartTicks, long outputEndTicks, bool isRamp, double speed)
        {
            ProjectStartTicks = projectStartTicks;
            ProjectEndTicks = projectEndTicks;
            OutputStartTicks = outputStartTicks;
            OutputEndTicks = outputEndTicks;
            IsRamp = isRamp;
            Speed = speed;
        }

        public long ProjectStartTicks { get; }

        public long ProjectEndTicks { get; }

        public long OutputStartTicks { get; }

        public long OutputEndTicks { get; }

        /// <summary>True when the speed changes across the span (an entry/exit ramp) — sample
        /// <see cref="TimeWarp.SpeedAt"/> for the instantaneous value.</summary>
        public bool IsRamp { get; }

        /// <summary>The span's constant speed (1 outside speed items). For a ramp segment this is
        /// the item's target factor, not an instantaneous value.</summary>
        public double Speed { get; }
    }

    /// <summary>
    /// The project-time ↔ output-time mapping produced by the speed items
    /// (<see cref="SpeedContent"/> on non-hidden <see cref="TrackKind.Effect"/> tracks): output
    /// time is <c>O(p) = ∫ dp' / s(p')</c> where the instantaneous speed <c>s</c> is 1 outside
    /// speed items, the item's factor inside, and eased 1 → factor → 1 across
    /// <see cref="TransitionKind.Ramp"/> entry/exit windows. Immutable — rebuilt wherever
    /// <see cref="ProjectTimelineMap"/> is rebuilt and swapped atomically.
    ///
    /// Spans at speed 1 map through a pure integer offset (bit-exact, preserving the unwarped
    /// preview/render parity fast paths); constant-speed spans round a single division; ramps
    /// interpolate a precomputed monotone LUT with exact tick anchors at both ends. Both
    /// directions are monotone non-decreasing and mutually consistent: re-applying either map to
    /// its counterpart's result is stable within one tick. The inherent quantization of mapping
    /// through the coarser domain (a factor-10 span covers ten project ticks per output tick) is
    /// bounded by half the local speed ratio.
    /// </summary>
    public sealed class TimeWarp
    {
        private const int RampSamples = 256;
        private const double MinFactor = 0.1;
        private const double MaxFactor = 10.0;

        private sealed class Seg
        {
            public long PStart;
            public long PEnd;
            public long OStart;
            public long OEnd;

            /// <summary>Constant speed of the span; for ramps, the target factor.</summary>
            public double Speed = 1.0;

            /// <summary>Ramps only: output offset at <see cref="RampSamples"/> + 1 uniform
            /// project offsets across the span. Strictly increasing, [0] = 0 and [^1] = the
            /// span's exact integer output length.</summary>
            public double[] Lut;

            public TransitionEasing Easing;

            public bool IsExit;
        }

        private readonly Seg[] _segments;
        private readonly long _pEnd;
        private readonly long _oEnd;

        private TimeWarp(List<Seg> segments, bool isIdentity, long projectDurationTicks)
        {
            _segments = segments.ToArray();
            var last = _segments.Length > 0 ? _segments[^1] : null;
            _pEnd = last?.PEnd ?? 0;
            _oEnd = last?.OEnd ?? 0;
            IsIdentity = isIdentity;

            var list = new TimeWarpSegment[_segments.Length];
            for (int i = 0; i < _segments.Length; i++)
            {
                var seg = _segments[i];
                list[i] = new TimeWarpSegment(seg.PStart, seg.PEnd, seg.OStart, seg.OEnd,
                    seg.Lut != null, seg.Speed);
            }
            Segments = list;

            OutputDurationTicks = ToOutput(projectDurationTicks);
        }

        /// <summary>True when no speed item bends time — every instant maps to itself and both
        /// conversions are exact pass-throughs.</summary>
        public bool IsIdentity { get; }

        /// <summary>The warped length of the project: <see cref="ToOutput"/> of
        /// <see cref="Project.GetDurationTicks"/>. A speed item hanging past the last clip
        /// extends nothing (effect items are excluded from the project duration).</summary>
        public long OutputDurationTicks { get; }

        /// <summary>The warp's spans, contiguous in both domains from project tick 0 through the
        /// end of the last speed item or the project duration, whichever is later. Empty for an
        /// empty project; time past the last segment continues at speed 1.</summary>
        public IReadOnlyList<TimeWarpSegment> Segments { get; }

        public static TimeWarp Build(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);

            var segments = new List<Seg>();
            long cursor = 0;
            var isIdentity = true;

            foreach (var item in CollectSpeedItems(project))
            {
                long start = Math.Max(item.TimelineStartTicks, cursor);
                long end = item.TimelineEndTicks;
                if (end <= start)
                    continue;

                double factor = ((SpeedContent)item.Content).Factor;
                if (!(factor > 0) || Double.IsInfinity(factor))
                    continue;
                factor = Math.Clamp(factor, MinFactor, MaxFactor);
                if (factor == 1.0)
                    continue; // a unity target warps nothing, ramps included

                long duration = end - start;
                long entry = RampTicks(item.Entry, duration);
                long exit = RampTicks(item.Exit, duration);
                if (entry + exit > duration)
                {
                    // proportional shrink so the two ramps exactly fill the item
                    long shrunk = (long)Math.Round(entry * (double)duration / (entry + exit));
                    entry = shrunk;
                    exit = duration - shrunk;
                }

                if (start > cursor)
                    segments.Add(new Seg { PStart = cursor, PEnd = start });
                if (entry > 0)
                    segments.Add(MakeRamp(start, start + entry, factor, item.Entry.Easing, isExit: false));
                if (end - exit > start + entry)
                    segments.Add(new Seg { PStart = start + entry, PEnd = end - exit, Speed = factor });
                if (exit > 0)
                    segments.Add(MakeRamp(end - exit, end, factor, item.Exit.Easing, isExit: true));

                cursor = end;
                isIdentity = false;
            }

            long projectEnd = project.GetDurationTicks();
            if (projectEnd > cursor)
                segments.Add(new Seg { PStart = cursor, PEnd = projectEnd });

            // anchor output time cumulatively; speed-1 spans keep a pure integer offset
            long o = 0;
            foreach (var seg in segments)
            {
                long span = seg.PEnd - seg.PStart;
                seg.OStart = o;
                seg.OEnd = o += seg.Lut != null ? (long)seg.Lut[RampSamples]
                    : seg.Speed == 1.0 ? span
                    : (long)Math.Round(span / seg.Speed);
            }

            return new TimeWarp(segments, isIdentity, projectEnd);
        }

        /// <summary>Project instant → output instant. Monotone non-decreasing; negative input
        /// clamps to 0 and time past the last segment continues at speed 1.</summary>
        public long ToOutput(long projectTicks)
        {
            if (projectTicks <= 0)
                return 0;
            if (projectTicks >= _pEnd)
                return _oEnd + (projectTicks - _pEnd);

            var seg = FindByProject(projectTicks);
            long off = projectTicks - seg.PStart;
            if (seg.Lut == null)
            {
                return seg.Speed == 1.0
                    ? seg.OStart + off
                    : seg.OStart + (long)Math.Round(off / seg.Speed);
            }

            long span = seg.PEnd - seg.PStart;
            double x = off * (double)RampSamples / span;
            int i = (int)x;
            if (i >= RampSamples)
                i = RampSamples - 1;
            double value = seg.Lut[i] + (seg.Lut[i + 1] - seg.Lut[i]) * (x - i);
            return seg.OStart + (long)Math.Round(value);
        }

        /// <summary>Output instant → project instant, the inverse of <see cref="ToOutput"/>
        /// through the same segment anchors and ramp LUTs. Monotone non-decreasing; negative
        /// input clamps to 0 and time past the last segment continues at speed 1.</summary>
        public long ToProject(long outputTicks)
        {
            if (outputTicks <= 0)
                return 0;
            if (outputTicks >= _oEnd)
                return _pEnd + (outputTicks - _oEnd);

            var seg = FindByOutput(outputTicks);
            long rel = outputTicks - seg.OStart;
            if (seg.Lut == null)
            {
                return seg.Speed == 1.0
                    ? seg.PStart + rel
                    : seg.PStart + (long)Math.Round(rel * seg.Speed);
            }

            var lut = seg.Lut;
            int lo = 0, hi = RampSamples - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (lut[mid] <= rel)
                    lo = mid;
                else
                    hi = mid - 1;
            }

            double width = lut[lo + 1] - lut[lo];
            double frac = width > 0 ? (rel - lut[lo]) / width : 0;
            long span = seg.PEnd - seg.PStart;
            return seg.PStart + (long)Math.Round((lo + frac) * span / RampSamples);
        }

        /// <summary>True when the two warps describe the same project ↔ output mapping. Only the
        /// bent spans participate: speed-1 segments (including the trailing one a longer project
        /// appends) never move an anchor relative to their neighbors, so timelines differing
        /// only in unwarped footage compare equal — which lets the player skip re-anchoring the
        /// clock and flushing audio on edits that cannot have moved any output instant.</summary>
        public bool MappingEquals(TimeWarp other)
        {
            if (other == null)
                return false;
            if (ReferenceEquals(this, other))
                return true;

            static bool Unity(Seg s) => s.Lut == null && s.Speed == 1.0;
            int i = 0, j = 0;
            while (true)
            {
                while (i < _segments.Length && Unity(_segments[i]))
                    i++;
                while (j < other._segments.Length && Unity(other._segments[j]))
                    j++;
                if (i == _segments.Length || j == other._segments.Length)
                    return i == _segments.Length && j == other._segments.Length;

                var a = _segments[i++];
                var b = other._segments[j++];
                if (a.PStart != b.PStart || a.PEnd != b.PEnd
                    || a.OStart != b.OStart || a.OEnd != b.OEnd
                    || a.Speed != b.Speed || (a.Lut != null) != (b.Lut != null)
                    || a.Easing != b.Easing || a.IsExit != b.IsExit)
                    return false;
            }
        }

        /// <summary>Instantaneous speed at a project instant: 1 outside speed items (and past
        /// both ends), the factor inside, the eased value inside a ramp. Half-open item spans —
        /// the tick an item ends on is already back at 1.</summary>
        public double SpeedAt(long projectTicks)
        {
            if (projectTicks < 0 || projectTicks >= _pEnd)
                return 1;

            var seg = FindByProject(projectTicks);
            if (seg.Lut == null)
                return seg.Speed;

            double frac = (projectTicks - seg.PStart) / (double)(seg.PEnd - seg.PStart);
            return RampSpeed(seg.Speed, seg.Easing, seg.IsExit, frac);
        }

        private Seg FindByProject(long projectTicks)
        {
            int lo = 0, hi = _segments.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_segments[mid].PStart <= projectTicks)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            return _segments[lo];
        }

        private Seg FindByOutput(long outputTicks)
        {
            // the last segment whose OStart <= o skips any zero-output-width spans at the
            // same anchor, so the match always has OEnd > o.
            int lo = 0, hi = _segments.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_segments[mid].OStart <= outputTicks)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            return _segments[lo];
        }

        /// <summary>The speed items that shape the warp: <see cref="SpeedContent"/> on non-hidden
        /// effect tracks, in start order. Defensive against invalid models — overlaps resolve in
        /// favor of the earlier item (Build clamps into the free space).</summary>
        private static List<Item> CollectSpeedItems(Project project)
        {
            var result = new List<Item>();
            var tracks = project.Tracks;
            var items = project.Items;
            if (tracks == null || items == null)
                return result;

            HashSet<Guid> visible = null;
            foreach (var track in tracks)
            {
                if (track.Kind == TrackKind.Effect && !track.Hidden)
                    (visible ??= new HashSet<Guid>()).Add(track.Id);
            }
            if (visible == null)
                return result;

            foreach (var item in items)
            {
                if (item.Content is SpeedContent && item.DurationTicks > 0 && visible.Contains(item.TrackId))
                    result.Add(item);
            }

            result.Sort((a, b) =>
            {
                var byStart = a.TimelineStartTicks.CompareTo(b.TimelineStartTicks);
                return byStart != 0 ? byStart : a.Id.CompareTo(b.Id);
            });
            return result;
        }

        private static long RampTicks(Transition transition, long itemDurationTicks)
            => transition is { Kind: TransitionKind.Ramp } && transition.DurationTicks > 0
                ? Math.Min(transition.DurationTicks, itemDurationTicks)
                : 0;

        /// <summary>Speed at a fractional offset through a ramp span: the entry eases 1 → factor,
        /// the exit mirrors it back down, both through the shared <see cref="Easing"/> curves so
        /// a ramp sounds the way its transition looks.</summary>
        private static double RampSpeed(double factor, TransitionEasing easing, bool isExit, double frac)
        {
            double raw = isExit ? 1 - frac : frac;
            return 1 + (factor - 1) * Easing.Apply(easing, raw);
        }

        /// <summary>Precomputes a ramp span's output-offset LUT by trapezoidal integration of
        /// 1/s over a uniform project-offset grid, then rescales interior samples so the final
        /// anchor is an exact integer tick — both segment ends stay exact while the interior
        /// interpolates.</summary>
        private static Seg MakeRamp(long pStart, long pEnd, double factor, TransitionEasing easing, bool isExit)
        {
            long span = pEnd - pStart;
            var lut = new double[RampSamples + 1];
            double dt = span / (double)RampSamples;
            double prev = 1.0 / RampSpeed(factor, easing, isExit, 0);
            double acc = 0;
            for (int i = 1; i <= RampSamples; i++)
            {
                double inv = 1.0 / RampSpeed(factor, easing, isExit, i / (double)RampSamples);
                acc += dt * 0.5 * (prev + inv);
                lut[i] = acc;
                prev = inv;
            }

            long oSpan = (long)Math.Round(acc);
            if (oSpan > 0)
            {
                double scale = oSpan / acc;
                for (int i = 1; i < RampSamples; i++)
                    lut[i] *= scale;
            }
            else
            {
                Array.Clear(lut);
            }
            lut[RampSamples] = oSpan;

            return new Seg { PStart = pStart, PEnd = pEnd, Speed = factor, Lut = lut, Easing = easing, IsExit = isExit };
        }
    }
}
