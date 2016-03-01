using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Screeney.Audio
{
    internal class AudioDeviceRecorder : IDisposable
    {
        public bool Loopback { get; private set; }
        public string FileName { get; private set; }

        WasapiOut _silence;
        IWaveIn _capture;
        WaveFileWriter _writer;

        public AudioDeviceRecorder(string fileOut, MMDevice device)
        {
            WaveFormat audioFormat;
            FileName = fileOut;

            if (device.DataFlow == DataFlow.Render)
            {
                Loopback = true;
                _silence = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
                _silence.Init(new SilenceProvider(device.AudioClient.MixFormat));
                _capture = new WasapiLoopbackCapture(device) { ShareMode = AudioClientShareMode.Shared };
                _capture.DataAvailable += Audio_DataAvailable;
                audioFormat = device.AudioClient.MixFormat;
            }
            else
            {
                Loopback = false;
                _capture = new WasapiCapture(device);
                _capture.WaveFormat = new WaveFormat(8000, 1);
                audioFormat = _capture.WaveFormat;
            }

            _writer = new WaveFileWriter(fileOut, audioFormat);
        }

        public void Start()
        {
            if (_silence != null)
                _silence.Play();
            _capture.StartRecording();
        }
        public void Stop()
        {
            if (_silence != null)
                _silence.Stop();
            _capture.StopRecording();
        }

        private void Audio_DataAvailable(object sender, WaveInEventArgs e)
        {
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
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
                    _writer.Close();
                    _writer.Dispose();
                    _writer = null;
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
