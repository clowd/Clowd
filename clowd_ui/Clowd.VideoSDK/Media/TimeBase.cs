using System;

namespace Clowd.VideoSDK.Media
{
    /// <summary>
    /// Rational time conversion in 100ns ticks, with pure integer math.
    /// <para>
    /// Every conversion is the classic <c>a * b / c</c> with a 128-bit intermediate
    /// (<see cref="Int128"/>), i.e. the managed equivalent of FFmpeg's <c>av_rescale_rnd</c> /
    /// <c>av_rescale_q</c>. No <see cref="double"/> appears anywhere on these paths: at 29.97 or
    /// 23.976 fps a double (or an int-millisecond round trip) accumulates drift that is plainly
    /// audible as A/V desync at the end of a long timeline.
    /// </para>
    /// <para>
    /// <b>Rounding.</b> All value conversions round <i>half away from zero</i> — the semantics of
    /// <c>av_rescale_rnd(..., AV_ROUND_NEAR_INF)</c>, which is FFmpeg's default for
    /// <c>av_rescale_q</c>. Chosen deliberately so that this code and FFmpeg agree bit-for-bit on
    /// every timestamp that crosses the boundary between them, and so the conversion is symmetric
    /// about zero (negative timestamps exist — a stream <c>start_time</c> can be negative).
    /// The one exception is <see cref="TicksToFrameIndex"/>, which is a lookup, not a value
    /// conversion, and therefore floors (see its remarks).
    /// </para>
    /// </summary>
    public static class TimeBase
    {
        /// <summary>100ns ticks per second — the repo-wide time unit (same as TimeSpan.Ticks).</summary>
        public const long TicksPerSecond = TimeSpan.TicksPerSecond; // 10_000_000

        // ------------------------------------------------------------------ frame index <-> ticks

        /// <summary>
        /// Presentation instant of output frame <paramref name="frameIndex"/> for a constant frame
        /// rate of <paramref name="fpsNum"/>/<paramref name="fpsDen"/>, in 100ns ticks:
        /// <c>frameIndex * fpsDen * 10_000_000 / fpsNum</c>, rounded half away from zero.
        /// </summary>
        /// <remarks>
        /// Computed from the frame index every time rather than by accumulating a per-frame step,
        /// so the error against the exact rational instant stays bounded by half a tick (5e-8 s)
        /// forever — there is no accumulated drift over a 2-hour timeline at any rate.
        /// The 128-bit intermediate keeps 2h+ at 240fps (and far beyond) exact.
        /// </remarks>
        public static long FrameIndexToTicks(long frameIndex, int fpsNum, int fpsDen)
        {
            ValidateRate(fpsNum, fpsDen);
            return ToInt64(DivRoundNearest(MulChecked(frameIndex, (Int128)fpsDen * TicksPerSecond), fpsNum));
        }

        /// <summary>
        /// Index of the frame that <i>covers</i> the instant <paramref name="ticks"/> — the largest
        /// n with <c>FrameIndexToTicks(n) &lt;= ticks</c>. Floor semantics, including for negative
        /// ticks (floors toward negative infinity, so it is the exact inverse of
        /// <see cref="FrameIndexToTicks"/> on both sides of zero).
        /// </summary>
        /// <remarks>
        /// The floor is taken against the <i>rounded</i> frame instants, not against the exact
        /// rational ones. That distinction matters: at 30000/1001, frame 2's exact instant is
        /// 667333.33 ticks and <see cref="FrameIndexToTicks"/> emits 667333 — a plain
        /// <c>floor(ticks * fpsNum / (fpsDen * 10^7))</c> would answer 1 for that value and the
        /// round trip would be off by one. Since the rounding error is under half a tick and a
        /// frame is many ticks long, at most one correction step is ever needed, so
        /// <c>TicksToFrameIndex(FrameIndexToTicks(n)) == n</c> holds for every n.
        /// </remarks>
        public static long TicksToFrameIndex(long ticks, int fpsNum, int fpsDen)
        {
            ValidateRate(fpsNum, fpsDen);
            long n = ToInt64(DivFloor(MulChecked(ticks, fpsNum), (Int128)fpsDen * TicksPerSecond));

            // A frame whose exact instant rounded *down* lands at or before `ticks` even though the
            // exact instant is after it; one step forward is always enough to correct that.
            if (n < long.MaxValue && FrameIndexToTicks(n + 1, fpsNum, fpsDen) <= ticks)
                n++;
            return n;
        }

