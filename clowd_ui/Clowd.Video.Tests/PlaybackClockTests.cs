using System;
using Clowd.Video.Playback;
using Xunit;

namespace Clowd.Video.Tests
{
    public class PlaybackClockTests
    {
        private sealed class FakeTime : IMonotonicTime
        {
            public TimeSpan Elapsed { get; set; }
            public void Advance(double seconds) => Elapsed += TimeSpan.FromSeconds(seconds);
        }

        private sealed class FakeAudio : IAudioClockSource
        {
            public bool HasTiming { get; set; }
            public TimeSpan PlayedTime { get; set; }
        }

        [Fact]
        public void Stopped_clock_holds_position()
        {
            var time = new FakeTime();
            var clock = new PlaybackClock(time);

            Assert.Equal(TimeSpan.Zero, clock.Position);
            time.Advance(5);
            Assert.Equal(TimeSpan.Zero, clock.Position);
        }

        [Fact]
        public void Running_clock_advances_with_wall_time()
        {
            var time = new FakeTime();
            var clock = new PlaybackClock(time);

            clock.Start();
            time.Advance(2.5);
            Assert.Equal(TimeSpan.FromSeconds(2.5), clock.Position);
        }

        [Fact]
        public void Pause_freezes_and_resume_continues()
        {
            var time = new FakeTime();
            var clock = new PlaybackClock(time);

            clock.Start();
            time.Advance(2);
            clock.Stop();
            time.Advance(10);
            Assert.Equal(TimeSpan.FromSeconds(2), clock.Position);

            clock.Start();
            time.Advance(1);
            Assert.Equal(TimeSpan.FromSeconds(3), clock.Position);
        }

        [Fact]
        public void SetPosition_rebases_while_running()
        {
            var time = new FakeTime();
            var clock = new PlaybackClock(time);

            clock.Start();
            time.Advance(2);
            clock.SetPosition(TimeSpan.FromSeconds(60));
            time.Advance(0.5);
            Assert.Equal(TimeSpan.FromSeconds(60.5), clock.Position);
        }

        [Fact]
        public void SetPosition_rebases_while_paused()
        {
            var time = new FakeTime();
            var clock = new PlaybackClock(time);

            clock.SetPosition(TimeSpan.FromSeconds(30));
            time.Advance(4);
            Assert.Equal(TimeSpan.FromSeconds(30), clock.Position);
        }

        [Fact]
        public void Audio_master_wins_when_it_has_timing()
        {
            var time = new FakeTime();
            var audio = new FakeAudio { HasTiming = false };
            var clock = new PlaybackClock(time);
            clock.SetAudioSource(audio);

            clock.Start();
            time.Advance(1);
            // no audio timing yet — stopwatch drives
            Assert.Equal(TimeSpan.FromSeconds(1), clock.Position);

            audio.HasTiming = true;
            audio.PlayedTime = TimeSpan.FromSeconds(0.8);
            Assert.Equal(TimeSpan.FromSeconds(0.8), clock.Position);

            audio.PlayedTime = TimeSpan.FromSeconds(1.6);
            Assert.Equal(TimeSpan.FromSeconds(1.6), clock.Position);
        }

        [Fact]
        public void Detaching_audio_continues_from_current_position()
        {
            var time = new FakeTime();
            var audio = new FakeAudio { HasTiming = true, PlayedTime = TimeSpan.FromSeconds(12) };
            var clock = new PlaybackClock(time);
            clock.SetAudioSource(audio);
            clock.Start();

            // audio track ended: hand off to the stopwatch without a position jump.
            clock.SetAudioSource(null);
            Assert.Equal(TimeSpan.FromSeconds(12), clock.Position);
            time.Advance(3);
            Assert.Equal(TimeSpan.FromSeconds(15), clock.Position);
        }
    }
}
