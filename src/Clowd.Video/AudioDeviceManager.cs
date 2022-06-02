using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using RT.Util.ExtensionMethods;

namespace Clowd.Video
{
    public static class AudioDeviceManager
    {
        private const string TYPE_MICROPHONE = "microphone";
        private const string TYPE_SPEAKER = "speaker";
        private const string DEVICE_DEFAULT = "default";

        public static IEnumerable<AudioDeviceInfo> GetSpeakers()
        {
            yield return GetDefaultSpeaker();
            foreach (var d in GetDevices(DataFlow.Render, TYPE_SPEAKER))
                yield return d;
        }

        public static IEnumerable<AudioDeviceInfo> GetMicrophones()
        {
            yield return GetDefaultMicrophone();
            foreach (var d in GetDevices(DataFlow.Capture, TYPE_MICROPHONE))
                yield return d;
        }

        public static AudioDeviceInfo GetDefaultMicrophone()
        {
            return new()
            {
                DeviceId = DEVICE_DEFAULT,
                DeviceType = TYPE_MICROPHONE
            };
        }

        public static AudioDeviceInfo GetDefaultSpeaker()
        {
            return new()
            {
                DeviceId = DEVICE_DEFAULT,
                DeviceType = TYPE_SPEAKER
            };
        }

        public static string GetFriendlyName(AudioDeviceInfo info)
        {
            if (info.DeviceId.EqualsIgnoreCase(DEVICE_DEFAULT))
            {
                try
                {
                    if (info.DeviceType.EqualsIgnoreCase(TYPE_SPEAKER))
                    {
                        using var def = WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice();
                        return "Default - " + def.FriendlyName;
                    }

                    if (info.DeviceType.EqualsIgnoreCase(TYPE_MICROPHONE))
                    {
                        using var def = WasapiCapture.GetDefaultCaptureDevice();
                        return "Default - " + def.FriendlyName;
                    }
                }
                catch
                {
                    return "(none)";
                }

                throw new ArgumentException("If DeviceId is 'default', DeviceType must be 'speaker' or 'microphone'.");
            }

            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDevice(info.DeviceId);
            return dev.FriendlyName;
        }

        private static IEnumerable<AudioDeviceInfo> GetDevices(DataFlow flow, string type)
        {
            using (MMDeviceEnumerator enumerator = new MMDeviceEnumerator())
            {
                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    using (device)
                    {
                        yield return new()
                        {
                            DeviceId = device.ID,
                            DeviceType = type
                        };
                    }
                }
            }
        }
    }
}