        // ------------------------------------------------------------------------------- rescale

        /// <summary>
        /// <c>av_rescale_q</c> equivalent: reinterprets <paramref name="value"/>, expressed in units
        /// of <paramref name="srcNum"/>/<paramref name="srcDen"/> seconds, in units of
        /// <paramref name="dstNum"/>/<paramref name="dstDen"/> seconds. Rounds half away from zero.
        /// </summary>
        /// <remarks>
        /// The exact result is <c>value * srcNum * dstDen / (srcDen * dstNum)</c>. Numerator and
        /// denominator are formed in 128-bit and reduced by their GCD before the multiply, which is
        /// what keeps pathological time bases (e.g. 1/90000 -&gt; 1/10^7) from overflowing.
        /// All four rational components must be positive; time bases are never negative or zero.
        /// </remarks>
        public static long Rescale(long value, long srcNum, long srcDen, long dstNum, long dstDen)
        {
            if (srcNum <= 0) throw new ArgumentOutOfRangeException(nameof(srcNum), srcNum, "Time base components must be positive.");
            if (srcDen <= 0) throw new ArgumentOutOfRangeException(nameof(srcDen), srcDen, "Time base components must be positive.");
            if (dstNum <= 0) throw new ArgumentOutOfRangeException(nameof(dstNum), dstNum, "Time base components must be positive.");
            if (dstDen <= 0) throw new ArgumentOutOfRangeException(nameof(dstDen), dstDen, "Time base components must be positive.");

            Int128 mul = (Int128)srcNum * dstDen;
            Int128 div = (Int128)srcDen * dstNum;
            Int128 g = Gcd(mul, div);
            mul /= g;
            div /= g;

            return ToInt64(DivRoundNearest(MulChecked(value, mul), div));
        }

        /// <summary>Converts a timestamp in a stream time base (num/den seconds) to 100ns ticks.</summary>
        public static long StreamTimeToTicks(long value, int timeBaseNum, int timeBaseDen)
            => Rescale(value, timeBaseNum, timeBaseDen, 1, TicksPerSecond);

        /// <summary>Converts 100ns ticks to a timestamp in a stream time base (num/den seconds).</summary>
        public static long TicksToStreamTime(long ticks, int timeBaseNum, int timeBaseDen)
            => Rescale(ticks, 1, TicksPerSecond, timeBaseNum, timeBaseDen);

        // -------------------------------------------------------------------------------- 128-bit

        private static void ValidateRate(int fpsNum, int fpsDen)
        {
            if (fpsNum <= 0) throw new ArgumentOutOfRangeException(nameof(fpsNum), fpsNum, "Frame rate components must be positive.");
            if (fpsDen <= 0) throw new ArgumentOutOfRangeException(nameof(fpsDen), fpsDen, "Frame rate components must be positive.");
        }

        /// <summary>value * mul with an explicit 128-bit overflow check (mul &gt; 0).</summary>
        private static Int128 MulChecked(long value, Int128 mul)
        {
            if (value == 0 || mul == 0)
                return Int128.Zero;

            Int128 abs = value < 0 ? -(Int128)value : value;
            if (abs > Int128.MaxValue / mul)
                throw new OverflowException("Rational time conversion overflowed 128 bits.");
            return (Int128)value * mul;
        }

        /// <summary>n / d rounded half away from zero (AV_ROUND_NEAR_INF). d must be positive.</summary>
        private static Int128 DivRoundNearest(Int128 n, Int128 d)
        {
            bool neg = n < 0;
            Int128 a = neg ? -n : n;
            Int128 q = a / d;
            Int128 r = a - q * d;
            // 2r >= d, written so the doubling cannot overflow.
            if (r >= d - r)
                q++;
            return neg ? -q : q;
        }

        /// <summary>n / d rounded toward negative infinity. d must be positive.</summary>
        private static Int128 DivFloor(Int128 n, Int128 d)
        {
            Int128 q = n / d;
            if (n < 0 && q * d != n)
                q--;
            return q;
        }

        private static Int128 Gcd(Int128 a, Int128 b)
        {
            while (b != 0)
            {
                (a, b) = (b, a % b);
            }
            return a == 0 ? Int128.One : a;
        }

        private static long ToInt64(Int128 v)
        {
            if (v > long.MaxValue || v < long.MinValue)
                throw new OverflowException("Rational time conversion does not fit in a 64-bit tick count.");
            return (long)v;
        }
    }
}
