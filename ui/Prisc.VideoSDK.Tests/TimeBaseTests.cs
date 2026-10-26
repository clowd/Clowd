using System;
using System.Collections.Generic;
using System.Numerics;
using Clowd.VideoSDK.Media;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // TimeBase is the only place tick math happens, so it is tested against an *independent*
    // ground truth (BigInteger, arbitrary precision) rather than against itself: a bug shared
    // between implementation and expectation would otherwise be invisible.
    public class TimeBaseTests
    {
        private const long TicksPerSecond = 10_000_000;
        private const long TwoHoursSeconds = 7200;

        // ---------------------------------------------------------------------- ground truth

        /// <summary>n * den * 10^7 / num, rounded half away from zero, in arbitrary precision.</summary>
        private static BigInteger ExactFrameTicks(long n, int num, int den)
        {
            BigInteger numerator = (BigInteger)n * den * TicksPerSecond;
            BigInteger q = BigInteger.DivRem(numerator, num, out BigInteger rem);
            if (BigInteger.Abs(rem) * 2 >= num)
                q += numerator.Sign;
            return q;
        }

        /// <summary>value * srcNum * dstDen / (srcDen * dstNum), rounded half away from zero.</summary>
        private static BigInteger ExactRescale(long value, long srcNum, long srcDen, long dstNum, long dstDen)
        {
            BigInteger numerator = (BigInteger)value * srcNum * dstDen;
            BigInteger denominator = (BigInteger)srcDen * dstNum;
            BigInteger q = BigInteger.DivRem(numerator, denominator, out BigInteger rem);
            if (BigInteger.Abs(rem) * 2 >= denominator)
                q += numerator.Sign;
            return q;
        }

        private static long LastFrameOfTwoHours(int num, int den) => TwoHoursSeconds * num / den;

        private static IReadOnlyList<long> SampleIndices(long last)
        {
            var set = new SortedSet<long> { 0, 1, 2, 3, 5, 7, last - 1, last, last / 2, last / 3 };
            for (long p = 1; p > 0 && p <= last; p *= 2)
                set.Add(p);
            var list = new List<long>(set);
            list.RemoveAll(v => v < 0);
            return list;
        }

        // ----------------------------------------------------------- frame index -> ticks

        [Theory]
        [InlineData(30, 1, 333_333)]      // 333333.33 -> down
        [InlineData(60, 1, 166_667)]      // 166666.67 -> up
        [InlineData(30000, 1001, 333_667)] // 29.97: 333666.67 -> up
        [InlineData(24000, 1001, 417_083)] // 23.976: 417083.33 -> down
        [InlineData(25, 1, 400_000)]       // exact
        public void FrameIndexToTicks_rounds_first_frame_half_away_from_zero(int num, int den, long expected)
        {
            Assert.Equal(expected, TimeBase.FrameIndexToTicks(1, num, den));
            Assert.Equal(0, TimeBase.FrameIndexToTicks(0, num, den));
            // symmetric about zero (AV_ROUND_NEAR_INF)
            Assert.Equal(-expected, TimeBase.FrameIndexToTicks(-1, num, den));
        }

        [Theory]
        [InlineData(30, 1)]
        [InlineData(60, 1)]
        [InlineData(30000, 1001)]
        [InlineData(24000, 1001)]
        public void FrameIndexToTicks_matches_exact_rational_at_spot_checks(int num, int den)
        {
            long last = LastFrameOfTwoHours(num, den);
            foreach (long n in SampleIndices(last))
            {
                BigInteger expected = ExactFrameTicks(n, num, den);
                Assert.Equal(expected, (BigInteger)TimeBase.FrameIndexToTicks(n, num, den));
                Assert.Equal(-expected, (BigInteger)TimeBase.FrameIndexToTicks(-n, num, den));
            }
        }

        [Theory]
        [InlineData(30, 1)]
        [InlineData(60, 1)]
        [InlineData(30000, 1001)]
        [InlineData(24000, 1001)]
        public void FrameIndexToTicks_has_no_accumulated_drift_over_two_hours(int num, int den)
        {
            long last = LastFrameOfTwoHours(num, den);
            long frameTicksFloor = den * TicksPerSecond / num;

            long previous = 0;
            for (long n = 0; n <= last; n++)
            {
                long ticks = TimeBase.FrameIndexToTicks(n, num, den);

                // |ticks*num - n*den*10^7| <= num/2  <=>  the error against the exact rational
                // instant is at most half a tick, for every n. Nothing accumulates.
                Int128 err = (Int128)ticks * num - (Int128)n * den * TicksPerSecond;
                Int128 absErr = err < 0 ? -err : err;
                Assert.True(absErr * 2 <= num, $"frame {n} drifted by {err}/{num} ticks");

                if (n > 0)
                {
                    long step = ticks - previous;
                    Assert.InRange(step, frameTicksFloor, frameTicksFloor + 1);
                }

                previous = ticks;
            }

            // and the last frame still lands where the exact rational says it does
            Assert.Equal(ExactFrameTicks(last, num, den), (BigInteger)TimeBase.FrameIndexToTicks(last, num, den));
        }

        [Fact]
        public void FrameIndexToTicks_is_exact_for_a_two_hour_timeline_at_240fps()
        {
            // the spec's stress case: 2h+ at 240fps must not come anywhere near overflowing.
            long last = 240 * TwoHoursSeconds;
            Assert.Equal(2L * 3600 * TicksPerSecond, TimeBase.FrameIndexToTicks(last, 240, 1));
        }

        // ----------------------------------------------------------- ticks -> frame index

        [Theory]
        [InlineData(30, 1)]
        [InlineData(60, 1)]
        [InlineData(30000, 1001)]
        [InlineData(24000, 1001)]
        public void TicksToFrameIndex_inverts_FrameIndexToTicks_over_two_hours(int num, int den)
        {
            long last = LastFrameOfTwoHours(num, den);
            for (long n = 0; n <= last; n++)
            {
                long ticks = TimeBase.FrameIndexToTicks(n, num, den);
                Assert.Equal(n, TimeBase.TicksToFrameIndex(ticks, num, den));
            }
        }

        [Theory]
        [InlineData(30, 1)]
        [InlineData(60, 1)]
        [InlineData(30000, 1001)]
        [InlineData(24000, 1001)]
        public void TicksToFrameIndex_inverts_FrameIndexToTicks_for_negative_indices(int num, int den)
        {
            foreach (long n in SampleIndices(LastFrameOfTwoHours(num, den)))
            {
                long ticks = TimeBase.FrameIndexToTicks(-n, num, den);
                Assert.Equal(-n, TimeBase.TicksToFrameIndex(ticks, num, den));
            }
        }

        [Theory]
        [InlineData(30, 1)]
        [InlineData(30000, 1001)]
        public void TicksToFrameIndex_floors_inside_the_frame(int num, int den)
        {
            foreach (long n in new long[] { 0, 1, 2, 3, 100, 12_345 })
            {
                long start = TimeBase.FrameIndexToTicks(n, num, den);
                long next = TimeBase.FrameIndexToTicks(n + 1, num, den);

                Assert.Equal(n, TimeBase.TicksToFrameIndex(start, num, den));
                Assert.Equal(n, TimeBase.TicksToFrameIndex(start + 1, num, den));
                Assert.Equal(n, TimeBase.TicksToFrameIndex((start + next) / 2, num, den));
                Assert.Equal(n, TimeBase.TicksToFrameIndex(next - 1, num, den));
                Assert.Equal(n + 1, TimeBase.TicksToFrameIndex(next, num, den));
            }
        }

        [Fact]
        public void TicksToFrameIndex_floors_toward_negative_infinity()
        {
            // 30fps: frame -1 covers [-333333, 0). Anything above that instant but below 0 is
            // still frame -1, never 0.
            Assert.Equal(-1, TimeBase.TicksToFrameIndex(-333_333, 30, 1));
            Assert.Equal(-1, TimeBase.TicksToFrameIndex(-1, 30, 1));
            Assert.Equal(0, TimeBase.TicksToFrameIndex(0, 30, 1));
            Assert.Equal(-2, TimeBase.TicksToFrameIndex(-333_334, 30, 1));
        }

        [Fact]
        public void FrameRate_components_must_be_positive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TimeBase.FrameIndexToTicks(1, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => TimeBase.FrameIndexToTicks(1, 30, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => TimeBase.FrameIndexToTicks(1, -30, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => TimeBase.TicksToFrameIndex(1, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => TimeBase.TicksToFrameIndex(1, 30, -1));
        }

        // --------------------------------------------------------------------------- rescale

        [Theory]
        [InlineData(0, 1, 1, 1, 1, 0)]
        [InlineData(123, 1, 1, 1, 1, 123)]
        [InlineData(1, 1, 1000, 1, 10_000_000, 10_000)]         // 1 ms -> ticks
        [InlineData(90_000, 1, 90_000, 1, 10_000_000, 10_000_000)] // 1 s at 90kHz -> ticks
        [InlineData(1_000_000, 1, 1_000_000, 1, 10_000_000, 10_000_000)] // AV_TIME_BASE -> ticks
        [InlineData(10_000_000, 1, 10_000_000, 1, 1000, 1000)]  // ticks -> ms
        [InlineData(-90_000, 1, 90_000, 1, 10_000_000, -10_000_000)]
        public void Rescale_known_conversions(long value, long sn, long sd, long dn, long dd, long expected)
        {
            Assert.Equal(expected, TimeBase.Rescale(value, sn, sd, dn, dd));
        }

        [Theory]
        [InlineData(1, 1, 2, 1, 1, 1)]    // 0.5 -> 1 (away from zero)
        [InlineData(-1, 1, 2, 1, 1, -1)]  // -0.5 -> -1
        [InlineData(3, 1, 2, 1, 1, 2)]    // 1.5 -> 2
        [InlineData(1, 1, 3, 1, 1, 0)]    // 0.33 -> 0
        [InlineData(2, 1, 3, 1, 1, 1)]    // 0.67 -> 1
        [InlineData(-2, 1, 3, 1, 1, -1)]
        [InlineData(-1, 1, 3, 1, 1, 0)]
        public void Rescale_rounds_half_away_from_zero(long value, long sn, long sd, long dn, long dd, long expected)
        {
            Assert.Equal(expected, TimeBase.Rescale(value, sn, sd, dn, dd));
        }

        [Theory]
        [InlineData(1, 90_000)]      // mpeg-ts
        [InlineData(1, 1_000_000)]   // AV_TIME_BASE
        [InlineData(1, 1000)]        // matroska ms
        [InlineData(1001, 30_000)]   // 29.97 frame ticks
        public void Rescale_stream_time_roundtrips_through_ticks(int tbNum, int tbDen)
        {
            foreach (long pts in new long[] { 0, 1, 2, 7, 12_345, 1_000_000, -1, -12_345 })
            {
                long ticks = TimeBase.StreamTimeToTicks(pts, tbNum, tbDen);
                Assert.Equal(ExactRescale(pts, tbNum, tbDen, 1, TicksPerSecond), (BigInteger)ticks);
                // ticks are finer than every one of these bases, so the round trip is exact
                Assert.Equal(pts, TimeBase.TicksToStreamTime(ticks, tbNum, tbDen));
            }
        }

        [Fact]
        public void Rescale_matches_exact_math_on_large_values()
        {
            // a 24h+ timeline in ticks, converted down into coarse stream time bases
            long[] tickValues = { 1_000_000_000_000_000L, 864_000_000_000_000L, long.MaxValue / 100_000, -864_000_000_000_000L };
            foreach (long v in tickValues)
            {
                Assert.Equal(ExactRescale(v, 1, TicksPerSecond, 1, 90_000), (BigInteger)TimeBase.Rescale(v, 1, TicksPerSecond, 1, 90_000));
                Assert.Equal(ExactRescale(v, 1, TicksPerSecond, 1001, 30_000), (BigInteger)TimeBase.Rescale(v, 1, TicksPerSecond, 1001, 30_000));
            }

            // and timestamps big enough that value*num*den wraps a 64-bit intermediate (1e12 * 1001
            // * 1e7 ~ 1e22), converted up into ticks
            long[] ptsValues = { 1_000_000_000_000L, 999_999_999_999L, -1_000_000_000_000L };
            foreach (long v in ptsValues)
            {
                Assert.Equal(ExactRescale(v, 1, 90_000, 1, TicksPerSecond), (BigInteger)TimeBase.Rescale(v, 1, 90_000, 1, TicksPerSecond));
                Assert.Equal(ExactRescale(v, 1001, 30_000, 1, TicksPerSecond), (BigInteger)TimeBase.Rescale(v, 1001, 30_000, 1, TicksPerSecond));
            }
        }

        [Fact]
        public void Rescale_reduces_before_multiplying_so_huge_time_bases_do_not_overflow()
        {
            // srcNum*dstDen and srcDen*dstNum are both ~8.1e37 here; multiplying the value by that
            // unreduced would blow past Int128. Reduced by their GCD the conversion is the identity.
            const long huge = 9_000_000_000_000_000_000L;
            Assert.Equal(1_000_000, TimeBase.Rescale(1_000_000, huge, huge, huge, huge));
            Assert.Equal(1234, TimeBase.Rescale(1234, 4_000_000_000L, 8_000_000_000L, 1, 2));
        }

        [Fact]
        public void Rescale_overflowing_the_result_throws()
        {
            // result exceeds Int64
            Assert.Throws<OverflowException>(() => TimeBase.Rescale(long.MaxValue, 1000, 1, 1, 1));
            Assert.Throws<OverflowException>(() => TimeBase.Rescale(long.MaxValue / 2, 1_000_000, 1, 1, 1));
            Assert.Throws<OverflowException>(() => TimeBase.FrameIndexToTicks(long.MaxValue, 30, 1));
            // intermediate exceeds Int128
            Assert.Throws<OverflowException>(() => TimeBase.Rescale(long.MaxValue, long.MaxValue, 1, 1, 3));
        }

        [Fact]
        public void Rescale_components_must_be_positive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TimeBase.Rescale(1, 0, 1, 1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => TimeBase.Rescale(1, 1, 0, 1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => TimeBase.Rescale(1, 1, 1, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => TimeBase.Rescale(1, 1, 1, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => TimeBase.Rescale(1, -1, 1, 1, 1));
        }

        [Fact]
        public void Rescale_agrees_with_FrameIndexToTicks()
        {
            // FrameIndexToTicks(n) is Rescale(n, fpsDen, fpsNum, 1, TicksPerSecond) by definition.
            foreach (var (num, den) in new[] { (30, 1), (60, 1), (30000, 1001), (24000, 1001) })
            {
                foreach (long n in new long[] { 0, 1, 2, 1000, 215_784, -7 })
                {
                    Assert.Equal(
                        TimeBase.Rescale(n, den, num, 1, TicksPerSecond),
                        TimeBase.FrameIndexToTicks(n, num, den));
                }
            }
        }
    }
}
