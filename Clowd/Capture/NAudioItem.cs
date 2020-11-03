using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Clowd.Capture
{
    class NAudioItem : IDisposable
    {
        public MMDevice Device { get; }

        public bool IsLoopback { get; }

        AudioClient _audioClient;

        public void StartListeningForPeakLevel()
        {
            if (_audioClient != null)
                return;

            // Peak Level is available for recording devices only when they are active
            if (IsLoopback)
                return;

            _audioClient = Device.AudioClient;
            _audioClient.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.None,
                100,
                100,
                _audioClient.MixFormat,
                Guid.Empty);

            _audioClient.Start();
        }

        public void StopListeningForPeakLevel()
        {
            if (_audioClient == null)
                return;

            _audioClient.Stop();
            _audioClient.Dispose();
            _audioClient = null;

            _audioClient = null;
        }

        public string Name { get; }

        public NAudioItem(MMDevice Device, bool IsLoopback)
            : this(Device, Device.FriendlyName, IsLoopback)
        {
        }

        NAudioItem(MMDevice Device, string Name, bool IsLoopback)
        {
            this.Device = Device;
            this.IsLoopback = IsLoopback;
            this.Name = Name;
        }

        const string DefaultDeviceName = "Default";

        public static IEnumerable<NAudioItem> Microphones => GetDevices(DataFlow.Capture).Select(m => new NAudioItem(m, false));

        public static IEnumerable<NAudioItem> Speakers => GetDevices(DataFlow.Render).Select(m => new NAudioItem(m, true));

        public static NAudioItem DefaultMicrophone => new NAudioItem(
            WasapiCapture.GetDefaultCaptureDevice(),
            DefaultDeviceName,
            false);

        public static NAudioItem DefaultSpeaker => new NAudioItem(
            WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice(),
            DefaultDeviceName,
            true);

        public double PeakLevel => Device.AudioMeterInformation.MasterPeakValue;

        public override string ToString() => Name;

        public void Dispose()
        {
            StopListeningForPeakLevel();
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
}
