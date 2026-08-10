using System;
using System.Threading;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    public class SilentAudioOutputTests
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;

        /// <summary>Test-driven wall clock; read from the pump thread, written from the test.</summary>
        private sealed class FakeTime : IMonotonicTime
        {
            private long _ticks;
            public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
            public void Advance(double seconds) =>
                Interlocked.Add(ref _ticks, (long)(seconds * TimeSpan.TicksPerSecond));
        }

        private sealed class SampleCounter
        {
            private long _samples;
            public long Samples => Interlocked.Read(ref _samples);
            public long Frames => Interlocked.Read(ref _samples) / Channels;
            public void Render(Span<float> buffer) => Interlocked.Add(ref _samples, buffer.Length);
        }

        private static SilentAudioOutput Create(IMonotonicTime time, out SampleCounter counter)
        {
            counter = new SampleCounter();
            var output = new SilentAudioOutput(time);
            output.Initialize(SampleRate, Channels, 100, counter.Render);
            return output;
        }

        [Fact]
        public void Position_is_zero_until_play()
        {
            var time = new FakeTime();
            using var output = Create(time, out _);

            Assert.Equal(TimeSpan.Zero, output.Position);
            time.Advance(5);
            Assert.Equal(TimeSpan.Zero, output.Position);
        }

        [Fact]
        public void Position_advances_with_wall_time_while_playing()
        {
            var time = new FakeTime();
            using var output = Create(time, out _);

            output.Play();
            time.Advance(2.5);
            Assert.Equal(TimeSpan.FromSeconds(2.5), output.Position);
        }

        [Fact]
        public void Pause_freezes_position_and_play_resumes_from_it()
        {
            var time = new FakeTime();
            using var output = Create(time, out _);

            output.Play();
            time.Advance(1);
            output.Pause();

            time.Advance(10);
            Assert.Equal(TimeSpan.FromSeconds(1), output.Position);

            output.Play();
            time.Advance(0.5);
            Assert.Equal(TimeSpan.FromSeconds(1.5), output.Position);
        }

        [Fact]
        public void Stop_resets_position_and_play_restarts_from_zero()
        {
            var time = new FakeTime();
            using var output = Create(time, out _);

            output.Play();
            time.Advance(3);
            output.Stop();

            time.Advance(7);
            Assert.Equal(TimeSpan.Zero, output.Position);

            output.Play();
            time.Advance(0.25);
            Assert.Equal(TimeSpan.FromSeconds(0.25), output.Position);
        }

        [Fact]
        public void Play_before_initialize_throws()
        {
            using var output = new SilentAudioOutput(new FakeTime());
            Assert.Throws<InvalidOperationException>(() => output.Play());
        }

        [Fact]
        public void Factory_picks_the_backend_for_the_running_os()
        {
            using var output = AudioOutputFactory.Create();
            if (OperatingSystem.IsWindows())
                Assert.IsType<WasapiAudioOutput>(output);
            else
                Assert.IsType<SilentAudioOutput>(output);
        }

        [Fact]
        public void Render_callback_is_pulled_at_about_real_time()
        {
            // the whole point of the silent output: without a real device pulling, the sink's
            // played-sample count never moves and the master clock stalls.
            using var output = Create(new StopwatchTimeProbe(), out var counter);

            output.Play();
            Thread.Sleep(400);
            output.Pause();
            var position = output.Position;

            // never ahead of the clock (plus one pump block of slack for a pull that started
            // just before the Pause), and not lagging by more than a few pump intervals.
            long expected = (long)(position.TotalSeconds * SampleRate);
            Assert.InRange(counter.Frames, expected - SampleRate / 5, expected + SampleRate / 50);
            Assert.InRange(position.TotalMilliseconds, 300, 3000);
        }

        [Fact]
        public void Pause_stops_pulling_the_render_callback()
        {
            using var output = Create(new StopwatchTimeProbe(), out var counter);

            output.Play();
            Thread.Sleep(150);
            output.Pause();

            // let any pump in flight finish, then latch.
            Thread.Sleep(100);
            long afterPause = counter.Frames;
            Assert.True(afterPause > 0, "expected the callback to have been pulled while playing");

            Thread.Sleep(250);
            Assert.Equal(afterPause, counter.Frames);
        }

        [Fact]
        public void Dispose_stops_pulling()
        {
            var output = Create(new StopwatchTimeProbe(), out var counter);
            output.Play();
            Thread.Sleep(100);
            output.Dispose();

            Thread.Sleep(100);
            long afterDispose = counter.Frames;
            Thread.Sleep(200);
            Assert.Equal(afterDispose, counter.Frames);
        }

        /// <summary>The real stopwatch, for the tests that must observe true pacing.</summary>
        private sealed class StopwatchTimeProbe : IMonotonicTime
        {
            private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
            public TimeSpan Elapsed => _sw.Elapsed;
        }
    }
}
