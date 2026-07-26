using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Clowd.UI
{
    /// <summary>
    /// macOS peak-level listener (see IAudioLevelListener). Microphones meter via a HAL IOProc
    /// on the input device itself. Speakers have no per-device meter API — a CoreAudio process
    /// tap (macOS 14.2+, hidden below that) mixes the WHOLE system output into a private
    /// aggregate device whose input IOProc is metered instead, regardless of which output device
    /// is selected. All CoreAudio setup runs on a background thread; a setup failure just leaves
    /// the meter at 0 (a dead meter on a live toggle is the user's "wrong device" signal, same
    /// as Windows).
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal sealed class CoreAudioLevelListener : IAudioLevelListener
    {
        public string DeviceId { get; }
        public bool IsSupported { get; }

        public double PeakLevel
        {
            get
            {
                // linear peak -> dB -> 0..100 UI scale (same mapping as the Windows listener)
                double level = Math.Min(_linearPeak, 1f);
                return level > 0
                    ? Math.Clamp(20d * Math.Log10(level) / 60d * 100d + 100d, 0d, 100d)
                    : 0d;
            }
        }

        private readonly string _deviceType;
        // instance field so the native thunk survives as long as the HAL holds the IOProc
        private readonly CoreAudioInterop.AudioDeviceIOProc _ioProc;
        private volatile float _linearPeak;
        private volatile bool _exit;
        private float[] _scratch = new float[4096];

        public CoreAudioLevelListener(string deviceId, string deviceType)
        {
            DeviceId = deviceId;
            _deviceType = deviceType;
            _ioProc = MeterIoProc;
            IsSupported = deviceType == AudioDeviceManager.TypeMicrophone || OperatingSystem.IsMacOSVersionAtLeast(14, 2);

            if (IsSupported)
                new Thread(ThreadProc) { IsBackground = true, Name = "CoreAudioLevelListener" }.Start();
        }

        public void Dispose()
        {
            _exit = true;
        }

        private void ThreadProc()
        {
            uint device = 0, tap = 0, aggregate = 0;
            var procId = IntPtr.Zero;
            try
            {
                if (_deviceType == AudioDeviceManager.TypeMicrophone)
                {
                    device = CoreAudioInterop.FindDevice(DeviceId, output: false);
                }
                else
                {
                    (tap, aggregate) = CoreAudioInterop.CreateSystemAudioTap();
                    device = aggregate;
                }

                if (device == 0)
                    return;

                if (CoreAudioInterop.AudioDeviceCreateIOProcID(device, _ioProc, IntPtr.Zero, out procId) != 0)
                    return;

                if (CoreAudioInterop.AudioDeviceStart(device, procId) != 0)
                    return;

                while (!_exit)
                    Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CoreAudio level listener failed for '{DeviceId}': {ex.Message}");
                SentryConfig.CaptureHandled(ex, "audio.coreaudio-level-listener");
            }
            finally
            {
                try
                {
                    if (procId != IntPtr.Zero)
                    {
                        CoreAudioInterop.AudioDeviceStop(device, procId);
                        CoreAudioInterop.AudioDeviceDestroyIOProcID(device, procId);
                    }

                    if (aggregate != 0)
                        CoreAudioInterop.AudioHardwareDestroyAggregateDevice(aggregate);
                    if (tap != 0)
                        CoreAudioInterop.AudioHardwareDestroyProcessTap(tap);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"CoreAudio level listener teardown failed for '{DeviceId}': {ex.Message}");
                }
            }
        }

        private int MeterIoProc(uint device, IntPtr now, IntPtr inputData, IntPtr inputTime, IntPtr outputData, IntPtr outputTime, IntPtr clientData)
        {
            // The HAL hands clients canonical Float32 samples. AudioBufferList layout:
            // u32 mNumberBuffers, pad, then per buffer { u32 channels, u32 byteSize, void* data }.
            var peak = 0f;
            if (inputData != IntPtr.Zero)
            {
                int buffers = Marshal.ReadInt32(inputData);
                var p = inputData + 8;
                for (int i = 0; i < buffers; i++, p += 16)
                {
                    int count = Marshal.ReadInt32(p, 4) / 4;
                    var data = Marshal.ReadIntPtr(p, 8);
                    if (data == IntPtr.Zero || count <= 0)
                        continue;

                    if (_scratch.Length < count)
                        _scratch = new float[count];
                    Marshal.Copy(data, _scratch, 0, count);
                    for (int s = 0; s < count; s++)
                    {
                        var v = Math.Abs(_scratch[s]);
                        if (v > peak)
                            peak = v;
                    }
                }
            }

            // meter ballistics: instant attack, ~0.5 s decay at the typical ~10 ms IO cadence
            _linearPeak = Math.Max(peak, _linearPeak * 0.94f);
            return 0;
        }
    }
}
