using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;

namespace Clowd.UI
{
    /// <summary>An enumerated audio endpoint. DeviceId is what obs-express consumes verbatim
    /// (Windows: WASAPI MMDevice endpoint id; macOS: CoreAudio device UID; or "default").</summary>
    public sealed record AudioDeviceInfo(string DeviceId, string DeviceType, string FriendlyName);

    /// <summary>
    /// A live peak-level source for one device, driving the toolbar volume bars (WPF parity —
    /// the meter is how the user confirms they picked the right device before recording).
    /// </summary>
    public interface IAudioLevelListener : IDisposable
    {
        string DeviceId { get; }

        /// <summary>False on platforms without metering support (macOS for now) — hide the bar.</summary>
        bool IsSupported { get; }

        /// <summary>Current peak level on the WPF UI scale (0..100, dB-mapped).</summary>
        double PeakLevel { get; }
    }

    /// <summary>
    /// Cross-platform audio device enumeration + level metering (mirrors the WPF
    /// Clowd.Video.AudioDeviceManager API). Windows uses NAudio.Wasapi (MMDevice ids match what
    /// obs-express's wasapi sources expect); macOS enumerates via a CoreAudio P/Invoke shim
    /// (device UIDs, matching the coreaudio sources) with metering deferred.
    /// </summary>
    public static class AudioDeviceManager
    {
        public const string TypeSpeaker = "speaker";
        public const string TypeMicrophone = "microphone";
        public const string DefaultDeviceId = "default";

        /// <summary>Output/render devices; the "default" pseudo-device is always first.</summary>
        public static List<AudioDeviceInfo> GetSpeakers() => GetDevices(TypeSpeaker);

        /// <summary>Input/capture devices; the "default" pseudo-device is always first.</summary>
        public static List<AudioDeviceInfo> GetMicrophones() => GetDevices(TypeMicrophone);

        /// <summary>Returns the id when it still exists, else "default" (WPF semantics).</summary>
        public static string VerifySpeakerOrDefault(string deviceId) => Verify(deviceId, GetSpeakers());

        /// <summary>Returns the id when it still exists, else "default" (WPF semantics).</summary>
        public static string VerifyMicrophoneOrDefault(string deviceId) => Verify(deviceId, GetMicrophones());

        public static IAudioLevelListener CreateLevelListener(string deviceId, string deviceType)
        {
            if (OperatingSystem.IsWindows())
                return new WasapiLevelListener(deviceId, deviceType);

            // macOS metering needs AudioQueue taps (mic) / macOS 14.2+ process taps (speaker) —
            // deferred with the rest of the mac UI work. Enumeration works; the bar stays hidden.
            return new NullLevelListener(deviceId);
        }

        private static string Verify(string deviceId, List<AudioDeviceInfo> devices)
        {
            return !String.IsNullOrEmpty(deviceId) && devices.Any(d => d.DeviceId == deviceId)
                ? deviceId
                : DefaultDeviceId;
        }

        private static List<AudioDeviceInfo> GetDevices(string type)
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var flow = type == TypeSpeaker ? DataFlow.Render : DataFlow.Capture;
                    using var enumerator = new MMDeviceEnumerator();

                    var defName = "Default device";
                    try
                    {
                        using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                        defName = "Default - " + def.FriendlyName;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("No default audio endpoint for " + type + ": " + ex.Message);
                        SentryConfig.CaptureHandled(ex, "audio.default-endpoint");
                    }

                    list.Add(new AudioDeviceInfo(DefaultDeviceId, type, defName));

                    foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                    {
                        using (device)
                            list.Add(new AudioDeviceInfo(device.ID, type, device.FriendlyName));
                    }

                    return list;
                }

                list.Add(new AudioDeviceInfo(DefaultDeviceId, type, "Default device"));

                if (OperatingSystem.IsMacOS())
                    list.AddRange(CoreAudioInterop.GetDevices(type == TypeSpeaker, type));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to enumerate audio devices (" + type + "): " + ex.Message);
                SentryConfig.CaptureHandled(ex, "audio.enumerate");
                if (list.Count == 0)
                    list.Add(new AudioDeviceInfo(DefaultDeviceId, type, "Default device"));
            }

            return list;
        }

        internal static MMDevice GetMMDevice(string deviceId, string deviceType)
        {
            var enumerator = new MMDeviceEnumerator();
            try
            {
                if (String.Equals(deviceId, DefaultDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    var flow = deviceType == TypeSpeaker ? DataFlow.Render : DataFlow.Capture;
                    return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                }

                return enumerator.GetDevice(deviceId);
            }
            finally
            {
                enumerator.Dispose();
            }
        }

        /// <summary>
        /// Windows level listener: polls AudioMeterInformation on a background thread. Capture
        /// endpoints only meter while a stream is open (NAudio #347), so mics hold a keep-alive
        /// WasapiCapture; render endpoints meter passively whenever anything plays — opening a
        /// capture stream on one throws (a swallowed bug in the original WPF listener, not
        /// replicated here).
        /// </summary>
        private sealed class WasapiLevelListener : IAudioLevelListener
        {
            public string DeviceId { get; }
            public bool IsSupported => true;
            public double PeakLevel => _peak;

            private readonly string _deviceType;
            private volatile bool _exit;
            private volatile float _peak;

            public WasapiLevelListener(string deviceId, string deviceType)
            {
                DeviceId = deviceId;
                _deviceType = deviceType;

                var thread = new Thread(ThreadProc) { IsBackground = true, Name = "AudioLevelListener" };
                if (OperatingSystem.IsWindows())
                    thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }

            private void ThreadProc()
            {
                WasapiCapture keepAlive = null;
                try
                {
                    using var device = GetMMDevice(DeviceId, _deviceType);

                    if (device.DataFlow == DataFlow.Capture)
                    {
                        keepAlive = new WasapiCapture(device);
                        keepAlive.DataAvailable += (s, e) => { };
                        keepAlive.StartRecording();
                    }

                    while (!_exit)
                    {
                        Thread.Sleep(50);
                        var level = device.AudioMeterInformation.MasterPeakValue;

                        // linear peak -> dB -> 0..100 UI scale (same mapping as WPF Clowd)
                        _peak = level > 0 && level <= 1
                            ? (float)Math.Clamp(20d * Math.Log10(level) / 60d * 100d + 100d, 0d, 100d)
                            : 0f;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Audio level listener failed for '{DeviceId}': {ex.Message}");
                    SentryConfig.CaptureHandled(ex, "audio.level-listener");
                    _peak = 0f;
                }
                finally
                {
                    try
                    {
                        keepAlive?.StopRecording();
                        keepAlive?.Dispose();
                    }
                    catch { }
                }
            }

            public void Dispose()
            {
                _exit = true;
            }
        }

        private sealed class NullLevelListener : IAudioLevelListener
        {
            public string DeviceId { get; }
            public bool IsSupported => false;
            public double PeakLevel => 0;

            public NullLevelListener(string deviceId)
            {
                DeviceId = deviceId;
            }

            public void Dispose()
            {
            }
        }
    }
}
