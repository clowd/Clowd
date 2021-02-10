﻿using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Video
{
    public class AudioDeviceManager
    {
        public static IEnumerable<IAudioSpeakerDevice> GetSpeakers()
        {
            yield return GetDefaultSpeaker();
            foreach (var d in GetDevices(DataFlow.Render).Select(m => new NAudioDevice(m.ID)))
                yield return d;
        }

        public static IEnumerable<IAudioMicrophoneDevice> GetMicrophones()
        {
            yield return GetDefaultMicrophone();
            foreach (var d in GetDevices(DataFlow.Capture).Select(m => new NAudioDevice(m.ID)))
                yield return d;
        }

        public static IAudioMicrophoneDevice GetDefaultMicrophone()
        {
            return new NAudioDevice(DataFlow.Capture);
        }

        public static IAudioSpeakerDevice GetDefaultSpeaker()
        {
            return new NAudioDevice(DataFlow.Render);
        }

        protected static IEnumerable<MMDevice> GetDevices(DataFlow flow)
        {
            using (MMDeviceEnumerator enumerator = new MMDeviceEnumerator())
            {
                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    yield return device;
                }
            }
        }
    }

    internal class NAudioDevice : IAudioMicrophoneDevice, IAudioSpeakerDevice, IEquatable<NAudioDevice>
    {
        public string DeviceId
        {
            get
            {
                var mm = GetMM();
                return mm.isDefault ? "default" : mm.device.ID;
            }
        }

        public string FriendlyName
        {
            get
            {
                var mm = GetMM();
                return mm.isDefault ? "Default - " + mm.device.FriendlyName : mm.device.FriendlyName;
            }
        }

        const string DEFAULT_CAPTURE = "default-capture";
        const string DEFAULT_RENDER = "default-render";

        private string _deviceId;

        private NAudioDevice() { } // serialization

        public NAudioDevice(DataFlow flow)
        {
            _deviceId = flow == DataFlow.Capture ? DEFAULT_CAPTURE : DEFAULT_RENDER;
        }

        public NAudioDevice(string deviceId)
        {
            _deviceId = deviceId;
        }

        private (bool isDefault, MMDevice device) GetMM()
        {
            if (_deviceId == DEFAULT_CAPTURE)
                return (true, WasapiCapture.GetDefaultCaptureDevice());

            if (_deviceId == DEFAULT_RENDER)
                return (true, WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice());

            using (var enumerator = new MMDeviceEnumerator())
                return (false, enumerator.GetDevice(_deviceId));
        }

        public IAudioLevelListener GetLevelListener() => new LevelListener(GetMM().device);

        public override bool Equals(object obj)
        {
            if (obj is NAudioDevice dev) return Equals(dev);
            return false;
        }

        public bool Equals(NAudioDevice other) => _deviceId == other._deviceId;
        public override int GetHashCode() => _deviceId?.GetHashCode() ?? 0;
    }

    internal class LevelListener : IAudioLevelListener
    {
        private readonly MMDevice _device;
        private readonly AudioClient _audioClient;
        private bool _disposed = false;

        ~LevelListener()
        {
            Dispose();
        }

        public LevelListener(MMDevice device)
        {
            _device = device;
            _audioClient = device.AudioClient; // this property creates a new audio client that must be disposed
            _audioClient.Initialize(AudioClientShareMode.Shared,
               AudioClientStreamFlags.None,
               100,
               100,
               _audioClient.MixFormat,
               Guid.Empty);
            _audioClient.Start();
        }

        public double GetPeakLevel()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LevelListener));
            return _device.AudioMeterInformation.MasterPeakValue;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _audioClient.Stop();
                _audioClient.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
