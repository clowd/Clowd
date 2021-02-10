using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Video
{
    public class AudioDeviceManager
    {
        public IEnumerable<IAudioSpeakerDevice> GetSpeakers()
        {
            return GetDevices(DataFlow.Render).Select(m => new NAudioSpeakerDevice(m));
        }

        public IEnumerable<IAudioMicrophoneDevice> GetMicrophones()
        {
            return GetDevices(DataFlow.Capture).Select(m => new NAudioMicrophoneDevice(m));
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

    internal class NAudioMicrophoneDevice : NAudioDevice, IAudioMicrophoneDevice
    {
        public NAudioMicrophoneDevice(MMDevice device) : base(device)
        {
        }
    }

    internal class NAudioSpeakerDevice : NAudioDevice, IAudioSpeakerDevice
    {
        public NAudioSpeakerDevice(MMDevice device) : base(device)
        {
        }
    }

    internal class NAudioDevice : IAudioDevice
    {
        public string DeviceId => _device.ID;

        public string FriendlyName => _device.FriendlyName;

        private MMDevice _device;

        public NAudioDevice(MMDevice device)
        {
            _device = device;
        }

        public IAudioLevelListener GetLevelListener()
        {
            return new LevelListener(_device);
        }
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
            _audioClient = device.AudioClient;
            _audioClient.Initialize(AudioClientShareMode.Shared,
               AudioClientStreamFlags.None,
               100,
               100,
               _audioClient.MixFormat,
               Guid.Empty);
            _audioClient.Start();
            this._device = device;
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
