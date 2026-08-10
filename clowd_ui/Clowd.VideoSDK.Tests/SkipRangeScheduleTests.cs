using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    public class SkipRangeScheduleTests
    {
        private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

        [Fact]
        public void Empty_schedule_never_skips()
        {
            Assert.False(SkipRangeSchedule.Empty.TryGetSkipEnd(S(5), out _));
            Assert.Equal(TimeSpan.MaxValue, SkipRangeSchedule.Empty.NextSkipStart(S(0)));
        }

        [Fact]
        public void Position_inside_range_returns_range_end()
        {
            var s = new SkipRangeSchedule(new[] { new TimeRange(S(2), S(4)) });

            Assert.True(s.TryGetSkipEnd(S(2), out var end)); // inclusive start
            Assert.Equal(S(4), end);
            Assert.True(s.TryGetSkipEnd(S(3.999), out _));
            Assert.False(s.TryGetSkipEnd(S(4), out _));      // exclusive end
            Assert.False(s.TryGetSkipEnd(S(1.999), out _));
        }

        [Fact]
        public void Ranges_are_sorted_and_merged()
        {
            var s = new SkipRangeSchedule(new[]
            {
                new TimeRange(S(10), S(12)),
                new TimeRange(S(1), S(3)),
                new TimeRange(S(2), S(5)),   // overlaps the previous
                new TimeRange(S(5), S(6)),   // touches — merges too
            });

            Assert.Equal(2, s.Ranges.Count);
            Assert.Equal(new TimeRange(S(1), S(6)), s.Ranges[0]);
            Assert.Equal(new TimeRange(S(10), S(12)), s.Ranges[1]);

            Assert.True(s.TryGetSkipEnd(S(4.5), out var end));
            Assert.Equal(S(6), end);
        }

        [Fact]
        public void Empty_ranges_are_dropped()
        {
            var s = new SkipRangeSchedule(new[] { new TimeRange(S(3), S(3)), new TimeRange(S(5), S(7)) });
            Assert.Single(s.Ranges);
            Assert.False(s.TryGetSkipEnd(S(3), out _));
        }

        [Fact]
        public void Contained_range_is_absorbed()
        {
            var s = new SkipRangeSchedule(new[] { new TimeRange(S(1), S(10)), new TimeRange(S(2), S(3)) });
            Assert.Single(s.Ranges);
            Assert.True(s.TryGetSkipEnd(S(9), out var end));
            Assert.Equal(S(10), end);
        }

        [Fact]
        public void NextSkipStart_finds_the_following_cut()
        {
            var s = new SkipRangeSchedule(new[] { new TimeRange(S(2), S(4)), new TimeRange(S(8), S(9)) });

            Assert.Equal(S(2), s.NextSkipStart(S(0)));
            Assert.Equal(S(2), s.NextSkipStart(S(2)));
            Assert.Equal(S(8), s.NextSkipStart(S(2.1)));
            Assert.Equal(TimeSpan.MaxValue, s.NextSkipStart(S(9)));
        }

        [Fact]
        public void Null_input_produces_empty_schedule()
        {
            var s = new SkipRangeSchedule(null);
            Assert.Empty(s.Ranges);
            Assert.False(s.TryGetSkipEnd(TimeSpan.Zero, out _));
        }

        [Fact]
        public void Many_ranges_binary_search_is_correct()
        {
            var ranges = new List<TimeRange>();
            for (int i = 0; i < 100; i++)
                ranges.Add(new TimeRange(S(i * 10), S(i * 10 + 2)));

            var s = new SkipRangeSchedule(ranges);
            for (int i = 0; i < 100; i++)
            {
                Assert.True(s.TryGetSkipEnd(S(i * 10 + 1), out var end));
                Assert.Equal(S(i * 10 + 2), end);
                Assert.False(s.TryGetSkipEnd(S(i * 10 + 5), out _));
            }
        }

        [Fact]
        public void Inverted_range_throws()
        {
            Assert.Throws<ArgumentException>(() => new TimeRange(S(5), S(2)));
        }
    }
}
