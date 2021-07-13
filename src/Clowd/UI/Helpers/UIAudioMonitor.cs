using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Clowd.Util;
using Clowd.Video;

namespace Clowd.UI.Helpers
{
    public class UIAudioMonitor : IDisposable, INotifyPropertyChanged
    {
        public IAudioMicrophoneDevice MicrophoneDevice
        {
            get
            {
                ThrowIfStateInvalid();
                return _microphoneDevice;
            }
            set
            {
                ThrowIfStateInvalid();

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
                ThrowIfStateInvalid();
                return _speakerDevice;
            }
            set
            {
                ThrowIfStateInvalid();

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

        public bool MicrophoneEnabled
        {
            get => _settings.CaptureMicrophone;
            set => _settings.CaptureMicrophone = value;
        }

        public bool SpeakerEnabled
        {
            get => _settings.CaptureSpeaker;
            set => _settings.CaptureSpeaker = value;
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
        private IDisposable _timer;
        private Thread _thread;

        public UIAudioMonitor(VideoCapturerSettings settings, int refreshDelayMs)
        {
            _thread = Thread.CurrentThread;
            _settings = settings;

            ThrowIfStateInvalid();

            if (_settings.CaptureMicrophoneDevice == null)
                _settings.CaptureMicrophoneDevice = AudioDeviceManager.GetDefaultMicrophone();
            if (_settings.CaptureSpeakerDevice == null)
                _settings.CaptureSpeakerDevice = AudioDeviceManager.GetDefaultSpeaker();

            _timer = DisposableTimer.Start(TimeSpan.FromMilliseconds(20), AudioTimer_Elapsed);

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
            OnPropertyChanged(nameof(MicrophoneEnabled));
            OnPropertyChanged(nameof(SpeakerEnabled));
        }

        private void AudioTimer_Elapsed()
        {
            try
            {
                double spk = ConvertLevelToDb(_lvlSpeaker);
                double mic = ConvertLevelToDb(_lvlMic);

                if (spk != SpeakerLevel || MicrophoneLevel != MicrophoneLevel)
                {
                    MicrophoneLevel = mic;
                    SpeakerLevel = spk;
                }
            }
            catch (ObjectDisposedException)
            { }
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

        public ProgressBar GetSpeakerVisual()
        {
            return GetLevelVisual(nameof(SpeakerLevel), nameof(SpeakerEnabled));
        }

        public ProgressBar GetMicrophoneVisual()
        {
            return GetLevelVisual(nameof(MicrophoneLevel), nameof(MicrophoneEnabled));
        }

        private ProgressBar GetLevelVisual(string levelPath, string enabledPath)
        {
            ThrowIfStateInvalid();
            var prog = new ProgressBar { Style = AppStyles.AudioLevelProgressBarStyle };

            var valueBinding = new Binding(levelPath);
            valueBinding.Source = this;
            valueBinding.Mode = BindingMode.OneWay;
            prog.SetBinding(ProgressBar.ValueProperty, valueBinding);

            var visibilityBinding = new Binding(enabledPath);
            visibilityBinding.Source = this;
            visibilityBinding.Mode = BindingMode.OneWay;
            visibilityBinding.Converter = new Converters.BoolToVisibilityConverter2();
            prog.SetBinding(ProgressBar.VisibilityProperty, visibilityBinding);

            return prog;
        }

        public Binding GetMicrophoneEnabledBinding()
        {
            ThrowIfStateInvalid();
            var bnd = new Binding(nameof(MicrophoneEnabled));
            bnd.Source = this;
            bnd.Mode = BindingMode.OneWay;
            return bnd;
        }

        public Binding GetSpeakerEnabledBinding()
        {
            ThrowIfStateInvalid();
            var bnd = new Binding(nameof(SpeakerEnabled));
            bnd.Source = this;
            bnd.Mode = BindingMode.OneWay;
            return bnd;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _settings.PropertyChanged -= settings_PropertyChanged;
            _timer.Dispose();
            _lvlSpeaker?.Dispose();
            _lvlMic?.Dispose();
        }

        private void ThrowIfStateInvalid()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UIAudioMonitor));

            if (_thread != Thread.CurrentThread)
                throw new InvalidOperationException("This object must only be accessed from the thread which created it");
        }
    }
}
