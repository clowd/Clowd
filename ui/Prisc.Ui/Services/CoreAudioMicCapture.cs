using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Prisc.Interop;

namespace Prisc.UI.Services
{
    /// <summary>
    /// The macOS <see cref="MicCapture"/>: an AudioToolbox input queue pointed at one CoreAudio
    /// device, opened through <see cref="CoreAudioInput"/> (prisc_interop). The queue is asked for
    /// interleaved float32 at <see cref="MicRecorder.RecordSampleRate"/> straight away and does the
    /// rate conversion from whatever the device runs at itself, so the recorder's own resampler is
    /// idle on this platform. The channel count is the device's, so the recorder downmixes here
    /// exactly as it does with a WASAPI mix format.
    /// </summary>
    /// <remarks>
    /// <para>Device selection: a picker id is a CoreAudio device UID (what
    /// <see cref="AudioDeviceManager"/> lists). The library resolves it, falling back to the system
    /// default when it is gone, and pins the queue to that concrete device, so, as on Windows, a take
    /// stays on the microphone it started on even if the system default changes underneath it.</para>
    /// <para>Threads: buffers arrive on the queue's own thread; an unplug arrives as a stop from
    /// CoreAudio's notification thread (the library watches the device's is-alive flag). Both are
    /// delivered one at a time, and <see cref="Dispose"/> waits out the one in flight.</para>
    /// <para>Permission: a denial does not fail the queue, it just delivers silence forever, so the
    /// library reads the stored microphone verdict at open and a refused microphone is an open failure
    /// with a reason in the log, not a meter that never moves.</para>
    /// </remarks>
    [SupportedOSPlatform("macos")]
    internal sealed class CoreAudioMicCapture : MicCapture
    {
        /// <summary>Taken by the stop signal and by <see cref="Dispose"/>, so <see cref="MicCapture.Stopped"/>
        /// is raised at most once and never after a dispose began.</summary>
        private readonly object _gate = new object();

        private readonly CoreAudioInput _input;
        private byte[] _managed = Array.Empty<byte>();
        private bool _started;
        private bool _stopped;      // Stopped has been signalled, or a dispose has begun
        private bool _disposed;

        private CoreAudioMicCapture(string deviceId, int bufferMs)
        {
            // "default" is the picker's pseudo-device; the library takes an empty UID for it.
            var uid = deviceId == AudioDeviceManager.DefaultDeviceId ? null : deviceId;
            _input = CoreAudioInput.Open(uid, MicRecorder.RecordSampleRate, bufferMs, OnData, OnStopped);
            if (_input.UsedDefault && !String.IsNullOrEmpty(uid))
                Debug.WriteLine("Microphone '" + deviceId + "' is gone, using the default.");
        }

        public override string FriendlyName => _input.Name;
        public override int Channels => _input.Channels;
        public override int SampleRate => _input.SampleRateHz;
        public override MicSampleFormat SampleFormat => MicSampleFormat.Float32;

        /// <summary>See <see cref="MicCapture.Open"/>. A refused microphone is an
        /// <see cref="UnauthorizedAccessException"/>, no microphone or a CoreAudio failure an
        /// <see cref="InvalidOperationException"/>; nothing is left open when this throws.</summary>
        public static CoreAudioMicCapture Create(string deviceId, int bufferMs) => new CoreAudioMicCapture(deviceId, bufferMs);

        public override void Start()
        {
            bool watching;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_started)
                    return;

                watching = _input.Start();
                _started = true;
            }

            // Best effort: without the watch an unplug freezes the meter at its last value instead of
            // clearing it.
            if (!watching)
                Debug.WriteLine("Could not watch the microphone '" + FriendlyName + "' for removal.");
        }

        public override void RequestStop()
        {
            SignalStopped(null);
            try
            {
                _input.RequestStop();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or InteropException)
            {
                Debug.WriteLine("Failed to stop the microphone: " + ex.Message);
            }
        }

        /// <summary>Raises <see cref="MicCapture.Stopped"/> once, off the calling thread (which may be the
        /// queue's or the HAL's). Later calls are no-ops.</summary>
        private void SignalStopped(Exception error)
        {
            lock (_gate)
            {
                if (_stopped || _disposed)
                    return;

                _stopped = true;
            }

            Task.Run(() => RaiseStopped(error));
        }

        public override void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _stopped = true;
            }

            try
            {
                // Ends delivery, waits out a buffer or stop in flight, then stops and frees the queue.
                _input.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to close the microphone: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "voiceover.close-mic");
            }
        }

        // --------------------------------------------------------------------------- callbacks

        /// <summary>The queue's thread: one filled buffer. Returning false ends delivery.</summary>
        private bool OnData(ReadOnlySpan<byte> data)
        {
            try
            {
                lock (_gate)
                {
                    if (_stopped || _disposed)
                        return false;
                }

                // Copied out rather than handed over as a span: the handler is the recorder's
                // conversion loop, which reads a byte[] like the WASAPI path gives it.
                if (_managed.Length < data.Length)
                    _managed = new byte[data.Length];
                data.CopyTo(_managed);

                try
                {
                    RaiseDataAvailable(_managed, data.Length);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Microphone capture handler failed: " + ex.Message);
                    SignalStopped(ex);
                    return false;
                }

                // A handler that asked for a stop makes this the last buffer.
                lock (_gate)
                    return !_stopped;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Microphone queue callback failed: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "voiceover.coreaudio-callback");
                return false;
            }
        }

        /// <summary>The device went away, or the queue stopped taking buffers back.</summary>
        private void OnStopped(AudioInputStop reason, int osStatus)
        {
            if (reason == AudioInputStop.RequeueFailed)
            {
                Debug.WriteLine("CoreAudio error " + osStatus + " re-queueing a capture buffer.");
                SignalStopped(new IOException("The microphone stopped accepting buffers (CoreAudio error " + osStatus + ")."));
                return;
            }

            SignalStopped(new IOException("The microphone '" + FriendlyName + "' was disconnected."));
        }
    }
}
