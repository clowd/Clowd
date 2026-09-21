using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Clowd.UI.Services
{
    /// <summary>
    /// A single microphone held open for the video editor's voice-over recorder: it meters the
    /// input continuously so the user can see the mic works before a take, and writes a take to a
    /// WAV file on demand.
    /// </summary>
    /// <remarks>
    /// <para>Windows only for now. Capture is WASAPI shared mode through NAudio
    /// (<see cref="WasapiCapture"/>), which hands us the device mix format; everything written to
    /// disk is normalized to 48 kHz mono 16-bit PCM so the editor always imports the same shape of
    /// file and FFmpeg can probe it without surprises. On macOS <see cref="IsSupported"/> is false
    /// and <see cref="Open"/> is a no-op that reports no level: the CoreAudio shim behind
    /// <see cref="AudioDeviceManager"/> only enumerates devices, it does not open them.</para>
    /// <para>Threading: <see cref="Open"/>, <see cref="StartRecording"/>,
    /// <see cref="StopRecording"/> and <see cref="Close"/> are meant to be called from the UI
    /// thread, and every field they share with the WASAPI capture thread is behind one lock,
    /// which is only ever held for bookkeeping and sample conversion. The disk never sits under
    /// it: the take's file lives in a <see cref="TakeWriter"/> with a gate of its own, so a
    /// slow write on the capture thread cannot stall a UI thread that is merely reading
    /// <see cref="DeviceName"/> or <see cref="IsDeviceOpen"/> (those read volatile fields and
    /// take no lock at all). <see cref="PeakDbfsChanged"/> is always raised on the Avalonia UI
    /// thread. Nothing here throws because of the device: an endpoint that will not open, or
    /// one unplugged mid-take, reports a null level and closes itself.</para>
    /// </remarks>
    public sealed class MicRecorder : IDisposable
    {
        /// <summary>Sample rate every take is written at, whatever the device runs at.</summary>
        public const int RecordSampleRate = 48000;

        /// <summary>What <see cref="DeviceName"/> reads when no device is open.</summary>
        public const string NoDeviceName = "No microphone";

        /// <summary>dBFS reported for a window that held nothing but digital silence. Well under
        /// the -60 dB floor <see cref="Clowd.UI.Controls.Tray.TrayLevel.FromPeakDbfs"/> maps from,
        /// so it draws as an empty meter rather than as a missing one.</summary>
        private const double SilenceDbfs = -100.0;

        /// <summary>Length of one metering window. The peak of each window is one
        /// <see cref="PeakDbfsChanged"/> event, which is the same 10 Hz feed the tray level pills
        /// are drawn from.</summary>
        private const double LevelWindowSeconds = 0.1;

        /// <summary>WASAPI buffer length. Half a metering window, so a window is never much late
        /// and the meter stays responsive.</summary>
        private const int CaptureBufferMs = 50;

        private readonly object _sync = new object();

        // volatile: the capture thread sets these under _sync, the UI thread reads them
        // without it (see the class remarks)
        private volatile WasapiCapture _capture;
        private volatile bool _isRecording;
        private volatile string _deviceName = NoDeviceName;

        private MMDevice _device;
        private TakeWriter _writer;
        private WdlResampler _resampler;

        // Shape of the device mix format, resolved once when the device opens.
        private SourceSampleFormat _sourceFormat;
        private int _sourceChannels;
        private int _sourceSampleRate;
        private int _sourceBytesPerSample;

        private float[] _monoBuffer = Array.Empty<float>();
        private float[] _resampleBuffer = Array.Empty<float>();
        private short[] _writeBuffer = Array.Empty<short>();

        private float _windowPeak;
        private int _windowFrames;
        private int _windowFrameTarget = (int)(RecordSampleRate * LevelWindowSeconds);

        private bool _disposed;

        /// <summary>
        /// Whether this machine can capture a microphone at all. False on macOS, where the
        /// CoreAudio shim enumerates devices but does not open them, so the recorder tool should
        /// stay unavailable rather than offer a record button that cannot work.
        /// </summary>
        public static bool IsSupported => OperatingSystem.IsWindows();

        /// <summary>The input devices to choose between, the "default" pseudo-device first.</summary>
        public static IReadOnlyList<AudioDeviceInfo> Devices() => AudioDeviceManager.GetMicrophones();

        /// <summary>
        /// Friendly name of the device currently open (the endpoint's own name, never "Default"),
        /// or <see cref="NoDeviceName"/> when nothing is open, the platform cannot capture, or the
        /// device went away.
        /// </summary>
        public string DeviceName => _deviceName;

        /// <summary>Whether a take is being written right now.</summary>
        public bool IsRecording => _isRecording;

        /// <summary>
        /// Whether a device is open and feeding the meter, which is the same question as whether
        /// <see cref="StartRecording"/> would succeed. Goes false on its own if the device is
        /// unplugged, at the same moment <see cref="PeakDbfsChanged"/> reports null.
        /// </summary>
        public bool IsDeviceOpen => _capture != null;

        /// <summary>
        /// Peak level of the last metering window (roughly 100 ms) in dBFS, or null when there is
        /// no device: nothing opened, the platform cannot capture, or the open device failed or
        /// was unplugged. Always raised on the Avalonia UI thread, so a handler may touch controls
        /// directly. Feed it through
        /// <see cref="Clowd.UI.Controls.Tray.TrayLevel.FromPeakDbfs"/> to get a 0..1 meter fill.
        /// </summary>
        public event Action<double?> PeakDbfsChanged;

        /// <summary>
        /// Opens a microphone and starts capturing immediately so the meter runs; captured audio
        /// is discarded until <see cref="StartRecording"/>. Closes whatever was open first, so this
        /// doubles as "switch device". Null, empty or
        /// <see cref="AudioDeviceManager.DefaultDeviceId"/> opens the system default input.
        /// </summary>
        /// <param name="deviceId">A WASAPI endpoint id from <see cref="Devices"/>, or null/"default".</param>
        /// <remarks>Never throws: an endpoint that cannot be opened leaves the recorder closed and
        /// reports a null level.</remarks>
        public void Open(string deviceId)
        {
            Close();

            WasapiCapture orphanedCapture = null;
            MMDevice orphanedDevice = null;
            var failed = false;

            lock (_sync)
            {
                if (_disposed || !IsSupported)
                {
                    RaiseLevel(null);
                    return;
                }

                try
                {
                    _device = ResolveDevice(deviceId);
                    _capture = new WasapiCapture(_device, false, CaptureBufferMs);

                    if (!TryResolveSourceFormat(_capture.WaveFormat))
                        throw new NotSupportedException("Unsupported microphone format " + _capture.WaveFormat);

                    _resampler = _sourceSampleRate == RecordSampleRate ? null : CreateResampler(_sourceSampleRate);
                    _windowFrameTarget = Math.Max(1, (int)(_sourceSampleRate * LevelWindowSeconds));
                    _windowPeak = 0;
                    _windowFrames = 0;

                    _capture.DataAvailable += OnDataAvailable;
                    _capture.RecordingStopped += OnRecordingStopped;
                    _capture.StartRecording();

                    _deviceName = _device.FriendlyName;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to open microphone '" + deviceId + "': " + ex.Message);
                    SentryConfig.CaptureHandled(ex, "voiceover.open-mic");
                    (orphanedCapture, orphanedDevice) = DetachDevice();
                    failed = true;
                }
            }

            if (failed)
            {
                // Outside the lock: releasing a capture client joins its capture thread, which may
                // itself be waiting on the lock inside a callback.
                ReleaseDevice(orphanedCapture, orphanedDevice);
                RaiseLevel(null);
            }
        }

        /// <summary>
        /// Starts writing captured audio to <paramref name="wavPath"/> (48 kHz mono 16-bit PCM).
        /// The meter keeps running exactly as before; recording only decides whether the samples
        /// are also kept.
        /// </summary>
        /// <param name="wavPath">File to create. Its directory is created if missing, and an
        /// existing file is overwritten.</param>
        /// <exception cref="InvalidOperationException">No device is open (test
        /// <see cref="IsDeviceOpen"/> first), or a take is already being recorded.</exception>
        public void StartRecording(string wavPath)
        {
            if (String.IsNullOrEmpty(wavPath))
                throw new ArgumentNullException(nameof(wavPath));

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ThrowIfCannotRecord();
            }

            // The file is created outside the lock (a slow directory is not the capture
            // thread's problem) and attached under it, re-checking that the device did not go
            // away in between.
            var writer = new TakeWriter(wavPath);
            var attached = false;
            try
            {
                lock (_sync)
                {
                    ThrowIfCannotRecord();
                    _resampler?.Reset();
                    _writer = writer;
                    _isRecording = true;
                    attached = true;
                }
            }
            finally
            {
                if (!attached)
                    writer.Close();
            }
        }

        /// <summary>The <see cref="StartRecording"/> preconditions; the caller holds the lock.</summary>
        private void ThrowIfCannotRecord()
        {
            if (_capture == null)
                throw new InvalidOperationException("No microphone is open.");

            if (_isRecording)
                throw new InvalidOperationException("A take is already being recorded.");
        }

        /// <summary>
        /// Finishes the take: flushes and closes the WAV synchronously, so the file is complete and
        /// probeable by the time this returns. The device stays open and the meter keeps running.
        /// </summary>
        /// <returns>The recorded length, measured from the samples actually written rather than
        /// from the wall clock, or <see cref="TimeSpan.Zero"/> if no take was running. A device
        /// lost mid-take still reports what reached the file.</returns>
        public TimeSpan StopRecording()
        {
            var writer = DetachWriter();
            if (writer == null)
                return TimeSpan.Zero;

            writer.Close();
            return TimeSpan.FromSeconds(writer.FramesWritten / (double)RecordSampleRate);
        }

        /// <summary>
        /// Stops capturing and releases the device, finishing any take in progress first. Safe to
        /// call when nothing is open.
        /// </summary>
        public void Close()
        {
            WasapiCapture capture;
            MMDevice device;
            TakeWriter writer;

            lock (_sync)
            {
                writer = DetachWriterLocked();
                (capture, device) = DetachDevice();
            }

            writer?.Close();
            if (capture == null && device == null)
                return;

            ReleaseDevice(capture, device);
            RaiseLevel(null);
        }

        /// <summary>Same as <see cref="Close"/>, and refuses any later <see cref="Open"/>.</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            Close();
        }

        /// <summary>Opens the endpoint for an id from <see cref="Devices"/>. An id that no longer
        /// exists falls back to the default input, the same forgiving rule as
        /// <see cref="AudioDeviceManager.VerifyMicrophoneOrDefault"/>.</summary>
        private static MMDevice ResolveDevice(string deviceId)
        {
            using var enumerator = new MMDeviceEnumerator();

            if (!String.IsNullOrEmpty(deviceId) && deviceId != AudioDeviceManager.DefaultDeviceId)
            {
                try
                {
                    return enumerator.GetDevice(deviceId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Microphone '" + deviceId + "' is gone, using the default: " + ex.Message);
                }
            }

            // Role.Multimedia to match the endpoint AudioDeviceManager names its "Default - ..."
            // row after, so the picker's label and the device we actually open agree.
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }

        /// <summary>The WDL resampler configured the way NAudio's own sample provider configures
        /// it, in input-driven feed mode: we push whatever the capture thread handed us instead of
        /// pulling a fixed number of output frames.</summary>
        private static WdlResampler CreateResampler(int sourceSampleRate)
        {
            var resampler = new WdlResampler();
            resampler.SetMode(true, 2, false);
            resampler.SetFilterParms();
            resampler.SetFeedMode(true);
            resampler.SetRates(sourceSampleRate, RecordSampleRate);
            return resampler;
        }

        /// <summary>Reads the device mix format into the few numbers the conversion loop needs.
        /// Returns false for a format we cannot decode, which is treated as a failure to
        /// open.</summary>
        private bool TryResolveSourceFormat(WaveFormat format)
        {
            // Shared-mode mix formats are usually WAVE_FORMAT_EXTENSIBLE, whose Encoding reads as
            // "Extensible"; the encoding that matters is in the subformat GUID.
            if (format is WaveFormatExtensible extensible)
            {
                try
                {
                    format = extensible.ToStandardWaveFormat();
                }
                catch (InvalidOperationException ex)
                {
                    Debug.WriteLine("Unrecognized microphone subformat: " + ex.Message);
                    return false;
                }
            }

            _sourceChannels = format.Channels;
            _sourceSampleRate = format.SampleRate;
            _sourceBytesPerSample = format.BitsPerSample / 8;

            if (_sourceChannels < 1 || _sourceSampleRate < 1)
                return false;

            _sourceFormat = (format.Encoding, format.BitsPerSample) switch
            {
                (WaveFormatEncoding.IeeeFloat, 32) => SourceSampleFormat.Float32,
                (WaveFormatEncoding.Pcm, 16) => SourceSampleFormat.Pcm16,
                (WaveFormatEncoding.Pcm, 24) => SourceSampleFormat.Pcm24,
                (WaveFormatEncoding.Pcm, 32) => SourceSampleFormat.Pcm32,
                _ => SourceSampleFormat.Unsupported,
            };

            return _sourceFormat != SourceSampleFormat.Unsupported;
        }

        /// <summary>WASAPI capture thread. The conversion runs under the lock so a concurrent
        /// <see cref="StartRecording"/> or <see cref="Close"/> cannot swap the writer or the
        /// buffers out from under it; the disk write itself happens after the lock is released
        /// (the writer has its own gate, and a stop that races it simply closes the file after
        /// or before this buffer). Nothing is allowed to escape as an exception on this
        /// thread.</summary>
        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            double? level = null;
            var haveLevel = false;
            TakeWriter writer = null;
            var writeCount = 0;

            try
            {
                lock (_sync)
                {
                    // Not our device any more: the recorder was closed or switched while this
                    // buffer was in flight.
                    if (_capture == null || !ReferenceEquals(sender, _capture))
                        return;

                    var frameBytes = _sourceBytesPerSample * _sourceChannels;
                    var frames = frameBytes == 0 ? 0 : e.BytesRecorded / frameBytes;
                    if (frames <= 0)
                        return;

                    if (_monoBuffer.Length < frames)
                        _monoBuffer = new float[frames];

                    var bufferPeak = DownmixToMono(e.Buffer, frames);

                    if (_isRecording && _writer != null)
                    {
                        writer = _writer;
                        writeCount = ConvertFrames(frames);
                    }

                    if (bufferPeak > _windowPeak)
                        _windowPeak = bufferPeak;

                    _windowFrames += frames;
                    if (_windowFrames >= _windowFrameTarget)
                    {
                        level = _windowPeak <= 0f ? SilenceDbfs : Math.Max(SilenceDbfs, 20.0 * Math.Log10(_windowPeak));
                        haveLevel = true;
                        _windowPeak = 0;
                        _windowFrames = 0;
                    }
                }

                // _writeBuffer is only ever filled by this thread, one buffer at a time, so it
                // is safe to hand to the writer after the lock is gone.
                if (writeCount > 0)
                    writer.Write(_writeBuffer, writeCount);
            }
            catch (Exception ex)
            {
                // A conversion or a disk write blowing up on the capture thread would otherwise
                // take the process down. Abandon the take, keep the app alive, and ask the capture
                // client to stop so OnRecordingStopped does the tidying.
                Debug.WriteLine("Microphone capture callback failed: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "voiceover.capture-callback");
                RequestStopAfterFailure(sender as WasapiCapture);
                return;
            }

            if (haveLevel)
                RaiseLevel(level);
        }

        /// <summary>
        /// Converts one captured buffer into mono floats in <see cref="_monoBuffer"/> (channels
        /// averaged) and returns the buffer's peak magnitude, which is the largest absolute sample
        /// across all channels rather than of the downmix, so one loud channel is not diluted by
        /// the quiet ones.
        /// </summary>
        private float DownmixToMono(byte[] buffer, int frames)
        {
            var channels = _sourceChannels;
            var bytesPerSample = _sourceBytesPerSample;
            var peak = 0f;
            var offset = 0;

            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;

                for (var channel = 0; channel < channels; channel++, offset += bytesPerSample)
                {
                    var sample = ReadSample(buffer, offset);
                    sum += sample;

                    var magnitude = Math.Abs(sample);
                    if (magnitude > peak)
                        peak = magnitude;
                }

                _monoBuffer[frame] = sum / channels;
            }

            return peak;
        }

        /// <summary>One sample of the device mix format as a float in -1..1 (give or take: float
        /// mix formats are allowed to exceed it).</summary>
        private float ReadSample(byte[] buffer, int offset)
        {
            switch (_sourceFormat)
            {
                case SourceSampleFormat.Float32:
                    return BitConverter.ToSingle(buffer, offset);
                case SourceSampleFormat.Pcm16:
                    return BitConverter.ToInt16(buffer, offset) / 32768f;
                case SourceSampleFormat.Pcm24:
                    var packed = buffer[offset] | (buffer[offset + 1] << 8) | ((sbyte)buffer[offset + 2] << 16);
                    return packed / 8388608f;
                case SourceSampleFormat.Pcm32:
                    return BitConverter.ToInt32(buffer, offset) / 2147483648f;
                default:
                    return 0f;
            }
        }

        /// <summary>Resamples <see cref="_monoBuffer"/> to <see cref="RecordSampleRate"/> when the
        /// device runs at some other rate and converts it to 16-bit PCM in
        /// <see cref="_writeBuffer"/>. Returns the sample count ready to write (0 when the
        /// resampler is still filling). The caller holds the lock; the write happens without it.</summary>
        private int ConvertFrames(int frames)
        {
            float[] source;
            int count;

            if (_resampler == null)
            {
                source = _monoBuffer;
                count = frames;
            }
            else
            {
                var wanted = _resampler.ResamplePrepare(frames, 1, out var inBuffer, out var inOffset);
                var inFrames = Math.Min(frames, wanted);
                Array.Copy(_monoBuffer, 0, inBuffer, inOffset, inFrames);

                // The ratio plus a little slack: the resampler carries a fractional position
                // between calls, so a given input block can yield a frame more than the ratio
                // alone suggests.
                var capacity = (int)(inFrames * ((double)RecordSampleRate / _sourceSampleRate)) + 16;
                if (_resampleBuffer.Length < capacity)
                    _resampleBuffer = new float[capacity];

                source = _resampleBuffer;
                count = _resampler.ResampleOut(_resampleBuffer, 0, inFrames, capacity, 1);
            }

            if (count <= 0)
                return 0;

            if (_writeBuffer.Length < count)
                _writeBuffer = new short[count];

            for (var i = 0; i < count; i++)
            {
                // Clamped rather than wrapped: a float mix format can hand us samples past full
                // scale, and a wrapped sample is a loud click in the take.
                var sample = Math.Clamp(source[i], -1f, 1f);
                _writeBuffer[i] = (short)(sample * 32767f);
            }

            return count;
        }

        /// <summary>
        /// The capture client stopped on its own, which we only ever see when something went
        /// wrong: the device was unplugged or disabled, the audio service restarted, or a callback
        /// of ours failed. Our own <see cref="Close"/> detaches the client first, so a stop it
        /// caused arrives here for a client that is no longer the current one and is ignored.
        /// </summary>
        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            WasapiCapture capture;
            MMDevice device;

            lock (_sync)
            {
                if (!ReferenceEquals(sender, _capture))
                    return;

                if (e.Exception != null)
                {
                    Debug.WriteLine("Microphone capture stopped: " + e.Exception.Message);
                    SentryConfig.CaptureHandled(e.Exception, "voiceover.capture-stopped");
                }

                // The take keeps whatever reached the file, so StopRecording still reports a
                // sensible length for a device lost mid-take: recording stops here, the file
                // stays attached for StopRecording to close and measure.
                _isRecording = false;
                (capture, device) = DetachDevice();
            }

            // On the thread pool because this event can be raised from the capture thread itself,
            // and releasing the client waits for that thread to end.
            Task.Run(() => ReleaseDevice(capture, device));
            RaiseLevel(null);
        }

        /// <summary>Asks a capture client to stop after one of our callbacks failed on it. Setting
        /// the stop flag is all that is safe from the capture thread; the release happens in
        /// <see cref="OnRecordingStopped"/> once that thread has ended.</summary>
        private void RequestStopAfterFailure(WasapiCapture capture)
        {
            if (capture == null)
                return;

            DetachWriter()?.Close();

            try
            {
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to stop the microphone after a capture failure: " + ex.Message);
            }
        }

        /// <summary>Takes the current device out of the fields and returns it for release. The
        /// caller holds the lock; the release itself must happen without it.</summary>
        private (WasapiCapture Capture, MMDevice Device) DetachDevice()
        {
            var capture = _capture;
            var device = _device;

            _capture = null;
            _device = null;
            _resampler = null;
            _deviceName = NoDeviceName;

            return (capture, device);
        }

        /// <summary>Unsubscribes, stops and disposes a detached capture client and its endpoint.
        /// Must be called without the lock: disposing the client waits for its capture thread,
        /// which may be blocked on the lock inside <see cref="OnDataAvailable"/>.</summary>
        private void ReleaseDevice(WasapiCapture capture, MMDevice device)
        {
            if (capture != null)
            {
                try
                {
                    capture.DataAvailable -= OnDataAvailable;
                    capture.RecordingStopped -= OnRecordingStopped;
                    capture.StopRecording();
                    capture.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to close the microphone: " + ex.Message);
                    SentryConfig.CaptureHandled(ex, "voiceover.close-mic");
                }
            }

            if (device != null)
            {
                try
                {
                    device.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to release the microphone endpoint: " + ex.Message);
                }
            }
        }

        /// <summary>Ends the take's recording and takes its file out of the fields, for the
        /// caller to close without the lock; null when no take was running.</summary>
        private TakeWriter DetachWriter()
        {
            lock (_sync)
                return DetachWriterLocked();
        }

        /// <summary>The body of <see cref="DetachWriter"/>; the caller holds the lock.</summary>
        private TakeWriter DetachWriterLocked()
        {
            _isRecording = false;
            var writer = _writer;
            _writer = null;
            return writer;
        }

        /// <summary>
        /// One take's WAV file, with the gate the capture thread and the stopping thread share.
        /// <see cref="Write"/> and <see cref="Close"/> serialize on it, so a stop that lands in
        /// the middle of a write waits for that one write and no more, and a write that lands
        /// after the close is dropped rather than thrown at a disposed stream. Neither of the
        /// recorder's other threads holds the recorder's lock while in here.
        /// </summary>
        private sealed class TakeWriter
        {
            private readonly object _gate = new object();
            private WaveFileWriter _wav;
            private long _framesWritten;

            public TakeWriter(string wavPath)
            {
                var directory = Path.GetDirectoryName(wavPath);
                if (!String.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                _wav = new WaveFileWriter(wavPath, new WaveFormat(RecordSampleRate, 16, 1));
            }

            /// <summary>Samples that reached the file. Read after <see cref="Close"/>, which
            /// is the last write.</summary>
            public long FramesWritten
            {
                get
                {
                    lock (_gate)
                        return _framesWritten;
                }
            }

            public void Write(short[] samples, int count)
            {
                lock (_gate)
                {
                    if (_wav == null)
                        return;

                    _wav.WriteSamples(samples, 0, count);
                    _framesWritten += count;
                }
            }

            /// <summary>Flushes and closes the file; a second call is a no-op.</summary>
            public void Close()
            {
                lock (_gate)
                {
                    if (_wav == null)
                        return;

                    try
                    {
                        _wav.Flush();
                        _wav.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Failed to finalize the voice take: " + ex.Message);
                        SentryConfig.CaptureHandled(ex, "voiceover.finalize-wav");
                    }
                    finally
                    {
                        _wav = null;
                    }
                }
            }
        }

        /// <summary>Hands one level (or the absence of one) to the UI thread.</summary>
        private void RaiseLevel(double? dbfs)
        {
            var handler = PeakDbfsChanged;
            if (handler == null)
                return;

            try
            {
                Dispatcher.UIThread.Post(() => handler(dbfs));
            }
            catch (Exception ex)
            {
                // Posting is the last thing a capture callback does, and a shutting-down
                // dispatcher refusing the job is not worth dropping the device over.
                Debug.WriteLine("Failed to post a microphone level: " + ex.Message);
            }
        }

        /// <summary>How one sample of the device mix format is laid out in the capture buffer.</summary>
        private enum SourceSampleFormat
        {
            Unsupported,
            Float32,
            Pcm16,
            Pcm24,
            Pcm32,
        }
    }
}
