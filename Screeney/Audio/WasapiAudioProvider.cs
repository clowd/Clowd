using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Screeney.Audio
{
    internal class WasapiAudioProvider : IDisposable
    {
        public bool IsRunning { get; private set; }
        public WaveFormat WaveFormat { get; private set; }
        public EventHandler<WaveInEventArgs> DataAvailable;

        WasapiOut _silence;
        IWaveIn _capture;

        public WasapiAudioProvider(MMDevice device)
        {
            if (device.DataFlow == DataFlow.Render)
            {
                _silence = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
                _silence.Init(new SilenceProvider(device.AudioClient.MixFormat));
                _capture = new WasapiLoopbackCapture(device) { ShareMode = AudioClientShareMode.Shared };
                this.WaveFormat = device.AudioClient.MixFormat;
            }
            else
            {
                _capture = new WasapiCapture(device);
                this.WaveFormat = new WaveFormat(44100, 1);
            }

            _capture.WaveFormat = this.WaveFormat;
            _capture.DataAvailable += Audio_DataAvailable;
        }

        public void Start()
        {
            if (_silence != null)
                _silence.Play();
            _capture.StartRecording();
            IsRunning = true;
        }
        public void Stop()
        {
            if (_silence != null)
                _silence.Stop();
            _capture.StopRecording();
            IsRunning = false;
        }

        public static IEnumerable<MMDevice> GetActiveDevices()
        {
            return new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
        }

        private void Audio_DataAvailable(object sender, WaveInEventArgs e)
        {
            DataAvailable?.Invoke(this, e);
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _capture.StopRecording();
                    _capture.Dispose();
                    _capture = null;
                    if (_silence != null)
                    {
                        _silence.Stop();
                        _silence.Dispose();
                        _silence = null;
                    }
                }
                disposedValue = true;
            }
        }
        public void Dispose()
        {
            Dispose(true);
        }
        #endregion
    }
}
