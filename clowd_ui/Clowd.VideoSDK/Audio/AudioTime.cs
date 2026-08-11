using System;
using Clowd.VideoSDK.Media;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// Tick ↔ sample-frame conversion for the audio path, integer math only (the audio analogue
    /// of <see cref="TimeBase"/>'s frame-index conversions). Sample frame <c>p</c> at rate
    /// <c>r</c> covers the instant span <c>[p/r, (p+1)/r)</c> seconds.
    ///
    /// Unlike <see cref="TimeBase.Rescale"/> (which rounds half away from zero so timestamps
    /// agree with FFmpeg), these are floor/ceil <b>lookups</b>: <see cref="SamplesFloor"/>
    /// answers "which sample covers this instant", <see cref="SamplesCeil"/> "which is the first
    /// sample at or after it", and <see cref="TicksFloor"/> "when does this sample start" —
    /// floored so a sample's own instant never lands before the boundary it was derived from
    /// (for integer boundary ticks, <c>TicksFloor(SamplesCeil(t)) &gt;= t</c> always holds,
    /// which is what keeps item-coverage checks exact).
    /// </summary>
    internal static class AudioTime
    {
        /// <summary>The sample frame covering <paramref name="ticks"/>:
        /// <c>floor(ticks * rate / 10^7)</c>. Floors toward negative infinity.</summary>
        public static long SamplesFloor(long ticks, int sampleRate)
        {
            ValidateRate(sampleRate);
            return FloorDiv((Int128)ticks * sampleRate, TimeBase.TicksPerSecond);
        }

        /// <summary>The first sample frame at or after <paramref name="ticks"/>:
        /// <c>ceil(ticks * rate / 10^7)</c>.</summary>
        public static long SamplesCeil(long ticks, int sampleRate)
        {
            ValidateRate(sampleRate);
            return CeilDiv((Int128)ticks * sampleRate, TimeBase.TicksPerSecond);
        }

        /// <summary>
        /// The sample frame nearest <paramref name="ticks"/> (half rounds up). For a timestamp
        /// that IS a sample position rendered into ticks — a decoder pts from a sample-aligned
        /// stream time base — this recovers the exact sample where <see cref="SamplesFloor"/>
        /// does not: <see cref="TimeBase.Rescale"/> renders sample <c>s</c> as
        /// <c>round(s·10^7/rate)</c>, which can fall a fraction of a tick short, and flooring
        /// that back re-reads it as <c>s − 1</c> (at 48 kHz, every <c>s ≡ 1 (mod 3)</c>).
        /// Use it to anchor positions on pts; keep the floor/ceil forms for instant lookups.
        /// </summary>
        public static long SamplesNearest(long ticks, int sampleRate)
        {
            ValidateRate(sampleRate);
            Int128 n = (Int128)ticks * sampleRate * 2 + TimeBase.TicksPerSecond;
            return FloorDiv(n, (Int128)TimeBase.TicksPerSecond * 2);
        }

        /// <summary>The instant sample frame <paramref name="samples"/> starts, in ticks:
        /// <c>floor(samples * 10^7 / rate)</c>.</summary>
        public static long TicksFloor(long samples, int sampleRate)
        {
            ValidateRate(sampleRate);
            return FloorDiv((Int128)samples * TimeBase.TicksPerSecond, sampleRate);
        }

        /// <summary>
        /// The constant sample offset of a media item: output sample <c>s</c> (timeline
        /// position) reads source sample <c>s + offset</c>. Computed once per item as
        /// <c>floor((sourceInTicks - timelineStartTicks) * rate / 10^7)</c> so consecutive
        /// chunks step by exact sample counts — converting every chunk boundary through ticks
        /// would round each one independently and drift.
        /// </summary>
        public static long SourceSampleOffset(long sourceInTicks, long timelineStartTicks, int sampleRate)
        {
            ValidateRate(sampleRate);
            return FloorDiv((Int128)(sourceInTicks - timelineStartTicks) * sampleRate, TimeBase.TicksPerSecond);
        }

        private static void ValidateRate(int sampleRate)
        {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        private static long FloorDiv(Int128 n, Int128 d)
        {
            Int128 q = n / d;
            if (n < 0 && q * d != n)
                q--;
            return checked((long)q);
        }

        private static long CeilDiv(Int128 n, Int128 d)
        {
            Int128 q = n / d;
            if (n > 0 && q * d != n)
                q++;
            return checked((long)q);
        }
    }
}
