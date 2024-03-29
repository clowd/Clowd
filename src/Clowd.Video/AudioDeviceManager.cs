﻿using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using NLog;
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

        public static AudioDeviceInfo VerifyMicrophoneOrDefault(AudioDeviceInfo device)
        {
            if (device != null && device.DeviceType.EqualsIgnoreCase(TYPE_MICROPHONE))
            {
                if (GetMicrophones().Any(m => m.DeviceId.Equals(device.DeviceId)))
                {
                    return device;
                }
            }

            return GetDefaultMicrophone();
        }

        public static AudioDeviceInfo VerifySpeakerOrDefault(AudioDeviceInfo device)
        {
            if (device != null && device.DeviceType.EqualsIgnoreCase(TYPE_SPEAKER))
            {
                if (GetSpeakers().Any(m => m.DeviceId.Equals(device.DeviceId)))
                {
                    return device;
                }
            }

            return GetDefaultSpeaker();
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

        public static IAudioLevelListener GetAudioListener(AudioDeviceInfo info)
        {
            return new NAudioLevelListener(info);
        }

        internal static MMDevice GetDevice(AudioDeviceInfo info)
        {
            if (info.DeviceId.EqualsIgnoreCase(DEVICE_DEFAULT))
            {
                if (info.DeviceType.EqualsIgnoreCase(TYPE_SPEAKER))
                    return WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice();
                if (info.DeviceType.EqualsIgnoreCase(TYPE_MICROPHONE))
                    return WasapiCapture.GetDefaultCaptureDevice();
            }

            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(info.DeviceId);
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
