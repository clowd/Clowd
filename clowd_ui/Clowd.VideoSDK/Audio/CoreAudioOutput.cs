using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// CoreAudio output on macOS: the default-output AudioUnit, which is what every macOS media
    /// app plays through. The unit is created on the first <see cref="Play"/> — opening one binds a
    /// device and spins up its render thread, and the editor builds an output long before anything
    /// asks it to play.
    /// <para>
    /// The render callback runs on CoreAudio's own real-time thread. It must not allocate, lock or
    /// block, so it does nothing but hand the unit's buffer to <see cref="AudioRenderCallback"/> as
    /// a span over unmanaged memory — the same zero-copy shape WASAPI's byte buffer gets. The
    /// delegate is held in a field for exactly as long as the unit is open, because CoreAudio keeps
    /// a raw function pointer to it and the GC has no idea.
    /// </para>
    /// <para>
    /// As on Windows, nothing here touches a device volume: attenuation is the sink's job (see
    /// <see cref="Playback.NAudioSink"/>). <c>kAudioUnitProperty_MaximumFramesPerSlice</c> and the
    /// device's own buffer frame size are left at their defaults — the requested
    /// <c>latencyMs</c> is what the caller corrects media time by, and quietly moving the device
    /// off it would put the correction and the device out of step, which is precisely what
    /// <see cref="IAudioOutput.Initialize"/> forbids.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("macos")]
    public sealed class CoreAudioOutput : IAudioOutput
    {
        // ------------------------------------------------------------------ AudioToolbox interop

        private const string AudioToolbox =
            "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

        // FourCC codes, spelled as the constants CoreAudio's headers define.
        private const uint kAudioUnitType_Output = 0x61756F75;         // 'auou'
        private const uint kAudioUnitSubType_DefaultOutput = 0x64656620; // 'def '
        private const uint kAudioUnitManufacturer_Apple = 0x6170706C;  // 'appl'
        private const uint kAudioFormatLinearPCM = 0x6C70636D;         // 'lpcm'

        private const uint kAudioFormatFlagIsFloat = 1 << 0;
        private const uint kAudioFormatFlagIsPacked = 1 << 3;

        private const uint kAudioUnitProperty_StreamFormat = 8;
        private const uint kAudioUnitProperty_SetRenderCallback = 23;
        private const uint kAudioUnitScope_Input = 1;
        private const uint kAudioUnitScope_Output = 2;

        // The three device properties that add up to output latency, all in frames.
        private const uint kAudioDevicePropertyBufferFrameSize = 0x6673697A; // 'fsiz'
        private const uint kAudioDevicePropertyLatency = 0x6C746E63;         // 'ltnc'
        private const uint kAudioDevicePropertySafetyOffset = 0x73616674;    // 'saft'

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioComponentDescription
        {
            public uint componentType;
            public uint componentSubType;
            public uint componentManufacturer;
            public uint componentFlags;
            public uint componentFlagsMask;
        }

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

        [StructLayout(LayoutKind.Sequential)]
        private struct AURenderCallbackStruct
        {
            public IntPtr inputProc;
            public IntPtr inputProcRefCon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioBuffer
        {
            public uint mNumberChannels;
            public uint mDataByteSize;
            public IntPtr mData;
        }

        /// <summary>
        /// CoreAudio's variable-length buffer list. One interleaved stream means exactly one
        /// buffer, so the single inline element is the whole list.
        /// <para>
        /// The nesting is load-bearing, not decoration: <c>AudioBuffer</c> holds a pointer, so it
        /// is pointer-aligned and the first one starts at offset 8, four bytes past
        /// <c>mNumberBuffers</c>. Flattening the three fields into this struct silently reads
        /// <c>mDataByteSize</c> out of <c>mNumberChannels</c> — a byte count of 2, one empty span
        /// per callback, and a device that plays nothing while every status code says OK.
        /// </para>
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct AudioBufferList
        {
            public uint mNumberBuffers;
            public AudioBuffer mBuffers0;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AURenderCallback(
            IntPtr inRefCon, ref uint ioActionFlags, IntPtr inTimeStamp,
            uint inBusNumber, uint inNumberFrames, IntPtr ioData);

        [DllImport(AudioToolbox)]
        private static extern IntPtr AudioComponentFindNext(IntPtr inComponent, ref AudioComponentDescription inDesc);

        [DllImport(AudioToolbox)]
        private static extern int AudioComponentInstanceNew(IntPtr inComponent, out IntPtr outInstance);

        [DllImport(AudioToolbox)]
        private static extern int AudioComponentInstanceDispose(IntPtr inInstance);

        [DllImport(AudioToolbox)]
        private static extern int AudioUnitSetProperty(IntPtr inUnit, uint inID, uint inScope,
            uint inElement, ref AudioStreamBasicDescription inData, uint inDataSize);

        [DllImport(AudioToolbox)]
        private static extern int AudioUnitSetProperty(IntPtr inUnit, uint inID, uint inScope,
            uint inElement, ref AURenderCallbackStruct inData, uint inDataSize);

        [DllImport(AudioToolbox)]
        private static extern int AudioUnitGetProperty(IntPtr inUnit, uint inID, uint inScope,
            uint inElement, ref uint outData, ref uint ioDataSize);

        [DllImport(AudioToolbox)]
        private static extern int AudioUnitInitialize(IntPtr inUnit);

        [DllImport(AudioToolbox)]
        private static extern int AudioUnitUninitialize(IntPtr inUnit);

        [DllImport(AudioToolbox)]
        private static extern int AudioOutputUnitStart(IntPtr inUnit);

        [DllImport(AudioToolbox)]
        private static extern int AudioOutputUnitStop(IntPtr inUnit);

        // ------------------------------------------------------------------------------- state

        private readonly object _sync = new object();

        private int _sampleRate;
        private int _channels;
        private AudioRenderCallback _render;

        private IntPtr _unit;
        private AURenderCallback _callback; // rooted: CoreAudio holds the raw pointer
        private bool _playing;
        private bool _disposed;
        private int _requestedLatencyMs;
        private int _actualLatencyMs;

        public void Initialize(int sampleRate, int channels, int latencyMs, AudioRenderCallback render)
        {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(channels));

            lock (_sync)
            {
                _render = render ?? throw new ArgumentNullException(nameof(render));
                _sampleRate = sampleRate;
                _channels = channels;
                // stands in until the unit is opened and can be asked; the device almost always
                // has far less latency than the caller asked for, never more.
                _requestedLatencyMs = latencyMs;
                System.Threading.Volatile.Write(ref _actualLatencyMs, latencyMs);
            }
        }

        /// <summary>
        /// What the device actually runs at, measured once the unit is open: its IO buffer plus the
        /// device's own latency plus its safety offset. Typically ~14 ms (512 + 72 + 76 frames at
        /// 48 kHz), against the 100 ms a caller asks for — reporting the request instead would put
        /// the master clock 86 ms behind the sound.
        /// </summary>
        /// <remarks>Deliberately lock-free: the master clock reads this on every presented frame,
        /// and it is one <see langword="int"/> written once when the unit opens.</remarks>
        public int ActualLatencyMs => System.Threading.Volatile.Read(ref _actualLatencyMs);

        /// <summary>Sums the device's latency properties, in milliseconds. Falls back to the
        /// requested value if the unit will not answer — every one of these is optional.</summary>
        private int MeasureLatencyLocked(IntPtr unit)
        {
            uint total = 0;
            bool any = false;
            foreach (var property in new[]
                     {
                         kAudioDevicePropertyBufferFrameSize,
                         kAudioDevicePropertyLatency,
                         kAudioDevicePropertySafetyOffset,
                     })
            {
                uint frames = 0, size = sizeof(uint);
                if (AudioUnitGetProperty(unit, property, kAudioUnitScope_Output, 0, ref frames, ref size) == 0)
                {
                    total += frames;
                    any = true;
                }
            }

            if (!any || _sampleRate <= 0)
                return _requestedLatencyMs;

            return (int)Math.Round(total * 1000.0 / _sampleRate);
        }

        public void Play()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                if (_render == null)
                    throw new InvalidOperationException("Initialize must be called before Play.");
                if (_playing)
                    return;

                if (_unit == IntPtr.Zero)
                    OpenUnitLocked();

                Check(AudioOutputUnitStart(_unit), "starting the output unit");
                _playing = true;
            }
        }

        public void Pause()
        {
            lock (_sync)
            {
                if (!_playing || _unit == IntPtr.Zero)
                    return;
                Check(AudioOutputUnitStop(_unit), "stopping the output unit");
                _playing = false;
            }
        }

        /// <summary>The unit has no position of its own — it plays whatever the callback fills —
        /// so stopping the pull is the whole of Stop.</summary>
        public void Stop() => Pause();

        private void OpenUnitLocked()
        {
            var desc = new AudioComponentDescription
            {
                componentType = kAudioUnitType_Output,
                componentSubType = kAudioUnitSubType_DefaultOutput,
                componentManufacturer = kAudioUnitManufacturer_Apple,
            };

            var component = AudioComponentFindNext(IntPtr.Zero, ref desc);
            if (component == IntPtr.Zero)
                throw new InvalidOperationException("No default CoreAudio output unit is available.");

            Check(AudioComponentInstanceNew(component, out var unit), "creating the output unit");

            try
            {
                // Interleaved float32, which is what the callback contract is written in: packed
                // and *without* kAudioFormatFlagIsNonInterleaved, so one buffer carries every
                // channel. The unit resamples to the device's own rate itself.
                uint bytesPerFrame = (uint)(_channels * sizeof(float));
                var format = new AudioStreamBasicDescription
                {
                    mSampleRate = _sampleRate,
                    mFormatID = kAudioFormatLinearPCM,
                    mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
                    mBytesPerPacket = bytesPerFrame,
                    mFramesPerPacket = 1,
                    mBytesPerFrame = bytesPerFrame,
                    mChannelsPerFrame = (uint)_channels,
                    mBitsPerChannel = 32,
                };
                Check(AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input,
                        0, ref format, (uint)Marshal.SizeOf<AudioStreamBasicDescription>()),
                    "setting the output stream format");

                _callback = RenderProc;
                var cb = new AURenderCallbackStruct
                {
                    inputProc = Marshal.GetFunctionPointerForDelegate(_callback),
                    inputProcRefCon = IntPtr.Zero,
                };
                Check(AudioUnitSetProperty(unit, kAudioUnitProperty_SetRenderCallback, kAudioUnitScope_Input,
                        0, ref cb, (uint)Marshal.SizeOf<AURenderCallbackStruct>()),
                    "installing the render callback");

                Check(AudioUnitInitialize(unit), "initializing the output unit");
                // only meaningful once initialized — before that the unit has no device bound.
                System.Threading.Volatile.Write(ref _actualLatencyMs, MeasureLatencyLocked(unit));
                _unit = unit;
            }
            catch
            {
                _callback = null;
                AudioComponentInstanceDispose(unit);
                throw;
            }
        }

        /// <summary>
        /// CoreAudio's real-time render thread. Everything it touches is already allocated: it
        /// reads the one buffer out of the list and hands it straight to the sink as a span over
        /// the unit's own memory.
        /// </summary>
        private unsafe int RenderProc(IntPtr inRefCon, ref uint ioActionFlags, IntPtr inTimeStamp,
            uint inBusNumber, uint inNumberFrames, IntPtr ioData)
        {
            var list = (AudioBufferList*)ioData;
            if (list == null || list->mNumberBuffers == 0 || list->mBuffers0.mData == IntPtr.Zero)
                return 0;

            // deliberately not under _sync: the lock is held across device calls that can block,
            // and stalling the real-time thread on one is how you get a glitch. Play/Pause bracket
            // the callback themselves (the unit is stopped before the field is cleared), and a
            // torn read here can only cost one buffer of silence.
            var render = _render;
            int floats = (int)(list->mBuffers0.mDataByteSize / sizeof(float));
            var buffer = new Span<float>((void*)list->mBuffers0.mData, floats);

            if (render == null)
            {
                buffer.Clear();
                return 0;
            }

            render(buffer);
            return 0;
        }

        private static void Check(int status, string what)
        {
            if (status != 0)
                throw new InvalidOperationException($"CoreAudio error {status} while {what}.");
        }

        public void Dispose()
        {
            IntPtr unit;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _playing = false;
                unit = _unit;
                _unit = IntPtr.Zero;
                _render = null;
            }

            if (unit == IntPtr.Zero)
                return;

            // stop first: the callback must be off the real-time thread before the delegate it
            // points at becomes collectable.
            try { AudioOutputUnitStop(unit); } catch { }
            try { AudioUnitUninitialize(unit); } catch { }
            try { AudioComponentInstanceDispose(unit); } catch { }
            _callback = null;
        }
    }
}
