using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Threading;
using Clowd.Video;

namespace Clowd.UI.Helpers
{
    public class UIAudioMonitor : IDisposable, INotifyPropertyChanged
    {
        public IAudioMicrophoneDevice MicrophoneDevice
        {
            get
            {
                ThrowIfDisposed();
                return _microphoneDevice;
            }
            set
            {
                ThrowIfDisposed();

                if (value != null && value.Equals(_microphoneDevice))
                    return;

                IAudioLevelListener tmp = _lvlMic;
                _lvlMic = null;
                tmp?.Dispose();

                var newv = value ?? AudioDeviceManager.GetDefaultMicrophone();
                _lvlMic = newv.GetLevelListener();
                _microphoneDevice = newv;

                OnPropertyChanged();
            }
        }

        public IAudioSpeakerDevice SpeakerDevice
        {
            get
            {
                ThrowIfDisposed();
                return _speakerDevice;
            }
            set
            {
                ThrowIfDisposed();

                if (value != null && value.Equals(_speakerDevice))
                    return;

                IAudioLevelListener tmp = _lvlSpeaker;
                _lvlSpeaker = null;
                tmp?.Dispose();

                var newv = value ?? AudioDeviceManager.GetDefaultSpeaker();
                _lvlSpeaker = newv.GetLevelListener();
                _speakerDevice = newv;

                OnPropertyChanged();
            }
        }

        public double MicrophoneLevel
        {
            get => _microphoneLevel;
            private set
            {
                if (value == _microphoneLevel)
                    return;

                _microphoneLevel = value;
                OnPropertyChanged();
            }
        }

        public double SpeakerLevel
        {
            get => _speakerLevel;
            private set
            {
                if (value == _speakerLevel)
                    return;

                _speakerLevel = value;
                OnPropertyChanged();
            }
        }

        private IAudioMicrophoneDevice _microphoneDevice;
        private IAudioSpeakerDevice _speakerDevice;
        private IAudioLevelListener _lvlSpeaker;
        private IAudioLevelListener _lvlMic;
        private double _microphoneLevel;
        private double _speakerLevel;

        public event PropertyChangedEventHandler PropertyChanged;

        private bool _disposed;
        private VideoCapturerSettings _settings;
        private Dispatcher _dispatcher;
        private Timer _audioTimer;

        public UIAudioMonitor(VideoCapturerSettings settings, Dispatcher dispatcher, int refreshDelayMs)
        {
            _dispatcher = dispatcher;
            _settings = settings;

            if (_settings.CaptureMicrophoneDevice == null)
                _settings.CaptureMicrophoneDevice = AudioDeviceManager.GetDefaultMicrophone();
            if (_settings.CaptureSpeakerDevice == null)
                _settings.CaptureSpeakerDevice = AudioDeviceManager.GetDefaultSpeaker();

            _audioTimer = new Timer(refreshDelayMs);
            _audioTimer.Elapsed += AudioTimer_Elapsed;
            _audioTimer.AutoReset = true;
            _audioTimer.Enabled = true;
            _audioTimer.Start();

            _settings.PropertyChanged += settings_PropertyChanged;
            settings_PropertyChanged(null, null);
        }

        ~UIAudioMonitor()
        {
            Dispose();
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            SpeakerDevice = _settings.CaptureSpeakerDevice;
            MicrophoneDevice = _settings.CaptureMicrophoneDevice;
        }

        private void AudioTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            double spk = ConvertLevelToDb(_lvlSpeaker);
            double mic = ConvertLevelToDb(_lvlMic);

            if (spk != SpeakerLevel || MicrophoneLevel != MicrophoneLevel)
            {
                _dispatcher.Invoke(() =>
                {
                    MicrophoneLevel = mic;
                    SpeakerLevel = spk;
                });
            }
        }

        private double ConvertLevelToDb(IAudioLevelListener item)
        {
            if (item == null)
                return 0;

            double level = item.GetPeakLevel();

            if (level > 0 && level <= 1)
                return (20 * Math.Log10(level)) / 60 * 100 + 100;

            return 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _settings.PropertyChanged -= settings_PropertyChanged;
            _audioTimer.Stop();
            _audioTimer.Dispose();
            _lvlSpeaker?.Dispose();
            _lvlMic?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UIAudioMonitor));
        }
    }
}
