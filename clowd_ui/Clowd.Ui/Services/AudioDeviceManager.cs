using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;

namespace Clowd.UI
{
    /// <summary>An enumerated audio endpoint. DeviceId is what obs-express consumes verbatim
    /// (Windows: WASAPI MMDevice endpoint id; macOS: CoreAudio device UID; or "default").</summary>
    public sealed record AudioDeviceInfo(string DeviceId, string DeviceType, string FriendlyName);

    /// <summary>
    /// Cross-platform audio device enumeration (mirrors the WPF Clowd.Video.AudioDeviceManager
    /// API). Windows uses NAudio.Wasapi (MMDevice ids match what obs-express's wasapi sources
    /// expect); macOS uses a CoreAudio P/Invoke shim (device UIDs, matching the coreaudio sources).
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
    }
}
