using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.UI.Services
{
    /// <summary>
    /// The macOS <see cref="MicCapture"/>: an AudioToolbox input queue
    /// (<c>AudioQueueNewInput</c>) pointed at one CoreAudio device. The queue is asked for
    /// interleaved float32 at <see cref="MicRecorder.RecordSampleRate"/> straight away and does
    /// the rate conversion from whatever the device runs at itself, so the recorder's own
    /// resampler is idle on this platform. The channel count is the device's, so the recorder
    /// downmixes here exactly as it does with a WASAPI mix format.
    /// </summary>
    /// <remarks>
    /// <para>Device selection: a picker id is a CoreAudio device UID (what
    /// <see cref="CoreAudioInterop"/> enumerates), translated back to a device so we can read
    /// its name and channel layout, then handed to the queue as
    /// <c>kAudioQueueProperty_CurrentDevice</c>. The default is resolved to a concrete device at
    /// open time and pinned the same way, so, as on Windows, a take stays on the microphone it
    /// started on even if the system default changes underneath it.</para>
    /// <para>Threads: the queue delivers buffers on a thread of its own, with no run loop of
    /// ours involved. Device loss is not something the queue reports; it just stops calling.
    /// So a listener on the device's <c>kAudioDevicePropertyDeviceIsAlive</c> turns an unplug
    /// into <see cref="MicCapture.Stopped"/>, from CoreAudio's notification thread.</para>
    /// <para>Callbacks reach managed code through delegates CoreAudio holds raw pointers to.
    /// Those are static and rooted for the life of the process; each instance is found again
    /// through a token in a registry rather than a GC handle, so a callback that lands after a
    /// dispose looks up nothing instead of dereferencing a freed handle.</para>
    /// <para>Permission: the first start makes macOS show its microphone prompt, and a denial
    /// does not fail anything, the queue just delivers silence forever. <see cref="Open"/> asks
    /// AVFoundation for the stored verdict first so a refused microphone is an open failure
    /// with a reason in the log, not a meter that never moves.</para>
    /// </remarks>
    [SupportedOSPlatform("macos")]
    internal sealed class CoreAudioMicCapture : MicCapture
    {
        // ------------------------------------------------------------------ AudioToolbox interop

        private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
        private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string AvFoundationLib = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
        private const string ObjCLib = "/usr/lib/libobjc.A.dylib";

        private const uint kAudioFormatLinearPCM = 0x6C70636D;          // 'lpcm'
        private const uint kAudioFormatFlagIsFloat = 1 << 0;
        private const uint kAudioFormatFlagIsPacked = 1 << 3;
        private const uint kAudioQueueProperty_CurrentDevice = 0x61716364; // 'aqcd', CFStringRef (device UID)
        private const uint kCFStringEncodingUTF8 = 0x08000100;

        // AVAuthorizationStatus
        private const long AVAuthorizationStatusRestricted = 1;
        private const long AVAuthorizationStatusDenied = 2;

        /// <summary>Buffers in flight. Three is the textbook number: one being filled, one on
        /// its way to us, one spare so a slow callback costs no audio.</summary>
        private const int BufferCount = 3;

        // AudioQueueBuffer, 64-bit layout:
        //   0  UInt32 mAudioDataBytesCapacity
        //   8  void*  mAudioData               (pointer-aligned, hence the gap)
        //   16 UInt32 mAudioDataByteSize
        //   24 void*  mUserData ... (unused here)
        private const int AudioDataOffset = 8;
        private const int AudioDataByteSizeOffset = 16;

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioStreamBasicDescription
        {
            public double mSampleRate;
            public uint mFormatID;
            public uint mFormatFlags;
            public uint mBytesPerPacket;
            public uint mFramesPerPacket;
            public uint mBytesPerFrame;
            public uint mChannelsPerFrame;
            public uint mBitsPerChannel;
            public uint mReserved;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void AudioQueueInputCallback(
            IntPtr inUserData, IntPtr inAQ, IntPtr inBuffer, IntPtr inStartTime,
            uint inNumberPacketDescriptions, IntPtr inPacketDescs);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AudioObjectPropertyListenerProc(
            uint inObjectID, uint inNumberAddresses, IntPtr inAddresses, IntPtr inClientData);

        [DllImport(AudioToolbox)]
        private static extern int AudioQueueNewInput(ref AudioStreamBasicDescription inFormat, IntPtr inCallbackProc,
            IntPtr inUserData, IntPtr inCallbackRunLoop, IntPtr inCallbackRunLoopMode, uint inFlags, out IntPtr outAQ);

        [DllImport(AudioToolbox)]
        private static extern int AudioQueueAllocateBuffer(IntPtr inAQ, uint inBufferByteSize, out IntPtr outBuffer);

        [DllImport(AudioToolbox)]
        private static extern int AudioQueueEnqueueBuffer(IntPtr inAQ, IntPtr inBuffer, uint inNumPacketDescs, IntPtr inPacketDescs);

        [DllImport(AudioToolbox)]
        private static extern int AudioQueueStart(IntPtr inAQ, IntPtr inStartTime);

        [DllImport(AudioToolbox)]
        private static extern int AudioQueueStop(IntPtr inAQ, [MarshalAs(UnmanagedType.I1)] bool inImmediate);

        [DllImport(AudioToolbox)]
        private static extern int AudioQueueDispose(IntPtr inAQ, [MarshalAs(UnmanagedType.I1)] bool inImmediate);

        [DllImport(AudioToolbox)]
        private static extern int AudioQueueSetProperty(IntPtr inAQ, uint inID, ref IntPtr inData, uint inDataSize);

        [DllImport(CoreAudioLib)]
        private static extern int AudioObjectAddPropertyListener(uint inObjectID,
            ref CoreAudioInterop.AudioObjectPropertyAddress inAddress, IntPtr inListener, IntPtr inClientData);

        [DllImport(CoreAudioLib)]
        private static extern int AudioObjectRemovePropertyListener(uint inObjectID,
            ref CoreAudioInterop.AudioObjectPropertyAddress inAddress, IntPtr inListener, IntPtr inClientData);

        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);

        [DllImport(CoreFoundationLib)]
        private static extern void CFRelease(IntPtr cf);

        [DllImport(ObjCLib, EntryPoint = "objc_getClass")]
        private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(ObjCLib, EntryPoint = "sel_registerName")]
        private static extern IntPtr GetSelector([MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern nint SendMessageInteger(IntPtr receiver, IntPtr selector, IntPtr arg1);

        // Rooted for the life of the process: CoreAudio keeps the raw pointers and the GC has no
        // idea. Both callbacks find their instance through s_live.
        private static readonly AudioQueueInputCallback s_inputCallback = InputCallback;
        private static readonly IntPtr s_inputCallbackPtr = Marshal.GetFunctionPointerForDelegate(s_inputCallback);
        private static readonly AudioObjectPropertyListenerProc s_aliveListener = AliveListener;
        private static readonly IntPtr s_aliveListenerPtr = Marshal.GetFunctionPointerForDelegate(s_aliveListener);

        private static readonly ConcurrentDictionary<IntPtr, CoreAudioMicCapture> s_live = new();
        private static long s_nextToken;

        // ------------------------------------------------------------------------------- state

        /// <summary>Held for the whole of a buffer callback, so <see cref="Dispose"/> (which
        /// takes it first) never tears the queue down under one.</summary>
        private readonly object _gate = new object();

        private readonly IntPtr _token;
        private readonly uint _deviceId;
        private readonly string _name;
        private readonly int _channels;
        private readonly int _bufferBytes;

        private IntPtr _queue;
        private IntPtr _deviceUid;   // the CFString the queue was pointed at; released on dispose
        private byte[] _managed = Array.Empty<byte>();
        private bool _started;
        private bool _listening;
        private bool _stopped;      // Stopped has been signalled, or a dispose has begun
        private bool _disposed;

        private CoreAudioMicCapture(uint deviceId, string name, int channels, int bufferMs)
        {
            _token = (IntPtr)Interlocked.Increment(ref s_nextToken);
            _deviceId = deviceId;
            _name = name;
            _channels = channels;
            _bufferBytes = Math.Max(1, (int)(MicRecorder.RecordSampleRate * bufferMs / 1000.0)) * channels * sizeof(float);
            s_live[_token] = this;
        }

        public override string FriendlyName => _name;
        public override int Channels => _channels;
        public override int SampleRate => MicRecorder.RecordSampleRate;
        public override MicSampleFormat SampleFormat => MicSampleFormat.Float32;

        /// <summary>See <see cref="MicCapture.Open"/>.</summary>
        public static CoreAudioMicCapture Create(string deviceId, int bufferMs)
        {
            ThrowIfMicrophoneDenied();

            var device = ResolveDevice(deviceId);
            var uid = CoreAudioInterop.GetDeviceUid(device);
            if (String.IsNullOrEmpty(uid))
                throw new InvalidOperationException("The microphone has no device UID.");

            var name = CoreAudioInterop.GetDeviceName(device) ?? uid;
            var channels = Math.Max(1, CoreAudioInterop.GetInputChannelCount(device));

            var capture = new CoreAudioMicCapture(device, name, channels, bufferMs);
            try
            {
                capture.CreateQueue(uid);
                return capture;
            }
            catch
            {
                capture.Dispose();
                throw;
            }
        }

        /// <summary>The device for a picker id: a UID that no longer resolves, or no id at all,
        /// is the default input.</summary>
        private static uint ResolveDevice(string deviceId)
        {
            if (!String.IsNullOrEmpty(deviceId) && deviceId != AudioDeviceManager.DefaultDeviceId)
            {
                var device = CoreAudioInterop.TranslateUidToDevice(deviceId);
                if (device != 0)
                    return device;

                Debug.WriteLine("Microphone '" + deviceId + "' is gone, using the default.");
            }

            var fallback = CoreAudioInterop.GetDefaultInputDevice();
            if (fallback == 0)
                throw new InvalidOperationException("There is no default microphone.");

            return fallback;
        }

        private void CreateQueue(string uid)
        {
            var bytesPerFrame = (uint)(_channels * sizeof(float));
            var format = new AudioStreamBasicDescription
            {
                mSampleRate = MicRecorder.RecordSampleRate,
                mFormatID = kAudioFormatLinearPCM,
                mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
                mBytesPerPacket = bytesPerFrame,
                mFramesPerPacket = 1,
                mBytesPerFrame = bytesPerFrame,
                mChannelsPerFrame = (uint)_channels,
                mBitsPerChannel = 32,
            };

            // No run loop: the callback comes on the queue's own thread.
            Check(AudioQueueNewInput(ref format, s_inputCallbackPtr, _token, IntPtr.Zero, IntPtr.Zero, 0, out var queue),
                "creating the input queue");
            _queue = queue;

            _deviceUid = CFStringCreateWithCString(IntPtr.Zero, uid, kCFStringEncodingUTF8);
            if (_deviceUid == IntPtr.Zero)
                throw new InvalidOperationException("Could not build the microphone's device UID string.");

            var uidRef = _deviceUid;
            Check(AudioQueueSetProperty(queue, kAudioQueueProperty_CurrentDevice, ref uidRef, (uint)IntPtr.Size),
                "selecting the microphone '" + _name + "'");

            for (var i = 0; i < BufferCount; i++)
            {
                Check(AudioQueueAllocateBuffer(queue, (uint)_bufferBytes, out var buffer), "allocating a capture buffer");
                Check(AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero), "queueing a capture buffer");
            }
        }

        public override void Start()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_started)
                    return;

                Check(AudioQueueStart(_queue, IntPtr.Zero), "starting the microphone '" + _name + "'");
                _started = true;
            }

            ListenForDeviceLoss();
        }

        /// <summary>Subscribes to the device's is-alive flag. Best effort: without it an unplug
        /// freezes the meter at its last value instead of clearing it.</summary>
        private void ListenForDeviceLoss()
        {
            var addr = new CoreAudioInterop.AudioObjectPropertyAddress(
                CoreAudioInterop.kAudioDevicePropertyDeviceIsAlive, CoreAudioInterop.GlobalScope);
            var status = AudioObjectAddPropertyListener(_deviceId, ref addr, s_aliveListenerPtr, _token);
            if (status == 0)
                _listening = true;
            else
                Debug.WriteLine("CoreAudio error " + status + " while watching the microphone for removal.");
        }

        public override void RequestStop() => SignalStopped(null);

        /// <summary>Ends delivery and raises <see cref="MicCapture.Stopped"/> once, off the
        /// calling thread (which may be the queue's or the HAL's). Later calls are no-ops.</summary>
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

            // From here no callback does anything but return; the queue is torn down after.
            s_live.TryRemove(_token, out _);

            if (_listening)
            {
                _listening = false;
                var addr = new CoreAudioInterop.AudioObjectPropertyAddress(
                    CoreAudioInterop.kAudioDevicePropertyDeviceIsAlive, CoreAudioInterop.GlobalScope);
                try
                {
                    AudioObjectRemovePropertyListener(_deviceId, ref addr, s_aliveListenerPtr, _token);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to stop watching the microphone: " + ex.Message);
                }
            }

            if (_queue != IntPtr.Zero)
            {
                try
                {
                    // Both immediate: stop synchronously, then free the queue and its buffers.
                    // On a device that has just been unplugged these report errors, which is fine.
                    AudioQueueStop(_queue, true);
                    AudioQueueDispose(_queue, true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to close the microphone: " + ex.Message);
                    SentryConfig.CaptureHandled(ex, "voiceover.close-mic");
                }

                _queue = IntPtr.Zero;
            }

            if (_deviceUid != IntPtr.Zero)
            {
                CFRelease(_deviceUid);
                _deviceUid = IntPtr.Zero;
            }
        }

        // --------------------------------------------------------------------------- callbacks

        /// <summary>The queue's thread. Nothing may escape as an exception: this frame was
        /// entered from native code and an unhandled one takes the process down.</summary>
        private static void InputCallback(IntPtr inUserData, IntPtr inAQ, IntPtr inBuffer, IntPtr inStartTime,
            uint inNumberPacketDescriptions, IntPtr inPacketDescs)
        {
            try
            {
                if (s_live.TryGetValue(inUserData, out var capture))
                    capture.OnBuffer(inAQ, inBuffer);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Microphone queue callback failed: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "voiceover.coreaudio-callback");
            }
        }

        private void OnBuffer(IntPtr queue, IntPtr buffer)
        {
            lock (_gate)
            {
                if (_stopped || _disposed || queue != _queue)
                    return;

                var bytes = Marshal.ReadInt32(buffer, AudioDataByteSizeOffset);
                var data = Marshal.ReadIntPtr(buffer, AudioDataOffset);
                if (bytes > 0 && data != IntPtr.Zero)
                {
                    // Copied out rather than handed over as a span: the handler is the recorder's
                    // conversion loop, which reads a byte[] like the WASAPI path gives it.
                    if (_managed.Length < bytes)
                        _managed = new byte[bytes];
                    Marshal.Copy(data, _managed, 0, bytes);

                    try
                    {
                        RaiseDataAvailable(_managed, bytes);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Microphone capture handler failed: " + ex.Message);
                        SignalStopped(ex);
                        return;
                    }
                }

                // Hand the buffer back for the next fill. A handler that asked for a stop makes
                // this the last one; the queue is stopped for good in Dispose.
                if (_stopped)
                    return;

                var status = AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
                if (status != 0)
                {
                    Debug.WriteLine("CoreAudio error " + status + " re-queueing a capture buffer.");
                    SignalStopped(new IOException("The microphone stopped accepting buffers (CoreAudio error " + status + ")."));
                }
            }
        }

        /// <summary>CoreAudio's notification thread: the device's is-alive flag changed.</summary>
        private static int AliveListener(uint inObjectID, uint inNumberAddresses, IntPtr inAddresses, IntPtr inClientData)
        {
            try
            {
                if (s_live.TryGetValue(inClientData, out var capture))
                    capture.OnAliveChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Microphone removal listener failed: " + ex.Message);
            }

            return 0;
        }

        private void OnAliveChanged()
        {
            // A device that is gone will not answer either, which is the same news.
            var alive = CoreAudioInterop.GetUInt32Property(_deviceId, CoreAudioInterop.kAudioDevicePropertyDeviceIsAlive,
                CoreAudioInterop.GlobalScope);
            if (alive.GetValueOrDefault() == 0)
                SignalStopped(new IOException("The microphone '" + _name + "' was disconnected."));
        }

        // -------------------------------------------------------------------------- permission

        /// <summary>Fails the open when the user has already refused Clowd the microphone, so the
        /// recorder reports no device rather than metering silence.</summary>
        private static void ThrowIfMicrophoneDenied()
        {
            var status = MicrophoneAuthorizationStatus();
            if (status == AVAuthorizationStatusDenied || status == AVAuthorizationStatusRestricted)
                throw new UnauthorizedAccessException(
                    "Microphone access is denied for Clowd (System Settings > Privacy & Security > Microphone).");
        }

        /// <summary><c>[AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio]</c>, or
        /// -1 when AVFoundation would not say (in which case the queue is simply tried).</summary>
        private static long MicrophoneAuthorizationStatus()
        {
            try
            {
                var avFoundation = NativeLibrary.Load(AvFoundationLib);
                var symbol = NativeLibrary.GetExport(avFoundation, "AVMediaTypeAudio");
                var mediaType = symbol == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(symbol);
                var deviceClass = GetClass("AVCaptureDevice");
                if (mediaType == IntPtr.Zero || deviceClass == IntPtr.Zero)
                    return -1;

                return SendMessageInteger(deviceClass, GetSelector("authorizationStatusForMediaType:"), mediaType);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Could not read the microphone permission: " + ex.Message);
                return -1;
            }
        }

        private static void Check(int status, string what)
        {
            if (status != 0)
                throw new InvalidOperationException("CoreAudio error " + status + " while " + what + ".");
        }
    }
}
