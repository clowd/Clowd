using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Prisc.Interop
{
    /// <summary>A captured buffer of interleaved float32 samples, valid only until the handler
    /// returns. Return false to end delivery (no stop notification follows an end the handler asked
    /// for).</summary>
    public delegate bool AudioInputDataHandler(ReadOnlySpan<byte> interleavedFloat32);

    /// <summary>Delivery ended by itself: the device went away, or the queue refused a buffer
    /// (<paramref name="osStatus"/> is the OSStatus then). Raised at most once.</summary>
    public delegate void AudioInputStoppedHandler(AudioInputStop reason, int osStatus);

    /// <summary>
    /// The microphone input of <c>prisc_audio_input_*</c> (an AudioQueue on one CoreAudio device on
    /// macOS; Windows captures through NAudio and answers <see cref="InteropStatus.Unsupported"/>).
    /// </summary>
    /// <remarks>
    /// <para>Both handlers run on the OS audio threads, never two at once and never after
    /// <see cref="Dispose"/> returns. They must not block for long. Calling <see cref="RequestStop"/>
    /// or <see cref="Dispose"/> from inside a handler is allowed: neither calls into the library from
    /// there (ABI rule C3), the first ends delivery when the handler returns and the second finishes
    /// the teardown on the thread pool.</para>
    /// <para>Every call goes through a lease (R19), and <see cref="Dispose"/> ends delivery first, so
    /// a teardown racing <see cref="RequestStop"/> on another thread destroys the input only after that
    /// call is out of the library. The handlers are rooted by a <see cref="GCHandle"/> freed after the
    /// native destroy has returned.</para>
    /// <para>Failures surface as <see cref="UnauthorizedAccessException"/> (the microphone is refused),
    /// <see cref="InvalidOperationException"/> (no microphone, or a CoreAudio call failed, with the
    /// crate's message) or <see cref="InteropException"/> (Unsupported, InvalidArg).</para>
    /// </remarks>
    public sealed unsafe class CoreAudioInput : IDisposable
    {
        [ThreadStatic] private static InputBinding? t_current;

        private readonly InputHandle _handle;
        private readonly InputBinding _binding;
        private int _disposed;

        private CoreAudioInput(InputHandle handle, InputBinding binding, in PriscAudioInputInfo info)
        {
            _handle = handle;
            _binding = binding;
            // Mode 3: borrowed from the handle, which is alive here.
            Name = Utf8.Read(info.name);
            Channels = (int)info.channels;
            SampleRateHz = (int)info.sample_rate_hz;
            UsedDefault = info.used_default != 0;
        }

        /// <summary>The device's own name (its UID when the name cannot be read), never "Default".</summary>
        public string Name { get; }

        /// <summary>Interleaved channel count of every buffer: the device's own (at least 1).</summary>
        public int Channels { get; }

        /// <summary>The rate of every buffer (the requested one; the queue converts).</summary>
        public int SampleRateHz { get; }

        /// <summary>Whether the system default input was opened because no UID was given or it no
        /// longer resolved.</summary>
        public bool UsedDefault { get; }

        /// <summary>
        /// Opens the device with <paramref name="deviceUid"/> (null or empty, or a UID that no longer
        /// resolves, is the system default input) for float32 at <paramref name="sampleRateHz"/> in
        /// buffers of about <paramref name="bufferMs"/>. Delivery starts with <see cref="Start"/>.
        /// Never prompts: a microphone already refused is an <see cref="UnauthorizedAccessException"/>.
        /// </summary>
        public static CoreAudioInput Open(string? deviceUid, int sampleRateHz, int bufferMs,
            AudioInputDataHandler onData, AudioInputStoppedHandler onStopped)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
            ArgumentOutOfRangeException.ThrowIfNegative(bufferMs);
            ArgumentNullException.ThrowIfNull(onData);
            ArgumentNullException.ThrowIfNull(onStopped);
            InteropLibrary.Require();

            var binding = new InputBinding(onData, onStopped);
            var rooted = GCHandle.Alloc(binding);
            byte[] uid = Encoding.UTF8.GetBytes(deviceUid ?? String.Empty);
            PriscAudioInput* input = null;
            PriscAudioInputInfo info = default;
            int status;
            fixed (byte* p = uid)
            {
                status = NativeMethods.prisc_audio_input_open(p, (nuint)uid.Length, (uint)sampleRateHz, (uint)bufferMs,
                    &Receive, (void*)GCHandle.ToIntPtr(rooted), &input, &info);
            }

            if (status != (int)InteropStatus.Ok || input == null)
            {
                rooted.Free();
                AudioStatus.Throw(status, nameof(NativeMethods.prisc_audio_input_open));
            }

            return new CoreAudioInput(new InputHandle(input, rooted), binding, info);
        }

        /// <summary>Starts delivery (idempotent) and the device-loss watch; false when the OS refused the
        /// watch (an unplug then freezes delivery instead of raising the stop handler).</summary>
        /// <exception cref="InvalidOperationException">The input already stopped, or the queue would
        /// not start.</exception>
        public bool Start()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var lease = _handle.Lease();
            byte watching = 0;
            AudioStatus.Check(NativeMethods.prisc_audio_input_start((PriscAudioInput*)lease.Handle, &watching),
                nameof(NativeMethods.prisc_audio_input_start));
            return watching != 0;
        }

        /// <summary>Ends delivery; true for the call that ended it, false when it had already ended (an
        /// earlier request, a handler that returned false, or the stop handler). Never invokes a
        /// handler.</summary>
        public bool RequestStop()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return RequestStopCore();
        }

        private bool RequestStopCore()
        {
            if (ReferenceEquals(t_current, _binding))
                return Interlocked.Exchange(ref _binding.EndRequested, 1) == 0;

            using var lease = _handle.Lease();
            byte first = 0;
            AudioStatus.Check(NativeMethods.prisc_audio_input_request_stop((PriscAudioInput*)lease.Handle, &first),
                nameof(NativeMethods.prisc_audio_input_request_stop));
            return first != 0;
        }

        /// <summary>Ends delivery, waits out a handler in flight, and closes the device. Idempotent; no
        /// handler runs after it returns (from inside a handler, the teardown completes on the thread
        /// pool once that handler has returned).</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (ReferenceEquals(t_current, _binding))
            {
                Interlocked.Exchange(ref _binding.EndRequested, 1);
                ThreadPool.QueueUserWorkItem(static input => input.Teardown(), this, preferLocal: false);
                return;
            }

            Teardown();
        }

        private void Teardown()
        {
            try
            {
                RequestStopCore();
            }
            catch (Exception)
            {
                // Best effort: the destroy below ends delivery as well.
            }

            _handle.Dispose();
        }

        /// <summary>What the user pointer roots: the two handlers and the in-handler stop request.</summary>
        private sealed class InputBinding
        {
            internal readonly AudioInputDataHandler OnData;
            internal readonly AudioInputStoppedHandler OnStopped;
            internal int EndRequested;

            internal InputBinding(AudioInputDataHandler onData, AudioInputStoppedHandler onStopped)
            {
                OnData = onData;
                OnStopped = onStopped;
            }
        }

        /// <summary>Owns the native input and the <see cref="GCHandle"/> rooting its handlers, and frees
        /// them in that order.</summary>
        private sealed class InputHandle : InteropHandle
        {
            private GCHandle _binding;

            internal InputHandle(PriscAudioInput* input, GCHandle binding)
            {
                _binding = binding;
                Adopt((IntPtr)input);
            }

            protected override int Destroy(IntPtr h)
            {
                int status = NativeMethods.prisc_audio_input_destroy((PriscAudioInput*)h);
                // Destroy waits out a callback in flight before it returns OK, so the handlers can be
                // unrooted. A refused destroy (inside the callback) or a panic inside it leaks
                // instead: a leaked handle beats a callback reading freed memory.
                if (status >= 0 && _binding.IsAllocated)
                    _binding.Free();
                return status;
            }
        }

        /// <summary>The OS audio threads, one frame past the native trampolines.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static byte Receive(void* user, PriscAudioInputEvent* e)
        {
            // Nothing may escape an UnmanagedCallersOnly method: an exception here is a fail-fast.
            try
            {
                var binding = (InputBinding)GCHandle.FromIntPtr((IntPtr)user).Target!;
                var outer = t_current;
                t_current = binding;
                try
                {
                    if (e->kind == (uint)AudioInputEventKind.Data)
                    {
                        bool keep = binding.OnData(new ReadOnlySpan<byte>(e->data, (int)e->data_len));
                        return keep && Volatile.Read(ref binding.EndRequested) == 0 ? (byte)1 : (byte)0;
                    }

                    binding.OnStopped((AudioInputStop)e->stop, e->os_status);
                    return 0;
                }
                finally
                {
                    t_current = outer;
                }
            }
            catch
            {
                // A handler that threw ends delivery, like one that returned false.
                return 0;
            }
        }
    }
}
