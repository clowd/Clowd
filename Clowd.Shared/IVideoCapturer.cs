using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd
{
    public class VideoCapturerSettings : INotifyPropertyChanged
    {
        private string _outputDirectory;
        private int _fps = 30;
        private int _maxResolutionWidth = 0;
        private int _maxResolutionHeight = 0;
        private VideoQuality _quality = VideoQuality.Medium;
        private VideoPerformance _performance = VideoPerformance.Medium;
        private VideoSubsamplingMode _subsamplingMode = VideoSubsamplingMode.yuv420;
        private bool _captureSpeaker = false;
        private IAudioSpeakerDevice _captureSpeakerDevice;
        private bool _captureMicrophone = false;
        private IAudioMicrophoneDevice _captureMicrophoneDevice;
        private bool _hardwareAccelerated = true;

        public event PropertyChangedEventHandler PropertyChanged;

        public string OutputDirectory
        {
            get => _outputDirectory;
            set
            {
                if (value == _outputDirectory)
                {
                    return;
                }

                _outputDirectory = value;
                OnPropertyChanged();
            }
        }
        public int Fps
        {
            get => _fps;
            set
            {
                if (value == _fps)
                {
                    return;
                }

                _fps = value;
                OnPropertyChanged();
            }
        }
        public int MaxResolutionWidth
        {
            get => _maxResolutionWidth;
            set
            {
                if (value == _maxResolutionWidth)
                {
                    return;
                }

                _maxResolutionWidth = value;
                OnPropertyChanged();
            }
        }
        public int MaxResolutionHeight
        {
            get => _maxResolutionHeight;
            set
            {
                if (value == _maxResolutionHeight)
                {
                    return;
                }

                _maxResolutionHeight = value;
                OnPropertyChanged();
            }
        }
        public VideoQuality Quality
        {
            get => _quality;
            set
            {
                if (value == _quality)
                {
                    return;
                }

                _quality = value;
                OnPropertyChanged();
            }
        }
        public VideoPerformance Performance
        {
            get => _performance;
            set
            {
                if (value == _performance)
                {
                    return;
                }

                _performance = value;
                OnPropertyChanged();
            }
        }
        public VideoSubsamplingMode SubsamplingMode
        {
            get => _subsamplingMode;
            set
            {
                if (value == _subsamplingMode)
                {
                    return;
                }

                _subsamplingMode = value;
                OnPropertyChanged();
            }
        }
        public bool CaptureSpeaker
        {
            get => _captureSpeaker;
            set
            {
                if (value == _captureSpeaker)
                {
                    return;
                }

                _captureSpeaker = value;
                OnPropertyChanged();
            }
        }
        public IAudioSpeakerDevice CaptureSpeakerDevice
        {
            get => _captureSpeakerDevice;
            set
            {
                if (ReferenceEquals(value, _captureSpeakerDevice))
                {
                    return;
                }

                _captureSpeakerDevice = value;
                OnPropertyChanged();
            }
        }
        public bool CaptureMicrophone
        {
            get => _captureMicrophone;
            set
            {
                if (value == _captureMicrophone)
                {
                    return;
                }

                _captureMicrophone = value;
                OnPropertyChanged();
            }
        }
        public IAudioMicrophoneDevice CaptureMicrophoneDevice
        {
            get => _captureMicrophoneDevice;
            set
            {
                if (ReferenceEquals(value, _captureMicrophoneDevice))
                {
                    return;
                }

                _captureMicrophoneDevice = value;
                OnPropertyChanged();
            }
        }
        public bool HardwareAccelerated
        {
            get => _hardwareAccelerated;
            set
            {
                if (value == _hardwareAccelerated)
                {
                    return;
                }

                _hardwareAccelerated = value;
                OnPropertyChanged();
            }
        }

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public enum VideoQuality
    {
        Low = 29,
        Medium = 24,
        High = 19,
    }

    public enum VideoPerformance
    {
        Slow,
        Medium,
        Fast,
    }

    public enum VideoSubsamplingMode
    {
        yuv420,
        yuv444,
    }

    public interface IVideoCapturer : IDisposable, INotifyPropertyChanged, IModule
    {
        event EventHandler<VideoCriticalErrorEventArgs> CriticalError;
        event EventHandler<VideoStatusEventArgs> StatusReceived;
        string BusyStatus { get; }
        bool IsRecording { get; }
        Task Initialize();
        Task<string> StartAsync(Rectangle captureRect, VideoCapturerSettings settings);
        Task StopAsync();
        void WriteLogToFile(string fileName);
    }

    public abstract class VideoCapturerBase : IVideoCapturer
    {
        public virtual string BusyStatus
        {
            get => _busyStatus;
            protected set
            {
                if (value != _busyStatus)
                {
                    _busyStatus = value;
                    OnPropertyChanged();
                }
            }
        }
        public virtual bool IsRecording
        {
            get => _isRecording;
            protected set
            {
                if (value != _isRecording)
                {
                    _isRecording = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _busyStatus = "Initializing...";
        private bool _isRecording = false;

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<VideoCriticalErrorEventArgs> CriticalError;
        public event EventHandler<VideoStatusEventArgs> StatusReceived;

        protected void OnCriticalError(string error)
        {
            CriticalError?.Invoke(this, new VideoCriticalErrorEventArgs(error));
        }

        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected void OnStatusRecieved(int avgFps, int droppedFrames, TimeSpan totalTime)
        {
            StatusReceived?.Invoke(this, new VideoStatusEventArgs(avgFps, droppedFrames, totalTime));
        }

        public abstract void Dispose();

        public abstract Task<string> StartAsync(Rectangle captureRect, VideoCapturerSettings settings);

        public abstract Task StopAsync();

        public abstract void WriteLogToFile(string fileName);
        public abstract Task Initialize();
    }

    public class VideoCriticalErrorEventArgs : EventArgs
    {
        public string Error { get; }
        public VideoCriticalErrorEventArgs(string error)
        {
            Error = error;
        }
    }

    public class VideoStatusEventArgs : EventArgs
    {
        public int AvgFps { get; }
        public int DroppedFrames { get; }
        public TimeSpan TotalTime { get; }
        public VideoStatusEventArgs(int avgFps, int droppedFrames, TimeSpan totalTime)
        {
            AvgFps = avgFps;
            DroppedFrames = droppedFrames;
            TotalTime = totalTime;
        }
    }
}
