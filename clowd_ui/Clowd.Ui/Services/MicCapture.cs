using System;

namespace Clowd.UI.Services
{
    /// <summary>How one sample of a capture buffer is laid out.</summary>
    internal enum MicSampleFormat
    {
        Float32,
        Pcm16,
        Pcm24,
        Pcm32,
    }

    /// <summary>
    /// One open microphone: the platform half of <see cref="MicRecorder"/>. An instance is bound
    /// to a device and a fixed interleaved PCM format from the moment <see cref="Open"/> returns,
    /// and hands raw buffers in that format to <see cref="DataAvailable"/> from a capture thread
    /// of its own once <see cref="Start"/> is called. Everything above this (downmix, resampling,
    /// metering, the WAV file) is shared and lives in the recorder.
    /// </summary>
    /// <remarks>
    /// <para>Backends: <see cref="WasapiMicCapture"/> (shared-mode WASAPI through NAudio, the
    /// device mix format) on Windows and <see cref="CoreAudioMicCapture"/> (an AudioQueue, which
    /// resamples to <see cref="MicRecorder.RecordSampleRate"/> itself) on macOS. Elsewhere
    /// <see cref="IsSupported"/> is false and <see cref="Open"/> throws.</para>
    /// <para>Contract: <see cref="DataAvailable"/> is raised on the backend's capture thread with
    /// a buffer the handler may read until it returns. <see cref="Stopped"/> is raised at most
    /// once, never on the caller's thread of <see cref="RequestStop"/>, when capture ends for
    /// any reason other than <see cref="Dispose"/>: the device went away, the backend failed, or
    /// a caller asked it to stop after a failure of its own. <see cref="Dispose"/> waits for a
    /// callback in flight to finish and must therefore never be called under a lock that the
    /// handlers take.</para>
    /// </remarks>
    internal abstract class MicCapture : IDisposable
    {
        /// <summary>Whether this platform has a capture backend at all.</summary>
        public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        /// <summary>
        /// Opens a device for an id from <see cref="AudioDeviceManager.GetMicrophones"/>. Null,
        /// empty or <see cref="AudioDeviceManager.DefaultDeviceId"/> is the system default
        /// input, and an id that no longer exists falls back to it, the same forgiving rule as
        /// <see cref="AudioDeviceManager.VerifyMicrophoneOrDefault"/>. Capture does not start
        /// until <see cref="Start"/>.
        /// </summary>
        /// <param name="deviceId">The device to open.</param>
        /// <param name="bufferMs">How much audio each <see cref="DataAvailable"/> buffer should
        /// hold, roughly; a backend may round it to what the device does.</param>
        /// <exception cref="Exception">The device could not be opened or its format cannot be
        /// decoded. Nothing is left open when this throws.</exception>
        public static MicCapture Open(string deviceId, int bufferMs)
        {
            if (OperatingSystem.IsWindows())
                return WasapiMicCapture.Create(deviceId, bufferMs);

            if (OperatingSystem.IsMacOS())
                return CoreAudioMicCapture.Create(deviceId, bufferMs);

            throw new PlatformNotSupportedException("Microphone capture is not supported on this platform.");
        }

        /// <summary>The device's own name, never "Default".</summary>
        public abstract string FriendlyName { get; }

        /// <summary>Interleaved channel count of every <see cref="DataAvailable"/> buffer.</summary>
        public abstract int Channels { get; }

        /// <summary>Sample rate of every <see cref="DataAvailable"/> buffer.</summary>
        public abstract int SampleRate { get; }

        /// <summary>Sample layout of every <see cref="DataAvailable"/> buffer.</summary>
        public abstract MicSampleFormat SampleFormat { get; }

        /// <summary>Bytes one sample of <see cref="SampleFormat"/> occupies.</summary>
        public int BytesPerSample => SampleFormat switch
        {
            MicSampleFormat.Pcm16 => 2,
            MicSampleFormat.Pcm24 => 3,
            _ => 4,
        };

        /// <summary>A captured buffer: this capture, the bytes, and how many of them are
        /// valid. Raised on the capture thread.</summary>
        public event Action<MicCapture, byte[], int> DataAvailable;

        /// <summary>Capture ended on its own (see the class remarks). The exception is the
        /// backend's, or null when a caller asked for the stop.</summary>
        public event Action<MicCapture, Exception> Stopped;

        /// <summary>Starts delivering buffers. Throws if the device refuses to start.</summary>
        public abstract void Start();

        /// <summary>
        /// Asks capture to end because a <see cref="DataAvailable"/> handler failed. Safe to call
        /// from the capture thread: it only flags the backend, and <see cref="Stopped"/> follows
        /// on another thread once delivery has ended.
        /// </summary>
        public abstract void RequestStop();

        /// <summary>Stops and releases the device. Idempotent; no event is raised after it
        /// returns.</summary>
        public abstract void Dispose();

        protected void RaiseDataAvailable(byte[] buffer, int bytes) => DataAvailable?.Invoke(this, buffer, bytes);

        protected void RaiseStopped(Exception error) => Stopped?.Invoke(this, error);
    }
}
