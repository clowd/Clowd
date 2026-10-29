using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Prisc.Interop
{
    /// <summary>Pull callback, invoked on the OS audio thread: fill the whole span with interleaved
    /// float samples (pad silence on an underrun; the device plays whatever is in the buffer).</summary>
    /// <remarks>It must not block, allocate or take a lock the control path holds, and must not call
    /// into Prisc.Interop. An exception out of it silences that buffer and is counted
    /// (<see cref="CoreAudioOutputDevice.RenderFaults"/>).</remarks>
    public delegate void AudioRenderHandler(Span<float> interleaved, int frames, int channels);

    /// <summary>
    /// The default audio output of <c>prisc_audio_output_*</c> (the default-output AudioUnit on
    /// macOS; Windows plays through NAudio and answers <see cref="InteropStatus.Unsupported"/>): open
    /// it with a format and a pull callback, start, stop, dispose.
    /// </summary>
    /// <remarks>
    /// <para>The handler is rooted by a <see cref="GCHandle"/> passed as the callback's user pointer
    /// and freed only after <c>prisc_audio_output_destroy</c> has returned, which tears the IO proc
    /// down and waits out a callback in flight. Every call goes through a lease (R19), so a
    /// <see cref="Dispose"/> racing <see cref="Start"/> destroys the unit only after that call is
    /// out of the library.</para>
    /// <para>Device and OS failures surface as <see cref="InvalidOperationException"/> with the
    /// crate's message (the type the audio callers have always caught), the
    /// <see cref="InteropException"/> as its inner exception.</para>
    /// </remarks>
    public sealed unsafe class CoreAudioOutputDevice : IDisposable
    {
        private readonly OutputHandle _handle;
        private readonly RenderBinding _binding;

        private CoreAudioOutputDevice(OutputHandle handle, RenderBinding binding, in PriscAudioOutputInfo info)
        {
            _handle = handle;
            _binding = binding;
            LatencyFrames = info.latency_frames == 0 ? null : (int)info.latency_frames;
            DeviceSampleRateHz = (int)info.sample_rate_hz;
        }

        /// <summary>The device's IO buffer plus its own latency plus its safety offset, in frames at
        /// <see cref="DeviceSampleRateHz"/>, measured when the unit opened; null when the device
        /// publishes none of the three (keep the requested figure).</summary>
        public int? LatencyFrames { get; }

        /// <summary>The rate <see cref="LatencyFrames"/> counts in: the device side of the unit.</summary>
        public int DeviceSampleRateHz { get; }

        /// <summary>The most channels <see cref="Open"/> accepts. Mirrors <c>MAX_CHANNELS</c> in
        /// crates/prisc_interop/src/ffi/audio_output.rs, which refuses more with InvalidArg.</summary>
        public const int MaxChannels = 4096;

        /// <summary>How many exceptions the handler threw (each buffer played silence). Zero in a
        /// healthy run.</summary>
        public long RenderFaults => Interlocked.Read(ref _binding.Faults);

        /// <summary>
        /// Opens the default output at this format with <paramref name="render"/> as the pull,
        /// initialised and not started. Binds a device (tens of milliseconds on a cold HAL), so it
        /// belongs on a play path, not a paint path.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A rate of zero or less, or channels outside
        /// 1..<see cref="MaxChannels"/>.</exception>
        /// <exception cref="InvalidOperationException">No default output unit, or an AudioToolbox call
        /// failed (the inner <see cref="InteropException"/> carries the OSStatus).</exception>
        /// <exception cref="InteropException"><see cref="InteropStatus.Unsupported"/> off macOS.</exception>
        public static CoreAudioOutputDevice Open(int sampleRateHz, int channels, AudioRenderHandler render)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
            if (channels <= 0 || channels > MaxChannels)
                throw new ArgumentOutOfRangeException(nameof(channels));
            ArgumentNullException.ThrowIfNull(render);
            InteropLibrary.Require();

            var binding = new RenderBinding(render);
            var rooted = GCHandle.Alloc(binding);
            PriscAudioOutput* output = null;
            PriscAudioOutputInfo info = default;
            int status = NativeMethods.prisc_audio_output_create(
                (uint)sampleRateHz, (uint)channels, &RenderThunk, (void*)GCHandle.ToIntPtr(rooted), &output, &info);
            if (status != (int)InteropStatus.Ok || output == null)
            {
                rooted.Free();
                AudioStatus.Throw(status, nameof(NativeMethods.prisc_audio_output_create));
            }

            return new CoreAudioOutputDevice(new OutputHandle(output, rooted), binding, info);
        }

        /// <summary>Starts the pull. Idempotent; a failure leaves the device stopped and a later start
        /// retries.</summary>
        public void Start()
        {
            using var lease = _handle.Lease();
            AudioStatus.Check(NativeMethods.prisc_audio_output_start((PriscAudioOutput*)lease.Handle),
                nameof(NativeMethods.prisc_audio_output_start));
        }

        /// <summary>Stops the pull. No further callback begins after it returns, but one already
        /// running may still be in flight; only <see cref="Dispose"/> ends the handler's use.</summary>
        public void Stop()
        {
            using var lease = _handle.Lease();
            AudioStatus.Check(NativeMethods.prisc_audio_output_stop((PriscAudioOutput*)lease.Handle),
                nameof(NativeMethods.prisc_audio_output_stop));
        }

        /// <summary>Stops, uninitialises and disposes the unit, then unroots the handler.</summary>
        public void Dispose()
        {
            long faults = RenderFaults;
            if (faults > 0)
                System.Diagnostics.Debug.WriteLine($"The audio output's render handler threw {faults} time(s); those buffers played silence.");
            _handle.Dispose();
        }

        /// <summary>What the user pointer roots: the handler and its fault count.</summary>
        private sealed class RenderBinding
        {
            internal readonly AudioRenderHandler Handler;
            internal long Faults;

            internal RenderBinding(AudioRenderHandler handler) => Handler = handler;
        }

        /// <summary>Owns the native output and the <see cref="GCHandle"/> rooting its handler, and frees
        /// them in that order.</summary>
        private sealed class OutputHandle : InteropHandle
        {
            private GCHandle _binding;

            internal OutputHandle(PriscAudioOutput* output, GCHandle binding)
            {
                _binding = binding;
                Adopt((IntPtr)output);
            }

            protected override int Destroy(IntPtr h)
            {
                int status = NativeMethods.prisc_audio_output_destroy((PriscAudioOutput*)h);
                // Destroy tears down on every path except a panic inside destroy itself; then the IO
                // thread may still be live, and a leaked handle beats it reading freed memory.
                if (status != (int)InteropStatus.Panic && _binding.IsAllocated)
                    _binding.Free();
                return status;
            }
        }

        /// <summary>The OS audio thread, one frame past the native trampoline.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void RenderThunk(void* user, float* samples, uint frames, uint channels)
        {
            // Nothing may escape an UnmanagedCallersOnly method: an exception here is a fail-fast.
            int count = (int)(frames * channels);
            try
            {
                var binding = (RenderBinding)GCHandle.FromIntPtr((IntPtr)user).Target!;
                try
                {
                    binding.Handler(new Span<float>(samples, count), (int)frames, (int)channels);
                }
                catch
                {
                    new Span<float>(samples, count).Clear();
                    Interlocked.Increment(ref binding.Faults);
                }
            }
            catch
            {
                new Span<float>(samples, count).Clear();
            }
        }
    }

    /// <summary>The audio area's status mapping, shared by the output and input wrappers.</summary>
    internal static class AudioStatus
    {
        /// <summary>Returns for a success; throws for the rest (<see cref="Throw"/>).</summary>
        internal static void Check(int status, string operation)
        {
            if (status >= 0)
                return;
            Throw(status, operation);
        }

        /// <summary>Permission is <see cref="UnauthorizedAccessException"/>, a device or OS failure
        /// (Capability, Os, State) is <see cref="InvalidOperationException"/>, both with the crate's
        /// message and the <see cref="InteropException"/> inside; the rest (Unsupported, InvalidArg,
        /// Panic) is the <see cref="InteropException"/> itself.</summary>
        [DoesNotReturn]
        internal static void Throw(int status, string operation)
        {
            string detail = InteropLibrary.IsAvailable ? Native.LastErrorMessage() : String.Empty;
            var failure = Native.Failure(status, operation);
            string message = detail.Length > 0 ? detail : failure.Message;
            switch ((InteropStatus)status)
            {
                case InteropStatus.Permission:
                    throw new UnauthorizedAccessException(message, failure);
                case InteropStatus.Capability:
                case InteropStatus.Os:
                case InteropStatus.State:
                    throw new InvalidOperationException(message, failure);
                default:
                    throw failure;
            }
        }
    }
}
