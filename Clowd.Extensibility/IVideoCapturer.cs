using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd
{
    public class VideoCapturerSettings
    {
        public string OutputDirectory { get; set; }
        public bool CaptureCursor { get; set; } = true;
        public int FPS { get; set; } = 30;
        public int MaxResolutionWidth { get; set; } = 0;
        public int MaxResolutionHeight { get; set; } = 0;
        public VideoQuality Quality { get; set; } = VideoQuality.Medium;
        public VideoPerformance Performance { get; set; } = VideoPerformance.Medium;
        public VideoSubsamplingMode SubsamplingMode { get; set; } = VideoSubsamplingMode.yuv420;
        public bool CaptureSpeaker { get; set; } = false;
        public IAudioSpeakerDevice CaptureSpeakerDeviceId { get; set; }
        public bool CaptureMicrophone { get; set; } = false;
        public IAudioMicrophoneDevice CaptureMicrophoneDeviceId { get; set; }
        public bool HardwareAccelerated { get; set; } = true;
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

    public interface IVideoCapturer : IDisposable, INotifyPropertyChanged
    {
        string BusyStatus { get; }
        bool IsRecording { get; }
        Task<string> StartAsync(Rectangle captureRect, VideoCapturerSettings settings);
        Task StopAsync();
        void WriteLogToFile(string fileName);
    }

    public abstract class VideoCapturerBase : IVideoCapturer
    {
        public string BusyStatus
        {
            get => _busy;
            protected set
            {
                if (value != _busy)
                {
                    _busy = value;
                    OnPropertyChanged(nameof(BusyStatus));
                }
            }
        }
        public bool IsRecording
        {
            get => _isRecording;
            protected set
            {
                if (value != _isRecording)
                {
                    _isRecording = value;
                    OnPropertyChanged(nameof(IsRecording));
                }
            }
        }

        private string _busy = "Initializing...";
        private bool _isRecording = false;

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<VideoCriticalErrorEventArgs> CriticalError;
        public event EventHandler<VideoStatusEventArgs> StatusReceived;

        protected void OnCriticalError(string error)
        {
            CriticalError?.Invoke(this, new VideoCriticalErrorEventArgs(error));
        }

        protected void OnPropertyChanged(string propertyName)
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
