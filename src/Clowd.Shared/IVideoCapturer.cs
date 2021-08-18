using Clowd.Config;
using Clowd.PlatformUtil;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Clowd
{
    public interface IVideoCapturer : IDisposable, INotifyPropertyChanged
    {
        event EventHandler<VideoCriticalErrorEventArgs> CriticalError;
        event EventHandler<VideoStatusEventArgs> StatusReceived;
        string BusyStatus { get; }
        bool IsRecording { get; }
        Task Initialize();
        Task<string> StartAsync(ScreenRect captureRect, VideoSettings settings);
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

        public abstract Task<string> StartAsync(ScreenRect captureRect, VideoSettings settings);

        public abstract Task StopAsync();

        public abstract void WriteLogToFile(string fileName);
        public virtual Task Initialize() { return Task.CompletedTask; }
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
