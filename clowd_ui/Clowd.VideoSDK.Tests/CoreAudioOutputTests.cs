using System;
using System.Runtime.Versioning;
using System.Threading;
using Clowd.VideoSDK.Audio;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The macOS output against a real device — there is no seam to fake here, since what these
    /// assert is exactly that CoreAudio drives the callback (the WASAPI output has no unit tests
    /// for the same reason). Every one of them skips off macOS, and on a machine with no usable
    /// output device: a build agent without audio hardware must not fail the suite.
    /// </summary>
    /// <remarks><see cref="SupportedOSPlatform"/> is what stops CA1416 firing on every call into
    /// the macOS-annotated output; xUnit reaches these by reflection, so the attribute constrains
    /// nothing at run time and the skips below are what actually keep the class safe elsewhere.
    /// </remarks>
    [SupportedOSPlatform("macos")]
    public class CoreAudioOutputTests
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;

        /// <summary>Counts what the device pulled, and fills silence — nothing here listens.</summary>
        private sealed class SampleCounter
        {
            private long _samples;
            public long Frames => Interlocked.Read(ref _samples) / Channels;
            public void Render(Span<float> buffer)
            {
                buffer.Clear();
                Interlocked.Add(ref _samples, buffer.Length);
            }
        }

        /// <summary>An initialized, playing output, or null when this machine cannot give us one.
        /// Opening the unit is what actually touches the device, so that is what is guarded.</summary>
        private static CoreAudioOutput TryPlay(out SampleCounter counter)
        {
            counter = new SampleCounter();
            if (!OperatingSystem.IsMacOS())
                return null;

            var output = new CoreAudioOutput();
            output.Initialize(SampleRate, Channels, 50, counter.Render);
            try
            {
                output.Play();
            }
            catch (InvalidOperationException)
            {
                output.Dispose();
                return null;
            }
            return output;
        }

        private static void RequireDevice(CoreAudioOutput output) =>
            Assert.SkipWhen(output == null,
                "no CoreAudio output device (not macOS, or the machine has no audio hardware)");

        /// <summary>Waits for the device to pull at least <paramref name="frames"/> frames, and
        /// reports what it had pulled when the wait ended.</summary>
        private static long WaitForFrames(SampleCounter counter, long frames, double timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (counter.Frames < frames && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            return counter.Frames;
        }

        [Fact]
        public void The_device_pulls_the_render_callback_at_about_real_time()
        {
            using var output = TryPlay(out var counter);
            RequireDevice(output);

            // half a second of audio, which a working device delivers in about half a second. The
            // ceiling is generous on purpose: it is here to catch "never pulls" and "pulls once",
            // not to measure a real-time thread's jitter on a loaded build machine.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long frames = WaitForFrames(counter, SampleRate / 2, timeoutSeconds: 10);
            sw.Stop();

            Assert.True(frames >= SampleRate / 2,
                $"device pulled only {frames} frames in {sw.ElapsedMilliseconds}ms");
            Assert.InRange(sw.Elapsed.TotalMilliseconds, 100, 5_000);
        }

        [Fact]
        public void Pause_stops_the_pull_and_play_resumes_it()
        {
            using var output = TryPlay(out var counter);
            RequireDevice(output);

            Assert.True(WaitForFrames(counter, 1, timeoutSeconds: 10) > 0,
                "expected the device to have pulled while playing");

            output.Pause();

            // AudioOutputUnitStop is synchronous, but let a callback already on the real-time
            // thread finish before latching the count.
            Thread.Sleep(200);
            long afterPause = counter.Frames;
            Thread.Sleep(300);
            Assert.Equal(afterPause, counter.Frames);

            output.Play();
            Assert.True(WaitForFrames(counter, afterPause + 1, timeoutSeconds: 10) > afterPause,
                "expected the device to resume pulling after Play");
        }

        [Fact]
        public void Dispose_stops_the_pull()
        {
            var output = TryPlay(out var counter);
            RequireDevice(output);

            Assert.True(WaitForFrames(counter, 1, timeoutSeconds: 10) > 0,
                "expected the device to have pulled while playing");

            output.Dispose();

            Thread.Sleep(200);
            long afterDispose = counter.Frames;
            Thread.Sleep(300);
            Assert.Equal(afterDispose, counter.Frames);
        }

        [Fact]
        public void Dispose_is_idempotent_and_play_after_it_is_a_no_op()
        {
            var output = TryPlay(out _);
            RequireDevice(output);

            output.Dispose();
            output.Dispose();
            output.Play();  // must not resurrect the unit, and must not throw
            output.Pause();
        }

        [Fact]
        public void Play_before_initialize_throws()
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only");

            using var output = new CoreAudioOutput();
            Assert.Throws<InvalidOperationException>(() => output.Play());
        }
    }
}
