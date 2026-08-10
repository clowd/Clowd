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
        public void Audio_master_interpolates_between_renderer_updates()
        {
            // WASAPI only moves its played-time once per device callback (~10ms). Read raw, the
            // master clock is a step function and every video frame due inside a step comes due
            // at once — the presenter then drops the ones that land more than a frame late.
            var time = new FakeTime();
            var audio = new FakeAudio { HasTiming = true, PlayedTime = TimeSpan.FromSeconds(1) };
            var clock = new PlaybackClock(time);
            clock.SetAudioSource(audio);
            clock.Start();

            Assert.Equal(TimeSpan.FromSeconds(1), clock.Position);

            time.Advance(0.004);
            Assert.Equal(TimeSpan.FromSeconds(1.004), clock.Position);
            time.Advance(0.004);
            Assert.Equal(TimeSpan.FromSeconds(1.008), clock.Position);

            // the next renderer update re-anchors: interpolation error never compounds.
            audio.PlayedTime = TimeSpan.FromSeconds(1.01);
            Assert.Equal(TimeSpan.FromSeconds(1.01), clock.Position);
            time.Advance(0.004);
            Assert.Equal(TimeSpan.FromSeconds(1.014), clock.Position);
        }

        [Fact]
        public void Audio_interpolation_stops_when_audio_stops_advancing()
        {
            // a lost device / permanent underrun must not have the clock inventing time forever.
            var time = new FakeTime();
            var audio = new FakeAudio { HasTiming = true, PlayedTime = TimeSpan.FromSeconds(3) };
            var clock = new PlaybackClock(time);
            clock.SetAudioSource(audio);
            clock.Start();

            Assert.Equal(TimeSpan.FromSeconds(3), clock.Position);
            time.Advance(30);
            Assert.Equal(TimeSpan.FromSeconds(3.5), clock.Position);
        }

        [Fact]
        public void Seek_resets_interpolation_to_the_seek_target()
        {
            // the sink resets its own timing on a seek; the clock must not carry an anchor from
            // before it across, or the first read after the seek drifts off the target.
            var time = new FakeTime();
            var audio = new FakeAudio { HasTiming = true, PlayedTime = TimeSpan.FromSeconds(2) };
            var clock = new PlaybackClock(time);
            clock.SetAudioSource(audio);
            clock.Start();
            time.Advance(0.006);

            audio.HasTiming = false; // NAudioSink.ResetTiming
            clock.SetPosition(TimeSpan.FromSeconds(40));
            Assert.Equal(TimeSpan.FromSeconds(40), clock.Position);

            // audio comes back at the seek target: the first read re-anchors there exactly,
            // and interpolation resumes from it rather than from the pre-seek anchor.
            audio.HasTiming = true;
            audio.PlayedTime = TimeSpan.FromSeconds(40);
            Assert.Equal(TimeSpan.FromSeconds(40), clock.Position);
            time.Advance(0.003);
            Assert.Equal(TimeSpan.FromSeconds(40.003), clock.Position);
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
