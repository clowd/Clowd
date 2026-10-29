using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Interop.Tests
{
    /// <summary>
    /// The devices and audio wrappers (<see cref="CaptureDevices"/>, <see cref="CoreAudioOutputDevice"/>,
    /// <see cref="CoreAudioInput"/>) against the real library: layouts, enum values, one success and one
    /// failure per family, and the input's <c>request_stop</c> racing its destroy (R19). Device facts
    /// skip on a machine without the device; the microphone facts skip without a grant.
    /// </summary>
    public sealed class AudioDeviceTests
    {
        private static bool IsMac => OperatingSystem.IsMacOS();

        [Fact]
        public void The_structs_match_the_rust_layout()
        {
            Assert.Equal(48, Marshal.SizeOf<PriscDevice>());
            Assert.Equal(16, (int)Marshal.OffsetOf<PriscDevice>(nameof(PriscDevice.name)));
            Assert.Equal(32, (int)Marshal.OffsetOf<PriscDevice>(nameof(PriscDevice.kind)));
            Assert.Equal(36, (int)Marshal.OffsetOf<PriscDevice>(nameof(PriscDevice.is_default)));
            Assert.Equal(40, (int)Marshal.OffsetOf<PriscDevice>(nameof(PriscDevice.input_channels)));
            Assert.Equal(8, Marshal.SizeOf<PriscAudioOutputInfo>());
            Assert.Equal(4, (int)Marshal.OffsetOf<PriscAudioOutputInfo>(nameof(PriscAudioOutputInfo.sample_rate_hz)));
            Assert.Equal(24, Marshal.SizeOf<PriscAudioInputEvent>());
            Assert.Equal(8, (int)Marshal.OffsetOf<PriscAudioInputEvent>(nameof(PriscAudioInputEvent.data)));
            Assert.Equal(16, (int)Marshal.OffsetOf<PriscAudioInputEvent>(nameof(PriscAudioInputEvent.stop)));
            Assert.Equal(20, (int)Marshal.OffsetOf<PriscAudioInputEvent>(nameof(PriscAudioInputEvent.os_status)));
            Assert.Equal(32, Marshal.SizeOf<PriscAudioInputInfo>());
            Assert.Equal(8, (int)Marshal.OffsetOf<PriscAudioInputInfo>(nameof(PriscAudioInputInfo.used_default)));
            Assert.Equal(16, (int)Marshal.OffsetOf<PriscAudioInputInfo>(nameof(PriscAudioInputInfo.name)));
        }

        [Fact]
        public void The_enum_values_are_the_wire_contract()
        {
            Assert.Equal(0, (int)DeviceKind.Video);
            Assert.Equal(1, (int)DeviceKind.AudioInput);
            Assert.Equal(2, (int)DeviceKind.AudioOutput);
            Assert.Equal(0u, (uint)AudioInputEventKind.Data);
            Assert.Equal(1u, (uint)AudioInputEventKind.Stopped);
            Assert.Equal(1, (int)AudioInputStop.DeviceLost);
            Assert.Equal(2, (int)AudioInputStop.RequeueFailed);
        }

        // ---------------------------------------------------------------------------------- lists

        [Fact]
        public void Each_kind_answers_truthfully_on_this_os()
        {
            InteropTestGate.Require();
            foreach (var kind in new[] { DeviceKind.Video, DeviceKind.AudioInput, DeviceKind.AudioOutput })
            {
                bool listed = IsMac || (OperatingSystem.IsWindows() && kind == DeviceKind.Video);
                if (!listed)
                {
                    var e = Assert.Throws<InteropException>(() => CaptureDevices.Enumerate(kind));
                    Assert.Equal(InteropStatus.Unsupported, e.Status);
                    Assert.Null(CaptureDevices.TryEnumerate(kind, out var error));
                    Assert.IsType<InteropException>(error);
                    continue;
                }

                var devices = CaptureDevices.Enumerate(kind);
                Assert.All(devices, d => Assert.Equal(kind, d.Kind));
                Assert.All(devices, d => Assert.False(String.IsNullOrEmpty(d.Id)));
                Assert.Equal(devices.Count, devices.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
                Assert.True(devices.Count(d => d.IsDefault) <= 1);
                if (kind == DeviceKind.AudioInput)
                    Assert.All(devices, d => Assert.True(d.InputChannels > 0));
                else
                    Assert.All(devices, d => Assert.Equal(0, d.InputChannels));
            }
        }

        [Fact]
        public void An_unknown_kind_is_an_argument_error()
        {
            InteropTestGate.Require();
            var e = Assert.Throws<InteropException>(() => CaptureDevices.Enumerate((DeviceKind)7));
            Assert.Equal(InteropStatus.InvalidArg, e.Status);
            Assert.Contains("PriscDeviceKind", e.Message, StringComparison.Ordinal);
        }

        // --------------------------------------------------------------------------------- output

        [Fact]
        public void Output_pulls_while_started_and_never_after_dispose()
        {
            InteropTestGate.Require();
            long pulled = 0;
            void Render(Span<float> buffer, int frames, int channels)
            {
                Assert.Equal(frames * channels, buffer.Length);
                buffer.Clear();
                Interlocked.Add(ref pulled, frames);
            }

            if (!IsMac)
            {
                var e = Assert.Throws<InteropException>(() => CoreAudioOutputDevice.Open(48000, 2, Render));
                Assert.Equal(InteropStatus.Unsupported, e.Status);
                return;
            }

            CoreAudioOutputDevice output;
            try
            {
                output = CoreAudioOutputDevice.Open(48000, 2, Render);
            }
            catch (InvalidOperationException e)
            {
                Assert.Skip("no usable output device: " + e.Message);
                return;
            }

            Assert.True(output.DeviceSampleRateHz > 0);
            Assert.True(output.LatencyFrames is null or > 0);
            output.Start();
            Assert.True(SpinWait.SpinUntil(() => Interlocked.Read(ref pulled) > 0, 3000), "never pulled");
            output.Stop();
            output.Start();
            output.Dispose();
            Thread.Sleep(200);
            long latched = Interlocked.Read(ref pulled);
            Thread.Sleep(300);
            Assert.Equal(latched, Interlocked.Read(ref pulled));
            Assert.Equal(0, output.RenderFaults);
            Assert.Throws<ObjectDisposedException>(() => output.Start());
        }

        [Fact]
        public void A_throwing_render_handler_is_counted_and_plays_silence()
        {
            InteropTestGate.Require();
            Assert.SkipUnless(IsMac, "the output is macOS only");
            CoreAudioOutputDevice output;
            try
            {
                output = CoreAudioOutputDevice.Open(48000, 2, (_, _, _) => throw new InvalidOperationException("render failed"));
            }
            catch (InvalidOperationException e)
            {
                Assert.Skip("no usable output device: " + e.Message);
                return;
            }

            using (output)
            {
                output.Start();
                Assert.True(SpinWait.SpinUntil(() => output.RenderFaults > 0, 3000), "the handler never ran");
            }
        }

        [Fact]
        public void Output_arguments_are_checked_in_csharp()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CoreAudioOutputDevice.Open(0, 2, (_, _, _) => { }));
            Assert.Throws<ArgumentOutOfRangeException>(() => CoreAudioOutputDevice.Open(48000, CoreAudioOutputDevice.MaxChannels + 1, (_, _, _) => { }));
            Assert.Throws<ArgumentNullException>(() => CoreAudioOutputDevice.Open(48000, 2, null!));
        }

        /// <summary>The C# <see cref="CoreAudioOutputDevice.MaxChannels"/> is the crate's PRISC_AUDIO_OUTPUT_MAX_CHANNELS:
        /// one more is refused for its channel count, and the limit itself passes the channel check and
        /// is refused only for the null callback checked after it (on every OS, before any device).</summary>
        [Fact]
        public unsafe void The_channel_limit_matches_the_crate()
        {
            InteropTestGate.Require();
            PriscAudioOutput* output = null;
            PriscAudioOutputInfo info = default;
            int status = NativeMethods.prisc_audio_output_create(48000, CoreAudioOutputDevice.MaxChannels + 1, null, null, &output, &info);
            Assert.Equal((int)InteropStatus.InvalidArg, status);
            Assert.Contains("channels", Native.LastErrorMessage(), StringComparison.Ordinal);

            status = NativeMethods.prisc_audio_output_create(48000, CoreAudioOutputDevice.MaxChannels, null, null, &output, &info);
            Assert.Equal((int)InteropStatus.InvalidArg, status);
            Assert.DoesNotContain("channels", Native.LastErrorMessage(), StringComparison.Ordinal);
            Assert.True(output == null);
        }

        // ---------------------------------------------------------------------------------- input

        /// <summary>The default input, or a skip naming why there is none (no input device; no grant,
        /// asserting the documented answer for a refusal).</summary>
        private static CoreAudioInput OpenInput(AudioInputDataHandler onData, AudioInputStoppedHandler onStopped)
        {
            if (!IsMac)
            {
                var e = Assert.Throws<InteropException>(() => CoreAudioInput.Open(null, 48000, 50, onData, onStopped));
                Assert.Equal(InteropStatus.Unsupported, e.Status);
                Assert.Skip("the input is macOS only (NAudio serves Windows)");
            }

            try
            {
                return CoreAudioInput.Open(null, 48000, 50, onData, onStopped);
            }
            catch (UnauthorizedAccessException e)
            {
                Assert.Contains("Microphone access is denied", e.Message, StringComparison.Ordinal);
                Assert.Skip("no microphone grant: " + e.Message);
            }
            catch (InvalidOperationException e)
            {
                Assert.Skip("no microphone: " + e.Message);
            }

            return null!;
        }

        [Fact]
        public void Input_delivers_data_and_request_stop_ends_it_once()
        {
            InteropTestGate.Require();
            int buffers = 0, stops = 0;
            using var input = OpenInput(data =>
            {
                Assert.Equal(0, data.Length % 4);
                Interlocked.Increment(ref buffers);
                return true;
            }, (_, _) => Interlocked.Increment(ref stops));

            Assert.True(input.UsedDefault);
            Assert.Equal(48000, input.SampleRateHz);
            Assert.True(input.Channels >= 1);
            Assert.False(String.IsNullOrEmpty(input.Name));
            input.Start();
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref buffers) > 0, 3000))
            {
                // A machine whose grant is not determined runs the queue but may deliver nothing.
                Assert.SkipWhen(Environment.GetEnvironmentVariable("CI") != null, "no microphone data on this CI machine");
                Assert.Fail("no microphone data within 3 s");
            }

            Assert.True(input.RequestStop());
            Assert.False(input.RequestStop());
            Thread.Sleep(150);
            int latched = Volatile.Read(ref buffers);
            Thread.Sleep(250);
            Assert.Equal(latched, Volatile.Read(ref buffers));
            Assert.Equal(0, Volatile.Read(ref stops));
            Assert.Throws<InvalidOperationException>(() => input.Start());
        }

        /// <summary>A handler that returned false ended delivery, so a later request is not the first.</summary>
        [Fact]
        public void Request_stop_after_a_handler_ended_delivery_is_not_first()
        {
            InteropTestGate.Require();
            int buffers = 0;
            using var input = OpenInput(_ =>
            {
                Interlocked.Increment(ref buffers);
                return false;
            }, (_, _) => { });

            input.Start();
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref buffers) > 0, 3000))
            {
                Assert.SkipWhen(Environment.GetEnvironmentVariable("CI") != null, "no microphone data on this CI machine");
                Assert.Fail("no microphone data within 3 s");
            }

            Assert.False(input.RequestStop());
            Thread.Sleep(200);
            Assert.Equal(1, Volatile.Read(ref buffers));
        }

        /// <summary>A handler of one input may stop and dispose another (a device switch): neither call
        /// reaches the library from the callback thread (C3), the stop answers first exactly once, the
        /// other input delivers nothing after it, and its teardown completes off the callback.</summary>
        [Fact]
        public void A_handler_may_stop_and_dispose_another_input()
        {
            InteropTestGate.Require();
            int otherBuffers = 0, otherStops = 0;
            using var other = OpenInput(_ =>
            {
                Interlocked.Increment(ref otherBuffers);
                return true;
            }, (_, _) => Interlocked.Increment(ref otherStops));
            bool? firstStop = null, secondStop = null;
            int latched = -1;
            var done = new ManualResetEventSlim();
            int calls = 0;
            using var input = OpenInput(_ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstStop = other.RequestStop();
                    latched = Volatile.Read(ref otherBuffers);
                    secondStop = other.RequestStop();
                    other.Dispose();
                    done.Set();
                }

                return true;
            }, (_, _) => { });

            other.Start();
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref otherBuffers) > 0, 3000))
            {
                Assert.SkipWhen(Environment.GetEnvironmentVariable("CI") != null, "no microphone data on this CI machine");
                Assert.Fail("no microphone data within 3 s");
            }

            input.Start();
            Assert.True(done.Wait(3000), "the first input delivered nothing within 3 s");
            Assert.True(firstStop);
            Assert.False(secondStop);
            Thread.Sleep(300);
            // At most one buffer already inside the other handler when the stop landed.
            Assert.InRange(Volatile.Read(ref otherBuffers), latched, latched + 1);
            Assert.Equal(0, Volatile.Read(ref otherStops));
            Assert.Throws<ObjectDisposedException>(() => other.RequestStop());
        }

        [Fact]
        public void A_handler_may_stop_and_dispose_from_inside_the_callback()
        {
            InteropTestGate.Require();
            CoreAudioInput input = null!;
            int buffers = 0;
            var disposed = new ManualResetEventSlim();
            input = OpenInput(_ =>
            {
                if (Interlocked.Increment(ref buffers) == 1)
                {
                    // Neither may call into the library from here (C3); both still work.
                    Assert.True(input.RequestStop());
                    input.Dispose();
                    disposed.Set();
                }

                return true;
            }, (_, _) => { });

            input.Start();
            if (!disposed.Wait(3000))
            {
                input.Dispose();
                Assert.SkipWhen(Environment.GetEnvironmentVariable("CI") != null, "no microphone data on this CI machine");
                Assert.Fail("no microphone data within 3 s");
            }

            Thread.Sleep(300);
            Assert.Equal(1, Volatile.Read(ref buffers));
            Assert.Throws<ObjectDisposedException>(() => input.Start());
        }

        /// <summary>R19: a <c>request_stop</c> in flight on another thread when <see cref="CoreAudioInput.Dispose"/>
        /// runs must neither crash nor leave the device open; the destroy waits for the lease.</summary>
        [Fact]
        public async Task Request_stop_racing_dispose_is_safe()
        {
            InteropTestGate.Require();
            for (int round = 0; round < 20; round++)
            {
                var input = OpenInput(_ => true, (_, _) => { });
                input.Start();
                using var go = new Barrier(2);
                var stopper = Task.Run(() =>
                {
                    go.SignalAndWait();
                    for (int i = 0; i < 200; i++)
                    {
                        try
                        {
                            input.RequestStop();
                        }
                        catch (ObjectDisposedException)
                        {
                            return;
                        }
                    }
                });
                go.SignalAndWait();
                input.Dispose();
                // A hang throws TimeoutException: request_stop must not block against the destroy.
                await stopper.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                Assert.Throws<ObjectDisposedException>(() => input.RequestStop());
            }
        }

        [Fact]
        public void Input_arguments_are_checked_in_csharp()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CoreAudioInput.Open(null, 0, 50, _ => true, (_, _) => { }));
            Assert.Throws<ArgumentOutOfRangeException>(() => CoreAudioInput.Open(null, 48000, -1, _ => true, (_, _) => { }));
            Assert.Throws<ArgumentNullException>(() => CoreAudioInput.Open(null, 48000, 50, null!, (_, _) => { }));
        }
    }
}
