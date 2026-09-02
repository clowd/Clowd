using System;
using System.Xml.Linq;
using Clowd.VideoSDK.Composition;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The SMIL sampler: a real interval search over non-uniform keyTimes, a seamless loop at
    /// the phase wrap, and rejection of every form the wallpapers do not use.
    /// </summary>
    public class SmilTrackTests
    {
        private static XElement Animate(string values, string keyTimes = null, string calcMode = "linear",
            string repeat = "indefinite", string dur = "60s")
        {
            var e = new XElement("animate",
                new XAttribute("attributeName", "cx"),
                new XAttribute("dur", dur),
                new XAttribute("values", values));
            if (keyTimes != null) e.Add(new XAttribute("keyTimes", keyTimes));
            if (calcMode != null) e.Add(new XAttribute("calcMode", calcMode));
            if (repeat != null) e.Add(new XAttribute("repeatCount", repeat));
            return e;
        }

        private static float[] One(string s) => SvgPathSkeleton.ParseNumbers(s) is { Length: 1 } n ? n : null;

        private static float SampleAt(SmilTrack track, double phase)
        {
            Span<float> dst = stackalloc float[1];
            track.Sample(phase, dst);
            return dst[0];
        }

        [Fact]
        public void Interval_search_on_non_uniform_keytimes()
        {
            // 0..0.1 rises 0 -> 10, 0.1..1 falls 10 -> 0: the first segment is ten times steeper.
            var track = SmilTrack.TryParse(Animate("0;10;0", "0;0.1;1"), 1, One, out var why);
            Assert.Null(why);
            Assert.NotNull(track);
            Assert.Equal(60 * TimeSpan.TicksPerSecond, track.DurationTicks);
            Assert.Equal(5f, SampleAt(track, 0.05), 3);
            Assert.Equal(10f, SampleAt(track, 0.1), 3);
            Assert.Equal(5f, SampleAt(track, 0.55), 3);
            Assert.Equal(0f, SampleAt(track, 0), 3);
        }

        [Fact]
        public void Loop_is_seamless_when_first_equals_last()
        {
            var track = SmilTrack.TryParse(Animate("3;9;3", "0;0.5;1"), 1, One, out _);
            Assert.Equal(SampleAt(track, 0), SampleAt(track, 1 - 1e-7), 3);
            // Phases outside [0, 1) clamp rather than wrap; the caller wraps in ticks.
            Assert.Equal(SampleAt(track, 0), SampleAt(track, -1), 3);
            Assert.Equal(SampleAt(track, 1 - 1e-9), SampleAt(track, 2), 3);
        }

        [Fact]
        public void Monotonic_phase_gives_monotonic_position_on_a_two_value_track()
        {
            var track = SmilTrack.TryParse(Animate("0;100", "0;1"), 1, One, out _);
            float last = -1;
            for (int i = 0; i <= 100; i++)
            {
                float v = SampleAt(track, i / 100.0);
                Assert.True(v >= last);
                last = v;
            }
            Assert.Equal(100f, last, 3);
        }

        [Fact]
        public void Missing_keytimes_default_to_even_spacing()
        {
            var track = SmilTrack.TryParse(Animate("0;10;20"), 1, One, out _);
            Assert.Equal(new[] { 0f, 0.5f, 1f }, track.KeyTimes);
            Assert.Equal(10f, SampleAt(track, 0.5), 3);
        }

        [Fact]
        public void Multi_component_rows_interpolate_componentwise()
        {
            var track = SmilTrack.TryParse(Animate("0 100;10 0", "0;1"), 2,
                s => SvgPathSkeleton.ParseNumbers(s), out _);
            Span<float> dst = stackalloc float[2];
            track.Sample(0.25, dst);
            Assert.Equal(2.5f, dst[0], 3);
            Assert.Equal(75f, dst[1], 3);
        }

        [Theory]
        [InlineData("0;1", "0;1", "spline", "indefinite", "60s")]      // calcMode
        [InlineData("0;1", "0;1", "linear", "3", "60s")]               // finite repeat
        [InlineData("0;1", "0;1", "linear", null, "60s")]              // no repeat
        [InlineData("0;1", "0;0.5", "linear", "indefinite", "60s")]    // keyTimes not ending at 1
        [InlineData("0;1", "0;1;1", "linear", "indefinite", "60s")]    // count mismatch
        [InlineData("0;1;2", "0;0.8;0.5", "linear", "indefinite", "60s")] // not monotonic
        [InlineData("0;1", "0;1", "linear", "indefinite", null)]       // no dur
        [InlineData("0;a", "0;1", "linear", "indefinite", "60s")]      // a value that is not a number
        public void Forms_outside_the_corpus_are_rejected(string values, string keyTimes, string calcMode,
            string repeat, string dur)
        {
            var e = Animate(values, keyTimes, calcMode, repeat, dur ?? "0s");
            if (dur == null)
                e.Attribute("dur").Remove();
            Assert.Null(SmilTrack.TryParse(e, 1, One, out var why));
            Assert.False(string.IsNullOrEmpty(why));
        }

        [Fact]
        public void From_to_form_is_rejected()
        {
            var e = new XElement("animate", new XAttribute("from", "0"), new XAttribute("to", "1"),
                new XAttribute("dur", "1s"), new XAttribute("repeatCount", "indefinite"));
            Assert.Null(SmilTrack.TryParse(e, 1, One, out var why));
            Assert.Contains("values", why);
        }

        [Theory]
        [InlineData("60s", 600_000_000L)]
        [InlineData("90s", 900_000_000L)]
        [InlineData("1.6s", 16_000_000L)]
        [InlineData("400ms", 4_000_000L)]
        public void Durations_become_exact_ticks(string dur, long ticks)
        {
            Assert.Equal(ticks, SmilTrack.Ticks(dur));
        }
    }
}
