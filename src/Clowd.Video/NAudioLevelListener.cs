using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NLog;

namespace Clowd.Video
{
    internal class NAudioLevelListener : SimpleNotifyObject, IAudioLevelListener
    {
        public AudioDeviceInfo Device => _info;

        public double PeakLevel
        {
            get => _peakLevel;
            set => Set(ref _peakLevel, value);
        }

        private readonly SynchronizationContext _context;
        private readonly Thread _thread;
        private readonly AudioDeviceInfo _info;
        private bool _exitRequested;
        private double _peakLevel;

        private static readonly ILogger _log = LogManager.GetCurrentClassLogger();

        public NAudioLevelListener(AudioDeviceInfo info)
        {
            _info = info;
            _context = SynchronizationContext.Current;
            _thread = new Thread(ThreadProc);
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void ThreadProc()
        {
            try
            {
                using var device = AudioDeviceManager.GetDevice(_info);
                using var capturer = new WasapiCapture(device);

                // if we are not recording we will not get level information from WASAPI
                // https://github.com/naudio/NAudio/issues/347
                capturer.StartRecording();

                while (!_exitRequested)
                {
                    Thread.Sleep(1);
                    double level = device.AudioMeterInformation.MasterPeakValue;

                    // convert to Db and UI scale (0-100)
                    if (level > 0 && level <= 1)
                        level = (20 * Math.Log10(level)) / 60 * 100 + 100;

                    // uncomment to scale meter by current volume
                    // level *= device.AudioEndpointVolume.MasterVolumeLevelScalar;

                    if (_context != null)
                    {
                        // synchronize notification to calling thread
                        _context.Send(s => PeakLevel = (double)s, level);
                    }
                    else
                    {
                        PeakLevel = level;
                    }
                }

                capturer.StopRecording();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Fatal error while retrieving audio levels for device: " + _info);
            }
        }

        public void Dispose()
        {
            _exitRequested = true;
        }
    }
}
